using Simulador.Data;
using Simulador.Net;
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
            string l = dm != null ? LensLabel(dm, dm.Left.LensId) : "?";
            string r = dm != null ? LensLabel(dm, dm.Right.LensId) : "?";
            string sc = scenarios != null ? Safe(scenarios.Current) : "?";
            string ha = glare != null ? (glare.halosEnabled ? "ON" : "off") : "?";
            // Convencion clinica: OD primero, OI despues (solo orden de presentacion;
            // el mapeo de botones no cambia: A cicla ojo izquierdo, B ojo derecho).
            text.text = $"FPS {_fps:0}\nEscena: {sc}\nOD (B): {r}\nOI (A): {l}\nHalos (X): {ha}\nY: cambiar escena{PairingLine()}";
        }

        /// <summary>
        /// Linea de emparejamiento. Sin tablet autenticada muestra el PIN de la sesion
        /// para que el clinico lo lea y lo tipee en la tablet; con al menos una tablet
        /// autenticada lo reemplaza por un aviso discreto (deja de exponer el PIN).
        /// NetworkController.Instance puede no existir (escenas/momentos sin red) -> sin
        /// linea. AuthenticatedClientCount es null-safe (0 si el server no arranco).
        /// </summary>
        private static string PairingLine()
        {
            var net = NetworkController.Instance;
            if (net == null) return "";
            if (net.AuthenticatedClientCount > 0) return "\nTablet conectada";
            return string.IsNullOrEmpty(net.PairingPin) ? "" : $"\nPIN tablet: {net.PairingPin}";
        }

        private static string Safe(string s) => string.IsNullOrEmpty(s) ? "-" : s;

        /// <summary>
        /// Etiqueta legible de la lente para el HUD: el nombre que le puso el admin
        /// (LensDef.Nombre, clave "nombre" de lentes.json) resuelto por id via
        /// DataManager.GetLens. Fallback al id crudo si la lente no esta en el catalogo
        /// (borrada del catalogo pero todavia aplicada al ojo: caso borde real, Refresh
        /// corre cada ~0.4 s) o si el nombre viniera vacio. Sin id -> "-" (como Safe).
        /// </summary>
        private static string LensLabel(DataManager dm, string lensId)
        {
            if (string.IsNullOrEmpty(lensId)) return "-";
            string nombre = dm.GetLens(lensId)?.Nombre;
            return string.IsNullOrEmpty(nombre) ? lensId : nombre;
        }
    }
}
