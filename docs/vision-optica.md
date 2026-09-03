# Sistema de visión / óptica (simulación IOL por ojo)

## Qué es y por qué

Simula en VR (Meta Quest, URP + Single Pass Instanced / multiview) cómo ve un paciente con distintas
lentes intraoculares: desenfoque dióptrico según distancia, pérdida de contraste, halos difractivos y
starburst sobre luces reales, astigmatismo direccional y encandilamiento clínico (disability glare).
Todo está **bifurcado por ojo** (`unity_StereoEyeIndex`): cada ojo puede llevar una lente distinta
(modo blend). Es un port del proyecto Godot original (`main.gd`, `glare_source.gd`, etc.).

## Arquitectura actual

```
DataManager (catálogo, EyeState por ojo)
   │ evento VisionStateChanged(eye, state)   [los 3 heredan de VisionStateBinder → ciclo de vida común]
   ├─► VisionParamsBinder ──► Material VisionPostProcess (_XxxL/_XxxR)
   ├─► GlareController ─────► Shader globals glare_*_l / glare_*_r (billboards)
   └─► DisabilityGlareController ─ lee "straylight" por ojo
              │ + GlareBillboardInstance activos (fuentes)
              └─► globals _GlareVeilL/R, _GlareVeilUV, _GlareVeilTint

Render por frame (URP RenderGraph):
  opacos + skybox ─► VisionRendererFeature (blur+astig+contraste+velo, POR OJO)
                  ─► transparentes (billboards GlareBillboard, aditivos, SIN blur)
  (gate de CPU: si VisionActivity.AnyActive == false, la feature NO inyecta passes ese frame)

VisionRendererFeature, cadena de passes (etapa C):
  VisionPublishGlobals   (unsafe, sin attachments)  _VisionPxPerDeg + _VisionLowTexel
  VisionLowDown          source      → _VisionLowA   1/16   pass 2  (box 4x4, 4 taps)
  VisionLowGather        _VisionLowA → _VisionLowB   1/16   pass 3  (espiral 24 taps)
                                        └─ SetGlobalTextureAfterPass ⇒ _VisionLowBlur
  VisionDefocus          source      → _VisionTemp   full   pass 0  (disco 13 taps + tier bajo)
                                        └─ UseGlobalTexture(_VisionLowBlur)
  VisionAstigContrastVeil _VisionTemp → source        full   pass 1
```

- `Assets/Scripts/Runtime/Vision/VisionRendererFeature.cs` — ScriptableRendererFeature con la API
  RenderGraph (Unity 6). Inyecta en `BeforeRenderingTransparents` **cuatro blits** (etapa C, ver el
  diagrama de arriba): dos a **1/16** de resolución que construyen el tier de desenfoque grande
  (`_VisionLowA` → `_VisionLowB`, publicado como textura global `_VisionLowBlur`) y los dos full-res
  del ping-pong original (pass 0 esfera/defocus `source→temp`, pass 1 cilindro/astig + contraste +
  velos `temp→source`). Pide
  `ScriptableRenderPassInput.Depth` y aborta si el target activo es el backbuffer (no se puede
  leer+escribir). **Gate de CPU (3.1):** antes de `EnqueuePass` consulta `VisionActivity.AnyActive`;
  si NINGÚN efecto es no-nulo en ambos ojos, saltea la inyección (se ahorran los 4 blits
  por ojo). Loguea `[Vision] Post-proceso gate ON/OFF` solo en las transiciones.
  - **`lowDesc` (R1, invariante XR no negociable):** el descriptor del tier de baja se **copia del
    `cameraTargetDescriptor` tocando SOLO `width`/`height`** (`/ LowDiv`, `LowDiv = 4`).
    `RenderGraphUtils.IsTextureXR` exige `volumeDepth > 1 && volumeDepth == TextureXR.slices`; un
    `RenderTextureDescriptor` construido a mano deja `volumeDepth = 1` ⇒ `AddBlitPass` **no** detecta
    XR ⇒ escribe solo el slice 0 ⇒ **el ojo derecho recibe la imagen del izquierdo**. Es INVISIBLE
    en el Game View mono: solo se detecta en el visor.
  - **Globals `_VisionPxPerDeg` + `_VisionLowTexel` en un pass propio.** `_VisionPxPerDeg` es
    `float2` = píxeles por GRADO del render target por ojo (`x` = izq, `y` = der) =
    `0.5 · desc.height · (π/180) · |GetProjectionMatrix(ojo).m11|`. `_VisionLowTexel` es
    `(1/anchoBaja, 1/altoBaja, anchoBaja/anchoFull, altoBaja/altoFull)` — la relación **real** por eje,
    no un "1/4" asumido (la división entera del descriptor puede no ser exacta). Se publican con
    `cmd.SetGlobalVector` dentro de un `AddUnsafePass` **sin attachments** y con
    `AllowPassCulling(false)` (si no, el graph lo cula por no producir nada): sin attachments no
    fuerza load/store de tile, así que en Quest es gratis. Antes se usaba `Shader.SetGlobalVector`
    desde `RecordRenderGraph` — funcionaba (URP compila+ejecuta+`Submit()` por cámara y
    `StreamingCapture` renderiza sincrónico en `LateUpdate`) pero es un global **inmediato** que no
    queda grabado en el command buffer, o sea frágil ante cualquier cambio de orden. Los 4 blits son
    unsafe passes creados por `AddBlitPass`, que ya se queda con su `SetRenderFunc`: no hay dónde
    colgar el seteo, de ahí el pass dedicado.
    **Efecto lateral al diagnosticar:** `Shader.GetGlobalVector("_VisionPxPerDeg")` desde C# ya **no**
    devuelve el valor vigente (los globals de command buffer no se reflejan en la tabla inmediata).
    Para verificar el ppd hay que recalcular la fórmula en C# o mirar el efecto en una captura.
  - **Blindaje MultiPass:** `GetProjectionMatrix(1)` va a `XRPass.GetProjMatrix(1)`, que en **MultiPass**
    indexaría `m_Views[1]` de una lista de UNA vista ⇒ excepción dentro de `RecordRenderGraph`. Se
    consulta `desc.volumeDepth > 1` (el target con dos slices es el único caso donde el shader lee la
    componente `.y`, `unity_StereoEyeIndex == 1`); con una sola vista, la vista 0 YA es el ojo que se
    está renderizando, así que replicar `m11` de la vista 0 es lo correcto. `Mathf.Abs` sobre `m11`
    porque un `radiusPx` negativo daría `sharpW = 1` y **apagaría el blur en silencio**.
  - `SetGlobalTextureAfterPass(lowB, _VisionLowBlur)` en el builder del blit del gather +
    `UseGlobalTexture(_VisionLowBlur, Read)` en el del pass 0 (los dos con
    `AddBlitPass(..., returnBuilder: true)` dentro de un `using`, porque el `Dispose` del builder es
    lo que registra el global y cierra el pass). El `UseGlobalTexture` genera la dependencia
    read-after-write que **garantiza el orden**, y RenderGraph emite el `cmd.SetGlobalTexture` al
    terminar el pass que lo setea. Verificado: funciona en un pass **unsafe** (que es lo que
    `AddBlitPass` con material crea), sin excepciones ni warnings de consola.
  - **Ojo:** los globals solo se publican cuando el gate está ON (si no, `RecordRenderGraph` no
    corre); no importa porque el único consumidor es el pass que el gate saltea, y el valor se
    publica ANTES de que ese graph se ejecute.
  - **Costo:** los 2 blits del tier de baja se pagan SIEMPRE que el gate esté ON, incluso si ningún
    píxel del frame tiene radio > `TIER_LO_PX` (el shader no puede decidirlo en CPU: el radio depende
    del depth per-pixel). ~~Es la primera palanca a revisar~~ — **corregido por el dossier de
    performance (F0): el pass 0 full-res es el ~82 % del coste, no el tier.** Además el pass 3 ya
    tiene early-out per-pixel del caso degenerado (F1, ver §Coste), así que el blit 2 se sigue
    pagando entero pero el 3 se abarata solo. Orden de palancas vigente: §Coste.
- `Assets/Scripts/Runtime/Vision/VisionActivity.cs` — estado agregado "hay efecto" por ojo para el
  gate. Lo escriben (con estado C# ya conocido, NO leyendo el material): `VisionParamsBinder`
  (`ParamsL/R = max(desenfoque_max, contrast_loss, cataract_yellow, cataract_scatter)`),
  `GlareController` (`AstigL/R`),
  `DisabilityGlareController` (`VeilL/R` = velo **suavizado** actual, no el target). `AnyActive` es
  el OR con epsilon 0.001 sobre los 6 campos. Criterio conservador: `desenfoque_max > 0` mantiene el
  pass aunque todo esté en foco (no se sabe per-pixel sin correr el shader). Sin histéresis: el velo
  es continuo (suavizado exponencial), no titila al cruzar el epsilon. Un
  `[RuntimeInitializeOnLoadMethod(SubsystemRegistration)]` resetea los 6 estáticos a 0 al entrar a
  Play (simetría con `GlareBillboardInstance.ResetRegistry`): en fast-enter-playmode sin domain
  reload los estáticos conservarían el último valor de la sesión previa y el gate arrancaría ON
  espurio hasta que los binders repongan su estado real.
- `Assets/Shaders/VisionPostProcess.shader` — el post-proceso en sí (ver fórmulas abajo).
- `Assets/Scripts/Runtime/Vision/VisionStateBinder.cs` — **clase base abstracta (P6.1)** de los tres
  suscriptores de `DataManager.VisionStateChanged`. Centraliza el ciclo de vida que triplicaban:
  coroutine `Start` que espera al singleton `DataManager`, suscripción al evento, despacho inicial
  por ojo (`ApplyEyeState("left"/"right", …)`) y desuscripción en `OnDisable`. Las subclases sólo
  implementan `protected abstract ApplyEyeState(eye, state)` (su mapeo específico). Dos hooks
  virtuales preservan las diferencias sutiles SIN homogeneizar: `OnAfterInitialDispatch()` (trabajo
  extra dentro de la MISMA coroutine tras el despacho inicial — lo usa `VisionParamsBinder` para el
  blend demo opt-in que espera al catálogo) y `OnBinderDisable()` (limpieza extra en `OnDisable` — lo
  usa `DisabilityGlareController` para resetear `_GlareVeilL/R` + `VisionActivity.Veil*`).
  `GlareController` conserva su propio `OnEnable` (publica los umbrales de facing), independiente de
  este ciclo de vida, por lo que no necesita hook. Los tres MonoBehaviours mantienen su nombre de
  clase (referencias serializadas en `Main.unity`).
- `Assets/Scripts/Runtime/Vision/VisionParamsBinder.cs` — puente DataManager→material (hereda
  `VisionStateBinder`). Mapea claves
  del catálogo a uniforms por ojo: `foco_lejos_m→_FocoLejosL/_FocoLejosR`,
  `foco_intermedio_m→_FocoIntermedioL/R`, `foco_cerca_m→_FocoCercaL/R`,
  `profundidad_foco_m→_ProfundidadFocoL/R`, `desenfoque_max→_DesenfoqueMaxL/R`,
  `contrast_loss→_ContrastLossL/R`, `cataract_yellow→_CataractL/R` (tinte amarillo de catarata,
  ver §Post-proceso), `cataract_scatter→_CataractScatterL/R` (dispersión intraocular, ver
  §cataract_scatter). Además aplica un blend demo al arrancar
  (`applyDemoBlendOnStart`: monofocal OI / panoptix OD).
  **Gate (C2.2 + P-optica-B):** `cataract_yellow` y `cataract_scatter` entran al agregado
  `VisionActivity.ParamsL/R` junto con `desenfoque_max` y `contrast_loss` (`max` de los cuatro).
  Sin esto, con blur y contraste en 0 el gate de CPU apagaría el pass y el tinte/velo desaparecerían
  (son efectos uniformes por ojo del pass 1). `cataract_scatter` es el más crítico: es el único que
  degrada la visión SIN depender de la distancia ni de una fuente de glare en el campo, así que
  nada más lo "activaría". Se lee con `TryGetValue` (catálogos viejos sin la clave no rompen).
- `Assets/Scripts/Runtime/Vision/GlareController.cs` — DataManager→shader globals de los billboards
  (hereda `VisionStateBinder`; `ApplyEyeState` delega en `SetEyeGlobals`):
  `halo_intensity→glare_halo_l/r`, `halo_extra_rings→glare_pupil_l/r` (en mm 1–6 desde v0.6.0; se normaliza `(v-1)/5` a 0–1 acá antes de publicar — ver `docs/catalogo-lentes.md`),
  `destello_intensity→glare_star_l/r`, `destello_rayos→glare_rays_l/r`. Escala por escenario:
  halos × `haloScale`, destellos × `starScale`, `destello_rayos` (cantidad) nunca se escala.
  **`cataract_yellow→glare_cataract_l/r` (v0.9.1):** transmitancia ámbar del cristalino para que los
  halos de los faros NO salgan blancos sobre una escena ámbar (ver §Tinte amarillo de catarata). NO
  se escala por `haloScale` ni se apaga con `halosEnabled` (es un filtro, no un halo) y si la clave
  falta se publica **0** (nunca el valor de la lente anterior).
  **Astigmatismo del catálogo (P4.4):** `SetEyeGlobals` también lee `astig_magnitude` (0..1) y
  `astig_axis_deg` (grados→radianes) del estado del ojo y los publica por el MISMO camino per-eye
  que el comando live, llamando `SetAstigmatism` (independiente de `halosEnabled`: el astigmatismo es
  un defecto óptico, no un halo). Expone `SetAstigmatism(eye, enabled, magnitudNorm 0..1, ánguloRad)`
  con `eye ∈ "left"|"right"|"both"` (misma convención que `DataManager.OverrideParams`) → globals
  PER-EYE `glare_astig_l/r` y `glare_astig_angle_l/r` (patrón `glare_*_l/r`) + `VisionActivity.AstigL/R`
  (gate de CPU); cada global es el estado por ojo, independiente del otro. Los consumen ambos shaders
  (billboards y post-proceso). Publica los umbrales de facing (`_GlareFacingLo/Hi`) en **`OnEnable`**
  (no en `Start`): `Start` es una coroutine que espera a `DataManager`, y hasta que resolvía, el
  billboard hacía `smoothstep(0,0,·)` degenerado los primeros frames (ver gotcha).
- `Assets/Shaders/GlareBillboard.shader` — halo + starburst + trazo astigmático procedurales sobre
  un quad que sigue a la cámara con tamaño angular constante. Aditivo (`Blend One One`),
  `ZTest LEqual` (se ocluye tras geometría). Constantes angulares en radianes:
  `HALO_ANG_RADIUS 0.10`, `PUPIL_GAIN 1.7`, `STAR_ANG_RADIUS 0.22`, `ASTIG_ANG_RADIUS 0.12`,
  `ASTIG_WIDTH 0.02`, `ASTIG_GAIN 2.2`, `DIST_REF_M 8.0`, `TOWARD_CAM_FRAC 0.10`. El halo lleva
  glow gaussiano + 3 anillos difractivos concéntricos (a r normalizado 0.45 / 0.68 / 0.90) cuyo
  peso escala ~`v_halo²` (una monofocal casi no muestra anillos). Fade por distancia:
  `v_fade = saturate(src_energy · DIST_REF_M / dist) · facing`.
  **Filtro ámbar de catarata (v0.9.1):** el color emitido se multiplica por
  `lerp(1, CATARACT_YELLOW, v_cataract)` con `v_cataract` = `glare_cataract_l/r` per-ojo (resuelto en
  el vertex, viaja en `p2.w`). Es la ÚNICA parte del patrón clínico que este shader tiene y
  `WindowPortal.shader` **no** — a propósito, porque el portal es opaco y ya pasa por el
  post-proceso (ver §Tinte amarillo de catarata; los dos archivos llevan el comentario cruzado).
  **Clip de esquinas (F1 del plan de FPS, perf pura y bit-exacta):** el Frag arranca con
  `clip(0.98 - r)` apenas `r = length(uv*2-1)` está disponible. El quad es CUADRADO (`r` llega a
  `sqrt(2) = 1.414` en las esquinas) pero el patrón es CIRCULAR: `edge_fade = 1 − smoothstep(0.80,
  0.98, r)` vale **exactamente 0** para todo `r ≥ 0.98` y el color emitido es
  `col · (total · v_fade · edge_fade)`, así que esos fragmentos aportan `(0,0,0)` y con `Blend One One`
  no suman NADA. Descartarlos ahorra el resto del Frag (el `atan2` del starburst, cuatro `exp` del
  halo, dos `hash11`, el seno/coseno del astig). El umbral **0.98 es el mismo borde del `smoothstep`**,
  no un valor nuevo: si se recalibra `edge_fade`, hay que mover el clip con él. En `r = 0.98` exacto
  `clip(0)` NO descarta y `edge_fade` ya vale 0 ⇒ el borde del halo no puede mostrar un corte.
  **Verificado:** par ANTES/DESPUÉS en `ruta_noche` con halos fuertes queda **exactamente en el piso
  de la metodología** (24 / 18 / 47 px distintos en tres encuadres, los MISMOS valores que el piso
  NEW-vs-NEW del mismo bloque). Control POSITIVO de que el test no es vacío: mover el clip a
  `r > 0.30` cambia 42 060 px con maxd 100. Cobertura real y por qué el ahorro es CHICO: ver §Coste.
  **Alfa:** el fragmento escribía `alpha = 1.0` aditivo también en las esquinas y eso deja de pasar.
  No lo consume nadie (el color HDR del visor es **B10G11R11, sin canal alfa**; el stream de la tablet
  se decodifica a `RGB24` — ver `StreamingCapture`/`TabletController`).
  **`WindowPortal.shader` NO lleva el clip** (y lleva un comentario cruzado explicando por qué): es
  OPACO con early-Z y su "quad" es el portal/backdrop entero, que afuera del patrón del sol todavía
  tiene que escribir el PAISAJE — un clip ahí agujerearía la ventana y desactivaría el early-Z del
  draw. Su recorte equivalente es el `if (rNorm < 1.05)`, que es un branch, no un descarte.
  **Gotcha (tamaño angular fijo):** la ESCALA del transform del GameObject (billboards de faros/
  faroles de `ruta_noche`; los del sol `SunGlare`/`SunGlare2` tienen el `MeshRenderer` OFF desde
  P-sol-3 y solo alimentan el velo — ver `WindowPortal.shader`/`SunSkyAnchor`)
  NO afecta el tamaño del halo/starburst. El shader reconstruye el quad en el vertex desde las
  constantes angulares (`HALO_ANG_RADIUS`, `STAR_ANG_RADIUS`, …) a una distancia fija de cámara, así
  que el tamaño en pantalla es puramente angular e ignora el `localScale` del GO. Para agrandar/achicar
  el patrón se tocan esas constantes — en `GlareBillboard.shader` para las fuentes billboard
  (ruta_noche), y en `WindowPortal.shader` para el SOL del consultorio (copia verbatim desde
  P-sol-3, ver DEUDA en el bloque del portal) —, no el transform.
- `Assets/Scripts/Runtime/Vision/GlareSource.cs` — factoría estática: quad compartido + material
  compartido (`Simulador/GlareBillboard`) + `Attach(parent, pos, color, energy, beamDir)`.
- `Assets/Scripts/Runtime/Vision/GlareBillboardInstance.cs` — componente serializable por fuente:
  `srcColor`, `srcEnergy` (faro = 1.0, relativo), `srcDir` (dirección local del haz; 0 =
  omnidireccional), `seed`, `distanceInvariant` (sol). Reaplica todo por MaterialPropertyBlock en
  `OnEnable` y dibuja gizmos de dirección en editor. **Registro estático (3.3):** mantiene una lista
  estática `Active` (read-only) por `OnEnable`/`OnDisable` que consume `DisabilityGlareController` en
  lugar de escanear la escena; `[RuntimeInitializeOnLoadMethod(SubsystemRegistration)]` la limpia al
  arrancar Play (robusto ante fast-enter-play-mode donde los statics no se resetean).
- `Assets/Scripts/Runtime/Vision/DisabilityGlareController.cs` — encandilamiento clínico (abajo;
  hereda `VisionStateBinder`, `ApplyEyeState` delega en `ReadStray` y `OnBinderDisable` resetea el velo).
- `Assets/Scripts/Runtime/Vision/ScenarioManager.cs` — cambia consultorio ↔ ruta_noche: activa el
  root, muestra/oculta el libro, setea ambiente (día: `ambientLight (0.55,0.52,0.45)`, sol
  direccional configurado pero OFF; noche: ambiente `(0.14,0.14,0.15)`, `reflectionIntensity 0`,
  luna direccional 0.3 casi neutra sin sombras, fondo `SolidColor` casi negro), recoloca el rig XR
  y fija el TARGET de pupila (`_pupilTarget` 0 = día, 1 = noche). De día `haloScale=0.2` y
  `starScale=0.7`. **Pupila dinámica (4.6):** `Update()` interpola `_PupilScene` hacia el target con
  constantes de tiempo ASIMÉTRICAS (constricción rápida `pupilConstrictTau≈0.9 s`, redilatación lenta
  `pupilDilateTau≈3 s`; el reflejo fotomotor no es instantáneo y la dilatación es más lenta que la
  constricción [9]), más una **miosis transitoria** opcional (`glareMiosisGain`) que baja el target
  cuando hay velo intenso mirado de frente (proxy `VisionActivity.Veil`), reduciendo el desenfoque por
  efecto estenopeico. En `Start` arranca ya en el target (sin rampa al cargar). Verificado:
  1→0.012 a día (rápido), 0→0.74→~1 a noche (más lento).
  **Recentrado del paciente (`RecenterPatient()`, B1):** método público (comando `recenter` de la
  tablet; el comando WS y el botón los agrega @unity-dev) que lleva la **cámara** (el ojo) a la pose
  de diseño del escenario actual. Con `TrackingOriginMode.Device` el origen del rig queda **fijado
  por la pose del visor al arrancar** (donde estaba el paciente al ponerse el casco), así que la
  cabeza puede quedar descentrada respecto de la silla/asiento de diseño; `recenter` corrige eso.
  Algoritmo (yaw-only, **el orden importa**): (1) rota el rig alrededor de la cámara
  (`xrOrigin.RotateAround(cam.position, up, Δyaw)`) — la cámara no se mueve en este paso, solo cambia
  su orientación al yaw de diseño; (2) traslada el rig para llevar la cámara al ojo de diseño
  `originPos + up·CameraYOffset` (el offset se lee de `cam.parent.localPosition.y`, el Camera Offset;
  en modo Device su `localPosition.y == CameraYOffset`, verificado = 1.1176). **Nunca toca pitch/roll
  del rig** (mareo). **Limitación conocida:** no persiste tras `load_scenario` — `SwitchTo` reposiciona
  el rig por pose serializada, no reaplica el recentrado; hay que volver a mandar `recenter` tras un
  cambio de escenario si el paciente sigue descentrado. Verificado en play (consultorio): desde una
  pose arbitraria del rig, la cámara aterriza en `(0.274, 1.1176, -0.500)` con error 0 y yaw de
  diseño con error 0.
- `Assets/Scripts/Runtime/Vision/NightTraffic.cs` — tráfico bidireccional: instancia prefabs de
  `Assets/Prefabs/Cars` (frente del auto = +Z local) en dos carriles (`laneX=±2.6 m`); carril
  derecho se aleja (se ven pilotos), izquierdo viene de frente (faros). Tramo entre `startZ=70` y
  `endZ=-14`, `speed=16 m/s` base. `count=4` (default; ajustable, se reparte alternado entre
  carriles). Tinta solo el material llamado "Body" vía MaterialPropertyBlock. **Flujo
  aleatorizado (no determinista):** (a) **distribución inicial** por muestreo estratificado por
  carril — el tramo se divide en `laneCount` segmentos y cada auto del carril cae en uno DISTINTO
  en posición random dentro del segmento (z aleatorio sin apelotonar dos autos del mismo carril,
  reemplaza el viejo reparto uniforme por `Lerp`); (b) **wrap con gap aleatorio EXTRA**
  (`wrapGapMax=35 m`): al salir del tramo el auto reaparece MÁS ALLÁ del punto de reaparición
  (el que se aleja detrás de `endZ`, el que viene delante de `startZ`) a distancia random → tarda
  un tiempo variable en re-entrar, dejando huecos (no siempre están los `count` autos visibles);
  (c) **jitter de velocidad por auto** (`speedJitter=0.15` → ±15% de `speed`, guardado en
  `_speeds`) que rompe la periodicidad del paso. **Invariante de espaciado mínimo por carril
  (`minGap=18 m`, ~1.1 s a 16 m/s):** evita que dos autos del MISMO carril viajen/reaparezcan
  pegados (el jitter de velocidad hacía que el rápido alcanzara al lento a lo largo del tramo de
  84 m; el wrap con `Random.Range(0, wrapGapMax)` podía re-inyectar dos autos casi juntos). Dos
  mecanismos, ambos con el "compañero de carril" resuelto por **paridad de índice** (`for j = i%2;
  j += 2`) — no se hardcodea "2 autos/carril", funciona con cualquier `count`: **(1)** en el wrap
  (`WrapZ`) el gap es `Random.Range(minGap, max(minGap, wrapGapMax))` medido desde el compañero
  de carril MÁS REZAGADO si éste quedó fuera del tramo cerca del punto de reaparición (`min z` para
  el que se aleja, `max z` para el que viene) — empuja la reaparición detrás de él; si el compañero
  está lejos dentro del tramo, el borde (`endZ`/`startZ`) alcanza. Protege el caso degenerado
  `wrapGapMax <= minGap` con `Mathf.Max`. **(2)** en `Update` (`NearestAhead`) un **clamp de
  seguimiento**: si el auto de adelante del mismo carril está a menos de `minGap`, la velocidad se
  topa a la suya ese frame (`effSpeed = min(propia, del líder)`, tope suave sin física) para que no
  lo siga alcanzando; NO se re-sortea `_speeds[i]` (recupera su velocidad propia sola al reabrirse
  el hueco). **Re-tinte en el wrap:** el pool de GameObjects
  se recicla (no se destruye/instancia — minimal footprint), así que en cada wrap se re-randomiza
  el color de carrocería (`ApplyBodyColor` + `PickColor`, evitando repetir el color inmediato
  anterior del mismo auto vía `_colorIdx`) y se re-sortea la velocidad → cada reentrada parece un
  auto distinto. Sin esto, con `count=4` los 2 autos por carril reaparecían siempre con su color
  inicial ("en el carril derecho solo pasan azules"). El color inicial además evita repetir dentro
  del mismo carril, y el `MaterialPropertyBlock` se cachea en `_mpb` (sin alocar por re-tinte).
- `Assets/Shaders/WindowPortal.shader` — "portal" de paisaje para la ventana del consultorio
  (`Consultorio/EnlargedWindow`): lo usan los VIDRIOS del marco `WindowFrame` (modelo
  `window/WINDOW.fbx`, PVC 3 paños + alféizar; su helper `CTRL_Hole` va desactivado y su slot
  "Glass" se reemplaza por `WindowView.mat`) y el quad de respaldo `BackdropBig`
  (`EnlargedWindow_Backdrop.asset`, 3 cm detrás del marco, tapa el hueco original de la pared).
  El shader samplea el paisaje
  (`consultorio_paisaje.png`, en `WindowView.mat`) por DIRECCIÓN DE VISTA → paisaje a infinito
  con paralaje correcto en VR, no un "cuadro" plano. La imagen se mapea a un SECTOR angular
  acotado, NO a la esfera 360 (mapeo 360 dejaba ~230 px visibles por la ventana y pixelaba):
  `_HFovDeg` (120°) × `_VFovDeg` (67° = 120·alto/ancho), `_YawCenterDeg` (-40.5° = casa+molino
  centrados frente a la ventana), `_HorizonV` (0.49 = fila del horizonte, pitch 0). Import de la
  textura anti-pixelado: NPOT `None` (resolución nativa 2752×1536, sin resample a POT), SIN
  mipmaps (se samplea LOD 0), override Android ASTC 4x4, wrap Clamp. XR: `GetCameraPositionWS()`
  (por-ojo en single-pass instanced). Skybox de la escena: `ConsultorioSkybox.mat`
  (Skybox/Panoramic, misma textura; en la práctica no se ve — la ventana lo tapa y de noche la
  cámara va en SolidColor). **Sol de día anclado al cielo (P-sol):** el DISCO solar se pinta
  DENTRO de este shader por dirección de vista (mismas matemáticas que el paisaje): dado
  `_SunDirWS` (dirección del sol en el mundo, unit) se calcula la separación angular
  `ang = acos(dot(dir, _SunDirWS))` y se suma un núcleo (`_SunCoreDeg`, radio en grados, borde
  smoothstep) + glow gaussiano (`_SunGlowDeg`), coloreado por `_SunColor`×`_SunIntensity`. Así el
  sol queda a **distancia infinita** (cero paralaje al mover/trasladar la cabeza, solidario con el
  paisaje) y lo **ocluyen marco y paredes** igual que al paisaje, porque este quad es OPACO. Se
  dibuja en los DOS planos que usan `WindowView.mat` (Window glass + BackdropBig) sin doblarse: son
  opacos y coplanares, el frente tapa al fondo (nearest wins) en la misma dirección de pantalla.
  Valores actuales en `WindowView.mat`: `_SunDirWS≈(-0.415, 0.191, 0.890)` (yaw ≈ -25°, pitch ≈ +11°:
  dentro de la abertura de la ventana desde el ojo del paciente, en zona de cielo de la textura,
  uv≈(0.63, 0.65)), `_SunIntensity=5`, `_SunCoreDeg=0.35`, `_SunGlowDeg=0.8`, `_SunColor=(1,0.96,0.88)`
  (defaults del shader `WindowPortal.shader` sincronizados con estos valores — un material nuevo no
  debe nacer con el look viejo).
  **Dimensionado del disco (P-sol-2):** con esos valores la zona clippeada a blanco mide
  ~1.7° de diámetro. Regla: la contribución solar `_SunIntensity·(core + 0.6·glow)` clippea a blanco
  cuando `0.6·exp(-ang²/_SunGlowDeg²)·_SunIntensity ≥ 1` (sin tonemap/bloom en la escena) → radio de
  clip `= _SunGlowDeg·√(ln(0.6·_SunIntensity)) = 0.8·√(ln 3) ≈ 0.84°` (diámetro ~1.68°). Lo que agranda
  el sol NO es el núcleo sino el "quemado" blanco del glow saturando, así que el tamaño se controla
  bajando `_SunGlowDeg` (`_SunCoreDeg` acompaña para que el núcleo no domine el disco más chico). Se
  eligió ese ~1.7° de diámetro para que el disco sea COMPACTO y quede MUY por debajo del radio del
  starburst clínico de PanOptix (`starR≈4.41°`, ver `GlareBillboard`): así los rayos del destello
  SOBRESALEN con claridad del disco en vez de quedar lavados dentro del blanco. Historia del
  dimensionado (cada tanda a ~la mitad): valores originales `_SunIntensity=8`/`_SunGlowDeg=5.5` →
  zona blanca ~13.8° (tapaba el starburst); `_SunGlowDeg=2.0`/`_SunCoreDeg=0.6` → ~4.2°;
  `_SunGlowDeg=1.0`/`_SunCoreDeg=0.45` → ~2.1°; tanda actual `_SunGlowDeg=0.8`/`_SunCoreDeg=0.35`
  → ~1.7° (~20% más chico que la anterior). `_SunIntensity=5` se mantuvo
  (baja el disco de forma pareja y conserva un halo suave, no un círculo recortado y duro).
  Verificado en play (consultorio día): sin lente el disco es compacto y sin starburst
  (`capturas/sol_sin_lente_v3.png`); con PanOptix, apartando la mirada ~12° del sol, los
  rayos del starburst sobresalen limpios del disco chico (`capturas/sol_panoptix_starburst_v3.png`).
  Mirando de FRENTE al sol con PanOptix el núcleo (disco + halo + starburst additivos) satura y lava
  el centro: para EVIDENCIAR los rayos hay que apartar la mirada. El disco y el patrón de glare son
  fijos en dirección; el velo no.
  **Halo + starburst + astig clínicos pintados POR DIRECCIÓN dentro del portal (P-sol-3):** el
  patrón clínico del sol (halo difractivo, starburst y trazo astigmático) YA NO lo dibujan los
  billboards `SunGlare`/`SunGlare2`, sino este mismo fragmento opaco — es un **traslado 1:1** del
  Frag de `GlareBillboard.shader` (constantes angulares `HALO_ANG_RADIUS 0.10` / `STAR_ANG_RADIUS
  0.22` / `ASTIG_ANG_RADIUS 0.12`, curvas y energías **VERBATIM**, NO rediseñadas). Se reusa la misma
  dirección de vista `dir` que el paisaje/disco: la separación angular al sol `angRad = acos(dot(dir,
  sdir))` (en RADIANES, unidades del billboard) hace de `r` normalizado (`rNorm = angRad/angMax`,
  small-angle) y la proyección de `dir−sdir·⟨dir,sdir⟩` sobre el right/up de cámara
  (`UNITY_MATRIX_I_V`) hace de `p` (offset en pantalla) para el ángulo de los rayos y el eje del
  astig. Per-eye con los MISMOS globals `glare_*_l/r` + `_StreamForceEye` que setea `GlareController`
  (gating clínico intacto: sin lente los globals quedan en 0 → `angMax=0` → cero aporte). `v_fade`
  del billboard satura a 1.0 para el sol (omnidireccional, `src_energy·DIST_REF_M/dist =
  1.8·8/4.9 ≥ 1`) → se fija a 1.0. Se suman DOS aportes additivos con los seeds/colores de las dos
  fuentes que reemplaza (`SunGlare` seed 5, color HDR `(2.2,2.046,1.716)`; `SunGlare2` seed 23,
  color `(2.2,2.09,1.804)`) — dos billboards coincidentes = starburst más rico; el resultado es
  idéntico al look billboard, solo cambia DÓNDE se pinta.
  **DEUDA (drift silencioso, hallazgo de review P-sol-3):** la fórmula clínica del glare vive ahora
  DUPLICADA en dos shaders (`GlareBillboard.shader` y `WindowPortal.shader`), copiada verbatim, sin
  include compartido. Si se recalibra el patrón clínico (anillos difractivos, curvas, energías) en
  uno solo, el sol del consultorio diverge en silencio del resto de fuentes. Follow-up pendiente:
  extraer el núcleo (`SunGlareTotal`/`hash11` + constantes `*_ANG_RADIUS`/`ASTIG_*`/`PUPIL_GAIN`) a
  un `.hlsl` compartido incluido por ambos. Mientras tanto: **todo cambio al patrón clínico se aplica
  en LOS DOS shaders en la misma tarea.** **Beneficios del traslado:** (a) marco y
  paredes ocluyen halo+starburst JUNTO con el disco (antes los billboards eran quads físicos DENTRO
  de la sala a 4.9 m: si el marco tapaba el sol, los destellos seguían flotando en el aire — el
  defecto que reportó el usuario); (b) el patrón queda a **vergencia infinita por ojo** (cada ojo usa
  su `dir`), lo que **ELIMINA la disparidad binocular de ~0.74°** que tenía el transform a 4.9 m
  (ver `SunSkyAnchor`). Costo Quest: dos evaluaciones procedurales del patrón (mismo orden que los
  dos billboards que reemplaza) SOLO en los píxeles del portal ×2 ojos, con early-out `angMax>0.004`
  (uniforme) y `rNorm<1.05` (recorta el ~99% de los píxeles lejos del sol).
  **El disco había reemplazado al quad `SunCore`** (eliminado): el diseño original colgaba `SunCore`
  + los glares a ~4.9 m DELANTE del vidrio-portal, con paralaje de objeto cercano contra el paisaje a
  infinito ("el sol se veía dentro de la sala"). **Gotcha:** disco y patrón SOLO se ven donde la
  dirección `_SunDirWS` cruza el quad del portal desde el ojo actual; si se cambia el punto de vista o
  la dirección del sol, verificar que siga cayendo dentro de la abertura de la ventana (fuera de ella
  se pintan sobre… nada, no hay quad). `_SunDirWS` (material) y `SunSkyAnchor.sunDirection` DEBEN
  coincidir (disco, glare y velo a la misma dirección).
  **Gotcha (actualizado — cirugía de escala):** `Consultorio` está ahora a **scale 1** (metros
  reales); tras preservar exactas las poses de mundo, `EnlargedWindow`, `DayWindow` y `OptotipoETDRS`
  quedaron también a **scale 1** (ya no compensan el 0.37 viejo con localScale 1/0.37). El cuarto del
  FBX **sigue rotado ~62°** (las paredes NO están alineadas a los ejes del mundo — este gotcha sigue
  vigente).
- `Assets/Scripts/Runtime/Vision/SunSkyAnchor.cs` — ancla las fuentes del sol
  (`SunGlare`/`SunGlare2`, hijos del GameObject `SunSky` bajo `Consultorio/DayWindow`) a una
  DIRECCIÓN de cielo fija en el mundo: cada `LateUpdate` reposiciona el objeto a
  `camPos + sunDirection·distance` (`distance=4.9 m`). `sunDirection` debe coincidir con `_SunDirWS`
  de `WindowView.mat`.
  **Rol remanente tras P-sol-3:** el halo/starburst visible se pinta ahora POR DIRECCIÓN en el portal
  (ver `WindowPortal.shader`), así que los **`MeshRenderer` de `SunGlare`/`SunGlare2` están
  DESHABILITADOS** (no dibujan quad) — pero sus GameObjects y sus componentes `GlareBillboardInstance`
  siguen **ACTIVOS y registrados** en `GlareBillboardInstance.Active`. El ÚNICO propósito que le queda
  a `SunSkyAnchor` es mantener esa `transform.position` sobre la dirección fija del sol para que
  `DisabilityGlareController` (que lee `transform.position` de cada fuente activa) calcule θ del velo
  CIE contra una dirección fija = fuente al infinito. El **velo NO se tocó** y funciona idéntico.
  (`distance=4.9 m` ya no importa para oclusión de un quad — no hay quad — pero se conserva; el velo
  usa `distanceInvariant=true`, así que la distancia no atenúa.)
  **Gotcha RESUELTO (disparidad estéreo, antes limitación aceptada):** cuando el halo/starburst era
  billboard colgado de un transform único a 4.9 m (MISMA posición de mundo para ambos ojos), conservaba
  una disparidad binocular de `IPD/distance = 0.063/4.9 ≈ 0.74°` frente al disco (que sí estaba a
  vergencia infinita). Con P-sol-3 el patrón también se pinta por dirección de vista dentro del portal
  (cada ojo usa su `dir`) → disco Y halo/starburst quedan a la MISMA vergencia infinita, disparidad
  binocular = 0. Ya no queda desajuste de profundidad entre disco y halo.
- `Assets/Scripts/Runtime/Vision/BookHolder.cs` — mide distancia libro→cámara (suavizada) y la pasa
  al material como `_BookDistanceM` + máscara en pantalla `_BookScreenUV` / `_BookScreenRadius`
  (radio angular real del libro × 1.45, clamp 0.06..0.45). El shader usa esa distancia dentro de
  la máscara porque el depth del libro en la mano no es confiable. **En escena
  (`XR Origin (VR)/Camera Offset/Right Controller/ReadingBook`) `bookHalfMeters = 0.16`**, no el
  `0.14` del default del código: el lado mayor del libro mide 32.8 cm (mesh × `lossyScale 0.02`).
  `book` apunta al propio `ReadingBook`, y el libro cuelga RÍGIDO del `Right Controller` (sin
  `XRGrabInteractable`) ⇒ **no hay piso de distancia**: el usuario puede pegárselo a la cara, y
  validar cualquier cosa del libro exige play mode con el Game view enfocado (si no, `LateUpdate` no
  publica la máscara y una captura miente).
  - **TRAMPA de nombres (costó una medición mal hecha, ver §Consecuencia clínica):** el POEMA está en
    el mesh **`Book_Diary`** (material `Book_Diary`, textura **`Book_Diary_Tapa_Albedo.png`** — el
    nombre miente). **`Book_Diary_Tapa` es la TAPA** (textura `xx.png`: fondo bordó, "imagine" y dos
    triángulos dorados) y `Book_Diary_Pages` es el CANTO de las hojas (textura de madera). Los tres
    materiales usan `Shader Graphs/glTF-pbrMetallicRoughness` con **`renderQueue = 2000`** ⇒ están en
    el rango opaco y el post-proceso SÍ los alcanza (no arrastran el bug del optotipo).
- `Assets/Scripts/Runtime/Vision/SimuladorInput.cs` — mandos Quest (acciones creadas en código):
  A = cicla lente ojo izquierdo, B = ojo derecho, X = toggle halos, Y = cambia de escenario.
  **Los CUATRO atajos exigen dispositivo ADMINISTRADOR** (helper privado `AdminGate`): en sesión
  clínica ni el paciente ni el médico deben poder cambiar lente, halos o escenario por accidente
  con el mando. La fuente es el flag `is_admin` del `POST /api/verify`, que
  `LicenseManager.IsAdmin` ya expone (ver `docs/licenciamiento.md` §P7) — no se agregó ningún
  dato nuevo al contrato. **Falla CERRADO a propósito**: sin cache, sin red, con cache pre-P7 o
  contra un backend viejo, `IsAdmin` es `false` y los atajos quedan inhibidos.
  - **Gotcha — por qué el gate va DENTRO de los handlers y no en `enabled`:**
    `LicenseBlockScreenVR` y `UpdatePromptVR` ya se disputan `SimuladorInput.enabled` con guards
    anti-restore cruzados (ver sus docstrings); una tercera mano sobre ese flag rompe los
    carteles de licencia/update. Los botones propios de esos carteles son `InputAction`
    independientes, no pasan por este gate y siguen funcionando siempre.
  - **El control por TABLET no está afectado**: entra por `NetworkController.OnTextReceived`
    (`apply_lens`/`load_scenario`/`recenter`), un camino de código separado que no comparte
    origen con `SimuladorInput` — solo el destino (`DataManager`/`GlareController`/
    `ScenarioManager`). Ver `docs/networking.md`.
- `Assets/Scripts/Runtime/Vision/HudController.cs` — HUD world-space anclado a la cámara: FPS,
  escenario, lente por ojo (**convención clínica: `OD (B)` primero, `OI (A)` después** — solo orden
  de presentación; el mapeo de botones no cambia: A cicla OI, B cicla OD), estado de halos (UI
  legacy, sin TMP). **La lente se muestra por su NOMBRE legible** (`LensDef.Nombre`, la clave
  `"nombre"` que le pone el admin en `lentes.json`, ej. "PanOptix Trifocal"), NO por el `LensId`
  crudo: el helper `LensLabel(dm, lensId)` resuelve el nombre vía `DataManager.GetLens(lensId)?.Nombre`
  y hace **fallback al id crudo** si la lente ya no está en el catálogo (borrada pero todavía
  aplicada al ojo — caso borde real, `Refresh()` corre cada ~0.4 s) o si el nombre viniera vacío;
  id vacío sigue mostrándose como "-". `EyeState` NO lleva el nombre (es contrato de red, ver
  `docs/networking.md`): la resolución id→nombre es un lookup local del HUD contra el catálogo del
  `DataManager`. Última línea de emparejamiento:
  sin tablet autenticada muestra `PIN tablet: NNNNNN` (de `NetworkController.Instance.PairingPin`)
  para que el clínico lo tipee en la tablet; con al menos una autenticada
  (`NetworkController.AuthenticatedClientCount > 0`) pasa a `Tablet conectada` y deja de exponer el
  PIN. Sin `NetworkController.Instance` (escenas/momentos sin red) no dibuja la línea.
  Mostrar/ocultar el HUD completo desde la tablet (comando `set_hud`, ver `docs/networking.md`) se
  resuelve desde `Net/NetworkController` con `gameObject.SetActive` sobre este componente —
  `HudController.cs` en sí no cambió, no sabe que existe ese comando.
  **Textos vía `L10n` (es/en, Fase D3 — ver `docs/localizacion.md`)**: todo fragmento visible sale
  de una clave `hud.*` (`hud.fps`, `hud.scenario`, `hud.eye_od`, `hud.eye_os`, `hud.halos`,
  `hud.change_scenario`, `hud.tablet_connected`, `hud.pairing_pin`, `hud.halo_on`/`hud.halo_off`),
  y el escenario se traduce **por id** con las MISMAS claves `scenario.<id>` que usa la tablet
  (`L10n.Has(key) ? L10n.T(key) : id crudo` — un escenario nuevo sin traducción todavía degrada al
  id, no muestra la clave). En inglés el ojo izquierdo es **`OS (A)`** (oculus sinister), nunca
  "OI" — ver el glosario clínico de `docs/localizacion.md`. El FPS se formatea con
  `InvariantCulture` ANTES de entrar al placeholder para que la clave sea un `{0}` pelado en ambos
  idiomas (mismo criterio que `ParamMeta.FormatValue`). Los `Debug.Log` del sistema siguen en
  español (son diagnóstico de desarrollo, no UI).
- `Assets/Scripts/Runtime/Vision/GlareTestRig.cs` — rig de verificación: baja la luz y spawnea 3
  lámparas emisivas con billboards de glare al frente, a altura de ojos.

### Post-proceso: modelo dióptrico (VisionPostProcess, CUATRO passes)

El efecto está partido para que el astigmatismo (cilindro) opere sobre la imagen ya
desenfocada por el defocus (esfera) — correctitud óptica: el ojo aplica esfera+cilindro como una
PSF combinada, no el cilindro sobre la imagen nítida. Los índices de pass **0 y 1 no se mueven**
(los referencia `VisionRendererFeature`); los del tier de baja resolución van al final del
`SubShader`:

| # blit | pass | src → dst | res | taps |
|---|---|---|---|---|
| 1 | **2** `FragLowDown` | `source → _VisionLowA` | 1/16 | 4 color (box 4×4) |
| 2 | **3** `FragLowGather` | `_VisionLowA → _VisionLowB` | 1/16 | 24 color + **25** `BlurRadiusDeg` (centro + 24 taps) — **1 + 1 con el early-out `g = 0`** |
| 3 | **0** `FragEffect` | `source → _VisionTemp` | full | 13 disco + 4 low + 1 base + 1 depth |
| 4 | **1** `FragPost` | `_VisionTemp → source` | full | 1 (7 con astig) |

**Pass 0 — esfera (defocus + scatter), `source→temp`.** Toda la óptica del radio vive en
**`BlurRadiusDeg(uv, eyeIdx)`** dentro del `HLSLINCLUDE` — **fuente única**, no duplicar: la llaman
el pass 0 (una vez por píxel full-res) Y el pass 3 (una vez por píxel de baja + una por cada tap del
gather); si las dos fórmulas divergen el composite entre tiers rompe, que es la deuda que ya
arrastran `GlareBillboard`/`WindowPortal`:
1. `depth → metros`: `SampleSceneDepth` + `ComputeWorldSpacePosition` → distancia radial a cámara.
   Dentro de la máscara del libro se usa `_BookDistanceM` (CPU) en vez del depth.
2. `Diopters(d) = 1 / max(d, 0.05)` (dioptrías = 1/metros, clamp a 5 cm).
3. Error de enfoque: `errD = min(|D(d) − D(foco)|)` sobre los focos ACTIVOS (foco = 0 ⇒ no usado).
   **Si NINGÚN foco está definido (los 3 ≤ 0.001) devuelve 0, no el centinela `1e9`.** La UI de la
   tablet muestra "off" para esos valores, así que es un estado alcanzable con una lente
   custom/admin; sin la guarda el `1e9` se propagaba a `over` y el radio quedaba clampeado a
   `MAX_BLUR_DEG` ⇒ **pantalla entera al peor radio posible** (hallazgo M8 de review). Sin foco
   definido ⇒ sin defocus, no defocus infinito.
4. Tolerancia y exceso: `over = max(errD − profundidad_foco_m × DOF_M_TO_D(0.5), 0)` [D].
5. **Radio del círculo de desenfoque, en GRADOS** (magnitud angular, NO píxeles):
   ```
   pupilM     = lerp(PUPIL_DAY_MM 3.0, PUPIL_NIGHT_MM 4.0, saturate(_PupilScene)) · 1e-3   [m]
   defocusDeg = 0.5 · pupilM · over · (180/π) · desenfoque_max                          [grados]
   scatterDeg = SCATTER_BLUR_DEG(0.22) · cataract_scatter²                              [grados]
   radiusDeg  = min( sqrt(defocusDeg² + scatterDeg²), MAX_BLUR_DEG(2.0) )
   radiusPx   = radiusDeg · _VisionPxPerDeg[ojo]
   ```
   `β = p · errD` es la geometría del blur circle (β = DIÁMETRO angular, radio = β/2). Ancla de
   sanidad: pupila 3 mm y 1 D de error ⇒ **10.3 arcmin de DIÁMETRO, 5.16′ de radio**, que es lo que
   devuelve la función (0.0860°). Ojo con "corregir" el `0.5` de la fórmula: duplicaría todo el blur.
   El modelo es **PARAXIAL**: el radio se expresa en grados sobre el eje visual y se convierte con un
   ppd constante, pero el ppd real de una proyección en perspectiva crece como `sec²θ` (~+33 % a 30°
   del eje) ⇒ el desenfoque queda algo SUBestimado en la periferia. Aceptado: lo clínicamente
   relevante (optotipo, libro, cartel) está a pocos grados del eje. **Excepción medida: el tablero de
   `ruta_noche`** — ver §Pendientes / deuda, "Error paraxial `sec²θ` en el tablero".
   - **Suma en CUADRATURA** (no `max` ni suma lineal): son dos PSF independientes ⇒ se suman las
     varianzas. Además es C¹ (sin codo ⇒ sin popping) y el piso de scatter nunca REDUCE el defocus.
   - `desenfoque_max` multiplica **solo** el término dióptrico (es param de la LIO; el scatter es del
     cristalino) y ya **no es un cap 0..1 sino un MULTIPLICADOR del radio físico**: 1 = óptica real,
     >1 exageración deliberada, 0 = nunca borroso. Por eso desaparecieron los dos `saturate` y el
     fudge `blur ×= lerp(1, 1.35, _PupilScene)`: **la pupila entra ahora por la física**.
6. **`DiscBlur13`** (full-res): centro + anillo interno de 4 a 0.5r (ejes) + anillo externo de 8 a
   1.0r (ejes + diagonales). **Pesos por área de verdad**: con taps a radio normalizado 0 / 0.5 / 1.0
   las fronteras equidistantes caen en 0.25 y 0.75 ⇒ `0.0625 + 4·0.125 + 8·0.0546875 = 1.0`.
   - **Reemplaza a `DiscBlur9` (eliminado), que era un bug perceptual doble** (hallazgos M1 y MENOR
     de review): (a) sus pesos ponían **0.50 en el anillo externo** — por área serían ≈0.075/0.13/0.10
     — o sea una **PSF tipo dona**, que es lo que producía el "relieve hueco" de los glifos y del
     "18 °C" del cartel; (b) su anillo externo eran 4 taps a 90° ⇒ separación `1.41·r`, limpio solo
     hasta `r ≈ 1.4 px`, y el modelo genera 20+ px ⇒ el percepto era **poliopía** (glifos repetidos),
     no desenfoque.
   - **Límite de validez:** un kernel de taps discretos produce un disco sin huecos mientras la
     separación entre taps vecinos sea del orden del footprint bilineal (~1.5–2 px). La separación
     crítica es la del anillo externo: 8 taps a radio r ⇒ `2·r·sin(22.5°) = 0.765·r` ⇒ limpio hasta
     `r ≈ 2.6 px`. **`MAX_BLUR_DEG` NO acota este artefacto** porque el tope está en GRADOS y el
     artefacto depende de PÍXELES: el que lo acota es el cruce de tiers. Además el pass 0 pasa
     `min(radiusPx, TIER_HI_PX)` como clamp explícito en PÍXELES.
   - **El cruce NO lo elimina, solo lo atenúa** (hallazgo MENOR de review: antes decía "a
     `TIER_HI_PX` = 6 ya dio peso 0", cierto **solo en 6.0 exacto**). Pesos reales del disco en el
     medio del cruce: a **4.5 px** pesa **0.39**, con el anillo externo separado 3.44 px (límite
     limpio 2.6) ⇒ 8 copias de **2.2 %** cada una, y el anillo **interno**, que es el peor, 4 copias
     a 3.18 px con **4.9 %** cada una. A 5.0 px el disco pesa 0.21 ⇒ 1.1 % / 2.6 %. Perceptualmente
     menor (el `DiscBlur9` dejaba copias al 12.5 %, que es lo que hacía legible el texto que no
     debía leerse), pero **no es cero**: es la mitad "optimista" del hueco de calidad de abajo.
7. **Cruce de TIERS: `lowW = smoothstep(TIER_LO_PX 2.5, TIER_HI_PX 6.0, radiusPx)`** — mezcla de dos
   imágenes **BORROSAS**, nunca con la nítida. Por debajo de 2.5 px manda el disco full-res (exacto);
   por encima de 6 px manda el gather a 1/16 y el disco **no se evalúa** (13 taps de menos en los
   píxeles más borrosos: el tier también es una optimización). El tier es exacto de **6.3 px** hacia
   arriba; justo en el borde superior del cruce (6.0–6.3 px) entrega su piso, o sea hasta **+5 %**.

   **HUECO DE CALIDAD 2.6–6.3 px — EXISTE Y ES ESTRUCTURAL** (hallazgo N1 de review; la versión
   anterior de esta sección afirmaba que los dos tiers se solapaban y era **falso**). Los dos
   kernels **no se solapan**: el disco es limpio hasta ~2.6 px y **el tier no puede bajar de
   ~6.3 px** (piso de varianza, ver `LOW_PSF_VAR` en el pass 3). En la banda del medio **ninguno de
   los dos es correcto** y el `smoothstep` solo elige QUÉ error se paga.

   **Criterio de la elección (clínico, no estético):** se paga **sobre-borroneo** y no fantasmas.
   El sobre-borroneo es un sesgo **cuantitativo, monótono, medible y PESIMISTA** (el paciente ve
   algo peor de lo que el modelo pide); las copias fantasma son un error **cualitativo y OPTIMISTA**
   (preservan el trazo de alta frecuencia ⇒ hacen legible texto que no debería leerse — es
   exactamente el bug de `DiscBlur9` de la etapa B, ver §medición del libro). Por eso `TIER_LO_PX`
   se deja **bajo** (el disco pierde peso rápido) en vez de subirlo a 6.

   **Sesgo residual medido** (radio efectivo compuesto vs pedido, proxy por varianzas
   `R_eq = sqrt((1−lowW)·R² + lowW·R_tier²)`, con `R_tier = max(R, 6.32)`):

   | R pedido | antes de N1 | con N1 | caso clínico |
   |---|---|---|---|
   | 2.50 px | 2.50 (+0 %) | 2.50 (+0 %) | `lowW = 0`, disco puro |
   | 3.80 px | 5.03 (**+32 %**) | 4.73 (**+25 %**) | libro 40 cm @ ppd 24 |
   | 4.50 px | 6.42 (**+43 %**) | 5.67 (**+26 %**) | peor punto de la banda |
   | 6.24 px | 8.42 (**+35 %**) | 6.32 (**+1.3 %**) | libro 25 cm @ ppd 24 |
   | 7.13 px | 9.24 (**+30 %**) | 7.13 (**+0 %**) | optotipo `scatter 0.6` @ ppd 90 |
   | 10.58 px | 12.13 (**+15 %**) | 10.58 (**+0 %**) | libro 15 cm @ ppd 24 |
   | ≥ 6.32 px | +3 % a +35 % | **EXACTO** | todo el régimen `lowW = 1` |

   O sea: contabilizar el piso completo deja **exacto todo el régimen del tier puro** y baja el peor
   caso de la banda de +43 % a **+26 %** (≤ **0.10 logMAR** de sesgo pesimista, y solo en 2.6–6 px).

   **Verificado con capturas encuadre-idénticas** (mismo script de pose, `monofocal` de PROD,
   consultorio día, ppd 24; el par se tomó cambiando SOLO el `#define`, con re-push de params tras
   cada reimport). Métrica: RMS del gradiente = cuánto detalle sobrevive (más alto = menos borroso):

   | distancia | radio pedido | régimen | antes | después | Δ detalle | captura |
   |---|---|---|---|---|---|---|
   | 60 cm | 2.44 px | `lowW = 0`, disco puro | 9.079 | 9.088 | **+0.1 %** | — |
   | 40 cm | 3.80 px | cruce, `lowW = 0.31` | 5.267 | 5.550 | **+5.4 %** | `N1_par_libro_monofocal_40cm_ppd24_ANTESvsDESPUES.png` |
   | 25 cm | 6.24 px | `lowW = 1` | 2.159 | 3.397 | **+57.3 %** | `N1_par_libro_monofocal_25cm_ppd24_ANTESvsDESPUES.png` |
   | 15 cm | 10.58 px | `lowW = 1` | 1.445 | 1.648 | **+14.1 %** | `N1_par_libro_monofocal_15cm_ppd24_ANTESvsDESPUES.png` |

   El patrón medido reproduce la tabla predicha punto por punto: **cero cambio en disco puro**,
   cambio máximo justo en 25 cm (donde el error predicho era el mayor, +35 % → +1.3 %), y +14 % a
   15 cm (predicho +15 % → 0 %). Es la confirmación de que el descuento nuevo hace lo que dice la
   aritmética. **El veredicto clínico no se movió:** a 25 cm el texto sigue ilegible en las dos
   ramas (ver el par) ⇒ la aceptación de Bug 2 **sobrevive** a quitarle el sesgo de ancho, que era
   justamente la duda de review.
   Frames completos sin recortar: `capturas/N1_{antes,despues}_full_libro_monofocal_*cm_ppd24.png`.

   **Por qué no se cierra del todo** (opciones evaluadas, con números):
   - Subir `TIER_LO_PX` a ~6 con el disco actual: a 6 px el anillo interno deja 4 copias al 12.5 %
     separadas 4.24 px ⇒ **poliopía franca**. Peor que el sesgo. Rechazado.
   - Subir `TIER_LO_PX` a ~6 **agrandando el disco**: para separación ≤ 2 px a 6 px de radio el
     anillo externo necesita 19 taps (`2·6·sin(π/N) ≤ 2`) más los internos ⇒ **~38 taps full-res**.
     Y el peor caso no es raro: la catarata de PROD de noche deja **la sala entera a ~2.5 px**, o sea
     38 taps a pantalla completa (~19 Gtex/s en Quest 3) con la **captura L todavía pendiente**.
     Rechazado por presupuesto no medido.
   - Disco intermedio de **25 taps** (limpio a ~4.1 px, rings 0/⅓/⅔/1 con 1+4+8+12): bajaría el peor
     caso a **~+10 %** por **+12 taps/px** a pantalla completa en esa misma config. Es la mejor
     relación de las tres ⇒ **primera palanca a considerar DESPUÉS de la captura L**, no antes.
   - Bajar el piso con upsample de **1 tap** en vez del tent: 6.32 → **5.16 px** (el tent aporta
     0.375 de los 0.625). **No cierra el hueco** y reintroduce el bloqueo de 4 px que el tent existe
     para tapar. Rechazado.
   - **Tier intermedio a 1/4** (÷2 por eje, piso **3.16 px**): sí cerraría el hueco, pero son 2
     passes más y ~13 taps-equiv/px/ojo **siempre que el gate esté ON** (4× el tier actual).
     Rechazado con la captura L pendiente.
8. **Cero passthrough nítido.** La fuerza del desenfoque la da el RADIO. El viejo
   `lerp(base, blurred, blurAmount)` con `desenfoque_max = 0.79` dejaba pasar el **21 % de imagen
   nítida**, y eso era justo lo que salvaba los bordes de alto contraste del texto (bug reportado:
   "el libro pegado se lee").
   `sharpW = 1 − smoothstep(SUBPIXEL_LO_PX 0.15, SUBPIXEL_HI_PX 0.45, radiusPx)`;
   `color = lerp(blurred, base, sharpW)`.
   **`sharpW` NO es una mezcla óptica: es un EARLY-OUT de 13 taps.** `DiscBlur13` con radio → 0
   converge EXACTAMENTE a `base` (sus 13 pesos suman 1 y todos los taps caen sobre `uv`), así que por
   debajo de medio píxel las dos ramas dan el mismo resultado y la mezcla no agrega ni quita
   información. Por eso la ventana puede estar tan abajo, y por eso **cualquier píxel con radio
   ≥ 0.45 px lleva peso 0 de imagen nítida**. Con la ventana anterior (0.5–1.5 px) eso NO era cierto:
   a radio 1.35 px se colaba un ~6 % de imagen original *y* el umbral se convertía en un
   acoplamiento **clínico** con el `renderScale` (hallazgo M9 de review) — a `renderScale = 1.0` el
   radio de scatter 0.6 en campo lejano cae de 1.88 a 1.35 px y volvía el 6,5 % de nítida. Con
   0.15–0.45 px el margen es de 3× en el peor caso.

**Pass 2 — `FragLowDown`, `source → _VisionLowA` a 1/16 (1/4 por eje).** Box 4×4 EXACTO con solo 4
taps bilineales: cada tap se pide a ±1 texel **full-res** (`_ScreenSize.zw`) del centro del píxel de
baja, o sea exactamente sobre la esquina de un bloque 2×2 ⇒ el filtro bilineal promedia esos 4
texels; cuatro cuadrantes de 2×2 = el bloque 4×4 completo. Este box es el "piso" de desenfoque del
tier (de ahí la cota inferior de `TIER_LO_PX`).

**Pass 3 — `FragLowGather`, `_VisionLowA → _VisionLowB` a 1/16.** Gather de **radio variable por
píxel**: espiral de `LOW_TAPS = 24` taps de ÁREA UNIFORME (`r_i = sqrt((i+0.5)/N)`, incremento de
ángulo dorado `GOLDEN_ANG`), escalada al radio de ese píxel, con la fase rotada por un hash de
coordenadas de **PÍXEL** (sin frame counter ⇒ estable en el tiempo, no titila, y el error de muestreo
residual queda como ruido de alta frecuencia entre píxeles vecinos en vez de estructura coherente).
- `ppdLow = _VisionPxPerDeg[ojo] · _VisionLowTexel.w`. **`_ScreenSize` es un global de CÁMARA**
  (resolución full por ojo) y sigue valiendo lo mismo dentro de un pass que dibuja a 1/16: usarlo
  como texel/tamaño de la textura de baja es un **error silencioso de 4×**. De ahí `_VisionLowTexel`.
- **Descuento del PISO DE PSF en cuadratura:** `g = sqrt(max(rLow² − 4·LOW_PSF_VAR, 0))`. Un disco de
  radio `g` tiene varianza `g²/4`, así que `g²/4 + LOW_PSF_VAR = rLow²/4`.
  **`LOW_PSF_VAR = 0.625` px-de-baja² por eje**, y el nombre viejo (`LOW_BOX_VAR = 1/12`) era el
  error: contaba **solo el box** y dejaba afuera dos términos de la misma cadena (hallazgo N1 de
  review). Balance completo, todo en px de baja² por eje:

  | término | var/eje | de dónde sale |
  |---|---|---|
  | box 4×4 del pass 2 | **1/12 = 0.0833** | box de ancho 1 px de baja |
  | reconstrucción **bilineal de cada tap** del gather | **1/6 = 0.1667** | bilineal a fase `f` = 2 taps con pesos `(1−f, f)` a distancia 1 ⇒ var `f(1−f)`, media 1/6. Se aplica a CADA tap ⇒ **suma** a la PSF del conjunto, no se promedia entre los 24 |
  | `LowUpsample` (tent de 4 taps a ±0.5 texel) | **0.25–0.5, media 0.375** | a fase 0 el kernel discreto compuesto es `(0.25, 0.5, 0.25)` ⇒ var 0.5; a fase 0.5 es `(0.5, 0.5)` ⇒ 0.25. Ya incluye el bilineal de sus propios taps |
  | **TOTAL** | **~0.625** (rango 0.54–0.79) | |

  ⇒ **radio efectivo MÍNIMO del tier** (con `g = 0`) `= 2·sqrt(0.625) = 1.58 px de baja =`
  **~6.3 px full-res** (rango 5.9–6.9 según la fase). El tier **no puede** producir nada más angosto:
  con `rLow` por debajo del piso, `g = 0`, los 24 taps colapsan sobre el centro y el gather devuelve
  el box del pass 2 — que ES su PSF mínima. De ahí el **hueco de calidad 2.6–6.3 px** (ver punto 7 del
  pass 0) y de ahí que el recorte a 0 no sea solo una guarda numérica.
- 🚀 **EARLY-OUT del caso degenerado `g = 0` (F1 del plan de FPS, perf pura).** El código ya no
  escribe `sqrt(max(rLow² − 4·LOW_PSF_VAR, 0))` en una línea: calcula
  `g2 = rLow² − 4·LOW_PSF_VAR` y si `g2 ≤ 0` **devuelve el tap central y sale** (ahorra los 24 taps,
  las 25 evaluaciones de `BlurRadiusDeg` y la propia `sqrt`). `g2 ≤ 0` es EXACTAMENTE el conjunto
  `g == 0` (`sqrt(max(x,0)) == 0 ⟺ x ≤ 0`), no un umbral elegido: no hay franja de comportamiento
  distinto.
  **Por qué el resultado es el mismo (la cuenta, no una intuición):** con `g = 0`,
  `ri·g = 0` ⇒ `off = (cos,sin)·0 = (0,0)` EXACTO ⇒ `tuv = uv + (0,0)·lowTexel = uv` ⇒
  `rt = BlurRadiusDeg(uv)·ppdLow = rLow` (misma expresión que la línea de arriba) y
  `length(off) = 0` ⇒ `w = saturate(rLow − 0 + 1) = saturate(rLow + 1) = 1.0` EXACTO para los 24
  (`BlurRadiusDeg` devuelve `min(sqrt(...), MAX_BLUR_DEG) ≥ 0` y `ppdLow ≥ 0` ⇒ `rLow ≥ 0` ⇒ el
  `saturate` clampea). Los 24 pesos son iguales entre sí Y al del centro — **el centro no es un tap
  aparte: los 24 SON el centro** — así que `sum/wsum` es el promedio de 24 copias del mismo color.
  ⚠️ **La única diferencia es numérica y va A FAVOR del early-out:** la rama larga acumulaba 24 sumas
  en `half` antes de dividir por 24, así que devolvía `fl(24·c)/24`, no `c`. **Medido** (ver §Banco de
  capturas, tabla F1): con catarata de PROD en consultorio el par ANTES/DESPUÉS difiere en **9 621 px
  de 2 057 076 (0.47 %), 9 616 de ellos por 1 LSB y 5 por 2**, y el DESPUÉS es **bit-idéntico
  (0 px, maxd 0)** a una referencia exacta construida sin early-out y con `LOW_TAPS = 1` (que con
  `g = 0` da el tap central sin acumular). O sea: el early-out no introduce error, **le quita** a la
  rama vieja su error de acumulación. En Adreno (`half` = fp16 de verdad) ese error es mayor que en
  el Editor, así que el beneficio también.
  **Cobertura:** `g = 0` ⟺ `radiusPx ≤ 6.34 px` full-res, que con la `catarata` de PROD
  (`cataract_scatter 0.9` ⇒ piso de radio `0.22·0.81 = 0.1782°` = **4.28 px** a ppd 24) es
  **TODO el campo** salvo lo que esté a menos de ~42 cm de día / ~55 cm de noche. Verificado
  empíricamente: en el frame de consultorio la referencia `LOW_TAPS = 1` da 0 px de diferencia, lo
  que sólo puede pasar si `g = 0` en el 100 % de los píxeles.
- **Peso "scatter-as-gather":** `w_i = saturate(radio_del_tap − |offset| + 1)`, normalizado por
  `Σw` con fallback al tap central si `Σw ≈ 0`. Un objeto borroso derrama sobre su entorno; uno
  nítido no contamina el fondo borroso. El CoC de cada tap se **recalcula** con `BlurRadiusDeg`
  (misma fuente única que el pass 0) en vez de empaquetarse en alfa: el formato HDR de Quest es
  **B10G11R11, sin alfa**. Son **25** evaluaciones de `BlurRadiusDeg` por píxel de baja (el del
  centro + una por tap), no 24: a 1/16 son ~1.6 taps-full-res-equivalentes de textura, pero **el
  costo dominante no es el texturado sino el ALU** (ver §Coste).
- **Upsample (`LowUpsample`, en el pass 0):** tent de 4 taps bilineales a ±0.5 texel de baja. Un solo
  tap bilineal desde 1/16 reconstruye una superficie C⁰ con quiebres cada 4 px full-res (bloques /
  pliegues visibles en gradientes suaves); el tent los suaviza y además promedia el ruido residual
  del dithering. Solo se paga en los píxeles donde el tier tiene peso, que son justo los que NO
  pagan `DiscBlur13`.
  **Costo óptico del tent:** es el término **más grande** del piso de PSF del tier (0.375 de los
  0.625 de `LOW_PSF_VAR` ⇒ ~5.0 de los ~6.3 px del piso). Se descuenta en cuadratura, pero por debajo
  del piso el descuento se agota. Bajarlo a 1 tap daría un piso de ~5.2 px a cambio de devolver el
  bloqueo de 4 px: evaluado y rechazado (ver punto 7 del pass 0).

**Pass 1 — cilindro + contraste + tinte catarata + velo de scatter + velo CIE, `temp→source`**
(`_BlitTexture` = temp = imagen ya esfero-desenfocada):
9. Astigmatismo POR OJO: smear direccional de 7 taps gaussianos a lo largo del eje
   (`glare_astig_angle_l/r`), largo `ASTIG_BLUR_DEG(1.3°) × _VisionPxPerDeg[ojo] × glare_astig_l/r`
   (selección por `eyeIdx`, respeta `_StreamForceEye`). **El largo está en GRADOS**: era
   `ASTIG_BLUR_PX = 22` y arrastraba el MISMO bug de resolución que tenía el defocus (el astig se veía
   distinto según el alto del target); 1.3° = esos 22 px a **ppd 17**. `DirBlur` samplea **temp** (la
   imagen desenfocada), no el original: el cilindro hereda el defocus de la esfera.
   **OJO — el look del astigmatismo SÍ cambió en el visor** (hallazgo MENOR de review): esos ppd 17
   corresponden a `renderScale 1.0`, no a la config real (`Mobile_RPAsset` tiene `renderScale 1.4` ⇒
   ppd ≈ 24), así que en el dispositivo el smear pasa de 22 a **~31 px, ~+40 % de largo**. Es la
   consecuencia **correcta** del fix (el largo es angular y antes estaba sub-especificado), no una
   regresión — pero la frase anterior ("el look a esa resolución se preserva") solo valía a
   `renderScale 1.0`. La captura I está a ppd 90 y **no muestra el look del dispositivo**.
10. Contraste: `color = (color − pivot) × (1 − contrast_loss) + pivot`, con el **pivote = nivel de
    ADAPTACIÓN del campo**, no una constante: `pivot = CONTRAST_PIVOT_DAY · (NIGHT/DAY)^saturate(_PupilScene)`
    (0.22 de día → 0.025 de noche). Ver §`contrast_loss`: el pivote adaptativo.
11. Tinte amarillo de catarata (MULTIPLICATIVO, después del contraste y ANTES de los velos — ver
    "Tinte amarillo de catarata").
12. **Pedestal de velo difuso por scatter** (aditivo; entre el tinte y el velo CIE — ver
    "§`cataract_scatter`"). Lleva su propio factor `CATARACT_YELLOW`.
13. Velo de encandilamiento CIE (aditivo, después del contraste — ver "Disability glare"). Lleva
    su propio factor `CATARACT_YELLOW` (tanda v0.9.1 — ver §El velo CIE también pasa por el
    filtro ámbar).

**Coste** (por ojo, taps-equivalentes-full-res):
- Passes 2+3 a 1/16: `(4 + 24 + 25)/16 ≈ 3.3` en el peor caso. Se pagan SIEMPRE que el gate esté ON
  (ver la nota de `VisionRendererFeature`). Dos tile store/load extra, a 1/16: despreciables. VRAM:
  2 texturas a 1/16 del target por ojo (~9 MB en Quest 3 con `renderScale 1.4`, ×2 slices, B10G11R11).
  - **Con el early-out `g = 0` del pass 3 (F1) el pass 3 cuesta `1/16 ≈ 0.06` en vez de `49/16 ≈ 3.1`
    en los píxeles degenerados**, y ahorra además las **25 reconstrucciones de mundo**
    (`ComputeWorldSpacePosition` + `distance` + `sqrt` + la rama de focos) que son el término ALU
    dominante del pass — es decir, el ahorro real es MUCHO mayor que el que sugiere la cuenta de taps
    de textura. Con la `catarata` de PROD (`cataract_scatter 0.9`) la condición se cumple en
    prácticamente todo el campo (`radiusPx ≤ 6.34`), así que el pass 3 pasa de "24 taps + 25
    reconstrucciones en todo el frame" a "1 tap + 1 reconstrucción". Cero cambio óptico (ver la
    verificación en el bullet del pass 3).
- Pass 0: 2 taps si el radio es sub-píxel (early-out) · **15** en la banda 0.45–2.5 px (13 disco +
  base + depth) · 19 en el cruce 2.5–6 px (disco + tent) · **6** por encima de 6 px (no hay disco).
- Pass 1: 1 tap (copia) con astig = 0; **7 con astig activo**.
- El gate de `VisionActivity` lo lleva **todo a cero** con una lente sin efectos.
- **Peor caso REAL de la cadena: ~29 taps-equiv/px/ojo**, no 19 (corrección de review). Los 19 eran
  el peor caso *a radio ~2 px*; el máximo de la cadena es `3.3 (tier) + 19 (cruce de tiers, disco +
  tent) + 7 (astig) ≈ 29`, contra ~11 en la etapa B y ~6 antes de la tanda.
- **Y esa cuenta es solo de taps de TEXTURA.** El pass 3 hace **25 `ComputeWorldSpacePosition` +
  `distance` + `sqrt`** por píxel de baja (una por tap del gather más la del centro), y en un Adreno
  es **probable que el ALU domine sobre el texturado** en ese pass. ⇒ La **captura L tiene que medir
  el pass 3 aparte** (no solo el frame time agregado): si el cuello es ALU, `LOW_TAPS → 16` ahorra
  proporcionalmente y `LowDiv → 8` ahorra 4× (a costa de subir el piso de PSF de ~6.3 a ~12.6 px, ver
  `LOW_PSF_VAR`). **Parcialmente resuelto por el early-out de F1**: en el régimen degenerado ya no se
  paga NADA de ese ALU, así que bajar `LOW_TAPS` sólo compra algo en los píxeles con `g > 0`.
- **Coste del pass de billboards (`GlareBillboard`), MEDIDO en el Editor (F1).** Contra la intuición
  de "los halos llenan la pantalla", en `ruta_noche` con `panoptix` los quads de billboard cubren
  **2.23 % de la pantalla** en el encuadre `frente` y **1.59 %** apuntando a un faro de frente
  (unión de todos los quads que realmente dibujan: la mayoría de las 22 fuentes colapsa por
  `angMax < 0.004`, por `facing < 0.01` o por `ZTest`). De esa cobertura, la **zona descartada por el
  clip de F1 (`r > 0.98`) es 0.89 % / 0.64 % de la pantalla**, o sea ~40 % del área que tocan los
  billboards. ⇒ **el clip es una mejora real pero CHICA en términos absolutos**: no es la palanca que
  mueve el frame time, es un ahorro gratis y bit-exacto. La cuenta de "~25 % de los fragmentos
  desperdiciados" es correcta POR QUAD (`1 − π·0.98²/4 = 24.6 %`), no por pantalla.
  Método de la medición (reproducible): reemplazar el `clip` por `discard` para obtener el frame sin
  billboards, y por `if (r <= 0.98) discard; return half4(0.25,0,0,1);` para pintar sólo el anillo
  descartado; contar píxeles distintos entre los tres frames. Visualización:
  `capturas/perf_f1/CTRL_zona_descartada_r_mayor_098.png` (cada cuadrado rojo es un quad con su
  círculo útil recortado).
- **Medición en Quest pendiente (captura L).** ⚠️ **Orden de palancas CORREGIDO por el dossier de
  performance (F0):** el dossier midió que **el pass 0 (`FragEffect`, full-res) es el ~82 % del coste
  del post-proceso**, no los passes 2+3 del tier. La lista anterior de esta sección (subir
  `TIER_LO_PX`, `LOW_TAPS → 16`, `LowDiv → 8`) atacaba los passes 2 y 3, o sea el ~18 %: eran
  palancas de segundo orden mal priorizadas. Orden vigente:
  1. **Pass 0** (full-res, 82 %): es donde hay que buscar. `DiscBlur13` + tent del upsample + base +
     depth se pagan por píxel de pantalla completa.
  2. **Pass 3** (tier): ya recortado por el early-out de F1 en el régimen degenerado; lo que queda es
     `LOW_TAPS → 16` / `LowDiv → 8` para los píxeles con `g > 0`.
  3. **Pass 1** (astig): 7 taps sólo con astigmatismo activo.
  4. Escape hatch del plan (volver a 2 blits con `MAX_BLUR_DEG 1.0`).
  Las dos palancas de F1 (early-out del pass 3 + clip del billboard) son **bit-exactas**: no gastan
  presupuesto clínico, así que van primero por definición.

**Coeficientes perceptuales empíricos** (no tocar sin datos ni sin recapturar la verificación):
- `DOF_M_TO_D = 0.5` — mapea profundidad de foco en m a tolerancia dióptrica. Elegido a ojo.
- `CONTRAST_PIVOT_DAY = 0.22` / `CONTRAST_PIVOT_NIGHT = 0.025` — **depende del espacio de color**:
  el proyecto es **Linear + HDR B10G11R11** (`ProjectSettings.asset`, `Mobile_RPAsset`), así que el
  pivote está en LINEAR, no en sRGB. Lo mismo vale para `SCATTER_VEIL`. Era un solo
  `CONTRAST_PIVOT = 0.22`; ver §`contrast_loss`: el pivote adaptativo para el porqué, la fisiología
  y los números medidos del campo en cada escenario.
- `PUPIL_DAY_MM = 3.0` — pupila fotópica (~100 cd/m²) de un paciente de ~70 años [12]. Sustituye,
  junto con la nocturna, al fudge `lerp(1, 1.35, …)`.
- `PUPIL_NIGHT_MM = 4.0` — pupila mesópica del mismo paciente. **Era 5.5 y se bajó a 4.0**
  (tanda "pupila mesópica", ver §Ancla de calibración día↔noche). `5.5` no es defendible a los
  ~70 años: **Winn et al. 1994** [14], el dataset canónico de diámetro pupilar vs edad y luminancia,
  ubica a esa edad entre **4.0 y 4.8 mm en TODO el rango mesópico** (≈4.5 mm a 0.44 cd/m², ≈4.8 mm a
  0.09 cd/m²) y **nunca por encima de ~5 mm**. `ruta_noche` (6 faroles + faros del tráfico) es
  **mesópico alto** ⇒ extremo luminoso del rango ⇒ 4.0 mm.

**Ancla de calibración día↔noche (leer antes de tocar `PUPIL_NIGHT_MM`).** El radio es *lineal* en
`pupilM`, así que el salto día→noche multiplica el desenfoque por `PUPIL_NIGHT_MM / PUPIL_DAY_MM`
independientemente de la distancia. Con 5.5 ese factor era **×1.833** y **invertía el orden de las
distancias** entre escenarios. Objetos de referencia, medidos desde la pose de diseño de cada
escenario, con la `monofocal` de PROD (`0.6.0-clinical.a50`: `foco_lejos 6.021838`,
`profundidad_foco 0`, `desenfoque_max 0.78903085`) y ppd 24:

| objeto | escenario | distancia | `over` | pupila | radio [°] | radio px | régimen |
|---|---|---|---|---|---|---|---|
| libro al brazo estirado | consultorio (día) | 0.780 m | 1.116 D | 3.0 mm | 0.07568 | **1.82** | `lowW = 0`, disco puro |
| velocímetro (`misc_d/display1`) | ruta_noche | 0.841 m | 1.023 D | 4.0 mm | 0.09253 | **2.22** | `lowW = 0`, disco puro |
| infotainment (`misc_d/display2`) | ruta_noche | 0.834 m | 1.033 D | 4.0 mm | 0.09340 | **2.24** | `lowW = 0`, disco puro |

- Las tres distancias son **compatibles de por sí** (Δ ≤ 0.17 D) y los tamaños angulares del texto
  también (libro ≈26′, tablero ≈21–24′): ni la geometría ni el contenido explicaban el síntoma
  reportado ("con la misma lente el tablero se comporta distinto que el libro"). La causa era
  **sólo** la pupila.
- Con `PUPIL_NIGHT_MM = 5.5` el tablero daba **3.05 px** contra 1.82 px del libro: **+68 % de
  desenfoque estando MÁS LEJOS y con MENOS error dióptrico** ⇒ se percibía como el libro a 0.49 m.
  Con 4.0 da 2.22 px (**+22 %**): monótono y coherente. **Si alguien vuelve a subir
  `PUPIL_NIGHT_MM`, reintroduce esa inversión.**
- Efecto lateral favorable (no es la justificación, es una consecuencia): 3.05 px caía dentro del
  hueco de calidad 2.6–6.3 px (`lowW ≈ 0.07`); 2.22 px queda por debajo de `TIER_LO_PX = 2.5` ⇒
  **disco full-res puro, régimen exacto**, sin el sesgo pesimista del cruce de tiers.
- **Verificado con capturas frame-matched a ppd 24 exacto** (mismo escenario, misma lente, misma
  pose; sólo cambia el `#define`; tráfico oculto y `_PupilScene`/velo CIE fijados para determinismo;
  piso de ruido intra-sesión medido = **0 px de diferencia**):

  | caso | radio antes | radio después | grad RMS antes → después | captura |
  |---|---|---|---|---|
  | libro 0.78 m, día | 1.82 px | 1.82 px (**sin cambio**) | 4.1223 → 4.1224 (+0.00 %) | `PUP_par_consultorio_libro078_monofocal_ANTESvsDESPUES.png` |
  | `display1` (velocímetro) | 3.05 px | **2.22 px** | 5.4275 → 6.4693 (**+19.2 %**) | `PUP_par_ruta_display1_monofocal_ANTESvsDESPUES.png` |
  | `display2` (infotainment) | 3.08 px | **2.24 px** | 7.8408 → 8.9129 (**+13.7 %**) | `PUP_par_ruta_display2_monofocal_ANTESvsDESPUES.png` |
  | `catarata` PROD, `display2` | 8.45 px | 6.28 px | 1.2527 → 1.5310 (sigue ilegible) | `PUP_par_ruta_display2_catarata_ANTESvsDESPUES.png` |
  | par cruzado libro↔tablero | — | — | — | `PUP_par_cruzado_libro078dia_vs_tablero0841noche.png` |

  El día es **bit a bit intacto** (`_PupilScene = 0` ⇒ `lerp(3.0, ·, 0) = 3.0` exacto): el par del
  libro difiere en 119 px de 786 432 y **todos por 1 LSB**, por debajo del piso de ruido
  entre-sesiones medido con el **pass apagado** (`gate OFF`, donde el `#define` no puede intervenir):
  461 px con hasta 3 LSB. El **pedestal de velo por scatter** de la catarata es **idéntico**
  (p01 y p50 del frame = 96.189 / 98.116 en ambas ramas): usa el factor 0..1 `_PupilScene`, no los
  mm, así que no está acoplado a esta constante.
- `MAX_BLUR_DEG = 2.0` — tope del radio angular (4° de diámetro). En la etapa B estuvo temporalmente
  en 1.0 porque el disco full-res no aguantaba más.
  **La densidad de la espiral NO alcanza en el techo del rango** (hallazgo N3 de review; antes esta
  línea decía "~12 px de baja, que la espiral de 24 taps cubre densamente" y era **falso**). La
  separación media de un muestreo de área uniforme de N taps sobre un disco de radio r es
  `2·r/√N = 0.408·r`: a 48 px full-res (2° a ppd 24) el radio de baja es 12 px ⇒ separación
  **4.9 px de baja (~20 px full-res)** contra un footprint bilineal de ~1–1.5 px de baja ⇒
  **sub-muestreo de 3–4×**, el mismo mecanismo que produjo M1 un nivel más abajo. La densidad solo
  alcanza hasta `rLow ≲ 3.5–4` ⇒ **`radiusPx ≲ 15 px`**.
  - **Atenuante:** la fase se dithera **por píxel**, así que el sub-muestreo sale como **ruido de
    alta frecuencia**, no como copias coherentes; el box 4×4 suaviza cada copia y el tent del
    upsample promedia 4 vecinos. El residuo esperado es un **moteado de 4–8 px full-res** que puede
    "hervir" con el movimiento de cabeza (el ruido queda a 1/16 y se amplía con el tent) — **solo
    verificable en el visor**.
  - **Configs de PROD que exceden el techo:** `catarata` (`desenfoque_max 2.0`,
    `profundidad_foco_m 0`) con el libro cerca ⇒ **27 px de día y 48 px de noche** (clamp).
  - **Palancas:** `LOW_TAPS → 32` mejora la separación solo un **13 %** (escala `1/√N`); bajar
    `MAX_BLUR_DEG`. Llegar a separación ≤ 1.5 px de baja a `rLow = 12` exigiría **N ≈ 256 taps** ⇒ el
    sub-muestreo en el techo es **estructural**, no un parámetro mal elegido.
  - **MEDIDO — el moteado está en el piso de cuantización de 8 bits.** Barrido de `rLow` a **ppd 90
    fijo** (mismo contenido y misma escala; solo cambia el radio vía `foco_lejos`), midiendo el
    residuo de alta frecuencia (imagen − box de 3 px) **dentro del panel blanco desenfocado**, que es
    el peor caso: zona plana, brillante y de alto contraste alrededor. Escala 0–255:

    | `rLow` | radio full-res | separación de la espiral | HP-RMS | pico-a-pico |
    |---|---|---|---|---|
    | 4 | 16 px | 1.50 px de baja | 0.679 | 16 |
    | 6 | 24 px | 2.36 px de baja | 0.477 | 10 |
    | 10 | 40 px | 4.03 px de baja | 0.507 | 9 |
    | 14 | 56 px | 5.68 px de baja | 0.632 | 12 |
    | **22** | **88 px** | **8.95 px de baja** | **0.759** | **13** |

    Capturas: `capturas/N3_sweep_rLow{4,6,10,14,22}_ppd90.png`. De `rLow` 6 a 22 la separación crece
    **3.8×** pero el residuo solo **1.6×**, y en absoluto se queda en **≤ 0.8/255 RMS (≈ 0.3 % de la
    luminancia local)**, o sea ~1 LSB: el dither convierte el sub-muestreo en ruido de media cero y
    el box 4×4 + el tent lo promedian. `rLow = 22` es además **~2× peor que el techo que el visor
    puede alcanzar** (`MAX_BLUR_DEG` a ppd 24 ⇒ `rLow = 12`).
  - **Casos de PROD reales (ppd 24), frame completo:**

    | caso | radio | `rLow` | HP-RMS | pico-a-pico | captura |
    |---|---|---|---|---|---|
    | catarata PROD **día**, libro 15 cm | 27 px | 6.75 | 0.146 | 2 | `N3_techo_dia_catarata_libro15cm_ppd24.png` |
    | catarata PROD **noche**, libro 15 cm (clamp) | 48 px | 12.0 | 0.167 | 2 | `N3_techo_noche_catarata_libro15cm_ppd24.png` |
    | stress alto contraste (optotipo forzado) | 23 px | 5.83 | 0.548 | 13 | `N3_stress_optotipo_altocontraste_r23px_ppd24.png` |

    En los dos casos de PROD el residuo es **0.15–0.17 LSB**: sin moteado, sin banding, sin copias
    fantasma, sin bloqueo del upsample — el campo queda uniforme. ⇒ **`LOW_TAPS` se queda en 24**: el
    sub-muestreo geométrico es real e innegable, pero su consecuencia perceptual está por debajo del
    piso de 8 bits, así que gastar +8 taps no compra nada medible.
  - **Lo que NO se puede medir en una captura fija:** el hash del dither es **estático en espacio de
    pantalla**, así que al mover la cabeza el contenido se desliza bajo un patrón fijo ⇒ posible
    "hervor"/crawl de patrón fijo. La amplitud sería la medida arriba (≤ 0.3 %), así que se espera
    imperceptible, pero **queda como verificación en dispositivo** junto con R1.
- `SUBPIXEL_LO_PX = 0.15` / `SUBPIXEL_HI_PX = 0.45` — **early-out** del kernel full-res (no es una
  mezcla óptica: ver punto 8 de arriba). En píxeles del RENDER TARGET (ver gotcha del `renderScale`).
- `TIER_LO_PX = 2.5` / `TIER_HI_PX = 6.0` — cruce entre el disco full-res y el gather a 1/16.
  **Los dos tiers NO se solapan** (el disco es limpio hasta ~2.6 px; el tier no baja de ~6.3 px):
  el cruce cubre un **hueco de calidad real** pagando sobre-borroneo ≤ +26 % en 2.6–6 px. Elección
  razonada, alternativas y tabla del sesgo: ver punto 7 del pass 0.
- `LOW_TAPS = 24`, `GOLDEN_ANG = 2.39996323`, `LOW_PSF_VAR = 0.625` — espiral del gather de baja.
  `LOW_PSF_VAR` es el **piso de varianza de toda la cadena del tier** (box + bilineal de cada tap +
  tent del upsample), no solo el box: se llamaba `LOW_BOX_VAR = 1/12` y eso era el error de N1.
- `ASTIG_BLUR_DEG = 1.3` — largo del smear astigmático **en grados** (era 22 px). Equivale a los 22 px
  originales a ppd ≈ 17. Ver la deuda de densidad de taps en §Pendientes.
- `SCATTER_BLUR_DEG = 0.22` y `SCATTER_VEIL = 0.05` (linear) — ver §`cataract_scatter`.
- **ELIMINADOS**: `BLUR_RADIUS_PX = 7.0`, `MAX_DEFOCUS_D = 1.5` (el radio ahora es físico/angular) y
  `ASTIG_BLUR_PX = 22.0` (ahora en grados). `MAX_DEFOCUS_D` era la causa raíz del bug "el libro
  pegado se lee": saturaba el blur a 1.5 D de error, o sea que de ~60 cm hacia adentro el desenfoque
  era **constante**.

La calibración fina de las curvas por LIO sigue siendo contra **defocus curves** publicadas (agudeza
vs desenfoque): PanOptix trifocal — Kohnen et al. [7]; Vivity EDOF — McCabe et al. [8]; y una
monofocal de referencia. Tarea futura.

**Consecuencia clínica del modelo físico (documentar antes de "arreglar"):** para un objeto de
tamaño fijo, tanto el tamaño angular de sus detalles como el radio del desenfoque escalan ~`1/d`, así
que **acercar el objeto NO cambia mucho la legibilidad RELATIVA** — solo agranda todo. El cociente
`ratio(d) = diámetro_del_blur / altura_del_glifo = pupila · desenfoque_max · (1 − d/f − d·tolD) / h`,
con **techo estructural `lím(d→0) = pupila / h`**.

**Tamaño real del glifo del libro: x-height = 2.21 mm ± 0.15 (MEDIDO).** Cadena de medición
(reproducible sin play mode): `dP/dv` ponderado por área sobre las 22 caras con normal +Y del mesh
`Book_Diary` × `lossyScale 0.02` ⇒ **0.13375 mm/texel** a 2048²; control de la cadena: V ∈ [0.100,
0.900] × 0.2739 m = 0.219 m contra `renderer.bounds.z = 0.2202 m` (**0.5 % de error**); perfil de
filas oscuras de la textura ⇒ baselines en y = 1443/1513/1583/1653 (**interlineado 70.0 texels =
9.36 mm**), techo de las redondas ('o','e','w','s') 16 texels sobre la baseline ⇒ **x-height 2.21 mm**;
ascendentes ('t','h','l') 55 texels = 7.36 mm. O sea **1.5 M**: letra grande de libro real, NO "letra
gigante".

Con `h = 2.21 mm`, pupila 3 mm, `desenfoque_max = 1.0` y la `monofocal` de PROD (`foco_lejos 6`,
`profundidad_foco_m 1.0` ⇒ `tolD 0.5 D`), el cociente y el veredicto (criterio calibrado contra las
capturas de la §medición de referencia del libro: `ratio ≈ 1.0` = frontera de ilegibilidad):

| d | `ratio` | logMAR entregado | logMAR que el libro EXIGE | veredicto |
|---|---|---|---|---|
| 1.00 m | 0.46 | 0.16 | 0.18 | se lee |
| 0.78 m (brazo estirado) | **0.65** | 0.42 | 0.29 | degradado, se lee con esfuerzo |
| 0.55 m | **0.86** | 0.69 | 0.44 | marginal |
| 0.40 m (lectura) | **1.00** | 0.90 | 0.58 | **ilegible** |
| 0.25 m (pegado) | **1.13** | 1.16 | 0.78 | **ilegible** |

⇒ **el déficit CRECE al acercar el libro** y el techo es `3/2.21 = 1.36`, no ~0.6: **con
`desenfoque_max = 1.0` el modelo YA hace el libro ilegible de 40 cm hacia adentro**, y el brazo
estirado es el punto menos malo (déficit 0.13 logMAR ≈ 1.3 líneas) — que es exactamente el
comportamiento clínico buscado. Contraste externo: la defocus curve publicada de una monofocal a
−2.33 D da 0.75–0.90 logMAR y el modelo entrega **0.90 a 40 cm**: está calibrado, no flojo.

> ⚠️ **Esta sección decía antes "glifos de ~5.6 mm", cociente "~0.5", "marginalmente legible a
> cualquier distancia" y "la palanca clínica para 'pegado = ilegible' es `desenfoque_max > 1`". Las
> cuatro afirmaciones eran FALSAS y encadenadas al mismo error: se midió la CAJA DE LÍNEA**
> (interlineado 9.36 mm / ascendente 7.36 mm) **en vez del glifo** (x-height 2.21 mm), y de paso sobre
> el mesh equivocado (ver la trampa de `Book_Diary` vs `Book_Diary_Tapa` en el bullet de
> `BookHolder.cs`). El error **indujo una propuesta de recalibración que subía `desenfoque_max` a
> 1.3–1.4 en la monofocal** para "arreglar" que el libro se leyera de cerca; con el `h` real eso
> llevaría el brazo estirado de 0.65 a 0.88 (rompiendo lo único que estaba bien) y el logMAR a 40 cm
> a 1.03, **fuera** de la banda publicada. Se detectó a tiempo y no se aplicó. **Regla: cualquier
> argumento de legibilidad se ancla al glifo MEDIDO con su cadena de medición, no a la caja de
> línea.** Si el libro se lee de cerca en el visor, el sospechoso NO es el catálogo: es la build que
> corre en el dispositivo o el camino del libro (máscara/depth) — ver el gotcha de
> `supportsCameraDepthTexture`.

**Medición de referencia del libro (captura D re-corrida en la etapa C, `monofocal`, consultorio
día, pupila 3 mm, `foco_lejos_m = 6`).** Capturas: `capturas/etapaC_D_zoomVisor_*.png` (render a la
ppd del visor con FOV recortado y ampliación nearest-neighbour del PNG — lupa fiel, NO cambia el ppd)
y `capturas/etapaC_D_ppd19_PROD_*.png` (mismo ppd que las capturas de la etapa B, para comparar):

| distancia | `over` [D] | radio [°] | px pedidos @ ppd 24 | radio EFECTIVO (post-N1) | veredicto de legibilidad |
|---|---|---|---|---|---|
| 60 cm | 1.500 | 0.1017 | **2.44** | 2.44 (exacto, disco puro) | marginal — se ve la estructura de las palabras, no se lee corrido |
| 40 cm | 2.333 | 0.1582 | **3.80** | 4.73 (+25 %, banda del cruce) | ilegible (manchas de palabra) |
| 25 cm | 3.833 | 0.2599 | **6.24** | 6.32 (+1.3 %) | ilegible total |
| 15 cm | 6.500 | 0.4408 | **10.58** | 10.58 (exacto) | ilegible total, campo uniforme |

La columna "radio EFECTIVO" es lo que el kernel entrega de verdad tras el fix de N1 (antes era
2.44 / 5.03 / 8.42 / 12.13 px): **el único punto con sesgo residual apreciable es 40 cm**, y su
veredicto "ilegible" se verificó con el par encuadre-idéntico antes/después (ver punto 7 del pass 0).
Valores con el `desenfoque_max` de PROD (**0.789**, `profundidad_foco_m = 0`). Con el default del
repo (**0.9**, `profundidad_foco_m = 1.0`) los radios son 1.86 / 3.40 / 6.19 / 11.14 px — dentro del
10 % salvo a 60 cm, donde la profundidad de foco de 1 m resta 0.5 D y lo deja algo MÁS legible. Los
veredictos son los mismos.

**Por qué esta medición contradice la de la etapa B** (donde el libro a 25 cm **sí se leía**, incluso
más que ANTES del cambio): la etapa B mostraba "So the young man spent his life, waiting…" legible y
la etapa C no. La explicación es el KERNEL, no la óptica: `DiscBlur9` a ~4.9 px dejaba 4 copias
fantasma al 12.5 % con los trazos **intactos** (más un delta central al 10 %), y una copia nítida al
12 % de un trazo de alto contraste **se lee**; el gather del tier de baja destruye esa alta
frecuencia.

> **El careo de capturas NO es una prueba encuadre-idéntica** (hallazgo N4 de review; antes esta
> sección afirmaba "a la MISMA ppd (19) y con el MISMO radio (4.94 px)" y eso **sobre-vendía** la
> evidencia). La ppd y el radio sí son calculables y coinciden, pero **la pose de cámara no es la
> misma**: en `capturas/etapaB_D_libro_monofocal_25cm_DESPUES.png` se ven ventana + SmartTV y la
> página se mira casi de frente; en `capturas/etapaC_D_ppd19_PROD_25cm.png` el fondo es pared/piso,
> la página está más picada, la iluminación de página es otra y **la misma línea de texto mide ~20 %
> menos en píxeles** (~620 px vs ~500 px). Con el mismo radio, glifos 20 % más chicos **ya son menos
> legibles por sí solos** — es la misma lección que M5. No se puede recapturar el par con la pose de
> etapa B (ese código ya no existe), así que el enunciado queda **bajado de tono a propósito**: el
> veredicto probablemente se sostiene (el delta perceptual es mucho mayor que un 20 %, y la serie
> `capturas/etapaC_D_zoomVisor_*.png` es internamente consistente y encuadre-idéntica entre sí), pero
> **la evidencia válida de Bug 2 es esa serie, no el careo B↔C**.

### `contrast_loss`: el pivote adaptativo (tanda v0.9.1) — CIERRA el bug del velo nocturno

**El operador.** En el pass 1, por ojo:

```
pivot = CONTRAST_PIVOT_DAY · (CONTRAST_PIVOT_NIGHT / CONTRAST_PIVOT_DAY)^saturate(_PupilScene)
color = (color − pivot) · (1 − contrast_loss) + pivot          // todo en LINEAR
CONTRAST_PIVOT_DAY = 0.22   ·   CONTRAST_PIVOT_NIGHT = 0.025
```

**Qué estaba mal.** El pivote era **fijo en 0.22 linear** y el comentario del shader decía *"pivote
bajo: no levanta los negros"*: cierto de día, **falso de noche**. En `ruta_noche` casi todo el frame
está POR DEBAJO de 0.22, así que el operador dejaba de comprimir desde arriba y **empujaba el piso
hacia arriba** — un pedestal ADITIVO uniforme, o sea un **velo**. Y el velo ya lo aportan dos
términos con su propia física: el velo CIE de las fuentes [1][2] y el pedestal de scatter del
cristalino [13]. `contrast_loss` estaba haciendo de tercer velo sin serlo.

**Por qué el pivote es el nivel de ADAPTACIÓN (fisiología, no estética).** El contraste se define
como modulación alrededor de la luminancia de adaptación (Weber: el umbral escala con `L`), y la
función de sensibilidad al contraste se mide a una luminancia MEDIA fija [15]. Perder sensibilidad
al contraste comprime la modulación **hacia la media de adaptación**; no levanta el piso de negros.
⇒ el pivote no es una constante del shader: es el nivel de adaptación del CAMPO.

**Señal de adaptación = `_PupilScene`** (0 = día/fotópico, 1 = noche/mesópico). No es un atajo: el
diámetro pupilar **es** una función monótona de la luminancia de adaptación del campo [12][14], así
que la MISMA señal que dilata la pupila fija el nivel de adaptación. Ventajas: **footprint cero**
(uniforme ya publicado por `ScenarioManager`, nada nuevo en C#, un `exp2` en el fragment porque el
compilador plega `log2(constante)`), y hereda las taus asimétricas del reflejo fotomotor [9] ⇒ el
cambio de escenario es un **transitorio de adaptación suave**, no un salto.

**Interpolación GEOMÉTRICA** (`DAY · (NIGHT/DAY)^t`), no lineal: la adaptación es logarítmica en
luminancia. Y a `t = 0` da `0.22` **EXACTO** (`pow(x, 0) == 1`), que es lo que deja el día intacto.

**Anclaje de los dos valores — MEDIDO, no elegido a ojo.** Luminancia media **lineal** del campo
(BT.709 [6] sobre el frame completo, lente `paciente_joven` = sin efectos, ppd 24):

| escenario | encuadre | `L̄` lineal del campo |
|---|---|---|
| consultorio día | optotipo 4 m | **0.30312** |
| consultorio día | libro 0.55 m | **0.18773** |
| `ruta_noche` | frente | **0.02331** |
| `ruta_noche` | tablero | **0.03025** |

El **0.22 histórico cae DENTRO del rango diurno [0.188, 0.303]** ⇒ era, sin saberlo, el nivel de
adaptación del consultorio de día, y de ahí que "funcionara" de día. El nocturno se fija con el
MISMO criterio dentro de [0.023, 0.030] ⇒ **0.025** (razón día/noche **8.8×**).
⚠️ El render **no tiene ancla fotométrica absoluta** (misma limitación que el velo): estos números
son del campo RENDERIZADO, no cd/m². La razón real fotópico/mesópico sería mayor (sala
~20–100 cd/m² vs ruta de noche ~1–3 cd/m² ⇒ 10–50×), o sea **8.8× es el extremo CONSERVADOR**: el
pivote nocturno queda si acaso alto (levanta un poco los negros) y nunca los hunde.

**El catálogo NO se recalibra** (y por eso este cambio no es contrato compartido con
`docs/catalogo-lentes.md`): `CONTRAST_PIVOT_DAY` no se movió, así que los `contrast_loss` calibrados
contra el optotipo ETDRS **de día** (panoptix 0.1149, vivity 0.0503, catarata 0.5924 en PROD
`0.8.1-clinical.a1`) siguen valiendo exactamente lo mismo. Interpolar DESDE el pivote diurno, en vez
de reemplazarlo, es lo que compra eso.

**Medido — par ANTES/DESPUÉS en la MISMA corrida** (`ruta_noche`, encuadre `tablero`, ppd 24, tráfico
congelado, `_PupilScene` pinneada a 1, velo y astig en 0; el "antes" se reconstruye poniendo
`CONTRAST_PIVOT_NIGHT = CONTRAST_PIVOT_DAY`, que devuelve el operador viejo EXACTO — ver §Banco de
capturas, receta del A/B en la misma corrida). Luminancia **byte** BT.709 del recorte `display1`
(bbox) y de la **cabina** (45 % inferior del frame):

| lente (`contrast_loss`) | `display1` antes | después | cabina antes | después |
|---|---|---|---|---|
| `paciente_joven` (0) — control | 43.78 | **43.78** | 30.53 | **30.53** |
| `monofocal` (0) — control | 44.72 | **44.72** | 31.35 | **31.35** |
| `vivity` (0.050) | 55.72 (**+27 %**) | **44.26 (+1.1 %)** | 46.71 (**+53 %**) | **32.61 (+6.8 %)** |
| `panoptix` (0.115) | 66.08 (**+51 %**) | **45.09 (+3.0 %)** | 58.82 (**+93 %**) | **34.56 (+13.2 %)** |
| `catarata` (0.592) | 104.50 (**+139 %**) | **54.58 (+24.7 %)** | 101.98 (**+234 %**) | **49.74 (+62.9 %)** |

(los `%` son contra `paciente_joven` en la misma columna). En el encuadre `frente`, frame completo:
`panoptix` 58.34 (**+126 %**) → **31.74 (+23.2 %)**; `catarata` 102.40 (**+297 %**) → **49.97 (+94 %)**.

**La modulación NO se movió — es la prueba de que el fix es quirúrgico.** Desviación estándar de la
luminancia LINEAL del recorte `display1` (= `rmsC × L̄`), relativa a `paciente_joven` (0.12480):

| lente | antes | después | esperado `1 − contrast_loss` |
|---|---|---|---|
| `vivity` | 0.11778 (**0.9438**) | 0.11780 (**0.9439**) | 0.9497 |
| `panoptix` | 0.10974 (**0.8793**) | 0.10959 (**0.8781**) | 0.8851 |
| `catarata` | 0.03250 (0.2605) | 0.03248 (0.2602) | 0.4076 (más bajo: además hay blur + scatter) |

⇒ la pérdida de modulación es **la misma antes y después (Δ ≤ 0.15 %)** y coincide con
`1 − contrast_loss`. Lo único que el fix elimina es el **pedestal aditivo**. Corolario para leer
métricas: el `gradRMS` de `display1` de `panoptix` **sube** 20.69 → 23.37 (81 % → 91 % de `joven`)
y eso **no** es "menos castigo": el gradiente en bytes crece cuando el nivel baja (la sRGB expande
diferencias en la zona oscura). En LINEAR el castigo es idéntico; el 81 % viejo mezclaba la niebla.

**Residual honesto: el operador SIGUE levantando los negros, pero por la magnitud correcta.** Un
píxel negro (0 linear) sale en `pivot · contrast_loss`: de noche eso es 0.0148 linear con `catarata`
(**byte 32**, contra **byte 101** antes) y 0.0029 con `panoptix` (**byte 15**). Es lo que
"perder sensibilidad al contraste" significa: no se distingue el 0.1 % del nivel de adaptación del
0.4 %. Lo que ya NO pasa es que el negro se vaya a gris medio.

**Miosis transitoria por glare (efecto lateral, favorable).** `ScenarioManager.glareMiosisGain`
(0.3) baja el target de `_PupilScene` cuando hay velo intenso ⇒ el pivote **sube**: con velo máximo
(`maxVeil 0.6`) `t = 0.82` ⇒ pivote **0.037** (×1.48). La dirección es la correcta (una fuente
brillante en el campo SUBE el nivel de adaptación ⇒ sube el piso de negros percibido) y la magnitud
es suave.

**Triple vía de la MISMA fuente al MISMO píxel — evaluada, sin doble contabilidad relevante**
(hallazgo MENOR de review). Un faro alimenta el píxel por tres caminos a la vez: (1) el **velo CIE
aditivo**, (2) el **pedestal de scatter** (que no depende de la fuente, pero se compone en el mismo
sitio) y (3) este **pivote × miosis** (la fuente baja `_PupilScene`, que sube el pivote, que levanta
el piso de negros). Los tres son mecanismos fisiológicos distintos (straylight de fuente puntual /
straylight difuso del medio / re-adaptación pupilar), no tres copias del mismo. La magnitud del
tercer camino es la que importa para descartar doble contabilidad: con `panoptix` de PROD
(`contrast_loss 0.115`) el delta de piso que agrega la miosis es **~0.0014 linear** (≈4 bytes en
sRGB en la zona oscura), o sea el mismo orden que el residual honesto del operador. Dirección
clínicamente defendible y magnitud despreciable ⇒ **no se corrige**.

**Transitorio del día (≤ 1 LSB, se extingue).** El día converge a `_PupilScene → 0` exponencialmente
(τ 0.9 s); a los ~4 s de un `load_scenario` vale ~0.012 ⇒ pivote 0.2144 (**−2.6 %**). Medido en el
optotipo (par `_PupilScene` 0 vs 0.012, misma corrida): `monofocal` 0.58 % de píxeles a **maxd 2**,
`panoptix` 59.8 % a **maxd 2** (meanAbs 0.296), `catarata` 99.5 % a **maxd 3** (meanAbs 0.858). O
sea ≤ 3 LSB durante unos segundos después de cambiar de escenario, y **exactamente 0 en régimen**.

**Capturas:** `capturas/contraste_nocturno/` — `{antes,despues}_{tablero,frente}_<lente>.png`,
`{antes,despues}_dia_{optotipo,libro055}_<lente>.png`, y las hojas
`_HOJA_tablero_antes_vs_despues.png` (vivity/panoptix/catarata), `_HOJA_frente_antes_vs_despues.png`,
`_HOJA_CONTROL_joven_monofocal_sin_cambio.png`, `_HOJA_dia_optotipo_sin_cambio.png`,
`_HOJA_dia_libro055_sin_cambio.png`.

### Tinte amarillo de catarata (`cataract_yellow`) — C2

Modela la **transmitancia del cristalino envejecido/brunescente**: con la edad el cristalino
amarillea y absorbe fuerte en azul y casi nada en rojo (base de la catarata nuclear amarilla).
Es un filtro de absorción **MULTIPLICATIVO** en el pass 1:

```
cataract = saturate(eye==L ? _CataractL : _CataractR)   // 0..1
color   *= lerp(half3(1,1,1), CATARACT_YELLOW, cataract)   // CATARACT_YELLOW = half3(1.0, 0.86, 0.55)
```

- **Triple (1.0, 0.86, 0.55)** = proyección perceptual del espectro de transmitancia del cristalino
  senil a primarios sRGB, **normalizada a rojo = 1** [11]. A `cataract = 1` implica una caída de
  luminancia Rec.709 de `1 − (0.2126·1 + 0.7152·0.86 + 0.0722·0.55) ≈ 13%`: **modela la pérdida de
  transmitancia TOTAL** del cristalino, por eso NO se agrega término extra de luminancia.
- **Multiplicativo, NO aditivo:** el cristalino no emite luz; solo absorbe. La luz dispersada
  (straylight) ya la modela el velo/disability glare — son mecanismos distintos.
- **Orden: después del contraste y ANTES de los dos velos aditivos.** El criterio es *qué filtra
  cada multiply*, no "evitar doblar": este multiply filtra la **IMAGEN**, y cada velo aditivo lleva
  **su propio** factor `CATARACT_YELLOW` explícito (pedestal de scatter y velo CIE). Los cuatro
  términos del modelo (imagen, pedestal, halo del billboard, velo CIE) cruzan el mismo medio
  absorbente y llevan la transmitancia **una vez cada uno**.
  > ⚠️ Esta línea decía antes *"si fuera después del velo, doble-amarillearía el `_GlareVeilTint`
  > cálido que el velo ya aplica"* y **confundía orden con física** (hallazgo MAYOR de review, tanda
  > del velo ámbar): `_GlareVeilTint = (1, 0.95, 0.85)` es **tinte de LOOK de la fuente** (el color
  > cálido de un faro), **no una transmitancia**; multiplicarlo por `CATARACT_YELLOW` compone
  > *fuente × medio* y no dobla nada. El argumento del "doble amarilleo" **sí** vale para
  > `WindowPortal.shader` (ahí el multiply sería sobre luz que YA pasó por este pass), y esa es la
  > única excepción real del patrón.
- **El triple vive en UN solo `#define CATARACT_YELLOW`** (`VisionPostProcess.shader:338`) que
  consumen TRES lugares dentro de este shader: este filtro de imagen, el **pedestal de scatter**
  (§Dispersión intraocular (b)) y el **velo CIE** (§El velo CIE también pasa por el filtro ámbar),
  porque las tres cosas cruzan el mismo medio absorbente. Si se recalibra el amarillo, tocar el
  `#define` mueve los tres juntos — no editar el literal en un solo sitio o los velos se despegan
  del tinte de la imagen.
  **Hay un SEGUNDO `#define` con el mismo triple** en `GlareBillboard.shader` (halos de los faros,
  ver abajo) porque los billboards se dibujan fuera del post-proceso y no hay include compartido:
  **una recalibración del amarillo toca LOS DOS archivos en la misma tanda** (los dos llevan el
  comentario cruzado). `WindowPortal.shader` **no** lo lleva, a propósito (también abajo).

#### Los halos de los faros SÍ pasan por el filtro ámbar (tanda v0.9.1) — CERRADO

`GlareBillboard.shader` es `Queue = Transparent` con `Blend One One` y el pass de visión se inyecta
en `BeforeRenderingTransparents` ⇒ los billboards se dibujan **DESPUÉS** de todo el post-proceso.
Un paciente con catarata brunescente veía la escena ámbar y los halos de los faros **BLANCOS** —
siendo que la luz de un faro es luz **DIRECTA** cruzando el mismo cristalino absorbente que la
imagen (el mismo argumento físico que justifica teñir el pedestal de scatter, acá con más fuerza).

- **Camino de datos (per-ojo, patrón existente):** `GlareController.SetEyeGlobals` lee
  `cataract_yellow` del `EyeState` y publica **`glare_cataract_l` / `glare_cataract_r`** por el mismo
  camino `glare_*_l/r` que halos/destellos/astig. **NO** se escala por `haloScale` ni se apaga con
  `halosEnabled`: es un filtro de absorción del ojo, no un halo (mismo criterio que el astigmatismo).
  Si la clave falta se publica **0** (nunca el ámbar de la lente ANTERIOR — mismo piso que
  `VisionParamsBinder.Map`; una lente creada por un admin desde la tablet puede no estar en los
  defaults embebidos y `MergeMissingParams` indexa por `id`).
- **En el shader:** `v_cataract` se resuelve en el **vertex** (uniforme por instancia) y viaja en
  `p2.w` (el interpolador `p2` pasó de `float3` a `float4`, coste 0: ya ocupaba un registro de 4).
  En el fragment: `col *= lerp(half3(1,1,1), CATARACT_YELLOW, v_cataract)`, **sobre el color emitido**
  (antes del blend aditivo), que es lo correcto: el framebuffer con el que se mezcla ya viene
  filtrado, así que el multiply deja el halo en la MISMA transmitancia que la imagen.
  `v_cataract` **no entra en `angMax`** ⇒ no puede resucitar un billboard colapsado ni cambiar su
  geometría, y con `cataract_yellow = 0` el `lerp` da **1.0 exacto** ⇒ toda lente sin brunescencia
  queda **bit a bit** como antes.
- **Verificado aislando el aporte PROPIO del billboard** (con `Blend One One`, restar el frame con
  los globals de halo/destello en 0 es una separación EXACTA), `ruta_noche`, faro de frente a ~14 m,
  par en la MISMA corrida (toggle del global `glare_cataract_*`, sin recompilar):

  | caso | B/R del halo antes | B/R después | G/R antes | G/R después | ΣR |
  |---|---|---|---|---|---|
  | `catarata` de PROD (`halo_intensity` 0.2), anillo 0–200 px | 0.7203 | **0.3965** | 0.9113 | **0.7872** | idéntico (120.52) |
  | stress `halo`/`destello` al máximo, anillo 0–400 px | 0.6618 | **0.3640** | 0.8336 | **0.7168** | idéntico (41138.6) |

  Los cocientes después/antes son **B/R ×0.550** y **G/R ×0.860** con el canal **R intacto** en los
  dos casos ⇒ es exactamente la transmitancia `(1.0, 0.86, 0.55)` normalizada a rojo = 1, aplicada
  solo a la luz de la fuente. Diferencia de frame: `catarata` de PROD 2.85 % de píxeles (maxd 29,
  meanAbs 0.055 — el halo de una catarata NO es difractivo, es sutil); stress 30 % (maxd 57,
  meanAbs 2.65, evidente sin métrica).
- **Controles al piso de la metodología** (reconstruyendo el shader viejo en la misma corrida,
  comentando el multiply): `panoptix` y `monofocal` publican `glare_cataract = 0.000` ⇒ **24 y 32
  píxeles de 2 057 076 con maxd 53–61 en núcleos HDR de faros** (meanAbs 0.0001), que es el **piso
  de la metodología de reimport** (el control equivalente del pivote da 21–64 px). **No decir
  "bit-idénticas"**: el `lerp` a `t = 0` sí da 1.0 exacto, pero un reimport + re-push de params
  mueve 1 ULP en píxeles HDR saturados y eso flipea un byte. O sea: sin brunescencia no cambia nada
  **perceptible ni medible por encima del piso**.
- **`WindowPortal.shader` NO se toca — y NO es un olvido de la regla del patrón duplicado.** El sol
  del consultorio es **OPACO** (`Queue = Geometry`) ⇒ ya pasa por el pass de visión y por su filtro
  ámbar; agregarle el triple lo **doble-amarillearía** (transmitancia al cuadrado). **Verificado por
  captura** (consultorio, cámara al sol, anillo 30–90 px sin píxeles clippeados): B/R del glare del
  sol `paciente_joven` **0.8619** → `catarata` **0.4939**, cociente **0.573** ≈ 0.55 esperado
  (el color propio de la fuente ya trae B/R 0.88 ⇒ 0.88 × 0.55 = 0.484). El archivo lleva un
  comentario explícito marcando la excepción.
- **Lo que sigue AFUERA del post-proceso para los billboards (deuda acotada, deliberada):**
  `contrast_loss` y el pedestal de scatter tampoco los alcanzan. Se acepta: el pedestal aditivo
  sobre un halo sería **doble contabilidad** (el halo YA es luz dispersada), y `contrast_loss`
  dejaría el halo ~11 % más tenue en `panoptix` — de segundo orden sobre un patrón que además es
  empírico. Moverlo exigiría inyectar el pass en `AfterRenderingTransparents`, lo que **desenfocaría
  los billboards** y recalibraría todo lo medido.
- **Capturas:** `capturas/halos_ambar/` — `{antes,despues}_faro_{catarata,panoptix,monofocal}.png`,
  `{antes,despues}_faro_stress_halofuerte.png`, `_DIAG_fondo_sin_billboard_catarata.png`,
  `control_windowportal_sol_<lente>.png` y las hojas `_HOJA_faro_catarata_antes_vs_despues.png`,
  `_HOJA_stress_halofuerte_antes_vs_despues.png`,
  `_HOJA_CONTROL_panoptix_monofocal_bitidentico.png` (el nombre del archivo dice "bitidentico" y es
  legacy: lo correcto es "al piso de la metodología", 24–32 px con maxd 53–61 — no se renombró para
  no romper las referencias).
- **Referencia:** Pokorny, Smith & Lutze (1987) [11]. **Aproximación perceptual PENDIENTE de
  calibración** (mismo status que `CONTRAST_PIVOT`/`DOF_M_TO_D`): el triple sRGB se eligió por
  plausibilidad perceptual, no por integración espectral contra un observador CIE + transmitancia
  medida. Recalibración fina = tanda futura.

#### El velo CIE también pasa por el filtro ámbar (tanda del velo ámbar) — CERRADO

El **cuarto y último** término del modelo que le faltaba la transmitancia. Era el hallazgo MAYOR de
review de la tanda "pivote adaptativo + halos ámbar": el velo de disability glare se sumaba con
`_GlareVeilTint = (1, 0.95, 0.85)` **sin pasar por `CATARACT_YELLOW`**, así que con una catarata
brunescente el encandilamiento salía **blanco grisáceo** y **des-amarilleaba el frame entero** —
justo el mismo bug que ya se había corregido dos veces (pedestal de scatter y halo del billboard).

```
// VisionPostProcess.shader, pass 1, bloque de _GlareVeil
veil = _GlareVeilTint.rgb * lerp(half3(1,1,1), CATARACT_YELLOW, cataract);
color += veil * L;                       // L = veilAmt · (0.35 + 0.65·glow)
```

- **Fundamento físico (idéntico al del pedestal y del halo):** la luz de la fuente se dispersa
  **DENTRO** del cristalino brunescente y atraviesa el **mismo medio absorbente** antes de llegar a
  la retina ⇒ el straylight llega **ámbar, no blanco** [11][13]. Con `cataract_yellow = 0` el `lerp`
  da **1.0 exacto** ⇒ toda lente sin brunescencia queda igual.
- **`_GlareVeilTint` NO es una transmitancia**: es el tinte de **look** del faro (color cálido de la
  fuente). Multiplicar tinte-de-fuente × transmitancia-del-medio **compone**, no dobla (ver el
  recuadro de §Tinte amarillo → "Orden").
- **El velo es el término DOMINANTE del núcleo, no un detalle.** Medido con el aporte del velo
  aislado (misma corrida, `maxVeil = 0` vs `0.6`): con un faro de frente a 4 m el velo aporta el
  **88 % de la energía R** del disco de 150 px alrededor de la fuente. Por eso el término sin filtrar
  se comía el ámbar de toda la escena.

**Medido — par ANTES/DESPUÉS en la MISMA corrida** (receta 🔧 del §Banco de capturas: se parchea
la línea del velo a la rama vieja, reimport, re-push de params; `catarata` de PROD
`0.8.1-clinical.a1` con `cataract_yellow = 1.0`, ppd 24, `NightTraffic` congelado, `_PupilScene`
pinneado, `glareMiosisGain = 0`, billboards en 0 — el frame `_DIAG` es fondo post-procesado + velo).
Métrica: **B/R en LINEAR** del disco de 150 px centrado en la fuente (`_GlareVeilUV`):

| escenario / encuadre | `_GlareVeil` | B/R núcleo ANTES | DESPUÉS | B/R del velo AISLADO antes → después | fracción de la R del núcleo que pone el velo | lift de luminancia Rec.709 del velo |
|---|---|---|---|---|---|---|
| `ruta_noche`, faro de frente a **4 m** | **0.600** (= `maxVeil`) | **0.8077** | **0.4883** | 0.8665 → 0.5046 (**×0.582**) | **88.2 %** | +1101 % → +960 % (**−12.9 %**) |
| `ruta_noche`, faro de frente a **14 m** | 0.0397 | **0.6235** | **0.4459** | 0.8604 → 0.4780 (**×0.556**) | 46.5 % | +114.6 % → +99.8 % (**−12.9 %**) |
| **consultorio DÍA**, mirando al sol | **0.600** (= `maxVeil`) | **0.7394** | **0.5276** | 0.9496 → 0.5581 (**×0.588**) | 54.1 % | +144.2 % → +125.8 % (**−12.8 %**) |
| consultorio día, pose de diseño | 0.0048 | 0.3960 | 0.3932 | 0.7703 → 0.4358 | 0.85 % | +0.93 % → +0.84 % |

- El cociente del velo aislado es **×0.55–0.59**, o sea la transmitancia `(1.0, 0.86, 0.55)`
  normalizada a rojo = 1 (mismo número que dio el halo del billboard, ×0.550, y el control de
  `WindowPortal`, ×0.573). El excedente sobre 0.55 lo explica la **desaturación** del propio bloque
  (`lerp` hacia la luminancia con peso `veil × 0.12`), que empuja el canal chico hacia arriba.
- **Piso del B/R del núcleo:** una zona dominada por el velo no puede bajar de
  `0.85 × 0.55 = 0.4675` antes de la desaturación ⇒ el **0.4883** del caso de 4 m **es el piso**, no
  un filtrado incompleto. (El objetivo "≤ 0.47" del handoff de review se alcanza a 14 m — 0.4459 —
  y a 4 m queda 0.0208 por encima del piso teórico por la desaturación.)
- **El velo sigue visible y encandilando:** el lift de luminancia solo cae **−12.9 %** en los tres
  casos, que es **exactamente** la pérdida de transmitancia Rec.709 del filtro ámbar (≈13 %, ver
  §Tinte amarillo) — el mismo número que ya se aceptó para el pedestal. No se movió `sensitivity`
  ni `maxVeil`: la pérdida es física.
- 🔴 **El velo CIE NO está gateado por escenario — de día también satura** (corrección: el handoff
  de review asumía que el velo era exclusivo de `ruta_noche`). Las dos fuentes del sol
  (`SunGlare`/`SunGlare2`, `srcEnergy 1.8`, `distanceInvariant`) dan `Σw ≈ 3.6` ⇒
  `baseVeil = 0.9` ⇒ con `straylight ≥ 0.67` el velo **satura en `maxVeil = 0.6`** mirando al sol,
  con `nightPupilFactor` = 1. O sea el fix cambia el consultorio **tanto como la ruta** cuando el
  paciente mira al sol (`velo_{antes,despues}_dia_sol_catarata.png`), y **nada** en la pose de
  diseño (velo 0.0048).
- **Controles (`cataract_yellow = 0` ⇒ el `lerp` a `t = 0`):**
  - `panoptix` **consultorio día / sol** (velo **0.600**, el control fuerte porque el consultorio
    *sí* es bit-reproducible): **51 px de 2 057 076 con maxd 1**. Es el control limpio del extremo
    `t = 0`.
  - `panoptix` `ruta_noche` a 4 m (velo 0.526): 594 px, maxd 47 — **pero el par NEW-vs-NEW del mismo
    frame da exactamente los mismos 594 px / maxd 47** ⇒ es el piso de no-reproducibilidad nocturna
    entre bloques (ver §Banco de capturas), no el cambio de rama.
  - `monofocal` `ruta_noche` (velo **0.000**, la rama no puede tocarlo): 3667 px, maxd 86 — mismo
    piso nocturno, medido con `NightTraffic` congelado.
  - **Bit-return** tras los dos reimports (restaurar la rama nueva y re-renderizar): `catarata`
    **557 px de 2 057 076 con maxd 2**. Redacción correcta para todos estos números: *"N px de 2.06 M
    con maxd ≤ M en núcleos HDR de faros = piso de la metodología de reimport"*, **no**
    "bit-idénticas".
- **Capturas:** `capturas/halos_ambar/`, prefijo `velo_` — `velo_{antes,despues}_faro_catarata_{cerca,d14}.png`,
  `velo_{antes,despues}_DIAG_{fondo,sinvelo}_{catarata,panoptix,monofocal}_{cerca,d14}.png`,
  `velo_{antes,despues}_dia_{sol,pose}_catarata.png`,
  `velo_{antes,despues}_dia_DIAG_{fondo,sinvelo}_{catarata,panoptix}_{sol,pose}.png`,
  `velo_VERIF_DIAG_fondo_{catarata,panoptix}_cerca.png` (bit-return) y las hojas
  `_HOJA_velo_faro_catarata_cerca_antes_vs_despues.png`,
  `_HOJA_velo_faro_catarata_d14_antes_vs_despues.png`,
  `_HOJA_velo_DIAG_nucleo_cerca_antes_vs_despues.png`,
  `_HOJA_velo_dia_sol_catarata_antes_vs_despues.png`,
  `_HOJA_velo_dia_pose_catarata_sin_cambio.png`,
  `_HOJA_velo_CONTROL_panoptix_dia_sol_51px_maxd1.png`.
  Parámetros exactos de cada captura (distancia del faro, `_GlareVeil`, `_GlareVeilUV`, pose):
  `capturas/halos_ambar/_velo_setup.txt`.
- ⚠️ **El anclaje del hallazgo NO es transferible entre corridas.** La review midió B/R 0.5687 sobre
  `_DIAG_fondo_sin_billboard_catarata.png` (tanda anterior) y ese número **no se puede reproducir**:
  `NightTraffic.Start()` re-aleatoriza el tráfico en cada sesión, así que ni el encuadre ni la
  magnitud del velo se repiten (ver §Banco de capturas). Los números de esta sección salen de un
  A/B **en la misma corrida**, que es la única evidencia válida. Por la misma razón la
  descomposición "~40 % de la energía R venía del velo" del handoff no se re-usa: con el aporte del
  velo aislado en la misma corrida la fracción es **88 %** (faro a 4 m) / **46 %** (14 m).

### Dispersión intraocular de catarata (`cataract_scatter`) — P-optica-B

**Por qué existe.** Antes de este param el ÚNICO mecanismo de degradación era el defocus dióptrico, y
con `foco_lejos_m = 9` (valor de prod de la lente `catarata`) el error de enfoque nunca excede
0.111 D del lado lejano ⇒ **nada más allá de ~2 m podía desenfocarse jamás**: el cartel del
pronóstico del tiempo del SmartTV (a 4.86 m del ojo de diseño) se leía perfecto con catarata, que es
el bug reportado. El `contrast_loss` comprime ganancia pero deja contraste Weber legible, el
`cataract_yellow` solo tiñe, y el velo CIE (`straylight`) no aporta nada mirando el TV porque
**exige una `GlareBillboardInstance` en el campo** y decae como `1/θ²`. Faltaba lo que la catarata
real hace: **dispersión intraocular ⇒ pérdida de MTF a TODA distancia + velo difuso sin fuente
puntual.** `cataract_scatter` (0..1, contrato del catálogo, ver `docs/catalogo-lentes.md`) es
deliberadamente **independiente de `cataract_yellow`**: permite catarata brunescente (tiñe, dispersa
poco) vs nuclear dispersora.

**Anclaje bibliográfico.** El straylight de van den Berg crece **log-lineal** con el grado de
catarata (C-Quant [13]: `log s ≈ 0.9–1.0` en un joven normal, `≈ 1.4–1.7` en nuclear moderada,
`≥ 2.0` en avanzada). Mapeando `scatter ∈ [0,1] → log s ∈ [1,2]`, el exceso normalizado
`(10^scatter − 1)/9` se aproxima por **`scatter²`** con error < 0.04 — de ahí el cuadrado en las dos
mitades del modelo.

**(a) Piso de radio (pass 0, dentro de `BlurRadiusDeg`)** — pérdida de MTF independiente de la
distancia y del foco: `scatterDeg = SCATTER_BLUR_DEG · scatter²`, sumado **en cuadratura** al término
dióptrico. `SCATTER_BLUR_DEG = 0.22` está **calibrado con capturas** del optotipo ETDRS a 4.0 m y
**RE-VALIDADO en la etapa C con el kernel correcto** (`capturas/etapaC_F_optotipo_catarata_s*_ppd90.png`,
barrido de `cataract_scatter` sobre la catarata de PROD; el barrido se hace variando el param y no el
`#define`, porque `scatterDeg = SCATTER_BLUR_DEG · s²` y reimportar el shader borra los uniforms del
material — ver gotcha):

| `cataract_scatter` | radio | diámetro | última fila legible **@ ppd 90** (etapa C, con el sesgo de N1) | interpretación clínica |
|---|---|---|---|---|
| 0.0 | 0 | 0 | 20/25 (límite del propio display a ppd 90) | medio claro, sin efecto |
| 0.4 | 0.035° | 4.2′ | ~20/50 | incipiente |
| 0.5 | 0.055° | 6.6′ | 20/63–20/50 | leve |
| **0.6** | **0.079°** | **9.5′** | **20/80, marginal 20/63** | **nuclear moderada** |
| 0.8 | 0.141° | 17′ | 20/125–20/100 | moderada-avanzada |
| 1.0 | 0.220° | 26′ | ~20/160 | avanzada |

> ⚠️ **Esa columna se midió con el sesgo de ancho de N1 y quedó desactualizada.** A `scatter 0.6` el
> radio a ppd 90 es 7.13 px (tier puro) y el tier lo entregaba a 9.24 px (+30 %). Con el descuento
> corregido el radio es exacto y **la agudeza a ppd 90 sube una fila: 20/63, marginal 20/50**
> (medido: RMS de gradiente 3.239 → 4.010, **+23.8 %**;
> `capturas/N1_{antes,despues}_optotipo_catarata_s06_ppd90.png`). El resto de las filas de la tabla
> se movería en la misma dirección (~una fila) y **no se re-midieron una por una**: la ancla que
> importa es la de ppd 24 de abajo.

**Agudeza en el PUNTO DE OPERACIÓN DEL VISOR (ppd 24) — la que ve el paciente.** Medida con la
técnica de FOV recortado + ampliación nearest-neighbour (ppd exactamente 24, sin cambiar el régimen
de píxeles del kernel), `catarata` de PROD, optotipo a 4.0 m:

| configuración | última fila legible @ ppd 24 | grad RMS | captura |
|---|---|---|---|
| sin efecto óptico (params nulos) | **20/50, marginal 20/40** ⇒ **límite del DISPLAY** | 18.56 | `N2_optotipo_ppd24_REF_nitido.png` |
| catarata PROD, `scatter 0.0` | 20/63, marginal 20/50 | 6.79 | `N2_optotipo_ppd24_catarata_s00.png` |
| **catarata PROD, `scatter 0.6`** | **20/100, marginal 20/80** | 4.22 | `N2_optotipo_ppd24_catarata_s06.png` |

- **A la ppd del visor el DISPLAY es co-limitante, no solo la óptica:** el renglón 20/20 a 4 m mide
  2.0 px a ppd 24, así que ni con lente perfecta se resuelve por debajo de ~20/50–20/40. Parte de la
  caída a 20/100 es resolución del display, no `cataract_scatter`. Por eso el barrido de calibración
  se hace ampliado (ppd 90) y este número es el **ancla de lo entregado**, no de la curva.
- **`SCATTER_BLUR_DEG = 0.22` NO se movió**, y N1 no da motivo para moverlo: a ppd 24 el radio de
  `scatter 0.6` es **1.90 px ⇒ `lowW = 0`, disco full-res exacto**, el único de los cuatro regímenes
  que **nunca toca el tier** ⇒ la corrección de N1 lo deja bit a bit igual. Lo entregado (20/100–20/80)
  bordea el objetivo 20/60–20/80 por el lado pesimista, con el display aportando ~0.1–0.2 logMAR de
  eso. Subir el coeficiente empujaría al visor más lejos del objetivo; bajarlo rompería el ancla de
  ppd 90.

**Ajuste aproximado** (no "regla medida" — corrección de review): `logMAR_umbral ≈
log10(diámetro_en_arcmin) − 0.38`. Reproduce **la fila 0.6 por construcción** (es de donde salió el
−0.38) y contra la tabla de arriba se desvía **+0.16 logMAR en 0.4, −0.10 en 0.8 y −0.14 en 1.0** ⇒
tratarla como ajuste con **±0.15 logMAR** de incertidumbre, no como ley. Lo que sí sostiene: el
criterio ingenuo "diámetro del disco = altura de la letra" se queda ~0.3 logMAR corto y fue lo que
llevó a estimar 0.42 en el diseño; con 0.42 la agudeza medida caía a ~20/135 (demasiado) — de ahí la
bajada a 0.22.

**Sobre el sesgo del kernel (hallazgo M4 de review, verificado y descartado).** La calibración de la
etapa B se hizo a través de `DiscBlur9` (kernel fantasma con un delta central al 10 %) y a ppd 170–240,
así que la sospecha razonable era que 0.22 fuera OPTIMISTA y que con el gather correcto el mismo valor
degradara MÁS. **Medido: no se mueve.** Con `DiscBlur13` + tier a 1/16 y a ppd 90, `scatter 0.6` sigue
cayendo en 20/80 con 20/63 marginal — exactamente la banda buscada. La razón es que el disco de 9 taps
**desplazaba** energía (copias fantasma) pero no la **preservaba**: la MTF promedio a esa frecuencia era
parecida; lo único que preservaba nitidez de verdad era el tap central, y su peso bajó de 0.10 a
0.0625, un cambio de segundo orden en agudeza de alto contraste. Donde el kernel viejo SÍ mentía es en
targets de trazo fino y alto contraste sobre fondo claro (el texto del libro): ahí una copia al 12 %
se lee — ver la §medición del libro.

**Validez de las capturas ampliadas — con su sesgo explícito (hallazgo N2 de review).** Toda agudeza
de esta sección se enuncia **con su ppd**, porque el punto de operación del visor y el de las
capturas de calibración **caen en regímenes distintos del kernel**:

| ppd | radio a `scatter 0.6` | régimen | sesgo de ancho vs el modelo |
|---|---|---|---|
| **24** (visor, `renderScale 1.4`) | **1.90 px** | `lowW = 0`, `DiscBlur13` **exacto** | **0 %** |
| 45 (captura G chica) | 3.56 px | cruce, `lowW ≈ 0.24` | +23 % (era +28 % antes de N1) |
| **90** (barrido de calibración) | **7.13 px** | tier puro | **0 %** (era +30 % antes de N1) |
| 108 (captura G grande) | 8.55 px | tier puro | 0 % (era +21 % antes de N1) |

- **Lo que la captura G validó y lo que no.** G compara ppd 45 vs ppd 108 con el **mismo FOV**, y la
  degradación relativa salió consistente — pero **los dos puntos tienen tier**, y antes de N1 el
  sobre-borroneo era casualmente parejo en ambos (~+28 % vs ~+21 %). O sea que el careo salió
  consistente **sin validar nada sobre el punto de operación del visor** (ppd 24, disco puro, el
  único de los cuatro que no toca el tier). La conclusión que estaba escrita acá —*"la PSF es la
  misma a cualquier ppd", "ya es una lupa fiel"*— estaba **sobre-generalizada**: es fiel **dentro**
  de un régimen, no **a través** del cruce.
- **Después de N1 la asimetría es explícita, no menor:** el lado ppd 108 quedó exacto y el lado
  ppd 45 sigue con +23 % ⇒ una captura en la banda 2.6–6 px **sigue siendo una lupa con un ~+25 % de
  ancho de más**. Las capturas en tier puro (ppd ≥ ~26 a `scatter 0.6`) o en disco puro (ppd ≤ ~32
  para radio ≤ 2.5 px) **sí** son lupas fieles.
- **Impacto acotado sobre la calibración:** `log10(1.36) ≈ 0.13 logMAR`, así que lo que el barrido a
  ppd 90 medía como 20/80 antes de N1 correspondía a ~20/63 sin el sesgo — **todavía dentro de la
  banda objetivo 20/60–20/80**. Con N1 el barrido a ppd 90 ya no tiene sesgo de ancho, y a la ppd del
  visor el radio nunca tocó el tier ⇒ **`SCATTER_BLUR_DEG = 0.22` no se movió** (ver §Verificación
  a ppd 24 abajo).

**(b) Pedestal de velo difuso aditivo (pass 1)** — el velo que NO necesita fuente puntual:
```
sc      = saturate(cataract_scatter del ojo)
field   = lerp(1.0, SCATTER_VEIL_NIGHT(0.30), saturate(_PupilScene))   // proxy de iluminancia del campo
pedTint = lerp(SCATTER_TINT(1.0,0.97,0.92), SCATTER_TINT · CATARACT_YELLOW(1.0,0.86,0.55), cataract)
color  += pedTint · SCATTER_VEIL(0.05) · sc² · field
```

- **El pedestal se TIÑE con el mismo ámbar que la imagen** (tanda del tinte ámbar). Fundamento
  físico: la luz que se dispersa **dentro** de un cristalino brunescente atraviesa el **mismo medio
  absorbente** antes de llegar a la retina ⇒ el straylight sale **ámbar, no blanco**. Por eso el
  triple amarillo vive en **un solo `#define CATARACT_YELLOW`** que usan los TRES consumidores de
  este shader (el filtro multiplicativo de la imagen, este pedestal y el **velo CIE** — ver §El velo
  CIE también pasa por el filtro ámbar): si se re-calibra el amarillo, los tres se mueven juntos por
  construcción, no por disciplina.
- **Qué estaba mal.** El pedestal casi neutro (`B/R = 0.92`) se sumaba **encima** de una imagen ya
  desaturada en azul por el filtro, y en sRGB un aditivo fijo levanta más el canal chico que el
  grande ⇒ **des-amarilleaba la catarata**. Medido (consultorio, libro 0.55 m, catarata de PROD,
  `_PupilScene = 0` ⇒ `field = 1.0`), aislando el pedestal con `cataract_scatter` 0.9 vs 0.0 en la
  misma corrida:

  | | B/R del pedestal aislado | B/R del frame `scatter 0.9` | B/R del frame `scatter 0` (referencia) | des-amarilleo |
  |---|---|---|---|---|
  | **antes** | **1.304** (más AZUL que neutro) | 0.7755 | 0.7242 | **+7.1 %** |
  | **después** | **0.742** (ámbar) | **0.7258** | 0.7242 | **+0.2 %** |

  De noche (`ruta_noche`, encuadre tablero, `field = 0.30`): pedestal **1.169 → 0.680**, y el frame
  pasa de **+2.5 %** de des-amarilleo a **−0.3 %** (0.7359 vs 0.7379 de referencia) ⇒ el tinte
  sobrevive al `×0.30` del `field` sin desaparecer ni ensuciar. ⚠️ Los números nocturnos válidos son
  ESTOS (ratio contra referencia propia dentro de una misma corrida); la hoja
  `capturas/tinte_ambar/_HOJA_noche_catarata_antes_vs_despues.png` es **ilustrativa del color
  solamente** — sus dos frames tienen el tráfico en posiciones distintas, así que sus B/R absolutos
  no son comparables entre sí (regla de §Banco de capturas: `ruta_noche` no es bit-reproducible
  entre corridas).
- **El velo sigue visible**: el lift de luminancia Rec.709 del pedestal baja de **+11.2 % a +9.7 %**
  de día y de **+4.7 % a +4.0 %** de noche. Esa caída del **~13 %** es exactamente la pérdida de
  transmitancia que el filtro ámbar ya aplica a la imagen (§Tinte amarillo) — es **el mismo 13 %**, y
  es físicamente lo correcto: la luz parásita también se atenúa al cruzar el medio. ⚠️ Implica que
  para `cataract_yellow = 1` el pedestal es ~13 % más débil en luminancia que cuando se calibró
  `SCATTER_VEIL = 0.05`; **no se movió `SCATTER_VEIL`** porque la pérdida es física y las capturas de
  aceptación muestran el velo con fuerza de sobra (`capturas/tinte_ambar/`).
- **`cataract_yellow` y `cataract_scatter` siguen siendo independientes**: el `lerp` en `cataract`
  tiene su extremo `t = 0` **exactamente** en `SCATTER_TINT`, así que la catarata nuclear dispersora
  sin brunescencia (`cataract_yellow = 0, scatter > 0`) queda **bit-idéntica** — verificado por
  captura, `maxdiff = 0` (ver §Cómo probar).
- **`SCATTER_VEIL = 0.05` está en LINEAR** (proyecto Linear + HDR): sube un negro sRGB 0.05
  (0.002 lin) a sRGB ~0.25 ⇒ lavado perceptual fuerte con casi nula pérdida sobre el blanco. Es el
  número más sensible del diseño (mismo status de dependencia del espacio de color que
  `CONTRAST_PIVOT`). Se dejó en el valor propuesto: las capturas E/J muestran una bruma clara sin
  "fog", y **subirlo no borraría el titular del cartel** — un velo aditivo casi no toca el contraste
  Michelson de un target de alto contraste; lo que destruye es la sensibilidad al contraste bajo.
  Eso es correcto fisiológicamente (el disability glare degrada contraste, no agudeza de alto
  contraste), y es la razón de que agudeza (a) y velo (b) sean dos mitades separadas del modelo.
- **`field`**: de día hay mucha luz que dispersar; de noche el campo es oscuro y el pedestal cae a
  30 % (ahí domina el velo CIE de los faros). Ojo con la asimetría: de noche el pedestal BAJA pero el
  radio SUBE (pupila **4.0 mm**, `PUPIL_NIGHT_MM` vigente — este texto decía 5.5, el valor previo a la
  tanda de pupila mesópica).
- **Orden: después del tinte amarillo, antes del bloque `_GlareVeil`** — misma razón ya documentada
  para el tinte: el filtro amarillo filtra la IMAGEN, no la luz parásita. Los dos velos se componen
  aditivamente y no saturan a blanco (verificado en `ruta_noche`).
- **NO se reusa `_GlareVeilTint`**: puede valer `(0,0,0,0)` si `DisabilityGlareController` todavía no
  publicó ⇒ el pedestal desaparecería. Tinte propio: `pedTint = lerp(SCATTER_TINT,
  SCATTER_TINT·CATARACT_YELLOW, cataract)` — casi blanco sin brunescencia (y ese caso aditivo ya
  desatura solo), ámbar con `cataract_yellow > 0`.

**Por qué NO se usa `glare_pupil_*` como señal de pupila.** `GlareController.cs:72-79` normaliza
`halo_extra_rings` a 0..1, **lo multiplica por `haloScale`** (0.2 de día) **y lo pone en 0 si
`halosEnabled == false`** (botón X del mando). Es un knob de *look* de halo, no un diámetro pupilar:
usarlo colapsaría la pupila a ~1 mm de día y a 0 al togglear halos. La pupila del modelo dióptrico
sale de **`_PupilScene`** (ya modelado con taus asimétricas + miosis por glare en `ScenarioManager`).

### Disability glare (velo por ojo) — CIE general disability glare equation

`DisabilityGlareController` implementa la **CIE general disability glare equation** en su forma simple
de **Stiles-Holladay** (Vos 1984; CIE 146:2002 [1][2][3]): `Lveil = 10·Egl / θ²`, con θ en GRADOS y
validez `1° < θ < 30°`. Egl (iluminancia de la fuente en el ojo) se modela como
`energía · luminancia_mesópica · (1/d²) · facing`. Itera `GlareBillboardInstance.Active` (registro
estático). Por cada fuente activa:

```
θ        = ángulo(gaze, dirección→fuente) en grados;   cull si θ ≥ outer(42°)
angular  = θmin²(1°) / max(θ, θmin)²                                  # CIE 1/θ²  [1][2]
           × (1 − smoothstep(θmax(30°) → outer(42°)))                 # caída suave fin de validez CIE
           # NORMALIZADO a pico 1 en θmin=1° (la constante 10 cd/m²·deg²/lux se absorbe en la
           # normalización: modelo sin unidades; se corrige la FORMA, no la magnitud del ref)
lum      = clamp01(0.02·R + 0.70·G + 0.28·B)                          # luminancia ESCOTÓPICA V'(λ)
                                                                       # CIE 1951 sobre sRGB [4]
                                                                       # (Purkinje: rojo casi no dispersa)
distFactor = refDist²(4 m) / max(d², nearClamp²(2 m))                  # ley 1/d² (iluminancia en ojo)
distGate   = smoothstep entre fullWeight(10 m) y cutoff(20 m)          # fuente lejana no aporta
             (sol: distanceInvariant ⇒ distFactor = distGate = 1)
facing     = SmoothStep sobre dot(haz, −dirFuente→ojo) si srcDir ≠ 0   # umbrales GlareController.FacingLo/Hi
oclusión   = Physics.Linecast(cámara, fuente, occluders) ⇒ aporte 0
w = max(srcEnergy, 0.01) · lum · distFactor · distGate · angular · facing
```

Término de edad CIE 146:2002 [2][3] `[1 + (A/62.5)⁴]` (campo serializado `age`, default **70 años** =
edad media típica de cirugía de catarata [5]): se aplica NORMALIZADO al paciente de referencia
(`CalibAge = 70`) → a la edad default el factor es 1 (no cambia la intensidad global); cambiar `age`
escala el velo relativo a ese paciente (más edad ⇒ más dispersión ⇒ más velo).

Velo final: `veil_ojo = min(maxVeil=0.6, straylight_ojo × Σw × sensitivity(0.25) × pupila × ageFactor)`,
con `pupila` = `nightPupilFactor` en ruta_noche y 1.0 de día. **`nightPupilFactor` vale 3 en escena**
(campo serializado del `DisabilityGlareController` en `Main.unity`), NO el default `1.5` del código: de
noche el velo CIE es **×3**, no ×1.5. Cualquier cuenta de velo hecha con el default del `.cs` sale a la
mitad. Suavizado temporal exponencial
`k = 1 − e^(−5·dt)`. Se publica en `_GlareVeilL/_GlareVeilR` (y en `VisionActivity.VeilL/R`, el valor
suavizado, para el gate de CPU) + UV de la fuente dominante (`_GlareVeilUV`) y tinte cálido
`_GlareVeilTint (1, 0.95, 0.85)`. En el shader:
`L = veil × (0.35 + 0.65·exp(−|uv−src|²/0.05))` (pedestal uniforme + glow en la fuente), se SUMA
`tint × transmitancia_ámbar × L` (straylight aditivo: levanta negros = baja contraste como el velo
real) y desatura `veil × 0.12` usando luminancia **Rec.709** [6]. La `transmitancia_ámbar` es
`lerp(1, CATARACT_YELLOW, cataract_yellow)` — el velo se dispersa DENTRO del cristalino y sale
ámbar; detalle, números y controles en §El velo CIE también pasa por el filtro ámbar. Las magnitudes
son **normalizadas** (energía relativa, faro = 1.0), no cd/m² físicos.

⚠️ **El velo NO está gateado por escenario.** `nightPupilFactor` solo se aplica en `ruta_noche`,
pero las fuentes del sol del consultorio (`SunGlare`/`SunGlare2`, `srcEnergy 1.8`,
`distanceInvariant` ⇒ sin caída por distancia) dan `Σw ≈ 3.6` ⇒ `baseVeil = Σw · 0.25 ≈ 0.9` ⇒
mirando al sol el velo **satura en `maxVeil = 0.6`** también de DÍA (verificado en play). Cualquier
cambio al bloque del velo cambia el consultorio tanto como la ruta: medir los dos escenarios.

**Por qué 1/θ² y no el cono previo:** el término angular viejo (`InverseLerp(42°,5°,θ)²`) era casi
plano hasta 5° y caía lineal hasta 42°; la CIE `1/θ²` está mucho más concentrada en la línea de
visión. Verificado (velo normalizado, misma fuente/energía): frontal θ≈1° → 0.31 en ambos modelos
(intensidad del ref preservada); a **15° el viejo daba ~0.165 (media) y el nuevo ~0.001** — el glare
deja de "lavar" la escena apenas apartás la mirada, como en la clínica.

### Optotipo ETDRS (agudeza funcional en consultorio) — P4.5

Cartilla de agudeza tipo **ETDRS** (Ferris et al. 1982 [10]) en el escenario consultorio, para que el
paciente LEA la línea mínima con cada LIO (medición funcional, no solo demo). Es **contenido de
escena** (sin script de runtime, footprint cero de código): un GameObject raíz `OptotipoETDRS` bajo
`ScenarioContainer/Consultorio`, así que **se enciende/apaga solo con el escenario** (ScenarioManager
activa `Consultorio` de día y lo desactiva de noche — no hace falta UI ni toggle nuevo).

- **Layout**: 11 filas × 5 letras, progresión logMAR en pasos de 0.1, de **logMAR 1.0 (20/200)** arriba
  a **logMAR 0.0 (20/20)** abajo (se quitó la fila extra -0.1/20/16 que originalmente cerraba la
  cartilla — por debajo del límite clínico útil a la distancia de diseño). Cada fila etiquetada al
  margen izquierdo con logMAR + Snellen (20/x). Letras **Sloan** (C D H K N O R S V Z). Alto contraste:
  texto negro (TMP SDF, unlit) sobre panel blanco **unlit** (`Assets/Materials/OptotypeBackground.mat`,
  URP Unlit doble cara) → contraste garantizado, la iluminación de la sala no lo lava.
- **El post-proceso de visión ALCANZA las letras (P4.5-fix)**: por defecto los `TextMeshPro` usan el
  material de fuente `Inter-SemiBold SDF Material` en **cola Transparent (renderQueue 3000)**, y el pass
  se inyecta en `BeforeRenderingTransparents` → las letras se dibujaban DESPUÉS del pass y quedaban
  NÍTIDAS mientras la sala se veía borrosa/astigmática (bug clínico: la cartilla debe leerse BAJO los
  efectos de la LIO, no exenta). Fix: los 22 TMP (`row_*` + `label_*`) usan un material dedicado
  **`Assets/Materials/OptotypeText.mat`** (copia del material de fuente, mismo shader
  `TextMeshPro/Mobile/Distance Field` y mismo atlas `Inter-SemiBold SDF Atlas`) con **`renderQueue = 2450`**
  (rango opaco, ≤2500). Así las letras se dibujan en la fase opaca (después del panel opaco en 2000, que
  las precede y contra el que hacen alpha-blend) y quedan capturadas por el pass → reciben defocus, astig
  y contraste como el resto de la escena. NO se tocó el material de fuente compartido (lo usa la UI de la
  tablet, sigue en 3000); el AA/nitidez base de las letras NO se degrada en cola opaca (verificado con
  captura sin efectos). Verificado en play (consultorio, astig fuerte): letras deformadas junto con la
  sala (`capturas/optotipo_fix_despues.png`) vs base nítida (`capturas/optotipo_fix_base.png`).
- **Calibración angular** (fórmula, en la doc por ser la fuente de verdad clínica): a distancia D la
  letra de agudeza logMAR L subtiende en altura `θ(L) = 5·10^L` arcmin (5 arcmin = 20/20). Altura
  física de la letra: **`h(L) = 2·D·tan(θ(L)/2) = 2·D·tan(2.5·10^L arcmin)`** (arcmin→rad ×π/10800).
  **Distancia de diseño: D = 4.0 m** (estándar ETDRS): las alturas de letra están calibradas para
  4.0 m exactos desde la posición del ojo medida en Editor (`camPos≈(0.274, 1.1176, -0.500)`) hasta la
  cartilla (`(0.274, 1.1176, 3.500)`, a la derecha de la ventana → pared sólida detrás, sin cielo/sol
  que lave; no interfiere con el libro ni la ventana). **Con la cirugía de escala (Consultorio a
  scale 1) las locales del optotipo son ahora directamente los metros de diseño** `(0.274, 1.1176,
  3.5)` — antes iban escaladas por el 0.37 del padre. El ojo de diseño `(0.274, 1.1176, -0.500)` es
  además el destino de `ScenarioManager.RecenterPatient()` (ver zona del rig). Ojo: la posición REAL del ojo depende del
  origen sentado del rig (`ScenarioManager.consultorioOriginPos=(-0.35,-0.05,-0.40)`, mirando +X) y
  de la altura/postura del usuario → la distancia efectiva puede variar ~±1% (≈ ±0.005 logMAR,
  despreciable clínicamente). Verificado: cap-height renderizado del renglón 20/20 =
  **5.818 mm** = target `h(0.0)=2·4·tan(2.5 arcmin)=5.818 mm` ✓. Alturas por fila a 4 m: 20/200=5.82 cm,
  20/100=2.92 cm, 20/40=1.16 cm, 20/20=5.82 mm (última fila). El tamaño de fuente TMP se calibra con
  `fontSize = h(L)/ratio`, `ratio = capHeight/fontSize = 0.07554` medido de la fuente.
- **Limitación tipográfica (desvío documentado)**: no hay fuente Sloan en el proyecto → se usa
  `Inter-SemiBold SDF` (la TMP geométrica de la tablet, `Assets/Resources/TabletFonts`). El **layout
  logMAR y las alturas angulares son correctos**; la tipografía **no es Sloan** ⇒ no es un equivalente
  clínico EXACTO de agudeza absoluta, sí un instrumento **comparativo entre lentes** (misma cartilla,
  distinta LIO). El espaciado inter-letra es el de TMP (no el "1 ancho de letra" ETDRS estricto): la
  cota clínica es la ALTURA (define el MAR), que sí está calibrada.
- **Uso clínico**: con el paciente sentado en consultorio, pedirle que mire la cartilla (pared a su
  derecha) y lea **la línea más baja que pueda** con cada LIO. La agudeza = el logMAR/Snellen de esa
  fila (etiquetado al margen). Comparar entre lentes (aplicar OI/OD y ciclar) da la pérdida funcional
  relativa. A 4 m todas las LIOs del catálogo enfocan (foco lejano 6 m), así que entre las LIOs el
  diferenciador a distancia es el **contraste** (`contrast_loss`): la panoptix lava los
  renglones bajos y el paciente "pierde líneas" respecto de la monofocal — verificado con captura
  nítido vs panoptix. **Ojo con QUÉ catálogo se cita** (esta línea decía "(0.20)", que es el valor del
  catálogo demo y NO el que ve el paciente): el `contrast_loss` de la panoptix es **0.1149295 en PROD**
  (`https://vr.conecta.sh/api/lenses`, la fuente de verdad de lo que corre en el visor) y **0.20 en el
  catálogo demo que viaja en el APK** (`Assets/StreamingAssets/lentes.json`, que solo aporta defaults
  para las claves AUSENTES, vía `CatalogParser.MergeMissingParams` e indexando por `id`). Los dos
  divergen en varios params (`desenfoque_max`, `profundidad_foco_m`, `cataract_scatter`…): al citar un
  número, decir de cuál de los dos sale. La **catarata** es el otro caso: su `cataract_scatter` sí baja la AGUDEZA a
  distancia (a 0.6, **20/100 con marginal 20/80 a la ppd del visor**; 20/63–20/50 en captura ampliada
  a ppd 90 — **siempre citar la ppd**, ver §`cataract_scatter`), y el optotipo es el instrumento con
  el que se calibró `SCATTER_BLUR_DEG`. Ojo al leer agudezas de este optotipo en el visor: a ppd 24 el
  **display** ya topa en ~20/50–20/40 con lente perfecta, así que las filas de abajo no discriminan.

## Decisiones y porqués

- RenderGraph API (no la inyección de comandos vieja) → es la API vigente de URP en Unity 6.5.
- Inyección `BeforeRenderingTransparents` → los billboards de glare (cola transparente, aditivos)
  se componen ENCIMA de la imagen ya borroseada y no se difuminan; el halo se suma después del
  contraste, igual que en Godot (post-quad priority −1 / glare priority 10).
- Billboards procedurales y no glare screen-space → el gather de mips del backbuffer no funciona
  en Quest multiview (los halos desaparecían).
- Muestreo SIEMPRE con `SAMPLE_TEXTURE2D_X` / `SampleSceneDepth` → en Vulkan+multiview el sampleo
  plano de `_CameraDepthTexture` devuelve el depth del ojo izquierdo en el ojo derecho.
- Distancia del libro medida en CPU (`_BookDistanceM` + máscara) → el depth del libro en la mano
  no es confiable para la curva de focos.
- Luminancia mesópica del velo = pesos ESCOTÓPICOS V'(λ) CIE 1951 sobre sRGB (0.02/0.70/0.28) [4] →
  de noche (visión escotópica/mesópica) el rojo casi no dispersa (Purkinje): un piloto rojo encandila
  mucho menos que un faro blanco. Distinta base que la luminancia FOTÓPICA Rec.709 (0.2126/0.7152/
  0.0722) [6] de la desaturación del velo en el shader: aquélla mide cuánto DISPERSA la fuente de
  noche; ésta el gris percibido de la imagen mostrada (cada una con su base correcta, 4.3).
- `distanceInvariant` en el sol → fuente "al infinito": el velo no debe atenuar por 1/d².
- Facing UNIFICADO (4.2): una sola definición de los umbrales `smoothstep(Lo=0.05, Hi=0.35, dot(haz,
  →cámara))` para billboard (halo) y velo. Fuente única = consts `GlareController.FacingLo/FacingHi`;
  C# las usa directo y las publica como globals de shader `_GlareFacingLo/_GlareFacingHi` que lee el
  billboard (HLSL y C# no comparten constantes → publicar es lo robusto: un solo lugar que cambiar).
- Curvas de distancia billboard vs velo DIVERGEN a propósito (4.2): el **velo** usa `1/d²` (iluminancia
  real en el ojo, ley del inverso del cuadrado — es straylight físico). El **billboard** usa fade
  `src_energy·DIST_REF_M/dist` (`1/d`) porque representa el PATRÓN de glare renderizado, de extensión
  angular fija, que debe seguir visible a distancia; con `1/d²` los halos de luces lejanas
  desaparecerían demasiado rápido. Son efectos distintos (patrón perceptual vs iluminancia), no una
  incoherencia.
- Material del billboard COMPARTIDO + parámetros por instancia vía MaterialPropertyBlock → un solo
  material instanciable; `GlareBillboardInstance` existe porque el MPB no se serializa.
- Tope `maxVeil=0.6` y suavizado temporal → confort VR (evitar flashes bruscos).
- `_StreamForceEye` (0/1/2) → la captura mono del stream a tablet (`Assets/Scripts/Runtime/Net/StreamingCapture.cs`)
  puede forzar qué ojo se renderiza sin afectar el render estéreo (default 0).
- **Radio del desenfoque en GRADOS, no en píxeles (P-optica-B)** → un desenfoque es una magnitud
  angular (propiedad del ojo, no del display). Con `BLUR_RADIUS_PX` hardcodeado el mismo paciente
  "veía" distinto según el alto de la ventana (Game View chico ≈ 7 px/° vs Quest 3 ≈ 17 px/°) y la
  agudeza simulada era un accidente de la resolución. Se publica `_VisionPxPerDeg` por ojo desde
  `VisionRendererFeature` y la conversión se hace en el shader.
- **`desenfoque_max` reinterpretado como MULTIPLICADOR del radio físico** (era un cap 0..1 sobre una
  mezcla) → el radio lo da la física (pupila × dioptrías de error); el param queda como palanca
  clínica de exageración. Efecto lateral **deliberado y documentado**: las LIOs con
  `desenfoque_max < 1` quedan MÁS nítidas de cerca que antes (vivity a 40 cm cae en régimen
  sub-píxel), porque un desenfoque angular sub-píxel no se puede mostrar en el display. La palanca
  para exagerar es subir el param, no volver a mezclar con la imagen nítida.
- **Cero passthrough nítido por encima del régimen sub-píxel** → mezclar la imagen original con la
  borrosa deja los bordes de alto contraste intactos (un texto sigue legible con 21 % de nítida
  encima de un blur fuerte). El desenfoque real no tiene una "capa nítida": la fuerza la da el radio.
  **Corolario de la etapa C:** ni siquiera hace falta un `lerp` con la nítida en el régimen
  sub-píxel, porque el kernel del disco YA converge a la identidad cuando el radio → 0 (sus pesos
  suman 1). El `sharpW` que queda es puramente un early-out de perf, y bajarlo a 0.15–0.45 px eliminó
  el "delta nítido residual" del kernel y **redujo a irrelevancia perceptual** —**no eliminó**— el
  acoplamiento clínico con el `renderScale` (corrección de review: la afirmación "se eliminó" que
  estaba acá contradecía el gotcha del `renderScale`, que lo documenta bien). **Cualquier umbral
  expresado en píxeles de target tiene ese acoplamiento por construcción**: a `scatter ≈ 0.3` el
  radio es 0.34 px a ppd 17 (`sharpW = 0.28`, o sea 28 % de imagen nítida) vs 0.475 px a ppd 24
  (`sharpW = 0`). La consecuencia perceptual es nula (a 0.3–0.5 px de radio las dos ramas dan
  prácticamente lo mismo), pero el acoplamiento **existe** y no hay que venderlo como resuelto.
- **Tier de baja a 1/16 con gather de radio variable** (y no las alternativas):
  - **÷4 por eje, no ÷2**: con ÷2 un radio de 48 px full-res son 24 px de baja y ningún kernel
    razonable lo cubre.
  - **Gather de radio variable por píxel, no gaussiana separable H+V**: una separable tiene **un solo
    σ para toda la pantalla**, pero el radio del defocus varía por píxel (depende del depth);
    compositar contra un σ fijo reintroduce exactamente el problema del passthrough nítido con un piso
    más alto. El gather es correcto **y son 2 passes en vez de 3**.
  - **El CoC de cada tap se recalcula desde `SampleSceneDepth` (vía `BlurRadiusDeg`), no se empaqueta
    en alfa**: el formato HDR de Quest es **B10G11R11, sin alfa**.
  - Rechazadas: espiral full-res de 28–32 taps (~21 Gtex/s, y `N` dinámico es rama divergente); mip
    chain (mismos passes, filtro box peor, mip popping); dual-Kawase (8 tile store/load); composite por
    blending de HW (`AddBlitPass` declara el destino con `AccessFlags.Write` ⇒ `DontCare` en GPU
    tile-based ⇒ el blend leería basura).
  - **Un objeto muy desenfocado no derrama sobre un fondo nítido** más allá de lo que alcance el peso
    `scatter-as-gather`, porque la espiral se dimensiona con el CoC del píxel central. El único caso
    clínico es el libro sobre la sala, y su máscara ya se extiende **1.45×** más allá de su radio
    (`BookHolder.cs`). Marcado como atajo deliberado.
- Post-proceso en DOS passes (esfera → cilindro+contraste+velo) → que el smear astigmático opere
  sobre la imagen ya desenfocada (correctitud óptica) reusando el pass 1 que antes solo copiaba;
  coste de taps ≈ igual o menor que el monolítico y sin re-samplear el original (3.2).
- Gate de CPU vía `VisionActivity` (estado C#, no material) → saltear los 4 blits por ojo
  cuando no hay ningún efecto activo (lente perfecta / sin astig / sin velo) es el ahorro más grande
  para el presupuesto GPU de Quest; los halos/starburst (billboards) no dependen del post-proceso, así
  que se siguen viendo con el gate cerrado (3.1).
- Registro estático de fuentes de glare (`GlareBillboardInstance.Active`) → elimina el
  `FindObjectsByType` cada 0.5 s: una fuente nueva encandila el mismo frame y no hay costo de escaneo
  periódico (3.3).
- Base `VisionStateBinder` (P6.1) → los tres suscriptores de `VisionStateChanged` triplicaban el mismo
  boilerplate (espera del singleton, suscripción/despacho por ojo, desuscripción). Extraerlo a una base
  abstracta con un abstract (`ApplyEyeState`) + dos hooks virtuales deja cada subclase con SOLO su
  lógica de dominio, sin homogeneizar las diferencias sutiles (blend demo, reset del velo, facing en
  `OnEnable`). Los nombres de clase MonoBehaviour NO cambian (referencias serializadas en `Main.unity`).

## Gotchas

- **Nunca samplear `_CameraDepthTexture` plano** en shaders de pantalla: bug Vulkan+multiview, el
  ojo derecho recibe el depth del izquierdo. Usar las macros `_X` / `SampleSceneDepth`.
- **TODO el defocus fuera de la máscara del libro cuelga de UNA línea:
  `_pass.ConfigureInput(ScriptableRenderPassInput.Depth)` (`VisionRendererFeature.cs:63`)**, porque el
  tier de Quest **`Mobile_RPAsset` tiene `supportsCameraDepthTexture = false`** (verificado; el
  `PC_RPAsset` lo tiene en `true`, así que el síntoma NO aparecería igual en los dos tiers). Sin ese
  `ConfigureInput`, URP no genera la textura de depth, `SampleSceneDepth` devuelve el valor del plano
  lejano ⇒ `distM` enorme ⇒ `over ≈ 0` ⇒ **el desenfoque desaparece EN SILENCIO en toda la pantalla
  salvo dentro de la máscara del libro** (que usa `_BookDistanceM` de CPU y sigue funcionando). Es un
  fallo especialmente traicionero: la escena se ve "correcta pero nítida", no rota. Síntoma inverso
  útil para diagnosticar: si el LIBRO se desenfoca y el resto de la sala no, sospechar esto antes que
  el catálogo. **`cataract_scatter` es el testigo limpio**: su radio no depende del depth ni de la
  máscara (`shader:361`), así que una lente con scatter alto (`catarata`) tiene que desenfocar TODO el
  campo aunque el depth esté caído.
- **El pass aborta silenciosamente si el target activo es el backbuffer** (`isActiveTargetBackBuffer`):
  si el efecto "no hace nada", revisar en qué punto se inyecta y si hay upscaling/intermedios.
- **`GlareBillboardInstance` debe vivir en un archivo con el nombre exacto de la clase**: Unity
  necesita el MonoScript para serializar la referencia en prefabs (si no, "Missing Script").
- **El MPB no se serializa**: sin `Apply()` en `OnEnable` los billboards de escena/prefab quedarían
  invisibles en el build.
- **`applyDemoBlendOnStart` en `VisionParamsBinder` es opt-in (default `false`)**: al arrancar
  se respetan las lentes reales/persistidas. Activarlo (inspector) solo para reproducir el blend
  demo monofocal(OI)/panoptix(OD); no dejarlo prendido en la escena o pisa las lentes reales.
- **Las fuentes de glare se registran solas** (`GlareBillboardInstance.OnEnable/OnDisable` →
  `Active`): `DisabilityGlareController` itera esa lista, sin `FindObjectsByType` ni lag de 0.5 s.
  El `[RuntimeInitializeOnLoadMethod(SubsystemRegistration)]` limpia la lista al entrar a Play; si se
  agregan fuentes por instanciación en runtime, aparecen en cuanto su `OnEnable` corre (mismo frame).
- **El gate de CPU depende de que los binders publiquen en `VisionActivity`**: si agregás un efecto
  nuevo al post-proceso (uniforme por ojo), sumá su señal a `VisionActivity` o el gate lo apagará
  cuando el resto esté en cero (el efecto no se vería). El gate usa `desenfoque_max` como proxy del
  blur (conservador: no sabe per-pixel si algo está fuera de foco).
- **La oclusión usa `Physics.Linecast` con `occluders = ~0`**: geometría sin collider NO ocluye el
  velo (los billboards mismos no tienen collider, así que no se auto-bloquean).
- **Precedencia astigmatismo catálogo vs live (P4.4)**: dos fuentes escriben los MISMOS globals
  `glare_astig_l/r` + `glare_astig_angle_l/r` — el catálogo (`astig_magnitude`/`astig_axis_deg` vía
  `GlareController.SetEyeGlobals` en cada `VisionStateChanged`) y el comando live no-persistente
  `set_astigmatism` (tablet → `NetworkController` → `SetAstigmatism`). Regla: **último que escribe
  gana**. Consecuencia sutil: un `set_astigmatism` live queda pisado por el SIGUIENTE
  `VisionStateChanged` (cualquier `ApplyLens`/`OverrideParams` de cualquier param re-asserta el valor
  del catálogo). Si se quisiera que el live tenga prioridad estable habría que introducir un flag de
  override — deuda, no implementada (el pipeline actual prioriza que el catálogo sea la fuente
  persistente y el live un ajuste efímero).
- **Los umbrales de facing se publican en `GlareController.OnEnable`, no en `Start`**: `Start` es
  coroutine y espera a `DataManager`; publicar ahí dejaba `_GlareFacingLo/Hi` en 0/0 los primeros
  frames → `smoothstep(0,0,x)` degenerado en el billboard (facing binario/indefinido). `OnEnable`
  corre antes del primer render y no depende de `DataManager`.
- **`destello_rayos` es CANTIDAD de rayos, no intensidad**: nunca se escala por escenario
  (la intensidad la da `destello_intensity`).
- **Los uniforms del material persisten en el asset en editor**: el material de
  `VisionRendererFeature` y el de `VisionParamsBinder`/`BookHolder` deben ser el MISMO asset.
- **El velo depende de `LateUpdate` (player loop), no solo del render**: al validar por MCP,
  `unity_graphics_game_capture` corre `Camera.Render` pero NO avanza el player loop si el Game view no
  tiene foco → `DisabilityGlareController.LateUpdate` no tickea y el velo queda en su último valor (0
  al arrancar). Para evidencia sin foco: forzar el cómputo (invocar `LateUpdate` por reflexión hasta
  converger el suavizado) o mantener el Game view visible. El blur/astig/contraste NO sufren esto: se
  fijan por evento (binders) y los lee el shader en el render.
- **`renderScale = 1.4` en `Mobile_RPAsset`: píxel del render target ≠ píxel del display.**
  `_VisionPxPerDeg` se calcula con `cameraTargetDescriptor.height`, que YA incluye el render scale
  (verificado: Game View de 800 px de alto ⇒ target de 1120 ⇒ ppd publicado 16.9 en vez de 12.1). Eso
  es lo correcto (el blur se aplica en el espacio del target), pero implica que `SUBPIXEL_LO/HI_PX`
  están en píxeles de target: el umbral "sub-píxel" es 1/1.4 del píxel real del display, o sea
  ligeramente conservador (se desenfoca un poco antes de lo estrictamente necesario). Si alguien
  cambia el render scale, el look del régimen sub-píxel cambia (nada más).
- **Un blur ANGULAR se puede "ampliar" bajando el FOV de la cámara para verificarlo.** Estrechar el
  FOV sube `_VisionPxPerDeg` y agranda letras y radio de blur en la MISMA proporción ⇒ la captura es
  una lupa fiel de la imagen retiniana. Es la forma de juzgar legibilidad de líneas ETDRS que a
  ppd 17 miden 1-2 px (a la ppd del Quest 3, el renglón 20/20 a 4 m **no es resoluble por el display**
  ni en el mejor caso). En la etapa B esto **no era una lupa fiel**: a ppd 168 se veían las 9 copias
  discretas del `DiscBlur9`, y esa fue la razón por la que la calibración de `SCATTER_BLUR_DEG` y la
  verificación de Bug 2 quedaron sesgadas.
  **Con el kernel de la etapa C es una lupa fiel SOLO dentro de un régimen, no a través del cruce**
  (hallazgo N2 de review; antes esta línea decía "la PSF es la misma a cualquier ppd" y eso era
  **falso**). Es fiel si el radio resultante cae en **disco puro** (≤ 2.5 px) o en **tier puro**
  (≥ 6.3 px); si cae en la banda 2.6–6 px la lupa exagera el ancho **~+25 %** (ver el hueco de
  calidad en el punto 7 del pass 0). Regla práctica: **calcular el radio en px a la ppd de la captura
  antes de creerle**, y enunciar toda agudeza con su ppd.
  **Alternativa aún más fiel cuando lo que importa es lo que ve el visor** (la que se usó en la
  captura D de la etapa C): renderizar a la ppd del visor con el **FOV recortado** (dividir
  `tan(fov/2)` y el alto del RT por el mismo factor deja el ppd EXACTAMENTE igual y recorta el campo)
  y después ampliar el PNG por **nearest-neighbour**. Eso es ampliación de imagen pura: no toca el
  régimen de píxeles del kernel. El zoom por FOV angosto sí lo cambia.
- **El smear astigmático (`DirBlur`) son 7 taps sobre TODO el largo del smear** ⇒ a ppd altas la
  separación entre taps pasa de ~2 px y se ven 7 copias en vez de un trazo continuo (marcado en el
  shader con `// SIM: atajo deliberado`). A la ppd del visor (23.8 con `renderScale 1.4`) el largo a
  magnitud 0.6 son ~18.6 px ⇒ 2.7 px de separación, aceptable; en una captura ampliada por FOV
  angosto (ppd 90) el artefacto es evidente — ver `capturas/etapaC_I_optotipo_catarata_astig45_ppd90.png`.
  Es deuda PRE-existente (con `ASTIG_BLUR_PX = 22` la separación ya era 7.3 px), no una regresión de
  la etapa C.
- **Reimportar el shader borra los uniforms del material que no están en `Properties`.** Este shader
  no tiene bloque `Properties`: TODOS los `_XxxL/_XxxR` se escriben por `Material.SetFloat` (patrón
  del repo). Tras un `AssetDatabase.ImportAsset` del `.shader` (típico al iterar constantes en play
  mode) el material queda con esos valores en cero y `Material.GetFloat` loguea
  `"doesn't have a float or range property '_CataractScatterL'"`. No es un error del shader:
  hay que re-pushear los params (`DataManager.ApplyLens` / `OverrideParams`) antes de volver a
  capturar. Pasó durante la calibración de la etapa B y arruinó una tanda de capturas.
- **El gate OFF no es byte-idéntico al pass corriendo con params nulos, y la causa es el
  `renderScale` — NO la precisión del ping-pong ni el MSAA.** Medido (etapa C, mismo encuadre, mismo
  estado de escena, misma ruta de captura, dos `Camera.Render()` consecutivos que dan diff EXACTO 0
  como control):

  | `renderScale` | bytes distintos (de 3.07 M) | max Δ | Δ medio |
  |---|---|---|---|
  | **1.0** | **36 (0.001 %)** | 152 | 38 |
  | **1.4** (el del `Mobile_RPAsset`) | **575 096 (18.7 %)** | 192 | 3.3 |

  Con `QualitySettings.antiAliasing` en 4 y en 0 los números son **idénticos** ⇒ **no es MSAA**. A
  `renderScale = 1.0` las dos rutas son esencialmente byte-idénticas ⇒ **tampoco es la precisión
  B10G11R11 del round-trip por `_VisionTemp`** (ese round-trip existe en los dos casos). Las
  diferencias están concentradas en los **bordes de alta frecuencia** (marco de la ventana, texto del
  TV, detalle del paisaje) y las zonas planas difieren 1–2 LSB — ver
  `capturas/etapaC_A_diff_x16.png` (mapa de diferencia amplificado ×16),
  `etapaC_A_gateOFF.png` y `etapaC_A_passON_nulo.png`.
  El mecanismo exacto dentro de URP **no está identificado**; el candidato es la ruta del *final blit*
  con rescale (los blits extra cambian cuál es el `activeColorTexture` que llega al blit final que
  hace el 1792→1280). **Regla práctica sin cambios:** al comparar capturas "sin efecto" contra una
  referencia, usar la MISMA ruta (gate ON o gate OFF) o la diferencia se atribuye mal. Verificado
  también que el post-proceso **sí se aplica** con `renderScale = 1.0` (scatter 0 vs 0.6 difiere en el
  99.3 % de los bytes en los dos render scales): no hay early-out escondido.
- **`Shader.GetGlobalVector("_VisionPxPerDeg")` / `"_VisionLowTexel"` desde C# NO devuelven el valor
  vigente.** Desde la etapa C se publican con `cmd.SetGlobalVector` dentro del graph, y los globals
  de command buffer no se reflejan en la tabla inmediata que lee `Shader.GetGlobalVector` (devuelve
  el último valor puesto por `Shader.SetGlobalVector`, o sea posiblemente basura vieja). Para
  diagnosticar el ppd hay que recalcular `0.5 · H_target · |m11| · π/180` en C#. **Truco de
  verificación:** poner los dos globals en cero con `Shader.SetGlobalVector` y comprobar que el blur
  sigue apareciendo — si apareciera por leer el valor rancio, el pass nuevo no está corriendo.
- **El dither de la espiral es IDÉNTICO en los dos ojos** (hallazgo MENOR de review, pendiente de
  validar en el visor). `FragLowGather` rota la fase con un hash de **coordenada de pantalla**, así
  que los dos ojos usan la misma secuencia de ángulos **aplicada a contenidos distintos** (cada ojo
  ve la escena desde su posición) ⇒ el ruido residual del gather queda **descorrelacionado entre
  ojos**. Es candidato a **rivalidad binocular / shimmer estéreo**, y el Game View mono **no puede
  detectarlo**: va en la misma lista de verificación en dispositivo que R1. Si aparece, las opciones
  son sumar `unity_StereoEyeIndex` al hash (los desacopla del todo, no los correlaciona) o bajar el
  residuo con `LOW_TAPS → 32`.
- 🔴 **`OptotipoETDRS` está DESACTIVADO en `Main.unity` (`m_IsActive: 0`, también en HEAD).** La
  cartilla ETDRS **no aparece** en el consultorio ni en el Editor ni en el build, aunque esta doc
  describe su calibración a 4 m como instrumento clínico y dice que "se enciende/apaga solo con el
  escenario". El escenario activa `Consultorio`, pero el hijo `OptotipoETDRS` tiene su propio
  `activeSelf = false` ⇒ nunca se ve. Verificado en play (v0.9.1): hubo que activarlo a mano para
  poder capturar el encuadre del optotipo; la posición y la distancia sí son las de diseño
  (`(0.274, 1.1176, 3.5)`, **4.0000 m** exactos desde el ojo de diseño). **No se tocó la escena**
  (es mutación de escena, y no estaba en scope): decidir si se enciende y quién lo hace.
  Efecto lateral: `GameObject.Find("OptotipoETDRS")` devuelve **null** (no ve inactivos) — usar
  el hijo por nombre desde el transform de `Consultorio`.
- **El post-proceso solo alcanza el rango OPACO (renderQueue ≤ 2500)**: el pass se inyecta en
  `BeforeRenderingTransparents`, así que TODO lo que se dibuja en cola Transparent (≥2501) — TMP SDF
  por defecto (3000), billboards de glare, HUD overlay — queda EXENTO de blur/astig/contraste/velo.
  Es deliberado para los billboards y el HUD (deben componerse nítidos ENCIMA), pero fue un bug para el
  optotipo (letras nítidas sobre sala borrosa). Regla: **cualquier contenido que DEBA verse afectado por
  la visión del paciente tiene que estar en el rango opaco** (≤2500). Para TMP: material dedicado con
  `renderQueue` forzado a 2450 (ver `OptotypeText.mat`), NUNCA mutar el material de fuente compartido
  (rompería la cola de la UI de la tablet, que comparte `Inter-SemiBold SDF`).

## Cómo probar

1. Abrir `Assets/Scenes/Main.unity` y entrar en Play (o build Quest vía `unity_build`).
2. El HUD muestra FPS, escenario, lente por ojo y halos. Controles: **A** cicla lente OI, **B**
   cicla OD, **X** toggle halos, **Y** alterna consultorio ↔ ruta_noche.
3. Blend demo (opt-in): al arrancar se respetan las lentes reales. Para el demo por ojo activar
   `applyDemoBlendOnStart` en el inspector de `VisionParamsBinder` → OI monofocal (nítido lejos,
   libro ilegible) vs OD panoptix (halos y anillos marcados de noche, libro legible). Cerrar un
   ojo por vez para comparar.
4. Ruta nocturna: mirar un auto que viene de frente → faros blancos generan velo (más con
   panoptix, `straylight=1.0`, que con monofocal, `0.15`); los pilotos rojos casi no encandilan;
   al apartar la mirada el velo cae MUY rápido (CIE `1/θ²`: ~0.31 frontal → ~0.001 a 15°).
   Nota: el velo lo computa `LateUpdate`; en el editor sin foco del Game view el player loop puede
   no tickear (ver Gotchas) — validar en Play con el Game view visible o en build.
   **Contraste nocturno (v0.9.1):** bajar la vista al tablero y ciclar `joven → vivity → panoptix →
   catarata`. Lo que se debe ver es **menos contraste y halos**, con el bisel negro del CarPlay
   **NEGRO** y la cabina oscura; si aparece una **niebla gris/beige uniforme** sobre todo el campo,
   el pivote adaptativo no está actuando (revisar que `_PupilScene` llegue a ~1 de noche — es la
   señal de adaptación que lo mueve). Y con `catarata` los halos de los faros tienen que verse
   **ÁMBAR**, no blancos.
   **Velo ámbar (tanda del velo ámbar):** mirando de frente un faro cercano con `catarata`, el
   encandilamiento que lava la escena tiene que ser un **velo ÁMBAR** (la escena queda brunescente
   y más lavada); si el velo se ve **gris/blanco** y "borra" el amarillo de la catarata, el filtro
   del velo no está actuando. Lo mismo mirando al **sol** en consultorio (el velo diurno también
   satura: no es un efecto exclusivo de la ruta).
5. Consultorio: acercar/alejar el libro; con monofocal se desenfoca a ~40 cm y mejora al brazo
   extendido; el sol por la ventana produce destello (starburst) pero halos casi nulos (día).
6. Aislado: agregar `GlareTestRig` a la escena para 3 lámparas de prueba con billboards.
7. **Optotipo ETDRS (consultorio):** en consultorio, mirar la cartilla `OptotipoETDRS` en la pared a
   la derecha (a 4 m). Pedirle al paciente que lea la línea más baja legible con cada LIO; la fila
   está etiquetada con su agudeza (logMAR / 20-x). Comparar nítido (sin lente) vs una LIO con
   `contrast_loss` (p.ej. panoptix) → se pierden los renglones bajos por pérdida de contraste. A 4 m
   todas las LIOs enfocan (foco lejano 6 m): el diferenciador a distancia es el contraste, no el blur.

### Banco de capturas comparables (cómo medir sin engañarse)

Receta usada por los bancos de día (`capturas/matriz_libro/`), tablero (`capturas/matriz_tablero/`),
tinte ámbar (`capturas/tinte_ambar/`), contraste nocturno (`capturas/contraste_nocturno/`), halos
ámbar (`capturas/halos_ambar/`) y velo ámbar (`capturas/halos_ambar/velo_*`). Vale para cualquier
medición antes/después:

- **Determinismo**: `NightTraffic.enabled = false`, astigmatismo y `_GlareVeil*` en 0, HUD off, y
  `_PupilScene` **pinneado** — no basta el global: hay que setear por reflexión `_pupilCurrent` **y**
  `_pupilTarget` de `ScenarioManager`, o el `Update` lo arrastra de vuelta. Si el efecto medido
  necesita el velo ENCENDIDO, además **`glareMiosisGain = 0`** (con velo alto la miosis baja el
  target de pupila y arruina el pin).
- **Medir CON velo (banco del velo ámbar).** Tres cosas que cuestan una tanda si se ignoran:
  1. El velo lo calcula `LateUpdate` y **el player loop está congelado si el Editor no tiene foco**
     (ver Gotchas) ⇒ invocar `DisabilityGlareController.LateUpdate` por reflexión ~200 veces hasta
     que el suavizado exponencial converja (y `SunSkyAnchor.LateUpdate` de día, que es lo que
     coloca las fuentes del sol sobre su dirección fija).
  2. **El término CIE `1/θ²` es AGUDÍSIMO**: apuntar al punto MEDIO de los dos faros de un auto
     deja θ ≈ 4.3° y el velo cae a **0.039**; apuntar exactamente a UN faro (θ → 0 ⇒ clamp a 1° ⇒
     `angular = 1`) a 4 m lo lleva a **0.600 = `maxVeil`**. Para medir el velo hay que apuntarle al
     faro, no "hacia el auto".
  3. Posicionar el auto por el **faro**, no por el origen del prefab: los `Headlights` están
     ~2.45 m delante del origen (iterar 2-3 veces `carZ += (d_objetivo − d_medida)` converge a
     <5 mm). Receta completa y valores por captura: `capturas/halos_ambar/_velo_setup.txt`.
- **Pose**: `SwitchTo(escenario)` + **`RecenterPatient()`** deja la cámara exactamente en la pose de
  diseño (verificado: conductor de `ruta_noche` en `(-0.2200, 1.0676, -0.4200)`, `fwd = (0,0,1)`).
- **ppd explícito**: `cam.aspect` + `fieldOfView` fijados a mano; `ppd = 0.5·(H·1.4)·(π/180)/tan(fov/2)`.
  Con `H = 1134` y `fov_v = 60°` ⇒ **ppd 24**, el punto de operación del visor. El aspect cambia el
  **hFOV** pero no la ppd (que la fija el eje vertical): para que entren los dos displays del tablero
  hay que ir a `1814×1134` (hFOV 85.4°), no a un cuadrado.
- ⚠️ **Descartar el PRIMER `cam.Render()` después de un `SwitchTo`**: es un frame de warm-up (historia
  temporal / jitter sin asentar). Medido: la celda `joven` de dos corridas idénticas difería hasta
  **183 LSB** en el 23 % de los píxeles siendo el **mismo** código de shader, mientras las celdas 2ª a
  6ª de las mismas corridas salían **bit-idénticas**. Tirar un render a la basura antes de medir.
- ⚠️ **`ruta_noche` NO es bit-reproducible entre corridas** (algo sigue animando incluso con
  `NightTraffic` apagado): el ruido de frame completo es de ~2/255 en R. Toda comparación nocturna
  tiene que hacerse **dentro de una misma corrida** (renderizar la referencia y la variante en la
  misma llamada). El consultorio **sí** es bit-reproducible a partir del 2º render.
- **Aislar un término del modelo** = renderizar el mismo lente con el param en 0 y en su valor, en la
  misma corrida, y restar. Así se midió el pedestal de scatter (`cataract_scatter` 0.9 vs 0.0) y así
  se demostró que el tinte ámbar del pedestal deja **`maxdiff = 0`** para todo lente con
  `cataract_scatter = 0` (monofocal) o `cataract_yellow = 0`.
  - **Para el velo CIE la palanca es `maxVeil`**, no un param del catálogo: poner
    `DisabilityGlareController.maxVeil = 0`, re-converger y renderizar da el frame **sin velo** con
    todo lo demás intacto ⇒ restando en LINEAR se obtiene el aporte EXACTO del velo (es aditivo).
    Con eso se midió que el velo pone el **88 %** de la energía R del núcleo del faro y que su lift
    de luminancia cae solo **−12.9 %** al teñirse (= el 13 % de transmitancia del filtro ámbar).
- **Aislar el aporte de un BILLBOARD** = restar el frame con sus globals (`glare_halo_*`,
  `glare_star_*`, `glare_pupil_*`) en 0. Con `Blend One One` la separación es **exacta** en linear
  (descartando píxeles clippeados): así se midió el B/R del halo ámbar (v0.9.1). Muy superior a medir
  el color de un anillo alrededor de la fuente: ahí **domina el fondo**, que ya viene teñido por el
  post-proceso, y el efecto se diluye (el mismo par dio ×0.97 midiendo el anillo crudo y ×0.55 —
  el valor real — midiendo el aporte aislado).
- 🔧 **A/B del MISMO shader en UNA sola corrida (la receta más fuerte, v0.9.1).** Para comparar dos
  ramas de código de shader sin salir de la corrida (y por lo tanto content-matched al 100 %):
  desde `unity_execute_code`, **en play mode**, (1) renderizar la rama nueva y guardar los píxeles en
  memoria; (2) `File.WriteAllText` del `.shader` con el `#define`/línea parcheada a la rama vieja +
  `AssetDatabase.ImportAsset(..., ForceUpdate)`; (3) **re-pushear los params** (`DataManager.ApplyLens`:
  reimportar el shader borra los uniforms del material que no están en `Properties` — gotcha de
  arriba), 2 renders de warm-up, y medir; (4) restaurar el archivo, reimportar y **re-renderizar la
  rama nueva para verificar que vuelve bit a bit** (piso medido: **9–64 píxeles de 2 057 076**,
  meanAbs 0.0000, incluidos dos reimports). Un `ImportAsset` de `.shader` **no** dispara domain
  reload, así que el play mode sobrevive; editar un `.cs` **sí** lo dispara y corta la corrida.
  ⚠️⚠️ **El re-push del paso (3) también hace falta cuando lo que se reimporta es
  `GlareBillboard.shader`, y ahí el síntoma es MUCHO más grosero: los halos DESAPARECEN.** Medido
  (bloque de diagnóstico F1): un `ImportAsset` **no-op** de `GlareBillboard.shader` sin re-pushear
  deja el frame de `ruta_noche` con brillo medio **19.20** contra **25.43**, y **2 041 122 px de
  2 057 076 (99 %) distintos, maxd 102**; un `DataManager.ApplyLens` posterior lo devuelve al piso
  (24 px). Los globals `glare_*_l/r` sobreviven al reimport (verificado: `glare_halo_l` sigue en
  0.2085), así que lo que se pierde son los datos **por instancia** del billboard, no los de lente.
  **Consecuencia práctica: cualquier `grab()` del harness tiene que llamar `ApplyLens` INMEDIATAMENTE
  antes**, no una vez por bloque — una tanda entera de "control positivo" se midió mal por esto y
  hubo que repetirla, y los números malos parecían plausibles (99 % de píxeles distintos se lee como
  "el cambio rompió todo" cuando en realidad era el harness).
  ⚠️ El bloque entero puede pasarse de los **30 s** del bridge MCP: el código igual **termina** en el
  main thread (los PNG se escriben y el archivo se restaura), pero se **pierde el log de vuelta** ⇒
  escribir los resultados a disco o partir el bloque; y ante un timeout, **verificar primero que el
  `.shader` quedó restaurado**.
- ⚠️ **Entre PLAY SESSIONS, `ruta_noche` no es comparable a frame completo: `NightTraffic.Start()`
  re-aleatoriza posiciones y colores de los autos.** Medido (mismo código, mismo encuadre, dos
  sesiones): el frame completo del encuadre `frente` se mueve **7–8 LSB de media** y el encuadre
  `tablero` 16 % de los píxeles. Lo que **sí** es bit-idéntico entre sesiones son los recortes que el
  tráfico no puede alcanzar: `display1` y `display2` salieron **exactamente iguales** con dos lentes
  de control; la "cabina" (45 % inferior) tiene un piso de **0.13 byte** (ve la ruta por el
  parabrisas). Regla: comparación nocturna entre sesiones **sólo** en recortes de cabina/tablero, y
  siempre con `paciente_joven` (gate OFF) **y** `monofocal` (gate ON, `contrast_loss = 0`) como
  controles de piso — el par de controles es lo que distingue "el fix hizo algo" de "la escena se
  movió".
- **Resolver componentes con `Resources.FindObjectsOfTypeAll`, no `FindFirstObjectByType`.** En el
  harness de capturas se apagan/prenden escenarios y componentes, y `FindFirstObjectByType` **no ve
  objetos inactivos**: `NightTraffic` no existe mientras estás en consultorio, `BookHolder` no existe
  mientras estás en `ruta_noche`, y el `HudController` desaparece en cuanto lo apagás para la captura.
  Tres `NullReferenceException` de esta misma causa en una tanda.

- 🧪 **REFERENCIA EXACTA para probar un early-out (técnica nueva, F1).** Cuando el cambio es "saltear
  trabajo cuyo resultado ya está garantizado", comparar ANTES vs DESPUÉS **no alcanza para decidir
  quién tiene razón** si sale un delta: puede ser el early-out que se equivoca o la rama larga que
  acumula redondeo. La salida es construir una **TERCERA rama que calcule lo mismo por un camino
  aritméticamente exacto** y comparar las dos contra ella. Para el pass 3 fue: sin early-out
  **y con `LOW_TAPS = 1`** (con `g = 0` un solo tap con `w = 1` da `sum/wsum = c` sin acumular nada).
  Resultado: DESPUÉS = referencia con **0 px / maxd 0**, ANTES = 9 621 px ⇒ el "delta" era el error de
  la rama vieja. **Bonus:** la misma comparación prueba la COBERTURA del early-out sin instrumentar el
  shader — si algún píxel tuviera `g > 0`, `LOW_TAPS = 1` vs `24` diferiría enormemente ahí (control:
  el mismo par con `monofocal` a 25 cm da 610 313 px / maxd 51, porque ahí `g > 0` de verdad).
- 📋 **Tabla F1 (los dos early-outs de perf, todo en el Editor, ppd 24, `1814×1134`).** La columna
  "piso" es DESPUÉS vs DESPUÉS medido en la MISMA corrida tras los mismos reimports:

  | caso | escenario / lente | ANTES vs DESPUÉS | piso | veredicto |
  |---|---|---|---|---|
  | C1 libro 0.55 m | consultorio / `catarata` PROD | **9 621 px, maxd 2** | 0 px | delta = redondeo de la rama VIEJA (DESPUÉS = referencia exacta, 0 px) |
  | C3 libro 0.25 m | consultorio / `monofocal` PROD | 44 px, maxd 1 | 2 px | `g > 0` en la máscara: camino no-degenerado INTACTO |
  | C4 libro 0.15 m | consultorio / `catarata` PROD | 74 px, maxd 1 | 0 px | `g` grande en la máscara: intacto |
  | N1 tablero | `ruta_noche` / `catarata` PROD | 1 073 px, maxd 2 | 39 px | ídem C1 (DESPUÉS vs referencia = 39 px = el piso) |
  | N2 frente | `ruta_noche` / `panoptix` | 24 px, maxd 9 | **24 px** | en el piso |
  | H1/H2/H3 halos | `ruta_noche`, clip del billboard | 24 / 18 / 47 px | 24 / 18 / 47 | **en el piso exacto** |

  Capturas en `capturas/perf_f1/` (`*_ANTES.png` / `*_DESPUES.png`), log completo del harness en
  `capturas/perf_f1/_f1_log.txt`.
  ⚠️ **`maxd 9` y `maxd 19` en los encuadres con faros NO son ruido de escena**: son el piso de
  recompilación del shader en núcleos HDR clippeados (coherente con el "maxd ≤ 61" ya documentado).
  Lo que los identifica como piso es que el par ANTES/DESPUÉS da **exactamente los mismos** píxeles y
  sumas que el par DESPUÉS/DESPUÉS.
  ⚠️ `ruta_noche` sí resultó **bit-reproducible DENTRO de esta corrida** (dos `grab()` seguidos sin
  tocar nada: 0 px), con el player loop congelado (Editor sin foco) y `NightTraffic` apagado. La
  advertencia de "algo sigue animando" vale entre corridas, no dentro de una.
- ⚠️ **WARNING NUEVO Y BENIGNO en `VisionPostProcess.shader` (F1):** el early-out del pass 3 mete un
  `return` condicional antes del loop del gather, así que FXC avisa `gradient instruction used in a
  loop with varying iteration; partial derivatives may have undefined value` (2 mensajes, variante
  Android del pass `VisionLowGather`). **NO sale por `unity_console_log`**: sólo por
  `ShaderUtil.GetShaderMessages` al reimportar. Es inocuo porque `lowDesc` se copia del
  `cameraTargetDescriptor` (**1 solo mip**, `useMipMap = false`), así que el LOD siempre resuelve a 0
  por más indefinida que sea la derivada — y está confirmado empíricamente por el `0 px / maxd 0`
  contra la referencia exacta. Si algún día molesta, se silencia pasando a
  `SAMPLE_TEXTURE2D_X_LOD(..., 0)` dentro del loop (bit-idéntico mientras la textura de baja siga sin
  mips), pero eso exige recorrer la tabla F1 de nuevo.

**Los dos encuadres del banco nocturno (ahora con números exactos, P-tablero-emisivo).** Desde la
pose de diseño del conductor (`SwitchTo("ruta_noche")` + `RecenterPatient()`), el encuadre se fija
rotando SOLO la cámara (`cam.transform.rotation = Quaternion.Euler(pitch, yaw, 0)`, el rig no se
toca):

| encuadre | pitch | yaw | qué mira |
|---|---|---|---|
| `frente` | **0** | **0** | ruta al frente; el tablero cae en el tercio inferior |
| `tablero` | **17.73°** | **13.56°** | el conductor baja la vista entre los dos displays |

El `tablero` se **recuperó por ajuste** contra `capturas/matriz_tablero/_FACT_luzBASE_joven.png`
(la fase 1 no lo había anotado): minimizando el `meanAbs` del 45 % inferior del frame se llega a
**0.94/255**, o sea dentro del ruido nocturno documentado. El mínimo es **agudo**: ±0.5° lo sube a
3–9/255, ±2° a ~15 ⇒ si el número que devuelve el ajuste no baja de ~1, el encuadre NO es el mismo y
las comparaciones contra el banco viejo no valen.

**Recortes y máscara de los displays del tablero** (coords del RT 1814×1134, **origen abajo**, que es
lo que devuelve `GetPixels32`/`WorldToScreenPoint`):

- Caja (bbox de los vértices del submesh proyectados con `cam.WorldToScreenPoint`):
  `display1` `x[371..894] y[439..651]`, `display2` `x[874..1412] y[496..657]`. **Ojo:** la caja
  incluye volante/salpicadero alrededor del panel, así que diluye cualquier métrica y **sube cuando
  se ilumina la cabina pero no cuando solo se enciende la pantalla** (por eso la banda de aceptación
  de la sonda de luz NO es transferible tal cual a un cambio de emisión — ver la deuda cerrada).
- Máscara exacta del panel (recomendada): poner `_EMISSION_COLOR = (10,10,10)` **sin** emission map
  en el material del display y quedarse con los píxeles que cambian >90 respecto del frame base ⇒
  **55 924 px** de `display1` y **69 573 px** de `display2`. Es la única forma barata de aislar los
  píxeles del panel (la cabina no tiene colliders: un raycast miente, ver la nota de `CarplayScreen`).
- Métrica de detalle: **`gradRMS = sqrt(mean(dx² + dy²))`** sobre la luminancia BT.709 [6] de los
  bytes sRGB (0–255), diferencias hacia adelante, promediada sobre la máscara. ⚠️ **Los números de
  gradRMS de la fase 1 (`25.96`, `43.95`, …) están en otra escala: son ≈ 1.49 × los de esta
  fórmula.** Verificado sobre la serie completa de la sonda de luz (I = 0.2/1/2/4/8): el cociente sale
  1.486–1.505 en los cinco puntos, o sea las dos métricas son la MISMA hasta un factor de escala.
  Al comparar contra un número viejo, dividir por 1.49 (o remedir el `antes` en la corrida).

## Pendientes / deuda

- **Astigmatismo en el catálogo: CERRADO (P4.4).** Ya existen `astig_magnitude` (0..1) y
  `astig_axis_deg` (0..180) como params estándar del catálogo (v`0.5.0-clinical`, default 0 en las 3
  lentes) — ajustables con sliders y persistentes/overrideables como cualquier param.
  `GlareController.SetEyeGlobals` los mapea a los globals per-eye. El comando live `set_astigmatism`
  (Net/tablet) sigue funcionando como override efímero encima (ver gotcha de precedencia). Deuda
  coordinada: el test de integración de `DataLogicTests` quedó rojo por el bump de versión/conteo de
  params → lo actualiza @unity-dev (dueño de `Assets/Tests/`).
- **Astigmatismo per-eye: CERRADO en ambos lados (P2.2).** La API y ambos shaders resuelven
  astigmatismo por ojo (`glare_astig_l/r`, `glare_astig_angle_l/r`); `NetworkController` ya lee
  `cmd["eye"]` (comando `set_astigmatism`, default `"both"`) y llama la firma con ojo. La
  sobrecarga legacy `SetAstigmatism(enabled, magnitud, ángulo)` (marcada `// SIM`) se eliminó de
  `GlareController` — no queda código muerto. Detalle de protocolo en `docs/networking.md`.
- El modelo de velo es normalizado (sin unidades fotométricas reales cd/m²/lux): se corrige la FORMA
  de la curva (CIE `1/θ²`) pero la escala es relativa (faro = 1.0). Calibración fotométrica absoluta
  (cd/m²/lux) pendiente si se necesita comparabilidad cuantitativa con literatura CIE.
- Coeficientes de defocus/contraste (`DOF_M_TO_D`, `CONTRAST_PIVOT`) empíricos, pendientes de
  recalibrar contra defocus curves publicadas por LIO: PanOptix — Kohnen et al. [7]; Vivity —
  McCabe et al. [8]. Tanda futura. (`MAX_DEFOCUS_D` ya no existe: el radio es físico.)
- **Error paraxial `sec²θ` en el tablero — DIFERIDO a propósito (no es un olvido).** `_VisionPxPerDeg`
  es una constante por ojo, pero el ppd real de una proyección en perspectiva crece como `sec²θ`.
  Medido desde la pose de diseño del conductor en `ruta_noche` mirando **al frente**: `display1` está
  a **18.0°** del eje y `display2` a **32.0°** (los bordes de los paneles llegan a ~47°) ⇒ el tablero
  recibe entre **11 % y 119 % menos radio** del que le correspondería; el libro del consultorio, que
  se mira de frente, paga **0 %**. El sesgo **muerde menos justo cuando importa**: cuando el conductor
  BAJA la vista al tablero (el gesto real, y el encuadre de las capturas del ancla) θ→0 y el error
  desaparece. Corregirlo **aumentaría** el desenfoque del tablero, o sea va en dirección **contraria**
  al ajuste de `PUPIL_NIGHT_MM`: no mezclar las dos cosas en la misma tanda. Arreglo natural si
  alguna vez se hace: escalar `radiusPx` por `sec²θ` per-pixel (θ del ángulo de la dirección de vista
  del píxel contra el eje) — es aritmética barata en el fragment, pero **cambia TODAS las
  calibraciones ya medidas** (agudeza, `SCATTER_BLUR_DEG`, la tabla de sesgo de N1).
- **`contrast_loss` levantaba los negros de noche: CERRADO (tanda v0.9.1).** El pivote pasó de una
  constante `CONTRAST_PIVOT = 0.22` a **`CONTRAST_PIVOT_DAY = 0.22` / `CONTRAST_PIVOT_NIGHT = 0.025`
  interpolados geométricamente con `_PupilScene`** (el nivel de adaptación del campo). Fórmula,
  fisiología [15], anclaje medido de los dos valores, tabla antes/después y el residual: ver
  §`contrast_loss`: el pivote adaptativo. Resumen de lo medido (encuadre `tablero`, luminancia byte
  del recorte `display1` / de la cabina, contra `paciente_joven`): `panoptix` **+51 %/+93 % →
  +3.0 %/+13.2 %**, `catarata` **+139 %/+234 % → +24.7 %/+62.9 %**, con la **modulación lineal
  intacta** (Δ ≤ 0.15 %, = `1 − contrast_loss`) ⇒ **el catálogo NO se recalibra** y el día queda
  **bit a bit igual** (0–6 px de 2 057 076 a 1 LSB; `catarata` maxd 0). Palanca (b) de la lista
  original (`max(color, pivot)`) **rechazada**: de noche anularía `contrast_loss` por completo
  (justo donde la pérdida de sensibilidad al contraste más importa) y de día blanquearía las letras
  del optotipo ⇒ rompería la calibración.
- **Transmitancia ámbar en los CUATRO términos del modelo: CERRADO (tandas v0.9.1 + velo ámbar).**
  Todo lo que llega a la retina cruzando el cristalino brunescente lleva ahora
  `lerp(1, CATARACT_YELLOW, cataract_yellow)` **una vez**:
  1. **imagen** — filtro multiplicativo del pass 1 (C2);
  2. **pedestal de scatter** — `pedTint` (tanda del tinte ámbar);
  3. **halo/starburst del billboard** — `GlareController` publica `glare_cataract_l/r` y
     `GlareBillboard.shader` multiplica el color emitido (v0.9.1). Se eligió teñir en el billboard y
     **no** mover la inyección a `AfterRenderingTransparents` (eso desenfocaría los billboards y
     recalibraría TODO lo medido). Aporte aislado: **B/R ×0.550, G/R ×0.860, R intacto**;
  4. **velo CIE de encandilamiento** — `_GlareVeilTint × lerp(1, CATARACT_YELLOW, cataract)` (tanda
     del velo ámbar, hallazgo MAYOR de review). Aporte aislado **B/R ×0.55–0.59**; era el término
     **dominante** del núcleo (88 % de la energía R con un faro de frente a 4 m) y el que más
     des-amarilleaba. Detalle: §El velo CIE también pasa por el filtro ámbar.

  Lentes con `cataract_yellow = 0` quedan al **piso de la metodología de reimport** (control fuerte
  del velo: 51 px de 2 057 076 con maxd 1 en consultorio; los controles nocturnos dan 594–3667 px
  con maxd ≤86 en núcleos HDR de faros, que es el piso de no-reproducibilidad de `ruta_noche`, no el
  cambio de rama) — **no** "bit-idénticas".
  El triple queda **duplicado en dos shaders** (`VisionPostProcess` + `GlareBillboard`) con
  comentario cruzado en los dos. **Única excepción documentada: `WindowPortal.shader`**, que queda
  fuera a propósito porque es **opaco** (`Queue = Geometry`) y ya lo filtra el pass — agregarle el
  triple sería transmitancia al cuadrado; verificado por captura que su glare sale ámbar solo
  (B/R 0.8619 → 0.4939, cociente 0.573) y el archivo lleva el comentario que marca la excepción.
  **Deuda que sobrevive:** `contrast_loss` y el pedestal de scatter siguen sin alcanzar a los
  billboards (justificado en esa sección: el pedestal sobre un halo sería doble contabilidad y
  `contrast_loss` es de segundo orden ahí), y el follow-up de extraer el patrón clínico a un `.hlsl`
  compartido sigue abierto (con **tres** constantes compartidas en juego, una razón más para
  hacerlo).
- **Pantallas del tablero no emisivas: CERRADO (P-tablero-emisivo).** `display1.mat` (velocímetro) y
  `display2.mat` (infotainment/CarPlay) — submeshes 13 y 14 de `RutaNoche/PlayerCabin/misc_d`, shader
  `Shader Graphs/PhysicalMaterial3DsMax`, usados **sólo** por la cabina del jugador
  (`auto-nuevo/interior.fbx` + `Main.unity`; los autos del tráfico NO los usan) — eran superficies
  **puramente difusas con textura de UI**, iluminadas sólo por `InteriorLight` (`Point`, intensidad
  **0.2**, rango 1.5) bajo ambiente **0.14**. Una pantalla es una **fuente**, no un difusor: ahora
  emiten. *(Lo que justificó la tanda, medido en la fase 1 con un factorial 2×2 óptica × luminancia:
  el déficit de legibilidad del tablero se repartía **66 % luz / 34 % óptica** — `display1` 72/28,
  `display2` 60/40 —, o sea 2/3 del problema no era la lente. Luminancia media de partida de los
  recortes: `display1` 33.9/255 y `display2` 39.4/255 en la escala de la fase 1.)*
  **Valores finales (los únicos 3 campos que cambian por material):**

  | material | `_EMISSION_COLOR` (lineal) | `_EMISSION_COLOR_MAP` | `_EMISSION_WEIGHT` |
  |---|---|---|---|
  | `display1.mat` (cluster) | **(1.25, 1.25, 1.25)** | = su `_BASE_COLOR_MAP` (textura `13`) | 1 (sin cambio) |
  | `display2.mat` (CarPlay) | **(0.75, 0.75, 0.75)** | = su `_BASE_COLOR_MAP` (textura `14`) | 1 (sin cambio) |

  - **Emisión = `SampleTexture(_EMISSION_COLOR_MAP) × _EMISSION_COLOR × _EMISSION_WEIGHT`**
    (verificado leyendo el grafo del paquete URP: tres nodos, `Multiply`→`Multiply`→slot Emission del
    PBR Master). **NO hay keyword**: el `_EMISSION` que vivía en `m_InvalidKeywords` de los dos
    materiales era **basura muerta** (herencia de `URP/Lit`; este Shader Graph no tiene ese keyword y
    la emisión es incondicional) ⇒ se borró de los dos `.mat`. Corolario de rendimiento: **coste GPU
    añadido = 0**, el sample de `_EMISSION_COLOR_MAP` ya se pagaba contra la textura default.
  - **Por qué la albedo como emission map y color neutro:** la textura de UI *es* el patrón de
    radiancia de la pantalla; con `E` neutro los colores del CarPlay/cluster salen de la textura y no
    se tiñen. Multiplicar por la albedo es lo que conserva el negro del bisel (el fondo del panel es
    albedo ~0 ⇒ sigue negro) — **el contraste sube sin levantar el piso**, que es exactamente lo que
    la luz difusa NO puede hacer.
  - **Por qué `E` distinto por material:** `E` no es luminancia, es un multiplicador de la albedo. El
    cluster es negro con grafismos finos (albedo media baja) y el CarPlay tiene iconos casi blancos;
    con el mismo `E` el CarPlay quedaría muy por encima. Lo que queda emparejado es la **luminancia
    resultante del panel** (42.2 vs 37.7 de 255), no `E`.
  - Sin ancla fotométrica absoluta (misma limitación que el velo: el render es normalizado). Valor de
    ingeniería de referencia: cluster/infotainment de noche ~30–150 cd/m² contra una cabina difusa de
    pocos cd/m². El ancla real usada es la **sonda de luz de la fase 1** (ver abajo).

  **Calibración (todo en una misma corrida de `ruta_noche`, encuadre `tablero`, ppd 24, lente de
  referencia `paciente_joven`; métrica `gradRMS` sobre la máscara del panel — ojo con la escala, ver
  §Banco de capturas):**

  | recorte | antes | después | sonda de luz `I=1.0` | sonda `I=2.0` |
  |---|---|---|---|---|
  | `display1` gradRMS | 17.22 | **31.47** (+83 %) | 28.84 | 34.23 |
  | `display1` luminancia | 29.6 | **42.2** | 52.4 | 68.8 |
  | `display1` clipping | 0.00 % | **1.07 %** | 1.33 % | 4.06 % |
  | `display2` gradRMS | 22.58 | **27.61** (+22 %) | 33.39 | 35.74 |
  | `display2` luminancia | 30.5 | **37.7** | 51.9 | 62.6 |
  | `display2` clipping | 0.03 % | **7.53 %** | 13.37 % | 16.73 % |

  `display1` aterriza **entre `I=1.0` y `I=2.0`**, o sea dentro de la banda de aceptación que fijó la
  fase 1 (`44–51` en su escala = `29.5–34.2` en ésta; el valor final **31.47 ≈ 46.9** de la vieja).
  `display2` se dejó **deliberadamente por debajo de la banda**: su restricción que muerde primero es
  el **color de los iconos**, no el detalle. Medido (saturación media `(max−min)/max` de la máscara):
  0.401 antes → **0.350** a `E=0.75` (−13 %) → 0.308 a `E=1.05` (−23 %) → 0.232 a `E=1.6` (−42 %, los
  iconos se ven pastel/blancos, es visible sin métrica en
  `capturas/tablero_emisivo/_HOJA_CAL_barrido_emision_d2.png`). A `E=0.75` el clipping es **7.5 %**,
  **la mitad** del 13.4 % que dejaba la sonda de luz a `I=1.0` para el mismo panel.
  - **Efecto lateral deseable CONFIRMADO:** el castigo óptico de `monofocal` respecto de `joven`
    **creció** — `display1` de **21.3 % → 25.4 %** (gradRMS 13.55/17.22 → 23.48/31.47) y `display2` de
    29.7 % → 30.9 %. La predicción de la fase 1 (18 % → ~25 %) se cumple: la oscuridad **tapaba** la
    diferencia entre lentes.
  - Tabla por lente después / antes (gradRMS máscara, `display1` | `display2`), params PROD
    `0.8.1-clinical.a1`: `joven` 31.47|27.61 (era 17.22|22.58) · `monofocal` 23.48|19.09 (era
    13.55|15.88) · `panoptix` 27.61|19.76 (era 14.31|15.53) · `catarata` 3.07|3.49 (era 1.39|2.62).
    ⚠️ **Las celdas de `panoptix` y `catarata` son PRE-v0.9.1**: se midieron con el `contrast_loss`
    de pivote FIJO, o sea con la niebla gris encima (luminancia de la máscara 65 y 105). Con el
    **pivote adaptativo** su luminancia baja al nivel de `joven` y su `gradRMS` **sube** (en el
    recorte-bbox de `display1`: `panoptix` 20.69 → 23.37) **sin** que cambie su pérdida de modulación
    lineal — ver §`contrast_loss`: el pivote adaptativo. `joven` y `monofocal` (loss = 0) no se
    movieron ni un LSB. No se re-midió la máscara exacta del panel para las cuatro lentes: si se
    necesita la tabla completa post-fix, rehacerla con la máscara de emisión.
  - **Control de que no se tocó nada más:** con el tráfico congelado y el mismo encuadre, la
    diferencia emisión ON vs OFF cambia >8 LSB en **200 de 1 931 579 píxeles FUERA de las dos máscaras
    (0.010 %, maxdiff 30)** — son los píxeles de borde del panel (MSAA 4×). O sea: **cero derrame** de
    luz a la cabina, halos/starbursts de los faros idénticos
    (`_HOJA_CONTROL_nighttraffic_halos.png`), velo CIE intacto (`DisabilityGlareController` sólo mira
    `GlareBillboardInstance`, no la emisión).
  - **Las trampas de la fase 1, revisadas (una era falsa, y apareció una nueva):**
    1. ~~"Hay **Bloom activo** en `DefaultVolumeProfile.asset`"~~ — **FALSO, corregido**: ese perfil
       tiene `Bloom.intensity = 0` y `Tonemapping = None`, **y** la `Main Camera` tiene
       `renderPostProcessing = false` (el perfil con Bloom 0.25 es `SampleSceneProfile`, que **no** lo
       usa ninguna escena). Con `renderPostProcessing = false` **no corre NINGÚN post-proceso de
       volumen** (el pass de visión es una `ScriptableRendererFeature`, va por otro camino): la emisión
       escala lineal y lo único que hace un valor alto es **clippear a blanco**. Verificado también por
       captura: zoom ×3 al borde de los dos paneles, el bisel y el tapizado quedan **idénticos**
       (`_HOJA_ZOOM_bordes_antes_vs_despues.png`).
    2. "No usar la luminancia media como criterio" — **sigue vigente y fue la restricción decisiva**
       en `display2` (ver saturación arriba). El criterio bueno es gradRMS **+** clipping **+** una
       mirada al color de los iconos.
    3. Trampa NUEVA: **emisión sin `_EMISSION_COLOR_MAP` es peor que nada.** El grafo samplea la
       textura default (blanca) ⇒ el panel emite plano y **se lava**: con `E=0.3` sin mapa la
       luminancia del recorte-caja de `display1` salta 37.4→65.7 pero el gradRMS **BAJA** 17.5→15.0.
       El mapa no es opcional.
  - **Limitación conocida:** `m_LightmapFlags` sigue en `BakedEmissive` (2) y la escena no tiene GI
    horneada ⇒ las pantallas **no iluminan** la cabina ni las manos del conductor (una pantalla real
    sí, un poco). `InteriorLight` se dejó en **0.2** a propósito (pedido del usuario). Si alguna vez
    se quiere ese derrame, es una luz nueva o GI horneada, no más emisión.
  - Evidencia: `capturas/tablero_emisivo/` (`antes_*`/`despues_*` por lente y por display,
    `_HOJA_tablero_joven_antes_vs_despues.png`, `_HOJA_d1_cluster_*`, `_HOJA_d2_carplay_*`,
    `_HOJA_por_lente_despues_d1/d2.png`, `_HOJA_ZOOM_bordes_*`, `_HOJA_CAL_barrido_emision_d1/d2.png`,
    `_HOJA_CONTROL_nighttraffic_halos.png`, `_DIAG_mascara_displays.png` y el par de cierre
    `_PAR_monofocal_libro_vs_tablero_despues.png`).
- **`RutaNoche/CarplayScreen`: BORRADO (tanda v0.9.0, aprobado por el usuario).** Era un quad
  `URP/Lit` emisivo de 22×13 cm **100 % ocluido** por el salpicadero `misc_d`: apagando su
  `MeshRenderer` y difiando el frame, `0/786432` píxeles cambiaban — ni desde la pose del conductor
  ni mirándolo de frente. @scene-editor lo eliminó de `Main.unity` (GameObject fileID `1792110576`
  + Transform/MeshFilter/MeshRenderer + su material embebido `37338692`; diff de −214 líneas, 0
  referencias colgantes, escena guardada). **El "CarPlay" real es `misc_d/display2`** (UI de Apple
  CarPlay: Phone / Music / Maps / Messages, reloj y `HYUNDAI`) y el velocímetro es
  `misc_d/display1` — desde la tanda del tablero emisivo, ambos emiten con su propia albedo.
  **Nota metodológica que SOBREVIVE al borrado**: un `Physics.Raycast` daba 121/121 rayos "libres"
  y **mentía**, porque la cabina no tiene colliders — la oclusión se mide por render
  (diff de frame con el renderer on/off), no por física.
- **Etapa C: CERRADA.** Tier de baja resolución implementado (passes 2 y 3, `_VisionLowA/B`,
  `_VisionLowBlur`, `MAX_BLUR_DEG` 2.0, `DiscBlur13`). Queda **pendiente la captura L: medición de
  frame time en Quest** (build + HUD FPS, @build-deploy). Estimación de coste y palancas: ver §Coste
  del modelo dióptrico. Riesgo R1 (texturas reducidas en XR) mitigado en código pero **solo
  verificable en el visor**: el Game View es mono.
  - **La captura L tiene que medir el PASS 3 APARTE**, no solo el frame time agregado: ese pass hace
    25 `ComputeWorldSpacePosition` + `distance` + `sqrt` por píxel de baja, y en un Adreno es probable
    que el ALU domine sobre el texturado (ver §Coste). El diagnóstico cambia la palanca: si el cuello
    es ALU, `LOW_TAPS → 16`; si es ancho de banda, `LowDiv → 8` (que sube el piso de PSF del tier de
    ~6.3 a ~12.6 px — coste óptico real, ver `LOW_PSF_VAR`).
- **Hueco de calidad del cruce de tiers 2.6–6.3 px: ABIERTO Y CUANTIFICADO** (hallazgo N1 de review).
  El piso de PSF del tier (`LOW_PSF_VAR`) ya está contabilizado completo, lo que dejó **exacto todo el
  régimen `lowW = 1`**, pero en la banda queda un sobre-borroneo **pesimista de hasta +26 %**
  (≤ 0.10 logMAR). Es estructural del divisor ÷4: ningún ajuste de umbrales lo cierra. **Primera
  palanca a evaluar DESPUÉS de la captura L** (no antes: cuesta GPU): disco full-res de **25 taps**
  (anillos 0/⅓/⅔/1 con 1+4+8+12, limpio hasta ~4.1 px) ⇒ bajaría el peor caso a ~+10 % por +12 taps/px
  en los píxeles de la banda. Alternativa que sí cerraría el hueco pero cuesta 2 passes más y ~13
  taps-equiv/px/ojo permanentes: un tier intermedio a 1/4. Análisis completo con números: punto 7 del
  pass 0.
- **Dither de la espiral idéntico en los dos ojos: pendiente de validar en el visor** (ver Gotchas).
  Amplitud medida del ruido residual ≤ 0.3 % de la luminancia local ⇒ se espera imperceptible, pero la
  descorrelación binocular y el posible "hervor" al mover la cabeza **solo se ven en el dispositivo**.
  Va en el mismo viaje que R1.
- **`ASTIG_BLUR_DEG`: el bug de resolución del astigmatismo está CERRADO** (era `ASTIG_BLUR_PX = 22`).
  Queda la deuda de **densidad de taps**: `DirBlur` son 7 taps repartidos sobre TODO el largo del
  smear, así que la separación entre taps es `largo/3` y a largos > ~8 px se ven copias discretas en
  vez de un trazo continuo. Es deuda PRE-existente (con 22 px la separación ya era 7.3 px), marcada en
  el shader con `// SIM: atajo deliberado`. Fix posible: escalar la cantidad de taps con el largo en
  px, o pasar el smear por el tier de baja. Recapturar la verificación I si se toca.
  - **El look del astig SÍ cambió en el dispositivo y no está capturado a la ppd real** (hallazgo
    MENOR de review): los 1.3° equivalen a 22 px a ppd 17 (`renderScale 1.0`), pero la config real es
    `renderScale 1.4` ⇒ ppd ~24 ⇒ **~31 px, +40 % de largo**. La captura I existente
    (`etapaC_I_optotipo_catarata_astig45_ppd90.png`) está a ppd 90 y sirve para ver el artefacto de
    los 7 taps, **no** para juzgar el look del visor. Pendiente barato: recapturar I a ppd 24 con la
    técnica de FOV recortado (la misma de la §agudeza a ppd 24).
- **El tier de baja se computa siempre que el gate esté ON**, incluso si ningún píxel del frame supera
  `TIER_LO_PX` (el radio depende del depth per-pixel: no se puede decidir en CPU). Un sub-gate barato
  sería saltear los passes 2 y 3 cuando `desenfoque_max` Y `cataract_scatter` son ~0 en los dos ojos
  (lente que solo aporta contraste/tinte/velo/astig) — requiere una señal nueva en `VisionActivity`
  (dueño: @unity-dev). Primera palanca si la captura L no sostiene 72 Hz.
- **Recalibración clínica de los overrides de PROD: APLICADA** (etapa D). El texto anterior de este
  bullet (`catarata desenfoque_max = 2.0` / `profundidad_foco_m = 0.0`, `monofocal
  profundidad_foco_m = 0.0`, `paciente_joven desenfoque_max = 0.0299`) describía el catálogo
  `0.6.0-clinical.a50` y **ya no es el estado de PROD**. Vigente en **`0.8.0-clinical.a1`**:
  `monofocal` `profundidad_foco_m = 1.0` (±0.5 D, el valor de manual), `catarata`
  `desenfoque_max = 1.0` + `cataract_scatter = 0.9` (el scatter es lo que mata el titular del cartel:
  a 4.86 m aporta el **96 %** del radio; nunca bajar `SCATTER_BLUR_DEG`, eso rompe el ancla del
  optotipo), `paciente_joven` `desenfoque_max = 0.0` **exacto** ⇒ el **gate de CPU sí apaga** los 4
  blits por ojo con la lente de referencia (la deuda de GPU que describía el bullet siguiente está
  cerrada).
- **Tanda de limpieza pendiente de aplicar en PROD ⇒ `0.8.1-clinical.a1`** (son 4 celdas de 90; es
  DATO, no código, y no requiere rebuild):

  | lente | param | de → a | por qué |
  |---|---|---|---|
  | `monofocal` | `halo_extra_rings` | 5.010313 → **1.0** | **param MUERTO**: con `halo_intensity = 0` ⇒ `haloR = 0` ⇒ `GlareBillboard` saltea el bloque de halo entero. El 5.01 no pinta nada y en *Ajuste fino* le hace creer al clínico que la lente de referencia tiene halos grandes y azulados. `1.0` normaliza a 0 exacto |
  | `generic_a209ba91` | `halo_extra_rings` | 3.957684 → **1.0** | ídem |
  | `generic_a209ba91` | `profundidad_foco_m` | 1.4 → **1.6** | fidelidad Eyhance: las monofocales de rango extendido publican **+0.4–0.6 D** de DOF sobre una monofocal estándar; 1.0→1.4 modelaba solo +0.2 D. Con 1.6 el intermedio a 66 cm pasa de 0.43 a **0.35 logMAR** (2 líneas sobre la monofocal, que da 0.55) |
  | `panoptix` | `destello_rayos` | 5.0 → **9.0** | coherencia con su propia descripción del catálogo (*"starburst de 8-10 rayos"*): `GlareBillboard.shader` usa `n = clamp(v_rays,1,16)` como **cantidad literal de sectores**, sin factor ×2 ⇒ hoy pinta 5. **Sin ancla clínica** (el conteo de espículas no está cuantificado en la literatura del repo): la alternativa igual de válida es corregir la descripción |

  Incoherencia de DATO, no de óptica: `catarata.descripcion` dice *"nuclear **moderada**"* pero los
  params son de **avanzada** (`cataract_yellow 1.0` + `scatter 0.9` ⇒ ~20/180 a 4 m; la tabla de
  calibración de §`cataract_scatter` ubica 0.6 = moderada, 1.0 = avanzada). El camino limpio es el que
  sugiere la propia descripción — **agregar `catarata_moderada` (`scatter 0.6`)** como lente aparte —,
  no bajarle el scatter a la existente (reabriría el bug del titular del cartel).
- Pupila: la miosis transitoria por glare (`glareMiosisGain`) modula `_PupilScene` (→ blur) pero NO
  el `nightPupilFactor` del velo (sigue escalonado por escenario). Unificar pupila blur↔velo es deuda
  menor si se busca coherencia total.

## Referencias

- **[1]** Vos, J.J. (1984). *Disability glare — a state of the art report.* CIE Journal, 3(2), 39–53.
  (Aproximación de Stiles-Holladay `Lveil = k·Egl/θ²`.)
- **[2]** CIE 146:2002. *CIE equations for disability glare.* (Ecuación general + término de edad
  `[1+(A/62.5)⁴]`.)
- **[3]** Stiles, W.S. (1929) / Holladay, L.L. (1926) — formulación original del velo equivalente por
  luz difusa (base histórica de la forma `1/θ²`).
- **[4]** CIE (1951). *Scotopic luminous efficiency function V'(λ)* (observador escotópico estándar).
  Pesos escotópicos sobre primarios sRGB ≈ 0.02/0.70/0.28 (aproximación).
- **[5]** Edad media de cirugía de catarata ~70–74 años en cohortes de LIO (usado como paciente de
  referencia CIE, `age`/`CalibAge` = 70).
- **[6]** ITU-R BT.709. Coeficientes de luminancia fotópica sRGB `Y = 0.2126R + 0.7152G + 0.0722B`.
- **[7]** Kohnen, T. et al. — defocus curve de la trifocal difractiva AcrySof IQ PanOptix (agudeza vs
  desenfoque). Objetivo de calibración de los coeficientes de defocus.
- **[8]** McCabe, C. et al. — defocus curve de la EDOF no difractiva AcrySof IQ Vivity. Objetivo de
  calibración de los coeficientes de defocus.
- **[9]** Reflejo fotomotor pupilar: latencia ~0.2–0.5 s y constricción (~1 s) más rápida que la
  redilatación (varios s) — p.ej. Ellis, C.J. (1981), *The pupillary light reflex in normal
  subjects*, Br J Ophthalmol 65:754–759. Base de las constantes de tiempo asimétricas de la pupila.
- **[10]** Ferris, F.L. III, Kassoff, A., Bresnick, G.H., Bailey, I. (1982). *New visual acuity charts
  for clinical research.* Am J Ophthalmol 94(1):91–96. (Diseño de la cartilla ETDRS: progresión
  logMAR 0.1, 5 letras Sloan por fila, espaciado proporcional.) Base del optotipo del consultorio.
- **[11]** Pokorny, J., Smith, V.C., Lutze, M. (1987). *Aging of the human lens.* Applied Optics
  26(8):1437–1440. (Transmitancia espectral del cristalino vs edad: absorción creciente en el azul,
  casi nula en el rojo.) Base del triple sRGB del tinte amarillo de catarata (`cataract_yellow`).
- **[12]** Watson, A.B., Yellott, J.I. (2012). *A unified formula for light-adapted pupil size.*
  Journal of Vision 12(10):12. (Diámetro pupilar vs luminancia de adaptación, edad y campo.) Base de
  `PUPIL_DAY_MM = 3.0` (fotópica en paciente ~70 años, con miosis senil — de ahí que NO sean 7 mm).
  El valor **mesópico** ya no sale de acá: ver [14].
- **[13]** van den Berg, T.J.T.P. (1995). *Analysis of intraocular straylight, especially in relation
  to age.* Optometry and Vision Science 72(2):52–59; y Franssen, L., Coppens, J.E., van den Berg,
  T.J.T.P. (2006). *Compensation comparison method for assessment of retinal straylight.* IOVS
  47(2):768–776 (método **C-Quant**). Base del crecimiento log-lineal del straylight con el grado de
  catarata ⇒ la dependencia `scatter²` de `cataract_scatter` (radio y velo).
- **[14]** Winn, B., Whitaker, D., Elliott, D.B., Phillips, N.J. (1994). *Factors affecting
  light-adapted pupil size in normal human subjects.* Investigative Ophthalmology & Visual Science
  35(3):1132–1137. (Dataset canónico de diámetro pupilar vs **edad** y **luminancia**: a los ~70 años
  el diámetro se mantiene entre ~4.0 y ~4.8 mm a lo largo de todo el rango mesópico y nunca supera
  ~5 mm.) Base de `PUPIL_NIGHT_MM = 4.0` — ver §Ancla de calibración día↔noche.
- **[15]** Campbell, F.W., Robson, J.G. (1968). *Application of Fourier analysis to the visibility of
  gratings.* Journal of Physiology 197(3):551–566. (La sensibilidad al contraste se define y se mide
  como **modulación alrededor de una luminancia MEDIA fija**: perder sensibilidad al contraste
  comprime la modulación hacia el nivel de adaptación — no agrega un pedestal, que es lo que hace un
  velo.) Base del **pivote adaptativo** de `contrast_loss` — ver §`contrast_loss`: el pivote
  adaptativo.
