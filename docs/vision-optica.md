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
   │ evento VisionStateChanged(eye, state)
   ├─► VisionParamsBinder ──► Material VisionPostProcess (_XxxL/_XxxR)
   ├─► GlareController ─────► Shader globals glare_*_l / glare_*_r (billboards)
   └─► DisabilityGlareController ─ lee "straylight" por ojo
              │ + GlareBillboardInstance activos (fuentes)
              └─► globals _GlareVeilL/R, _GlareVeilUV, _GlareVeilTint

Render por frame (URP RenderGraph):
  opacos + skybox ─► VisionRendererFeature (blur+contraste+velo, POR OJO)
                  ─► transparentes (billboards GlareBillboard, aditivos, SIN blur)
```

- `Assets/Scripts/Runtime/Vision/VisionRendererFeature.cs` — ScriptableRendererFeature con la API
  RenderGraph (Unity 6). Inyecta en `BeforeRenderingTransparents` dos blits ping-pong: pass 0
  (efecto) `source→temp` y pass 1 (copia) `temp→source`. Pide `ScriptableRenderPassInput.Depth`
  y aborta si el target activo es el backbuffer (no se puede leer+escribir).
- `Assets/Shaders/VisionPostProcess.shader` — el post-proceso en sí (ver fórmulas abajo).
- `Assets/Scripts/Runtime/Vision/VisionParamsBinder.cs` — puente DataManager→material. Mapea claves
  del catálogo a uniforms por ojo: `foco_lejos_m→_FocoLejosL/_FocoLejosR`,
  `foco_intermedio_m→_FocoIntermedioL/R`, `foco_cerca_m→_FocoCercaL/R`,
  `profundidad_foco_m→_ProfundidadFocoL/R`, `desenfoque_max→_DesenfoqueMaxL/R`,
  `contrast_loss→_ContrastLossL/R`. Además aplica un blend demo al arrancar
  (`applyDemoBlendOnStart`: monofocal OI / panoptix OD).
- `Assets/Scripts/Runtime/Vision/GlareController.cs` — DataManager→shader globals de los billboards:
  `halo_intensity→glare_halo_l/r`, `halo_extra_rings→glare_pupil_l/r`,
  `destello_intensity→glare_star_l/r`, `destello_rayos→glare_rays_l/r`. Escala por escenario:
  halos × `haloScale`, destellos × `starScale`, `destello_rayos` (cantidad) nunca se escala.
  Expone `SetAstigmatism(enabled, magnitudNorm 0..1, ánguloRad)` → globals `glare_astig` /
  `glare_astig_angle` (GLOBAL, mismo valor para ambos ojos; lo llama la capa Net/tablet).
- `Assets/Shaders/GlareBillboard.shader` — halo + starburst + trazo astigmático procedurales sobre
  un quad que sigue a la cámara con tamaño angular constante. Aditivo (`Blend One One`),
  `ZTest LEqual` (se ocluye tras geometría). Constantes angulares en radianes:
  `HALO_ANG_RADIUS 0.10`, `PUPIL_GAIN 1.7`, `STAR_ANG_RADIUS 0.22`, `ASTIG_ANG_RADIUS 0.12`,
  `ASTIG_WIDTH 0.02`, `ASTIG_GAIN 2.2`, `DIST_REF_M 8.0`, `TOWARD_CAM_FRAC 0.10`. El halo lleva
  glow gaussiano + 3 anillos difractivos concéntricos (a r normalizado 0.45 / 0.68 / 0.90) cuyo
  peso escala ~`v_halo²` (una monofocal casi no muestra anillos). Fade por distancia:
  `v_fade = saturate(src_energy · DIST_REF_M / dist) · facing`.
- `Assets/Scripts/Runtime/Vision/GlareSource.cs` — factoría estática: quad compartido + material
  compartido (`Simulador/GlareBillboard`) + `Attach(parent, pos, color, energy, beamDir)`.
- `Assets/Scripts/Runtime/Vision/GlareBillboardInstance.cs` — componente serializable por fuente:
  `srcColor`, `srcEnergy` (faro = 1.0, relativo), `srcDir` (dirección local del haz; 0 =
  omnidireccional), `seed`, `distanceInvariant` (sol). Reaplica todo por MaterialPropertyBlock en
  `OnEnable` y dibuja gizmos de dirección en editor.
- `Assets/Scripts/Runtime/Vision/DisabilityGlareController.cs` — encandilamiento clínico (abajo).
- `Assets/Scripts/Runtime/Vision/ScenarioManager.cs` — cambia consultorio ↔ ruta_noche: activa el
  root, muestra/oculta el libro, setea ambiente (día: `ambientLight (0.55,0.52,0.45)`, sol
  direccional configurado pero OFF; noche: ambiente `(0.14,0.14,0.15)`, `reflectionIntensity 0`,
  luna direccional 0.3 casi neutra sin sombras, fondo `SolidColor` casi negro), recoloca el rig XR
  y setea `_PupilScene` (0 = día, 1 = noche). De día `haloScale=0.2` y `starScale=0.7`.
- `Assets/Scripts/Runtime/Vision/NightTraffic.cs` — tráfico bidireccional: instancia prefabs de
  `Assets/Prefabs/Cars` (frente del auto = +Z local) en dos carriles (`laneX=±2.6 m`); carril
  derecho se aleja (se ven pilotos), izquierdo viene de frente (faros). Wrap entre `startZ=70` y
  `endZ=-14`, `speed=16 m/s`. Tinta solo el material llamado "Body" vía MaterialPropertyBlock.
- `Assets/Scripts/Runtime/Vision/BookHolder.cs` — mide distancia libro→cámara (suavizada) y la pasa
  al material como `_BookDistanceM` + máscara en pantalla `_BookScreenUV` / `_BookScreenRadius`
  (radio angular real del libro × 1.45, clamp 0.06..0.45). El shader usa esa distancia dentro de
  la máscara porque el depth del libro en la mano no es confiable.
- `Assets/Scripts/Runtime/Vision/SimuladorInput.cs` — mandos Quest (acciones creadas en código):
  A = cicla lente ojo izquierdo, B = ojo derecho, X = toggle halos, Y = cambia de escenario.
- `Assets/Scripts/Runtime/Vision/HudController.cs` — HUD world-space anclado a la cámara: FPS,
  escenario, lente por ojo, estado de halos (UI legacy, sin TMP).
- `Assets/Scripts/Runtime/Vision/GlareTestRig.cs` — rig de verificación: baja la luz y spawnea 3
  lámparas emisivas con billboards de glare al frente, a altura de ojos.

### Post-proceso: modelo dióptrico (VisionPostProcess, pass 0)

1. `depth → metros`: `SampleSceneDepth` + `ComputeWorldSpacePosition` → distancia radial a cámara.
2. `Diopters(d) = 1 / max(d, 0.05)` (dioptrías = 1/metros, clamp a 5 cm).
3. Error de enfoque: `errD = min(|D(d) − D(foco)|)` sobre los focos ACTIVOS (foco = 0 ⇒ no usado).
4. Tolerancia: `tolD = profundidad_foco_m × DOF_M_TO_D(0.5)` [D]. Blur:
   `blur = desenfoque_max × saturate(max(errD − tolD, 0) / MAX_DEFOCUS_D)`, con
   `MAX_DEFOCUS_D = 1.5 D` (error que satura el blur). De noche `blur ×= lerp(1, 1.35, _PupilScene)`.
5. Box blur de 4 taps bilineales en diagonales, radio `BLUR_RADIUS_PX(7) × blur`.
6. Astigmatismo global: smear direccional de 7 taps gaussianos a lo largo del eje
   (`glare_astig_angle`), largo `ASTIG_BLUR_PX(22 px) × glare_astig`.
7. Contraste: `color = (color − 0.22) × (1 − contrast_loss) + 0.22` (pivote bajo: no levanta negros).
8. Velo de encandilamiento (aditivo, después del contraste — ver siguiente sección).

### Disability glare (velo por ojo)

`DisabilityGlareController` implementa un modelo tipo CIE **aproximado** (comentario del código:
"Modelo CIE aproximado"; no usa la constante literal 10·E/θ² de la CIE general disability glare
equation — el término angular es un cono suavizado). Por cada `GlareBillboardInstance` activo:

```
f        = clamp01(InverseLerp(outer=42°, inner=5°, ángulo))²        # concentración angular
lum      = clamp01(0.10·R + 0.78·G + 0.12·B)                          # luminancia mesópica
                                                                       # (Purkinje: rojo ~10× menos)
distFactor = refDist²(4 m) / max(d², nearClamp²(2 m))                  # ley 1/d² (iluminancia en ojo)
distGate   = smoothstep entre fullWeight(10 m) y cutoff(20 m)          # fuente lejana no aporta
             (sol: distanceInvariant ⇒ distFactor = distGate = 1)
facing     = SmoothStep sobre dot(haz, −dirFuente→ojo) si srcDir ≠ 0   # el faro que se aleja no encandila
oclusión   = Physics.Linecast(cámara, fuente, occluders) ⇒ aporte 0
w = max(srcEnergy, 0.01) · lum · distFactor · distGate · f · facing
```

Velo final: `veil_ojo = min(maxVeil=0.6, straylight_ojo × Σw × sensitivity(0.18) × pupila)`, con
`pupila = 1.5` en ruta_noche (`nightPupilFactor`) y 1.0 de día. Suavizado temporal exponencial
`k = 1 − e^(−5·dt)`. Se publica en `_GlareVeilL/_GlareVeilR` + UV de la fuente dominante
(`_GlareVeilUV`) y tinte cálido `_GlareVeilTint (1, 0.95, 0.85)`. En el shader:
`L = veil × (0.35 + 0.65·exp(−|uv−src|²/0.05))` (pedestal uniforme + glow en la fuente), se SUMA
`tint × L` (straylight aditivo: levanta negros = baja contraste como el velo real) y desatura
`veil × 0.12`. Las magnitudes son **normalizadas** (energía relativa, faro = 1.0), no cd/m² físicos.

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
- Ponderación mesópica anti-rojo (0.10/0.78/0.12) → efecto Purkinje: un piloto rojo encandila
  ~10× menos que un faro blanco del mismo brillo.
- `distanceInvariant` en el sol → fuente "al infinito": el velo no debe atenuar por 1/d².
- Material del billboard COMPARTIDO + parámetros por instancia vía MaterialPropertyBlock → un solo
  material instanciable; `GlareBillboardInstance` existe porque el MPB no se serializa.
- Tope `maxVeil=0.6` y suavizado temporal → confort VR (evitar flashes bruscos).
- `_StreamForceEye` (0/1/2) → la captura mono del stream a tablet (`Assets/Scripts/Runtime/Net/StreamingCapture.cs`)
  puede forzar qué ojo se renderiza sin afectar el render estéreo (default 0).

## Gotchas

- **Nunca samplear `_CameraDepthTexture` plano** en shaders de pantalla: bug Vulkan+multiview, el
  ojo derecho recibe el depth del izquierdo. Usar las macros `_X` / `SampleSceneDepth`.
- **El pass aborta silenciosamente si el target activo es el backbuffer** (`isActiveTargetBackBuffer`):
  si el efecto "no hace nada", revisar en qué punto se inyecta y si hay upscaling/intermedios.
- **`GlareBillboardInstance` debe vivir en un archivo con el nombre exacto de la clase**: Unity
  necesita el MonoScript para serializar la referencia en prefabs (si no, "Missing Script").
- **El MPB no se serializa**: sin `Apply()` en `OnEnable` los billboards de escena/prefab quedarían
  invisibles en el build.
- **`applyDemoBlendOnStart = true` en `VisionParamsBinder`** pisa las lentes al arrancar con
  monofocal/panoptix; apagarlo para demos reales.
- **`DisabilityGlareController` refresca fuentes con `FindObjectsByType` cada 0.5 s**: una fuente
  nueva puede tardar hasta medio segundo en encandilar; también es costo de escaneo periódico.
- **La oclusión usa `Physics.Linecast` con `occluders = ~0`**: geometría sin collider NO ocluye el
  velo (los billboards mismos no tienen collider, así que no se auto-bloquean).
- **`destello_rayos` es CANTIDAD de rayos, no intensidad**: nunca se escala por escenario
  (la intensidad la da `destello_intensity`).
- **Los uniforms del material persisten en el asset en editor**: el material de
  `VisionRendererFeature` y el de `VisionParamsBinder`/`BookHolder` deben ser el MISMO asset.
- **`RING_POS`/`RING_WIDTH` en GlareBillboard.shader están definidos pero no se usan**: los anillos
  reales están hardcodeados en el frag (0.45/0.68/0.90).

## Cómo probar

1. Abrir `Assets/Scenes/SampleScene.unity` y entrar en Play (o build Quest vía `unity_build`).
2. El HUD muestra FPS, escenario, lente por ojo y halos. Controles: **A** cicla lente OI, **B**
   cicla OD, **X** toggle halos, **Y** alterna consultorio ↔ ruta_noche.
3. Blend demo al arrancar: OI monofocal (nítido lejos, libro ilegible) vs OD panoptix (halos y
   anillos marcados de noche, libro legible). Cerrar un ojo por vez para comparar.
4. Ruta nocturna: mirar un auto que viene de frente → faros blancos generan velo (más con
   panoptix, `straylight=1.0`, que con monofocal, `0.15`); los pilotos rojos casi no encandilan;
   al mirar lejos de la luz el velo cae (cono 5°–42°).
5. Consultorio: acercar/alejar el libro; con monofocal se desenfoca a ~40 cm y mejora al brazo
   extendido; el sol por la ventana produce destello (starburst) pero halos casi nulos (día).
6. Aislado: agregar `GlareTestRig` a la escena para 3 lámparas de prueba con billboards.

## Pendientes / deuda

- `GlareController.SetAstigmatism` solo se invoca desde la capa Net (tablet); no hay parámetro de
  astigmatismo en el catálogo de lentes.
- El modelo de velo es normalizado (sin unidades fotométricas reales cd/m²/lux); calibración
  clínica pendiente si se necesita comparabilidad con literatura CIE.
- `RING_POS`/`RING_WIDTH` muertos en `Assets/Shaders/GlareBillboard.shader` (limpiar o usar).
- Comentario en `GlareController.cs` ("el gating por escenario llega en F5") quedó desactualizado:
  ya lo hace `ScenarioManager`.
