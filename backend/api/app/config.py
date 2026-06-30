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
    api_key_ci: str = "dev-ci-key"

    # Logging
    log_level: str = "info"


settings = Settings()
