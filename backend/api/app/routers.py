"""Endpoints publicos del backend (consumidos por el visor).

Sprint 3 alcance:
  - GET  /api/manifest.json  → version activa actual
  - POST /api/verify         → verificacion de licencia
  - GET  /api/lenses         → catalogo de lentes activo
  - POST /api/log            → recepcion de logs del visor

Sprint 8 agregara /api/admin/* con JWT + CRUD completo.
"""
import json
from datetime import date, datetime
from typing import Annotated

from fastapi import APIRouter, Depends, Request
from fastapi.responses import JSONResponse
from pydantic import BaseModel, Field
from slowapi import Limiter
from slowapi.util import get_remote_address
from sqlmodel import Session, select

from app.database import get_session
from app.models import Device, LensCatalog, UpdateLog, Version

limiter = Limiter(key_func=get_remote_address)
router = APIRouter(prefix="/api", tags=["public"])

SessionDep = Annotated[Session, Depends(get_session)]


# ---------------------------------------------------------------------------
# Schemas Pydantic (separadas de los modelos SQLModel para no exponer la BD)
# ---------------------------------------------------------------------------
class ManifestResponse(BaseModel):
    min_apk_version: str
    current_apk_version: str
    current_asset_version: str
    apk_url: str
    pck_url: str
    pck_sha256: str
    changelog: str


class VerifyRequest(BaseModel):
    device_id: str = Field(min_length=1, max_length=128)
    current_apk_version: str | None = None
    current_asset_version: str | None = None


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
def get_manifest(session: SessionDep) -> ManifestResponse:
    version = session.exec(
        select(Version).where(Version.is_active == True)  # noqa: E712
    ).first()
    if version is None:
        return JSONResponse(
            status_code=503,
            content={"detail": "No hay version activa publicada."},
        )
    return ManifestResponse(
        min_apk_version=version.min_apk_version,
        current_apk_version=version.apk_version,
        current_asset_version=version.asset_version,
        apk_url=version.apk_url,
        pck_url=version.pck_url,
        pck_sha256=version.pck_sha256,
        changelog=version.changelog,
    )


# ---------------------------------------------------------------------------
# POST /api/verify
# ---------------------------------------------------------------------------
@router.post("/verify")
@limiter.limit("1/minute")
def verify_license(request: Request, body: VerifyRequest, session: SessionDep):
    """Verifica si un device_id tiene licencia valida.

    Rate-limited a 1 request/min/IP para evitar brute-force.
    Decision Sprint 0: licencias permanentes (license_expiry NULL = permanente).
    """
    device = session.exec(
        select(Device).where(Device.device_id == body.device_id)
    ).first()

    # Actualizar last_seen / last_ip si el device existe (auditoria).
    if device is not None:
        device.last_seen = datetime.utcnow()
        device.last_ip = request.client.host if request.client else None

    if device is None:
        # Decision Sprint 0: pre-registro manual. Devices desconocidos = denied.
        session.commit()
        return JSONResponse(
            status_code=403,
            content=VerifyDenied(
                reason="DEVICE_NOT_FOUND",
                message="Este dispositivo no esta registrado. Contacte al administrador.",
            ).model_dump(),
        )

    if device.status == "suspended":
        session.commit()
        return JSONResponse(
            status_code=403,
            content=VerifyDenied(
                reason="DEVICE_SUSPENDED",
                message="Este dispositivo esta suspendido.",
            ).model_dump(),
        )

    if device.license_expiry is not None and device.license_expiry < date.today():
        session.commit()
        return JSONResponse(
            status_code=403,
            content=VerifyDenied(
                reason="LICENSE_EXPIRED",
                message="La licencia de este dispositivo ha vencido.",
            ).model_dump(),
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
        return JSONResponse(
            status_code=503,
            content={"detail": "No hay catalogo de lentes activo."},
        )
    data = json.loads(catalog.data)
    return LensesResponse(
        version=data.get("version", catalog.version),
        catalogo=data.get("catalogo", []),
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
    return {"status": "ok", "events_logged": len(body.events)}
