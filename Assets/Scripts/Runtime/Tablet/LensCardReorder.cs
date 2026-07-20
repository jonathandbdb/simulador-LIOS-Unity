using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace Simulador.Tablet
{
    /// <summary>
    /// Drag-reorder con long-press para las cards de lente de CATALOGO (ver
    /// <see cref="LensCardView.Origen"/>), agregado por
    /// <c>TabletController.RebuildLensList</c> SOLO cuando el visor conectado es
    /// admin (<see cref="TabletSession.IsAdmin"/>, ver docs/tablet.md). Las
    /// lentes propias (<c>origen=="custom"</c>) nunca reciben este componente:
    /// quedan siempre despues en la lista y <see cref="DragReorder"/> nunca las
    /// toca (clamp a las primeras K cards, ver <see cref="CatalogIdsInOrder"/>).
    ///
    /// Arbitraje de gesto: mismo patron que <see cref="ScrollFriendlySlider"/>
    /// (esta card ya tiene un <see cref="TabletButton"/> con
    /// <c>IPointerClickHandler</c> -- el tap corto sigue aplicando la lente). El
    /// gesto SIEMPRE resuelve <c>pointerDrag</c> a esta card (implementamos
    /// IBeginDragHandler/IDragHandler/IEndDragHandler en el MISMO GameObject); en
    /// <see cref="OnBeginDrag"/> decidimos recien ahi si reenviarlo al ScrollRect
    /// ancestro (drag sin armar: scroll normal de la lista) o manejarlo nosotros
    /// (drag armado: reorden). El umbral de movimiento sigue siendo el
    /// <c>EventSystem.pixelDragThreshold</c> ya fijado en
    /// <c>TabletController.BuildUI</c> -- no se agrega ninguno nuevo.
    ///
    /// Long-press: <see cref="OnPointerDown"/> arranca un timer de
    /// <see cref="LongPressSeconds"/>; si el puntero no se movio lo suficiente
    /// para disparar OnBeginDrag en ese lapso, arma el modo reorden (highlight
    /// visual, ver <see cref="SetArmed"/>). Un OnBeginDrag POSTERIOR al armado
    /// mueve la card; uno anterior cede el gesto al scroll (y cancela el timer).
    ///
    /// Limitacion v1 aceptada (sin auto-scroll cerca de los bordes de la lista,
    /// ver docs/tablet.md): para listas de catalogo largas el admin reordena en
    /// mas de un drag.
    /// </summary>
    public class LensCardReorder : MonoBehaviour,
        IPointerDownHandler, IPointerUpHandler, IBeginDragHandler, IDragHandler, IEndDragHandler
    {
        // ~450ms: alcanza para distinguir "quiero reordenar" de un tap o el
        // arranque de un scroll, sin sentirse lento al clinico.
        private const float LongPressSeconds = 0.45f;

        private ScrollRect _scrollRect;
        private TabletButton _btn;
        private Coroutine _armTimer;
        private bool _armed;
        private bool _dragging;
        private List<string> _orderAtDragStart;
        private Action<List<string>> _onReordered;

        /// <summary>
        /// Agrega (si falta) el componente a una card de catalogo ya creada por
        /// <see cref="LensCardView.Create"/>. <paramref name="onReordered"/> se
        /// invoca al soltar SOLO si el orden visual de las cards de catalogo
        /// cambio respecto al que tenian al empezar el drag.
        /// </summary>
        public static void Attach(GameObject card, Action<List<string>> onReordered)
        {
            var c = card.GetComponent<LensCardReorder>() ?? card.AddComponent<LensCardReorder>();
            c._onReordered = onReordered;
        }

        private void Awake()
        {
            _scrollRect = GetComponentInParent<ScrollRect>();
            _btn = GetComponent<TabletButton>();
        }

        // RebuildLensList puede destruir esta card en medio de un gesto (llego un
        // hello mientras el admin arrastraba) -- no dejar el timer corriendo ni
        // estado colgado. Destroy() ya para las coroutines solo; esto es
        // defensivo (cubre tambien un SetActive(false) del padre, si lo hubiera).
        private void OnDisable()
        {
            CancelArmTimer();
            _armed = false;
            _dragging = false;
            // SIM: atajo deliberado — si esta card se destruye (RebuildLensList
            // llego un hello) justo entre un beginDrag reenviado al ScrollRect y
            // su endDrag, no se le manda un endDrag sintetico para "cerrarle" el
            // drag (haria falta cachear el ultimo PointerEventData reenviado solo
            // para este borde). Ventana angosta (requiere un hello exacto durante
            // el reenvio de OTRO drag) y de bajo impacto: como mucho el scroll
            // queda con velocidad residual hasta el proximo toque, sin NRE ni
            // estado propio colgado (ver docs/tablet.md §P8, "Robustez ante un
            // hello a mitad de gesto").
        }

        public void OnPointerDown(PointerEventData eventData)
        {
            CancelArmTimer();
            _armTimer = StartCoroutine(ArmAfterDelay());
        }

        public void OnPointerUp(PointerEventData eventData)
        {
            CancelArmTimer();
            if (_dragging) return; // OnEndDrag corre justo despues y hace el commit/la limpieza
            // Armado (long-press cumplido) pero soltado SIN llegar a mover:
            // decision de producto -- cancela el modo reorden (no aplica la
            // lente, no manda nada), a diferencia de un tap corto SIN armar (que
            // si debe seguir aplicando la lente -- ahi no tocamos eligibleForClick).
            if (_armed) eventData.eligibleForClick = false;
            SetArmed(false);
        }

        private void CancelArmTimer()
        {
            if (_armTimer == null) return;
            StopCoroutine(_armTimer);
            _armTimer = null;
        }

        private IEnumerator ArmAfterDelay()
        {
            yield return new WaitForSeconds(LongPressSeconds);
            _armTimer = null;
            SetArmed(true);
        }

        // Feedback visual reusando colores YA resueltos por TabletUiKit.StyleButton
        // para esta card (paleta del tema activo -- Card/CardActive comparten
        // PressedBorder == Accent, ver TabletUiKit.StyleButton): mezclamos el fill
        // "pressed" con ese acento para un resaltado claramente distinto del look
        // "pressed" nativo que TabletButton YA muestra mientras el dedo esta
        // abajo (sin esto, armar no se veria: el fill ya estaria en PressedFill
        // desde el instante del toque). Repaint() restaura el estado real
        // (normal/hover/activo) al desarmar -- no hace falta cachear el color
        // original. Suma un escalado leve (2%) como segunda senal, independiente
        // del tema.
        private void SetArmed(bool on)
        {
            _armed = on;
            transform.localScale = on ? Vector3.one * 1.02f : Vector3.one;
            if (_btn == null) return;
            if (on)
            {
                if (_btn.Fill != null)
                    _btn.Fill.color = TabletPalette.Mix(_btn.PressedFill, _btn.PressedBorder, 0.5f);
            }
            else
            {
                _btn.Repaint();
            }
        }

        public void OnBeginDrag(PointerEventData eventData)
        {
            if (!_armed)
            {
                // El dedo se movio antes de armar: cedemos el gesto al ScrollRect
                // ancestro (mismo patron que ScrollFriendlySlider) para no romper
                // el scroll normal de la lista de lentes.
                CancelArmTimer();
                if (_scrollRect != null)
                    ExecuteEvents.ExecuteHierarchy(_scrollRect.gameObject, eventData, ExecuteEvents.beginDragHandler);
                // Drag real (aunque no sea el nuestro): sin esto el click de
                // TabletButton se dispararia igual al soltar. En un scroll SIN
                // este componente, Unity ya evita el click solo (pointerDrag
                // resuelve al ScrollRect, distinto de pointerPress); aca
                // pointerDrag resuelve a ESTA MISMA card (implementamos
                // IDragHandler nosotros), asi que hay que replicarlo a mano.
                eventData.eligibleForClick = false;
                return;
            }
            _dragging = true;
            eventData.eligibleForClick = false;
            _orderAtDragStart = CatalogIdsInOrder();
        }

        public void OnDrag(PointerEventData eventData)
        {
            if (_armed && _dragging) { DragReorder(eventData); return; }
            if (_scrollRect != null)
                ExecuteEvents.ExecuteHierarchy(_scrollRect.gameObject, eventData, ExecuteEvents.dragHandler);
        }

        public void OnEndDrag(PointerEventData eventData)
        {
            if (_armed && _dragging)
            {
                var finalOrder = CatalogIdsInOrder();
                if (_orderAtDragStart != null && !finalOrder.SequenceEqual(_orderAtDragStart))
                    _onReordered?.Invoke(finalOrder);
            }
            else if (_scrollRect != null)
            {
                ExecuteEvents.ExecuteHierarchy(_scrollRect.gameObject, eventData, ExecuteEvents.endDragHandler);
            }
            _dragging = false;
            SetArmed(false);
        }

        // Un paso a la vez contra el hermano inmediato de cada lado, comparado en
        // espacio de PANTALLA (RectTransformUtility.WorldToScreenPoint, seguro
        // con Camera null -- Canvas ScreenSpaceOverlay de la tablet, ver
        // TabletController.BuildUI) para no depender del pivot/ancla de
        // _lensList: el VerticalLayoutGroup reacomoda el resto de la lista solo
        // en cuanto cambia el sibling index. Clampeado a [0, K-1]: las cards de
        // catalogo son siempre las primeras K de la lista (ver
        // CatalogIdsInOrder) -- las custom, despues, nunca se comparan ni se
        // tocan.
        private void DragReorder(PointerEventData eventData)
        {
            var parent = transform.parent;
            if (parent == null) return;
            int limit = CatalogIdsInOrder().Count;
            int index = transform.GetSiblingIndex();
            if (index < 0 || index >= limit) return; // defensivo: esta card deberia ser de catalogo

            float pointerY = eventData.position.y;
            var cam = eventData.pressEventCamera;

            while (index > 0)
            {
                var prevRt = parent.GetChild(index - 1) as RectTransform;
                if (prevRt == null || pointerY <= RectTransformUtility.WorldToScreenPoint(cam, prevRt.position).y)
                    break;
                transform.SetSiblingIndex(index - 1);
                index--;
            }
            while (index < limit - 1)
            {
                var nextRt = parent.GetChild(index + 1) as RectTransform;
                if (nextRt == null || pointerY >= RectTransformUtility.WorldToScreenPoint(cam, nextRt.position).y)
                    break;
                transform.SetSiblingIndex(index + 1);
                index++;
            }
        }

        // Ids de las cards de CATALOGO en orden visual actual (sibling index):
        // recorre desde el principio y para en la primera "custom" -- el
        // contrato del backend (merge blob base + custom del device, ver
        // docs/catalogo-lentes.md) siempre las entrega asi, catalogo primero.
        private List<string> CatalogIdsInOrder()
        {
            var parent = transform.parent;
            var ids = new List<string>();
            if (parent == null) return ids;
            for (int i = 0; i < parent.childCount; i++)
            {
                var lc = parent.GetChild(i).GetComponent<LensCardView>();
                if (lc == null || lc.Origen == "custom") break;
                ids.Add(lc.LensId);
            }
            return ids;
        }
    }
}
