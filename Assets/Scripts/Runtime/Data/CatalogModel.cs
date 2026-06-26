using System.Collections.Generic;
using Newtonsoft.Json;

namespace Simulador.Data
{
    // Modelo del catalogo de lentes. Mapea 1:1 el schema de defaults/lentes.json
    // (compat con el backend FastAPI). NO cambiar claves ni rangos.

    /// <summary>Especificacion de un parametro clinico: valor por defecto + rango valido.</summary>
    public class ParamSpec
    {
        [JsonProperty("default")] public float Default;
        [JsonProperty("min")] public float Min;
        [JsonProperty("max")] public float Max;
    }

    /// <summary>Definicion de una lente intraocular (IOL).</summary>
    public class LensDef
    {
        [JsonProperty("id")] public string Id;
        [JsonProperty("nombre")] public string Nombre;
        [JsonProperty("descripcion")] public string Descripcion;

        // Claves dinamicas (foco_lejos_m, halo_intensity, ...) -> {default,min,max}.
        // Dictionary para tolerar params nuevos que agregue el backend sin recompilar.
        [JsonProperty("params")] public Dictionary<string, ParamSpec> Params = new();
    }

    /// <summary>Catalogo completo: version + lista ordenada de lentes.</summary>
    public class LensCatalog
    {
        [JsonProperty("version")] public string Version;
        // Sin inicializador: si el JSON no trae la clave 'catalogo', queda null y
        // CatalogParser.Parse lo trata como invalido (igual que data.has("catalogo") en Godot).
        [JsonProperty("catalogo")] public List<LensDef> Catalogo;
    }

    /// <summary>
    /// Estado de vision de UN ojo: la lente aplicada y sus parametros numericos ya
    /// resueltos (defaults del catalogo + overrides de la tablet). Equivale al
    /// Dictionary que en Godot se emitia en vision_state_changed (alli lens_id viaja
    /// dentro del dict; aca se separa pero se reexpone aplanado para shader/red).
    /// </summary>
    public class EyeState
    {
        public string LensId = "";
        public Dictionary<string, float> Params = new();

        public bool IsEmpty => string.IsNullOrEmpty(LensId) && Params.Count == 0;

        public EyeState Clone()
        {
            return new EyeState
            {
                LensId = LensId,
                Params = new Dictionary<string, float>(Params)
            };
        }

        /// <summary>Aplana a {lens_id + params numericos} como el dict de Godot (para red/JSON).</summary>
        public Dictionary<string, object> ToFlatDict()
        {
            var d = new Dictionary<string, object>();
            foreach (var kv in Params) d[kv.Key] = kv.Value;
            d["lens_id"] = LensId;
            return d;
        }
    }
}
