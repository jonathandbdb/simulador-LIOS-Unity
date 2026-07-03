"""Test del path de adopcion de Alembic sobre una BD pre-existente.

Regresion del hallazgo de @reviewer: `run_migrations()` estampaba `"head"`
en vez de la revision inicial. Es inocuo mientras `head == "0001"`, pero en
cuanto exista una revision `0002+` una BD vieja (creada por `create_all`,
schema equivalente a `0001`) quedaria marcada como si ya tuviera aplicado el
DDL de `0002` sin haberlo corrido nunca (columnas faltantes silenciosas).
"""
from sqlmodel import SQLModel

from app.database import engine
from app.migrations import _INITIAL_REVISION, run_migrations


def test_legacy_db_is_stamped_to_initial_revision_not_head(monkeypatch):
    # Simula una BD "legacy": tablas creadas por create_all, sin alembic_version.
    SQLModel.metadata.create_all(engine)

    stamped_with: list[str] = []
    upgraded_with: list[str] = []

    monkeypatch.setattr("app.migrations.command.stamp", lambda cfg, rev: stamped_with.append(rev))
    monkeypatch.setattr("app.migrations.command.upgrade", lambda cfg, rev: upgraded_with.append(rev))

    run_migrations()

    assert stamped_with == [_INITIAL_REVISION]
    assert stamped_with != ["head"]
    assert upgraded_with == ["head"]
