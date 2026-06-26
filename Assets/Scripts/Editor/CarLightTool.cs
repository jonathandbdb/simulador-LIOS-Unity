using System.IO;
using System.Text;
using Simulador.Vision;
using UnityEditor;
using UnityEngine;

namespace Simulador.EditorTools
{
    /// <summary>
    /// Repara los prefabs de auto (Assets/Prefabs/Cars): quita scripts faltantes en
    /// los marcadores de luz y les agrega un GlareBillboardInstance valido. Se ejecuta
    /// desde el assembly real (no el codigo dinamico del MCP), para que la referencia
    /// al script serialice bien (evita el "Missing Script").
    /// </summary>
    public static class CarLightTool
    {
        [MenuItem("Simulador/Fix Car Light Prefabs")]
        public static void FixMenu() => Debug.Log("CarLightTool: " + FixCarPrefabs());

        [MenuItem("Simulador/Build Street Lamp Prefab")]
        public static void BuildLampMenu() => Debug.Log("Lamp: " + BuildStreetLampPrefab());

        /// <summary>
        /// Crea un prefab de FAROLA prolija (poste + brazo + luminaria + spot + halo).
        /// Convencion: el brazo/luz se extiende hacia +X (la calle esta del lado +X).
        /// El GlareBillboardInstance se agrega desde el assembly real -> serializa OK.
        /// </summary>
        // Carga un material asset, o lo crea (URP/Lit) y aplica el setup. Materiales
        // ASSET (no runtime) para que el prefab los serialice (si no -> magenta).
        private static Material GetOrCreateMat(string path, System.Action<Material> setup)
        {
            var m = AssetDatabase.LoadAssetAtPath<Material>(path);
            if (m == null)
            {
                m = new Material(Shader.Find("Universal Render Pipeline/Lit"));
                AssetDatabase.CreateAsset(m, path);
            }
            setup(m);
            EditorUtility.SetDirty(m);
            return m;
        }

        public static string BuildStreetLampPrefab()
        {
            var warm = new Color(1f, 0.96f, 0.85f);
            // Materiales como ASSET (los materiales creados en runtime NO serializan en prefabs -> magenta).
            var dark = GetOrCreateMat("Assets/Materials/LampMetal.mat", m =>
            {
                m.SetColor("_BaseColor", new Color(0.07f, 0.07f, 0.09f));
                m.SetFloat("_Smoothness", 0.2f);
            });

            var root = new GameObject("Lamp_Street");

            // Poste vertical
            var pole = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
            pole.name = "Pole"; pole.transform.SetParent(root.transform, false);
            pole.transform.localPosition = new Vector3(0f, 3.5f, 0f);
            pole.transform.localScale = new Vector3(0.16f, 3.5f, 0.16f);
            Object.DestroyImmediate(pole.GetComponent<Collider>());
            pole.GetComponent<MeshRenderer>().sharedMaterial = dark;

            // Brazo horizontal hacia +X
            var arm = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
            arm.name = "Arm"; arm.transform.SetParent(root.transform, false);
            arm.transform.localPosition = new Vector3(1.0f, 6.9f, 0f);
            arm.transform.localRotation = Quaternion.Euler(0f, 0f, 90f);
            arm.transform.localScale = new Vector3(0.07f, 1.1f, 0.07f);
            Object.DestroyImmediate(arm.GetComponent<Collider>());
            arm.GetComponent<MeshRenderer>().sharedMaterial = dark;

            // Luminaria (cabeza) al final del brazo, con cara emisiva
            var lum = GameObject.CreatePrimitive(PrimitiveType.Cube);
            lum.name = "Luminaire"; lum.transform.SetParent(root.transform, false);
            lum.transform.localPosition = new Vector3(2.0f, 6.78f, 0f);
            lum.transform.localRotation = Quaternion.Euler(12f, 0f, 0f);
            lum.transform.localScale = new Vector3(0.5f, 0.16f, 0.32f);
            Object.DestroyImmediate(lum.GetComponent<Collider>());
            var lm = GetOrCreateMat("Assets/Materials/LampEmissive.mat", m =>
            {
                m.SetColor("_BaseColor", new Color(0.1f, 0.1f, 0.1f));
                m.EnableKeyword("_EMISSION");
                m.globalIlluminationFlags = MaterialGlobalIlluminationFlags.RealtimeEmissive;
                m.SetColor("_EmissionColor", warm * 5f);
            });
            lum.GetComponent<MeshRenderer>().sharedMaterial = lm;

            // Spot hacia abajo (ilumina la calle)
            var spot = new GameObject("Spot");
            spot.transform.SetParent(root.transform, false);
            spot.transform.localPosition = new Vector3(2.0f, 6.7f, 0f);
            spot.transform.localRotation = Quaternion.Euler(90f, 0f, 0f);
            var l = spot.AddComponent<Light>();
            l.type = LightType.Spot; l.color = warm; l.intensity = 60f; l.range = 14f; l.spotAngle = 70f; l.innerSpotAngle = 30f; l.shadows = LightShadows.None;

            // Halo (GlareBillboardInstance) en la luminaria
            var glare = new GameObject("Glare");
            glare.transform.SetParent(root.transform, false);
            glare.transform.localPosition = new Vector3(2.0f, 6.7f, 0f);
            glare.AddComponent<MeshFilter>().sharedMesh = AssetDatabase.LoadAssetAtPath<Mesh>("Assets/Meshes/GlareQuad.asset");
            var gmr = glare.AddComponent<MeshRenderer>();
            gmr.sharedMaterial = AssetDatabase.LoadAssetAtPath<Material>("Assets/Materials/GlareBillboard.mat");
            gmr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            gmr.lightProbeUsage = UnityEngine.Rendering.LightProbeUsage.Off;
            gmr.reflectionProbeUsage = UnityEngine.Rendering.ReflectionProbeUsage.Off;
            var inst = glare.AddComponent<GlareBillboardInstance>();
            inst.srcColor = warm; inst.srcEnergy = 1.0f; inst.srcDir = Vector3.zero; inst.seed = Random.Range(0, 97);

            const string path = "Assets/Prefabs/Lamp_Street.prefab";
            PrefabUtility.SaveAsPrefabAsset(root, path);
            Object.DestroyImmediate(root);
            AssetDatabase.SaveAssets();
            return "Lamp_Street.prefab creado";
        }

        public static string FixCarPrefabs()
        {
            var sb = new StringBuilder();
            foreach (var g in AssetDatabase.FindAssets("t:Prefab", new[] { "Assets/Prefabs/Cars" }))
            {
                var path = AssetDatabase.GUIDToAssetPath(g);
                var root = PrefabUtility.LoadPrefabContents(path);
                foreach (var n in new[] { "Headlights", "Taillights" })
                {
                    var mk = root.transform.Find(n);
                    if (mk == null) continue;
                    GameObjectUtility.RemoveMonoBehavioursWithMissingScript(mk.gameObject);
                    var inst = mk.gameObject.AddComponent<GlareBillboardInstance>();
                    bool head = n == "Headlights";
                    inst.srcColor = head ? new Color(1f, 0.96f, 0.85f) : new Color(1f, 0.08f, 0.05f);
                    inst.srcEnergy = head ? 1.0f : 0.8f;
                    inst.srcDir = head ? Vector3.forward : Vector3.back;
                    inst.seed = Random.Range(0, 97);
                }
                PrefabUtility.SaveAsPrefabAsset(root, path);
                PrefabUtility.UnloadPrefabContents(root);
                sb.Append(Path.GetFileNameWithoutExtension(path)).Append(" ");
            }
            AssetDatabase.SaveAssets();
            return sb.ToString();
        }
    }
}
