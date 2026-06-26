using System;
using Newtonsoft.Json.Linq;
using Simulador.Data;
using Simulador.Vision;
using UnityEngine;

namespace Simulador.Net
{
    /// <summary>
    /// Orquesta el networking del visor (F6). Levanta el WebSocketServer (:9090), el
    /// DiscoveryBeacon (:9091) y la captura de streaming. Al conectarse un cliente le
    /// manda el "hello" (catalogo + estado), procesa comandos (apply_lens,
    /// override_params, set_astigmatism, load_scenario) y reenvia el vision_state al
    /// cambiar. Port de la parte de red de main.gd + streaming_server.gd.
    /// </summary>
    public class NetworkController : MonoBehaviour
    {
        public static NetworkController Instance { get; private set; }

        private WebSocketServer _server;
        private DiscoveryBeacon _beacon;
        private StreamingCapture _capture;
        private ScenarioManager _scenarios;
        private GlareController _glare;
        private DataManager _dm;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void Bootstrap()
        {
            if (Instance != null) return;
            // En la app tablet (escena con TabletController) NO se levanta el server:
            // la tablet es cliente.
            if (FindFirstObjectByType<TabletController>() != null) return;
            var go = new GameObject("NetworkController");
            go.AddComponent<NetworkController>();
            DontDestroyOnLoad(go);
        }

        private void Awake()
        {
            if (Instance != null && Instance != this) { Destroy(gameObject); return; }
            Instance = this;
        }

        private void Start()
        {
            _server = new WebSocketServer();
            _server.ClientConnected += OnClientConnected;
            _server.TextReceived += OnTextReceived;
            _server.ClientDisconnected += id => Debug.Log($"Net: cliente {id} desconectado");
            _server.Start(9090);

            _beacon = new DiscoveryBeacon();
            _beacon.Start(SystemInfo.deviceUniqueIdentifier);

            // Captura de streaming (sigue la camara XR)
            var cam = Camera.main;
            _capture = gameObject.AddComponent<StreamingCapture>();
            _capture.Server = _server;
            if (cam != null) _capture.headToFollow = cam.transform;

            _dm = DataManager.Instance;
            if (_dm != null) _dm.VisionStateChanged += OnVisionStateChanged;
        }

        private void Update()
        {
            _server?.PumpEvents();
            double unix = (DateTime.UtcNow - new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc)).TotalSeconds;
            _beacon?.Tick(Time.deltaTime, unix);
            // refs que pueden aparecer tras cargar escena
            if (_scenarios == null) _scenarios = FindFirstObjectByType<ScenarioManager>();
            if (_glare == null) _glare = FindFirstObjectByType<GlareController>();
            if (_capture != null && _capture.headToFollow == null && Camera.main != null)
                _capture.headToFollow = Camera.main.transform;
        }

        private void OnDestroy()
        {
            _server?.Stop();
            _beacon?.Stop();
        }

        // ---------------- Mensajes salientes ----------------
        private void OnClientConnected(int id)
        {
            Debug.Log($"Net: cliente {id} conectado, enviando hello.");
            _server.SendTextTo(id, BuildHello());
        }

        private void OnVisionStateChanged(string eye, EyeState state)
        {
            if (_server == null || _server.OpenClientCount == 0) return;
            var msg = new JObject { ["type"] = "vision_state", ["vision_state"] = BuildVisionState() };
            _server.BroadcastText(msg.ToString(Newtonsoft.Json.Formatting.None));
        }

        private string BuildHello()
        {
            var dm = DataManager.Instance;
            var hello = new JObject
            {
                ["type"] = "hello",
                ["catalog_version"] = dm?.Catalog?.Version ?? "?",
                ["lenses"] = dm?.Catalog != null ? JArray.FromObject(dm.Catalog.Catalogo) : new JArray(),
                ["vision_state"] = BuildVisionState(),
                ["scenario"] = _scenarios != null ? _scenarios.Current : "ruta_noche",
                ["scenarios"] = new JArray("consultorio", "ruta_noche"),
            };
            return hello.ToString(Newtonsoft.Json.Formatting.None);
        }

        private JObject BuildVisionState()
        {
            var dm = DataManager.Instance;
            var vs = new JObject();
            if (dm != null)
            {
                vs["left"] = EyeToJson(dm.Left);
                vs["right"] = EyeToJson(dm.Right);
            }
            return vs;
        }

        private static JObject EyeToJson(EyeState e)
        {
            var o = new JObject { ["lens_id"] = e?.LensId ?? "" };
            if (e != null) foreach (var kv in e.Params) o[kv.Key] = kv.Value;
            return o;
        }

        // ---------------- Comandos entrantes ----------------
        private void OnTextReceived(int id, string text)
        {
            JObject cmd;
            try { cmd = JObject.Parse(text); }
            catch (Exception) { Debug.LogWarning("Net: comando no-JSON: " + text); return; }
            string type = (string)cmd["cmd"] ?? "";
            var dm = DataManager.Instance;
            switch (type)
            {
                case "apply_lens":
                    dm?.ApplyLens((string)cmd["lens_id"] ?? "", (string)cmd["eye"] ?? "both");
                    break;
                case "override_params":
                    var p = cmd["params"] as JObject;
                    if (p != null)
                    {
                        var dict = new System.Collections.Generic.Dictionary<string, float>();
                        foreach (var kv in p) dict[kv.Key] = kv.Value.Value<float>();
                        dm?.OverrideParams(dict, (string)cmd["eye"] ?? "both");
                    }
                    break;
                case "set_astigmatism":
                    if (_glare != null)
                        _glare.SetAstigmatism((bool?)cmd["enabled"] ?? false,
                            (float?)cmd["magnitude"] ?? 0f, (float?)cmd["angle"] ?? 0f);
                    break;
                case "load_scenario":
                    if (_scenarios != null) _scenarios.SwitchTo((string)cmd["scenario"] ?? "");
                    break;
                default:
                    Debug.LogWarning("Net: comando desconocido: " + type);
                    break;
            }
        }
    }
}
