"""Endpoints publicos del backend (consumidos por el visor).

Sprint 3 alcance:
  - GET  /api/manifest.json  → version activa actual
  - POST /api/verify         → verificacion de licencia
  - GET  /api/lenses         → catalogo de lentes activo
  - POST /api/log            → recepcion de logs del visor

Sprint 8 agregara /api/admin/* con JWT + CRUD completo.
"""
import hashlib
import json
import logging
import re
import secrets
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
from app.models import CustomLens, Device, LensCatalog, UpdateLog, Version
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
    # P7: modo de app y flag admin por dispositivo. Aditivos: un visor viejo
    # (JsonUtility) ignora campos desconocidos sin romper.
    app_mode: str = "standard"
    is_admin: bool = False
    message: str


class VerifyDenied(BaseModel):
    status: str = "denied"
    reason: str
    message: str


class LensesResponse(BaseModel):
    version: str
    catalogo: list[dict]


class CustomLensCreate(BaseModel):
    device_id: str = Field(min_length=1, max_length=128)
    scope: Literal["private", "generic"] = "private"
    nombre: str = Field(min_length=1, max_length=80)
    descripcion: str = Field(default="", max_length=500)
    params: dict[str, dict]


class CustomLensUpdate(BaseModel):
    device_id: str = Field(min_length=1, max_length=128)
    nombre: str = Field(min_length=1, max_length=80)
    descripcion: str = Field(default="", max_length=500)
    params: dict[str, dict]


class LensReorderRequest(BaseModel):
    device_id: str = Field(min_length=1, max_length=128)
    # Ids del catalogo BASE en el nuevo orden. max_length acota el payload
    # (el catalogo real hoy tiene un puñado de lentes; 200 es margen sano).
    order: list[str] = Field(max_length=200)


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
def get_manifest(
    session: SessionDep,
    app: Literal["visor", "tablet"] = "visor",
    device_id: str | None = None,
) -> ManifestResponse:
    """Manifest de actualizacion, UNA version activa POR APP (`app` query param).

    Sin `?app=` devuelve el canal "visor" (compat con el unico consumidor
    previsto hoy). Un valor fuera de {"visor","tablet"} devuelve 422
    automatico (FastAPI valida el `Literal`).

    `device_id` (opcional): gate por-dispositivo del OTA del backend. La
    mayoria de los visores corren en kiosco de Meta Horizon Managed Services
    (se actualizan por el Admin Center de Meta, no por aca) y NO deben recibir
    el OTA del backend; el mecanismo de supresion es gratis, `UpdateManager`
    (Unity) ya trata un 503 como "no hay update" en silencio (sin UI/error).

    GOTCHA no obvio: si `device_id` viene AUSENTE o vacio, esto responde 200
    normal (mismo comportamiento que antes de este parametro), NO 503. Es
    deliberado y NO negociable: el APK ya instalado en campo (0.6.1) todavia
    no manda `device_id` en esta URL (se agrega en el build siguiente); si
    esta rama devolviera 503, esos visores nunca se enterarian del release
    que agrega el `device_id` a la request y quedariamos con el OTA
    bloqueado permanentemente para toda esa flota. Es la fila de
    compatibilidad hacia atras del mecanismo.

    La tablet SIEMPRE recibe 200 (nunca esta en la tabla `devices`, no tiene
    gate de licencia) — el chequeo de `device_id` ni se evalua para `app ==
    "tablet"`.
    """
    if app != "tablet" and device_id:
        device = session.exec(
            select(Device).where(Device.device_id == device_id)
        ).first()
        if device is None or not device.ota_enabled:
            # Mismo status code que el 503 de "no hay version activa" de mas
            # abajo: el cliente no distingue el detail, solo el codigo.
            raise HTTPException(
                status_code=503,
                detail="OTA del backend deshabilitado para este dispositivo.",
            )

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
            last_apk_version=body.current_apk_version or None,
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
    # Solo pisamos la version conocida si llega un valor no vacio: un verify
    # sin current_apk_version (cliente viejo o campo omitido) no debe borrar
    # el ultimo valor bueno que ya teniamos registrado.
    if body.current_apk_version:
        device.last_apk_version = body.current_apk_version

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
        app_mode=device.app_mode,
        is_admin=device.is_admin,
        message="Licencia verificada correctamente.",
    )


# ---------------------------------------------------------------------------
# Helpers compartidos por /api/lenses y /api/lenses/custom (P7)
# ---------------------------------------------------------------------------
def _device_effectively_active(device: Device) -> bool:
    """Un device puede usar features (customs) si esta activo Y su licencia
    no vencio. Mismos criterios que el flujo de verify, condensados."""
    return device.status == "active" and (
        device.license_expiry is None or device.license_expiry >= date.today()
    )


def _merged_version(base_version: str, extras: list[CustomLens]) -> str:
    """Version del catalogo mergeado. Sin extras devuelve la version base
    LITERAL (compat total con caches existentes); con extras, un fingerprint
    determinístico que cambia ante cualquier alta/edicion/borrado (via
    lens_id + updated_at de cada extra incluida)."""
    if not extras:
        return base_version
    h = hashlib.sha256()
    for lens in sorted(extras, key=lambda l: l.lens_id):
        h.update(f"{lens.lens_id}|{lens.updated_at.isoformat()}".encode())
    return f"{base_version}+x{h.hexdigest()[:10]}"


def _lens_to_dict(lens: CustomLens) -> dict:
    """Serializa una CustomLens (P7.2: siempre privada de un device — las
    "genericas" dejaron de vivir en esta tabla, ver mas abajo) al contrato
    compartido de lente, con el campo extra `origen: "custom"` que Unity usa
    para gatear la UI (las lentes del blob base van SIN el campo)."""
    return {
        "id": lens.lens_id,
        "nombre": lens.nombre,
        "descripcion": lens.descripcion,
        "params": json.loads(lens.params_json),
        "origen": "custom",
    }


# ---------------------------------------------------------------------------
# GET /api/lenses
# ---------------------------------------------------------------------------
@router.get("/lenses", response_model=LensesResponse)
def get_lenses(session: SessionDep, device_id: str | None = None) -> LensesResponse:
    """Catalogo mergeado: base (P7.2: incluye las ex-lentes "genericas", que
    ahora son lentes BASE mas dentro del blob versionado) + (si `device_id`
    valido y efectivamente activo) las lentes CUSTOM privadas de ese device.

    `device_id` es opcional: la tablet sincroniza anonima (solo base).
    Un device_id desconocido o no-activo responde como anonimo — NO 403: el
    sync nunca bloquea, y para un device suspendido esto purga sus customs
    del cache local en el proximo arranque (comportamiento deseado). Este
    endpoint jamas auto-registra (eso es exclusivo de verify).
    """
    catalog = session.exec(
        select(LensCatalog).where(LensCatalog.is_active == True)  # noqa: E712
    ).first()
    if catalog is None:
        raise HTTPException(status_code=503, detail="No hay catalogo de lentes activo.")
    data = json.loads(catalog.data)
    base_lenses: list[dict] = data.get("catalogo", [])

    extras: list[CustomLens] = []
    if device_id:
        device = session.exec(
            select(Device).where(Device.device_id == device_id)
        ).first()
        if device is not None and _device_effectively_active(device):
            extras = list(session.exec(
                select(CustomLens)
                .where(CustomLens.owner_device_pk == device.id)
                .order_by(CustomLens.lens_id)
            ))

    # Colision defensiva: solo alcanzable si un admin metio a mano un id
    # "custom_*"/"generic_*" en el blob base — el catalogo base es autoritativo.
    seen_ids = {l.get("id") for l in base_lenses}
    merged = list(base_lenses)
    for lens in extras:
        if lens.lens_id in seen_ids:
            logger.warning(
                "Lente extra '%s' colisiona con un id del catalogo base; salteada.",
                lens.lens_id,
            )
            continue
        seen_ids.add(lens.lens_id)
        merged.append(_lens_to_dict(lens))

    return LensesResponse(
        version=_merged_version(data.get("version", catalog.version), extras),
        catalogo=merged,
    )


# ---------------------------------------------------------------------------
# CRUD /api/lenses/custom (P7) — consumido por el VISOR (comandado por la
# tablet via WebSocket). El device_id actua como identidad de facto
# (limitacion aceptada, documentada en docs/licenciamiento.md: TLS en prod,
# id opaco no enumerable, rate limit, danio acotado a lentes).
# ---------------------------------------------------------------------------
MAX_CUSTOM_LENSES_PER_DEVICE = 50
MAX_LENS_PARAMS = 20


def _validate_lens_params(params: dict) -> str | None:
    """Valida el dict params del contrato de lente. Devuelve un mensaje de
    error o None si es valido. No se valida contra la lista de params del
    catalogo base (Unity tolera params faltantes/extra)."""
    if not params:
        return "params no puede estar vacio."
    if len(params) > MAX_LENS_PARAMS:
        return f"params admite como maximo {MAX_LENS_PARAMS} claves."
    for key, spec in params.items():
        if not isinstance(key, str) or not (1 <= len(key) <= 64):
            return f"Clave de parametro invalida: {key!r}."
        if not isinstance(spec, dict) or set(spec.keys()) != {"default", "min", "max"}:
            return f"El parametro '{key}' debe ser un dict con exactamente default/min/max."
        try:
            d, lo, hi = float(spec["default"]), float(spec["min"]), float(spec["max"])
        except (TypeError, ValueError):
            return f"El parametro '{key}' tiene valores no numericos."
        if not (lo <= d <= hi):
            return f"El parametro '{key}' no cumple min <= default <= max."
    return None


def _authorize_lens_write(session: Session, device_id: str, need_admin: bool):
    """Autoriza una mutacion de lentes. Devuelve el Device o un JSONResponse
    de denegacion (mismo shape que verify: {status:"denied", reason, message}).

    NO auto-registra devices desconocidos (eso es exclusivo de verify)."""
    device = session.exec(
        select(Device).where(Device.device_id == device_id)
    ).first()
    if device is None:
        return _denied(
            "DEVICE_NOT_FOUND",
            "Este dispositivo no esta registrado. Contacte al administrador.",
        )
    if not _device_effectively_active(device):
        return _denied(
            "DEVICE_NOT_AUTHORIZED",
            "Este dispositivo no esta habilitado para gestionar lentes.",
        )
    if need_admin and not device.is_admin:
        return _denied(
            "NOT_ADMIN",
            "Solo un dispositivo administrador puede gestionar lentes genericas.",
        )
    if not need_admin and device.app_mode != "pro" and not device.is_admin:
        return _denied(
            "MODE_NOT_PRO",
            "La creacion de lentes propias requiere el modo Pro.",
        )
    return device


def _catalog_version_for(session: Session, device: Device | None) -> str:
    """Version mergeada post-cambio para ese device (le ahorra al visor
    adivinar si tiene que re-sincronizar). P7.2: ya no suma genericas (esa
    tabla es solo customs por device)."""
    catalog = session.exec(
        select(LensCatalog).where(LensCatalog.is_active == True)  # noqa: E712
    ).first()
    base_version = "unknown"
    if catalog is not None:
        base_version = json.loads(catalog.data).get("version", catalog.version)
    extras: list[CustomLens] = []
    if device is not None:
        extras = list(session.exec(
            select(CustomLens).where(CustomLens.owner_device_pk == device.id)
        ))
    return _merged_version(base_version, extras)


# ---------------------------------------------------------------------------
# P7.1/P7.2: edicion, alta y borrado de lentes del CATALOGO BASE por un
# admin. La base nunca se pisa in-place: cada mutacion clona el catalogo
# activo en una version nueva `.aN` con el cambio aplicado (rollback manual
# desde /admin/lenses activando la fila vieja). El sufijo se calcula sobre
# la RAIZ de la version activa (sin un `.aN` final, si ya tenia uno) para
# que ediciones encadenadas den .a1, .a2, ... en vez de .a1.a1.
#
# P7.2 (decision de producto): las lentes "genericas" dejaron de ser una
# categoria aparte en `custom_lenses` — crear con scope="generic" ahora
# AGREGA la lente al blob base (`_add_base_lens`) y un admin puede BORRAR
# cualquier lente del catalogo (`_delete_base_lens`, ya no rechaza siempre
# con BASE_LENS: el historial de versiones .aN cubre el rollback). El
# reason "BASE_LENS" queda sin uso pero no se retira del vocabulario del
# contrato (evita romper un cliente viejo que lo hubiera hardcodeado).
# ---------------------------------------------------------------------------
_VERSION_ROOT_RE = re.compile(r"^(.*?)(\.a(\d+))?$")


def _version_root_and_suffix(version: str) -> tuple[str, int]:
    m = _VERSION_ROOT_RE.match(version)
    root = m.group(1) if m else version
    suffix = int(m.group(3)) if m and m.group(3) else 0
    return root, suffix


def _next_admin_lens_version(session: Session, active_version: str) -> str:
    """Siguiente version `.aN` para la raiz de `active_version`. N = mayor
    sufijo existente (entre TODAS las filas de LensCatalog, activas o no,
    con esa misma raiz) + 1; el while de colision es defensivo (no deberia
    dispararse dado el calculo de N, mismo espiritu que el retry de
    lens_id en create_custom_lens)."""
    root, _ = _version_root_and_suffix(active_version)
    existing = {c.version for c in session.exec(select(LensCatalog)).all()}
    max_n = 0
    for v in existing:
        r, n = _version_root_and_suffix(v)
        if r == root:
            max_n = max(max_n, n)
    n = max_n + 1
    candidate = f"{root}.a{n}"
    while candidate in existing:
        n += 1
        candidate = f"{root}.a{n}"
    return candidate


def _active_base_lens_index(catalog_data: dict, lens_id: str) -> int | None:
    """Indice de `lens_id` dentro de `catalogo` del catalogo activo, o None."""
    for i, lens in enumerate(catalog_data.get("catalogo", [])):
        if lens.get("id") == lens_id:
            return i
    return None


def _update_base_lens(session: Session, lens_id: str, body: CustomLensUpdate):
    """Rama P7.1 de PUT /api/lenses/custom/{lens_id}: `lens_id` no es una
    custom/generica pero coincide con una lente del catalogo BASE activo.
    Solo un admin puede editarla (device efectivamente activo + is_admin,
    mismo shape de rechazo que las genericas). La fila vieja de LensCatalog
    NUNCA se toca — queda de historial/rollback en /admin/lenses."""
    catalog = session.exec(
        select(LensCatalog).where(LensCatalog.is_active == True)  # noqa: E712
    ).first()
    if catalog is None:
        raise HTTPException(status_code=404, detail="Lente no encontrada.")
    data = json.loads(catalog.data)
    idx = _active_base_lens_index(data, lens_id)
    if idx is None:
        raise HTTPException(status_code=404, detail="Lente no encontrada.")

    device = _authorize_lens_write(session, body.device_id, need_admin=True)
    if not isinstance(device, Device):
        return device

    new_data = json.loads(catalog.data)  # clon independiente para mutar
    new_data["catalogo"][idx] = {
        "id": lens_id,
        "nombre": body.nombre,
        "descripcion": body.descripcion,
        "params": body.params,
    }
    new_version = _next_admin_lens_version(session, catalog.version)
    new_data["version"] = new_version

    catalog.is_active = False
    session.add(catalog)
    new_catalog = LensCatalog(
        version=new_version,
        data=json.dumps(new_data, ensure_ascii=False),
        is_active=True,
    )
    session.add(new_catalog)
    session.commit()

    return {
        "status": "ok",
        "lens": dict(new_data["catalogo"][idx]),
        "catalog_version": _catalog_version_for(session, device),
    }


def _add_base_lens(session: Session, body: CustomLensCreate):
    """Rama P7.2 de POST /api/lenses/custom con scope="generic": en vez de
    crear una fila "generica" en `custom_lenses` (modelo viejo, P7), AGREGA
    la lente nueva al FINAL del array `catalogo` del blob BASE activo, en
    una version `.aN` nueva (mismo mecanismo de clon-versionado que
    `_update_base_lens`). Solo un admin puede hacerlo. El id generado
    conserva el prefijo `generic_` (estable y ya usado por Unity/tests como
    marca de "creada desde el panel/tablet", aunque ya no viva en
    `custom_lenses`) — no hay tope (MAX_GENERIC_LENSES se elimino: el blob
    no tiene limite de tamano)."""
    catalog = session.exec(
        select(LensCatalog).where(LensCatalog.is_active == True)  # noqa: E712
    ).first()
    if catalog is None:
        raise HTTPException(status_code=503, detail="No hay catalogo de lentes activo.")

    device = _authorize_lens_write(session, body.device_id, need_admin=True)
    if not isinstance(device, Device):
        return device

    data = json.loads(catalog.data)
    existing_ids = {l.get("id") for l in data.get("catalogo", [])}
    lens_id = None
    for _ in range(20):
        # Retry defensivo ante la (improbable) colision del token_hex, mismo
        # espiritu que el retry de lens_id en la rama privada de abajo.
        candidate = f"generic_{secrets.token_hex(4)}"
        if candidate not in existing_ids:
            lens_id = candidate
            break
    if lens_id is None:
        raise HTTPException(status_code=500, detail="No se pudo generar un id de lente unico.")

    new_lens = {
        "id": lens_id,
        "nombre": body.nombre,
        "descripcion": body.descripcion,
        "params": body.params,
    }
    new_data = json.loads(catalog.data)  # clon independiente para mutar
    new_data["catalogo"].append(new_lens)
    new_version = _next_admin_lens_version(session, catalog.version)
    new_data["version"] = new_version

    catalog.is_active = False
    session.add(catalog)
    new_catalog = LensCatalog(
        version=new_version,
        data=json.dumps(new_data, ensure_ascii=False),
        is_active=True,
    )
    session.add(new_catalog)
    session.commit()

    return {
        "status": "ok",
        "lens": dict(new_lens),
        "catalog_version": _catalog_version_for(session, device),
    }


def _delete_base_lens(session: Session, lens_id: str, device_id: str):
    """Rama P7.2 de DELETE /api/lenses/custom/{lens_id}: `lens_id` coincide
    con una lente del catalogo BASE activo. Antes (P7.1) esto se rechazaba
    SIEMPRE con `BASE_LENS`; decision de producto P7.2: un admin SI puede
    eliminar cualquier lente del catalogo — nueva version `.aN` sin esa
    lente (mismo mecanismo de clon-versionado; la fila vieja queda de
    historial/rollback en /admin/lenses, activandola de nuevo restaura la
    lente borrada)."""
    catalog = session.exec(
        select(LensCatalog).where(LensCatalog.is_active == True)  # noqa: E712
    ).first()
    if catalog is None:
        raise HTTPException(status_code=404, detail="Lente no encontrada.")
    data = json.loads(catalog.data)
    idx = _active_base_lens_index(data, lens_id)
    if idx is None:
        raise HTTPException(status_code=404, detail="Lente no encontrada.")

    device = _authorize_lens_write(session, device_id, need_admin=True)
    if not isinstance(device, Device):
        return device

    new_data = json.loads(catalog.data)  # clon independiente para mutar
    del new_data["catalogo"][idx]
    new_version = _next_admin_lens_version(session, catalog.version)
    new_data["version"] = new_version

    catalog.is_active = False
    session.add(catalog)
    new_catalog = LensCatalog(
        version=new_version,
        data=json.dumps(new_data, ensure_ascii=False),
        is_active=True,
    )
    session.add(new_catalog)
    session.commit()

    return {
        "status": "ok",
        "catalog_version": _catalog_version_for(session, device),
    }


@router.post("/lenses/custom", status_code=201)
@limiter.limit("30/minute")
def create_custom_lens(request: Request, body: CustomLensCreate, session: SessionDep):
    """Crea una lente custom privada (scope "private") o (P7.2) agrega una
    lente nueva al CATALOGO BASE (scope "generic", solo admin — ver
    `_add_base_lens`; ya no crea una fila "generica" en `custom_lenses`).
    El lens_id lo genera el server: sin colisiones con el catalogo base ni
    entre devices, por construccion."""
    err = _validate_lens_params(body.params)
    if err:
        raise HTTPException(status_code=422, detail=err)

    if body.scope == "generic":
        # P7.2: ya no es una CustomLens, es un alta directa sobre el blob
        # base (nueva version .aN) — ver _add_base_lens.
        return _add_base_lens(session, body)

    device = _authorize_lens_write(session, body.device_id, need_admin=False)
    if not isinstance(device, Device):
        return device  # JSONResponse de denegacion

    count = session.exec(
        select(func.count()).select_from(CustomLens)
        .where(CustomLens.owner_device_pk == device.id)
    ).one()
    if count >= MAX_CUSTOM_LENSES_PER_DEVICE:
        return JSONResponse(status_code=409, content=VerifyDenied(
            reason="LENS_LIMIT_REACHED",
            message=f"Tope de lentes propias alcanzado ({MAX_CUSTOM_LENSES_PER_DEVICE}).",
        ).model_dump())

    lens = None
    for _ in range(3):
        # Retry ante la (improbable) colision del token_hex: unique index en
        # lens_id + regeneracion, mismo patron defensivo que el auto-registro.
        candidate = CustomLens(
            owner_device_pk=device.id,
            lens_id=f"custom_{secrets.token_hex(4)}",
            nombre=body.nombre,
            descripcion=body.descripcion,
            params_json=json.dumps(body.params),
        )
        session.add(candidate)
        try:
            session.commit()
            lens = candidate
            break
        except IntegrityError:
            session.rollback()
    if lens is None:
        raise HTTPException(status_code=500, detail="No se pudo generar un id de lente unico.")

    session.refresh(lens)
    return {
        "status": "ok",
        "lens": _lens_to_dict(lens),
        "catalog_version": _catalog_version_for(session, device),
    }


@router.put("/lenses/custom/{lens_id}")
@limiter.limit("30/minute")
def update_custom_lens(request: Request, lens_id: str, body: CustomLensUpdate, session: SessionDep):
    """Edita una lente CUSTOM propia (dueño), o (P7.1) una lente del
    CATALOGO BASE activo, incluidas las ex-"genericas" que ahora viven ahi
    (solo admin — ver _update_base_lens). Un Pro no-admin NO puede editar
    lentes base (requisito de producto).

    `is_generic` abajo queda como chequeo defensivo: P7.2 dejo de crear
    filas de CustomLens con `owner_device_pk is None` (las genericas nuevas
    van directo al blob), pero una BD migrada desde antes de P7.2 puede
    tener filas asi hasta que corra la migracion (ver §migraciones)."""
    err = _validate_lens_params(body.params)
    if err:
        raise HTTPException(status_code=422, detail=err)

    lens = session.exec(
        select(CustomLens).where(CustomLens.lens_id == lens_id)
    ).first()
    if lens is None:
        return _update_base_lens(session, lens_id, body)

    is_generic = lens.owner_device_pk is None
    device = _authorize_lens_write(session, body.device_id, need_admin=is_generic)
    if not isinstance(device, Device):
        return device
    if not is_generic and lens.owner_device_pk != device.id:
        return _denied("NOT_OWNER", "Esta lente pertenece a otro dispositivo.")

    lens.nombre = body.nombre
    lens.descripcion = body.descripcion
    lens.params_json = json.dumps(body.params)
    lens.updated_at = utcnow()  # dispara el fingerprint de version
    session.add(lens)
    session.commit()
    session.refresh(lens)
    return {
        "status": "ok",
        "lens": _lens_to_dict(lens),
        "catalog_version": _catalog_version_for(session, device),
    }


@router.delete("/lenses/custom/{lens_id}")
@limiter.limit("30/minute")
def delete_custom_lens(request: Request, lens_id: str, device_id: str, session: SessionDep):
    """Borra una lente CUSTOM propia, o (P7.2) cualquier lente del CATALOGO
    BASE si el device es admin (`_delete_base_lens` — antes, P7.1, esto
    rechazaba SIEMPRE con `BASE_LENS`; decision de producto: el historial
    de versiones .aN ya cubre el rollback, ver docs/backend.md).
    `device_id` va por query param (body en DELETE es antipatico para
    UnityWebRequest)."""
    lens = session.exec(
        select(CustomLens).where(CustomLens.lens_id == lens_id)
    ).first()
    if lens is None:
        catalog = session.exec(
            select(LensCatalog).where(LensCatalog.is_active == True)  # noqa: E712
        ).first()
        if catalog is not None and _active_base_lens_index(json.loads(catalog.data), lens_id) is not None:
            return _delete_base_lens(session, lens_id, device_id)
        raise HTTPException(status_code=404, detail="Lente no encontrada.")

    is_generic = lens.owner_device_pk is None
    device = _authorize_lens_write(session, device_id, need_admin=is_generic)
    if not isinstance(device, Device):
        return device
    if not is_generic and lens.owner_device_pk != device.id:
        return _denied("NOT_OWNER", "Esta lente pertenece a otro dispositivo.")

    session.delete(lens)
    session.commit()
    return {
        "status": "ok",
        "catalog_version": _catalog_version_for(session, device),
    }


# ---------------------------------------------------------------------------
# P7.3: reorden del catalogo BASE (drag & drop desde el panel/tablet). Mismo
# patron de clon-versionado `.aN` que P7.1/P7.2 (_update_base_lens/
# _add_base_lens/_delete_base_lens): nunca se pisa in-place. Las lentes
# CUSTOM por device NO participan de este orden — `get_lenses` siempre las
# agrega DESPUES de las lentes base, sin importar como esten en
# `custom_lenses`.
# ---------------------------------------------------------------------------
def _validate_lens_order(base_ids: list[str], order: list[str]) -> str | None:
    """Valida que `order` sea una permutacion EXACTA de `base_ids` (mismos
    ids, sin duplicados, sin desconocidos, sin faltantes). Devuelve un
    mensaje de error especifico (duplicado/desconocido/faltante) o None si
    es valida."""
    seen: set[str] = set()
    duplicates: list[str] = []
    for lens_id in order:
        if lens_id in seen:
            duplicates.append(lens_id)
        seen.add(lens_id)
    if duplicates:
        return f"order contiene id(s) duplicados: {sorted(set(duplicates))}."

    base_set = set(base_ids)
    unknown = [lens_id for lens_id in order if lens_id not in base_set]
    if unknown:
        return f"order contiene id(s) desconocidos (no estan en el catalogo): {unknown}."

    missing = [lens_id for lens_id in base_ids if lens_id not in seen]
    if missing:
        return f"order no incluye id(s) del catalogo: {missing}."

    return None


@router.post("/lenses/reorder")
@limiter.limit("30/minute")
def reorder_lenses(request: Request, body: LensReorderRequest, session: SessionDep):
    """Reordena el array `catalogo` del catalogo BASE activo segun `order`
    (lista de ids en el nuevo orden). Solo un admin puede hacerlo (mismo
    `_authorize_lens_write(need_admin=True)` que editar/agregar/borrar una
    lente base). `order` debe ser una permutacion EXACTA de los ids
    actualmente activos: mismo largo, sin duplicados, sin ids desconocidos
    y sin faltantes (422 con detalle de que fallo si no).

    Si `order` ya coincide con el orden actual, es un no-op: responde
    ok sin clonar una version `.aN` nueva (evita quemar historial por un
    reorden que no cambia nada). Si cambia, usa el mismo mecanismo de
    clon-versionado que el resto de las mutaciones del catalogo base
    (`_next_admin_lens_version`): la fila vieja se desactiva pero nunca se
    borra (rollback manual desde /admin/lenses).

    Las lentes CUSTOM por device no entran en este orden — no viven en este
    array, y `get_lenses` las agrega siempre al final del merge."""
    catalog = session.exec(
        select(LensCatalog).where(LensCatalog.is_active == True)  # noqa: E712
    ).first()
    if catalog is None:
        raise HTTPException(status_code=503, detail="No hay catalogo de lentes activo.")

    device = _authorize_lens_write(session, body.device_id, need_admin=True)
    if not isinstance(device, Device):
        return device

    data = json.loads(catalog.data)
    base_lenses: list[dict] = data.get("catalogo", [])
    base_ids = [l.get("id") for l in base_lenses]

    err = _validate_lens_order(base_ids, body.order)
    if err:
        raise HTTPException(status_code=422, detail=err)

    if body.order == base_ids:
        # No-op: el orden pedido es igual al actual, no se quema un .aN.
        return {"status": "ok", "catalog_version": _catalog_version_for(session, device)}

    new_data = json.loads(catalog.data)  # clon independiente para mutar
    by_id = {l.get("id"): l for l in new_data["catalogo"]}
    new_data["catalogo"] = [by_id[lens_id] for lens_id in body.order]
    new_version = _next_admin_lens_version(session, catalog.version)
    new_data["version"] = new_version

    catalog.is_active = False
    session.add(catalog)
    new_catalog = LensCatalog(
        version=new_version,
        data=json.dumps(new_data, ensure_ascii=False),
        is_active=True,
    )
    session.add(new_catalog)
    session.commit()

    return {
        "status": "ok",
        "catalog_version": _catalog_version_for(session, device),
    }


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
