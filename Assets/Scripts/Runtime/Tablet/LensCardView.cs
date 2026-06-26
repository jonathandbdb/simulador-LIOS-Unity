using System;
using UnityEngine;
using UnityEngine.UI;
using TMPro;

namespace Simulador.Tablet
{
    /// <summary>
    /// Card de lente intraocular: nombre humano, descripcion clinica y chips OD/OI
    /// que indican en que ojo(s) esta aplicada. Tap = aplicar la lente. Port de
    /// features/tablet/ui/lens_card.gd (el resaltado activo = StyleBox CardButtonActive).
    /// </summary>
    public class LensCardView : MonoBehaviour
    {
        public string LensId;
        private TabletUiKit _kit;
        private TabletButton _btn;
        private GameObject _chipOd, _chipOi;
        private bool _active;

        public static LensCardView Create(TabletUiKit kit, Transform parent, string id, string nombre,
            string descripcion, Action<string> onSelected)
        {
            var root = new GameObject("LensCard", typeof(RectTransform));
            root.transform.SetParent(parent, false);
            var view = root.AddComponent<LensCardView>();
            view._kit = kit;
            view.LensId = id;

            var bg = root.AddComponent<Image>();
            bg.sprite = TabletUiKit.Rounded(10); bg.type = Image.Type.Sliced;
            var btn = root.AddComponent<TabletButton>();
            btn.transition = Selectable.Transition.None;
            btn.Fill = bg; btn.targetGraphic = bg;
            btn.OnClick = () => onSelected?.Invoke(id);
            view._btn = btn;

            var vbox = root.AddComponent<VerticalLayoutGroup>();
            vbox.padding = new RectOffset(14, 14, 12, 12);
            vbox.spacing = 4;
            vbox.childControlWidth = true; vbox.childControlHeight = true;
            vbox.childForceExpandWidth = true; vbox.childForceExpandHeight = false;

            // Fila superior: nombre + chips OD/OI.
            var top = kit.Box(root.transform, "TopRow", false, 6, null, expandW: false);
            top.GetComponent<HorizontalLayoutGroup>().childAlignment = TextAnchor.MiddleLeft;
            var name = kit.Label(top, string.IsNullOrEmpty(nombre) ? id : nombre, LabelKind.Section, TextAlignmentOptions.Left);
            name.raycastTarget = false;
            kit.Size(name.rectTransform, flexW: 1);
            view._chipOd = Chip(kit, top, "OD", LabelKind.ChipOD);
            view._chipOi = Chip(kit, top, "OI", LabelKind.ChipOI);

            // Descripcion clinica (oculta si vacia).
            if (!string.IsNullOrEmpty(descripcion))
            {
                var desc = kit.Label(root.transform, descripcion, LabelKind.Hint, TextAlignmentOptions.TopLeft);
                desc.raycastTarget = false;
            }

            view._chipOd.SetActive(false);
            view._chipOi.SetActive(false);
            // Estilo segun estado (registrado despues del estilo base del boton para ganar en Apply).
            kit.Register(p => kit.StyleButton(btn, view._active ? BtnStyle.CardActive : BtnStyle.Card, p));
            return view;
        }

        private static GameObject Chip(TabletUiKit kit, Transform parent, string text, LabelKind kind)
        {
            Func<TabletPalette, Color> sel = kind == LabelKind.ChipOD
                ? (Func<TabletPalette, Color>)(p => { var c = p.ChipOd; c.a = 0.16f; return c; })
                : (p => { var c = p.ChipOi; c.a = 0.16f; return c; });
            // Pildora de tamano fijo (hugea "OD"/"OI"); evita que el layout la infle.
            var go = new GameObject("Chip", typeof(RectTransform));
            go.transform.SetParent(parent, false);
            var img = go.AddComponent<Image>();
            img.sprite = TabletUiKit.Rounded(99); img.type = Image.Type.Sliced;
            img.raycastTarget = false;
            kit.Tint(img, sel);
            kit.Size(go.GetComponent<RectTransform>(), minW: 40, prefW: 40, minH: 26, prefH: 26, flexW: 0, flexH: 0);
            var lbl = kit.Label(go.transform, text, kind, TextAlignmentOptions.Center);
            lbl.raycastTarget = false;
            var lrt = lbl.rectTransform;
            lrt.anchorMin = Vector2.zero; lrt.anchorMax = Vector2.one;
            lrt.offsetMin = Vector2.zero; lrt.offsetMax = Vector2.zero;
            return go;
        }

        /// <summary>Enciende los chips segun el estado de vision y resalta la card.</summary>
        public void SetEyeState(bool onOd, bool onOi)
        {
            _chipOd.SetActive(onOd);
            _chipOi.SetActive(onOi);
            _active = onOd || onOi;
            _kit.StyleButton(_btn, _active ? BtnStyle.CardActive : BtnStyle.Card, _kit.P);
        }
    }
}
