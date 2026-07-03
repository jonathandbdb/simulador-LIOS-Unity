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
    /// Hereda el ciclo de vida comun (espera del singleton + suscripcion + despacho inicial por
    /// ojo + desuscripcion) de VisionStateBinder (P6.1); aqui vive solo el mapeo especifico y el
    /// blend demo opt-in (via el hook OnAfterInitialDispatch).
    /// </summary>
    public class VisionParamsBinder : VisionStateBinder
    {
        [Tooltip("El MISMO material asignado en VisionRendererFeature.")]
        public Material visionMaterial;

        [Header("Demo / verificacion en visor (F3)")]
        [Tooltip("Aplica un blend de prueba al arrancar para validar el efecto por ojo. " +
                 "Opt-in: por defecto OFF para no pisar las lentes reales al arrancar.")]
        public bool applyDemoBlendOnStart = false;
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

        // Blend demo opt-in: corre tras el despacho inicial, dentro de la coroutine Start de la base.
        protected override IEnumerator OnAfterInitialDispatch()
        {
            if (!applyDemoBlendOnStart) yield break;
            while (_dm.Catalog == null) yield return null;
            _dm.ApplyLens(demoLeftLens, "left");
            _dm.ApplyLens(demoRightLens, "right");
            Debug.Log($"VisionParamsBinder: blend demo {demoLeftLens}(OI) / {demoRightLens}(OD).");
        }

        protected override void ApplyEyeState(string eye, EyeState state) => PushEye(eye, state);

        private void PushEye(string eye, EyeState state)
        {
            if (visionMaterial == null || state == null || state.IsEmpty) return;
            bool left = eye == "left";
            foreach (var kv in Map)
            {
                if (state.Params.TryGetValue(kv.Key, out float v))
                    visionMaterial.SetFloat(left ? kv.Value.l : kv.Value.r, v);
            }

            // Gate de CPU (3.1): publica "hay blur/contraste" por ojo para que la feature
            // pueda saltear el post-proceso cuando todo esta en cero. desenfoque_max es el
            // proxy del blur (si 0, no hay desenfoque posible; si >0, lo decide el shader).
            float des = state.Params.TryGetValue("desenfoque_max", out var d) ? d : 0f;
            float con = state.Params.TryGetValue("contrast_loss", out var c) ? c : 0f;
            float act = Mathf.Max(des, con);
            if (left) VisionActivity.ParamsL = act; else VisionActivity.ParamsR = act;
        }
    }
}
