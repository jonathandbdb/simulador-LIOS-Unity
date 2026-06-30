using UnityEngine;

namespace Simulador.Vision
{
    /// <summary>
    /// Componente serializable de un billboard de glare. Guarda color/energia/
    /// direccion/seed de la fuente y los reaplica al MeshRenderer via
    /// MaterialPropertyBlock en cada OnEnable. CLAVE: el MPB no se serializa, asi
    /// que sin este componente las fuentes (en escena o prefab) perderian sus
    /// parametros al buildear (halos invisibles). Aca se re-aplican en runtime.
    ///
    /// DEBE estar en un archivo con el mismo nombre que la clase: Unity necesita el
    /// MonoScript correspondiente para serializar la referencia en prefabs (si no,
    /// el componente queda como "Missing Script").
    /// </summary>
    [RequireComponent(typeof(MeshRenderer))]
    public class GlareBillboardInstance : MonoBehaviour
    {
        public Color srcColor = Color.white;
        public float srcEnergy = 1f;
        public Vector3 srcDir = Vector3.zero;
        public float seed = 0f;

        [Tooltip("Fuente 'al infinito' (sol): el encandilamiento NO atenua por distancia (1/d²). " +
                 "Las luces puntuales (faros/faroles) lo dejan en false. Solo lo usa DisabilityGlareController.")]
        public bool distanceInvariant = false;

        private void OnEnable() => Apply();

        public void Apply()
        {
            var mr = GetComponent<MeshRenderer>();
            if (mr == null) return;
            var mpb = new MaterialPropertyBlock();
            mr.GetPropertyBlock(mpb);
            mpb.SetColor("src_color", srcColor);
            mpb.SetFloat("src_energy", srcEnergy);
            mpb.SetVector("src_dir", new Vector4(srcDir.x, srcDir.y, srcDir.z, 0f));
            mpb.SetFloat("seed", seed);
            mr.SetPropertyBlock(mpb);
        }

#if UNITY_EDITOR
        // Ayuda visual SOLO en el editor: dibuja hacia donde "mira" la luz (srcDir),
        // para acomodar faros (adelante) y pilotos (atras) en los prefabs.
        private void OnDrawGizmos()
        {
            // Marcador de la fuente, con su color.
            Gizmos.color = srcColor;
            Gizmos.DrawWireSphere(transform.position, 0.10f);

            if (srcDir.sqrMagnitude > 0.0001f)
            {
                // src_dir es local (el shader lo transforma por el model matrix).
                Vector3 dir = transform.TransformDirection(srcDir.normalized);
                Vector3 from = transform.position;
                Vector3 to = from + dir * 0.7f;
                Gizmos.color = new Color(1f, 1f, 0.2f); // amarillo: direccion del haz
                Gizmos.DrawLine(from, to);
                Gizmos.DrawSphere(to, 0.05f);
                // punta de flecha
                Vector3 right = Vector3.Cross(dir, Mathf.Abs(dir.y) > 0.9f ? Vector3.right : Vector3.up).normalized;
                Vector3 up = Vector3.Cross(right, dir).normalized;
                Gizmos.DrawLine(to, to - dir * 0.18f + right * 0.09f);
                Gizmos.DrawLine(to, to - dir * 0.18f - right * 0.09f);
                Gizmos.DrawLine(to, to - dir * 0.18f + up * 0.09f);
                Gizmos.DrawLine(to, to - dir * 0.18f - up * 0.09f);
            }
            else
            {
                // Omnidireccional (sin direccion de haz).
                Gizmos.color = new Color(0.6f, 0.8f, 1f);
                Gizmos.DrawWireSphere(transform.position, 0.16f);
            }
        }
#endif
    }
}
