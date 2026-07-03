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
| `ProjectSettings/ProjectSettings.asset` | `companyName: Simulador`, `productName: Simulador`, `applicationIdentifier` Android: `com.simulador.vr` (visor). La tablet usa `com.simulador.tablet`/"Simulador Tablet", seteados SOLO durante `TabletBuild` y restaurados al terminar (P6.7, ver abajo) — este archivo nunca queda con los valores de la tablet. |
| `backend/docker-compose.yml` + `backend/Caddyfile` | Deploy del backend (detalle en `docs/backend.md`). |
| `README.md` (raíz, sección 3) | Instrucciones de instalación para humanos; este doc es la referencia operativa. |

### Matriz visor vs tablet

| | **Visor (Quest)** | **Tablet (Android)** |
|---|---|---|
| Escena incluida | `Assets/Scenes/Main.unity` (única en EditorBuildSettings) | `Assets/Scenes/Tablet.unity` (pasada explícitamente por el script; NO está en EditorBuildSettings) |
| Loader OpenXR | **ON** (estado normal del proyecto) | **OFF** solo durante el build; restaurado después |
| Método de build | Build normal de Unity para Android (*File → Build Profiles / Build Settings*) | **SOLO** menú `Simulador → Build Tablet (Android)` o `-executeMethod Simulador.EditorTools.TabletBuild.BuildTablet` (batchmode) |
| Ruta de salida | La que elija el usuario (localmente existen `builds/Simulador_VR.apk` y `build/Simulador.apk`, ambas carpetas gitignoradas) | `Builds/Android/Simulador.apk` (constante `OutputPath` en `TabletBuild.cs`) |
| Package | `com.simulador.vr` | `com.simulador.tablet` (P6.7, CERRADO — antes compartía `com.simulador.vr` con el visor; ver Decisiones/Gotchas) |
| Product name | `Simulador` (Project Settings) | `Simulador Tablet` (seteado/restaurado solo durante el build, igual que el package) |
| Scripting backend | IL2CPP / arm64-v8a, min SDK 29 (según `README.md`) | Idéntico (mismo target compartido) |

### Flujo del build de tablet (`TabletBuild.BuildTablet()`)

```
¿target activo == Android? --no--> LogError + return null (no toca nada, ni loaders ni identifier)
        | sí
GetAndroidXrManager()  ← lee XRGeneralSettingsPerBuildTarget vía EditorBuildSettings
guardar loaders actuales (SerializedObject "m_Loaders")
guardar applicationIdentifier (NamedBuildTarget.Android) + productName actuales   (P6.7)
try:
    SetLoaders(manager, lista vacía)        ← XR OFF
    SetApplicationIdentifier(Android, "com.simulador.tablet") + productName = "Simulador Tablet"
    BuildPipeline.BuildPlayer(Tablet.unity → Builds/Android/Simulador.apk)
finally:
    SetLoaders(manager, loaders guardados)                 ← XR ON de nuevo, SIEMPRE
    SetApplicationIdentifier(Android, guardado) + productName = guardado   ← SIEMPRE (P6.7)
    (+ SaveAssets: el .asset de XR queda persistido como estaba)
```

### Instalación por adb

```bash
# Visor (Quest en modo desarrollador, conectado por USB o adb wifi)
adb install -r builds/Simulador_VR.apk

# Tablet
adb install -r Builds/Android/Simulador.apk

# Lanzar sin tocar el dispositivo (P6.7: packages DISTINTOS desde esta tarea)
adb shell monkey -p com.simulador.vr 1        # visor
adb shell monkey -p com.simulador.tablet 1    # tablet

# Logs de la app
adb logcat -s Unity
```

Con visor y tablet conectados a la vez, usar `adb -s <serial>` (los seriales salen de `adb devices`).
Con packages distintos (P6.7), ambos APKs también pueden convivir instalados en el MISMO
dispositivo si hiciera falta probarlos juntos (antes, instalar uno reemplazaba al otro).

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
- **`applicationIdentifier`/`productName` propios de la tablet, mismo patrón try/finally que el
  loader XR (P6.7)** → visor y tablet comparten el Player Settings del target Android (un solo
  proyecto, ver primera decisión); sin un identifier propio, ambos APKs salían con
  `com.simulador.vr` y no podían convivir instalados en el mismo dispositivo (instalar uno
  reemplazaba al otro). `TabletBuild.BuildTablet()` guarda `GetApplicationIdentifier(NamedBuildTarget.
  Android)` y `PlayerSettings.productName` ANTES de tocarlos (mismo momento que los loaders XR),
  los pisa con `com.simulador.tablet`/"Simulador Tablet" dentro del `try`, y los restaura SIEMPRE
  en el `finally` — igual de atómico e imposible de olvidar que la conmutación XR. El guard de
  target no-Android sigue devolviendo `null` ANTES de guardar/tocar nada, así que no hay estado
  que restaurar en ese camino.

## Gotchas

- **Buildear la tablet con `unity_build` (o Build Settings) directo = pantalla negra.** El target Android comparte config con Quest y tiene el loader OpenXR activo; en una tablet sin runtime VR el subsistema XR inicializa igual (`m_InitManagerOnStart: 1` en `Assets/XR/XRGeneralSettingsPerBuildTarget.asset`), secuestra el present y no se presenta ningún frame — la app corre pero la pantalla queda negra. Ese es el motivo de existir de `TabletBuild.cs`. Usar SIEMPRE el menú `Simulador → Build Tablet (Android)`.
- **`TabletBuild` restaura el loader incluso si el build falla.** Verificable en `Assets/Scripts/Editor/TabletBuild.cs` líneas 61–84: la llamada a `BuildPipeline.BuildPlayer` está dentro de un `try` cuyo `finally` ejecuta `SetLoaders(manager, savedLoaders)`. Un build fallido (o una excepción) no deja el proyecto sin XR. Cómo verificarlo en la práctica: forzar un fallo (p.ej. renombrar temporalmente `Tablet.unity`), correr el menú, y comprobar que `Android Providers → m_Loaders` en el `.asset` sigue conteniendo el OpenXRLoader (o mirar *Project Settings → XR Plug-in Management → Android*). Excepción real: si el Editor crashea a mitad del build, el `finally` no corre y hay que reactivar el loader a mano.
- **Si el build target activo no es Android, `BuildTablet()` devuelve `null` sin buildear** (solo un `LogError`). Un pipeline CI debe cambiar el target antes (`-buildTarget Android`) y no asumir que el método lo hace.
- **`applicationIdentifier` distinto para visor/tablet — RESUELTO (P6.7).** Hasta esta tarea
  compartían `com.simulador.vr` y no podían convivir instalados en el mismo dispositivo. Ahora la
  tablet builda como `com.simulador.tablet` (ver Decisiones); el visor (build normal, fuera de
  `TabletBuild`) sigue con `com.simulador.vr`. Si se ve un APK de tablet con el package viejo,
  sospechar de un build hecho ANTES de esta tarea o de haber buildeado la tablet sin pasar por
  `TabletBuild` (p.ej. `unity_build`/Build Settings directo — que además da pantalla negra por el
  loader XR, ver el gotcha de arriba).
- **`PlayerSettings.productName` es GLOBAL, no por plataforma** (a diferencia de
  `applicationIdentifier`, que sí es por `NamedBuildTarget`): `TabletBuild` lo pisa y restaura
  igual, pero si algún día se agrega OTRO consumidor de `productName` durante un build (analytics,
  splash screen custom, etc.) hay que tenerlo en cuenta — cualquier build que corra EN PARALELO al
  de la tablet (no debería pasar: Unity no soporta builds concurrentes en el mismo Editor) vería
  el nombre de la tablet a mitad de camino.
- **Restauración de `applicationIdentifier`/`productName` (P6.7): correcta en memoria, NO
  flusheada a disco sola.** Verificado en vivo (build real de tablet, no solo lectura de código):
  tras `TabletBuild.BuildTablet()`, consultar `PlayerSettings.GetApplicationIdentifier`/
  `PlayerSettings.productName` por `unity_execute_code` YA devuelve los valores del visor
  (`com.simulador.vr`/`Simulador`) — el `finally` corrió bien. Pero `git diff
  ProjectSettings/ProjectSettings.asset` en ese momento **todavía muestra los valores de la
  tablet** (`com.simulador.tablet`/`Simulador Tablet`) porque Unity no persiste `ProjectSettings.asset`
  en cada `PlayerSettings.Set*` — a diferencia del loader XR, que sí se fuerza con
  `AssetDatabase.SaveAssets()` dentro del propio script. El archivo en disco se pone al día recién
  con el próximo guardado del proyecto (`File → Save Project`, cierre del Editor, o cualquier otra
  operación que dispare el flush de Player Settings). **Implicación para el paso 2 de "Cómo
  probar"**: no alcanza con mirar el `git status` inmediatamente después del build para confirmar
  la restauración — si sale sucio, correr `File → Save Project` (o esperar el próximo flush) antes
  de concluir que algo quedó mal. El estado en memoria (que es lo que importa para builds
  subsiguientes en la misma sesión del Editor, p.ej. un build de visor inmediatamente después) es
  correcto igual, con o sin ese guardado.
- **`Builds/` vs `builds/` vs `build/`.** El script escribe en `Builds/Android/`; en el repo local existen además `build/` y `builds/` (salidas manuales previas, gitignoradas). En Windows el filesystem es case-insensitive, así que `Builds` y `builds` son la MISMA carpeta: el APK de la tablet puede aparecer junto a salidas viejas del visor. No confundir `builds/Simulador_VR.apk` (visor) con `Builds/Android/Simulador.apk` (tablet).
- **El visor build normal solo incluye `Main.unity`**: si se agregan escenas nuevas del visor hay que sumarlas a EditorBuildSettings; la tablet en cambio se controla desde la constante `ScenePath` del script.

## Cómo probar

1. **Tablet, camino feliz:** en el editor (target Android activo) ejecutar `Simulador → Build Tablet (Android)`. Esperar el log `[TabletBuild] Succeeded — 0 errores...` con la ruta `Builds/Android/Simulador.apk`.
2. **Verificar restauración del loader Y del identifier/nombre (P6.7):** tras el build, abrir
   *Project Settings → XR Plug-in Management → Android* y confirmar que OpenXR sigue tildado;
   abrir *Project Settings → Player → Android* y confirmar que `Package Name` volvió a
   `com.simulador.vr` y `Product Name` a `Simulador` (NO deben quedar en `com.simulador.tablet`/
   "Simulador Tablet"). `git status` no debe mostrar `Assets/XR/XRGeneralSettingsPerBuildTarget.asset`
   ni `ProjectSettings/ProjectSettings.asset` modificados.
3. **Instalar y arrancar:**
   ```bash
   adb install -r Builds/Android/Simulador.apk
   adb shell monkey -p com.simulador.tablet 1
   adb logcat -s Unity   # debe verse "DataManager: catalogo v... cargado desde ..."
   ```
   La tablet debe mostrar UI (no pantalla negra) y descubrir el visor por UDP si hay uno en la misma Wi-Fi.
   Confirmar el package instalado: `adb shell pm list packages | grep simulador` debe mostrar
   `com.simulador.tablet` (y, si el visor también está instalado en el mismo dispositivo de
   prueba, `com.simulador.vr` aparte — ya no se pisan).
4. **Visor:** build Android normal con `Main.unity`, `adb install -r` en el Quest, y comprobar en el casco que arranca en VR (si arranca "plano", el loader quedó apagado — ver Gotchas).
5. **Backend:** `docker compose up -d` en `backend/` y `curl http://localhost:8080/api/lenses` (receta completa en `docs/backend.md`).

## CI local

No hay runner remoto (dev único, una sola máquina): `scripts/ci-local.sh` (bash, Git Bash) es la
CI, pensada para correrse a mano antes de cerrar una tarea grande. Corre en secuencia:

1. **Tests EditMode de Unity** en batchmode (`-runTests -testPlatform EditMode -testResults
   <xml>`), parseando el NUnit XML resultante para reportar passed/failed.
2. **Build de tablet** (opcional, `--build`/`--build=tablet`): invoca
   `Simulador.EditorTools.TabletBuild.BuildTabletMenu` headless (mismo try/finally del loader
   OpenXR y del `applicationIdentifier`/`productName` de P6.7 que la receta manual) y valida el
   resultado por el log (`[TabletBuild] Succeeded`) + la presencia del APK. `--build=visor` o
   `--build=both` incluyen un intento de build del visor, que hoy queda como **SKIP explicado**:
   no existe un método de Editor invocable headless para el visor (el build "normal" es vía
   Build Settings o `unity_build` por MCP, no `-executeMethod`); agregar ese wrapper es tarea de
   `@unity-dev`, no del script.
3. **pytest del backend** (salvo `--skip-backend`): detecta un Python 3 utilizable
   (`python`/`python3`/`py -3`, filtrando el stub falso de Microsoft Store en Windows), crea un
   **venv temporal** (`ci-artifacts/venv-XXXXXX`, borrado siempre al terminar la etapa), instala
   `backend/api/requirements-dev.txt` y corre `pytest -q` en `backend/api`.

Uso:

```bash
scripts/ci-local.sh                       # tests EditMode + backend (sin build)
scripts/ci-local.sh --build               # + build de tablet
scripts/ci-local.sh --build=both          # + intento de build de tablet y visor (visor = SKIP)
scripts/ci-local.sh --skip-tests          # solo backend (o solo build, si se combina con --build)
scripts/ci-local.sh --skip-backend
scripts/ci-local.sh --unity-path="/c/Program Files/Unity/Hub/Editor/6000.5.1f1/Editor/Unity.exe"
```

`UNITY_PATH` (env var) o `--unity-path` fuerzan el ejecutable de Unity; si no se define ninguno,
el script autodetecta `Program Files/Unity/Hub/Editor/<versión de ProjectVersion.txt>/Editor/Unity.exe`.
Artefactos (logs, XML, venv temporal) van a `ci-artifacts/` (gitignorado).

**Gotcha crítico — lock del Editor:** Unity en `-batchmode` no puede compartir el proyecto con
una instancia del Editor abierta; en el mejor caso falla rápido, en el peor se cuelga. El script
detecta esto ANTES de invocar Unity chequeando `Temp/UnityLockfile`: si existe, mira si
`Unity.exe` sigue corriendo (`tasklist`) para distinguir "Editor realmente abierto" (cerralo y
reintentá) de "lockfile residual de un crash" (verificar y, si corresponde, borrarlo a mano), y
**aborta todo el script** con exit code 1 — nunca deja que Unity falle de forma críptica. Este
chequeo solo se dispara si se va a usar Unity (tests sin `--skip-tests`, o `--build`); un run
`--skip-tests` sin `--build` corre el backend solo y no toca el lock en absoluto. Validado en
vivo: con el Editor abierto, `scripts/ci-local.sh --build=both` aborta limpio mostrando el
mensaje de arriba; `scripts/ci-local.sh --skip-tests` corre el backend igual sin verse afectado.

## Pendientes / deuda

- CI local por script (`scripts/ci-local.sh`: tests EditMode + build de tablet opcional + pytest
  backend); runner remoto (GitHub Actions o similar) pendiente. El build de visor headless
  también está pendiente (falta un método de Editor dedicado, ver §CI local).
- `README.md` raíz puede quedar desalineado con la versión real del catálogo embebido
  (`Assets/StreamingAssets/lentes.json`, hoy `0.5.0-clinical`) si no se actualiza a mano en cada
  bump — no hay ningún mecanismo que lo mantenga en sync.
