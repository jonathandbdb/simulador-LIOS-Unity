using Simulador.Data;
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

            _a.performed += _ => CycleLens("left");
            _b.performed += _ => CycleLens("right");
            _x.performed += _ => { if (glare) { glare.halosEnabled = !glare.halosEnabled; glare.Refresh(); Debug.Log($"halos={glare.halosEnabled}"); } };
            _y.performed += _ => { if (scenarios) scenarios.CycleScenario(); };

            _a.Enable(); _b.Enable(); _x.Enable(); _y.Enable();
        }

        private void OnDisable()
        {
            _a?.Disable(); _b?.Disable(); _x?.Disable(); _y?.Disable();
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
