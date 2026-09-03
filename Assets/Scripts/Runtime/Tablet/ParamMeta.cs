using System.Collections.Generic;
using System.Globalization;
using Simulador.Localization;

namespace Simulador.Tablet
{
    /// <summary>
    /// Metadata clinica de los parametros de lente que llegan en el catalogo.
    /// Port VERBATIM de features/tablet/ui/param_meta.gd. Las claves son las del
    /// catalogo/shader (no afecta al protocolo).
    ///
    /// Localizacion (D1, ver docs/localizacion.md): <see cref="Entry"/> guarda
    /// CLAVES de L10n (<c>LabelKey</c>/<c>HintKey</c>), no texto resuelto -- si
    /// guardara el texto ya traducido en el inicializador estatico de
    /// <see cref="META"/>, quedaria fijado en el idioma que estuviera activo la
    /// PRIMERA vez que algo toque el tipo <c>ParamMeta</c> (orden de
    /// inicializacion estatica de C#, fragil: podria correr antes de que
    /// TabletController.Start() llame L10n.Initialize(override)). Resolver la
    /// clave recien en <see cref="LabelFor"/>/<see cref="HintFor"/>/
    /// <see cref="FormatValue"/> (llamados en cada RefreshParamsPanel/
    /// ParamRowView.Create, no en el cctor) evita ese problema por completo: el
    /// texto se calcula con el idioma YA resuelto en ese momento.
    /// </summary>
    public static class ParamMeta
    {
        public class Entry
        {
            public string LabelKey;
            public string HintKey;
            public string Unit; // "m", "mm", "°", "rayos", ""
            public string Fmt;  // "F2" (%.2f) o "F0" (%.0f)
        }

        public static readonly Dictionary<string, Entry> META = new()
        {
            ["foco_lejos_m"] = new Entry
            {
                LabelKey = "param.foco_lejos_m.label",
                HintKey = "param.foco_lejos_m.hint",
                Unit = "m", Fmt = "F2",
            },
            ["foco_intermedio_m"] = new Entry
            {
                LabelKey = "param.foco_intermedio_m.label",
                HintKey = "param.foco_intermedio_m.hint",
                Unit = "m", Fmt = "F2",
            },
            ["foco_cerca_m"] = new Entry
            {
                LabelKey = "param.foco_cerca_m.label",
                HintKey = "param.foco_cerca_m.hint",
                Unit = "m", Fmt = "F2",
            },
            ["profundidad_foco_m"] = new Entry
            {
                LabelKey = "param.profundidad_foco_m.label",
                HintKey = "param.profundidad_foco_m.hint",
                Unit = "m", Fmt = "F2",
            },
            ["desenfoque_max"] = new Entry
            {
                // Etapa B (v0.8.0) cambia la semantica: de cap 0..1 a MULTIPLICADOR del radio
                // fisico del circulo de desenfoque. 1 = optica real; >1 exagera para que un
                // desenfoque sub-pixel (invisible en el visor) se vea; 0 = nunca borroso.
                LabelKey = "param.desenfoque_max.label",
                HintKey = "param.desenfoque_max.hint",
                Unit = "", Fmt = "F2",
            },
            ["halo_intensity"] = new Entry
            {
                LabelKey = "param.halo_intensity.label",
                HintKey = "param.halo_intensity.hint",
                Unit = "", Fmt = "F2",
            },
            ["halo_extra_rings"] = new Entry
            {
                LabelKey = "param.halo_extra_rings.label",
                HintKey = "param.halo_extra_rings.hint",
                Unit = "mm", Fmt = "F1",
            },
            ["contrast_loss"] = new Entry
            {
                LabelKey = "param.contrast_loss.label",
                HintKey = "param.contrast_loss.hint",
                Unit = "", Fmt = "F2",
            },
            ["destello_intensity"] = new Entry
            {
                LabelKey = "param.destello_intensity.label",
                HintKey = "param.destello_intensity.hint",
                Unit = "", Fmt = "F2",
            },
            ["destello_rayos"] = new Entry
            {
                LabelKey = "param.destello_rayos.label",
                HintKey = "param.destello_rayos.hint",
                Unit = "rayos", Fmt = "F0",
            },
            ["straylight"] = new Entry
            {
                LabelKey = "param.straylight.label",
                HintKey = "param.straylight.hint",
                Unit = "", Fmt = "F2",
            },
            // P4.4: astigmatismo residual PERSISTENTE por lente (vive en el catalogo,
            // distinto del ajuste LIVE de la card "Astigmatismo" -- ver
            // TabletController.BuildAstigCard/SendAstigmatism y el hint que agrega esa
            // card sobre la precedencia entre ambos).
            ["astig_magnitude"] = new Entry
            {
                LabelKey = "param.astig_magnitude.label",
                HintKey = "param.astig_magnitude.hint",
                Unit = "", Fmt = "F2",
            },
            ["astig_axis_deg"] = new Entry
            {
                LabelKey = "param.astig_axis_deg.label",
                HintKey = "param.astig_axis_deg.hint",
                Unit = "°", Fmt = "F0",
            },
            // v0.7.0: tinte de catarata del cristalino NATIVO (no un artefacto de la
            // LIO) -- separado del resto de disfotopsias porque modela el cristalino
            // sin operar, no la lente implantada. Alimenta el shader/binder de
            // @vision-optics (_CataractL/R). Ver docs/catalogo-lentes.md.
            ["cataract_yellow"] = new Entry
            {
                LabelKey = "param.cataract_yellow.label",
                HintKey = "param.cataract_yellow.hint",
                Unit = "", Fmt = "F2",
            },
            // v0.8.0: dispersion intraocular del cristalino cataratoso -- separado del tinte
            // porque es un mecanismo optico distinto (van den Berg / C-Quant straylight): baja
            // la nitidez a TODA distancia (no solo fuera de foco) y agrega un velo difuso sin
            // necesidad de una fuente de luz en el campo. Ver docs/catalogo-lentes.md.
            ["cataract_scatter"] = new Entry
            {
                LabelKey = "param.cataract_scatter.label",
                HintKey = "param.cataract_scatter.hint",
                Unit = "", Fmt = "F2",
            },
        };

        // P7: whitelist del modo STANDARD — los unicos parametros visibles/editables
        // en la UI simplificada (carrusel) y en el "Ajuste fino" Pro sobre lentes
        // que no son propias (base/genericas). Fuente unica compartida.
        public static readonly string[] STANDARD_PARAMS =
        {
            "astig_magnitude", "astig_axis_deg",
            "halo_intensity", "halo_extra_rings",
            "destello_intensity", "destello_rayos",
            // v0.7.0: mostrar el avance de la catarata al paciente es un caso de uso
            // del modo Standard (no requiere edicion completa de una lente propia).
            "cataract_yellow",
            // v0.8.0: mismo caso de uso que cataract_yellow (mostrar el avance de la
            // catarata al paciente).
            "cataract_scatter",
        };

        /// <summary>True si el parametro esta permitido en el modo Standard (P7).</summary>
        public static bool IsStandardParam(string p) => System.Array.IndexOf(STANDARD_PARAMS, p) >= 0;

        // Orden clinico de presentacion: focos -> blur/astigmatismo (ambos son error
        // refractivo, a diferencia de los halos/destellos que son artefactos difractivos)
        // -> disfotopsias. Parametros del catalogo que no esten aca se agregan al final
        // (orden del catalogo).
        public static readonly string[] ORDER =
        {
            "foco_lejos_m", "foco_intermedio_m", "foco_cerca_m",
            "profundidad_foco_m", "desenfoque_max",
            "astig_magnitude", "astig_axis_deg",
            "halo_intensity", "halo_extra_rings",
            "destello_intensity", "destello_rayos",
            "straylight",
            "contrast_loss",
            "cataract_yellow",
            "cataract_scatter",
        };

        public static string LabelFor(string p) => META.TryGetValue(p, out var m) ? L10n.T(m.LabelKey) : p;
        public static string HintFor(string p) => META.TryGetValue(p, out var m) ? L10n.T(m.HintKey) : "";
        public static bool IsInteger(string p) => META.TryGetValue(p, out var m) && m.Fmt == "F0";

        /// <summary>Texto del codigo de unidad ("m"/"mm"/"°" son iguales en ambos idiomas; "rayos" se traduce).</summary>
        private static string UnitText(string unitCode) =>
            unitCode == "rayos" ? L10n.T("param.unit.rayos") : (unitCode ?? "");

        public static string FormatValue(string p, float value)
        {
            META.TryGetValue(p, out var m);
            string unit = UnitText(m?.Unit);
            string fmt = m?.Fmt ?? "F2";
            // Distancias en metros: 0 = foco desactivado. MENOR (correcciones):
            // el literal salia sin pasar por L10n -- reusa common.off, misma
            // semantica que el "Off" de CheckToggle (ver docs/localizacion.md).
            if (m?.Unit == "m" && value <= 0.001f) return L10n.T("common.off");
            string num = value.ToString(fmt, CultureInfo.InvariantCulture);
            if (unit == "") return num;
            return (num + " " + unit).Trim();
        }
    }
}
