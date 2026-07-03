using System.Collections.Generic;
using System.Globalization;

namespace Simulador.Tablet
{
    /// <summary>
    /// Metadata clinica de los parametros de lente que llegan en el catalogo.
    /// Port VERBATIM de features/tablet/ui/param_meta.gd. Las claves son las del
    /// catalogo/shader (no afecta al protocolo).
    /// </summary>
    public static class ParamMeta
    {
        public class Entry
        {
            public string Label;
            public string Hint;
            public string Unit; // "m", "rayos", ""
            public string Fmt;  // "F2" (%.2f) o "F0" (%.0f)
        }

        public static readonly Dictionary<string, Entry> META = new()
        {
            ["foco_lejos_m"] = new Entry
            {
                Label = "Foco lejano",
                Hint = "Distancia donde el paciente ve nitido a lejos. 6 m ≈ infinito optico. 0 = desactivado.",
                Unit = "m", Fmt = "F2",
            },
            ["foco_intermedio_m"] = new Entry
            {
                Label = "Foco intermedio",
                Hint = "Distancia del segundo plano nitido (PC, tablero del auto). 0 = sin foco intermedio.",
                Unit = "m", Fmt = "F2",
            },
            ["foco_cerca_m"] = new Entry
            {
                Label = "Foco cercano",
                Hint = "Distancia de lectura nitida (libro, celular). Tipico 35-45 cm. 0 = sin foco cercano.",
                Unit = "m", Fmt = "F2",
            },
            ["profundidad_foco_m"] = new Entry
            {
                Label = "Profundidad de foco",
                Hint = "Ancho de la zona nitida alrededor de cada foco. Bajo = pico estrecho (trifocal). Alto = plateau ancho (EDOF).",
                Unit = "m", Fmt = "F2",
            },
            ["desenfoque_max"] = new Entry
            {
                Label = "Desenfoque maximo",
                Hint = "Cuanto se borronea fuera de toda zona de foco (0 = nunca borroso, 1 = maximo).",
                Unit = "", Fmt = "F2",
            },
            ["halo_intensity"] = new Entry
            {
                Label = "Intensidad de halos",
                Hint = "Tamano e intensidad del halo difractivo alrededor de fuentes brillantes. Trifocal alto, monofocal casi nulo.",
                Unit = "", Fmt = "F2",
            },
            ["halo_extra_rings"] = new Entry
            {
                Label = "Dilatacion pupilar (noche)",
                Hint = "Pupila mesopica/escotopica. Agranda el halo y agrega tinte azulado (efecto Purkinje). Subir en escena nocturna.",
                Unit = "", Fmt = "F2",
            },
            ["contrast_loss"] = new Entry
            {
                Label = "Perdida de contraste",
                Hint = "Reduccion de sensibilidad al contraste (imagen mas lavada). Trifocal pierde mas que EDOF, EDOF mas que monofocal.",
                Unit = "", Fmt = "F2",
            },
            ["destello_intensity"] = new Entry
            {
                Label = "Intensidad de starburst",
                Hint = "Rayos radiales desde fuentes brillantes (disfotopsia difractiva). 0 = sin destello.",
                Unit = "", Fmt = "F2",
            },
            ["destello_rayos"] = new Entry
            {
                Label = "Cantidad de rayos",
                Hint = "Cantidad de spokes del starburst. Pacientes con trifocal reportan 8-12 rayos visibles.",
                Unit = "rayos", Fmt = "F0",
            },
            ["straylight"] = new Entry
            {
                Label = "Encandilamiento (straylight)",
                Hint = "Luz parasita intraocular: ante una fuente brillante (sol/faros) vela la imagen y baja el contraste (disability glare). Trifocal alto, EDOF medio, monofocal bajo.",
                Unit = "", Fmt = "F2",
            },
            // P4.4: astigmatismo residual PERSISTENTE por lente (vive en el catalogo,
            // distinto del ajuste LIVE de la card "Astigmatismo" -- ver
            // TabletController.BuildAstigCard/SendAstigmatism y el hint que agrega esa
            // card sobre la precedencia entre ambos).
            ["astig_magnitude"] = new Entry
            {
                Label = "Astigmatismo residual",
                Hint = "Astigmatismo NO corregido por la lente: borronea la imagen en un eje. 0 = sin astigmatismo residual.",
                Unit = "", Fmt = "F2",
            },
            ["astig_axis_deg"] = new Entry
            {
                Label = "Eje del astigmatismo",
                Hint = "Orientacion del eje de mayor borrosidad (0-180°). Solo relevante si hay astigmatismo residual (>0).",
                Unit = "°", Fmt = "F0",
            },
        };

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
        };

        public static string LabelFor(string p) => META.TryGetValue(p, out var m) ? m.Label : p;
        public static string HintFor(string p) => META.TryGetValue(p, out var m) ? m.Hint : "";
        public static bool IsInteger(string p) => META.TryGetValue(p, out var m) && m.Fmt == "F0";

        public static string FormatValue(string p, float value)
        {
            META.TryGetValue(p, out var m);
            string unit = m?.Unit ?? "";
            string fmt = m?.Fmt ?? "F2";
            // Distancias en metros: 0 = foco desactivado.
            if (unit == "m" && value <= 0.001f) return "off";
            string num = value.ToString(fmt, CultureInfo.InvariantCulture);
            if (unit == "") return num;
            return (num + " " + unit).Trim();
        }
    }
}
