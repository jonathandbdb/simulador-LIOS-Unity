"""Conexion a Postgres + sesion SQLModel."""
from collections.abc import Generator

from sqlmodel import Session, SQLModel, create_engine

from app.config import settings

# pool_pre_ping = true → maneja conexiones viejas si Postgres se reinicia.
# SQLite (solo lo usan los tests, ver tests/conftest.py) necesita StaticPool +
# check_same_thread=False para que el mismo ":memory:" sea visible entre
# sesiones/threads del TestClient.
if settings.database_url.startswith("sqlite"):
    from sqlalchemy.pool import StaticPool

    engine = create_engine(
        settings.database_url,
        echo=False,
        connect_args={"check_same_thread": False},
        poolclass=StaticPool,
    )
else:
    engine = create_engine(settings.database_url, echo=False, pool_pre_ping=True)


def init_db() -> None:
    """Crea las tablas directo desde los modelos (sin Alembic).

    El arranque normal del contenedor usa `app.migrations.run_migrations()`
    (Alembic). Esta funcion queda como fallback para tests (SQLite en
    memoria) donde no vale la pena correr migraciones.
    """
    SQLModel.metadata.create_all(engine)


def get_session() -> Generator[Session, None, None]:
    """Dependency injection de FastAPI para sesiones de DB."""
    with Session(engine) as session:
        yield session
