"""versions per app

Rompe el shape de `versions` a proposito: pasa de UNA version activa global
(con PCK, herencia del prototipo Godot) a UNA VERSION ACTIVA POR APP (visor /
tablet), sin PCK (Unity no tiene equivalente — todo va en el APK). Es un
breaking change de riesgo cero porque hoy nada consume `/api/manifest.json`
todavia (Unity aun no tiene UpdateManager).

Revision ID: 0002
Revises: 0001
Create Date: 2026-07-09

"""
from typing import Sequence, Union

import sqlalchemy as sa
from alembic import op

# revision identifiers, used by Alembic.
revision: str = "0002"
down_revision: Union[str, None] = "0001"
branch_labels: Union[str, Sequence[str], None] = None
depends_on: Union[str, Sequence[str], None] = None


def upgrade() -> None:
    # server_default="visor": la fila (unica) que ya exista en un despliegue
    # previo a esta migracion queda clasificada como canal "visor" (el seed
    # viejo solo conocia ese canal implicitamente).
    op.add_column("versions", sa.Column("app", sa.String(), nullable=False, server_default="visor"))
    op.create_index("ix_versions_app", "versions", ["app"], unique=False)
    op.add_column("versions", sa.Column("apk_sha256", sa.String(), nullable=False, server_default=""))
    op.drop_column("versions", "asset_version")
    op.drop_column("versions", "pck_url")
    op.drop_column("versions", "pck_sha256")


def downgrade() -> None:
    op.add_column("versions", sa.Column("asset_version", sa.String(), nullable=False, server_default=""))
    op.add_column("versions", sa.Column("pck_url", sa.String(), nullable=False, server_default=""))
    op.add_column("versions", sa.Column("pck_sha256", sa.String(), nullable=False, server_default=""))
    op.drop_index("ix_versions_app", table_name="versions")
    op.drop_column("versions", "app")
    op.drop_column("versions", "apk_sha256")
