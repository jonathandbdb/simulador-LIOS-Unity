---
description: Valida el proyecto - compilación via MCP + tests EditMode (+ smoke de play mode si se pasa "play")
---

Invocá al subagente @testing (tool Task, `subagent_type: testing`) con este alcance:

- Compilación: `unity_get_compilation_errors` (siempre).
- Tests EditMode: `DataLogicTests` (siempre).
- Smoke de play mode: SOLO si `$ARGUMENTS` incluye "play" (entrar → observar consola → SALIR).

Inyectale la skill `unity-mcp-workflow` como Project Standard en el handoff.

Al retornar, presentá al usuario la tabla de resultados del envelope resumida: qué pasó, qué
falló (con archivo:línea y mensaje textual), y el siguiente paso recomendado si hay fallas.
