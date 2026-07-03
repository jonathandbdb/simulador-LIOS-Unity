"""Aplica migraciones Alembic al arrancar el contenedor.

Reemplaza el `SQLModel.metadata.create_all` que se usaba hasta ahora en el
arranque (`app.database.init_db` sigue existiendo solo como fallback para
tests). Maneja la adopcion de Alembic en una BD ya existente creada por
`create_all` en despliegues previos: si detecta tablas de la app pero no
`alembic_version`, asume que el schema ya coincide con la revision INICIAL
(`_INITIAL_REVISION`, no necesariamente `head`) y hace `stamp` a esa revision
puntual antes de aplicar el resto de las migraciones pendientes con
`upgrade head`. Estampar `head` directamente seria incorrecto en cuanto
exista una revision posterior a la inicial: la BD vieja se marcaria como si
ya tuviera ese DDL aplicado sin haberlo corrido nunca (columnas faltantes
silenciosas).
"""
import logging
from pathlib import Path

from alembic import command
from alembic.config import Config
from sqlalchemy import inspect

from app.database import engine

logger = logging.getLogger(__name__)

_API_DIR = Path(__file__).resolve().parent.parent
_ALEMBIC_INI = _API_DIR / "alembic.ini"
_ALEMBIC_DIR = _API_DIR / "alembic"

# Revision que representa el schema que ya generaba `create_all` antes de
# introducir Alembic (ver alembic/versions/0001_initial_schema.py). Es la
# revision a la que se estampan las BDs pre-existentes, NO "head".
_INITIAL_REVISION = "0001"


def _alembic_config() -> Config:
    cfg = Config(str(_ALEMBIC_INI))
    cfg.set_main_option("script_location", str(_ALEMBIC_DIR))
    return cfg


def run_migrations() -> None:
    """Aplica migraciones pendientes; adopta Alembic en BDs pre-existentes."""
    cfg = _alembic_config()
    inspector = inspect(engine)
    existing_tables = set(inspector.get_table_names())

    if existing_tables and "alembic_version" not in existing_tables:
        logger.info(
            "BD existente sin alembic_version (%d tablas); adoptando Alembic "
            "con stamp a la revision inicial (%s), no a head.",
            len(existing_tables), _INITIAL_REVISION,
        )
        command.stamp(cfg, _INITIAL_REVISION)

    command.upgrade(cfg, "head")
