"""ota_enabled por device

Agrega `devices.ota_enabled` (bool, server_default false): gate por-dispositivo
del OTA propio del backend (`GET /api/manifest.json`). La mayoria de la flota
corre en modo kiosco gestionado por Meta Horizon Managed Services (se
actualiza por el Admin Center de Meta, APK auto-hospedada + SHA256 fijado) y
NO debe recibir el OTA del backend; las excepciones (visores de desarrollo)
se marcan a mano desde `/admin/devices`. `server_default=false` deja TODAS
las filas existentes en False (comportamiento esperado, no un bug: el admin
prende el flag a mano en los devices que corresponda).

Revision ID: 0006
Revises: 0005
Create Date: 2026-09-03

"""
from typing import Sequence, Union

import sqlalchemy as sa
from alembic import op

# revision identifiers, used by Alembic.
revision: str = "0006"
down_revision: Union[str, None] = "0005"
branch_labels: Union[str, Sequence[str], None] = None
depends_on: Union[str, Sequence[str], None] = None


def upgrade() -> None:
    op.add_column(
        "devices",
        sa.Column("ota_enabled", sa.Boolean(), nullable=False, server_default=sa.false()),
    )


def downgrade() -> None:
    op.drop_column("devices", "ota_enabled")
