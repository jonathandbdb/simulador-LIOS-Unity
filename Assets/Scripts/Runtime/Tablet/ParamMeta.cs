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
                // Etapa B (v0.8.0) cambia la semantica: de cap 0..1 a MULTIPLICADOR del radio
                // fisico del circulo de desenfoque. 1 = optica real; >1 exagera para que un
                // desenfoque sub-pixel (invisible en el visor) se vea; 0 = nunca borroso.
                Hint = "Multiplicador del desenfoque fuera de foco. 1 = optica real; mayor exagera para hacer visible un desenfoque sub-pixel; 0 = nunca borroso.",
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
                Hint = "Diametro pupilar en mm (1 = miosis, 6 = midriasis mesopica/escotopica). Agranda el halo y agrega tinte azulado (efecto Purkinje). Subir en escena nocturna.",
                Unit = "mm", Fmt = "F1",
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
            // v0.7.0: tinte de catarata del cristalino NATIVO (no un artefacto de la
            // LIO) -- separado del resto de disfotopsias porque modela el cristalino
            // sin operar, no la lente implantada. Alimenta el shader/binder de
            // @vision-optics (_CataractL/R). Ver docs/catalogo-lentes.md.
            ["cataract_yellow"] = new Entry
            {
                Label = "Catarata (tinte amarillo)",
                Hint = "Amarilleo del cristalino catarático: filtra la luz azul y lava los colores. 0 = medio transparente, 1 = catarata brunescente avanzada.",
                Unit = "", Fmt = "F2",
            },
            // v0.8.0: dispersion intraocular del cristalino cataratoso -- separado del tinte
            // porque es un mecanismo optico distinto (van den Berg / C-Quant straylight): baja
            // la nitidez a TODA distancia (no solo fuera de foco) y agrega un velo difuso sin
            // necesidad de una fuente de luz en el campo. Ver docs/catalogo-lentes.md.
            ["cataract_scatter"] = new Entry
            {
                Label = "Catarata (dispersion)",
                Hint = "Dispersion intraocular del cristalino cataratoso: baja la nitidez a toda distancia y agrega un velo difuso sin necesidad de una luz en el campo. 0 = medio claro, 0.6 = nuclear moderada (~20/70), 1 = avanzada (~20/200).",
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
