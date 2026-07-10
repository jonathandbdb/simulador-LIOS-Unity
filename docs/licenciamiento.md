# Licenciamiento por dispositivo (visor)

## Qué es y por qué

Gate de arranque del visor Quest que exige que el dispositivo esté dado de alta y activo en el
backend antes de dejarlo operar con normalidad. Sin esto, cualquier APK del visor que llegara a
un dispositivo fuera de control (perdido, prestado, robado) seguiría funcionando indefinidamente.
El backend (`POST /api/verify`, ya desplegado) es la fuente de verdad: un dispositivo nuevo se
auto-registra `pending` en su primer intento y un administrador humano lo aprueba/rechaza/suspende
desde el panel (`/admin/devices`). Mismo patrón que `docs/catalogo-lentes.md` y
`docs/updates.md`: lógica de gate PURA y testeable (`LicenseLogic.cs`, namespace
`Simulador.License`, 26 tests en `LicenseLogicTests.cs`) + un `MonoBehaviour` (`LicenseManager`)
que orquesta IO/red/corrutinas.

**Estado actual: F3 completa + hardening fail-closed** (integración: `LicenseManager` +
`LicenseBlockScreenVR` + refactors compartidos en `Net`/`Data`). Validado contra un backend local
(`docker compose`) recorriendo los 5 estados de bloqueo, la gracia offline y la recuperación.
Tras la review de F3 se cerraron 2 MAYORES de diseño (ver "Fail-closed" más abajo): la premisa del
usuario es "bloqueo de app completa", así que (a) **cualquier** bloqueo (no solo el 403 del
servidor) corta la red del visor, y (b) la red YA NO se auto-crea al cargar la escena -- su
creación queda condicionada por completo al gate de licencia.

**Exclusivo del visor**: la tablet no tiene este gate (mismo criterio que
`NetworkController`/`UpdateManager.MaybeShowVrPrompt` — guard por presencia de
`TabletController` en escena, no por `Application.identifier`, ver `docs/tablet.md` y
`docs/updates.md`).

## Arquitectura actual

```
LicenseManager.Bootstrap (AfterSceneLoad, guard TabletController -- solo visor)
        │
        ▼
WaitUntil(DataManager.BackendConfigReady)
        │
        ▼
leer persistentDataPath/license_cache.json
        │
        ▼
LicenseLogic.EvaluateOffline(cache, DateTime.UtcNow)
        │
        ├─ AllowOfflineGrace ──────────────► la app arranca NORMAL (NetworkController ya
        │                                     bootstrapeó por su cuenta, ver más abajo)
        │
        └─ cualquier Block* ───────────────► Block(result, "Verificando licencia...")
                                              (LicenseBlockScreenVR YA visible, sin salida)
        │
        ▼ (en AMBOS casos, siempre)
POST {backend}/api/verify {device_id, current_apk_version}
        │
        ├─ 200 + TryParseVerifyOk       → escribir cache; si estaba bloqueado: Unblock() +
        │                                  NetworkController.EnsureCreated() + telemetría
        │                                  license_recovered
        ├─ 403 + TryParseVerifyDenied   → BORRAR cache; Block(MapDeniedReason(reason), message
        │                                  del server); si NetworkController.Instance existe →
        │                                  Destroy(su gameObject); telemetría license_denied
        ├─ 429                          → transitorio: NO tocar cache/estado; si ya estaba
        │                                  bloqueado, solo cambia el MENSAJE ("demasiados
        │                                  intentos..."), no el LicenseGateResult
        └─ inalcanzable / timeout /     → NO tocar cache; si offlineResult era grace: telemetría
           200-403 no parseables          license_offline_grace{days_left}; si era Block*:
                                           reemplaza el mensaje generico por el definitivo
                                           (MessageFor(result)) + telemetría license_blocked_offline
```

- `Assets/Scripts/Runtime/License/LicenseLogic.cs` — lógica PURA (preexistente a esta tarea,
  F1/F2): DTOs (`VerifyRequestDto`/`VerifyOkDto`/`VerifyDeniedDto`/`LicenseCacheDto`),
  `SerializeVerifyRequest`, `TryParseVerifyOk`/`TryParseVerifyDenied`, `MapDeniedReason`,
  `EvaluateOffline` (gracia de `GraceDays`=10 días), `BuildCacheJson`. Ver
  `Assets/Tests/EditMode/LicenseLogicTests.cs` (26 tests) para el detalle exacto de cada regla
  (fail-safe CERRADO ante reason desconocido → `BlockUnknown`, clamp de reloj adelantado,
  `license_expiry` vencida gana aunque el cache sea "fresco", etc.) — no se duplica acá.
- **`Assets/Scripts/Runtime/License/LicenseManager.cs`** (F3, nuevo) — `MonoBehaviour` singleton,
  bootstrap `[RuntimeInitializeOnLoadMethod(AfterSceneLoad)]` con el mismo guard
  `FindFirstObjectByType<TabletController>()` que `NetworkController.EnsureCreated()` (solo
  visor). Cache en `persistentDataPath/license_cache.json`. Corrutina desde `Start()`
  (`InitializeAsync`): espera `DataManager.BackendConfigReady`, evalúa el cache offline, bloquea
  YA si corresponde (mensaje genérico "Verificando licencia..." — lo reemplaza el resultado real
  del verify apenas termina, sea cual sea) y siempre dispara un verify contra el backend.
  - `public void RetryVerify()` — cooldown de `RetryCooldownSeconds` (15 s, ignora llamadas
    dentro de la ventana); lo llama el botón "A" de `LicenseBlockScreenVR` o el propio código
    (usado en el gate de esta tarea vía `unity_execute_code`).
  - `public static bool IsBlocked` / eventos `OnBlocked(LicenseGateResult, string message)` /
    `OnUnblocked` — `LicenseBlockScreenVR` no los necesita (lo crea/destruye directamente
    `LicenseManager`), quedan expuestos por si algo más de la UI necesita reaccionar a futuro.
  - Telemetría (`license_recovered`, `license_denied{reason}`, `license_offline_grace{days_left}`,
    `license_blocked_offline`) reusa `Simulador.Update.UpdateLogic.LogEvent`/`SerializeLogBatch`
    (lógica pura ya testeada, mismo contrato `POST /api/log` de `docs/updates.md` — no es
    específico de Update, es `{device_id, events[{event,detail}]}` genérico) + el nuevo
    `BackendTelemetry.PostJson` (ver abajo). Fire-and-forget: nunca bloquea el gate.
- **`Assets/Scripts/Runtime/License/LicenseBlockScreenVR.cs`** (F3, nuevo) — molde de
  `Simulador.Update.UpdatePromptVR`: canvas world-space por código, child de `Camera.main`, sin
  `GraphicRaycaster`/`EventSystem`. Lo crea/destruye `LicenseManager` (`AddComponent`/`Destroy`
  en `Block()`/`Unblock()`), nunca vive en una escena. Diferencias deliberadas con
  `UpdatePromptVR` (ver Decisiones): **sin salida** (no hay botón B/"cerrar"), fondo **opaco**
  (no semi-transparente) posicionado **muy cerca de la cámara** (0.2 m, con la escala reducida en
  la misma proporción — 0.0002 en vez de 0.0015 — para no agrandar el texto) para garantizar que
  tapa la escena por completo incluso con geometría de cockpit muy cercana a la cámara. Contenido:
  título fijo, mensaje (viene de `LicenseManager`, ya sea el genérico o el texto del server/
  `MessageFor`), `device_id` completo en fuente chica (para que el administrador lo identifique
  en el panel) y leyenda con cuenta regresiva del cooldown ("Reintentar en Ns..." / "A:
  reintentar"). Input propio (botón A, patrón `UpdatePromptVR`) + guard anti-restore en `Update()`
  que re-deshabilita `Vision.SimuladorInput` mientras `LicenseManager.IsBlocked` (cubre que
  `UpdatePromptVR` se abra encima y, al cerrarse, restaure el input de gameplay — los updates
  siguen funcionando con la licencia bloqueada, ver Decisiones).
- **`Assets/Scripts/Runtime/Net/NetworkController.cs`** — refactor quirúrgico: el cuerpo del
  bootstrap se extrajo a `public static void EnsureCreated()` (idempotente: no-op si `Instance`
  existe o si hay `TabletController` en escena). **Ya NO tiene un `[RuntimeInitializeOnLoadMethod]`
  propio** (fail-closed, ver sección arriba): la creación depende por completo de que
  `LicenseManager` la llame -- al arrancar por gracia offline, tras un verify 200 OK (siempre,
  idempotente) o al desbloquear. Nada más cambió en `Net/` — ver `docs/networking.md` (cross-ref).
- **`Assets/Scripts/Runtime/Data/BackendTelemetry.cs`** (nuevo) — `static IEnumerator
  PostJson(string url, string json, string logPrefix, int timeoutSeconds = 5)`: extrae el cuerpo
  que antes vivía solo en `UpdateManager.SendTelemetryAsync` (JSON vía `UploadHandlerRaw`,
  timeout corto, degradación sin excepción). `UpdateManager` y `LicenseManager` lo comparten —
  ver `docs/updates.md` (cross-ref).

## Contrato de `POST /api/verify` (backend, ya desplegado)

Request:
```json
{"device_id": "<SystemInfo.deviceUniqueIdentifier>", "current_apk_version": "<Application.version>"}
```

Respuesta `200` (dispositivo activo):
```json
{"status": "ok", "device_name": "Visor Consultorio 1", "license_expiry": "2026-12-31", "message": "..."}
```
`license_expiry` puede venir `null` (sin vencimiento).

Respuesta `403` (dispositivo bloqueado), con uno de estos 5 `reason`:
```json
{"status": "denied", "reason": "DEVICE_PENDING", "message": "..."}
```

| `reason` | `LicenseGateResult` | Significado |
|---|---|---|
| `DEVICE_PENDING` | `BlockPending` | Recién auto-registrado, esperando aprobación del admin. |
| `DEVICE_REJECTED` | `BlockRejected` | El admin lo rechazó explícitamente. |
| `DEVICE_SUSPENDED` | `BlockSuspended` | Estaba activo, el admin lo suspendió. |
| `LICENSE_EXPIRED` | `BlockExpired` | `license_expiry` ya pasó (lo detecta el backend; el gate offline también lo detecta localmente, ver `EvaluateOffline`). |
| `DEVICE_NOT_FOUND` | `BlockNotFound` | No debería ocurrir en el flujo normal (el backend auto-registra); cualquier otro caso raro. |
| *(cualquier otro, incluido ausente)* | `BlockUnknown` | Fail-safe CERRADO — reason nuevo del backend a futuro, el visor sigue bloqueando. |

`429` — rate limit (10/min/IP, ver backend): transitorio, no es un `reason` de negocio.

## Flujo de estados del visor (diagrama de decisión)

```
                    ┌─────────────────────┐
                    │ leer license_cache   │
                    └──────────┬───────────┘
                               ▼
                  EvaluateOffline(cache, utcNow)
                   /                         \
        AllowOfflineGrace                  Block*
                 │                            │
                 ▼                            ▼
        app corre YA (normal)        Block(result, "Verificando...")
        + Net.EnsureCreated()        (corta Net.Instance si existia --
        YA (fail-closed)              corte de red en TODO bloqueo)
                 │                            │
                 └────────────┬───────────────┘
                              ▼
                    POST /api/verify (siempre)
                    /       |        |        \
                 200       403      429    inalcanzable/
                  │         │         │      no-parseable
                  ▼         ▼         ▼         │
              Unblock   Block(mapped  (solo    ┌┴──────────────┐
              (si       reason)      cambia    │ si offline era │
              estaba)   (corta Net   mensaje   │ grace: sigue   │
              + SIEMPRE si existia)  si ya     │ corriendo,     │
              Net.Ensure             estaba    │ solo telemetría│
              Created (idem-        bloqueado)│ si offline era │
              potente)                        │ Block*: queda  │
                                               │ bloqueado con  │
                                               │ el mensaje real│
                                               │ (y la red sigue│
                                               │ cortada)       │
                                               └────────────────┘
```

## Fail-closed: la red del visor solo existe con licencia válida

Hardening posterior a la integración inicial de F3 (2 MAYORES de una review): la premisa del
usuario para este gate es **"bloqueo de app completa"**, no "bloqueo de la UI de lentes con la red
todavía arriba". Dos cambios sobre `NetworkController`/`LicenseManager`:

1. **`NetworkController` ya NO se auto-crea al cargar la escena.** Antes tenía su propio
   `[RuntimeInitializeOnLoadMethod]` que llamaba `EnsureCreated()` directo -- eso levantaba el
   WebSocketServer/DiscoveryBeacon ANTES de que el gate de licencia terminara de decidir (mientras
   corre `WaitUntil(BackendConfigReady)` + el verify HTTP), dejando un visor eventualmente denegado
   igual de descubrible/conectable por una tablet durante esa ventana. Ahora `EnsureCreated()`
   sigue existiendo (sigue siendo idempotente: no-op si ya hay `Instance` o si hay
   `TabletController` en escena) pero **nadie la llama automáticamente**: es
   `Simulador.License.LicenseManager` quien decide cuándo, y solo en 3 puntos, todos dentro del
   propio gate:
   - `InitializeAsync`, en la rama `AllowOfflineGrace` (la lectura del cache es síncrona, así que
     en el caso común -- dispositivo activo, cache fresco -- la red se levanta enseguida, sin
     esperar el verify).
   - `HandleOk` (verify 200), SIEMPRE (no solo si `wasBlocked` -- `EnsureCreated()` es idempotente,
     así que llamarla incondicionalmente es más simple y cubre cualquier caso donde el arranque
     fail-closed no la hubiera levantado todavía).
   - Al desbloquear (mismo `HandleOk`, justo después de `Unblock()`).

   La tablet (escena con `TabletController`, sin `LicenseManager`) nunca dependió de este
   bootstrap: `EnsureCreated()` ya la excluía por su propio guard, y sigue sin levantar server
   porque nadie la necesita del lado cliente.

2. **`LicenseManager.Block()` corta la red en TODO bloqueo, no solo en el 403 del servidor.**
   Antes solo `HandleDenied` (403) destruía `NetworkController.Instance`; un `BlockOffline`/
   `BlockExpired` decidido LOCALMENTE (sin ni siquiera llegar a hablar con el backend) dejaba la
   red arriba si ya se había levantado por otra vía. Ahora el `Destroy(NetworkController.Instance
   .gameObject)` vive dentro de `Block()` mismo -- se ejecuta para cualquier
   `LicenseGateResult`, sea el motivo offline o la respuesta explícita del servidor. Combinado con
   el punto 1 (la red nunca se crea sola), el resultado neto es: **la red del visor existe si y
   solo si la licencia está OK** (por gracia offline o por verify 200), nunca en ningún estado de
   bloqueo.

## Gracia offline (10 días) y sus reglas

- `LicenseLogic.GraceDays = 10`. Se mide desde `verified_at` (timestamp ISO-8601 UTC del ÚLTIMO
  verify 200 exitoso, persistido en el cache) hasta `DateTime.UtcNow` en el momento de evaluar.
- **`license_expiry` vencida gana aunque el cache sea "fresco"**: si el cache tiene
  `license_expiry` y ya pasó, `EvaluateOffline` devuelve `BlockExpired` sin importar cuán
  reciente sea `verified_at` — la gracia offline es sobre "¿hace cuánto no hablamos con el
  backend?", no sobre "¿la licencia sigue vigente?" (eso lo dice `license_expiry`
  independientemente).
- **Cache corrupto/ilegible/sin `verified_at` parseable → `BlockOffline`** (fail-safe cerrado:
  más seguro bloquear que confiar en un dato que no se pudo leer).
- **Reloj del dispositivo adelantado**: si `verified_at` quedara en el "futuro" respecto de
  `utcNow` (reloj mal seteado), se clampea a `utcNow` — no debe regalar más días de gracia, pero
  tampoco debe brickear la app por una diferencia de reloj.
- **La gracia NO es un segundo canal de confianza**: solo determina si la app arranca
  YA (sin esperar red) o queda bloqueada YA; en AMBOS casos el verify real contra el backend
  siempre se dispara en background y su resultado (200/403/429/inalcanzable) es quien decide el
  estado final — la gracia nunca "gana" contra un 403 real que llegue después.

## Decisiones y porqués

- **Cache sin firma — modelo de amenaza aceptado**: `license_cache.json` es JSON plano sin
  firma/cifrado (mismo nivel que `pairing.json`/`paired_tokens.json` de `docs/networking.md`).
  Un usuario con acceso al filesystem del dispositivo (adb, root) podría editarlo a mano para
  extender la gracia offline. Se acepta porque el modelo de amenaza de este proyecto es
  "dispositivo de consultorio, no un adversario con acceso físico sostenido" (mismo criterio que
  el resto del proyecto — ver Modelo de amenaza en `docs/networking.md`); si se necesitara
  blindarlo, la vía natural es firmar el cache con una clave embebida en el binario (HMAC), pero
  eso es deuda futura, no parte de esta tarea.
- **403 SIEMPRE borra el cache** (no solo bloquea): un dispositivo rechazado/suspendido/vencido
  no debe poder "volver a la gracia offline" simplemente perdiendo la conexión después — borrar
  el cache fuerza que la PRÓXIMA vez que el backend sea alcanzable, el resultado real se vuelva a
  evaluar desde cero, y que si el backend sigue inalcanzable, `EvaluateOffline` dé `BlockOffline`
  (sin cache) en vez de potencialmente reusar un cache viejo que ya no aplica.
- **Los updates (`Simulador.Update`) siguen funcionando con la licencia bloqueada, deliberado**:
  `LicenseManager`/`LicenseBlockScreenVR` no deshabilitan `UpdateManager` ni interceptan su
  cartel (`UpdatePromptVR`) — un dispositivo bloqueado (p. ej. `BlockPending` recién instalado, o
  suspendido temporalmente) igual debe poder recibir un update crítico del backend mientras
  espera la aprobación/reactivación. `LicenseBlockScreenVR.Update()` tiene el guard anti-restore
  específicamente para este caso: si `UpdatePromptVR` se abre por encima (ambos son canvases
  world-space independientes, pueden coexistir) y se cierra, su `OnDestroy` restaura
  `SimuladorInput.enabled = true` — sin el guard, eso "reactivaría" el ciclo de lentes de fondo
  mientras la licencia sigue bloqueada. Guard **simétrico** en el otro sentido (hallado en
  review): si es `LicenseBlockScreenVR` quien se destruye (verify OK) con `UpdatePromptVR`
  todavía visible por encima, su `RestoreGameplayInput` NO reactiva `SimuladorInput` -- deja que
  sea `UpdatePromptVR` quien lo haga en su propio `OnDestroy` al cerrarse (mismo criterio: nunca
  reactivar el input de gameplay mientras cualquiera de los dos carteles siga tapando la pantalla).
- **Un dispositivo `DEVICE_REJECTED` nunca vuelve a `pending` solo**: es una decisión exclusiva
  del backend/admin (fuera del alcance de este cambio del lado Unity) — el visor solo mapea el
  `reason` que el servidor le manda, nunca reinterpreta ni "reintenta" un rechazo como si fuera
  pendiente. Si un dispositivo rechazado necesita otra oportunidad, es el administrador quien lo
  edita manualmente en el panel (`/admin/devices`).
- **Regla operativa: aprobar ANTES de entregar el dispositivo al clínico, suspender en vez de
  borrar** — igual que cualquier gate de aprobación humana, el flujo esperado en producción es
  que el administrador apruebe el dispositivo la PRIMERA vez que aparece `pending` (típicamente
  antes de la primera puesta en marcha real) en vez de dejar que el clínico se encuentre con el
  cartel de bloqueo. Para retirar temporalmente un dispositivo de servicio (mantenimiento, fin de
  contrato temporal, etc.), la acción correcta es **suspender**, no borrar el registro: suspender
  preserva el historial/nombre del dispositivo y es reversible con un solo click ("Aprobar" de
  nuevo); borrar el registro entero lo obligaría a re-registrarse desde cero como `pending` la
  próxima vez (mismo device_id, pero sin historial).
- **El Editor se auto-registra `pending` en el backend real** (comportamiento esperado y
  documentado, no un bug): `LicenseManager` corre igual en Play Mode del Editor que en un build
  real (mismo guard que `NetworkController`/`UpdateManager` — la ausencia de `TabletController`
  en `Main.unity` alcanza). Cada vez que alguien abre `Main.unity` y da Play con el backend de
  PRODUCCIÓN configurado (sin override de `config.json`), el `device_id` de esa máquina de
  desarrollo (`SystemInfo.deviceUniqueIdentifier`, estable por instalación de Unity/SO) va a
  auto-registrarse `pending` contra prod si todavía no existe ahí. Es inofensivo (queda pending,
  no afecta a ningún dispositivo real) pero puede generar entradas `pending` "ruido" en el panel
  de producción con nombres no descriptivos — el admin puede simplemente ignorarlas o borrarlas.
  Para validar este sistema sin tocar prod, usar el override de
  `persistentDataPath/config.json` apuntando a un backend local (ver Cómo probar).

## Gotchas (hallados validando esta tarea)

- **`UnityWebRequest.result` NO es `Success` para NINGÚN código HTTP ≥ 400** — reporta
  `ProtocolError` igual para un `403` "legítimo" (con body parseable, `TryParseVerifyDenied`
  hubiera funcionado) que para un `500` sin body. Gatear "inalcanzable" por `req.result !=
  Success` (como hace, con cuidado, el resto del proyecto en casos donde el único código de
  éxito relevante es 200 — ver `UpdateManager.CheckManifest`) es un bug si el endpoint puede
  devolver 4xx/429 con contenido útil: se pierde el 403 (nunca llega a parsearse como denied).
  Fix en `LicenseManager.Verify()`: el gate de "inalcanzable" es por `req.responseCode == 0`
  (verdaderamente sin respuesta — timeout, conexión rechazada, DNS, backend caído), NO por
  `req.result`. Con `responseCode != 0` siempre se lee `req.downloadHandler.text` y se intenta
  parsear como 200/403/429 antes de rendirse. **Si se toca `UpdateManager.CheckManifest` a
  futuro y se necesita distinguir un código de error CON body útil, aplicar el mismo criterio.**
- **`Destroy(componente)` no destruye el GameObject que el componente creó por su cuenta** —
  `LicenseBlockScreenVR` construye su canvas (`_canvasGo`) como un GameObject NUEVO, hijo de la
  cámara, NO hijo de su propio `transform` (mismo patrón que `UpdatePromptVR._canvasGo`). Pero a
  diferencia de `UpdatePromptVR` (que se autodestruye con `Close()`, y ahí mismo hace
  `Destroy(_canvasGo)` + `Destroy(this)` juntos), a `LicenseBlockScreenVR` lo destruye
  `LicenseManager.Unblock()` desde AFUERA con `Destroy(_blockScreen)` — eso solo destruye el
  **componente**, dejando `_canvasGo` huérfano en la escena, siguiendo renderizado
  indefinidamente (bug real: el cartel de bloqueo seguía visible en pantalla después de
  desbloquear, aunque `LicenseManager.IsBlocked` ya fuera `false`). Fix: `OnDestroy()` de
  `LicenseBlockScreenVR` ahora también hace `Destroy(_canvasGo)`. **Regla general para cualquier
  componente cuyo ciclo de vida lo controle OTRO objeto (no él mismo)**: si el componente crea
  GameObjects propios que no son hijos de su propio transform, su `OnDestroy()` debe limpiarlos
  explícitamente — no alcanza con que Unity destruya el componente.
- **Tamaño/distancia del canvas world-space para "tapar la escena por completo" no es trivial**:
  mover el canvas más cerca de la cámara (necesario para ganarle a geometría de cockpit muy
  cercana, ver arriba) sin achicar la ESCALA en la misma proporción agranda el texto
  visualmente (el tamaño angular percibido depende de `worldSize / distancia`, y `worldSize =
  sizeDelta * scale` es independiente de la distancia). Regla usada acá: si se reduce la
  distancia por un factor k, reducir también la escala por el mismo factor k para mantener el
  tamaño angular original del texto, y ajustar `sizeDelta` (en unidades de canvas) para que seguir
  cubriendo el FOV requerido a la nueva distancia.
- **`Application.runInBackground == false` (default del proyecto) congela el player loop del
  Editor en Play Mode cuando la ventana no tiene foco de SO** — no es un throttle suave, es un
  freeze real (`Time.frameCount` se queda clavado en `1`, minutos de reloj real sin avanzar un
  solo frame), reproducido validando esta tarea vía MCP (que nunca le da foco de SO a la ventana
  del Editor). Ya documentado como "throttle agresivo" en `docs/updates.md`, pero acá se confirmó
  que en este entorno de automatización es un freeze TOTAL, no solo lento. Mitigación que
  funcionó: `Application.runInBackground = true` vía `unity_execute_code` INMEDIATAMENTE después
  de entrar a Play Mode (no persiste entre sesiones de Play, hay que repetirlo cada vez). Esto es
  puramente una característica del Editor sin foco de SO — no reproducible en un build real ni
  relevante para dispositivo (Quest/tablet siempre tienen foco).
- **`unity_screenshot_game` puede devolver un archivo con un frame viejo cacheado** (mismo
  gotcha ya documentado en `docs/updates.md`) — para evidencia confiable de esta tarea se usó
  `unity_graphics_game_capture` (devuelve la imagen inline, no un archivo) que sí reflejó el
  estado real en cada captura tras forzar avance de frames con `runInBackground=true`.

## Cómo probar

1. **EditMode**: `Simulador → Run EditMode Tests` — `LicenseLogicTests` (26 tests) + el resto de
   la suite deben seguir en verde (86/86 al cerrar F3; no se agregaron tests nuevos en esta tarea
   porque no se tocó lógica pura, solo el `MonoBehaviour`/UI).
2. **Contra un backend local** (`docker compose up` en `backend/`): override
   `persistentDataPath/config.json` → `{"backend_url":"http://localhost:8080"}` (ver capas en
   `docs/catalogo-lentes.md`). **Borrar el override al terminar** — no es config de producción,
   y dejarlo puesto hace que el Editor apunte a local indefinidamente.
3. **Bloqueado "pendiente"**: con `license_cache.json` inexistente y el `device_id` del Editor
   (`SystemInfo.deviceUniqueIdentifier`, vía `unity_execute_code`) SIN registrar en la BD local →
   Play en `Main.unity` → debe bloquear con "Esperando aprobación del administrador.", el device
   debe aparecer `pending` en `/admin/devices`, y `NetworkController.Instance` debe quedar `null`
   (fail-closed: nunca llegó a crearse -- ni el bootstrap automático, que ya no existe, ni
   `LicenseManager`, que solo la crea en el camino OK/gracia).
4. **Desbloqueo**: aprobar el dispositivo (`POST /admin/devices/{id}/approve` con cookie de
   sesión admin, o desde el panel) → `LicenseManager.Instance.RetryVerify()` (botón "A" en VR
   real, o por código en el Editor) → debe desbloquear, escribir `license_cache.json`, y recrear
   `NetworkController` (log `WebSocketServer: escuchando en :9090` de nuevo, DESPUÉS de
   `License: verify OK`).
5. **Suspendido**: suspender el dispositivo (`status=suspended` vía el panel) con un
   `license_cache.json` vigente → Play → arranca por gracia offline (log "cache local dentro de
   la gracia offline", que además levanta la red YA -- ver `WebSocketServer: escuchando en :9090`
   inmediatamente después de ese log, sin esperar el verify), el verify en background debe
   bloquear con "Este dispositivo esta suspendido." y BORRAR el cache -- `Block()` corta esa
   misma red que la gracia había levantado (`NetworkController.Instance` vuelve a `null`).
6. **Gracia offline**: re-aprobar, `RetryVerify()` para regenerar el cache, salir de Play →
   `docker compose stop caddy` (el proxy que expone :8080) → Play → debe correr NORMAL (sin
   bloquear), log `License: cache local dentro de la gracia offline...`, y un intento de
   telemetría `license_offline_grace` que falla silenciosamente (backend caído, esperado).
7. **Sin conexión, sin cache**: con el backend caído, borrar `license_cache.json` → Play → debe
   bloquear con "Sin conexión. Conecte el visor a internet y reintente." — fail-closed: la red
   nunca llega a crearse en este camino (offline evalúa `Block*` de entrada, `Block()` no tiene
   nada que destruir), a diferencia de antes de este hardening (solo el 403/denied la destruía,
   dejando este caso con la red arriba si ya se había levantado por otra vía).
   `docker compose start caddy` al terminar.
8. **Gotcha de Editor sin foco**: si los logs no avanzan más allá de la carga inicial durante
   varios segundos reales, ejecutar `UnityEngine.Application.runInBackground = true;` vía
   `unity_execute_code` (ver Gotchas) antes de seguir esperando.
9. **En dispositivo real (Quest, requiere build)**: instalar el APK, con el dispositivo sin
   aprobar en el backend real → debe bloquear igual que en el Editor, y el administrador debe
   poder aprobarlo desde el panel usando el `device_id` visible en el propio cartel de bloqueo.
10. **Fail-closed, verificado en esta tarea (Editor, backend local)**: (a) con `license_cache.json`
    vigente y el dispositivo `active`, Play → secuencia real observada:
    `License: cache local dentro de la gracia offline...` → `Net: PIN de emparejamiento...` →
    `WebSocketServer: escuchando en :9090` → recién DESPUÉS `License: verify OK` -- confirma que
    la red la levanta el gate (gracia offline), no un bootstrap automático. (b) con el dispositivo
    `suspended` en la BD y `license_cache.json` borrado, Play → secuencia observada:
    `License: bloqueado (BlockOffline)...` → `License: bloqueado (BlockSuspended)...`, con CERO
    apariciones de `WebSocketServer`/`DiscoveryBeacon` en toda la sesión (varios segundos de
    margen tras el segundo bloqueo) -- confirma que un dispositivo bloqueado nunca llega a tener
    la red arriba.

## Pendientes / deuda

- **Cache sin firma** (ver Decisiones) — aceptado para el modelo de amenaza actual.
- **Sin UI de administración de "días de gracia restantes" en el visor** — el cartel de bloqueo
  no muestra un contador de gracia mientras la app corre normal por gracia (solo hay telemetría
  `license_offline_grace{days_left}` hacia el backend); si hiciera falta avisar al clínico
  proactivamente ("la licencia offline vence en N días"), es agregar un HUD/aviso no bloqueante,
  fuera del alcance de F3.
- **`LicenseManager`/`LicenseBlockScreenVR` no tienen tests de lógica pura propios** porque no
  contienen lógica pura nueva (toda la lógica de gate ya estaba en `LicenseLogic.cs`, F1/F2,
  cubierta por `LicenseLogicTests.cs`); si a futuro se extrae algo puro nuevo de estas clases
  (p. ej. `ComputeDaysLeft`), extender `LicenseLogicTests.cs` o el `DataLogicTests.cs` según
  corresponda en esa misma tarea.
- **F4 (fuera de esta tarea)**: validación E2E en un Quest real con un build firmado, incluyendo
  confirmar que el flujo completo (pending → aprobar → activo → suspender → reactivar) se ve y
  se comporta igual que en el Editor, y que la telemetría `license_*` llega a `/admin/logs` desde
  un dispositivo real.
