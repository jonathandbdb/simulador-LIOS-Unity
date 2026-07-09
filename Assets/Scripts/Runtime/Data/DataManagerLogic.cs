using System;
using System.Collections.Generic;
using Newtonsoft.Json;

namespace Simulador.Data
{
    /// <summary>
    /// Logica PURA extraida de DataManager (sin Unity, sin IO, sin corrutinas):
    /// armado de la URL de sync con el backend y serializacion/deserializacion de
    /// lens_overrides.json. Existe para poder cubrir con tests EditMode las partes de
    /// DataManager que SI son puras (P6.5) sin redisenar el MonoBehaviour orquestador
    /// (cadena defaults/cache/backend por corrutinas + UnityWebRequest + debounce de
    /// guardado): eso sigue sin tests unitarios -- se valida por play mode, ver
    /// "Limite de cobertura" en docs/catalogo-lentes.md.
    /// </summary>
    public static class DataManagerLogic
    {
        /// <summary>
        /// Concatena backendUrl + endpoint normalizando la barra entre ambos: evita un
        /// "//" si backendUrl trae un trailing slash (p.ej. tipeado a mano en
        /// StreamingAssets/config.json, ver P2.4) y agrega la barra si endpoint no la
        /// trae. Nunca tira excepcion (backendUrl/endpoint nulos se tratan como "").
        /// </summary>
        public static string BuildSyncUrl(string backendUrl, string endpoint)
        {
            string baseUrl = (backendUrl ?? string.Empty).TrimEnd('/');
            string ep = endpoint ?? string.Empty;
            if (ep.Length > 0 && ep[0] != '/') ep = "/" + ep;
            return baseUrl + ep;
        }

        /// <summary>
        /// Serializa el diccionario de overrides (lens_id -> {param -> valor}) tal como
        /// lo persiste DataManager en lens_overrides.json (mismo formato/Formatting que
        /// se usaba inline antes de esta extraccion).
        /// </summary>
        public static string SerializeLensOverrides(Dictionary<string, Dictionary<string, float>> overrides)
        {
            return JsonConvert.SerializeObject(
                overrides ?? new Dictionary<string, Dictionary<string, float>>(),
                Formatting.Indented);
        }

        /// <summary>
        /// Intenta parsear el JSON de lens_overrides.json. Devuelve false (result en
        /// null) ante JSON invalido, vacio o que deserializa a null -- el llamador debe
        /// ignorar y seguir con los overrides que ya tenia (arrancar sin overrides es
        /// valido), igual que hacia el try/catch inline en
        /// DataManager.LoadLensOverrides. Nunca tira excepcion.
        /// </summary>
        public static bool TryParseLensOverrides(string json, out Dictionary<string, Dictionary<string, float>> result)
        {
            result = null;
            if (string.IsNullOrWhiteSpace(json)) return false;
            try
            {
                var parsed = JsonConvert.DeserializeObject<Dictionary<string, Dictionary<string, float>>>(json);
                if (parsed == null) return false;
                result = parsed;
                return true;
            }
            catch (Exception)
            {
                return false;
            }
        }

        // ---------------- Config del backend por capas ----------------
        private class BackendConfig { public string backend_url; }

        /// <summary>
        /// Extrae "backend_url" de un JSON de config (StreamingAssets/config.json o el
        /// override de persistentDataPath, mismo schema para ambos). Devuelve null si
        /// json es vacio/null, si no parsea, o si "backend_url" esta ausente/vacio --
        /// nunca tira excepcion. El llamador usa null para decidir "esta capa no aplica".
        /// </summary>
        public static string ExtractBackendUrl(string json)
        {
            if (string.IsNullOrWhiteSpace(json)) return null;
            try
            {
                var cfg = JsonConvert.DeserializeObject<BackendConfig>(json);
                return cfg != null && !string.IsNullOrWhiteSpace(cfg.backend_url) ? cfg.backend_url.Trim() : null;
            }
            catch (Exception)
            {
                return null;
            }
        }

        /// <summary>
        /// Resuelve la URL efectiva del backend con precedencia
        /// override (persistentDataPath) &gt; streaming (StreamingAssets, empaquetado en
        /// el build) &gt; defaultUrl (el [SerializeField] legacy, fallback de ultima
        /// instancia). Cada capa es opcional: streamingJson/overrideJson nulos, vacios o
        /// invalidos simplemente no participan y se sigue con la siguiente en la
        /// precedencia. "source" indica cual capa gano ("default" | "streaming" |
        /// "override") para que el llamador loguee sin duplicar el parseo. Nunca tira
        /// excepcion.
        /// </summary>
        public static string ResolveBackendUrl(string defaultUrl, string streamingJson, string overrideJson, out string source)
        {
            string resolved = defaultUrl ?? string.Empty;
            source = "default";

            string streamingUrl = ExtractBackendUrl(streamingJson);
            if (streamingUrl != null)
            {
                resolved = streamingUrl;
                source = "streaming";
            }

            string overrideUrl = ExtractBackendUrl(overrideJson);
            if (overrideUrl != null)
            {
                resolved = overrideUrl;
                source = "override";
            }

            return resolved;
        }
    }
}
