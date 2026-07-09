"""Tests del panel admin de versiones (una version activa POR APP).

Mockea `upload_file_streaming` (usado por `app.admin.router`) para no
depender de boto3/MinIO reales: siempre devuelve una URL y un SHA256 fijos.
"""
from sqlmodel import Session, select

from app.config import settings
from app.database import engine
from app.models import Version


def _login(client):
    r = client.post(
        "/admin/login",
        data={"username": settings.admin_default_user, "password": settings.admin_default_pass},
        follow_redirects=False,
    )
    assert r.status_code == 303
    return client


def _fake_upload(monkeypatch, url="http://testserver/files/apk/fake.apk", sha="deadbeef" * 8):
    monkeypatch.setattr(
        "app.admin.router.upload_file_streaming",
        lambda stream, key, content_type: (url, sha),
    )


def _upload_version(client, app: str, apk_version: str, min_apk_version: str = "0.1.0"):
    return client.post(
        "/admin/versions",
        data={"app": app, "apk_version": apk_version, "min_apk_version": min_apk_version, "changelog": ""},
        files={"apk_file": ("simulador.apk", b"fake-apk-bytes", "application/vnd.android.package-archive")},
        follow_redirects=False,
    )


def _versions_for_app(app: str) -> list[Version]:
    with Session(engine) as s:
        return list(s.exec(select(Version).where(Version.app == app)).all())


def test_upload_visor_does_not_touch_tablet_active(client, monkeypatch):
    _login(client)
    _fake_upload(monkeypatch)

    r = _upload_version(client, "visor", "1.0.0")
    assert r.status_code == 303

    # La version dummy de "tablet" (creada por el seed) sigue activa: subir
    # una version de "visor" no debe desactivar canales ajenos.
    tablet_versions = _versions_for_app("tablet")
    assert any(v.is_active for v in tablet_versions)


def test_upload_persists_apk_sha256(client, monkeypatch):
    _login(client)
    _fake_upload(monkeypatch, sha="cafebabe" * 8)

    r = _upload_version(client, "visor", "1.0.1")
    assert r.status_code == 303

    visor_versions = _versions_for_app("visor")
    active = [v for v in visor_versions if v.is_active]
    assert len(active) == 1
    assert active[0].apk_sha256 == "cafebabe" * 8
    assert active[0].apk_version == "1.0.1"


def test_upload_deactivates_only_same_app_previous_versions(client, monkeypatch):
    _login(client)
    _fake_upload(monkeypatch)

    _upload_version(client, "visor", "1.0.0")
    _upload_version(client, "visor", "1.0.1")

    visor_versions = _versions_for_app("visor")
    active_visor = [v for v in visor_versions if v.is_active]
    assert len(active_visor) == 1
    assert active_visor[0].apk_version == "1.0.1"

    # El canal tablet no se toco.
    tablet_versions = _versions_for_app("tablet")
    assert sum(1 for v in tablet_versions if v.is_active) == 1


def test_activate_deactivates_only_same_app(client, monkeypatch):
    _login(client)
    _fake_upload(monkeypatch)

    _upload_version(client, "visor", "2.0.0")
    _upload_version(client, "visor", "2.0.1")
    visor_versions = sorted(_versions_for_app("visor"), key=lambda v: v.id)
    older = visor_versions[0]  # 2.0.0, ya desactivada por el segundo upload

    tablet_before = {v.id: v.is_active for v in _versions_for_app("tablet")}

    r = client.post(f"/admin/versions/{older.id}/activate", follow_redirects=False)
    assert r.status_code == 303

    visor_versions_after = _versions_for_app("visor")
    active_visor = [v for v in visor_versions_after if v.is_active]
    assert len(active_visor) == 1
    assert active_visor[0].id == older.id

    tablet_after = {v.id: v.is_active for v in _versions_for_app("tablet")}
    assert tablet_before == tablet_after


def test_upload_rejects_invalid_app(client, monkeypatch):
    _login(client)
    _fake_upload(monkeypatch)

    before = len(_versions_for_app("visor")) + len(_versions_for_app("tablet"))
    r = _upload_version(client, "phone", "1.0.0")
    assert r.status_code == 303  # flash redirect, no exception
    after = len(_versions_for_app("visor")) + len(_versions_for_app("tablet"))
    assert after == before
