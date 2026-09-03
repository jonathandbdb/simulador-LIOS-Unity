# Builds y deploy

## Qué es y por qué

Pipeline de compilación e instalación de las tres piezas del simulador: el APK del **visor** (Meta Quest, VR con OpenXR), el APK de la **tablet** de control (Android plano, sin VR) y el **backend** (Docker Compose). Visor y tablet salen del mismo proyecto Unity y comparten el build target Android, por lo que el punto crítico es la gestión del loader OpenXR: activo para el visor, apagado para la tablet.

## Arquitectura actual

| Archivo | Rol |
|---------|-----|
| `Assets/Scripts/Editor/TabletBuild.cs` | Build script dedicado de la tablet: apaga el loader OpenXR, buildea `Tablet.unity` y restaura el loader al terminar. Expone `IsTabletBuildInProgress` (gate para `TabletBootConfigPatcher`, ver fila siguiente). |
| `Assets/Scripts/Editor/TabletBootConfigPatcher.cs` | `IPostGenerateGradleAndroidProject` (`callbackOrder = 9999`, corre último): borra del `boot.config` ya generado toda línea `xr-*` que el hook de OpenXR haya escrito, SOLO si `TabletBuild.IsTabletBuildInProgress` — fix determinista del gotcha del teclado (ver Gotchas). |
| `Assets/Scripts/Editor/TabletManifestPatcher.cs` (nuevo, Fase A kiosco) | `IPostGenerateGradleAndroidProject` (`callbackOrder = 9998`, sin relación de orden real con `TabletBootConfigPatcher` — editan archivos distintos del proyecto Gradle generado), gateado igual por `TabletBuild.IsTabletBuildInProgress`. Edita `unityLibrary/src/main/AndroidManifest.xml` YA GENERADO (con `System.Xml.Linq`, idempotente) con TRES inyecciones: (1) un segundo `<intent-filter>` MAIN+HOME+DEFAULT a la Activity de Unity (sin tocar el LAUNCHER existente); (2) un `<receiver>` `SimuladorDeviceAdminReceiver` con su `<meta-data>` (`@xml/device_admin`) e intent-filter de `DEVICE_ADMIN_ENABLED`/`PROFILE_PROVISIONING_COMPLETE`; (3) un `<receiver>` `InstallResultReceiver` (Fase C, updates silenciosos — `android:exported="false"`, sin permiso especial, recibe el `PendingIntent` del commit de `PackageInstaller` que lanza `SilentInstaller.java`, ver `docs/updates.md` §"Instalación silenciosa en kiosco (F8)"). Detalle completo de (1)/(2) en §"Provisión de tablets (Device Owner)" más abajo. |
| `Assets/XR/XRGeneralSettingsPerBuildTarget.asset` | Config XR por build target. El bloque "Android Providers" tiene `m_Loaders` apuntando al loader OpenXR (guid `0613ddada2fe14947a9b75e90912b7ba`). |
| `Assets/XR/Loaders/OpenXRLoader.asset` | El loader OpenXR que se activa/desactiva. |
| `Assets/XR/Settings/OpenXR Package Settings.asset` | Configuración del paquete OpenXR (features, interaction profiles). |
| `ProjectSettings/EditorBuildSettings.asset` | Lista de escenas del build normal: solo `Assets/Scenes/Main.unity` (visor). También registra el config object XR bajo la key `com.unity.xr.management.loader_settings`, la misma que lee `TabletBuild.GetAndroidXrManager()`. |
| `ProjectSettings/ProjectSettings.asset` | `companyName: Simulador`, `productName: Simulador`, `applicationIdentifier` Android: `com.simulador.vr` (visor). La tablet usa `com.simulador.tablet`/"Simulador Tablet", seteados SOLO durante `TabletBuild` y restaurados al terminar (P6.7, ver abajo) — este archivo nunca queda con los valores de la tablet. También guarda el icono default del proyecto (`PlayerSettings.SetIcons(NamedBuildTarget.Unknown, ...)`, serializado en `m_BuildTargetIcons` con `m_BuildTarget` vacío): es el icono del **visor** (`Assets/Textures/Icons/icon_visor.png`) — Android lo hereda como fallback salvo que tenga icono propio seteado (ver fila siguiente y `TabletBuild`). |
| `Assets/Textures/Icons/icon_visor.png`, `Assets/Textures/Icons/icon_tablet.png` | Iconos 1024×1024 full-bleed (sin alpha), regenerados 2026-09-03 con el logo real (ver Decisiones "Logo real (2026-09-03)" en `docs/tablet.md`): el símbolo de marca (esfera azul con gradiente radial + dos anillos elípticos diagonales, gris azulado) centrado al ~62 % del lado — visor sobre fondo azul marino `#0B2A4A`, tablet sobre fondo blanco `#FFFFFF` (se distinguen si conviven instalados en un dispositivo de desarrollo). Sin texto (ilegible a 48 px, el tamaño real de un ícono de app). Fuente vectorial en `Assets/Textures/Icons/src/iolsimulator-mark.svg`. El default del proyecto (todas las plataformas que no tengan override) es el del visor; `TabletBuild` pisa el de Android con el de tablet SOLO durante su build (ver abajo). |
| `backend/docker-compose.yml` + `backend/Caddyfile` | Deploy del backend (detalle en `docs/backend.md`). |
| `README.md` (raíz, sección 3) | Instrucciones de instalación para humanos; este doc es la referencia operativa. |

### Matriz visor vs tablet

| | **Visor (Quest)** | **Tablet (Android)** |
|---|---|---|
| Escena incluida | `Assets/Scenes/Main.unity` (única en EditorBuildSettings) | `Assets/Scenes/Tablet.unity` (pasada explícitamente por el script; NO está en EditorBuildSettings) |
| Loader OpenXR | **ON** (estado normal del proyecto) | **OFF** solo durante el build (`.asset` vía `SetLoaders` **y** cache runtime vía `TrySetLoaders`); restaurado después |
| Flags `xr-*` en `boot.config` | Presentes (el visor los necesita: late latching, keyboard overlay, etc.) | Borrados post-build por `TabletBootConfigPatcher` — ver Gotchas del teclado (fix determinista, no un toggle en memoria) |
| GraphicsAPI (Android) | **Vulkan** (`m_APIs: 15000000`, requerido por el visor Quest) | **OpenGLES3** solo durante el build (driver Vulkan roto en tablets Unisoc/Mali, ver Gotchas); restaurado después |
| Método de build | Build normal de Unity para Android (*File → Build Profiles / Build Settings*) | **SOLO** menú `Simulador → Build Tablet (Android)` o `-executeMethod Simulador.EditorTools.TabletBuild.BuildTablet` (batchmode) |
| Ruta de salida | La que elija el usuario (localmente existen `builds/Simulador_VR.apk` y `build/Simulador.apk`, ambas carpetas gitignoradas) | `Builds/Android/Simulador.apk` (constante `OutputPath` en `TabletBuild.cs`) |
| Package | `com.simulador.vr` | `com.simulador.tablet` (P6.7, CERRADO — antes compartía `com.simulador.vr` con el visor; ver Decisiones/Gotchas) |
| Product name | `IOLSIMULATOR` (Project Settings, marca visible desde 2026-09-03 — ver Decisiones `docs/tablet.md`) | `IOLSIMULATOR Tablet` (seteado/restaurado solo durante el build, igual que el package) |
| Icono | `icon_visor.png` (es el default del proyecto, target `Unknown`; Android lo hereda si no tiene override) | `icon_tablet.png` (seteado en los slots de Android SOLO durante `TabletBuild`, restaurado al terminar — mismo patrón try/finally que package/nombre) |
| Scripting backend | IL2CPP / arm64-v8a, min SDK 29 (según `README.md`) | Idéntico (mismo target compartido) |
| Manifest: kiosco (HOME + DeviceAdminReceiver) | Ausente (el `.java`/`.xml` compilan igual pero el manifest del visor no los declara — inertes) | Inyectado post-Gradle por `TabletManifestPatcher.cs`, gateado por `IsTabletBuildInProgress` (Fase A, ver `docs/tablet.md`) |

### Flujo del build de tablet (`TabletBuild.BuildTablet()`)

```
¿target activo == Android? --no--> LogError + return null (no toca nada, ni loaders ni identifier)
        | sí
GetAndroidXrManager()  ← lee XRGeneralSettingsPerBuildTarget vía EditorBuildSettings
guardar loaders actuales (SerializedObject "m_Loaders") + como List<XRLoader> para TrySetLoaders
guardar GraphicsAPIs(Android) + UseDefaultGraphicsAPIs(Android)
guardar applicationIdentifier (NamedBuildTarget.Android) + productName actuales   (P6.7)
guardar platform icons de Android por kind (GetPlatformIcons(Android, Legacy/Round/Adaptive))
try:
    IsTabletBuildInProgress = true          ← gate de TabletBootConfigPatcher (ver más abajo)
    SetLoaders(manager, lista vacía)        ← XR OFF en el .asset
    manager.TrySetLoaders(lista vacía)      ← XR OFF en la cache runtime (activeLoaders) — sin esto queda stale
    SetGraphicsAPIs(Android, [OpenGLES3]) + SetUseDefaultGraphicsAPIs(Android, false)   ← driver Vulkan roto en Unisoc/Mali, ver Gotchas
    SetApplicationIdentifier(Android, "com.simulador.tablet") + productName = "Simulador Tablet"
    SetPlatformIcons(Android, Legacy/Round/Adaptive, icon_tablet.png en todas las capas)  ← icono propio (NO IconKind.Application, ver Gotchas — esa API generica no tiene efecto en Android)
    BuildPipeline.BuildPlayer(Tablet.unity → Builds/Android/Simulador.apk)
        └─ durante la generación del proyecto Gradle, Unity llama a TODOS los
           IPostGenerateGradleAndroidProject registrados, en orden de callbackOrder;
           TabletManifestPatcher (callbackOrder 9998) inyecta en el AndroidManifest.xml
           ya generado el intent-filter HOME + el <receiver> SimuladorDeviceAdminReceiver
           del kiosco (Fase A, ver docs/tablet.md) — sin relación de orden con el siguiente;
           TabletBootConfigPatcher (callbackOrder 9999, corre último) borra del
           boot.config ya escrito toda línea "xr-*" — ver Gotchas del teclado
finally:
    IsTabletBuildInProgress = false
    SetLoaders(manager, loaders guardados) + manager.TrySetLoaders(loaders guardados)   ← XR ON de nuevo (.asset y cache), SIEMPRE
    SetGraphicsAPIs(Android, guardado) + SetUseDefaultGraphicsAPIs(Android, guardado)   ← Vulkan de nuevo, SIEMPRE
    SetApplicationIdentifier(Android, guardado) + productName = guardado   ← SIEMPRE (P6.7)
    SetPlatformIcons(Android, Legacy/Round/Adaptive, icons guardados)      ← SIEMPRE (icono del visor de nuevo, heredado del default)
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

### Provisión de tablets (Device Owner)

Procedimiento para dejar una tablet lista para salir a una clínica sin volver a tocarla: modo
kiosco vía **Android Device Owner** (Fase A del pedido de "vender bundles Quest + tablet sin
volver a tocar los dispositivos"; el porqué de elegir Device Owner en vez de otro mecanismo vive
en `docs/tablet.md` Decisiones "Kiosco vía Android Device Owner"). Fase B (WiFi desde la propia
app) y Fase C (updates silenciosos, QR provisioning) son requisitos posteriores que ya dejan el
terreno preparado (`SimuladorDeviceAdminReceiver.onProfileProvisioningComplete`, ver
`docs/tablet.md`) pero NO son parte de este procedimiento.

Pensado para que **cualquier dev con una PC** (no hace falta Unity ni un build local) pueda
provisionar una tablet nueva con `scripts/provision-tablet.sh`: por defecto descarga el APK
publicado en el backend (mismo manifest que usa el OTA, `docs/updates.md`), lo verifica por
SHA256 y recién ahí instala.

#### Qué tablet comprar

- **Android 10 o superior** (preferible **13+** — es lo validado hoy).
- **Nueva de fábrica, o factory-reseteada sin ninguna cuenta agregada** — `dpm set-device-owner`
  exige cero cuentas.
- Con **Opciones de desarrollador / Depuración USB** habilitables (todas las Android estándar la
  tienen; algunas MDM corporativas la bloquean — evitarlas).
- **WiFi de 5 GHz recomendado** — el streaming por ojo entre visor y tablet pesa, y 2.4 GHz en un
  entorno con muchas redes (clínica, congreso) degrada la latencia visible.
- **10" o más, USB-C** (para carga/depuración con un solo tipo de cable).
- **Evitar Fire OS de Amazon** (no es Android Enterprise, `dpm set-device-owner` no aplica igual)
  **y ROMs con "espacio dual"/"app twin"** de algunos fabricantes chinos (duplican el package
  manager y confunden a `dpm`).
- **Validado en campo:** PHILCO TP10A464 (Android 13) — primera provisión real, 2026-09-03 (ver
  gotchas más abajo).
- **Recomendadas si hay que comprar sin poder probar antes:** Samsung Galaxy Tab A9+ o Lenovo Tab
  M11 — ambas son **Android Enterprise Recommended** (Google certifica que `dpm
  set-device-owner`/lock task funcionan sin sorpresas de fabricante).

#### Requisitos de la PC del operador

- Un shell con bash: **Git Bash** en Windows 11, o Linux/macOS.
- `adb` (Android platform-tools) y `curl` disponibles. Si `adb` no está en el PATH, el script
  busca `adb.exe` en `$LOCALAPPDATA/Android/Sdk/platform-tools/`, `/c/Android/platform-tools/` y
  `$HOME/Android/Sdk/platform-tools/`; si no aparece ahí tampoco, hay que instalar
  [Android SDK Platform-Tools](https://developer.android.com/tools/releases/platform-tools).
- **Internet en la PC** (para bajar el APK del backend) — salvo que se use `--apk <ruta>` con un
  build local. **La tablet NO necesita WiFi para provisionarse.**
- Un cable USB de **datos** (no solo carga) para conectar la tablet.

#### Preparación de la tablet

1. Si la tablet no está de fábrica: hacerle un **reseteo de fábrica**.
2. Al pasar el asistente inicial de Android: **saltear el inicio de sesión, sin agregar ninguna
   cuenta** (Google ni de fabricante) — `dpm set-device-owner` exige cero cuentas.
3. **Ajustes → Acerca de la tablet →** tocar 7 veces **"Número de compilación"** (activa
   Opciones de desarrollador).
4. **Ajustes → Sistema → Opciones de desarrollador →** activar **"Depuración USB"**.
5. Conectar el cable USB a la PC. En el diálogo que aparece **en la tablet**, tocar **"Permitir
   siempre desde esta computadora"** y aceptar.

(El propio script imprime esta misma checklist en pantalla si no detecta ninguna tablet en los
primeros 15 segundos, y sigue esperando hasta 5 minutos antes de abortar.)

#### La orden

```bash
scripts/provision-tablet.sh                        # todo por defecto: descarga del backend,
                                                     # provisiona, reinicia y verifica
scripts/provision-tablet.sh --serial <serial>       # si hay más de un dispositivo conectado
```

El flujo por defecto: descarga y verifica el APK (manifest `GET
https://vr.conecta.sh/api/manifest.json?app=tablet` → `apk_url` + `apk_sha256`) → busca la
tablet por adb (checklist si tarda) → verifica que no tenga cuentas → `adb install -r` → `dpm
set-device-owner com.simulador.tablet/com.simulador.kiosk.SimuladorDeviceAdminReceiver` →
`appops` + `immersive_mode_confirmations` (ver gotchas abajo) → lanza la app con un intent HOME
explícito → verifica HOME persistente y `type=home` → **reinicia la tablet** y confirma en vivo
que arranca directo en la app, en foco (`mCurrentFocus`) y con el kiosco activo
(`mLockTaskModeState=LOCKED`) — exactamente lo que le va a pasar al paciente/clínico al prender
la tablet en la clínica. Cualquier paso que falle corta con `exit 1` y un mensaje explicando qué
pasó y qué hacer.

Variantes (detalle completo en `scripts/provision-tablet.sh --help`):

| Flag | Para qué |
|------|----------|
| `--backend <url>` | Backend alternativo (default `https://vr.conecta.sh`). |
| `--apk <path>` | Modo desarrollador: usa un APK local en vez de descargar (ej. un build recién generado con `Simulador → Build Tablet (Android)`). |
| `--download-only` | Solo descarga + verifica el APK e imprime su ruta — no toca ningún dispositivo (sirve para probar la conexión al backend sin tener la tablet a mano). |
| `--no-reboot` | Salta el reboot final de verificación (para seguir trabajando sobre la tablet a mano). |
| `--fix-setup` | Gotcha "already provisioned" — ver tabla de abajo. |
| `--unprovision` | SOLO desinstala la app — NO quita el Device Owner (ver "Cómo se sale" más abajo). |

#### Qué imprime al final

```
==> TABLET LISTA PARA ENTREGAR
    modelo: PHILCO TP10A464 (Android 13)   serial: TP10A46414379100691
    app: IOLSIMULATOR Tablet 0.7.0 (700)   owner: OK   kiosco: LOCKED   arranque directo: OK
```

Si ves esto, la tablet está lista para entregar tal cual — no hace falta tocar nada más.

#### Problemas → solución

| Síntoma | Solución |
|---------|----------|
| `adb devices` muestra `unauthorized` | Desbloqueá la pantalla de la tablet y aceptá el diálogo "Permitir depuración USB" / "Permitir siempre desde esta computadora"; si no aparece, desconectá y reconectá el cable. |
| `dpm set-device-owner` falla con **"already provisioned"** | `scripts/provision-tablet.sh --fix-setup --serial <serial>` y reintentar sin esa flag (ver gotcha abajo). |
| Sin internet en la PC, o el manifest responde **503** | Backend sin versión activa de tablet, o PC sin conexión — usar `--apk <ruta>` con un APK local, o revisar `/admin/versions` en el backend. |
| El SHA256 del APK descargado no coincide | Descarga corrupta o manifest inconsistente — el script no instala nada; reintentar, y si persiste avisar a quien administra el backend. |
| La tablet ya tenía un Device Owner de **otra** app/proyecto | No se puede reasignar sin borrar el anterior — factory reset completo y empezar de cero. |
| No se detecta ningún dispositivo tras 5 minutos | Revisar la checklist que imprime el script; en Windows puede hacer falta instalar el driver USB del fabricante de la tablet. |
| Falla la verificación final (foco / `LOCKED`) tras el reboot | Puede ser una tablet que quedó a mitad de camino de una provisión anterior — repetir el comando; si persiste, factory reset. |

#### Detalle técnico y gotchas

**Con el Device Owner puesto, el OTA es silencioso (Fase C, lado Unity — `docs/updates.md`
§"Instalación silenciosa en kiosco (F8)"):** `UpdateInstaller` instala el APK descargado vía
`PackageInstaller` (`SilentInstaller.java`) sin ningún diálogo ni intervención humana — el
`appops set ... REQUEST_INSTALL_PACKAGES allow` que aplica el script queda como **red de
seguridad**, no como el mecanismo real de instalación: cubre el caso borde de que
`KioskManager.IsDeviceOwner` de `false` por algún motivo (p. ej. el registro de Device Owner se
perdió) y el flujo caiga de vuelta al intent `ACTION_VIEW` visible de `UpdateInstaller`, que sí
necesita ese permiso concedido para no pedir confirmación manual.

**Gotcha "already provisioned":** `dpm set-device-owner` falla con *"Trying to set the device
owner, but device is already provisioned"* en cualquier tablet que ya pasó por el asistente de
configuración inicial de Android — lo hacen la mayoría de fábrica al primer boot, aunque no se
haya agregado ninguna cuenta. `scripts/provision-tablet.sh --fix-setup` aplica el truco **sin
root** (`adb shell settings put global device_provisioned 0` + insertar
`user_setup_complete=0` en `Settings.Secure` vía `content insert`) para volver la tablet a un
estado "no provisionado" y reintentar `set-device-owner` sin un factory reset. **Primera
provisión real (2026-09-03, PHILCO TP10A464, Android 13):** esta tablet aceptó
`set-device-owner` al primer intento CON el asistente de configuración completado (asistente
corrido normalmente, sin cuentas agregadas) y SIN necesitar `--fix-setup` — el gotcha de arriba
no es universal, depende del fabricante/ROM (algunas OEM no marcan `device_provisioned` tras el
asistente si no se agregó ninguna cuenta).

**Gotcha "carrera tarea-standard + Home = doble instancia de Activity = crash de Unity,
auto-recuperable" — INCIDENTE REAL, corregido (2026-09-03, PHILCO TP10A464).** Al provisionar
por primera vez, lanzar la app con `monkey -p` (intent LAUNCHER, que es lo que hacía este script
antes de esta tarea) crea una tarea `type=standard` aunque la Activity sea `singleTask`
(`android:launchMode="singleTask"`, confirmado presente en el manifest). Si justo después de ese
lanzamiento se pulsa Home (físico o `KEYCODE_HOME`), Android busca la app de inicio en una tarea
`type=home` — no reusa la tarea `standard` existente aunque sea la MISMA Activity singleTask — y
crea una **segunda instancia de la Activity en el mismo proceso**. Unity no lo tolera:
`UnityFoldingFeaturesWrapper` es estático por proceso, y el segundo `onCreate()` tira
`RuntimeException: UnityFoldingFeaturesWrapper.init() should be called only once. Use
getInstance() instead.`, tumbando el proceso entero. Reproducido en vivo 1 de 5 veces (no
determinístico: depende de si `KioskManager.ApplyPolicies()`/`EnterLockTask()` ya corrieron para
cuando se pulsa Home). **Auto-recuperable**: el crash mata el proceso, pero la HOME persistente
(`addPersistentPreferredActivity`, ya registrada por `ApplyPolicies()` para cuando esto pasa)
relanza la app sola en una tarea `type=home` — el problema no vuelve a aparecer en esa sesión.
Tras un reboot real la app SIEMPRE nace `type=home` (la relanza el propio Android vía el
mecanismo de HOME persistente, nunca vía LAUNCHER), así que el bug es exclusivo de la ventana de
la primera provisión.

**Por qué el fix NO es "lanzar por LAUNCHER y corregir después con `force-stop` + relanzar por
HOME"** (variante intentada y descartada durante esta tarea, confirmada en vivo que NO
funciona): una vez que `ApplyPolicies()`+`EnterLockTask()` corrieron (pasa solo, apenas la app
arranca, sin intervención externa), Android **bloquea el `force-stop`** de la app en lock task —
confirmado con logcat del sistema: `ActivityManager: Ignoring request to force stop protected
package com.simulador.tablet u0` (exit code 0, sin error visible en la salida de `adb`, pero el
proceso NUNCA se reinicia — el PID queda idéntico). Sin poder matar el proceso, reintentar
`am start ... HOME` sobre esa tarea `standard` ya bloqueada dispara la MISMA carrera de arriba,
ahora **garantizada en cada provisión** en vez de ocasional. **Fix real, aplicado en
`scripts/provision-tablet.sh`:** lanzar la app la PRIMERA vez con un intent HOME explícito
(`am start -a android.intent.action.MAIN -c android.intent.category.HOME -n
com.simulador.tablet/com.unity3d.player.UnityPlayerGameActivity`) en vez de LAUNCHER
(`monkey -p`) — la tarea nace `type=home` desde el vamos (mismo mecanismo con el que Android la
relanza sola tras un reboot), así que nunca hay una tarea `standard` que bloquear ni una carrera
que ganar. El script después espera hasta 30 s a que `cmd package resolve-activity` confirme que
`ApplyPolicies()` corrió y verifica `type=home` en la tarea ya corriendo, sin volver a tocarla.
**Verificado post-fix en la PHILCO:** rebuild + reinstalación (mismo `applicationId`/firma, el
Device Owner sobrevive), 10× `KEYCODE_HOME` con 3 s de por medio sin ningún `FATAL` en logcat y
con el mismo PID antes/después, y reboot final con la app sola en foco, `type=home` y
`mLockTaskModeState=LOCKED`. Repetir la receta vieja (`monkey -p` + `force-stop` + Home) en la
tablet YA locked sigue sin poder forzar el kill (mismo "Ignoring request to force stop protected
package") — es un camino que ya no ejecuta el script, documentado acá solo como referencia de
por qué no es viable arreglarlo por ese lado.

**Gotcha "diálogo de modo inmersivo la primera vez que se oculta la barra de estado":** la
PRIMERA vez que `KioskManager.ApplyPolicies()` llama `setStatusBarDisabled(true)`, Android
muestra un diálogo nativo ("Visualización en pantalla completa" / "Entendido") que alguien tiene
que tocar a mano — inaceptable en una tablet que sale a una clínica sin volver a tocarla.
`DevicePolicyManager.setSecureSetting()` del Device Owner NO cubre la clave
`immersive_mode_confirmations` (no está en su allowlist), así que no se puede resolver desde
`KioskManager` (C#/JNI) — el único camino soportado es `adb shell settings put secure
immersive_mode_confirmations confirmed` (requiere `WRITE_SECURE_SETTINGS`, que el shell de `adb`
sí tiene). `scripts/provision-tablet.sh` lo aplica ANTES del primer lanzamiento de la app,
idempotente.

**Qué verifica el reboot final del script** (ya automatizado, no hace falta repetirlo a mano):
1. La tablet arranca DIRECTO en la app (sin launcher de Android visible) — la HOME persistente
   (`KioskManager.ApplyPolicies`, `addPersistentPreferredActivity`) hace que la app sea la única
   pantalla de inicio.
2. `mCurrentFocus` (`dumpsys window`) contiene `com.simulador.tablet`.
3. `mLockTaskModeState` (`dumpsys activity activities`) es `LOCKED` — no se puede salir con los
   gestos normales (recientes/atrás/home del sistema no están disponibles bajo `startLockTask`).

Manual, si hace falta confirmarlo a ojo: el botón físico de power SÍ debe abrir el menú de
apagado (`LOCK_TASK_FEATURE_GLOBAL_ACTIONS`, ver `docs/tablet.md` Decisiones — si no aparece,
revisar que `setLockTaskFeatures` se haya aplicado), y el botón "Red Wi-Fi" de la `ConnectScreen`
debe abrir el panel de WiFi de Android (bajo lock task, gracias a `com.android.settings` en el
allowlist — ver `docs/tablet.md`).

**Cómo se sale (soporte real):** `dpm remove-active-admin` **NO sirve** para un Device Owner
(solo aplica a "device admins" comunes, no al owner) — Android exige `clearDeviceOwnerApp()`
llamado DESDE la propia app, o un factory reset completo. El camino normal de soporte es el gesto
de la app: 7 taps en el título del `ConnectScreen` (`app.title` — **`IOLSIMULATOR`, idéntico en
es/en, ver `docs/localizacion.md`**) dentro de 3 segundos + PIN de servicio (ver
`docs/tablet.md` "Pantallas" y Decisiones). El PIN correcto entra a un **modo servicio** real
(`KioskManager.EnterServiceMode()`): sale del lock task de verdad, libera la HOME persistente y
deja la app ABIERTA con un banner y un botón "Volver al kiosco" — el operador tiene una ventana
real (sin carrera, sin relanzamiento) para ir a Home/Ajustes y conectar adb, y decide cuándo
volver al kiosco (o reinstalar). `scripts/provision-tablet.sh --unprovision` es más limitado:
SOLO desinstala la app (el registro de Device Owner queda a nivel de sistema); para limpiarlo
del todo hace falta `clearDeviceOwnerApp()` desde la app o un factory reset.

#### Recuperación remota por QR

Para cuando la tablet NO está en el taller: una clínica en el exterior sufre un **factory
reset** (batería agotada durante una actualización de Android, restablecimiento accidental,
etc.) y el procedimiento de arriba (`scripts/provision-tablet.sh` por USB/adb) no es viable a
distancia. La alternativa es **Android Enterprise QR provisioning**: el propio asistente de
configuración de Android sabe leer un QR con las extras `PROVISIONING_*` y hacer todo el
`dpm set-device-owner` + descarga del APK solo, sin adb ni PC de por medio del lado del
cliente. El backend genera ese QR en `/admin/provisioning` (detalle del payload, los dos
checksums posibles y por qué se prefiere el de firma en `docs/backend.md` > "Auth y panel
admin" > Provisioning).

**Pasos del cliente (instrucciones que se le mandan por mail/teléfono junto con el QR):**
1. Encender la tablet recién factory-reseteada y llegar a la pantalla de bienvenida del
   asistente de configuración (el primer paso, antes de elegir idioma/WiFi).
2. Tocar 6 veces seguidas en un punto vacío de esa pantalla — Android abre el lector de QR de
   provisioning.
3. Escanear el QR (impreso o desde la pantalla de otro dispositivo).
4. Esperar sin tocar nada: la tablet descarga el APK desde `PROVISIONING_DEVICE_ADMIN_PACKAGE_DOWNLOAD_LOCATION`
   (el `/files/apk/tablet/...` público del backend), verifica el checksum, se registra como
   Device Owner y la app se abre sola al terminar (`SimuladorDeviceAdminReceiver.onProfileProvisioningComplete`,
   ver `docs/tablet.md`).
5. **No crear ninguna cuenta Google** en ningún momento del proceso — igual que en la provisión
   de taller, cualquier cuenta agregada antes de terminar el provisioning lo hace fallar.

**Pasos del operador (backend):**
1. Confirmar que la versión ACTIVA del canal `tablet` en `/admin/versions` es la que se quiere
   mandar (el QR apunta a esa `apk_url`/checksum en el momento de generarlo).
2. Entrar a `/admin/provisioning`. Si el cliente dio datos de su WiFi/idioma/zona horaria,
   cargarlos en el form (opcionales; no se guardan en el servidor, solo viajan dentro del QR de
   esa respuesta) para que la tablet salga configurada sin pasos manuales adicionales.
3. Mandar el QR resultante (captura de pantalla o impresión) al cliente junto con los pasos de
   arriba.

**Requisito para que el QR mandado por mail no caduque:** la versión activa del canal `tablet`
tiene que estar firmada con el **keystore del proyecto** (el mismo `keystore/simulador.keystore`
de la sección Firma más abajo) y `PROVISIONING_SIGNATURE_CHECKSUM` tiene que estar configurado en
el `.env` del backend — si no, `/admin/provisioning` cae al checksum del PAQUETE (derivado del
APK activo), que queda inválido en cuanto se publica una versión nueva de la tablet (un QR viejo
en la bandeja de entrada de un cliente dejaría de servir). El checksum de firma es **configuración
única por proyecto** (no cambia entre releases, se configura una sola vez):

```bash
<Editor Unity>/Data/PlaybackEngines/AndroidPlayer/OpenJDK/bin/keytool \
  -exportcert -alias simulador -keystore keystore/simulador.keystore \
  | openssl dgst -sha256 -binary | openssl base64 | tr -d '=' | tr -- '+/' '-_'
```

### Deploy del backend (resumen)

```bash
cd backend
cp .env.example .env      # defaults sirven para local
docker compose up -d      # api + db + bucket (MinIO) + caddy
curl http://localhost:8080/healthz
```

En producción: VPS con Docker, DNS apuntando al dominio, `.env` con `DOMAIN=api.tu-dominio.com`, `SCHEME=` (vacío), `PORT=443` y secrets regenerados; Caddy emite el certificado Let's Encrypt solo. Detalle completo en `docs/backend.md` y `backend/README.md`.

**Deploy real (2026-07-09):** VPS `root@2.25.81.197` (Ubuntu, Docker 29.1.3, Compose 2.40.3),
dominio `vr.conecta.sh` (DNS A record → esa IP), repo clonado en `/opt/simulador-lios`
(reemplaza al deploy viejo del prototipo Godot que vivía en `/opt/simulador`, dado de baja:
dump de su Postgres en `/root/backups/simulador-viejo-20260709.sql` y su `.env` en
`/root/backups/env-viejo-20260709`). `docker-compose.prod.yml` se copió a mano al server
(en esa fecha el archivo todavía no estaba commiteado en el repo) y se linkeó también como
`docker-compose.override.yml` para que un `docker compose` pelado en `/opt/simulador-lios/backend`
también aplique el override. Comando de arranque usado:

```bash
cd /opt/simulador-lios/backend
docker compose -f docker-compose.yml -f docker-compose.prod.yml up -d --build
```

Verificado end-to-end: `docker compose ps` con los 4 servicios Up/healthy, migraciones Alembic +
seed del catálogo (`0.5.1-clinical`, 3 lentes) sin errores en `docker compose logs api`,
`https://vr.conecta.sh/healthz` → `ok`, `/api/lenses` y `/api/manifest.json` respondiendo,
`http://vr.conecta.sh/healthz` → 308 a HTTPS, `/admin/login` → 200, y consola MinIO (9001)
solo en `127.0.0.1` (no expuesta). Certificado Let's Encrypt emitido sin intervención manual.

## Firma (keystore) del proyecto

**Desde 2026-07-09 el proyecto firma con un keystore propio, no con el debug keystore de la
máquina.** Esto es requisito no negociable del sistema de updates semi-automáticos
(`docs/updates.md`): Android exige que dos APKs con el mismo `applicationId` estén firmados con
el **mismo certificado** para que uno pueda instalarse sobre el otro sin desinstalar — la
primera build update-capable que se distribuya define la firma **para siempre** en esos
dispositivos.

| Archivo | Rol |
|---------|-----|
| `keystore/simulador.keystore` | Keystore JKS, alias `simulador`, RSA 2048, validez 10000 días, `dname` `CN=Simulador LIOs, O=TFM, C=UY`. Generado con el `keytool` que trae el propio Unity (`<Editor>/Data/PlaybackEngines/AndroidPlayer/OpenJDK/bin/keytool.exe`), no hace falta un JDK del sistema. |
| `keystore/keystore.properties` | `storePassword` / `keyPassword` (misma password única para ambos) / `keyAlias=simulador` / `storeFile=keystore/simulador.keystore`. Documenta la password para quien tenga que reconfigurar el Editor; **no lo lee ningún script automáticamente todavía** (no hay Gradle template custom que lo parsee). |
| `.gitignore` → `/keystore/` | Toda la carpeta gitignorada. El repo es **público**: el keystore JAMÁS se commitea, ni siquiera cifrado. |

**Cómo se configura el Editor (por sesión, no persiste solo):**

```csharp
// PlayerSettings.Android.useCustomKeystore / keystoreName / keyaliasName SÍ persisten en
// ProjectSettings/ProjectSettings.asset (solo path + alias, ninguna password) — commiteable.
PlayerSettings.Android.useCustomKeystore = true;
PlayerSettings.Android.keystoreName = "keystore/simulador.keystore";
PlayerSettings.Android.keyaliasName = "simulador";

// keystorePass / keyaliasPass NUNCA se persisten a disco (ni en ProjectSettings.asset ni en
// ningún .asset) — hay que setearlas por unity_execute_code EN CADA sesión del Editor, antes
// de cualquier build Android, o el build queda esperando el diálogo interactivo de contraseña
// (que cuelga un build headless/CI).
PlayerSettings.Android.keystorePass = "<password>";
PlayerSettings.Android.keyaliasPass = "<password>";
```

**BACKUP — CRÍTICO, léase dos veces:** `keystore/` vive SOLO en esta máquina y está gitignorada
a propósito. **Hacer backup de la carpeta completa fuera del repo (y fuera de esta máquina) es
responsabilidad del operador humano.** Si se pierde el keystore, no hay forma de recuperarlo
(no es un secreto derivable) y **ninguna build futura puede actualizar los APKs ya publicados/
instalados** en dispositivos de campo — la única salida sería desinstalar manualmente cada
dispositivo y reinstalar desde cero con una firma nueva. Es, junto con la base de datos de
producción, el activo más irreemplazable del proyecto.

**Checklist de release del operador** (una vez que exista un release real a publicar):

1. Bump de versión: `PlayerSettings.bundleVersion` (semver `major.minor.patch`, contrato del
   manifest en `docs/updates.md`) + `PlayerSettings.Android.bundleVersionCode` (entero,
   incremental).
2. Setear `keystorePass`/`keyaliasPass` por `unity_execute_code` (no persisten entre sesiones
   del Editor, ver arriba).
3. Build visor (`unity_build`, `Main.unity`, loader OpenXR ON) y build tablet (**SIEMPRE**
   `Simulador → Build Tablet (Android)` / `TabletBuild.BuildTablet()`, nunca `unity_build`
   directo — gotcha de arriba).
4. Verificar el gate F4 en AMBOS APKs (aapt/apksigner, ver `docs/updates.md`): activity
   `UnityPlayerGameActivity` completa con intent-filter LAUNCHER, permiso
   `REQUEST_INSTALL_PACKAGES`, provider `<applicationId>.fileprovider` con
   `FILE_PROVIDER_PATHS`, clase `androidx.core.content.FileProvider` presente en el dex, y firma
   con el certificado del keystore del proyecto (no debug) — ver gotcha de `keytool -printcert`
   más abajo. **Además, en el APK de tablet: extraer el PNG del ícono embebido (`aapt dump
   badging | grep application-icon-480` para ubicar el resource ofuscado, `unzip -j <apk>
   res/<nombre>.png`) y compararlo visual/por hash contra `icon_tablet.png` — `aapt dump badging`
   solo confirma que HAY un ícono, no cuál; hay un bug real abierto donde el APK de tablet sale
   con el ícono del visor pese a que `TabletBuild.cs` restaura bien loader/identifier/productName
   (ver gotcha de íconos más abajo, 2026-07-09).**
5. Subir ambos APKs al panel admin del backend con `app` (`visor`/`tablet`), `apk_version`,
   `changelog` (ver `docs/updates.md` §Contrato del manifest).
6. Activar la versión desde el panel.
7. Verificar en `/admin/logs` (o `docker compose exec db psql ...`) que los dispositivos de
   campo reportan `update_check`/`update_prompt_shown` y, tras aceptar, el resto de la cadena
   `update_*` (`docs/updates.md` §Telemetría).

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
- **Icono propio por app (visor vs tablet), mismo patrón try/finally que loader XR / identifier /
  productName.** Antes de esta tarea ambas apps usaban el icono default de Unity (nunca se había
  seteado ninguno). Ahora: (1) el icono default del proyecto —
  `PlayerSettings.SetIcons(NamedBuildTarget.Unknown, ..., IconKind.Application)` — es el del
  **visor** (`Assets/Textures/Icons/icon_visor.png`), seteado una sola vez (no por `TabletBuild`,
  es config persistente del proyecto) y persistido en `ProjectSettings.asset`; (2) `TabletBuild`
  guarda `PlayerSettings.GetPlatformIcons(NamedBuildTarget.Android, kind)` para los tres kinds
  reales de Android (`AndroidPlatformIconKind.Legacy/Round/Adaptive` — **NO**
  `IconKind.Application`, esa es la API genérica multi-plataforma sin efecto en Android, ver el
  gotcha resuelto más abajo con la causa raíz confirmada) ANTES de tocarlos (por defecto, antes de
  esta tarea, `layerCount == 0` en los tres — Android no tenía override propio y heredaba el
  default), los pisa con `Assets/Textures/Icons/icon_tablet.png` replicado en todas las capas que
  pide cada slot (`icon.maxLayerCount` — 1 para Legacy/Round, 2 para Adaptive), y los restaura
  SIEMPRE en el `finally` — Android vuelve a quedar sin override propio y hereda otra vez el
  default (visor). Motivo de usar `NamedBuildTarget.Unknown` para el default en vez de setear
  Android directamente para el visor: el visor se buildea con Build Settings/`unity_build` normal
  (no hay un `VisorBuild.cs` dedicado), así que no hay un punto único try/finally para pisar el
  icono de Android solo durante SU build — más simple dejar que Android herede el default y que
  sea `TabletBuild` el único que hace un override temporal (igual que ya hacía con XR/identifier).
  Carga del `Texture2D` por `AssetDatabase.LoadAssetAtPath` (NO `Resources.Load` — restricción del
  repo, ver `AGENTS.md` §Reglas de assets Unity).
- **`TabletBootConfigPatcher` borra flags `xr-*` del `boot.config` YA GENERADO en vez de evitar que
  el hook de OpenXR los escriba** → un intento previo togglear `MetaQuestFeature.enabled` en
  memoria durante el `BuildPipeline` (detalle completo en el Gotcha del teclado) no fue confiable:
  el paquete OpenXR relee/persiste su propio estado en momentos fuera del control de `TabletBuild`,
  y el toggle además dirteaba a disco un asset compartido con el visor
  (`OpenXR Package Settings.asset`). Post-procesar el artefacto ya escrito (vía
  `IPostGenerateGradleAndroidProject`, `callbackOrder` alto para correr último) es determinista
  porque no depende de ganarle una carrera al estado interno de un paquete de terceros — solo lee y
  reescribe un archivo de texto plano después de que todos los escritores ya corrieron.

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
- **Manifest custom incompleto rompe el merge del launcher (visor Y tablet, mismo manifest) —
  INCIDENTE REAL, corregido.** `Assets/Plugins/Android/AndroidManifest.xml` (compartido por ambos
  builds, ver `docs/tablet.md`) tuvo una versión que declaraba SOLO 2 `<uses-permission>` sin
  ningún bloque `<application>`/`<activity>`, asumiendo que Unity fusionaba sin pisar nada. En la
  práctica, cuando el manifest custom agrega `<application>` pero NO declara la Activity de Unity
  (`UnityPlayerGameActivity`) con su `intent-filter`/`theme`/`meta-data`, el merge no hereda ese
  bloque completo desde el motor: el APK resultante queda con
  `<activity android:name="com.unity3d.player.UnityPlayerGameActivity" />` pelado — sin
  `exported`, sin `theme`, sin intent-filter MAIN/LAUNCHER. Síntomas: la app no aparece en el
  launcher del dispositivo, y `adb shell monkey -p <package> 1` falla con `SecurityException:
  Permission Denial: starting Intent ... not exported` o `No activities found to run`. Señal
  temprana en el log de build: el warning de Unity `"Unable to find Unity activity in manifest.
  Some attributes may not be set properly"` (también queda registrado en
  `Library/Bee/artifacts/Android/Manifest/IntermediateLauncherManifestDiag.txt`); confirmable post
  build con `aapt dump xmltree <apk> AndroidManifest.xml` mirando el nodo `activity`. Diagnóstico
  confirmado inspeccionando la cadena de merge en
  `Library/Bee/Android/Prj/IL2CPP/Gradle/unityLibrary/xrmanifest.androidlib/`: sin un bloque de
  Activity completo en el manifest custom, Unity emite ahí un stub de rescate mínimo (solo
  `android:name`, nada más) en vez de copiar el bloque real de su plantilla interna
  (`<UnityEditorInstall>/Editor/Data/PlaybackEngines/AndroidPlayer/Apk/UnityManifest.xml`). **Fix:**
  el manifest custom debe declarar la Activity COMPLETA — copiar tal cual el bloque "Used when
  Application Entry is set to GameActivity" de esa plantilla (`UnityPlayerGameActivity`,
  `android:theme="@style/BaseUnityGameActivityTheme"`, intent-filter MAIN/LAUNCHER, meta-data
  `unityplayer.UnityActivity` + `android.app.lib_name=game`), agregando `android:exported="true"`
  explícito (Android exige declararlo en toda Activity con intent-filter desde targetSdkVersion
  31+; este proyecto targetea 36). **Regla para cualquier manifest custom futuro en este
  proyecto:** un manifest "solo permisos" (que no toca `<application>` en absoluto) es seguro; uno
  que agrega `<application>` sin declarar la Activity completa junto con ella NO lo es.
- **F4 de updates semi-automáticos agregó entradas al manifest compartido (2026-07-09)**:
  `<uses-permission android:name="android.permission.REQUEST_INSTALL_PACKAGES" />` junto a
  los permisos existentes, y un `<provider android:name="androidx.core.content.FileProvider"
  .../>` como HERMANO de la `<activity>` (sin tocarla) dentro de `<application>` — respetando
  las dos reglas de abajo (activity completa intacta, sin `--` en comentarios nuevos). El
  provider depende de la carpeta Android Library Plugin
  `Assets/Plugins/Android/SimuladorUpdate.androidlib/` (recursos `res/xml/file_paths.xml`);
  detalle completo, incluyendo el pendiente de verificar la versión de `androidx.core` en el
  primer build real, en `docs/updates.md` §Gotchas.
- **Comentario XML con `--` en `AndroidManifest.xml` rompe la generación del manifest —
  INCIDENTE REAL, ABIERTO (2026-07-04).** Al corregir el gotcha anterior, el comentario de
  cabecera agregado a `Assets/Plugins/Android/AndroidManifest.xml` usa `--` (doble guión) como
  separador de frase en dos puntos ("...de esa Activity -- rompe el launcher..." y "...GameActivity"
  -- UnityPlayerGameActivity,..."). La spec de XML prohíbe la secuencia `--` dentro de un
  comentario (`<!-- ... -->`), fuera de los delimitadores de apertura/cierre. Con eso presente,
  `TabletBuild.BuildTablet()` (y el build normal del visor, mismo manifest compartido) falla al
  generar `Library/Bee/artifacts/Android/Manifest/IntermediateLauncherManifestDiag.txt` con
  `System.Xml.XmlException: An XML comment cannot contain '--', and '-' cannot be the last
  character` — no es error de compilación C# (`unity_get_compilation_errors` sale limpio), es un
  fallo del `BuildReport` (`summary.result == Failed`, visible en `unity_console_log`). El loader
  OpenXR y el `applicationIdentifier`/`productName` SÍ se restauran igual (el `finally` de
  `TabletBuild` corre aunque `BuildPlayer` falle). **Fix:** sacar las dos ocurrencias de `--` del
  comentario (reemplazar por `-` simple, `—` em dash, o reformular la frase). **Regla:** ningún
  comentario XML en este manifest puede contener `--`. La regla del `--` en comentarios XML
  aplica a **todo XML de Android**, no solo a este manifest — mismo bug apareció en
  `SimuladorUpdate.androidlib/res/xml/device_admin.xml` (incidente real, 2026-09-02) y tardó
  varios minutos de IL2CPP en manifestarse porque el Editor y el compile-gate no validan
  recursos de un `.androidlib` (solo Gradle/`aapt2` en build, vía `XMLStreamException`/
  `aapt2 error: not well-formed`). **Regla:** tras crear/editar cualquier `res/**/*.xml` del
  androidlib, correr `aapt2 compile <archivo> -o <dir>` (mismo parser que Gradle, toma
  milisegundos) antes de buildear.
- **`keytool -printcert -jarfile <apk>` dice "No es un archivo jar firmado" en APKs firmados por
  Unity 6, aunque SÍ estén firmados — falso negativo, verificado en vivo (2026-07-09) con las
  primeras builds firmadas con el keystore del proyecto.** El Gradle/Unity moderno firma con
  **APK Signature Scheme v2** (y NO con v1/JAR signing — no hay `META-INF/*.RSA`/`*.SF`), y
  `keytool -printcert -jarfile` solo entiende firmas v1 estilo JAR. La forma correcta de
  verificar firma/certificado en este proyecto es `apksigner` (mismo `build-tools/<version>/`
  que trae Unity, `lib/apksigner.jar`, se invoca con el `java` del OpenJDK embebido):
  ```bash
  java -jar ".../build-tools/36.0.0/lib/apksigner.jar" verify -v <apk>            # esquema usado (v2: true)
  java -jar ".../build-tools/36.0.0/lib/apksigner.jar" verify --print-certs <apk>  # DN + hashes del certificado
  ```
  Confirmar que el `Signer #1 certificate DN` sea el del keystore del proyecto
  (`CN=Simulador LIOs, O=TFM, C=UY`) y no el de un debug keystore (`CN=Android Debug`).
- **Un intento de build Android (aunque falle) puede ensuciar el working tree fuera del alcance
  del try/finally de `TabletBuild`.** Detectado en vivo durante el incidente de arriba: tras un
  `BuildTablet()` fallido, además del loader/identifier (que sí se restauran), aparecieron dos
  efectos colaterales no gestionados por el script: (1) `PlayerSettings.preloadedAssets` gana una
  entrada nueva apuntando a `Assets/XR/Settings/OpenXR Package Settings.asset` (Unity la inyecta
  al preprocesar XR para el target Android, independientemente de si el build termina en éxito);
  y (2) se generan `Assets/Resources/PerformanceTestRunInfo.json` y
  `PerformanceTestRunSettings.json` (con sus `.meta`) — artefactos del test framework de
  performance que Unity escribe al iniciar cualquier build Android. Ninguno de los dos lo maneja
  `TabletBuild.cs`. Verificar `git status` después de un build (exitoso o no) y, si aparecen,
  limpiarlos: el preload con `PlayerSettings.SetPreloadedAssets(...)` quitando esa entrada +
  `AssetDatabase.SaveAssets()`, y los JSON con `unity_asset_delete` (o borrarlos y dejar que el
  Editor limpie el `.meta` huérfano). No se automatizó la limpieza en `TabletBuild.cs` todavía —
  quedaría a criterio de `@unity-dev` si vale la pena extender el `finally`.
- **Otro efecto colateral de build Android detectado (2026-07-09): `Assets/Settings/
  Mobile_RPAsset.asset` (el `UniversalRenderPipelineAsset` del tier Mobile, ver `AGENTS.md`
  §Reglas de assets Unity) cambia `m_PrefilterXRKeywords` de `0` a `1`.** Es Unity precompilando/
  prefiltrando keywords de shader para XR al preprocesar el build Android, mismo mecanismo que
  el preload de OpenXR Package Settings de arriba — no es una edición deliberada de nadie.
  Mismo tratamiento: revisar `git status` post-build y `git checkout -- "Assets/Settings/
  Mobile_RPAsset.asset"` si aparece y no se querían tocar sus flags de prefiltrado a propósito
  en esa tarea.
- **`TabletBuild.cs` pisaba `PlayerSettings.SetIcons(NamedBuildTarget.Android, ..., IconKind.
  Application)` pero el APK de tablet salía con el ícono del VISOR — BUG REAL, RESUELTO (detectado
  en vivo en el release 0.2.0, 2026-07-09; fix en la misma fecha).** Causa raíz **confirmada** (no
  ya hipótesis) inspeccionando la API de Unity 6000.5 por reflection: `UnityEditor.IconKind`
  (`Application/Settings/Notification/Spotlight/Store/Any`) es un enum **genérico multi-plataforma**
  sin efecto real en lo que Android empaqueta. Android resuelve su ícono de lanzador contra los
  **platform icons** específicos de la plataforma:
  `PlayerSettings.GetPlatformIcons(NamedBuildTarget, PlatformIconKind)` /
  `SetPlatformIcons(...)`, con los kinds expuestos en
  `UnityEditor.Android.AndroidPlatformIconKind.Legacy` / `.Round` / `.Adaptive` (assembly
  `UnityEditor.Android.Extensions`, namespace `UnityEditor.Android` — alcanza con
  `using UnityEditor.Android;`, NO hace falta agregar una referencia nueva al
  `Simulador.Editor.asmdef`, la extensión de plataforma Android ya está disponible porque el
  módulo Android está instalado). Antes del fix, los tres kinds estaban en su estado por defecto
  (`layerCount == 0`, ningún `Texture2D` asignado) — Android cae al ícono default genérico
  (`NamedBuildTarget.Unknown`, el del visor) cuando sus platform icons están vacíos, que es
  exactamente lo que se observó. **Fix aplicado:** `TabletBuild.cs` ahora guarda
  `PlayerSettings.GetPlatformIcons(Android, kind)` para los tres kinds ANTES de tocar nada, en el
  `try` los pisa con `icon_tablet.png` (replicado en todas las capas que pide cada slot — Legacy
  y Round piden 1 capa, Adaptive pide EXACTAMENTE 2, `minLayerCount == maxLayerCount == 2`
  background+foreground; se usa el mismo PNG en ambas capas ya que es full-bleed opaco, así que el
  foreground cubre completo y el resultado visual es equivalente a Legacy/Round recortado por el
  mask circular/squircle de Android), y los restaura SIEMPRE en el `finally` — mismo patrón
  try/finally que loader XR / `applicationIdentifier` / `productName`. Verificado en frío (sin
  build, por `unity_execute_code`): tras aplicar el swap con la misma API, `GetPlatformIcons`
  devuelve `icon_tablet` en los 6 tamaños × 3 kinds (12 slots en Adaptive por las 2 capas); tras
  restaurar, los tres kinds vuelven a `layerCount == 0` (estado pristino, idéntico al inicial).
  **Verificación end-to-end en APK real — CERRADO (2026-07-09, rebuild de `tablet-0.2.0.apk`
  tras el fix).** `aapt dump badging` ya no lista PNGs sueltos para `application-icon-*` sino un
  único `res/<hash>.xml` (mismo valor en las 7 densidades) — es el descriptor `<adaptive-icon>`
  (`res/Qu.xml` en esta build: `background=@0x7f0c0002`, `foreground=@0x7f0c0003`), señal en sí
  misma de que el Adaptive icon kind quedó seteado (antes del fix no existía ese XML, aaptdump
  mostraba PNGs directos heredados del default). Log de Unity durante `BuildPlayer` cambió de
  `"Compressed texture icon_visor is used as icon"` (bug) a **`"Compressed texture icon_tablet is
  used as icon"`** (fix) — más dos warnings nuevos y esperables `"Round/Legacy icons are
  deprecated, use Adaptive instead"` (confirma que los tres kinds quedaron poblados). Resolviendo
  los resource ID del XML contra la tabla de recursos (`aapt dump --values resources <apk>`) y
  extrayendo los PNG de xxxhdpi (`unzip -j`): **Legacy (`mipmap/app_icon`) y Round
  (`mipmap/app_icon_round`) son el mismo archivo** (mismo MD5, `65b80e48...`, 10038 bytes —
  esperable, ambos piden 1 capa y usan el mismo `icon_tablet.png` fuente) y **el foreground y
  background del Adaptive también coinciden entre sí** (mismo MD5, `0cb86952...`, 29728 bytes —
  esperable también, ver "Fix aplicado" arriba: se replica el mismo PNG full-bleed opaco en ambas
  capas). Inspección visual de los 3 PNG únicos extraídos (Legacy/Round + Adaptive fg/bg): las
  4 imágenes muestran el diseño correcto de tablet — LIO blanca sobre fondo teal con las 4 marcas
  de escala — **no** el cian-sobre-azul-marino del visor. Firma (`apksigner --print-certs`) y
  `aapt dump badging` (package `com.simulador.tablet`, versionCode `200`, versionName `0.2.0`)
  también verificados sobre el mismo APK. Bug cerrado del todo: código corregido + verificado a
  nivel API (por @unity-dev) + verificado end-to-end en el artefacto compilado real
  (por @build-deploy).
- **El teclado Android no abre en la tablet (el clínico no puede tipear el PIN) — BUG REAL,
  RESUELTO (fix determinista en ronda 2, ver abajo).** Síntoma en el dispositivo:
  `TouchScreenKeyboard.Open` no abre nada; el log muestra `"Oculus overlay keyboard is disabled,
  add 'oculus.software.overlay_keyboard' feature request to your Android manifest"` — la app
  intenta el teclado overlay de Oculus (que no existe en una tablet plana sin runtime VR) en vez
  del teclado Android normal. No determinista: un build podía salir sano y el siguiente no, sin
  cambios de repo entre medio. **Causa raíz confirmada** inspeccionando el paquete OpenXR
  instalado (`Library/PackageCache/com.unity.xr.openxr@.../Editor/MetaQuest/BuildTargetSupport/
  MetaQuestFeatureBuildHooks.cs:72`): `OnProcessBootConfigExt` escribe
  `xr-keyboard-overlay-enabled=1` (y ~11 flags `xr-*` más — late latching, low latency audio,
  pipeline cache, etc., ver la lista completa en esa misma función) en
  `assets/bin/Data/boot.config` del APK. El ÚNICO gate propio del hook es `IsExtensionEnabled`
  (`Editor/FeatureSupport/OpenXRFeatureBuildHooks.cs:29`), que exige
  `BuildHelperUtils.HasActiveLoader(group, typeof(OpenXRLoaderBase))` **&&** `feature.enabled` del
  feature Meta Quest Support. El problema: `TabletBuild.SetLoaders` vacía `m_Loaders` **por
  `SerializedObject`**, que solo persiste el `.asset` (`XRGeneralSettingsPerBuildTarget.asset`) —
  pero `HasActiveLoader` lee `activeLoaders`, una **cache runtime en memoria** del
  `XRManagerSettings` que ese camino NUNCA refresca. Según en qué estado quedó esa cache de una
  sesión anterior del Editor (Play Mode, otro build, etc.), el hook corría o no — de ahí lo no
  determinista.
  **Intento 1 (ronda 1 — FALLÓ, revertido en ronda 2).** Dos capas en memoria: (a)
  `TabletBuild.BuildTablet()` llama además `manager.TrySetLoaders(new List<XRLoader>())` — la API
  pública de XR Management que SÍ sincroniza `activeLoaders` — restaurado con
  `TrySetLoaders(savedXrLoaders)` en el `finally`; **esta parte SÍ es correcta y se conserva** (ver
  Decisiones). (b) Encima, `TabletBuild` resolvía
  `OpenXRSettings.GetSettingsForBuildTargetGroup(BuildTargetGroup.Android).GetFeature<MetaQuestFeature>()`
  y ponía `enabled = false` durante el build (restaurado en el `finally`), para anular
  `IsExtensionEnabled` aunque la cache del loader quedara stale por cualquier otro motivo. **Esta
  parte (b) se retiró: verificado en build real (@build-deploy) que el APK seguía saliendo con
  `xr-keyboard-overlay-enabled=1` en `boot.config` pese al toggle** — el paquete OpenXR relee/
  persiste su propio estado (`MetaQuestFeatureBuildHooks.ApplySettingsOverride` →
  `AssetDatabase.SaveAssetIfDirty`) en momentos que este script no controla dentro del
  `BuildPipeline`. Peor: ese mismo hook, al correr con el feature en `enabled = false`, persistió
  `m_enabled: 0` **a disco** en `Assets/XR/Settings/OpenXR Package Settings.asset` — un asset
  **compartido con el visor** — dejando el working tree sucio con un cambio que, si se commiteara
  por error, apagaría Meta Quest Support también para el build del visor. @build-deploy lo revirtió
  con `git checkout -- "Assets/XR/Settings/OpenXR Package Settings.asset"`. **Lección: pelear
  contra el estado interno de un paquete de terceros durante el `BuildPipeline` (togglear un
  `ScriptableObject` que el propio paquete relee/persiste en su propio momento) es frágil — y en
  este caso, riesgoso para el visor.** Si `git status` muestra ese `.asset` modificado después de
  un build de tablet, es señal de que este bug volvió: revertir el archivo y revisar
  `TabletBuild.cs`/`TabletBootConfigPatcher.cs`.
  **Fix real (ronda 2 — determinista).** En vez de evitar que el hook escriba los flags, se los
  borra del `boot.config` YA GENERADO: `Assets/Scripts/Editor/TabletBootConfigPatcher.cs`
  implementa `IPostGenerateGradleAndroidProject` (`callbackOrder = 9999`, para correr después de
  cualquier otro postprocesador, incluido el propio hook de OpenXR) y, gateado por
  `TabletBuild.IsTabletBuildInProgress` (`true` solo dentro del `try` de `BuildTablet()`), abre
  `<path>/src/main/assets/bin/Data/boot.config` (con fallback a
  `<path>/../unityLibrary/src/main/assets/bin/Data/boot.config` si el `path` recibido es el del
  módulo `launcher` en vez de `unityLibrary`), borra toda línea cuyo key empiece con `xr-` y
  reescribe el archivo, logueando `[TabletBuild] boot.config: N flags xr-* eliminados`. Si el flag
  está armado y el archivo no se encuentra en ninguna de las dos rutas, `Debug.LogError` (no falla
  en silencio). En un build del visor `IsTabletBuildInProgress` es `false` y el patcher no toca
  nada — los flags `xr-*` quedan intactos, que es lo correcto para Quest. No requiere referencias
  nuevas a `Unity.XR.OpenXR`/`Unity.XR.OpenXR.Features.MetaQuestSupport` en
  `Simulador.Editor.asmdef` (esas se agregaron en el intento 1 para resolver `MetaQuestFeature` y
  se retiraron al revertirlo — el patcher solo necesita `UnityEditor.Android`, ya disponible sin
  referencia extra, igual que `AndroidPlatformIconKind` más arriba).
- **Pantalla negra en la tablet PHILCO (Unisoc/Mali) con el fix del loader XR ya aplicado — BUG
  REAL, RESUELTO.** Con el loader OpenXR apagado correctamente (loader del gotcha anterior
  descartado como causa), la app seguía sin presentar ningún frame en esa tablet específica.
  Diagnóstico verificado empíricamente (build experimental, 2026-07-16): el driver Vulkan del
  Unisoc/Mali de esa tablet no presenta frames — el swapchain queda mudo en silencio
  (`queued-frames=0`, sin excepción ni error visible), pero el mismo APK con GraphicsAPI
  OpenGLES3 renderiza perfecto. El proyecto tiene Android en Vulkan-only
  (`ProjectSettings/ProjectSettings.asset`, `m_APIs: 15000000`) porque el visor Quest lo necesita
  — no se puede cambiar ese default global sin romper el visor. **Fix:** `TabletBuild.BuildTablet()`
  guarda `PlayerSettings.GetGraphicsAPIs(BuildTarget.Android)` +
  `GetUseDefaultGraphicsAPIs(BuildTarget.Android)` ANTES de tocarlos (mismo momento que el resto
  del estado), en el `try` fuerza `SetUseDefaultGraphicsAPIs(Android, false)` +
  `SetGraphicsAPIs(Android, new[] { GraphicsDeviceType.OpenGLES3 })`, y restaura ambos SIEMPRE en
  el `finally` — mismo patrón try/finally que loader/identifier/icono. Vulkan queda intacto para
  el visor (build normal, fuera de `TabletBuild`). **Alcance:** esto es un workaround por
  hardware, no una preferencia general de GLES3 sobre Vulkan — si aparece OTRA tablet de destino
  con un driver Vulkan sano, seguiría buildeando en GLES3 igual (el swap no distingue modelos);
  reevaluar si algún día hace falta lo contrario.

- **`unity_build` / `unity_execute_menu_item` (Build Tablet) devuelven `Timed out after 30s
  waiting for main thread` en builds Android reales, pero el build SIGUE corriendo** — no es un
  fallo. `BuildPipeline.BuildPlayer` bloquea el hilo principal de Unity durante varios minutos
  (IL2CPP/Gradle); el bridge MCP tiene un timeout de 30s para la respuesta del tool, pero no
  cancela la operación en curso. Verificado en vivo (release 0.4.5, 2026-07-21): tanto el build
  del visor (`unity_build`) como el de la tablet (`unity_execute_menu_item` →
  `Simulador/Build Tablet (Android)`) devolvieron ese timeout y, sin embargo, el APK apareció
  completo y correcto minutos después. Durante el build, `unity_editor_ping`/
  `unity_get_compilation_errors` también pueden devolver "bridge not reachable" (el hilo
  principal está ocupado) — no confundir con el Editor caído. **Protocolo correcto:** tras el
  timeout, esperar (sleeps cortos, reintentando `unity_editor_ping` cada 1-2 min) y verificar el
  artefacto por filesystem (mtime/tamaño del APK en `builds/` o `Builds/Android/`) en vez de
  reintentar el build o asumir fallo. Solo tratar como fallo real si tras varios minutos el APK no
  aparece o el log muestra un `BuildReport` con `summary.result == Failed`.

## Cómo probar

1. **Tablet, camino feliz:** en el editor (target Android activo) ejecutar `Simulador → Build Tablet (Android)`. Esperar el log `[TabletBuild] Succeeded — 0 errores...` con la ruta `Builds/Android/Simulador.apk`.
2. **Verificar restauración del loader Y del identifier/nombre (P6.7):** tras el build, abrir
   *Project Settings → XR Plug-in Management → Android* y confirmar que OpenXR sigue tildado;
   abrir *Project Settings → Player → Android* y confirmar que `Package Name` volvió a
   `com.simulador.vr` y `Product Name` a `Simulador` (NO deben quedar en `com.simulador.tablet`/
   "Simulador Tablet"). `git status` no debe mostrar `Assets/XR/XRGeneralSettingsPerBuildTarget.asset`
   ni `ProjectSettings/ProjectSettings.asset` modificados.
2.b. **Verificar restauración del icono (Legacy/Round/Adaptive, no el genérico "Icon" del panel):**
   en *Project Settings → Player → Android → Icon*, las secciones **Adaptive**, **Round** y
   **Legacy** deben quedar vacías (sin textura asignada) tras el build — Android hereda entonces
   `icon_visor.png` del default del proyecto (sección "Default Icon" arriba del todo). Si alguna
   quedó con `icon_tablet.png` asignado, el `finally` no restauró (bug). Chequeo equivalente por
   código: `unity_execute_code` con `PlayerSettings.GetPlatformIcons(NamedBuildTarget.Android,
   UnityEditor.Android.AndroidPlatformIconKind.Adaptive)` (y `.Round`/`.Legacy`) — cada
   `PlatformIcon.GetTextures()` debe devolver `null`/vacío. **La verificación real del artefacto
   (no solo del estado del Editor) es extraer el PNG embebido del APK y compararlo por hash**
   contra `icon_tablet.png` (ver el gotcha resuelto más abajo para el comando exacto) —
   `aapt dump badging Builds/Android/Simulador.apk | grep application-icon` sin extraer el PNG NO
   alcanza para confirmar cuál ícono quedó embebido, solo que hay uno.
2.c. **Verificar el patch de `boot.config` (gotcha del teclado):** durante el build, el log de
   Unity (`unity_console_log` o la consola del Editor) debe mostrar
   `[TabletBuild] boot.config: N flags xr-* eliminados (...)` con `N > 0` (si el hook de OpenXR no
   escribió nada esa vez, `N` puede salir en `0` — no es un fallo, solo no había nada que limpiar).
   Si en cambio aparece `[TabletBuild] No se encontro boot.config para parchear...`, el patch NO
   corrió y el bug del teclado puede haber vuelto — investigar antes de instalar. Verificación
   directa del artefacto: el proyecto Gradle generado queda en
   `Library/Bee/Android/Prj/IL2CPP/Gradle/` — buscar `unityLibrary/src/main/assets/bin/Data/
   boot.config` ahí y confirmar que NO tiene ninguna línea que empiece con `xr-`
   (`grep "^xr-" boot.config` no debe devolver nada).
3. **Instalar y arrancar:**
   ```bash
   adb install -r Builds/Android/Simulador.apk
   adb shell monkey -p com.simulador.tablet 1
   adb logcat -s Unity   # debe verse "DataManager: catalogo v... cargado desde ..."
   ```
   **Prueba del teclado (gotcha de arriba):** en la pantalla que pide el PIN/login, tocar el campo
   de texto y confirmar que abre el teclado Android normal (no debe aparecer el log
   `"Oculus overlay keyboard is disabled..."` en `adb logcat`, ni quedar la pantalla sin teclado).
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
