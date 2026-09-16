# Pantalla de chequeo de calce

## Qué es y por qué

Pantalla de texto corta en el visor Quest que el paciente debe poder leer **NÍTIDA** para
confirmar que el casco está bien colocado, antes de (o en cualquier momento durante) la
consulta. No mide la LIO simulada ni ningún efecto óptico clínico: mide el **hardware** — si el
casco quedó mal ajustado o descalzado del sweet spot de las lentes del Quest, el paciente ve
borroso por eso, no por la lente intraocular que se está simulando. Se muestra sola al arrancar
el visor y el médico la puede mostrar/ocultar desde la tablet en cualquier momento de la sesión
(comando `set_focus_check`, ver `docs/networking.md`), típicamente cuando el paciente dice "no
veo bien" y hay que descartar que sea el casco antes de tocar ningún parámetro de la simulación.

## Arquitectura actual

| Archivo | Rol |
|---|---|
| `Assets/Scripts/Runtime/Onboarding/FocusCheckScreenVR.cs` | Único archivo del sistema. `MonoBehaviour` que se auto-crea (`[RuntimeInitializeOnLoadMethod(AfterSceneLoad)]`) solo si hay un `ScenarioManager` en la escena (componente exclusivo del visor, `Vision/`) — así nunca se dispara en `Tablet.unity`, que comparte el asmdef `Simulador.Runtime` pero no tiene ese componente. Expone `static bool IsVisible` y `static void SetVisible(bool)`. |
| `Assets/Scripts/Runtime/Net/NetworkController.cs` | `case "set_focus_check"` en el switch de comandos (`OnTextReceived`) llama `FocusCheckScreenVR.SetVisible(...)` directo (clase estática, sin resolver una referencia de escena como `ResolveHud()`). `BuildHello()` agrega `["focus_check"] = FocusCheckScreenVR.IsVisible`. |
| `Assets/Scripts/Runtime/Tablet/TabletSession.cs` | `FocusCheckVisible` (bool, default `true`) parseado del campo `"focus_check"` del hello. |
| `Assets/Scripts/Runtime/Net/TabletController.cs` | Botón "Ocultar calce"/"Mostrar calce" en el header Pro (`BuildHeader`, junto a "Recentrar") y en `StdTopBar` (modo Standard, también junto a "Recentrar") — **en AMBOS modos**, a diferencia del toggle de HUD (Pro/admin only): es una acción clínica básica. `OnFocusCheckTogglePressed` (compartido por los dos botones) manda `{"cmd":"set_focus_check","visible":bool}`; `OnSessionHello` resincroniza `_focusCheckVisible` desde `_session.FocusCheckVisible` en cada hello. |
| `Assets/Scripts/Runtime/Localization/L10nTable.cs` | Namespace `focus.*` (título + 3 líneas + marca de centrado) y `main.focus_check_show`/`main.focus_check_hide` (label del botón). |

```
Arranque del visor (Main.unity)
  FocusCheckScreenVR.Bootstrap() [AfterSceneLoad]
    ¿hay ScenarioManager en la escena? no -> no-op (Tablet.unity)
    sí -> crea el GameObject, SetVisible(true) -- arranca visible SOLO

Tablet (conectada despues, o no)
  hello -> TabletSession.FocusCheckVisible = focus_check (default true)
        -> TabletController.OnSessionHello sincroniza el label del botón

Médico toca "Ocultar calce"/"Mostrar calce" (header Pro o StdTopBar)
  -> {"cmd":"set_focus_check","visible":bool}
  -> NetworkController.OnTextReceived -> FocusCheckScreenVR.SetVisible(bool)
  -> canvas world-space se activa/desactiva + CameraSceneOcclusionGate Acquire/Release
```

## Decisiones y porqués

- **Fuera de `Vision/` a propósito** → esto es UI de sesión (calce del hardware), no óptica
  clínica; `Vision/` y `Assets/Shaders/` son de `@vision-optics`. Un namespace/carpeta propios
  (`Simulador.Onboarding`) evitan mezclar responsabilidades en una carpeta que no es la suya.
- **Misma geometría que `LicenseBlockScreenVR`** (2.0 m, `localScale` 0.002, canvas world-space
  hijo de `Camera.main`) → dos motivos: (a) es la distancia de fusión estéreo probada en
  dispositivo (hubo diplopía REAL en Quest con canvas a 0.15–0.2 m, ver
  `Data/CameraSceneOcclusionGate.cs` y `docs/updates.md`); (b) coincide con el plano focal fijo
  del Quest, que es justo donde el texto está ópticamente más nítido — lo que se está midiendo.
  **Efecto colateral corregido (revisión, MAYOR):** esta misma geometría deja los dos canvases
  COPLANARES (mismo `z`, `localScale`, `sizeDelta`, opacidad de panel), así que sin más el orden
  de dibujado entre ambos durante los ≤500 ms de la ventana del guard anti-coexistencia (ver más
  abajo) es arbitrario — el cartel de licencia, que es fail-closed, podía quedar tapado por el
  panel de calce. Fix: `canvas.sortingOrder = -1` en `BuildCanvas` — el cartel de calce siempre
  se manda detrás; la licencia (que no setea `sortingOrder`, queda en 0) siempre gana si ambos
  llegan a coexistir en ese margen.
- **`CameraSceneOcclusionGate` compartido con License/Update** → mismo refcount que
  `LicenseBlockScreenVR`/`UpdatePromptVR`. A diferencia de esos dos (que se crean/destruyen una
  vez por evento), esta pantalla puede toggearse muchas veces por sesión (botón de la tablet), así
  que el canvas se arma UNA sola vez (`BuildCanvas`, primera vez que se muestra) y los toggles
  siguientes solo hacen `SetActive` + Acquire/Release — evita destruir/reconstruir la jerarquía de
  UI en cada tap.
- **`IsVisible` estático en vez de un mensaje de confirmación nuevo** → la pantalla se muestra
  sola al arrancar (antes de que exista ninguna tablet conectada), así que el patrón optimista de
  `set_hud` (sin estado consultable) haría que el botón de una tablet recién conectada mienta
  ("Mostrar calce" con la pantalla ya visible). En vez de agregar un mensaje/ack nuevo,
  `NetworkController.BuildHello()` lee `FocusCheckScreenVR.IsVisible` y lo manda como
  `"focus_check"` en cada hello — mismo criterio de minimal-footprint que `blend_active` (un
  campo más en un mensaje que ya existía, ver `docs/networking.md`).
- **Botón presente en Standard Y Pro** (a diferencia del toggle de HUD, Pro/admin only) → "el
  paciente no ve bien, descartemos que sea el casco" es una acción clínica básica de cualquier
  operador, no una herramienta de diagnóstico técnico. `OnFocusCheckTogglePressed` y
  `UpdateFocusCheckLabel` son compartidos por los dos botones (`_focusCheckToggleBtn` header Pro,
  `_stdFocusCheckToggleBtn` StdTopBar) para no duplicar lógica.
- **No coexiste con `LicenseBlockScreenVR`, resuelto SIN tocar `License/`** → dos canvases
  superpuestos a la misma distancia/escala serían ilegibles. `SetVisible(true)` no hace nada si
  ya hay un `LicenseBlockScreenVR` en la escena (bloqueo fail-closed, de mayor prioridad); mientras
  esta pantalla está visible, `Update()` la oculta sola si el bloqueo de licencia aparece después
  (verify async que falla a mitad del chequeo de calce). No hay guard simétrico del otro lado —
  `LicenseBlockScreenVR` no sabe de esta pantalla porque el bloqueo de licencia siempre gana.
  **El chequeo está acotado a ~2 Hz** (`LicenseCheckIntervalS = 0.5f`, acumulando `Time.deltaTime`
  en `Update()`), no por frame — mismo criterio que `NetworkController.DiscoverSceneRefs`
  (`Net/NetworkController.cs:201-213`, ya documentado ahí por el mismo motivo): un
  `FindFirstObjectByType` con `FindObjectsInactive.Include` barre TODA la escena (incluida la
  jerarquía inactiva de `RutaNoche`), y esta pantalla puede quedar visible minutos enteros
  (el paciente acomodándose la correa) a 72–90 Hz en el Quest — sin acotar, ese barrido corría en
  cada frame durante todo ese tiempo (hallazgo de revisión, corregido antes de cerrar la tarea).
- **Sin `InputAction` propia, sin tocar `SimuladorInput.enabled`** → esta pantalla se controla
  EXCLUSIVAMENTE desde la tablet (comando `set_focus_check`). `SimuladorInput.cs` (comentario de
  `AdminGate`, líneas ~54-57) ya documenta que `LicenseBlockScreenVR`/`UpdatePromptVR` se disputan
  ese flag con guards anti-restore cruzados y que una tercera mano lo rompería — como esta
  pantalla nunca toca `enabled`, el riesgo desaparece por diseño en vez de sumar un guard más.

## Gotchas

- **El texto sale nítido SIN tocar el sistema de visión, y es intencional** → el post-proceso
  (blur dióptrico, astigmatismo, contraste, velo) se inyecta en
  `VisionRendererFeature.injectionPoint = RenderPassEvent.BeforeRenderingTransparents`
  (`Assets/Scripts/Runtime/Vision/VisionRendererFeature.cs:31-36`). Un `Canvas` world-space
  (`RenderMode.WorldSpace`, cola Transparent, `renderQueue` 3000 por defecto de la UI legacy) se
  dibuja DESPUÉS de ese pass, así que queda fuera del blur/astigmatismo/contraste/velo **por
  construcción** — igual que los billboards de glare (aditivos, cola transparente, se componen
  encima de la imagen ya borroseada). Es el **INVERSO EXACTO** del gotcha del optotipo ETDRS
  (`docs/vision-optica.md` §"Optotipo ETDRS", P4.5-fix): ese texto sí debe leerse CON la LIO
  puesta (mide agudeza funcional), así que se forzó a `renderQueue = 2450` (cola opaca) para que
  el post-proceso SÍ lo alcanzara. Un agente futuro que note "este texto no tiene blur" **no debe
  imitar ese fix acá** — sería exactamente el bug contrario. No hace falta bypass del
  `VisionRendererFeature` ni el escenario `paciente_joven`: la lente aplicada nunca se toca ni se
  pierde mientras esta pantalla está visible.
- **El fondo detrás del panel NO siempre queda neutro (revisión, documentado — no se arregla)** →
  `CameraSceneOcclusionGate.Acquire()` restringe el `cullingMask` de `Camera.main` a la capa `UI`
  y fuerza `clearFlags = SolidColor`, pero **no desactiva** `VisionRendererFeature`: ese Renderer
  Feature sigue corriendo como post-proceso de pantalla completa sobre lo que la cámara efectivamente
  dibuja, color de clear incluido. Con una lente que agrega tinte o velo (p. ej. `catarata`), el
  color sólido oscuro del gate se ve TEÑIDO de ámbar/velado fuera del panel. **El panel y el texto
  en sí NO se ven afectados** (son la parte que importa) — **porque** el `Canvas` world-space
  dibuja DESPUÉS de `RenderPassEvent.BeforeRenderingTransparents` (ver el punto de arriba, "El
  texto sale nítido..."): si algún día se mueve ese `injectionPoint` a después de la cola
  transparente, esta afirmación deja de ser cierta y hay que revisar las dos cosas juntas, no solo
  el tinte de fondo. Es comportamiento **PREEXISTENTE**, no introducido por esta pantalla — afecta
  igual a `LicenseBlockScreenVR` y `UpdatePromptVR` (mismo gate). Lo que sí cambia con esta tarea:
  antes solo se veía en eventos raros (bloqueo de licencia, prompt de update, ambos poco
  frecuentes); ahora, como esta pantalla arranca visible en **toda sesión** del visor, el tinte de
  fondo se vuelve visible en el arranque normal, no solo en casos borde. **Deliberadamente no se
  corrige acá**: es puramente cosmético (el instrumento de medición — el texto — sigue siendo
  fiable), y tocar `VisionRendererFeature` para neutralizar un tinte de fondo sería un riesgo de
  regresión desproporcionado en territorio de `@vision-optics` para un problema que no afecta la
  función de ninguna de las tres pantallas. Si hiciera falta un fondo garantizado neutro, la vía
  natural es que el gate también controle el `enabled`/`renderPassEvent` del
  `VisionRendererFeature` mientras esté activo — fuera de alcance de esta tarea, ver Pendientes.
- **Un `Acquire` sin su `Release` deja la cámara ocluida para siempre** (`CameraSceneOcclusionGate`,
  refcount compartido con License/Update) — `SetVisibleInternal` guarda contra el doble-toggle
  (`if (_visible) return;` / `if (!_visible) return;`) antes de tocar Acquire/Release, así que
  llamar `SetVisible(true)` dos veces seguidas (p. ej. dos comandos `set_focus_check` en ráfaga)
  no infla el refcount de más.
- **No hay bootstrap en `Tablet.unity`** → el guard es "¿hay `ScenarioManager` en la escena?", no
  un chequeo de nombre de escena; si algún día `Tablet.unity` ganara un `ScenarioManager` por
  error (no debería), esta pantalla se dispararía ahí también.
- **Reset estático defensivo** (`[RuntimeInitializeOnLoadMethod(SubsystemRegistration)]`) — mismo
  patrón que `CameraSceneOcclusionGate.ResetStaticState`: sin él, una sesión de Play anterior con
  Domain Reload deshabilitado (Editor) dejaría `IsVisible`/`_instance` apuntando a un objeto ya
  destruido en la sesión siguiente.

## Cómo probar

1. **Arranque solo (1 dispositivo, Editor o Quest):** dar Play en `Main.unity` sin ninguna tablet
   conectada → la pantalla de chequeo de calce debe verse SOLA apenas arranca la escena (canvas a
   2 m, título + 3 líneas + marca de centrado), sin acción de la tablet.
2. **Sincronización del label al conectar (2 dispositivos):** con la pantalla visible en el
   visor, conectar la tablet (Pro o Standard) → el botón debe arrancar mostrando "Ocultar calce"
   (confirma que `focus_check:true` llegó en el hello). Si el visor la tuviera oculta al momento
   de conectar, el botón debe arrancar en "Mostrar calce".
3. **Toggle (2 dispositivos, ambos modos):** tocar "Ocultar calce"/"Mostrar calce" (header Pro o
   `StdTopBar` Standard) → la pantalla debe desaparecer/reaparecer en el visor/HMD al instante; el
   label del botón debe reflejar el nuevo estado. Repetir en el otro modo (reconectar) → mismo
   comportamiento.
4. **Stream de la tablet:** con la pantalla visible en el visor, el panel de stream de la tablet
   (o el overlay a pantalla completa) debe mostrar el texto de calce nítido — confirma que el
   `Canvas` viaja en la captura de `StreamingCapture` como cualquier otra UI world-space de la
   cámara.
5. **No coexistencia con el bloqueo de licencia:** forzar un bloqueo de licencia (ver
   `docs/licenciamiento.md`) mientras la pantalla de calce está visible → la pantalla de calce
   debe desaparecer sola (gana el bloqueo de licencia) sin quedar superpuesta.

## Pendientes / deuda

- **Sin persistencia del estado entre reinicios del visor** — cada arranque vuelve a mostrar la
  pantalla (comportamiento deliberado: el chequeo de calce es justamente para el momento en que el
  paciente se pone el visor). No hay caso de uso pedido para recordar "el médico la ocultó la
  última vez".
- **Sin selector de idioma propio** — usa `L10n.Lang` como el resto del visor (default por
  idioma del sistema, override solo desde la tablet), mismo pendiente que documenta
  `docs/localizacion.md`.
- **Fondo fuera del panel no garantizado neutro con lentes de tinte/velo** (ver Gotchas) —
  `CameraSceneOcclusionGate` no desactiva `VisionRendererFeature`, así que el color sólido del
  gate puede verse teñido. Cosmético, compartido con License/Update, deliberadamente sin
  corregir — la vía natural si algún día hiciera falta es que el gate también controle el
  Renderer Feature, territorio de `@vision-optics`.
- **Sin salida desde el HMD — decisión consciente del usuario, no un descuido.** Esta pantalla no
  tiene ningún gesto ni botón del mando para ocultarla: el ÚNICO camino es `set_focus_check` desde
  la tablet. Consecuencia real: con una **tablet de un build anterior a este cambio** (que nunca
  manda `set_focus_check`), o si **ninguna tablet llega a conectar** en toda la sesión, el visor
  queda mostrando el cartel de calce el resto de la sesión — y el paciente tampoco ve la
  simulación, porque `StreamingCapture.SyncFromHeadCamera` copia `cullingMask`/`clearFlags` de la
  cámara del ojo (el mismo gate que oculta la escena en el HMD la oculta también en el stream). El
  reviewer marcó esto como CRÍTICO (visor inutilizable sin una tablet compatible conectada); se le
  planteó al usuario con cuatro alternativas — (1) auto-ocultar al primer cliente autenticado,
  (2) timeout automático, (3) botón del mando (rompería la frontera de `SimuladorInput`, ver
  Decisiones), (4) dejarlo como está — y **el usuario eligió (4) explícitamente**. No implementar
  ninguna salida de emergencia es la decisión tomada, no un pendiente técnico por resolver solo.
