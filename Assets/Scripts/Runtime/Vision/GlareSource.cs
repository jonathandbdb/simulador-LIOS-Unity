using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

namespace Simulador.Vision
{
    /// <summary>
    /// Billboard de glare procedural anclado a una fuente de luz (farola, faro,
    /// piloto). Port de glare_source.gd. Material COMPARTIDO; parametros por
    /// instancia via GlareBillboardInstance (serializados, reaplicados en runtime).
    /// </summary>
    public static class GlareSource
    {
        private static Mesh _quad;
        private static Material _sharedMat;
        private static int _seedCounter;
        private static readonly List<GameObject> _all = new();

        private static Mesh Quad()
        {
            if (_quad != null) return _quad;
            _quad = new Mesh { name = "GlareQuad" };
            _quad.vertices = new[]
            {
                new Vector3(-1f, -1f, 0f), new Vector3(1f, -1f, 0f),
                new Vector3(1f, 1f, 0f),   new Vector3(-1f, 1f, 0f)
            };
            _quad.uv = new[] { new Vector2(0, 0), new Vector2(1, 0), new Vector2(1, 1), new Vector2(0, 1) };
            _quad.triangles = new[] { 0, 1, 2, 0, 2, 3 };
            _quad.bounds = new Bounds(Vector3.zero, Vector3.one * 200f);
            return _quad;
        }

        public static Material SharedMat()
        {
            if (_sharedMat != null) return _sharedMat;
            var sh = Shader.Find("Simulador/GlareBillboard");
            if (sh == null)
            {
                Debug.LogError("GlareSource: shader 'Simulador/GlareBillboard' NO encontrado.");
                return null;
            }
            _sharedMat = new Material(sh) { name = "GlareBillboardShared", enableInstancing = true };
            return _sharedMat;
        }

        public static GameObject Attach(Transform parent, Vector3 localPos, Color color,
            float energy, Vector3 beamDir = default)
        {
            var go = new GameObject("Glare");
            go.transform.SetParent(parent, false);
            go.transform.localPosition = localPos;

            go.AddComponent<MeshFilter>().sharedMesh = Quad();
            var mr = go.AddComponent<MeshRenderer>();
            mr.sharedMaterial = SharedMat();
            mr.shadowCastingMode = ShadowCastingMode.Off;
            mr.receiveShadows = false;
            mr.lightProbeUsage = LightProbeUsage.Off;
            mr.reflectionProbeUsage = ReflectionProbeUsage.Off;

            _seedCounter = (_seedCounter + 1) % 97;
            var inst = go.AddComponent<GlareBillboardInstance>();
            inst.srcColor = color;
            inst.srcEnergy = energy;
            inst.srcDir = beamDir;
            inst.seed = _seedCounter;
            inst.Apply();

            _all.Add(go);
            return go;
        }

        public static void SetAllVisible(bool visible)
        {
            for (int i = _all.Count - 1; i >= 0; i--)
            {
                if (_all[i] == null) { _all.RemoveAt(i); continue; }
                _all[i].SetActive(visible);
            }
        }
    }
}
