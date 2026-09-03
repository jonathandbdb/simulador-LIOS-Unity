using System.Collections.Generic;
using System.Globalization;
using UnityEngine;

namespace Simulador.Localization
{
    /// <summary>
    /// Motor de localizacion es/en del simulador (Fase D1, ver docs/localizacion.md).
    /// Estatico, sin MonoBehaviour: usable desde plain C# (Simulador.Tablet.TabletSession)
    /// ademas de MonoBehaviours. El idioma se resuelve UNA vez (override persistido &gt;
    /// idioma del sistema) y NO cambia en caliente durante una sesion -- el clinico no
    /// cambia de idioma a mitad de consulta (ver docs/localizacion.md "Decisiones").
    /// Las tablas es/en viven separadas en <see cref="L10nTable"/> para no enterrar la
    /// logica del motor bajo el volumen de strings.
    /// </summary>
    public static class L10n
    {
        private static string _lang;
        private static readonly HashSet<string> _warnedMissing = new();

        /// <summary>Idioma activo ("es" | "en"). Auto-inicializa con el idioma del sistema si nadie llamo <see cref="Initialize"/> todavia.</summary>
        public static string Lang
        {
            get
            {
                if (_lang == null) Initialize(null);
                return _lang;
            }
        }

        /// <summary>
        /// Resuelve y fija el idioma activo. <paramref name="overrideCode"/> ("es"/"en")
        /// gana si es valido; si es null/vacio/desconocido cae a
        /// <see cref="ResolveFromSystem"/>. Idempotente: puede volver a llamarse (p.ej.
        /// la tablet la llama en Start() antes de construir la UI) sin efectos raros --
        /// el idioma solo se recalcula, nunca dispara un evento de cambio en caliente
        /// (no hay uno: la app no soporta cambiar de idioma a mitad de sesion, ver
        /// docs/localizacion.md).
        /// </summary>
        public static void Initialize(string overrideCode)
        {
            if (overrideCode == "es" || overrideCode == "en") _lang = overrideCode;
            else _lang = ResolveFromSystem(Application.systemLanguage);
        }

        /// <summary>
        /// Logica PURA y testeable: mapea <see cref="SystemLanguage"/> a "es"/"en".
        /// Unico caso que da "es" es Spanish; CUALQUIER otro idioma (incluidos
        /// portugues, aleman, frances, Unknown, etc.) cae a "en" -- default
        /// internacional: un clinico en un pais no hispanohablante con el Quest en su
        /// propio idioma ve ingles, no espanol, en vez de que le aparezca un idioma
        /// arbitrario que tampoco entiende (ver docs/localizacion.md).
        /// </summary>
        public static string ResolveFromSystem(SystemLanguage sys) =>
            sys == SystemLanguage.Spanish ? "es" : "en";

        /// <summary>Todas las claves conocidas (union es/en), para el test de completitud.</summary>
        public static IReadOnlyCollection<string> Keys => L10nTable.AllKeys;

        /// <summary>
        /// true si <paramref name="key"/> existe en la tabla "es" (fuente). D2: lo
        /// usa el fallback por id de escenario de la tablet
        /// (<c>L10n.Has("scenario." + id) ? L10n.T(...) : label</c>, ver
        /// TabletController.ScenarioLabel/docs/localizacion.md) para decidir si hay
        /// traduccion antes de reemplazar el label crudo que manda el visor.
        /// </summary>
        public static bool Has(string key) => L10nTable.Es.ContainsKey(key);

        /// <summary>
        /// Texto de <paramref name="key"/> en el idioma activo; si falta ahi cae al
        /// texto en "es" (idioma fuente); si tampoco existe devuelve la propia
        /// <paramref name="key"/> y loguea un warning UNA sola vez por clave (para no
        /// inundar la consola si un widget se repinta seguido).
        /// </summary>
        public static string T(string key)
        {
            var table = Lang == "en" ? L10nTable.En : L10nTable.Es;
            if (table.TryGetValue(key, out var value)) return value;
            if (L10nTable.Es.TryGetValue(key, out var fallback)) return fallback;
            if (_warnedMissing.Add(key)) Debug.LogWarning($"[L10n] clave faltante: {key}");
            return key;
        }

        /// <summary>
        /// Como <see cref="T(string)"/> pero formatea el resultado con
        /// <see cref="string.Format(System.IFormatProvider, string, object[])"/>
        /// (InvariantCulture, igual que el resto del proyecto para numeros/JSON --
        /// no se toca ese criterio). Un placeholder mal puesto (args de mas/menos)
        /// jamas debe tirar una excepcion hacia la UI: se loguea y se devuelve el
        /// texto SIN formatear.
        /// </summary>
        public static string T(string key, params object[] args)
        {
            string format = T(key);
            try
            {
                return string.Format(CultureInfo.InvariantCulture, format, args);
            }
            catch (System.Exception e) when (e is System.FormatException || e is System.ArgumentNullException)
            {
                // MENOR (correcciones): ArgumentNullException salta si args es null
                // (llamada T(key, null) o T(key) resuelto por la sobrecarga de
                // params con un unico argumento null) -- mismo criterio que
                // FormatException, nunca debe llegar a la UI.
                Debug.LogWarning($"[L10n] error de formato en clave '{key}': {e.Message}");
                return format;
            }
        }
    }
}
