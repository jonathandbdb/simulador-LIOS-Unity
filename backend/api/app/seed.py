"""Seed inicial de la BD para desarrollo y testing.

Se ejecuta una vez al arrancar el container (idempotente: solo crea si falta).
En produccion, crea el admin user para que se pueda entrar al panel.
En desarrollo, ademas crea una version dummy + catalogo seed + 1 device de test.
"""
import json
import logging
from datetime import date, datetime
from pathlib import Path

from passlib.context import CryptContext
from sqlmodel import Session, select

from app.config import settings
from app.models import AdminUser, Device, LensCatalog, Version

logger = logging.getLogger(__name__)
pwd_ctx = CryptContext(schemes=["bcrypt"], deprecated="auto")


def seed(session: Session) -> None:
    _seed_admin(session)
    _seed_lens_catalog(session)
    _seed_version(session)
    _seed_test_device(session)
    session.commit()


def _seed_admin(session: Session) -> None:
    existing = session.exec(
        select(AdminUser).where(AdminUser.username == settings.admin_default_user)
    ).first()
    if existing:
        return
    admin = AdminUser(
        username=settings.admin_default_user,
        password_hash=pwd_ctx.hash(settings.admin_default_pass),
        role="superadmin",
    )
    session.add(admin)
    logger.info("[seed] admin user creado: %s", settings.admin_default_user)


# Versiones de catalogo creadas por este seed en releases anteriores. Si la
# version activa en BD coincide con alguna de estas, asumimos que NO fue
# editada por un admin desde el panel y la podemos reemplazar por la nueva
# version del JSON. Si la version activa NO esta aqui, respetamos la edicion
# manual del admin y no la pisamos.
_KNOWN_SEED_VERSIONS = {
    "0.0.1-seed",
    "0.1.0-fallback",
    "0.2.0-noche",
    "0.3.0-clinical",
    "0.4.0-clinical",
    "0.4.0-fallback",
    "0.5.0-clinical",
    "0.5.1-clinical",
    "0.6.0-clinical",
    "0.6.1-clinical",
    "0.7.0-clinical",
}


# NOTA (P7): este seed jamas toca `custom_lenses` — su logica de promocion
# opera solo sobre `lens_catalogs` (el blob base). Las lentes creadas por
# dispositivos sobreviven cualquier re-seed, por construccion.


def _seed_lens_catalog(session: Session) -> None:
    catalog_data = _load_default_catalog()
    json_version = catalog_data.get("version", "0.0.1-seed")
    lens_count = len(catalog_data.get("catalogo", []))

    existing = session.exec(select(LensCatalog).where(LensCatalog.is_active == True)).first()  # noqa: E712
    if existing is not None:
        if existing.version == json_version:
            return  # nada que migrar, ya esta al dia
        if existing.version not in _KNOWN_SEED_VERSIONS:
            # Edicion manual del admin: no pisar.
            logger.info(
                "[seed] catalogo activo v%s NO es seed conocido; se respeta. "
                "JSON del repo (v%s) ignorado.",
                existing.version, json_version,
            )
            return
        existing.is_active = False
        session.add(existing)
        logger.info("[seed] desactivado catalogo seed previo v%s", existing.version)

    catalog = LensCatalog(
        version=json_version,
        data=json.dumps(catalog_data, ensure_ascii=False),
        is_active=True,
    )
    session.add(catalog)
    logger.info("[seed] catalogo de lentes activo: v%s (%d lentes)", catalog.version, lens_count)


def _load_default_catalog() -> dict:
    # En desarrollo, defaults/lentes.json se puede montar como volumen.
    # Tambien aceptamos un fallback inline para que el seed funcione sin volumen.
    candidate_paths = [
        Path("/seed/lentes.json"),
        Path("/app/seed/lentes.json"),
    ]
    for p in candidate_paths:
        if p.exists():
            try:
                return json.loads(p.read_text(encoding="utf-8"))
            except Exception as e:  # noqa: BLE001
                logger.warning("[seed] error leyendo %s: %s", p, e)
    # Fallback minimo (1 lente, sin straylight): NO es una copia del catalogo real,
    # por eso usa su propia version "-fallback" en vez de pisar la version clinica
    # real. Al aparecer el volumen con el catalogo completo, la promocion lo
    # reemplaza igual porque esta version esta en _KNOWN_SEED_VERSIONS.
    return {
        "version": "0.4.0-fallback",
        "catalogo": [
            {
                "id": "monofocal",
                "nombre": "Monofocal Estandar",
                "descripcion": "Foco unico. Fallback minimo del seed.",
                "params": {
                    "foco_lejos_m":       {"default": 6.0,  "min": 0.0, "max": 20.0},
                    "foco_intermedio_m":  {"default": 0.0,  "min": 0.0, "max": 20.0},
                    "foco_cerca_m":       {"default": 0.0,  "min": 0.0, "max": 20.0},
                    "profundidad_foco_m": {"default": 1.2,  "min": 0.1, "max": 5.0},
                    "desenfoque_max":     {"default": 0.9,  "min": 0.0, "max": 1.0},
                    "halo_intensity":     {"default": 0.03, "min": 0.0, "max": 1.0},
                    "halo_extra_rings":   {"default": 0.0,  "min": 0.0, "max": 1.0},
                    "contrast_loss":      {"default": 0.0,  "min": 0.0, "max": 0.6},
                    "destello_intensity": {"default": 0.0,  "min": 0.0, "max": 1.0},
                    "destello_rayos":     {"default": 0.0,  "min": 0.0, "max": 16.0},
                },
            },
        ],
    }


# Version dummy del seed. DELIBERADAMENTE igual al bundleVersion actual de
# Unity (visor y tablet) para que el dummy jamas dispare el cartel de
# actualizacion (min_apk_version == apk_version instalado real). Si el
# bundleVersion base de Unity cambia, actualizar esta constante a mano.
_DUMMY_APK_VERSION = "0.1.0"


def _seed_version(session: Session) -> None:
    """Crea una version dummy POR APP (visor, tablet) si ese canal no tiene activa.

    Una version activa es por canal desde que se partio `Version.app` (antes
    era global, herencia del prototipo Godot con PCK). En un backend que ya
    corrio antes de esta migracion, la fila vieja (unica) quedo con
    `app='visor'` (server_default de la migracion 0002) — por eso este seed
    normalmente solo agrega la version dummy que falta, la de "tablet".
    """
    for app_name in ("visor", "tablet"):
        existing = session.exec(
            select(Version).where(Version.is_active == True, Version.app == app_name)  # noqa: E712
        ).first()
        if existing:
            continue
        version = Version(
            app=app_name,
            apk_version=_DUMMY_APK_VERSION,
            min_apk_version=_DUMMY_APK_VERSION,
            apk_url=f"{settings.public_base_url}/dummy/simulador-{app_name}-{_DUMMY_APK_VERSION}.apk",
            apk_sha256="",  # dummy: sin APK real todavia
            changelog=f"Version dummy del seed para el canal '{app_name}'. Sin APK real.",
            is_active=True,
        )
        session.add(version)
        logger.info(
            "[seed] version dummy creada: app=%s APK v%s",
            version.app, version.apk_version,
        )


def _seed_test_device(session: Session) -> None:
    # Solo en desarrollo, para poder probar /api/verify sin pasar por el panel admin.
    existing = session.exec(select(Device).where(Device.device_id == "DEV_TEST_001")).first()
    if existing:
        return
    device = Device(
        device_id="DEV_TEST_001",
        name="Visor de desarrollo",
        status="active",
        app_mode="pro",   # P7: pro+admin para probar todo el flujo de lentes con curl
        is_admin=True,
        license_expiry=None,  # permanente
        notes="Device de testing creado por el seed. Eliminar en produccion.",
    )
    session.add(device)
    logger.info("[seed] device de testing creado: %s", device.device_id)
