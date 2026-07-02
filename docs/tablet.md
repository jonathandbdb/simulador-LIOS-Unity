# App tablet (control de consultorio)

## Qué es y por qué
App Android plana (sin VR) que corre en la tablet del oftalmólogo: descubre el visor Quest en la
red, se conecta por WebSocket, muestra en vivo lo que ve el paciente (stream por ojo) y permite
aplicar lentes intraoculares, ajustar parámetros clínicos, simular astigmatismo y cambiar de
escenario. Es la réplica fiel de `features/tablet/streaming_client.gd` del proyecto Godot original.

## Arquitectura actual

| Archivo | Rol |
|---|---|
| `Assets/Scripts/Runtime/Net/TabletController.cs` | Único MonoBehaviour de la app (858 líneas). Orquesta red + UI: construye toda la interfaz en `Start()`, drena discovery/WS en `Update()`, parsea el protocolo y arma cada pantalla/card por código. |
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
Start()
  Resources.Load fuentes ─▶ new TabletUiKit(paleta según prefs)
  BuildUI()  ─▶ ConnectScreen + MainScreen (oculta)
  WebSocketClient + DiscoveryListener.Start()
                │
   beacon UDP ──┴─▶ lista "Visores detectados" (poda a los 6 s sin beacon)
   tap visor / IP manual ─▶ ws.Connect(host, 9090)
   "hello" ─▶ catálogo + vision_state + escenarios ─▶ ShowMainScreen()
   binario 'B'/'L'/'R'+JPG ─▶ LoadImage ─▶ RawImage por ojo
   UI ─▶ SendCmd(apply_lens / override_params / set_astigmatism / load_scenario)
   "vision_state" ─▶ chips OD/OI + labels de stream + SyncParamRowsFromState()
```

## Pantallas y secciones (todas en `TabletController`)
- **ConnectScreen:** glifo de ojo + título, lista de visores descubiertos (botones `Visor Quest · IP`),
  estado de búsqueda, y "Conexión manual" colapsable con `LineEdit` de IP + botón Conectar.
- **MainScreen / Header:** glifo + título, selector de escenarios (segment buttons), toggle de tema
  claro/oscuro, badge de estado (punto de color + texto) y botón Desconectar.
- **Panel de stream (izquierda):** uno o dos panes con `RawImage` dentro de un `AspectRatioFitter`
  4:3 (768/576). En modo blend (lentes distintas por ojo) los panes se apilan verticalmente
  (`OI · <lente>` / `OD · <lente>`); con la misma lente se muestra un solo pane "Ambos ojos".
- **Columna scrolleable (derecha):** cards "Ojo a tratar" (Ambos / OD / OI), "Lentes intraoculares"
  (LensCardViews del catálogo), "Ajuste fino" (colapsable, ParamRowViews de la lente en edición +
  Restaurar valores) y "Astigmatismo" (colapsable: switch + sliders de magnitud 0–50 px y eje 0–180°).
- **Footer:** `N fps · X.X MB recibidos`, actualizado cada segundo.

## Decisiones y porqués
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
  `ParamMeta.ORDER` impone el orden de presentación (focos → blur → disfotopsias). Claves fuera de
  la metadata caen al final con su nombre crudo: el catálogo puede crecer sin tocar la tablet.
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

## Gotchas
- **No hay prefabs de UI: sin Play no hay nada.** La escena `Tablet.unity` solo tiene `TabletApp`;
  jerarquía, EventSystem (con `InputSystemUIInputModule`) y Canvas (1280×800, ScaleWithScreenSize)
  se crean en `BuildUI()`. Cualquier cambio visual se hace en `TabletUiKit`/`TabletController`, no
  en el Editor — editar la escena no sirve.
- **Fuentes por convención de path:** `Resources.Load<TMP_FontAsset>("TabletFonts/Inter-Regular SDF")`.
  Renombrar/mover los assets de `Assets/Resources/TabletFonts/` rompe silencioso (labels sin fuente).
- **`NetworkController` detecta a `TabletController` para no levantar server:** su `Bootstrap`
  hace `FindFirstObjectByType<TabletController>()` y aborta. Si se renombra o divide
  `TabletController`, la app tablet pasaría a levantar un WebSocketServer propio.
- **Escenarios matcheados por texto del label:** `OnScenarioPressed` marca el botón activo comparando
  `b.Label.text == ScenarioLabel(id)`. Dos escenarios con el mismo label rompen la selección.
- **La actualización optimista descarta params:** `OnLensSelected` reemplaza el estado del ojo por
  `new JObject { lens_id }`; hasta que llega el `vision_state` real, `CurrentParamValue` cae a los
  defaults del catálogo. Ventana corta, pero visible si el visor tarda en confirmar.
- **El fps del footer cuenta frames totales:** en modo blend llegan frames L y R separados, así que
  el número mostrado es ~2× la tasa por ojo.
- **`set_astigmatism` envía `eye` pero el visor lo ignora** (aplica global). No prometer al operador
  astigmatismo por ojo hasta cerrar esa asimetría (ver `docs/networking.md`).
- **`RefreshDiscovered` reconstruye la lista destruyendo hijos cada segundo:** válido por lo chico
  de la lista, pero cualquier estado que se quiera guardar en esos botones se pierde en cada tick.
- **Los `Texture2D` del stream se recrean por `LoadImage`** sobre las mismas instancias `_texLeft`/
  `_texRight` (RGB24 2×2 inicial que se redimensiona solo). No cachear referencias a su tamaño.

## Cómo probar
1. Con el visor corriendo (Play en `Assets/Scenes/Main.unity` o build Quest), abrir
   `Assets/Scenes/Tablet.unity` y dar Play: debe aparecer la pantalla de conexión con el visor
   detectado en pocos segundos (mismo host en Editor: fallback manual `127.0.0.1`).
2. Conectar: debe llegar el `hello`, pasar a la pantalla principal con badge verde `Conectado · IP`,
   las cards del catálogo y el stream en movimiento (footer con fps/MB creciendo).
3. Tocar una lente con "Ambos" seleccionado → chips OD y OI encendidos en la card y editor de
   "Ajuste fino" con las filas de `ParamMeta` en orden clínico; mover un slider debe verse reflejado
   en el stream. "Restaurar valores" vuelve a defaults y manda un solo `override_params`.
4. Elegir "OD · Derecho", aplicar otra lente → el stream debe partirse en dos panes apilados
   (`OI ·`/`OD ·`) y cada card mostrar su chip.
5. Togglear tema claro/oscuro (debe repintar todo en caliente y persistir tras reiniciar) y cambiar
   de escenario desde el header.
6. Probar desconexión: botón Desconectar → "Sesión finalizada."; matar el visor → "Se perdió la
   conexión con el visor." en rojo.
7. Para device real: **Simulador → Build Tablet (Android)** genera `Builds/Android/Simulador.apk`
   (ver `docs/builds-deploy.md`).

## Pendientes / deuda
- Astigmatismo por ojo: la UI ya manda `eye`, falta que el visor lo respete.
- Selección de escenario por comparación de label (frágil ante labels duplicados).
- Footer cuenta frames L+R juntos en blend (fps mostrado engañoso).
- El `hello` es el único momento en que se reconstruyen catálogo y escenarios; no hay refresh en caliente.
