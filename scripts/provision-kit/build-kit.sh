#!/usr/bin/env bash
# Arma el kit de Windows para configurar tablets sin adb ni Git Bash -- lo
# corre el OPERADOR (esta PC, Linux/Git Bash), no el medico. El resultado es
# un .zip que se le manda al medico: adentro hay un doble-clic
# ("Configurar tablet.bat") que deja la tablet lista como Device Owner
# (kiosco), sin depender del QR de Android Enterprise (ver
# docs/builds-deploy.md "Kit para clinicas (Windows)" -- bloqueado hoy en
# Android 16 con GMS por el verificador de desarrolladores de Google).
#
# Empaqueta:
#   - scripts/provision-kit/windows/Configurar tablet.bat
#   - scripts/provision-kit/windows/configurar.ps1
#   - scripts/provision-kit/Guia - Configurar tablet.pdf   (si existe -- la
#     arma otro proceso/agente; si falta, este script AVISA y sigue igual,
#     el kit queda armado sin la guia)
#
# Salida: Builds/IOLSIMULATOR-Configurar-Tablet.zip (carpeta gitignorada,
# ver .gitignore "# Builds" -- mismo directorio que usan los builds del
# visor/tablet, ver docs/builds-deploy.md).
#
# Uso: scripts/provision-kit/build-kit.sh

set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd "$SCRIPT_DIR/../.." && pwd)"

WINDOWS_DIR="$SCRIPT_DIR/windows"
BAT_FILE="$WINDOWS_DIR/Configurar tablet.bat"
PS1_FILE="$WINDOWS_DIR/configurar.ps1"
PDF_FILE="$SCRIPT_DIR/Guia - Configurar tablet.pdf"

OUT_DIR="$REPO_ROOT/Builds"
OUT_ZIP="$OUT_DIR/IOLSIMULATOR-Configurar-Tablet.zip"
STAGE_NAME="Configurar tablet"

step() { echo "==> $*"; }
fail() { echo "ERROR: $*" >&2; exit 1; }

[[ -f "$BAT_FILE" ]] || fail "no se encontró '$BAT_FILE'"
[[ -f "$PS1_FILE" ]] || fail "no se encontró '$PS1_FILE'"

if [[ -f "$PDF_FILE" ]]; then
    step "Guía encontrada: $PDF_FILE"
else
    echo "AVISO: no se encontró '$PDF_FILE' -- el kit se arma SIN la guía impresa." >&2
    echo "       La guía la genera otro proceso/agente; agregala en scripts/provision-kit/" >&2
    echo "       con ese nombre exacto y volvé a correr este script para incluirla." >&2
fi

mkdir -p "$OUT_DIR"
rm -f "$OUT_ZIP"

STAGE_DIR="$(mktemp -d)"
trap 'rm -rf "$STAGE_DIR"' EXIT

mkdir -p "$STAGE_DIR/$STAGE_NAME"
cp "$BAT_FILE" "$STAGE_DIR/$STAGE_NAME/"
cp "$PS1_FILE" "$STAGE_DIR/$STAGE_NAME/"
if [[ -f "$PDF_FILE" ]]; then
    cp "$PDF_FILE" "$STAGE_DIR/$STAGE_NAME/"
fi

step "Empaquetando $OUT_ZIP..."
(
    cd "$STAGE_DIR"
    if command -v zip >/dev/null 2>&1; then
        zip -r -q "$OUT_ZIP" "$STAGE_NAME"
    elif command -v python3 >/dev/null 2>&1; then
        python3 - "$STAGE_NAME" "$OUT_ZIP" <<'PYEOF'
import sys, zipfile, os

stage, out = sys.argv[1], sys.argv[2]
with zipfile.ZipFile(out, "w", zipfile.ZIP_DEFLATED) as zf:
    for root, _dirs, files in os.walk(stage):
        for name in files:
            full = os.path.join(root, name)
            zf.write(full, full)
PYEOF
    else
        fail "no se encontró 'zip' ni 'python3' en esta PC -- instalá alguno de los dos para armar el .zip."
    fi
)

step "Listo: $OUT_ZIP"

if command -v python3 >/dev/null 2>&1; then
    echo "Contenido:"
    python3 - "$OUT_ZIP" <<'PYEOF'
import sys, zipfile
with zipfile.ZipFile(sys.argv[1]) as zf:
    for info in zf.infolist():
        print(f"  {info.filename}  ({info.file_size} bytes)")
PYEOF
elif command -v unzip >/dev/null 2>&1; then
    unzip -l "$OUT_ZIP"
fi
