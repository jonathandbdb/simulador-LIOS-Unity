#!/bin/bash
# Hook SessionStart del enjambre Simulador LIOs: banner corto de orientacion.
# Salida por stdout => se agrega como contexto de la sesion. Nunca bloquea.

cat <<'EOF'
[enjambre Simulador LIOs]
- Proyecto: simulador VR de lentes intraoculares (Quest) + tablet Android + backend FastAPI.
- Editor Unity ABIERTO requerido para las tools MCP (unity_*). Compile-gate: unity_get_compilation_errors tras editar .cs.
- Docs vivas en docs/ — leer la del sistema ANTES de tocar codigo; actualizarla al cerrar.
- Git: commit/push SOLO a pedido del usuario y SOLO al remote 'lios' (origin PROHIBIDO).
- Orquestacion, agentes y flujos: CLAUDE.md. Convenciones: AGENTS.md.
EOF
exit 0
