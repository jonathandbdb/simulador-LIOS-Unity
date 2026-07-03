using System.Collections.Generic;
using System.Globalization;
using Newtonsoft.Json.Linq;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace Simulador.Tablet
{
    /// <summary>
    /// App tablet (cliente de control del consultorio) — capa de UI. Construye todas
    /// las pantallas/widgets por codigo, drena la sesion (<see cref="TabletSession"/>,
    /// P6.2) en su Update() y traduce eventos de sesion -> widgets y clicks -> metodos
    /// de la sesion. Replica fiel de features/tablet/streaming_client.gd: pantalla de
    /// conexion + pantalla principal (header con escenarios/tema/estado, stream con
    /// split en modo blend, cards de ojo/lentes/ajuste fino/astigmatismo/A-B/presets)
    /// con tema oscuro/claro Inter.
    ///
    /// P6.2 (split god-object): la capa de red + protocolo + estado de sesion vivia
    /// toda ACA (WebSocketClient/DiscoveryListener, auth por PIN, maquina de
    /// reconexion, parsing de hello/vision_state/frames) y se extrajo a
    /// Assets/Scripts/Runtime/Tablet/TabletSession.cs (plain C#, sin MonoBehaviour).
    /// Esta clase NO cambio de nombre ni de archivo (Tablet.unity la referencia por
    /// GUID) pero SI de namespace: Simulador.Net -> Simulador.Tablet (verificado que
    /// no rompe nada — ver Gotchas en docs/tablet.md sobre
    /// NetworkController.Bootstrap). Quien posee que: ver tabla de arquitectura en
    /// docs/tablet.md.
    /// </summary>
    public class TabletController : MonoBehaviour
    {
        private TabletSession _session;

        // --- Discovery (UI: botones por host) ---
        // P5.6: boton por host detectado, para diffear en vez de destruir/reconstruir
        // toda la lista cada segundo (RefreshDiscovered). La lista de hosts en si vive
        // en TabletSession (DiscoveredHosts).
        private readonly Dictionary<string, TabletButton> _discoveredButtons = new();

        // --- Emparejamiento por PIN (estado de pantalla, no de protocolo) ---
        private string _pinPendingHost = "";

        // --- Tema / kit ---
        private TabletUiKit _kit;
        private bool _isDark = true;
        private string PrefsPath => Application.persistentDataPath + "/ui_prefs.cfg";

        // --- Pantallas ---
        private GameObject _connectScreen, _mainScreen, _pinScreen, _reconnectScreen;
        private RectTransform _discoveredList, _advancedBox;
        private TMP_Text _connectStatus;
        private TMP_InputField _hostEdit;
        private TMP_InputField _pinEdit;
        private TMP_Text _pinHostLabel, _pinStatus;
        private TMP_Text _reconnectHostLabel, _reconnectStatus;

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

        // --- Comparacion A/B (P5.1) ---
        // Reusa apply_lens (sin protocolo nuevo): A/B solo recuerdan 2 ids de lente
        // localmente, y el toggle aplica el que NO este activo en el ojo seleccionado.
        private string _abLensA = "", _abLensB = "";
        private TMP_Text _abLabelA, _abLabelB;
        private TabletButton _abToggleBtn;

        // --- Presets de sesion (P5.2) ---
        // Persistencia local (nunca al visor): snapshot de vision_state (lens_id +
        // params por ojo, tal como llega del visor) + escenario. Aplicar = secuencia
        // de comandos existentes (apply_lens/override_params/load_scenario).
        private readonly List<JObject> _presets = new();
        private RectTransform _presetList;
        private TMP_InputField _presetNameEdit;
        private TMP_Text _presetStatus;
        private string PresetsPath => Application.persistentDataPath + "/presets.json";

        // --- Footer ---
        private TMP_Text _footer;
        private int _framesReceived, _framesLastTick;
        private long _bytesReceived;
        private float _footerTimer, _discoveryTimer;

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
            LoadPresetsFromDisk();
            RebuildPresetList();

            _session = new TabletSession();
            _session.Connected += OnSessionConnected;
            _session.AuthOk += OnSessionAuthOk;
            _session.PinScreenRequested += OnSessionPinScreenRequested;
            _session.ShowConnectScreenRequested += OnSessionShowConnectScreenRequested;
            _session.ReconnectStarted += OnSessionReconnectStarted;
            _session.ReconnectStatusChanged += OnSessionReconnectStatusChanged;
            _session.HelloReceived += OnSessionHello;
            _session.VisionStateChanged += OnSessionVisionStateChanged;
            _session.FrameReceived += OnSessionFrame;
            _session.Begin();

            ShowConnectScreen("Buscando visores en la red...");
        }

        private void Update()
        {
            _session.Update(Time.deltaTime);

            _discoveryTimer += Time.deltaTime;
            if (_discoveryTimer >= 1f) { _discoveryTimer = 0f; RefreshDiscovered(); }

            _footerTimer += Time.deltaTime;
            if (_footerTimer >= 1f) { _footerTimer = 0f; UpdateFooter(); }
        }

        private void OnDestroy() => _session?.Shutdown();

        // ============================================================
        // Eventos de TabletSession -> UI
        // ============================================================
        private void OnSessionConnected()
        {
            if (_session.IsReconnecting) SetReconnectStatus("Conectado. Autenticando...");
            else SetConnectStatus("Conectado. Autenticando...");
        }

        private void OnSessionAuthOk() => SetConnectStatus("Autenticado. Esperando catálogo del visor...");

        private void OnSessionPinScreenRequested(string message) => ShowPinScreen(_session.CurrentHost, message);

        private void OnSessionShowConnectScreenRequested(string message, bool isError) => ShowConnectScreen(message, isError);

        private void OnSessionReconnectStarted(string message) => ShowReconnectScreen(_session.CurrentHost, message);

        private void OnSessionReconnectStatusChanged(string message) => SetReconnectStatus(message);

        private void OnSessionHello(JArray lenses)
        {
            RebuildLensList(lenses);
            RebuildScenarioList();
            RefreshVisionUI();
            ShowMainScreen();
        }

        private void OnSessionVisionStateChanged()
        {
            RefreshVisionUI();
            SyncParamRowsFromState();
        }

        private void OnSessionFrame(char eye, byte[] jpg)
        {
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
            _bytesReceived += jpg.Length + 1; // +1: el byte de header que TabletSession ya separo
        }

        // ============================================================
        // Tema claro / oscuro
        // ============================================================
        private void ApplyTheme(bool dark)
        {
            _isDark = dark;
            _kit.Apply(TabletPalette.For(dark));
            if (_themeToggle?.Label != null)
                _themeToggle.Label.text = dark ? "Modo claro" : "Modo oscuro";
            if (_session != null && _session.IsSessionActive) SetBadge(_kit.P.Ok, ConnectedBadgeText());
            SaveThemePref();
        }

        private string ConnectedBadgeText() =>
            string.IsNullOrEmpty(_session.CurrentHost) ? "Conectado" : "Conectado · " + _session.CurrentHost;

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
            _pinScreen.SetActive(false);
            _reconnectScreen.SetActive(false);
            _mainScreen.SetActive(false);
            SetConnectStatus(message, isError);
        }

        private void ShowMainScreen()
        {
            _connectScreen.SetActive(false);
            _pinScreen.SetActive(false);
            _reconnectScreen.SetActive(false);
            _mainScreen.SetActive(true);
            SetBadge(_kit.P.Ok, ConnectedBadgeText());
        }

        // Pantalla de PIN: se intercala entre ConnectScreen y MainScreen cuando hace
        // falta el PIN de emparejamiento (host sin PIN guardado en memoria, o
        // reintento tras auth_fail/auth_locked, ver OnSessionPinScreenRequested).
        private void ShowPinScreen(string host, string message = "")
        {
            _pinPendingHost = host;
            _connectScreen.SetActive(false);
            _mainScreen.SetActive(false);
            _reconnectScreen.SetActive(false);
            _pinScreen.SetActive(true);
            _pinHostLabel.text = "Visor: " + host;
            _pinEdit.text = "";
            SetPinStatus(message);
        }

        // Pantalla de reconexion automatica (P2.5): se muestra ante una caida NO
        // manual de una sesion activa, mientras dura el backoff (TabletSession decide
        // cuando via ReconnectStarted/ReconnectStatusChanged). "Cancelar" corta el
        // loop y vuelve al discovery normal.
        private void ShowReconnectScreen(string host, string message)
        {
            _connectScreen.SetActive(false);
            _pinScreen.SetActive(false);
            _mainScreen.SetActive(false);
            _reconnectScreen.SetActive(true);
            _reconnectHostLabel.text = "Visor: " + host;
            SetReconnectStatus(message);
        }

        private void SetConnectStatus(string text, bool isError = false)
        {
            if (_connectStatus == null) return;
            _connectStatus.text = text;
            _connectStatus.color = isError ? _kit.P.Error : _kit.P.TextHint;
        }

        private void SetPinStatus(string text, bool isError = false)
        {
            if (_pinStatus == null) return;
            _pinStatus.text = text;
            _pinStatus.color = isError ? _kit.P.Error : _kit.P.TextHint;
        }

        private void SetReconnectStatus(string text, bool isError = false)
        {
            if (_reconnectStatus == null) return;
            _reconnectStatus.text = text;
            _reconnectStatus.color = isError ? _kit.P.Error : _kit.P.TextHint;
        }

        private void SetBadge(Color color, string text)
        {
            if (_statusDot != null) _statusDot.color = color;
            if (_statusText != null) _statusText.text = text;
        }

        // ============================================================
        // Discovery + conexion
        // ============================================================
        private void RefreshDiscovered()
        {
            if (_connectScreen == null || !_connectScreen.activeSelf) return;

            // P5.6: diff en vez de destruir/reconstruir TODA la lista cada segundo
            // (parpadeo visible aunque nada cambiara). Solo crea los hosts nuevos y
            // remueve los que expiraron (TabletSession ya podo los viejos); los que
            // siguen vigentes quedan intactos.
            var current = new HashSet<string>(_session.DiscoveredHosts);
            var expired = new List<string>();
            foreach (var host in _discoveredButtons.Keys)
                if (!current.Contains(host)) expired.Add(host);
            foreach (var host in expired)
            {
                if (_discoveredButtons[host] != null) Destroy(_discoveredButtons[host].gameObject);
                _discoveredButtons.Remove(host);
            }
            foreach (var host in current)
            {
                if (_discoveredButtons.ContainsKey(host)) continue;
                string h = host;
                var btn = _kit.Button(_discoveredList, "Visor Quest  ·  " + h, BtnStyle.Segment, false, 64, 16);
                btn.OnClick = () => StartConnectFlow(h);
                _discoveredButtons[host] = btn;
            }

            if (current.Count == 0)
            {
                if (!_session.IsConnecting) SetConnectStatus("Buscando visores en la red...");
                return;
            }
            if (!_session.IsConnecting) SetConnectStatus("Tocá un visor para conectarte.");
        }

        private void OnConnectPressed()
        {
            string host = _hostEdit.text.Trim();
            if (string.IsNullOrEmpty(host))
            {
                foreach (var h in _session.DiscoveredHosts) { host = h; break; }
            }
            StartConnectFlow(host);
        }

        // Antes de abrir el WebSocket hace falta el PIN de emparejamiento del visor:
        // si ya quedo guardado en memoria para este host (sesion previa exitosa) se
        // reusa sin volver a pedirlo; si no, se pide con el PinScreen.
        private void StartConnectFlow(string host)
        {
            if (string.IsNullOrEmpty(host))
            {
                SetConnectStatus("Ingresá la IP del visor o tocá uno detectado.", true);
                return;
            }
            if (_session.TryGetCachedPin(host, out var savedPin))
                BeginConnect(host, savedPin);
            else
                ShowPinScreen(host);
        }

        private void OnPinCancelPressed() => ShowConnectScreen("Tocá un visor para conectarte.");

        private void OnPinConfirmPressed()
        {
            string pin = (_pinEdit.text ?? "").Trim();
            if (pin.Length != 6)
            {
                SetPinStatus("El PIN tiene 6 dígitos.", true);
                return;
            }
            BeginConnect(_pinPendingHost, pin);
        }

        private void BeginConnect(string host, string pin)
        {
            ShowConnectScreen("Conectando a " + host + "...");
            _session.Connect(host, pin);
        }

        private void OnDisconnectPressed() => _session.Disconnect();

        // P5.4: refresh en caliente -- pide {"cmd":"refresh"}; el visor responde con
        // el mismo payload del "hello" (BuildHello reusado del lado visor) y
        // OnSessionHello ya sabe reconstruir catalogo/escenarios/vision_state (la
        // misma rama que usa una reconexion exitosa, P2.5), asi que no hace falta
        // parsear nada nuevo del lado tablet.
        private void OnRefreshPressed()
        {
            if (!_session.IsWsOpen) { SetBadge(_kit.P.Warn, "Sin conexión"); return; }
            _session.SendCommand(new JObject { ["cmd"] = "refresh" });
        }

        private void OnReconnectCancelPressed()
        {
            if (!_session.CancelReconnect()) ShowConnectScreen("Tocá un visor para conectarte.");
        }

        // ============================================================
        // Lentes
        // ============================================================
        private string LensDisplayName(string lensId)
        {
            if (_session.LensesById.TryGetValue(lensId, out var l))
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
            if (!_session.IsWsOpen) { SetBadge(_kit.P.Warn, "Sin conexión"); return; }
            _session.SendCommand(new JObject { ["cmd"] = "apply_lens", ["lens_id"] = lensId, ["eye"] = _selectedEye });
            // Actualizacion optimista del estado local (vision_state compartido con la sesion).
            if (_selectedEye == "left" || _selectedEye == "both")
                _session.VisionState["left"] = new JObject { ["lens_id"] = lensId };
            if (_selectedEye == "right" || _selectedEye == "both")
                _session.VisionState["right"] = new JObject { ["lens_id"] = lensId };
            RefreshVisionUI();
            BuildParamsEditor(lensId);
        }

        private void RefreshVisionUI()
        {
            var visionState = _session.VisionState;
            string leftId = (string)(visionState["left"]?["lens_id"]) ?? "";
            string rightId = (string)(visionState["right"]?["lens_id"]) ?? "";
            // P2.1: el split de panes sigue a blend_active (fuente unica de verdad:
            // LensEngine.ComputeBlend del visor, exige AMBOS ojos con lente y distintas),
            // no a esta heuristica local -- con un solo ojo con lente, leftId != rightId
            // pero blend_active es false (1 pane, sin etiqueta vacia).
            bool isBlend = (bool?)visionState["blend_active"] ?? false;

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
            RefreshAbUI(); // P5.1: mantiene el indicador de "activa" al dia con cualquier cambio de vision_state
        }

        // ============================================================
        // Comparacion A/B (P5.1)
        // ============================================================
        // Lente actualmente activa en el ojo seleccionado (si "both", se mira el
        // ojo izquierdo -- alcanza para decidir cual de A/B esta activa hoy).
        private string CurrentEyeLensId()
        {
            string eye = _selectedEye == "both" ? "left" : _selectedEye;
            return (string)(_session.VisionState[eye]?["lens_id"]) ?? "";
        }

        private void SetAbSlotA() { _abLensA = CurrentEyeLensId(); RefreshAbUI(); }
        private void SetAbSlotB() { _abLensB = CurrentEyeLensId(); RefreshAbUI(); }

        private void RefreshAbUI()
        {
            if (_abLabelA == null) return; // BuildAbCard todavia no corrio (p.ej. durante BuildUI)
            string current = CurrentEyeLensId();
            _abLabelA.text = "A: " + (string.IsNullOrEmpty(_abLensA) ? "—"
                : LensDisplayName(_abLensA) + (!string.IsNullOrEmpty(current) && current == _abLensA ? " (activa)" : ""));
            _abLabelB.text = "B: " + (string.IsNullOrEmpty(_abLensB) ? "—"
                : LensDisplayName(_abLensB) + (!string.IsNullOrEmpty(current) && current == _abLensB ? " (activa)" : ""));
            _abToggleBtn.interactable = !string.IsNullOrEmpty(_abLensA) && !string.IsNullOrEmpty(_abLensB) && _abLensA != _abLensB;
        }

        // Alterna al slot que NO esta activo -- reusa OnLensSelected (apply_lens +
        // actualizacion optimista + editor de "Ajuste fino"), sin protocolo nuevo.
        private void OnAbTogglePressed()
        {
            if (string.IsNullOrEmpty(_abLensA) || string.IsNullOrEmpty(_abLensB)) return;
            string next = CurrentEyeLensId() == _abLensA ? _abLensB : _abLensA;
            OnLensSelected(next);
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

            if (!_session.LensesById.TryGetValue(lensId, out var lens) || !(lens["params"] is JObject paramsDef) || !paramsDef.HasValues)
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
                var state = _session.VisionState[eye] as JObject;
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
            var visionState = _session.VisionState;
            string leftId = (string)(visionState["left"]?["lens_id"]) ?? "";
            string rightId = (string)(visionState["right"]?["lens_id"]) ?? "";
            bool onLeft = leftId == _editingLensId, onRight = rightId == _editingLensId;
            if (onLeft && onRight) return "both";
            if (onLeft) return "left";
            if (onRight) return "right";
            return "";
        }

        private void SendParamOverride(string paramName, float value)
        {
            if (!_session.IsWsOpen) return;
            string eye = EyesForEditingLens();
            if (eye == "") return;
            _session.SendCommand(new JObject
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
            if (eye != "" && _session.IsWsOpen)
                _session.SendCommand(new JObject { ["cmd"] = "override_params", ["eye"] = eye, ["params"] = all });
        }

        // ============================================================
        // Escenarios
        // ============================================================
        private readonly Dictionary<string, TabletButton> _scenarioButtons = new();

        private void RebuildScenarioList()
        {
            for (int i = _scenarioList.childCount - 1; i >= 0; i--) Destroy(_scenarioList.GetChild(i).gameObject);
            _scenarioButtons.Clear();
            foreach (var sid in _session.Scenarios)
            {
                string id = sid;
                var btn = _kit.Button(_scenarioList, ScenarioLabel(id), BtnStyle.Segment, true, 48, 15);
                _kit.Size(btn.GetComponent<RectTransform>(), minW: 120, prefW: 120, flexW: 0);
                btn.SetOn(id == _session.CurrentScenario, false);
                btn.OnClick = () => OnScenarioPressed(id);
                _scenarioButtons[id] = btn;
            }
        }

        // Fallback defensivo si algun id llega sin label en el "hello" (no deberia
        // pasar: NetworkController siempre manda {id,label}).
        private string ScenarioLabel(string sid)
        {
            if (_session.ScenarioLabels.TryGetValue(sid, out var label) && !string.IsNullOrEmpty(label)) return label;
            return sid.Length > 0 ? char.ToUpper(sid[0]) + sid.Substring(1) : sid;
        }

        private void OnScenarioPressed(string scenarioId)
        {
            if (!_session.IsWsOpen) { SetBadge(_kit.P.Warn, "Sin conexión"); return; }
            _session.CurrentScenario = scenarioId;
            // P2.3: seleccion por id (clave del diccionario), no por comparar el texto
            // del label del boton -- dos escenarios con el mismo label ya no rompen esto.
            foreach (var kv in _scenarioButtons)
                kv.Value.SetOn(kv.Key == scenarioId, false);
            _session.SendCommand(new JObject { ["cmd"] = "load_scenario", ["id"] = scenarioId });
        }

        // ============================================================
        // Presets de sesion (P5.2)
        // ============================================================
        private JObject CloneEyeState(string eye)
        {
            var state = _session.VisionState[eye] as JObject;
            return state != null ? (JObject)state.DeepClone() : new JObject();
        }

        private void OnSavePresetPressed()
        {
            string name = (_presetNameEdit.text ?? "").Trim();
            if (string.IsNullOrEmpty(name)) { SetPresetStatus("Ingresá un nombre.", true); return; }
            // Snapshot de vision_state (lens_id + params por ojo, tal como los manda
            // el visor) + escenario actual. "Aplicar" reconstruye esto con los
            // comandos existentes (ver ApplyPreset) -- nada de protocolo nuevo.
            var preset = new JObject
            {
                ["name"] = name,
                ["scenario"] = _session.CurrentScenario,
                ["left"] = CloneEyeState("left"),
                ["right"] = CloneEyeState("right"),
            };
            int idx = _presets.FindIndex(pr => (string)pr["name"] == name);
            if (idx >= 0) _presets[idx] = preset; else _presets.Add(preset);
            SavePresetsToDisk();
            RebuildPresetList();
            _presetNameEdit.text = "";
            SetPresetStatus("Preset \"" + name + "\" guardado.");
        }

        private void OnDeletePreset(string name)
        {
            _presets.RemoveAll(pr => (string)pr["name"] == name);
            SavePresetsToDisk();
            RebuildPresetList();
            SetPresetStatus("Preset \"" + name + "\" borrado.");
        }

        private void ApplyPreset(JObject preset)
        {
            if (!_session.IsWsOpen) { SetBadge(_kit.P.Warn, "Sin conexión"); return; }
            ApplyPresetEye("left", preset["left"] as JObject);
            ApplyPresetEye("right", preset["right"] as JObject);
            string scenario = (string)preset["scenario"];
            if (!string.IsNullOrEmpty(scenario))
                _session.SendCommand(new JObject { ["cmd"] = "load_scenario", ["id"] = scenario });
            SetPresetStatus("Preset \"" + (string)preset["name"] + "\" aplicado.");
        }

        // apply_lens (fija los defaults del catalogo) + override_params con el resto
        // del snapshot ENCIMA (reproduce los overrides que tenia guardados el preset).
        private void ApplyPresetEye(string eye, JObject state)
        {
            if (state == null) return;
            string lensId = (string)state["lens_id"] ?? "";
            if (string.IsNullOrEmpty(lensId)) return;
            _session.SendCommand(new JObject { ["cmd"] = "apply_lens", ["lens_id"] = lensId, ["eye"] = eye });
            var paramsObj = new JObject();
            foreach (var kv in state)
                if (kv.Key != "lens_id") paramsObj[kv.Key] = kv.Value;
            if (paramsObj.HasValues)
                _session.SendCommand(new JObject { ["cmd"] = "override_params", ["eye"] = eye, ["params"] = paramsObj });
        }

        private void RebuildPresetList()
        {
            if (_presetList == null) return;
            for (int i = _presetList.childCount - 1; i >= 0; i--) Destroy(_presetList.GetChild(i).gameObject);
            if (_presets.Count == 0)
            {
                _kit.Label(_presetList, "Sin presets guardados.", LabelKind.Hint, TextAlignmentOptions.Left);
                return;
            }
            foreach (var preset in _presets)
            {
                var p = preset;
                string name = (string)p["name"] ?? "?";
                var row = _kit.Box(_presetList, "Preset_" + name, false, 6, null, expandW: true);
                row.GetComponent<HorizontalLayoutGroup>().childAlignment = TextAnchor.MiddleLeft;
                var label = _kit.Label(row, name, LabelKind.Body, TextAlignmentOptions.Left);
                _kit.Size(label.rectTransform, flexW: 1);
                var applyBtn = _kit.Button(row, "Aplicar", BtnStyle.Ghost, false, 36, 13);
                applyBtn.OnClick = () => ApplyPreset(p);
                var delBtn = _kit.Button(row, "Borrar", BtnStyle.Ghost, false, 36, 13);
                delBtn.OnClick = () => OnDeletePreset(name);
            }
        }

        private void SetPresetStatus(string text, bool isError = false)
        {
            if (_presetStatus == null) return;
            _presetStatus.text = text;
            _presetStatus.color = isError ? _kit.P.Error : _kit.P.TextHint;
        }

        // Persistencia LOCAL de la tablet, nunca al visor (a diferencia del PIN, no
        // hay nada sensible aca: son lentes/params/escenario, igual que
        // lens_overrides.json del lado visor). Archivo corrupto/ausente -> arranca
        // sin presets, mismo patron que DataManager.LoadLensOverrides.
        private void LoadPresetsFromDisk()
        {
            try
            {
                if (!System.IO.File.Exists(PresetsPath)) return;
                var arr = JArray.Parse(System.IO.File.ReadAllText(PresetsPath));
                _presets.Clear();
                foreach (var p in arr) if (p is JObject po) _presets.Add(po);
            }
            catch { /* archivo corrupto: se ignora */ }
        }

        private void SavePresetsToDisk()
        {
            try
            {
                var arr = new JArray();
                foreach (var p in _presets) arr.Add(p);
                System.IO.File.WriteAllText(PresetsPath, arr.ToString(Newtonsoft.Json.Formatting.Indented));
            }
            catch { }
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
            if (!_session.IsWsOpen) { SetBadge(_kit.P.Warn, "Sin conexión"); return; }
            // El GlareController del visor espera magnitud normalizada 0..1 y angulo
            // en radianes; el slider muestra 0-50 px (fiel a Godot) y 0-180°.
            _session.SendCommand(new JObject
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
            if (!_session.IsSessionActive) { if (_footer != null) _footer.text = ""; return; }
            int rawFps = _framesReceived - _framesLastTick;
            _framesLastTick = _framesReceived;
            // P5.5: en blend, StreamingCapture manda un frame 'L' + un frame 'R' por
            // tick (cada uno cuenta en _framesReceived via OnSessionFrame) -> el conteo
            // crudo duplica la tasa real por pane. Fuera de blend llega un solo frame
            // 'B' por tick, el conteo crudo ya es la tasa real.
            bool blend = (bool?)_session.VisionState["blend_active"] ?? false;
            int fps = blend ? rawFps / 2 : rawFps;
            _footer.text = fps + " fps · " + (_bytesReceived / 1048576.0).ToString("F1", CultureInfo.InvariantCulture) + " MB recibidos";
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
            BuildPinScreen(canvasGo.transform);
            BuildReconnectScreen(canvasGo.transform);
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

        // Pantalla de PIN: se intercala entre ConnectScreen y MainScreen (ver
        // StartConnectFlow/BeginConnect/OnSessionPinScreenRequested). Campo numerico
        // TMP (ContentType.IntegerNumber -> teclado numerico en Android).
        private void BuildPinScreen(Transform parent)
        {
            _pinScreen = new GameObject("PinScreen", typeof(RectTransform));
            _pinScreen.transform.SetParent(parent, false);
            Stretch(_pinScreen.GetComponent<RectTransform>());
            _pinScreen.SetActive(false);

            var wrap = new GameObject("PinWrap", typeof(RectTransform));
            wrap.transform.SetParent(_pinScreen.transform, false);
            var wrt = wrap.GetComponent<RectTransform>();
            wrt.anchorMin = wrt.anchorMax = new Vector2(0.5f, 0.5f);
            wrt.pivot = new Vector2(0.5f, 0.5f);
            wrt.sizeDelta = new Vector2(420, 0);
            var wvb = wrap.AddComponent<VerticalLayoutGroup>();
            wvb.spacing = 12; wvb.childControlWidth = true; wvb.childControlHeight = true;
            wvb.childForceExpandWidth = true; wvb.childForceExpandHeight = false;
            wvb.childAlignment = TextAnchor.UpperCenter;
            var fit = wrap.AddComponent<ContentSizeFitter>();
            fit.verticalFit = ContentSizeFitter.FitMode.PreferredSize;

            EyeGlyph(wrap.transform, 48);
            _kit.Label(wrap.transform, "PIN de emparejamiento", LabelKind.Title, TextAlignmentOptions.Center);
            _pinHostLabel = _kit.Label(wrap.transform, "", LabelKind.Subtitle, TextAlignmentOptions.Center);
            _kit.Label(wrap.transform, "Ingresá el PIN de 6 dígitos que muestra el visor.", LabelKind.Hint, TextAlignmentOptions.Center);
            _kit.Spacer(wrap.transform, 8, false);

            _pinEdit = _kit.LineEdit(wrap.transform, "000000");
            _pinEdit.contentType = TMP_InputField.ContentType.IntegerNumber;
            _pinEdit.characterLimit = 6;
            _pinEdit.onSubmit.AddListener(_ => OnPinConfirmPressed());

            _pinStatus = _kit.Label(wrap.transform, "", LabelKind.Hint, TextAlignmentOptions.Center);
            _kit.Spacer(wrap.transform, 8, false);

            var row = _kit.Box(wrap.transform, "PinButtons", false, 8, null, expandW: true);
            var cancelBtn = _kit.Button(row, "Cancelar", BtnStyle.Ghost, false, 48, 16);
            _kit.Size(cancelBtn.GetComponent<RectTransform>(), flexW: 1);
            cancelBtn.OnClick = OnPinCancelPressed;
            var confirmBtn = _kit.Button(row, "Conectar", BtnStyle.Accent, false, 48, 16);
            _kit.Size(confirmBtn.GetComponent<RectTransform>(), flexW: 1);
            confirmBtn.OnClick = OnPinConfirmPressed;
        }

        // Pantalla de reconexion automatica (P2.5): igual de simple que PinScreen pero
        // sin input -- solo estado + Cancelar (vuelve al discovery y corta el loop).
        private void BuildReconnectScreen(Transform parent)
        {
            _reconnectScreen = new GameObject("ReconnectScreen", typeof(RectTransform));
            _reconnectScreen.transform.SetParent(parent, false);
            Stretch(_reconnectScreen.GetComponent<RectTransform>());
            _reconnectScreen.SetActive(false);

            var wrap = new GameObject("ReconnectWrap", typeof(RectTransform));
            wrap.transform.SetParent(_reconnectScreen.transform, false);
            var wrt = wrap.GetComponent<RectTransform>();
            wrt.anchorMin = wrt.anchorMax = new Vector2(0.5f, 0.5f);
            wrt.pivot = new Vector2(0.5f, 0.5f);
            wrt.sizeDelta = new Vector2(420, 0);
            var wvb = wrap.AddComponent<VerticalLayoutGroup>();
            wvb.spacing = 12; wvb.childControlWidth = true; wvb.childControlHeight = true;
            wvb.childForceExpandWidth = true; wvb.childForceExpandHeight = false;
            wvb.childAlignment = TextAnchor.UpperCenter;
            var fit = wrap.AddComponent<ContentSizeFitter>();
            fit.verticalFit = ContentSizeFitter.FitMode.PreferredSize;

            EyeGlyph(wrap.transform, 48);
            _kit.Label(wrap.transform, "Reconectando", LabelKind.Title, TextAlignmentOptions.Center);
            _reconnectHostLabel = _kit.Label(wrap.transform, "", LabelKind.Subtitle, TextAlignmentOptions.Center);
            _kit.Spacer(wrap.transform, 8, false);
            _reconnectStatus = _kit.Label(wrap.transform, "", LabelKind.Hint, TextAlignmentOptions.Center);
            _kit.Spacer(wrap.transform, 8, false);

            var cancelBtn = _kit.Button(wrap.transform, "Cancelar", BtnStyle.Ghost, false, 48, 16);
            cancelBtn.OnClick = OnReconnectCancelPressed;
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
            var refreshBtn = _kit.Button(header, "Actualizar", BtnStyle.Ghost, false, 44, 14);
            refreshBtn.OnClick = OnRefreshPressed;
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
            // Vertical: en blend los dos ojos se apilan (cada uno usa el ancho completo
            // => imagen mas grande que lado a lado). En no-blend, el panel unico llena todo.
            var eyes = _kit.Box(stream, "EyesContainer", true, 8, null, expandW: true, expandH: true);

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
            BuildAbCard(content);
            BuildParamsCard(content);
            BuildAstigCard(content);
            BuildPresetsCard(content);
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

        // P5.1: comparacion A/B minimal -- 2 slots (recuerdan un lens_id cada uno,
        // tomado de la lente activa en el ojo seleccionado) + 1 boton grande que
        // alterna cual esta aplicada. Sin protocolo nuevo (reusa apply_lens via
        // OnLensSelected).
        private void BuildAbCard(Transform parent)
        {
            var card = _kit.Card(parent, "AbCard");
            _kit.Label(card, "Comparar A / B", LabelKind.Section, TextAlignmentOptions.Left);
            _kit.Label(card, "Marcá la lente activa como A o B y alterná entre ambas en el ojo seleccionado.",
                LabelKind.Hint, TextAlignmentOptions.Left);

            var rowA = _kit.Box(card, "AbRowA", false, 8, null, expandW: true);
            rowA.GetComponent<HorizontalLayoutGroup>().childAlignment = TextAnchor.MiddleLeft;
            _abLabelA = _kit.Label(rowA, "A: —", LabelKind.Body, TextAlignmentOptions.Left);
            _kit.Size(_abLabelA.rectTransform, flexW: 1);
            var setABtn = _kit.Button(rowA, "Usar actual", BtnStyle.Ghost, false, 36, 13);
            setABtn.OnClick = SetAbSlotA;

            var rowB = _kit.Box(card, "AbRowB", false, 8, null, expandW: true);
            rowB.GetComponent<HorizontalLayoutGroup>().childAlignment = TextAnchor.MiddleLeft;
            _abLabelB = _kit.Label(rowB, "B: —", LabelKind.Body, TextAlignmentOptions.Left);
            _kit.Size(_abLabelB.rectTransform, flexW: 1);
            var setBBtn = _kit.Button(rowB, "Usar actual", BtnStyle.Ghost, false, 36, 13);
            setBBtn.OnClick = SetAbSlotB;

            _abToggleBtn = _kit.Button(card, "A ↔ B", BtnStyle.Accent, false, 48, 16);
            _abToggleBtn.OnClick = OnAbTogglePressed;
            _abToggleBtn.interactable = false;
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

            // P4.4: aclaracion de precedencia -- desde que el catalogo trae astig_magnitude/
            // astig_axis_deg (persistentes, por lente, editables en "Ajuste fino"), hay DOS
            // controles de astigmatismo. Este switch/sliders son el ajuste LIVE de
            // GlareController.SetAstigmatism (set_astigmatism, no persiste) y siempre pisa lo
            // que muestre el shader por encima del valor de catalogo mientras este activo;
            // cambiar de lente u override_params no lo apaga ni lo sincroniza. Ver
            // docs/tablet.md.
            _kit.Label(_astigContent, "Ajuste temporal para esta sesión: se pisa al cambiar de lente o parámetros. Para un astigmatismo residual que persista con la lente, usá los sliders de \"Ajuste fino\".",
                LabelKind.Hint, TextAlignmentOptions.Left);

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

        // P5.2: presets de sesion (lente por ojo + overrides + escenario), persistidos
        // localmente en la tablet (persistentDataPath/presets.json). Aplicar reproduce el
        // snapshot con los comandos existentes (ver ApplyPreset).
        private void BuildPresetsCard(Transform parent)
        {
            var card = _kit.Card(parent, "PresetsCard");
            var presetsToggle = _kit.Button(card, "Presets", BtnStyle.Ghost, true, 48, 16);
            var presetsContent = _kit.Box(card, "PresetsContent", true, 8, null, expandW: true);

            _presetList = _kit.Box(presetsContent, "PresetList", true, 6, null, expandW: true);

            var saveRow = _kit.Box(presetsContent, "PresetSaveRow", false, 8, null, expandW: true);
            _presetNameEdit = _kit.LineEdit(saveRow, "Nombre del preset");
            var saveBtn = _kit.Button(saveRow, "Guardar", BtnStyle.Accent, false, 40, 14);
            _kit.Size(saveBtn.GetComponent<RectTransform>(), minW: 100, prefW: 100, flexW: 0);
            saveBtn.OnClick = OnSavePresetPressed;
            _presetNameEdit.onSubmit.AddListener(_ => OnSavePresetPressed());

            _presetStatus = _kit.Label(presetsContent, "", LabelKind.Hint, TextAlignmentOptions.Left);

            presetsContent.gameObject.SetActive(false);
            presetsToggle.OnToggled += on => presetsContent.gameObject.SetActive(on);
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
