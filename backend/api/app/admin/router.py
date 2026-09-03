"""Router del panel admin.

Cablea login/logout, cambio de idioma, dashboard y las secciones CRUD.
Se monta en `main.py` con prefix `/admin`.
"""
import csv
import io
import json
import urllib.parse as up
from datetime import date, datetime
from typing import Annotated

from fastapi import APIRouter, Depends, File, Form, HTTPException, Request, UploadFile
from fastapi.responses import RedirectResponse, StreamingResponse
from sqlalchemy import desc
from sqlmodel import Session, func, select

from app.admin.auth import (
    COOKIE_TTL_HOURS,
    authenticate_user,
    clear_session_cookie,
    create_session_token,
    decode_session_token,
    get_current_admin,
    set_session_cookie,
)
from app.admin.provisioning import (
    build_provisioning_payload,
    has_usable_checksum,
    payload_to_json,
    render_qr_svg,
)
from app.admin.storage import delete_object, upload_file_streaming
from app.admin.templating import LANG_COOKIE, get_lang, render
from app.config import settings
from app.database import get_session
from app.models import AdminUser, CustomLens, Device, LensCatalog, UpdateLog, Version
from app.utils import utcnow

router = APIRouter(prefix="/admin", tags=["admin"])

SessionDep = Annotated[Session, Depends(get_session)]
AdminDep = Annotated[AdminUser, Depends(get_current_admin)]


# ---------------------------------------------------------------------------
# Idioma (i18n)
# ---------------------------------------------------------------------------
def _set_lang_cookie(response, code: str) -> None:
    response.set_cookie(LANG_COOKIE, code, max_age=60 * 60 * 24 * 365, path="/")


@router.post("/lang")
def set_lang_post(request: Request, code: Annotated[str, Form()]):
    resp = RedirectResponse(request.headers.get("referer") or "/admin/dashboard", status_code=303)
    _set_lang_cookie(resp, code)
    return resp


@router.get("/lang")
def set_lang_get(code: str, next: str = "/admin/dashboard"):
    resp = RedirectResponse(next, status_code=303)
    _set_lang_cookie(resp, code)
    return resp


# ---------------------------------------------------------------------------
# Login / Logout
# ---------------------------------------------------------------------------
@router.get("")
@router.get("/")
def admin_root(request: Request):
    if request.cookies.get("admin_session") and decode_session_token(request.cookies["admin_session"]):
        return RedirectResponse("/admin/dashboard", status_code=303)
    return RedirectResponse("/admin/login", status_code=303)


@router.get("/login")
def login_form(request: Request):
    return render(request, "login.html")


@router.post("/login")
def login_submit(
    request: Request,
    session: SessionDep,
    username: Annotated[str, Form()],
    password: Annotated[str, Form()],
):
    user = authenticate_user(session, username, password)
    if user is None:
        return render(request, "login.html", error=True)
    token = create_session_token(user.username)
    secure = request.url.scheme == "https"
    resp = RedirectResponse("/admin/dashboard", status_code=303)
    set_session_cookie(resp, token, secure=secure)
    return resp


@router.get("/logout")
def logout():
    resp = RedirectResponse("/admin/login", status_code=303)
    clear_session_cookie(resp)
    return resp


# ---------------------------------------------------------------------------
# Dashboard
# ---------------------------------------------------------------------------
@router.get("/dashboard")
def dashboard(request: Request, admin: AdminDep, session: SessionDep):
    devices_total = session.exec(select(func.count()).select_from(Device)).one()
    devices_active = session.exec(
        select(func.count()).select_from(Device).where(Device.status == "active")
    ).one()
    devices_pending = session.exec(
        select(func.count()).select_from(Device).where(Device.status == "pending")
    ).one()
    logs_total = session.exec(select(func.count()).select_from(UpdateLog)).one()
    active_version_visor = session.exec(
        select(Version).where(Version.is_active == True, Version.app == "visor")  # noqa: E712
    ).first()
    active_version_tablet = session.exec(
        select(Version).where(Version.is_active == True, Version.app == "tablet")  # noqa: E712
    ).first()
    active_catalog = session.exec(select(LensCatalog).where(LensCatalog.is_active == True)).first()  # noqa: E712
    recent_logs = session.exec(
        select(UpdateLog).order_by(desc(UpdateLog.created_at)).limit(20)
    ).all()
    # Aviso de seguridad: refleja la config del entorno (no el hash en BD).
    # Suficiente porque el seed crea el admin con admin_default_pass y no
    # hay UI de cambio de contrasena.
    insecure_pass = settings.admin_default_pass == "admin123"
    insecure_jwt = settings.jwt_secret == "dev-jwt-secret-change-me"
    return render(
        request, "dashboard.html",
        admin_user=admin,
        devices_total=devices_total,
        devices_active=devices_active,
        devices_pending=devices_pending,
        logs_total=logs_total,
        active_version_visor=active_version_visor,
        active_version_tablet=active_version_tablet,
        active_catalog=active_catalog,
        recent_logs=recent_logs,
        insecure_pass=insecure_pass,
        insecure_jwt=insecure_jwt,
    )


# ---------------------------------------------------------------------------
# Devices
# ---------------------------------------------------------------------------
def _parse_date(value: str | None) -> date | None:
    if not value:
        return None
    try:
        return date.fromisoformat(value)
    except ValueError:
        return None


def _flash_redirect(target: str, msg: str, kind: str = "ok") -> RedirectResponse:
    q = up.urlencode({"flash": msg, "flash_kind": kind})
    return RedirectResponse(f"{target}?{q}", status_code=303)


@router.get("/devices")
def devices_list(request: Request, admin: AdminDep, session: SessionDep):
    # Pending primero (requieren accion del admin), despues el resto por
    # fecha de creacion descendente.
    devices = session.exec(
        select(Device).order_by(
            (Device.status != "pending"), desc(Device.created_at)
        )
    ).all()
    pending_count = sum(1 for d in devices if d.status == "pending")
    # P7: contador de lentes custom por device (una sola query agregada).
    lens_counts = dict(session.exec(
        select(CustomLens.owner_device_pk, func.count())
        .where(CustomLens.owner_device_pk != None)  # noqa: E711
        .group_by(CustomLens.owner_device_pk)
    ).all())
    return render(
        request, "devices.html", admin_user=admin,
        devices=devices, pending_count=pending_count, lens_counts=lens_counts,
    )


@router.post("/devices")
def devices_create(
    admin: AdminDep, session: SessionDep,
    device_id: Annotated[str, Form()],
    name: Annotated[str, Form()],
    status: Annotated[str, Form()] = "active",
    app_mode: Annotated[str, Form()] = "standard",
    is_admin: Annotated[str, Form()] = "",
    license_expiry: Annotated[str, Form()] = "",
    notes: Annotated[str, Form()] = "",
):
    existing = session.exec(select(Device).where(Device.device_id == device_id)).first()
    if existing is not None:
        return _flash_redirect("/admin/devices", "Duplicate device_id", "error")
    now = utcnow()
    normalized_mode = app_mode if app_mode in ("standard", "pro") else "standard"
    d = Device(
        device_id=device_id.strip(),
        name=name.strip(),
        status=status,
        app_mode=normalized_mode,
        # is_admin solo tiene sentido en modo pro (P7); la UI ya lo oculta en
        # standard, pero esto es la regla de integridad real, no la UI.
        is_admin=bool(is_admin) and normalized_mode == "pro",  # checkbox: "on" si esta tildado, ausente si no
        license_expiry=_parse_date(license_expiry),
        notes=notes.strip() or None,
        created_at=now,
        updated_at=now,
    )
    session.add(d)
    session.commit()
    return _flash_redirect("/admin/devices", "OK")


@router.post("/devices/{device_pk}/edit")
def devices_edit(
    admin: AdminDep, session: SessionDep, device_pk: int,
    name: Annotated[str, Form()],
    status: Annotated[str, Form()],
    app_mode: Annotated[str, Form()] = "standard",
    is_admin: Annotated[str, Form()] = "",
    license_expiry: Annotated[str, Form()] = "",
):
    d = session.get(Device, device_pk)
    if d is None:
        raise HTTPException(404)
    d.name = name.strip()
    d.status = status
    d.app_mode = app_mode if app_mode in ("standard", "pro") else "standard"
    # is_admin solo tiene sentido en modo pro (P7); la UI ya lo oculta en
    # standard, pero esto es la regla de integridad real, no la UI.
    d.is_admin = bool(is_admin) and d.app_mode == "pro"
    d.license_expiry = _parse_date(license_expiry)
    d.updated_at = utcnow()
    session.add(d)
    session.commit()
    return _flash_redirect("/admin/devices", "OK")


@router.post("/devices/{device_pk}/approve")
def devices_approve(admin: AdminDep, session: SessionDep, device_pk: int):
    d = session.get(Device, device_pk)
    if d is None:
        raise HTTPException(404)
    d.status = "active"
    d.updated_at = utcnow()
    session.add(d)
    session.commit()
    return _flash_redirect("/admin/devices", "OK")


@router.post("/devices/{device_pk}/reject")
def devices_reject(admin: AdminDep, session: SessionDep, device_pk: int):
    d = session.get(Device, device_pk)
    if d is None:
        raise HTTPException(404)
    d.status = "rejected"
    d.updated_at = utcnow()
    session.add(d)
    session.commit()
    return _flash_redirect("/admin/devices", "OK")


@router.post("/devices/{device_pk}/delete")
def devices_delete(admin: AdminDep, session: SessionDep, device_pk: int):
    d = session.get(Device, device_pk)
    if d is not None:
        # Cascade app-level de sus lentes custom (ademas del ON DELETE CASCADE
        # de Postgres: en los tests SQLite el pragma de FKs esta off por
        # default, esta capa garantiza el mismo comportamiento). P7.2: ya no
        # hay lentes "genericas" con owner NULL en esta tabla (viven en el
        # blob base) — el filtro por owner_device_pk == d.id nunca alcanza
        # nada mas que las lentes propias de ESTE device, por construccion.
        own_lenses = session.exec(
            select(CustomLens).where(CustomLens.owner_device_pk == d.id)
        ).all()
        for lens in own_lenses:
            session.delete(lens)
        session.delete(d)
        session.commit()
    return _flash_redirect("/admin/devices", "OK")


@router.post("/devices/{device_pk}/replace")
def devices_replace(
    admin: AdminDep, session: SessionDep, device_pk: int,
    new_device_id: Annotated[str, Form()],
):
    """Reemplazo de hardware (P7): re-apunta la fila del device al device_id
    del visor nuevo. Licencia, modo, flag admin y lentes custom (que cuelgan
    del PK int, no del string) se conservan solas.

    Si el visor nuevo ya se auto-registro (fila placeholder "pending"/
    "rejected" sin lentes), se elimina ese placeholder. Si el destino es un
    device real (activo/suspendido o con lentes), se rechaza: nunca merge
    silencioso.
    """
    d = session.get(Device, device_pk)
    if d is None:
        raise HTTPException(404)
    new_id = new_device_id.strip()
    if not new_id or new_id == d.device_id:
        return _flash_redirect("/admin/devices", "new device_id invalido", "error")

    other = session.exec(select(Device).where(Device.device_id == new_id)).first()
    if other is not None:
        other_lens_count = session.exec(
            select(func.count()).select_from(CustomLens)
            .where(CustomLens.owner_device_pk == other.id)
        ).one()
        if other.status not in ("pending", "rejected") or other_lens_count > 0:
            return _flash_redirect(
                "/admin/devices",
                "El device_id destino pertenece a otro dispositivo real",
                "error",
            )
        session.delete(other)  # placeholder del auto-registro: se descarta
        # Flush explicito: sin esto SQLAlchemy puede ordenar el UPDATE del
        # device_id ANTES del DELETE del placeholder y pisar el unique.
        session.flush()

    old_id = d.device_id
    d.device_id = new_id
    # Datos del hardware viejo: se resetean (eran de otro aparato).
    d.last_seen = None
    d.last_ip = None
    d.last_apk_version = None
    d.updated_at = utcnow()
    stamp = f"[reemplazo] {old_id} -> {new_id} ({date.today().isoformat()})"
    d.notes = f"{d.notes}\n{stamp}" if d.notes else stamp
    session.add(d)
    session.commit()
    return _flash_redirect("/admin/devices", "OK")


# ---------------------------------------------------------------------------
# Lenses
# ---------------------------------------------------------------------------
@router.get("/lenses")
def lenses_list(request: Request, admin: AdminDep, session: SessionDep):
    catalogs_raw = session.exec(select(LensCatalog).order_by(desc(LensCatalog.created_at))).all()
    catalogs = []
    for c in catalogs_raw:
        try:
            count = len(json.loads(c.data).get("catalogo", []))
        except Exception:
            count = 0
        catalogs.append({
            "id": c.id,
            "version": c.version,
            "is_active": c.is_active,
            "created_at": c.created_at,
            "lens_count": count,
        })
    # Esqueleto con el esquema clinico actual (10 params, ver defaults/lentes.json).
    default_json = json.dumps(
        {"version": "1.0.0", "catalogo": [
            {"id": "monofocal_default", "nombre": "Monofocal", "descripcion": "",
             "params": {
                 "foco_lejos_m":       {"default": 6.0,  "min": 0.0, "max": 20.0},
                 "foco_intermedio_m":  {"default": 0.0,  "min": 0.0, "max": 20.0},
                 "foco_cerca_m":       {"default": 0.0,  "min": 0.0, "max": 20.0},
                 "profundidad_foco_m": {"default": 1.2,  "min": 0.1, "max": 5.0},
                 "desenfoque_max":     {"default": 0.9,  "min": 0.0, "max": 1.0},
                 "halo_intensity":     {"default": 0.03, "min": 0.0, "max": 1.0},
                 "halo_extra_rings":   {"default": 0.0,  "min": 0.0, "max": 1.0},
                 "contrast_loss":      {"default": 0.0,  "min": 0.0, "max": 0.6},
                 "destello_intensity": {"default": 0.0,  "min": 0.0, "max": 1.0},
                 "destello_rayos":     {"default": 0.0,  "min": 0.0, "max": 16.0},
             }}
        ]},
        indent=2, ensure_ascii=False,
    )
    # Catalogo activo serializado para alimentar el editor visual.
    active = next((c for c in catalogs_raw if c.is_active), None)
    active_data = active.data if active else default_json
    active_version = active.version if active else "1.0.0"
    return render(
        request, "lenses.html", admin_user=admin,
        catalogs=catalogs, default_json=default_json,
        active_data=active_data, active_version=active_version,
    )


@router.post("/lenses")
def lenses_create(
    admin: AdminDep, session: SessionDep,
    version: Annotated[str, Form()],
    json_data: Annotated[str, Form()],
):
    try:
        parsed = json.loads(json_data)
    except json.JSONDecodeError as e:
        return _flash_redirect("/admin/lenses", f"Invalid JSON: {e}", "error")
    if not isinstance(parsed, dict) or not isinstance(parsed.get("catalogo"), list):
        return _flash_redirect("/admin/lenses", "JSON must contain 'catalogo' array", "error")
    parsed.setdefault("version", version)
    # Desactivar todos los catalogos previos.
    for prev in session.exec(select(LensCatalog).where(LensCatalog.is_active == True)).all():  # noqa: E712
        prev.is_active = False
        session.add(prev)
    cat = LensCatalog(version=version, data=json.dumps(parsed, ensure_ascii=False), is_active=True)
    session.add(cat)
    session.commit()
    return _flash_redirect("/admin/lenses", "OK")


@router.post("/lenses/{catalog_pk}/activate")
def lenses_activate(admin: AdminDep, session: SessionDep, catalog_pk: int):
    target = session.get(LensCatalog, catalog_pk)
    if target is None:
        raise HTTPException(404)
    for prev in session.exec(select(LensCatalog).where(LensCatalog.is_active == True)).all():  # noqa: E712
        prev.is_active = False
        session.add(prev)
    target.is_active = True
    session.add(target)
    session.commit()
    return _flash_redirect("/admin/lenses", "OK")


# ---------------------------------------------------------------------------
# Custom lenses (P7) — ver/borrar; la creacion/edicion es de los devices.
# P7.2: esta tabla ya solo tiene lentes CUSTOM privadas por device — las
# "genericas" dejaron de existir aca: pasaron a ser lentes BASE mas del
# blob versionado (se editan/borran/consultan su historial desde
# /admin/lenses, mecanismo .aN). Por eso el filtro por scope generic/private
# desaparecio; solo queda filtrar por dispositivo dueño. El badge
# "Generica" del template queda como defensivo por si una fila vieja con
# `owner_device_pk IS NULL` sobrevive (p. ej. colision de id detectada por
# la migracion 0005, que la deja sin migrar para revision manual).
# ---------------------------------------------------------------------------
@router.get("/custom-lenses")
def custom_lenses_list(
    request: Request, admin: AdminDep, session: SessionDep,
    device_pk: int | None = None,
):
    query = select(CustomLens).order_by(desc(CustomLens.updated_at))
    if device_pk is not None:
        query = query.where(CustomLens.owner_device_pk == device_pk)
    lenses = session.exec(query).all()

    # Datos del dueño para mostrar (nombre + device_id corto).
    devices_by_pk = {
        d.id: d for d in session.exec(select(Device)).all()
    }
    rows = []
    for lens in lenses:
        owner = devices_by_pk.get(lens.owner_device_pk) if lens.owner_device_pk else None
        try:
            params_pretty = json.dumps(json.loads(lens.params_json), indent=2, ensure_ascii=False)
        except json.JSONDecodeError:
            params_pretty = lens.params_json
        rows.append({"lens": lens, "owner": owner, "params_pretty": params_pretty})
    return render(
        request, "custom_lenses.html", admin_user=admin,
        rows=rows, device_pk=device_pk,
        devices=sorted(devices_by_pk.values(), key=lambda d: d.name.lower()),
    )


@router.post("/custom-lenses/{lens_pk}/delete")
def custom_lenses_delete(admin: AdminDep, session: SessionDep, lens_pk: int):
    lens = session.get(CustomLens, lens_pk)
    if lens is not None:
        session.delete(lens)
        session.commit()
    return _flash_redirect("/admin/custom-lenses", "OK")


# ---------------------------------------------------------------------------
# Versions (upload APK directo al bucket)
# ---------------------------------------------------------------------------
@router.get("/versions")
def versions_list(request: Request, admin: AdminDep, session: SessionDep):
    versions = session.exec(select(Version).order_by(desc(Version.created_at))).all()
    return render(request, "versions.html", admin_user=admin, versions=versions)


_VALID_APPS = {"visor", "tablet"}


@router.post("/versions")
async def versions_create(
    admin: AdminDep, session: SessionDep,
    app: Annotated[str, Form()],
    apk_version: Annotated[str, Form()],
    min_apk_version: Annotated[str, Form()],
    apk_file: Annotated[UploadFile, File()],
    changelog: Annotated[str, Form()] = "",
):
    app = app.strip().lower()
    if app not in _VALID_APPS:
        return _flash_redirect("/admin/versions", f"Invalid app channel: {app}", "error")

    try:
        apk_key = f"apk/{app}/simulador-{app}-{apk_version}.apk"
        apk_url, apk_sha = upload_file_streaming(apk_file.file, apk_key, "application/vnd.android.package-archive")
    except Exception as e:
        return _flash_redirect("/admin/versions", f"Upload error: {e}", "error")

    # Desactivar solo las versiones previas del MISMO canal (una activa por app).
    for prev in session.exec(
        select(Version).where(Version.is_active == True, Version.app == app)  # noqa: E712
    ).all():
        prev.is_active = False
        session.add(prev)
    v = Version(
        app=app,
        apk_version=apk_version.strip(),
        min_apk_version=min_apk_version.strip(),
        apk_url=apk_url,
        apk_sha256=apk_sha,
        changelog=changelog.strip(),
        is_active=True,
    )
    session.add(v)
    session.commit()
    return _flash_redirect("/admin/versions", "OK")


@router.post("/versions/{version_pk}/activate")
def versions_activate(admin: AdminDep, session: SessionDep, version_pk: int):
    target = session.get(Version, version_pk)
    if target is None:
        raise HTTPException(404)
    # Desactivar solo las previas del MISMO canal que la version a activar.
    for prev in session.exec(
        select(Version).where(Version.is_active == True, Version.app == target.app)  # noqa: E712
    ).all():
        prev.is_active = False
        session.add(prev)
    target.is_active = True
    session.add(target)
    session.commit()
    return _flash_redirect("/admin/versions", "OK")


@router.post("/versions/{version_pk}/delete")
def versions_delete(admin: AdminDep, session: SessionDep, version_pk: int):
    v = session.get(Version, version_pk)
    if v is not None:
        # Borramos el objeto del bucket (best-effort). Sin PCK: solo APK.
        key = v.apk_url.split("/files/", 1)[-1] if "/files/" in v.apk_url else None
        if key:
            delete_object(key)
        session.delete(v)
        session.commit()
    return _flash_redirect("/admin/versions", "OK")


# ---------------------------------------------------------------------------
# Provisioning QR (Android Enterprise) para recuperacion remota de tablets
# Device Owner tras un factory reset. Ver app/admin/provisioning.py y
# docs/backend.md ("Auth y panel admin" > Provisioning).
# ---------------------------------------------------------------------------
def _provisioning_context(
    session: Session,
    *,
    wifi_ssid: str = "",
    wifi_password: str = "",
    locale: str = "",
    timezone: str = "",
) -> dict:
    """Arma el contexto Jinja para `provisioning.html`, compartido entre el
    `GET` (pagina base, sin WiFi) y el `POST` (form completo, ver abajo)."""
    version = session.exec(
        select(Version).where(Version.is_active == True, Version.app == "tablet")  # noqa: E712
    ).first()

    ctx: dict = {"version": version, "signature_checksum": settings.provisioning_signature_checksum}
    if version is None:
        return ctx
    if not has_usable_checksum(version):
        # Ni checksum de firma (.env) ni un apk_sha256 utilizable en la
        # version activa (dummy del seed con "" o un hex corrupto): nunca
        # armar el payload/QR con un checksum vacio o tirar 500 por
        # bytes.fromhex — se avisa y no se genera QR.
        ctx["no_checksum"] = True
        return ctx

    payload = build_provisioning_payload(
        version,
        wifi_ssid=wifi_ssid.strip(),
        wifi_password=wifi_password,
        locale=locale.strip(),
        timezone=timezone.strip(),
    )
    ctx["payload_json"] = payload_to_json(payload)
    ctx["qr_svg"] = render_qr_svg(payload)
    ctx["used_signature_checksum"] = bool(settings.provisioning_signature_checksum)
    ctx["wifi_ssid"] = wifi_ssid
    ctx["locale"] = locale
    ctx["timezone"] = timezone
    return ctx


@router.get("/provisioning")
def provisioning_page(request: Request, admin: AdminDep, session: SessionDep):
    """Pagina base: SIN campos de WiFi/locale/timezone en la query string a
    proposito. `wifi_password` es sensible y un GET lo dejaria en el
    historial del navegador, el `Referer` de requests salientes y el access
    log de Caddy (delante de la app, loguea la URI completa) — ver
    `POST /admin/provisioning` mas abajo, que es donde vive el form real."""
    ctx = _provisioning_context(session)
    return render(request, "provisioning.html", admin_user=admin, **ctx)


@router.post("/provisioning")
def provisioning_generate(
    request: Request, admin: AdminDep, session: SessionDep,
    wifi_ssid: Annotated[str, Form()] = "",
    wifi_password: Annotated[str, Form()] = "",
    locale: Annotated[str, Form()] = "",
    timezone: Annotated[str, Form()] = "",
):
    """Genera el QR con los campos opcionales del form.

    Deliberadamente `POST` (body), no `GET` (query string): `wifi_password`
    viaja en texto plano hasta convertirse en `PROVISIONING_WIFI_PASSWORD`
    dentro del QR, y una query string sensible queda en el historial del
    navegador, el `Referer` de requests salientes que dispare esta pagina, y
    el access log de Caddy — que esta DELANTE de esta app (`reverse_proxy
    api:8000` en `backend/Caddyfile`) y loguea la URI completa sin que
    ningun filtro de la app pueda tocarlo. Con POST el valor va en el body:
    ninguno de esos tres lugares lo ve. El `_RedactWifiPasswordFilter` sobre
    `uvicorn.access` (`app/main.py`) se mantiene como defensa en
    profundidad, por si alguien pega la URL con `?wifi_password=...` a mano
    (soporta ambos casos, no asume que nunca va a pasar)."""
    ctx = _provisioning_context(
        session,
        wifi_ssid=wifi_ssid,
        wifi_password=wifi_password,
        locale=locale,
        timezone=timezone,
    )
    return render(request, "provisioning.html", admin_user=admin, **ctx)


# ---------------------------------------------------------------------------
# Logs (con filtros + paginacion + export CSV)
# ---------------------------------------------------------------------------
PAGE_SIZE = 50


def _build_logs_query(device_id: str, event: str, date_from: str, date_to: str):
    stmt = select(UpdateLog)
    if device_id:
        stmt = stmt.where(UpdateLog.device_id == device_id)
    if event:
        stmt = stmt.where(UpdateLog.event == event)
    df = _parse_date(date_from)
    dt = _parse_date(date_to)
    if df:
        stmt = stmt.where(UpdateLog.created_at >= datetime.combine(df, datetime.min.time()))
    if dt:
        stmt = stmt.where(UpdateLog.created_at <= datetime.combine(dt, datetime.max.time()))
    return stmt


@router.get("/logs")
def logs_list(
    request: Request, admin: AdminDep, session: SessionDep,
    device_id: str = "", event: str = "",
    date_from: str = "", date_to: str = "",
    page: int = 1,
):
    page = max(1, page)
    stmt = _build_logs_query(device_id, event, date_from, date_to).order_by(desc(UpdateLog.created_at))
    total = session.exec(select(func.count()).select_from(stmt.subquery())).one()
    total_pages = max(1, (total + PAGE_SIZE - 1) // PAGE_SIZE)
    logs = session.exec(stmt.offset((page - 1) * PAGE_SIZE).limit(PAGE_SIZE)).all()
    qs = up.urlencode({
        "device_id": device_id, "event": event,
        "date_from": date_from, "date_to": date_to,
    })
    return render(
        request, "logs.html",
        admin_user=admin,
        logs=logs,
        filters={"device_id": device_id, "event": event, "date_from": date_from, "date_to": date_to},
        page=page, total_pages=total_pages, qs=qs,
        log_retention_days=settings.log_retention_days,
    )


@router.get("/logs.csv")
def logs_csv(
    admin: AdminDep, session: SessionDep,
    device_id: str = "", event: str = "",
    date_from: str = "", date_to: str = "",
):
    stmt = _build_logs_query(device_id, event, date_from, date_to).order_by(UpdateLog.created_at)
    rows = session.exec(stmt).all()
    buf = io.StringIO()
    w = csv.writer(buf)
    w.writerow(["created_at", "device_id", "event", "detail"])
    for r in rows:
        w.writerow([r.created_at.isoformat(), r.device_id, r.event, r.detail])
    buf.seek(0)
    filename = f"logs_{utcnow().strftime('%Y%m%d_%H%M%S')}.csv"
    return StreamingResponse(
        iter([buf.getvalue()]),
        media_type="text/csv",
        headers={"Content-Disposition": f'attachment; filename="{filename}"'},
    )
