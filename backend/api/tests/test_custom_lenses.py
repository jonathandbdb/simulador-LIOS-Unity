"""Tests P7: modo de app por device, lentes custom/genericas y reemplazo de hardware.

Los endpoints de mutacion (/api/lenses/custom) estan rate-limited a 30/min/IP;
el `limiter.reset()` del conftest evita arrastre entre tests, y ningun test
individual llega a 30 requests.
"""
from datetime import date, timedelta

from app.config import settings

VALID_PARAMS = {
    "halo_intensity": {"default": 0.5, "min": 0.0, "max": 1.0},
    "destello_intensity": {"default": 0.3, "min": 0.0, "max": 1.0},
}


def _add_device(device_id, name="Test", status="active", app_mode="standard",
                is_admin=False, license_expiry=None):
    from app.database import engine
    from app.models import Device
    from sqlmodel import Session

    with Session(engine) as s:
        d = Device(device_id=device_id, name=name, status=status,
                   app_mode=app_mode, is_admin=is_admin, license_expiry=license_expiry)
        s.add(d)
        s.commit()
        s.refresh(d)
        return d.id


def _create_lens(client, device_id, scope="private", nombre="Lente test",
                 params=None):
    # OJO: `params if params is not None` y no `params or ...` — el caso
    # "params vacio" ({} es falsy) debe llegar tal cual al endpoint.
    return client.post("/api/lenses/custom", json={
        "device_id": device_id,
        "scope": scope,
        "nombre": nombre,
        "descripcion": "desc",
        "params": params if params is not None else VALID_PARAMS,
    })


def _login(client):
    r = client.post(
        "/admin/login",
        data={"username": settings.admin_default_user, "password": settings.admin_default_pass},
        follow_redirects=False,
    )
    assert r.status_code == 303
    return client


# ---------------------------------------------------------------------------
# Verify: campos nuevos
# ---------------------------------------------------------------------------
def test_verify_returns_mode_and_admin(client):
    # DEV_TEST_001 (seed) es pro + admin desde P7.
    r = client.post("/api/verify", json={"device_id": "DEV_TEST_001"})
    assert r.status_code == 200
    body = r.json()
    assert body["app_mode"] == "pro"
    assert body["is_admin"] is True

    _add_device("DEV_STD", app_mode="standard")
    r = client.post("/api/verify", json={"device_id": "DEV_STD"})
    assert r.status_code == 200
    body = r.json()
    assert body["app_mode"] == "standard"
    assert body["is_admin"] is False


# ---------------------------------------------------------------------------
# CRUD privadas: matriz de autorizacion
# ---------------------------------------------------------------------------
def test_create_private_lens_pro_ok(client):
    _add_device("DEV_PRO", app_mode="pro")
    r = _create_lens(client, "DEV_PRO")
    assert r.status_code == 201
    body = r.json()
    assert body["status"] == "ok"
    assert body["lens"]["id"].startswith("custom_")
    assert body["lens"]["origen"] == "custom"
    assert body["catalog_version"].startswith("0.")
    assert "+x" in body["catalog_version"]


def test_create_private_lens_standard_denied(client):
    _add_device("DEV_STD2", app_mode="standard")
    r = _create_lens(client, "DEV_STD2")
    assert r.status_code == 403
    assert r.json()["reason"] == "MODE_NOT_PRO"


def test_create_lens_unknown_device_denied_and_not_registered(client):
    r = _create_lens(client, "GHOST_DEVICE")
    assert r.status_code == 403
    assert r.json()["reason"] == "DEVICE_NOT_FOUND"
    # NO auto-registra (eso es exclusivo de verify).
    from app.database import engine
    from app.models import Device
    from sqlmodel import Session, select
    with Session(engine) as s:
        assert s.exec(select(Device).where(Device.device_id == "GHOST_DEVICE")).first() is None


def test_create_lens_inactive_device_denied(client):
    _add_device("DEV_SUSP", app_mode="pro", status="suspended")
    r = _create_lens(client, "DEV_SUSP")
    assert r.status_code == 403
    assert r.json()["reason"] == "DEVICE_NOT_AUTHORIZED"

    _add_device("DEV_EXP", app_mode="pro",
                license_expiry=date.today() - timedelta(days=1))
    r = _create_lens(client, "DEV_EXP")
    assert r.status_code == 403
    assert r.json()["reason"] == "DEVICE_NOT_AUTHORIZED"


# ---------------------------------------------------------------------------
# Genericas: solo admin
# ---------------------------------------------------------------------------
def test_generic_lens_requires_admin(client):
    _add_device("DEV_PRO2", app_mode="pro", is_admin=False)
    r = _create_lens(client, "DEV_PRO2", scope="generic")
    assert r.status_code == 403
    assert r.json()["reason"] == "NOT_ADMIN"

    _add_device("DEV_ADM", app_mode="pro", is_admin=True)
    r = _create_lens(client, "DEV_ADM", scope="generic")
    assert r.status_code == 201
    lens_id = r.json()["lens"]["id"]
    assert lens_id.startswith("generic_")

    # Un Pro no-admin NO puede editar ni borrar la generica (requisito 4).
    r = client.put(f"/api/lenses/custom/{lens_id}", json={
        "device_id": "DEV_PRO2", "nombre": "Hackeada", "descripcion": "", "params": VALID_PARAMS,
    })
    assert r.status_code == 403
    assert r.json()["reason"] == "NOT_ADMIN"
    r = client.delete(f"/api/lenses/custom/{lens_id}", params={"device_id": "DEV_PRO2"})
    assert r.status_code == 403
    assert r.json()["reason"] == "NOT_ADMIN"

    # El admin si puede editarla.
    r = client.put(f"/api/lenses/custom/{lens_id}", json={
        "device_id": "DEV_ADM", "nombre": "Generica v2", "descripcion": "", "params": VALID_PARAMS,
    })
    assert r.status_code == 200
    assert r.json()["lens"]["nombre"] == "Generica v2"


def test_update_delete_foreign_private_lens_denied(client):
    _add_device("DEV_OWNER", app_mode="pro")
    _add_device("DEV_OTHER", app_mode="pro")
    lens_id = _create_lens(client, "DEV_OWNER").json()["lens"]["id"]

    r = client.put(f"/api/lenses/custom/{lens_id}", json={
        "device_id": "DEV_OTHER", "nombre": "Robada", "descripcion": "", "params": VALID_PARAMS,
    })
    assert r.status_code == 403
    assert r.json()["reason"] == "NOT_OWNER"

    r = client.delete(f"/api/lenses/custom/{lens_id}", params={"device_id": "DEV_OTHER"})
    assert r.status_code == 403
    assert r.json()["reason"] == "NOT_OWNER"

    # El dueño si.
    r = client.delete(f"/api/lenses/custom/{lens_id}", params={"device_id": "DEV_OWNER"})
    assert r.status_code == 200


def test_lens_not_found_404(client):
    _add_device("DEV_PRO3", app_mode="pro")
    r = client.put("/api/lenses/custom/custom_deadbeef", json={
        "device_id": "DEV_PRO3", "nombre": "X", "descripcion": "", "params": VALID_PARAMS,
    })
    assert r.status_code == 404
    r = client.delete("/api/lenses/custom/custom_deadbeef", params={"device_id": "DEV_PRO3"})
    assert r.status_code == 404


# ---------------------------------------------------------------------------
# Validacion de payload
# ---------------------------------------------------------------------------
def test_lens_params_validation(client):
    _add_device("DEV_VAL", app_mode="pro")
    # min > default
    r = _create_lens(client, "DEV_VAL", params={
        "halo_intensity": {"default": 0.1, "min": 0.5, "max": 1.0},
    })
    assert r.status_code == 422
    # no numerico
    r = _create_lens(client, "DEV_VAL", params={
        "halo_intensity": {"default": "alto", "min": 0.0, "max": 1.0},
    })
    assert r.status_code == 422
    # claves de spec incompletas
    r = _create_lens(client, "DEV_VAL", params={
        "halo_intensity": {"default": 0.5, "min": 0.0},
    })
    assert r.status_code == 422
    # demasiadas claves
    many = {f"p{i}": {"default": 0.0, "min": 0.0, "max": 1.0} for i in range(21)}
    r = _create_lens(client, "DEV_VAL", params=many)
    assert r.status_code == 422
    # params vacio
    r = _create_lens(client, "DEV_VAL", params={})
    assert r.status_code == 422


# ---------------------------------------------------------------------------
# Merge y versionado de GET /api/lenses
# ---------------------------------------------------------------------------
def test_lenses_merge_and_versioning(client):
    # El engine SQLite en memoria es un singleton de modulo: la BD se comparte
    # entre tests de la misma sesion de pytest, asi que este test NO asume BD
    # virgen — compara estados relativos (antes/despues de sus propias lentes).
    base = client.get("/api/lenses").json()
    base_version = base["version"]
    base_count = len(base["catalogo"])

    _add_device("DEV_M1", app_mode="pro")
    _add_device("DEV_M2", app_mode="pro")
    _add_device("DEV_ADM2", app_mode="pro", is_admin=True)

    lens1 = _create_lens(client, "DEV_M1", nombre="Privada M1").json()["lens"]["id"]
    generic = _create_lens(client, "DEV_ADM2", scope="generic", nombre="Generica").json()["lens"]["id"]

    # Anonimo: base + genericas (sin las privadas de nadie).
    anon = client.get("/api/lenses").json()
    ids = [l["id"] for l in anon["catalogo"]]
    assert generic in ids and lens1 not in ids
    assert len(anon["catalogo"]) == base_count + 1
    assert anon["version"] != base_version and "+x" in anon["version"]
    # Campo origen solo en extras (la primera lente del merge es del blob base).
    by_id = {l["id"]: l for l in anon["catalogo"]}
    assert by_id[generic]["origen"] == "generic"
    assert "origen" not in anon["catalogo"][0]

    # Con device: + sus privadas, nunca las de otro.
    m1 = client.get("/api/lenses", params={"device_id": "DEV_M1"}).json()
    m1_ids = [l["id"] for l in m1["catalogo"]]
    assert lens1 in m1_ids and generic in m1_ids
    m2 = client.get("/api/lenses", params={"device_id": "DEV_M2"}).json()
    assert lens1 not in [l["id"] for l in m2["catalogo"]]

    # Versiones: distintas entre devices con customs distintas; estables
    # entre dos GETs sin cambios.
    assert m1["version"] != m2["version"]
    assert client.get("/api/lenses", params={"device_id": "DEV_M1"}).json()["version"] == m1["version"]

    # Editar la privada cambia la version de M1.
    r = client.put(f"/api/lenses/custom/{lens1}", json={
        "device_id": "DEV_M1", "nombre": "Privada M1 v2", "descripcion": "", "params": VALID_PARAMS,
    })
    assert r.status_code == 200
    assert client.get("/api/lenses", params={"device_id": "DEV_M1"}).json()["version"] != m1["version"]

    # Device suspendido responde como anonimo (purga de customs del cache).
    from app.database import engine
    from app.models import Device
    from sqlmodel import Session, select
    with Session(engine) as s:
        d = s.exec(select(Device).where(Device.device_id == "DEV_M1")).first()
        d.status = "suspended"
        s.add(d)
        s.commit()
    susp = client.get("/api/lenses", params={"device_id": "DEV_M1"}).json()
    assert lens1 not in [l["id"] for l in susp["catalogo"]]

    # device_id desconocido: como anonimo, sin auto-registro.
    unk = client.get("/api/lenses", params={"device_id": "NO_EXISTE"}).json()
    assert generic in [l["id"] for l in unk["catalogo"]]


def test_lenses_merge_skips_base_id_collision(client):
    """Extra cuyo lens_id colisiona con el catalogo base: se saltea."""
    from app.database import engine
    from app.models import CustomLens
    from sqlmodel import Session

    base = client.get("/api/lenses").json()
    base_id = base["catalogo"][0]["id"]
    with Session(engine) as s:
        s.add(CustomLens(owner_device_pk=None, lens_id=base_id,
                         nombre="Colision", params_json="{}"))
        s.commit()
    merged = client.get("/api/lenses").json()
    assert [l["id"] for l in merged["catalogo"]].count(base_id) == 1


# ---------------------------------------------------------------------------
# Panel admin
# ---------------------------------------------------------------------------
def test_admin_device_mode_and_admin_flags_persist(client):
    from app.database import engine
    from app.models import Device
    from sqlmodel import Session, select

    _login(client)
    pk = _add_device("DEV_PANEL", app_mode="standard")
    r = client.post(f"/admin/devices/{pk}/edit", data={
        "name": "Editado", "status": "active", "app_mode": "pro", "is_admin": "on",
    }, follow_redirects=False)
    assert r.status_code == 303
    with Session(engine) as s:
        d = s.exec(select(Device).where(Device.device_id == "DEV_PANEL")).first()
        assert d.app_mode == "pro" and d.is_admin is True

    # Sin checkbox -> is_admin False.
    r = client.post(f"/admin/devices/{pk}/edit", data={
        "name": "Editado", "status": "active", "app_mode": "standard",
    }, follow_redirects=False)
    assert r.status_code == 303
    with Session(engine) as s:
        d = s.exec(select(Device).where(Device.device_id == "DEV_PANEL")).first()
        assert d.app_mode == "standard" and d.is_admin is False


def test_admin_custom_lenses_page_and_delete(client):
    from app.database import engine
    from app.models import CustomLens
    from sqlmodel import Session, select

    _login(client)
    _add_device("DEV_PAGE", app_mode="pro")
    lens_id = _create_lens(client, "DEV_PAGE", nombre="VisibleEnPanel").json()["lens"]["id"]

    r = client.get("/admin/custom-lenses")
    assert r.status_code == 200
    assert "VisibleEnPanel" in r.text

    with Session(engine) as s:
        lens = s.exec(select(CustomLens).where(CustomLens.lens_id == lens_id)).first()
        pk = lens.id
    r = client.post(f"/admin/custom-lenses/{pk}/delete", follow_redirects=False)
    assert r.status_code == 303
    with Session(engine) as s:
        assert s.exec(select(CustomLens).where(CustomLens.lens_id == lens_id)).first() is None


def test_admin_device_delete_cascades_own_lenses_only(client):
    from app.database import engine
    from app.models import CustomLens
    from sqlmodel import Session, select

    _login(client)
    pk = _add_device("DEV_CASC", app_mode="pro", is_admin=True)
    own = _create_lens(client, "DEV_CASC", nombre="Propia").json()["lens"]["id"]
    generic = _create_lens(client, "DEV_CASC", scope="generic", nombre="Generica").json()["lens"]["id"]

    r = client.post(f"/admin/devices/{pk}/delete", follow_redirects=False)
    assert r.status_code == 303
    with Session(engine) as s:
        assert s.exec(select(CustomLens).where(CustomLens.lens_id == own)).first() is None
        # La generica sobrevive (owner NULL).
        assert s.exec(select(CustomLens).where(CustomLens.lens_id == generic)).first() is not None


def test_admin_replace_hardware(client):
    from app.database import engine
    from app.models import CustomLens, Device
    from sqlmodel import Session, select

    _login(client)
    pk = _add_device("OLD_HW", name="Consultorio 1", app_mode="pro")
    lens_id = _create_lens(client, "OLD_HW", nombre="Del consultorio").json()["lens"]["id"]

    # El visor nuevo ya se auto-registro como pending (placeholder).
    _add_device("NEW_HW", name="Visor NEW_HW", status="pending")

    r = client.post(f"/admin/devices/{pk}/replace", data={"new_device_id": "NEW_HW"},
                    follow_redirects=False)
    assert r.status_code == 303
    with Session(engine) as s:
        d = s.get(Device, pk)
        assert d.device_id == "NEW_HW"
        assert d.app_mode == "pro"
        assert d.last_seen is None and d.last_apk_version is None
        assert "[reemplazo]" in (d.notes or "")
        # El placeholder se elimino (queda UNA fila con NEW_HW).
        rows = s.exec(select(Device).where(Device.device_id == "NEW_HW")).all()
        assert len(rows) == 1 and rows[0].id == pk
        # La lente sigue colgando del mismo PK.
        lens = s.exec(select(CustomLens).where(CustomLens.lens_id == lens_id)).first()
        assert lens is not None and lens.owner_device_pk == pk

    # La lente ahora es visible para el device_id NUEVO.
    merged = client.get("/api/lenses", params={"device_id": "NEW_HW"}).json()
    assert lens_id in [l["id"] for l in merged["catalogo"]]


def test_admin_replace_rejects_real_target(client):
    _login(client)
    pk = _add_device("SRC_HW", app_mode="pro")
    _add_device("BUSY_HW", status="active")  # device real, no placeholder

    r = client.post(f"/admin/devices/{pk}/replace", data={"new_device_id": "BUSY_HW"},
                    follow_redirects=False)
    assert r.status_code == 303
    assert "error" in r.headers["location"]
    # Nada cambio.
    from app.database import engine
    from app.models import Device
    from sqlmodel import Session, select
    with Session(engine) as s:
        assert s.get(Device, pk).device_id == "SRC_HW"
        assert s.exec(select(Device).where(Device.device_id == "BUSY_HW")).first() is not None
