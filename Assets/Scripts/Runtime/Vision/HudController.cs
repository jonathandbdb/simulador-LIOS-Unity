using Simulador.Data;
using UnityEngine;
using UnityEngine.UI;

namespace Simulador.Vision
{
    /// <summary>
    /// HUD de diagnostico world-space (estilo los Label3D del proyecto Godot): FPS,
    /// escenario activo, lente por ojo y estado de halos. Anclado a la camara para
    /// quedar fijo en la vista. UI legacy + fuente builtin (sin dependencia de TMP).
    /// </summary>
    public class HudController : MonoBehaviour
    {
        public ScenarioManager scenarios;
        public GlareController glare;
        public Text text;

        private float _t;
        private int _frames;
        private float _fps;

        private void Update()
        {
            _frames++;
            _t += Time.unscaledDeltaTime;
            if (_t >= 0.4f)
            {
                _fps = _frames / _t;
                _frames = 0;
                _t = 0f;
                Refresh();
            }
        }

        private void Refresh()
        {
            if (text == null) return;
            var dm = DataManager.Instance;
            string l = dm != null ? Safe(dm.Left.LensId) : "?";
            string r = dm != null ? Safe(dm.Right.LensId) : "?";
            string sc = scenarios != null ? Safe(scenarios.Current) : "?";
            string ha = glare != null ? (glare.halosEnabled ? "ON" : "off") : "?";
            text.text = $"FPS {_fps:0}\nEscena: {sc}\nOI (A): {l}\nOD (B): {r}\nHalos (X): {ha}\nY: cambiar escena";
        }

        private static string Safe(string s) => string.IsNullOrEmpty(s) ? "-" : s;
    }
}
