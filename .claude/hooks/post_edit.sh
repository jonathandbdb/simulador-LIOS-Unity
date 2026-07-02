#!/bin/bash
# Hook PostToolUse (NO bloqueante) del enjambre Simulador LIOs.
# Tras cada Write/Edit inyecta recordatorios al agente via additionalContext:
#   1. compile-gate para .cs (y consola para shaders)
#   2. .meta faltante (asset nuevo) o .meta huerfano
#   3. doc viva del sistema tocado (regla anti-drift)
# Contrato: exit 0 siempre; salida JSON con hookSpecificOutput.additionalContext.

input=$(cat)

DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
# shellcheck source=lib.sh
. "$DIR/lib.sh"

file_path=$(sim_json_field "$input" "file_path")
[ -z "$file_path" ] && exit 0
p=$(sim_norm_path "$file_path")

notes=""
add_note() {
    if [ -n "$notes" ]; then notes="$notes
$1"; else notes="$1"; fi
}

# 1) Compile-gate / consola de shaders
case "$p" in
    *.cs)
        add_note "COMPILE-GATE: editaste un .cs — verifica 'unity_get_compilation_errors' antes de reportar OK (regla del enjambre: sin evidencia de compilacion limpia, el resultado se trata como PARTIAL)." ;;
    *.shader|*.hlsl|*.cginc)
        add_note "SHADER: los errores de shader NO salen por unity_get_compilation_errors — revisa 'unity_console_log' (filtrando por el nombre del shader) antes de reportar OK." ;;
esac

# 2) .meta pareado (solo assets bajo Assets/)
case "$p" in
    *Assets/*)
        if [ -f "$file_path" ] && [ ! -f "${file_path}.meta" ]; then
            add_note "META FALTANTE: '$file_path' no tiene su .meta par. No lo crees a mano: deja que el Editor lo genere (refresh via MCP, p.ej. unity_execute_code -> AssetDatabase.Refresh()) y commitealo JUNTO al asset."
        fi ;;
esac

# 3) Doc viva del sistema (anti-drift)
doc=$(sim_doc_for_path "$p")
if [ -n "$doc" ]; then
    add_note "DOC VIVA de este sistema: '$doc'. Si tu cambio altera arquitectura, comportamiento o gotchas, actualizala EN SITIO antes de cerrar la tarea (regla anti-drift, no opcional — una doc que miente envenena el contexto del proximo agente)."
fi

# Sin nada que inyectar: salida silenciosa.
[ -z "$notes" ] && exit 0

escaped=$(sim_json_escape "$notes")
printf '{"hookSpecificOutput":{"hookEventName":"PostToolUse","additionalContext":"%s"}}\n' "$escaped"
exit 0
