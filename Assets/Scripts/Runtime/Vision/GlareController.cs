using System.Collections;
using System.Collections.Generic;
using Simulador.Data;
using UnityEngine;

namespace Simulador.Vision
{
    /// <summary>
    /// Mapea los parametros de lente por ojo a los shader globals del glare billboard
    /// (port de GlareSource.set_eye_globals / set_astig_globals). halosEnabled refleja
    /// el escenario (consultorio de dia = off); el gating por escenario llega en F5.
    /// </summary>
    public class GlareController : MonoBehaviour
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

        // catalog key -> (global ojo izq, global ojo der)
        private static readonly Dictionary<string, (string l, string r)> Map = new()
        {
            { "halo_intensity",     ("glare_halo_l",  "glare_halo_r") },
            { "halo_extra_rings",   ("glare_pupil_l", "glare_pupil_r") },
            { "destello_intensity", ("glare_star_l",  "glare_star_r") },
            { "destello_rayos",     ("glare_rays_l",  "glare_rays_r") },
        };

        private DataManager _dm;

        private IEnumerator Start()
        {
            while (DataManager.Instance == null) yield return null;
            _dm = DataManager.Instance;
            _dm.VisionStateChanged += OnVisionChanged;
            SetEyeGlobals("left", _dm.Left);
            SetEyeGlobals("right", _dm.Right);
        }

        private void OnDisable()
        {
            if (_dm != null) _dm.VisionStateChanged -= OnVisionChanged;
        }

        private void OnVisionChanged(string eye, EyeState state) => SetEyeGlobals(eye, state);

        private void SetEyeGlobals(string eye, EyeState state)
        {
            if (state == null || state.IsEmpty) return;
            bool left = eye == "left";
            foreach (var kv in Map)
            {
                if (state.Params.TryGetValue(kv.Key, out float v))
                {
                    // Halos (anillos) y destellos (rayos) se escalan distinto por escenario.
                    // destello_rayos es CANTIDAD de rayos: no se escala (la intensidad la da destello_intensity).
                    float scale = kv.Key == "destello_intensity" ? starScale
                                : kv.Key == "destello_rayos" ? 1f
                                : haloScale; // halo_intensity, halo_extra_rings
                    Shader.SetGlobalFloat(left ? kv.Value.l : kv.Value.r, halosEnabled ? v * scale : 0f);
                }
            }
        }

        /// <summary>Astigmatismo GLOBAL (un valor para ambos ojos). magnitudeNorm 0..1, angle en rad.</summary>
        public void SetAstigmatism(bool enabled, float magnitudeNorm, float angleRad)
        {
            Shader.SetGlobalFloat("glare_astig", enabled ? Mathf.Clamp01(magnitudeNorm) : 0f);
            Shader.SetGlobalFloat("glare_astig_angle", angleRad);
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
