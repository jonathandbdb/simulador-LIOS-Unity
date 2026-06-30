"""Conexion a Postgres + sesion SQLModel."""
from collections.abc import Generator

from sqlmodel import Session, SQLModel, create_engine

from app.config import settings

# pool_pre_ping = true → maneja conexiones viejas si Postgres se reinicia.
engine = create_engine(settings.database_url, echo=False, pool_pre_ping=True)


def init_db() -> None:
    """Crea las tablas si no existen.

    Sprint 3: usa SQLModel.metadata.create_all en lugar de Alembic.
    Cuando el schema se estabilice (Sprint 8+), migrar a Alembic.
    """
    SQLModel.metadata.create_all(engine)


def get_session() -> Generator[Session, None, None]:
    """Dependency injection de FastAPI para sesiones de DB."""
    with Session(engine) as session:
        yield session
