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
                    // override sobre el default, clampeado al [min,max] del ParamSpec (defensa
                    // en profundidad: lens_overrides.json puede venir corrupto o editado a mano).
                    state.Params[kv.Key] = ClampToSpec(kv.Key, kv.Value, lens.Params);
            return state;
        }

        /// <summary>
        /// Clampea un valor al rango [min, max] del ParamSpec de esa clave. Defensa en
        /// profundidad para overrides que llegan sin pasar por la UI de la tablet: aunque el
        /// canal tiene auth por PIN (P1.1), un cliente ya autenticado igual podria mandar
        /// cualquier valor (bug, version distinta, etc.). Si la clave no tiene spec conocido
        /// (param nuevo/desconocido) pasa sin clamp, igual que hoy. Si el spec no define un
        /// rango valido (min/max ausentes en el JSON => quedan en 0,0 por deserializacion)
        /// tambien pasa sin clamp, para no aplastar el valor a cero.
        /// </summary>
        public static float ClampToSpec(string key, float value, IReadOnlyDictionary<string, ParamSpec> specs)
        {
            if (specs == null || !specs.TryGetValue(key, out var spec) || spec == null) return value;
            if (spec.Max <= spec.Min) return value;
            return Math.Clamp(value, spec.Min, spec.Max);
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
