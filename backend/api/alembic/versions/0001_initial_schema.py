"""initial schema

Refleja el schema que ya generaba `SQLModel.metadata.create_all` (verificado
contra la BD real antes de introducir Alembic: `devices`, `versions`,
`lens_catalogs`, `update_logs`, `admin_users`). Escrita a mano en vez de
autogenerada para no tener que reflejar/tocar la BD existente del volumen
durante la migracion inicial; `app.migrations.run_migrations()` hace
`stamp head` en vez de aplicar este DDL si detecta que esas tablas ya existen.

Revision ID: 0001
Revises:
Create Date: 2026-07-02

"""
from typing import Sequence, Union

import sqlalchemy as sa
from alembic import op

# revision identifiers, used by Alembic.
revision: str = "0001"
down_revision: Union[str, None] = None
branch_labels: Union[str, Sequence[str], None] = None
depends_on: Union[str, Sequence[str], None] = None


def upgrade() -> None:
    op.create_table(
        "devices",
        sa.Column("id", sa.Integer(), nullable=False),
        sa.Column("device_id", sa.String(), nullable=False),
        sa.Column("name", sa.String(), nullable=False),
        sa.Column("status", sa.String(), nullable=False),
        sa.Column("last_seen", sa.DateTime(), nullable=True),
        sa.Column("last_ip", sa.String(), nullable=True),
        sa.Column("license_expiry", sa.Date(), nullable=True),
        sa.Column("notes", sa.String(), nullable=True),
        sa.Column("created_at", sa.DateTime(), nullable=False),
        sa.Column("updated_at", sa.DateTime(), nullable=False),
        sa.PrimaryKeyConstraint("id"),
    )
    op.create_index("ix_devices_device_id", "devices", ["device_id"], unique=True)

    op.create_table(
        "versions",
        sa.Column("id", sa.Integer(), nullable=False),
        sa.Column("apk_version", sa.String(), nullable=False),
        sa.Column("min_apk_version", sa.String(), nullable=False),
        sa.Column("asset_version", sa.String(), nullable=False),
        sa.Column("apk_url", sa.String(), nullable=False),
        sa.Column("pck_url", sa.String(), nullable=False),
        sa.Column("pck_sha256", sa.String(), nullable=False),
        sa.Column("changelog", sa.String(), nullable=False),
        sa.Column("is_active", sa.Boolean(), nullable=False),
        sa.Column("created_at", sa.DateTime(), nullable=False),
        sa.PrimaryKeyConstraint("id"),
    )

    op.create_table(
        "lens_catalogs",
        sa.Column("id", sa.Integer(), nullable=False),
        sa.Column("version", sa.String(), nullable=False),
        sa.Column("data", sa.String(), nullable=False),
        sa.Column("is_active", sa.Boolean(), nullable=False),
        sa.Column("created_at", sa.DateTime(), nullable=False),
        sa.PrimaryKeyConstraint("id"),
    )

    op.create_table(
        "update_logs",
        sa.Column("id", sa.Integer(), nullable=False),
        sa.Column("device_id", sa.String(), nullable=False),
        sa.Column("event", sa.String(), nullable=False),
        sa.Column("detail", sa.String(), nullable=False),
        sa.Column("created_at", sa.DateTime(), nullable=False),
        sa.PrimaryKeyConstraint("id"),
    )
    op.create_index("ix_update_logs_device_id", "update_logs", ["device_id"], unique=False)

    op.create_table(
        "admin_users",
        sa.Column("id", sa.Integer(), nullable=False),
        sa.Column("username", sa.String(), nullable=False),
        sa.Column("password_hash", sa.String(), nullable=False),
        sa.Column("role", sa.String(), nullable=False),
        sa.Column("created_at", sa.DateTime(), nullable=False),
        sa.PrimaryKeyConstraint("id"),
    )
    op.create_index("ix_admin_users_username", "admin_users", ["username"], unique=True)


def downgrade() -> None:
    op.drop_table("admin_users")
    op.drop_table("update_logs")
    op.drop_table("lens_catalogs")
    op.drop_table("versions")
    op.drop_table("devices")
