using Simulador.Data;
using UnityEngine;

namespace Simulador.Vision
{
    /// <summary>
    /// Libro de lectura (consultorio). Port de book_holder.gd: cada frame mide la
    /// distancia libro->camara (suavizada) y la pasa al material de vision como
    /// _BookDistanceM + mascara en pantalla (_BookScreenUV / _BookScreenRadius). El
    /// shader aplica la MISMA curva de focos por ojo dentro de la mascara (el depth
    /// del libro en la mano no es confiable). No interpreta focos: solo mide.
    /// </summary>
    public class BookHolder : MonoBehaviour
    {
        [Tooltip("Material del post-proceso de vision (el mismo de VisionRendererFeature).")]
        public Material visionMaterial;
        [Tooltip("Transform del libro (si null, usa este GameObject).")]
        public Transform book;
        [Tooltip("Suavizado temporal (evita parpadeo por jitter del mando).")]
        public float smoothing = 8f;
        [Tooltip("Mitad del lado mayor del libro (m) para el radio de la mascara.")]
        public float bookHalfMeters = 0.14f;

        private Camera _cam;
        private float _smoothedDist = -1f;

        private static readonly int BookDistanceM = Shader.PropertyToID("_BookDistanceM");
        private static readonly int BookScreenUV = Shader.PropertyToID("_BookScreenUV");
        private static readonly int BookScreenRadius = Shader.PropertyToID("_BookScreenRadius");

        private void Awake()
        {
            if (book == null) book = transform;
        }

        private void OnDisable()
        {
            // Al salir del consultorio, desactivar la mascara del libro.
            if (visionMaterial != null) visionMaterial.SetFloat(BookScreenRadius, 0f);
        }

        private void LateUpdate()
        {
            if (visionMaterial == null) return;
            if (_cam == null) _cam = Camera.main;
            if (_cam == null || book == null) return;

            float dist = Vector3.Distance(book.position, _cam.transform.position);
            float alpha = Mathf.Clamp01(Time.deltaTime * smoothing);
            _smoothedDist = _smoothedDist <= 0f ? dist : Mathf.Lerp(_smoothedDist, dist, alpha);
            visionMaterial.SetFloat(BookDistanceM, _smoothedDist);

            UpdateScreenMask();
        }

        private void UpdateScreenMask()
        {
            // Detras de la camara -> sin mascara.
            Vector3 vp = _cam.WorldToViewportPoint(book.position);
            if (vp.z <= 0f)
            {
                visionMaterial.SetFloat(BookScreenRadius, 0f);
                return;
            }
            // Radio segun el tamano ANGULAR real del libro (proyectar punto a medio-libro).
            Vector3 edge = book.position + _cam.transform.up * bookHalfMeters;
            Vector3 vpEdge = _cam.WorldToViewportPoint(edge);
            float radius = Mathf.Abs(vpEdge.y - vp.y);
            radius = Mathf.Clamp(radius * 1.45f, 0.06f, 0.45f);
            visionMaterial.SetVector(BookScreenUV, new Vector4(vp.x, vp.y, 0f, 0f));
            visionMaterial.SetFloat(BookScreenRadius, radius);
        }
    }
}
