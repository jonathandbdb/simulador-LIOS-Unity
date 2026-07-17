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
# Genericas (P7.2): scope="generic" ya no crea una CustomLens — AGREGA la
# lente al CATALOGO BASE activo (nueva version .aN), solo admin.
# ---------------------------------------------------------------------------
def test_generic_lens_requires_admin(client):
    _add_device("DEV_PRO2", app_mode="pro", is_admin=False)
    r = _create_lens(client, "DEV_PRO2", scope="generic")
    assert r.status_code == 403
    assert r.json()["reason"] == "NOT_ADMIN"

    from app.database import engine
    from app.models import LensCatalog
    from sqlmodel import Session, select as sql_select

    def _active_raw_version():
        with Session(engine) as s:
            cat = s.exec(
                sql_select(LensCatalog).where(LensCatalog.is_active == True)  # noqa: E712
            ).first()
            return cat.version

    _add_device("DEV_ADM", app_mode="pro", is_admin=True)
    before_version = _active_raw_version()
    r = _create_lens(client, "DEV_ADM", scope="generic", nombre="Generica")
    assert r.status_code == 201
    body = r.json()
    lens_id = body["lens"]["id"]
    assert lens_id.startswith("generic_")
    assert "origen" not in body["lens"]  # ya es una lente BASE, no un extra
    # Nueva version .aN (mismo mecanismo que _update_base_lens): cambia
    # respecto de la version activa previa.
    assert _active_raw_version() != before_version
    assert ".a" in _active_raw_version()

    # Visible por GET /api/lenses SIN el campo origen (es parte del blob base).
    merged = client.get("/api/lenses").json()
    added = next(l for l in merged["catalogo"] if l["id"] == lens_id)
    assert added["nombre"] == "Generica"
    assert "origen" not in added

    # Un Pro no-admin NO puede editar ni borrar esta lente (sigue siendo la
    # rama de edicion/borrado de lentes BASE, ver test_edit_base_lens_*).
    r = client.put(f"/api/lenses/custom/{lens_id}", json={
        "device_id": "DEV_PRO2", "nombre": "Hackeada", "descripcion": "", "params": VALID_PARAMS,
    })
    assert r.status_code == 403
    assert r.json()["reason"] == "NOT_ADMIN"
    r = client.delete(f"/api/lenses/custom/{lens_id}", params={"device_id": "DEV_PRO2"})
    assert r.status_code == 403
    assert r.json()["reason"] == "NOT_ADMIN"

    # El admin si puede editarla (misma rama _update_base_lens que cualquier
    # otra lente del catalogo).
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
# P7.1: edicion de lentes BASE del catalogo por un admin (PUT/DELETE con un
# lens_id que no esta en custom_lenses pero SI en el catalogo base activo).
# Estos tests van ANTES de `test_lenses_merge_skips_base_id_collision` (mas
# abajo) a proposito: ese test inserta a mano una CustomLens cuyo lens_id
# colisiona con el id de la PRIMERA lente base — si corriera antes, el
# lookup por lens_id de estos tests encontraria esa fila stray en vez de
# caer a la rama de lente base. pytest colecciona en orden de definicion
# dentro de un mismo archivo (sin plugin de randomizacion en este repo), asi
# que la posicion en el archivo alcanza como garantia.
# ---------------------------------------------------------------------------
def test_admin_edit_base_lens_versions_and_history(client):
    import re

    from app.database import engine
    from app.models import LensCatalog
    from sqlmodel import Session, select as sql_select

    def _active_raw_version():
        # Version RAW de LensCatalog (no la fingerprint mergeada que GET
        # /api/lenses expone, que puede llevar "+xHASH" si ya hay
        # genericas creadas por otros tests de este mismo archivo).
        with Session(engine) as s:
            cat = s.exec(
                sql_select(LensCatalog).where(LensCatalog.is_active == True)  # noqa: E712
            ).first()
            return cat.version

    # Mismo regex que `_VERSION_ROOT_RE` de app/routers.py, duplicado aca
    # para predecir la version esperada SIN asumir que `base_version` llega
    # "limpia" (sin sufijo .aN) — P7.2 hace que crear/editar/borrar una
    # lente de admin mute el mismo blob base, asi que otro test de este
    # mismo archivo (p. ej. test_generic_lens_requires_admin) puede haber
    # corrido antes y dejado la version activa ya con un sufijo.
    _root_re = re.compile(r"^(.*?)(\.a(\d+))?$")

    def _root_and_suffix(version):
        m = _root_re.match(version)
        return m.group(1), (int(m.group(3)) if m.group(3) else 0)

    _add_device("DEV_BASE_ADM", app_mode="pro", is_admin=True)
    base = client.get("/api/lenses").json()
    base_lens = base["catalogo"][0]
    base_id = base_lens["id"]
    assert "origen" not in base_lens  # confirma que es una lente BASE, no un extra
    base_version = _active_raw_version()
    root, base_suffix = _root_and_suffix(base_version)

    some_key = next(iter(base_lens["params"]))
    params_v1 = dict(base_lens["params"])
    spec_v1 = dict(params_v1[some_key])
    spec_v1["default"] = spec_v1["min"]
    params_v1[some_key] = spec_v1

    r = client.put(f"/api/lenses/custom/{base_id}", json={
        "device_id": "DEV_BASE_ADM",
        "nombre": base_lens["nombre"] + " v2",
        "descripcion": base_lens["descripcion"],
        "params": params_v1,
    })
    assert r.status_code == 200
    body = r.json()
    assert body["status"] == "ok"
    assert body["lens"]["id"] == base_id
    assert body["lens"]["nombre"] == base_lens["nombre"] + " v2"
    assert "origen" not in body["lens"]
    v1 = f"{root}.a{base_suffix + 1}"
    assert _active_raw_version() == v1
    # catalog_version es la version MERGEADA para ese device: arranca con
    # v1, puede llevar "+xHASH" si ya hay genericas (de otros tests).
    assert body["catalog_version"].startswith(v1)

    # El cambio se sirve por GET /api/lenses con la version .a1 activa.
    merged = client.get("/api/lenses").json()
    assert merged["version"].startswith(v1)
    edited = next(l for l in merged["catalogo"] if l["id"] == base_id)
    assert edited["nombre"] == base_lens["nombre"] + " v2"
    assert edited["params"][some_key]["default"] == spec_v1["min"]
    assert "origen" not in edited

    # La fila vieja NUNCA se toca: sigue en BD, solo desactivada (historial/rollback).
    with Session(engine) as s:
        old = s.exec(sql_select(LensCatalog).where(LensCatalog.version == base_version)).first()
        assert old is not None and old.is_active is False

    # Segunda edicion: encadena a .a2 (raiz de "{base}.a1" sigue siendo
    # "{base}"), NUNCA ".a1.a1".
    params_v2 = dict(params_v1)
    spec_v2 = dict(params_v2[some_key])
    spec_v2["default"] = spec_v2["max"]
    params_v2[some_key] = spec_v2
    r = client.put(f"/api/lenses/custom/{base_id}", json={
        "device_id": "DEV_BASE_ADM",
        "nombre": base_lens["nombre"] + " v3",
        "descripcion": base_lens["descripcion"],
        "params": params_v2,
    })
    assert r.status_code == 200
    v2 = f"{root}.a{base_suffix + 2}"
    assert _active_raw_version() == v2
    assert r.json()["catalog_version"].startswith(v2)
    assert client.get("/api/lenses").json()["version"].startswith(v2)


def test_edit_base_lens_requires_admin(client):
    _add_device("DEV_BASE_PRO", app_mode="pro", is_admin=False)
    base_id = client.get("/api/lenses").json()["catalogo"][0]["id"]
    r = client.put(f"/api/lenses/custom/{base_id}", json={
        "device_id": "DEV_BASE_PRO", "nombre": "Hackeada", "descripcion": "", "params": VALID_PARAMS,
    })
    assert r.status_code == 403
    assert r.json()["reason"] == "NOT_ADMIN"


def test_delete_base_lens_by_admin_versions_and_history(client):
    """P7.2 (decision de producto): un admin SI puede borrar cualquier lente
    del catalogo BASE — antes (P7.1) esto rechazaba siempre con BASE_LENS.
    Nueva version .aN sin esa lente; la vieja queda de historial/rollback."""
    import json as _json

    from app.database import engine
    from app.models import LensCatalog
    from sqlmodel import Session, select as sql_select

    def _active_raw_version():
        with Session(engine) as s:
            cat = s.exec(
                sql_select(LensCatalog).where(LensCatalog.is_active == True)  # noqa: E712
            ).first()
            return cat.version

    _add_device("DEV_BASE_DEL", app_mode="pro", is_admin=True)
    before = client.get("/api/lenses").json()
    target_id = before["catalogo"][0]["id"]
    before_version = _active_raw_version()
    before_count = len(before["catalogo"])

    r = client.delete(f"/api/lenses/custom/{target_id}", params={"device_id": "DEV_BASE_DEL"})
    assert r.status_code == 200
    assert r.json()["status"] == "ok"

    assert _active_raw_version() != before_version
    assert ".a" in _active_raw_version()
    merged = client.get("/api/lenses").json()
    assert len(merged["catalogo"]) == before_count - 1
    assert target_id not in [l["id"] for l in merged["catalogo"]]

    # Rollback implicito por historial: la version vieja sigue en BD
    # (desactivada) con la lente intacta — activarla desde /admin/lenses
    # restaura la lente borrada.
    with Session(engine) as s:
        old = s.exec(sql_select(LensCatalog).where(LensCatalog.version == before_version)).first()
        assert old is not None and old.is_active is False
        old_data = _json.loads(old.data)
        assert target_id in [l["id"] for l in old_data["catalogo"]]


def test_delete_base_lens_requires_admin(client):
    _add_device("DEV_BASE_DEL_PRO", app_mode="pro", is_admin=False)
    base_id = client.get("/api/lenses").json()["catalogo"][0]["id"]
    r = client.delete(f"/api/lenses/custom/{base_id}", params={"device_id": "DEV_BASE_DEL_PRO"})
    assert r.status_code == 403
    assert r.json()["reason"] == "NOT_ADMIN"
    # Nada se borro.
    assert base_id in [l["id"] for l in client.get("/api/lenses").json()["catalogo"]]


# ---------------------------------------------------------------------------
# Merge y versionado de GET /api/lenses
# ---------------------------------------------------------------------------
def test_lenses_merge_and_versioning(client):
    # El engine SQLite en memoria es un singleton de modulo: la BD se comparte
    # entre tests de la misma sesion de pytest, asi que este test NO asume BD
    # virgen — compara estados relativos (antes/despues de sus propias lentes).
    base_before = client.get("/api/lenses").json()
    base_count_before_generic = len(base_before["catalogo"])

    _add_device("DEV_M1", app_mode="pro")
    _add_device("DEV_M2", app_mode="pro")
    _add_device("DEV_ADM2", app_mode="pro", is_admin=True)

    lens1 = _create_lens(client, "DEV_M1", nombre="Privada M1").json()["lens"]["id"]
    # P7.2: scope="generic" agrega la lente al catalogo BASE (nueva version
    # .aN) — deja de ser un "extra" del merge.
    generic = _create_lens(client, "DEV_ADM2", scope="generic", nombre="Generica").json()["lens"]["id"]

    # Anonimo: la generica YA es parte del catalogo base (sin las privadas
    # de nadie, esas si son extras del merge).
    anon = client.get("/api/lenses").json()
    ids = [l["id"] for l in anon["catalogo"]]
    assert generic in ids and lens1 not in ids
    assert len(anon["catalogo"]) == base_count_before_generic + 1
    by_id = {l["id"]: l for l in anon["catalogo"]}
    assert "origen" not in by_id[generic]  # ya es BASE, no un extra

    base_count = len(anon["catalogo"])
    base_version = anon["version"]  # version base "limpia": sin extras (customs) todavia

    # Con device: + sus privadas (customs), nunca las de otro. La generica
    # (base) la ve cualquiera.
    m1 = client.get("/api/lenses", params={"device_id": "DEV_M1"}).json()
    m1_ids = [l["id"] for l in m1["catalogo"]]
    assert lens1 in m1_ids and generic in m1_ids
    assert len(m1["catalogo"]) == base_count + 1
    assert m1["version"] != base_version and "+x" in m1["version"]
    assert next(l for l in m1["catalogo"] if l["id"] == lens1)["origen"] == "custom"

    m2 = client.get("/api/lenses", params={"device_id": "DEV_M2"}).json()
    assert lens1 not in [l["id"] for l in m2["catalogo"]]
    assert generic in [l["id"] for l in m2["catalogo"]]
    # M2 no tiene customs propias -> version BASE literal (sin "+x").
    assert m2["version"] == base_version

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

    # Device suspendido responde como anonimo (purga de customs del cache);
    # la generica (ahora base) sigue viendose porque no depende del device.
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
    assert generic in [l["id"] for l in susp["catalogo"]]

    # device_id desconocido: como anonimo, sin auto-registro; ve la generica
    # (es base) pero ninguna privada.
    unk = client.get("/api/lenses", params={"device_id": "NO_EXISTE"}).json()
    assert generic in [l["id"] for l in unk["catalogo"]]


def test_lenses_merge_skips_base_id_collision(client):
    """Custom cuyo lens_id colisiona con el catalogo base: se saltea (P7.2:
    ya no aplica a genericas — esa tabla es solo customs por device)."""
    from app.database import engine
    from app.models import CustomLens
    from sqlmodel import Session

    device_pk = _add_device("DEV_COLLISION", app_mode="pro")
    base = client.get("/api/lenses").json()
    base_id = base["catalogo"][0]["id"]
    with Session(engine) as s:
        s.add(CustomLens(owner_device_pk=device_pk, lens_id=base_id,
                         nombre="Colision", params_json="{}"))
        s.commit()
    merged = client.get("/api/lenses", params={"device_id": "DEV_COLLISION"}).json()
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

    # Normalizacion (P7): is_admin solo tiene sentido en modo pro. Un submit
    # manual con standard + checkbox tildado NO debe dejar admin en True
    # (la UI oculta el checkbox, pero la regla de integridad vive aca).
    r = client.post(f"/admin/devices/{pk}/edit", data={
        "name": "Editado", "status": "active", "app_mode": "standard", "is_admin": "on",
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
    """P7.2: la lente "generica" ya no vive en custom_lenses (es una lente
    BASE mas), asi que el cascade del borrado de device solo puede afectar
    sus propias CUSTOM: verificamos que se borra la propia y que la lente
    de admin (ahora parte del blob base) ni siquiera esta en esta tabla, y
    sigue viva en el catalogo despues de borrar el device que la creo."""
    from app.database import engine
    from app.models import CustomLens
    from sqlmodel import Session, select

    _login(client)
    pk = _add_device("DEV_CASC", app_mode="pro", is_admin=True)
    own = _create_lens(client, "DEV_CASC", nombre="Propia").json()["lens"]["id"]
    generic = _create_lens(client, "DEV_CASC", scope="generic", nombre="Generica").json()["lens"]["id"]

    with Session(engine) as s:
        # La generica nunca paso por custom_lenses.
        assert s.exec(select(CustomLens).where(CustomLens.lens_id == generic)).first() is None

    r = client.post(f"/admin/devices/{pk}/delete", follow_redirects=False)
    assert r.status_code == 303
    with Session(engine) as s:
        assert s.exec(select(CustomLens).where(CustomLens.lens_id == own)).first() is None

    # La lente de admin sigue viva en el catalogo BASE (no depende del
    # device que la creo — borrar ese device no la afecta).
    merged = client.get("/api/lenses").json()
    assert generic in [l["id"] for l in merged["catalogo"]]


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
