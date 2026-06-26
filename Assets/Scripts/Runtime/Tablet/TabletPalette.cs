using UnityEngine;

namespace Simulador.Tablet
{
    /// <summary>
    /// Paleta de colores de la app tablet (modo oscuro / claro). Port VERBATIM de
    /// las constantes DARK/LIGHT de features/tablet/theme/theme_builder.gd. El
    /// controlador construye los widgets con TabletUiKit y, al togglear el tema,
    /// reaplica una paleta nueva sin reconstruir la jerarquia.
    /// </summary>
    public class TabletPalette
    {
        public bool IsDark;
        public Color Bg, Surface, SurfaceRaised, SurfaceHover, Border;
        public Color TextPrimary, TextSecondary, TextHint;
        public Color Accent, AccentPressed, AccentSoft, AccentText;
        public Color Ok, Warn, Error, ChipOd, ChipOi, StreamBg, Icon;

        private static Color H(string hex)
        {
            ColorUtility.TryParseHtmlString(hex, out var c);
            return c;
        }

        public static TabletPalette For(bool dark) => dark ? Dark() : Light();

        // Paleta oscura: consola medica (gris azulado + teal clinico).
        public static TabletPalette Dark() => new TabletPalette
        {
            IsDark = true,
            Bg = H("#11151C"), Surface = H("#1B212B"), SurfaceRaised = H("#242C38"),
            SurfaceHover = H("#2B3442"), Border = H("#313C4B"),
            TextPrimary = H("#E9EEF5"), TextSecondary = H("#9AA7B8"), TextHint = H("#6C7A8C"),
            Accent = H("#17A398"), AccentPressed = H("#0F7E76"), AccentSoft = H("#17A39826"),
            AccentText = H("#04201D"), Ok = H("#3FCF8E"), Warn = H("#F2B33D"), Error = H("#E5655E"),
            ChipOd = H("#5B9BD5"), ChipOi = H("#C58BD8"), StreamBg = H("#06080C"), Icon = H("#E9EEF5"),
        };

        // Paleta clara: historia clinica electronica (blanco/gris + azul clinico).
        public static TabletPalette Light() => new TabletPalette
        {
            IsDark = false,
            Bg = H("#F5F7FA"), Surface = H("#FFFFFF"), SurfaceRaised = H("#EDF1F6"),
            SurfaceHover = H("#E3E9F1"), Border = H("#D5DCE5"),
            TextPrimary = H("#1A2330"), TextSecondary = H("#5A6878"), TextHint = H("#8895A5"),
            Accent = H("#2563EB"), AccentPressed = H("#1D4FBF"), AccentSoft = H("#2563EB1F"),
            AccentText = H("#FFFFFF"), Ok = H("#16A34A"), Warn = H("#D97706"), Error = H("#DC2626"),
            ChipOd = H("#2E6FB7"), ChipOi = H("#9456B8"), StreamBg = H("#0B0E13"), Icon = H("#1A2330"),
        };

        public static Color Mix(Color a, Color b, float t) => Color.Lerp(a, b, t);
        public static Color Lightened(Color c, float amt) => Color.Lerp(c, Color.white, amt);
    }
}
