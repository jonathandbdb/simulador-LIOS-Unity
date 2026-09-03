"""Tests de `/admin/provisioning` (QR de Android Enterprise para la tablet).

Molde: `test_admin_versions.py`. La `Version` dummy del seed para el canal
"tablet" tiene `apk_sha256=""` (sin APK real todavia, ver `app/seed.py`) —
para no depender de ese caso limite al verificar el checksum del PAQUETE,
los tests que necesitan un checksum concreto activan una `Version` propia
con un `apk_sha256` conocido.
"""
import base64

from sqlmodel import Session, select

from app.config import settings
from app.database import engine
from app.models import Version


def _login(client):
    r = client.post(
        "/admin/login",
        data={"username": settings.admin_default_user, "password": settings.admin_default_pass},
        follow_redirects=False,
    )
    assert r.status_code == 303
    return client


def _activate_tablet_version(apk_version: str, apk_sha256: str) -> Version:
    """Crea y activa una `Version` de tablet con un sha256 conocido,
    desactivando cualquier otra version activa del mismo canal (incluida la
    dummy del seed)."""
    with Session(engine) as s:
        for prev in s.exec(
            select(Version).where(Version.is_active == True, Version.app == "tablet")  # noqa: E712
        ).all():
            prev.is_active = False
            s.add(prev)
        v = Version(
            app="tablet",
            apk_version=apk_version,
            min_apk_version=apk_version,
            apk_url=f"http://testserver/files/apk/tablet/simulador-tablet-{apk_version}.apk",
            apk_sha256=apk_sha256,
            changelog="",
            is_active=True,
        )
        s.add(v)
        s.commit()
        s.refresh(v)
        return v


def _deactivate_all_tablet_versions() -> None:
    with Session(engine) as s:
        for v in s.exec(
            select(Version).where(Version.is_active == True, Version.app == "tablet")  # noqa: E712
        ).all():
            v.is_active = False
            s.add(v)
        s.commit()


def test_provisioning_requires_login(client):
    r = client.get("/admin/provisioning", follow_redirects=False)
    assert r.status_code == 303
    assert r.headers["location"].startswith("/admin/login")


def test_provisioning_ok_with_active_tablet_version(client):
    _login(client)
    _activate_tablet_version("1.2.3", "00" * 32)

    r = client.get("/admin/provisioning")
    assert r.status_code == 200
    body = r.text
    assert "com.simulador.tablet/com.simulador.kiosk.SimuladorDeviceAdminReceiver" in body

    expected_checksum = base64.urlsafe_b64encode(bytes.fromhex("00" * 32)).rstrip(b"=").decode()
    assert expected_checksum.startswith("AAAA")
    assert expected_checksum in body
    assert 'class="segno"' in body


def test_provisioning_wifi_fields_in_payload(client):
    _login(client)
    _activate_tablet_version("1.2.4", "ab" * 32)

    # POST, no GET: `wifi_password` va en el body, nunca en la query string
    # (MAYOR #6 — ver docstring de `provisioning_generate` en router.py).
    r = client.post(
        "/admin/provisioning",
        data={"wifi_ssid": "ClinicaWifi", "wifi_password": "supersecreta"},
    )
    assert r.status_code == 200
    body = r.text
    assert "PROVISIONING_WIFI_SSID" in body
    assert "ClinicaWifi" in body
    assert "PROVISIONING_WIFI_SECURITY_TYPE" in body
    assert "WPA" in body
    assert "PROVISIONING_WIFI_PASSWORD" in body
    assert "supersecreta" in body


def test_provisioning_get_never_accepts_wifi_query_params(client):
    """El `GET` no tiene parametros de WiFi: aunque alguien pegue
    `?wifi_password=...` a mano, la ruta los ignora (Fast API ni los
    declara) — el form real vive solo en `POST`."""
    _login(client)
    _activate_tablet_version("1.2.4b", "ab" * 32)

    r = client.get(
        "/admin/provisioning",
        params={"wifi_ssid": "ClinicaWifi", "wifi_password": "supersecreta"},
    )
    assert r.status_code == 200
    assert "supersecreta" not in r.text
    assert "ClinicaWifi" not in r.text


def test_provisioning_without_ssid_omits_wifi_keys(client):
    _login(client)
    _activate_tablet_version("1.2.5", "cd" * 32)

    r = client.get("/admin/provisioning")
    assert r.status_code == 200
    assert "WIFI_" not in r.text


def test_provisioning_wifi_without_password_uses_security_type_none(client):
    """MENOR: sin password, `PROVISIONING_WIFI_SECURITY_TYPE` debe ser
    "NONE" (red abierta), no "WPA" hardcodeado — mandar "WPA" sin
    PROVISIONING_WIFI_PASSWORD hace que el asistente pida una clave que
    nunca se le va a dar."""
    _login(client)
    _activate_tablet_version("1.2.5b", "cd" * 32)

    r = client.post("/admin/provisioning", data={"wifi_ssid": "ClinicaWifi"})
    assert r.status_code == 200
    body = r.text
    # `payload_json` se renderiza escapado por Jinja (autoescape=True: `"`
    # -> `&#34;`), asi que se busca por substrings sin comillas, igual que
    # el resto de los tests de este archivo.
    assert "PROVISIONING_WIFI_SSID" in body
    assert "PROVISIONING_WIFI_SECURITY_TYPE" in body
    assert "NONE" in body
    assert "WPA" not in body
    assert "PROVISIONING_WIFI_PASSWORD" not in body


def test_provisioning_wifi_with_password_uses_security_type_wpa(client):
    _login(client)
    _activate_tablet_version("1.2.5c", "cd" * 32)

    r = client.post(
        "/admin/provisioning",
        data={"wifi_ssid": "ClinicaWifi", "wifi_password": "supersecreta"},
    )
    assert r.status_code == 200
    body = r.text
    assert "PROVISIONING_WIFI_SECURITY_TYPE" in body
    assert "WPA" in body
    assert "NONE" not in body


def test_provisioning_without_active_tablet_version_shows_notice(client):
    _login(client)
    _deactivate_all_tablet_versions()

    r = client.get("/admin/provisioning")
    assert r.status_code == 200
    # `base.html` siempre trae SVGs decorativos (logo, toggle de tema); el
    # marcador inequivoco de que NO se genero el QR es la clase que segno le
    # pone a su propio `<svg>` (`class="segno"`).
    assert 'class="segno"' not in r.text


def test_provisioning_prefers_signature_checksum_when_configured(client, monkeypatch):
    _login(client)
    _activate_tablet_version("1.2.6", "ef" * 32)
    monkeypatch.setattr(settings, "provisioning_signature_checksum", "fake-signature-checksum-value")

    r = client.get("/admin/provisioning")
    assert r.status_code == 200
    body = r.text
    assert "PROVISIONING_DEVICE_ADMIN_SIGNATURE_CHECKSUM" in body
    assert "fake-signature-checksum-value" in body
    assert "PROVISIONING_DEVICE_ADMIN_PACKAGE_CHECKSUM" not in body


# --- MAYOR #7: checksum invalido/vacio -> banner, nunca 500 ---------------

def test_provisioning_empty_apk_sha256_shows_no_checksum_banner_not_500(client):
    """La `Version` dummy del seed (canal tablet) trae `apk_sha256 == ""`:
    sin `PROVISIONING_SIGNATURE_CHECKSUM` configurado, no hay checksum
    usable. Antes del fix esto renderizaba un QR con
    PACKAGE_CHECKSUM vacio sin avisar; ahora debe mostrar el banner
    `prov.no_checksum` y NO generar QR."""
    _login(client)
    _activate_tablet_version("1.2.7", "")

    r = client.get("/admin/provisioning")
    assert r.status_code == 200
    assert 'class="segno"' not in r.text
    assert "PROVISIONING_DEVICE_ADMIN_PACKAGE_CHECKSUM" not in r.text


def test_provisioning_invalid_hex_apk_sha256_returns_200_not_500(client):
    """Un `apk_sha256` no-hex (dato corrupto) tiraba `ValueError` sin
    capturar -> 500. Debe degradar al mismo banner que el checksum vacio."""
    _login(client)
    _activate_tablet_version("1.2.8", "no-es-hex-" + "z" * 54)

    r = client.get("/admin/provisioning")
    assert r.status_code == 200
    assert 'class="segno"' not in r.text


def test_provisioning_short_hex_apk_sha256_returns_200_not_500(client):
    """Un `apk_sha256` hex pero de largo incorrecto (no 64 chars = 32
    bytes de sha256) tambien debe degradar al banner, no crashear."""
    _login(client)
    _activate_tablet_version("1.2.9", "ab" * 10)

    r = client.get("/admin/provisioning")
    assert r.status_code == 200
    assert 'class="segno"' not in r.text


def test_provisioning_signature_checksum_bypasses_invalid_apk_sha256(client, monkeypatch):
    """Si hay checksum de FIRMA configurado, un `apk_sha256` invalido en la
    version activa no importa: igual se genera el QR (el checksum de
    paquete ni se calcula)."""
    _login(client)
    _activate_tablet_version("1.3.0", "")
    monkeypatch.setattr(settings, "provisioning_signature_checksum", "fake-signature-checksum-value")

    r = client.get("/admin/provisioning")
    assert r.status_code == 200
    assert 'class="segno"' in r.text
    assert "fake-signature-checksum-value" in r.text
