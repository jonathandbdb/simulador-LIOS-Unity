#!/usr/bin/env bash
# Provisión de tablets como Android Device Owner (kiosco) -- Simulador de LIOs.
#
# Pensado para que OTRO dev, en OTRA PC (Windows 11 + Git Bash, sin Unity
# necesariamente), pueda dejar una tablet nueva lista para el cliente con UNA
# sola orden. Por defecto NO hace falta un build local: el script descarga el
# APK de la tablet publicado en el backend (mismo manifest que usa el OTA,
# `docs/updates.md`), verifica su SHA256 y recién ahí instala.
#
# Qué hace (flujo por defecto, ver docs/builds-deploy.md "Provisión de tablets
# (Device Owner)"):
#   0. Si NO se pasó --apk: descarga el manifest de `--backend` (default
#      https://vr.conecta.sh), baja el APK que indica y verifica su SHA256.
#      Aborta sin instalar nada si el hash no coincide o el backend no tiene
#      versión activa (503).
#   1. Pre-flight: busca un dispositivo por adb con checklist en pantalla si
#      tarda (hasta 5 min).
#   2. verifica que la tablet NO tenga cuentas configuradas (dpm set-device-owner
#      falla si hay alguna) -- si las hay, aborta con instrucciones.
#   3. adb install -r <apk>
#   4. adb shell dpm set-device-owner com.simulador.tablet/com.simulador.kiosk.SimuladorDeviceAdminReceiver
#   5. adb shell appops set com.simulador.tablet REQUEST_INSTALL_PACKAGES allow
#      (red de seguridad para el OTA mientras no exista la Fase C)
#   6. lanza la app (intent HOME explícito, ver gotcha más abajo)
#   7. verifica: dumpsys device_policy (Device Owner) + dumpsys package (versionName)
#   8. reinicia la tablet y confirma en vivo que arranca directo en la app,
#      en foco y con el kiosco (lock task) activo -- salvo --no-reboot.
#
# Qué NO hace:
#   - No configura el WiFi de la tablet (se hace DESDE la app, botón "Red
#     Wi-Fi" -- Fase B, KioskManager.OpenWifiSettings). La tablet NO necesita
#     WiFi para provisionarse (solo esta PC necesita internet, y solo si no
#     se pasa --apk).
#   - No desbloquea el storage (FBE) tras un reboot en tablets ya bloqueadas
#     -- ver la nota de memoria "tablet-fbe-locked-boot-install": desbloqueá a
#     mano antes de instalar/lanzar, si no "Not enough storage space" es
#     espurio.
#   - No puede QUITAR un Device Owner ya registrado -- ver --unprovision abajo.
#
# Uso:
#   scripts/provision-tablet.sh [--backend <url>] [--serial <adb serial>] [--no-reboot]
#   scripts/provision-tablet.sh --apk <path> [--serial <serial>]     # modo desarrollador
#   scripts/provision-tablet.sh --download-only [--backend <url>]   # solo probar la descarga
#   scripts/provision-tablet.sh --fix-setup [--serial <serial>]
#   scripts/provision-tablet.sh --unprovision [--serial <serial>]
#   scripts/provision-tablet.sh --help
#
# Requisitos:
#   - Esta PC: `adb` (o `adb.exe` de platform-tools autodetectado, ver más
#     abajo) y `curl` disponibles. Sin --apk, además necesita internet para
#     llegar al backend (la tablet NO).
#   - Tablet recién salida de fábrica (o factory-reset), CERO cuentas
#     agregadas, Depuración USB activada y esta PC autorizada
#     (`adb devices` debe listarla como "device", no "unauthorized" -- el
#     pre-flight de este script guía ese paso si hace falta).
#   - Con --apk: un APK de la tablet ya buildeado (Simulador > Build Tablet
#     (Android), Builds/Android/Simulador.apk por default).
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

readonly BACKEND_URL_DEFAULT="https://vr.conecta.sh"
readonly APP_LABEL="IOLSIMULATOR Tablet" # product name fijado por TabletBuild (ver docs/tablet.md)

APK_PATH=""                       # vacío = descargar del backend; --apk lo fija (modo desarrollador)
BACKEND_URL="$BACKEND_URL_DEFAULT"
SERIAL=""
MODE="provision" # provision | unprovision | fix-setup
DOWNLOAD_ONLY=0
NO_REBOOT=0

readonly PACKAGE="com.simulador.tablet"
readonly RECEIVER="com.simulador.kiosk.SimuladorDeviceAdminReceiver"
readonly ADMIN_COMPONENT="${PACKAGE}/${RECEIVER}"
readonly UNITY_ACTIVITY="com.unity3d.player.UnityPlayerGameActivity"
readonly APP_ACTIVITY="${PACKAGE}/${UNITY_ACTIVITY}"

usage() {
    cat <<EOF
Uso: scripts/provision-tablet.sh [opciones]

Deja una tablet Android lista como Device Owner (kiosco) para el Simulador de
LIOs, con UNA sola orden. Por defecto DESCARGA el APK de la tablet publicado
en el backend -- no hace falta Unity ni un build local. Ver
docs/builds-deploy.md "Provisión de tablets (Device Owner)" para el
procedimiento completo y los gotchas.

  --backend <url>      Backend del que descargar el manifest/APK (default:
                        ${BACKEND_URL_DEFAULT}).
  --apk <path>         Usa un APK LOCAL en vez de descargar del backend (modo
                        desarrollador -- ej. un build recién generado con
                        Simulador > Build Tablet (Android)).
  --download-only      Solo descarga y verifica el APK (SHA256), imprime su
                        ruta local y termina -- no toca ningún dispositivo.
                        Sirve para probar la conexión al backend sin tener la
                        tablet a mano.
  --serial <serial>    Serial de adb (si hay más de un dispositivo conectado;
                        ver 'adb devices').
  --fix-setup          Aplica el truco sin root para volver la tablet a
                        estado "no provisionado" (gotcha "already
                        provisioned", ver cabecera del script).
  --unprovision        SOLO desinstala la app -- NO quita el Device Owner
                        (ver cabecera del script).
  --no-reboot          No reinicia la tablet al final para confirmar el
                        arranque directo (por defecto SÍ reinicia y verifica).
  -h, --help           Esta ayuda.

Ejemplos:
  scripts/provision-tablet.sh
      Flujo completo por defecto: descarga el APK del backend, provisiona la
      primera tablet que se conecte por USB y reinicia para confirmar que
      arranca directo en modo kiosco.

  scripts/provision-tablet.sh --serial R58N123ABC
      Igual que arriba, apuntando a un serial concreto (varios dispositivos
      conectados a la vez -- seriales con 'adb devices').

  scripts/provision-tablet.sh --apk Builds/Android/Simulador.apk
      Modo desarrollador: usa un APK local en vez de descargar del backend.

  scripts/provision-tablet.sh --download-only
      Solo prueba la descarga + verificación del APK del backend, sin tocar
      ninguna tablet.

  scripts/provision-tablet.sh --fix-setup --serial R58N123ABC
      Gotcha "already provisioned": desbloquea esa tablet para poder
      reintentar el provisioning sin un factory reset.

  scripts/provision-tablet.sh --unprovision --serial R58N123ABC
      Desinstala la app de esa tablet (el Device Owner queda registrado a
      nivel de sistema -- ver cabecera del script).

  scripts/provision-tablet.sh --no-reboot
      Flujo completo pero sin el reboot final de verificación (útil si vas a
      seguir trabajando sobre la tablet a mano).

Requisitos y qué NO hace: ver la cabecera del script y docs/builds-deploy.md.
EOF
}

while [[ $# -gt 0 ]]; do
    case "$1" in
        --apk)
            APK_PATH="$2"; shift 2 ;;
        --apk=*)
            APK_PATH="${1#*=}"; shift ;;
        --backend)
            BACKEND_URL="$2"; shift 2 ;;
        --backend=*)
            BACKEND_URL="${1#*=}"; shift ;;
        --serial)
            SERIAL="$2"; shift 2 ;;
        --serial=*)
            SERIAL="${1#*=}"; shift ;;
        --download-only)
            DOWNLOAD_ONLY=1; shift ;;
        --fix-setup)
            MODE="fix-setup"; shift ;;
        --unprovision)
            MODE="unprovision"; shift ;;
        --no-reboot)
            NO_REBOOT=1; shift ;;
        -h|--help)
            usage; exit 0 ;;
        *)
            echo "ERROR: opción desconocida: $1" >&2
            usage
            exit 1
            ;;
    esac
done

step() { echo "==> $*"; }
fail() { echo "ERROR: $*" >&2; exit 1; }

# ---------------------------------------------------------------------------
# APK: --apk local (modo desarrollador) o descarga + verificación desde el
# manifest del backend (mismo contrato que el OTA, docs/updates.md).
# Sin jq ni python (Git Bash no los trae) -- grep/cut sobre el JSON plano.
# ---------------------------------------------------------------------------
manifest_field() {
    # manifest_field <json de una sola línea> <nombre del campo>
    echo "$1" | grep -oE "\"$2\":\"[^\"]*\"" | head -n1 | cut -d'"' -f4
}

download_apk_from_backend() {
    local manifest_url raw_response curl_exit http_status body
    local apk_version apk_url apk_sha256
    local tmp_dir apk_file sha_tool actual_sha256 file_size size_mb

    manifest_url="${BACKEND_URL%/}/api/manifest.json?app=tablet"
    step "Consultando manifest de tablet en $manifest_url..."
    raw_response="$(curl -sS -w '\nHTTPSTATUS:%{http_code}' "$manifest_url")"
    curl_exit=$?
    if [[ $curl_exit -ne 0 ]]; then
        fail "No se pudo contactar $BACKEND_URL (curl exit=$curl_exit) -- revisá la conexión a internet de esta PC, o usá --apk <path> con un APK local ya buildeado."
    fi

    http_status="$(echo "$raw_response" | sed -n 's/^HTTPSTATUS://p')"
    body="$(echo "$raw_response" | sed '$d')"

    if [[ "$http_status" == "503" ]]; then
        fail "El backend no tiene ninguna versión de tablet publicada (503 en $manifest_url) -- avisá a quien administra el backend, o usá --apk <path> con un APK local ya buildeado."
    fi
    if [[ "$http_status" != "200" ]]; then
        fail "El backend respondió HTTP ${http_status:-?} en $manifest_url -- revisar manualmente (¿la URL de --backend es correcta?)."
    fi

    apk_version="$(manifest_field "$body" apk_version)"
    apk_url="$(manifest_field "$body" apk_url)"
    apk_sha256="$(manifest_field "$body" apk_sha256)"
    if [[ -z "$apk_version" || -z "$apk_url" || -z "$apk_sha256" ]]; then
        fail "No se pudo parsear el manifest de $manifest_url (respuesta: $body)"
    fi
    step "Versión publicada: tablet $apk_version ($apk_url)"

    tmp_dir="$(mktemp -d)" || fail "No se pudo crear un directorio temporal para la descarga."
    apk_file="$tmp_dir/simulador-tablet-${apk_version}.apk"

    step "Descargando..."
    curl -fL --progress-bar -o "$apk_file" "$apk_url" \
        || fail "La descarga del APK falló ($apk_url) -- revisá la conexión a internet."

    if command -v sha256sum >/dev/null 2>&1; then
        sha_tool="sha256sum"
        actual_sha256="$(sha256sum "$apk_file" | awk '{print $1}')"
    elif command -v shasum >/dev/null 2>&1; then
        sha_tool="shasum -a 256"
        actual_sha256="$(shasum -a 256 "$apk_file" | awk '{print $1}')"
    else
        fail "No se encontró 'sha256sum' ni 'shasum' en esta PC -- no se puede verificar la integridad del APK descargado."
    fi

    if [[ "$(echo "$actual_sha256" | tr '[:upper:]' '[:lower:]')" != "$(echo "$apk_sha256" | tr '[:upper:]' '[:lower:]')" ]]; then
        rm -f "$apk_file"
        fail "El SHA256 del APK descargado NO coincide (esperado $apk_sha256, obtenido $actual_sha256 vía $sha_tool) -- descarga corrupta o manifest inconsistente. No se instala nada."
    fi

    file_size="$(wc -c < "$apk_file" | tr -d ' ')"
    size_mb="$(awk -v b="$file_size" 'BEGIN{printf "%.1f", b/1024/1024}')"
    step "APK verificado: tablet ${apk_version} -- ${file_size} bytes (~${size_mb} MB) -- SHA256 OK"

    APK_PATH="$apk_file"
}

resolve_apk() {
    if [[ -n "$APK_PATH" ]]; then
        [[ -f "$APK_PATH" ]] || fail "No se encontró el APK en '$APK_PATH' (usá --apk <path> para otra ruta, o quitá --apk para descargar la última versión publicada en el backend)."
        step "Usando APK local: $APK_PATH"
        return 0
    fi
    download_apk_from_backend
}

if [[ $DOWNLOAD_ONLY -eq 1 ]]; then
    resolve_apk
    echo "$APK_PATH"
    exit 0
fi

# ---------------------------------------------------------------------------
# Detección de adb: PATH primero, si no rutas conocidas de platform-tools
# (Git Bash en Windows no suele tener adb en PATH salvo que se instale a mano).
# ---------------------------------------------------------------------------
resolve_adb_binary() {
    if command -v adb >/dev/null 2>&1; then
        echo "adb"
        return 0
    fi
    local candidates=(
        "${LOCALAPPDATA:-}/Android/Sdk/platform-tools/adb.exe"
        "/c/Android/platform-tools/adb.exe"
        "${HOME:-}/Android/Sdk/platform-tools/adb.exe"
    )
    local c
    for c in "${candidates[@]}"; do
        if [[ -n "$c" && -f "$c" ]]; then
            echo "$c"
            return 0
        fi
    done
    return 1
}

ADB_BIN="$(resolve_adb_binary)" \
    || fail "No se encontró 'adb' en el PATH ni en las rutas conocidas de Android platform-tools (\$LOCALAPPDATA/Android/Sdk/platform-tools, /c/Android/platform-tools, \$HOME/Android/Sdk/platform-tools). Instalá 'Android SDK Platform-Tools' desde https://developer.android.com/tools/releases/platform-tools y agregalo al PATH, o copiá adb.exe a una de esas rutas."

ADB=("$ADB_BIN")
if [[ -n "$SERIAL" ]]; then
    ADB=("$ADB_BIN" -s "$SERIAL")
fi

# ---------------------------------------------------------------------------
# Pre-flight amigable: busca el dispositivo ANTES de asumir que ya está ahí,
# con checklist en pantalla si tarda -- en vez de un `wait-for-device` mudo
# que puede colgarse en silencio si nadie tocó "Depuración USB" todavía.
# ---------------------------------------------------------------------------
adb_state_for_serial() {
    "$ADB_BIN" devices 2>/dev/null | tr -d '\r' | awk -F'\t' -v s="$1" '$1==s{print $2}'
}

adb_any_device_present() {
    "$ADB_BIN" devices 2>/dev/null | tr -d '\r' | awk -F'\t' '$2=="device"{f=1} END{exit !f}'
}

adb_any_unauthorized() {
    "$ADB_BIN" devices 2>/dev/null | tr -d '\r' | awk -F'\t' '$2=="unauthorized"{f=1} END{exit !f}'
}

preflight_wait_for_device() {
    step "Buscando la tablet por adb..."
    local elapsed=0 interval=2 checklist_shown=0 max_wait=300 state

    while true; do
        if [[ -n "$SERIAL" ]]; then
            state="$(adb_state_for_serial "$SERIAL")"
            [[ "$state" == "device" ]] && return 0
            if [[ "$state" == "unauthorized" ]]; then
                step "El dispositivo '$SERIAL' figura como 'unauthorized' -- aceptá el diálogo 'Permitir depuración USB' en la pantalla de la tablet."
            fi
        else
            adb_any_device_present && return 0
            if adb_any_unauthorized; then
                step "Hay un dispositivo 'unauthorized' -- aceptá el diálogo 'Permitir depuración USB' en la pantalla de la tablet."
            fi
        fi

        if [[ $elapsed -ge 15 && $checklist_shown -eq 0 ]]; then
            checklist_shown=1
            cat >&2 <<'CHECKLIST'
No se detecta ninguna tablet por adb todavía. Checklist:
  1. Si la tablet no está de fábrica: hacele un reseteo de fábrica primero.
  2. Al pasar el asistente inicial de Android: SALTEÁ el inicio de sesión,
     SIN agregar ninguna cuenta (Google ni de fabricante).
  3. Ajustes -> Acerca de la tablet -> tocar 7 veces "Número de compilación"
     (activa Opciones de desarrollador).
  4. Ajustes -> Sistema -> Opciones de desarrollador -> activar
     "Depuración USB".
  5. Conectar el cable USB de DATOS (no solo carga) a esta PC. En el diálogo
     que aparece EN LA TABLET, tocar "Permitir siempre desde esta
     computadora" y aceptar.
Reintentando cada pocos segundos (hasta 5 minutos en total)...
CHECKLIST
        fi

        if [[ $elapsed -ge $max_wait ]]; then
            fail "No se detectó ninguna tablet en 5 minutos -- revisá el cable USB (que sea de datos), que la tablet esté encendida y desbloqueada, y la checklist de arriba."
        fi

        sleep "$interval"
        elapsed=$((elapsed + interval))
    done
}

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
    [[ -f "$APK_PATH" ]] || fail "No se encontró el APK en '$APK_PATH'."

    step "Verificando que la tablet no tenga cuentas configuradas..."
    local account_count
    account_count="$("${ADB[@]}" shell dumpsys account 2>/dev/null | tr -d '\r' | grep -c 'Account {' || true)"
    account_count="${account_count//[!0-9]/}"
    [[ -z "$account_count" ]] && account_count=0
    if [[ "$account_count" -ne 0 ]]; then
        fail "La tablet tiene $account_count cuenta(s) configurada(s). dpm set-device-owner exige CERO cuentas -- hacé un factory reset y SALTEÁ el asistente (no inicies sesión con ninguna cuenta durante el setup)."
    fi

    step "Instalando $APK_PATH..."
    "${ADB[@]}" install -r "$APK_PATH" || fail "adb install falló"

    step "Registrando Device Owner (${ADMIN_COMPONENT})..."
    local set_owner_output set_owner_status
    set_owner_output="$("${ADB[@]}" shell dpm set-device-owner "$ADMIN_COMPONENT" 2>&1 | tr -d '\r')"
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

    # Evita el dialogo nativo "Visualizacion en pantalla completa / Entendido"
    # que Android muestra la PRIMERA vez que una app oculta la barra de estado
    # (KioskManager.ApplyPolicies -> setStatusBarDisabled, se dispara con el
    # primer lanzamiento de abajo) -- en una clinica nadie deberia tener que
    # tocarlo a mano. DevicePolicyManager.setSecureSetting() del Device Owner
    # no cubre esta clave; el camino soportado es este ajuste via adb (shell
    # tiene WRITE_SECURE_SETTINGS). Idempotente, seguro de repetir.
    step "Confirmando el dialogo de modo inmersivo (immersive_mode_confirmations)..."
    "${ADB[@]}" shell settings put secure immersive_mode_confirmations confirmed \
        || fail "No se pudo setear immersive_mode_confirmations"

    # Gotcha real, CONFIRMADO en vivo en la PHILCO TP10A464 (ver docs/builds-deploy.md
    # "Provision de tablets"): lanzar con `monkey -p` (o el launcher de fabrica) usa
    # un intent LAUNCHER -> crea una tarea type=standard aunque la Activity sea
    # singleTask. Si justo despues se pulsa Home, Android busca la HOME en una
    # tarea type=home, NO reusa la standard, y crea una SEGUNDA instancia de la
    # Activity en el MISMO proceso -- Unity (UnityFoldingFeaturesWrapper es
    # estatico por proceso) crashea con "init() should be called only once".
    # El fix NO puede ser "lanzar por LAUNCHER y despues corregir con force-stop":
    # una vez que KioskManager.ApplyPolicies()+EnterLockTask() corren (Start() lo
    # hace solo, apenas la app arranca), el propio Android BLOQUEA el force-stop
    # de la app en lock task -- confirmado en vivo, logcat del sistema:
    # "ActivityManager: Ignoring request to force stop protected package
    # com.simulador.tablet u0" (exit code 0, sin error visible, pero la app NUNCA
    # se reinicia). Reintentar el `am start ... HOME` sobre esa tarea standard ya
    # bloqueada dispara la MISMA carrera de arriba, ahora GARANTIZADA en vez de
    # ocasional. Fix real: lanzar la app la PRIMERA vez con un intent HOME
    # explicito (mismo mecanismo que usa Android para relanzarla sola tras un
    # reboot) en vez de LAUNCHER -- la tarea nace type=home desde el vamos y
    # jamas hay carrera que ganar.
    step "Lanzando la app (intent HOME explicito, no LAUNCHER -- ver gotcha en el script)..."
    "${ADB[@]}" shell am start -a android.intent.action.MAIN -c android.intent.category.HOME \
        -n "$APP_ACTIVITY" >/dev/null \
        || fail "No se pudo lanzar la app (am start)"

    step "Esperando a que la app aplique las politicas de kiosco (HOME persistente, hasta 30s)..."
    local resolved_pkg waited task_line
    resolved_pkg=""
    waited=0
    while [[ $waited -lt 30 ]]; do
        resolved_pkg="$("${ADB[@]}" shell cmd package resolve-activity \
            -a android.intent.action.MAIN -c android.intent.category.HOME 2>/dev/null \
            | tr -d '\r' | grep -m1 'packageName=')"
        if [[ "$resolved_pkg" == *"$PACKAGE"* ]]; then
            break
        fi
        sleep 1
        waited=$((waited + 1))
    done
    [[ "$resolved_pkg" == *"$PACKAGE"* ]] \
        || fail "La app no quedo como HOME persistente tras 30s (¿ApplyPolicies no corrio? revisar: adb shell logcat -s Unity | grep Kiosk)"
    step "HOME persistente confirmada (${waited}s)."

    step "Verificando que la tarea sea 'home' (igual que tras un reboot real, sin relanzar nada)..."
    task_line="$("${ADB[@]}" shell dumpsys activity activities 2>/dev/null | tr -d '\r' | grep -m1 -E "Task\{.*$PACKAGE")"
    echo "  $task_line"
    [[ "$task_line" == *"type=home"* ]] \
        || fail "La tarea no quedo type=home -- revisar manualmente (dumpsys activity activities)."
    step "Tarea 'home' confirmada."

    step "Verificando Device Owner..."
    "${ADB[@]}" shell dumpsys device_policy 2>/dev/null | tr -d '\r' | grep -A3 "Device Owner" \
        || echo "  (sin salida -- revisar manualmente: adb shell dumpsys device_policy)"

    step "Verificando versión instalada..."
    "${ADB[@]}" shell dumpsys package "$PACKAGE" 2>/dev/null | tr -d '\r' | grep versionName \
        || echo "  (sin salida -- revisar manualmente: adb shell dumpsys package $PACKAGE)"

    step "Provisioning completo."
}

# ---------------------------------------------------------------------------
# Cierre con prueba real: reinicia la tablet y confirma en vivo que arranca
# directo en la app, en foco, con el kiosco (lock task) activo -- exactamente
# lo que le va a pasar al paciente/clínico al prender la tablet en la clínica.
# ---------------------------------------------------------------------------
adb_resolved_serial() {
    if [[ -n "$SERIAL" ]]; then
        echo "$SERIAL"
        return 0
    fi
    "${ADB[@]}" get-serialno 2>/dev/null | tr -d '\r\n'
}

print_summary() {
    local model android_release serial_shown version_name version_code

    model="$("${ADB[@]}" shell getprop ro.product.model 2>/dev/null | tr -d '\r\n')"
    android_release="$("${ADB[@]}" shell getprop ro.build.version.release 2>/dev/null | tr -d '\r\n')"
    serial_shown="$(adb_resolved_serial)"
    version_name="$("${ADB[@]}" shell dumpsys package "$PACKAGE" 2>/dev/null | tr -d '\r' | grep -m1 -oE 'versionName=[^[:space:]]*' | cut -d= -f2)"
    version_code="$("${ADB[@]}" shell dumpsys package "$PACKAGE" 2>/dev/null | tr -d '\r' | grep -m1 -oE 'versionCode=[0-9]*' | cut -d= -f2)"

    echo ""
    echo "==> TABLET LISTA PARA ENTREGAR"
    echo "    modelo: ${model:-?} (Android ${android_release:-?})   serial: ${serial_shown:-?}"
    echo "    app: ${APP_LABEL} ${version_name:-?} (${version_code:-?})   owner: OK   kiosco: LOCKED   arranque directo: OK"
}

verify_boot_and_summary() {
    if [[ $NO_REBOOT -eq 1 ]]; then
        step "Salteando el reboot final (--no-reboot). Para confirmar el arranque directo, reiniciá la tablet a mano."
        return 0
    fi

    step "Reiniciando la tablet para probar el arranque directo (adb reboot)..."
    "${ADB[@]}" reboot || fail "No se pudo reiniciar la tablet (adb reboot)."

    step "Esperando a que vuelva (adb wait-for-device)..."
    "${ADB[@]}" wait-for-device || fail "adb wait-for-device falló tras el reboot."

    step "Esperando a que termine de bootear (sys.boot_completed=1)..."
    local waited=0 max_boot_wait=120 boot_completed=""
    while [[ $waited -lt $max_boot_wait ]]; do
        boot_completed="$("${ADB[@]}" shell getprop sys.boot_completed 2>/dev/null | tr -d '\r\n')"
        [[ "$boot_completed" == "1" ]] && break
        sleep 2
        waited=$((waited + 2))
    done
    [[ "$boot_completed" == "1" ]] \
        || fail "La tablet no terminó de bootear en ${max_boot_wait}s tras el reboot (sys.boot_completed nunca llegó a 1) -- revisar manualmente."

    step "Boot completo (${waited}s). Esperando 15s más a que la app termine de arrancar y aplicar el kiosco..."
    sleep 15

    step "Verificando que la app quedó en foco..."
    local focus_line
    focus_line="$("${ADB[@]}" shell dumpsys window 2>/dev/null | tr -d '\r' | grep -m1 'mCurrentFocus')"
    echo "  $focus_line"
    [[ "$focus_line" == *"$PACKAGE"* ]] \
        || fail "La app no está en foco tras el reboot (mCurrentFocus no contiene $PACKAGE) -- revisar manualmente: adb shell dumpsys window | grep mCurrentFocus (¿'already provisioned' sin --fix-setup? ¿la tablet quedó en el asistente de Android?)"

    step "Verificando que el kiosco (lock task) está activo..."
    local lock_line
    lock_line="$("${ADB[@]}" shell dumpsys activity activities 2>/dev/null | tr -d '\r' | grep -m1 'mLockTaskModeState')"
    echo "  $lock_line"
    [[ "$lock_line" == *"LOCKED"* ]] \
        || fail "El kiosco no quedó LOCKED tras el reboot (mLockTaskModeState) -- revisar manualmente: adb shell dumpsys activity activities | grep mLockTaskModeState"

    print_summary
}

case "$MODE" in
    provision)
        resolve_apk
        preflight_wait_for_device
        run_provision
        verify_boot_and_summary
        ;;
    fix-setup)
        preflight_wait_for_device
        run_fix_setup
        ;;
    unprovision)
        preflight_wait_for_device
        run_unprovision
        ;;
esac
