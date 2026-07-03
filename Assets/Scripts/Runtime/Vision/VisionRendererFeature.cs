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

                // Ping-pong por ojo: pass 0 (defocus esferico) src->temp; pass 1
                // (astigmatismo + contraste + velo CIE) temp->src.
                renderGraph.AddBlitPass(
                    new RenderGraphUtils.BlitMaterialParameters(source, temp, _mat, 0), "VisionDefocus");
                renderGraph.AddBlitPass(
                    new RenderGraphUtils.BlitMaterialParameters(temp, source, _mat, 1), "VisionAstigContrastVeil");
            }
        }
    }
}
