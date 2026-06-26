using System;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;
using TMPro;

namespace Simulador.Tablet
{
    /// <summary>
    /// Boton temable de la tablet: fill + borde redondeados con colores por
    /// estado (normal / hover / pressed), modo toggle (segment buttons, escenarios,
    /// colapsables) y callback de click. Reemplaza el ColorBlock de uGUI para poder
    /// usar colores arbitrarios por estado y cambiar el borde al presionar/activar
    /// (replica los StyleBox de theme_builder.gd).
    /// </summary>
    public class TabletButton : Selectable, IPointerClickHandler
    {
        public Image Fill;          // fondo (puede ser el mismo que el borde si no hay borde)
        public Image Border;        // borde (puede ser null)
        public TMP_Text Label;
        public bool ToggleMode;
        public bool IsOn;
        public Action OnClick;
        public Action<bool> OnToggled;

        public Color NormalFill, HoverFill, PressedFill;
        public Color NormalBorder, HoverBorder, PressedBorder;
        public Color NormalText, HoverText, PressedText;

        private bool _hover, _down;

        public void SetOn(bool on, bool notify)
        {
            IsOn = on;
            Repaint();
            if (notify) OnToggled?.Invoke(on);
        }

        protected override void DoStateTransition(SelectionState state, bool instant)
        {
            // No usamos el tween de color de Selectable; pintamos a mano.
            Repaint();
        }

        public void Repaint()
        {
            bool pressedLook = _down || (ToggleMode && IsOn);
            Color f, b, t;
            if (!IsInteractable()) { f = NormalFill; b = NormalBorder; t = NormalText; }
            else if (pressedLook) { f = PressedFill; b = PressedBorder; t = PressedText; }
            else if (_hover) { f = HoverFill; b = HoverBorder; t = HoverText; }
            else { f = NormalFill; b = NormalBorder; t = NormalText; }
            if (Fill != null) Fill.color = f;
            if (Border != null) Border.color = b;
            if (Label != null) Label.color = t;
        }

        public override void OnPointerEnter(PointerEventData e) { base.OnPointerEnter(e); _hover = true; Repaint(); }
        public override void OnPointerExit(PointerEventData e) { base.OnPointerExit(e); _hover = false; _down = false; Repaint(); }
        public override void OnPointerDown(PointerEventData e) { base.OnPointerDown(e); _down = true; Repaint(); }
        public override void OnPointerUp(PointerEventData e) { base.OnPointerUp(e); _down = false; Repaint(); }

        public void OnPointerClick(PointerEventData e)
        {
            if (!IsInteractable()) return;
            if (ToggleMode) SetOn(!IsOn, true);
            OnClick?.Invoke();
        }
    }
}
