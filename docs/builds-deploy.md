# Builds y deploy

## Qué es y por qué

Pipeline de compilación e instalación de las tres piezas del simulador: el APK del **visor** (Meta Quest, VR con OpenXR), el APK de la **tablet** de control (Android plano, sin VR) y el **backend** (Docker Compose). Visor y tablet salen del mismo proyecto Unity y comparten el build target Android, por lo que el punto crítico es la gestión del loader OpenXR: activo para el visor, apagado para la tablet.

## Arquitectura actual

| Archivo | Rol |
|---------|-----|
| `Assets/Scripts/Editor/TabletBuild.cs` | Build script dedicado de la tablet: apaga el loader OpenXR, buildea `Tablet.unity` y restaura el loader al terminar. |
| `Assets/XR/XRGeneralSettingsPerBuildTarget.asset` | Config XR por build target. El bloque "Android Providers" tiene `m_Loaders` apuntando al loader OpenXR (guid `0613ddada2fe14947a9b75e90912b7ba`). |
| `Assets/XR/Loaders/OpenXRLoader.asset` | El loader OpenXR que se activa/desactiva. |
| `Assets/XR/Settings/OpenXR Package Settings.asset` | Configuración del paquete OpenXR (features, interaction profiles). |
| `ProjectSettings/EditorBuildSettings.asset` | Lista de escenas del build normal: solo `Assets/Scenes/Main.unity` (visor). También registra el config object XR bajo la key `com.unity.xr.management.loader_settings`, la misma que lee `TabletBuild.GetAndroidXrManager()`. |
| `ProjectSettings/ProjectSettings.asset` | `companyName: Simulador`, `productName: Simulador`, `applicationIdentifier` Android: `com.simulador.vr`. |
| `backend/docker-compose.yml` + `backend/Caddyfile` | Deploy del backend (detalle en `docs/backend.md`). |
| `README.md` (raíz, sección 3) | Instrucciones de instalación para humanos; este doc es la referencia operativa. |

### Matriz visor vs tablet

| | **Visor (Quest)** | **Tablet (Android)** |
|---|---|---|
| Escena incluida | `Assets/Scenes/Main.unity` (única en EditorBuildSettings) | `Assets/Scenes/Tablet.unity` (pasada explícitamente por el script; NO está en EditorBuildSettings) |
| Loader OpenXR | **ON** (estado normal del proyecto) | **OFF** solo durante el build; restaurado después |
| Método de build | Build normal de Unity para Android (*File → Build Profiles / Build Settings*) | **SOLO** menú `Simulador → Build Tablet (Android)` o `-executeMethod Simulador.EditorTools.TabletBuild.BuildTablet` (batchmode) |
| Ruta de salida | La que elija el usuario (localmente existen `builds/Simulador_VR.apk` y `build/Simulador.apk`, ambas carpetas gitignoradas) | `Builds/Android/Simulador.apk` (constante `OutputPath` en `TabletBuild.cs`) |
| Package | `com.simulador.vr` | `com.simulador.vr` (mismo — ver Gotchas) |
| Scripting backend | IL2CPP / arm64-v8a, min SDK 29 (según `README.md`) | Idéntico (mismo target compartido) |

### Flujo del build de tablet (`TabletBuild.BuildTablet()`)

```
¿target activo == Android? --no--> LogError + return null (no toca nada)
        | sí
GetAndroidXrManager()  ← lee XRGeneralSettingsPerBuildTarget vía EditorBuildSettings
guardar loaders actuales (SerializedObject "m_Loaders")
try:
    SetLoaders(manager, lista vacía)        ← XR OFF
    BuildPipeline.BuildPlayer(Tablet.unity → Builds/Android/Simulador.apk)
finally:
    SetLoaders(manager, loaders guardados)  ← XR ON de nuevo, SIEMPRE
    (+ SaveAssets: el .asset queda persistido como estaba)
```

### Instalación por adb

```bash
# Visor (Quest en modo desarrollador, conectado por USB o adb wifi)
adb install -r builds/Simulador_VR.apk

# Tablet
adb install -r Builds/Android/Simulador.apk

# Lanzar sin tocar el dispositivo (mismo package en ambos)
adb shell monkey -p com.simulador.vr 1

# Logs de la app
adb logcat -s Unity
```

Con visor y tablet conectados a la vez, usar `adb -s <serial>` (los seriales salen de `adb devices`).

### Deploy del backend (resumen)

```bash
cd backend
cp .env.example .env      # defaults sirven para local
docker compose up -d      # api + db + bucket (MinIO) + caddy
curl http://localhost:8080/healthz
```

En producción: VPS con Docker, DNS apuntando al dominio, `.env` con `DOMAIN=api.tu-dominio.com`, `SCHEME=` (vacío), `PORT=443` y secrets regenerados; Caddy emite el certificado Let's Encrypt solo. Detalle completo en `docs/backend.md` y `backend/README.md`.

## Decisiones y porqués

- **Un solo proyecto y un solo build target Android para visor y tablet** → evita duplicar código compartido (catálogo, red WebSocket, modelos); el costo es tener que conmutar el loader XR por build.
- **Script de editor dedicado (`TabletBuild`) en vez de un Build Profile separado** → la conmutación del loader queda automatizada y atómica (try/finally), imposible de olvidar a mano; además es invocable headless por CLI (`-executeMethod`).
- **La escena de la tablet no está en EditorBuildSettings** → el build normal (visor) nunca la arrastra por accidente; `TabletBuild` la pasa explícitamente en `BuildPlayerOptions.scenes`.
- **Manipular `m_Loaders` vía `SerializedObject` en vez de `XRPackageMetadataStore.Assign/Remove`** → control exacto de la lista y restauración byte a byte de lo que había, sin depender del metadata store.
- **`AssetDatabase.SaveAssets()` al restaurar** → la config XR es un `.asset` versionado; se persiste para que el working tree no quede sucio ni el estado dependa de la sesión del editor.
- **Salida fija `Builds/Android/Simulador.apk`** → ruta predecible para CI y para el `adb install -r` de la receta; el directorio se crea si falta.
- **Backend en Docker Compose** → una sola pieza desplegable con TLS automático (Caddy); el visor funciona sin él (catálogo embebido), así que el deploy del backend no bloquea las demos.

## Gotchas

- **Buildear la tablet con `unity_build` (o Build Settings) directo = pantalla negra.** El target Android comparte config con Quest y tiene el loader OpenXR activo; en una tablet sin runtime VR el subsistema XR inicializa igual (`m_InitManagerOnStart: 1` en `Assets/XR/XRGeneralSettingsPerBuildTarget.asset`), secuestra el present y no se presenta ningún frame — la app corre pero la pantalla queda negra. Ese es el motivo de existir de `TabletBuild.cs`. Usar SIEMPRE el menú `Simulador → Build Tablet (Android)`.
- **`TabletBuild` restaura el loader incluso si el build falla.** Verificable en `Assets/Scripts/Editor/TabletBuild.cs` líneas 61–84: la llamada a `BuildPipeline.BuildPlayer` está dentro de un `try` cuyo `finally` ejecuta `SetLoaders(manager, savedLoaders)`. Un build fallido (o una excepción) no deja el proyecto sin XR. Cómo verificarlo en la práctica: forzar un fallo (p.ej. renombrar temporalmente `Tablet.unity`), correr el menú, y comprobar que `Android Providers → m_Loaders` en el `.asset` sigue conteniendo el OpenXRLoader (o mirar *Project Settings → XR Plug-in Management → Android*). Excepción real: si el Editor crashea a mitad del build, el `finally` no corre y hay que reactivar el loader a mano.
- **Si el build target activo no es Android, `BuildTablet()` devuelve `null` sin buildear** (solo un `LogError`). Un pipeline CI debe cambiar el target antes (`-buildTarget Android`) y no asumir que el método lo hace.
- **Mismo `applicationIdentifier` (`com.simulador.vr`) para visor y tablet** → no pueden convivir ambos APK en un mismo dispositivo: instalar uno reemplaza al otro. En dispositivos distintos (el caso de uso real) no molesta, pero muerde al probar ambos en la misma tablet/Quest.
- **`Builds/` vs `builds/` vs `build/`.** El script escribe en `Builds/Android/`; en el repo local existen además `build/` y `builds/` (salidas manuales previas, gitignoradas). En Windows el filesystem es case-insensitive, así que `Builds` y `builds` son la MISMA carpeta: el APK de la tablet puede aparecer junto a salidas viejas del visor. No confundir `builds/Simulador_VR.apk` (visor) con `Builds/Android/Simulador.apk` (tablet).
- **El visor build normal solo incluye `Main.unity`**: si se agregan escenas nuevas del visor hay que sumarlas a EditorBuildSettings; la tablet en cambio se controla desde la constante `ScenePath` del script.

## Cómo probar

1. **Tablet, camino feliz:** en el editor (target Android activo) ejecutar `Simulador → Build Tablet (Android)`. Esperar el log `[TabletBuild] Succeeded — 0 errores...` con la ruta `Builds/Android/Simulador.apk`.
2. **Verificar restauración del loader:** tras el build, abrir *Project Settings → XR Plug-in Management → Android* y confirmar que OpenXR sigue tildado; `git status` no debe mostrar `Assets/XR/XRGeneralSettingsPerBuildTarget.asset` modificado.
3. **Instalar y arrancar:**
   ```bash
   adb install -r Builds/Android/Simulador.apk
   adb shell monkey -p com.simulador.vr 1
   adb logcat -s Unity   # debe verse "DataManager: catalogo v... cargado desde ..."
   ```
   La tablet debe mostrar UI (no pantalla negra) y descubrir el visor por UDP si hay uno en la misma Wi-Fi.
4. **Visor:** build Android normal con `Main.unity`, `adb install -r` en el Quest, y comprobar en el casco que arranca en VR (si arranca "plano", el loader quedó apagado — ver Gotchas).
5. **Backend:** `docker compose up -d` en `backend/` y `curl http://localhost:8080/api/lenses` (receta completa en `docs/backend.md`).

## Pendientes / deuda

- La URL del backend está hardcodeada en `Assets/Scripts/Runtime/Data/DataManager.cs` (`http://192.168.88.198:8080`, IP de LAN de desarrollo): cada build apunta a esa IP salvo edición manual.
- No hay CI: los builds son manuales desde el editor (el `-executeMethod` headless existe pero nadie lo invoca automáticamente).
- `README.md` raíz menciona catálogo embebido `v0.3.1-clinical`; el real en `Assets/StreamingAssets/lentes.json` es `0.4.0-clinical`.
- El APK de la tablet hereda el package `com.simulador.vr`; separar identifiers si algún día deben convivir en un dispositivo.
