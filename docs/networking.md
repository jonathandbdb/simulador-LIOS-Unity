# Networking visor ↔ tablet

## Qué es y por qué
Capa de comunicación LAN entre el visor Quest (que corre la simulación de lentes intraoculares) y la
tablet Android de control del consultorio. El visor emite un beacon UDP para ser descubierto sin IP
manual, sirve un WebSocket con el protocolo de comandos/estado y transmite la vista del paciente como
JPGs binarios. Todo es un port de la versión Godot (`streaming_server.gd`, `discovery_beacon.gd`).

## Arquitectura actual

| Archivo | Rol |
|---|---|
| `Assets/Scripts/Runtime/Net/NetworkController.cs` | Orquestador del lado visor. Se auto-crea vía `[RuntimeInitializeOnLoadMethod]` (salvo que la escena tenga un `TabletController`: ahí la app es cliente y NO levanta server). Genera el `PairingPin` (6 dígitos) de la sesión, arranca `WebSocketServer` (:9090), `DiscoveryBeacon` (:9091) y `StreamingCapture`; valida el emparejamiento por PIN (capa de protocolo) y traduce comandos JSON a llamadas sobre `DataManager`, `GlareController` y `ScenarioManager`. |
| `Assets/Scripts/Runtime/Net/WebSocketServer.cs` | Servidor WebSocket RFC 6455 hecho a mano sobre `TcpListener`/`NetworkStream`. Handshake HTTP, framing, ping→pong, broadcast texto/binario. Guarda el flag `Authenticated` por cliente (lo fija `NetworkController` tras validar el PIN) y filtra los broadcasts por ese flag. |
| `Assets/Scripts/Runtime/Net/WebSocketClient.cs` | Cliente WebSocket espejo (lado tablet). Handshake cliente, lectura de frames texto/binario, envío de texto **enmascarado** (obligatorio cliente→servidor por RFC 6455). |
| `Assets/Scripts/Runtime/Net/StreamingCapture.cs` | Captura la vista del paciente (cámara propia que sigue la XR camera) y la broadcastea como JPG con header de 1 byte por ojo. |
| `Assets/Scripts/Runtime/Net/DiscoveryBeacon.cs` | Beacon UDP del visor: broadcast a `255.255.255.255:9091` cada 2 s. |
| `Assets/Scripts/Runtime/Net/DiscoveryListener.cs` | Lado tablet: escucha :9091 en un thread y encola las IPs de visores detectados. |
| `Assets/Scripts/Runtime/Tablet/TabletSession.cs` (P6.2) | Consumidor de red del lado tablet: usa `DiscoveryListener` + `WebSocketClient`, parsea `hello`/`vision_state`, separa el header de los JPG del stream y expone eventos hacia `TabletController` (UI). Antes de P6.2 esto vivía en `TabletController.cs`; detalle completo del split en `docs/tablet.md`. |
| `Assets/Scripts/Runtime/Net/TabletController.cs` | Capa de UI de la tablet (namespace `Simulador.Tablet` desde P6.2): consume los eventos de `TabletSession` y decodifica los JPG en `RawImage` por ojo (detalle en `docs/tablet.md`). |

```
        VISOR (Quest)                                TABLET (Android)
 ┌──────────────────────────┐                 ┌──────────────────────────┐
 │ NetworkController        │                 │ TabletSession (P6.2)     │
 │  ├ DiscoveryBeacon ──────┼─UDP bcast:9091─▶│  ├ DiscoveryListener     │
 │  ├ WebSocketServer :9090 │◀──WS connect────┤  ├ WebSocketClient       │
 │  │   ◀── {"type":"auth","pin":...}  (1er mensaje, texto masked)       │
 │  │   ──▶ auth_ok | auth_fail | auth_locked   │  │                       │
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
  del payload) para identificar al visor — `device_label` es puramente informativo, ningún código
  lo parsea (ver Gotchas).
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

## Protocolo de mensajes (texto JSON, un mensaje por frame WS)

**Emparejamiento (previo a todo lo demás, ver Decisiones y porqués):**
- Tablet → visor, primer mensaje de la conexión: `{"type":"auth","pin":"NNNNNN"}`.
- Visor → tablet, una de tres:
  - `{"type":"auth_ok"}` — PIN correcto; inmediatamente después manda el `hello`.
  - `{"type":"auth_fail"}` — PIN incorrecto (y no hay lockout activo); el visor cierra esa
    conexión ahí mismo, la tablet debe reconectar para reintentar.
  - `{"type":"auth_locked","retry_in_s":N}` — se agotaron los `MaxAuthFailures` (3) intentos
    fallidos y la ventana de lockout (60 s) todavía no expiró; el visor NO evalúa el PIN mandado
    (puede ser el correcto) y cierra la conexión igual. `retry_in_s` es la cuenta regresiva
    redondeada hacia arriba.
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
- `{"cmd":"refresh"}` (P5.4) → el visor responde al MISMO cliente (`SendTextTo`, no broadcast) con
  el payload EXACTO de un `hello` (`BuildHello()` reusado tal cual). Permite reconstruir
  catálogo/escenarios/vision_state sin reconectar/re-autenticar — útil si el clínico sabe que el
  catálogo cambió en caliente (p.ej. sync con backend recién terminado). Nota de nombre: pese a
  llamarse informalmente "comando de refresh", sigue la convención `cmd` de todos los comandos
  autenticados (no introduce un segundo discriminador `"type"` para mensajes tablet→visor).
- Cualquier otro `cmd` loguea warning; texto no-JSON se descarta con warning.

**Stream binario:** `[1 byte header B/L/R][JPG]`, 768×576, 20 Hz, calidad JPG 85
(constantes en `StreamingCapture.cs`). Solo se manda a clientes autenticados (gate:
`WebSocketServer.AuthenticatedClientCount`, reemplazó a `OpenClientCount` en el `LateUpdate` de
`StreamingCapture`).

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
- **`device_label` del beacon es decorativo, no una clave:** `DiscoveryListener.Loop` solo chequea
  que el payload contenga `"app"` + el tag `simulador-vr` y encola la IP de ORIGEN del paquete
  (`ep.Address`); nunca deserializa el JSON ni lee `device_label`. `TabletSession._seenHosts`
  (P6.2 — antes en `TabletController`) y `_pinByHost` están keyeados por esa IP, no por el label.
  Si algún día se necesita distinguir dos
  visores en la misma IP (improbable en LAN doméstica/consultorio) o mostrar el nombre en la UI de
  descubrimiento, ahí sí habría que parsear el JSON del lado tablet — hoy no lo hace.
- **Solo el label de escenario sigue a mano en Net (P2.3, cerrado):** agregar un escenario nuevo
  en `ScenarioManager.Order` (Vision/) ya aparece solo en `hello.scenarios` (vía `ScenarioOrder`);
  si no se le agrega también una entrada en `NetworkController.ScenarioLabels`, el label cae al
  fallback (capitaliza el id) — funciona pero menos prolijo que un label a mano. No rompe nada:
  ya no hay riesgo de que la LISTA de ids diverja entre Vision/ y Net.
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
El canal ahora **exige PIN** (P1.1, emparejamiento por PIN — ver Decisiones y porqués y Protocolo):
cualquier dispositivo en la LAN que descubra el visor (o conozca su IP) todavía puede abrir el
socket WS, pero no puede leer catálogo/estado ni mandar comandos sin conocer el PIN de 6 dígitos
que el visor muestra en su HUD para esa sesión. Esto sube el costo de un atacante casual en la
misma LAN de "cualquiera que conecte" a "alguien que vio la pantalla del visor" — no es
criptográficamente fuerte (el PIN viaja en texto plano, sin TLS) pero cierra el acceso trivial.
Sigue **sin TLS**: el PIN y todo el tráfico posterior (comandos, vision_state, stream JPG) van sin
cifrar; alguien que ya esté haciendo sniffing pasivo de la LAN en el momento del handshake puede
capturar el PIN y suplantar a la tablet. TLS queda como deuda pendiente (no forma parte de esta
tarea). El lockout de intentos fallidos (`MaxAuthFailures=3` en una ventana de `LockWindowMs=60s`,
contado por conexión — ver Decisiones y porqués) mitiga fuerza bruta básica (3 intentos/min sobre
un espacio de 10^6 no es practicable) sin exigir reiniciar el visor, pero al ser un contador
global también da a un atacante en la LAN una forma barata de bloquear temporalmente al clínico
legítimo agotando el tope a propósito (ver Gotchas).

## Cómo probar
1. **En Editor (loopback):** abrir `Assets/Scenes/Main.unity` y dar Play — `NetworkController` se
   auto-instancia y loguea `WebSocketServer: escuchando en :9090`, el beacon a :9091 y el PIN de la
   sesión (`Net: PIN de emparejamiento de esta sesion: NNNNNN`; en el visor real lo muestra el HUD,
   fuera de este cambio). En otra instancia (o build) correr `Assets/Scenes/Tablet.unity`: debe
   listar el visor descubierto en segundos; si no, usar "Conexión manual" con `127.0.0.1`.
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
6. **Reconexión reusando PIN:** con la tablet ya autenticada una vez, desconectar (botón
   Desconectar) y volver a tocar el mismo visor → debe conectar directo sin mostrar el `PinScreen`
   (usa el PIN guardado en memoria de esa sesión de la tablet).
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

## Pendientes / deuda
- Sin `MulticastLock` Android en `DiscoveryListener` (documentado como "si hiciera falta se agrega").
- Sin close handshake WS saliente (se cierra el TCP directo). El keep-alive con ping propio (ver
  Decisiones y porqués) sí está resuelto en ambos lados.
- **Sin TLS** (ver Modelo de amenaza arriba): el PIN y el resto del tráfico van sin cifrar.
- **HUD del visor todavía no muestra `PairingPin`/`AuthenticatedClientCount`** — ambos expuestos
  como propiedad pública en `NetworkController`, falta que `Vision/` los pinte (fuera de alcance
  de esta tarea).
- **Lockout global (no por IP)** — un atacante en la LAN puede agotar el tope a propósito y
  bloquear también al clínico legítimo por hasta 60 s (ver Gotchas y Modelo de amenaza). Aceptado
  para el modelo de amenaza actual (LAN de consultorio, un solo visor); si hiciera falta acotarlo
  más, la vía natural es llevar el contador por IP de origen en vez de global.
