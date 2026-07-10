"""Endpoints publicos del backend (consumidos por el visor).

Sprint 3 alcance:
  - GET  /api/manifest.json  → version activa actual
  - POST /api/verify         → verificacion de licencia
  - GET  /api/lenses         → catalogo de lentes activo
  - POST /api/log            → recepcion de logs del visor

Sprint 8 agregara /api/admin/* con JWT + CRUD completo.
"""
import json
import logging
from datetime import date, datetime, timedelta
from typing import Annotated, Literal

from fastapi import APIRouter, Depends, HTTPException, Request
from fastapi.responses import JSONResponse
from pydantic import BaseModel, Field
from slowapi import Limiter
from slowapi.util import get_remote_address
from sqlalchemy import delete as sa_delete
from sqlalchemy.exc import IntegrityError
from sqlmodel import Session, func, select

from app.config import settings
from app.database import get_session
from app.models import Device, LensCatalog, UpdateLog, Version
from app.utils import utcnow

logger = logging.getLogger(__name__)

limiter = Limiter(key_func=get_remote_address)
router = APIRouter(prefix="/api", tags=["public"])

SessionDep = Annotated[Session, Depends(get_session)]


# ---------------------------------------------------------------------------
# Schemas Pydantic (separadas de los modelos SQLModel para no exponer la BD)
# ---------------------------------------------------------------------------
class ManifestResponse(BaseModel):
    app: str
    apk_version: str
    min_apk_version: str
    apk_url: str
    apk_sha256: str
    changelog: str


class VerifyRequest(BaseModel):
    device_id: str = Field(min_length=1, max_length=128)
    current_apk_version: str | None = None


class VerifyResponse(BaseModel):
    status: str
    device_name: str | None = None
    license_expiry: date | None = None
    message: str


class VerifyDenied(BaseModel):
    status: str = "denied"
    reason: str
    message: str


class LensesResponse(BaseModel):
    version: str
    catalogo: list[dict]


class LogEvent(BaseModel):
    event: str = Field(min_length=1, max_length=64)
    detail: str = Field(default="", max_length=2048)


class LogRequest(BaseModel):
    device_id: str = Field(min_length=1, max_length=128)
    events: list[LogEvent]


# ---------------------------------------------------------------------------
# GET /api/manifest.json
# ---------------------------------------------------------------------------
@router.get("/manifest.json", response_model=ManifestResponse)
def get_manifest(session: SessionDep, app: Literal["visor", "tablet"] = "visor") -> ManifestResponse:
    """Manifest de actualizacion, UNA version activa POR APP (`app` query param).

    Sin `?app=` devuelve el canal "visor" (compat con el unico consumidor
    previsto hoy). Un valor fuera de {"visor","tablet"} devuelve 422
    automatico (FastAPI valida el `Literal`).
    """
    version = session.exec(
        select(Version).where(Version.is_active == True, Version.app == app)  # noqa: E712
    ).first()
    if version is None:
        raise HTTPException(
            status_code=503,
            detail=f"No hay version activa publicada para el canal '{app}'.",
        )
    return ManifestResponse(
        app=version.app,
        apk_version=version.apk_version,
        min_apk_version=version.min_apk_version,
        apk_url=version.apk_url,
        apk_sha256=version.apk_sha256,
        changelog=version.changelog,
    )


# ---------------------------------------------------------------------------
# POST /api/verify
# ---------------------------------------------------------------------------
# Tope de devices auto-registrados en estado "pending" simultaneos. Evita que
# un atacante inunde la tabla de devices con auto-registros (DoS de storage);
# por encima del tope, un device_id desconocido vuelve a DEVICE_NOT_FOUND sin
# crear fila (el admin tiene que pre-registrarlo a mano o liberar cupo).
MAX_PENDING_DEVICES = 50


def _denied(reason: str, message: str) -> JSONResponse:
    return JSONResponse(
        status_code=403,
        content=VerifyDenied(reason=reason, message=message).model_dump(),
    )


@router.post("/verify")
@limiter.limit("10/minute")
def verify_license(request: Request, body: VerifyRequest, session: SessionDep):
    """Verifica si un device_id tiene licencia valida.

    Rate-limited a 10 requests/min/IP para evitar brute-force sin romper
    clinicas donde varios visores comparten IP publica por NAT.
    Decision Sprint 0: licencias permanentes (license_expiry NULL = permanente).

    Orden de chequeos (feature de licenciamiento por dispositivo):
    unknown (auto-registro) -> pending -> rejected -> suspended -> expired -> ok.
    """
    device = session.exec(
        select(Device).where(Device.device_id == body.device_id)
    ).first()
    client_ip = request.client.host if request.client else None

    if device is None:
        # Auto-registro: si hay cupo, se crea en estado "pending" para que el
        # admin lo apruebe/rechace desde el panel. Un device "rejected" NUNCA
        # vuelve a pasar por esta rama (ya existe la fila), asi que rechazar
        # es definitivo hasta que un admin lo edite a mano.
        pending_count = session.exec(
            select(func.count()).select_from(Device).where(Device.status == "pending")
        ).one()
        if pending_count >= MAX_PENDING_DEVICES:
            return _denied(
                "DEVICE_NOT_FOUND",
                "Este dispositivo no esta registrado. Contacte al administrador.",
            )
        device = Device(
            device_id=body.device_id,
            name=f"Visor {body.device_id[:8]}",
            status="pending",
            notes="auto-registrado por verify",
            last_seen=utcnow(),
            last_ip=client_ip,
        )
        session.add(device)
        try:
            session.commit()
        except IntegrityError:
            # Carrera: dos requests concurrentes con el mismo device_id
            # desconocido pasan ambas el SELECT de arriba antes de que
            # cualquiera hiciera commit; el segundo commit pisa el unique
            # constraint de device_id. Descartamos nuestro insert y releemos
            # la fila que gano la carrera para evaluar SU status (cae al
            # flujo normal de abajo; en la practica va a ser "pending", el
            # estado inicial del auto-registro, pero re-evaluamos en vez de
            # asumirlo por si un admin ya la edito en el intervalo).
            session.rollback()
            device = session.exec(
                select(Device).where(Device.device_id == body.device_id)
            ).first()
            if device is None:
                # Practicamente imposible (la fila ganadora se borraria justo
                # en este intervalo), pero no seguimos con device=None.
                return _denied(
                    "DEVICE_NOT_FOUND",
                    "Este dispositivo no esta registrado. Contacte al administrador.",
                )
        else:
            return _denied(
                "DEVICE_PENDING",
                "Dispositivo registrado, pendiente de aprobacion del administrador.",
            )

    # Device existente (o resuelto tras una carrera de auto-registro):
    # actualizar last_seen / last_ip (auditoria) antes de evaluar.
    device.last_seen = utcnow()
    device.last_ip = client_ip

    if device.status == "pending":
        session.commit()
        return _denied(
            "DEVICE_PENDING",
            "Dispositivo pendiente de aprobacion del administrador.",
        )

    if device.status == "rejected":
        session.commit()
        return _denied(
            "DEVICE_REJECTED",
            "Dispositivo rechazado. Contacte al administrador.",
        )

    if device.status == "suspended":
        session.commit()
        return _denied(
            "DEVICE_SUSPENDED",
            "Este dispositivo esta suspendido.",
        )

    if device.license_expiry is not None and device.license_expiry < date.today():
        session.commit()
        return _denied(
            "LICENSE_EXPIRED",
            "La licencia de este dispositivo ha vencido.",
        )

    session.commit()
    return VerifyResponse(
        status="ok",
        device_name=device.name,
        license_expiry=device.license_expiry,
        message="Licencia verificada correctamente.",
    )


# ---------------------------------------------------------------------------
# GET /api/lenses
# ---------------------------------------------------------------------------
@router.get("/lenses", response_model=LensesResponse)
def get_lenses(session: SessionDep) -> LensesResponse:
    catalog = session.exec(
        select(LensCatalog).where(LensCatalog.is_active == True)  # noqa: E712
    ).first()
    if catalog is None:
        raise HTTPException(status_code=503, detail="No hay catalogo de lentes activo.")
    data = json.loads(catalog.data)
    return LensesResponse(
        version=data.get("version", catalog.version),
        catalogo=data.get("catalogo", []),
    )


# ---------------------------------------------------------------------------
# Retencion de logs (UpdateLog) — purga los mas viejos que
# settings.log_retention_days. Se corre en el arranque de la app (siempre) y
# en cada POST /api/log (con throttle, ver _maybe_purge_logs) para que la
# tabla no crezca sin limite sin depender de un cron aparte.
# ---------------------------------------------------------------------------
LOG_PURGE_THROTTLE = timedelta(hours=1)
_last_log_purge_at: datetime | None = None  # timestamp en memoria; proceso unico


def purge_old_logs(session: Session) -> int:
    """Borra filas de UpdateLog mas viejas que log_retention_days. Devuelve
    la cantidad de filas borradas."""
    cutoff = utcnow() - timedelta(days=settings.log_retention_days)
    result = session.execute(sa_delete(UpdateLog).where(UpdateLog.created_at < cutoff))
    session.commit()
    return result.rowcount or 0


def _maybe_purge_logs(session: Session) -> None:
    """Purga logs viejos, como maximo una vez por hora.

    Proceso unico (un solo worker uvicorn) -> un timestamp de modulo en
    memoria alcanza como throttle, sin necesidad de lock distribuido ni
    tabla de estado. Se llama tanto en el arranque (siempre ejecuta, porque
    `_last_log_purge_at` arranca en None) como en cada POST /api/log.
    """
    global _last_log_purge_at
    now = utcnow()
    if _last_log_purge_at is not None and now - _last_log_purge_at < LOG_PURGE_THROTTLE:
        return
    _last_log_purge_at = now
    deleted = purge_old_logs(session)
    if deleted:
        logger.info(
            "Purga de logs: %d fila(s) borrada(s) (retention=%d dias)",
            deleted, settings.log_retention_days,
        )


# ---------------------------------------------------------------------------
# POST /api/log
# ---------------------------------------------------------------------------
@router.post("/log")
def post_log(body: LogRequest, session: SessionDep):
    """Recibe batch de eventos de actualizacion desde el visor.

    No rate-limited: un visor puede mandar varios eventos por update.
    Si el device_id no existe, igual aceptamos el log (debugging temprano).
    """
    for ev in body.events:
        session.add(UpdateLog(
            device_id=body.device_id,
            event=ev.event,
            detail=ev.detail,
        ))
    session.commit()
    _maybe_purge_logs(session)
    return {"status": "ok", "events_logged": len(body.events)}
