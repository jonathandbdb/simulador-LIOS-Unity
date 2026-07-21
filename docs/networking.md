# Networking visor ↔ tablet

## Qué es y por qué
Capa de comunicación LAN entre el visor Quest (que corre la simulación de lentes intraoculares) y la
tablet Android de control del consultorio. El visor emite un beacon UDP para ser descubierto sin IP
manual, sirve un WebSocket con el protocolo de comandos/estado y transmite la vista del paciente como
JPGs binarios. Todo es un port de la versión Godot (`streaming_server.gd`, `discovery_beacon.gd`).

## Arquitectura actual

| Archivo | Rol |
|---|---|
| `Assets/Scripts/Runtime/Net/NetworkController.cs` | Orquestador del lado visor. **Ya NO tiene un `[RuntimeInitializeOnLoadMethod]` propio** (fail-closed, hardening posterior a F3 — ver `docs/licenciamiento.md`): `public static void EnsureCreated()` (idempotente: no-op si ya hay `Instance` o si la escena tiene un `TabletController` — ahí la app es cliente y NO levanta server) sigue existiendo, pero la creación depende por completo de que `Simulador.License.LicenseManager` la llame — al arrancar por gracia offline, tras un verify 200 OK (siempre, idempotente) o al desbloquear tras un bloqueo. Antes de este hardening había un bootstrap automático al cargar la escena que dejaba la red arriba (y descubrible por una tablet) mientras el gate de licencia todavía estaba decidiendo; ahora la red del visor existe si y solo si la licencia está OK. Genera el `PairingPin` (6 dígitos) de la sesión, arranca `WebSocketServer` (:9090), `DiscoveryBeacon` (:9091) y `StreamingCapture`; valida el emparejamiento por PIN (capa de protocolo) y traduce comandos JSON a llamadas sobre `DataManager`, `GlareController`, `ScenarioManager` y (`set_hud`) el `HudController` de `Vision/` (referencia resuelta y cacheada on-demand con `FindObjectsInactive.Include`, sin editar `HudController.cs` — frontera con `Vision/`). |
| `Assets/Scripts/Runtime/Net/WebSocketServer.cs` | Servidor WebSocket RFC 6455 hecho a mano sobre `TcpListener`/`NetworkStream`. Handshake HTTP, framing, ping→pong, broadcast texto/binario. Guarda el flag `Authenticated` por cliente (lo fija `NetworkController` tras validar el PIN) y filtra los broadcasts por ese flag. |
| `Assets/Scripts/Runtime/Net/WebSocketClient.cs` | Cliente WebSocket espejo (lado tablet). Handshake cliente, lectura de frames texto/binario, envío de texto **enmascarado** (obligatorio cliente→servidor por RFC 6455). |
| `Assets/Scripts/Runtime/Net/StreamingCapture.cs` | Captura la vista del paciente (cámara propia que sigue la XR camera) y la broadcastea como JPG con header de 1 byte por ojo. |
| `Assets/Scripts/Runtime/Net/DiscoveryBeacon.cs` | Beacon UDP del visor: broadcast a `255.255.255.255:9091` cada 2 s. |
| `Assets/Scripts/Runtime/Net/DiscoveryListener.cs` | Lado tablet: escucha :9091 en un thread y encola `(IP, device_label)` de los visores detectados — parsea el `device_label` con `Newtonsoft.Json.Linq.JObject` en el propio thread (dato puro, no toca API de Unity) para que la tablet pueda mostrar un nombre amigable en vez de la IP (ver `docs/tablet.md`). |
| `Assets/Scripts/Runtime/Net/PairingStore.cs` | Logica PURA (sin Unity, sin IO) del emparejamiento persistente por token: genera el token (`GenerateToken`) y serializa/deserializa las dos formas que persiste el protocolo (lista de tokens del visor, mapa host→token de la tablet). La usan tanto `NetworkController` como `TabletSession`. Cubierta por `Assets/Tests/EditMode/PairingStoreTests.cs`. |
| `Assets/Scripts/Runtime/Tablet/TabletSession.cs` (P6.2) | Consumidor de red del lado tablet: usa `DiscoveryListener` + `WebSocketClient`, parsea `hello`/`vision_state`, separa el header de los JPG del stream y expone eventos hacia `TabletController` (UI). Antes de P6.2 esto vivía en `TabletController.cs`; detalle completo del split en `docs/tablet.md`. Tambien posee el emparejamiento persistente por token (mapa host→token en `persistentDataPath/pairing.json`). |
| `Assets/Scripts/Runtime/Net/TabletController.cs` | Capa de UI de la tablet (namespace `Simulador.Tablet` desde P6.2): consume los eventos de `TabletSession` y decodifica los JPG en `RawImage` por ojo (detalle en `docs/tablet.md`). |

```
        VISOR (Quest)                                TABLET (Android)
 ┌──────────────────────────┐                 ┌──────────────────────────┐
 │ NetworkController        │                 │ TabletSession (P6.2)     │
 │  ├ DiscoveryBeacon ──────┼─UDP bcast:9091─▶│  ├ DiscoveryListener     │
 │  ├ WebSocketServer :9090 │◀──WS connect────┤  ├ WebSocketClient       │
 │  │   ◀── {"type":"auth","pin":...|"token":...}  (1er mensaje, masked)  │
 │  │   ──▶ auth_ok[+token] | auth_fail[+reason] | auth_locked  │  │      │
 │  │   ◀── {"cmd": ...}    │  (solo tras auth_ok)                       │
 │  │   ──▶ hello / vision_state (texto, solo a autenticados)            │
 │  └ StreamingCapture ─────┼──binario [B|L|R]+JPG (solo a autenticados)─▶ eventos ─▶ TabletController (UI) ─▶ RawImages por ojo │
 └──────────────────────────┘                 └──────────────────────────┘
```

## Decisiones y porqués
- **WebSocket implementado a mano sobre `System.Net.Sockets`** → el docstring de `WebSocketServer.cs`
  lo dice explícito: *"System.Net.WebSockets server-side (HttpListener) no es confiable en
  IL2CPP/Android (Quest). Esto solo usa System.Net.Sockets, que sí funciona."* Se implementa lo
  mínimo del RFC 6455 (handshake, framing, ping/pong, close).
- **UDP broadcast para descubrimiento (puerto 9091)** → la tablet encuentra al visor sin tipear IP.
  El beacon manda `{"app":"simulador-vr","device_label":"<nombre>-<nonce>","ws_port":9090,"ts"}`
  cada 2 s; el listener filtra por el tag `simulador-vr` y usa la IP de ORIGEN del paquete (no la
  del payload) para IDENTIFICAR al visor — eso no cambió. `device_label` sí se parsea desde la
  tarea "SSID + lista sin IP" (ver `docs/tablet.md`) para uso puramente presentacional: la tablet
  lo usa como nombre amigable en la lista de visores descubiertos, nunca como clave de
  `_seenHosts`/`_tokenByHost` (ver Gotchas).
- **`device_label` no es un identificador de hardware (P1.5)** → hasta esta tarea el beacon mandaba
  `SystemInfo.deviceUniqueIdentifier` en el payload, broadcasteado sin auth ni cifrado a toda la
  subred: cualquiera escuchando en :9091 podía recolectar el ID de hardware estable del visor sin
  ninguna necesidad (el receptor nunca lo usó — ver arriba). `NetworkController.GenerateBeaconLabel()`
  lo reemplaza por `SystemInfo.deviceName` (saneado de `"`/`\` porque `DiscoveryBeacon.Tick` arma el
  JSON a mano, sin escaping) + un nonce de 8 hex generado por corrida (`Guid.NewGuid()`,
  independiente del `PairingPin`: no se deriva de él ni permite reconstruirlo). Si el visor
  reinicia, cambian PIN y nonce por igual — no hace falta persistirlo.
- **`blend_active` es la única fuente de verdad del modo blend (P2.1)** → antes la tablet decidía
  el split de panes con su propia heurística (`leftId != rightId`), que rompía con un solo ojo con
  lente (2 panes, uno con etiqueta vacía: `leftId="monofocal"`, `rightId=""` da `leftId != rightId`
  = `true`). `NetworkController.BuildVisionState()` agrega `blend_active` (bool, de
  `DataManager.BlendModeEnabled` = `LensEngine.ComputeBlend`, que exige AMBOS ojos con lente Y
  distintas) al `vision_state` — se manda en el `hello` (via `BuildVisionState()`) y en cada
  `vision_state` posterior. La tablet lee ESE campo en `RefreshVisionUI()` en vez de recalcularlo.
- **Astigmatismo por ojo (P2.2, cerró la deuda de asimetría)** → `NetworkController` ahora lee
  `cmd["eye"]` (default `"both"`, misma convención que `apply_lens`/`override_params`) y llama
  `GlareController.SetAstigmatism(eye, enabled, magnitude, angle)` (firma per-eye, ver
  `docs/vision-optica.md`). Se eliminó la sobrecarga legacy `SetAstigmatism(enabled, magnitud,
  ángulo)` de `GlareController` (aplicaba siempre a `"both"`) — no queda código muerto.
- **Escenarios por id, no por label (P2.3, CERRADO)** → `hello.scenarios` pasó de `["consultorio",
  "ruta_noche"]` (strings) a `[{"id":"consultorio","label":"Consultorio"}, ...]`; la tablet
  selecciona/resalta el botón activo por `id` (antes comparaba el TEXTO del label del botón, que
  rompía con labels duplicados). `load_scenario` ahora manda `{"cmd":"load_scenario","id":"<id>"}`
  (el visor sigue aceptando el campo `"scenario"` por compat, mismo significado: siempre fue un
  id). La LISTA/ORDEN de ids ya no se duplica: `@vision-optics` agregó
  `ScenarioManager.ScenarioOrder` (`public static IReadOnlyList<string>`, Vision/) y
  `NetworkController.BuildScenarioList()` la lee de ahí — solo el TEXTO del label (que no vive en
  Vision/) sigue en un `Dictionary<string,string>` propio de Net (`NetworkController.
  ScenarioLabels`); un id sin entrada en ese diccionario cae a un fallback que capitaliza el id,
  así que nunca hay un escenario sin label.
- **`refresh` reusa `BuildHello()` en vez de un mensaje de respuesta nuevo (P5.4)** → el payload
  que necesita la tablet para reconstruir catálogo/escenarios/vision_state en caliente es
  IDÉNTICO al del `hello` inicial, así que `NetworkController` arma exactamente ese JSON
  (`_server.SendTextTo(id, BuildHello())`, solo al cliente que lo pidió) y del lado tablet
  `OnText` no necesita ningún branch nuevo: el `else if (type == "hello")` que ya procesa el hello
  de la conexión (y el de una reconexión exitosa, P2.5) también procesa la respuesta de
  `refresh`. Cero parsing nuevo, cero mensajes nuevos del visor — solo un comando entrante más.
- **Todos los eventos de red se drenan con `PumpEvents()` desde `Update()`** → los sockets corren en
  threads pero la API de Unity solo puede tocarse desde el hilo principal; las colas
  `ConcurrentQueue` desacoplan ambos mundos.
- **Stream con header de 1 byte + JPG** → protocolo binario trivial: `'B'` (0x42) ambos ojos,
  `'L'` (0x4C) izquierdo, `'R'` (0x52) derecho. En modo blend (lentes distintas por ojo) se capturan
  y mandan L y R en cada tick, así cada ojo mantiene la tasa completa.
- **Render on-demand + encode en thread aparte** (`StreamingCapture`) → la cámara de captura está
  `enabled = false` y se renderiza con `RenderPipeline.SubmitRenderRequest` solo cuando toca enviar
  (20 Hz); el JPG se codifica con `ImageConversion.EncodeArrayToJPG` dentro de un `Task.Run` para no
  bloquear el hilo principal. `BroadcastBinary` es thread-safe (lock de escritura por cliente).
- **La cámara de stream usa el mismo renderer URP** → el JPG ya incluye el post-proceso de visión y
  glare: la tablet ve *lo que ve el paciente*. El global `_StreamForceEye` (1=izq, 2=der) fuerza el
  ojo en los shaders porque la cámara de captura es mono.
- **El visor confirma estado, la tablet no asume** → tras cada comando el `DataManager` dispara
  `VisionStateChanged` y `NetworkController` broadcastea el `vision_state` completo; la tablet
  sincroniza sus sliders desde ahí (con `SetValueSilent` para no re-emitir).
- **Emparejamiento por PIN (P1.1)** → el canal no tenía auth (ver Modelo de amenaza). Se agregó un
  PIN de 6 dígitos generado por sesión del visor (`NetworkController.PairingPin`, propiedad
  pública read-only para que el HUD lo muestre — eso lo consume `Vision/`, fuera de este cambio;
  también se expone `AuthenticatedClientCount` como passthrough de
  `WebSocketServer.AuthenticatedClientCount`, para que el HUD sepa si hay una tablet emparejada).
  El primer mensaje de cada cliente DEBE ser `{"type":"auth","pin":"NNNNNN"}`; hasta que no llega
  un PIN correcto, el server no manda `hello` y `BroadcastText`/`BroadcastBinary` (vision_state,
  stream JPG) lo excluyen. La validación vive en la capa de protocolo
  (`NetworkController.HandleAuthAttempt`, llamada desde `OnTextReceived` en `PumpEvents`), no en
  `WebSocketServer`: el server solo guarda el flag `Authenticated` por cliente y expone
  `MarkAuthenticated`/`IsAuthenticated`/`IsClientOpen`/`ForceDisconnect`/`AuthenticatedClientCount`.
- **Lockout con ventana temporal, contado por conexión (no por mensaje)** → tras revisión: la
  primera versión incrementaba el contador de fallos POR MENSAJE, así que un cliente que mandaba
  varios `auth` con PIN incorrecto en la misma ráfaga (todos ya encolados en `_textIn` antes de
  que el primer `ForceDisconnect` surtiera efecto) consumía varios intentos de un solo golpe.
  Fix: `OnTextReceived` chequea `WebSocketServer.IsClientOpen(id)` antes de procesar un mensaje de
  un cliente sin autenticar; si ya se le mandó `ForceDisconnect` en esta misma pasada de
  `PumpEvents`, el resto de sus mensajes encolados se ignoran — como mucho UN fallo por conexión.
  Además, el lockout ya NO es permanente hasta reiniciar la app: agotados `MaxAuthFailures` (3)
  fallos, `NetworkController` calcula `_lockUntilTicks = Environment.TickCount + LockWindowMs`
  (60 s, `unchecked`, mismo patrón que el keep-alive) y cualquier intento dentro de esa ventana
  recibe `auth_locked` (con `retry_in_s`) sin evaluar el PIN. Al expirar la ventana, o en un auth
  exitoso, `_authFailCount` se resetea a 0.
- **Emparejamiento persistente por token (opción B)** → el PIN de 6 dígitos molestaba en la
  práctica clínica: cualquier reinicio del visor o de la tablet obligaba a retipearlo. Ahora un
  PIN correcto (`NetworkController.HandleAuthAttempt`) además de autenticar EMITE un token nuevo
  (`PairingStore.GenerateToken()`, 2×`Guid.NewGuid()` en hex = 64 caracteres, ~256 bits) que viaja
  en el `auth_ok` (`{"type":"auth_ok","token":"..."}`) y se persiste en AMBOS lados:
  `persistentDataPath/paired_tokens.json` (visor, `List<string>` — admite varias tablets
  emparejadas a la vez) y `persistentDataPath/pairing.json` (tablet, `Dictionary<host,token>`).
  La serialización/parseo de las dos formas y la generación del token son lógica PURA en
  `PairingStore` (sin Unity, sin IO), testeada en `PairingStoreTests.cs` — mismo patrón que
  `DataManagerLogic`/`DataLogicTests` (ver `docs/catalogo-lentes.md`). El primer mensaje del
  cliente ahora puede ser `{"type":"auth","pin":"NNNNNN"}` (primer enlace, o el token quedó
  inválido) o `{"type":"auth","token":"..."}` (reconexión, manual o automática, sin volver a
  pedir el PIN — `TabletSession` prueba el token ANTES de mostrar el `PinScreen`, ver
  `docs/tablet.md`). Auth por token exitoso NO emite un token nuevo (el mismo sigue siendo la
  credencial de esa tablet hasta que se revoque). Auth por token inválido/revocado (visor
  reseteado sin ese token en su lista, o revocado por "Desvincular") responde
  `{"type":"auth_fail","reason":"token"}` y **NO toca el lockout de PIN**
  (`_authFailCount`/`_lockUntilTicks` — ver `HandleTokenAuth`): el espacio de ~256 bits hace que
  un token viejo no sea indicio de fuerza bruta, a diferencia de un PIN de 6 dígitos. El lockout
  de PIN sigue existiendo IDÉNTICO a antes y sigue aplicando SOLO al flujo de PIN.
  **Desvincular**: la tablet manda `{"cmd":"unpair"}` (comando autenticado, como cualquier otro);
  el visor resuelve el token del cliente que lo mandó vía `_tokenByClientId` (poblado al
  autenticar, sea por PIN o por token) y lo borra de `paired_tokens.json`
  (`NetworkController.RemovePairedToken`). No hay respuesta del visor: la tablet ya borra su
  token local y cierra la conexión por su cuenta apenas manda el comando
  (`TabletSession.Unpair`), confiando en el orden de escritura del mismo socket/hilo (ver
  Gotchas). Reset total del lado visor (sin UI dedicada, a propósito — ver Minimal footprint):
  borrar `paired_tokens.json` a mano revoca TODOS los emparejamientos de una.

## Protocolo de mensajes (texto JSON, un mensaje por frame WS)

**Emparejamiento (previo a todo lo demás, ver Decisiones y porqués — opción B, token persistente):**
- Tablet → visor, primer mensaje de la conexión: `{"type":"auth","pin":"NNNNNN"}` (primer enlace,
  o el token guardado quedó inválido) **o** `{"type":"auth","token":"<64 hex>"}` (reconexión con
  el token de un enlace previo, sin pedir el PIN). Si el mensaje trae `"token"` no vacío, el
  visor SIEMPRE evalúa esa rama (nunca mezcla pin+token).
- Visor → tablet, tras un intento por **PIN**:
  - `{"type":"auth_ok","token":"<64 hex>"}` — PIN correcto; el token es NUEVO (recién generado,
    `PairingStore.GenerateToken()`) y ya quedó persistido en `paired_tokens.json`; inmediatamente
    después manda el `hello`.
  - `{"type":"auth_fail"}` — PIN incorrecto (y no hay lockout activo); el visor cierra esa
    conexión ahí mismo, la tablet debe reconectar para reintentar. Sin `"reason"` (implícito
    `"pin"`).
  - `{"type":"auth_locked","retry_in_s":N}` — se agotaron los `MaxAuthFailures` (3) intentos
    fallidos y la ventana de lockout (60 s) todavía no expiró; el visor NO evalúa el PIN mandado
    (puede ser el correcto) y cierra la conexión igual. `retry_in_s` es la cuenta regresiva
    redondeada hacia arriba. Este mensaje NUNCA sale de un intento por **token** (ver abajo).
- Visor → tablet, tras un intento por **token**:
  - `{"type":"auth_ok"}` — token válido (sigue en `paired_tokens.json`); SIN campo `"token"` (no
    se emite uno nuevo, el mismo sigue siendo la credencial); inmediatamente después manda el
    `hello`.
  - `{"type":"auth_fail","reason":"token"}` — token inválido o revocado (visor reseteado sin ese
    token en su lista persistida, o revocado por `unpair`); el visor cierra la conexión. **No
    incrementa `_authFailCount`/no puede disparar `auth_locked`** (ver Decisiones y porqués) — la
    tablet debe borrar ese token local y caer al flujo de PIN (`docs/tablet.md`).
- Cualquier mensaje que no sea `{"type":"auth",...}` antes de autenticar se ignora y el visor
  cierra la conexión sin responder.

**Visor → tablet:**
- Tras `auth_ok` (ya NO al conectar — ver arriba), `hello`:
  ```json
  {"type":"hello","catalog_version":"...","lenses":[{...catálogo completo...}],
   "vision_state":{"left":{"lens_id":"...", "<param>":0.0},"right":{...},"blend_active":false},
   "scenario":"ruta_noche","scenarios":[{"id":"consultorio","label":"Consultorio"},
   {"id":"ruta_noche","label":"Ruta nocturna"}]}
  ```
- Ante cambios: `{"type":"vision_state","vision_state":{"left":{...},"right":{...},"blend_active":bool}}`.
  Cada ojo serializa `lens_id` + todos los params del `EyeState` aplanados en el mismo objeto.
  `blend_active` (P2.1) es hermano de `left`/`right` (no va dentro de cada ojo): `true` solo cuando
  AMBOS ojos tienen lente Y son distintas (`LensEngine.ComputeBlend`) — es la fuente de verdad para
  que la tablet decida el split de panes del stream. Solo se manda a clientes autenticados.

**Tablet → visor** (campo discriminador `cmd`, en `NetworkController.OnTextReceived`; solo se
procesan tras autenticar — antes de eso el único mensaje válido es el `auth` de arriba):
- `{"cmd":"apply_lens","lens_id":"<id>","eye":"left|right|both"}` → `DataManager.ApplyLens`.
- `{"cmd":"override_params","eye":"left|right|both","params":{"<param>":valor,...}}` → `DataManager.OverrideParams`.
- `{"cmd":"set_astigmatism","eye":"left|right|both","enabled":bool,"magnitude":0..1,"angle":radianes}`
  → `GlareController.SetAstigmatism(eye, enabled, magnitude, angle)` (P2.2 — el visor ya no ignora
  `"eye"`; default `"both"` si falta, misma convención que `apply_lens`/`override_params`).
- `{"cmd":"load_scenario","id":"consultorio|ruta_noche"}` → `ScenarioManager.SwitchTo` (P2.3 — el
  visor también acepta el campo legacy `"scenario"` con el mismo valor, por compat).
- `{"cmd":"recenter"}` → `ScenarioManager.RecenterPatient()` (recalibra la posición del paciente en
  el escenario actual). Fire-and-forget como `set_astigmatism`/`load_scenario`: sin ack ni campo en
  `vision_state`. Cualquier sesión autenticada puede mandarlo (sin gating de admin — es una acción
  clínica no destructiva). La corrección **no persiste** tras un `load_scenario` posterior (recentra
  contra el escenario activo en ESE momento, no guarda un offset). Botón "Recentrar" en la tablet
  (header Pro y `StdTopBar` de Standard, ver `docs/tablet.md`).
- `{"cmd":"refresh"}` (P5.4) → el visor responde al MISMO cliente (`SendTextTo`, no broadcast) con
  el payload EXACTO de un `hello` (`BuildHello()` reusado tal cual). Permite reconstruir
  catálogo/escenarios/vision_state sin reconectar/re-autenticar — útil si el clínico sabe que el
  catálogo cambió en caliente (p.ej. sync con backend recién terminado). Nota de nombre: pese a
  llamarse informalmente "comando de refresh", sigue la convención `cmd` de todos los comandos
  autenticados (no introduce un segundo discriminador `"type"` para mensajes tablet→visor).
- `{"cmd":"unpair"}` → revoca el token del CLIENTE QUE LO MANDA (resuelto vía
  `_tokenByClientId[id]`, poblado al autenticar sea por PIN o por token) de
  `paired_tokens.json`. Sin respuesta del visor: la tablet ya cierra la conexión y borra su
  token local por su cuenta apenas lo manda (ver Decisiones y porqués, `TabletSession.Unpair`).
- `{"cmd":"set_hud","visible":bool}` → togglea el HUD de diagnóstico del visor (FPS/lentes/halos/
  PIN, `Vision/HudController.cs`) desde la tablet. `NetworkController.ResolveHud()` resuelve y
  cachea la referencia con `FindFirstObjectByType<HudController>(FindObjectsInactive.Include)` (el
  `Include` es necesario para poder volver a encontrarlo — y por lo tanto re-mostrarlo — después de
  un `SetActive(false)`) y llama `hud.gameObject.SetActive(visible)` directo, sin tocar
  `HudController.cs` (frontera con `Vision/`). **Sin ack ni campo en `vision_state`**: es
  fire-and-forget, igual que `set_astigmatism`/`load_scenario` — el visor no confirma el estado
  resultante, así que la tablet no tiene forma de consultar si el HUD está realmente visible u
  oculto en este momento (ver Gotchas en `docs/tablet.md`). **La tablet manda este comando en TODO
  `hello`** (no solo al tocar el botón, nuevo): `TabletController.OnSessionHello` fuerza
  `set_hud false` en cada hello si `mode == "standard"` (el HUD no tiene sentido en manos del
  paciente/operador de Standard, que ni siquiera tiene el botón), y re-afirma el `_hudVisible`
  vigente de la tablet en cada hello si el modo es pro/admin — ver `docs/tablet.md` Decisiones
  "Toggle del HUD del visor". **Red de seguridad del lado visor (nuevo):** si el cliente que se
  desconecta (`NetworkController.OnClientDisconnected`) o que manda `"unpair"` estaba autenticado
  y no queda ninguna otra tablet autenticada, el visor fuerza el HUD visible de nuevo
  (`ResolveHud()?.gameObject.SetActive(true)`) — evita que una tablet Standard (que fuerza el HUD
  oculto) deje el visor sin HUD, y por lo tanto sin el PIN visible, para el PRÓXIMO
  emparejamiento. Ambos puntos ya corren en el hilo principal (`OnClientDisconnected`/
  `OnTextReceived` se disparan desde `PumpEvents()`), así que tocar la API de Unity ahí no viola el
  patrón thread→cola→Update.
- Cualquier otro `cmd` loguea warning; texto no-JSON se descarta con warning.

**Stream binario:** `[1 byte header B/L/R][JPG]`, 768×576, 20 Hz, calidad JPG 85
(constantes en `StreamingCapture.cs`). Solo se manda a clientes autenticados (gate:
`WebSocketServer.AuthenticatedClientCount`, reemplazó a `OpenClientCount` en el `LateUpdate` de
`StreamingCapture`).

### P7: comandos de lentes custom + modo en el hello

- `hello` suma `"mode"` (`"standard"|"pro"`, de `LicenseManager.AppMode`) e `"is_admin"`.
  Las lentes del hello serializan `origen` (`null`/ausente o `"custom"`; P7.2 fusionó la
  categoría `"generic"` con el catálogo base — ese valor ya no lo emite un backend nuevo, ver
  `docs/catalogo-lentes.md` §P7.2).
- Comandos tablet→visor nuevos: `create_lens {scope, nombre, descripcion, params}`,
  `update_lens {lens_id, nombre, descripcion, params}`, `delete_lens {lens_id}`. El visor
  agrega SU `device_id` y hace el HTTP (`Data/CustomLensClient.cs`, timeout 8 s, gate de
  inalcanzable por `responseCode==0` igual que el verify) contra `/api/lenses/custom`.
- Respuestas visor→tablet: `{"type":"lens_saved","op","lens_id"}` o
  `{"type":"lens_error","op","reason"}` (`reason`: `"offline"` o el reason del backend —
  `MODE_NOT_PRO`/`NOT_ADMIN`/`NOT_OWNER`/etc.). Al éxito el visor llama
  `DataManager.RefreshFromBackend()` y el catálogo nuevo llega en un **re-broadcast de hello**
  (suscripción a `CatalogSyncedWithBackend` en `Start`, des-suscripta en `OnDestroy`).

### P8: `reorder_lenses` — drag-reorder de catálogo desde la tablet (admin)

Pedido explícito: el admin arrastra una card en la tablet (long-press + drag, ver
`docs/tablet.md` §"P8: drag-reorder de catálogo") para reordenar las lentes de CATÁLOGO (las
custom no participan — siempre quedan después, ver `docs/catalogo-lentes.md`). El nuevo orden
debe persistir para todos los dispositivos, no solo la tablet que lo hizo.

- Tablet → visor: `{"cmd":"reorder_lenses","order":["id1","id2",...]}` — `order` son los ids de
  las lentes de catálogo en el orden visual final (después del drag), permutación exacta del
  array `catalogo` actual. Mismo patrón de autenticación que el resto de comandos (solo se procesa
  tras `auth_ok`, ver "Protocolo de mensajes" arriba).
- Visor: `NetworkController.OnTextReceived` valida el shape mínimo (`"order"` debe ser un array
  no vacío; si no, responde `lens_error` local con `reason:"invalid_order"` SIN llamar al
  backend) y reusa `RunLensCommand` (P7, mismo método que `create/update/delete_lens`, extendido
  con una 4ª rama): agrega su `device_id` y hace `POST {backendUrl}/api/lenses/reorder` con
  `{"device_id":"...","order":[...]}` (`Data/CustomLensClient.Reorder`, mismo timeout 8 s y gate
  de inalcanzable `responseCode==0` que el resto de `CustomLensClient`).
- Backend: 200 → `{"status":"ok","catalog_version":"..."}` (versiona la BASE, igual que una
  edición/borrado de admin — no el hash de extras, ver `docs/catalogo-lentes.md` §P7). Denegado
  (no-admin u otro) → JSON con `reason` (p.ej. `NOT_ADMIN`). Permutación inválida (no es
  exactamente el conjunto actual de ids de catálogo) → HTTP 422.
- Al éxito: el visor re-sincroniza (`DataManager.RefreshFromBackend()`) y el catálogo con el
  orden nuevo llega en el **mismo re-broadcast de hello** que create/update/delete_lens
  (`CatalogSyncedWithBackend`) — no hay un mensaje de confirmación dedicado. `RunLensCommand`
  igual manda un `{"type":"lens_saved","op":"reorder_lenses","lens_id":""}` al cliente que lo
  pidió (mismo código que las otras 3 mutaciones, `lens_id` no aplica y queda vacío) — la tablet
  lo ignora sin problema: `TabletController.OnLensSaved` solo reacciona a
  `create_lens`/`update_lens`/`delete_lens`, así que un `op` desconocido es un no-op silencioso
  (no hace falta filtrarlo del lado visor).
- Al fallo (denegado, 422, backend inalcanzable, o el guard de shape local): `lens_error` con
  `{"op":"reorder_lenses","reason":...}` — la tablet lo muestra con el mecanismo existente
  (`OnLensError`), y como `op != "create_lens"` cae al label general (`_ownLensStatus`, ver
  `docs/tablet.md`). El rollback visual del lado tablet es gratis: el drag ya movió las cards
  localmente vía `SetSiblingIndex`, y el próximo `hello`/reconexión reconstruye la lista con el
  orden REAL del backend (que no cambió si el comando falló).

## Modelo de threading
- **Server:** un thread `WSAccept` acepta clientes + un thread `WSRead{id}` por cliente + un thread
  `WSPing{id}` por cliente (keep-alive: ping cada 5 s, cierra si no hay actividad en 15 s; el mismo
  thread también cierra la conexión si no autenticó dentro de `AuthTimeoutMs` ~30 s — ver
  Emparejamiento por PIN). Los eventos de red y los logs (`_logQueue`, ej. tope de frame excedido,
  keep-alive-timeout o auth-timeout) se encolan en `_connected`/`_textIn`/`_disconnected`/
  `_logQueue`; `NetworkController.Update()` llama `PumpEvents()` que dispara los eventos C# (y los
  `Debug.LogWarning`) ya en el hilo principal. La validación del PIN (comparar contra
  `PairingPin`, contar intentos fallidos, decidir auth_ok/auth_fail) corre ahí mismo, en
  `NetworkController.OnTextReceived`/`HandleAuthAttempt` — nunca en los threads de socket.
- **Escrituras del server:** `SendFrame` puede llamarse desde cualquier thread (lo usan `ReadLoop`
  para el pong, `PingLoop` para el ping y el thread de encode para el broadcast); serializa por
  cliente con `Client.WriteLock`.
- **Cliente:** un thread `WSClient` hace connect + handshake + read loop, más un thread `WSPing`
  (mismo esquema de keep-alive que el server, iniciado tras el handshake). `SendText`/ping/pong
  comparten `SendFrame(opcode, payload)` con `_writeLock`. Flags `volatile` `_connectedFlag`/
  `_closedFlag` + colas (`_textIn`/`_binIn`/`_logQueue`), drenados por `TabletSession.Update()`
  (P6.2 — antes `TabletController.Update()`; ese método llama a su vez `TabletSession.Update()`
  desde `TabletController.Update()`, ver `docs/tablet.md`) → `PumpEvents()`.
- **Discovery:** el beacon no usa thread (se tickea desde `Update` cada 2 s); el listener usa un
  thread `Discovery` bloqueado en `UdpClient.Receive` y cola de hosts.
- **StreamingCapture:** timer en `LateUpdate` → render on-demand (main thread) →
  `AsyncGPUReadback` (callback en main thread, copia los bytes) → `Task.Run` encode + broadcast.
  Gate `_busy`/`_pending` (con `Interlocked.Decrement`) evita solapar ticks mientras hay encodes en vuelo.

## Gotchas
- **API de Unity nunca desde threads de socket.** Los callbacks `TextReceived`, `Connected`, etc.
  solo son seguros porque se disparan desde `PumpEvents()` en `Update`. Si se suscribe algo y se
  olvida el pump (o se llama desde el read loop), explota en runtime en el device. Excepción
  deliberada: `EncodeArrayToJPG` y `BroadcastBinary` sí corren en el `Task.Run` (ambos thread-safe).
- **Dispose de sockets:** `NetworkController.OnDestroy` llama `_server.Stop()` y `_beacon.Stop()`;
  `TabletController.OnDestroy` llama `TabletSession.Shutdown()` (P6.2), que cierra listener y WS.
  Sin eso, los threads background quedan bloqueados en `Read`/`Receive` y el puerto queda tomado
  entre play modes en el Editor. `Stop()` cierra el socket para desbloquear los `Read` (los
  threads son `IsBackground = true` como red de seguridad).
- **Masking direccional:** cliente→servidor DEBE ir enmascarado (el server igual tolera frames sin
  máscara); servidor→cliente va sin máscara. Si se toca el framing, respetar esto o los browsers/
  peers estrictos cortan la conexión.
- **Permisos Android:** no hay manejo de permisos en código. Requiere `INTERNET` (Player Settings →
  Internet Access: Require). `DiscoveryListener` omite adrede el `MulticastLock` de Android — el
  comentario del archivo dice que en la mayoría de redes el broadcast llega; en redes que lo filtran
  la tablet no descubre nada y hay que usar la conexión manual por IP (que existe en la UI).
- **`device_label` del beacon es decorativo, sigue sin ser una clave:** `DiscoveryListener.Loop`
  chequea que el payload contenga `"app"` + el tag `simulador-vr`, y ADEMÁS parsea `device_label`
  con `JObject.Parse(text)` (try/catch propio — un payload malformado no tira abajo el thread, solo
  encola `label = null`), pero sigue encolando la IP de ORIGEN del paquete (`ep.Address`) como
  identidad. `TabletSession._seenHosts`/`_tokenByHost`/`_pendingAuthToken` siguen keyeados por esa
  IP, no por el label — `_hostLabels` (nuevo, paralelo a `_seenHosts`) solo guarda el label crudo
  para que `TabletController.FriendlyVisorName` arme un nombre de UI (recorta el nonce de sesión;
  sin label cae a "Visor Quest" + sufijo `(2)`/`(3)`... si hay más de un host con el mismo nombre
  base). Si dos visores reales emiten el mismo `device_label` (mismo `SystemInfo.deviceName`), la
  tablet los distingue igual porque la clave sigue siendo la IP — el label nunca decide identidad,
  solo el texto del botón.
- **Solo el label de escenario sigue a mano en Net (P2.3, cerrado):** agregar un escenario nuevo
  en `ScenarioManager.Order` (Vision/) ya aparece solo en `hello.scenarios` (vía `ScenarioOrder`);
  si no se le agrega también una entrada en `NetworkController.ScenarioLabels`, el label cae al
  fallback (capitaliza el id) — funciona pero menos prolijo que un label a mano. No rompe nada:
  ya no hay riesgo de que la LISTA de ids diverja entre Vision/ y Net.
- **`DataManager.ApplyLens` debe llamar `UpdateBlend()` ANTES de disparar `VisionStateChanged`
  — bug real, corregido:** `NetworkController.OnVisionStateChanged` reacciona a CADA
  `VisionStateChanged.Invoke(eye, state)` armando y broadcasteando `BuildVisionState()` de forma
  SÍNCRONA, en el mismo call stack del Invoke (a diferencia del resto de la capa de red, que
  encola en threads y drena recién en `PumpEvents()` — acá no hay thread de por medio, así que no
  hay nada que lo demore). `BuildVisionState()` lee `DataManager.BlendModeEnabled` en ESE instante.
  `ApplyLens` llamaba antes `UpdateBlend()` DESPUÉS de disparar los Invoke de `"left"`/`"right"`:
  el primer broadcast (o el único, si `eye` era `"left"`/`"right"` en vez de `"both"`) salía con el
  `BlendModeEnabled` VIEJO — la tablet podía recibir un `blend_active` desactualizado y mostrar 2
  panes con la misma lente en ambos ojos (caso típico: estando en blend real, aplicar una lente a
  "Ambos"). Fix: `UpdateBlend()` corre después de asignar `Left`/`Right` pero ANTES de los dos
  `Invoke`, así CUALQUIER broadcast que dispare sale con `BlendModeEnabled` ya recalculado.
  `LensEngine.ComputeBlend` en sí siempre fue correcto — era un bug de orden, no de fórmula.
  `StreamingCapture` no tenía este bug (lee `dm.BlendModeEnabled` en vivo en cada tick, no depende
  del orden de eventos de `ApplyLens`). Si se agrega un evento nuevo que dispare un broadcast
  síncrono leyendo estado derivado de `DataManager`, recalcular ese estado ANTES del Invoke, no
  después.
- **Descubrimiento de refs de escena acotado (P3.4):** `NetworkController.Update()` buscaba
  `ScenarioManager`/`GlareController` con `FindFirstObjectByType` en CADA frame hasta encontrarlos
  — costo indefinido si la escena nunca los tiene. `DiscoverSceneRefs()` reintenta a 1 Hz, máximo
  10 veces (~10 s), y loguea un warning si se agotan los intentos sin encontrarlos (comandos
  `load_scenario`/`set_astigmatism` quedan sin efecto en ese caso, silenciosamente salvo el warning).
- **Headers de 8 bytes truncados al enviar:** en `SendFrame`/`SendText` el largo de 64 bits solo
  escribe los 4 bytes bajos (los 4 altos van en 0). Correcto mientras ningún payload supere 4 GB, pero
  no es una serialización completa del RFC.
- **`Server.AuthenticatedClientCount` desde `LateUpdate`** evita capturar/encodear sin clientes
  autenticados (antes era `OpenClientCount`: un cliente conectado-pero-sin-PIN ya no dispara
  render+encode). Si se agrega otro consumidor del stream, mantener ese guard.
- **Lockout de PIN es GLOBAL (todas las conexiones), no por IP ni por conexión:**
  `NetworkController._authFailCount`/`_lockUntilTicks` son campos de instancia únicos; agotado el
  tope, CUALQUIER cliente que intente autenticarse (aunque sea uno legítimo con el PIN correcto)
  recibe `auth_locked` hasta que expire la ventana de 60 s. Es intencional (simplicidad — un solo
  visor, una sola tablet real en la práctica) pero significa que un atacante que agote el tope a
  propósito bloquea también al clínico legítimo por hasta 60 s (DoS de disponibilidad acotado,
  no de confidencialidad). El contador SÍ está protegido contra la amplificación por ráfaga (ver
  Decisiones y porqués: `IsClientOpen` + a lo sumo un fallo por conexión).
- **`ForceDisconnect` no saca al cliente de `_clients` sincrónicamente:** solo pone `Open=false`
  y cierra el stream/socket; la remoción real (`_clients.TryRemove` + `_disconnected.Enqueue`)
  ocurre en el `ReadLoop` de ESE cliente cuando su `Read` bloqueado se desbloquea por el cierre
  (thread de socket, asíncrono respecto al `ForceDisconnect` que corrió en el hilo principal). Es
  la base de la que depende el fix de la ráfaga (`IsClientOpen` ve `Open=false` de inmediato,
  aunque la entrada siga un rato más en el diccionario) — no asumir que tras `ForceDisconnect` el
  id ya no existe en `_clients`.
- **`BroadcastText`/`BroadcastBinary` ahora filtran por `Authenticated`:** cualquier código nuevo
  que dependa de que el broadcast llegue a TODOS los clientes abiertos (autenticados o no) se
  rompe silenciosamente — usar `SendTextTo` por id si hace falta llegar a alguien no autenticado
  (como hace el propio flujo de auth con `auth_ok`/`auth_fail`).
- **Tope de tamaño de frame entrante** (`MaxIncomingFrameLength` en ambos `ReadLoop`): el largo de
  64 bits (caso `len==127`) viene del peer sin validar; antes de este tope, un valor falseado
  disparaba un `new byte[n]` gigante (hasta ~2 GB) antes de fallar — DoS barato. Servidor: 1 MB
  (solo recibe control JSON de la tablet). Cliente: 8 MB (recibe además los JPG del stream,
  768×576 q85, típicamente <300 KB, con margen amplio). Si se excede, se loguea (encolado, nunca
  desde el thread del socket) y se cierra esa conexión — mismo camino que cualquier otro corte.
- **Keep-alive con ping propio (ya no solo pong pasivo):** cada conexión tiene un thread `WSPing{id}`
  (servidor) / `WSPing` (cliente) que manda un ping cada 5 s y lleva el timestamp del último frame
  recibido (`LastRecvTicks`/`_lastRecvTicks`, `Environment.TickCount` con resta `unchecked` — no
  `TickCount64`: no existe en el API compatibility level del proyecto). Si pasan 15 s sin ningún
  frame entrante (pong u otro), el peer se considera muerto: se cierra el socket desde el thread de
  ping, lo que desbloquea el `Read` bloqueado del `ReadLoop` correspondiente y dispara la limpieza
  normal (libera el slot del server; en el cliente dispara `Disconnected` → `PumpEvents` →
  `TabletSession.OnWsDisconnected` (P6.2 — antes en `TabletController`), que decide el evento a
  disparar hacia la UI sin tocarla directamente). El cliente ahora también
  responde pong a los pings del servidor (`WebSocketClient.ReadLoop`, antes no manejaba `opcode 0x9`
  en absoluto). `WebSocketClient.SendText` quedó refactorizado sobre un `SendFrame(opcode, payload)`
  genérico enmascarado, reusado por texto y por ping/pong.

## Modelo de amenaza (resumen)
El canal ahora **exige PIN o token** (emparejamiento — ver Decisiones y porqués y Protocolo):
cualquier dispositivo en la LAN que descubra el visor (o conozca su IP) todavía puede abrir el
socket WS, pero no puede leer catálogo/estado ni mandar comandos sin conocer el PIN de 6 dígitos
que el visor muestra en su HUD para esa sesión, o sin poseer un token de un enlace previo. Esto
sube el costo de un atacante casual en la misma LAN de "cualquiera que conecte" a "alguien que vio
la pantalla del visor, o que robó el archivo de pairing de una tablet ya emparejada" — no es
criptográficamente fuerte (PIN y token viajan en texto plano, sin TLS) pero cierra el acceso
trivial. Sigue **sin TLS**: el PIN, el token y todo el tráfico posterior (comandos, vision_state,
stream JPG) van sin cifrar; alguien que ya esté haciendo sniffing pasivo de la LAN en el momento
del handshake puede capturar el PIN o el token y suplantar a la tablet. TLS queda como deuda
pendiente. El lockout de intentos fallidos de PIN (`MaxAuthFailures=3` en una ventana de
`LockWindowMs=60s`, contado por conexión — ver Decisiones y porqués) mitiga fuerza bruta básica
(3 intentos/min sobre un espacio de 10^6 no es practicable) sin exigir reiniciar el visor, pero al
ser un contador global también da a un atacante en la LAN una forma barata de bloquear
temporalmente al clínico legítimo agotando el tope a propósito (ver Gotchas). El lockout **NO
aplica al token** (espacio de ~256 bits, `PairingStore.GenerateToken`): un token robado no se
puede fuerza-brutear en ningún tiempo práctico, así que negarle el lockout no abre una vía de
ataque nueva. El riesgo del token es distinto al del PIN: es de **duración indefinida** (hasta que
se revoque) y viaja también sin cifrar en cada reconexión, así que **robo del archivo
`pairing.json` de la tablet = acceso al visor hasta que se revoque** (más aún si la tablet no
tiene bloqueo de pantalla). Revocación: botón "Desvincular" en la tablet (revoca ESE token
puntual) o borrar `persistentDataPath/paired_tokens.json` del lado visor a mano (revoca TODOS los
emparejamientos de una — no hay UI para esto en el visor, ver Decisiones y porqués).

## Cómo probar
1. **En Editor (loopback):** abrir `Assets/Scenes/Main.unity` y dar Play — `NetworkController` YA
   NO se auto-instancia sola (fail-closed, ver `docs/licenciamiento.md`): es
   `Simulador.License.LicenseManager` quien la crea apenas el gate de licencia lo permite (gracia
   offline al arrancar, o tras un verify 200 OK contra el backend configurado). Con un dispositivo
   con licencia válida (o dentro de la gracia offline) debe verse igual que antes, solo que el log
   `WebSocketServer: escuchando en :9090` aparece DESPUÉS de los logs de `License:`, no al cargar
   la escena. Loguea el beacon a :9091 y el PIN de la sesión (`Net: PIN de emparejamiento de esta
   sesion: NNNNNN`; en el visor real lo muestra el HUD, fuera de este cambio). En otra instancia
   (o build) correr `Assets/Scenes/Tablet.unity`: debe listar el visor descubierto en segundos; si
   no, usar "Conexión manual" con `127.0.0.1`.
2. Tocar el visor en la lista (o "Conectar" en manual) abre el `PinScreen`: ingresar el PIN de la
   consola del visor y confirmar. Verificar en consola del visor `Net: cliente N conectado;
   esperando PIN de emparejamiento.` → `Net: cliente N autenticado, enviando hello.`, y que la
   tablet pase a la pantalla principal con el catálogo de lentes y el stream moviéndose (~20 fps en
   el footer).
3. **PIN incorrecto:** en el `PinScreen`, ingresar un PIN erróneo → la tablet debe mostrar "PIN
   incorrecto. Volvé a intentarlo." y volver a pedirlo; la consola del visor debe loguear
   `Net: cliente N mando un PIN incorrecto (1/3); cerrando.`. Repetir 3 veces (cada vez reconecta,
   es una conexión nueva): al cuarto intento (con cualquier PIN, incluso el correcto) el visor
   debe responder `auth_locked` sin evaluarlo — la tablet muestra "Demasiados intentos. Esperá
   Ns y volvé a intentarlo." con la cuenta regresiva que manda el visor. Esperar los 60 s y
   reintentar con el PIN correcto → debe autenticar normal (el contador se resetea solo, sin
   reiniciar Play).
4. **Ráfaga de PIN incorrecto en una sola conexión (opcional, con `websocat` u otro cliente WS
   que permita mandar varios frames antes de leer la respuesta):** conectar a
   `ws://<ip-visor>:9090` y mandar 3 mensajes `{"type":"auth","pin":"000000"}` (PIN erróneo) uno
   detrás del otro sin esperar respuesta → la consola del visor debe mostrar solo UN
   `PIN incorrecto (1/3)` para esa conexión (no tres), confirmando que la ráfaga no amplifica el
   contador global.
5. **Comando sin autenticar (opcional, con `websocat` u otro cliente WS):** conectar a
   `ws://<ip-visor>:9090` y mandar cualquier JSON que no sea `{"type":"auth","pin":...}` (o texto
   no-JSON) → el visor debe cerrar la conexión sin responder nada (loguea
   `Net: cliente N mando un comando sin autenticar; cerrando.`).
6. **Reconexión reusando el token persistente:** con la tablet ya autenticada una vez (ya recibió
   el `auth_ok` con token, ver consola: no hay log directo del token pero `pairing.json` en
   `persistentDataPath` de la tablet debe existir), desconectar (botón Desconectar) y volver a
   tocar el mismo visor → debe conectar directo sin mostrar el `PinScreen` (usa el token
   persistido, no el PIN). **Repetir cerrando y reabriendo la app de la tablet entera** (Stop/Play
   en Editor, o matar/reabrir en device) → debe seguir conectando sin PIN (el token sobrevive al
   proceso, a diferencia del PIN viejo que solo vivía en memoria). **Repetir además reiniciando el
   VISOR** (nuevo PIN de sesión, pero el token persiste en `paired_tokens.json`) → la tablet debe
   seguir conectando sin pedir el PIN nuevo (el token no depende del PIN de sesión).
6b. **Token inválido tras borrar `paired_tokens.json` del visor:** con la tablet ya emparejada,
   borrar a mano `paired_tokens.json` del `persistentDataPath` del visor y reiniciar el visor (o
   solo el archivo si se puede sin reiniciar) → el siguiente intento de conexión de la tablet debe
   recibir `auth_fail` con `reason:"token"`, mostrar el `PinScreen` con "El emparejamiento con
   este visor ya no es válido. Ingresá el PIN nuevamente." y la tablet debe haber borrado esa
   entrada de su propio `pairing.json`. Confirmar en consola del visor
   `Net: cliente N mando un token de emparejamiento invalido o revocado; cerrando.` y que este
   evento NO afecta el contador de lockout de PIN (probar un PIN incorrecto justo después: debe
   seguir en 1/3, no arrastrar el fallo de token).
6c. **Desvincular:** con la tablet conectada y autenticada, tocar "Desvincular" en el header → debe
   volver al `ConnectScreen` con "Desvinculado. Ingresá el PIN si querés volver a conectarte."; la
   consola del visor debe loguear `Net: cliente N se desvinculo (token revocado).`. Tocar el mismo
   visor de nuevo → debe pedir el `PinScreen` (el token ya no está en `pairing.json` ni en
   `paired_tokens.json`).
7. Tocar una lente en la tablet → el visor debe cambiar el render y devolver `vision_state` (los
   chips OD/OI de la card se encienden con la confirmación).
8. Aplicar lentes distintas por ojo → el stream debe pasar a dos paneles (frames `L` y `R` separados).
   Aplicar una lente a UN solo ojo (dejando el otro sin lente) → debe quedar en UN solo pane, sin
   etiqueta vacía (`blend_active` en `false` porque `ComputeBlend` exige ambos ojos con lente).
9. Probar `load_scenario` (por id) y `set_astigmatism` con `"eye":"left"` y `"eye":"right"` por
   separado → el efecto (halo/smear direccional) debe verse solo en el ojo indicado. Mirar warnings
   `Net: comando desconocido` ante typos.
10. En device real: visor y tablet en la misma Wi-Fi; si el discovery no anda, es la red filtrando
   broadcast → validar el fallback de IP manual.
11. **`refresh` (P5.4):** con la tablet ya conectada, tocar "Actualizar" en el header → la consola
    del visor NO debe mostrar `comando desconocido` (confirma que llegó `{"cmd":"refresh"}`); la
    tablet debe repoblar la lista de lentes/escenarios sin mostrar ninguna pantalla intermedia
    (no hay reconexión, el WS sigue abierto). Test más exigente (simula "el catálogo cambió en el
    backend, refrescar sin reconectar"): con un segundo cliente WS (`websocat`, mismo PIN) aplicar
    una lente distinta; confirmar que "Actualizar" en la tablet trae el `vision_state` resultante
    aunque la tablet no hubiera recibido ese `vision_state` por el broadcast normal.
12. **`set_hud` (toggle de HUD, 2 dispositivos):** con la tablet conectada, tocar "Ocultar HUD" en
    el header → en el visor (HMD o Editor con la vista Game) el HUD de diagnóstico debe
    desaparecer al instante; el botón de la tablet debe pasar a decir "Mostrar HUD". Tocarlo de
    nuevo → el HUD debe reaparecer (con sus valores actualizados, no un frame congelado) y el botón
    vuelve a "Ocultar HUD". Confirmar en consola del visor que NO aparece `comando desconocido`
    (llegó `{"cmd":"set_hud"}`) y que, si el HUD no existe en la escena, se loguea el warning
    `Net: set_hud recibido pero no se encontro HudController en la escena.` en vez de una excepción.
    Con el HUD oculto, desconectar la tablet (botón Desconectar) → el HUD del visor debe
    REAPARECER SOLO, sin volver a conectar (red de seguridad nueva de
    `NetworkController.OnClientDisconnected`, ver Protocolo arriba: no queda ninguna tablet
    autenticada, así que se fuerza visible). Volver a conectar (mismo PIN o token) → el botón debe
    volver a mostrar "Ocultar HUD" (reset local, ver `docs/tablet.md`), coherente con el HUD ya
    visible (antes de este cambio el HUD real seguía oculto hasta el próximo toggle — mismatch
    parcialmente cerrado, ver Pendientes). **Con 2 tablets pro/admin emparejadas:** ocultar el HUD
    desde una y desconectar esa MISMA tablet → el HUD debe seguir oculto (la otra tablet sigue
    autenticada, `AuthenticatedClientCount > 0`); desconectar también la segunda → recién ahí
    reaparece. **Con `"unpair"`:** repetir ocultando el HUD y tocando "Desvincular" en vez de
    "Desconectar" → mismo resultado (el HUD reaparece si esa era la última tablet autenticada).
13. **`reorder_lenses` (P8, requiere visor ADMIN + backend accesible):** desde la tablet, hacer
    long-press + drag sobre una card de catálogo (gesto detallado en `docs/tablet.md` §"P8") y
    soltar en una posición distinta → la consola del visor debe loguear `Net: reorder_lenses OK
    (catalogo reordenado).` (NO `comando desconocido`) y, poco después,
    `Net: catalogo v... re-sincronizado; re-broadcast de hello.` (mismo log que cualquier otra
    mutación P7). Confirmar el orden nuevo con una SEGUNDA tablet conectada (o tras
    Desconectar/reconectar la misma): debe reflejar el orden reordenado, no el original. **Con un
    visor NO admin** (forzar `is_admin:false` del lado backend/tablet para la prueba): el comando
    ni debería poder dispararse desde la UI (gating del lado tablet), pero si se fuerza a mano vía
    `websocat` con un cliente autenticado no-admin, el visor debe responder `lens_error` con
    `reason:"NOT_ADMIN"` sin tocar el catálogo. **Permutación inválida** (mandar `order` con un id
    que no existe, o repetido, o incompleto vía `websocat`): el visor debe recibir un HTTP 422 del
    backend y responder `lens_error` a la tablet (reason variará según cómo el backend serialice
    el error de validación — ver `docs/tablet.md` sobre el mensaje genérico de fallback).
    **`order` vacío o ausente:** el visor debe loguear el warning `Net: reorder_lenses con "order"
    invalido o vacio.` y responder `lens_error` con `reason:"invalid_order"` SIN llegar a golpear
    el backend (confirmar que no aparece ningún log de sync/HTTP para ese intento).
14. **`recenter`:** con la tablet conectada, tocar "Recentrar" (header Pro o `StdTopBar` de
    Standard) → la consola del visor NO debe mostrar `comando desconocido` (confirma que llegó
    `{"cmd":"recenter"}`) y el paciente debe recalibrarse en el escenario actual (efecto observable
    en el visor/HMD, sin ack a la tablet — fire-and-forget). Repetir en ambos escenarios
    (`consultorio`/`ruta_noche`). Sin `ScenarioManager` en la escena (caso degenerado, no debería
    pasar en producción): el visor debe loguear `[Net] recenter sin ScenarioManager wired.` sin
    excepción.

## Pendientes / deuda
- Sin `MulticastLock` Android en `DiscoveryListener` (documentado como "si hiciera falta se agrega").
- Sin close handshake WS saliente (se cierra el TCP directo). El keep-alive con ping propio (ver
  Decisiones y porqués) sí está resuelto en ambos lados.
- **Sin TLS** (ver Modelo de amenaza arriba): el PIN, el token y el resto del tráfico van sin cifrar.
- **HUD del visor todavía no muestra `PairingPin`/`AuthenticatedClientCount`** — ambos expuestos
  como propiedad pública en `NetworkController`, falta que `Vision/` los pinte (fuera de alcance
  de esta tarea).
- **`set_hud` sigue sin estado sincronizado ni persistente, pero el caso más grave del mismatch ya
  se cerró (nuevo)** — sigue siendo fire-and-forget (sin ack, sin campo en `vision_state`): el
  visor no informa si el HUD terminó visible u oculto, y el botón de la tablet (`_hudVisible`, ver
  `docs/tablet.md`) es puramente optimista, reseteado a "visible" en cada conexión nueva
  (`OnSessionConnected`). Lo que SÍ se cerró: (1) `TabletController.OnSessionHello` manda
  `set_hud` explícito según el modo en TODO hello (Standard siempre fuerza `false`; pro/admin
  reafirma el `_hudVisible` vigente), y (2) `NetworkController.OnClientDisconnected`/`"unpair"`
  fuerzan el HUD visible de nuevo si el cliente que se va estaba autenticado y no queda ninguna
  otra tablet autenticada — evita que una tablet Standard (que fuerza el HUD oculto) deje el HUD, y
  el PIN que muestra, invisible para el PRÓXIMO emparejamiento. Lo que queda sin cerrar: con
  **2+ tablets pro/admin emparejadas simultáneamente**, el botón de cada una sigue siendo optimista
  y puede desincronizarse entre sí (una lo oculta, la otra sigue mostrando "Ocultar HUD" aunque el
  HUD real ya esté oculto) — ese caso no dispara la red de seguridad del disconnect (solo actúa
  cuando NO queda ninguna tablet autenticada) y sigue siendo un mismatch aceptado (HUD de
  diagnóstico, no un control clínico crítico); si hiciera falta cerrarlo del todo, la vía natural
  sigue siendo agregar `hud_visible` al `vision_state`/`hello` (mismo patrón que `blend_active`,
  P2.1) para que CUALQUIER tablet pueda sincronizar su botón contra el estado real.
- **Lockout global (no por IP)** — un atacante en la LAN puede agotar el tope a propósito y
  bloquear también al clínico legítimo por hasta 60 s (ver Gotchas y Modelo de amenaza). Aceptado
  para el modelo de amenaza actual (LAN de consultorio, un solo visor); si hiciera falta acotarlo
  más, la vía natural es llevar el contador por IP de origen en vez de global.
- **`paired_tokens.json` no tiene UI de administración ni expiración** (emparejamiento persistente
  por token): la lista solo crece (un token por PIN exitoso) salvo que se revoque a mano
  (Desvincular desde la tablet correspondiente, o borrar el archivo entero desde el visor). No hay
  límite de tokens, ni metadata de "última vez usado"/dispositivo asociado, ni expiración por
  tiempo — para el modelo de amenaza actual (consultorio con pocas tablets) es aceptable; si el
  número de tablets emparejadas creciera, convendría agregar esa metadata para poder auditar/podar
  desde la tablet sin tener que borrar TODO el archivo del visor.
- **`pairing.json`/`paired_tokens.json` no versionan su schema** (mismo patrón de deuda aceptada
  que `presets.json`, ver `docs/tablet.md`): son datos locales, un cambio de forma a futuro
  degradaría a "arranca vacío" en vez de migrar.
