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
```

- `Assets/Scripts/Runtime/Vision/VisionRendererFeature.cs` — ScriptableRendererFeature con la API
  RenderGraph (Unity 6). Inyecta en `BeforeRenderingTransparents` dos blits ping-pong: pass 0
  (esfera/defocus) `source→temp` y pass 1 (cilindro/astig + contraste + velo) `temp→source`. Pide
  `ScriptableRenderPassInput.Depth` y aborta si el target activo es el backbuffer (no se puede
  leer+escribir). **Gate de CPU (3.1):** antes de `EnqueuePass` consulta `VisionActivity.AnyActive`;
  si NINGÚN efecto es no-nulo en ambos ojos, saltea la inyección (se ahorran los 2 blits full-screen
  por ojo). Loguea `[Vision] Post-proceso gate ON/OFF` solo en las transiciones.
- `Assets/Scripts/Runtime/Vision/VisionActivity.cs` — estado agregado "hay efecto" por ojo para el
  gate. Lo escriben (con estado C# ya conocido, NO leyendo el material): `VisionParamsBinder`
  (`ParamsL/R = max(desenfoque_max, contrast_loss)`), `GlareController` (`AstigL/R`),
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
  `contrast_loss→_ContrastLossL/R`. Además aplica un blend demo al arrancar
  (`applyDemoBlendOnStart`: monofocal OI / panoptix OD).
- `Assets/Scripts/Runtime/Vision/GlareController.cs` — DataManager→shader globals de los billboards
  (hereda `VisionStateBinder`; `ApplyEyeState` delega en `SetEyeGlobals`):
  `halo_intensity→glare_halo_l/r`, `halo_extra_rings→glare_pupil_l/r`,
  `destello_intensity→glare_star_l/r`, `destello_rayos→glare_rays_l/r`. Escala por escenario:
  halos × `haloScale`, destellos × `starScale`, `destello_rayos` (cantidad) nunca se escala.
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
  **Gotcha:** `Consultorio` tiene scale 0.37 → `EnlargedWindow` compensa con localScale 1/0.37
  (sus mallas están en metros de mundo); el cuarto del FBX está rotado ~62° (las paredes NO están
  alineadas a los ejes del mundo).
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
  la máscara porque el depth del libro en la mano no es confiable.
- `Assets/Scripts/Runtime/Vision/SimuladorInput.cs` — mandos Quest (acciones creadas en código):
  A = cicla lente ojo izquierdo, B = ojo derecho, X = toggle halos, Y = cambia de escenario.
- `Assets/Scripts/Runtime/Vision/HudController.cs` — HUD world-space anclado a la cámara: FPS,
  escenario, lente por ojo, estado de halos (UI legacy, sin TMP). Última línea de emparejamiento:
  sin tablet autenticada muestra `PIN tablet: NNNNNN` (de `NetworkController.Instance.PairingPin`)
  para que el clínico lo tipee en la tablet; con al menos una autenticada
  (`NetworkController.AuthenticatedClientCount > 0`) pasa a `Tablet conectada` y deja de exponer el
  PIN. Sin `NetworkController.Instance` (escenas/momentos sin red) no dibuja la línea.
- `Assets/Scripts/Runtime/Vision/GlareTestRig.cs` — rig de verificación: baja la luz y spawnea 3
  lámparas emisivas con billboards de glare al frente, a altura de ojos.

### Post-proceso: modelo dióptrico (VisionPostProcess, dos passes)

El efecto está partido en dos passes para que el astigmatismo (cilindro) opere sobre la imagen ya
desenfocada por el defocus (esfera) — correctitud óptica: el ojo aplica esfera+cilindro como una
PSF combinada, no el cilindro sobre la imagen nítida.

**Pass 0 — esfera (defocus), `source→temp`:**
1. `depth → metros`: `SampleSceneDepth` + `ComputeWorldSpacePosition` → distancia radial a cámara.
2. `Diopters(d) = 1 / max(d, 0.05)` (dioptrías = 1/metros, clamp a 5 cm).
3. Error de enfoque: `errD = min(|D(d) − D(foco)|)` sobre los focos ACTIVOS (foco = 0 ⇒ no usado).
4. Tolerancia: `tolD = profundidad_foco_m × DOF_M_TO_D(0.5)` [D]. Blur:
   `blur = desenfoque_max × saturate(max(errD − tolD, 0) / MAX_DEFOCUS_D)`, con
   `MAX_DEFOCUS_D = 1.5 D` (error que satura el blur). De noche `blur ×= lerp(1, 1.35, _PupilScene)`.
5. Box blur de 4 taps bilineales en diagonales, radio `BLUR_RADIUS_PX(7) × blur`. Escribe a temp.

**Pass 1 — cilindro + contraste + velo, `temp→source`** (`_BlitTexture` = temp = imagen ya
esfero-desenfocada):
6. Astigmatismo POR OJO: smear direccional de 7 taps gaussianos a lo largo del eje
   (`glare_astig_angle_l/r`), largo `ASTIG_BLUR_PX(22 px) × glare_astig_l/r` (selección por
   `eyeIdx`, respeta `_StreamForceEye`). `DirBlur` samplea **temp** (la imagen desenfocada), no el
   original: el cilindro hereda el defocus de la esfera.
7. Contraste: `color = (color − 0.22) × (1 − contrast_loss) + 0.22` (pivote bajo: no levanta negros).
8. Velo de encandilamiento (aditivo, después del contraste — ver siguiente sección).

Coste: cuando astig = 0 el pass 1 es 1 tap (copia) y el pass 0 hasta 5 taps; con astig activo el
pass 1 sube a 7 taps. Antes (monolítico) el pass 0 hacía ~12 taps y el pass 1 era copia (~13 total);
ahora ~6 con astig off, ~12 con astig on: coste ≈ igual o menor, y sin re-samplear el original.

**Coeficientes perceptuales empíricos, PENDIENTES de calibración** (no tocar sin datos): `DOF_M_TO_D
= 0.5` (mapea profundidad de foco en m a tolerancia dióptrica), `MAX_DEFOCUS_D = 1.5 D` (error que
satura el blur) y `CONTRAST_PIVOT = 0.22`. Se eligieron a ojo; la calibración correcta es contra
**defocus curves** publicadas (agudeza vs desenfoque) de cada LIO: PanOptix trifocal — Kohnen et al.
[7]; Vivity EDOF — McCabe et al. [8]; y una monofocal de referencia. Tarea futura (recalibración),
fuera de esta tanda.

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
con `pupila` = `nightPupilFactor` en ruta_noche y 1.0 de día. Suavizado temporal exponencial
`k = 1 − e^(−5·dt)`. Se publica en `_GlareVeilL/_GlareVeilR` (y en `VisionActivity.VeilL/R`, el valor
suavizado, para el gate de CPU) + UV de la fuente dominante (`_GlareVeilUV`) y tinte cálido
`_GlareVeilTint (1, 0.95, 0.85)`. En el shader:
`L = veil × (0.35 + 0.65·exp(−|uv−src|²/0.05))` (pedestal uniforme + glow en la fuente), se SUMA
`tint × L` (straylight aditivo: levanta negros = baja contraste como el velo real) y desatura
`veil × 0.12` usando luminancia **Rec.709** [6]. Las magnitudes son **normalizadas** (energía
relativa, faro = 1.0), no cd/m² físicos.

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

- **Layout**: 12 filas × 5 letras, progresión logMAR en pasos de 0.1, de **logMAR 1.0 (20/200)** arriba
  a **logMAR 0.0 (20/20)** y una fila extra **-0.1 (20/16)** abajo. Cada fila etiquetada al margen
  izquierdo con logMAR + Snellen (20/x). Letras **Sloan** (C D H K N O R S V Z). Alto contraste: texto
  negro (TMP SDF, unlit) sobre panel blanco **unlit** (`Assets/Materials/OptotypeBackground.mat`, URP
  Unlit doble cara) → contraste garantizado, la iluminación de la sala no lo lava.
- **El post-proceso de visión ALCANZA las letras (P4.5-fix)**: por defecto los `TextMeshPro` usan el
  material de fuente `Inter-SemiBold SDF Material` en **cola Transparent (renderQueue 3000)**, y el pass
  se inyecta en `BeforeRenderingTransparents` → las letras se dibujaban DESPUÉS del pass y quedaban
  NÍTIDAS mientras la sala se veía borrosa/astigmática (bug clínico: la cartilla debe leerse BAJO los
  efectos de la LIO, no exenta). Fix: los 24 TMP (`row_*` + `label_*`) usan un material dedicado
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
  4.0 m exactos desde la posición del ojo medida en Editor (`camPos≈(0.274, 1.118, -0.500)`) hasta la
  cartilla (`(0.274, 1.118, 3.500)`, a la derecha de la ventana → pared sólida detrás, sin cielo/sol
  que lave; no interfiere con el libro ni la ventana). Ojo: la posición REAL del ojo depende del
  origen sentado del rig (`ScenarioManager.consultorioOriginPos=(-0.35,-0.05,-0.40)`, mirando +X) y
  de la altura/postura del usuario → la distancia efectiva puede variar ~±1% (≈ ±0.005 logMAR,
  despreciable clínicamente). Verificado: cap-height renderizado del renglón 20/20 =
  **5.818 mm** = target `h(0.0)=2·4·tan(2.5 arcmin)=5.818 mm` ✓. Alturas por fila a 4 m: 20/200=5.82 cm,
  20/100=2.92 cm, 20/40=1.16 cm, 20/20=5.82 mm, 20/16=4.62 mm. El tamaño de fuente TMP se calibra con
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
  relativa. A 4 m todas las LIOs del catálogo enfocan (foco lejano 6 m), así que a distancia el
  diferenciador es el **contraste** (`contrast_loss`): la panoptix (0.20) lava los renglones bajos y
  el paciente "pierde líneas" respecto de la monofocal — verificado con captura nítido vs panoptix.

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
- Post-proceso en DOS passes (esfera → cilindro+contraste+velo) → que el smear astigmático opere
  sobre la imagen ya desenfocada (correctitud óptica) reusando el pass 1 que antes solo copiaba;
  coste de taps ≈ igual o menor que el monolítico y sin re-samplear el original (3.2).
- Gate de CPU vía `VisionActivity` (estado C#, no material) → saltear los 2 blits full-screen por ojo
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
5. Consultorio: acercar/alejar el libro; con monofocal se desenfoca a ~40 cm y mejora al brazo
   extendido; el sol por la ventana produce destello (starburst) pero halos casi nulos (día).
6. Aislado: agregar `GlareTestRig` a la escena para 3 lámparas de prueba con billboards.
7. **Optotipo ETDRS (consultorio):** en consultorio, mirar la cartilla `OptotipoETDRS` en la pared a
   la derecha (a 4 m). Pedirle al paciente que lea la línea más baja legible con cada LIO; la fila
   está etiquetada con su agudeza (logMAR / 20-x). Comparar nítido (sin lente) vs una LIO con
   `contrast_loss` (p.ej. panoptix) → se pierden los renglones bajos por pérdida de contraste. A 4 m
   todas las LIOs enfocan (foco lejano 6 m): el diferenciador a distancia es el contraste, no el blur.

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
- Coeficientes de defocus/contraste (`DOF_M_TO_D`, `MAX_DEFOCUS_D`, `CONTRAST_PIVOT`) empíricos,
  pendientes de recalibrar contra defocus curves publicadas por LIO: PanOptix — Kohnen et al. [7];
  Vivity — McCabe et al. [8]. Tanda futura.
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
