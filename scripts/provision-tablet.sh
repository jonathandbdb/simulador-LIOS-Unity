#!/usr/bin/env bash
# Provisión de tablets como Android Device Owner (kiosco) -- Simulador de LIOs.
#
# Qué hace (flujo por defecto, ver docs/builds-deploy.md "Provisión de tablets
# (Device Owner)"):
#   1. adb wait-for-device
#   2. verifica que la tablet NO tenga cuentas configuradas (dpm set-device-owner
#      falla si hay alguna) -- si las hay, aborta con instrucciones.
#   3. adb install -r <apk>
#   4. adb shell dpm set-device-owner com.simulador.tablet/com.simulador.kiosk.SimuladorDeviceAdminReceiver
#   5. adb shell appops set com.simulador.tablet REQUEST_INSTALL_PACKAGES allow
#      (red de seguridad para el OTA mientras no exista la Fase C)
#   6. lanza la app (adb shell monkey)
#   7. verifica: dumpsys device_policy (Device Owner) + dumpsys package (versionName)
#
# Qué NO hace:
#   - No configura el WiFi de la tablet (se hace DESDE la app, botón "Red
#     Wi-Fi" -- Fase B, KioskManager.OpenWifiSettings).
#   - No desbloquea el storage (FBE) tras un reboot en tablets ya bloqueadas
#     -- ver la nota de memoria "tablet-fbe-locked-boot-install": desbloqueá a
#     mano antes de instalar/lanzar, si no "Not enough storage space" es
#     espurio.
#   - No puede QUITAR un Device Owner ya registrado -- ver --unprovision abajo.
#
# Uso:
#   scripts/provision-tablet.sh [--apk <path>] [--serial <adb serial>]
#   scripts/provision-tablet.sh --fix-setup [--serial <serial>]
#   scripts/provision-tablet.sh --unprovision [--serial <serial>]
#
# Requisitos (flujo por defecto):
#   - Tablet recién salida de fábrica (o factory-reset), CERO cuentas
#     agregadas, Depuración USB activada y esta PC autorizada
#     (`adb devices` debe listarla como "device", no "unauthorized").
#   - APK de la tablet ya buildeado: Simulador > Build Tablet (Android)
#     (Builds/Android/Simulador.apk, --apk para otra ruta).
#
# Gotcha "already provisioned": dpm set-device-owner falla con
# "Trying to set the device owner, but device is already provisioned" en
# cualquier tablet que ya pasó por el asistente de configuración inicial (lo
# hacen la mayoría de fábrica al primer boot, aunque no se haya agregado
# ninguna cuenta). --fix-setup aplica el truco SIN ROOT
# (`settings put global device_provisioned 0` + insertar
# `user_setup_complete=0` en Settings.Secure) para volver a un estado "sin
# provisionar" y poder reintentar set-device-owner sin un factory reset.
#
# Salida del kiosco / soporte: `dpm remove-active-admin` NO sirve para sacar
# un DEVICE OWNER (solo aplica a "device admins" comunes, no al owner).
# Android exige `clearDeviceOwnerApp()` llamado DESDE la propia app, o un
# factory reset completo. --unprovision de este script SOLO desinstala la
# app (el Device Owner queda registrado a nivel de sistema, aunque sin la app
# no puede hacer nada -- Android permite reinstalar y volver a apuntar el
# mismo componente como admin activo). Para soporte real en una tablet en
# campo: usar el gesto de la app (7 taps en el título de la pantalla de
# conexión + PIN de servicio, ver docs/tablet.md) para salir del lock task y
# conectar adb ahí, o factory reset si hace falta limpiar el Device Owner del
# todo.
#
# Exit code: 0 si todo OK; 1 en el primer paso que falla (mensaje claro en stderr).

set -uo pipefail

APK_PATH="Builds/Android/Simulador.apk"
SERIAL=""
MODE="provision" # provision | unprovision | fix-setup

readonly PACKAGE="com.simulador.tablet"
readonly RECEIVER="com.simulador.kiosk.SimuladorDeviceAdminReceiver"
readonly ADMIN_COMPONENT="${PACKAGE}/${RECEIVER}"

usage() {
    cat <<'EOF'
Uso: scripts/provision-tablet.sh [opciones]

  --apk <path>        Ruta al APK de la tablet (default: Builds/Android/Simulador.apk).
  --serial <serial>   Serial de adb (si hay más de un dispositivo conectado).
  --fix-setup         Aplica el truco sin root para volver la tablet a estado
                       "no provisionado" (gotcha "already provisioned", ver cabecera).
  --unprovision       SOLO desinstala la app -- NO quita el Device Owner (ver cabecera).
  -h, --help          Esta ayuda.

Requisitos y qué NO hace: ver la cabecera del script.
EOF
}

while [[ $# -gt 0 ]]; do
    case "$1" in
        --apk)
            APK_PATH="$2"; shift 2 ;;
        --apk=*)
            APK_PATH="${1#*=}"; shift ;;
        --serial)
            SERIAL="$2"; shift 2 ;;
        --serial=*)
            SERIAL="${1#*=}"; shift ;;
        --fix-setup)
            MODE="fix-setup"; shift ;;
        --unprovision)
            MODE="unprovision"; shift ;;
        -h|--help)
            usage; exit 0 ;;
        *)
            echo "ERROR: opción desconocida: $1" >&2
            usage
            exit 1
            ;;
    esac
done

ADB=(adb)
if [[ -n "$SERIAL" ]]; then
    ADB=(adb -s "$SERIAL")
fi

step() { echo "==> $*"; }
fail() { echo "ERROR: $*" >&2; exit 1; }

# MENOR (correcciones): chequear el APK ANTES de esperar el dispositivo -- solo
# aplica al flujo por defecto (--fix-setup/--unprovision no necesitan el APK), y
# evita quedarse colgado en wait-for-device para recien fallar por una ruta mal
# tipeada.
if [[ "$MODE" == "provision" ]]; then
    [[ -f "$APK_PATH" ]] || fail "No se encontró el APK en '$APK_PATH' (usá --apk <path> para otra ruta)."
fi

step "Esperando dispositivo..."
"${ADB[@]}" wait-for-device || fail "adb wait-for-device falló -- ¿está conectada y autorizada? (adb devices)"

run_fix_setup() {
    step "Aplicando --fix-setup (device_provisioned=0, user_setup_complete=0)..."
    "${ADB[@]}" shell settings put global device_provisioned 0 \
        || fail "No se pudo setear settings global device_provisioned"
    "${ADB[@]}" shell content insert --uri content://settings/secure \
        --bind name:s:user_setup_complete --bind value:s:0 \
        || fail "No se pudo setear Settings.Secure user_setup_complete"
    step "Listo. La tablet debería aceptar 'dpm set-device-owner' ahora -- reintentá sin --fix-setup."
}

run_unprovision() {
    step "AVISO: dpm remove-active-admin NO quita un Device Owner (solo device admins comunes)."
    step "Este flag SOLO desinstala la app -- el Device Owner queda registrado a nivel de sistema."
    step "Para limpiar el Device Owner del todo: clearDeviceOwnerApp() desde la app, o factory reset."
    step "Desinstalando ${PACKAGE}..."
    "${ADB[@]}" uninstall "$PACKAGE" || fail "adb uninstall falló (¿estaba instalado?)"
    step "App desinstalada."
}

run_provision() {
    [[ -f "$APK_PATH" ]] || fail "No se encontró el APK en '$APK_PATH' (usá --apk <path> para otra ruta)."

    step "Verificando que la tablet no tenga cuentas configuradas..."
    local account_count
    account_count="$("${ADB[@]}" shell dumpsys account 2>/dev/null | grep -c 'Account {' || true)"
    account_count="${account_count//[!0-9]/}"
    [[ -z "$account_count" ]] && account_count=0
    if [[ "$account_count" -ne 0 ]]; then
        fail "La tablet tiene $account_count cuenta(s) configurada(s). dpm set-device-owner exige CERO cuentas -- hacé un factory reset y SALTEÁ el asistente (no inicies sesión con ninguna cuenta durante el setup)."
    fi

    step "Instalando $APK_PATH..."
    "${ADB[@]}" install -r "$APK_PATH" || fail "adb install falló"

    step "Registrando Device Owner (${ADMIN_COMPONENT})..."
    local set_owner_output set_owner_status
    set_owner_output="$("${ADB[@]}" shell dpm set-device-owner "$ADMIN_COMPONENT" 2>&1)"
    set_owner_status=$?
    echo "$set_owner_output"
    if [[ $set_owner_status -ne 0 ]]; then
        if echo "$set_owner_output" | grep -qi "already provisioned"; then
            fail "La tablet ya está 'provisioned' (pasó por el asistente de configuración). Reintentá con --fix-setup y volvé a correr este script sin esa flag."
        fi
        fail "dpm set-device-owner falló -- ver la salida de arriba."
    fi

    step "Permitiendo REQUEST_INSTALL_PACKAGES (red de seguridad para el OTA, Fase C)..."
    "${ADB[@]}" shell appops set "$PACKAGE" REQUEST_INSTALL_PACKAGES allow \
        || fail "No se pudo setear el appop REQUEST_INSTALL_PACKAGES"

    step "Lanzando la app..."
    "${ADB[@]}" shell monkey -p "$PACKAGE" 1 >/dev/null || fail "No se pudo lanzar la app (monkey)"

    step "Verificando Device Owner..."
    "${ADB[@]}" shell dumpsys device_policy | grep -A3 "Device Owner" \
        || echo "  (sin salida -- revisar manualmente: adb shell dumpsys device_policy)"

    step "Verificando versión instalada..."
    "${ADB[@]}" shell dumpsys package "$PACKAGE" | grep versionName \
        || echo "  (sin salida -- revisar manualmente: adb shell dumpsys package $PACKAGE)"

    step "Provisioning completo. La tablet debería arrancar directo en la app (HOME persistente) en el próximo boot."
}

case "$MODE" in
    provision) run_provision ;;
    fix-setup) run_fix_setup ;;
    unprovision) run_unprovision ;;
esac
