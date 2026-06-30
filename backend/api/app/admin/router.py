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
from app.admin.storage import delete_object, upload_file_streaming
from app.admin.templating import LANG_COOKIE, get_lang, render
from app.config import settings
from app.database import get_session
from app.models import AdminUser, Device, LensCatalog, UpdateLog, Version

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
    logs_total = session.exec(select(func.count()).select_from(UpdateLog)).one()
    active_version = session.exec(select(Version).where(Version.is_active == True)).first()  # noqa: E712
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
        logs_total=logs_total,
        active_version=active_version,
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
    devices = session.exec(select(Device).order_by(Device.created_at.desc())).all()
    return render(request, "devices.html", admin_user=admin, devices=devices)


@router.post("/devices")
def devices_create(
    admin: AdminDep, session: SessionDep,
    device_id: Annotated[str, Form()],
    name: Annotated[str, Form()],
    status: Annotated[str, Form()] = "active",
    license_expiry: Annotated[str, Form()] = "",
    notes: Annotated[str, Form()] = "",
):
    existing = session.exec(select(Device).where(Device.device_id == device_id)).first()
    if existing is not None:
        return _flash_redirect("/admin/devices", "Duplicate device_id", "error")
    now = datetime.utcnow()
    d = Device(
        device_id=device_id.strip(),
        name=name.strip(),
        status=status,
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
    license_expiry: Annotated[str, Form()] = "",
):
    d = session.get(Device, device_pk)
    if d is None:
        raise HTTPException(404)
    d.name = name.strip()
    d.status = status
    d.license_expiry = _parse_date(license_expiry)
    d.updated_at = datetime.utcnow()
    session.add(d)
    session.commit()
    return _flash_redirect("/admin/devices", "OK")


@router.post("/devices/{device_pk}/delete")
def devices_delete(admin: AdminDep, session: SessionDep, device_pk: int):
    d = session.get(Device, device_pk)
    if d is not None:
        session.delete(d)
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
# Versions (upload APK + PCK directo al bucket)
# ---------------------------------------------------------------------------
@router.get("/versions")
def versions_list(request: Request, admin: AdminDep, session: SessionDep):
    versions = session.exec(select(Version).order_by(desc(Version.created_at))).all()
    return render(request, "versions.html", admin_user=admin, versions=versions)


@router.post("/versions")
async def versions_create(
    admin: AdminDep, session: SessionDep,
    apk_version: Annotated[str, Form()],
    asset_version: Annotated[str, Form()],
    min_apk_version: Annotated[str, Form()],
    apk_file: Annotated[UploadFile, File()],
    pck_file: Annotated[UploadFile, File()],
    changelog: Annotated[str, Form()] = "",
):
    try:
        apk_key = f"apk/simulador-{apk_version}.apk"
        pck_key = f"pck/assets-{asset_version}.pck"
        apk_url, _apk_sha = upload_file_streaming(apk_file.file, apk_key, "application/vnd.android.package-archive")
        pck_url, pck_sha = upload_file_streaming(pck_file.file, pck_key, "application/octet-stream")
    except Exception as e:
        return _flash_redirect("/admin/versions", f"Upload error: {e}", "error")

    for prev in session.exec(select(Version).where(Version.is_active == True)).all():  # noqa: E712
        prev.is_active = False
        session.add(prev)
    v = Version(
        apk_version=apk_version.strip(),
        min_apk_version=min_apk_version.strip(),
        asset_version=asset_version.strip(),
        apk_url=apk_url,
        pck_url=pck_url,
        pck_sha256=pck_sha,
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
    for prev in session.exec(select(Version).where(Version.is_active == True)).all():  # noqa: E712
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
        # Borramos objetos del bucket (best-effort).
        for url in (v.apk_url, v.pck_url):
            key = url.split("/files/", 1)[-1] if "/files/" in url else None
            if key:
                delete_object(key)
        session.delete(v)
        session.commit()
    return _flash_redirect("/admin/versions", "OK")


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
    filename = f"logs_{datetime.utcnow().strftime('%Y%m%d_%H%M%S')}.csv"
    return StreamingResponse(
        iter([buf.getvalue()]),
        media_type="text/csv",
        headers={"Content-Disposition": f'attachment; filename="{filename}"'},
    )
