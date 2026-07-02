---
description: Build del visor Quest (OpenXR ON). Con "instalar" lo instala por adb en el Quest conectado.
---

Invocá al subagente @build-deploy (tool Task, `subagent_type: build-deploy`) con el perfil
**visor**: escena `Main.unity`, loader OpenXR activo, build Android vía `unity_build`.

Inyectale la skill `build-pipeline` como Project Standard en el handoff.

- Si `$ARGUMENTS` incluye "instalar": además `adb install -r` + launch + smoke por logcat. Si
  hay más de un dispositivo adb, el agente debe devolver `NEEDS_INPUT` con la lista — llevásela
  al usuario.
- Precondiciones que el agente debe verificar: compilación limpia y sin play mode activo.

Al retornar, reportá: ruta/tamaño del APK, resultado del smoke (si aplica) y pendientes.
