# App tablet (control de consultorio)

## Qué es y por qué
App Android plana (sin VR) que corre en la tablet del oftalmólogo: descubre el visor Quest en la
red, se conecta por WebSocket, muestra en vivo lo que ve el paciente (stream por ojo) y permite
aplicar lentes intraoculares, ajustar parámetros clínicos, simular astigmatismo, cambiar de
escenario, ver el stream a pantalla completa, guardar/cargar presets de sesión y refrescar el
catálogo en caliente (P5). Es la réplica fiel de `features/tablet/streaming_client.gd` del
proyecto Godot original, con extensiones de flujo clínico propias del simulador. La comparación
A/B (P5.1) se agregó y luego se retiró (P6.8, ver Decisiones) — nunca se usó en la práctica clínica.

## Arquitectura actual

**P6.2 (split god-object):** hasta esta tarea `TabletController` era una sola clase con TODO
(red + protocolo + estado de sesión + construcción de UI, >1400 líneas). Se partió en dos capas
— ver Decisiones "Split sesión/UI" para el porqué y el mapeo detallado de responsabilidades:

| Archivo | Rol |
|---|---|
| `Assets/Scripts/Runtime/Tablet/TabletSession.cs` (nuevo, P6.2) | **Capa de sesión/protocolo.** Plain C# (NO MonoBehaviour). Posee `WebSocketClient` + `DiscoveryListener`, el flujo de conexión/emparejamiento (PIN de 6 dígitos o token persistente, ver Decisiones "Emparejamiento persistente por token"), la máquina de reconexión automática (P2.5) y el estado de sesión (`vision_state`, catálogo, escenarios, mapa host→token persistido en `pairing.json`, hosts descubiertos). Expone eventos tipados (`Connected`, `AuthOk`, `PinScreenRequested`, `ShowConnectScreenRequested`, `ReconnectStarted`, `ReconnectStatusChanged`, `HelloReceived`, `VisionStateChanged`, `FrameReceived`) y propiedades read-only (`IsConnecting`, `IsSessionActive`, `IsReconnecting`, `IsWsOpen`, `CurrentHost`, `DiscoveredHosts`, `LensesById`, `VisionState`, `Scenarios`, `ScenarioLabels`, `CurrentScenario`). Namespace `Simulador.Tablet`. |
| `Assets/Scripts/Runtime/Net/TabletController.cs` | **Capa de UI.** MonoBehaviour único de la app (sigue en `Net/` con ese nombre — la escena `Tablet.unity` lo referencia por GUID del `.cs`, ver Gotchas — pero cambió de namespace `Simulador.Net` → `Simulador.Tablet`, P6.2). Construye toda la interfaz en `Start()`, crea y drena la `TabletSession` en su `Update()` (`session.Update(Time.deltaTime)`), traduce eventos de sesión → widgets (`OnSession*` handlers) y clicks → métodos de la sesión (`_session.Connect/Disconnect/SendCommand/CancelReconnect/...`). |
| `Assets/Scripts/Runtime/Tablet/TabletUiKit.cs` | Fábrica de widgets uGUI temables: `Label`, `Button`, `Panel`/`Card`, `Slider`, `LineEdit` (TMP_InputField), `CheckToggle`, `StatusBadge`, `RawImage`, `ScrollColumn`, `Box`/`Spacer`/`Size`. Genera el sprite de esquinas redondeadas por código (9-slice cacheado por radio) y registra un callback de "repaint" por widget para retematizar en caliente. |
| `Assets/Scripts/Runtime/Tablet/TabletPalette.cs` | Paletas Dark (consola médica, teal) y Light (historia clínica, azul); port verbatim de las constantes del `theme_builder.gd` de Godot. |
| `Assets/Scripts/Runtime/Tablet/TabletButton.cs` | Botón custom (hereda `Selectable`): fill + borde + texto con color por estado (normal/hover/pressed), modo toggle y callbacks `OnClick`/`OnToggled`. Reemplaza el `ColorBlock` de uGUI. |
| `Assets/Scripts/Runtime/Tablet/LensCardView.cs` | Card de lente: nombre, descripción clínica, chips OD/OI que marcan en qué ojo(s) está aplicada; tap = aplicar. |
| `Assets/Scripts/Runtime/Tablet/ParamRowView.cs` | Fila de ajuste fino: label + valor formateado + slider + hint clínico. `SetValueSilent` sincroniza sin re-emitir. |
| `Assets/Scripts/Runtime/Tablet/ScrollFriendlySlider.cs` (nuevo, usabilidad táctil) | Subclase de `Slider` que le cede el drag vertical al `ScrollRect` padre en vez de consumirlo (ver Decisiones "Usabilidad táctil"). Usada por `TabletUiKit.Slider()` en vez del `Slider` estándar. |
| `Assets/Scripts/Runtime/Tablet/ParamMeta.cs` | Metadata clínica estática de los parámetros del catálogo (ver abajo). |
| `Assets/Scenes/Tablet.unity` | Escena mínima: raíces `TabletApp` (con `TabletController`), `Directional Light` y `Main Camera`. Nada de UI serializada. |
| `Assets/Resources/TabletFonts/` | `Inter-Regular SDF` e `Inter-SemiBold SDF` (TMP_FontAsset), cargados con `Resources.Load` en `Start()`. |
| `Assets/Scripts/Editor/TabletBuild.cs` | Menú **Simulador → Build Tablet (Android)**: buildea solo `Tablet.unity` con el loader de OpenXR apagado y lo restaura al terminar. Detalle en `docs/builds-deploy.md`. |

```
TabletController.Start()
  Resources.Load fuentes ─▶ new TabletUiKit(paleta según prefs)
  BuildUI()  ─▶ ConnectScreen + PinScreen + ReconnectScreen + MainScreen (ocultas salvo ConnectScreen)
  new TabletSession() ─▶ suscribe OnSession* a los eventos ─▶ session.Begin()
                           (crea WebSocketClient + DiscoveryListener, arranca discovery)
                │
   beacon UDP ──┴─▶ TabletSession._seenHosts ─▶ UI lee session.DiscoveredHosts en RefreshDiscovered
                    (diff in-place, poda a los 6 s sin beacon — poda vive en TabletSession.Update)
   tap visor / IP manual ─▶ UI.StartConnectFlow(host)
      ├─ session.TryGetCachedToken(host) ─▶ UI.BeginConnectWithToken(host, token) directo
      └─ sin token guardado ─▶ UI.ShowPinScreen(host) ─▶ "Conectar" ─▶ UI.BeginConnect(host, pin)
   UI.BeginConnect(WithToken) ─▶ ShowConnectScreen("Conectando...") + session.Connect/ConnectWithToken
   session: ws Connected ─▶ SendCommand({"type":"auth","pin"|"token":...}) ─▶ evento Connected ─▶ UI actualiza texto
   session: "auth_ok" ─▶ si trae "token" (vino de PIN) lo persiste en pairing.json ─▶ evento AuthOk ─▶ UI.SetConnectStatus(...)
   session: "auth_fail" ─▶ el visor cierra la conexión ─▶ OnWsDisconnected interno ─▶ evento
                           PinScreenRequested (mensaje segun reason: "pin"→"PIN incorrecto", "token"→
                           "emparejamiento ya no válido"), corta reconexión; reason=="token" además
                           borra la entrada de pairing.json
   session: "auth_locked" ─▶ el visor cierra la conexión ─▶ OnWsDisconnected interno:
                    si NO se estaba reconectando ─▶ evento PinScreenRequested ("Demasiados intentos...")
                    si SÍ (P2.5) ─▶ evento ReconnectStatusChanged, sigue el loop tras retry_in_s
   session: caída NO manual de sesión activa (P2.5) ─▶ StartReconnectLoop() interno (usa el token
                    persistido de _currentHost) ─▶ eventos ReconnectStarted + ReconnectStatusChanged
                    ─▶ UI.ShowReconnectScreen + backoff 2/4/8/15s
   session: "hello" ─▶ actualiza catálogo/vision_state/escenarios ─▶ evento HelloReceived(lenses) ─▶
                    UI.RebuildLensList/RebuildScenarioList/RefreshVisionUI/ShowMainScreen
   session: binario 'B'/'L'/'R'+JPG ─▶ separa header ─▶ evento FrameReceived(eye, jpg) ─▶
                    UI.LoadImage en RawImage por ojo + contadores del footer
   UI ─▶ session.SendCommand(apply_lens / override_params / set_astigmatism / load_scenario / refresh)
   UI (boton Desvincular) ─▶ session.Unpair() ─▶ SendCommand({"cmd":"unpair"}) + borra token local +
                    Disconnect("Desvinculado...") ─▶ evento ShowConnectScreenRequested
   session: "vision_state" ─▶ evento VisionStateChanged ─▶ UI.RefreshVisionUI + SyncParamRowsFromState()
```

## Pantallas y secciones (todas en `TabletController`, capa de UI)
- **ConnectScreen:** glifo de ojo + título, lista de visores descubiertos (botones con un nombre
  amigable — `Visor Quest` o el `device_label` del beacon sin el nonce, desambiguado con `(2)`,
  `(3)`... si hay más de uno; **la IP nunca aparece en la UI**, ver Decisiones "Lista de visores
  sin IP"), único camino de conexión — **sin conexión manual**, ver Decisiones "Solo
  descubrimiento automático"), estado de búsqueda, label de red Wi-Fi actual (`RefreshNetworkInfo`
  + permiso de ubicación pedido una vez por sesión — ver Decisiones "Info de red Wi-Fi"), mensaje
  de ayuda fijo ("El visor Quest y la tablet deben estar conectados a la misma red Wi-Fi.") y
  botón **Salir** (`Application.Quit()`).
- **PinScreen:** se intercala entre ConnectScreen y MainScreen cuando hace falta el PIN de
  emparejamiento (host SIN un token persistente válido en `pairing.json` -- primer enlace,
  Desvincular previo, o el token quedó revocado/inválido; o reintento tras `auth_fail`/
  `auth_locked`). Glifo + título, host destino, `LineEdit` numérico de 6 dígitos
  (`TMP_InputField.ContentType.IntegerNumber` → teclado numérico en Android) y botones
  Cancelar/Conectar. El mensaje de estado distingue PIN incorrecto ("PIN incorrecto. Volvé a
  intentarlo.") de token inválido ("El emparejamiento con este visor ya no es válido. Ingresá el
  PIN nuevamente.") de lockout del visor ("Demasiados intentos. Esperá Ns y volvé a intentarlo.",
  con el `retry_in_s` que manda el visor). Detalle del protocolo y el modelo de amenaza en
  `docs/networking.md`. **Anclado arriba-centro, no centrado** (el popup `PinWrap` usa
  `anchorMin/anchorMax/pivot = (0.5, 1)` + `anchoredPosition = (0, -40)` en vez de `(0.5, 0.5)`,
  ver Decisiones "Popup del PIN en el tercio superior") para que el teclado nativo de Android
  (cubre la mitad inferior de la pantalla) no tape el `LineEdit` al escribir el PIN.
  ConnectScreen/ReconnectScreen (sin teclado) siguen centrados, sin cambios.
- **ReconnectScreen (P2.5):** se muestra ante una caída NO manual de una sesión activa. Glifo +
  título "Reconectando", host destino, estado (cuenta atrás del backoff o "Reconectando… (intento
  N)") y botón Cancelar (corta el loop y vuelve al `ConnectScreen`). Ver StartReconnectLoop/
  DoReconnectAttempt/OnWsDisconnected y Decisiones abajo.
- **MainScreen / Header:** glifo + título, selector de escenarios (segment buttons), toggle de tema
  claro/oscuro, botón "Actualizar" (P5.4 — refresh en caliente, ver Decisiones), botón **"Ocultar
  HUD"/"Mostrar HUD"** (toggle del HUD de diagnóstico del visor vía comando `set_hud`, ver
  Decisiones "Toggle del HUD del visor"), badge de estado (punto de color + texto), botón
  Desconectar y botón **Desvincular** (emparejamiento persistente por token: revoca el token de
  esta tablet en el visor y olvida el emparejamiento local con el host actual -- ver Decisiones
  "Emparejamiento persistente por token").
- **Panel de stream (izquierda):** uno o dos panes con `RawImage` dentro de un `AspectRatioFitter`
  4:3 (768/576). El split lo decide `blend_active` del `vision_state` (P2.1 — fuente única de
  verdad, ver `docs/networking.md`): en blend los panes se apilan verticalmente, **OD arriba /
  OI abajo** (convención clínica OD-primero, ver Decisiones) con etiquetas
  (`OD · <lente>` / `OI · <lente>`); si no, un solo pane "Ambos ojos" (incluye el caso de un solo
  ojo con lente aplicada: antes mostraba 2 panes con uno de etiqueta vacía). Botón **"Pantalla
  completa"** (P6.8, esquina superior derecha del panel) abre el overlay de stream a pantalla
  completa — ver "Stream a pantalla completa" más abajo y Decisiones.
- **Columna scrolleable (derecha):** cards "Ojo a tratar" (Ambos / OD / OI), "Lentes intraoculares"
  (LensCardViews del catálogo), "Ajuste fino" (colapsable, ParamRowViews de la lente en edición +
  Restaurar valores — desde P4.4 incluye `astig_magnitude`/`astig_axis_deg`, persistentes por
  lente), "Astigmatismo" (colapsable: hint de precedencia + switch + sliders LIVE de magnitud
  0–50 px y eje 0–180°, no persistente — ver Decisiones "Dos controles de astigmatismo") y
  **"Presets"** (P5.2, colapsable: lista de presets guardados con Aplicar/Borrar + `LineEdit` de
  nombre y botón Guardar — ver Decisiones). La card "Comparar A / B" (P5.1) se retiró en P6.8 —
  ver Decisiones "Stream a pantalla completa, retiro de Comparar A/B".
- **Stream a pantalla completa (P6.8, overlay `FullscreenStream`):** se abre con el botón
  "Pantalla completa" del panel de stream o se cierra con el botón "Cerrar" (esquina superior
  derecha del overlay) o tocando en cualquier punto del fondo. Reusa las mismas `Texture2D`
  del panel normal (`_texLeft`/`_texRight`, ver `OnSessionFrame`) — no hay una segunda
  decodificación de JPG. Sigue el mismo criterio que el panel normal
  (`blend_active`/`RefreshVisionUI`): si ambos ojos comparten lente (incluido el caso sin lente
  en ninguno) muestra 1 imagen; si `blend_active` es `true` (lentes distintas por ojo) muestra 2
  paneles lado a lado, **OD a la izquierda / OI a la derecha** (misma convención OD-primero que el
  panel normal, ver Decisiones) con etiquetas "OD — \<lente\>" / "OI — \<lente\>". Reacciona a un
  cambio de lente mientras está abierto porque `RefreshFullscreenUI` se llama siempre desde
  `RefreshVisionUI` (no solo al abrir el overlay).
- **Footer:** `N fps · X.X MB recibidos`, actualizado cada segundo. En blend (P5.5), el fps
  mostrado se divide entre 2 (ver Decisiones): representa la tasa REAL por pane, no la suma L+R.

## Decisiones y porqués
- **Convención OD-primero en toda la UI (orden de presentación, no de protocolo)** → el ojo
  derecho (OD) se muestra siempre antes/arriba/a la izquierda que el izquierdo (OI), siguiendo la
  convención clínica habitual. Esto es puramente visual: los ids de protocolo (`"left"`/`"right"`
  en `vision_state`, comandos, presets) y el mapeo de los botones del visor (A cicla OI, B cicla
  OD — ver `docs/vision-optica.md`) NO cambian. Afecta: panel de stream normal (`BuildBody`, OD
  arriba / OI abajo — el pane derecho se crea antes que el izquierdo en `EyesContainer` para
  quedar primero en el stack vertical), overlay a pantalla completa (`BuildFullscreenStream`, OD a
  la izquierda / OI a la derecha — mismo truco de orden de creación en `FullscreenRow`). El
  selector "Ojo a tratar" (`BuildEyeCard`) y los chips OD/OI de `LensCardView` YA tenían OD antes
  que OI y no se tocaron.
- **Split sesión/UI: `TabletSession` (plain C#) + `TabletController` (MonoBehaviour) (P6.2)** →
  `TabletController` había crecido a un god-object (>1400 líneas: red + protocolo + estado +
  construcción de UI) a medida que se agregaron PIN/reconexión/A-B/presets/refresh. Refactor
  MECÁNICO (no rediseño): se movió código tal cual a `TabletSession.cs` (mismos nombres de método/
  campo/rama de decisión donde fue posible — `OnWsConnected`, `OnWsDisconnected`,
  `StartReconnectLoop`, `ScheduleNextReconnectAttempt`, `DoReconnectAttempt`, `CancelReconnect`
  —ex `OnReconnectCancelPressed`—, `OnText`, `OnBinary` siguen con la misma lógica interna), y las
  líneas que tocaban UI (`ShowXScreen`, `SetXStatus`, `RebuildXList`) se reemplazaron por
  invocaciones de eventos que `TabletController` consume con un handler `OnSession*` que ejecuta
  EXACTAMENTE esas mismas líneas. El diff es "código idéntico, repartido en dos archivos + glue".
  **Qué NO se movió a evento y por qué:** `VisionState` (JObject) y `CurrentScenario` (string) se
  exponen como propiedades MUTABLES (no solo getters) porque la UI los sigue mutando
  directamente para la actualización optimista (`OnLensSelected`/`OnScenarioPressed`, igual que
  antes del split) — encapsularlos detrás de métodos de sesión hubiera sido rediseño, no refactor.
  `RefreshDiscovered`'s pruning de hosts viejos pasó de "solo con ConnectScreen visible, 1 vez/seg"
  a "cada `Update()` de `TabletSession`, siempre" — equivalente observable (nadie lee
  `DiscoveredHosts` salvo el ConnectScreen) pero más simple de razonar sin depender de un guard de
  UI.
  **Qué se dejó deliberadamente FUERA de la lista de eventos "sugerida"** (el handoff mencionaba
  `Disconnected(reason)` y `ReconnectTick(attempt,countdown)` como ejemplos, con "..."): no hay un
  evento `Disconnected` genérico único — se preservaron las ramas EXACTAS de la `OnWsDisconnected`
  original como eventos más finos (`PinScreenRequested`, `ShowConnectScreenRequested`,
  `ReconnectStarted`, `ReconnectStatusChanged`) para no tener que reconstruir esa lógica de
  branching en la UI. Tampoco hay `ReconnectTick` con countdown numérico: la UI original NUNCA
  mostró una cuenta regresiva en vivo (el texto se fija una vez al programar el siguiente intento,
  no se actualiza frame a frame) — agregar un tick así habría sido UX nueva, no refactor.
- **`TabletController` cambió de namespace pero NO de archivo ni de nombre de clase (P6.2)** →
  `Simulador.Net` → `Simulador.Tablet`. La escena `Tablet.unity` referencia el componente por el
  GUID del `.meta` del archivo `.cs` (no por namespace/nombre de tipo en texto), así que el cambio
  de namespace NO produce Missing Script — verificado en Play Mode: `Tablet.unity` sigue
  arrancando `TabletController.Start()` (logs `Simulador.Tablet.TabletController:Start()` /
  `Simulador.Tablet.TabletSession:Begin()`) y `Main.unity` sigue levantando
  `WebSocketServer`+`DiscoveryBeacon` normalmente (ver Gotchas — ese es el riesgo real del split,
  no el archivo). `NetworkController.cs` (namespace `Simulador.Net`) agregó
  `using Simulador.Tablet;` para poder seguir resolviendo `FindFirstObjectByType<TabletController>()`
  en su `Bootstrap()`.
- **UI 100 % construida por código, cero prefabs** → la app es un port 1:1 de una UI Godot definida
  también por código (`theme_builder.gd` + escenas de `features/tablet/`); generar los widgets con
  `TabletUiKit` mantiene la paridad visual, evita mantener prefabs/`.unity` binarios en paralelo al
  código y hace el theming trivial (ver siguiente punto). El costo: no hay nada que inspeccionar en
  el Editor hasta darle Play.
- **Theming por "repaint" registrado** → cada widget registra `Action<TabletPalette>` en el kit;
  `ApplyTheme` solo hace `kit.Apply(paleta)` y toda la jerarquía se repinta sin reconstruirse.
  La preferencia se persiste en `Application.persistentDataPath/ui_prefs.cfg` (`dark_mode=`).
- **`TabletButton` en vez de `Button` de uGUI** → los StyleBox de Godot cambian fill, borde y texto
  por estado y por modo toggle; el `ColorBlock` de uGUI no puede, así que se pinta a mano en `Repaint()`.
- **Sprites redondeados generados en runtime** → `TabletUiKit.Rounded(radius)` fabrica la textura
  del 9-slice por código (cache por radio): ninguna dependencia de assets de imagen.
- **`ParamMeta` como capa clínica** → el catálogo trae claves de shader (`foco_lejos_m`,
  `halo_intensity`, `straylight`…); `ParamMeta.META` les da label, hint clínico, unidad y formato, y
  `ParamMeta.ORDER` impone el orden de presentación (focos → blur/astigmatismo → disfotopsias).
  Claves fuera de la metadata caen al final con su nombre crudo (orden del catálogo): el catálogo
  puede crecer sin tocar la tablet — es presentación pura, nunca bloquea que un param nuevo llegue
  a "Ajuste fino".
- **`astig_magnitude`/`astig_axis_deg` en `ParamMeta` (P4.4)** → el catálogo (`0.5.0-clinical`)
  agregó estos 2 params persistentes por lente (astigmatismo residual del PACIENTE, no de la
  LIO). `ParamMeta` les da label ("Astigmatismo residual" / "Eje del astigmatismo"), hint clínico
  y formato (`F2` sin unidad para la magnitud 0–1; `F0` en `°` para el eje 0–180) y los ubica en
  `ORDER` junto a `desenfoque_max` (antes de halos/destellos): astigmatismo es error refractivo
  (como el blur), no un artefacto difractivo. Aparecen solos en "Ajuste fino" al elegir una lente
  — no hizo falta tocar `TabletController`/`ParamRowView` (ya toleran claves nuevas del catálogo).
  Detalle óptico y el pipeline per-eye que consumen en `Vision/`: `docs/vision-optica.md` y
  `docs/catalogo-lentes.md`.
- **Dos controles de astigmatismo, distinta vida (P4.4, deliberado — sin refactor esta tanda)** →
  la card "Astigmatismo" (switch + sliders px/°, `set_astigmatism`) es un ajuste LIVE que NO
  persiste y se pisa apenas cambia la lente o llega un `override_params`; los nuevos
  `astig_magnitude`/`astig_axis_deg` de "Ajuste fino" SÍ persisten (viven en el catálogo/
  `lens_overrides.json`, igual que cualquier otro param). Son dos caminos independientes hacia el
  mismo efecto visual (`glare_astig_l/r` per-eye) sin sincronizar entre sí. Para no confundir al
  operador, la card live ahora agrega un hint de precedencia (ver `BuildAstigCard`); no se
  fusionaron los controles ni se auto-sincronizaron en esta tarea — evaluar en una tarea futura si
  conviene.
- **Actualización optimista + confirmación** → al tocar una lente se actualiza `_visionState` local
  al instante (UI responsiva) y el `vision_state` del visor después corrige/completa; los sliders se
  sincronizan con `SetValueSilent` para no generar eco de comandos.
- **El ajuste fino sigue a la lente, no al selector de ojo** → `EyesForEditingLens()` calcula a qué
  ojo(s) mandar el `override_params` según dónde esté aplicada la lente en edición; así no se pisan
  parámetros del ojo equivocado si el operador cambió el selector "Ojo a tratar".
- **Astigmatismo convertido en la tablet** → el slider muestra px (0–50, fiel a Godot) y grados,
  pero envía magnitud normalizada `valor/50` y ángulo en radianes, que es lo que espera el
  `GlareController` del visor.
- **Build propio con OpenXR OFF** → el target Android es compartido con Quest; si el loader OpenXR
  queda activo en una tablet sin runtime VR, la pantalla queda negra. `TabletBuild.cs` lo apaga solo
  durante el build y lo restaura siempre. Solo se menciona acá: el detalle vive en `docs/builds-deploy.md`.
- **Emparejamiento persistente por token, reemplaza el cache de PIN en memoria** → hasta esta
  tarea `_pinByHost` (`Dictionary<string,string>`) vivía solo mientras corría la app (a propósito:
  el PIN era el secreto de la sesión del visor). Cada reinicio de visor O tablet obligaba a
  retipear el PIN — molesto en la práctica clínica. Ahora `TabletSession._tokenByHost` persiste en
  `persistentDataPath/pairing.json` (mapa host→token, `PairingStore.SerializePairingMap`/
  `TryParsePairingMap`): un PIN correcto devuelve un token de ~256 bits
  (`PairingStore.GenerateToken`) que la tablet guarda y reusa en TODA reconexión futura (manual o
  automática), sobreviviendo a reinicios de la app y del visor. Solo se vuelve al `PinScreen`
  cuando no hay token guardado para ese host, o cuando el visor responde `auth_fail` con
  `reason:"token"` (token inválido/revocado — se borra la entrada local y cae al flujo de PIN).
  Protocolo completo, formato de los mensajes y modelo de amenaza en `docs/networking.md`. Gotcha
  nuevo: la clave del mapa es la IP del host, que puede cambiar por DHCP -- degradación aceptable
  (pide el PIN una vez más para el nuevo host, y ahí queda cacheado de nuevo).
- **Reconexión automática solo a la última sesión, con backoff acotado (P2.5)** → una caída NO
  manual (`_manualDisconnect == false`) de una sesión que estaba `_sessionActive` dispara
  `StartReconnectLoop()`: reintenta a `_currentHost` con el token de `_tokenByHost` (si no hay
  token cacheado, degrada al flujo manual — no debería pasar si hubo sesión activa, ya que llegar
  a `_sessionActive` implica que hubo un `auth_ok` con token, pero es defensivo).
  Backoff exponencial `DelayForAttempt(N) = min(2 * 2^(N-1), 15)` segundos → 2, 4, 8, 15, 15, ...
  indefinido hasta que el usuario cancela o el visor corta el loop (`auth_fail` → hace falta el
  PIN, no tiene sentido reintentar solo). `auth_locked` no puede ocurrir durante una reconexión
  por token (ver `docs/networking.md`: el lockout de PIN no alcanza al flujo de token) -- solo
  puede pasar si el primer enlace de esta MISMA conexión fue por PIN, en cuyo caso se espera el
  `retry_in_s` que manda el visor y se sigue reintentando. El timer solo cuenta cuando NO hay un
  intento en vuelo (`!_connecting`).
- **Botón "Desvincular" (emparejamiento persistente por token)** → discreto, al lado de
  Desconectar en el header (no es un botón del flujo clínico habitual). Manda `{"cmd":"unpair"}`
  (comando autenticado, el visor revoca el token de ESTA tablet), borra la entrada de
  `_tokenByHost`/`pairing.json` para el host actual y cierra la sesión localmente sin esperar
  respuesta del visor (`TabletSession.Unpair`, ver `docs/networking.md` Gotchas sobre el orden de
  escritura del socket) — vuelve al `ConnectScreen` con "Desvinculado..."; la próxima conexión a
  ese visor pide el PIN de nuevo. Reset total del lado visor (todos los emparejamientos): borrar
  `paired_tokens.json` a mano, sin UI dedicada (ver Minimal footprint en `docs/networking.md`).
- **FPS del footer normalizado por pane, no por mensaje (P5.5)** → `StreamingCapture` manda, por
  tick (20 Hz), UN frame `'B'` fuera de blend o DOS frames `'L'`+`'R'` en blend (ver
  `docs/networking.md`); `OnBinary` cuenta cada mensaje recibido igual. Sin corrección, el footer
  en blend mostraba ~2× la tasa real. Fix mínimo: `UpdateFooter()` divide el conteo crudo entre 2
  cuando `blend_active` (no hace falta contar L/R por separado: siempre llegan pareados).
- **Lista de hosts descubiertos por diff, no por destruir-y-reconstruir (P5.6)** →
  `_discoveredButtons` (`Dictionary<string, TabletButton>`) seguía sin existir hasta esta tarea;
  `RefreshDiscovered()` ahora solo crea los hosts nuevos y destruye los que expiraron, dejando el
  resto de los botones intactos (antes: `Destroy` de TODOS los hijos + recrear TODOS cada segundo,
  parpadeo visible aunque la lista no cambiara).
- **Stream a pantalla completa, retiro de Comparar A/B (P6.8)** → pedido explícito: la card
  "Comparar A / B" (P5.1) nunca se usó en la práctica clínica, se retiró entera (campos
  `_abLensA`/`_abLensB`/`_abLabelA`/`_abLabelB`/`_abToggleBtn`, métodos `SetAbSlotA/B`,
  `RefreshAbUI`, `OnAbTogglePressed`, `CurrentEyeLensId`, `BuildAbCard` y su invocación en
  `BuildBody`) — código muerto exclusivo de esa card, no compartido con nada más del protocolo
  (`apply_lens` que reusaba sigue intacto, lo sigue usando `OnLensSelected` desde la lista de
  lentes). En su lugar se agregó un overlay de stream a pantalla completa (`FullscreenStream`,
  `BuildFullscreenStream`): entrada con el botón "Pantalla completa" (esquina superior derecha
  del `StreamPanel`, posicionado con `PinTopRight`/`LayoutElement.ignoreLayout` para salir del
  `HorizontalLayoutGroup` del panel sin tocar su estructura existente), salida con el botón
  "Cerrar" (misma técnica, overlay del `FullscreenStream`) o tocando el fondo (`UnityEngine.UI.
  Button` liso sobre una `Image` negra de borde a borde — no un `TabletButton`: es una capa de
  tap invisible, no un control visible con fill/borde/texto). El modo (1 imagen o 2 lado a lado)
  reusa el mismo criterio que el panel normal (`isBlend`/`leftId`/`rightId`, ya calculados en
  `RefreshVisionUI`): `RefreshFullscreenUI(isBlend, leftId, rightId)` se llama SIEMPRE desde
  `RefreshVisionUI` (esté o no abierto el overlay en ese momento), así el modo ya está al día
  apenas se abre y reacciona a un cambio de lente mientras está abierto, sin un evento/suscripción
  nueva. **Reuso de textura, sin decodificación duplicada:** `_fsStreamLeft`/`_fsStreamRight` son
  `RawImage` NUEVOS pero apuntan a las MISMAS `Texture2D` `_texLeft`/`_texRight` que ya actualiza
  `OnSessionFrame` con `ImageConversion.LoadImage` — ese método solo agrega 2 líneas por rama
  (`_fsStreamLeft.texture = _texLeft; _fsStreamLeft.color = Color.white;` y su par derecho) que
  reflejan la misma asignación que ya hacía para `_streamLeft`/`_streamRight`; no hay un segundo
  `LoadImage` del mismo JPG. El overlay se cierra solo si la sesión se interrumpe
  (`ShowConnectScreen`/`ShowPinScreen`/`ShowReconnectScreen` llaman `CloseFullscreenStream()` al
  entrar) para no dejar al clínico viendo un frame congelado sobre una pantalla de reconexión —
  `ShowMainScreen` (incluye el branch de `refresh`, P5.4) NO lo cierra a propósito, para no
  interrumpir una vista en curso. **Bug corregido: el overlay se veía todo negro** →
  `FullscreenRow` (el `_kit.Box` que contiene los panes) se creaba sin `Stretch()` ni tamaño
  explícito, y su padre `_fullscreenStream` no cuelga de ningún `LayoutGroup` que lo dimensione
  — gotcha real de `TabletUiKit.Box()`: el `LayoutGroup` que agrega controla a sus HIJOS, nunca
  dimensiona el propio `RectTransform` del Box. Resultado: `FullscreenRow` quedaba con el rect
  default de Unity (anchors (0,0), sizeDelta 100×100, esquina inferior-izquierda), así que solo
  se veía el fondo negro `FullscreenBg` (que sí tenía `Stretch()`). Fix: `Stretch(row)` explícito
  después del `_kit.Box(...)`, igual que ya se hacía para `FullscreenBg`. Cualquier `_kit.Box`
  usado como contenedor de pantalla completa (o cualquier rect que no cuelgue de un ancestro con
  `LayoutGroup`/`ContentSizeFitter`) necesita este mismo tratamiento explícito.
- **Bug corregido: panes chicos en el overlay fullscreen (`expandH` en `FsLeftPane`/
  `FsRightPane`)** → `BuildFullscreenStream` creaba esos dos `_kit.Box` con `expandH: true`. Con
  `childForceExpandHeight = true`, el `VerticalLayoutGroup` fuerza `flexible = max(flexible, 1)`
  en TODOS los hijos, ignorando el `flexH: 0` que ya tenía el label (pide 26 px fijos, ver
  `_kit.Size(_fsLeftLabel.rectTransform, minH: 26, prefH: 26, flexH: 0)`): el alto terminaba
  repartido ~50/50 entre label y `StreamWrap`, y el `AspectRatioFitter` 4:3 achicaba la imagen del
  stream a un tamaño chico e innecesario. El panel lateral normal (`LeftEyePane`/`RightEyePane`,
  `BuildBody`) ya usaba `expandH: false` y nunca tuvo este bug — mismo patrón aplicado acá. Fix:
  `expandH: false` en los dos `_kit.Box`. Verificado numéricamente en Play Mode (reflection sobre
  `TabletController`, forzando `blend_active: true` vía `_session.VisionState` + `ShowMainScreen`/
  `RefreshVisionUI`/`OpenFullscreenStream` + `LayoutRebuilder.ForceRebuildLayoutImmediate`): con
  un pane de 744 px de alto, el label queda en 26 px y el `StreamWrap` en 712 px (~96 % del pane),
  contra un ~50/50 antes del fix. Cualquier pane nuevo de este overlay que combine un label de
  alto fijo con un contenido flexible debe seguir usando `expandH: false` en el `_kit.Box`
  contenedor (el `flexH: 0`/`flexH: 1` de los HIJOS solo se respeta así).
- **`BtnStyle.Overlay` (`TabletUiKit.cs`) para botones dibujados ENCIMA del stream en vivo** →
  "Pantalla completa" (panel de stream) y "Cerrar" (overlay fullscreen) usaban `BtnStyle.Ghost`
  (fill transparente, `NormalFill = Clear`), pensado para botones sobre el fondo sólido de la app
  — sobre un frame de video en vivo, el botón podía lavarse por completo según el contenido del
  frame (bug reportado: invisible en ciertos frames). `BtnStyle.Overlay` es un estilo nuevo,
  DELIBERADAMENTE NO tematizado (fill/borde/texto fijos, ignora la paleta `p` del `Register`):
  mismo criterio que `LabelKind.StreamChip` (label de los panes de stream, también con color fijo
  `#F2F6FB` por la misma razón) y que `FullscreenBg` — un control sobre video se comporta como
  parte del "lightbox", no de la chrome de la app, así que no tiene sentido que cambie con el tema
  claro/oscuro. Fill negro semi-opaco (72–90 % alfa según estado) + borde blanco de baja opacidad +
  texto igual al de `StreamChip`: garantiza contraste legible sobre CUALQUIER frame, oscuro o
  claro. Sigue registrado vía `Register(p => StyleButton(...))` como cualquier botón (se re-pinta
  en cada `ApplyTheme`), solo que el resultado es idéntico en Dark y Light — no rompe el contrato
  de repaint, solo lo vuelve un no-op visual a propósito.
- **Presets de sesión: snapshot del `vision_state`, aplicar = comandos existentes (P5.2)** →
  guardar un preset clona (`JObject.DeepClone`) el `left`/`right` TAL COMO llegan del visor
  (`lens_id` + todos los params aplanados, incluidos overrides ya aplicados) más `_currentScenario`
  — no hace falta un modelo de datos propio ni tocar el protocolo: es literalmente lo que ya
  parsea `OnText` para pintar la UI. Aplicar reproduce el snapshot con la MISMA secuencia que
  usaría un clínico a mano: `apply_lens` (fija los defaults del catálogo para esa lente) seguido
  de `override_params` con el resto de las claves del snapshot ENCIMA (reproduce los overrides
  que tenía guardados). Persistencia 100% LOCAL de la tablet
  (`persistentDataPath/presets.json`, `JArray` de objetos `{name, scenario, left, right}`) — el
  visor nunca se entera de que existen presets, no es un concepto de su protocolo. Igual patrón
  de resiliencia que `DataManager.LoadLensOverrides`: archivo ausente o corrupto → arranca sin
  presets, sin loguear error.
- **`refresh` en caliente reusa el branch de `"hello"` (P5.4)** → el botón "Actualizar" del header
  manda `{"cmd":"refresh"}`; el visor responde con el mismo payload EXACTO de un `hello`
  (`BuildHello()` reusado del lado visor, ver `docs/networking.md`), así que `OnText` no necesita
  ningún parsing nuevo — el `else if (type == "hello")` que ya reconstruye
  catálogo/escenarios/vision_state (y que también corre tras una reconexión exitosa, P2.5) procesa
  la respuesta tal cual. Cero estado nuevo del lado tablet más allá del botón y su handler
  (`OnRefreshPressed`), que solo valida que el WS esté abierto antes de mandar el comando.
- **Usabilidad táctil: 3 fixes puntuales + lock a landscape (decisión de producto, sin rediseño)**
  → el operador reportó que el scroll de "Ajuste fino" se sentía mal al dedo. Diagnóstico: (1)
  `EventSystem.pixelDragThreshold` nunca se seteaba (default 10 px REALES, ~5dp en pantallas
  ~320dpi — más chico que el touch-slop nativo de Android ~8dp) → taps leídos como drag y
  viceversa; fix en `TabletController.BuildUI()`: `pixelDragThreshold = max(10, round(10 *
  Screen.dpi / 160))` (≈10dp; si `Screen.dpi` es 0 cae al default 10). (2) `ScrollColumn`
  (`TabletUiKit.cs`) usaba `movementType = Clamped` (se sentía "duro" al llegar a un extremo) y
  el `decelerationRate` default (0.135, frena seco al soltar); ahora `Elastic` (rebote nativo,
  `elasticity` default 0.1) + `decelerationRate = 0.25f` (deslizamiento más suave); `inertia`
  queda en su default `true`. `scrollSensitivity` (24) se deja tal cual — uGUI solo lo usa para
  la rueda del mouse en el Editor, NO afecta touch (fuente de confusión si se lo toca esperando
  un efecto en device). (3) Los `Slider` de "Ajuste fino"/Astigmatismo (`Slider` estándar de
  uGUI) solo implementan `IDragHandler`, no `IBeginDragHandler`/`IEndDragHandler` — si el gesto
  arrancaba sobre el track/handle, el `ScrollRect` ancestro nunca se entera aunque el dedo se
  mueva sobre todo en vertical. Fix: `ScrollFriendlySlider` (nuevo) agrega esas dos interfaces;
  en `OnBeginDrag` mira la dirección dominante de `eventData.delta` (vertical → reenvía
  begin/drag/end al `ScrollRect` cacheado por `GetComponentInParent` vía
  `ExecuteEvents.ExecuteHierarchy`, sin mover el valor; horizontal → comportamiento normal del
  Slider) y además pisa `OnInitializePotentialDrag` sin llamar a `base` (Slider fuerza
  `useDragThreshold = false` para responder al instante, pero eso da un delta casi nulo en el
  primer frame; dejando el default `true` el `pixelDragThreshold` ya ajustado acumula
  movimiento antes de disparar `OnBeginDrag`, con una dirección ya representativa).
  `TabletUiKit.Slider()` crea `ScrollFriendlySlider` en vez de `Slider` — nada más cambió
  (`ParamRowView`/los sliders de Astigmatismo siguen tipados `Slider`, la subclase es
  compatible). **Landscape lock (decisión de producto explícita, NO se rediseña la UI para
  portrait):** `TabletController.Start()` fija `Screen.autorotateToPortrait/
  autorotateToPortraitUpsideDown = false` y `autorotateToLandscapeLeft/Right = true` +
  `Screen.orientation = ScreenOrientation.AutoRotation` ANTES de `BuildUI()` — es runtime-only
  (no toca `ProjectSettings.asset`, compartido con el visor; `TabletController` solo corre en
  `Tablet.unity`), y permite landscape en ambos sentidos (el layout de dos columnas de
  `BuildMainScreen` no distingue landscape-left de landscape-right). **Reforzado a nivel manifest
  (P6.8)** → el lock runtime deja un flash breve en portrait al arrancar (la Activity nativa
  arranca en la orientación que decida el sistema ANTES de que `TabletController.Start()` corra
  y fije los flags de `Screen`). `Assets/Plugins/Android/AndroidManifest.xml` agrega
  `android:screenOrientation="sensorLandscape"` a la Activity `UnityPlayerGameActivity`
  (compartida con el visor, ver la tabla de arquitectura arriba y el gotcha del manifest en
  `docs/builds-deploy.md`) — Android fija la orientación ANTES de que la Activity termine de
  crearse, eliminando el flash. Inocuo para el visor: en VR el compositor de OpenXR/el runtime
  del Quest maneja la orientación por su cuenta y no lee `screenOrientation` del manifest (no
  hay Activity 2D visible que rotar). El runtime-only NO se retiró: sigue siendo necesario para
  permitir landscape-left Y landscape-right (el manifest con `sensorLandscape` ya cubre ambos
  sentidos, pero los flags de `Screen.autorotateTo*` siguen fijando explícitamente qué
  orientaciones acepta la app después de que arrancó, y no dependen de reconstruir el APK para
  cambiar).
- **Solo descubrimiento automático, sin conexión manual (decisión de producto)** → la
  `ConnectScreen` tenía un toggle "Conexión manual" colapsable con un `LineEdit` de IP + botón
  "Conectar" (`OnConnectPressed`) que también servía de fallback si el discovery UDP no
  encontraba al visor. Se decidió que nunca se va a usar en la práctica clínica: se eliminó el
  toggle, el `LineEdit`, el botón y `OnConnectPressed` (código muerto exclusivo de ese camino).
  `StartConnectFlow(host)` — el método que arranca el flujo de PIN/token una vez que hay un host
  — **no se tocó**: sigue siendo el punto de entrada único, ahora solo alcanzable tocando un
  botón de la lista de visores descubiertos (`RefreshDiscovered`). Si el discovery UDP falla en
  una red real (AP isolation, firewall), ya no hay forma de conectar tipeando la IP a mano — ver
  Gotchas.
- **Info de red Wi-Fi de la ConnectScreen: SSID real vía permiso runtime, degrada a IP local** →
  `RefreshNetworkInfo()` (llamado desde `ShowConnectScreen`, así se refresca cada vez que se
  vuelve a esa pantalla) intenta `TryGetWifiSsid()` primero y, si no resuelve, cae a
  `TryGetLocalIPv4()`.
  `WifiManager.getConnectionInfo().getSSID()` vía `AndroidJavaClass`/`AndroidJavaObject` (JNI, no
  reflection de .NET — no lo toca el stripping de IL2CPP) exige `ACCESS_WIFI_STATE` (manifest) +
  permiso de ubicación en runtime desde Android 9 — sin alguno de los dos tira `SecurityException`
  (capturada en el propio `try/catch` de `TryGetWifiSsid`) o devuelve `"<unknown ssid>"`.
  **`Assets/Plugins/Android/AndroidManifest.xml`** agrega `ACCESS_WIFI_STATE` +
  `ACCESS_FINE_LOCATION`. Es **compartido con el visor** (mismo target Android, un solo manifest
  custom para todo el proyecto): inocuo para el visor, que nunca llama `RequestUserPermission` ni
  `TryGetWifiSsid` (ese código vive en `TabletController`, namespace/clase que no corre en
  `Main.unity`). El manifest declara la Activity de Unity COMPLETA (`UnityPlayerGameActivity` +
  `exported`/`theme`/intent-filter/meta-data, calcada de la plantilla oficial de Unity 6000.5) —
  NO es un manifest de solo permisos: un manifest custom que agrega `<application>` sin declarar
  esa Activity completa rompe el merge en AMBOS builds (incidente real, ver el gotcha "Manifest
  custom incompleto rompe el merge del launcher" más abajo y el postmortem completo en
  `docs/builds-deploy.md`). El permiso de ubicación en runtime lo pide
  `TabletController.RequestLocationPermissionOnce()` (`UnityEngine.Android.Permission`/
  `PermissionCallbacks`, sin reflection — IL2CPP-safe) desde `ShowConnectScreen`, UNA sola vez por
  sesión de la app (`_locationPermissionRequested`, no se vuelve a pedir aunque se pase por
  `ShowConnectScreen` de nuevo tras desconectar/cancelar PIN/etc.): si ya estaba concedido no hace
  nada; si lo concede en el momento, el callback `PermissionGranted` llama `RefreshNetworkInfo()`
  para que el SSID aparezca sin que el clínico tenga que volver a tocar nada; si lo niega (o
  "no preguntar de nuevo"), no hay reintento — la tablet sigue con el fallback de IP local para
  el resto de la sesión (ver Gotchas). `TryGetWifiSsid()` ya recortaba las comillas que devuelve
  Android (`ssid.Trim('"')`) y trataba `<unknown ssid>`/vacío como null — eso no cambió, solo ahora
  puede efectivamente resolver en vez de fallar siempre. `TryGetLocalIPv4()` no depende de la SSID
  ni de este permiso: abre un socket UDP y hace `Connect("8.8.8.8", 65530)` — el truco estándar de
  "no manda paquetes, solo hace que el SO resuelva la interfaz/ruta de salida" — y lee
  `Socket.LocalEndPoint`; funciona sin Internet real y sin permisos extra. Si ninguno de los dos
  resuelve (sin red), el label muestra "Red: no disponible". Ambos helpers de SSID/permiso son
  `#if UNITY_ANDROID && !UNITY_EDITOR` / fallback Editor (en Editor `TryGetWifiSsid()` devuelve
  `null` directo y `RequestLocationPermissionOnce()` es un no-op, así que el label en Editor
  siempre muestra la IP de loopback/LAN de la máquina de desarrollo, nunca un SSID).
- **Lista de visores sin IP, nombre amigable desde el `device_label` del beacon** →
  el pedido de producto fue explícito: ni el label de red ni la lista de visores detectados deben
  mostrar una IP (antes el botón decía `"Visor Quest  ·  " + IP`). `DiscoveryListener` (Net/, ver
  `docs/networking.md`) ahora también parsea `device_label` del payload UDP (con
  `Newtonsoft.Json.Linq.JObject`, ya usado en el resto de `Net/`/`Tablet/` — no es una dependencia
  nueva) y lo pasa junto con la IP; la IP SIGUE siendo la clave de identidad
  (`TabletSession._seenHosts`/`_tokenByHost`), el label es puramente decorativo
  (`TabletSession._hostLabels`, paralelo a `_seenHosts`, mismo ciclo de vida/poda a los 6 s).
  `TabletController.FriendlyVisorName(rawLabel)` recorta el nonce de sesión de 8 hex que
  `NetworkController.GenerateBeaconLabel()` le agrega al nombre (formato `"<nombre>-<nonce8>"`) —
  sin eso el botón mostraría algo como "Quest-a1b2c3d4", ruidoso para un clínico. Sin label
  (payload viejo, JSON que no parseó) cae al genérico "Visor Quest". `NextFriendlyVisorName`
  desambigua si aparece más de un host con el mismo nombre base agregando `(2)`, `(3)`... —
  best-effort, no determinístico si los hosts entran y salen de la lista entre refrescos (ver
  Gotchas), pero cubre el caso real (1–2 visores en la LAN de un consultorio). La IP sigue
  disponible en `Debug.Log` (`RefreshDiscovered`: `"[Tablet] Visor detectado: <nombre> (<IP>)"`)
  para troubleshooting de red, nunca en un widget visible.
- **Popup del PIN en el tercio superior, no centrado** → el teclado nativo de Android al enfocar el
  `LineEdit` numérico de `PinScreen` cubría el popup centrado (`TouchScreenKeyboard.area` no es
  confiable en Android para medir su alto real y evitarlo dinámicamente — no se intentó). Fix
  pragmático: `BuildPinScreen` ancla `PinWrap` arriba-centro (`anchorMin/anchorMax/pivot = (0.5,
  1)`, antes `(0.5, 0.5)`) con `anchoredPosition = (0, -40)` en vez de depender del centro de la
  pantalla — el popup queda hacia el tercio superior (glifo + título + host + hint + `LineEdit` +
  estado + botones, con `ContentSizeFitter` fijando el alto real) y el teclado, que ocupa la mitad
  inferior, ya no lo tapa. `ConnectScreen`/`ReconnectScreen` no llevan `LineEdit` (sin teclado) y
  quedaron centrados sin cambios — es un ajuste puntual del wrap de `PinScreen`, no un cambio al
  contenedor común (`Stretch()` del `_pinScreen` raíz sigue ocupando toda la pantalla).
- **Toggle del HUD del visor (`set_hud`)** → botón "Ocultar HUD"/"Mostrar HUD" en el header
  (`TabletController.OnHudTogglePressed`), manda `{"cmd":"set_hud","visible":bool}` (ver
  `docs/networking.md`) para mostrar/ocultar el HUD de diagnóstico del visor
  (`Vision/HudController.cs`, sin tocarlo — el visor resuelve la referencia desde `Net/`).
  Fire-and-forget, como `set_astigmatism`/`load_scenario`: no hay ack ni campo en `vision_state`
  que confirme el resultado, así que `_hudVisible` es el estado optimista de ESTA tablet nomás.
  Arranca en `true` ("Ocultar HUD" visible) y se resetea a `true` en `OnSessionConnected` (nueva
  conexión, inicial o P2.5) para no arrastrar el toggle de una sesión anterior — ver Gotchas
  abajo para el mismatch conocido si otra tablet ocultó el HUD antes.

## Gotchas
- **El botón "Ocultar/Mostrar HUD" no refleja el estado real del HUD, solo el de ESTA tablet en
  ESTA sesión de red:** `_hudVisible` se resetea a `true` en cada conexión nueva
  (`OnSessionConnected`) sin preguntarle al visor su estado real (no hay campo `hud_visible` en
  `vision_state`/`hello`, ver `docs/networking.md` Pendientes). Si una tablet oculta el HUD y se
  desconecta, la próxima tablet que conecte (o la misma, tras reconectar) va a mostrar "Ocultar
  HUD" aunque el HUD siga oculto de la vez anterior — hay que tocar el botón para que el estado
  mostrado y el real vuelvan a coincidir. Aceptado (HUD de diagnóstico, no clínico); la vía natural
  para cerrarlo es agregar `hud_visible` al `vision_state` (mismo patrón que `blend_active`, P2.1).
- **El permiso de ubicación se pide UNA sola vez por sesión de la app, no por visita a la
  ConnectScreen:** `RequestLocationPermissionOnce()` usa `_locationPermissionRequested` (campo de
  instancia, se resetea al reiniciar la app) — si el clínico lo niega la primera vez, la tablet NO
  vuelve a mostrar el prompt del sistema en toda esa sesión, aunque se recorra
  Desconectar→ConnectScreen→PIN→MainScreen varias veces. Si a futuro se necesita reintentar
  (p.ej. un botón "Reintentar permiso" en Ajustes), hay que resetear ese flag explícitamente, no
  alcanza con volver a llamar `ShowConnectScreen`.
- **`AndroidManifest.xml` custom es compartido con el visor (un solo target Android), y DEBE
  declarar la Activity de Unity completa — CORREGIDO tras un incidente real** (postmortem completo
  en `docs/builds-deploy.md`, gotcha "Manifest custom incompleto rompe el merge del launcher"):
  una versión anterior de este manifest era MÍNIMA a propósito (solo `<uses-permission>`, sin
  `<application>`/`<activity>`), asumiendo que Unity fusionaba sin pisar nada. En la práctica, un
  manifest custom que agrega `<application>` sin declarar la Activity de Unity con su
  `intent-filter`/`theme`/`meta-data` rompe el merge: el APK resultante (visor Y tablet, mismo
  manifest) quedó con `UnityPlayerGameActivity` sin `exported`/`theme`/intent-filter — la app no
  aparecía en el launcher ni arrancaba por `adb shell monkey`. Fix: el manifest ahora declara la
  Activity COMPLETA (calcada de la plantilla oficial de Unity 6000.5) con `android:exported="true"`
  explícito (obligatorio desde targetSdkVersion 31+ para toda Activity con intent-filter). No
  requiere tocar `TabletBuild.cs` (no hay lógica de manifest ahí, solo el toggle del loader OpenXR).
  `ACCESS_WIFI_STATE`/`ACCESS_FINE_LOCATION` quedan declarados en el visor también, pero son
  inofensivos ahí: el visor nunca llama `Permission.RequestUserPermission` ni `TryGetWifiSsid` (ese
  código vive en `TabletController`, que no corre en `Main.unity`) — un permiso
  declarado-pero-nunca-solicitado no dispara ningún prompt.
- **La desambiguación de nombres duplicados (`NextFriendlyVisorName`) no es estable entre
  refrescos:** el sufijo `(2)`/`(3)` se asigna en el momento en que el botón NUEVO se crea, mirando
  qué nombres ya están en uso — si el host que tenía "Visor Quest" (sin sufijo) expira y vuelve a
  aparecer más tarde, puede recibir un sufijo distinto la segunda vez si en el medio se descubrió
  otro host con el mismo nombre base. Aceptable: es solo el texto de un botón de una lista
  transitoria (nunca se persiste ni se usa como clave), y el caso real (1–2 visores en la LAN de
  un consultorio) casi nunca dispara la rama de desambiguación.
- **Sin conexión manual: si el discovery UDP no encuentra al visor, no hay forma de conectar
  desde la tablet.** (ver Decisiones "Solo descubrimiento automático" para el porqué). La primera
  causa de "no se descubren" sigue siendo AP isolation/firewall en la red del consultorio (ver
  `docs/networking.md`/`il2cpp-networking-gotchas`) — sin el fallback de IP a mano, ese escenario
  ahora bloquea por completo a la tablet hasta resolver la red (no hay ticket de UX pendiente para
  esto, es la decisión explícita del pedido que quitó la conexión manual).
- **No hay prefabs de UI: sin Play no hay nada.** La escena `Tablet.unity` solo tiene `TabletApp`;
  jerarquía, EventSystem (con `InputSystemUIInputModule`) y Canvas (1280×800, ScaleWithScreenSize)
  se crean en `BuildUI()`. Cualquier cambio visual se hace en `TabletUiKit`/`TabletController`, no
  en el Editor — editar la escena no sirve.
- **Fuentes por convención de path:** `Resources.Load<TMP_FontAsset>("TabletFonts/Inter-Regular SDF")`.
  Renombrar/mover los assets de `Assets/Resources/TabletFonts/` rompe silencioso (labels sin fuente).
- **`NetworkController` detecta a `TabletController` para no levantar server — EL riesgo real del
  split P6.2, no el archivo/GUID:** su `Bootstrap` hace `FindFirstObjectByType<TabletController>()`
  y aborta si lo encuentra (`Main.unity` no tiene uno, así que el visor SÍ levanta su
  `WebSocketServer`/`DiscoveryBeacon`; `Tablet.unity` sí lo tiene, así que la tablet NO levanta
  server propio). Si se renombra la clase, se la mueve de archivo, o (como en P6.2) se le cambia
  el namespace SIN actualizar el `using` de `NetworkController.cs`, esta llamada deja de compilar
  o de encontrar el tipo correcto y la tablet pasaría a levantar un `WebSocketServer` propio (dos
  servers compitiendo por el `:9090` en la misma LAN si además hay un visor real). Cualquier
  cambio futuro a `TabletController`/`TabletSession` debe re-verificar esto en Play Mode en AMBAS
  escenas (no solo compile-gate): `Main.unity` debe seguir logueando `WebSocketServer: escuchando
  en :9090` + `DiscoveryBeacon: broadcasting...`; `Tablet.unity` NO debe loguear ninguno de los
  dos (solo `DiscoveryListener: escuchando :9091`, que es del lado cliente).
- **Escenarios matcheados por id (P2.3, CERRADO, ya no por label):** `_scenarioButtons`
  (`Dictionary<string, TabletButton>`) guarda el botón de cada id; `OnScenarioPressed` marca el
  activo comparando la CLAVE del diccionario, no el texto del label. `hello.scenarios` manda
  `{id,label}` por escenario (la LISTA de ids viene de `ScenarioManager.ScenarioOrder`, ya no hay
  duplicación) y `load_scenario` viaja con `{"cmd":"load_scenario","id":...}`. Detalle de
  protocolo en `docs/networking.md`.
- **La actualización optimista descarta params:** `OnLensSelected` reemplaza el estado del ojo por
  `new JObject { lens_id }`; hasta que llega el `vision_state` real, `CurrentParamValue` cae a los
  defaults del catálogo. Ventana corta, pero visible si el visor tarda en confirmar. Nota P2.1: esa
  actualización optimista tampoco toca `blend_active` — queda con el valor previo hasta el
  `vision_state` real (misma ventana corta).
- **(P6.9) Los sliders de "Ajuste fino" de los 3 focos ya no pueden llegar a "off" (0) una vez
  movidos** — `foco_cerca_m`/`foco_intermedio_m`/`foco_lejos_m` pasaron a rangos clínicos con
  `min > 0` (antes `min: 0`, ver `docs/catalogo-lentes.md` "Rangos clínicos de los 3 focos"). Una
  lente con un foco en 0 (p. ej. `monofocal.foco_cerca_m`) sigue mostrándose como "off" al abrir la
  card (`ParamRowView.Create` fija `minValue`/`maxValue` ANTES de conectar `onValueChanged`, así que
  el clamp inicial no manda ningún `override_params`), pero si el clínico arrastra ese slider ya no
  hay forma de volver a 0 desde la tablet — solo reaplicando la lente o "Restaurar valores". Detalle
  completo (por qué no manda comandos espurios, qué lentes quedaron con defaults ajustados) en
  `docs/catalogo-lentes.md`.
- **Los `Texture2D` del stream se recrean por `LoadImage`** sobre las mismas instancias `_texLeft`/
  `_texRight` (RGB24 2×2 inicial que se redimensiona solo). No cachear referencias a su tamaño.
- **`auth_fail`/`auth_locked` implican desconexión inminente:** el visor manda el mensaje y CIERRA
  esa conexión del lado servidor casi inmediatamente. `TabletSession` (P6.2: antes era
  `TabletController`) no cierra nada por su cuenta: solo marca `_authFailed`/`_authFailReason`/
  `_authLocked` (y en el caso de `auth_locked`, `_authLockRetrySeconds`) en su `OnText`; el
  `Disconnected` real llega poco después vía `OnWsDisconnected` (interno de `TabletSession`), que
  es quien dispara el evento `PinScreenRequested` con el mensaje correcto (chequea `_authLocked`
  ANTES de `_authFailed`, son mutuamente excluyentes para una misma conexión). Si se toca ese
  flujo, mantener el orden (flags primero en `OnText`, evento en el disconnect) o la UI puede
  terminar mostrando el `PinScreen` mientras el socket todavía figura "abierto".
- **`auth_locked` NO limpia el token cacheado; `auth_fail` lo limpia SOLO si `reason=="token"`:**
  `auth_locked` no toca `_tokenByHost` (solo puede pasar del lado de un intento por PIN, ver
  `docs/networking.md` -- el token que se venía usando, si había uno, sigue intacto).
  `auth_fail` con `reason=="pin"` (default) tampoco toca `_tokenByHost` (fue un PIN puntual mal
  tipeado, no invalida un token existente de otra conexión); solo `reason=="token"` borra la
  entrada de `_tokenByHost[host]` porque ESE token específico ya se sabe inválido/revocado. Si se
  agrega lógica nueva alrededor de `_tokenByHost`, no asumir que todo fallo de auth implica
  "borrar el token guardado" — depende del `reason`.
- **Reintentar con PIN incorrecto o durante lockout exige reconectar:** el servidor no deja la
  conexión abierta para un segundo intento sobre el mismo socket (tanto `auth_fail` como
  `auth_locked` cierran esa conexión). El `PinScreen` → "Conectar" siempre pasa por
  `BeginConnect` → `_ws.Connect(...)` de nuevo, aunque el host sea el mismo.
- **`TabletSession.CancelReconnect()` (P6.2, ex `TabletController.OnReconnectCancelPressed`) no
  siempre puede cerrar el socket, y NO debe marcar `_manualDisconnect` cuando no hay nada que
  cerrar (P2.5, corregido en revisión):** durante el backoff (`_reconnecting && !_connecting`, sin
  thread de conexión en vuelo) `WebSocketClient.Close()` no dispara `Disconnected` (no hay nada
  que cerrar) — si en ese caso igual se seteaba `_manualDisconnect = true`, el flag quedaba en
  `true` sin ningún evento de socket que lo consumiera/reseteara, y la PRÓXIMA caída no manual de
  una sesión nueva se mostraba como "Sesión finalizada" en vez de disparar `StartReconnectLoop`
  (bug real, encontrado en revisión). Fix: `_manualDisconnect` solo se setea DENTRO del
  `if (_connecting)` (el único caso con un `Disconnected` real en camino); en la rama sin conexión
  en vuelo `CancelReconnect()` devuelve `false` y es la UI (`TabletController.
  OnReconnectCancelPressed`) quien llama `ShowConnectScreen` directo, sin que `TabletSession` toque
  el flag. `Connect()` (ex `BeginConnect`) además lo resetea a `false` como red de seguridad
  adicional (por si quedó en `true` por cualquier otro camino). Re-verificado por reflection
  DESPUÉS del split P6.2 (ver Cómo probar): comportamiento idéntico.
- **El countdown se NEUTRALIZA (no solo se deja de tickear) al iniciar un intento — bug real
  corregido en revisión, preservado tal cual en `TabletSession` tras el split P6.2:** la primera
  versión de `DoReconnectAttempt()` ponía `_connecting = true` y confiaba en el guard
  `_reconnecting && !_connecting` de `Update()` para no reprogramar un segundo intento mientras el
  primero seguía en curso. Pero `OnWsConnected` pone `_connecting = false` en cuanto el TCP
  conecta — ANTES de que llegue `auth_ok`/`hello` — así que en el MISMO frame el guard volvía a
  pasar, y como `_reconnectCountdown` nunca se había reseteado (seguía en `≤0` desde que disparó
  el intento actual), `Update()` llamaba `DoReconnectAttempt()` de nuevo, que hace
  `_ws.Connect(...)` → `Close()` del socket recién conectado, ANTES de que llegara el hello.
  Livelock: cada intento se autoderribaba y la tablet nunca terminaba de reconectar. Fix:
  `DoReconnectAttempt()` pone `_reconnectCountdown = float.PositiveInfinity` al disparar (no solo
  `_connecting = true`); el guard de `Update()` sigue pasando cuando `_connecting` vuelve a
  `false`, pero `_reconnectCountdown <= 0f` ya no es cierto, así que no reprograma nada. Solo
  `ScheduleNextReconnectAttempt()` (fallo real de conexión) o la rama `auth_locked` de
  `OnWsDisconnected` (espera explícita del `retry_in_s`) vuelven a poner un valor finito.
  Verificado por reflection en Play Mode ANTES y DESPUÉS del split P6.2 (accediendo al campo
  privado `_session` de `TabletController` y de ahí a los campos de `TabletSession`): tras simular
  `DoReconnectAttempt()` + `OnWsConnected()` en secuencia, `_reconnectAttempt` se mantuvo en 1 a lo
  largo de varios frames reales en ambas corridas (antes del fix original habría escalado sin límite).
- **`_reconnecting` se apaga en tres lugares, no solo al reconectar con éxito:** `auth_fail` (PIN
  ya no sirve), Cancelar, y `hello` (éxito). Si se agrega un cuarto camino de salida del loop,
  acordarse de apagar el flag ahí también o el timer de `TabletSession.Update()` sigue vivo
  compitiendo con la pantalla nueva.
- **Los presets NO revalidan contra el catálogo actual (P5.2):** si se borra una lente del
  catálogo (edición manual de `lentes.json`/backend) y se aplica un preset viejo que la
  referenciaba, `apply_lens` del visor solo loguea warning y no cambia el estado de ese ojo — el
  preset "falla en silencio" para esa lente puntual (el resto de los comandos del preset sí se
  mandan). No hay validación cliente-side de que los ids de un preset sigan existiendo.
- **`ScrollFriendlySlider` decide la dirección UNA sola vez por gesto (en `OnBeginDrag`) y no
  vuelve a evaluarla:** si el operador empieza arrastrando horizontal (mueve el slider) y a
  mitad de gesto curva el dedo hacia vertical sin soltar, sigue moviendo el valor (no se pasa a
  scrollear a mitad de camino) — comportamiento esperado de cualquier gesto de drag, no un bug.
  Tampoco toca `OnPointerDown` (el tap-to-jump del `Slider` base al tocar el track sigue
  intacto): el fix es solo sobre el DRAG, no sobre el toque inicial.
- **El lock a landscape ya no depende solo del runtime (P6.8, CERRADO el flash de arranque)** →
  antes de esta tarea, `Screen.orientation = ScreenOrientation.AutoRotation` + las 4 flags
  `autorotateTo*` en `TabletController.Start()` eran la ÚNICA barrera, y dejaban un flash breve
  en portrait al arrancar (la Activity nativa arranca en la orientación que decida el sistema
  ANTES de que `Start()` corra). `android:screenOrientation="sensorLandscape"` en
  `AndroidManifest.xml` (ver Decisiones) cierra ese hueco a nivel Android — la Activity nunca se
  presenta en portrait, ni siquiera por un frame. Lo que SIGUE dependiendo del runtime: la
  rotación del SISTEMA Android bloqueada en portrait puede demorar cuánto tarda la app en
  rotar entre landscape-left y landscape-right (el sensor debe estar habilitado), pero ya no
  puede hacer que arranque en portrait. `ProjectSettings.asset` (compartido con el visor) sigue
  con las 4 orientaciones habilitadas a propósito — no se tocó, es contrato compartido; el nuevo
  lock vive en el manifest custom, no en Player Settings.
- **`presets.json` no tiene versión/migración:** a diferencia de `lentes.json` (`version` +
  `MergeMissingParams`), el archivo de presets es un snapshot crudo del `vision_state` de cuando
  se guardó. Si el shape de `vision_state` cambia a futuro (nuevo campo obligatorio, etc.) un
  preset viejo puede aplicar parcialmente. Aceptable hoy: son datos locales de un solo clínico,
  no un contrato compartido.

## Cómo probar
0. **Regresión del split P6.2 (la que realmente importa, ver Gotchas):** Play en
   `Assets/Scenes/Main.unity` SOLO (sin tablet) → la consola debe mostrar `Net: PIN de
   emparejamiento...`, `WebSocketServer: escuchando en :9090` y `DiscoveryBeacon: broadcasting...`
   (el visor sigue levantando su server). Play en `Assets/Scenes/Tablet.unity` SOLO → la consola
   debe mostrar `DiscoveryListener: escuchando :9091` y NADA de `WebSocketServer`/`DiscoveryBeacon`
   (la tablet sigue sin levantar server propio). Si `Tablet.unity` no muestra ningún error de
   "Missing Script" en el Inspector del GameObject `TabletApp`, el cambio de namespace de
   `TabletController` no rompió la referencia de la escena.
1. Con el visor corriendo (Play en `Assets/Scenes/Main.unity` o build Quest), abrir
   `Assets/Scenes/Tablet.unity` y dar Play: debe aparecer la pantalla de conexión con el visor
   detectado en pocos segundos (mismo host en Editor, vía loopback UDP), con un nombre amigable
   en el botón (p.ej. "Visor Quest" o el `SystemInfo.deviceName` del visor, SIN la IP — ver
   Decisiones "Lista de visores sin IP"). La `ConnectScreen` ya no tiene conexión manual — si el
   visor no aparece en la lista, no hay forma de conectar (ver Gotchas "Sin conexión manual").
   En Editor esto no prueba SSID/permisos (ver 1b) ni la desambiguación de nombres (ver 9).
1b. **ConnectScreen: info de red + botón Salir.** Debajo del estado de búsqueda debe verse un
    label "Red: ...". En Editor siempre es la IP LAN/loopback de la máquina (SSID no aplica fuera
    de Android). **En un device Android real (nuevo, requiere build):** al primer Play/apertura de
    la app debe aparecer el prompt nativo de permiso de ubicación UNA sola vez (ver Decisiones
    "Info de red Wi-Fi" y Gotchas) — conceder el permiso: el label debe pasar a mostrar el SSID
    real de la red (sin comillas) apenas se concede, sin tocar nada más; negarlo: el label debe
    mostrar la IP local igual que antes, y el prompt NO debe volver a aparecer al desconectar/
    reconectar/volver a esta pantalla en la misma sesión de la app (solo reaparece si se mata y
    reabre la app). El texto fijo "El visor Quest y la tablet deben estar conectados a la misma
    red Wi-Fi." debe seguir apareciendo igual. Tocar "Salir" → la app debe cerrarse
    (`Application.Quit()`; en el Editor esto NO sale de Play Mode, es el comportamiento estándar
    de Unity — probar el cierre real en un build de device).
2. Tocar el visor detectado: debe aparecer el `PinScreen` pidiendo el PIN
   de 6 dígitos (lo loguea la consola del visor: `Net: PIN de emparejamiento de esta sesion: ...`;
   en el visor real lo muestra el HUD). Ingresarlo mal a propósito una vez → "PIN incorrecto. Volvé
   a intentarlo." y vuelve a pedirlo; ingresarlo bien → debe llegar el `auth_ok` con un token nuevo
   (persistido en `pairing.json` de la tablet y en `paired_tokens.json` del visor, ver
   `docs/networking.md`) seguido del `hello`, pasar a la pantalla principal con badge verde
   `Conectado · IP`, las cards del catálogo y el stream en movimiento (footer con fps/MB
   creciendo). Probar también un PIN con ceros a la izquierda (p.ej. `000123`, si el que generó el
   visor tiene esa forma) para confirmar que el `LineEdit` numérico no los recorta ni el envío los
   trunca (el PIN es un string de 6 caracteres, no un número).
2b. **Lockout:** repetir el PIN incorrecto 3 veces (reconectando cada vez) → al cuarto intento la
   tablet debe mostrar "Demasiados intentos. Esperá Ns y volvé a intentarlo." (no "PIN
   incorrecto") aunque esta vez se ingrese el PIN correcto. Esperar los Ns indicados y reintentar
   con el PIN correcto → debe autenticar normal.
3. Tocar una lente con "Ambos" seleccionado → chips OD y OI encendidos en la card y editor de
   "Ajuste fino" con las filas de `ParamMeta` en orden clínico; mover un slider debe verse reflejado
   en el stream. "Restaurar valores" vuelve a defaults y manda un solo `override_params`.
4. Elegir "OD · Derecho", aplicar otra lente → el stream debe partirse en dos panes apilados, **OD
   arriba / OI abajo** (`OD ·`/`OI ·`) y cada card mostrar su chip.
5. Togglear tema claro/oscuro (debe repintar todo en caliente y persistir tras reiniciar) y cambiar
   de escenario desde el header.
6. Probar desconexión manual: botón Desconectar → "Sesión finalizada." (vuelve al `ConnectScreen`
   directo, NO dispara reconexión automática — es el camino `_manualDisconnect`). Volver a tocar
   el mismo visor → debe conectar directo SIN pedir el PIN (usa el token persistido).
6b. **Reconexión automática (P2.5, 2 dispositivos):** con la tablet conectada y activa, matar/pausar
   el visor (o cortar su Wi-Fi) sin usar el botón Desconectar de la tablet → debe aparecer el
   `ReconnectScreen` ("Se perdió la conexión con el visor." → "Reconectando… (intento N)") con
   cuenta atrás creciente (2 s, 4 s, 8 s, tope 15 s). Reactivar el visor (sin reiniciarlo) antes de
   que el clínico cancele → debe reconectar solo (por token, sin pedir PIN) y volver al
   `MainScreen`. Probar también "Cancelar" durante la cuenta atrás → debe volver al `ConnectScreen`
   normal (discovery) sin más reintentos.
6c. **Reconexión + visor REINICIADO (emparejamiento persistente por token):** repetir el corte,
   pero esta vez REINICIAR el visor (PIN de sesión nuevo, HUD lo muestra distinto) antes de que la
   tablet reconecte → a diferencia del comportamiento previo a esta tarea, el intento automático
   debe reconectar solo POR TOKEN (el token persiste en `paired_tokens.json` del visor pese al
   reinicio) y volver al `MainScreen` SIN mostrar el `PinScreen`. Confirmar en consola del visor
   `Net: cliente N autenticado por token, enviando hello.`.
6d. **Token invalidado a mano:** con la tablet ya emparejada, cerrar el visor, borrar
   `paired_tokens.json` de su `persistentDataPath` y volver a abrirlo → el siguiente intento de
   conexión (manual o automático) de la tablet debe recibir `auth_fail` con `reason:"token"`,
   mostrar el `PinScreen` con "El emparejamiento con este visor ya no es válido. Ingresá el PIN
   nuevamente." y la tablet debe haber olvidado ese host de su `pairing.json` (verificar
   reingresando el PIN correcto: debe volver a emparejar y guardar un token nuevo).
6e. **Desvincular:** con la tablet conectada, tocar "Desvincular" en el header → debe volver al
   `ConnectScreen` con "Desvinculado. Ingresá el PIN si querés volver a conectarte."; la consola
   del visor debe loguear `Net: cliente N se desvinculo (token revocado).`. Tocar el mismo visor
   de nuevo → debe pedir el `PinScreen` (ya no hay token ni del lado tablet ni del lado visor).
7. Para device real: **Simulador → Build Tablet (Android)** genera `Builds/Android/Simulador.apk`
   (ver `docs/builds-deploy.md`). Verificar en la tablet real que el `LineEdit` del PIN abre teclado
   numérico (no el alfabético completo).
8. **FPS por pane (P5.5):** aplicar lentes distintas por ojo (blend) y comparar el fps del footer
   contra el modo sin blend (misma lente ambos ojos) — con la misma tasa real de envío (20 Hz en
   `StreamingCapture`), el footer debe mostrar un número similar en ambos casos (antes, blend
   mostraba ~2×).
9. **Lista de hosts sin parpadeo (P5.6):** con 1 visor detectado, observar la lista del
   `ConnectScreen` durante varios segundos — el botón no debe destruirse/recrearse visualmente
   (sin parpadeo). Apagar el visor y esperar >6 s → el botón debe desaparecer; volver a encenderlo
   → debe reaparecer.
9b. **Nombre amigable sin IP, con desambiguación (nuevo).** Revisar la consola: cada host nuevo
   debe loguear `[Tablet] Visor detectado: <nombre> (<IP>)` (la IP solo ahí, nunca en el botón).
   Con un único visor en la red, el botón debe decir el nombre base (p.ej. "Visor Quest") sin
   sufijo. Si se puede tener 2 visores/instancias de Editor emitiendo el mismo
   `SystemInfo.deviceName` en la misma red simultáneamente, el segundo botón debe aparecer como
   "<nombre> (2)" — confirmar que apretar CADA botón conecta al visor correcto (la IP sigue
   siendo la clave interna, solo el texto cambia).
10. **Astigmatismo residual persistente (P4.4):** aplicar cualquier lente → "Ajuste fino" debe
    mostrar las filas "Astigmatismo residual" (0–1) y "Eje del astigmatismo" (0–180°) junto a
    "Desenfoque máximo" (antes de los halos), ambas en 0 por default. Subir "Astigmatismo
    residual" → debe viajar por `override_params` y persistir en `lens_overrides.json` (igual que
    cualquier otro param; ver §Overrides en `docs/catalogo-lentes.md`). Abrir la card
    "Astigmatismo" (live) y confirmar que el hint nuevo aclara la precedencia ("ajuste temporal…
    para persistir usá Ajuste fino"); activar el switch LIVE y verificar que el efecto en el
    stream se ve igual sin importar cuál de los dos controles lo generó (mismo pipeline
    `glare_astig_l/r`) — no deben aparecer sincronizados entre sí (es esperado, ver Decisiones).
11. **Stream a pantalla completa, modo simple (P6.8):** con la misma lente en ambos ojos (o sin
    lente en ninguno), tocar "Pantalla completa" (esquina superior derecha del panel de stream)
    → debe verse el overlay con UNA imagen ocupando la pantalla, etiqueta "Ambos ojos — \<lente\>"
    (o "Ambos ojos" sin lente), sin el resto de la UI visible. Tocar "Cerrar" → vuelve al
    `MainScreen` normal. Repetir abriendo y tocando esta vez en cualquier punto del fondo (no el
    botón) → también debe cerrar.
11b. **Stream a pantalla completa, modo blend:** aplicar lentes distintas por ojo (blend activo,
    el panel normal ya muestra 2 panes) y abrir "Pantalla completa" → debe verse 2 paneles lado a
    lado (no apilados), **OD a la izquierda / OI a la derecha**, con etiquetas "OD — \<lente\>" /
    "OI — \<lente\>". Con el overlay ABIERTO,
    aplicar una lente distinta a un ojo desde la lista (sin cerrar el overlay) → las etiquetas y
    el contenido del pane correspondiente deben actualizarse solos (reacciona a `vision_state`
    sin necesidad de reabrir el overlay). Aplicar la MISMA lente a ambos ojos con el overlay
    abierto → debe pasar de 2 paneles a 1 imagen en el momento.
11c. **Sesión interrumpida con el overlay abierto:** con el stream a pantalla completa abierto,
    tocar "Desconectar" (o forzar una caída del visor) → el overlay debe cerrarse solo y mostrar
    el `ConnectScreen`/`ReconnectScreen` correspondiente (no debe quedar un frame congelado tapando
    la pantalla).
12. **Presets de sesión (P5.2):** armar un estado (lente + algún override + escenario), abrir
    "Presets", escribir un nombre y "Guardar" → debe aparecer en la lista. Cambiar todo (otra
    lente, otro escenario) y tocar "Aplicar" en el preset guardado → debe volver exactamente al
    estado guardado (lente + valor del override + escenario). Guardar CON EL MISMO NOMBRE otra
    vez → debe sobreescribir (no duplicar) la entrada. "Borrar" → desaparece de la lista. Cerrar y
    reabrir la app (Stop/Play en Editor, o matar/reabrir en device) → los presets guardados deben
    seguir ahí (persisten en `persistentDataPath/presets.json`, sobreviven a la sesión de WS).
13. **Refresh en caliente (P5.4):** con la tablet conectada, tocar "Actualizar" en el header → no
    debe pedir el PIN de nuevo ni pasar por ninguna pantalla intermedia (sigue en `MainScreen`
    todo el tiempo); la lista de lentes/escenarios se repuebla en el momento. Ver
    `docs/networking.md` para el test cruzado con un segundo cliente WS.
14. **Usabilidad táctil (device real, el Editor no reproduce touch-slop ni DPI real):** en
    "Ajuste fino" con varias filas (más contenido que el alto de la columna), arrastrar el dedo
    VERTICALMENTE empezando encima de un slider → la columna debe scrollear (el valor del
    slider NO debe cambiar). Arrastrar HORIZONTALMENTE sobre el mismo slider → debe mover el
    valor normal (sin scrollear la columna). Tocar (tap corto, sin arrastrar) un slider o un
    `TabletButton`/card → debe registrar el tap limpio, sin que se sienta como un micro-drag.
    Soltar el dedo tras un arrastre rápido sobre la columna → debe deslizar con inercia y
    frenar gradual (no seco) y, si se llega a un extremo, rebotar levemente (Elastic). Rotar el
    device 180° en landscape (de landscape-left a landscape-right) → la UI debe re-rotar sola;
    intentar poner el device en portrait → la UI NO debe rotar a portrait (queda en el último
    landscape válido).
15. **Sin flash en portrait al arrancar (P6.8, requiere build — el Editor no reproduce el
    arranque nativo de la Activity):** con el device en mano en cualquier orientación, instalar y
    abrir la app (o `adb shell monkey -p com.simulador.tablet 1` recién instalada) → la pantalla
    debe aparecer directamente en landscape, sin un frame/flash visible en portrait antes de
    estabilizarse (antes de esta tarea, el lock era runtime-only y el flash era perceptible en
    algunos devices). Confirmar también que el visor sigue arrancando normal en VR (el
    `screenOrientation` del manifest es inocuo ahí, ver Decisiones "Landscape lock").
16. **PinScreen no tapado por el teclado (device Android real, el Editor no despliega teclado
    nativo):** en el `ConnectScreen`, tocar un visor detectado para abrir el `PinScreen` y tocar el
    `LineEdit` del PIN → el teclado numérico debe desplegarse ocupando la mitad inferior de la
    pantalla y el popup completo (glifo, título, host, hint, `LineEdit`, estado, botones
    Cancelar/Conectar) debe quedar visible en el tercio superior, sin que el teclado tape ninguna
    parte. Escribir el PIN completo y tocar "Conectar" (o Enter) sin tener que cerrar el teclado a
    mano para ver qué se tipeó.
17. **Toggle de HUD del visor (2 dispositivos, `set_hud`):** ver el paso 12 de
    `docs/networking.md` — con la tablet conectada, tocar "Ocultar HUD"/"Mostrar HUD" en el header
    y confirmar en el visor (HMD o Editor) que el HUD de diagnóstico aparece/desaparece al
    instante, y que el texto del botón sigue el estado local. Desconectar y reconectar con el HUD
    oculto → el botón debe resetear a "Ocultar HUD" (aunque el HUD real siga oculto, ver Gotchas).

## Pendientes / deuda
- El lockout es global del lado del visor (no por tablet/IP): si otro cliente en la LAN agotó el
  tope, esta tablet también ve `auth_locked` aunque nunca haya fallado (ver `docs/networking.md`
  § Gotchas/Modelo de amenaza). El `PinScreen` ya muestra el `retry_in_s`, pero no distingue "fallé
  yo" de "falló otro cliente". La reconexión automática (P2.5) hereda esto: si el lockout es de
  OTRO cliente, esta tablet igual espera el `retry_in_s` en el `ReconnectScreen` antes de reintentar.
- **Dos controles de astigmatismo sin unificar (P4.4, deliberado por ahora):** la card
  "Astigmatismo" (live, no persiste) y las filas nuevas de "Ajuste fino" (persistentes,
  `astig_magnitude`/`astig_axis_deg`) apuntan al mismo efecto pero no se sincronizan entre sí ni
  comparten UI — se agregó solo un hint de precedencia en la card live (ver Decisiones). Evaluar
  en una tarea futura si conviene fusionarlos o si la separación live/persistente es intencional
  a largo plazo (p.ej. la card live podría ser para "probar rápido" sin comprometerse a guardarlo).
- **Presets (P5.2) no revalidan ids ni versionan el archivo** — ver el detalle en Gotchas. No
  rompe nada hoy (degrada a warning/estado parcial), pero es un borde a endurecer si los presets
  compartidos entre clínicos se vuelven un caso de uso real.
- **`refresh` (P5.4) no tiene indicador visual de "en curso" ni feedback de error**: `OnRefreshPressed`
  solo valida que el WS esté abierto (badge "Sin conexión" si no) pero no muestra nada mientras
  espera la respuesta ni si el visor tardara en contestar; como reusa el flujo de `"hello"`, el
  único indicio de éxito es que la lista de lentes/escenarios se repuebla. Aceptable para un botón
  de uso ocasional, pero si se vuelve frecuente convendría un estado de carga.
- **`TabletSession` sin tests unitarios de la máquina de reconexión/protocolo (P6.2):** es plain
  C# (sin corrutinas ni `UnityWebRequest`, a diferencia de `DataManager`), así que en teoría es más
  testeable que antes del split — pero sigue dependiendo de `WebSocketClient`/`DiscoveryListener`
  reales (no hay interfaz inyectable) y de eventos de `Newtonsoft.Json.Linq`, así que testear la
  máquina de reconexión hoy exige reflection + Play Mode real (ver Cómo probar), igual que se hizo
  para verificar el split. La ÚNICA porción que sí se extrajo a lógica pura testeable es el
  emparejamiento por token (generación + serialización, `PairingStore.cs`/`PairingStoreTests.cs`,
  ver `docs/networking.md`) — el resto (`DelayForAttempt`, parsing de `OnText`/`OnBinary`) sigue
  sin extraer; si se necesitara cobertura EditMode de verdad, el camino natural es el mismo patrón
  (`DataManagerLogic.cs`, ver `docs/catalogo-lentes.md`).
