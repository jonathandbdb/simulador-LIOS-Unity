using System.Collections;
using System.Collections.Generic;
using Simulador.Data;
using UnityEngine;

namespace Simulador.Vision
{
    /// <summary>
    /// Puente DataManager -> material del post-proceso de vision. Escucha
    /// VisionStateChanged y empuja los params clinicos de cada ojo a los uniforms
    /// _XxxL / _XxxR del shader (equivale a SHADER_PARAM_MAP + _on_vision_state_changed
    /// en main.gd). Solo mapea los params que usa el post-proceso (blur + contraste);
    /// halo/destello van a los billboards de GlareSource (F4).
    /// </summary>
    public class VisionParamsBinder : MonoBehaviour
    {
        [Tooltip("El MISMO material asignado en VisionRendererFeature.")]
        public Material visionMaterial;

        [Header("Demo / verificacion en visor (F3)")]
        [Tooltip("Aplica un blend de prueba al arrancar para validar el efecto por ojo.")]
        public bool applyDemoBlendOnStart = true;
        public string demoLeftLens = "monofocal";
        public string demoRightLens = "panoptix";

        // catalog key -> (uniform ojo izq, uniform ojo der)
        private static readonly Dictionary<string, (string l, string r)> Map = new()
        {
            { "foco_lejos_m",       ("_FocoLejosL", "_FocoLejosR") },
            { "foco_intermedio_m",  ("_FocoIntermedioL", "_FocoIntermedioR") },
            { "foco_cerca_m",       ("_FocoCercaL", "_FocoCercaR") },
            { "profundidad_foco_m", ("_ProfundidadFocoL", "_ProfundidadFocoR") },
            { "desenfoque_max",     ("_DesenfoqueMaxL", "_DesenfoqueMaxR") },
            { "contrast_loss",      ("_ContrastLossL", "_ContrastLossR") },
        };

        private DataManager _dm;

        private IEnumerator Start()
        {
            while (DataManager.Instance == null) yield return null;
            _dm = DataManager.Instance;
            _dm.VisionStateChanged += OnVisionChanged;

            // Empujar el estado actual (si ya habia lentes aplicadas).
            PushEye("left", _dm.Left);
            PushEye("right", _dm.Right);

            if (applyDemoBlendOnStart)
            {
                while (_dm.Catalog == null) yield return null;
                _dm.ApplyLens(demoLeftLens, "left");
                _dm.ApplyLens(demoRightLens, "right");
                Debug.Log($"VisionParamsBinder: blend demo {demoLeftLens}(OI) / {demoRightLens}(OD).");
            }
        }

        private void OnDisable()
        {
            if (_dm != null) _dm.VisionStateChanged -= OnVisionChanged;
        }

        private void OnVisionChanged(string eye, EyeState state) => PushEye(eye, state);

        private void PushEye(string eye, EyeState state)
        {
            if (visionMaterial == null || state == null || state.IsEmpty) return;
            bool left = eye == "left";
            foreach (var kv in Map)
            {
                if (state.Params.TryGetValue(kv.Key, out float v))
                    visionMaterial.SetFloat(left ? kv.Value.l : kv.Value.r, v);
            }
        }
    }
}
