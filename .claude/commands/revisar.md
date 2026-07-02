---
description: Revisión de código con el reviewer del enjambre (default - diff sin commitear; opcionalmente un scope)
---

Invocá al subagente @reviewer (tool Task, `subagent_type: reviewer`) con el scope:

- Si `$ARGUMENTS` está vacío: el diff sin commitear (`git diff` + `git status` + untracked
  relevantes).
- Si no: el scope indicado en `$ARGUMENTS` (sistema, carpeta, archivos, o rango de commits).

En el handoff:
- Nombrá la(s) doc(s) viva(s) de los sistemas tocados (tabla en `AGENTS.md`).
- Si el scope toca `Assets/Scripts/Runtime/Vision/`, `Assets/Shaders/`, escenas o UI: indicale
  que la evidencia visual es parte del review — si no hay capturas antes/después en el contexto,
  que tome las que pueda y marque la ausencia de "antes" como hallazgo MAYOR.

Al retornar, presentá los hallazgos por severidad (CRÍTICO/MAYOR/MENOR) y el veredicto. Con
CRÍTICOS, no des nada por cerrado: proponé reinvocar al dev correspondiente con la lista.
