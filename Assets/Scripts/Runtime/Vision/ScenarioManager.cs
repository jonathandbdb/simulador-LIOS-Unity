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
        public Vector3 consultorioOriginPos = new Vector3(-1.4f, 0f, -2.8f);
        public Vector3 rutaOriginPos = Vector3.zero;

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

            if (xrOrigin) { xrOrigin.position = night ? rutaOriginPos : consultorioOriginPos; xrOrigin.rotation = Quaternion.identity; }

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
            // Noche mas oscura: para que resalten las luces de farolas y autos.
            RenderSettings.ambientMode = AmbientMode.Flat;
            RenderSettings.ambientLight = new Color(0.004f, 0.005f, 0.01f); // casi negro
            // Luna APAGADA: una direccional ilumina TODO parejo y mata el contraste.
            // La luz la dan SOLO los faroles y los autos (pozos definidos).
            if (sun) { sun.intensity = 0f; sun.shadows = LightShadows.None; }
            if (xrCamera) { xrCamera.clearFlags = CameraClearFlags.SolidColor; xrCamera.backgroundColor = new Color(0.008f, 0.01f, 0.02f); }
            Shader.SetGlobalFloat("_PupilScene", 1f); // noche: pupila dilatada
        }
    }
}
