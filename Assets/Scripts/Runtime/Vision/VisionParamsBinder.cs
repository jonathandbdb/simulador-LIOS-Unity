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

        // catalog key -> (uniform ojo izq, uniform ojo der, piso si la clave falta en state.Params).
        // zeroDefault == null: NO pisar (los 4 focos: 0 es "foco desactivado" en el shader, forzarlo
        //   satura el centinela 1e9 de DefocusDiopters -- ver docs/catalogo-lentes.md).
        // zeroDefault == 0f: pisar con 0 (su default real) si la clave falta -- NUNCA dejar el
        //   uniform con el valor de la lente ANTERIOR. CatalogParser.MergeMissingParams indexa a
        //   los defaults por id de lente, asi que una lente que no existe en los defaults embebidos
        //   (p.ej. generic_a209ba91, "monofocal plus", creada por un admin desde la tablet) NUNCA
        //   recibe el merge y puede llegar sin cataract_scatter. Sin este piso, aplicar catarata
        //   (cataract_scatter=0.6) y despues esa monofocal deja el material con el _CataractScatterL/R
        //   de la lente ANTERIOR -- la monofocal se ve con el piso de blur + velo de la catarata.
        // El default va en la MISMA tupla que el uniform: agregar un param nuevo obliga al compilador
        // a decidir su piso aca mismo (no hay una segunda lista que mantener sincronizada a mano;
        // ver docs/catalogo-lentes.md, gotcha de PushEye).
        private static readonly Dictionary<string, (string l, string r, float? zeroDefault)> Map = new()
        {
            { "foco_lejos_m",       ("_FocoLejosL", "_FocoLejosR", null) },
            { "foco_intermedio_m",  ("_FocoIntermedioL", "_FocoIntermedioR", null) },
            { "foco_cerca_m",       ("_FocoCercaL", "_FocoCercaR", null) },
            { "profundidad_foco_m", ("_ProfundidadFocoL", "_ProfundidadFocoR", null) },
            { "desenfoque_max",     ("_DesenfoqueMaxL", "_DesenfoqueMaxR", 0f) },
            { "contrast_loss",      ("_ContrastLossL", "_ContrastLossR", 0f) },
            { "cataract_yellow",    ("_CataractL", "_CataractR", 0f) },
            { "cataract_scatter",   ("_CataractScatterL", "_CataractScatterR", 0f) },
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
                else if (kv.Value.zeroDefault.HasValue)
                    // Sin la clave: pisar con el piso declarado en el Map (ver comentario ahi),
                    // no dejar el uniform con el valor de la lente anterior. Los 4 focos declaran
                    // zeroDefault=null a proposito: 0 ahi significa "foco desactivado" y forzarlo
                    // satura el centinela 1e9 de DefocusDiopters (docs/catalogo-lentes.md).
                    visionMaterial.SetFloat(left ? kv.Value.l : kv.Value.r, kv.Value.zeroDefault.Value);
            }

            // Gate de CPU (3.1): publica "hay blur/contraste" por ojo para que la feature
            // pueda saltear el post-proceso cuando todo esta en cero. desenfoque_max es el
            // proxy del blur (si 0, no hay desenfoque posible; si >0, lo decide el shader).
            float des = state.Params.TryGetValue("desenfoque_max", out var d) ? d : 0f;
            float con = state.Params.TryGetValue("contrast_loss", out var c) ? c : 0f;
            // cataract_yellow DEBE entrar al gate: el tinte se aplica en el pass 1, asi que si el
            // gate lo ignora, con desenfoque_max=0 y contrast_loss=0 la feature no inyecta el pass
            // y el tinte desaparece (efecto uniforme por ojo sin senal en VisionActivity => apagado).
            float cat = state.Params.TryGetValue("cataract_yellow", out var ca) ? ca : 0f;
            // cataract_scatter DEBE entrar al gate por la misma razon, pero es mas critico
            // todavia: aporta un piso de radio de desenfoque Y un pedestal de velo INDEPENDIENTES
            // de la distancia y de cualquier fuente de glare en el campo (dispersion intraocular
            // del cristalino cataratoso). Si no entra, una lente con blur/contraste/tinte en 0
            // apagaria el pass aunque cataract_scatter > 0 -- la catarata dejaria de degradar la
            // vision lejana (no hay foco/glare que la "active" per-pixel, a diferencia de los
            // otros params). No asumir la clave presente: catalogos viejos (cache/backend sin
            // migrar) no la traen hasta que MergeMissingParams la complete.
            float scat = state.Params.TryGetValue("cataract_scatter", out var sc) ? sc : 0f;
            float act = Mathf.Max(Mathf.Max(Mathf.Max(des, con), cat), scat);
            if (left) VisionActivity.ParamsL = act; else VisionActivity.ParamsR = act;
        }
    }
}
