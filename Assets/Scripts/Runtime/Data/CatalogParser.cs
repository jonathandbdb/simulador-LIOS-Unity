using System.Collections.Generic;
using Newtonsoft.Json;

namespace Simulador.Data
{
    /// <summary>
    /// Parseo y normalizacion del catalogo. Logica PURA (sin IO ni Unity) para que
    /// sea testeable con el Test Framework en EditMode.
    /// </summary>
    public static class CatalogParser
    {
        /// <summary>
        /// Parsea el JSON del catalogo. Devuelve null si es invalido o no tiene una
        /// lista 'catalogo' (equivale al Dictionary vacio de _parse_catalog_json en Godot).
        /// </summary>
        public static LensCatalog Parse(string json)
        {
            if (string.IsNullOrWhiteSpace(json))
                return null;
            try
            {
                var cat = JsonConvert.DeserializeObject<LensCatalog>(json);
                if (cat == null || cat.Catalogo == null)
                    return null;
                return cat;
            }
            catch (JsonException)
            {
                return null;
            }
        }

        /// <summary>
        /// Completa params FALTANTES de cada lente con los del catalogo embebido (defaults).
        /// Un catalogo viejo (cache o backend sin migrar) puede no traer claves nuevas
        /// (p.ej. destello_*): sin este merge esos efectos quedarian apagados aunque el
        /// shader los soporte. Solo agrega claves ausentes; NUNCA pisa valores existentes.
        /// Devuelve cuantos params se agregaron.
        /// </summary>
        public static int MergeMissingParams(LensCatalog target, LensCatalog defaults)
        {
            if (target?.Catalogo == null || defaults?.Catalogo == null)
                return 0;

            // Index defaults por id para lookup O(1).
            var byId = new Dictionary<string, LensDef>();
            foreach (var d in defaults.Catalogo)
                if (d != null && d.Id != null) byId[d.Id] = d;

            int added = 0;
            foreach (var lens in target.Catalogo)
            {
                if (lens == null || lens.Id == null || !byId.TryGetValue(lens.Id, out var dlens))
                    continue;
                lens.Params ??= new Dictionary<string, ParamSpec>();
                foreach (var kv in dlens.Params)
                {
                    if (!lens.Params.ContainsKey(kv.Key))
                    {
                        lens.Params[kv.Key] = kv.Value;
                        added++;
                    }
                }
            }
            return added;
        }

        public static int CountLenses(LensCatalog cat) => cat?.Catalogo?.Count ?? 0;
    }
}
