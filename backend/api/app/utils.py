"""Utilidades chicas compartidas por el backend."""
from datetime import datetime, timezone


def utcnow() -> datetime:
    """Ahora en UTC, naive (sin tzinfo).

    Reemplaza a `datetime.utcnow()` (deprecado desde Python 3.12) sin cambiar
    el formato que ya se guarda en Postgres (columnas `timestamp without time
    zone`). Usamos `datetime.now(timezone.utc)` y le quitamos el tzinfo en vez
    de pasar a datetimes aware para no arriesgar mezclas naive/aware en
    comparaciones existentes (ej. `license_expiry < date.today()`).
    """
    return datetime.now(timezone.utc).replace(tzinfo=None)
