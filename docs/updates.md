# Updates semi-automáticos (visor / tablet)

## Qué es y por qué

Sistema que permite a las dos apps Android del proyecto (visor Quest `com.simulador.vr` y
tablet `com.simulador.tablet`) chequear contra el backend si hay una versión nueva de su
propio APK, descargarla y (en fases posteriores) instalarla — sin pasar por una store. El
objetivo es poder empujar fixes/nuevas builds a los dispositivos de campo sin reconectarlos
por USB. Igual que el catálogo de lentes (`docs/catalogo-lentes.md`, patrón calcado), la
lógica de negocio (parseo de semver, del manifest, decisión de actualizar) vive separada en
una clase PURA testeable en EditMode; el `MonoBehaviour` orquesta IO/red/corrutinas.

**Estado actual: F5 completa + F6 completa (telemetría + keystore propio).** Existe la lógica
pura, el manager con check de manifest, descarga con progreso, verificación SHA256, el
`UpdateInstaller` que lanza el intent de instalación Android (manifest/permisos/`FileProvider`
incluidos), las DOS UIs del cartel de update (tablet y visor VR) — ver "UI del cartel (F5)" más
abajo —, el envío de telemetría `update_*` al backend (`POST /api/log`) — ver "Telemetría (F6)"
más abajo — y el keystore propio del proyecto que firma los APKs (2026-07-09, detalle en
`docs/builds-deploy.md` §Firma (keystore) del proyecto; primeras builds update-capable
`visor-0.1.0-updatecap.apk`/`tablet-0.1.0-updatecap.apk` ya firmadas y verificadas con
`apksigner`, gate F4 de manifest/FileProvider pasado en ambas).
**Pendiente (fuera de esta tarea):**
- **F7** — validación E2E en dispositivos reales + primer release publicado por el panel
  (subir estos mismos APKs 0.1.0/vc1 al panel, `apk_sha256` real recién existirá ahí).

## Arquitectura actual

```
DataManager.BackendConfigReady (WaitUntil)             ← backendUrl ya resuelto por capas
        │
        ▼
GET {backendUrl}/api/manifest.json?app=visor|tablet     (UpdateManager.CheckManifest)
        │  200 → JSON manifest         │ 503 → sin version activa (NORMAL, no error)
        │  otro código / inalcanzable / excepción síncrona → log + fin silencioso
        ▼
UpdateLogic.TryParseManifest(json)  →  UpdateManifest (lógica pura)
        │
        ▼
UpdateLogic.Decide(Application.version, manifest)  →  None | Optional | Forced (lógica pura)
        │  None → fin
        ▼
UpdateAvailable(manifest, forced)   ◄── evento; F4/F5 le cuelgan la UI del cartel
        │  (usuario acepta, vía UI de F5)
        ▼
UpdateManager.AcceptUpdate() → DownloadApk (UnityWebRequest + DownloadHandlerFile)
        │  progreso por frame → DownloadProgress(float)
        │  fallo (excepción síncrona / result != Success / responseCode != 200)
        │      → borra el parcial + UpdateFailed(mensaje)
        ▼
¿manifest.ApkSha256 vacío?
   sí → ReadyToInstall(path)                    (nada que verificar, el dummy manda "")
   no → VerifySha256 (SHA256 chunked, 1 MB + yield return null entre bloques)
            │  no matchea → borra el parcial + UpdateFailed("sha_mismatch")
            ▼
        ReadyToInstall(path)   ◄── marca _readyToInstall = true (guarda a LaunchInstall)
        │  (usuario confirma instalar, vía UI de F5)
        ▼
UpdateManager.LaunchInstall() → UpdateInstaller.LaunchInstall(apkPath, targetVersion, onFailed)
        │  no-op si _readyToInstall es false (no hay descarga verificada vigente)
        ▼
¿packageManager.canRequestPackageInstalls()?
   no  → abre ajuste ACTION_MANAGE_UNKNOWN_APP_SOURCES (package:<identifier>)
         → InstallLaunchResult.PermissionRequested
         → UpdateManager arma _permissionPendingRetry; al volver a foco
           (OnApplicationPause(false)) reintenta LaunchInstall() una vez, solo
   sí  → escribe persistentDataPath/update_pending.json {"target_version": manifest.ApkVersion}
         → FileProvider.getUriForFile(...) + intent ACTION_VIEW
           (application/vnd.android.package-archive, FLAG_GRANT_READ_URI_PERMISSION |
           FLAG_ACTIVITY_NEW_TASK) + startActivity
         → InstallLaunchResult.Started (el instalador de Android toma el control)
```

En el próximo arranque de la app (post-instalación real, un proceso nuevo), `UpdateManager.Awake`
lee `update_pending.json` (si existe), compara `Application.version` contra `target_version`, loguea
`Update: update aplicado OK (X)` o `Update: update incompleto (sigue Y, esperaba X)`, deja el
resultado en `LastUpdateOutcome` (string simple, pensado para que F6 lo mande al backend) y borra
el marcador.

- `Assets/Scripts/Runtime/Update/UpdateLogic.cs` — lógica PURA (sin `UnityEngine`, sin IO),
  namespace `Simulador.Update`, mismo patrón que `DataManagerLogic.cs`:
  - `TryParseSemver(version, out (major, minor, patch))` — parsea `"major.minor.patch"`;
    componentes faltantes se completan con `0` (`"1.2"` → `(1,2,0)`, `"1"` → `(1,0,0)`).
    Más de 3 componentes (`"1.2.3.4"`) → **inválido** (`false`): el contrato del backend es
    estrictamente `major.minor.patch`, un cuarto componente es forma desconocida y es más
    seguro fallar que adivinar qué descartar. Nunca tira excepción.
  - `CompareVersions(a, b)` → `-1`/`0`/`1`. Si alguna cadena no parsea se la trata como
    `"0.0.0"` para poder comparar sin tirar excepción — en la práctica casi no importa
    porque `Decide` ya filtra aparte "versión remota no parseable" antes de comparar.
  - `UpdateManifest` (clase de datos: `App`, `ApkVersion`, `MinApkVersion`, `ApkUrl`,
    `ApkSha256`, `Changelog`, todos `string`).
  - `TryParseManifest(json, out manifest)` — Newtonsoft + DTO privado snake_case
    (`ManifestDto`, mismo patrón que `BackendConfig` de `DataManagerLogic.cs`). JSON
    inválido/vacío/nulo → `false`. **Claves faltantes** (p. ej. `{"app":"visor"}` solo) NO
    invalidan el parseo — el objeto no es null, solo esas propiedades quedan `null`; es
    `Decide` quien las trata como "no hay update" (ver abajo). Nunca tira excepción.
  - `UpdateDecision { None, Optional, Forced }`.
  - `Decide(installedVersion, manifest)` — fail-safe a `None` si el manifest es `null`, si
    `ApkVersion` no parsea, o si la remota es `<=` la instalada (cubre paridad — el caso
    dummy `0.1.0==0.1.0` — y downgrade). Si la remota es mayor: `Forced` solo si
    `MinApkVersion` **parsea** y la instalada queda por debajo; si `MinApkVersion` no
    parsea/está ausente se trata como "sin mínimo exigido" (nunca se fuerza por un dato
    faltante) → `Optional`.
  - `AppChannelFromIdentifier(identifier)` — `".tablet"` en el identifier → `"tablet"`;
    cualquier otra cosa (incluido `com.simulador.vr`, null, vacío) → `"visor"`.
  - `Sha256Matches(expectedHex, actualHex)` — comparación case-insensitive;
    `expectedHex` vacío/null/whitespace → `true` (nada que verificar, cubre el dummy que
    manda `apk_sha256: ""`).
  - `PendingMarkerFileName` (const `"update_pending.json"`), `UpdatePendingMarker`
    (`TargetVersion`), `SerializePendingMarker(targetVersion)` / `TryParsePendingMarker(json,
    out marker)` (F4) — el nombre del archivo vive acá como única fuente de verdad porque lo
    escribe `UpdateInstaller` (justo antes del intent de instalación) y lo lee
    `UpdateManager` (al arrancar); nunca tira excepción, mismo contrato que
    `TryParseManifest`.
- `Assets/Scripts/Runtime/Update/UpdateManager.cs` — `MonoBehaviour` singleton, bootstrap
  calcado de `DataManager` (`[RuntimeInitializeOnLoadMethod(BeforeSceneLoad)]` + chequeo de
  singleton duplicado en `Awake` + `DontDestroyOnLoad`). Al arrancar borra
  `persistentDataPath/updates/` si quedó un residuo de una corrida anterior (best-effort,
  silencioso). Espera `DataManager.BackendConfigReady` (`WaitUntil`) antes de chequear el
  manifest — necesita el `BackendUrl` ya resuelto por capas. El check y la descarga usan el
  mismo patrón de degradación sin excepciones que `DataManager.TrySyncWithBackend`
  (`SendWebRequest()` en try/catch síncrono, chequeo de `result`/`responseCode`).
  - Eventos: `UpdateAvailable(UpdateManifest, bool forced)`, `DownloadProgress(float)`,
    `UpdateFailed(string)`, `ReadyToInstall(string path)`.
  - API pública: `AcceptUpdate()` / `RetryDownload()` (arrancan/reintentan la descarga del
    último manifest recibido), `PostponeUpdate()` (solo loguea; F5 no agrega recordatorios —
    ver Decisiones), `LaunchInstall()` (F4 — dispara `UpdateInstaller.LaunchInstall` con el
    path/versión del último manifest; no-op logueado si `ReadyToInstall` todavía no se
    disparó o si una descarga nueva invalidó el estado), `CancelDownload()` (F5, nueva —
    aborta la `UnityWebRequest` activa vía `UnityWebRequest.Abort()` + corta la corutina de
    `DownloadApk`; deja el estado como si nunca se hubiera empezado a descargar y a propósito
    NO dispara `UpdateFailed` porque es una cancelación del usuario, no un fallo; no-op si no
    hay descarga en curso) y la propiedad pública `LastUpdateOutcome` (resultado del último
    update, ver abajo).
  - `OnApplicationPause(bool pause)` (F4): si `LaunchInstall()` tuvo que abrir el ajuste de
    "fuentes desconocidas" (sin permiso), reintenta automáticamente una vez al volver a foco
    (`pause == false`) — el clínico puede haber concedido el permiso en Settings y vuelto a
    la app.
  - Al arrancar (`Awake`, antes de `CleanupResidualUpdates`) lee `update_pending.json` si
    existe (dejado por `UpdateInstaller` antes del intent anterior), compara
    `Application.version` contra el `target_version` del marcador, loguea el resultado, lo
    deja en `LastUpdateOutcome` y borra el marcador (best-effort, nunca bloquea el arranque).
  - Descarga a `persistentDataPath/updates/simulador-update.apk` vía
    `UnityWebRequest` + `DownloadHandlerFile` (`removeFileOnAbort = true`), **sin
    `timeout`** (a propósito, ver Gotchas). Progreso emitido cada frame mientras
    `!op.isDone`.
  - Verificación SHA256 **chunked** (`VerifySha256`): `System.Security.Cryptography.SHA256`
    con `TransformBlock` sobre bloques de ~1 MB leídos de un `FileStream`, `yield return
    null` entre cada bloque (nunca threads — evita freeze del hilo principal con un APK
    grande). `TransformFinalBlock` al terminar, hex compilado y comparado con
    `UpdateLogic.Sha256Matches`.
- **`Assets/Scripts/Runtime/Data/DataManager.cs`** — 2 agregados mínimos para que
  `UpdateManager` pueda leer la config ya resuelta: `public string BackendUrl => backendUrl;`
  y `public bool BackendConfigReady { get; private set; }`, seteado a `true` al **final** de
  `LoadBackendConfig()` (siempre, incluso cuando ninguna capa aplicó y se usó el default
  serializado — "config resuelta" no es solo el camino feliz).
- `Assets/Scripts/Runtime/Update/UpdateInstaller.cs` (F4) — `static class` sin estado,
  namespace `Simulador.Update`. Toda la implementación real vive en
  `#if UNITY_ANDROID && !UNITY_EDITOR` (no-op logueado fuera de Android/Editor). JNI puro
  (`AndroidJavaClass`/`AndroidJavaObject`, mismo patrón que `TabletController.TryGetWifiSsid`
  — NUNCA reflection de C#, IL2CPP-safe):
  - `currentActivity` vía `com.unity3d.player.UnityPlayer.GetStatic<AndroidJavaObject>`.
  - `packageManager.canRequestPackageInstalls()` — si `false`: abre
    `android.settings.MANAGE_UNKNOWN_APP_SOURCES` con data `Uri.parse("package:" +
    Application.identifier)` y devuelve `InstallLaunchResult.PermissionRequested` (no lanza
    el instalador; el caller reintenta).
  - Si hay permiso: escribe el marcador (`WritePendingMarker`, best-effort — un fallo acá
    loguea pero NO aborta el intent) y arma el intent real:
    `androidx.core.content.FileProvider.getUriForFile(activity, applicationId +
    ".fileprovider", new File(apkPath))` → `Intent(ACTION_VIEW)` +
    `setDataAndType(uri, "application/vnd.android.package-archive")` + flags
    `FLAG_GRANT_READ_URI_PERMISSION | FLAG_ACTIVITY_NEW_TASK` → `startActivity`.
  - Try/catch total alrededor de todo el cuerpo: cualquier excepción (versión de
    androidx.core distinta, OEM raro, `FileProvider` no registrado) cae en
    `InstallLaunchResult.Failed` + invoca el callback `onFailed` que `UpdateManager` conecta
    a su evento `UpdateFailed`. Nunca deja escapar una excepción.
- `Assets/Plugins/Android/AndroidManifest.xml` (F4, compartido visor/tablet — ver
  `docs/builds-deploy.md` §Gotchas sobre los incidentes reales de este archivo) — agregados
  quirúrgicos, SIN tocar el bloque `<activity>` existente: `<uses-permission
  android:name="android.permission.REQUEST_INSTALL_PACKAGES" />` junto a los permisos
  existentes, y un `<provider android:name="androidx.core.content.FileProvider"
  android:authorities="${applicationId}.fileprovider" .../>` como hermano de la `<activity>`
  dentro de `<application>`. `${applicationId}` lo resuelve Gradle por build (`com.simulador.vr`
  / `com.simulador.tablet`, ver `docs/builds-deploy.md`), así que el mismo manifest sirve para
  ambos sin duplicar nada.
- `Assets/Plugins/Android/SimuladorUpdate.androidlib/` (F4) — Android Library Plugin (la forma
  soportada por Unity 6 para agregar recursos `res/xml/` sueltos sin un plugin AAR completo):
  `AndroidManifest.xml` mínimo (`package="com.simulador.updateres"`, sin más) y
  `res/xml/file_paths.xml` con las rutas que expone el `FileProvider`
  (`<files-path name="updates" path="updates/" />` + `<external-files-path>` equivalente,
  cubriendo el `persistentDataPath/updates/` donde vive el APK). Verificado con
  `PluginImporter.GetCompatibleWithPlatform(BuildTarget.Android) == true` sobre la carpeta raíz.
- `Assets/Tests/EditMode/UpdateLogicTests.cs` — cobertura de `UpdateLogic`: `TryParseSemver`
  (3/2/1 componentes, inválidos), `CompareVersions` (igual/mayor por cada componente/menor),
  `AppChannelFromIdentifier`, `Sha256Matches`, `TryParseManifest` (JSON real del backend,
  inválido, vacío/null, claves faltantes), `Decide` (paridad, optional, forced, `apk_version`
  inválido, downgrade, manifest null, `min_apk_version` inválido no fuerza) y (F4)
  `SerializePendingMarker`/`TryParsePendingMarker` (roundtrip, JSON inválido, vacío/null).

### UI del cartel (F5)

Dos implementaciones independientes que cuelgan de los MISMOS eventos de `UpdateManager`
(`UpdateAvailable`/`DownloadProgress`/`UpdateFailed`/`ReadyToInstall`) y llaman a la MISMA API
pública (`AcceptUpdate`/`CancelDownload`/`RetryDownload`/`LaunchInstall`/`PostponeUpdate`) —
ninguna vive en `Update/` salvo la del visor; la de la tablet vive en `Net/TabletController.cs`
porque esa es la única clase de UI de la app tablet (mismo criterio que `PinScreen`/
`FullscreenStream`, ver `docs/tablet.md`).

- **Tablet — `UpdateScreen` (región dentro de `Assets/Scripts/Runtime/Net/TabletController.cs`,
  namespace `Simulador.Tablet`)** — overlay modal full-screen (scrim semi-opaco + card
  centrada, mismo patrón visual que `FullscreenStream`), construido en `BuildUpdateScreen`
  y agregado ÚLTIMO en `BuildUI()` (`BuildFullscreenStream` → `BuildUpdateScreen`) para quedar
  por encima de TODAS las demás pantallas/overlays (Connect/Pin/Reconnect/Main/FullscreenStream),
  no solo de la que esté activa en ese momento. Un solo par de botones
  (`_updatePrimaryBtn`/`_updateSecondaryBtn`) cuyo texto/handler/visibilidad cambian según el
  estado en vez de construir 4 pares:
  - `OnUpdateAvailable(manifest, forced)` → título "Actualización disponible",
    "vINSTALADA → vMANIFEST", changelog; primario "Actualizar" (`AcceptUpdate`), secundario
    "Ahora no" (`PostponeUpdate` + oculta el overlay) OCULTO si `forced`.
  - `ShowUpdateDownloading()` (al tocar Actualizar/Reintentar) → "Descargando… NN %"
    (actualizado por `OnUpdateDownloadProgress`), secundario "Cancelar" (`CancelDownload` +
    `PostponeUpdate` + oculta) SIEMPRE visible, incluso si `forced` — a diferencia de
    Available/Failed, cancelar una descarga en curso no tiene la misma semántica que "saltear
    la actualización forzada" (ver Decisiones).
  - `OnUpdateReadyToInstall` → "Descarga verificada"; primario "Instalar" (`LaunchInstall` +
    oculta el overlay), secundario oculto.
  - `OnUpdateFailed(message)` → "Error al actualizar" + mensaje (`FriendlyUpdateError` traduce
    `"sha_mismatch"` a un texto clínico; cualquier otro mensaje de `UpdateManager` pasa tal
    cual); primario "Reintentar" (`RetryDownload` → vuelve a `ShowUpdateDownloading`),
    secundario "Cerrar" OCULTO si `forced`.
  - Suscripción null-safe en `Start()` (`SubscribeUpdateEvents`, warning + no-op si
    `UpdateManager.Instance` es null) y desuscripción en `OnDestroy()`
    (`UnsubscribeUpdateEvents`).
- **Visor — `Assets/Scripts/Runtime/Update/UpdatePromptVR.cs`** — solo lo instancia
  `UpdateManager.MaybeShowVrPrompt` (ver Decisiones "Selección de UI por presencia de
  TabletController, no por identifier" más abajo), nunca se referencia desde una escena.
  Canvas world-space 100% por código (nada de escena, ni siquiera un prefab), child directo de
  `Camera.main`, SIN `GraphicRaycaster`/`EventSystem` (no hay interacción por puntero — el visor
  no tiene mouse/touch, solo botones de mando). **Distancia estéreo cómoda (`localPosition
  (0,0,1.5)`, `localScale 0.0015`, `sizeDelta (700,420)`) + ocultamiento de la escena a nivel de
  CÁMARA** (rediseño, ver Decisiones "De canvas opaco pegado al ojo a
  `CameraSceneOcclusionGate`" — reemplaza el fondo opaco pegado a `0.15 m` de un fix anterior,
  que causaba diplopia real en Quest). 3 `UnityEngine.UI.Text` (fuente builtin
  `LegacyRuntime.ttf` vía `Resources.GetBuiltinResource<Font>`, igual que
  `HudController`/`LicenseBlockScreenVR` para no sumar una dependencia de fuente TMP nueva en
  Runtime): título, cuerpo (versión+changelog / progreso % / mensaje de error) y leyenda de
  controles. Estados y leyenda: `Available` → "A: actualizar   B: ahora no" (sin "B:" si
  `forced`); `Downloading` → sin leyenda (no hay opción de cancelar en VR, a diferencia de la
  tablet — ver Decisiones); `Ready` → "A: instalar"; `Failed` → "A: reintentar B: cerrar" (sin
  "B:" si `forced`). Mientras el cartel está visible (cualquier estado) el ocultamiento de la
  escena se mantiene sin interrupción — recién se libera cuando se destruye TODO el canvas en
  `Close()` ("Ahora no"/B en `Available`, "Cerrar"/B en `Failed`, o tras `LaunchInstall()` en
  `Ready`); si el update es `forced` no hay opción B en ningún estado, así que no hay forma de
  volver a ver la escena hasta terminar el flujo. Input propio (`InputAction` para
  `<XRController>{RightHand}/primaryButton|secondaryButton`, mismo patrón que
  `SimuladorInput.cs`) habilitado SOLO mientras el cartel está visible; **mientras está visible
  deshabilita `Vision.SimuladorInput`** (`FindFirstObjectByType` + `enabled = false`) para que
  A/B no ciclen lentes de fondo mientras el clínico decide sobre el update — se restaura en
  `OnDestroy()` (`RestoreGameplayInput`), que es el ÚNICO punto de limpieza: `Close()` hace
  `Destroy(_canvasGo)` + `Destroy(this)`, y es `OnDestroy()` quien desuscribe de
  `UpdateManager`, deshabilita/dispone las `InputAction` propias, libera
  `CameraSceneOcclusionGate` (si la había adquirido) y restaura `SimuladorInput.enabled = true`
  — así la limpieza es idéntica sin importar si el cartel se cierra por `Close()` explícito o
  porque la escena/GameObject se destruye por otra vía. El ocultamiento de escena es ORTOGONAL a
  esa lógica de input (sin cambios ahí) y coexiste con `LicenseBlockScreenVR` vía refcount (ver
  Decisiones).
- **`Assets/Scripts/Runtime/Data/CameraSceneOcclusionGate.cs`** (nuevo, rediseño de esta tarea)
  — `static class` sin `MonoBehaviour`, namespace `Simulador.Data` (mismo criterio que
  `BackendTelemetry.cs`: infra compartida entre `Update/` y `License/`, no es específica de
  ninguno de los dos). Oculta la escena de fondo a nivel de **cámara** en vez de con geometría:
  mientras esté "adquirida", restringe `Camera.main.cullingMask` a la capa builtin `UI` (layer
  5, libre en el proyecto — ninguna otra capa la usaba) y fuerza `clearFlags = SolidColor` +
  `backgroundColor` oscuro sólido; `ApplyOverlayLayer(GameObject root)` pone recursivamente todo
  un canvas (root + hijos) en esa capa (necesario: el `cullingMask` filtra por capa de
  GameObject, no se hereda del padre). `Acquire()`/`Release()` llevan un **refcount estático**:
  el primer `Acquire()` guarda el estado original de la cámara (`cullingMask`/`clearFlags`/
  `backgroundColor`) y lo aplica; `Release()` solo restaura cuando el refcount llega a `0` (el
  ÚLTIMO consumidor en cerrarse) — necesario porque `UpdatePromptVR` y `LicenseBlockScreenVR`
  PUEDEN coexistir (ver `docs/licenciamiento.md`) y si el primero en cerrarse restaurara la
  cámara, la escena reaparecería detrás del cartel que sigue visible. Reset defensivo vía
  `[RuntimeInitializeOnLoadMethod(SubsystemRegistration)]` (limpia el refcount estático al
  arrancar cada sesión de Play, con o sin Domain Reload). Cada consumidor adquiere en su
  `BuildCanvas()` (solo si `Camera.main` existe) y libera en su `OnDestroy()`, 1:1 con el ciclo
  de vida del canvas — ver el helper compartido justifica no duplicar esta lógica dos veces
  (criterio `minimal-footprint`: es el MISMO estado global de cámara con dos consumidores, la
  duplicación sería un bug latente).

### Telemetría (F6)

`UpdateManager` manda un batch de eventos `update_*` a `POST {backend}/api/log`
(`device_id` + `events[{event,detail}]`, ver "Contrato de `/api/log`" abajo) en los puntos
clave del flujo, para que queden visibles en el panel `/admin/logs`. Es **fire-and-forget**:
nunca bloquea ni reintenta el flujo real de update — un fallo de red (backend caído, timeout,
excepción síncrona de `SendWebRequest`) solo se loguea (`Update: telemetria ...`) y se
descarta, igual que el resto de las llamadas de red de esta clase.

- `UpdateLogic.LogEvent` (struct, `Event`+`Detail`) y `UpdateLogic.SerializeLogBatch(deviceId,
  events)` (lógica PURA, testeada en `UpdateLogicTests`) arman el JSON exacto que espera el
  backend. DTOs privados (`LogEventDto`/`LogBatchDto`) con `[JsonProperty("event")]` /
  `[JsonProperty("device_id")]` etc. porque `event` es palabra reservada de C# — se mapea por
  atributo en vez de escapar el identificador con `@event`.
- `UpdateManager.SendTelemetry(params UpdateLogic.LogEvent[])` encola un
  `StartCoroutine(SendTelemetryAsync(...))` por batch — arma la URL/JSON y delega el POST en sí
  a `Simulador.Data.BackendTelemetry.PostJson` (extraído en la tarea de licenciamiento, F3 de
  `docs/licenciamiento.md`, para que `LicenseManager` comparta el mismo cuerpo en vez de
  duplicarlo): `UploadHandlerRaw`/`DownloadHandlerBuffer`, `Content-Type: application/json`,
  mismo timeout que el chequeo de manifest (5 s, batch chico), degradación sin excepción. `device_id`
  es siempre `SystemInfo.deviceUniqueIdentifier` (no `Application.identifier`: eso es el canal,
  no el dispositivo).

| Evento | Cuándo se emite | Detail |
|--------|------------------|--------|
| `update_check` | Una vez por arranque, al terminar de resolver la decisión en `CheckManifest` — **solo si el manifest se obtuvo y parseó** (503/inalcanzable/JSON inválido NO mandan nada: el device puede estar offline, no hay nada que reportar). Se manda incluso con `decision=None`. | `app=<canal> installed=<v> remote=<v> decision=<None\|Optional\|Forced>` |
| `update_prompt_shown` | Junto con `update_check` en el MISMO batch, solo cuando `decision != None` (justo después de `UpdateAvailable?.Invoke(...)`/`MaybeShowVrPrompt`). | `app=<canal> remote=<v> forced=<bool>` |
| `update_accepted` | Al entrar a `UpdateManager.AcceptUpdate()` (botón "Actualizar"/UI), antes de arrancar la descarga. | `app=<canal> version=<manifest.ApkVersion>` |
| `update_postponed` | Al entrar a `UpdateManager.PostponeUpdate()` (botón "Ahora no"/"Cancelar" en la tablet). | `app=<canal> version=<manifest.ApkVersion>` |
| `update_download_ok` | Descarga completada con éxito (antes de la verificación SHA256; el dummy no tiene sha real y esto es "descarga OK" per se). | `bytes=<downloadedBytes> seconds=<elapsed:F1>` |
| `update_download_failed` | Cualquier fallo de la descarga: excepción al crear la carpeta/iniciar la request, `result != Success`, `responseCode != 200`, o error de IO al verificar el SHA256 (`verify_io_error`). | la razón corta (nombre de excepción, `req.result`, `http_<code>` o `verify_io_error`) |
| `update_sha_mismatch` | El hash calculado no matchea `manifest.ApkSha256` (`Sha256Matches` devuelve `false`). | `expected=<hex> actual=<hex>` |
| `update_install_launched` | Al llamar `LaunchInstall()` con `_readyToInstall == true` (pasa el guard), sea cual sea el resultado (`Started`/`PermissionRequested`/`Failed`). | `version=<targetVersion> result=<InstallLaunchResult>` |
| `update_success` / `update_incomplete` | Al arranque siguiente, calculado en `CheckPendingUpdateMarker` (Awake) a partir de `update_pending.json`, pero **enviado recién en `InitializeAsync`** tras el `WaitUntil(BackendConfigReady)` — mandarlo antes reventaría contra el `BackendUrl` default sin resolver. | `expected=<target_version> actual=<Application.version>` |

**Contrato de `POST /api/log`** (backend, ya desplegado): `{"device_id": "<str≤128>", "events":
[{"event": "<str≤64>", "detail": "<str≤2048>"}]}`. Acepta batch, es fire-and-forget del lado
del backend también (acepta `device_id` desconocidos, sin rate limit), y guarda cada evento en
la tabla `update_logs` (visible en el panel `/admin/logs`).

**Gotcha de infra descubierto validando esta tarea**: `Assets/Tests/EditMode/
Simulador.Tests.EditMode.asmdef` ya tenía `"Newtonsoft.Json"` en `references`, pero **eso nunca
funcionó** — el paquete `com.unity.nuget.newtonsoft-json` expone `Newtonsoft.Json.dll` como un
plugin precompilado SIN asmdef propio, y con `"overrideReferences": true` (como tiene este
asmdef) Unity NO autoreferencia plugins precompilados salvo que estén listados en
`"precompiledReferences"`. Ningún test anterior lo notó porque ninguno usaba `Newtonsoft.*` de
verdad (la única mención previa en `DataLogicTests.cs` era un comentario). Fix aplicado:
agregar `"Newtonsoft.Json.dll"` a `"precompiledReferences"` — necesario para que
`UpdateLogicTests.SerializeLogBatch_*` (que parsea el JSON de vuelta con `JObject.Parse`)
compile.

### Contrato del manifest (backend, F1/F2 ya desplegadas)

```
GET {backend}/api/manifest.json?app=visor|tablet&device_id=<opcional>     (sin ?app → visor)
```

Respuesta `200`:

```json
{
  "app": "visor",
  "apk_version": "0.1.0",
  "min_apk_version": "0.1.0",
  "apk_url": "https://...",
  "apk_sha256": "",
  "changelog": "..."
}
```

`503` si no hay versión activa publicada para ese canal (respuesta **esperada**, no un error
del backend). Semver estricto `major.minor.patch`. `apk_sha256` puede venir `""` (dummy
actual) — en ese caso no se verifica nada.

**`device_id` (query param opcional, backend, detalle completo en `docs/backend.md` §OTA
por-dispositivo)**: gate por-dispositivo del OTA del backend — la mayoría de la flota corre en
modo kiosco gestionado por Meta Horizon Managed Services (se actualiza por el Admin Center de
Meta, no por acá) y NO debe recibir el OTA propio; `Device.ota_enabled` (default `False`) decide
por dispositivo. Tabla de decisión:

| Caso | Respuesta |
|---|---|
| `app == "tablet"` | **200** siempre (la tablet no tiene gate de licencia, siempre se actualiza por backend) |
| `device_id` ausente o vacío | **200** (compat retro — ver gotcha abajo) |
| `device_id` existe y `ota_enabled == True` | **200** |
| `device_id` existe y `ota_enabled == False`, o no existe | **503** (mismo status code que el 503 de "sin versión activa"; `UpdateManager` ya trata cualquier 503 como "no hay update", en silencio — nada que cambiar del lado Unity para la SUPRESIÓN en sí) |

**El `device_id` en esta URL lo agrega `UpdateManager.CheckManifest`** (`Assets/Scripts/Runtime/Update/UpdateManager.cs`),
mismo valor que usa `LicenseManager`/telemetría (`SystemInfo.deviceUniqueIdentifier`) —
parámetro opcional aditivo, así que un backend viejo sin este gate lo ignora sin romper nada.
**Gotcha: la fila "`device_id` ausente → 200" es la compatibilidad hacia atrás con el APK 0.6.1
ya instalado en campo**, que todavía no manda este parámetro — si esa fila devolviera 503, esos
visores nunca se enterarían del release que agrega el `device_id`, autobloqueando el mecanismo
que se supone habilita esta feature.

## Decisiones y porqués

- **Fail-safe a `None`** en `Decide` ante cualquier ambigüedad (manifest null, versión remota
  no parseable, paridad, downgrade) → nunca se molesta al usuario con una actualización que
  no corresponde; el peor caso es "no ofrece update" en vez de ofrecer uno inválido.
- **SHA vacío = no verificar** → el manifest dummy no lo manda todavía (pendiente que el
  backend calcule y publique el hash real cuando exista keystore, F6); tratarlo como
  "verificación no aplica" evita leer el archivo entero al pedo cuando no hay nada contra qué
  comparar.
- **Modelo de seguridad de la descarga: `apk_sha256` da INTEGRIDAD, no AUTENTICIDAD**
  (precisado en revisión pre-F7) → verificar el hash contra el manifest solo prueba que el
  archivo descargado es bit-a-bit el que el manifest dijo que era; si un atacante hiciera MITM
  sobre el HTTP del manifest o de la descarga, controla AMBOS — la URL del APK y el hash
  "correcto" contra el que se compara — así que `Sha256Matches` no protege contra un manifest
  falsificado de punta a punta. El ancla de autenticidad real es la **verificación de firma
  del package installer de Android**: un APK solo se instala/actualiza in-place si está
  firmado con el MISMO keystore que la instalación existente (Android rechaza el intent si la
  firma no matchea), y ese keystore (pendiente, ver F6 resto/Pendientes) es del proyecto, no
  del atacante. En resumen: SHA256 protege contra corrupción/descargas parciales; el keystore
  protege contra un APK malicioso. Ambos son necesarios, ninguno sustituye al otro — y por eso
  server HTTPS (no cleartext) en `backend_url` para producción sigue siendo importante aunque
  el keystore ya cierre el vector de instalación maliciosa.
- **Canal por `Application.identifier`, no por escena/plataforma** → visor y tablet
  comparten el mismo endpoint parametrizado por `?app=`; `AppChannelFromIdentifier` es lógica
  pura y testeable, no depende de `#if UNITY_ANDROID` ni de la escena activa. **Esto sigue
  siendo así para el `?app=` del manifest** (`CheckManifest`, correcto en device real: cada
  APK tiene su propio `Application.identifier`). Pero la elección de QUÉ UI mostrar (F5) usa
  una señal DISTINTA — ver el punto siguiente.
- **Selección de UI por presencia de `TabletController` en escena, NO por `identifier` (F5)**
  → en el Editor, `Application.identifier` refleja el `PlayerSettings.applicationIdentifier`
  del build target activo (típicamente `com.simulador.vr`) SIN IMPORTAR qué escena esté
  abierta — abrir `Tablet.unity` en el Editor no cambia el identifier. Si
  `UpdateManager.MaybeShowVrPrompt` hubiera usado
  `UpdateLogic.AppChannelFromIdentifier(Application.identifier) == "visor"` para decidir si
  crear el cartel VR, habría creado un `UpdatePromptVR` DENTRO de la tablet en el Editor (dos
  cartels superpuestos, uno de cada UI). Fix: usa `FindFirstObjectByType<TabletController>()`
  — mismo criterio exacto que `NetworkController.Bootstrap` (ver `docs/tablet.md`) — para
  decidir si la escena actual es "la tablet" (no crear el prompt VR, `TabletController` arma su
  propia `UpdateScreen`) o "el visor" (crear `UpdatePromptVR`). En un device real ambas señales
  coinciden siempre (el identifier del APK instalado determina qué escena/prefabs corren), así
  que esto es puramente una corrección para que el flujo se pueda probar en el Editor con
  cualquiera de las dos escenas abiertas sin resultados cruzados.
- **`CancelDownload()` solo tiene botón en la tablet, no en el cartel VR (F5, deliberado)** →
  la tablet tiene teclado/mouse/touch de sobra para un botón "Cancelar" adicional durante la
  descarga; el cartel VR ya usa las 2 únicas entradas disponibles (A/B del mando) para
  aceptar/rechazar y no hay una tercera acción natural que mapear sin agregar un input nuevo. El
  clínico en VR puede esperar la descarga (rápida, un APK) o simplemente no tocar nada — no se
  pierde funcionalidad crítica, solo una conveniencia. La API pública
  (`UpdateManager.CancelDownload()`) es la misma para ambas UIs; si a futuro se necesita
  cancelar desde el visor, es cablear un botón, no un cambio de arquitectura.
- **SHA256 chunked con `yield return null` en vez de leer todo el archivo de un tirón** → un
  APK puede pesar decenas/cientos de MB; hashearlo de una sola vez congelaría el frame
  (especialmente crítico en Quest, VR). Nada de threads (no-negociable IL2CPP,
  `docs/networking.md` / skill `il2cpp-networking-gotchas`): el hasheo corre en el hilo
  principal pero cede el frame entre bloques.
- **Diff mínimo en `DataManager`** (2 líneas) en vez de duplicar la resolución de
  `backendUrl` en `UpdateManager` → reusa la misma cadena de capas (override > streaming >
  default) que ya usa el sync del catálogo; una sola fuente de verdad para "cuál es el
  backend".
- **Fondo opaco calcado de `LicenseBlockScreenVR`, sin extraer un helper compartido (fix
  posterior a F5) — REVERTIDO en esta tarea, ver el punto siguiente**: el pedido original fue
  que mientras el cartel de update esté visible NO se vea la simulación de fondo. El primer
  intento (documentado acá históricamente) copió el mecanismo de `LicenseBlockScreenVR`: canvas
  world-space MUY cerca de la cámara (`0.15 m`/`0.00015`/`2600x1900`, alpha `1`). Funcionaba
  visualmente en el Editor (game view mono) pero **causaba diplopia real en el Quest** — ver el
  punto siguiente y Gotchas.
- **De canvas opaco pegado al ojo a `CameraSceneOcclusionGate` (rediseño, bug reportado en
  dispositivo real)** → el fix de F5 (arriba) se probó SOLO en el Editor, donde el game view es
  mono y no puede mostrar diplopia; al probarlo en un Quest real, el usuario vio un cartel
  DUPLICADO, uno por ojo, sin fusión estéreo. Causa: un plano a `0.15-0.2 m` de la cámara exige
  una convergencia binocular que el ojo humano no puede sostener cómodamente (la distancia
  interpupilar, ~63 mm, genera una disparidad extrema a esa distancia — ver Gotchas para la
  regla general). Mismo problema, sin validar todavía en dispositivo, en
  `LicenseBlockScreenVR` (mismo mecanismo, `0.2 m`). Fix: **separar las dos responsabilidades**
  que el mecanismo anterior mezclaba en un solo parámetro (posición del canvas) — "a qué
  distancia se ve la UI" (debe ser cómoda para los ojos, `1.5-2 m`) y "qué tanto de la escena de
  fondo se oculta" (debe ser TOTAL, sin depender de cuán grande sea el canvas ni de qué
  geometría de cockpit haya más cerca). La primera se resuelve devolviendo el canvas a
  `1.5 m`/`0.0015`/`700x420` (los valores que ya se sabía que fusionaban bien, antes del fix
  roto). La segunda se resuelve a nivel de **cámara**, no de geometría:
  `CameraSceneOcclusionGate` (`Assets/Scripts/Runtime/Data/`, ver arriba) restringe el
  `cullingMask` de `Camera.main` a la capa `UI` + `clearFlags = SolidColor` mientras cualquiera
  de los dos cartels esté visible — así NINGUNA geometría de la escena llega a rasterizarse,
  sin importar la distancia/tamaño del canvas de UI. Este helper SÍ se extrajo compartido (a
  diferencia de la decisión anterior de esta misma sección, que evitó extraer un helper de
  geometría): acá el criterio es distinto — no es "la misma fórmula aplicada dos veces sobre
  contenido distinto", es **el mismo estado global mutable (la cámara)** con dos consumidores
  que pueden coexistir; duplicar el guardado/restaurado de `cullingMask`/`clearFlags`/
  `backgroundColor` en dos clases sería un bug latente esperando pasar (un consumidor pisando el
  estado guardado del otro) — la skill `minimal-footprint` señala esto explícitamente como caso
  donde SÍ se justifica el helper compartido. `UpdatePromptVR` sigue un poco más CERCA que
  `LicenseBlockScreenVR` (`1.5 m` vs `2.0 m`) — mismo criterio que antes del rediseño (ganar el
  orden de dibujado en la cola transparente si ambos coexisten), solo que ahora sin el riesgo de
  diplopia porque ninguno de los dos está pegado al ojo. Ninguno de los dos cartels destruye el
  GameObject del otro (son independientes, cada uno limpia solo el suyo en su propio
  `Close()`/`OnDestroy()`) ni interfiere con el guard anti-restore de
  `LicenseBlockScreenVR.Update()` (sin cambios ahí — ver `docs/licenciamiento.md`).

## Gotchas

- **NUNCA un panel de UI world-space opaco a menos de `~0.5 m` de la cámara en VR — causa
  diplopia real, NO detectable en el Editor (bug reportado en dispositivo real, esta tarea)**:
  un canvas world-space muy cerca del ojo (`0.15-0.2 m`, el mecanismo que tenían
  `UpdatePromptVR`/`LicenseBlockScreenVR` antes de este rediseño) exige que ambos ojos converjan
  en un punto extremadamente cercano — la distancia interpupilar humana (~63 mm) genera a esa
  distancia una disparidad binocular que el cerebro no puede fusionar cómodamente: el usuario ve
  DOS carteles superpuestos/desdoblados en vez de uno solo. El game view del Editor es MONO (una
  sola cámara, sin renderizar los dos ojos por separado), así que este bug es **invisible en
  toda validación por captura de pantalla en el Editor** (F5/F3 se dieron por buenas con
  capturas que se veían perfectas) — solo aparece con un HMD real puesto. Regla general: la UI
  world-space en VR va a distancia estéreo cómoda (`~1.5-2 m`, zona de fusión binocular normal
  para lectura/UI); si hace falta ocultar la escena de fondo detrás de un cartel modal, NO se
  resuelve acercando el canvas al ojo — se resuelve a nivel de cámara
  (`Simulador.Data.CameraSceneOcclusionGate`, ver Arquitectura arriba: `cullingMask` restringido
  + `clearFlags = SolidColor`). Cualquier cartel/HUD world-space nuevo en el visor debe revisar
  esta regla ANTES de elegir su distancia.
- **`503` es un caso NORMAL, no un error**: significa "no hay versión activa publicada para
  este canal todavía" (p. ej. recién desplegado el backend, sin release cargado por el
  panel). Se loguea con `Debug.Log` (no `LogWarning`/`LogError`) y termina en silencio, igual
  que cualquier otro código no-200.
- **`UpdateManager.BackendUrl` depende de que `DataManager` ya resolvió sus capas de
  config** — por eso el `WaitUntil(() => DataManager.Instance != null &&
  DataManager.Instance.BackendConfigReady)` antes de cualquier chequeo. Si se llama al check
  de manifest antes de tiempo, se leería el `backendUrl` con el default serializado en vez
  del efectivo (override/streaming).
- **La descarga del APK NO tiene `timeout`** a propósito: es un archivo grande y una descarga
  lenta pero progresando no debería cortarse por un timeout corto (a diferencia del chequeo
  de manifest, que sí tiene 5 s porque es un JSON chico).
- **`StopCoroutine` NO dispone el `using var req` de la corrutina cortada** (hallado en
  revisión pre-F7): parar una corrutina de Unity a mitad de camino no ejecuta el resto del
  método ni sus `using`/`finally` pendientes — el `UnityWebRequest` de una descarga vieja
  seguía vivo y podía seguir escribiendo el `ApkPath` en paralelo con una descarga nueva si
  `StartDownload()` solo hacía `StopCoroutine` (como antes de este fix) en vez de abortar la
  request explícitamente. Fix: `StartDownload()`/`CancelDownload()` comparten
  `AbortActiveDownload()` (`_activeDownloadReq.Abort()` + `StopCoroutine` + limpiar el
  parcial) — cualquier corte de una descarga en curso pasa siempre por ahí, nunca por un
  `StopCoroutine` suelto. `UpdateManager.OnDestroy()` hace lo mismo (abort+dispose) por si el
  singleton se destruye con una descarga en vuelo.
- **`UpdatePromptVR.SubscribeToManager()` es idempotente** (`_subscribedToManager`, fix
  pre-F7): si `Show()` se llama de nuevo sin haber pasado por `Close()`/`OnDestroy()` antes
  (p. ej. un segundo `UpdateAvailable` mientras el cartel ya está visible), NO vuelve a
  suscribirse — evita handlers duplicados en `DownloadProgress`/`UpdateFailed`/`ReadyToInstall`
  (que habrían disparado `OnDownloadProgress` etc. dos veces por evento). Mismo criterio que ya
  tenía `EnableOwnInput` (`if (_a != null) return`).
- **F5 cierra el círculo pero NO valida el intent de instalación en device real**: el botón
  "Instalar" (tablet) y "A: instalar" (VR) llaman `LaunchInstall()` de punta a punta, pero en
  el Editor eso cae siempre al no-op de `UpdateInstaller` (`Update: LaunchInstall no-op fuera
  de Android (Editor)`). El intent real (`ACTION_VIEW` + `FileProvider` + el reintento tras el
  permiso de "fuentes desconocidas") solo se puede probar en un build instalado (F7).
- **Smoke de F5 en el Editor con la ventana SIN foco de SO (`Application.isFocused == false`)
  es MUY lento y `unity_screenshot_game`/`ScreenCapture.CaptureScreenshot` puede devolver un
  frame CACHEADO/viejo en vez del estado actual** — descubierto validando esta tarea: con el
  Editor en segundo plano, Unity throttlea el player loop agresivamente (se observaron
  descargas de un APK de 2 MB tardando minutos de reloj en vez de segundos, y `Time.frameCount`
  congelado varios segundos reales entre ticks). Un `CaptureScreenshot` pedido inmediatamente
  después de cambiar de estado casi siempre devuelve el frame anterior (mismo hash de archivo
  que la captura previa) porque el Editor no llegó a renderizar uno nuevo. Mitigación que
  funcionó de forma consistente: forzar `EditorWindow.GetWindow(typeof(GameView), ...).Repaint()`
  y esperar ~15s de reloj REAL (no solo "el próximo frame") antes de pedir la captura; para
  verificar el estado de la UI sin depender de una captura visual, leer los campos privados por
  reflection (`_updateTitleLabel.text`, `_state`, etc.) es más confiable y no depende del
  render. Un `Destroy()` (limpieza de `UpdatePromptVR.Close()`) también puede tardar bloques de
  tiempo real enteros en aplicarse por el mismo motivo. Nada de esto es un bug del código de
  update: es una característica del Editor sin foco, no reproducible en un build real (donde la
  app siempre tiene foco).
- **`.meta` de `Update/` los genera el Editor** (refresh vía MCP), igual que cualquier
  carpeta nueva bajo `Assets/`.
- **Contenido de `SimuladorUpdate.androidlib/` sin `.meta` propio — es el comportamiento
  ESPERADO de Unity 6, no un olvido**: desde Unity 2023.1 el Editor deja de generar `.meta`
  para los archivos DENTRO de carpetas `.androidlib`/`.bundle`/`.framework`/`.plugin` (evita
  el ruido de metas auto-generados que antes rompían el merge de Gradle); solo la carpeta
  raíz (`SimuladorUpdate.androidlib/`) tiene `.meta` propio (`PluginImporter`). No crear
  `.meta` a mano para `AndroidManifest.xml`/`file_paths.xml` de esta carpeta — sería
  redundante con el comportamiento del Editor y potencialmente conflictivo.
- **Versión de `androidx.core` — VERIFICADO en el primer build real post-F4 (2026-07-09,
  @build-deploy)**: no hizo falta ninguna dependencia manual. Unity 6 resolvió
  `androidx.core.content.FileProvider` solo a partir del `<provider>` declarado en el manifest;
  confirmado en AMBOS APKs (visor y tablet) extrayendo `classes.dex` y comprobando la presencia
  de la cadena de clase `androidx/core/content/FileProvider` (`grep -a -c` sobre el dex, 1
  ocurrencia en cada uno). No se necesitó Gradle template custom ni External Dependency
  Manager — el plan B documentado abajo no aplicó.
- **Permiso `REQUEST_INSTALL_PACKAGES` es "normal" en manifest, pero el `canRequestPackageInstalls()`
  en runtime depende de que el usuario lo conceda por Settings** — es un permiso especial
  (no se pide con el diálogo estándar de runtime permissions); por eso `UpdateInstaller` abre
  `ACTION_MANAGE_UNKNOWN_APP_SOURCES` en vez de `ActivityCompat.requestPermissions`.

## Cómo probar

1. **EditMode**: `Simulador → Run EditMode Tests` (o Test Runner) → `UpdateLogicTests` debe
   quedar verde junto al resto de la suite (`DataLogicTests`, `DataManagerLogicTests`,
   `PairingStoreTests`) — 57/57 al cerrar F4.
2. **F3 real contra el backend desplegado** (chequeo + descarga): en el Editor, con Play Mode
   y el backend apuntado (según la config de capas de `docs/catalogo-lentes.md`), verificar en
   consola:
   - `Update: chequeando manifest -> {url}?app=visor` (o `tablet` según
     `Application.identifier` del build/Editor).
   - Si el backend tiene una versión activa mayor a `Application.version`: `Update: manifest
     visor vX.Y.Z disponible (actual A.B.C), decision=Optional|Forced`.
   - Llamar manualmente `UpdateManager.Instance.AcceptUpdate()` (consola de scripting o un
     botón temporal) y verificar `DownloadProgress` avanzando hasta 1.0, luego
     `ReadyToInstall` con el path en `persistentDataPath/updates/simulador-update.apk`.
   - Forzar un fallo (backend caído, URL inválida) y verificar `UpdateFailed` con mensaje +
     que el archivo parcial se borró.
3. **F4 en el Editor (smoke, sin JNI real)**: llamar `UpdateManager.Instance.LaunchInstall()`
   después de un `ReadyToInstall` — en el Editor cae al no-op de `UpdateInstaller` (rama
   `#else`) y loguea `Update: LaunchInstall no-op fuera de Android (Editor). apkPath=...`; sirve
   para confirmar el guard de `_readyToInstall` (llamarlo ANTES de `ReadyToInstall` debe loguear
   `Update: LaunchInstall llamado sin un APK listo para instalar.` y no hacer nada más).
4. **F4 en dispositivo real (Quest o tablet, requiere build post-F4)**: instalar el APK
   actual, forzar (vía consola de scripting, hasta que exista F5) `AcceptUpdate()` →
   `LaunchInstall()` tras `ReadyToInstall`, y verificar:
   - Primera vez (sin el permiso de fuentes desconocidas concedido): se abre el ajuste
     "Instalar apps desconocidas" del sistema para esta app; conceder el permiso, volver a la
     app (Home/back) y confirmar en logcat que se reintenta solo y esta vez lanza el
     instalador (`adb logcat -s Unity` → `Update: intent de instalacion lanzado para ...`).
   - Con el permiso ya concedido: el instalador de paquetes de Android se abre directo sobre
     el APK.
   - Tras instalar y reabrir la app: `adb logcat -s Unity` debe mostrar `Update: update
     aplicado OK (X)` (o `incompleto` si algo falló) y que `update_pending.json` desapareció
     de `persistentDataPath`.
5. **F5 en el Editor (smoke, ambas UIs)**: publicar una versión de prueba en el panel
   (`app=visor`, cualquier `apk_version` mayor a `Application.version`) — como
   `Application.identifier` en el Editor SIEMPRE resuelve al canal del build target activo sin
   importar la escena (ver Decisiones "Selección de UI por presencia de TabletController"), UNA
   sola versión publicada alcanza para probar las DOS escenas.
   - **`Tablet.unity`**: Play → esperar el check de manifest → debe aparecer `UpdateScreen`
     ("Actualización disponible", versión, changelog, Actualizar/Ahora no). Tocar "Actualizar"
     → progreso "Descargando… NN %" + "Cancelar". Dejarlo terminar → "Descarga verificada" +
     "Instalar" → tocarlo → `Update: LaunchInstall no-op fuera de Android (Editor)` en consola
     y el overlay se cierra. Repetir cancelando a mitad de descarga → `Update: descarga
     cancelada por el usuario.` sin que se dispare `UpdateFailed`.
   - **`Main.unity`**: Play → debe aparecer el cartel VR (canvas world-space frente a la
     cámara, visible incluso sin headset conectado) con la leyenda "A: actualizar B: ahora no".
     Sin un XR device real las `InputAction` de A/B no van a disparar solas: simular vía
     `unity_execute_code` → `Simulador.Update.UpdateManager.Instance.AcceptUpdate()` (dispara la
     descarga real; el cartel VR se actualiza solo porque está suscripto a los mismos eventos)
     y, si hace falta forzar el estado exacto de la UI (no solo la descarga), invocar por
     reflection los métodos privados de `UpdatePromptVR`/`TabletController`
     (`OnUpdateAvailable`/`ShowUpdateDownloading`/`OnUpdateReadyToInstall`/`OnUpdateFailed`) —
     ver el gotcha de arriba sobre por qué las capturas de pantalla pueden mentir en el Editor
     sin foco y por qué leer los campos por reflection es más confiable para verificar texto/
     estado. Confirmar que `Vision.SimuladorInput` queda `enabled = false` mientras el cartel
     está visible y vuelve a `true` tras cerrarlo (instalar/postergar/error-cerrar).
5-bis. **`CameraSceneOcclusionGate` (smoke real hecho al cerrar el rediseño de esta tarea,
   `Main.unity`, escenario `ruta_noche`)** — sin depender de un backend/manifest real,
   `unity_execute_code` con `runInBackground=true` (evita el throttle del Editor sin foco, ver
   gotcha de arriba):
   - Instanciar `UpdatePromptVR` a mano (`gameObject.AddComponent<...>()`) y llamar
     `Show(manifest, forced:false)`: `Camera.main.cullingMask` pasó de `-1` a `32` (`1 <<
     LayerMask.NameToLayer("UI")`), `clearFlags`/`backgroundColor` al color sólido oscuro, y
     `unity_graphics_game_capture` confirmó la escena TOTALMENTE oculta (ni cockpit ni halos
     visibles) con el cartel a distancia estéreo cómoda (tarjeta normal, ya no un plano gigante
     pegado al ojo).
   - **Coexistencia**: con el cartel de update todavía visible, instanciar
     `LicenseBlockScreenVR` y llamar `Show(...)` — la captura mostró AMBOS cartels superpuestos,
     el de update (más cerca, `1.5 m`) ganando el orden de dibujado sobre el de licencia (`2.0
     m`), confirmando la regla de distancias. Cerrar `UpdatePromptVR` primero (`Close()` por
     reflection): la cámara quedó IGUAL de oculta (`cullingMask` seguía en `32`) porque
     `LicenseBlockScreenVR` seguía activo — el refcount no restauró de más. Recién al destruir
     también `LicenseBlockScreenVR` (`Destroy(componente)`, mismo patrón que
     `LicenseManager.Unblock()`) la cámara volvió EXACTO al estado original
     (`cullingMask=-1`, `backgroundColor` original) y la captura mostró la escena `ruta_noche`
     restaurada al 100 % (cockpit, halos de disability glare, HUD de debug).
   - No se validó el flujo `Forced`/descarga real completo (requeriría backend con un manifest
     publicado; ver paso 6/7 para eso) ni la diplopia en sí misma (no reproducible en el
     Editor, ver Gotchas) — la validación final de que el fix realmente resuelve la diplopia
     queda para el usuario en un Quest real (ver Gotchas y la nota al pie de esta sección).
6. **En dispositivo real (Quest o tablet, requiere build post-F5)**: instalar el APK actual,
   publicar una versión nueva en el panel del backend, abrir la app y verificar el cartel de
   update correspondiente (tablet: `UpdateScreen`; visor: `UpdatePromptVR` con input real de
   controller) + que confirmar instalar dispara el flujo de intent de instalación de punta a
   punta (ver paso 4).
7. **Telemetría (F6) en el Editor contra un backend local** (`docker compose up` en
   `backend/`): apuntar el override de `persistentDataPath/config.json` (`{"backend_url":
   "http://localhost:8080"}`, ver capas en `docs/catalogo-lentes.md`) a ese backend, Play en
   `Main.unity` o `Tablet.unity` y verificar en la BD (`docker compose exec db psql -U
   simulador -d simulador -c "select device_id,event,detail from update_logs order by id desc
   limit 10;"`, o logueado como admin en `/admin/logs`) que aparece una fila `update_check` con
   el `device_id` de esta corrida. Para forzar el resto de los eventos sin publicar una versión
   nueva real, `unity_execute_code` → `Simulador.Update.UpdateManager.Instance.AcceptUpdate()` /
   `.PostponeUpdate()` alcanza (llaman al método público real, generan telemetría real aunque
   no haya manifest pendiente). Borrar el override al terminar — no es config de producción.

## Pendientes / deuda

- **Cancelar descarga solo existe en la tablet, no en el cartel VR** (deliberado, ver
  Decisiones) — si a futuro se necesita, es cablear un tercer estado de input en
  `UpdatePromptVR`, la API (`UpdateManager.CancelDownload()`) ya existe.
- **F5 no valida el intent de instalación real** (solo el no-op de Editor) — eso es F7.
- **F6 (resto) — RESUELTO (2026-07-09, @build-deploy)**: keystore propio del proyecto generado
  (`keystore/simulador.keystore`, gitignorado — detalle completo en `docs/builds-deploy.md`
  §Firma (keystore) del proyecto) y usado para firmar las primeras builds update-capable
  (visor `com.simulador.vr` y tablet `com.simulador.tablet`, ambas verificadas con `apksigner
  verify --print-certs` mostrando `CN=Simulador LIOs, O=TFM, C=UY`). **Sigue pendiente**: estos
  APKs 0.1.0/vc1 son builds locales de referencia, todavía NO subidas al panel del backend
  (eso es F7) — `apk_sha256` en el manifest dummy sigue vacío hasta que exista un release real
  publicado con el hash del APK firmado.
- **`CancelDownload()` no manda telemetría propia** — no está en la lista de eventos F6
  (deliberado, alcance acordado); si se necesita a futuro, es un `SendTelemetry` más en ese
  método, mismo patrón que el resto.
- **F7** — validación end-to-end en dispositivos reales (Quest + tablet) con un release
  publicado de punta a punta por el panel del backend, incluyendo confirmar en `/admin/logs`
  que los eventos de un dispositivo real (no el Editor) llegan correctamente.
- **`CameraSceneOcclusionGate` — el rediseño en sí NO fue validado en Quest real todavía**
  (esta tarea partió de un reporte de diplopia en dispositivo, pero el fix se validó solo en
  Editor por reflection/captura, ver "Cómo probar" 5-bis — el Editor no puede reproducir
  diplopia porque el game view es mono). Pendiente que el usuario confirme en la próxima build
  que: (a) el cartel de update y el de licencia se fusionan correctamente en estéreo a las
  nuevas distancias (`1.5 m`/`2.0 m`), y (b) la escena de fondo queda completamente oculta
  mientras cualquiera de los dos esté visible (sin fugas de geometría por los bordes del FOV,
  que con el mecanismo de cámara no deberían poder existir, a diferencia del canvas-plano
  anterior que dependía de cubrir el FOV con tamaño).
