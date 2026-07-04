using System;
using System.Collections.Generic;
using Newtonsoft.Json;

namespace Simulador.Net
{
    /// <summary>
    /// Logica PURA (sin Unity, sin IO) del emparejamiento persistente por token
    /// (opcion B, ver docs/networking.md "Decisiones y porques"): generacion del
    /// token y serializacion/deserializacion de las dos formas que necesita el
    /// protocolo:
    ///  - Lado visor: lista de tokens validos (persistentDataPath/paired_tokens.json,
    ///    NetworkController).
    ///  - Lado tablet: mapa host -> token (persistentDataPath/pairing.json,
    ///    TabletSession).
    /// Mismo patron de resiliencia que DataManagerLogic (ver docs/catalogo-lentes.md):
    /// JSON invalido/vacio/nulo -> false, nunca tira excepcion -- el llamador arranca
    /// vacio sin loguear error, igual que DataManager.LoadLensOverrides.
    /// </summary>
    public static class PairingStore
    {
        /// <summary>
        /// Token de emparejamiento nuevo: 2x Guid en hex (32+32 = 64 caracteres,
        /// ~256 bits de entropia). El espacio es lo bastante grande como para que un
        /// token invalido/revocado NUNCA se trate como indicio de fuerza bruta (a
        /// diferencia del PIN de 6 digitos, que si tiene lockout) -- ver Modelo de
        /// amenaza en docs/networking.md.
        /// </summary>
        public static string GenerateToken() =>
            Guid.NewGuid().ToString("N") + Guid.NewGuid().ToString("N");

        /// <summary>Serializa la lista de tokens emparejados del visor (paired_tokens.json). Descarta entradas vacias/nulas.</summary>
        public static string SerializeTokens(IEnumerable<string> tokens)
        {
            var list = new List<string>();
            if (tokens != null)
                foreach (var t in tokens)
                    if (!string.IsNullOrEmpty(t)) list.Add(t);
            return JsonConvert.SerializeObject(list, Formatting.Indented);
        }

        /// <summary>Intenta parsear paired_tokens.json. Devuelve false (result null) ante JSON invalido/vacio/nulo.</summary>
        public static bool TryParseTokens(string json, out List<string> tokens)
        {
            tokens = null;
            if (string.IsNullOrWhiteSpace(json)) return false;
            try
            {
                var parsed = JsonConvert.DeserializeObject<List<string>>(json);
                if (parsed == null) return false;
                tokens = parsed;
                return true;
            }
            catch (Exception)
            {
                return false;
            }
        }

        /// <summary>Serializa el mapa host -> token de la tablet (pairing.json).</summary>
        public static string SerializePairingMap(IDictionary<string, string> hostToToken)
        {
            return JsonConvert.SerializeObject(
                hostToToken ?? new Dictionary<string, string>(),
                Formatting.Indented);
        }

        /// <summary>Intenta parsear pairing.json. Devuelve false (result null) ante JSON invalido/vacio/nulo.</summary>
        public static bool TryParsePairingMap(string json, out Dictionary<string, string> hostToToken)
        {
            hostToToken = null;
            if (string.IsNullOrWhiteSpace(json)) return false;
            try
            {
                var parsed = JsonConvert.DeserializeObject<Dictionary<string, string>>(json);
                if (parsed == null) return false;
                hostToToken = parsed;
                return true;
            }
            catch (Exception)
            {
                return false;
            }
        }
    }
}
