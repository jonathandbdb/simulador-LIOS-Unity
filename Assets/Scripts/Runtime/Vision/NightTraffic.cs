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
        public int count = 4;
        public float speed = 16f;
        [Tooltip("Distancia |x| de cada carril al centro. Derecho = +laneX, izquierdo = -laneX.")]
        public float laneX = 2.6f;
        public float startZ = 70f;   // lejos, adelante del jugador
        public float endZ = -14f;    // detras del jugador
        [Tooltip("Gap aleatorio EXTRA (m) mas alla del punto de reaparicion al hacer wrap: el auto " +
                 "tarda un tiempo variable en volver a entrar al tramo, creando huecos en el flujo " +
                 "(no estan los 'count' autos visibles a la vez).")]
        public float wrapGapMax = 35f;
        [Range(0f, 0.5f)]
        [Tooltip("Variacion +/- de velocidad por auto (fraccion; 0.15 = +/-15%) para romper la " +
                 "periodicidad del flujo.")]
        public float speedJitter = 0.15f;

        // Sentido por auto: +1 = se aleja (+Z, luces traseras) ; -1 = viene (-Z, faros).
        private readonly List<Transform> _cars = new();
        private readonly List<int> _dirs = new();
        // Velocidad real por auto (speed base +/- speedJitter): rompe la cadencia periodica.
        private readonly List<float> _speeds = new();
        // Indice del color de carroceria actual por auto: al re-tintar en el wrap evitamos repetir
        // el color inmediato anterior del MISMO auto (que no se note el reciclado del GameObject).
        private readonly List<int> _colorIdx = new();
        // MPB reutilizado (no se serializa): evita alocar uno por cada re-tinte en el wrap.
        private MaterialPropertyBlock _mpb;

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
            // Ultimo color usado por carril: evita que dos autos del MISMO carril arranquen igual.
            int lastRightColor = -1, lastLeftColor = -1;
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
                // Distribucion inicial ALEATORIA por carril (muestreo estratificado): el tramo se
                // divide en 'laneCount' segmentos y cada auto del carril cae en uno DISTINTO, en
                // posicion random dentro del segmento -> z aleatorio pero sin apelotonar dos autos
                // del mismo carril (reemplaza el reparto uniforme por Lerp).
                int laneIdx = i / 2;
                int laneCount = rightLane ? (count + 1) / 2 : count / 2;
                float segLen = (startZ - endZ) / Mathf.Max(1, laneCount);
                float z = endZ + (laneIdx + Random.Range(0.15f, 0.85f)) * segLen;
                c.transform.localPosition = new Vector3(x, 0f, z);

                int colorIdx = PickColor(rightLane ? lastRightColor : lastLeftColor);
                if (rightLane) lastRightColor = colorIdx; else lastLeftColor = colorIdx;
                ApplyBodyColor(c, BodyColors[colorIdx]);

                _cars.Add(c.transform);
                _dirs.Add(dir);
                _speeds.Add(speed * (1f + Random.Range(-speedJitter, speedJitter)));
                _colorIdx.Add(colorIdx);
            }
        }

        private void Update()
        {
            float dt = Time.deltaTime;
            for (int i = 0; i < _cars.Count; i++)
            {
                var c = _cars[i];
                var p = c.localPosition;
                p.z += _dirs[i] * _speeds[i] * dt;
                // Wrap con gap aleatorio EXTRA fuera del tramo: el que se aleja reaparece detras
                // (mas alla de endZ) y el que viene reaparece adelante (mas alla de startZ), a una
                // distancia random -> el auto tarda un tiempo variable en re-entrar al tramo,
                // dejando huecos en el flujo (cadencia no periodica, no siempre 'count' visibles).
                bool wrapped = false;
                if (_dirs[i] > 0 && p.z > startZ) { p.z = endZ - Random.Range(0f, wrapGapMax); wrapped = true; }
                else if (_dirs[i] < 0 && p.z < endZ) { p.z = startZ + Random.Range(0f, wrapGapMax); wrapped = true; }
                c.localPosition = p;

                if (wrapped)
                {
                    // Reciclamos el MISMO GameObject, pero re-randomizamos apariencia y velocidad
                    // para que cada reentrada parezca un auto distinto (evita el "en el carril
                    // derecho solo pasan azules": con pocos autos los 2 del carril se reusaban
                    // siempre con su color inicial). Nuevo color != al anterior + nuevo jitter.
                    int ci = PickColor(_colorIdx[i]);
                    _colorIdx[i] = ci;
                    ApplyBodyColor(c.gameObject, BodyColors[ci]);
                    _speeds[i] = speed * (1f + Random.Range(-speedJitter, speedJitter));
                }
            }
        }

        // Sortea un indice de BodyColors distinto de 'exclude' (color inmediato anterior del
        // mismo auto/carril); exclude = -1 => sin restriccion. Con >=2 colores siempre encuentra.
        private static int PickColor(int exclude)
        {
            if (BodyColors.Length <= 1) return 0;
            int idx;
            do { idx = Random.Range(0, BodyColors.Length); } while (idx == exclude);
            return idx;
        }

        // Tinta solo el/los slot(s) cuyo material se llama "Body" (carroceria), dejando
        // intactos vidrios y luces. Usa MaterialPropertyBlock por indice de material.
        private void ApplyBodyColor(GameObject car, Color color)
        {
            _mpb ??= new MaterialPropertyBlock(); // reutilizado entre autos y re-tintes (sin alocar)
            foreach (var r in car.GetComponentsInChildren<MeshRenderer>(true))
            {
                var mats = r.sharedMaterials;
                for (int s = 0; s < mats.Length; s++)
                {
                    if (mats[s] == null || mats[s].name != "Body") continue;
                    r.GetPropertyBlock(_mpb, s);
                    _mpb.SetColor(BaseColorID, color);
                    r.SetPropertyBlock(_mpb, s);
                }
            }
        }
    }
}
