using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace Simulador.Tablet
{
    /// <summary>
    /// Slider que le cede el gesto vertical al ScrollRect padre en vez de
    /// consumirlo. El <see cref="Slider"/> de uGUI solo implementa IDragHandler
    /// (no IBeginDragHandler/IEndDragHandler): si el gesto arranca sobre el
    /// track/handle de un slider dentro de una columna scrolleable
    /// (TabletUiKit.ScrollColumn), el ScrollRect ancestro nunca se entera,
    /// aunque el dedo se mueva mayormente en vertical (la intencion real del
    /// operador es scrollear la columna, no cambiar el valor). Esta subclase
    /// agrega esas dos interfaces: al arrancar el drag mira la direccion
    /// dominante de <c>eventData.delta</c> y, si es vertical, reenvia
    /// begin/drag/end al ScrollRect cacheado (<see cref="GetComponentInParent{T}()"/>,
    /// sin reflection) via <see cref="ExecuteEvents.ExecuteHierarchy{T}"/> en vez
    /// de mover el valor. Si la direccion dominante es horizontal, se comporta
    /// como un Slider normal.
    /// Ademas NO salta al tocar: un tap/roce no cambia el valor y el drag es
    /// RELATIVO (el valor parte del actual y se mueve con el desplazamiento
    /// del dedo), nunca al punto tocado.
    /// </summary>
    public class ScrollFriendlySlider : Slider, IBeginDragHandler, IEndDragHandler
    {
        private ScrollRect _scrollRect;
        private bool _forwardToScroll;
        private float _dragNorm;

        protected override void Awake()
        {
            base.Awake();
            _scrollRect = GetComponentInParent<ScrollRect>();
        }

        public override void OnInitializePotentialDrag(PointerEventData eventData)
        {
            // NO llamamos a base: Slider fuerza useDragThreshold = false para
            // responder al instante, pero eso dispara OnBeginDrag en el primer
            // pixel de movimiento (delta casi nulo, sin senal de direccion
            // confiable). Dejamos el default (true) para que el
            // pixelDragThreshold del EventSystem (ver TabletController.BuildUI)
            // acumule movimiento antes de disparar OnBeginDrag con un delta ya
            // representativo de la direccion del gesto.
        }

        public override void OnPointerDown(PointerEventData eventData)
        {
            // NO llamamos a base: Slider.OnPointerDown hace UpdateDrag y salta el
            // valor al punto tocado (con notify, se mandaria al visor). Replicamos
            // solo lo de Selectable: seleccion + estado visual "pressed".
            if (!IsActive() || !IsInteractable() || eventData.button != PointerEventData.InputButton.Left) return;
            if (navigation.mode != Navigation.Mode.None && EventSystem.current != null)
                EventSystem.current.SetSelectedGameObject(gameObject, eventData);
            DoStateTransition(SelectionState.Pressed, false);
        }

        public void OnBeginDrag(PointerEventData eventData)
        {
            _dragNorm = normalizedValue;
            _forwardToScroll = _scrollRect != null &&
                Mathf.Abs(eventData.delta.y) > Mathf.Abs(eventData.delta.x);
            if (_forwardToScroll)
                ExecuteEvents.ExecuteHierarchy(_scrollRect.gameObject, eventData, ExecuteEvents.beginDragHandler);
        }

        public override void OnDrag(PointerEventData eventData)
        {
            if (_forwardToScroll)
                ExecuteEvents.ExecuteHierarchy(_scrollRect.gameObject, eventData, ExecuteEvents.dragHandler);
            else
                DragRelative(eventData);
        }

        /// <summary>Mueve el valor por el desplazamiento del dedo sobre el track (sin salto).</summary>
        private void DragRelative(PointerEventData eventData)
        {
            if (!IsActive() || !IsInteractable() || eventData.button != PointerEventData.InputButton.Left) return;
            var container = (handleRect != null ? handleRect.parent : null) as RectTransform;
            if (container == null) container = transform as RectTransform;
            var cam = eventData.pressEventCamera;
            if (!RectTransformUtility.ScreenPointToLocalPointInRectangle(container, eventData.position, cam, out var cur) ||
                !RectTransformUtility.ScreenPointToLocalPointInRectangle(container, eventData.position - eventData.delta, cam, out var prev))
                return;
            bool horizontal = direction == Direction.LeftToRight || direction == Direction.RightToLeft;
            bool reverse = direction == Direction.RightToLeft || direction == Direction.TopToBottom;
            float size = horizontal ? container.rect.width : container.rect.height;
            if (size <= 0f) return;
            float d = (horizontal ? cur.x - prev.x : cur.y - prev.y) / size;
            _dragNorm = Mathf.Clamp01(_dragNorm + (reverse ? -d : d));
            normalizedValue = _dragNorm; // notifica solo si el valor cambia (redondeo wholeNumbers incluido)
        }

        public void OnEndDrag(PointerEventData eventData)
        {
            if (!_forwardToScroll) return;
            ExecuteEvents.ExecuteHierarchy(_scrollRect.gameObject, eventData, ExecuteEvents.endDragHandler);
            _forwardToScroll = false;
        }
    }
}
