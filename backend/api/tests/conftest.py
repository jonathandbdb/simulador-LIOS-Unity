"""Fixtures compartidas de los tests del backend.

Fuerza SQLite en memoria *antes* de que se importe `app.config` (que lee las
variables de entorno una sola vez, al importarse). Por eso este bloque va al
tope del archivo, previo a cualquier `from app... import ...`.
"""
import os

os.environ.setdefault("DATABASE_URL", "sqlite:///:memory:")
os.environ.setdefault("PUBLIC_BASE_URL", "http://testserver")
os.environ.setdefault("JWT_SECRET", "test-secret-not-for-prod")

import pytest
from fastapi.testclient import TestClient

from app.database import init_db


@pytest.fixture()
def client(monkeypatch):
    """TestClient contra SQLite en memoria, sin Alembic/Postgres/MinIO reales.

    - `run_migrations` (Alembic) y `ensure_bucket` (MinIO) se noopean: no hay
      Postgres ni MinIO en el entorno de test; `init_db()` (create_all) hace
      de reemplazo, que es justo el fallback documentado para este caso.
    - `seed()` SI corre (no se mockea): crea el admin, el catalogo fallback,
      la version dummy y el device `DEV_TEST_001` que usan los tests.
    """
    import app.main as main_module

    monkeypatch.setattr(main_module, "run_migrations", lambda: None)
    monkeypatch.setattr(main_module, "ensure_bucket", lambda: None)

    init_db()

    with TestClient(main_module.app) as c:
        yield c
