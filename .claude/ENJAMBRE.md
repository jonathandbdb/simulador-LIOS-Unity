# ENJAMBRE.md — Arquitectura del enjambre Simulador LIOs

Explicación para humanos de cómo está armado el sistema multi-agente de este repo. El
comportamiento operativo del orquestador vive en `CLAUDE.md`; las convenciones de código en
`AGENTS.md`; el conocimiento por sistema en `docs/`. Este documento explica el **porqué** del
diseño. (Material apto para el anexo de metodología del TFM.)

## 1. El patrón: orquestador + especialistas

La sesión principal de Claude Code actúa como **orquestador**: es el único que habla con el
usuario y no escribe código — coordina subagentes especializados vía la tool Task. Los
subagentes **nunca se hablan entre sí**: toda comunicación pasa por el orquestador con un
**handoff estandarizado** (input) y un **Result Envelope** (output con `Status:` explícito
OK/BLOCKED/NEEDS_INPUT/PARTIAL/FAILED). El `Status` parseable hace fiables las decisiones del
orquestador (frenar, repreguntar, reintentar) sin interpretar prosa libre.

## 2. Roster y tiering

| Agente | Modelo | Rol | ¿Por qué ese modelo? |
|--------|--------|-----|----------------------|
| @unity-researcher | sonnet | Explora código + estado del Editor (read-only, paralelizable) | Recolección guiada con evidencia; no requiere juicio |
| @unity-dev | sonnet | Implementa C# (Data/Net/Tablet/Editor) | Ejecución guiada por doc viva + skill, verificada por compile-gate |
| @vision-optics | **opus** | Óptica clínica + shaders + RenderGraph (dueño de Vision/ y Shaders/) | El error aquí es **sutil y plausible**: un velo mal calculado "se ve bien" pero miente clínicamente. Juicio, no ejecución |
| @scene-editor | sonnet | Opera escenas vía MCP (interno) | Lista de operaciones determinista |
| @backend-dev | sonnet | FastAPI/Docker | Verificación real con docker+curl |
| @build-deploy | sonnet | Builds visor/tablet + adb + deploy | Procedimiento con fuente única (skill build-pipeline) |
| @testing | sonnet | Compilación + EditMode + smoke (no corrige) | Procedimiento determinista |
| @reviewer | **opus** | Review transversal con evidencia visual | Juicio sobre correctitud, plataforma y drift |
| @git-flow | sonnet | Git al remote `lios` (interno, solo a pedido) | Convención mecánica (skill git-lios) |

Regla del tiering: **opus donde el error es plausible y difícil de detectar; sonnet donde hay
verificación mecánica real** (compilador, curl, adb, capturas) que abarata el modelo.
@scene-editor y @git-flow son **procedimientos internos**: el orquestador no los expone al
usuario; los invoca como subrutinas.

## 3. Las tres fuentes de verdad (sin duplicación)

| Capa | Fuente única | Contenido |
|------|--------------|-----------|
| Convenciones + entorno | `AGENTS.md` | Idioma, asmdefs, estilo, IL2CPP, .meta, entorno (dev único — no hay `workspace.md`) |
| Conocimiento por sistema | `docs/` (docs vivas) | Arquitectura, decisiones, gotchas, cómo probar — por sistema |
| Comportamiento del enjambre | `CLAUDE.md` | Orquestación, flujos, envelope, contratos, fallback |

Agentes, skills, comandos y hooks **referencian** estas capas, no las copian. Las skills
(`.claude/skills/`) guardan el know-how estable transversal: `unity-mcp-workflow`,
`urp-rendergraph-patterns`, `il2cpp-networking-gotchas`, `minimal-footprint`, `git-lios`,
`build-pipeline`.

## 4. Docs vivas en lugar de SDD

El enjambre original usa specs formales con estados y versionado. Acá se eligió deliberadamente
algo más liviano: **una doc viva por sistema** (`docs/`) con la regla
**leer-primero / actualizar-al-cerrar** (protocolo en `docs/README.md`). Mismo efecto
anti-drift (la doc alimenta el contexto, el código alimenta la doc) sin la burocracia de
estados y version-sync, adecuado para un dev único. El hook `post_edit.sh` recuerda la
actualización tras cada edición.

## 5. Verificación real (la mejora central sobre el diseño original)

El enjambre original validaba con regex (lint por patrones). Acá hay **ground truth mecánico**:

- **Compile-gate**: `unity_get_compilation_errors` vía MCP es LA verdad de compilación; ningún
  cambio de `.cs` se acepta sin ese chequeo limpio (los shaders se verifican por consola).
- **Evidencia visual**: los cambios perceptuales (visión, glare, UI) exigen capturas MCP
  antes/después en el escenario correcto; @reviewer las compara.
- **Backend**: docker compose + curl como equivalente del compile-gate.
- El researcher además "ve" el Editor (jerarquías, componentes) — no solo archivos.

Por eso los hooks son **livianos**: `protect.sh` (PreToolUse, bloqueante) solo protege lo
irreversible (generados de Unity, `.meta` a mano, push a `origin`, alterar el remote `lios`);
`post_edit.sh` (PostToolUse) solo **recuerda** (compile-gate, .meta, doc viva) vía
`additionalContext`; `session_orient.sh` da el banner de orientación. La verificación fuerte
la hace el contrato de los agentes, no el hook.

## 6. Reglas operativas clave

- **Un solo agente MCP-activo a la vez**: el Editor es un recurso único con cola. Read-only
  independientes sí se paralelizan; escritores y usuarios del Editor, jamás.
- **Fallback consciente del Editor**: ante timeout MCP, `unity_editor_ping` ANTES de reintentar
  (no quemar intentos contra un Editor caído). Máx 2 intentos por agente por tarea.
- **Git de doble llave**: solo a pedido explícito del usuario, solo al remote `lios` (`origin`
  prohibido — regla + hook). Convención en la skill `git-lios`.
- **Fronteras de propiedad**: `Vision/`+`Shaders/` son de @vision-optics; @unity-dev devuelve
  `BLOCKED` si la tarea deriva ahí. El contrato `lentes.json` es compartido Unity↔backend:
  cambiarlo unilateralmente también es `BLOCKED`.
- **Minimal footprint** transversal: escalera anti-over-engineering con NO-negociables
  blindados (compile-gate, .meta, doc viva, tests de lógica pura, invariantes IL2CPP/XR).

## 7. Mapa de archivos del enjambre

```
CLAUDE.md            orquestador (importa @AGENTS.md)
AGENTS.md            convenciones + entorno
docs/                docs vivas (7)
.claude/
├── settings.json    hooks (coexiste con settings.local.json del usuario, que no se toca)
├── ENJAMBRE.md      este documento
├── agents/          9 agentes
├── skills/          6 skills
├── commands/        /compilar-y-probar /build-visor /build-tablet /revisar /contexto /salud
└── hooks/           lib.sh protect.sh post_edit.sh session_orient.sh
```

## 8. Mantenimiento

- Cambiar el modelo de un agente: en su frontmatter (fuente de verdad) y reflejar en la tabla
  de `CLAUDE.md` y acá.
- Agregar un sistema nuevo: crear su doc viva, sumarla a la tabla de `AGENTS.md` y al mapeo
  `sim_doc_for_path()` de `.claude/hooks/lib.sh`.
- Los hallazgos de drift que las docs vivas registran en "Pendientes / deuda" son la cola de
  trabajo natural del enjambre (`/contexto` los agrega).
