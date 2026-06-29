using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

namespace Simulador.Vision
{
    /// <summary>
    /// Gestiona el cambio entre escenarios (consultorio / ruta_noche): activa el root
    /// correspondiente y aplica su entorno (luz dia/noche), halos/astigmatismo,
    /// visibilidad del libro, fondo de camara y recolocacion del rig. Port del
    /// SCENARIOS + _apply_environment de main.gd, en version pragmatica.
    /// </summary>
    public class ScenarioManager : MonoBehaviour
    {
        [Header("Roots de escenario")]
        public GameObject consultorio;
        public GameObject rutaNoche;

        [Header("Refs")]
        public GlareController glare;
        public GameObject book;           // libro en la mano (solo consultorio)
        public Light sun;
        public Camera xrCamera;
        public Transform xrOrigin;

        [Header("Poses del rig por escenario")]
        public Vector3 consultorioOriginPos = new Vector3(-0.35f, -0.05f, -0.40f); // sentado en la silla, cerca del escritorio
        public Vector3 consultorioOriginEuler = new Vector3(0f, 90f, 0f);          // mirar hacia el escritorio (+X)
        public Vector3 rutaOriginPos = new Vector3(-0.45f, -0.14f, -0.55f);        // asiento del conductor
        public Vector3 rutaOriginEuler = Vector3.zero;                             // mirar al frente (+Z, hacia la ruta)

        [Header("Inicial")]
        public string startScenario = "ruta_noche";

        public string Current { get; private set; }
        private static readonly List<string> Order = new() { "consultorio", "ruta_noche" };

        private void Start() => SwitchTo(startScenario);

        public void CycleScenario()
        {
            int i = Order.IndexOf(Current);
            SwitchTo(Order[(i + 1) % Order.Count]);
        }

        public void SwitchTo(string id)
        {
            bool night = id == "ruta_noche";
            if (consultorio) consultorio.SetActive(!night);
            if (rutaNoche) rutaNoche.SetActive(night);
            if (book) book.SetActive(!night);                // libro solo de dia

            if (glare) { glare.halosEnabled = night; glare.Refresh(); }   // halos solo de noche

            if (night) ApplyNight(); else ApplyDay();

            if (xrOrigin)
            {
                xrOrigin.position = night ? rutaOriginPos : consultorioOriginPos;
                xrOrigin.rotation = Quaternion.Euler(night ? rutaOriginEuler : consultorioOriginEuler);
            }

            Current = id;
            Debug.Log($"ScenarioManager: -> {id}");
        }

        private void ApplyDay()
        {
            RenderSettings.ambientMode = AmbientMode.Flat;
            RenderSettings.ambientLight = new Color(0.55f, 0.52f, 0.45f);
            if (sun) { sun.intensity = 1.25f; sun.color = new Color(1f, 0.96f, 0.88f); sun.shadows = LightShadows.Soft; sun.transform.rotation = Quaternion.Euler(50f, -35f, 0f); }
            if (xrCamera) { xrCamera.clearFlags = CameraClearFlags.Skybox; }
            Shader.SetGlobalFloat("_PupilScene", 0f); // dia: pupila chica
        }

        private void ApplyNight()
        {
            // Noche: luz ambiental GENERAL de baja intensidad, suficiente para ver el
            // color de los autos en la oscuridad; los pozos de luz de los faroles lo
            // acentuan (quedan mucho mas brillantes que este piso ambiental).
            RenderSettings.ambientMode = AmbientMode.Flat;
            RenderSettings.ambientLight = new Color(0.4f, 0.4f, 0.4f); // ambiente general bajo
            // Luna (direccional) APAGADA: la direccionalidad la dan los faroles/autos.
            if (sun) { sun.intensity = 0f; sun.shadows = LightShadows.None; }
            if (xrCamera) { xrCamera.clearFlags = CameraClearFlags.SolidColor; xrCamera.backgroundColor = new Color(0.008f, 0.01f, 0.02f); }
            Shader.SetGlobalFloat("_PupilScene", 1f); // noche: pupila dilatada
        }
    }
}
