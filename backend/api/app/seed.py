"""Seed inicial de la BD para desarrollo y testing.

Se ejecuta una vez al arrancar el container (idempotente: solo crea si falta).
En produccion, crea el admin user para que se pueda entrar al panel.
En desarrollo, ademas crea una version dummy + catalogo seed + 1 device de test.
"""
import json
from datetime import date, datetime
from pathlib import Path

from passlib.context import CryptContext
from sqlmodel import Session, select

from app.config import settings
from app.models import AdminUser, Device, LensCatalog, Version

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
    print(f"[seed] admin user creado: {settings.admin_default_user}")


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
}


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
            print(
                f"[seed] catalogo activo v{existing.version} NO es seed conocido; "
                f"se respeta. JSON del repo (v{json_version}) ignorado."
            )
            return
        existing.is_active = False
        session.add(existing)
        print(f"[seed] desactivado catalogo seed previo v{existing.version}")

    catalog = LensCatalog(
        version=json_version,
        data=json.dumps(catalog_data, ensure_ascii=False),
        is_active=True,
    )
    session.add(catalog)
    print(f"[seed] catalogo de lentes activo: v{catalog.version} ({lens_count} lentes)")


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
                print(f"[seed] error leyendo {p}: {e}")
    # Fallback minimo. Mantiene la misma version semantica que el JSON del repo
    # asi nunca se promueve sobre un catalogo recalibrado por error.
    return {
        "version": "0.3.0-clinical",
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


def _seed_version(session: Session) -> None:
    existing = session.exec(select(Version).where(Version.is_active == True)).first()  # noqa: E712
    if existing:
        return
    version = Version(
        apk_version="0.1.0",
        min_apk_version="0.1.0",
        asset_version="0.1.0",
        apk_url=f"{settings.public_base_url}/dummy/simulador-0.1.0.apk",
        pck_url=f"{settings.public_base_url}/dummy/assets-0.1.0.pck",
        pck_sha256="e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855",  # SHA256 de archivo vacio
        changelog="Sprint 3: backend minimo desplegado. Sin APK/PCK reales.",
        is_active=True,
    )
    session.add(version)
    print(f"[seed] version activa creada: APK v{version.apk_version} / assets v{version.asset_version}")


def _seed_test_device(session: Session) -> None:
    # Solo en desarrollo, para poder probar /api/verify sin pasar por el panel admin.
    existing = session.exec(select(Device).where(Device.device_id == "DEV_TEST_001")).first()
    if existing:
        return
    device = Device(
        device_id="DEV_TEST_001",
        name="Visor de desarrollo",
        status="active",
        license_expiry=None,  # permanente
        notes="Device de testing creado por el seed. Eliminar en produccion.",
    )
    session.add(device)
    print(f"[seed] device de testing creado: {device.device_id}")
