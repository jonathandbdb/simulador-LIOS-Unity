---
name: urp-rendergraph-patterns
description: Patrones URP 17.5 RenderGraph para este proyecto - anatomía de passes, XR single-pass instanced por ojo, blit, MaterialPropertyBlock, presupuesto GPU Quest. Cargar antes de tocar Vision/ o Shaders/.
---

# URP 17.5 / RenderGraph — patrones del proyecto

Referencia viva del pass real: `Assets/Scripts/Runtime/Vision/VisionRendererFeature.cs` (leerlo
antes de escribir uno nuevo — es el patrón canónico del repo). Estado del sistema:
`docs/vision-optica.md`.

## Anatomía de un pass RenderGraph

- `ScriptableRenderPass.RecordRenderGraph(RenderGraph, ContextContainer)` es el único punto de
  entrada. **NUNCA** las APIs legacy: `Execute(ScriptableRenderContext, ...)`,
  `cmd.SetRenderTarget`, `OnRenderImage`, `CommandBuffer.Blit` crudo — compilan pero rompen el
  frame graph y el modo XR.
- Recursos por `ContextContainer`: `frameData.Get<UniversalResourceData>()` →
  `activeColorTexture`; texturas temporales con
  `UniversalRenderer.CreateRenderGraphTexture(...)`.
- Blit con material: `renderGraph.AddBlitPass(...)` o `RasterRenderPass` + `Blitter.BlitTexture`
  (patrón ping-pong src→temp→src como hace VisionRendererFeature).
- Punto de inyección: el post-proceso de visión va `BeforeRenderingTransparents` (decisión
  documentada en la doc viva — mantener salvo razón anotada).

## XR — single-pass instanced (Quest)

- La textura de color es **Texture2DArray** (slice por ojo). En HLSL: macros
  `UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX`, `TEXTURE2D_X` / `SAMPLE_TEXTURE2D_X`,
  `UNITY_VERTEX_INPUT_INSTANCE_ID` / `UNITY_VERTEX_OUTPUT_STEREO`.
- Efectos por ojo: uniforms separados `_XxxL` / `_XxxR` y selección por `unity_StereoEyeIndex`
  (patrón existente en `VisionPostProcess.shader` — replicarlo, no inventar otro).
- Probar SIEMPRE ambos ojos mentalmente: un efecto que lee el uniform del ojo equivocado "se ve
  bien" en la Game view mono y miente en el visor.

## Materiales e instancias

- Variar propiedades por instancia con **MaterialPropertyBlock** (patrón
  `GlareBillboardInstance.cs`), nunca `renderer.material` (clona el material → leak + rompe
  batching).
- Shader-globals (`Shader.SetGlobalFloat/Color`) para parámetros de sistema (patrón
  `GlareController`/`DisabilityGlareController`); properties de material para lo local al
  billboard.

## Presupuesto GPU (Quest = tile-based móvil)

- Cada pass extra = load/store de tile: **justificar cada pass y cada resolve**.
- Evitar: samples dependientes en cadena larga, `discard` masivo, texturas full-res temporales
  cuando media resolución alcanza, ramas divergentes por píxel en el shader de visión.
- El tier URP de Quest es **Mobile** (`Assets/Settings/Mobile_*`): los cambios de render
  features van ahí (PC_* es para pruebas en Editor de escritorio).

## Debug

- Errores de shader: `unity_console_log` (no salen por compilation_errors).
- Frame Debugger: pedirle al usuario que lo abra si hace falta inspección profunda (no hay tool
  MCP para eso).
- Verificación perceptual: capturas antes/después en el escenario correcto (glare/velo →
  ruta_noche; defocus/lectura → consultorio). La Game view mono NO valida estéreo: anotar
  siempre "pendiente validar en visor" cuando el efecto es por ojo.
