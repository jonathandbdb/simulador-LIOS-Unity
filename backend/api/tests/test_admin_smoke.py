"""Smoke test del login del panel admin (server-rendered, cookie JWT)."""
from app.config import settings


def test_login_success_sets_session_cookie(client):
    r = client.post(
        "/admin/login",
        data={"username": settings.admin_default_user, "password": settings.admin_default_pass},
        follow_redirects=False,
    )
    assert r.status_code == 303
    assert r.headers["location"] == "/admin/dashboard"
    assert "admin_session" in r.cookies


def test_login_wrong_password_no_cookie(client):
    r = client.post(
        "/admin/login",
        data={"username": settings.admin_default_user, "password": "not-the-password"},
        follow_redirects=False,
    )
    assert r.status_code == 200
    assert "admin_session" not in r.cookies


def _login(client):
    r = client.post(
        "/admin/login",
        data={"username": settings.admin_default_user, "password": settings.admin_default_pass},
        follow_redirects=False,
    )
    assert r.status_code == 303
    return client


def test_devices_approve_and_reject_change_status(client):
    from app.database import engine
    from app.models import Device
    from sqlmodel import Session

    _login(client)
    with Session(engine) as s:
        pending = Device(device_id="DEV_APPROVE_ME", name="Pendiente", status="pending")
        s.add(pending)
        s.commit()
        s.refresh(pending)
        pending_id = pending.id

        rejected = Device(device_id="DEV_REJECT_ME", name="Pendiente 2", status="pending")
        s.add(rejected)
        s.commit()
        s.refresh(rejected)
        rejected_id = rejected.id

    r = client.post(f"/admin/devices/{pending_id}/approve", follow_redirects=False)
    assert r.status_code == 303
    r = client.post(f"/admin/devices/{rejected_id}/reject", follow_redirects=False)
    assert r.status_code == 303

    with Session(engine) as s:
        p = s.get(Device, pending_id)
        rj = s.get(Device, rejected_id)
        assert p.status == "active"
        assert rj.status == "rejected"
