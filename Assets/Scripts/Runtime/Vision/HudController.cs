using System.Globalization;
using Simulador.Data;
using Simulador.Localization;
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
            string sc = scenarios != null ? ScenarioLabel(scenarios.Current) : "?";
            string ha = glare != null ? L10n.T(glare.halosEnabled ? "hud.halo_on" : "hud.halo_off") : "?";
            // Convencion clinica: OD primero, OI despues (solo orden de presentacion;
            // el mapeo de botones no cambia: A cicla ojo izquierdo, B ojo derecho).
            // D3: textos via L10n (claves hud.*, ver docs/localizacion.md). El FPS se
            // formatea con InvariantCulture ANTES de entrar al placeholder para que la
            // clave sea un simple "{0}" en ambos idiomas (mismo criterio que
            // ParamMeta.FormatValue).
            text.text = $"{L10n.T("hud.fps", _fps.ToString("0", CultureInfo.InvariantCulture))}\n" +
                        $"{L10n.T("hud.scenario", sc)}\n{L10n.T("hud.eye_od", r)}\n{L10n.T("hud.eye_os", l)}\n" +
                        $"{L10n.T("hud.halos", ha)}\n{L10n.T("hud.change_scenario")}{PairingLine()}";
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
            if (net.AuthenticatedClientCount > 0) return "\n" + L10n.T("hud.tablet_connected");
            return string.IsNullOrEmpty(net.PairingPin) ? "" : "\n" + L10n.T("hud.pairing_pin", net.PairingPin);
        }

        private static string Safe(string s) => string.IsNullOrEmpty(s) ? "-" : s;

        /// <summary>
        /// Nombre mostrable del escenario activo: si existe la clave "scenario.&lt;id&gt;"
        /// (las MISMAS que usa la tablet, ver docs/localizacion.md) gana la traduccion;
        /// si no (escenario nuevo sin entrada todavia en la tabla), cae al id crudo como
        /// antes -- sin esto un id no contemplado mostraria la propia clave en pantalla.
        /// Id vacio sigue mostrandose como "-" (via Safe).
        /// </summary>
        private static string ScenarioLabel(string id)
        {
            string key = "scenario." + id;
            return L10n.Has(key) ? L10n.T(key) : Safe(id);
        }

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
