using System;
using System.Collections.Generic;

namespace Simulador.Data
{
    /// <summary>
    /// Logica PURA de aplicacion de lentes y overrides (sin Unity ni IO), portada 1:1
    /// de data_manager.gd. Testeable en EditMode.
    /// </summary>
    public static class LensEngine
    {
        // Epsilon de comparacion de floats (igual que absf(v-def) < 0.0005 en Godot).
        public const float DefaultEpsilon = 0.0005f;

        /// <summary>
        /// Construye el estado de un ojo para una lente: defaults del catalogo + overrides
        /// persistidos de esa lente aplicados ENCIMA. Equivale a apply_lens (sin emitir).
        /// </summary>
        public static EyeState BuildEyeState(LensDef lens, IReadOnlyDictionary<string, float> savedOverrides)
        {
            var state = new EyeState { LensId = lens.Id };
            foreach (var kv in lens.Params)
                state.Params[kv.Key] = kv.Value.Default;
            if (savedOverrides != null)
                foreach (var kv in savedOverrides)
                    state.Params[kv.Key] = kv.Value; // override sobre el default
            return state;
        }

        /// <summary>
        /// Flag Blend: activo cuando ambos ojos tienen lente y son distintas. Solo
        /// informativo (no condiciona a quien se aplica), igual que en Godot.
        /// </summary>
        public static bool ComputeBlend(string leftId, string rightId)
        {
            return !string.IsNullOrEmpty(leftId)
                && !string.IsNullOrEmpty(rightId)
                && leftId != rightId;
        }

        /// <summary>
        /// Actualiza el diccionario de overrides persistidos de una lente con cambios nuevos.
        /// Si un valor vuelve al default del catalogo (dentro de epsilon) el override se ELIMINA
        /// (archivo minimo + el "reset" de la tablet limpia de verdad). Devuelve el dict
        /// resultante (vacio => la lente no deberia conservar overrides).
        /// </summary>
        public static Dictionary<string, float> CleanOverrides(
            Dictionary<string, float> saved,
            IReadOnlyDictionary<string, float> newParams,
            IReadOnlyDictionary<string, ParamSpec> catalogParams,
            float epsilon = DefaultEpsilon)
        {
            saved ??= new Dictionary<string, float>();
            foreach (var kv in newParams)
            {
                if (kv.Key == "lens_id")
                    continue;
                bool hasDefault = catalogParams != null && catalogParams.TryGetValue(kv.Key, out var spec);
                if (hasDefault && Math.Abs(kv.Value - catalogParams[kv.Key].Default) < epsilon)
                    saved.Remove(kv.Key);
                else
                    saved[kv.Key] = kv.Value;
            }
            return saved;
        }
    }
}
