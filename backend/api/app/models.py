"""Modelos de datos del backend (SQLModel = SQLAlchemy + Pydantic en uno)."""
from datetime import date, datetime
from typing import Optional

from sqlmodel import Field, SQLModel

from app.utils import utcnow


# ---------------------------------------------------------------------------
# Dispositivos (visores) registrados
# ---------------------------------------------------------------------------
class Device(SQLModel, table=True):
    __tablename__ = "devices"

    id: Optional[int] = Field(default=None, primary_key=True)
    device_id: str = Field(unique=True, index=True, description="OS.get_unique_id() del visor")
    name: str = Field(description='Nombre descriptivo (ej. "Visor Consultorio 3")')
    status: str = Field(default="active", description="active | suspended | pending | rejected")
    last_seen: Optional[datetime] = None
    last_ip: Optional[str] = None
    last_apk_version: Optional[str] = Field(
        default=None,
        description="Ultima version de APK reportada por el visor en /api/verify (current_apk_version)",
    )
    # NULL = licencia permanente (decision tomada en Sprint 0).
    license_expiry: Optional[date] = None
    notes: Optional[str] = None
    created_at: datetime = Field(default_factory=utcnow)
    updated_at: datetime = Field(default_factory=utcnow)


# ---------------------------------------------------------------------------
# Versiones (APK por app — una version activa por canal)
# ---------------------------------------------------------------------------
class Version(SQLModel, table=True):
    """Una fila = un release de APK para un canal (`app`).

    Herencia del prototipo Godot: originalmente habia una sola version activa
    global con APK+PCK (Godot empaqueta assets en un `.pck` separado). Unity
    no tiene equivalente a PCK (todo va en el APK/AAB), y el simulador ahora
    son DOS apps Android independientes (visor Quest y tablet), cada una con
    su propio ciclo de release. Por eso `app` particiona la version activa
    por canal en vez de haber una sola global.
    """
    __tablename__ = "versions"

    id: Optional[int] = Field(default=None, primary_key=True)
    app: str = Field(default="visor", index=True, description='Canal: "visor" | "tablet"')
    apk_version: str = Field(description='Version del APK, ej. "1.0.5"')
    min_apk_version: str = Field(description="Version minima requerida para correr esta release")
    apk_url: str = Field(description="URL publica del APK en el bucket")
    apk_sha256: str = Field(description="SHA256 hex del APK para verificacion de integridad")
    changelog: str = Field(default="", description="Notas de release")
    is_active: bool = Field(default=False, description="Una version activa por app (no global)")
    created_at: datetime = Field(default_factory=utcnow)


# ---------------------------------------------------------------------------
# Catalogo de lentes (versionado)
# ---------------------------------------------------------------------------
class LensCatalog(SQLModel, table=True):
    __tablename__ = "lens_catalogs"

    id: Optional[int] = Field(default=None, primary_key=True)
    version: str = Field(description='Version del catalogo, ej. "1.2.0"')
    data: str = Field(description="JSON string con el catalogo completo")
    is_active: bool = Field(default=False)
    created_at: datetime = Field(default_factory=utcnow)


# ---------------------------------------------------------------------------
# Logs de actualizacion (diagnostico remoto)
# ---------------------------------------------------------------------------
class UpdateLog(SQLModel, table=True):
    __tablename__ = "update_logs"

    id: Optional[int] = Field(default=None, primary_key=True)
    device_id: str = Field(index=True)
    event: str = Field(description="manifest_check, download_start, hash_ok, update_success, ...")
    detail: str = Field(default="")
    created_at: datetime = Field(default_factory=utcnow)


# ---------------------------------------------------------------------------
# Usuarios del panel admin
# ---------------------------------------------------------------------------
class AdminUser(SQLModel, table=True):
    __tablename__ = "admin_users"

    id: Optional[int] = Field(default=None, primary_key=True)
    username: str = Field(unique=True, index=True)
    password_hash: str = Field(description="bcrypt hash")
    role: str = Field(default="superadmin", description="superadmin | viewer")
    created_at: datetime = Field(default_factory=utcnow)
