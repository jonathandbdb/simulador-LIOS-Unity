using System.Collections;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace Simulador.Tablet
{
    /// <summary>
    /// Evita que el teclado nativo de Android tape un TMP_InputField dentro de una
    /// columna scrolleable (TabletUiKit.ScrollColumn). Mismo diagnostico que el
    /// popup del PIN (ver docs/tablet.md "Popup del PIN en el tercio superior"):
    /// TouchScreenKeyboard.area no es confiable en Android para medir el alto real
    /// del teclado, asi que en vez de medirlo se asume que ocupa la mitad inferior
    /// de la pantalla. Al enfocar el campo (OnSelect), agrega/activa un espaciador
    /// ("KeyboardSpacer") del alto asumido del teclado como ULTIMO hijo del Content
    /// del ScrollRect ancestro -- eso le da al scroll margen suficiente para llevar
    /// el campo al tercio superior de la pantalla, lejos del teclado. Al desenfocar
    /// (OnDeselect) colapsa el espaciador, salvo que el siguiente campo enfocado
    /// pertenezca al MISMO scroll (salto directo entre inputs, sin parpadeo).
    ///
    /// Componente HERMANO del TMP_InputField (no lo reemplaza): TabletUiKit.LineEdit()
    /// lo agrega automaticamente a TODO LineEdit, sin parametros ni opt-out. Si no
    /// hay un ScrollRect ancestro (p.ej. el LineEdit numerico del PIN, centrado sin
    /// scroll) el componente queda inerte.
    /// </summary>
    public class KeyboardAvoider : MonoBehaviour, ISelectHandler, IDeselectHandler
    {
        private const string SpacerName = "KeyboardSpacer";

        // Fraccion del alto del canvas raiz que se asume ocupa el teclado (mitad
        // inferior) -- mismo supuesto que el popup del PIN, no una medicion real.
        private const float SpacerHeightFraction = 0.5f;

        // Posicion vertical objetivo del CENTRO del campo, como fraccion desde
        // arriba del canvas (tercio superior, igual criterio que el popup del PIN).
        private const float TargetTopFraction = 0.3f;

        private ScrollRect _scrollRect;
        private RectTransform _rootCanvasRect;
        private Coroutine _deselectRoutine;

        private void Awake()
        {
            // null si no hay ScrollRect ancestro -- caso PIN, el componente queda
            // inerte (todos los metodos de abajo chequean esto primero).
            _scrollRect = GetComponentInParent<ScrollRect>();
            var canvas = GetComponentInParent<Canvas>();
            _rootCanvasRect = canvas != null ? canvas.rootCanvas.GetComponent<RectTransform>() : null;
        }

        public void OnSelect(BaseEventData eventData)
        {
            if (_scrollRect == null || !TouchScreenKeyboard.isSupported) return;
            if (_deselectRoutine != null) { StopCoroutine(_deselectRoutine); _deselectRoutine = null; }

            var content = _scrollRect.content;
            var spacer = GetOrCreateSpacer(content);
            float spacerH = _rootCanvasRect != null ? _rootCanvasRect.rect.height * SpacerHeightFraction : 400f;
            var le = spacer.GetComponent<LayoutElement>();
            le.minHeight = le.preferredHeight = spacerH;
            spacer.SetActive(true);
            spacer.transform.SetAsLastSibling();
            LayoutRebuilder.ForceRebuildLayoutImmediate(content);

            ScrollFieldIntoView(content);
        }

        public void OnDeselect(BaseEventData eventData)
        {
            if (_scrollRect == null || !TouchScreenKeyboard.isSupported) return;
            _deselectRoutine = StartCoroutine(CollapseNextFrameUnlessSameScroll());
        }

        private void OnDisable()
        {
            // Sin corutina: OnDisable puede correr sobre un objeto ya inactivo (una
            // card colapsada con el campo todavia enfocado) y los eventos de UI no
            // corren ahi -- colapsar ahora mismo, no esperar un frame que no llega.
            if (_scrollRect == null) return;
            CollapseSpacer(_scrollRect.content);
        }

        private IEnumerator CollapseNextFrameUnlessSameScroll()
        {
            yield return null;
            _deselectRoutine = null;
            var current = EventSystem.current != null ? EventSystem.current.currentSelectedGameObject : null;
            var nextAvoider = current != null ? current.GetComponent<KeyboardAvoider>() : null;
            // Salto directo a otro input del MISMO scroll: dejar el espaciador tal
            // cual (evita el parpadeo de colapsar y volver a expandir de inmediato).
            if (nextAvoider != null && nextAvoider._scrollRect == _scrollRect) yield break;
            CollapseSpacer(_scrollRect.content);
        }

        private static GameObject GetOrCreateSpacer(RectTransform content)
        {
            var existing = content.Find(SpacerName);
            if (existing != null) return existing.gameObject;
            var go = new GameObject(SpacerName, typeof(RectTransform), typeof(LayoutElement));
            go.transform.SetParent(content, false);
            return go;
        }

        private static void CollapseSpacer(RectTransform content)
        {
            var existing = content.Find(SpacerName);
            if (existing != null) existing.gameObject.SetActive(false);
        }

        // Centro del campo al ~30% desde arriba del canvas raiz: el Content del
        // ScrollColumn tiene pivot (0.5,1) y no hay escalas intermedias entre el
        // canvas raiz y el Content (mismo Canvas, sin Transform.localScale
        // adicional), asi que un delta medido en el espacio local del canvas raiz
        // se traduce 1:1 a anchoredPosition.y del Content.
        private void ScrollFieldIntoView(RectTransform content)
        {
            if (_rootCanvasRect == null) return;
            var fieldRect = transform as RectTransform;
            if (fieldRect == null) return;

            var corners = new Vector3[4];
            fieldRect.GetWorldCorners(corners);
            float fieldCenterY = 0.5f * (
                _rootCanvasRect.InverseTransformPoint(corners[0]).y +
                _rootCanvasRect.InverseTransformPoint(corners[1]).y);

            float canvasH = _rootCanvasRect.rect.height;
            float targetY = _rootCanvasRect.rect.yMax - canvasH * TargetTopFraction;

            // Cuanto hay que scrollear (Content.anchoredPosition.y, top-pivot: crece
            // al scrollear hacia abajo) para que el centro del campo pase de su
            // posicion actual a targetY. Si el campo ya esta mas arriba que el
            // target (delta <= 0), no scrollear -- ya esta visible.
            float delta = targetY - fieldCenterY;
            if (delta <= 0f) return;

            var viewport = _scrollRect.viewport != null ? _scrollRect.viewport : content.parent as RectTransform;
            float viewportH = viewport != null ? viewport.rect.height : canvasH;
            float maxScroll = Mathf.Max(0f, content.rect.height - viewportH);
            float newY = Mathf.Clamp(content.anchoredPosition.y + delta, 0f, maxScroll);

            content.anchoredPosition = new Vector2(content.anchoredPosition.x, newY);
            _scrollRect.velocity = Vector2.zero;
        }
    }
}
