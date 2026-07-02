---
name: build-pipeline
description: Fuente única del pipeline de builds - matriz visor/tablet (gotcha del loader OpenXR), TabletBuild, adb, smoke por logcat y deploy del backend. Cargar antes de buildear o instalar.
---

# Pipeline de builds — fuente única

Estado vivo del sistema (rutas exactas, package, hallazgos): `docs/builds-deploy.md` — leer
primero. Script clave: `Assets/Scripts/Editor/TabletBuild.cs`.

## Matriz de builds

| | **Visor (Quest)** | **Tablet (Android sin VR)** |
|---|---|---|
| Escena | `Assets/Scenes/Main.unity` | `Assets/Scenes/Tablet.unity` |
| Loader OpenXR | **ON** (verificarlo activo) | **OFF durante el build** (lo maneja TabletBuild) |
| Método | Build Android normal (`unity_build`) | **SOLO** `Simulador → Build Tablet (Android)` (`unity_execute_menu_item`) o `unity_execute_code` → `Simulador.EditorTools.TabletBuild.BuildTablet()` |
| Salida | según configuración del build | `Builds/Android/Simulador.apk` |

## El gotcha OpenXR (por qué existe TabletBuild)

Una app Android **sin VR** con el loader OpenXR activo arranca en **pantalla negra** (el runtime
XR intenta inicializar un HMD que no existe). `TabletBuild.cs`:
1. desactiva el loader OpenXR del target Android,
2. cambia la escena a `Tablet.unity` y buildea,
3. **restaura el loader SIEMPRE** (try/finally — incluso si `BuildPlayer` falla o lanza).

Reglas derivadas:
- **NUNCA** buildear la tablet con `unity_build` directo (no togglea el loader).
- **Post-build de tablet**: verificar que el loader quedó restaurado (si el Editor crasheó a
  mitad del build, puede quedar OFF → el próximo build de visor saldría sin VR). Chequeo:
  settings de XR Plug-in Management para Android con loader OpenXR activo.
- Nunca buildear con `unity_get_compilation_errors` sucio ni con play mode activo.

## adb (instalación y smoke)

```bash
adb devices                      # si hay >1 dispositivo: preguntar, no elegir
adb install -r <ruta.apk>
adb shell monkey -p <package> -c android.intent.category.LAUNCHER 1   # launch
adb logcat -s Unity              # smoke: sin excepciones en el arranque
```

- Visor y tablet comparten hoy el mismo package (`com.simulador.vr` — ver doc viva): **no
  coexisten en un mismo dispositivo**; instalar tablet sobre el Quest pisa el visor. Confirmar
  el dispositivo destino SIEMPRE.
- Smoke mínimo: la app abre, sin `FATAL EXCEPTION`/`MissingMethodException` (esta última huele
  a stripping IL2CPP) en los primeros segundos.

## Backend (deploy)

- Local: `docker compose up -d --build` en `backend/` + `docker compose ps` + curl de salud.
- Producción (VPS + Caddy HTTPS): **solo a pedido explícito** — receta en `backend/README.md`
  y estado en `docs/backend.md`.
