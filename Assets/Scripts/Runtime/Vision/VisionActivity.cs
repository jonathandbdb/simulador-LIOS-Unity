using UnityEngine;

namespace Simulador.Vision
{
    /// <summary>
    /// Estado agregado "hay efecto visible" por ojo, para que VisionRendererFeature
    /// decida BARATO si saltear el post-proceso (gate de CPU: evita 2 blits full-screen
    /// por ojo cuando todos los efectos estan en cero). Lo escriben los binders con el
    /// estado C# que YA conocen (no se lee el material):
    ///   - VisionParamsBinder: ParamsL/R = max(desenfoque_max, contrast_loss, cataract_yellow,
    ///     cataract_scatter) por ojo.
    ///   - GlareController:     AstigL/R  = magnitud de astigmatismo por ojo.
    ///   - DisabilityGlareController: VeilL/R = velo SUAVIZADO actual (no el target).
    /// Criterio conservador: desenfoque_max &gt; 0 mantiene el pass aunque todo este en
    /// foco (no se puede saber per-pixel sin correr el shader).
    /// </summary>
    public static class VisionActivity
    {
        // Umbral comun (mismo epsilon que usan los shaders para "no-op").
        private const float Eps = 0.001f;

        public static float ParamsL, ParamsR;   // max(desenfoque_max, contrast_loss, cataract_yellow, cataract_scatter)
        public static float AstigL, AstigR;      // magnitud astigmatismo 0..1
        public static float VeilL, VeilR;        // velo suavizado 0..1

        /// <summary>True si algun efecto es no-nulo en cualquiera de los dos ojos.</summary>
        public static bool AnyActive =>
            ParamsL > Eps || ParamsR > Eps ||
            AstigL  > Eps || AstigR  > Eps ||
            VeilL   > Eps || VeilR   > Eps;

        /// <summary>
        /// Resetea los estaticos al entrar a Play. Simetria con
        /// GlareBillboardInstance.ResetRegistry: en fast-enter-playmode (sin domain reload)
        /// los estaticos conservan el ultimo valor de la sesion anterior y el gate podria
        /// arrancar ON espurio (2 blits full-screen por ojo sin efecto real) hasta que los
        /// binders vuelvan a publicar. Esto solo evita ese arranque sucio; el estado real lo
        /// reponen VisionParamsBinder/GlareController/DisabilityGlareController en Start/evento.
        /// </summary>
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void ResetState()
        {
            ParamsL = ParamsR = 0f;
            AstigL  = AstigR  = 0f;
            VeilL   = VeilR   = 0f;
        }
    }
}
