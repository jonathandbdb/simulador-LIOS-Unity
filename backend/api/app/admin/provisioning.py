"""Android Enterprise QR provisioning para la tablet (`com.simulador.tablet`).

Genera el JSON que Android espera codificado en el QR de "recuperacion
remota" (factory reset en una clinica del exterior: 6 taps en la pantalla de
bienvenida del asistente + escanear este QR -> la tablet descarga el APK
desde `/files/...` y se auto-provisiona como Device Owner). Ver
`docs/backend.md` ("Auth y panel admin" > Provisioning) y
`docs/builds-deploy.md` ("Provision de tablets (Device Owner)" > "Recuperacion
remota por QR").
"""
import base64
import json

import segno

from app.config import settings
from app.models import Version

# Componente DeviceAdminReceiver de la app tablet (Fase A, ya en Unity).
# Constante unica: ni el router ni los tests la repiten a mano.
DEVICE_ADMIN_COMPONENT_NAME = "com.simulador.tablet/com.simulador.kiosk.SimuladorDeviceAdminReceiver"


def _package_checksum_from_sha256_hex(sha256_hex: str) -> str:
    """`apk_sha256` (hex, hash del APK completo) -> Base64 URL-safe SIN
    padding: es el formato que exige
    `PROVISIONING_DEVICE_ADMIN_PACKAGE_CHECKSUM` (NO hex crudo).

    Precondicion: `sha256_hex` ya paso por `_is_valid_apk_sha256` (el
    llamador — `has_usable_checksum` en el router — lo garantiza). Si se
    llama con un valor invalido, `bytes.fromhex` tira `ValueError`."""
    raw = bytes.fromhex(sha256_hex)
    return base64.urlsafe_b64encode(raw).rstrip(b"=").decode("ascii")


def _is_valid_apk_sha256(value: str) -> bool:
    """¿`value` es un sha256 hex utilizable como checksum de PAQUETE?

    Debe ser hex de 64 caracteres (32 bytes). La `Version` dummy del seed
    trae `apk_sha256 == ""` (sin APK real subido todavia) y un valor
    corrupto/no-hex tambien es posible si se edito la BD a mano — ambos
    casos deben resolver en "sin checksum usable", nunca en el `ValueError`
    de `bytes.fromhex` propagando como 500.
    """
    if len(value) != 64:
        return False
    try:
        bytes.fromhex(value)
    except ValueError:
        return False
    return True


def has_usable_checksum(version: Version) -> bool:
    """¿Hay al menos UN checksum usable para armar el QR de esta `version`?

    El de FIRMA (`.env`, constante del proyecto, no depende de la fila) o el
    de PAQUETE derivado de `version.apk_sha256` (necesita ser un sha256 hex
    valido, ver `_is_valid_apk_sha256`). El router llama esto ANTES de
    `build_provisioning_payload` para decidir si mostrar el banner
    `prov.no_checksum` en vez de arriesgar un 500."""
    if settings.provisioning_signature_checksum:
        return True
    return _is_valid_apk_sha256(version.apk_sha256)


def build_provisioning_payload(
    version: Version,
    *,
    wifi_ssid: str = "",
    wifi_password: str = "",
    locale: str = "",
    timezone: str = "",
) -> dict:
    """Arma el dict que se serializa como el contenido del QR.

    Preferimos el checksum del CERTIFICADO DE FIRMA
    (`settings.provisioning_signature_checksum`, constante para el proyecto)
    sobre el del PAQUETE (derivado de `version.apk_sha256`) cuando el
    primero esta configurado: el de firma no caduca al publicar una version
    nueva del APK, asi un QR ya mandado por mail sigue siendo valido. El de
    paquete es el fallback funcional, pero queda obsoleto en cuanto se sube
    una version nueva (el sha256 cambia).
    """
    payload: dict = {
        "android.app.extra.PROVISIONING_DEVICE_ADMIN_COMPONENT_NAME": DEVICE_ADMIN_COMPONENT_NAME,
        "android.app.extra.PROVISIONING_DEVICE_ADMIN_PACKAGE_DOWNLOAD_LOCATION": version.apk_url,
    }
    if settings.provisioning_signature_checksum:
        payload["android.app.extra.PROVISIONING_DEVICE_ADMIN_SIGNATURE_CHECKSUM"] = (
            settings.provisioning_signature_checksum
        )
    else:
        payload["android.app.extra.PROVISIONING_DEVICE_ADMIN_PACKAGE_CHECKSUM"] = (
            _package_checksum_from_sha256_hex(version.apk_sha256)
        )

    payload["android.app.extra.PROVISIONING_SKIP_ENCRYPTION"] = True
    # Obligatorio: sin esto Android deshabilita las apps de sistema no
    # esenciales (incluida com.android.settings), que la app necesita en el
    # allowlist de lock task para el panel de WiFi de la tablet.
    payload["android.app.extra.PROVISIONING_LEAVE_ALL_SYSTEM_APPS_ENABLED"] = True

    # Opcionales del form (nunca persistidos ni logueados: el password de
    # WiFi es sensible, vive solo en la respuesta HTTP de esta request). El
    # router expone el form SOLO por `POST /admin/provisioning` (body, no
    # query string) para que esto sea cierto de raiz — un `GET` con estos
    # campos en la URL terminaria en el historial del navegador, el
    # `Referer` de requests salientes y el access log de Caddy (que esta
    # DELANTE de la app y loguea la URI completa sin que la app pueda
    # filtrarlo). El `_RedactWifiPasswordFilter` de `app/main.py` sobre
    # `uvicorn.access` queda como defensa en profundidad, no como el fix.
    if wifi_ssid:
        payload["android.app.extra.PROVISIONING_WIFI_SSID"] = wifi_ssid
        # "NONE" si no hay password (red abierta): mandar "WPA" sin
        # PROVISIONING_WIFI_PASSWORD le pide al asistente una clave que
        # nunca vamos a mandarle y la conexion falla.
        if wifi_password:
            payload["android.app.extra.PROVISIONING_WIFI_SECURITY_TYPE"] = "WPA"
            payload["android.app.extra.PROVISIONING_WIFI_PASSWORD"] = wifi_password
        else:
            payload["android.app.extra.PROVISIONING_WIFI_SECURITY_TYPE"] = "NONE"
    if locale:
        payload["android.app.extra.PROVISIONING_LOCALE"] = locale
    if timezone:
        payload["android.app.extra.PROVISIONING_TIME_ZONE"] = timezone

    return payload


def payload_to_json(payload: dict) -> str:
    """JSON compacto (sin espacios) — es lo que se codifica en el QR."""
    return json.dumps(payload, separators=(",", ":"))


def render_qr_svg(payload: dict) -> str:
    """Payload -> SVG inline (string, sin <?xml ?> ni DOCTYPE) del QR.

    Correccion de errores "q" (quartile, ~25%): los QR de provisioning son
    largos (URL absoluta + checksum + wifi opcional) y este nivel deja
    margen razonable sin inflar demasiado la grilla. `scale=6` da un SVG de
    varios cientos de px de lado incluso para el payload minimo — sobra
    para escanear desde pantalla o imprimir (pedido: minimo ~360 px).
    """
    qr = segno.make(payload_to_json(payload), error="q")
    return qr.svg_inline(scale=6)
