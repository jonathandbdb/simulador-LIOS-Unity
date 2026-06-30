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

        [Header("Glare (consultorio / dia)")]
        [Range(0f, 1f)]
        [Tooltip("Halos (anillos) de dia: casi suprimidos: a plena luz y pupila contraida los anillos se lavan.")]
        public float dayHaloScale = 0.2f;
        [Range(0f, 1f)]
        [Tooltip("Destellos/starburst de dia: predominan alrededor del sol (clinicamente lo visible a plena luz).")]
        public float dayStarScale = 0.7f;

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

            // Glare siempre activo. De dia: halos casi suprimidos pero destellos visibles
            // (alrededor del sol); de noche: todo a full (halos marcados + starburst).
            if (glare)
            {
                glare.halosEnabled = true;
                glare.haloScale = night ? 1f : dayHaloScale;
                glare.starScale = night ? 1f : dayStarScale;
                glare.Refresh();
            }

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
            RenderSettings.reflectionIntensity = 1f; // de dia el interior si refleja el cielo (ventana del consultorio)
            // Sol al frente (hacia el lado de la ventana, yaw ~335 / elev ~14) para que se
            // vea por la ventana del consultorio. La luz APUNTA al interior (-sunDir).
            // El consultorio no usa direccional (GO apagado). La dejamos configurada pero
            // OFF, para que la luna nocturna no quede encendida al pasar de noche -> dia.
            if (sun) { sun.intensity = 1.25f; sun.color = new Color(1f, 0.96f, 0.88f); sun.shadows = LightShadows.Soft; sun.transform.rotation = Quaternion.LookRotation(new Vector3(0.410f, -0.242f, -0.879f)); sun.gameObject.SetActive(false); }
            if (xrCamera) { xrCamera.clearFlags = CameraClearFlags.Skybox; }
            Shader.SetGlobalFloat("_PupilScene", 0f); // dia: pupila chica
        }

        private void ApplyNight()
        {
            // Noche: luz ambiental GENERAL de baja intensidad, suficiente para ver el
            // color de los autos en la oscuridad; los pozos de luz de los faroles lo
            // acentuan (quedan mucho mas brillantes que este piso ambiental).
            RenderSettings.ambientMode = AmbientMode.Flat;
            RenderSettings.ambientLight = new Color(0.14f, 0.14f, 0.15f); // ambiente nocturno bajo (antes 0.4f: lavaba la escena)
            // Mata el reflejo del skybox diurno (azul) sobre el interior del auto: de noche el
            // entorno es oscuro y los reflejos los aportan faroles/faros (luces en tiempo real).
            RenderSettings.reflectionIntensity = 0f;
            // Luna tenue (direccional) CASI NEUTRA: da una base para ver la carroceria y el
            // color de los autos en la oscuridad sin lavar el interior (que queda bajo el
            // techo del auto). Los faroles/faros siguen dando el "pop" de color al pasar.
            if (sun)
            {
                sun.gameObject.SetActive(true);
                sun.type = LightType.Directional;
                sun.color = new Color(0.96f, 0.97f, 1f); // apenas fria, sin tinte azul marcado
                sun.intensity = 0.3f;                    // tenue: tunable (subir = autos mas visibles)
                sun.shadows = LightShadows.None;
                sun.transform.rotation = Quaternion.Euler(55f, 20f, 0f); // luna alta, casi cenital
            }
            if (xrCamera) { xrCamera.clearFlags = CameraClearFlags.SolidColor; xrCamera.backgroundColor = new Color(0.008f, 0.01f, 0.02f); }
            Shader.SetGlobalFloat("_PupilScene", 1f); // noche: pupila dilatada
        }
    }
}
