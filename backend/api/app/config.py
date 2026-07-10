"""Configuracion del backend, leida de variables de entorno."""
from pydantic_settings import BaseSettings, SettingsConfigDict


class Settings(BaseSettings):
    """Variables de configuracion del backend.

    Se leen de variables de entorno (Docker Compose las inyecta desde .env).
    """

    model_config = SettingsConfigDict(env_file=".env", extra="ignore")

    # Base de datos
    database_url: str = "postgresql+psycopg://simulador:changeme@db:5432/simulador"

    # Bucket S3 (MinIO en local, R2/S3 real en prod)
    s3_endpoint: str = "http://bucket:9000"
    s3_access_key: str = "minioadmin"
    s3_secret_key: str = "minioadmin"
    s3_bucket: str = "simulador-updates"

    # URL publica del backend (para construir links absolutos en manifest.json)
    public_base_url: str = "http://localhost:8080"

    # Auth
    jwt_secret: str = "dev-jwt-secret-change-me"
    admin_default_user: str = "admin"
    admin_default_pass: str = "admin123"

    # CORS de la API publica /api/*. Coma-separado; "*" = cualquier origen.
    # El panel /admin es server-rendered (mismo origen, cookie httpOnly) y no
    # depende de CORS. Unity (UnityWebRequest) tampoco es un browser: CORS no
    # aplica a ese cliente. Por eso el wildcard es seguro siempre que
    # allow_credentials quede en False (ver main.py) — Starlette no permite
    # combinar wildcard con credenciales.
    cors_origins: str = "*"

    # Logging
    log_level: str = "info"

    # Retencion de logs de UpdateLog (POST /api/log): se purgan los mas
    # viejos que N dias en el arranque y, con throttle de 1h, en cada
    # POST /api/log (ver app/routers.py — purge_old_logs/_maybe_purge_logs).
    log_retention_days: int = 30

    @property
    def cors_origins_list(self) -> list[str]:
        if self.cors_origins.strip() == "*":
            return ["*"]
        return [o.strip() for o in self.cors_origins.split(",") if o.strip()]


settings = Settings()
