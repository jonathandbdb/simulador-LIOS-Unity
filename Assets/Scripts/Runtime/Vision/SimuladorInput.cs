using Simulador.Data;
using Simulador.License;
using UnityEngine;
using UnityEngine.InputSystem;

namespace Simulador.Vision
{
    /// <summary>
    /// Input de los mandos (port del input de main.gd):
    ///   - A (mano derecha): cicla la lente del OJO IZQUIERDO.
    ///   - B (mano derecha): cicla la lente del OJO DERECHO.
    ///   - X (mano izquierda): toggle halos.
    ///   - Y (mano izquierda): cambia de escenario.
    /// Acciones creadas en codigo y bindeadas a los perfiles OpenXR de Quest.
    /// Los CUATRO atajos exigen que el dispositivo sea ADMINISTRADOR (ver AdminGate):
    /// en una sesion clinica ni el paciente ni el medico deben poder cambiar lente,
    /// halos o escenario por accidente con el mando. El control por TABLET no pasa por
    /// aca (Net/NetworkController) y sigue funcionando siempre.
    /// </summary>
    public class SimuladorInput : MonoBehaviour
    {
        public GlareController glare;
        public ScenarioManager scenarios;

        private InputAction _a, _b, _x, _y;

        private void OnEnable()
        {
            _a = new InputAction("A", InputActionType.Button, "<XRController>{RightHand}/primaryButton");
            _b = new InputAction("B", InputActionType.Button, "<XRController>{RightHand}/secondaryButton");
            _x = new InputAction("X", InputActionType.Button, "<XRController>{LeftHand}/primaryButton");
            _y = new InputAction("Y", InputActionType.Button, "<XRController>{LeftHand}/secondaryButton");

            _a.performed += _ => { if (!AdminGate("A: lente OI")) return; CycleLens("left"); };
            _b.performed += _ => { if (!AdminGate("B: lente OD")) return; CycleLens("right"); };
            _x.performed += _ => { if (!AdminGate("X: halos")) return; if (glare) { glare.halosEnabled = !glare.halosEnabled; glare.Refresh(); Debug.Log($"halos={glare.halosEnabled}"); } };
            _y.performed += _ => { if (!AdminGate("Y: escenario")) return; if (scenarios) scenarios.CycleScenario(); };

            _a.Enable(); _b.Enable(); _x.Enable(); _y.Enable();
        }

        private void OnDisable()
        {
            _a?.Disable(); _b?.Disable(); _x?.Disable(); _y?.Disable();
        }

        /// <summary>
        /// Gate de los atajos del mando: solo un dispositivo marcado como administrador en
        /// el backend puede cambiar estado desde el joystick. La fuente es el flag
        /// "is_admin" del POST /api/verify, que <see cref="LicenseManager.IsAdmin"/> ya
        /// expone (ver docs/licenciamiento.md §P7) -- no se agrega ningun dato nuevo.
        /// Falla CERRADO a proposito: sin cache, sin red, con cache pre-P7 o contra un
        /// backend viejo, IsAdmin es false y los atajos quedan inhibidos.
        /// Se filtra ACA y no con `enabled` porque LicenseBlockScreenVR y UpdatePromptVR
        /// ya se disputan ese flag con guards anti-restore cruzados: una tercera mano
        /// romperia los carteles de licencia/update. Los botones propios de esos carteles
        /// son InputActions independientes y no pasan por este gate.
        /// </summary>
        private bool AdminGate(string action)
        {
            if (LicenseManager.IsAdmin) return true;
            Debug.Log($"[Vision] Atajo del mando ignorado ({action}): el dispositivo no es administrador.");
            return false;
        }

        private void CycleLens(string eye)
        {
            var dm = DataManager.Instance;
            if (dm == null) return;
            var ids = dm.GetLensIds();
            if (ids.Count == 0) return;
            string cur = eye == "left" ? dm.Left.LensId : dm.Right.LensId;
            int idx = ids.IndexOf(cur);
            idx = (idx + 1) % ids.Count;
            dm.ApplyLens(ids[idx], eye);
            Debug.Log($"lens {eye} -> {ids[idx]}");
        }
    }
}
