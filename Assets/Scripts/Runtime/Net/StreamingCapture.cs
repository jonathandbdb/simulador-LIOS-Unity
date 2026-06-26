using System;
using Simulador.Data;
using UnityEngine;
using UnityEngine.Rendering;

namespace Simulador.Vision
{
    /// <summary>
    /// Captura la vista a una RenderTexture 256x256 y la envia como JPG (~10 Hz) por
    /// el WebSocketServer. Port de streaming_capture.gd. Header de 1 byte:
    /// 'B' (0x42) ambos ojos, 'L' (0x4C) izq, 'R' (0x52) der (modo blend alterna L/R).
    /// La camara de captura usa el mismo renderer URP, asi el stream YA muestra el
    /// post-proceso de vision + glare (lo que ve el paciente).
    /// </summary>
    public class StreamingCapture : MonoBehaviour
    {
        private const int Size = 256;
        private const float Hz = 10f;
        private const int JpgQuality = 55;
        private const byte HEADER_BOTH = 0x42, HEADER_LEFT = 0x4C, HEADER_RIGHT = 0x52;

        public Net.WebSocketServer Server;   // lo setea NetworkController
        public Transform headToFollow;       // XR Main Camera

        private Camera _cam;
        private RenderTexture _rt;
        private Texture2D _readTex;
        private float _timer;
        private bool _busy;
        private bool _nextLeft = true;

        private void Start()
        {
            _rt = new RenderTexture(Size, Size, 16, RenderTextureFormat.ARGB32) { name = "StreamRT" };
            _rt.Create();
            _readTex = new Texture2D(Size, Size, TextureFormat.RGBA32, false);

            var go = new GameObject("StreamCaptureCam");
            go.transform.SetParent(transform, false);
            _cam = go.AddComponent<Camera>();
            _cam.fieldOfView = 75f;
            _cam.nearClipPlane = 0.05f;
            _cam.farClipPlane = 200f;
            _cam.targetTexture = _rt;
            _cam.stereoTargetEye = StereoTargetEyeMask.None;
            _cam.depth = -10; // render antes que la camara XR
        }

        private void LateUpdate()
        {
            if (Server == null || headToFollow == null) return;
            // seguir la pose de la cabeza
            _cam.transform.SetPositionAndRotation(headToFollow.position, headToFollow.rotation);

            _timer += Time.deltaTime;
            if (_timer < 1f / Hz) return;
            _timer = 0f;
            if (_busy || Server.OpenClientCount == 0) return;

            byte header = HEADER_BOTH;
            var dm = DataManager.Instance;
            if (dm != null && dm.BlendModeEnabled)
            {
                header = _nextLeft ? HEADER_LEFT : HEADER_RIGHT;
                _nextLeft = !_nextLeft;
            }

            _busy = true;
            AsyncGPUReadback.Request(_rt, 0, TextureFormat.RGBA32, req => OnReadback(req, header));
        }

        private void OnReadback(AsyncGPUReadbackRequest req, byte header)
        {
            try
            {
                if (req.hasError) return;
                _readTex.LoadRawTextureData(req.GetData<byte>());
                _readTex.Apply(false);
                var jpg = _readTex.EncodeToJPG(JpgQuality);
                var outb = new byte[jpg.Length + 1];
                outb[0] = header;
                Buffer.BlockCopy(jpg, 0, outb, 1, jpg.Length);
                Server.BroadcastBinary(outb);
            }
            catch (Exception) { }
            finally { _busy = false; }
        }

        private void OnDestroy()
        {
            if (_rt != null) _rt.Release();
        }
    }
}
