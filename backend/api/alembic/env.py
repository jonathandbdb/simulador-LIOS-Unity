"""Entorno de Alembic: usa el engine y los modelos SQLModel de la app."""
from alembic import context
from sqlalchemy import engine_from_config, pool
from sqlmodel import SQLModel

from app.config import settings
from app.models import (  # noqa: F401  (registran las tablas en SQLModel.metadata)
    AdminUser,
    Device,
    LensCatalog,
    UpdateLog,
    Version,
)

config = context.config
config.set_main_option("sqlalchemy.url", settings.database_url)

# OJO: a proposito NO llamamos logging.config.fileConfig(config.config_file_name)
# aca. `run_migrations()` (app/migrations.py) invoca este env.py desde DENTRO
# del proceso de la app, no como CLI standalone; fileConfig() reconfigura el
# logger root (nivel/handlers de [logger_root] en alembic.ini) y pisa el
# logging.basicConfig(level=...) de app/main.py para el resto de la vida del
# proceso — silenciaba TODO log de la app (seed, ensure_bucket, etc.) despues
# del primer "Aplicando migraciones Alembic..." (bug real detectado con el
# seed: el catalogo se promovia bien pero sin ningun log despues). Los
# loggers propios de alembic ("alembic.runtime.migration") ya heredan el
# nivel/handler configurado por logging.basicConfig sin necesitar fileConfig.
# Costo: correr `alembic <cmd>` como CLI standalone (fuera de la app) pierde
# el formato lindo de alembic.ini y solo muestra WARNING+ (root sin handler
# propio). Ver docs/backend.md.

target_metadata = SQLModel.metadata


def run_migrations_offline() -> None:
    url = config.get_main_option("sqlalchemy.url")
    context.configure(
        url=url,
        target_metadata=target_metadata,
        literal_binds=True,
        dialect_opts={"paramstyle": "named"},
    )
    with context.begin_transaction():
        context.run_migrations()


def run_migrations_online() -> None:
    connectable = engine_from_config(
        config.get_section(config.config_ini_section, {}),
        prefix="sqlalchemy.",
        poolclass=pool.NullPool,
    )
    with connectable.connect() as connection:
        context.configure(connection=connection, target_metadata=target_metadata)
        with context.begin_transaction():
            context.run_migrations()


if context.is_offline_mode():
    run_migrations_offline()
else:
    run_migrations_online()
