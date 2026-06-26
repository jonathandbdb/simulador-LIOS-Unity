using System;
using System.Threading.Tasks;
using Simulador.Data;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;

namespace Simulador.Vision
{
    /// <summary>
    /// Captura la vista del paciente a una RenderTexture y la envia como JPG por el
    /// WebSocketServer. Port de streaming_capture.gd. Header de 1 byte: 'B' (0x42)
    /// ambos ojos, 'L' (0x4C) izq, 'R' (0x52) der (modo blend alterna L/R). La camara
    /// usa el mismo renderer URP, asi el stream YA muestra el post-proceso de vision +
    /// glare (lo que ve el paciente).
    ///
    /// Optimizado para subir calidad SIN castigar el FPS del visor:
    ///  - Render ON-DEMAND (SubmitRenderRequest) solo al enviar, no cada frame.
    ///  - Encode del JPG en un HILO aparte (EncodeArrayToJPG, sin Texture2D ni hilo
    ///    principal). El WebSocketServer.BroadcastBinary es thread-safe (lock por
    ///    cliente), asi que se envia desde ese hilo.
    /// </summary>
    public class StreamingCapture : MonoBehaviour
    {
        // 4:3, calidad alta. Aspecto que la tablet respeta con AspectRatioFitter.
        private const int Width = 768;
        private const int Height = 576;
        private const float Hz = 20f;
        private const int JpgQuality = 85;
        private const byte HEADER_BOTH = 0x42, HEADER_LEFT = 0x4C, HEADER_RIGHT = 0x52;

        public Net.WebSocketServer Server;   // lo setea NetworkController
        public Transform headToFollow;       // XR Main Camera

        private Camera _cam;
        private Camera _headCam;   // la camara XR (mismo GameObject que headToFollow)
        private RenderTexture _rt;
        private float _timer;
        private volatile bool _busy;
        private bool _nextLeft = true;

        private void Start()
        {
            _rt = new RenderTexture(Width, Height, 16, RenderTextureFormat.ARGB32) { name = "StreamRT" };
            _rt.Create();

            var go = new GameObject("StreamCaptureCam");
            go.transform.SetParent(transform, false);
            _cam = go.AddComponent<Camera>();
            _cam.fieldOfView = 75f;
            _cam.nearClipPlane = 0.05f;
            _cam.farClipPlane = 200f;
            _cam.aspect = (float)Width / Height;
            _cam.targetTexture = _rt;
            _cam.stereoTargetEye = StereoTargetEyeMask.None;
            _cam.enabled = false; // render on-demand (no cada frame)
        }

        private void LateUpdate()
        {
            if (Server == null || headToFollow == null) return;

            _timer += Time.deltaTime;
            if (_timer < 1f / Hz) return;
            _timer = 0f;
            if (_busy || Server.OpenClientCount == 0) return;

            // seguir la pose de la cabeza y renderizar la captura ahora mismo
            _cam.transform.SetPositionAndRotation(headToFollow.position, headToFollow.rotation);
            SyncFromHeadCamera();   // copiar clear/fondo/culling del ojo (noche = fondo negro)
            RenderNow();

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

        // El look de noche se logra poniendo el fondo de la camara XR en negro solido
        // (ScenarioManager.ApplyNight). La camara de captura es OTRA camara, asi que hay
        // que copiarle el clear/fondo/culling del ojo, si no renderiza el skybox de dia.
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
        private void RenderNow()
        {
            var request = new RenderPipeline.StandardRequest { destination = _rt };
            if (RenderPipeline.SupportsRenderRequest(_cam, request))
                RenderPipeline.SubmitRenderRequest(_cam, request);
            else
                _cam.Render();
        }

        private void OnReadback(AsyncGPUReadbackRequest req, byte header)
        {
            if (req.hasError) { _busy = false; return; }
            // Copiar los bytes crudos fuera del callback (la NativeArray se libera).
            byte[] raw = req.GetData<byte>().ToArray();
            // Encode en hilo aparte: no toca el hilo principal (no afecta FPS del visor).
            Task.Run(() =>
            {
                try
                {
                    // El readback viene bottom-up (convencion de textura de Unity);
                    // EncodeArrayToJPG espera top-down -> invertir filas para que el
                    // JPG salga derecho (igual que el path viejo con Texture2D).
                    int rowBytes = Width * 4;
                    var flipped = new byte[raw.Length];
                    for (int y = 0; y < Height; y++)
                        Buffer.BlockCopy(raw, y * rowBytes, flipped, (Height - 1 - y) * rowBytes, rowBytes);

                    byte[] jpg = ImageConversion.EncodeArrayToJPG(
                        flipped, GraphicsFormat.R8G8B8A8_UNorm, (uint)Width, (uint)Height, 0, JpgQuality);
                    if (jpg != null && jpg.Length > 0)
                    {
                        var outb = new byte[jpg.Length + 1];
                        outb[0] = header;
                        Buffer.BlockCopy(jpg, 0, outb, 1, jpg.Length);
                        Server.BroadcastBinary(outb);
                    }
                }
                catch (Exception) { }
                finally { _busy = false; }
            });
        }

        private void OnDestroy()
        {
            if (_rt != null) _rt.Release();
        }
    }
}
