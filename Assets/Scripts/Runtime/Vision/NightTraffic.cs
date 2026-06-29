using System.Collections.Generic;
using UnityEngine;

namespace Simulador.Vision
{
    /// <summary>
    /// Trafico nocturno BIDIRECCIONAL. Instancia prefabs de auto editables
    /// (Assets/Prefabs/Cars) en dos carriles, segun circulacion por la DERECHA y
    /// tomando como referencia el POV del conductor (que mira hacia +Z):
    ///   - Carril DERECHO (x = +laneX): autos ALEJANDOSE (+Z, "hacia donde mira la
    ///     camara") -> se ven sus LUCES TRASERAS (los enfocamos desde atras).
    ///   - Carril IZQUIERDO (x = -laneX): autos VINIENDO de frente (-Z) -> se ven sus FAROS.
    /// CONVENCION del prefab: el FRENTE del auto apunta a +Z local. Los que se alejan no
    /// se rotan (frente a +Z, nos da la espalda); los que vienen se rotan 180 (frente a
    /// -Z, hacia el jugador). Cada prefab trae carroceria+ruedas (hijo "Body") y sus
    /// marcadores de luz ("Headlights"/"Taillights" con GlareBillboardInstance).
    /// </summary>
    public class NightTraffic : MonoBehaviour
    {
        [Tooltip("Prefabs de auto (Assets/Prefabs/Cars). Frente del auto = +Z local.")]
        public GameObject[] carPrefabs;
        [Tooltip("Cantidad total de autos (se reparten alternados entre los dos carriles).")]
        public int count = 6;
        public float speed = 16f;
        [Tooltip("Distancia |x| de cada carril al centro. Derecho = +laneX, izquierdo = -laneX.")]
        public float laneX = 2.6f;
        public float startZ = 70f;   // lejos, adelante del jugador
        public float endZ = -14f;    // detras del jugador

        // Sentido por auto: +1 = se aleja (+Z, luces traseras) ; -1 = viene (-Z, faros).
        private readonly List<Transform> _cars = new();
        private readonly List<int> _dirs = new();

        private static readonly int BaseColorID = Shader.PropertyToID("_BaseColor");
        // El material "Body" del prefab viene gris sin textura; aca le damos un color
        // realista (uno random por auto) via MaterialPropertyBlock, sin tocar el material
        // compartido ni los slots de vidrio/luces.
        private static readonly Color[] BodyColors =
        {
            new Color(0.70f, 0.06f, 0.07f), // rojo
            new Color(0.07f, 0.12f, 0.38f), // azul
            new Color(0.85f, 0.86f, 0.88f), // blanco
            new Color(0.04f, 0.04f, 0.05f), // negro
            new Color(0.52f, 0.54f, 0.57f), // plata
            new Color(0.09f, 0.28f, 0.13f), // verde
            new Color(0.62f, 0.50f, 0.10f), // dorado
            new Color(0.16f, 0.18f, 0.22f), // grafito
        };

        private void Start()
        {
            if (carPrefabs == null || carPrefabs.Length == 0) { Debug.LogWarning("NightTraffic: sin carPrefabs"); return; }
            for (int i = 0; i < count; i++)
            {
                var prefab = carPrefabs[Random.Range(0, carPrefabs.Length)];
                if (prefab == null) continue;

                // Carriles alternados: par = DERECHO (se aleja), impar = IZQUIERDO (viene de frente).
                bool rightLane = (i % 2 == 0);
                int dir = rightLane ? +1 : -1;
                float x = (rightLane ? laneX : -laneX) + Random.Range(-0.4f, 0.4f);

                var c = Instantiate(prefab, transform);
                // Frente +Z. Si se aleja (dir +1) queda mirando +Z (no rota); si viene (dir -1) rota 180.
                c.transform.localRotation = Quaternion.Euler(0f, dir > 0 ? 0f : 180f, 0f);
                float z = Mathf.Lerp(startZ, endZ, i / (float)Mathf.Max(1, count)) + Random.Range(0f, 6f);
                c.transform.localPosition = new Vector3(x, 0f, z);

                ApplyBodyColor(c, BodyColors[Random.Range(0, BodyColors.Length)]);

                _cars.Add(c.transform);
                _dirs.Add(dir);
            }
        }

        private void Update()
        {
            float d = speed * Time.deltaTime;
            for (int i = 0; i < _cars.Count; i++)
            {
                var c = _cars[i];
                var p = c.localPosition;
                p.z += _dirs[i] * d;
                // Wrap: el que se aleja reaparece detras; el que viene reaparece adelante.
                if (_dirs[i] > 0 && p.z > startZ) p.z = endZ;
                else if (_dirs[i] < 0 && p.z < endZ) p.z = startZ;
                c.localPosition = p;
            }
        }

        // Tinta solo el/los slot(s) cuyo material se llama "Body" (carroceria), dejando
        // intactos vidrios y luces. Usa MaterialPropertyBlock por indice de material.
        private static void ApplyBodyColor(GameObject car, Color color)
        {
            var mpb = new MaterialPropertyBlock();
            foreach (var r in car.GetComponentsInChildren<MeshRenderer>(true))
            {
                var mats = r.sharedMaterials;
                for (int s = 0; s < mats.Length; s++)
                {
                    if (mats[s] == null || mats[s].name != "Body") continue;
                    r.GetPropertyBlock(mpb, s);
                    mpb.SetColor(BaseColorID, color);
                    r.SetPropertyBlock(mpb, s);
                }
            }
        }
    }
}
