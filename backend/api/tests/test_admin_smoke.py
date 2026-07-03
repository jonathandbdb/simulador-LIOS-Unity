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
