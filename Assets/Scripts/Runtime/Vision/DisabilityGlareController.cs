using System.Collections;
using Simulador.Data;
using UnityEngine;

namespace Simulador.Vision
{
    /// <summary>
    /// Encandilamiento clinico (disability glare / straylight). Ante fuentes brillantes
    /// (sol de dia; faros/farolas de noche) genera un velo de luminancia POR OJO que lava
    /// la imagen y baja el contraste. Modelo CIE aproximado: el velo crece con el brillo
    /// de la fuente y su cercania a la linea de vision, se anula si la fuente esta ocluida,
    /// y escala por el STRAYLIGHT de la lente (por ojo, desde el catalogo/tablet) y por la
    /// dilatacion pupilar (mayor de noche). Las fuentes son los GlareBillboardInstance
    /// activos (los mismos billboards del sol y de las luces nocturnas), asi sirve para
    /// ambos escenarios sin tocar la escena de noche.
    /// </summary>
    public class DisabilityGlareController : MonoBehaviour
    {
        [Tooltip("Camara del jugador (si null, usa Camera.main).")]
        public Camera cam;
        [Tooltip("Para el boost pupilar de noche (opcional).")]
        public ScenarioManager scenario;

        [Header("Respuesta")]
        [Tooltip("Sensibilidad global del velo.")]
        public float sensitivity = 0.18f;
        [Range(0f, 1f)]
        [Tooltip("Tope del velo (confort VR).")]
        public float maxVeil = 0.6f;
        [Tooltip("Angulo (grados) donde una fuente aporta el maximo.")]
        public float innerAngleDeg = 5f;
        [Tooltip("Angulo (grados) a partir del cual una fuente ya no aporta.")]
        public float outerAngleDeg = 42f;
        [Tooltip("Distancia de referencia (m) para el inverso del cuadrado: a esta distancia el aporte es 'nominal'.")]
        public float refDistance = 4f;
        [Tooltip("Distancia minima (m) para no explotar con fuentes muy pegadas.")]
        public float nearClampDistance = 2f;
        [Tooltip("Hasta esta distancia (m) la fuente puntual aporta pleno; mas alla decae hacia cero.")]
        public float fullWeightDistance = 10f;
        [Tooltip("Distancia (m) a partir de la cual una fuente puntual ya NO encandila (auto lejos ~ no influye).")]
        public float cutoffDistance = 20f;
        [Tooltip("Multiplicador pupilar de noche (pupila dilatada => mas straylight).")]
        public float nightPupilFactor = 1.5f;
        [Tooltip("Suavizado temporal (confort VR).")]
        public float smoothing = 5f;
        public Color veilTint = new Color(1f, 0.95f, 0.85f);
        [Tooltip("Capas que ocluyen las fuentes (paredes, cabina).")]
        public LayerMask occluders = ~0;

        private float _strayL, _strayR;
        private float _veilL, _veilR;
        private Vector2 _uv = new Vector2(0.5f, 0.5f);
        private GlareBillboardInstance[] _sources = System.Array.Empty<GlareBillboardInstance>();
        private float _refreshTimer;
        private DataManager _dm;

        private static readonly int VeilLId = Shader.PropertyToID("_GlareVeilL");
        private static readonly int VeilRId = Shader.PropertyToID("_GlareVeilR");
        private static readonly int VeilUVId = Shader.PropertyToID("_GlareVeilUV");
        private static readonly int VeilTintId = Shader.PropertyToID("_GlareVeilTint");

        private IEnumerator Start()
        {
            while (DataManager.Instance == null) yield return null;
            _dm = DataManager.Instance;
            _dm.VisionStateChanged += OnVision;
            ReadStray("left", _dm.Left);
            ReadStray("right", _dm.Right);
            RefreshSources();
        }

        private void OnDisable()
        {
            if (_dm != null) _dm.VisionStateChanged -= OnVision;
            Shader.SetGlobalFloat(VeilLId, 0f);
            Shader.SetGlobalFloat(VeilRId, 0f);
        }

        private void OnVision(string eye, EyeState state) => ReadStray(eye, state);

        private void ReadStray(string eye, EyeState state)
        {
            float v = (state != null && !state.IsEmpty &&
                       state.Params.TryGetValue("straylight", out var s)) ? s : 0f;
            if (eye == "left") _strayL = v; else _strayR = v;
        }

        private void RefreshSources()
        {
            // Solo activos (escenario actual). Incluye el sol (consultorio) y las luces de noche.
            _sources = Object.FindObjectsByType<GlareBillboardInstance>(FindObjectsSortMode.None);
        }

        private void LateUpdate()
        {
            if (cam == null) cam = Camera.main;

            _refreshTimer += Time.deltaTime;
            if (_refreshTimer >= 0.5f) { _refreshTimer = 0f; RefreshSources(); }

            float sum = 0f, domW = 0f;
            GlareBillboardInstance dom = null;

            if (cam != null)
            {
                Vector3 camPos = cam.transform.position;
                Vector3 fwd = cam.transform.forward;
                foreach (var s in _sources)
                {
                    if (s == null || !s.isActiveAndEnabled) continue;
                    Vector3 sp = s.transform.position;
                    Vector3 to = sp - camPos;
                    float dist = to.magnitude;
                    if (dist < 0.01f) continue;
                    Vector3 dirToSrc = to / dist;

                    float ang = Vector3.Angle(fwd, dirToSrc);
                    if (ang >= outerAngleDeg) continue;

                    // Concentracion angular: 1 mirando directo, 0 al borde del cono.
                    float f = Mathf.Clamp01(Mathf.InverseLerp(outerAngleDeg, innerAngleDeg, ang));
                    f *= f;
                    if (f <= 0.0001f) continue;

                    // Luminancia mesopica de la fuente: castiga el ROJO (Purkinje). Un piloto
                    // rojo aporta ~10x menos que un faro blanco del mismo brillo.
                    Color col = s.srcColor;
                    float lum = Mathf.Clamp01(0.10f * col.r + 0.78f * col.g + 0.12f * col.b);
                    if (lum <= 0.001f) continue;

                    // Ley del inverso del cuadrado (iluminancia en el ojo) + corte por distancia:
                    // auto lejos aporta ~0, cerca mucho. El sol (distanceInvariant) NO atenua.
                    float distFactor, distGate;
                    if (s.distanceInvariant) { distFactor = 1f; distGate = 1f; }
                    else
                    {
                        if (dist >= cutoffDistance) continue;
                        distFactor = (refDistance * refDistance) / Mathf.Max(dist * dist, nearClampDistance * nearClampDistance);
                        distGate = Mathf.Clamp01((cutoffDistance - dist) / Mathf.Max(cutoffDistance - fullWeightDistance, 0.01f));
                        distGate = distGate * distGate * (3f - 2f * distGate);
                    }

                    // Direccion del haz: un faro solo encandila si te APUNTA (el que se aleja, no).
                    float facing = 1f;
                    if (s.srcDir.sqrMagnitude > 0.25f)
                    {
                        Vector3 beam = s.transform.TransformDirection(s.srcDir).normalized;
                        facing = Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(0.05f, 0.35f, Vector3.Dot(beam, -dirToSrc)));
                        if (facing <= 0.001f) continue;
                    }

                    // Oclusion: si algo (pared/cabina) se interpone, esa fuente no encandila.
                    if (Physics.Linecast(camPos, sp, occluders, QueryTriggerInteraction.Ignore)) continue;

                    float w = Mathf.Max(s.srcEnergy, 0.01f) * lum * distFactor * distGate * f * facing;
                    sum += w;
                    if (w > domW) { domW = w; dom = s; }
                }
            }

            float pupil = (scenario != null && scenario.Current == "ruta_noche") ? nightPupilFactor : 1f;
            float baseVeil = sum * sensitivity * pupil;
            float tL = Mathf.Min(maxVeil, _strayL * baseVeil);
            float tR = Mathf.Min(maxVeil, _strayR * baseVeil);

            if (dom != null && cam != null)
            {
                Vector3 vp = cam.WorldToViewportPoint(dom.transform.position);
                if (vp.z > 0f) _uv = new Vector2(Mathf.Clamp01(vp.x), Mathf.Clamp01(vp.y));
            }

            float k = 1f - Mathf.Exp(-smoothing * Time.deltaTime);
            _veilL = Mathf.Lerp(_veilL, tL, k);
            _veilR = Mathf.Lerp(_veilR, tR, k);

            Shader.SetGlobalFloat(VeilLId, _veilL);
            Shader.SetGlobalFloat(VeilRId, _veilR);
            Shader.SetGlobalVector(VeilUVId, new Vector4(_uv.x, _uv.y, 0f, 0f));
            Shader.SetGlobalColor(VeilTintId, veilTint);
        }
    }
}
