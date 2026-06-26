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
    }
}
