"""Tests de la retencion automatica de logs (UpdateLog).

`purge_old_logs` borra filas mas viejas que `settings.log_retention_days` y
conserva las mas recientes. `_maybe_purge_logs` agrega throttle (no repurga
si paso menos de 1h desde la ultima purga) para no hacer un DELETE en cada
POST /api/log.
"""
from datetime import timedelta

from app.config import settings
from app.utils import utcnow


def test_purge_old_logs_deletes_old_keeps_recent(client):
    from app.database import engine
    from app.models import UpdateLog
    from app.routers import purge_old_logs
    from sqlmodel import Session, select

    old_cutoff = utcnow() - timedelta(days=settings.log_retention_days + 5)
    recent_ts = utcnow() - timedelta(days=1)

    with Session(engine) as s:
        s.add(UpdateLog(device_id="DEV_OLD", event="viejo", created_at=old_cutoff))
        s.add(UpdateLog(device_id="DEV_RECENT", event="reciente", created_at=recent_ts))
        s.commit()

    with Session(engine) as s:
        deleted = purge_old_logs(s)
        assert deleted >= 1

    with Session(engine) as s:
        remaining = s.exec(select(UpdateLog).where(UpdateLog.device_id == "DEV_OLD")).all()
        assert remaining == []
        kept = s.exec(select(UpdateLog).where(UpdateLog.device_id == "DEV_RECENT")).all()
        assert len(kept) == 1


def test_maybe_purge_logs_throttled_within_an_hour(client, monkeypatch):
    """La segunda llamada dentro de la misma hora no vuelve a purgar."""
    import app.routers as routers_module
    from app.database import engine
    from app.models import UpdateLog
    from sqlmodel import Session

    monkeypatch.setattr(routers_module, "_last_log_purge_at", None)

    calls = {"n": 0}
    original = routers_module.purge_old_logs

    def counting_purge(session):
        calls["n"] += 1
        return original(session)

    monkeypatch.setattr(routers_module, "purge_old_logs", counting_purge)

    old_ts = utcnow() - timedelta(days=settings.log_retention_days + 1)
    with Session(engine) as s:
        s.add(UpdateLog(device_id="DEV_THROTTLE", event="viejo", created_at=old_ts))
        s.commit()

    with Session(engine) as s:
        routers_module._maybe_purge_logs(s)  # 1ra vez: _last_log_purge_at era None -> purga
    with Session(engine) as s:
        routers_module._maybe_purge_logs(s)  # 2da vez, <1h despues -> no repurga

    assert calls["n"] == 1


def test_post_log_triggers_purge_of_old_logs(client, monkeypatch):
    """POST /api/log dispara la purga (via _maybe_purge_logs) ademas de
    insertar el evento nuevo."""
    import app.routers as routers_module
    from app.database import engine
    from app.models import UpdateLog
    from sqlmodel import Session, select

    monkeypatch.setattr(routers_module, "_last_log_purge_at", None)

    old_ts = utcnow() - timedelta(days=settings.log_retention_days + 1)
    with Session(engine) as s:
        s.add(UpdateLog(device_id="DEV_VIA_POST", event="viejo", created_at=old_ts))
        s.commit()

    r = client.post(
        "/api/log",
        json={"device_id": "DEV_TEST_001", "events": [{"event": "manifest_check", "detail": ""}]},
    )
    assert r.status_code == 200

    with Session(engine) as s:
        remaining = s.exec(select(UpdateLog).where(UpdateLog.device_id == "DEV_VIA_POST")).all()
        assert remaining == []
