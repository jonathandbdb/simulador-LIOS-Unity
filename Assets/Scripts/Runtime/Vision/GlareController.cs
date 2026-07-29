using System.Collections.Generic;
using Simulador.Data;
using UnityEngine;

namespace Simulador.Vision
{
    /// <summary>
    /// Mapea los parametros de lente por ojo a los shader globals del glare billboard
    /// (port de GlareSource.set_eye_globals / set_astig_globals). halosEnabled y las
    /// escalas halo/starScale reflejan el escenario: ScenarioManager las ajusta al
    /// alternar consultorio <-> ruta_noche.
    /// Hereda el ciclo de vida comun (espera del singleton + suscripcion + despacho inicial por
    /// ojo + desuscripcion) de VisionStateBinder (P6.1). Conserva su propio OnEnable (publica los
    /// umbrales de facing antes del primer render, independiente de DataManager).
    /// </summary>
    public class GlareController : VisionStateBinder
    {
        [Tooltip("Off apaga halos/starburst. Lo controla el escenario.")]
        public bool halosEnabled = true;

        [Range(0f, 1f)]
        [Tooltip("Escala de los HALOS (anillos/glow difractivos). Noche=1; de dia ~0 " +
                 "(pupila contraida + fondo claro => los anillos se lavan). Lo setea ScenarioManager.")]
        public float haloScale = 1f;

        [Range(0f, 1f)]
        [Tooltip("Escala de los DESTELLOS/starburst (rayos). De dia siguen visibles alrededor " +
                 "del sol; clinicamente predominan sobre los halos a plena luz. Lo setea ScenarioManager.")]
        public float starScale = 1f;

        // Umbrales del "facing" (cuanto el haz de una fuente direccional apunta a la camara):
        // FUENTE UNICA compartida entre el billboard (shader) y el velo (DisabilityGlareController).
        // Como HLSL y C# no comparten constantes, C# los publica como globals de shader en Start
        // (_GlareFacingLo/_GlareFacingHi) y el velo los usa directo. smoothstep(Lo, Hi, dot(haz, haciaCamara)).
        public const float FacingLo = 0.05f;
        public const float FacingHi = 0.35f;

        // catalog key -> (global ojo izq, global ojo der)
        private static readonly Dictionary<string, (string l, string r)> Map = new()
        {
            { "halo_intensity",     ("glare_halo_l",  "glare_halo_r") },
            { "halo_extra_rings",   ("glare_pupil_l", "glare_pupil_r") },
            { "destello_intensity", ("glare_star_l",  "glare_star_r") },
            { "destello_rayos",     ("glare_rays_l",  "glare_rays_r") },
        };

        private void OnEnable()
        {
            // Publica los umbrales de facing LO ANTES POSIBLE (no dependen de DataManager):
            // el billboard hace smoothstep(_GlareFacingLo, _GlareFacingHi, ...); si valieran
            // 0/0 (globals sin setear) seria smoothstep(0,0,x) DEGENERADO los primeros frames.
            // Start espera a DataManager (coroutine) => demasiado tarde. OnEnable corre antes
            // del primer render y sin dependencias externas.
            Shader.SetGlobalFloat("_GlareFacingLo", FacingLo);
            Shader.SetGlobalFloat("_GlareFacingHi", FacingHi);
        }

        protected override void ApplyEyeState(string eye, EyeState state) => SetEyeGlobals(eye, state);

        private void SetEyeGlobals(string eye, EyeState state)
        {
            if (state == null || state.IsEmpty) return;
            bool left = eye == "left";
            foreach (var kv in Map)
            {
                if (state.Params.TryGetValue(kv.Key, out float v))
                {
                    // halo_extra_rings llega en mm de pupila (rango clinico 1-6, v0.6.0);
                    // el shader del billboard consume 0-1 (satura glare_pupil_*): normalizar
                    // ACA, en la frontera con el shader. Valores <1 (catalogos viejos 0-1
                    // cacheados) normalizan a 0 hasta que el sync actualice la cache.
                    if (kv.Key == "halo_extra_rings")
                        v = Mathf.Clamp01((v - 1f) / 5f);
                    // Halos (anillos) y destellos (rayos) se escalan distinto por escenario.
                    // destello_rayos es CANTIDAD de rayos: no se escala (la intensidad la da destello_intensity).
                    float scale = kv.Key == "destello_intensity" ? starScale
                                : kv.Key == "destello_rayos" ? 1f
                                : haloScale; // halo_intensity, halo_extra_rings
                    Shader.SetGlobalFloat(left ? kv.Value.l : kv.Value.r, halosEnabled ? v * scale : 0f);
                }
            }

            // Transmitancia ambar del cristalino (cataract_yellow) -> billboards de glare.
            // Los billboards son cola Transparent y el pass de vision se inyecta en
            // BeforeRenderingTransparents, asi que el filtro ambar del post-proceso NO los alcanza:
            // sin esto, un paciente con catarata brunescente ve la escena ambar y los halos de los
            // faros BLANCOS. La luz de un faro es luz DIRECTA cruzando el mismo cristalino
            // absorbente (ver docs/vision-optica.md, §Tinte amarillo de catarata).
            // NO se escala por haloScale ni se apaga con halosEnabled: es un FILTRO de absorcion
            // del ojo, no un halo (mismo criterio que el astigmatismo de abajo).
            // Sin la clave se publica 0 y no se deja el ambar de la lente ANTERIOR: mismo piso que
            // VisionParamsBinder.Map (una lente creada por un admin desde la tablet puede no estar
            // en los defaults embebidos y MergeMissingParams indexa por id, asi que puede llegar
            // sin cataract_yellow).
            float yellow = state.Params.TryGetValue("cataract_yellow", out float cy) ? Mathf.Clamp01(cy) : 0f;
            Shader.SetGlobalFloat(left ? "glare_cataract_l" : "glare_cataract_r", yellow);

            // Astigmatismo residual del catalogo (P4.4): magnitud normalizada (misma escala
            // que el shader) + eje en GRADOS -> radianes. Se publica por el MISMO camino
            // per-eye que el override live (SetAstigmatism), que ademas actualiza
            // VisionActivity.Astig (gate de CPU). PRECEDENCIA: ultimo que escribe gana. El
            // comando live set_astigmatism (tablet, no-persistente) pisa este valor; pero un
            // VisionStateChanged posterior (cualquier OverrideParams/ApplyLens) RE-asserta el
            // del catalogo. Documentado como gotcha en docs/vision-optica.md.
            // Independiente de halosEnabled: el astigmatismo es un defecto optico, no un halo.
            if (state.Params.TryGetValue("astig_magnitude", out float astigMag))
            {
                float axisDeg = state.Params.TryGetValue("astig_axis_deg", out float ax) ? ax : 0f;
                SetAstigmatism(eye, astigMag > Mathf.Epsilon, astigMag, axisDeg * Mathf.Deg2Rad);
            }
        }

        /// <summary>
        /// Astigmatismo POR OJO. eye: "left" | "right" | "both" (misma convencion que
        /// DataManager.OverrideParams). magnitudeNorm 0..1, angle en rad. Publica los globals
        /// per-eye glare_astig_l/r y glare_astig_angle_l/r (patron glare_*_l/r del resto del
        /// glare); cada global es el estado por ojo, independiente del otro.
        /// </summary>
        public void SetAstigmatism(string eye, bool enabled, float magnitudeNorm, float angleRad)
        {
            float mag = enabled ? Mathf.Clamp01(magnitudeNorm) : 0f;
            if (eye == "left" || eye == "both")
            {
                Shader.SetGlobalFloat("glare_astig_l", mag);
                Shader.SetGlobalFloat("glare_astig_angle_l", angleRad);
                VisionActivity.AstigL = mag;   // gate de CPU (3.1)
            }
            if (eye == "right" || eye == "both")
            {
                Shader.SetGlobalFloat("glare_astig_r", mag);
                Shader.SetGlobalFloat("glare_astig_angle_r", angleRad);
                VisionActivity.AstigR = mag;   // gate de CPU (3.1)
            }
        }

        /// <summary>Re-empuja el estado actual (p.ej. al cambiar halosEnabled desde un escenario).</summary>
        public void Refresh()
        {
            if (_dm == null) return;
            SetEyeGlobals("left", _dm.Left);
            SetEyeGlobals("right", _dm.Right);
        }
    }
}
