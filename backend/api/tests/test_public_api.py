"""Tests de los endpoints publicos /api/* (manifest, lenses, verify)."""


def test_manifest_ok(client):
    """Sin ?app devuelve el canal 'visor' con el shape nuevo (sin PCK)."""
    r = client.get("/api/manifest.json")
    assert r.status_code == 200
    body = r.json()
    assert body["app"] == "visor"
    assert body["apk_version"] == "0.1.0"
    assert body["min_apk_version"] == "0.1.0"
    assert body["apk_url"]
    assert "apk_sha256" in body
    assert "changelog" in body
    assert "pck_url" not in body
    assert "pck_sha256" not in body
    assert "current_apk_version" not in body


def test_manifest_tablet_channel(client):
    r = client.get("/api/manifest.json", params={"app": "tablet"})
    assert r.status_code == 200
    body = r.json()
    assert body["app"] == "tablet"
    assert body["apk_version"]


def test_manifest_invalid_app_is_422(client):
    r = client.get("/api/manifest.json", params={"app": "phone"})
    assert r.status_code == 422


def test_lenses_ok(client):
    r = client.get("/api/lenses")
    assert r.status_code == 200
    body = r.json()
    assert body["version"]
    assert isinstance(body["catalogo"], list)
    assert len(body["catalogo"]) >= 1
    assert "id" in body["catalogo"][0]


def test_verify_valid_invalid_and_rate_limit(client):
    """Un solo test para no repartir la cuota de 10/min entre varios tests."""
    # 1) Device registrado por el seed (DEV_TEST_001): ok.
    r = client.post("/api/verify", json={"device_id": "DEV_TEST_001"})
    assert r.status_code == 200
    assert r.json()["status"] == "ok"

    # 2) Device desconocido: auto-registro -> 403 DEVICE_PENDING (ya no
    # DEVICE_NOT_FOUND; ver test_verify_unknown_auto_registers_as_pending).
    r = client.post("/api/verify", json={"device_id": "NOEXISTE"})
    assert r.status_code == 403
    assert r.json()["reason"] == "DEVICE_PENDING"

    # Van 2 requests consumidas de la cuota de 10/min. Agotamos las 8 que
    # quedan y confirmamos que la 11a cae en 429 (slowapi).
    for _ in range(8):
        client.post("/api/verify", json={"device_id": "NOEXISTE"})
    r = client.post("/api/verify", json={"device_id": "NOEXISTE"})
    assert r.status_code == 429


def test_verify_ignores_extra_legacy_field(client):
    """Un cliente Godot viejo mandando current_asset_version no debe romper
    (Pydantic v2 ignora campos extra por default)."""
    r = client.post(
        "/api/verify",
        json={
            "device_id": "DEV_TEST_001",
            "current_apk_version": "0.1.0",
            "current_asset_version": "0.4.0-clinical",
        },
    )
    assert r.status_code == 200
    assert r.json()["status"] == "ok"


def test_verify_unknown_auto_registers_as_pending(client):
    r = client.post("/api/verify", json={"device_id": "NUEVO_VISOR_001"})
    assert r.status_code == 403
    body = r.json()
    assert body["status"] == "denied"
    assert body["reason"] == "DEVICE_PENDING"

    from app.database import engine
    from app.models import Device
    from sqlmodel import Session, select

    with Session(engine) as s:
        d = s.exec(select(Device).where(Device.device_id == "NUEVO_VISOR_001")).first()
        assert d is not None
        assert d.status == "pending"
        assert d.notes == "auto-registrado por verify"
        assert d.last_seen is not None
        assert d.last_ip is not None

    # Un segundo verify del mismo device_id no duplica la fila y sigue pending.
    r2 = client.post("/api/verify", json={"device_id": "NUEVO_VISOR_001"})
    assert r2.status_code == 403
    assert r2.json()["reason"] == "DEVICE_PENDING"
    with Session(engine) as s:
        count = len(s.exec(select(Device).where(Device.device_id == "NUEVO_VISOR_001")).all())
        assert count == 1


def test_verify_pending_device(client):
    from app.database import engine
    from app.models import Device
    from sqlmodel import Session

    with Session(engine) as s:
        s.add(Device(device_id="DEV_PENDING", name="Pendiente", status="pending"))
        s.commit()

    r = client.post("/api/verify", json={"device_id": "DEV_PENDING"})
    assert r.status_code == 403
    assert r.json()["reason"] == "DEVICE_PENDING"


def test_verify_rejected_device_never_recreated(client):
    from app.database import engine
    from app.models import Device
    from sqlmodel import Session, select

    with Session(engine) as s:
        s.add(Device(device_id="DEV_REJECTED", name="Rechazado", status="rejected"))
        s.commit()

    r = client.post("/api/verify", json={"device_id": "DEV_REJECTED"})
    assert r.status_code == 403
    assert r.json()["reason"] == "DEVICE_REJECTED"

    with Session(engine) as s:
        devices = s.exec(select(Device).where(Device.device_id == "DEV_REJECTED")).all()
        assert len(devices) == 1
        assert devices[0].status == "rejected"


def test_verify_suspended_device(client):
    from app.database import engine
    from app.models import Device
    from sqlmodel import Session

    with Session(engine) as s:
        s.add(Device(device_id="DEV_SUSPENDED", name="Suspendido", status="suspended"))
        s.commit()

    r = client.post("/api/verify", json={"device_id": "DEV_SUSPENDED"})
    assert r.status_code == 403
    assert r.json()["reason"] == "DEVICE_SUSPENDED"


def test_verify_expired_license(client):
    from datetime import date, timedelta

    from app.database import engine
    from app.models import Device
    from sqlmodel import Session

    with Session(engine) as s:
        s.add(Device(
            device_id="DEV_EXPIRED", name="Vencido", status="active",
            license_expiry=date.today() - timedelta(days=1),
        ))
        s.commit()

    r = client.post("/api/verify", json={"device_id": "DEV_EXPIRED"})
    assert r.status_code == 403
    assert r.json()["reason"] == "LICENSE_EXPIRED"


def test_verify_auto_register_race_recovers_without_500(client, monkeypatch):
    """Dos requests concurrentes con el mismo device_id desconocido: ambas
    pasan el SELECT inicial (`device is None`) antes de que cualquiera haga
    commit; la segunda en commitear choca contra el unique constraint de
    `device_id` (IntegrityError real, no simulada). El handler debe atrapar
    ese error, hacer rollback, releer la fila ganadora y responder el 403
    que corresponda (DEVICE_PENDING) en vez de un 500.

    Corre ANTES del test de cap (`test_verify_pending_cap_falls_back_to_not_found`,
    mas abajo) a proposito: ese test deja 50 devices "pending" en la BD
    compartida en memoria (sin teardown, como el resto de este archivo) y
    haria que el chequeo de cupo de esta prueba fallara primero.

    No hay threads reales: se simula la interleaving parcheando
    `Session.commit` para que, la PRIMERA vez que se invoca dentro de este
    test (que es el commit de auto-registro del propio request), una
    sesion aparte inserte y comitee primero la fila "ganadora" con el mismo
    device_id -- reproduciendo el estado exacto que dispara el conflicto
    real en Postgres/SQLite, sin fabricar la excepcion a mano.
    """
    from sqlmodel import Session, select

    from app.database import engine
    from app.models import Device

    original_commit = Session.commit
    state = {"raced": False}

    def racing_commit(self, *args, **kwargs):
        if not state["raced"]:
            state["raced"] = True
            with Session(engine) as winner_session:
                winner_session.add(Device(
                    device_id="RACE_DEVICE",
                    name="Visor RACE_DEV",
                    status="pending",
                    notes="auto-registrado por verify",
                ))
                winner_session.commit()
        return original_commit(self, *args, **kwargs)

    monkeypatch.setattr(Session, "commit", racing_commit)

    r = client.post("/api/verify", json={"device_id": "RACE_DEVICE"})
    assert r.status_code == 403
    assert r.json()["reason"] == "DEVICE_PENDING"

    monkeypatch.undo()  # restaurar Session.commit antes de la query de verificacion

    with Session(engine) as s:
        rows = s.exec(select(Device).where(Device.device_id == "RACE_DEVICE")).all()
        assert len(rows) == 1  # el rollback del perdedor no dejo fila duplicada
        assert rows[0].status == "pending"


def test_verify_persists_apk_version_new_device(client):
    """El auto-registro persiste current_apk_version si vino en el body."""
    r = client.post(
        "/api/verify",
        json={"device_id": "NUEVO_VISOR_APK", "current_apk_version": "0.3.0"},
    )
    assert r.status_code == 403
    assert r.json()["reason"] == "DEVICE_PENDING"

    from app.database import engine
    from app.models import Device
    from sqlmodel import Session, select

    with Session(engine) as s:
        d = s.exec(select(Device).where(Device.device_id == "NUEVO_VISOR_APK")).first()
        assert d is not None
        assert d.last_apk_version == "0.3.0"


def test_verify_persists_apk_version_existing_device(client):
    """Un device ya existente actualiza last_apk_version en cada verify OK."""
    r = client.post(
        "/api/verify",
        json={"device_id": "DEV_TEST_001", "current_apk_version": "0.3.0"},
    )
    assert r.status_code == 200

    from app.database import engine
    from app.models import Device
    from sqlmodel import Session, select

    with Session(engine) as s:
        d = s.exec(select(Device).where(Device.device_id == "DEV_TEST_001")).first()
        assert d.last_apk_version == "0.3.0"

    # Un verify posterior sin current_apk_version (ausente) NO debe pisar el
    # valor ya conocido con None.
    r2 = client.post("/api/verify", json={"device_id": "DEV_TEST_001"})
    assert r2.status_code == 200
    with Session(engine) as s:
        d = s.exec(select(Device).where(Device.device_id == "DEV_TEST_001")).first()
        assert d.last_apk_version == "0.3.0"

    # Tampoco un current_apk_version vacio ("").
    r3 = client.post(
        "/api/verify",
        json={"device_id": "DEV_TEST_001", "current_apk_version": ""},
    )
    assert r3.status_code == 200
    with Session(engine) as s:
        d = s.exec(select(Device).where(Device.device_id == "DEV_TEST_001")).first()
        assert d.last_apk_version == "0.3.0"


def test_verify_pending_cap_falls_back_to_not_found(client):
    """Con MAX_PENDING_DEVICES pending ya existentes, un unknown nuevo no crea
    fila y responde DEVICE_NOT_FOUND (no DEVICE_PENDING)."""
    from app.database import engine
    from app.models import Device
    from app.routers import MAX_PENDING_DEVICES
    from sqlmodel import Session, select

    with Session(engine) as s:
        for i in range(MAX_PENDING_DEVICES):
            s.add(Device(device_id=f"CAP_PENDING_{i}", name=f"Pendiente {i}", status="pending"))
        s.commit()

    r = client.post("/api/verify", json={"device_id": "CAP_OVERFLOW"})
    assert r.status_code == 403
    assert r.json()["reason"] == "DEVICE_NOT_FOUND"

    with Session(engine) as s:
        assert s.exec(select(Device).where(Device.device_id == "CAP_OVERFLOW")).first() is None
