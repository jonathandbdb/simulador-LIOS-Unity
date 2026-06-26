using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;
using TMPro;

namespace Simulador.Tablet
{
    public enum LabelKind { Title, Subtitle, Section, Hint, Value, Body, ChipOD, ChipOI, StreamChip }
    public enum BtnStyle { Accent, Segment, Card, CardActive, Ghost, Neutral }

    /// <summary>
    /// Fabrica de widgets uGUI temables que replican los StyleBox del tema de la
    /// tablet Godot (features/tablet/theme/theme_builder.gd): cards y botones con
    /// esquinas redondeadas (sprite 9-slice generado por codigo), tipografia Inter
    /// via TextMeshPro y colores por estado. Cada widget registra un "repaint" para
    /// poder cambiar de tema (oscuro/claro) en caliente sin reconstruir la jerarquia.
    /// </summary>
    public class TabletUiKit
    {
        public TabletPalette P;
        public readonly TMP_FontAsset FontRegular;
        public readonly TMP_FontAsset FontSemibold;
        private readonly List<Action<TabletPalette>> _repaint = new();
        private static readonly Color Clear = new Color(0, 0, 0, 0);

        public TabletUiKit(TabletPalette palette, TMP_FontAsset regular, TMP_FontAsset semibold)
        {
            P = palette; FontRegular = regular; FontSemibold = semibold;
        }

        public void Register(Action<TabletPalette> r) { _repaint.Add(r); r(P); }
        public void Apply(TabletPalette palette) { P = palette; foreach (var r in _repaint) r(palette); }

        // ============================================================
        // Sprite redondeado (cache por radio)
        // ============================================================
        private static readonly Dictionary<int, Sprite> _sprites = new();
        public static Sprite Rounded(int radius)
        {
            if (radius <= 0) return null;
            if (_sprites.TryGetValue(radius, out var s)) return s;
            int r = radius, size = r * 2 + 4;
            var tex = new Texture2D(size, size, TextureFormat.RGBA32, false)
            { filterMode = FilterMode.Bilinear, wrapMode = TextureWrapMode.Clamp };
            var px = new Color32[size * size];
            for (int y = 0; y < size; y++)
                for (int x = 0; x < size; x++)
                {
                    float cx = x < r ? r : (x > size - 1 - r ? size - 1 - r : x);
                    float cy = y < r ? r : (y > size - 1 - r ? size - 1 - r : y);
                    float dx = x - cx, dy = y - cy;
                    float d = Mathf.Sqrt(dx * dx + dy * dy);
                    byte a = (byte)Mathf.RoundToInt(Mathf.Clamp01(r - d + 0.5f) * 255f);
                    px[y * size + x] = new Color32(255, 255, 255, a);
                }
            tex.SetPixels32(px); tex.Apply();
            s = Sprite.Create(tex, new Rect(0, 0, size, size), new Vector2(.5f, .5f), 100f, 0,
                SpriteMeshType.FullRect, new Vector4(r, r, r, r));
            _sprites[radius] = s;
            return s;
        }

        // ============================================================
        // Helpers basicos
        // ============================================================
        private static RectTransform RT(GameObject go)
        {
            var rt = go.GetComponent<RectTransform>();
            return rt != null ? rt : go.AddComponent<RectTransform>();
        }

        public RectTransform Box(Transform parent, string name, bool vertical, float spacing = 0,
            RectOffset padding = null, bool expandW = true, bool expandH = false,
            TextAnchor align = TextAnchor.UpperLeft)
        {
            var go = new GameObject(name, typeof(RectTransform));
            go.transform.SetParent(parent, false);
            HorizontalOrVerticalLayoutGroup lg = vertical
                ? go.AddComponent<VerticalLayoutGroup>()
                : (HorizontalOrVerticalLayoutGroup)go.AddComponent<HorizontalLayoutGroup>();
            lg.spacing = spacing;
            lg.padding = padding ?? new RectOffset(0, 0, 0, 0);
            lg.childControlWidth = true; lg.childControlHeight = true;
            lg.childForceExpandWidth = expandW; lg.childForceExpandHeight = expandH;
            lg.childAlignment = align;
            return RT(go);
        }

        public RectTransform Spacer(Transform parent, float size, bool flexible)
        {
            var go = new GameObject("Spacer", typeof(RectTransform));
            go.transform.SetParent(parent, false);
            var le = go.AddComponent<LayoutElement>();
            // Flexible = solo en ancho (separador horizontal del header). Si tambien
            // flexibilizara el alto, el HorizontalLayoutGroup padre heredaria
            // flexibleHeight>0 y el header se estiraria verticalmente.
            if (flexible) { le.flexibleWidth = 1; le.flexibleHeight = 0; }
            else { le.minHeight = size; le.preferredHeight = size; le.minWidth = size; }
            return RT(go);
        }

        public void Size(RectTransform rt, float minW = -1, float minH = -1, float prefW = -1, float prefH = -1,
            float flexW = -1, float flexH = -1)
        {
            var le = rt.GetComponent<LayoutElement>() ?? rt.gameObject.AddComponent<LayoutElement>();
            if (minW >= 0) le.minWidth = minW;
            if (minH >= 0) le.minHeight = minH;
            if (prefW >= 0) le.preferredWidth = prefW;
            if (prefH >= 0) le.preferredHeight = prefH;
            if (flexW >= 0) le.flexibleWidth = flexW;
            if (flexH >= 0) le.flexibleHeight = flexH;
        }

        public void Tint(Graphic g, Func<TabletPalette, Color> sel) => Register(p => g.color = sel(p));

        // ============================================================
        // Labels (TextMeshPro + Inter)
        // ============================================================
        public TMP_Text Label(Transform parent, string text, LabelKind kind = LabelKind.Body,
            TextAlignmentOptions align = TextAlignmentOptions.Left)
        {
            var go = new GameObject("Label", typeof(RectTransform));
            go.transform.SetParent(parent, false);
            var t = go.AddComponent<TextMeshProUGUI>();
            t.text = text;
            t.alignment = align;
            t.enableWordWrapping = (kind == LabelKind.Hint || kind == LabelKind.Subtitle);
            t.richText = false;
            bool semibold;
            float size;
            switch (kind)
            {
                case LabelKind.Title: semibold = true; size = 22; break;
                case LabelKind.Subtitle: semibold = false; size = 15; break;
                case LabelKind.Section: semibold = true; size = 17; break;
                case LabelKind.Hint: semibold = false; size = 13; break;
                case LabelKind.Value: semibold = true; size = 15; break;
                case LabelKind.ChipOD: semibold = true; size = 13; break;
                case LabelKind.ChipOI: semibold = true; size = 13; break;
                case LabelKind.StreamChip: semibold = true; size = 14; break;
                default: semibold = false; size = 16; break;
            }
            t.font = semibold ? FontSemibold : FontRegular;
            t.fontSize = size;
            Register(p => t.color = LabelColor(kind, p));
            return t;
        }

        private static Color LabelColor(LabelKind k, TabletPalette p)
        {
            switch (k)
            {
                case LabelKind.Title: return p.TextPrimary;
                case LabelKind.Subtitle: return p.TextSecondary;
                case LabelKind.Section: return p.TextPrimary;
                case LabelKind.Hint: return p.TextHint;
                case LabelKind.Value: return p.Accent;
                case LabelKind.ChipOD: return p.ChipOd;
                case LabelKind.ChipOI: return p.ChipOi;
                case LabelKind.StreamChip: { ColorUtility.TryParseHtmlString("#F2F6FB", out var c); return c; }
                default: return p.TextPrimary;
            }
        }

        // ============================================================
        // Paneles
        // ============================================================
        public RectTransform Panel(Transform parent, string name, Func<TabletPalette, Color> fill,
            int radius, bool vertical, float spacing, RectOffset padding)
        {
            var go = new GameObject(name, typeof(RectTransform));
            go.transform.SetParent(parent, false);
            var img = go.AddComponent<Image>();
            var sp = Rounded(radius);
            if (sp != null) { img.sprite = sp; img.type = Image.Type.Sliced; }
            Register(p => img.color = fill(p));
            HorizontalOrVerticalLayoutGroup lg = vertical
                ? go.AddComponent<VerticalLayoutGroup>()
                : (HorizontalOrVerticalLayoutGroup)go.AddComponent<HorizontalLayoutGroup>();
            lg.spacing = spacing;
            lg.padding = padding ?? new RectOffset(0, 0, 0, 0);
            lg.childControlWidth = true; lg.childControlHeight = true;
            lg.childForceExpandWidth = true; lg.childForceExpandHeight = false;
            lg.childAlignment = TextAnchor.UpperLeft;
            return RT(go);
        }

        public RectTransform Card(Transform parent, string name) =>
            Panel(parent, name, p => p.Surface, 12, true, 10, new RectOffset(16, 16, 16, 16));

        // ============================================================
        // Botones
        // ============================================================
        public TabletButton Button(Transform parent, string text, BtnStyle style, bool toggle = false,
            float minHeight = 48, float fontSize = 16)
        {
            var root = new GameObject("Btn", typeof(RectTransform));
            root.transform.SetParent(parent, false);
            var border = root.AddComponent<Image>();
            border.sprite = Rounded(8); border.type = Image.Type.Sliced;
            var btn = root.AddComponent<TabletButton>();
            btn.transition = Selectable.Transition.None;
            btn.Border = border;

            // Fill interior (1px inset para dejar ver el borde).
            var fillGo = new GameObject("Fill", typeof(RectTransform));
            fillGo.transform.SetParent(root.transform, false);
            var frt = RT(fillGo);
            frt.anchorMin = Vector2.zero; frt.anchorMax = Vector2.one;
            frt.offsetMin = new Vector2(1.5f, 1.5f); frt.offsetMax = new Vector2(-1.5f, -1.5f);
            var fill = fillGo.AddComponent<Image>();
            fill.sprite = Rounded(8); fill.type = Image.Type.Sliced;
            fill.raycastTarget = false;
            btn.Fill = fill;

            // Label centrado.
            var lbl = Label(root.transform, text, LabelKind.Body, TextAlignmentOptions.Center);
            lbl.fontSize = fontSize;
            lbl.raycastTarget = false;
            var lrt = lbl.rectTransform;
            lrt.anchorMin = Vector2.zero; lrt.anchorMax = Vector2.one;
            lrt.offsetMin = new Vector2(10, 4); lrt.offsetMax = new Vector2(-10, -4);
            btn.Label = lbl;

            btn.ToggleMode = toggle;
            btn.targetGraphic = border;
            // Ancho que hugea el texto (+ padding). Quien quiera expandir/uniformar
            // (segmentos, escenarios) sobreescribe con Size despues de crear.
            float textW = 0f;
            try { textW = lbl.GetPreferredValues(text).x; } catch { }
            float w = Mathf.Max(textW + 40f, 56f);
            Size(RT(root), minW: w, prefW: w, minH: minHeight, prefH: minHeight, flexW: 0);
            Register(p => StyleButton(btn, style, p));
            return btn;
        }

        public void StyleButton(TabletButton b, BtnStyle s, TabletPalette p)
        {
            switch (s)
            {
                case BtnStyle.Accent:
                    b.NormalFill = p.Accent; b.HoverFill = TabletPalette.Lightened(p.Accent, .06f); b.PressedFill = p.AccentPressed;
                    b.NormalBorder = b.HoverBorder = b.PressedBorder = Clear;
                    b.NormalText = b.HoverText = b.PressedText = p.AccentText; break;
                case BtnStyle.Segment:
                    b.NormalFill = p.SurfaceRaised; b.HoverFill = p.SurfaceHover; b.PressedFill = p.Accent;
                    b.NormalBorder = p.Border; b.HoverBorder = p.Border; b.PressedBorder = Clear;
                    b.NormalText = p.TextSecondary; b.HoverText = p.TextPrimary; b.PressedText = p.AccentText; break;
                case BtnStyle.Card:
                    b.NormalFill = p.SurfaceRaised; b.HoverFill = p.SurfaceHover; b.PressedFill = p.SurfaceHover;
                    b.NormalBorder = p.Border; b.HoverBorder = p.Border; b.PressedBorder = p.Accent;
                    b.NormalText = b.HoverText = b.PressedText = p.TextPrimary; break;
                case BtnStyle.CardActive:
                    b.NormalFill = TabletPalette.Mix(p.SurfaceRaised, p.Accent, .10f);
                    b.HoverFill = TabletPalette.Mix(p.SurfaceHover, p.Accent, .10f);
                    b.PressedFill = TabletPalette.Mix(p.SurfaceHover, p.Accent, .16f);
                    b.NormalBorder = b.HoverBorder = b.PressedBorder = p.Accent;
                    b.NormalText = b.HoverText = b.PressedText = p.TextPrimary; break;
                case BtnStyle.Ghost:
                    b.NormalFill = Clear; b.HoverFill = p.SurfaceRaised; b.PressedFill = p.SurfaceRaised;
                    b.NormalBorder = p.Border; b.HoverBorder = p.Border; b.PressedBorder = p.Accent;
                    b.NormalText = p.TextSecondary; b.HoverText = p.TextPrimary; b.PressedText = p.TextPrimary; break;
                default:
                    b.NormalFill = p.SurfaceRaised; b.HoverFill = p.SurfaceHover; b.PressedFill = p.SurfaceHover;
                    b.NormalBorder = p.Border; b.HoverBorder = p.Border; b.PressedBorder = p.Accent;
                    b.NormalText = b.HoverText = b.PressedText = p.TextPrimary; break;
            }
            b.Repaint();
        }

        // ============================================================
        // StatusBadge (pildora con punto de color + texto)
        // ============================================================
        public RectTransform StatusBadge(Transform parent, out Image dot, out TMP_Text text)
        {
            var root = Panel(parent, "StatusBadge", p => p.SurfaceRaised, 99, false, 8,
                new RectOffset(14, 14, 6, 6));
            var hlg = root.GetComponent<HorizontalLayoutGroup>();
            hlg.childForceExpandWidth = false; hlg.childForceExpandHeight = false;
            hlg.childAlignment = TextAnchor.MiddleLeft;
            Size(root, minH: 36, prefH: 36, flexW: 0, flexH: 0);

            var dotGo = new GameObject("Dot", typeof(RectTransform));
            dotGo.transform.SetParent(root, false);
            dot = dotGo.AddComponent<Image>();
            dot.sprite = Rounded(16); dot.type = Image.Type.Simple;
            Size(RT(dotGo), minW: 12, minH: 12, prefW: 12, prefH: 12, flexW: 0, flexH: 0);

            text = Label(root, "", LabelKind.Body, TextAlignmentOptions.Left);
            text.fontSize = 14;
            Size(text.rectTransform, flexW: 0, flexH: 0);
            return root;
        }

        // ============================================================
        // Slider touch-friendly
        // ============================================================
        public UnityEngine.UI.Slider Slider(Transform parent)
        {
            var root = new GameObject("Slider", typeof(RectTransform));
            root.transform.SetParent(parent, false);
            var slider = root.AddComponent<UnityEngine.UI.Slider>();
            Size(RT(root), minH: 40, prefH: 40);

            // Track de fondo.
            var bg = new GameObject("Track", typeof(RectTransform));
            bg.transform.SetParent(root.transform, false);
            var bgrt = RT(bg);
            bgrt.anchorMin = new Vector2(0, 0.5f); bgrt.anchorMax = new Vector2(1, 0.5f);
            bgrt.sizeDelta = new Vector2(0, 10); bgrt.anchoredPosition = Vector2.zero;
            var bgImg = bg.AddComponent<Image>();
            bgImg.sprite = Rounded(4); bgImg.type = Image.Type.Sliced;
            Tint(bgImg, p => p.SurfaceRaised);

            // Fill area + fill.
            var fillArea = new GameObject("Fill Area", typeof(RectTransform));
            fillArea.transform.SetParent(root.transform, false);
            var fart = RT(fillArea);
            fart.anchorMin = new Vector2(0, 0.5f); fart.anchorMax = new Vector2(1, 0.5f);
            fart.offsetMin = new Vector2(0, -5); fart.offsetMax = new Vector2(0, 5);
            var fillGo = new GameObject("Fill", typeof(RectTransform));
            fillGo.transform.SetParent(fillArea.transform, false);
            var fillrt = RT(fillGo);
            fillrt.anchorMin = Vector2.zero; fillrt.anchorMax = new Vector2(0, 1);
            fillrt.sizeDelta = new Vector2(10, 0);
            var fillImg = fillGo.AddComponent<Image>();
            fillImg.sprite = Rounded(4); fillImg.type = Image.Type.Sliced;
            Tint(fillImg, p => p.Accent);

            // Handle.
            var handleArea = new GameObject("Handle Slide Area", typeof(RectTransform));
            handleArea.transform.SetParent(root.transform, false);
            var hart = RT(handleArea);
            hart.anchorMin = new Vector2(0, 0); hart.anchorMax = new Vector2(1, 1);
            hart.offsetMin = new Vector2(11, 0); hart.offsetMax = new Vector2(-11, 0);
            var handleGo = new GameObject("Handle", typeof(RectTransform));
            handleGo.transform.SetParent(handleArea.transform, false);
            var hrt = RT(handleGo);
            hrt.sizeDelta = new Vector2(26, 26);
            var handleImg = handleGo.AddComponent<Image>();
            handleImg.sprite = Rounded(13); handleImg.type = Image.Type.Sliced;
            Tint(handleImg, p => p.Accent);

            slider.fillRect = fillrt;
            slider.handleRect = hrt;
            slider.targetGraphic = handleImg;
            slider.direction = UnityEngine.UI.Slider.Direction.LeftToRight;
            return slider;
        }

        // ============================================================
        // LineEdit (TMP_InputField)
        // ============================================================
        public TMP_InputField LineEdit(Transform parent, string placeholder)
        {
            var root = new GameObject("LineEdit", typeof(RectTransform));
            root.transform.SetParent(parent, false);
            var bg = root.AddComponent<Image>();
            bg.sprite = Rounded(8); bg.type = Image.Type.Sliced;
            Tint(bg, p => p.SurfaceRaised);
            var input = root.AddComponent<TMP_InputField>();
            Size(RT(root), minH: 48, prefH: 48, flexW: 1);

            var area = new GameObject("Text Area", typeof(RectTransform));
            area.transform.SetParent(root.transform, false);
            var art = RT(area);
            art.anchorMin = Vector2.zero; art.anchorMax = Vector2.one;
            art.offsetMin = new Vector2(14, 8); art.offsetMax = new Vector2(-14, -8);
            area.AddComponent<RectMask2D>();

            var ph = Label(area.transform, placeholder, LabelKind.Body, TextAlignmentOptions.Left);
            ph.rectTransform.anchorMin = Vector2.zero; ph.rectTransform.anchorMax = Vector2.one;
            ph.rectTransform.offsetMin = Vector2.zero; ph.rectTransform.offsetMax = Vector2.zero;
            Register(p => ph.color = p.TextHint);

            var txt = Label(area.transform, "", LabelKind.Body, TextAlignmentOptions.Left);
            txt.rectTransform.anchorMin = Vector2.zero; txt.rectTransform.anchorMax = Vector2.one;
            txt.rectTransform.offsetMin = Vector2.zero; txt.rectTransform.offsetMax = Vector2.zero;
            Register(p => txt.color = p.TextPrimary);

            input.textViewport = art;
            input.textComponent = txt;
            input.placeholder = ph;
            input.fontAsset = FontRegular;
            input.pointSize = 16;
            return input;
        }

        // ============================================================
        // CheckToggle (CheckButton de Godot): etiqueta + switch toggle
        // ============================================================
        public TabletButton CheckToggle(Transform parent, string labelText, out TabletButton sw)
        {
            var row = Box(parent, "CheckRow", false, 8, null, expandW: true);
            row.GetComponent<HorizontalLayoutGroup>().childAlignment = TextAnchor.MiddleLeft;
            Size(row, minH: 48);
            var lbl = Label(row, labelText, LabelKind.Body, TextAlignmentOptions.Left);
            Size(lbl.rectTransform, flexW: 1);
            sw = Button(row, "Off", BtnStyle.Segment, toggle: true, minHeight: 40, fontSize: 14);
            Size(RT(sw.gameObject), minW: 72, prefW: 72, flexW: 0);
            var swRef = sw;
            sw.OnToggled += on => swRef.Label.text = on ? "On" : "Off";
            return sw;
        }

        // ============================================================
        // RawImage (stream)
        // ============================================================
        public RawImage RawImage(Transform parent)
        {
            var go = new GameObject("Stream", typeof(RectTransform));
            go.transform.SetParent(parent, false);
            var ri = go.AddComponent<RawImage>();
            ri.color = Color.white;
            return ri;
        }

        // ============================================================
        // ScrollColumn (ScrollContainer + VBox de contenido)
        // ============================================================
        public ScrollRect ScrollColumn(Transform parent, out RectTransform content)
        {
            var root = new GameObject("Scroll", typeof(RectTransform));
            root.transform.SetParent(parent, false);
            var scroll = root.AddComponent<ScrollRect>();
            root.AddComponent<RectMask2D>();
            scroll.horizontal = false; scroll.vertical = true;
            scroll.movementType = ScrollRect.MovementType.Clamped;
            scroll.scrollSensitivity = 24;

            var viewport = new GameObject("Viewport", typeof(RectTransform));
            viewport.transform.SetParent(root.transform, false);
            var vrt = RT(viewport);
            vrt.anchorMin = Vector2.zero; vrt.anchorMax = Vector2.one;
            vrt.offsetMin = Vector2.zero; vrt.offsetMax = Vector2.zero;
            scroll.viewport = vrt;

            content = Box(viewport.transform, "Content", true, 12, new RectOffset(2, 2, 2, 2));
            content.anchorMin = new Vector2(0, 1); content.anchorMax = new Vector2(1, 1);
            content.pivot = new Vector2(0.5f, 1);
            // Ancho exacto del viewport (evita overflow horizontal por sizeDelta residual).
            content.sizeDelta = new Vector2(0f, content.sizeDelta.y);
            content.anchoredPosition = new Vector2(0f, 0f);
            var fitter = content.gameObject.AddComponent<ContentSizeFitter>();
            fitter.verticalFit = ContentSizeFitter.FitMode.PreferredSize;
            scroll.content = content;
            return scroll;
        }
    }
}
