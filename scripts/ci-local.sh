#!/usr/bin/env bash
# CI local para el Simulador de LIOs (Windows + Git Bash, sin runner remoto).
#
# Corre en secuencia:
#   (a) tests EditMode de Unity en batchmode (NUnit XML)
#   (b) opcionalmente, build de tablet y/o visor (--build[=tablet|visor|both])
#   (c) pytest del backend (venv temporal) salvo --skip-backend
#
# Uso:
#   scripts/ci-local.sh [--build[=tablet|visor|both]] [--skip-tests] [--skip-backend]
#                        [--unity-path=<ruta a Unity.exe>] [-h|--help]
#
# Variables de entorno:
#   UNITY_PATH   ruta al ejecutable de Unity (si no se pasa --unity-path). Si no se define
#                ninguna, se autodetecta la instalación de Unity Hub para la versión de
#                ProjectSettings/ProjectVersion.txt.
#
# Gotcha crítico: Unity en -batchmode falla (o cuelga) si el proyecto ya está abierto en el
# Editor (Temp/UnityLockfile). Este script lo detecta ANTES de tocar Unity y aborta con un
# mensaje claro en vez de dejar que Unity falle de forma críptica.
#
# Exit code: 0 si todas las etapas ejecutadas terminaron OK; !=0 si alguna falló.
# Ver docs/builds-deploy.md § CI local para el detalle y los gotchas documentados.

set -uo pipefail

PROJECT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
LOCKFILE="$PROJECT_DIR/Temp/UnityLockfile"
ARTIFACTS_DIR="$PROJECT_DIR/ci-artifacts"
EDITMODE_XML="$ARTIFACTS_DIR/editmode-results.xml"
UNITY_TEST_LOG="$ARTIFACTS_DIR/unity-editmode.log"
UNITY_BUILD_LOG="$ARTIFACTS_DIR/unity-build-tablet.log"
BACKEND_LOG="$ARTIFACTS_DIR/backend-pytest.log"
TABLET_APK="$PROJECT_DIR/Builds/Android/Simulador.apk"

DO_BUILD=""            # "" | tablet | visor | both
SKIP_TESTS=0
SKIP_BACKEND=0
UNITY_PATH_OVERRIDE=""

STAGE_NAMES=()
STAGE_STATUSES=()
OVERALL_FAIL=0

usage() {
    cat <<'EOF'
Uso: scripts/ci-local.sh [opciones]

  --build              Buildea la tablet (headless, via TabletBuild.BuildTabletMenu).
  --build=tablet        Idem --build.
  --build=visor         Intenta el build del visor (ver Gotchas: hoy no está soportado en
                         headless sin un método de Editor dedicado; queda como SKIP explicado).
  --build=both          Tablet + intento de visor (ver arriba).
  --skip-tests          Salta los tests EditMode de Unity.
  --skip-backend        Salta pytest del backend.
  --unity-path=<ruta>   Ruta explícita a Unity.exe (si no, usa $UNITY_PATH o autodetección).
  -h, --help            Esta ayuda.

Exit code 0 solo si todas las etapas ejecutadas (no saltadas) pasaron.
EOF
}

while [[ $# -gt 0 ]]; do
    case "$1" in
        --build)
            DO_BUILD="tablet"
            shift
            ;;
        --build=*)
            DO_BUILD="${1#*=}"
            shift
            ;;
        --skip-tests)
            SKIP_TESTS=1
            shift
            ;;
        --skip-backend)
            SKIP_BACKEND=1
            shift
            ;;
        --unity-path=*)
            UNITY_PATH_OVERRIDE="${1#*=}"
            shift
            ;;
        -h|--help)
            usage
            exit 0
            ;;
        *)
            echo "Argumento desconocido: $1" >&2
            usage
            exit 2
            ;;
    esac
done

if [[ -n "$DO_BUILD" && "$DO_BUILD" != "tablet" && "$DO_BUILD" != "visor" && "$DO_BUILD" != "both" ]]; then
    echo "Valor inválido para --build: '$DO_BUILD' (usar tablet|visor|both)" >&2
    exit 2
fi

mkdir -p "$ARTIFACTS_DIR"

record() {
    # record <nombre> <status: PASS|FAIL|SKIP>
    STAGE_NAMES+=("$1")
    STAGE_STATUSES+=("$2")
}

pass() {
    echo "✔ $1"
    record "$1" "PASS"
}

fail() {
    echo "✖ $1 — $2"
    record "$1" "FAIL"
    OVERALL_FAIL=1
}

skip() {
    echo "⚠ $1 (skip) — $2"
    record "$1" "SKIP"
}

# ---------------------------------------------------------------------------
# Gotcha crítico: Unity batchmode + proyecto abierto en el Editor.
# ---------------------------------------------------------------------------
check_unity_not_locked() {
    if [[ ! -f "$LOCKFILE" ]]; then
        return 0
    fi

    echo ""
    echo "✖ El Editor de Unity tiene este proyecto abierto (existe $LOCKFILE)."
    if command -v tasklist >/dev/null 2>&1 && tasklist //FI "IMAGENAME eq Unity.exe" //NH 2>/dev/null | grep -qi "Unity.exe"; then
        echo "   Unity.exe está corriendo ahora mismo. Un batchmode contra el mismo"
        echo "   proyecto se cuelga o falla de forma críptica (proyecto en uso)."
        echo "   -> Cerrá el Editor primero y volvé a correr scripts/ci-local.sh."
    else
        echo "   No se detecta un proceso Unity.exe activo: puede ser un lockfile residual"
        echo "   de un cierre anormal (crash). Verificá que el Editor esté realmente"
        echo "   cerrado; si estás seguro, podés borrar '$LOCKFILE' a mano y reintentar."
        echo "   -> Cerrá el Editor primero (o confirmá que ya está cerrado) y reintentá."
    fi
    echo ""
    return 1
}

# ---------------------------------------------------------------------------
# Detección del ejecutable de Unity.
# ---------------------------------------------------------------------------
resolve_unity_path() {
    if [[ -n "$UNITY_PATH_OVERRIDE" ]]; then
        echo "$UNITY_PATH_OVERRIDE"
        return 0
    fi
    if [[ -n "${UNITY_PATH:-}" ]]; then
        echo "$UNITY_PATH"
        return 0
    fi

    local version=""
    if [[ -f "$PROJECT_DIR/ProjectSettings/ProjectVersion.txt" ]]; then
        version="$(grep -m1 '^m_EditorVersion:' "$PROJECT_DIR/ProjectSettings/ProjectVersion.txt" | awk '{print $2}')"
    fi

    local candidates=()
    if [[ -n "$version" ]]; then
        candidates+=("/c/Program Files/Unity/Hub/Editor/${version}/Editor/Unity.exe")
    fi
    candidates+=("/c/Program Files/Unity/Editor/Unity.exe")

    local c
    for c in "${candidates[@]}"; do
        if [[ -f "$c" ]]; then
            echo "$c"
            return 0
        fi
    done
    return 1
}

# ---------------------------------------------------------------------------
# (a) Tests EditMode de Unity.
# ---------------------------------------------------------------------------
run_unity_editmode_tests() {
    if (( SKIP_TESTS )); then
        skip "unity-editmode-tests" "saltado por --skip-tests"
        return 0
    fi

    local unity_bin
    if ! unity_bin="$(resolve_unity_path)"; then
        fail "unity-editmode-tests" "no se encontró Unity.exe (definí \$UNITY_PATH o pasá --unity-path=<ruta>)"
        return 1
    fi
    if [[ ! -f "$unity_bin" ]]; then
        fail "unity-editmode-tests" "UNITY_PATH apunta a un archivo inexistente: $unity_bin"
        return 1
    fi

    echo "-> Unity: $unity_bin"
    echo "-> Corriendo tests EditMode (log: $UNITY_TEST_LOG)..."
    rm -f "$EDITMODE_XML"

    "$unity_bin" \
        -batchmode -nographics \
        -projectPath "$PROJECT_DIR" \
        -runTests -testPlatform EditMode \
        -testResults "$EDITMODE_XML" \
        -logFile "$UNITY_TEST_LOG"
    local exit_code=$?

    if [[ ! -f "$EDITMODE_XML" ]]; then
        local hint=""
        if [[ -f "$UNITY_TEST_LOG" ]]; then
            hint=" — posibles errores de compilación: $(grep -m3 'error CS' "$UNITY_TEST_LOG" | tr '\n' ';')"
        fi
        fail "unity-editmode-tests" "no se generó $EDITMODE_XML (Unity exit=$exit_code, ver $UNITY_TEST_LOG)$hint"
        return 1
    fi

    local root_tag total passed failed
    root_tag="$(grep -m1 -o '<test-run [^>]*>' "$EDITMODE_XML" || true)"
    total="$(echo "$root_tag" | grep -o 'total="[0-9]*"' | grep -o '[0-9]*' || echo '?')"
    passed="$(echo "$root_tag" | grep -o 'passed="[0-9]*"' | grep -o '[0-9]*' || echo '?')"
    failed="$(echo "$root_tag" | grep -o 'failed="[0-9]*"' | grep -o '[0-9]*' || echo '0')"

    if [[ -z "$root_tag" ]]; then
        fail "unity-editmode-tests" "no se pudo parsear $EDITMODE_XML (formato inesperado, revisar a mano)"
        return 1
    fi

    if [[ "$failed" != "0" && -n "$failed" ]]; then
        local failnames
        failnames="$(grep -o 'name="[^"]*" fullname="[^"]*"[^>]*result="Failed"' "$EDITMODE_XML" | grep -o 'fullname="[^"]*"' | sed 's/fullname="//;s/"$//' | tr '\n' ',' )"
        fail "unity-editmode-tests" "$failed de $total tests fallaron (${failnames%,}). Detalle: $EDITMODE_XML"
        return 1
    fi

    pass "unity-editmode-tests (${passed}/${total} passed)"
    return 0
}

# ---------------------------------------------------------------------------
# (b) Build de tablet / visor (opcional, --build).
# ---------------------------------------------------------------------------
run_build_tablet() {
    local unity_bin
    if ! unity_bin="$(resolve_unity_path)"; then
        fail "build-tablet" "no se encontró Unity.exe (definí \$UNITY_PATH o pasá --unity-path=<ruta>)"
        return 1
    fi

    echo "-> Buildeando tablet (headless, TabletBuild.BuildTabletMenu; log: $UNITY_BUILD_LOG)..."
    rm -f "$TABLET_APK"

    "$unity_bin" \
        -batchmode -nographics -quit \
        -projectPath "$PROJECT_DIR" \
        -buildTarget Android \
        -executeMethod Simulador.EditorTools.TabletBuild.BuildTabletMenu \
        -logFile "$UNITY_BUILD_LOG"
    local exit_code=$?

    if grep -q '\[TabletBuild\] Succeeded' "$UNITY_BUILD_LOG" 2>/dev/null && [[ -f "$TABLET_APK" ]]; then
        local size_mb
        size_mb=$(( $(stat -c%s "$TABLET_APK") / 1024 / 1024 ))
        pass "build-tablet (APK: $TABLET_APK, ${size_mb} MB)"
        return 0
    fi

    local hint=""
    if grep -q '\[TabletBuild\] El build target activo' "$UNITY_BUILD_LOG" 2>/dev/null; then
        hint=" — build target activo no es Android (pasar -buildTarget Android ya está incluido; revisar log)"
    elif grep -q 'error CS' "$UNITY_BUILD_LOG" 2>/dev/null; then
        hint=" — errores de compilación en el log"
    fi
    fail "build-tablet" "Unity exit=$exit_code, no se encontró APK o build falló (ver $UNITY_BUILD_LOG)$hint"
    return 1
}

run_build_visor() {
    # Gotcha: a diferencia de la tablet, no existe un método de Editor dedicado e
    # invocable headless para el build del visor (el build "normal" se hace desde el
    # menú Build Settings / MCP unity_build, no vía -executeMethod). Agregar uno es
    # tarea de @unity-dev, no de este script. Documentado como deuda en
    # docs/builds-deploy.md § CI local.
    skip "build-visor" "no soportado en headless todavía (no hay -executeMethod dedicado; usar unity_build vía MCP o el Editor)"
}

run_build_stage() {
    if [[ -z "$DO_BUILD" ]]; then
        skip "build" "no solicitado (pasar --build[=tablet|visor|both])"
        return 0
    fi

    if [[ "$DO_BUILD" == "tablet" || "$DO_BUILD" == "both" ]]; then
        run_build_tablet
    fi
    if [[ "$DO_BUILD" == "visor" || "$DO_BUILD" == "both" ]]; then
        run_build_visor
    fi
}

# ---------------------------------------------------------------------------
# (c) pytest del backend, en un venv temporal.
# ---------------------------------------------------------------------------
detect_python() {
    local c
    for c in python python3; do
        if command -v "$c" >/dev/null 2>&1; then
            local out
            out="$("$c" --version 2>&1)"
            if [[ "$out" == Python\ 3* ]]; then
                echo "$c"
                return 0
            fi
        fi
    done
    if command -v py >/dev/null 2>&1; then
        local out
        out="$(py -3 --version 2>&1)"
        if [[ "$out" == Python\ 3* ]]; then
            echo "py -3"
            return 0
        fi
    fi
    return 1
}

venv_python_bin() {
    local venv_dir="$1"
    if [[ -f "$venv_dir/Scripts/python.exe" ]]; then
        echo "$venv_dir/Scripts/python.exe"
    elif [[ -f "$venv_dir/bin/python" ]]; then
        echo "$venv_dir/bin/python"
    else
        return 1
    fi
}

run_backend_tests() {
    if (( SKIP_BACKEND )); then
        skip "backend-pytest" "saltado por --skip-backend"
        return 0
    fi

    local pybin
    if ! pybin="$(detect_python)"; then
        skip "backend-pytest" "no se encontró un Python 3 utilizable (instalalo o usá --skip-backend)"
        return 0
    fi
    echo "-> Python: $pybin"

    local venv_dir
    venv_dir="$(mktemp -d "$ARTIFACTS_DIR/venv-XXXXXX" 2>/dev/null)"
    if [[ -z "$venv_dir" ]]; then
        fail "backend-pytest" "no se pudo crear el venv temporal en $ARTIFACTS_DIR"
        return 1
    fi
    # venv temporal: se borra siempre al salir de esta función (éxito o error).
    trap 'rm -rf "$venv_dir"' RETURN

    echo "-> Creando venv temporal en $venv_dir..."
    if ! $pybin -m venv "$venv_dir" >"$BACKEND_LOG" 2>&1; then
        fail "backend-pytest" "no se pudo crear el venv (ver $BACKEND_LOG)"
        return 1
    fi

    local venv_py
    if ! venv_py="$(venv_python_bin "$venv_dir")"; then
        fail "backend-pytest" "el venv se creó pero no se encontró su python (revisar $venv_dir)"
        return 1
    fi

    echo "-> Instalando requirements-dev.txt..."
    if ! "$venv_py" -m pip install -q --disable-pip-version-check -r "$PROJECT_DIR/backend/api/requirements-dev.txt" >>"$BACKEND_LOG" 2>&1; then
        fail "backend-pytest" "falló pip install (ver $BACKEND_LOG)"
        return 1
    fi

    echo "-> Corriendo pytest (backend/api)..."
    (
        cd "$PROJECT_DIR/backend/api" || exit 1
        "$venv_py" -m pytest -q
    ) >>"$BACKEND_LOG" 2>&1
    local exit_code=$?

    local summary
    summary="$(grep -E '^[0-9]+ (passed|failed)' "$BACKEND_LOG" | tail -1)"

    if [[ $exit_code -ne 0 ]]; then
        fail "backend-pytest" "pytest exit=$exit_code ${summary:+(${summary})} — ver $BACKEND_LOG"
        return 1
    fi

    pass "backend-pytest ${summary:+(${summary})}"
    return 0
}

# ---------------------------------------------------------------------------
# Main
# ---------------------------------------------------------------------------
echo "== CI local — Simulador LIOs =="
echo "Proyecto: $PROJECT_DIR"
echo ""

NEEDS_UNITY=0
if (( ! SKIP_TESTS )); then NEEDS_UNITY=1; fi
if [[ -n "$DO_BUILD" ]]; then NEEDS_UNITY=1; fi

if (( NEEDS_UNITY )); then
    if ! check_unity_not_locked; then
        echo "== CI local abortada: cerrá el Editor de Unity y volvé a intentar. =="
        exit 1
    fi
fi

echo "--- (a) Tests EditMode ---"
run_unity_editmode_tests
echo ""

echo "--- (b) Build ---"
run_build_stage
echo ""

echo "--- (c) Backend (pytest) ---"
run_backend_tests
echo ""

echo "== Resumen =="
for i in "${!STAGE_NAMES[@]}"; do
    case "${STAGE_STATUSES[$i]}" in
        PASS) echo "  ✔ ${STAGE_NAMES[$i]}" ;;
        FAIL) echo "  ✖ ${STAGE_NAMES[$i]}" ;;
        SKIP) echo "  ⚠ ${STAGE_NAMES[$i]} (skip)" ;;
    esac
done

if (( OVERALL_FAIL )); then
    echo ""
    echo "== CI local: FALLÓ =="
    exit 1
fi

echo ""
echo "== CI local: OK =="
exit 0
