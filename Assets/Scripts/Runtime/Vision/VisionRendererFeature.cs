using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using UnityEngine.Rendering.RenderGraphModule;
using UnityEngine.Rendering.RenderGraphModule.Util;

namespace Simulador.Vision
{
    /// <summary>
    /// Renderer Feature URP que aplica el post-proceso de visualizacion IOL
    /// (blur dioptrico + perdida de contraste, por ojo). Implementado con la
    /// RenderGraph API de URP (Unity 6.5) — NO con la inyeccion de comandos vieja.
    ///
    /// Se inyecta ANTES del post-proceso: ahi activeColorTexture es un render target
    /// intermedio (no el backbuffer), condicion necesaria para leer+escribir el color.
    ///
    /// Encadena CUATRO blits (etapa C): dos a 1/16 de resolucion que construyen el tier de
    /// desenfoque grande (_VisionLowA -> _VisionLowB, publicado como global _VisionLowBlur) y
    /// los dos full-res del ping-pong original (defocus -> _VisionTemp -> astig/contraste/velos).
    ///
    /// Ademas publica los globals _VisionPxPerDeg (pixeles por grado, por ojo) y _VisionLowTexel:
    /// el radio del desenfoque se calcula en GRADOS en el shader y este es el unico lugar donde
    /// el alto del target por ojo, la matriz de proyeccion y el tamano del tier de baja son
    /// simultaneamente conocidos y correctos (incluyendo la camara mono de StreamingCapture y el
    /// Game View del editor).
    /// </summary>
    public class VisionRendererFeature : ScriptableRendererFeature
    {
        [Tooltip("Material con el shader Simulador/VisionPostProcess")]
        public Material material;
        // Inyectar tras opaco+skybox y ANTES de transparentes: asi los billboards
        // de glare (F4, cola transparente, aditivos) se componen ENCIMA de la imagen
        // ya borroseada y NO se difuminan — igual que en Godot (post-quad priority -1,
        // glare priority 10). Ademas el halo se suma despues del contraste (no se le
        // baja contraste), como en el shader original.
        public RenderPassEvent injectionPoint = RenderPassEvent.BeforeRenderingTransparents;

        private VisionPass _pass;
        // Gate de CPU (3.1): recuerda el estado para loguear solo en las TRANSICIONES.
        private bool _gateActive = true;

        public override void Create()
        {
            _pass = new VisionPass { renderPassEvent = injectionPoint };
        }

        public override void AddRenderPasses(ScriptableRenderer renderer, ref RenderingData renderingData)
        {
            if (material == null) return;

            // Gate de CPU: si NINGUN efecto es visible en ambos ojos (blur/contraste,
            // astigmatismo, velo), saltear la inyeccion => se evitan los 2 blits full-screen
            // por ojo ese frame. La decision es barata (lee estado C# agregado, no el material).
            bool active = VisionActivity.AnyActive;
            if (active != _gateActive)
            {
                _gateActive = active;
                Debug.Log($"[Vision] Post-proceso gate {(active ? "ON (hay efecto)" : "OFF (todo en cero: se saltea)")}.");
            }
            if (!active) return;

            _pass.Setup(material);
            _pass.ConfigureInput(ScriptableRenderPassInput.Depth); // necesitamos _CameraDepthTexture
            renderer.EnqueuePass(_pass);
        }

        protected override void Dispose(bool disposing) { }

        private class VisionPass : ScriptableRenderPass
        {
            private Material _mat;
            public void Setup(Material m) => _mat = m;

            // Pixeles por grado del render target, por ojo (x = izq, y = der).
            private static readonly int PxPerDegId = Shader.PropertyToID("_VisionPxPerDeg");
            // Texel (xy) y relacion baja/full (zw) del tier de desenfoque a 1/16.
            private static readonly int LowTexelId = Shader.PropertyToID("_VisionLowTexel");
            // Slot de textura global donde el pass 3 deja el tier de baja para el pass 0.
            private static readonly int LowBlurTexId = Shader.PropertyToID("_VisionLowBlur");

            // Divisor POR EJE del tier de baja (4 => 1/16 del area). No es 2 porque con 1/4 de
            // area un radio de 48 px full-res son 24 px de baja y ningun kernel razonable los
            // cubre; con 1/16 son ~12 px.
            // OJO (hallazgo N3 de review): 12 px de baja NO los cubre "densamente" la espiral de
            // 24 taps — la separacion media es 2*r/sqrt(N) = 4.9 px de baja contra un footprint de
            // ~1-1.5, o sea sub-muestreo de 3-4x. La densidad alcanza hasta ~3.5-4 px de baja
            // (radiusPx <~ 15). Se acepta porque la fase se dithera por pixel y el residuo sale
            // como ruido de alta frecuencia, no como copias coherentes. Detalle y palancas:
            // MAX_BLUR_DEG en VisionPostProcess.shader y docs/vision-optica.md.
            // El divisor tampoco es gratis por el otro lado: el tier no puede producir una PSF mas
            // angosta que ~1.58*LowDiv px full-res (ver LOW_PSF_VAR en el shader) => subir LowDiv
            // a 8 para ahorrar GPU subiria ese piso de ~6.3 a ~12.6 px.
            private const int LowDiv = 4;

            // Datos del pass que publica los globals (ver mas abajo por que es un pass y no un
            // Shader.SetGlobalVector inmediato).
            private class PublishData
            {
                public Vector4 pxPerDeg;
                public Vector4 lowTexel;
            }

            public override void RecordRenderGraph(RenderGraph renderGraph, ContextContainer frameData)
            {
                var resourceData = frameData.Get<UniversalResourceData>();
                var cameraData = frameData.Get<UniversalCameraData>();

                // No se puede leer+escribir el backbuffer directamente.
                if (resourceData.isActiveTargetBackBuffer) return;

                var source = resourceData.activeColorTexture;
                if (!source.IsValid()) return;

                var desc = cameraData.cameraTargetDescriptor;
                desc.msaaSamples = 1;
                desc.depthBufferBits = 0;
                var temp = UniversalRenderer.CreateRenderGraphTexture(renderGraph, desc, "_VisionTemp", false);

                // Tier de desenfoque a 1/16 (1/4 por eje). IMPRESCINDIBLE derivarlo del
                // cameraTargetDescriptor y tocar SOLO width/height: RenderGraphUtils.IsTextureXR
                // exige "volumeDepth > 1 && volumeDepth == TextureXR.slices", y un
                // RenderTextureDescriptor construido a mano deja volumeDepth = 1 => AddBlitPass no
                // detectaria XR, escribiria solo el slice 0 y el OJO DERECHO recibiria la imagen
                // del izquierdo. Es invisible en el Game View mono.
                var lowDesc = desc;
                lowDesc.width = Mathf.Max(1, desc.width / LowDiv);
                lowDesc.height = Mathf.Max(1, desc.height / LowDiv);
                var lowA = UniversalRenderer.CreateRenderGraphTexture(renderGraph, lowDesc, "_VisionLowA", false);
                var lowB = UniversalRenderer.CreateRenderGraphTexture(renderGraph, lowDesc, "_VisionLowB", false);

                // Pixeles por GRADO por ojo. El desenfoque del shader es una magnitud ANGULAR
                // (radio del circulo de desenfoque en grados); sin este factor el mismo efecto
                // se ve distinto en el Game View chico (~7 px/grado) que en Quest 3 (~17), y el
                // ancho de la ventana cambiaria la agudeza simulada del paciente.
                //   clip.y = m11*tan(theta) y pixel = (H/2)*ndc.y  =>  ppd = 0.5*H*m11*PI/180
                // H = alto POR OJO (cameraTargetDescriptor, ya respeta render scale). Vale para
                // frustums asimetricos. Abs() por si m11 llegara negativo: un radiusPx negativo
                // daria sharpW = 1 y APAGARIA el blur en silencio.
                float k = 0.5f * desc.height * Mathf.Deg2Rad;
                float m11L = Mathf.Abs(cameraData.GetProjectionMatrix(0).m11);
                // Blindaje MultiPass: GetProjectionMatrix(1) va a XRPass.GetProjMatrix(1), que en
                // MultiPass indexaria m_Views[1] de una lista de UNA vista (excepcion dentro de
                // RecordRenderGraph). El target con dos vistas es exactamente el que tiene dos
                // slices, que es tambien el unico caso donde el shader lee la componente .y
                // (unity_StereoEyeIndex == 1); con una sola vista, la vista 0 YA es el ojo que se
                // esta renderizando, asi que replicar m11L es lo correcto.
                float m11R = desc.volumeDepth > 1
                    ? Mathf.Abs(cameraData.GetProjectionMatrix(1).m11)
                    : m11L;

                // Los globals se publican con cmd.SetGlobalVector DENTRO del graph (pass propio,
                // sin attachments => no fuerza load/store de tile) y no con Shader.SetGlobalVector,
                // que es inmediato y no queda grabado en el command buffer: se sostenia con el
                // setup actual (URP compila+ejecuta+Submit() por camara y StreamingCapture
                // renderiza sincronico en LateUpdate) pero era fragil ante cualquier cambio de
                // orden. Los 4 blits son unsafe passes creados por AddBlitPass, que ya se queda con
                // su SetRenderFunc: no hay donde colgar el seteo, de ahi el pass dedicado.
                using (var pub = renderGraph.AddUnsafePass<PublishData>("VisionPublishGlobals", out var pd))
                {
                    pd.pxPerDeg = new Vector4(k * m11L, k * m11R, 0f, 0f);
                    pd.lowTexel = new Vector4(
                        1f / lowDesc.width, 1f / lowDesc.height,
                        (float)lowDesc.width / desc.width, (float)lowDesc.height / desc.height);
                    // Sin attachments ni recursos: el graph lo cularia por "no produce nada".
                    pub.AllowPassCulling(false);
                    // INVARIANTE IMPLICITA — NO MOVER ESTE PASS (hallazgo MENOR de review). No
                    // declara NINGUNA dependencia de recursos, asi que su correctitud descansa
                    // SOLO en que RenderGraph respete el orden de declaracion: AllowPassCulling
                    // (false) evita que lo culen, pero NO lo ordena respecto de los 4 blits que
                    // consumen los globals. Tiene que quedar declarado ANTES del primer blit.
                    // Si algun dia hace falta el orden garantizado por dependencia, el camino es
                    // convertir los 4 blits en AddRasterRenderPass a mano y setear los globals en
                    // el SetRenderFunc del primero (ver skill urp-rendergraph-patterns).
                    pub.SetRenderFunc(static (PublishData d, UnsafeGraphContext ctx) =>
                    {
                        ctx.cmd.SetGlobalVector(PxPerDegId, d.pxPerDeg);
                        ctx.cmd.SetGlobalVector(LowTexelId, d.lowTexel);
                    });
                }

                // 1) Downsample box 4x4 a 1/16.
                renderGraph.AddBlitPass(
                    new RenderGraphUtils.BlitMaterialParameters(source, lowA, _mat, 2), "VisionLowDown");

                // 2) Gather de radio variable a 1/16. SetGlobalTextureAfterPass deja lowB en el
                // slot global _VisionLowBlur; el pass 0 lo declara con UseGlobalTexture, lo que
                // genera la dependencia read-after-write (garantiza el orden) y ademas hace que
                // RenderGraph emita el cmd.SetGlobalTexture al terminar este pass.
                using (var b = renderGraph.AddBlitPass(
                    new RenderGraphUtils.BlitMaterialParameters(lowA, lowB, _mat, 3), "VisionLowGather", true))
                {
                    b.SetGlobalTextureAfterPass(lowB, LowBlurTexId);
                }

                // 3) Ping-pong full-res por ojo: pass 0 (defocus esferico, disco full-res + tier
                // de baja) src->temp; 4) pass 1 (astig + contraste + velos) temp->src.
                using (var b = renderGraph.AddBlitPass(
                    new RenderGraphUtils.BlitMaterialParameters(source, temp, _mat, 0), "VisionDefocus", true))
                {
                    b.UseGlobalTexture(LowBlurTexId, AccessFlags.Read);
                }
                renderGraph.AddBlitPass(
                    new RenderGraphUtils.BlitMaterialParameters(temp, source, _mat, 1), "VisionAstigContrastVeil");
            }
        }
    }
}
