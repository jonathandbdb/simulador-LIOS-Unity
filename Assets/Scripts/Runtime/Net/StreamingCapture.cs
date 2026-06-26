using System;
using System.Threading;
using System.Threading.Tasks;
using Simulador.Data;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;

namespace Simulador.Vision
{
    /// <summary>
    /// Captura la vista del paciente y la envia como JPG por el WebSocketServer. Port
    /// de streaming_capture.gd. Header de 1 byte: 'B' ambos ojos, 'L' izq, 'R' der.
    /// La camara usa el mismo renderer URP, asi el stream YA muestra el post-proceso
    /// de vision + glare (lo que ve el paciente).
    ///
    /// Optimizado: render ON-DEMAND (SubmitRenderRequest, solo al enviar) + encode del
    /// JPG en HILO aparte (EncodeArrayToJPG, fuera del hilo principal; BroadcastBinary
    /// es thread-safe). En modo blend captura AMBOS ojos en cada tick (dos RT) para que
    /// cada ojo vaya a la tasa completa (antes alternaba L/R -> mitad de fps por ojo).
    /// El override _StreamForceEye fuerza el ojo en los shaders (la camara es mono).
    /// </summary>
    public class StreamingCapture : MonoBehaviour
    {
        private const int Width = 768;
        private const int Height = 576;
        private const float Hz = 20f;
        private const int JpgQuality = 85;
        private const byte HEADER_BOTH = 0x42, HEADER_LEFT = 0x4C, HEADER_RIGHT = 0x52;

        public Net.WebSocketServer Server;   // lo setea NetworkController
        public Transform headToFollow;       // XR Main Camera

        private Camera _cam;
        private Camera _headCam;
        private RenderTexture _rtL, _rtR;
        private float _timer;
        private volatile bool _busy;
        private int _pending;

        private void Start()
        {
            _rtL = NewRT("StreamRT_L");
            _rtR = NewRT("StreamRT_R");

            var go = new GameObject("StreamCaptureCam");
            go.transform.SetParent(transform, false);
            _cam = go.AddComponent<Camera>();
            _cam.fieldOfView = 75f;
            _cam.nearClipPlane = 0.05f;
            _cam.farClipPlane = 200f;
            _cam.aspect = (float)Width / Height;
            _cam.stereoTargetEye = StereoTargetEyeMask.None;
            _cam.enabled = false; // render on-demand (no cada frame)

            Shader.SetGlobalFloat("_StreamForceEye", 0f); // 0 = off (no forzar ojo)
        }

        private RenderTexture NewRT(string name)
        {
            var rt = new RenderTexture(Width, Height, 16, RenderTextureFormat.ARGB32) { name = name };
            rt.Create();
            return rt;
        }

        private void LateUpdate()
        {
            if (Server == null || headToFollow == null) return;

            _timer += Time.deltaTime;
            if (_timer < 1f / Hz) return;
            _timer = 0f;
            if (_busy || Server.OpenClientCount == 0) return;

            var dm = DataManager.Instance;
            bool blend = dm != null && dm.BlendModeEnabled;

            _cam.transform.SetPositionAndRotation(headToFollow.position, headToFollow.rotation);
            SyncFromHeadCamera();   // clear/fondo/culling del ojo (noche = fondo negro)

            if (blend)
            {
                _busy = true;
                _pending = 2;
                CaptureEye(1f, _rtL, HEADER_LEFT);   // ojo izquierdo
                CaptureEye(2f, _rtR, HEADER_RIGHT);  // ojo derecho
            }
            else
            {
                _busy = true;
                _pending = 1;
                CaptureEye(1f, _rtL, HEADER_BOTH);   // misma lente ambos ojos
            }
            Shader.SetGlobalFloat("_StreamForceEye", 0f); // off para el render de los ojos XR
        }

        // Fuerza el ojo, renderiza la captura a 'rt' (sincrono) y pide el readback.
        private void CaptureEye(float forcedEye, RenderTexture rt, byte header)
        {
            Shader.SetGlobalFloat("_StreamForceEye", forcedEye);
            RenderNow(rt);
            AsyncGPUReadback.Request(rt, 0, TextureFormat.RGBA32, req => OnReadback(req, header));
        }

        // El look de noche pone el fondo de la camara XR en negro solido; la camara de
        // captura es OTRA, asi que hay que copiarle clear/fondo/culling del ojo.
        private void SyncFromHeadCamera()
        {
            if (_headCam == null || _headCam.transform != headToFollow)
                _headCam = headToFollow.GetComponent<Camera>();
            if (_headCam == null) return;
            _cam.clearFlags = _headCam.clearFlags;
            _cam.backgroundColor = _headCam.backgroundColor;
            _cam.cullingMask = _headCam.cullingMask;
        }

        // Render on-demand con la API soportada de URP; fallback a Camera.Render().
        private void RenderNow(RenderTexture rt)
        {
            var request = new RenderPipeline.StandardRequest { destination = rt };
            if (RenderPipeline.SupportsRenderRequest(_cam, request))
                RenderPipeline.SubmitRenderRequest(_cam, request);
            else { _cam.targetTexture = rt; _cam.Render(); }
        }

        private void OnReadback(AsyncGPUReadbackRequest req, byte header)
        {
            if (req.hasError) { Done(); return; }
            byte[] raw = req.GetData<byte>().ToArray(); // copiar fuera del callback
            Task.Run(() =>
            {
                try
                {
                    byte[] jpg = ImageConversion.EncodeArrayToJPG(
                        raw, GraphicsFormat.R8G8B8A8_UNorm, (uint)Width, (uint)Height, 0, JpgQuality);
                    if (jpg != null && jpg.Length > 0)
                    {
                        var outb = new byte[jpg.Length + 1];
                        outb[0] = header;
                        Buffer.BlockCopy(jpg, 0, outb, 1, jpg.Length);
                        Server.BroadcastBinary(outb);
                    }
                }
                catch (Exception) { }
                finally { Done(); }
            });
        }

        // Libera el gate cuando terminaron todos los encodes del tick (1 o 2 ojos).
        private void Done()
        {
            if (Interlocked.Decrement(ref _pending) <= 0) _busy = false;
        }

        private void OnDestroy()
        {
            if (_rtL != null) _rtL.Release();
            if (_rtR != null) _rtR.Release();
        }
    }
}
