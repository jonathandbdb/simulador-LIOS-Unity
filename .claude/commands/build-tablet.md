---
description: Build de la tablet Android (OpenXR OFF via TabletBuild). Con "instalar" lo instala por adb en la tablet conectada.
---

Invocá al subagente @build-deploy (tool Task, `subagent_type: build-deploy`) con el perfil
**tablet**: **SOLO** vía `Simulador → Build Tablet (Android)` (`unity_execute_menu_item`) o
`Simulador.EditorTools.TabletBuild.BuildTablet()` (`unity_execute_code`) — NUNCA `unity_build`
directo (el loader OpenXR activo en una tablet sin VR da pantalla negra; TabletBuild lo
desactiva y restaura).

Inyectale la skill `build-pipeline` como Project Standard en el handoff.

- Si `$ARGUMENTS` incluye "instalar": `adb install -r` + launch + smoke por logcat.
  **Ojo**: visor y tablet comparten package — confirmá con el usuario el dispositivo destino
  antes de instalar (instalar tablet en el Quest pisa el visor).
- El agente debe verificar post-build que el loader OpenXR quedó restaurado.

Al retornar, reportá: ruta/tamaño del APK, loader restaurado, resultado del smoke (si aplica).
