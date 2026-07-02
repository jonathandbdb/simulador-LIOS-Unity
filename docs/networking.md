# Networking visor ↔ tablet

## Qué es y por qué
Capa de comunicación LAN entre el visor Quest (que corre la simulación de lentes intraoculares) y la
tablet Android de control del consultorio. El visor emite un beacon UDP para ser descubierto sin IP
manual, sirve un WebSocket con el protocolo de comandos/estado y transmite la vista del paciente como
JPGs binarios. Todo es un port de la versión Godot (`streaming_server.gd`, `discovery_beacon.gd`).

## Arquitectura actual

| Archivo | Rol |
|---|---|
| `Assets/Scripts/Runtime/Net/NetworkController.cs` | Orquestador del lado visor. Se auto-crea vía `[RuntimeInitializeOnLoadMethod]` (salvo que la escena tenga un `TabletController`: ahí la app es cliente y NO levanta server). Arranca `WebSocketServer` (:9090), `DiscoveryBeacon` (:9091) y `StreamingCapture`; traduce comandos JSON a llamadas sobre `DataManager`, `GlareController` y `ScenarioManager`. |
| `Assets/Scripts/Runtime/Net/WebSocketServer.cs` | Servidor WebSocket RFC 6455 hecho a mano sobre `TcpListener`/`NetworkStream`. Handshake HTTP, framing, ping→pong, broadcast texto/binario. |
| `Assets/Scripts/Runtime/Net/WebSocketClient.cs` | Cliente WebSocket espejo (lado tablet). Handshake cliente, lectura de frames texto/binario, envío de texto **enmascarado** (obligatorio cliente→servidor por RFC 6455). |
| `Assets/Scripts/Runtime/Net/StreamingCapture.cs` | Captura la vista del paciente (cámara propia que sigue la XR camera) y la broadcastea como JPG con header de 1 byte por ojo. |
| `Assets/Scripts/Runtime/Net/DiscoveryBeacon.cs` | Beacon UDP del visor: broadcast a `255.255.255.255:9091` cada 2 s. |
| `Assets/Scripts/Runtime/Net/DiscoveryListener.cs` | Lado tablet: escucha :9091 en un thread y encola las IPs de visores detectados. |
| `Assets/Scripts/Runtime/Net/TabletController.cs` | Consumidor de red del lado tablet: usa `DiscoveryListener` + `WebSocketClient`, parsea `hello`/`vision_state`, decodifica los JPG del stream y envía los comandos (detalle de UI en `docs/tablet.md`). |

```
        VISOR (Quest)                                TABLET (Android)
 ┌──────────────────────────┐                 ┌──────────────────────────┐
 │ NetworkController        │                 │ TabletController         │
 │  ├ DiscoveryBeacon ──────┼─UDP bcast:9091─▶│  ├ DiscoveryListener     │
 │  ├ WebSocketServer :9090 │◀──WS connect────┤  ├ WebSocketClient       │
 │  │   ◀── {"cmd": ...}    │  (texto masked) │  │                       │
 │  │   ──▶ hello / vision_state (texto)      │  │                       │
 │  └ StreamingCapture ─────┼──binario [B|L|R]+JPG──▶ RawImages por ojo  │
 └──────────────────────────┘                 └──────────────────────────┘
```

## Decisiones y porqués
- **WebSocket implementado a mano sobre `System.Net.Sockets`** → el docstring de `WebSocketServer.cs`
  lo dice explícito: *"System.Net.WebSockets server-side (HttpListener) no es confiable en
  IL2CPP/Android (Quest). Esto solo usa System.Net.Sockets, que sí funciona."* Se implementa lo
  mínimo del RFC 6455 (handshake, framing, ping/pong, close).
- **UDP broadcast para descubrimiento (puerto 9091)** → la tablet encuentra al visor sin tipear IP.
  El beacon manda `{"app":"simulador-vr","device_id","ws_port":9090,"ts"}` cada 2 s; el listener
  filtra por el tag `simulador-vr` y usa la IP de origen del paquete (no la del payload).
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

## Protocolo de mensajes (texto JSON, un mensaje por frame WS)

**Visor → tablet:**
- Al conectar (`OnClientConnected`), `hello`:
  ```json
  {"type":"hello","catalog_version":"...","lenses":[{...catálogo completo...}],
   "vision_state":{"left":{"lens_id":"...", "<param>":0.0},"right":{...}},
   "scenario":"ruta_noche","scenarios":["consultorio","ruta_noche"]}
  ```
- Ante cambios: `{"type":"vision_state","vision_state":{"left":{...},"right":{...}}}`.
  Cada ojo serializa `lens_id` + todos los params del `EyeState` aplanados en el mismo objeto.

**Tablet → visor** (campo discriminador `cmd`, en `NetworkController.OnTextReceived`):
- `{"cmd":"apply_lens","lens_id":"<id>","eye":"left|right|both"}` → `DataManager.ApplyLens`.
- `{"cmd":"override_params","eye":"left|right|both","params":{"<param>":valor,...}}` → `DataManager.OverrideParams`.
- `{"cmd":"set_astigmatism","enabled":bool,"magnitude":0..1,"angle":radianes}` → `GlareController.SetAstigmatism`.
  La tablet además manda `"eye"`, pero el visor lo **ignora** (ver Gotchas).
- `{"cmd":"load_scenario","scenario":"consultorio|ruta_noche"}` → `ScenarioManager.SwitchTo`.
- Cualquier otro `cmd` loguea warning; texto no-JSON se descarta con warning.

**Stream binario:** `[1 byte header B/L/R][JPG]`, 768×576, 20 Hz, calidad JPG 85
(constantes en `StreamingCapture.cs`).

## Modelo de threading
- **Server:** un thread `WSAccept` acepta clientes + un thread `WSRead{id}` por cliente. Estos
  encolan en `_connected`/`_textIn`/`_disconnected`; `NetworkController.Update()` llama
  `PumpEvents()` que dispara los eventos C# ya en el hilo principal.
- **Escrituras del server:** `SendFrame` puede llamarse desde cualquier thread; serializa por
  cliente con `Client.WriteLock` (el broadcast del JPG viene del thread de encode).
- **Cliente:** un único thread `WSClient` hace connect + handshake + read loop; `SendText` se llama
  desde el hilo principal con `_writeLock`. Flags `volatile` `_connectedFlag`/`_closedFlag` +
  colas, drenados por `TabletController.Update()` → `PumpEvents()`.
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
  `TabletController.OnDestroy` cierra listener y WS. Sin eso, los threads background quedan
  bloqueados en `Read`/`Receive` y el puerto queda tomado entre play modes en el Editor. `Stop()`
  cierra el socket para desbloquear los `Read` (los threads son `IsBackground = true` como red de seguridad).
- **Masking direccional:** cliente→servidor DEBE ir enmascarado (el server igual tolera frames sin
  máscara); servidor→cliente va sin máscara. Si se toca el framing, respetar esto o los browsers/
  peers estrictos cortan la conexión.
- **Permisos Android:** no hay manejo de permisos en código. Requiere `INTERNET` (Player Settings →
  Internet Access: Require). `DiscoveryListener` omite adrede el `MulticastLock` de Android — el
  comentario del archivo dice que en la mayoría de redes el broadcast llega; en redes que lo filtran
  la tablet no descubre nada y hay que usar la conexión manual por IP (que existe en la UI).
- **`set_astigmatism` es global:** la tablet manda `"eye"` pero `NetworkController` no lo lee y
  `GlareController.SetAstigmatism(enabled, magnitude, angle)` no recibe ojo. Asimetría de protocolo latente.
- **Lista de escenarios hardcodeada** en `BuildHello()` (`"consultorio"`, `"ruta_noche"`): agregar
  un escenario nuevo requiere tocar ese JArray, no se descubre solo.
- **Headers de 8 bytes truncados al enviar:** en `SendFrame`/`SendText` el largo de 64 bits solo
  escribe los 4 bytes bajos (los 4 altos van en 0). Correcto mientras ningún payload supere 4 GB, pero
  no es una serialización completa del RFC.
- **`Server.OpenClientCount` desde `LateUpdate`** evita capturar/encodear sin clientes: si se agrega
  otro consumidor del stream, mantener ese guard o el visor paga el costo de render+encode gratis.

## Cómo probar
1. **En Editor (loopback):** abrir `Assets/Scenes/Main.unity` y dar Play — `NetworkController` se
   auto-instancia y loguea `WebSocketServer: escuchando en :9090` y el beacon a :9091. En otra
   instancia (o build) correr `Assets/Scenes/Tablet.unity`: debe listar el visor descubierto en
   segundos; si no, usar "Conexión manual" con `127.0.0.1`.
2. Verificar en consola del visor `Net: cliente N conectado, enviando hello.` y que la tablet pase a
   la pantalla principal con el catálogo de lentes y el stream moviéndose (~20 fps en el footer).
3. Tocar una lente en la tablet → el visor debe cambiar el render y devolver `vision_state` (los
   chips OD/OI de la card se encienden con la confirmación).
4. Aplicar lentes distintas por ojo → el stream debe pasar a dos paneles (frames `L` y `R` separados).
5. Probar `load_scenario` y `set_astigmatism` y mirar warnings `Net: comando desconocido` ante typos.
6. En device real: visor y tablet en la misma Wi-Fi; si el discovery no anda, es la red filtrando
   broadcast → validar el fallback de IP manual.

## Pendientes / deuda
- `set_astigmatism` ignora el ojo (la tablet ya lo envía; falta soporte por-ojo en `GlareController`/`NetworkController`).
- Lista de escenarios hardcodeada en `NetworkController.BuildHello()`.
- Sin `MulticastLock` Android en `DiscoveryListener` (documentado como "si hiciera falta se agrega").
- Sin close handshake WS saliente (se cierra el TCP directo) ni ping keep-alive propio.
