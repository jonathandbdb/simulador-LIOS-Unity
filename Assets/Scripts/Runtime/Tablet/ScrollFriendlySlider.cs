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
    /// </summary>
    public class ScrollFriendlySlider : Slider, IBeginDragHandler, IEndDragHandler
    {
        private ScrollRect _scrollRect;
        private bool _forwardToScroll;

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

        public void OnBeginDrag(PointerEventData eventData)
        {
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
                base.OnDrag(eventData);
        }

        public void OnEndDrag(PointerEventData eventData)
        {
            if (!_forwardToScroll) return;
            ExecuteEvents.ExecuteHierarchy(_scrollRect.gameObject, eventData, ExecuteEvents.endDragHandler);
            _forwardToScroll = false;
        }
    }
}
