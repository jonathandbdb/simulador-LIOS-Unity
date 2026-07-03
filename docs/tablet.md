# App tablet (control de consultorio)

## Qué es y por qué
App Android plana (sin VR) que corre en la tablet del oftalmólogo: descubre el visor Quest en la
red, se conecta por WebSocket, muestra en vivo lo que ve el paciente (stream por ojo) y permite
aplicar lentes intraoculares, ajustar parámetros clínicos, simular astigmatismo, cambiar de
escenario, comparar dos lentes A/B, guardar/cargar presets de sesión y refrescar el catálogo en
caliente (P5). Es la réplica fiel de `features/tablet/streaming_client.gd` del proyecto Godot
original, con extensiones de flujo clínico propias del simulador.

## Arquitectura actual

**P6.2 (split god-object):** hasta esta tarea `TabletController` era una sola clase con TODO
(red + protocolo + estado de sesión + construcción de UI, >1400 líneas). Se partió en dos capas
— ver Decisiones "Split sesión/UI" para el porqué y el mapeo detallado de responsabilidades:

| Archivo | Rol |
|---|---|
| `Assets/Scripts/Runtime/Tablet/TabletSession.cs` (nuevo, P6.2) | **Capa de sesión/protocolo.** Plain C# (NO MonoBehaviour). Posee `WebSocketClient` + `DiscoveryListener`, el flujo de conexión/emparejamiento por PIN, la máquina de reconexión automática (P2.5) y el estado de sesión (`vision_state`, catálogo, escenarios, cache de PIN por host, hosts descubiertos). Expone eventos tipados (`Connected`, `AuthOk`, `PinScreenRequested`, `ShowConnectScreenRequested`, `ReconnectStarted`, `ReconnectStatusChanged`, `HelloReceived`, `VisionStateChanged`, `FrameReceived`) y propiedades read-only (`IsConnecting`, `IsSessionActive`, `IsReconnecting`, `IsWsOpen`, `CurrentHost`, `DiscoveredHosts`, `LensesById`, `VisionState`, `Scenarios`, `ScenarioLabels`, `CurrentScenario`). Namespace `Simulador.Tablet`. |
| `Assets/Scripts/Runtime/Net/TabletController.cs` | **Capa de UI.** MonoBehaviour único de la app (sigue en `Net/` con ese nombre — la escena `Tablet.unity` lo referencia por GUID del `.cs`, ver Gotchas — pero cambió de namespace `Simulador.Net` → `Simulador.Tablet`, P6.2). Construye toda la interfaz en `Start()`, crea y drena la `TabletSession` en su `Update()` (`session.Update(Time.deltaTime)`), traduce eventos de sesión → widgets (`OnSession*` handlers) y clicks → métodos de la sesión (`_session.Connect/Disconnect/SendCommand/CancelReconnect/...`). |
| `Assets/Scripts/Runtime/Tablet/TabletUiKit.cs` | Fábrica de widgets uGUI temables: `Label`, `Button`, `Panel`/`Card`, `Slider`, `LineEdit` (TMP_InputField), `CheckToggle`, `StatusBadge`, `RawImage`, `ScrollColumn`, `Box`/`Spacer`/`Size`. Genera el sprite de esquinas redondeadas por código (9-slice cacheado por radio) y registra un callback de "repaint" por widget para retematizar en caliente. |
| `Assets/Scripts/Runtime/Tablet/TabletPalette.cs` | Paletas Dark (consola médica, teal) y Light (historia clínica, azul); port verbatim de las constantes del `theme_builder.gd` de Godot. |
| `Assets/Scripts/Runtime/Tablet/TabletButton.cs` | Botón custom (hereda `Selectable`): fill + borde + texto con color por estado (normal/hover/pressed), modo toggle y callbacks `OnClick`/`OnToggled`. Reemplaza el `ColorBlock` de uGUI. |
| `Assets/Scripts/Runtime/Tablet/LensCardView.cs` | Card de lente: nombre, descripción clínica, chips OD/OI que marcan en qué ojo(s) está aplicada; tap = aplicar. |
| `Assets/Scripts/Runtime/Tablet/ParamRowView.cs` | Fila de ajuste fino: label + valor formateado + slider + hint clínico. `SetValueSilent` sincroniza sin re-emitir. |
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
      ├─ session.TryGetCachedPin(host) ─▶ UI.BeginConnect(host, pin) directo
      └─ sin PIN guardado ─▶ UI.ShowPinScreen(host) ─▶ "Conectar" ─▶ UI.BeginConnect(host, pin)
   UI.BeginConnect ─▶ ShowConnectScreen("Conectando...") + session.Connect(host, pin)
   session: ws Connected ─▶ SendCommand({"type":"auth",...}) ─▶ evento Connected ─▶ UI actualiza texto
   session: "auth_ok" ─▶ cachea el PIN ─▶ evento AuthOk ─▶ UI.SetConnectStatus(...)
   session: "auth_fail" ─▶ el visor cierra la conexión ─▶ OnWsDisconnected interno ─▶ evento
                           PinScreenRequested ─▶ UI.ShowPinScreen("PIN incorrecto"), corta reconexión
   session: "auth_locked" ─▶ el visor cierra la conexión ─▶ OnWsDisconnected interno:
                    si NO se estaba reconectando ─▶ evento PinScreenRequested ("Demasiados intentos...")
                    si SÍ (P2.5) ─▶ evento ReconnectStatusChanged, sigue el loop tras retry_in_s
   session: caída NO manual de sesión activa (P2.5) ─▶ StartReconnectLoop() interno ─▶ eventos
                    ReconnectStarted + ReconnectStatusChanged ─▶ UI.ShowReconnectScreen + backoff 2/4/8/15s
   session: "hello" ─▶ actualiza catálogo/vision_state/escenarios ─▶ evento HelloReceived(lenses) ─▶
                    UI.RebuildLensList/RebuildScenarioList/RefreshVisionUI/ShowMainScreen
   session: binario 'B'/'L'/'R'+JPG ─▶ separa header ─▶ evento FrameReceived(eye, jpg) ─▶
                    UI.LoadImage en RawImage por ojo + contadores del footer
   UI ─▶ session.SendCommand(apply_lens / override_params / set_astigmatism / load_scenario / refresh)
   session: "vision_state" ─▶ evento VisionStateChanged ─▶ UI.RefreshVisionUI + SyncParamRowsFromState()
```

## Pantallas y secciones (todas en `TabletController`, capa de UI)
- **ConnectScreen:** glifo de ojo + título, lista de visores descubiertos (botones `Visor Quest · IP`),
  estado de búsqueda, y "Conexión manual" colapsable con `LineEdit` de IP + botón Conectar.
- **PinScreen:** se intercala entre ConnectScreen y MainScreen cuando hace falta el PIN de
  emparejamiento (host sin PIN guardado en memoria, o reintento tras `auth_fail`/`auth_locked`).
  Glifo + título, host destino, `LineEdit` numérico de 6 dígitos
  (`TMP_InputField.ContentType.IntegerNumber` → teclado numérico en Android) y botones
  Cancelar/Conectar. El mensaje de estado distingue PIN incorrecto ("PIN incorrecto. Volvé a
  intentarlo.") de lockout del visor ("Demasiados intentos. Esperá Ns y volvé a intentarlo.",
  con el `retry_in_s` que manda el visor). Detalle del protocolo y el modelo de amenaza en
  `docs/networking.md`.
- **ReconnectScreen (P2.5):** se muestra ante una caída NO manual de una sesión activa. Glifo +
  título "Reconectando", host destino, estado (cuenta atrás del backoff o "Reconectando… (intento
  N)") y botón Cancelar (corta el loop y vuelve al `ConnectScreen`). Ver StartReconnectLoop/
  DoReconnectAttempt/OnWsDisconnected y Decisiones abajo.
- **MainScreen / Header:** glifo + título, selector de escenarios (segment buttons), toggle de tema
  claro/oscuro, botón "Actualizar" (P5.4 — refresh en caliente, ver Decisiones), badge de estado
  (punto de color + texto) y botón Desconectar.
- **Panel de stream (izquierda):** uno o dos panes con `RawImage` dentro de un `AspectRatioFitter`
  4:3 (768/576). El split lo decide `blend_active` del `vision_state` (P2.1 — fuente única de
  verdad, ver `docs/networking.md`): en blend los panes se apilan verticalmente
  (`OI · <lente>` / `OD · <lente>`); si no, un solo pane "Ambos ojos" (incluye el caso de un solo
  ojo con lente aplicada: antes mostraba 2 panes con uno de etiqueta vacía).
- **Columna scrolleable (derecha):** cards "Ojo a tratar" (Ambos / OD / OI), "Lentes intraoculares"
  (LensCardViews del catálogo), **"Comparar A / B"** (P5.1: 2 slots que recuerdan una lente cada
  uno + botón grande "A ↔ B" que alterna cuál está aplicada en el ojo seleccionado — ver
  Decisiones), "Ajuste fino" (colapsable, ParamRowViews de la lente en edición + Restaurar
  valores — desde P4.4 incluye `astig_magnitude`/`astig_axis_deg`, persistentes por lente),
  "Astigmatismo" (colapsable: hint de precedencia + switch + sliders LIVE de magnitud 0–50 px y
  eje 0–180°, no persistente — ver Decisiones "Dos controles de astigmatismo") y **"Presets"**
  (P5.2, colapsable: lista de presets guardados con Aplicar/Borrar + `LineEdit` de nombre y botón
  Guardar — ver Decisiones).
- **Footer:** `N fps · X.X MB recibidos`, actualizado cada segundo. En blend (P5.5), el fps
  mostrado se divide entre 2 (ver Decisiones): representa la tasa REAL por pane, no la suma L+R.

## Decisiones y porqués
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
- **PIN guardado en memoria por host, nunca a disco** (P1.1) → `_pinByHost` (`Dictionary<string,
  string>`) vive solo mientras corre la app; a diferencia de la preferencia de tema (persistida en
  `ui_prefs.cfg`), el PIN no se escribe a `persistentDataPath` a propósito (es el secreto de
  emparejamiento de la sesión del visor). Reconectar al mismo host en la misma sesión de la tablet
  reusa el PIN sin volver a pedirlo; cerrar y abrir la app de nuevo, o que el visor responda
  `auth_fail`, lo borra y hay que reingresarlo. Protocolo completo en `docs/networking.md`.
- **Reconexión automática solo a la última sesión, con backoff acotado (P2.5)** → una caída NO
  manual (`_manualDisconnect == false`) de una sesión que estaba `_sessionActive` dispara
  `StartReconnectLoop()`: reintenta a `_currentHost` con el PIN de `_pinByHost` (si no hay PIN
  cacheado, degrada al flujo manual — no debería pasar si hubo sesión activa, pero es defensivo).
  Backoff exponencial `DelayForAttempt(N) = min(2 * 2^(N-1), 15)` segundos → 2, 4, 8, 15, 15, ...
  indefinido hasta que el usuario cancela o el visor corta el loop (`auth_fail` → PIN nuevo, no
  tiene sentido reintentar solo). `auth_locked` es la excepción: en vez de cortar, se espera el
  `retry_in_s` que manda el visor y se sigue reintentando (el PIN cacheado puede ser el correcto,
  el visor ni lo evaluó). El timer solo cuenta cuando NO hay un intento en vuelo (`!_connecting`).
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
- **Comparación A/B minimal, sin protocolo nuevo (P5.1)** → el flujo clínico real es "¿ve mejor
  con 1 o con 2?"; en vez de un comparador complejo, 2 slots (`_abLensA`/`_abLensB`, solo un
  `lens_id` cada uno) que se cargan con "Usar actual" (toma `CurrentEyeLensId()`, la lente activa
  hoy en el ojo seleccionado) y un botón grande "A ↔ B" que llama `OnLensSelected(next)` con el
  slot que NO está activo — reusa el `apply_lens` + actualización optimista + editor de "Ajuste
  fino" que ya existían para tocar una lente de la lista, cero mensajes nuevos. El label de cada
  slot muestra "(activa)" junto al que coincide con `CurrentEyeLensId()`; `RefreshAbUI()` se
  llama desde `RefreshVisionUI()` así el indicador se mantiene al día con cualquier `vision_state`
  entrante (confirmación del visor, refresh, etc.), no solo con el click local.
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

## Gotchas
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
- **Los `Texture2D` del stream se recrean por `LoadImage`** sobre las mismas instancias `_texLeft`/
  `_texRight` (RGB24 2×2 inicial que se redimensiona solo). No cachear referencias a su tamaño.
- **`auth_fail`/`auth_locked` implican desconexión inminente:** el visor manda el mensaje y CIERRA
  esa conexión del lado servidor casi inmediatamente. `TabletSession` (P6.2: antes era
  `TabletController`) no cierra nada por su cuenta: solo marca `_authFailed`/`_authLocked` (y en
  el caso de `auth_locked`, `_authLockRetrySeconds`) en su `OnText`; el `Disconnected` real llega
  poco después vía `OnWsDisconnected` (interno de `TabletSession`), que es quien dispara el evento
  `PinScreenRequested` con el mensaje correcto (chequea `_authLocked` ANTES de `_authFailed`, son
  mutuamente excluyentes para una misma conexión). Si se toca ese flujo, mantener el orden (flag
  primero en `OnText`, evento en el disconnect) o la UI puede terminar mostrando el `PinScreen`
  mientras el socket todavía figura "abierto".
- **`auth_locked` NO limpia el PIN cacheado:** a diferencia de `auth_fail` (que borra
  `_pinByHost[host]` porque el PIN mandado ya se sabe que está mal), `auth_locked` no toca el
  cache — el PIN mandado puede ser el correcto, el visor ni lo evaluó. Si se agrega lógica nueva
  alrededor de `_pinByHost`, no asumir que todo fallo de auth implica "borrar el PIN guardado".
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
- **`CurrentEyeLensId()` (P5.1) mira el ojo IZQUIERDO cuando el selector está en "Ambos":** si el
  operador arma A/B con "Ambos" seleccionado y las lentes activas de cada ojo son distintas
  (modo blend), el slot capturado y el marcador "(activa)" reflejan solo `left`. Es una
  simplificación deliberada (el comparador A/B asume que se compara sobre UN ojo o sobre ambos
  con la MISMA lente) — no está pensado para blend.
- **Los slots A/B no se limpian si la lente deja de existir (P5.1):** un `refresh` (P5.4) que
  traiga un catálogo sin esa lente no vacía `_abLensA`/`_abLensB`; `LensDisplayName` cae al id
  crudo (degradación grácil, mismo patrón que en el resto de la tablet) pero el botón "A ↔ B"
  seguiría intentando aplicar un id que el visor ya no reconoce (`DataManager.ApplyLens` solo
  loguea warning y no cambia nada, ver `docs/catalogo-lentes.md`).
- **Los presets NO revalidan contra el catálogo actual (P5.2):** si se borra una lente del
  catálogo (edición manual de `lentes.json`/backend) y se aplica un preset viejo que la
  referenciaba, `apply_lens` del visor solo loguea warning y no cambia el estado de ese ojo — el
  preset "falla en silencio" para esa lente puntual (el resto de los comandos del preset sí se
  mandan). No hay validación cliente-side de que los ids de un preset sigan existiendo.
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
   detectado en pocos segundos (mismo host en Editor: fallback manual `127.0.0.1`).
2. Tocar el visor detectado (o "Conectar" en manual): debe aparecer el `PinScreen` pidiendo el PIN
   de 6 dígitos (lo loguea la consola del visor: `Net: PIN de emparejamiento de esta sesion: ...`;
   en el visor real lo muestra el HUD). Ingresarlo mal a propósito una vez → "PIN incorrecto. Volvé
   a intentarlo." y vuelve a pedirlo; ingresarlo bien → debe llegar el `hello`, pasar a la pantalla
   principal con badge verde `Conectado · IP`, las cards del catálogo y el stream en movimiento
   (footer con fps/MB creciendo). Probar también un PIN con ceros a la izquierda (p.ej. `000123`,
   si el que generó el visor tiene esa forma) para confirmar que el `LineEdit` numérico no los
   recorta ni el envío los trunca (el PIN es un string de 6 caracteres, no un número).
2b. **Lockout:** repetir el PIN incorrecto 3 veces (reconectando cada vez) → al cuarto intento la
   tablet debe mostrar "Demasiados intentos. Esperá Ns y volvé a intentarlo." (no "PIN
   incorrecto") aunque esta vez se ingrese el PIN correcto. Esperar los Ns indicados y reintentar
   con el PIN correcto → debe autenticar normal.
3. Tocar una lente con "Ambos" seleccionado → chips OD y OI encendidos en la card y editor de
   "Ajuste fino" con las filas de `ParamMeta` en orden clínico; mover un slider debe verse reflejado
   en el stream. "Restaurar valores" vuelve a defaults y manda un solo `override_params`.
4. Elegir "OD · Derecho", aplicar otra lente → el stream debe partirse en dos panes apilados
   (`OI ·`/`OD ·`) y cada card mostrar su chip.
5. Togglear tema claro/oscuro (debe repintar todo en caliente y persistir tras reiniciar) y cambiar
   de escenario desde el header.
6. Probar desconexión manual: botón Desconectar → "Sesión finalizada." (vuelve al `ConnectScreen`
   directo, NO dispara reconexión automática — es el camino `_manualDisconnect`).
6b. **Reconexión automática (P2.5, 2 dispositivos):** con la tablet conectada y activa, matar/pausar
   el visor (o cortar su Wi-Fi) sin usar el botón Desconectar de la tablet → debe aparecer el
   `ReconnectScreen` ("Se perdió la conexión con el visor." → "Reconectando… (intento N)") con
   cuenta atrás creciente (2 s, 4 s, 8 s, tope 15 s). Reactivar el visor (mismo PIN de sesión, no
   reiniciarlo) antes de que el clínico cancele → debe reconectar solo y volver al `MainScreen`
   sin pedir el PIN de nuevo. Probar también "Cancelar" durante la cuenta atrás → debe volver al
   `ConnectScreen` normal (discovery) sin más reintentos.
6c. **Reconexión + visor reiniciado (PIN nuevo):** repetir el corte, pero esta vez REINICIAR el
   visor (nuevo PIN de sesión) antes de que la tablet reconecte → el intento automático debe
   recibir `auth_fail`, cortar el loop y mostrar el `PinScreen` pidiendo el PIN nuevo (no debe
   seguir reintentando solo con el PIN viejo).
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
10. **Astigmatismo residual persistente (P4.4):** aplicar cualquier lente → "Ajuste fino" debe
    mostrar las filas "Astigmatismo residual" (0–1) y "Eje del astigmatismo" (0–180°) junto a
    "Desenfoque máximo" (antes de los halos), ambas en 0 por default. Subir "Astigmatismo
    residual" → debe viajar por `override_params` y persistir en `lens_overrides.json` (igual que
    cualquier otro param; ver §Overrides en `docs/catalogo-lentes.md`). Abrir la card
    "Astigmatismo" (live) y confirmar que el hint nuevo aclara la precedencia ("ajuste temporal…
    para persistir usá Ajuste fino"); activar el switch LIVE y verificar que el efecto en el
    stream se ve igual sin importar cuál de los dos controles lo generó (mismo pipeline
    `glare_astig_l/r`) — no deben aparecer sincronizados entre sí (es esperado, ver Decisiones).
11. **Comparar A/B (P5.1):** con "OD · Derecho" seleccionado, aplicar monofocal, abrir "Comparar
    A / B" y tocar "Usar actual" en la fila A (debe mostrar "A: Monofocal Estandar (activa)").
    Aplicar panoptix al mismo ojo y tocar "Usar actual" en B ("B: PanOptix Pro (activa)"; el botón
    "A ↔ B" pasa a interactuable). Tocar "A ↔ B" repetidamente → el stream del OD debe alternar
    entre ambas lentes y el marcador "(activa)" debe saltar de A a B en cada toque. Cambiar el
    selector a "OI · Izquierdo" sin tocar A/B → el marcador debe desaparecer de ambos (ninguna de
    las dos está activa en OI, salvo coincidencia).
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
- **A/B (P5.1) no cubre blend ni revalida contra el catálogo, y presets (P5.2) no revalidan ids
  ni versionan el archivo** — ver el detalle de cada caso en Gotchas. Ninguno rompe nada hoy
  (degradan a warning/estado parcial), pero son los bordes a endurecer si el flujo A/B-en-blend o
  los presets compartidos entre clínicos se vuelven un caso de uso real.
- **`refresh` (P5.4) no tiene indicador visual de "en curso" ni feedback de error**: `OnRefreshPressed`
  solo valida que el WS esté abierto (badge "Sin conexión" si no) pero no muestra nada mientras
  espera la respuesta ni si el visor tardara en contestar; como reusa el flujo de `"hello"`, el
  único indicio de éxito es que la lista de lentes/escenarios se repuebla. Aceptable para un botón
  de uso ocasional, pero si se vuelve frecuente convendría un estado de carga.
- **`TabletSession` sin tests unitarios (P6.2):** es plain C# (sin corrutinas ni `UnityWebRequest`,
  a diferencia de `DataManager`), así que en teoría es más testeable que antes del split — pero
  sigue dependiendo de `WebSocketClient`/`DiscoveryListener` reales (no hay interfaz inyectable) y
  de eventos de `Newtonsoft.Json.Linq`, así que testear la máquina de reconexión hoy exige
  reflection + Play Mode real (ver Cómo probar), igual que se hizo para verificar el split. Si se
  necesitara cobertura EditMode de verdad, el camino natural es extraer `DelayForAttempt` y el
  parsing de `OnText`/`OnBinary` a funciones puras separadas (mismo patrón que
  `DataManagerLogic.cs`, ver `docs/catalogo-lentes.md`) — no se hizo en esta tarea por ser un
  refactor mecánico, no una tarea de cobertura.
