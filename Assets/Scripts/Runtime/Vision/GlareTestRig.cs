using UnityEngine;

namespace Simulador.Vision
{
    /// <summary>
    /// Rig de verificacion de F4: baja un poco la luz (los halos aditivos se ven mejor
    /// sobre fondo mas oscuro) y spawnea fuentes de luz con sus billboards de glare,
    /// a la altura de los ojos y al frente. Se quita/ajusta en F5.
    /// </summary>
    public class GlareTestRig : MonoBehaviour
    {
        public bool dimScene = true;

        private void Start()
        {
            if (dimScene)
            {
                RenderSettings.ambientMode = UnityEngine.Rendering.AmbientMode.Flat;
                RenderSettings.ambientLight = new Color(0.10f, 0.11f, 0.14f);
                var sun = Object.FindFirstObjectByType<Light>();
                if (sun != null && sun.type == LightType.Directional)
                {
                    sun.intensity = 0.20f;
                    sun.color = new Color(0.7f, 0.75f, 1f);
                }
            }

            // Lamparas a la altura de los ojos (~1.2 m), al frente (+Z), bien visibles.
            int n = 0;
            n += Spawn(new Vector3(-0.8f, 1.25f, 3.0f), new Color(1f, 0.95f, 0.82f), 1.0f);
            n += Spawn(new Vector3(0.9f, 1.35f, 3.6f), new Color(1f, 0.95f, 0.82f), 0.9f);
            n += Spawn(new Vector3(0.0f, 1.15f, 2.4f), new Color(1f, 0.18f, 0.12f), 0.85f);
            Debug.Log($"GlareTestRig: {n} lamparas spawneadas (dimScene={dimScene}).");
        }

        private int Spawn(Vector3 pos, Color color, float energy)
        {
            var lamp = GameObject.CreatePrimitive(PrimitiveType.Sphere);
            lamp.name = "TestLamp";
            lamp.transform.position = pos;
            lamp.transform.localScale = Vector3.one * 0.12f;
            Destroy(lamp.GetComponent<Collider>());
            var mr = lamp.GetComponent<MeshRenderer>();
            var m = new Material(Shader.Find("Universal Render Pipeline/Lit"));
            m.color = color;
            m.EnableKeyword("_EMISSION");
            m.globalIlluminationFlags = MaterialGlobalIlluminationFlags.RealtimeEmissive;
            m.SetColor("_EmissionColor", color * 6f);
            mr.sharedMaterial = m;

            GlareSource.Attach(lamp.transform, Vector3.zero, color, energy);
            return 1;
        }
    }
}
