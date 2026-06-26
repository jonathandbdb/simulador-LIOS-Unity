using System.Collections.Generic;
using System.Globalization;
using Newtonsoft.Json.Linq;
using Simulador.Tablet;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace Simulador.Net
{
    /// <summary>
    /// App tablet (cliente de control del consultorio). Descubre el visor por UDP,
    /// se conecta por WS (:9090), muestra el stream por ojo y manda comandos
    /// (apply_lens, override_params, set_astigmatism, load_scenario). Replica fiel
    /// de features/tablet/streaming_client.gd: pantalla de conexion + pantalla
    /// principal (header con escenarios/tema/estado, stream con split en modo blend,
    /// cards de ojo/lentes/ajuste fino/astigmatismo) con tema oscuro/claro Inter.
    ///
    /// La UI se arma por codigo con TabletUiKit (uGUI + TextMeshPro). El networking
    /// (WebSocketClient + DiscoveryListener) se reusa sin cambios.
    /// </summary>
    public class TabletController : MonoBehaviour
    {
        private const int WsPort = 9090;
        private const float HostTimeout = 6f; // s: hosts vistos hace mas de esto se descartan

        // --- Red ---
        private DiscoveryListener _disc;
        private WebSocketClient _ws;
        private bool _connecting, _sessionActive, _manualDisconnect;
        private string _currentHost = "";
        private readonly Dictionary<string, float> _seenHosts = new();

        // --- Tema / kit ---
        private TabletUiKit _kit;
        private bool _isDark = true;
        private string PrefsPath => Application.persistentDataPath + "/ui_prefs.cfg";

        // --- Pantallas ---
        private GameObject _connectScreen, _mainScreen;
        private RectTransform _discoveredList, _advancedBox;
        private TMP_Text _connectStatus;
        private TMP_InputField _hostEdit;

        // --- Header ---
        private RectTransform _scenarioList;
        private TabletButton _themeToggle;
        private Image _statusDot;
        private TMP_Text _statusText;

        // --- Stream ---
        private RawImage _streamLeft, _streamRight;
        private TMP_Text _leftEyeLabel, _rightEyeLabel;
        private GameObject _rightEyePane;
        private Texture2D _texLeft, _texRight;

        // --- Selector de ojo ---
        private TabletButton _eyeBoth, _eyeOd, _eyeOi;
        private string _selectedEye = "both";

        // --- Lentes ---
        private RectTransform _lensList;
        private readonly Dictionary<string, LensCardView> _lensCards = new();

        // --- Ajuste fino ---
        private RectTransform _paramsContent, _paramsList;
        private TMP_Text _editingLensLabel;
        private TabletButton _resetButton;
        private string _editingLensId = "";
        private readonly Dictionary<string, ParamRowView> _paramRows = new();
        private readonly Dictionary<string, float> _paramDefaults = new();

        // --- Astigmatismo ---
        private RectTransform _astigContent;
        private TabletButton _astigEnabled;
        private Slider _magSlider, _angleSlider;
        private TMP_Text _magValue, _angleValue;

        // --- Footer ---
        private TMP_Text _footer;
        private int _framesReceived, _framesLastTick;
        private long _bytesReceived;
        private float _footerTimer, _discoveryTimer;

        // --- Catalogo / estado ---
        private readonly Dictionary<string, JObject> _lensesById = new();
        private JObject _visionState = new();
        private List<string> _scenarios = new();
        private string _currentScenario = "";

        // ============================================================
        private void Start()
        {
            var regular = Resources.Load<TMP_FontAsset>("TabletFonts/Inter-Regular SDF");
            var semibold = Resources.Load<TMP_FontAsset>("TabletFonts/Inter-SemiBold SDF");
            _isDark = LoadThemePref();
            _kit = new TabletUiKit(TabletPalette.For(_isDark), regular, semibold);

            _texLeft = new Texture2D(2, 2, TextureFormat.RGB24, false);
            _texRight = new Texture2D(2, 2, TextureFormat.RGB24, false);

            BuildUI();
            ApplyTheme(_isDark);

            _ws = new WebSocketClient();
            _ws.Connected += OnWsConnected;
            _ws.Disconnected += OnWsDisconnected;
            _ws.TextReceived += OnText;
            _ws.BinaryReceived += OnBinary;

            _disc = new DiscoveryListener();
            _disc.VisorDiscovered += OnVisorDiscovered;
            _disc.Start();

            ShowConnectScreen("Buscando visores en la red...");
        }

        private void Update()
        {
            _disc?.PumpEvents();
            _ws?.PumpEvents();

            _discoveryTimer += Time.deltaTime;
            if (_discoveryTimer >= 1f) { _discoveryTimer = 0f; RefreshDiscovered(); }

            _footerTimer += Time.deltaTime;
            if (_footerTimer >= 1f) { _footerTimer = 0f; UpdateFooter(); }
        }

        private void OnDestroy() { _disc?.Stop(); _ws?.Close(); }

        // ============================================================
        // Tema claro / oscuro
        // ============================================================
        private void ApplyTheme(bool dark)
        {
            _isDark = dark;
            _kit.Apply(TabletPalette.For(dark));
            if (_themeToggle?.Label != null)
                _themeToggle.Label.text = dark ? "Modo claro" : "Modo oscuro";
            if (_sessionActive) SetBadge(_kit.P.Ok, ConnectedBadgeText());
            SaveThemePref();
        }

        private string ConnectedBadgeText() =>
            string.IsNullOrEmpty(_currentHost) ? "Conectado" : "Conectado · " + _currentHost;

        private bool LoadThemePref()
        {
            try
            {
                if (System.IO.File.Exists(PrefsPath))
                {
                    foreach (var line in System.IO.File.ReadAllLines(PrefsPath))
                        if (line.Trim().StartsWith("dark_mode"))
                            return line.Contains("true") || line.Contains("1");
                }
            }
            catch { }
            return true;
        }

        private void SaveThemePref()
        {
            try { System.IO.File.WriteAllText(PrefsPath, "[ui]\ndark_mode=" + (_isDark ? "true" : "false") + "\n"); }
            catch { }
        }

        // ============================================================
        // Pantallas
        // ============================================================
        private void ShowConnectScreen(string message, bool isError = false)
        {
            _connectScreen.SetActive(true);
            _mainScreen.SetActive(false);
            SetConnectStatus(message, isError);
        }

        private void ShowMainScreen()
        {
            _connectScreen.SetActive(false);
            _mainScreen.SetActive(true);
            SetBadge(_kit.P.Ok, ConnectedBadgeText());
        }

        private void SetConnectStatus(string text, bool isError = false)
        {
            if (_connectStatus == null) return;
            _connectStatus.text = text;
            _connectStatus.color = isError ? _kit.P.Error : _kit.P.TextHint;
        }

        private void SetBadge(Color color, string text)
        {
            if (_statusDot != null) _statusDot.color = color;
            if (_statusText != null) _statusText.text = text;
        }

        // ============================================================
        // Discovery + conexion
        // ============================================================
        private void OnVisorDiscovered(string host) => _seenHosts[host] = Time.time;

        private void RefreshDiscovered()
        {
            if (_connectScreen == null || !_connectScreen.activeSelf) return;

            // Podar hosts viejos.
            var stale = new List<string>();
            foreach (var kv in _seenHosts) if (Time.time - kv.Value > HostTimeout) stale.Add(kv.Key);
            foreach (var h in stale) _seenHosts.Remove(h);

            for (int i = _discoveredList.childCount - 1; i >= 0; i--)
                Destroy(_discoveredList.GetChild(i).gameObject);

            if (_seenHosts.Count == 0)
            {
                if (!_connecting) SetConnectStatus("Buscando visores en la red...");
                return;
            }
            foreach (var host in _seenHosts.Keys)
            {
                string h = host;
                var btn = _kit.Button(_discoveredList, "Visor Quest  ·  " + h, BtnStyle.Segment, false, 64, 16);
                btn.OnClick = () => { _hostEdit.text = h; OnConnectPressed(); };
            }
            if (!_connecting) SetConnectStatus("Tocá un visor para conectarte.");
        }

        private void OnConnectPressed()
        {
            string host = _hostEdit.text.Trim();
            if (string.IsNullOrEmpty(host))
            {
                foreach (var h in _seenHosts.Keys) { host = h; break; }
            }
            if (string.IsNullOrEmpty(host))
            {
                SetConnectStatus("Ingresá la IP del visor o tocá uno detectado.", true);
                return;
            }
            _currentHost = host;
            SetConnectStatus("Conectando a " + host + "...");
            _connecting = true;
            _ws.Connect(host, WsPort);
        }

        private void OnDisconnectPressed() { _manualDisconnect = true; _ws.Close(); }

        private void OnWsConnected()
        {
            _connecting = false;
            SetConnectStatus("Conectado. Esperando catálogo del visor...");
        }

        private void OnWsDisconnected()
        {
            if (_connecting)
            {
                _connecting = false;
                ShowConnectScreen("No se pudo conectar con " + _currentHost + ".", true);
            }
            else if (_sessionActive)
            {
                _sessionActive = false;
                if (_manualDisconnect) ShowConnectScreen("Sesión finalizada.");
                else ShowConnectScreen("Se perdió la conexión con el visor.", true);
            }
            _manualDisconnect = false;
        }

        // ============================================================
        // Protocolo
        // ============================================================
        private void OnText(string text)
        {
            JObject o;
            try { o = JObject.Parse(text); } catch { return; }
            string type = (string)o["type"] ?? "";
            if (type == "hello")
            {
                _lensesById.Clear();
                var lenses = o["lenses"] as JArray ?? new JArray();
                foreach (var l in lenses)
                    if (l is JObject lo && lo["id"] != null)
                        _lensesById[(string)lo["id"]] = lo;
                _visionState = o["vision_state"] as JObject ?? new JObject();
                _scenarios = new List<string>();
                foreach (var s in (o["scenarios"] as JArray ?? new JArray())) _scenarios.Add((string)s);
                _currentScenario = (string)o["scenario"] ?? "";
                RebuildLensList(lenses);
                RebuildScenarioList();
                RefreshVisionUI();
                _sessionActive = true;
                ShowMainScreen();
            }
            else if (type == "vision_state")
            {
                _visionState = o["vision_state"] as JObject ?? new JObject();
                RefreshVisionUI();
                SyncParamRowsFromState();
            }
        }

        private void OnBinary(byte[] data)
        {
            if (data == null || data.Length < 2) return;
            char eye = (char)data[0];
            var jpg = new byte[data.Length - 1];
            System.Buffer.BlockCopy(data, 1, jpg, 0, jpg.Length);
            if (eye == 'R')
            {
                if (ImageConversion.LoadImage(_texRight, jpg)) { _streamRight.texture = _texRight; _streamRight.color = Color.white; }
            }
            else if (eye == 'L')
            {
                if (ImageConversion.LoadImage(_texLeft, jpg)) { _streamLeft.texture = _texLeft; _streamLeft.color = Color.white; }
            }
            else // 'B' o desconocido -> mismo frame en ambos paneles
            {
                if (ImageConversion.LoadImage(_texLeft, jpg))
                {
                    _streamLeft.texture = _texLeft; _streamLeft.color = Color.white;
                    if (ImageConversion.LoadImage(_texRight, jpg)) { _streamRight.texture = _texRight; _streamRight.color = Color.white; }
                }
            }
            _framesReceived++;
            _bytesReceived += data.Length;
        }

        // ============================================================
        // Lentes
        // ============================================================
        private string LensDisplayName(string lensId)
        {
            if (_lensesById.TryGetValue(lensId, out var l))
            {
                string nombre = (string)l["nombre"];
                if (!string.IsNullOrEmpty(nombre)) return nombre;
            }
            return lensId;
        }

        private void RebuildLensList(JArray lenses)
        {
            for (int i = _lensList.childCount - 1; i >= 0; i--) Destroy(_lensList.GetChild(i).gameObject);
            _lensCards.Clear();
            foreach (var l in lenses)
            {
                if (!(l is JObject lo)) continue;
                string id = (string)lo["id"] ?? "?";
                var card = LensCardView.Create(_kit, _lensList, id, (string)lo["nombre"],
                    (string)lo["descripcion"], OnLensSelected);
                _lensCards[id] = card;
            }
        }

        private void OnLensSelected(string lensId)
        {
            if (_ws == null || !_ws.IsOpen) { SetBadge(_kit.P.Warn, "Sin conexión"); return; }
            SendCmd(new JObject { ["cmd"] = "apply_lens", ["lens_id"] = lensId, ["eye"] = _selectedEye });
            // Actualizacion optimista del estado local.
            if (_selectedEye == "left" || _selectedEye == "both")
                _visionState["left"] = new JObject { ["lens_id"] = lensId };
            if (_selectedEye == "right" || _selectedEye == "both")
                _visionState["right"] = new JObject { ["lens_id"] = lensId };
            RefreshVisionUI();
            BuildParamsEditor(lensId);
        }

        private void RefreshVisionUI()
        {
            string leftId = (string)(_visionState["left"]?["lens_id"]) ?? "";
            string rightId = (string)(_visionState["right"]?["lens_id"]) ?? "";
            bool isBlend = leftId != rightId;

            foreach (var kv in _lensCards)
                kv.Value.SetEyeState(kv.Key == rightId, kv.Key == leftId);

            if (isBlend)
            {
                _rightEyePane.SetActive(true);
                _leftEyeLabel.text = "OI · " + LensDisplayName(leftId);
                _rightEyeLabel.text = "OD · " + LensDisplayName(rightId);
            }
            else
            {
                _rightEyePane.SetActive(false);
                _leftEyeLabel.text = string.IsNullOrEmpty(leftId)
                    ? "Ambos ojos" : "Ambos ojos · " + LensDisplayName(leftId);
            }
        }

        // ============================================================
        // Ajuste fino de parametros
        // ============================================================
        private void BuildParamsEditor(string lensId)
        {
            _editingLensId = lensId;
            _paramRows.Clear();
            _paramDefaults.Clear();
            for (int i = _paramsList.childCount - 1; i >= 0; i--) Destroy(_paramsList.GetChild(i).gameObject);

            if (!_lensesById.TryGetValue(lensId, out var lens) || !(lens["params"] is JObject paramsDef) || !paramsDef.HasValues)
            {
                _resetButton.interactable = false;
                _editingLensLabel.text = "Esta lente no tiene parámetros editables.";
                return;
            }

            // Orden clinico (focos primero); params extra al final, en orden del catalogo.
            var ordered = new List<string>();
            foreach (var k in ParamMeta.ORDER) if (paramsDef[k] != null) ordered.Add(k);
            foreach (var prop in paramsDef.Properties()) if (!ordered.Contains(prop.Name)) ordered.Add(prop.Name);

            int added = 0;
            foreach (var key in ordered)
            {
                if (!(paramsDef[key] is JObject e) || e["default"] == null || e["min"] == null || e["max"] == null)
                    continue;
                float def = (float)e["default"];
                _paramDefaults[key] = def;
                var row = ParamRowView.Create(_kit, _paramsList, key, (float)e["min"], (float)e["max"],
                    CurrentParamValue(key, def));
                row.Changed += OnParamChanged;
                _paramRows[key] = row;
                added++;
            }

            _resetButton.interactable = added > 0;
            _editingLensLabel.text = added == 0
                ? "Esta lente no tiene parámetros editables."
                : "Los ajustes se aplican al ojo que tiene esta lente.";
        }

        private float CurrentParamValue(string key, float def)
        {
            foreach (var eye in new[] { "left", "right" })
            {
                var state = _visionState[eye] as JObject;
                if (state != null && (string)state["lens_id"] == _editingLensId && state[key] != null)
                    return (float)state[key];
            }
            return def;
        }

        private void SyncParamRowsFromState()
        {
            foreach (var kv in _paramRows)
                kv.Value.SetValueSilent(CurrentParamValue(kv.Key, _paramDefaults.TryGetValue(kv.Key, out var d) ? d : 0f));
        }

        private void OnParamChanged(string paramName, float value) => SendParamOverride(paramName, value);

        // El override sigue a la LENTE en edicion, no al selector "Ojo a tratar".
        private string EyesForEditingLens()
        {
            string leftId = (string)(_visionState["left"]?["lens_id"]) ?? "";
            string rightId = (string)(_visionState["right"]?["lens_id"]) ?? "";
            bool onLeft = leftId == _editingLensId, onRight = rightId == _editingLensId;
            if (onLeft && onRight) return "both";
            if (onLeft) return "left";
            if (onRight) return "right";
            return "";
        }

        private void SendParamOverride(string paramName, float value)
        {
            if (_ws == null || !_ws.IsOpen) return;
            string eye = EyesForEditingLens();
            if (eye == "") return;
            SendCmd(new JObject
            {
                ["cmd"] = "override_params",
                ["eye"] = eye,
                ["params"] = new JObject { [paramName] = value },
            });
        }

        private void OnResetParamsPressed()
        {
            if (_editingLensId == "" || _paramDefaults.Count == 0) return;
            string eye = EyesForEditingLens();
            var all = new JObject();
            foreach (var kv in _paramDefaults)
            {
                if (_paramRows.TryGetValue(kv.Key, out var row)) row.SetValueSilent(kv.Value);
                all[kv.Key] = kv.Value;
            }
            if (eye != "" && _ws != null && _ws.IsOpen)
                SendCmd(new JObject { ["cmd"] = "override_params", ["eye"] = eye, ["params"] = all });
        }

        // ============================================================
        // Escenarios
        // ============================================================
        private void RebuildScenarioList()
        {
            for (int i = _scenarioList.childCount - 1; i >= 0; i--) Destroy(_scenarioList.GetChild(i).gameObject);
            foreach (var sid in _scenarios)
            {
                string id = sid;
                var btn = _kit.Button(_scenarioList, ScenarioLabel(id), BtnStyle.Segment, true, 48, 15);
                _kit.Size(btn.GetComponent<RectTransform>(), minW: 120, prefW: 120, flexW: 0);
                btn.SetOn(id == _currentScenario, false);
                btn.OnClick = () => OnScenarioPressed(id);
            }
        }

        private static string ScenarioLabel(string sid)
        {
            switch (sid)
            {
                case "consultorio": return "Consultorio";
                case "ruta_noche": return "Ruta nocturna";
                default: return sid.Length > 0 ? char.ToUpper(sid[0]) + sid.Substring(1) : sid;
            }
        }

        private void OnScenarioPressed(string scenarioId)
        {
            if (_ws == null || !_ws.IsOpen) { SetBadge(_kit.P.Warn, "Sin conexión"); return; }
            _currentScenario = scenarioId;
            foreach (Transform child in _scenarioList)
            {
                var b = child.GetComponent<TabletButton>();
                if (b != null) b.SetOn(b.Label != null && b.Label.text == ScenarioLabel(scenarioId), false);
            }
            SendCmd(new JObject { ["cmd"] = "load_scenario", ["scenario"] = scenarioId });
        }

        // ============================================================
        // Astigmatismo
        // ============================================================
        private void OnAstigChanged()
        {
            UpdateAstigLabels();
            if (_astigEnabled.IsOn) SendAstigmatism();
        }

        private void UpdateAstigLabels()
        {
            _magValue.text = _magSlider.value.ToString("F0", CultureInfo.InvariantCulture) + " px";
            _angleValue.text = _angleSlider.value.ToString("F0", CultureInfo.InvariantCulture) + "°";
        }

        private void SendAstigmatism()
        {
            if (_ws == null || !_ws.IsOpen) { SetBadge(_kit.P.Warn, "Sin conexión"); return; }
            // El GlareController del visor espera magnitud normalizada 0..1 y angulo
            // en radianes; el slider muestra 0-50 px (fiel a Godot) y 0-180°.
            SendCmd(new JObject
            {
                ["cmd"] = "set_astigmatism",
                ["eye"] = _selectedEye,
                ["enabled"] = _astigEnabled.IsOn,
                ["magnitude"] = _magSlider.value / 50f,
                ["angle"] = _angleSlider.value * Mathf.Deg2Rad,
            });
        }

        // ============================================================
        // Footer
        // ============================================================
        private void UpdateFooter()
        {
            if (!_sessionActive) { if (_footer != null) _footer.text = ""; return; }
            int fps = _framesReceived - _framesLastTick;
            _framesLastTick = _framesReceived;
            _footer.text = fps + " fps · " + (_bytesReceived / 1048576.0).ToString("F1", CultureInfo.InvariantCulture) + " MB recibidos";
        }

        // ============================================================
        // Comandos
        // ============================================================
        private void SendCmd(JObject cmd)
        {
            if (_ws != null && _ws.IsOpen) _ws.SendText(cmd.ToString(Newtonsoft.Json.Formatting.None));
        }

        // ============================================================
        // Construccion de la UI
        // ============================================================
        private void BuildUI()
        {
            var canvasGo = new GameObject("TabletCanvas", typeof(Canvas), typeof(CanvasScaler), typeof(GraphicRaycaster));
            var canvas = canvasGo.GetComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            var scaler = canvasGo.GetComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1280, 800);
            scaler.matchWidthOrHeight = 0.5f;

            if (FindFirstObjectByType<UnityEngine.EventSystems.EventSystem>() == null)
                new GameObject("EventSystem", typeof(UnityEngine.EventSystems.EventSystem),
                    typeof(UnityEngine.InputSystem.UI.InputSystemUIInputModule));

            // Fondo general.
            var bg = new GameObject("Background", typeof(RectTransform), typeof(Image));
            bg.transform.SetParent(canvasGo.transform, false);
            Stretch(bg.GetComponent<RectTransform>());
            _kit.Tint(bg.GetComponent<Image>(), p => p.Bg);

            BuildConnectScreen(canvasGo.transform);
            BuildMainScreen(canvasGo.transform);
        }

        private void BuildConnectScreen(Transform parent)
        {
            _connectScreen = new GameObject("ConnectScreen", typeof(RectTransform));
            _connectScreen.transform.SetParent(parent, false);
            Stretch(_connectScreen.GetComponent<RectTransform>());

            var wrap = new GameObject("CenterWrap", typeof(RectTransform));
            wrap.transform.SetParent(_connectScreen.transform, false);
            var wrt = wrap.GetComponent<RectTransform>();
            wrt.anchorMin = wrt.anchorMax = new Vector2(0.5f, 0.5f);
            wrt.pivot = new Vector2(0.5f, 0.5f);
            wrt.sizeDelta = new Vector2(560, 0);
            var wvb = wrap.AddComponent<VerticalLayoutGroup>();
            wvb.spacing = 12; wvb.childControlWidth = true; wvb.childControlHeight = true;
            wvb.childForceExpandWidth = true; wvb.childForceExpandHeight = false;
            wvb.childAlignment = TextAnchor.UpperCenter;
            var fit = wrap.AddComponent<ContentSizeFitter>();
            fit.verticalFit = ContentSizeFitter.FitMode.PreferredSize;

            EyeGlyph(wrap.transform, 56);
            _kit.Label(wrap.transform, "Simulador IOL", LabelKind.Title, TextAlignmentOptions.Center);
            _kit.Label(wrap.transform, "Control para consultorio oftalmológico", LabelKind.Subtitle, TextAlignmentOptions.Center);
            _kit.Spacer(wrap.transform, 12, false);
            _kit.Label(wrap.transform, "Visores detectados", LabelKind.Section, TextAlignmentOptions.Left);
            _discoveredList = _kit.Box(wrap.transform, "DiscoveredList", true, 8, null, expandW: true);
            _connectStatus = _kit.Label(wrap.transform, "Buscando visores en la red...", LabelKind.Hint, TextAlignmentOptions.Center);
            _kit.Spacer(wrap.transform, 8, false);

            var advToggle = _kit.Button(wrap.transform, "Conexión manual", BtnStyle.Ghost, true, 48, 16);
            _advancedBox = _kit.Box(wrap.transform, "AdvancedBox", false, 8, null, expandW: true);
            _advancedBox.GetComponent<HorizontalLayoutGroup>().childForceExpandWidth = false;
            _hostEdit = _kit.LineEdit(_advancedBox, "IP del visor (misma red Wi-Fi)");
            var connectBtn = _kit.Button(_advancedBox, "Conectar", BtnStyle.Accent, false, 48, 16);
            _kit.Size(connectBtn.GetComponent<RectTransform>(), minW: 140, prefW: 140, flexW: 0);
            connectBtn.OnClick = OnConnectPressed;
            _advancedBox.gameObject.SetActive(false);
            advToggle.OnToggled += on => _advancedBox.gameObject.SetActive(on);
            _hostEdit.onSubmit.AddListener(_ => OnConnectPressed());
        }

        private void BuildMainScreen(Transform parent)
        {
            _mainScreen = new GameObject("Main", typeof(RectTransform));
            _mainScreen.transform.SetParent(parent, false);
            Stretch(_mainScreen.GetComponent<RectTransform>());
            var mvb = _mainScreen.AddComponent<VerticalLayoutGroup>();
            mvb.spacing = 0; mvb.childControlWidth = true; mvb.childControlHeight = true;
            mvb.childForceExpandWidth = true; mvb.childForceExpandHeight = false;

            BuildHeader(_mainScreen.transform);
            BuildBody(_mainScreen.transform);
            BuildFooter(_mainScreen.transform);
        }

        private void BuildHeader(Transform parent)
        {
            var header = _kit.Panel(parent, "HeaderBar", p => p.Surface, 0, false, 12, new RectOffset(16, 16, 8, 8));
            var hlg = header.GetComponent<HorizontalLayoutGroup>();
            hlg.childForceExpandWidth = false; hlg.childAlignment = TextAnchor.MiddleLeft;
            _kit.Size(header, minH: 62);

            EyeGlyph(header, 26);
            var title = _kit.Label(header, "Simulador IOL", LabelKind.Title, TextAlignmentOptions.Left);
            title.fontSize = 19;
            _kit.Spacer(header, 0, true);
            _kit.Label(header, "Escenario:", LabelKind.Subtitle, TextAlignmentOptions.Right);
            _scenarioList = _kit.Box(header, "ScenarioList", false, 6, null, expandW: false);
            _scenarioList.GetComponent<HorizontalLayoutGroup>().childAlignment = TextAnchor.MiddleLeft;
            _kit.Spacer(header, 0, true);
            _themeToggle = _kit.Button(header, "Modo claro", BtnStyle.Ghost, false, 44, 14);
            _themeToggle.OnClick = () => ApplyTheme(!_isDark);
            _kit.StatusBadge(header, out _statusDot, out _statusText);
            var disconnect = _kit.Button(header, "Desconectar", BtnStyle.Ghost, false, 44, 14);
            disconnect.OnClick = OnDisconnectPressed;
        }

        private void BuildBody(Transform parent)
        {
            var body = _kit.Box(parent, "Body", false, 12, new RectOffset(12, 12, 12, 4), expandW: true, expandH: true);
            body.GetComponent<HorizontalLayoutGroup>().childForceExpandWidth = false;
            _kit.Size(body, flexH: 1);

            // --- Panel de stream (izquierda) ---
            var stream = _kit.Panel(body, "StreamPanel", p => p.StreamBg, 10, false, 8, new RectOffset(8, 8, 8, 8));
            _kit.Size(stream, minW: 280, flexW: 2.3f, flexH: 1);
            var eyes = _kit.Box(stream, "EyesContainer", false, 8, null, expandW: true, expandH: true);

            var leftPane = _kit.Box(eyes, "LeftEyePane", true, 6, null, expandW: true, expandH: false);
            _kit.Size(leftPane, flexW: 1);
            _leftEyeLabel = _kit.Label(leftPane, "Ambos ojos", LabelKind.StreamChip, TextAlignmentOptions.Center);
            _kit.Size(_leftEyeLabel.rectTransform, minH: 22, prefH: 22, flexH: 0);
            _streamLeft = MakeStreamView(leftPane);

            _rightEyePane = _kit.Box(eyes, "RightEyePane", true, 6, null, expandW: true, expandH: false).gameObject;
            _kit.Size(_rightEyePane.GetComponent<RectTransform>(), flexW: 1);
            _rightEyeLabel = _kit.Label(_rightEyePane.transform, "OD", LabelKind.StreamChip, TextAlignmentOptions.Center);
            _kit.Size(_rightEyeLabel.rectTransform, minH: 22, prefH: 22, flexH: 0);
            _streamRight = MakeStreamView(_rightEyePane.transform);
            _rightEyePane.SetActive(false);

            // --- Scroll de controles (derecha) ---
            var scroll = _kit.ScrollColumn(body, out var content);
            _kit.Size(scroll.GetComponent<RectTransform>(), minW: 360, flexW: 1, flexH: 1);

            BuildEyeCard(content);
            BuildLensesCard(content);
            BuildParamsCard(content);
            BuildAstigCard(content);
        }

        private void BuildEyeCard(Transform parent)
        {
            var card = _kit.Card(parent, "EyeCard");
            _kit.Label(card, "Ojo a tratar", LabelKind.Section, TextAlignmentOptions.Left);
            var row = _kit.Box(card, "EyeSelector", false, 6, null, expandW: true);
            _eyeBoth = _kit.Button(row, "Ambos", BtnStyle.Segment, true, 52, 16);
            _eyeOd = _kit.Button(row, "OD · Derecho", BtnStyle.Segment, true, 52, 15);
            _eyeOi = _kit.Button(row, "OI · Izquierdo", BtnStyle.Segment, true, 52, 15);
            foreach (var b in new[] { _eyeBoth, _eyeOd, _eyeOi })
                _kit.Size(b.GetComponent<RectTransform>(), flexW: 1);
            _eyeBoth.OnClick = () => SelectEye("both");
            _eyeOd.OnClick = () => SelectEye("right");
            _eyeOi.OnClick = () => SelectEye("left");
            SelectEye("both");
        }

        private void SelectEye(string eye)
        {
            _selectedEye = eye;
            _eyeBoth.SetOn(eye == "both", false);
            _eyeOd.SetOn(eye == "right", false);
            _eyeOi.SetOn(eye == "left", false);
        }

        private void BuildLensesCard(Transform parent)
        {
            var card = _kit.Card(parent, "LensesCard");
            _kit.Label(card, "Lentes intraoculares", LabelKind.Section, TextAlignmentOptions.Left);
            _lensList = _kit.Box(card, "LensList", true, 8, null, expandW: true);
        }

        private void BuildParamsCard(Transform parent)
        {
            var card = _kit.Card(parent, "ParamsCard");
            var paramsToggle = _kit.Button(card, "Ajuste fino", BtnStyle.Ghost, true, 48, 16);
            _paramsContent = _kit.Box(card, "ParamsContent", true, 10, null, expandW: true);
            _editingLensLabel = _kit.Label(_paramsContent, "Aplicá una lente para ajustar sus parámetros.", LabelKind.Hint, TextAlignmentOptions.Left);
            _paramsList = _kit.Box(_paramsContent, "ParamsList", true, 10, null, expandW: true);
            _resetButton = _kit.Button(_paramsContent, "Restaurar valores", BtnStyle.Ghost, false, 44, 15);
            _resetButton.OnClick = OnResetParamsPressed;
            _resetButton.interactable = false;
            _paramsContent.gameObject.SetActive(false);
            paramsToggle.OnToggled += on => _paramsContent.gameObject.SetActive(on);
        }

        private void BuildAstigCard(Transform parent)
        {
            var card = _kit.Card(parent, "AstigCard");
            var astigToggle = _kit.Button(card, "Astigmatismo", BtnStyle.Ghost, true, 48, 16);
            _astigContent = _kit.Box(card, "AstigContent", true, 8, null, expandW: true);

            _kit.CheckToggle(_astigContent, "Simular astigmatismo", out _astigEnabled);
            _astigEnabled.OnToggled += _ => SendAstigmatism();

            var magHeader = _kit.Box(_astigContent, "MagHeader", false, 8, null, expandW: true);
            magHeader.GetComponent<HorizontalLayoutGroup>().childAlignment = TextAnchor.MiddleLeft;
            var magLabel = _kit.Label(magHeader, "Magnitud", LabelKind.Body, TextAlignmentOptions.Left);
            _kit.Size(magLabel.rectTransform, flexW: 1);
            _magValue = _kit.Label(magHeader, "25 px", LabelKind.Value, TextAlignmentOptions.Right);
            _magSlider = _kit.Slider(_astigContent);
            _magSlider.minValue = 0; _magSlider.maxValue = 50; _magSlider.wholeNumbers = true;
            _magSlider.SetValueWithoutNotify(25);
            _magSlider.onValueChanged.AddListener(_ => OnAstigChanged());

            var angleHeader = _kit.Box(_astigContent, "AngleHeader", false, 8, null, expandW: true);
            angleHeader.GetComponent<HorizontalLayoutGroup>().childAlignment = TextAnchor.MiddleLeft;
            var angleLabel = _kit.Label(angleHeader, "Eje", LabelKind.Body, TextAlignmentOptions.Left);
            _kit.Size(angleLabel.rectTransform, flexW: 1);
            _angleValue = _kit.Label(angleHeader, "0°", LabelKind.Value, TextAlignmentOptions.Right);
            _angleSlider = _kit.Slider(_astigContent);
            _angleSlider.minValue = 0; _angleSlider.maxValue = 180; _angleSlider.wholeNumbers = true;
            _angleSlider.SetValueWithoutNotify(0);
            _angleSlider.onValueChanged.AddListener(_ => OnAstigChanged());

            _astigContent.gameObject.SetActive(false);
            astigToggle.OnToggled += on => _astigContent.gameObject.SetActive(on);
            UpdateAstigLabels();
        }

        private void BuildFooter(Transform parent)
        {
            var footer = _kit.Box(parent, "Footer", false, 0, new RectOffset(16, 16, 2, 6), expandW: true);
            _kit.Size(footer, minH: 26);
            _footer = _kit.Label(footer, "", LabelKind.Hint, TextAlignmentOptions.Right);
            _kit.Size(_footer.rectTransform, flexW: 1);
        }

        // Vista de stream por ojo: contenedor flexible (lo dimensiona la columna) con
        // un RawImage que se ajusta dentro preservando el aspecto 4:3 del visor (sin
        // distorsion / sin estirar). El placeholder oscuro = "sin señal".
        private RawImage MakeStreamView(Transform pane)
        {
            var wrap = new GameObject("StreamWrap", typeof(RectTransform));
            wrap.transform.SetParent(pane, false);
            _kit.Size(wrap.GetComponent<RectTransform>(), flexW: 1, flexH: 1);
            var img = _kit.RawImage(wrap.transform);
            img.color = new Color(0.03f, 0.04f, 0.06f, 1f);
            var rt = img.rectTransform;
            rt.anchorMin = rt.anchorMax = new Vector2(0.5f, 0.5f);
            rt.pivot = new Vector2(0.5f, 0.5f);
            var arf = img.gameObject.AddComponent<AspectRatioFitter>();
            arf.aspectMode = AspectRatioFitter.AspectMode.FitInParent;
            arf.aspectRatio = 768f / 576f;
            return img;
        }

        // Glifo "ojo" estilizado (sin assets): circulo de acento + iris + pupila.
        private void EyeGlyph(Transform parent, float size)
        {
            var root = new GameObject("EyeGlyph", typeof(RectTransform));
            root.transform.SetParent(parent, false);
            _kit.Size(root.GetComponent<RectTransform>(), minW: size, minH: size, prefW: size, prefH: size, flexW: 0);
            Circle(root.transform, size, p => p.Accent);
            Circle(root.transform, size * 0.62f, p => p.Bg);
            Circle(root.transform, size * 0.28f, p => p.Accent);
        }

        private void Circle(Transform parent, float diameter, System.Func<TabletPalette, Color> sel)
        {
            var go = new GameObject("Circle", typeof(RectTransform));
            go.transform.SetParent(parent, false);
            var rt = go.GetComponent<RectTransform>();
            rt.anchorMin = rt.anchorMax = new Vector2(0.5f, 0.5f);
            rt.pivot = new Vector2(0.5f, 0.5f);
            rt.sizeDelta = new Vector2(diameter, diameter);
            var img = go.AddComponent<Image>();
            img.sprite = TabletUiKit.Rounded(Mathf.RoundToInt(diameter / 2f));
            img.type = Image.Type.Simple;
            img.raycastTarget = false;
            _kit.Tint(img, sel);
        }

        private static void Stretch(RectTransform rt)
        {
            rt.anchorMin = Vector2.zero; rt.anchorMax = Vector2.one;
            rt.offsetMin = Vector2.zero; rt.offsetMax = Vector2.zero;
        }
    }
}
