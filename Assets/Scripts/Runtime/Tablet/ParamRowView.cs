using System;
using UnityEngine;
using UnityEngine.UI;
using TMPro;

namespace Simulador.Tablet
{
    /// <summary>
    /// Fila de ajuste en vivo de un parametro de lente: nombre + valor formateado,
    /// slider touch-friendly y hint clinico debajo. Port de
    /// features/tablet/ui/param_row.gd. set_value_silent actualiza sin emitir
    /// (para sincronizar con el vision_state que confirma el visor sin re-enviar).
    /// </summary>
    public class ParamRowView : MonoBehaviour
    {
        public string ParamName;
        private Slider _slider;
        private TMP_Text _value;
        private bool _integer;
        private bool _silent;

        public event Action<string, float> Changed;

        public static ParamRowView Create(TabletUiKit kit, Transform parent, string paramName,
            float min, float max, float value)
        {
            var col = kit.Box(parent, "ParamRow", true, 2, null, expandW: true);
            var view = col.gameObject.AddComponent<ParamRowView>();
            view.ParamName = paramName;
            view._integer = ParamMeta.IsInteger(paramName);

            var header = kit.Box(col, "Header", false, 8, null, expandW: true);
            header.GetComponent<HorizontalLayoutGroup>().childAlignment = TextAnchor.MiddleLeft;
            var name = kit.Label(header, ParamMeta.LabelFor(paramName), LabelKind.Body, TextAlignmentOptions.Left);
            kit.Size(name.rectTransform, flexW: 1);
            view._value = kit.Label(header, ParamMeta.FormatValue(paramName, value), LabelKind.Value, TextAlignmentOptions.Right);

            view._slider = kit.Slider(col);
            view._slider.minValue = min;
            view._slider.maxValue = max;
            view._slider.wholeNumbers = view._integer;
            view._slider.SetValueWithoutNotify(value);
            view._slider.onValueChanged.AddListener(view.OnSlider);

            string hint = ParamMeta.HintFor(paramName);
            if (!string.IsNullOrEmpty(hint))
                kit.Label(col, hint, LabelKind.Hint, TextAlignmentOptions.TopLeft);

            return view;
        }

        private void OnSlider(float v)
        {
            _value.text = ParamMeta.FormatValue(ParamName, v);
            if (!_silent) Changed?.Invoke(ParamName, v);
        }

        public void SetValueSilent(float v)
        {
            _silent = true;
            _slider.SetValueWithoutNotify(v);
            _value.text = ParamMeta.FormatValue(ParamName, v);
            _silent = false;
        }
    }
}
