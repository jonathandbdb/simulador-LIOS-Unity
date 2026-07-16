"""custom lenses + modo de app por device

P7 (modos Standard/Pro): agrega a `devices` el modo de app (`app_mode`,
"standard" | "pro") y el flag `is_admin` (puede crear/editar lentes
GENERICAS visibles para todos). `server_default` en ambas para que todo
device pre-existente quede en modo estandar sin flag admin (comportamiento
identico al previo a esta migracion).

Crea `custom_lenses`: lentes creadas desde dispositivos, aparte del catalogo
base (que sigue siendo el blob JSON de `lens_catalogs`). `owner_device_pk`
es FK a `devices.id` (el PK int, NO el string `device_id`): el reemplazo de
hardware se resuelve editando `device_id` en la fila de `devices` y las
lentes siguen colgando del mismo PK. NULL = lente GENERICA (global); el
ON DELETE CASCADE borra las privadas de un device eliminado y por
construccion jamas toca las genericas. `lens_id` (string del contrato de
lente, "custom_xxxxxxxx"/"generic_xxxxxxxx", generado por el server) lleva
indice UNIQUE global: sin colisiones entre devices ni con el catalogo base.

Revision ID: 0004
Revises: 0003
Create Date: 2026-07-15

"""
from typing import Sequence, Union

import sqlalchemy as sa
from alembic import op

# revision identifiers, used by Alembic.
revision: str = "0004"
down_revision: Union[str, None] = "0003"
branch_labels: Union[str, Sequence[str], None] = None
depends_on: Union[str, Sequence[str], None] = None


def upgrade() -> None:
    op.add_column(
        "devices",
        sa.Column("app_mode", sa.String(), nullable=False, server_default="standard"),
    )
    op.add_column(
        "devices",
        sa.Column("is_admin", sa.Boolean(), nullable=False, server_default=sa.false()),
    )
    op.create_table(
        "custom_lenses",
        sa.Column("id", sa.Integer(), nullable=False),
        sa.Column("owner_device_pk", sa.Integer(), nullable=True),
        sa.Column("lens_id", sa.String(), nullable=False),
        sa.Column("nombre", sa.String(), nullable=False),
        sa.Column("descripcion", sa.String(), nullable=False, server_default=""),
        sa.Column("params_json", sa.String(), nullable=False),
        sa.Column("created_at", sa.DateTime(), nullable=False),
        sa.Column("updated_at", sa.DateTime(), nullable=False),
        sa.PrimaryKeyConstraint("id"),
        sa.ForeignKeyConstraint(["owner_device_pk"], ["devices.id"], ondelete="CASCADE"),
    )
    op.create_index(
        "ix_custom_lenses_owner_device_pk", "custom_lenses", ["owner_device_pk"], unique=False
    )
    op.create_index("ix_custom_lenses_lens_id", "custom_lenses", ["lens_id"], unique=True)


def downgrade() -> None:
    op.drop_index("ix_custom_lenses_lens_id", table_name="custom_lenses")
    op.drop_index("ix_custom_lenses_owner_device_pk", table_name="custom_lenses")
    op.drop_table("custom_lenses")
    op.drop_column("devices", "is_admin")
    op.drop_column("devices", "app_mode")
