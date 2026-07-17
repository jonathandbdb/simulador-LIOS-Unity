"""mover lentes GENERICAS de custom_lenses al catalogo base (P7.2)

Decision de producto P7.2: las lentes "genericas" (`custom_lenses` con
`owner_device_pk IS NULL`) dejan de ser una categoria aparte — pasan a ser
lentes BASE mas dentro del blob versionado `lens_catalogs`. Esta migracion
mueve, en UNA sola operacion, cada fila generica preexistente al array
`catalogo` del blob ACTIVO (conservando su `lens_id` como `id`, `nombre`,
`descripcion`, `params`), en una nueva version `.aN` (mismo esquema de
sufijo que `_next_admin_lens_version` de `app/routers.py` — duplicado aca a
proposito: una migracion de datos no debe depender de codigo de la app que
puede cambiar en el futuro), y borra esas filas de `custom_lenses`.

Usa `sa.table`/`sa.column` (no los modelos SQLModel ni `autoload_with`) para
no atarse al schema ORM actual — patron recomendado por Alembic para
migraciones de datos.

Idempotente: si no hay filas genericas (`owner_device_pk IS NULL`), no-op
(no crea una version `.aN` nueva sin motivo real — correr esta migracion
dos veces, o contra una BD que nunca tuvo genericas, no hace nada la
segunda vez). Si el `lens_id` de una generica ya esta presente en el blob
activo (colision defensiva, no deberia pasar por construccion del id), esa
lente se SALTEA con un warning y queda en `custom_lenses` para revision
manual — no se pierde, pero tampoco se mueve a ciegas.

Revision ID: 0005
Revises: 0004
Create Date: 2026-07-17

"""
import json
import logging
import re
from datetime import datetime, timezone
from typing import Sequence, Union

import sqlalchemy as sa
from alembic import op

# revision identifiers, used by Alembic.
revision: str = "0005"
down_revision: Union[str, None] = "0004"
branch_labels: Union[str, Sequence[str], None] = None
depends_on: Union[str, Sequence[str], None] = None

logger = logging.getLogger(__name__)

_CUSTOM_LENSES = sa.table(
    "custom_lenses",
    sa.column("id", sa.Integer),
    sa.column("owner_device_pk", sa.Integer),
    sa.column("lens_id", sa.String),
    sa.column("nombre", sa.String),
    sa.column("descripcion", sa.String),
    sa.column("params_json", sa.String),
)

_LENS_CATALOGS = sa.table(
    "lens_catalogs",
    sa.column("id", sa.Integer),
    sa.column("version", sa.String),
    sa.column("data", sa.String),
    sa.column("is_active", sa.Boolean),
    sa.column("created_at", sa.DateTime),
)

# Mismo regex que `_VERSION_ROOT_RE` de app/routers.py (P7.1/P7.2),
# duplicado a proposito (ver docstring del modulo: sin dependencias de
# codigo de la app en una migracion de datos).
_VERSION_ROOT_RE = re.compile(r"^(.*?)(\.a(\d+))?$")


def _version_root_and_suffix(version: str) -> tuple[str, int]:
    m = _VERSION_ROOT_RE.match(version)
    root = m.group(1) if m else version
    suffix = int(m.group(3)) if m and m.group(3) else 0
    return root, suffix


def _next_version(conn, all_versions: list[str], active_version: str) -> str:
    root, _ = _version_root_and_suffix(active_version)
    max_n = 0
    for v in all_versions:
        r, n = _version_root_and_suffix(v)
        if r == root:
            max_n = max(max_n, n)
    return f"{root}.a{max_n + 1}"


def migrate_generic_lenses(conn) -> int:
    """Logica de datos, separada de `upgrade()` para poder testearla
    directamente contra una Connection (ver
    `tests/test_generic_lens_migration.py`) sin correr Alembic de verdad.
    Devuelve la cantidad de lentes movidas (0 = no-op, sea porque no habia
    genericas o porque todas colisionaron y se saltearon)."""
    generic_rows = conn.execute(
        sa.select(_CUSTOM_LENSES).where(_CUSTOM_LENSES.c.owner_device_pk.is_(None))
    ).mappings().all()
    if not generic_rows:
        return 0

    active = conn.execute(
        sa.select(_LENS_CATALOGS).where(_LENS_CATALOGS.c.is_active.is_(True))
    ).mappings().first()
    if active is None:
        logger.warning(
            "[migracion 0005] %d lente(s) generica(s) en custom_lenses pero "
            "no hay catalogo activo; se dejan sin migrar.",
            len(generic_rows),
        )
        return 0

    data = json.loads(active["data"])
    catalogo = data.setdefault("catalogo", [])
    existing_ids = {lens.get("id") for lens in catalogo}

    moved_pks: list[int] = []
    for row in generic_rows:
        lens_id = row["lens_id"]
        if lens_id in existing_ids:
            logger.warning(
                "[migracion 0005] lente generica '%s' colisiona con un id ya "
                "presente en el catalogo base; se saltea (queda en "
                "custom_lenses para revision manual).",
                lens_id,
            )
            continue
        catalogo.append({
            "id": lens_id,
            "nombre": row["nombre"],
            "descripcion": row["descripcion"],
            "params": json.loads(row["params_json"]),
        })
        existing_ids.add(lens_id)
        moved_pks.append(row["id"])

    if not moved_pks:
        return 0

    all_versions = [
        row[0] for row in conn.execute(sa.select(_LENS_CATALOGS.c.version))
    ]
    new_version = _next_version(conn, all_versions, active["version"])
    data["version"] = new_version

    conn.execute(
        _LENS_CATALOGS.update()
        .where(_LENS_CATALOGS.c.id == active["id"])
        .values(is_active=False)
    )
    conn.execute(
        _LENS_CATALOGS.insert().values(
            version=new_version,
            data=json.dumps(data, ensure_ascii=False),
            is_active=True,
            created_at=datetime.now(timezone.utc).replace(tzinfo=None),
        )
    )
    conn.execute(
        _CUSTOM_LENSES.delete().where(_CUSTOM_LENSES.c.id.in_(moved_pks))
    )
    logger.info(
        "[migracion 0005] %d lente(s) generica(s) movidas al catalogo base "
        "(nueva version %s); custom_lenses limpiado.",
        len(moved_pks), new_version,
    )
    return len(moved_pks)


def upgrade() -> None:
    migrate_generic_lenses(op.get_bind())


def downgrade() -> None:
    # Irreversible a proposito: una vez mergeadas al blob, no hay forma de
    # distinguir "generica migrada" de una lente base de siempre mirando
    # solo el JSON resultante (misma limitacion que cualquier otra version
    # `.aN` de edicion admin). Rollback real = activar a mano la version de
    # LensCatalog anterior desde /admin/lenses (mismo mecanismo de siempre).
    pass
