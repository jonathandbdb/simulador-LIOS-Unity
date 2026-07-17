"""Test de la migracion 0005 (P7.2): mover lentes GENERICAS preexistentes
de `custom_lenses` (owner_device_pk IS NULL) al catalogo BASE activo.

La migracion vive en `alembic/versions/0005_generic_lenses_to_catalog.py`
(el nombre del archivo arranca con un digito, no es un modulo Python
importable con `import` normal) — se carga por ruta con `importlib`, mismo
mecanismo que usaria Alembic para descubrirla, pero sin correr Alembic de
verdad: se llama a `migrate_generic_lenses` directo contra una Connection
del engine de tests (SQLite en memoria, StaticPool — la misma conexion
subyacente que usa el resto de la suite via `app.database.engine`).
"""
import importlib.util
import json
from pathlib import Path

from sqlmodel import Session, select

from app.database import engine
from app.models import CustomLens, LensCatalog

_MIGRATION_PATH = (
    Path(__file__).resolve().parent.parent
    / "alembic" / "versions" / "0005_generic_lenses_to_catalog.py"
)


def _load_migration_module():
    spec = importlib.util.spec_from_file_location("migration_0005_test", _MIGRATION_PATH)
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module


def _add_generic_lens(lens_id: str, nombre: str = "Vieja generica") -> None:
    with Session(engine) as s:
        s.add(CustomLens(
            owner_device_pk=None,
            lens_id=lens_id,
            nombre=nombre,
            descripcion="creada antes de P7.2",
            params_json=json.dumps({
                "halo_intensity": {"default": 0.2, "min": 0.0, "max": 1.0},
            }),
        ))
        s.commit()


def _active_catalog():
    with Session(engine) as s:
        return s.exec(
            select(LensCatalog).where(LensCatalog.is_active == True)  # noqa: E712
        ).first()


def test_migration_moves_generic_lens_to_catalog_and_removes_row(client):
    # `client` fixture ya corrio `init_db()` + `seed()`: hay un catalogo
    # activo real. Insertamos una fila "generica" preexistente a mano,
    # simulando el estado pre-P7.2.
    _add_generic_lens("generic_deadbeef", nombre="Vieja generica")

    before = _active_catalog()
    before_version = before.version
    before_count = len(json.loads(before.data)["catalogo"])

    module = _load_migration_module()
    with engine.begin() as conn:
        moved = module.migrate_generic_lenses(conn)
    assert moved == 1

    after = _active_catalog()
    assert after.version != before_version
    assert ".a" in after.version
    after_data = json.loads(after.data)
    assert len(after_data["catalogo"]) == before_count + 1
    moved_lens = next(l for l in after_data["catalogo"] if l["id"] == "generic_deadbeef")
    assert moved_lens["nombre"] == "Vieja generica"
    assert "halo_intensity" in moved_lens["params"]

    # La fila vieja de custom_lenses desaparecio.
    with Session(engine) as s:
        remaining = s.exec(
            select(CustomLens).where(CustomLens.lens_id == "generic_deadbeef")
        ).first()
        assert remaining is None

    # La lente migrada ahora se sirve por GET /api/lenses como lente BASE
    # (sin campo origen).
    merged = client.get("/api/lenses").json()
    served = next(l for l in merged["catalogo"] if l["id"] == "generic_deadbeef")
    assert "origen" not in served


def test_migration_is_idempotent_noop_without_generic_rows(client):
    before = _active_catalog()
    before_version = before.version

    module = _load_migration_module()
    with engine.begin() as conn:
        moved = module.migrate_generic_lenses(conn)
    assert moved == 0

    after = _active_catalog()
    assert after.version == before_version
    assert after.id == before.id  # ni siquiera crea una fila nueva


def _make_admin_device(device_id: str) -> None:
    from app.models import Device

    with Session(engine) as s:
        s.add(Device(
            device_id=device_id, name="Migration test admin", status="active",
            app_mode="pro", is_admin=True,
        ))
        s.commit()


def test_migration_skips_id_collision_with_existing_base_lens(client):
    """Si el `lens_id` de una fila 'generica' colisiona con un id YA
    presente en el catalogo base activo, la migracion la saltea (no la
    mueve, no pisa el id existente) y la deja en custom_lenses para
    revision manual.

    El id colisionante se genera con una alta real via la API (scope
    "generic", P7.2) en vez de tomar `catalogo[0]` a ciegas: la BD en
    memoria se comparte entre TODOS los tests de la sesion de pytest (ver
    nota en test_custom_lenses.py), y otro test de esa suite deja a
    proposito una fila en custom_lenses cuyo lens_id coincide con un id
    del catalogo — tomar el primer id del catalogo sin controlarlo puede
    pisar esa fila (UNIQUE constraint) en vez de probar lo que este test
    quiere probar.
    """
    _make_admin_device("DEV_MIGR_COLLISION")
    r = client.post("/api/lenses/custom", json={
        "device_id": "DEV_MIGR_COLLISION",
        "scope": "generic",
        "nombre": "Ya en el catalogo",
        "descripcion": "",
        "params": {"halo_intensity": {"default": 0.1, "min": 0.0, "max": 1.0}},
    })
    assert r.status_code == 201
    colliding_id = r.json()["lens"]["id"]  # ya vive en el catalogo BASE

    _add_generic_lens(colliding_id, nombre="Colisiona con una base")

    module = _load_migration_module()
    with engine.begin() as conn:
        moved = module.migrate_generic_lenses(conn)
    assert moved == 0  # se saltea, no se mueve nada

    # La fila stray se deja en custom_lenses para revision manual (no se
    # pierde, tampoco se mueve a ciegas pisando un id existente).
    with Session(engine) as s:
        remaining = s.exec(
            select(CustomLens).where(CustomLens.lens_id == colliding_id)
        ).first()
        assert remaining is not None
