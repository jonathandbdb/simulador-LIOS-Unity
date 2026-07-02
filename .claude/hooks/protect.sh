#!/bin/bash
# Hook PreToolUse (bloqueante) del enjambre Simulador LIOs.
# Doble proposito segun la tool:
#   - Write/Edit/MultiEdit: bloquea escrituras en generados de Unity, .meta a
#     mano y settings.local.json del usuario.
#   - Bash: bloquea `git push ... origin` y alteraciones del remote `lios`.
# Contrato de Claude Code: exit 2 => bloquear; stderr se devuelve a Claude.

input=$(cat)

DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
# shellcheck source=lib.sh
. "$DIR/lib.sh"

tool_name=$(sim_json_field "$input" "tool_name")

# --- Rama Bash: guard de git ---
if [ "$tool_name" = "Bash" ]; then
    # Trabajamos sobre el JSON crudo: alcanza para detectar los patrones prohibidos.
    if printf '%s' "$input" | grep -Eq 'git[[:space:]]+push[^;|&"]*[[:space:]]origin([[:space:]]|$|\\")'; then
        echo "BLOQUEADO por protect.sh: push a 'origin' esta PROHIBIDO en este repo. El unico remote de push permitido es 'lios' (ver skill git-lios). Usa: git push lios <rama>." >&2
        exit 2
    fi
    if printf '%s' "$input" | grep -Eq 'git[[:space:]]+remote[[:space:]]+(remove|rename|set-url)[^;|&"]*lios'; then
        echo "BLOQUEADO por protect.sh: no se permite alterar el remote 'lios' (remove/rename/set-url). Es el unico destino de push del proyecto (ver skill git-lios)." >&2
        exit 2
    fi
    exit 0
fi

# --- Rama Write/Edit: proteccion de archivos ---
file_path=$(sim_json_field "$input" "file_path")
[ -z "$file_path" ] && exit 0
p=$(sim_norm_path "$file_path")

case "$p" in
    */Library/*|Library/*|*/Temp/*|Temp/*|*/Logs/*|Logs/*|*/obj/*|obj/*|*/UserSettings/*|UserSettings/*)
        echo "BLOQUEADO por protect.sh: '$file_path' es un archivo generado por Unity (Library/Temp/Logs/obj/UserSettings). No se edita a mano jamas (ver AGENTS.md)." >&2
        exit 2 ;;
    *.csproj|*.slnx)
        echo "BLOQUEADO por protect.sh: '$file_path' es un archivo de proyecto generado por Unity (*.csproj / *.slnx). No se edita a mano (ver AGENTS.md)." >&2
        exit 2 ;;
    *.meta)
        echo "BLOQUEADO por protect.sh: los .meta los genera el Editor de Unity, no se crean/editan a mano. Crea el asset via MCP o forza un refresh (unity_execute_code -> AssetDatabase.Refresh()) para que el Editor genere el .meta (ver AGENTS.md)." >&2
        exit 2 ;;
    */.claude/settings.local.json)
        echo "BLOQUEADO por protect.sh: '.claude/settings.local.json' es configuracion local del usuario; el enjambre no la toca. Los cambios del enjambre van en '.claude/settings.json'." >&2
        exit 2 ;;
esac

exit 0
