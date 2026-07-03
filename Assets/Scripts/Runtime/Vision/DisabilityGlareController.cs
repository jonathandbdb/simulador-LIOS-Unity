using Simulador.Data;
using UnityEngine;

namespace Simulador.Vision
{
    /// <summary>
    /// Encandilamiento clinico (disability glare / straylight). Ante fuentes brillantes
    /// (sol de dia; faros/farolas de noche) genera un velo de luminancia POR OJO que lava
    /// la imagen y baja el contraste.
    ///
    /// Modelo: CIE general disability glare equation en su forma simple de Stiles-Holladay
    /// (Vos 1984; CIE 146:2002): Lveil = 10 * Egl / theta^2, con theta en GRADOS y validez
    /// 1deg < theta < 30deg. Egl (iluminancia de la fuente en el ojo) se modela proporcional
    /// a energia_fuente * luminancia_mesopica * (1/d^2) * facing. Termino de edad opcional
    /// [1 + (A/62.5)^4] (CIE 146:2002). El modelo es NORMALIZADO (energia relativa, faro=1.0;
    /// sin cd/m^2 reales): la constante 10 y el termino de edad se expresan relativos al
    /// paciente/caso de referencia para preservar la intensidad global de hoy y corregir SOLO
    /// la FORMA de la curva angular (1/theta^2 es mucho mas aguda cerca de la fuente que el
    /// cono suavizado previo). Ver docs/vision-optica.md (formulas + Referencias).
    ///
    /// Las fuentes son los GlareBillboardInstance activos (registro estatico); el velo escala
    /// por el STRAYLIGHT de la lente (por ojo) y la dilatacion pupilar (mayor de noche), se
    /// anula si la fuente esta ocluida, y sirve para ambos escenarios sin tocar la escena.
    /// Hereda el ciclo de vida comun (espera del singleton + suscripcion + despacho inicial por
    /// ojo + desuscripcion) de VisionStateBinder (P6.1); en OnBinderDisable resetea los globals
    /// del velo y VisionActivity.Veil* (limpieza propia de este binder).
    /// </summary>
    public class DisabilityGlareController : VisionStateBinder
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
        [Tooltip("Clamp inferior de theta (grados) para el termino CIE 1/theta^2: evita la " +
                 "divergencia en la linea de vision. CIE 146:2002 valida desde ~1 grado.")]
        public float cieThetaMinDeg = 1f;
        [Tooltip("Limite superior de validez CIE (grados): mas alla el velo cae suavemente a 0.")]
        public float cieThetaMaxDeg = 30f;
        [Tooltip("Angulo (grados) a partir del cual una fuente ya no aporta (cull + fin del fade).")]
        public float outerAngleDeg = 42f;
        [Tooltip("Edad del paciente (anios) para el termino CIE de dispersion intraocular " +
                 "[1+(A/62.5)^4]. Default 70 = edad media tipica de cirugia de catarata; el velo " +
                 "se normaliza a ese paciente (cambiar la edad escala relativo a el).")]
        public float age = 70f;
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

        // Edad del paciente de referencia (anios): edad media tipica de cirugia de catarata.
        // El termino de edad CIE se normaliza a este valor (ver ageFactor en LateUpdate).
        private const float CalibAge = 70f;

        private float _strayL, _strayR;
        private float _veilL, _veilR;
        private Vector2 _uv = new Vector2(0.5f, 0.5f);

        private static readonly int VeilLId = Shader.PropertyToID("_GlareVeilL");
        private static readonly int VeilRId = Shader.PropertyToID("_GlareVeilR");
        private static readonly int VeilUVId = Shader.PropertyToID("_GlareVeilUV");
        private static readonly int VeilTintId = Shader.PropertyToID("_GlareVeilTint");

        protected override void ApplyEyeState(string eye, EyeState state) => ReadStray(eye, state);

        // Limpieza propia: al desactivarse, apaga el velo (globals + gate de CPU 3.1). La
        // desuscripcion del evento la hace la base (VisionStateBinder.OnDisable).
        protected override void OnBinderDisable()
        {
            Shader.SetGlobalFloat(VeilLId, 0f);
            Shader.SetGlobalFloat(VeilRId, 0f);
            VisionActivity.VeilL = 0f;   // gate de CPU (3.1)
            VisionActivity.VeilR = 0f;
        }

        private void ReadStray(string eye, EyeState state)
        {
            float v = (state != null && !state.IsEmpty &&
                       state.Params.TryGetValue("straylight", out var s)) ? s : 0f;
            if (eye == "left") _strayL = v; else _strayR = v;
        }

        private void LateUpdate()
        {
            if (cam == null) cam = Camera.main;

            float sum = 0f, domW = 0f;
            GlareBillboardInstance dom = null;

            if (cam != null)
            {
                Vector3 camPos = cam.transform.position;
                Vector3 fwd = cam.transform.forward;
                // Registro estatico mantenido por GlareBillboardInstance.OnEnable/OnDisable:
                // una fuente nueva encandila el MISMO frame (antes: escaneo cada 0.5 s).
                var sources = GlareBillboardInstance.Active;
                for (int i = 0; i < sources.Count; i++)
                {
                    var s = sources[i];
                    if (s == null || !s.isActiveAndEnabled) continue;
                    Vector3 sp = s.transform.position;
                    Vector3 to = sp - camPos;
                    float dist = to.magnitude;
                    if (dist < 0.01f) continue;
                    Vector3 dirToSrc = to / dist;

                    float ang = Vector3.Angle(fwd, dirToSrc);
                    if (ang >= outerAngleDeg) continue;

                    // Termino angular CIE (Stiles-Holladay): Lveil ~ 1/theta^2 (theta en grados).
                    // Normalizado a pico ~1 en theta = cieThetaMinDeg (=1 grado) para preservar la
                    // intensidad del caso de referencia (la constante 10 cd/m2 por lux/deg2 se
                    // absorbe en la normalizacion; modelo sin unidades). Clamp inferior evita la
                    // divergencia; mas alla de cieThetaMaxDeg (30 grados, fin de validez CIE) cae
                    // suavemente a 0 hacia outerAngleDeg.
                    float theta = Mathf.Max(ang, cieThetaMinDeg);
                    float angular = (cieThetaMinDeg * cieThetaMinDeg) / (theta * theta);
                    if (ang > cieThetaMaxDeg)
                    {
                        // Caida suave a 0 entre cieThetaMaxDeg (fin validez CIE) y outerAngleDeg.
                        // OJO: Mathf.SmoothStep(from,to,t) NO es el smoothstep de HLSL (t es 0..1);
                        // el equivalente correcto es SmoothStep(0,1, InverseLerp(edge0,edge1,x)).
                        float taper = Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(cieThetaMaxDeg, outerAngleDeg, ang));
                        angular *= 1f - taper;
                    }
                    if (angular <= 0.0001f) continue;

                    // Luminancia mesopica de la fuente derivada de la eficiencia luminosa
                    // ESCOTOPICA V'(lambda) (CIE 1951) aplicada a primarios sRGB (aprox.
                    // ~0.02/0.70/0.28): el rojo casi no dispersa de noche (Purkinje: un piloto
                    // rojo encandila mucho menos que un faro blanco); el azul-verde domina.
                    Color col = s.srcColor;
                    float lum = Mathf.Clamp01(0.02f * col.r + 0.70f * col.g + 0.28f * col.b);
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
                    // Umbrales de facing UNIFICADOS con el billboard (fuente unica:
                    // GlareController.FacingLo/Hi, publicados tambien como globals de shader).
                    float facing = 1f;
                    if (s.srcDir.sqrMagnitude > 0.25f)
                    {
                        Vector3 beam = s.transform.TransformDirection(s.srcDir).normalized;
                        facing = Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(
                            GlareController.FacingLo, GlareController.FacingHi, Vector3.Dot(beam, -dirToSrc)));
                        if (facing <= 0.001f) continue;
                    }

                    // Oclusion: si algo (pared/cabina) se interpone, esa fuente no encandila.
                    if (Physics.Linecast(camPos, sp, occluders, QueryTriggerInteraction.Ignore)) continue;

                    float w = Mathf.Max(s.srcEnergy, 0.01f) * lum * distFactor * distGate * angular * facing;
                    sum += w;
                    if (w > domW) { domW = w; dom = s; }
                }
            }

            float pupil = (scenario != null && scenario.Current == "ruta_noche") ? nightPupilFactor : 1f;
            // Termino de edad CIE 146:2002 [1+(A/62.5)^4], normalizado al paciente de referencia
            // (CalibAge) para que la edad default no cambie la intensidad global: cambiar 'age'
            // escala el velo relativo a ese paciente (mayor edad => mas dispersion => mas velo).
            float ageFactor = (1f + Mathf.Pow(age / 62.5f, 4f)) /
                              (1f + Mathf.Pow(CalibAge / 62.5f, 4f));
            float baseVeil = sum * sensitivity * pupil * ageFactor;
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

            // Gate de CPU (3.1): publica el velo SUAVIZADO actual (no el target). Es continuo,
            // asi el gate no titila cuando el velo decae exponencialmente a cero.
            VisionActivity.VeilL = _veilL;
            VisionActivity.VeilR = _veilR;

            Shader.SetGlobalFloat(VeilLId, _veilL);
            Shader.SetGlobalFloat(VeilRId, _veilR);
            Shader.SetGlobalVector(VeilUVId, new Vector4(_uv.x, _uv.y, 0f, 0f));
            Shader.SetGlobalColor(VeilTintId, veilTint);
        }
    }
}
