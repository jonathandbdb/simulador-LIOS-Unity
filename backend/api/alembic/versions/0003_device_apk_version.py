"""device apk version

Agrega `devices.last_apk_version` (nullable, sin default): version de APK
que el visor reporta en cada `POST /api/verify` (`current_apk_version`,
campo opcional que ya llegaba pero no se persistia). Se muestra en el panel
`/admin/devices` como dato de solo lectura (lo reporta el dispositivo, no
es editable desde el form).

Revision ID: 0003
Revises: 0002
Create Date: 2026-07-10

"""
from typing import Sequence, Union

import sqlalchemy as sa
from alembic import op

# revision identifiers, used by Alembic.
revision: str = "0003"
down_revision: Union[str, None] = "0002"
branch_labels: Union[str, Sequence[str], None] = None
depends_on: Union[str, Sequence[str], None] = None


def upgrade() -> None:
    op.add_column("devices", sa.Column("last_apk_version", sa.String(), nullable=True))


def downgrade() -> None:
    op.drop_column("devices", "last_apk_version")
