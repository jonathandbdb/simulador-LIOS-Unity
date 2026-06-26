using System.Collections.Generic;
using UnityEngine;

namespace Simulador.Vision
{
    /// <summary>
    /// Trafico nocturno. Instancia prefabs de auto EDITABLES (Assets/Prefabs/Cars) y
    /// los mueve viniendo de frente. CONVENCION del prefab: el FRENTE del auto apunta
    /// a +Z local; aca se spawnea rotado 180 para que venga hacia el jugador (-Z).
    /// Cada prefab trae su carroceria+ruedas (hijo "Body") y sus marcadores de luz
    /// ("Headlights"/"Taillights" con GlareBillboardInstance). No hay deteccion en
    /// runtime: lo que pongas en el prefab es lo que se ve.
    /// </summary>
    public class NightTraffic : MonoBehaviour
    {
        [Tooltip("Prefabs de auto (Assets/Prefabs/Cars). Frente del auto = +Z local.")]
        public GameObject[] carPrefabs;
        public int count = 5;
        public float speed = 16f;
        public float laneX = 2.6f;
        public float startZ = 75f;
        public float endZ = -16f;

        private readonly List<Transform> _cars = new();

        private void Start()
        {
            if (carPrefabs == null || carPrefabs.Length == 0) { Debug.LogWarning("NightTraffic: sin carPrefabs"); return; }
            for (int i = 0; i < count; i++)
            {
                var prefab = carPrefabs[Random.Range(0, carPrefabs.Length)];
                if (prefab == null) continue;
                var c = Instantiate(prefab, transform);
                c.transform.localRotation = Quaternion.Euler(0f, 180f, 0f); // +Z frente -> -Z hacia el jugador
                float lane = laneX + Random.Range(-0.4f, 0.4f);
                float z = Mathf.Lerp(startZ, endZ, i / (float)Mathf.Max(1, count)) + Random.Range(0f, 6f);
                c.transform.localPosition = new Vector3(lane, 0f, z);
                _cars.Add(c.transform);
            }
        }

        private void Update()
        {
            float dz = speed * Time.deltaTime;
            foreach (var c in _cars)
            {
                var p = c.localPosition;
                p.z -= dz;
                if (p.z < endZ) p.z = startZ;
                c.localPosition = p;
            }
        }
    }
}
