---
description: Orientación rápida read-only - estado git, Editor, docs vivas y pendientes
---

Orientate SIN escribir nada y SIN invocar subagentes (es barato, hacelo vos). Lanzá en paralelo
(un solo mensaje, varias tool calls):

1. `git status` + `git log --oneline -5` + rama actual.
2. `unity_editor_state` (¿Editor abierto? ¿play mode? ¿escena activa?).
3. `unity_get_compilation_errors` (¿compila?).
4. Leé `docs/README.md` (índice) y la sección **Pendientes / deuda** de cada doc viva en
   `docs/` (son cortas — podés leer las 6).

Presentá al usuario un resumen compacto:
- Rama + últimos commits + cambios sin commitear.
- Editor: estado, escena, compilación.
- Pendientes agregados de las docs vivas, agrupados por sistema (los accionables primero).

No propongas trabajo: es un comando de orientación.
