# AGENTS.md — Simulador LIOs

Este archivo describe convenciones y reglas que **TODO agente de IA** (Claude Code, Cursor, Codex,
etc.) debe respetar al trabajar en este repositorio.

> 🤖 **Arquitectura del enjambre** (orquestador, subagentes, flujos, envelope, fallback) **no se
> documenta acá**: vive en `CLAUDE.md` (comportamiento del orquestador) y `.claude/ENJAMBRE.md`
> (arquitectura para humanos). Este documento es **solo convenciones de código y entorno**.

> 📚 **Conocimiento por sistema** (arquitectura de visión, networking, tablet, catálogo, builds,
> backend) tampoco vive acá: está en las **docs vivas** de `docs/` — ver §Docs vivas al final.

## 🖥️ Entorno

Desarrollador único, una sola máquina. No hay `workspace.md` por dev: el entorno se describe acá,
una sola vez.

- **SO**: Windows 11 · shell de trabajo **Git Bash** (los hooks corren con `bash`).
- **Unity**: 6000.5.1f1 (Unity 6), URP 17.5. El Editor debe estar **abierto** para que funcionen
  las tools MCP `unity_*` (plugin `com.anklebreaker.unity-mcp`, server en `.mcp.json`).
- **No hay compile CLI standalone**: la única verificación de compilación es
  `unity_get_compilation_errors` vía MCP.
- **Backend**: Docker Desktop (`backend/docker-compose.yml`).
- **Dispositivos**: Meta Quest (visor) y tablet Android, ambos por `adb`.
- **Git**: remote de trabajo **`lios`** (único destino permitido de push). `origin` existe pero
  está **PROHIBIDO** — detalle en la skill `git-lios`.

## 🌐 Idioma

- Código (clases, métodos, variables, archivos): **inglés**.
- Comentarios y docstrings/`<summary>`: **español**.
- Textos de UI de la app (visor/tablet): **español**.
- Documentación (`docs/`, READMEs): **español**.

## 🧱 Estructura y asmdefs

Tres assembly definitions — respetar sus fronteras:

| Asmdef | Carpeta | Regla |
|--------|---------|-------|
| `Simulador.Runtime` | `Assets/Scripts/Runtime/` | **Sin referencias a UnityEditor.** `#if UNITY_EDITOR` solo con justificación explícita en comentario. |
| `Simulador.Editor` | `Assets/Scripts/Editor/` | Tooling de Editor (builds, reparación de prefabs). |
| `Simulador.Tests.EditMode` | `Assets/Tests/EditMode/` | Tests NUnit de lógica pura. |

- Namespaces `Simulador.*` espejando la carpeta (`Simulador.Vision`, `Simulador.Net`, …) — seguir
  el patrón de los archivos existentes.
- Un archivo por clase MonoBehaviour, con el **mismo nombre que la clase** (Unity lo exige para
  serialización — un rename desincronizado produce "Missing Script").

## 🐍 Estilo C#

- Seguir el estilo de los archivos existentes: `<summary>` en español al tope de la clase; si el
  script es un port de Godot, el summary **cita el `.gd` de origen** — preservar y mantener esa
  cita al editar.
- Campos serializados: `[SerializeField] private tipo _nombre;` (o `campo` sin guion según el
  archivo — respetar el estilo local, diff mínimo).
- **Input**: solo Input System (`Assets/InputSystem_Actions.inputactions` /
  `SimuladorInput.cs`). **Nunca** `UnityEngine.Input` legacy.
- Nada de `Resources.Load` nuevo sin justificación (las fuentes de la tablet ya lo usan — está
  bien; no extender el patrón alegremente).
- Logs con prefijo del sistema (`[Vision]`, `[Net]`, `[Tablet]`…) como hace el código existente.

## 🎮 Reglas de assets Unity

- **`.meta` pareados**: todo asset bajo `Assets/` va con su `.meta`. Nunca crear/editar un `.meta`
  a mano — los genera el Editor (refresh vía MCP). Al mover/borrar un asset, su `.meta` lo
  acompaña **en el mismo commit**.
- **Generados — nunca editar**: `Library/`, `Temp/`, `Logs/`, `obj/`, `UserSettings/`,
  `*.csproj`, `*.slnx`.
- **URP por tiers**: `Assets/Settings/` tiene pares PC (`PC_Renderer`/`PC_RPAsset`) y Mobile
  (`Mobile_Renderer`/`Mobile_RPAsset`). **Quest usa el tier Mobile** — cambios de render features
  van al tier correcto, no asumir un solo pipeline asset.
- **Escenas**: `Main.unity` (visor VR), `Tablet.unity` (tablet). No hay otras escenas
  (`SampleScene.unity`, resto de plantilla, fue eliminada).
- Tras mutar una escena vía MCP: `unity_scene_save` **siempre**.

## 📱 IL2CPP — no-negociables

Resumen (el detalle y el porqué viven en la skill `il2cpp-networking-gotchas`):

1. **Nunca `System.Net.WebSockets`** — no es fiable en IL2CPP/Android; el proyecto implementa
   RFC 6455 a mano sobre `System.Net.Sockets` (`Assets/Scripts/Runtime/Net/`).
2. **Threads de socket jamás tocan API de Unity** — encolar (`ConcurrentQueue`) y drenar en
   `Update()`.
3. Cuidado con reflection/generics dinámicos: el **stripping** de IL2CPP los rompe en build
   aunque funcionen en Editor.
4. Sockets con dispose/shutdown explícito en `OnDestroy`/`OnApplicationPause`.

## 🐍 Backend (`backend/`)

- Estilo del repo: FastAPI + SQLModel, routers en `api/app/routers.py`, admin Jinja2/HTMX en
  `api/app/admin/`.
- **Secretos solo en `.env`** (nunca committeados; `env.example` como plantilla).
- `defaults/lentes.json` y `Assets/StreamingAssets/lentes.json` comparten schema con
  `CatalogParser` en Unity: es un **contrato compartido** — cambiarlo exige tocar ambos lados en
  la misma tarea (ver `docs/catalogo-lentes.md`).

## ✂️ Reglas de edición

- **Diff mínimo**: nunca reformatear un archivo entero; aplicar las guías solo al código que se
  está cambiando. Mover código = commit separado del cambio funcional.
- **Minimal footprint** (no sobre-construir lo nuevo): antes de escribir lógica, reusar lo que ya
  existe (LensEngine, TabletUiKit, NetworkController, paquete instalado). Fuente única: skill
  `minimal-footprint` (incluye los NO-negociables que nunca se recortan). Atajos deliberados se
  marcan `// SIM: atajo deliberado — <razón>` (greppable).
- **Tests**: NO escribir tests salvo que se pidan — **con una excepción**: si la tarea toca
  lógica pura (`LensEngine.cs`, `CatalogParser.cs`), extender
  `Assets/Tests/EditMode/DataLogicTests.cs` **sí es parte de la tarea** (es barato y es la red de
  seguridad del motor de lentes).
- **Compile-gate**: ningún cambio de `.cs` se reporta como terminado sin
  `unity_get_compilation_errors` limpio. Errores de **shader** no salen ahí: revisar
  `unity_console_log`.

## 📚 Docs vivas (fuente de conocimiento por sistema)

| Sistema | Código | Doc viva |
|---------|--------|----------|
| Visión / óptica / shaders | `Assets/Scripts/Runtime/Vision/`, `Assets/Shaders/` | `docs/vision-optica.md` |
| Networking visor↔tablet | `Assets/Scripts/Runtime/Net/` | `docs/networking.md` |
| App tablet | `Assets/Scripts/Runtime/Tablet/`, `TabletController.cs` | `docs/tablet.md` |
| Catálogo / motor de lentes | `Assets/Scripts/Runtime/Data/`, `StreamingAssets/lentes.json` | `docs/catalogo-lentes.md` |
| Builds y deploy | `Assets/Scripts/Editor/TabletBuild.cs`, adb, docker | `docs/builds-deploy.md` |
| Backend | `backend/` | `docs/backend.md` |

**Regla (loop anti-drift)**: la doc viva se **lee ANTES de grepear el código** (es el resumen
curado) y se **actualiza EN SITIO en la misma tarea** si el cambio altera arquitectura,
comportamiento o gotchas. Una doc que miente envenena el contexto del próximo agente. Protocolo
completo en `docs/README.md`.

## 🚫 Restricciones

- No introducir dependencias/paquetes nuevos sin justificación explícita.
- No tocar `Assets/TutorialInfo/` (resto de plantilla).
- No hardcodear IPs/URLs nuevas (la del backend en `DataManager.cs` es deuda conocida — ver
  `docs/catalogo-lentes.md`).
- Git: **nunca** commit/push por iniciativa propia; solo a pedido y solo al remote `lios`.
