---
name: unity-mcp-workflow
description: Operativa canónica del Editor de Unity vía MCP (unity_*) - compile-gate, receta de tests EditMode, etiqueta de play mode, capturas, escenas, Editor caído. Cargar al operar el Editor desde cualquier agente.
---

# Unity vía MCP — operativa canónica

Todo lo del Editor va por tools `unity_*`. NUNCA llamar el bridge HTTP (`http://127.0.0.1:7890`)
directo: saltea la cola de agentes y los mecanismos de seguridad.

## Instancias

En la primera tool call se auto-descubren instancias. Si hay **más de una**, preguntá al usuario
cuál usar y llamá `unity_select_instance` antes de seguir. `unity_list_instances` para verlas.

## Compile-gate (la verdad de compilación)

No hay compile CLI en este flujo: `unity_get_compilation_errors` es la única verdad.

1. Tras editar `.cs` por filesystem, el Editor recompila al recuperar foco / refresh. Llamá
   `unity_get_compilation_errors`; si la respuesta parece vieja (no refleja tu cambio), esperá
   unos segundos y repetí — la recompilación de este proyecto tarda poco pero no es instantánea.
2. Con errores: corregí e iterá (máx 3 ciclos en agentes dev; después `PARTIAL`).
3. **Shaders**: sus errores NO salen por compilation_errors — revisá `unity_console_log`
   (filtrá por el nombre del shader).

## Tests EditMode — receta canónica

Suite: `Assets/Tests/EditMode/DataLogicTests.cs` (asmdef `Simulador.Tests.EditMode`, NUnit).

1. `unity_console_clear`.
2. `unity_execute_code` con un snippet que dispare el Test Runner y loguee resultados, p.ej.:

```csharp
var api = ScriptableObject.CreateInstance<UnityEditor.TestTools.TestRunner.Api.TestRunnerApi>();
var filter = new UnityEditor.TestTools.TestRunner.Api.Filter {
    testMode = UnityEditor.TestTools.TestRunner.Api.TestMode.EditMode
};
api.RegisterCallbacks(new ResultLogger()); // callback que hace Debug.Log de cada resultado
api.Execute(new UnityEditor.TestTools.TestRunner.Api.ExecutionSettings(filter));
```

   (Si `unity_execute_code` no admite definir el callback, alternativa mínima: loguear en
   `RunFinished` el resumen `passed/failed` y por cada `TestFinished` fallado su nombre+mensaje.)
3. Leer resultados por `unity_console_log`. Reportar N pasados / N fallados con mensajes.

## Etiqueta de play mode

- `unity_console_clear` → entrar (`unity_play_mode`) → observar (`unity_console_log`,
  `unity_screenshot_game`) → **SALIR SIEMPRE**. Jamás dejar el Editor reproduciendo al terminar.
- No entrar a play mode con compilación rota.

## Escenas

- Tras CUALQUIER mutación de escena: `unity_scene_save`. Escena sin guardar = trabajo perdido.
- Verificar con `unity_search_missing_references` que no quedaron referencias rotas.
- No operar escenas con play mode activo.

## Capturas (evidencia visual)

- `unity_graphics_scene_capture` — vista de Scene (estado del mundo, sin necesidad de play).
- `unity_screenshot_game` / `unity_graphics_game_capture` — vista de Game (lo que ve el
  jugador; para post-proceso de visión, capturar en play mode dentro del escenario correcto).
- Convención: captura ANTES y DESPUÉS del cambio, mismo encuadre/escenario, referenciadas en el
  retorno del agente.

## Editor caído / que no responde

1. `unity_editor_ping`. Si no responde: NO reintentar la operación a ciegas.
2. Reportar al orquestador (`FAILED`) pidiendo que el usuario verifique: Editor abierto, sin
   diálogos modales, sin import largo en curso.
3. El orquestador no quema reintentos del fallback contra un Editor caído.

## Assets y .meta

- Crear assets preferentemente vía MCP (el Editor genera el `.meta`).
- Si se creó un archivo por filesystem bajo `Assets/`, forzar refresh (p.ej.
  `unity_execute_code` → `AssetDatabase.Refresh()`) para que el Editor genere el `.meta` antes
  de commitear.
