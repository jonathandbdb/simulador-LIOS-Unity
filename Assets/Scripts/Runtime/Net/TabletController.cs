using System.Collections.Generic;
using Newtonsoft.Json.Linq;
using UnityEngine;
using UnityEngine.UI;

namespace Simulador.Net
{
    /// <summary>
    /// App tablet (cliente de control). Descubre el visor por UDP, se conecta por WS,
    /// muestra el stream (lo que ve el paciente) y manda comandos: elegir lente por
    /// ojo, cambiar escena, astigmatismo. UI construida por codigo (overlay 2D).
    /// Port pragmatico de features/tablet/streaming_client.gd.
    /// </summary>
    public class TabletController : MonoBehaviour
    {
        private DiscoveryListener _disc;
        private WebSocketClient _ws;
        private string _eye = "both";
        private bool _connected;

        private Text _status;
        private RawImage _stream;
        private Texture2D _streamTex;
        private Text _eyeLabel;
        private RectTransform _lensPanel;
        private Font _font;
        private float _astigMag;
        private bool _astigOn;

        private void Start()
        {
            _font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
            _streamTex = new Texture2D(2, 2, TextureFormat.RGB24, false);
            BuildUI();

            _ws = new WebSocketClient();
            _ws.Connected += () => { _connected = true; SetStatus("Conectado al visor"); };
            _ws.Disconnected += () => { _connected = false; SetStatus("Desconectado"); };
            _ws.TextReceived += OnText;
            _ws.BinaryReceived += OnBinary;

            _disc = new DiscoveryListener();
            _disc.VisorDiscovered += OnDiscovered;
            _disc.Start();
            SetStatus("Buscando visor en la LAN...");
        }

        private void Update()
        {
            _disc?.PumpEvents();
            _ws?.PumpEvents();
        }

        private void OnDestroy() { _disc?.Stop(); _ws?.Close(); }

        private void OnDiscovered(string host)
        {
            if (_connected || (_ws != null && _ws.IsOpen)) return;
            SetStatus("Visor en " + host + ", conectando...");
            _ws.Connect(host, 9090);
        }

        // ---------------- Mensajes del visor ----------------
        private void OnText(string text)
        {
            JObject o;
            try { o = JObject.Parse(text); } catch { return; }
            string type = (string)o["type"] ?? "";
            if (type == "hello")
            {
                BuildLensButtons(o["lenses"] as JArray);
                UpdateCurrent(o["vision_state"] as JObject);
            }
            else if (type == "vision_state")
            {
                UpdateCurrent(o["vision_state"] as JObject);
            }
        }

        private void OnBinary(byte[] data)
        {
            if (data == null || data.Length < 2) return;
            char eye = (char)data[0];
            var jpg = new byte[data.Length - 1];
            System.Buffer.BlockCopy(data, 1, jpg, 0, jpg.Length);
            if (ImageConversion.LoadImage(_streamTex, jpg))
                _stream.texture = _streamTex;
            _eyeLabel.text = eye == 'L' ? "Ojo Izquierdo" : eye == 'R' ? "Ojo Derecho" : "Ambos ojos";
        }

        private void UpdateCurrent(JObject vs)
        {
            if (vs == null) return;
            string l = (string)(vs["left"]?["lens_id"]) ?? "-";
            string r = (string)(vs["right"]?["lens_id"]) ?? "-";
            SetStatus($"Conectado  |  OI: {l}   OD: {r}");
        }

        // ---------------- Comandos ----------------
        private void Send(JObject cmd) { if (_ws != null && _ws.IsOpen) _ws.SendText(cmd.ToString(Newtonsoft.Json.Formatting.None)); }
        private void ApplyLens(string id) => Send(new JObject { ["cmd"] = "apply_lens", ["lens_id"] = id, ["eye"] = _eye });
        private void LoadScenario(string id) => Send(new JObject { ["cmd"] = "load_scenario", ["scenario"] = id });
        private void SendAstig() => Send(new JObject { ["cmd"] = "set_astigmatism", ["enabled"] = _astigOn, ["magnitude"] = _astigMag, ["angle"] = 0f });

        // ---------------- UI ----------------
        private void SetStatus(string s) { if (_status) _status.text = s; }

        private void BuildUI()
        {
            var canvasGo = new GameObject("TabletCanvas", typeof(Canvas), typeof(CanvasScaler), typeof(GraphicRaycaster));
            var canvas = canvasGo.GetComponent<Canvas>(); canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            var scaler = canvasGo.GetComponent<CanvasScaler>(); scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize; scaler.referenceResolution = new Vector2(1280, 800);
            if (FindFirstObjectByType<UnityEngine.EventSystems.EventSystem>() == null)
                // InputSystemUIInputModule: el proyecto usa el nuevo Input System
                // (StandaloneInputModule lee el Input viejo y tira excepciones).
                new GameObject("EventSystem", typeof(UnityEngine.EventSystems.EventSystem),
                    typeof(UnityEngine.InputSystem.UI.InputSystemUIInputModule));

            // fondo
            var bg = NewImage(canvasGo.transform, new Color(0.08f, 0.09f, 0.12f, 1f));
            Stretch(bg.rectTransform, 0, 0, 0, 0);

            // status arriba
            _status = NewText(canvasGo.transform, "...", 26, TextAnchor.MiddleLeft);
            Anchor(_status.rectTransform, new Vector2(0, 1), new Vector2(1, 1), new Vector2(20, -40), new Vector2(-20, -10));

            // stream a la izquierda
            var streamGo = new GameObject("Stream", typeof(RawImage));
            streamGo.transform.SetParent(canvasGo.transform, false);
            _stream = streamGo.GetComponent<RawImage>(); _stream.color = Color.white;
            var srt = _stream.rectTransform; srt.anchorMin = new Vector2(0, 0); srt.anchorMax = new Vector2(0, 1);
            srt.pivot = new Vector2(0, 0.5f); srt.anchoredPosition = new Vector2(20, 0); srt.sizeDelta = new Vector2(560, -120);
            srt.offsetMin = new Vector2(20, 20); srt.offsetMax = new Vector2(580, -60);
            _eyeLabel = NewText(streamGo.transform, "", 24, TextAnchor.UpperCenter);
            Anchor(_eyeLabel.rectTransform, new Vector2(0, 1), new Vector2(1, 1), new Vector2(0, -34), new Vector2(0, -4));

            // panel de controles a la derecha
            var panel = new GameObject("Panel", typeof(RectTransform)).GetComponent<RectTransform>();
            panel.SetParent(canvasGo.transform, false);
            panel.anchorMin = new Vector2(0, 0); panel.anchorMax = new Vector2(1, 1);
            panel.offsetMin = new Vector2(600, 20); panel.offsetMax = new Vector2(-20, -60);

            float y = -10f;
            NewLabelAt(panel, "OJO:", ref y);
            var eyeRow = NewRow(panel, ref y);
            MakeButton(eyeRow, "Ambos", () => SetEye("both"));
            MakeButton(eyeRow, "OI", () => SetEye("left"));
            MakeButton(eyeRow, "OD", () => SetEye("right"));

            NewLabelAt(panel, "LENTE:", ref y);
            _lensPanel = NewRow(panel, ref y);  // se llena con el hello

            NewLabelAt(panel, "ESCENA:", ref y);
            var scRow = NewRow(panel, ref y);
            MakeButton(scRow, "Consultorio", () => LoadScenario("consultorio"));
            MakeButton(scRow, "Ruta noche", () => LoadScenario("ruta_noche"));

            NewLabelAt(panel, "ASTIGMATISMO:", ref y);
            var asRow = NewRow(panel, ref y);
            MakeButton(asRow, "On/Off", () => { _astigOn = !_astigOn; SendAstig(); });
            var sld = MakeSlider(asRow, v => { _astigMag = v * 50f; SendAstig(); });
        }

        private void SetEye(string e) { _eye = e; SetStatus("Ojo objetivo: " + (e == "both" ? "Ambos" : e == "left" ? "OI" : "OD")); }

        private void BuildLensButtons(JArray lenses)
        {
            for (int i = _lensPanel.childCount - 1; i >= 0; i--) Destroy(_lensPanel.GetChild(i).gameObject);
            if (lenses == null) return;
            foreach (var l in lenses)
            {
                string id = (string)l["id"]; string nombre = (string)l["nombre"] ?? id;
                MakeButton(_lensPanel, nombre, () => ApplyLens(id));
            }
        }

        // --- helpers UI ---
        private Image NewImage(Transform p, Color c) { var g = new GameObject("Img", typeof(Image)); g.transform.SetParent(p, false); var im = g.GetComponent<Image>(); im.color = c; return im; }
        private Text NewText(Transform p, string s, int size, TextAnchor a)
        {
            var g = new GameObject("Text", typeof(Text)); g.transform.SetParent(p, false);
            var t = g.GetComponent<Text>(); t.font = _font; t.fontSize = size; t.color = Color.white; t.alignment = a;
            t.horizontalOverflow = HorizontalWrapMode.Overflow; t.text = s; return t;
        }
        private void NewLabelAt(RectTransform panel, string s, ref float y)
        {
            var t = NewText(panel, s, 22, TextAnchor.MiddleLeft);
            var rt = t.rectTransform; rt.anchorMin = new Vector2(0, 1); rt.anchorMax = new Vector2(1, 1); rt.pivot = new Vector2(0.5f, 1);
            rt.anchoredPosition = new Vector2(0, y); rt.sizeDelta = new Vector2(0, 28); y -= 32;
        }
        private RectTransform NewRow(RectTransform panel, ref float y)
        {
            var go = new GameObject("Row", typeof(RectTransform), typeof(HorizontalLayoutGroup));
            var rt = go.GetComponent<RectTransform>(); rt.SetParent(panel, false);
            rt.anchorMin = new Vector2(0, 1); rt.anchorMax = new Vector2(1, 1); rt.pivot = new Vector2(0.5f, 1);
            rt.anchoredPosition = new Vector2(0, y); rt.sizeDelta = new Vector2(0, 56);
            var h = go.GetComponent<HorizontalLayoutGroup>(); h.spacing = 8; h.childForceExpandWidth = true; h.childForceExpandHeight = true; h.childControlWidth = true; h.childControlHeight = true;
            y -= 66; return rt;
        }
        private void MakeButton(RectTransform row, string label, UnityEngine.Events.UnityAction onClick)
        {
            var go = new GameObject("Btn", typeof(Image), typeof(Button)); go.transform.SetParent(row, false);
            go.GetComponent<Image>().color = new Color(0.16f, 0.4f, 0.5f, 1f);
            go.GetComponent<Button>().onClick.AddListener(onClick);
            var t = NewText(go.transform, label, 22, TextAnchor.MiddleCenter); Stretch(t.rectTransform, 4, 4, 4, 4);
        }
        private Slider MakeSlider(RectTransform row, UnityEngine.Events.UnityAction<float> onChange)
        {
            var go = new GameObject("Slider", typeof(Slider), typeof(Image)); go.transform.SetParent(row, false);
            go.GetComponent<Image>().color = new Color(0.2f, 0.2f, 0.25f, 1f);
            var s = go.GetComponent<Slider>(); s.minValue = 0; s.maxValue = 1; s.value = 0;
            var fillGo = new GameObject("Fill", typeof(Image)); fillGo.transform.SetParent(go.transform, false);
            fillGo.GetComponent<Image>().color = new Color(0.3f, 0.7f, 0.8f, 1f);
            var fr = fillGo.GetComponent<RectTransform>(); Stretch(fr, 0, 0, 0, 0); s.fillRect = fr;
            s.onValueChanged.AddListener(onChange); return s;
        }
        private static void Stretch(RectTransform rt, float l, float b, float r, float t) { rt.anchorMin = Vector2.zero; rt.anchorMax = Vector2.one; rt.offsetMin = new Vector2(l, b); rt.offsetMax = new Vector2(-r, -t); }
        private static void Anchor(RectTransform rt, Vector2 amin, Vector2 amax, Vector2 omin, Vector2 omax) { rt.anchorMin = amin; rt.anchorMax = amax; rt.offsetMin = omin; rt.offsetMax = omax; }
    }
}
