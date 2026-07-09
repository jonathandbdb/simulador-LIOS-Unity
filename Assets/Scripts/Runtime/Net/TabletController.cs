using System.Collections.Generic;
using System.Globalization;
using Newtonsoft.Json.Linq;
using Simulador.Update;
using TMPro;
using UnityEngine;
using UnityEngine.Android;
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
        // Nombre amigable ya asignado a cada boton (para desambiguar duplicados sin
        // recrear los existentes, ver RefreshDiscovered/FriendlyVisorName). La IP
        // NUNCA aparece en la UI, solo en Debug.Log.
        private readonly Dictionary<string, string> _discoveredNames = new();

        // --- Emparejamiento por PIN (estado de pantalla, no de protocolo) ---
        private string _pinPendingHost = "";

        // --- Tema / kit ---
        private TabletUiKit _kit;
        private bool _isDark = true;
        private string PrefsPath => Application.persistentDataPath + "/ui_prefs.cfg";

        // --- Pantallas ---
        private GameObject _connectScreen, _mainScreen, _pinScreen, _reconnectScreen;
        private RectTransform _discoveredList;
        private TMP_Text _connectStatus;
        private TMP_Text _networkInfoLabel;
        private TMP_InputField _pinEdit;
        private TMP_Text _pinHostLabel, _pinStatus;
        private TMP_Text _reconnectHostLabel, _reconnectStatus;

        // --- Header ---
        private RectTransform _scenarioList;
        private TabletButton _themeToggle;
        private Image _statusDot;
        private TMP_Text _statusText;

        // --- Toggle de HUD del visor (comando "set_hud", ver docs/networking.md) ---
        // Estado puramente local/optimista: no hay campo en vision_state que
        // confirme el estado real del HUD (fire-and-forget, igual que
        // set_astigmatism). Arranca en "visible" y se resetea ahi en cada conexion
        // nueva (ver OnSessionConnected) para no arrastrar el toggle de una sesion
        // anterior.
        private TabletButton _hudToggleBtn;
        private bool _hudVisible = true;

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

        // --- Update semi-automatico (F5, ver docs/updates.md) ---
        // Overlay MODAL construido por encima de TODAS las demas pantallas (se
        // agrega ULTIMO en BuildUI -> ultimo hijo del canvas -> se dibuja arriba
        // de todo, incluido el overlay de FullscreenStream). Sigue el mismo
        // patron que PinScreen (region dentro de esta clase, no una clase
        // aparte): UpdateManager es quien decide/dispara los eventos, esta
        // clase solo traduce eventos -> widgets y clicks -> metodos de
        // UpdateManager, igual que TabletSession/OnSession* para la sesion de
        // red. En el visor VR la UI equivalente es Update/UpdatePromptVR.cs
        // (esta clase NO corre en Main.unity).
        private GameObject _updateScreen;
        private TMP_Text _updateTitleLabel, _updateVersionLabel, _updateChangelogLabel, _updateStatusLabel;
        private TabletButton _updatePrimaryBtn, _updateSecondaryBtn;
        private UpdateLogic.UpdateManifest _updateManifest;
        private bool _updateForced;

        // --- Stream a pantalla completa ---
        // Overlay que reusa las MISMAS Texture2D del stream normal (_texLeft/
        // _texRight, ver OnSessionFrame): RawImage nuevo por ojo apuntando a la
        // misma textura, sin decodificar el JPG una segunda vez. El modo (1 imagen
        // o 2 lado a lado) sigue a blend_active, igual que el panel normal -- ver
        // RefreshFullscreenUI.
        private GameObject _fullscreenStream, _fsRightPane;
        private RawImage _fsStreamLeft, _fsStreamRight;
        private TMP_Text _fsLeftLabel, _fsRightLabel;

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

        // Permiso de ubicacion (SSID Wi-Fi, ver RequestLocationPermissionOnce): se
        // pide una sola vez por sesion de la app, no en cada vuelta al ConnectScreen.
        private bool _locationPermissionRequested;

        // ============================================================
        private void Start()
        {
            // Decision de producto: la app queda bloqueada a landscape (ambos
            // sentidos) — el layout de dos columnas de BuildMainScreen (stream +
            // columna scrolleable) asume ese aspecto, no se rediseña para
            // portrait. Runtime-only (no toca ProjectSettings, compartido con el
            // visor): esta clase solo corre en Tablet.unity.
            Screen.autorotateToPortrait = false;
            Screen.autorotateToPortraitUpsideDown = false;
            Screen.autorotateToLandscapeLeft = true;
            Screen.autorotateToLandscapeRight = true;
            Screen.orientation = ScreenOrientation.AutoRotation;

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
            SubscribeUpdateEvents();

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

        private void OnDestroy()
        {
            _session?.Shutdown();
            UnsubscribeUpdateEvents();
        }

        // ============================================================
        // Eventos de TabletSession -> UI
        // ============================================================
        private void OnSessionConnected()
        {
            // Nueva conexion (inicial o reconexion automatica, P2.5): resetear el
            // toggle local del HUD a "visible" -- ver comentario del campo
            // _hudVisible, no hay estado de HUD que sincronizar desde el visor.
            _hudVisible = true;
            UpdateHudToggleLabel();
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
            // El overlay de pantalla completa (_fsStreamLeft/_fsStreamRight) reusa
            // estas MISMAS Texture2D: no hay una segunda decodificacion de JPG, solo
            // se refleja el mismo LoadImage en el RawImage del overlay.
            if (eye == 'R')
            {
                if (ImageConversion.LoadImage(_texRight, jpg))
                {
                    _streamRight.texture = _texRight; _streamRight.color = Color.white;
                    _fsStreamRight.texture = _texRight; _fsStreamRight.color = Color.white;
                }
            }
            else if (eye == 'L')
            {
                if (ImageConversion.LoadImage(_texLeft, jpg))
                {
                    _streamLeft.texture = _texLeft; _streamLeft.color = Color.white;
                    _fsStreamLeft.texture = _texLeft; _fsStreamLeft.color = Color.white;
                }
            }
            else // 'B' o desconocido -> mismo frame en ambos paneles
            {
                if (ImageConversion.LoadImage(_texLeft, jpg))
                {
                    _streamLeft.texture = _texLeft; _streamLeft.color = Color.white;
                    _fsStreamLeft.texture = _texLeft; _fsStreamLeft.color = Color.white;
                    if (ImageConversion.LoadImage(_texRight, jpg))
                    {
                        _streamRight.texture = _texRight; _streamRight.color = Color.white;
                        _fsStreamRight.texture = _texRight; _fsStreamRight.color = Color.white;
                    }
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
            CloseFullscreenStream(); // sesion interrumpida: no dejar el overlay de stream congelado
            _connectScreen.SetActive(true);
            _pinScreen.SetActive(false);
            _reconnectScreen.SetActive(false);
            _mainScreen.SetActive(false);
            SetConnectStatus(message, isError);
            RefreshNetworkInfo();
            RequestLocationPermissionOnce();
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
        // falta el PIN de emparejamiento (host sin token persistente valido, o
        // reintento tras auth_fail/auth_locked, ver OnSessionPinScreenRequested).
        private void ShowPinScreen(string host, string message = "")
        {
            CloseFullscreenStream();
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
            CloseFullscreenStream();
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
                _discoveredNames.Remove(host);
            }
            foreach (var host in current)
            {
                if (_discoveredButtons.ContainsKey(host)) continue;
                string h = host;
                _session.HostLabels.TryGetValue(host, out var rawLabel);
                string display = NextFriendlyVisorName(FriendlyVisorName(rawLabel));
                _discoveredNames[h] = display;
                // La IP solo va al log (troubleshooting de red); en la UI nunca.
                Debug.Log($"[Tablet] Visor detectado: {display} ({h})");
                var btn = _kit.Button(_discoveredList, display, BtnStyle.Segment, false, 64, 16);
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

        // Deriva un nombre amigable a partir del device_label del beacon (formato
        // "<nombre>-<nonce8>", ver NetworkController.GenerateBeaconLabel): recorta
        // el nonce de sesion (no aporta nada al clinico). Sin label (payload viejo
        // o sin parsear) cae a un generico -- la IP NUNCA se muestra en la lista.
        private static string FriendlyVisorName(string rawLabel)
        {
            if (string.IsNullOrWhiteSpace(rawLabel)) return "Visor Quest";
            int dash = rawLabel.LastIndexOf('-');
            bool hasNonce = dash > 0 && rawLabel.Length - dash - 1 == 8;
            string name = hasNonce ? rawLabel.Substring(0, dash) : rawLabel;
            return string.IsNullOrWhiteSpace(name) ? "Visor Quest" : name;
        }

        // Si ya hay un boton con ese mismo nombre base en la lista, agrega un
        // sufijo neutro "(2)", "(3)"... para desambiguar sin usar la IP.
        private string NextFriendlyVisorName(string baseName)
        {
            int count = 1;
            foreach (var used in _discoveredNames.Values)
                if (used == baseName || used.StartsWith(baseName + " (")) count++;
            return count > 1 ? $"{baseName} ({count})" : baseName;
        }

        // Antes de abrir el WebSocket hace falta autenticarse contra el visor: si hay
        // un token persistente de un enlace previo (pairing.json, ver
        // TabletSession.TryGetCachedToken) se reusa sin pedir el PIN; si no, se pide
        // con el PinScreen (primer enlace, o el token quedo invalido/revocado).
        private void StartConnectFlow(string host)
        {
            if (string.IsNullOrEmpty(host))
            {
                SetConnectStatus("Ingresá la IP del visor o tocá uno detectado.", true);
                return;
            }
            if (_session.TryGetCachedToken(host, out var savedToken))
                BeginConnectWithToken(host, savedToken);
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

        private void BeginConnectWithToken(string host, string token)
        {
            ShowConnectScreen("Conectando a " + host + "...");
            _session.ConnectWithToken(host, token);
        }

        private void OnDisconnectPressed() => _session.Disconnect();

        // Boton "Desvincular" (header): revoca el token de esta tablet en el visor y
        // olvida el emparejamiento local con el host actual (ver
        // TabletSession.Unpair) -- vuelve al ConnectScreen y la proxima conexion a
        // este visor va a pedir el PIN de nuevo.
        private void OnUnpairPressed() => _session.Unpair();

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

        // Boton "Ocultar HUD" / "Mostrar HUD" (header): comando "set_hud" (ver
        // docs/networking.md), togglea el HUD de diagnostico del visor (FPS/lentes/
        // halos/PIN). Fire-and-forget como set_astigmatism/load_scenario -- no hay
        // ack ni campo en vision_state que confirme el estado real del HUD, asi que
        // _hudVisible es puramente el estado optimista de ESTA tablet (se resetea a
        // "visible" en cada conexion nueva, ver OnSessionConnected).
        private void OnHudTogglePressed()
        {
            if (!_session.IsWsOpen) { SetBadge(_kit.P.Warn, "Sin conexión"); return; }
            _hudVisible = !_hudVisible;
            UpdateHudToggleLabel();
            _session.SendCommand(new JObject { ["cmd"] = "set_hud", ["visible"] = _hudVisible });
        }

        private void UpdateHudToggleLabel()
        {
            if (_hudToggleBtn?.Label != null)
                _hudToggleBtn.Label.text = _hudVisible ? "Ocultar HUD" : "Mostrar HUD";
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
            RefreshFullscreenUI(isBlend, leftId, rightId); // mantiene el overlay al dia con cualquier cambio de vision_state
        }

        // ============================================================
        // Stream a pantalla completa
        // ============================================================
        // Mismo criterio que el panel normal de arriba (isBlend/leftId/rightId ya
        // calculados en RefreshVisionUI): 1 imagen si ambos ojos comparten lente
        // (incluye el caso sin lente en ningun ojo), 2 lado a lado si difieren.
        // Se llama SIEMPRE desde RefreshVisionUI (este o no abierto el overlay), asi
        // el modo ya esta al dia apenas se abre y reacciona a un cambio de lente
        // mientras esta abierto.
        private void RefreshFullscreenUI(bool isBlend, string leftId, string rightId)
        {
            if (_fsLeftLabel == null) return; // BuildFullscreenStream todavia no corrio (p.ej. durante BuildUI)
            if (isBlend)
            {
                _fsRightPane.SetActive(true);
                _fsLeftLabel.text = "OI — " + LensDisplayName(leftId);
                _fsRightLabel.text = "OD — " + LensDisplayName(rightId);
            }
            else
            {
                _fsRightPane.SetActive(false);
                _fsLeftLabel.text = string.IsNullOrEmpty(leftId)
                    ? "Ambos ojos" : "Ambos ojos — " + LensDisplayName(leftId);
            }
        }

        private void OpenFullscreenStream() => _fullscreenStream.SetActive(true);

        private void CloseFullscreenStream() => _fullscreenStream?.SetActive(false);

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
            {
                var esGo = new GameObject("EventSystem", typeof(UnityEngine.EventSystems.EventSystem),
                    typeof(UnityEngine.InputSystem.UI.InputSystemUIInputModule));
                // Default de pixelDragThreshold son 10 px REALES (no dp): en una
                // pantalla ~320dpi eso es ~5dp, mas chico que el touch-slop nativo
                // de Android (~8dp) -> taps que se leen como drag y viceversa.
                // Escalamos el umbral por densidad (10dp de referencia); si
                // Screen.dpi no esta disponible (Editor, algunos devices) cae al
                // default de 10.
                var es = esGo.GetComponent<UnityEngine.EventSystems.EventSystem>();
                es.pixelDragThreshold = Screen.dpi > 0f
                    ? Mathf.Max(10, Mathf.RoundToInt(10f * Screen.dpi / 160f))
                    : 10;
            }

            // Fondo general.
            var bg = new GameObject("Background", typeof(RectTransform), typeof(Image));
            bg.transform.SetParent(canvasGo.transform, false);
            Stretch(bg.GetComponent<RectTransform>());
            _kit.Tint(bg.GetComponent<Image>(), p => p.Bg);

            BuildConnectScreen(canvasGo.transform);
            BuildPinScreen(canvasGo.transform);
            BuildReconnectScreen(canvasGo.transform);
            BuildMainScreen(canvasGo.transform);
            BuildFullscreenStream(canvasGo.transform);
            BuildUpdateScreen(canvasGo.transform); // ULTIMO: debe quedar arriba de TODAS las demas pantallas/overlays
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
            _networkInfoLabel = _kit.Label(wrap.transform, "", LabelKind.Hint, TextAlignmentOptions.Center);
            _kit.Label(wrap.transform,
                "El visor Quest y la tablet deben estar conectados a la misma red Wi-Fi.",
                LabelKind.Hint, TextAlignmentOptions.Center);
            _kit.Spacer(wrap.transform, 12, false);

            var exitBtn = _kit.Button(wrap.transform, "Salir", BtnStyle.Ghost, false, 48, 16);
            exitBtn.OnClick = () => Application.Quit();
        }

        // Info de red Wi-Fi de la ConnectScreen (SSID si hay permiso de ubicacion,
        // si no cae a la IP local -- ver RequestLocationPermissionOnce). Se llama
        // cada vez que se muestra la pantalla (ShowConnectScreen) para reflejar un
        // posible cambio de red o de permiso recien concedido.
        private void RefreshNetworkInfo()
        {
            if (_networkInfoLabel == null) return;
            string info = TryGetWifiSsid();
            if (string.IsNullOrEmpty(info)) info = TryGetLocalIPv4();
            _networkInfoLabel.text = string.IsNullOrEmpty(info) ? "Red: no disponible" : "Red: " + info;
        }

#if UNITY_ANDROID && !UNITY_EDITOR
        // Gotcha Android: WifiManager.getConnectionInfo().getSSID() devuelve
        // "<unknown ssid>" sin el permiso de ubicacion en runtime (Android 9+) Y
        // sin ACCESS_WIFI_STATE en el manifest (Assets/Plugins/Android/
        // AndroidManifest.xml) -- sin ninguno de los dos tira SecurityException
        // (capturada abajo). RequestLocationPermissionOnce pide el permiso runtime
        // una vez por sesion; si el clinico lo niega, esto sigue fallando y cae a
        // la IP local (TryGetLocalIPv4), sin insistir.
        private static string TryGetWifiSsid()
        {
            try
            {
                using var unityPlayer = new AndroidJavaClass("com.unity3d.player.UnityPlayer");
                using var activity = unityPlayer.GetStatic<AndroidJavaObject>("currentActivity");
                using var wifiManager = activity.Call<AndroidJavaObject>("getSystemService", "wifi");
                using var wifiInfo = wifiManager.Call<AndroidJavaObject>("getConnectionInfo");
                string ssid = wifiInfo.Call<string>("getSSID");
                if (string.IsNullOrEmpty(ssid) || ssid.Contains("unknown ssid")) return null;
                return ssid.Trim('"');
            }
            catch (System.Exception e)
            {
                Debug.LogWarning("[Tablet] No se pudo leer el SSID Wi-Fi: " + e.Message);
                return null;
            }
        }
#else
        // SIM: atajo deliberado — fuera de Android (Editor) no hay WifiManager; cae
        // directo a la IP local via TryGetLocalIPv4.
        private static string TryGetWifiSsid() => null;
#endif

        // Fallback cuando el SSID no resuelve: IP local via System.Net (el truco del
        // "connect" UDP no manda paquetes, solo hace que el SO resuelva la interfaz/
        // ruta de salida -- funciona sin depender de reflection ni de permisos).
        private static string TryGetLocalIPv4()
        {
            try
            {
                using var socket = new System.Net.Sockets.Socket(
                    System.Net.Sockets.AddressFamily.InterNetwork,
                    System.Net.Sockets.SocketType.Dgram,
                    System.Net.Sockets.ProtocolType.Udp);
                socket.Connect("8.8.8.8", 65530);
                return (socket.LocalEndPoint as System.Net.IPEndPoint)?.Address.ToString();
            }
            catch (System.Exception e)
            {
                Debug.LogWarning("[Tablet] No se pudo resolver la IP local: " + e.Message);
                return null;
            }
        }

#if UNITY_ANDROID && !UNITY_EDITOR
        // Pide el permiso de ubicacion (requisito de Android 9+ para leer el SSID
        // real, ver TryGetWifiSsid) UNA vez por sesion de la app, al mostrar la
        // ConnectScreen -- no en cada vuelta a esta pantalla (desconectar/
        // reconectar/cancelar PIN todos pasan por ShowConnectScreen). Si el
        // clinico lo concede, refresca el label al toque; si lo niega, la tablet
        // sigue funcionando con el fallback de IP local sin volver a insistir.
        private void RequestLocationPermissionOnce()
        {
            if (_locationPermissionRequested) return;
            _locationPermissionRequested = true;
            if (Permission.HasUserAuthorizedPermission(Permission.FineLocation)) return;
            var callbacks = new PermissionCallbacks();
            callbacks.PermissionGranted += _ => RefreshNetworkInfo();
            // Denegado / "no preguntar de nuevo": sin accion -- RefreshNetworkInfo
            // ya muestra el fallback de IP, no hay nada mas que hacer aca.
            Permission.RequestUserPermission(Permission.FineLocation, callbacks);
        }
#else
        // SIM: atajo deliberado — fuera de Android (Editor) no hay permisos runtime
        // que pedir; RefreshNetworkInfo ya cae a TryGetLocalIPv4 sin esto.
        private void RequestLocationPermissionOnce() { }
#endif

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
            // Anclado arriba-centro (NO centrado vertical, a diferencia de
            // Connect/ReconnectScreen que no llevan teclado): el teclado nativo de
            // Android cubre la mitad inferior de la pantalla y
            // TouchScreenKeyboard.area no es confiable para medir su alto real y
            // evitarlo dinamicamente, asi que el popup se fija en el tercio superior
            // con un margen fijo -- solucion pragmatica, no una medicion del teclado.
            wrt.anchorMin = wrt.anchorMax = new Vector2(0.5f, 1f);
            wrt.pivot = new Vector2(0.5f, 1f);
            wrt.sizeDelta = new Vector2(420, 0);
            wrt.anchoredPosition = new Vector2(0f, -40f);
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
            _hudToggleBtn = _kit.Button(header, "Ocultar HUD", BtnStyle.Ghost, false, 44, 14);
            _hudToggleBtn.OnClick = OnHudTogglePressed;
            _kit.StatusBadge(header, out _statusDot, out _statusText);
            var disconnect = _kit.Button(header, "Desconectar", BtnStyle.Ghost, false, 44, 14);
            disconnect.OnClick = OnDisconnectPressed;
            // Emparejamiento persistente por token (ver docs/networking.md): discreto,
            // al lado de Desconectar -- accion poco frecuente (revocar el
            // emparejamiento), no un boton principal del flujo clinico.
            var unpair = _kit.Button(header, "Desvincular", BtnStyle.Ghost, false, 44, 14);
            unpair.OnClick = OnUnpairPressed;
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

            // Orden clinico OD-primero (convencion OD/OI, ver docs/tablet.md): el pane
            // derecho se crea ANTES que el izquierdo para quedar arriba en el stack
            // vertical de "eyes" (EyesContainer). "LeftEyePane" sigue siendo el pane
            // siempre-activo que ademas cubre la vista compartida "Ambos ojos" en
            // no-blend -- su posicion ahi es irrelevante porque es el unico hijo activo.
            _rightEyePane = _kit.Box(eyes, "RightEyePane", true, 6, null, expandW: true, expandH: false).gameObject;
            _kit.Size(_rightEyePane.GetComponent<RectTransform>(), flexW: 1);
            _rightEyeLabel = _kit.Label(_rightEyePane.transform, "OD", LabelKind.StreamChip, TextAlignmentOptions.Center);
            _kit.Size(_rightEyeLabel.rectTransform, minH: 22, prefH: 22, flexH: 0);
            _streamRight = MakeStreamView(_rightEyePane.transform);
            _rightEyePane.SetActive(false);

            var leftPane = _kit.Box(eyes, "LeftEyePane", true, 6, null, expandW: true, expandH: false);
            _kit.Size(leftPane, flexW: 1);
            _leftEyeLabel = _kit.Label(leftPane, "Ambos ojos", LabelKind.StreamChip, TextAlignmentOptions.Center);
            _kit.Size(_leftEyeLabel.rectTransform, minH: 22, prefH: 22, flexH: 0);
            _streamLeft = MakeStreamView(leftPane);

            // Boton "Pantalla completa": overlay ignoreLayout anclado a la esquina
            // superior derecha del StreamPanel (no participa del HorizontalLayoutGroup
            // del panel, ver PinTopRight).
            var fullscreenBtn = _kit.Button(stream, "Pantalla completa", BtnStyle.Overlay, false, 36, 13);
            PinTopRight(fullscreenBtn.GetComponent<RectTransform>(), fullscreenBtn.GetComponent<LayoutElement>(), 8, 8);
            fullscreenBtn.OnClick = OpenFullscreenStream;

            // --- Scroll de controles (derecha) ---
            var scroll = _kit.ScrollColumn(body, out var content);
            _kit.Size(scroll.GetComponent<RectTransform>(), minW: 360, flexW: 1, flexH: 1);

            BuildEyeCard(content);
            BuildLensesCard(content);
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

        // Overlay de pantalla completa del stream: 1 imagen (misma lente en ambos
        // ojos, incluido sin lente en ninguno) o 2 lado a lado (blend, lentes
        // distintas por ojo) -- ver RefreshFullscreenUI, que se llama desde
        // RefreshVisionUI cada vez que cambia el vision_state. Las RawImage reusan
        // las MISMAS Texture2D del panel normal (_texLeft/_texRight, ver
        // OnSessionFrame): no hay una segunda decodificacion de JPG.
        private void BuildFullscreenStream(Transform parent)
        {
            _fullscreenStream = new GameObject("FullscreenStream", typeof(RectTransform));
            _fullscreenStream.transform.SetParent(parent, false);
            Stretch(_fullscreenStream.GetComponent<RectTransform>());
            _fullscreenStream.SetActive(false);

            // Fondo solido (deliberadamente NO tematizado: un visor de stream a
            // pantalla completa se comporta como un "lightbox", independiente del
            // tema claro/oscuro de la app) que ademas cierra el overlay al tocarlo.
            // UnityEngine.UI.Button liso (no TabletButton): esto es una capa de tap
            // invisible de borde a borde, no un control visible con fill/borde/texto.
            var bg = new GameObject("FullscreenBg", typeof(RectTransform), typeof(Image), typeof(Button));
            bg.transform.SetParent(_fullscreenStream.transform, false);
            Stretch(bg.GetComponent<RectTransform>());
            bg.GetComponent<Image>().color = Color.black;
            var bgBtn = bg.GetComponent<Button>();
            bgBtn.transition = Selectable.Transition.None;
            bgBtn.onClick.AddListener(CloseFullscreenStream);

            var row = _kit.Box(_fullscreenStream.transform, "FullscreenRow", false, 16,
                new RectOffset(28, 28, 28, 28), expandW: true, expandH: true);
            // _kit.Box solo agrega un LayoutGroup que controla a sus HIJOS: no
            // dimensiona su propio RectTransform. FullscreenRow no cuelga de ningun
            // ancestro con LayoutGroup, asi que sin este Stretch() explicito queda
            // con el rect default de Unity (100x100 en la esquina) -- el mismo
            // tratamiento que ya tiene FullscreenBg mas arriba.
            Stretch(row);

            // Orden clinico OD-primero (convencion OD/OI, ver docs/tablet.md): el pane
            // derecho se crea ANTES que el izquierdo para quedar a la IZQUIERDA de la
            // pantalla en el FullscreenRow horizontal. "FsLeftPane" sigue siendo el pane
            // siempre-activo que ademas cubre la vista compartida "Ambos ojos" en
            // no-blend -- su posicion ahi es irrelevante porque es el unico hijo activo.
            _fsRightPane = _kit.Box(row, "FsRightPane", true, 6, null, expandW: true, expandH: false).gameObject;
            _kit.Size(_fsRightPane.GetComponent<RectTransform>(), flexW: 1);
            _fsRightLabel = _kit.Label(_fsRightPane.transform, "OD", LabelKind.StreamChip, TextAlignmentOptions.Center);
            _kit.Size(_fsRightLabel.rectTransform, minH: 26, prefH: 26, flexH: 0);
            _fsStreamRight = MakeStreamView(_fsRightPane.transform);
            _fsStreamRight.raycastTarget = false;
            _fsRightPane.SetActive(false);

            var leftPane = _kit.Box(row, "FsLeftPane", true, 6, null, expandW: true, expandH: false);
            _kit.Size(leftPane, flexW: 1);
            _fsLeftLabel = _kit.Label(leftPane, "Ambos ojos", LabelKind.StreamChip, TextAlignmentOptions.Center);
            _kit.Size(_fsLeftLabel.rectTransform, minH: 26, prefH: 26, flexH: 0);
            _fsStreamLeft = MakeStreamView(leftPane);
            _fsStreamLeft.raycastTarget = false; // deja pasar el tap al fondo (tambien cierra)

            var closeBtn = _kit.Button(_fullscreenStream.transform, "Cerrar", BtnStyle.Overlay, false, 40, 14);
            PinTopRight(closeBtn.GetComponent<RectTransform>(), closeBtn.GetComponent<LayoutElement>(), 16, 16);
            closeBtn.OnClick = CloseFullscreenStream;
        }

        // ============================================================
        // Update semi-automatico (F5) -- cartel modal, ver docs/updates.md
        // ============================================================
        // Overlay full-screen: scrim semi-opaco (deliberadamente NO tematizado,
        // igual criterio que FullscreenBg -- un modal se comporta como
        // "lightbox" encima de CUALQUIER pantalla/tema) + card centrada con
        // titulo/version/changelog/estado + 2 botones (primario/secundario) cuyo
        // texto/handler/visibilidad cambian segun el estado (Available/
        // Downloading/Ready/Failed) en vez de construir 4 pares de botones.
        private void BuildUpdateScreen(Transform parent)
        {
            _updateScreen = new GameObject("UpdateScreen", typeof(RectTransform));
            _updateScreen.transform.SetParent(parent, false);
            Stretch(_updateScreen.GetComponent<RectTransform>());
            _updateScreen.SetActive(false);

            var scrim = new GameObject("UpdateScrim", typeof(RectTransform), typeof(Image));
            scrim.transform.SetParent(_updateScreen.transform, false);
            Stretch(scrim.GetComponent<RectTransform>());
            scrim.GetComponent<Image>().color = new Color(0f, 0f, 0f, 0.6f);

            var card = _kit.Card(_updateScreen.transform, "UpdateCard");
            card.anchorMin = card.anchorMax = new Vector2(0.5f, 0.5f);
            card.pivot = new Vector2(0.5f, 0.5f);
            card.sizeDelta = new Vector2(480, 0);
            var fit = card.gameObject.AddComponent<ContentSizeFitter>();
            fit.verticalFit = ContentSizeFitter.FitMode.PreferredSize;

            _updateTitleLabel = _kit.Label(card, "Actualización disponible", LabelKind.Title, TextAlignmentOptions.Center);
            _updateVersionLabel = _kit.Label(card, "", LabelKind.Subtitle, TextAlignmentOptions.Center);
            _updateChangelogLabel = _kit.Label(card, "", LabelKind.Hint, TextAlignmentOptions.Left);
            _updateStatusLabel = _kit.Label(card, "", LabelKind.Hint, TextAlignmentOptions.Center);
            _kit.Spacer(card, 4, false);

            var row = _kit.Box(card, "UpdateButtons", false, 8, null, expandW: true);
            _updateSecondaryBtn = _kit.Button(row, "Ahora no", BtnStyle.Ghost, false, 48, 16);
            _kit.Size(_updateSecondaryBtn.GetComponent<RectTransform>(), flexW: 1);
            _updatePrimaryBtn = _kit.Button(row, "Actualizar", BtnStyle.Accent, false, 48, 16);
            _kit.Size(_updatePrimaryBtn.GetComponent<RectTransform>(), flexW: 1);
        }

        // ---- UpdateManager -> UI (suscripcion null-safe: UpdateManager es un
        // singleton bootstrapeado por RuntimeInitializeOnLoad, deberia existir
        // ya para cuando corre este Start(), pero si algo fallo en su
        // inicializacion no hay por que romper el resto de la tablet) ----
        private void SubscribeUpdateEvents()
        {
            var um = UpdateManager.Instance;
            if (um == null)
            {
                Debug.LogWarning("[Tablet] UpdateManager no encontrado; UI de actualizaciones deshabilitada.");
                return;
            }
            um.UpdateAvailable += OnUpdateAvailable;
            um.DownloadProgress += OnUpdateDownloadProgress;
            um.UpdateFailed += OnUpdateFailed;
            um.ReadyToInstall += OnUpdateReadyToInstall;
        }

        private void UnsubscribeUpdateEvents()
        {
            var um = UpdateManager.Instance;
            if (um == null) return;
            um.UpdateAvailable -= OnUpdateAvailable;
            um.DownloadProgress -= OnUpdateDownloadProgress;
            um.UpdateFailed -= OnUpdateFailed;
            um.ReadyToInstall -= OnUpdateReadyToInstall;
        }

        private void OnUpdateAvailable(UpdateLogic.UpdateManifest manifest, bool forced)
        {
            _updateManifest = manifest;
            _updateForced = forced;
            _updateTitleLabel.text = "Actualización disponible";
            _updateVersionLabel.text = $"v{Application.version} → v{manifest.ApkVersion}";
            _updateChangelogLabel.text = manifest.Changelog ?? "";
            _updateStatusLabel.text = "";
            _updatePrimaryBtn.gameObject.SetActive(true);
            _updatePrimaryBtn.Label.text = "Actualizar";
            _updatePrimaryBtn.OnClick = OnUpdateAcceptPressed;
            _updateSecondaryBtn.gameObject.SetActive(!forced); // "Ahora no" oculto si es forzada
            _updateSecondaryBtn.Label.text = "Ahora no";
            _updateSecondaryBtn.OnClick = OnUpdatePostponePressed;
            _updateScreen.SetActive(true);
        }

        private void OnUpdateAcceptPressed()
        {
            UpdateManager.Instance?.AcceptUpdate();
            ShowUpdateDownloading();
        }

        private void ShowUpdateDownloading()
        {
            _updateTitleLabel.text = "Descargando actualización";
            _updateStatusLabel.text = "Descargando… 0 %";
            _updatePrimaryBtn.gameObject.SetActive(false);
            _updateSecondaryBtn.gameObject.SetActive(true); // Cancelar siempre disponible, incluso si es forzada
            _updateSecondaryBtn.Label.text = "Cancelar";
            _updateSecondaryBtn.OnClick = OnUpdateCancelPressed;
        }

        private void OnUpdateDownloadProgress(float progress)
        {
            if (_updateStatusLabel == null) return;
            _updateStatusLabel.text = $"Descargando… {Mathf.RoundToInt(progress * 100f)} %";
        }

        // Sin API de cancelacion previa en UpdateManager (F3/F4 no la necesitaban,
        // no habia UI todavia) -- se agrego UpdateManager.CancelDownload() en esta
        // tarea (F5) para este boton, ver docs/updates.md.
        private void OnUpdateCancelPressed()
        {
            UpdateManager.Instance?.CancelDownload();
            UpdateManager.Instance?.PostponeUpdate();
            HideUpdateScreen();
        }

        private void OnUpdateReadyToInstall(string path)
        {
            _updateTitleLabel.text = "Descarga verificada";
            _updateStatusLabel.text = "Descarga verificada";
            _updatePrimaryBtn.gameObject.SetActive(true);
            _updatePrimaryBtn.Label.text = "Instalar";
            _updatePrimaryBtn.OnClick = OnUpdateInstallPressed;
            _updateSecondaryBtn.gameObject.SetActive(false);
            _updateScreen.SetActive(true);
        }

        private void OnUpdateInstallPressed()
        {
            UpdateManager.Instance?.LaunchInstall();
            HideUpdateScreen();
        }

        private void OnUpdateFailed(string message)
        {
            _updateTitleLabel.text = "Error al actualizar";
            _updateStatusLabel.text = FriendlyUpdateError(message);
            _updatePrimaryBtn.gameObject.SetActive(true);
            _updatePrimaryBtn.Label.text = "Reintentar";
            _updatePrimaryBtn.OnClick = OnUpdateRetryPressed;
            _updateSecondaryBtn.gameObject.SetActive(!_updateForced); // "Cerrar" oculto si es forzada
            _updateSecondaryBtn.Label.text = "Cerrar";
            _updateSecondaryBtn.OnClick = OnUpdateClosePressed;
            _updateScreen.SetActive(true);
        }

        private void OnUpdateRetryPressed()
        {
            UpdateManager.Instance?.RetryDownload();
            ShowUpdateDownloading();
        }

        private void OnUpdateClosePressed() => HideUpdateScreen();

        private void OnUpdatePostponePressed()
        {
            UpdateManager.Instance?.PostponeUpdate();
            HideUpdateScreen();
        }

        private void HideUpdateScreen() => _updateScreen?.SetActive(false);

        private static string FriendlyUpdateError(string raw) =>
            raw == "sha_mismatch" ? "La descarga no pasó la verificación de integridad." : raw;

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

        // Ancla un widget ya dimensionado por _kit.Button (Size() le puso min/pref
        // en el LayoutElement) a la esquina superior derecha de su padre, sacandolo
        // del layout group (ignoreLayout) para que no participe del flujo normal de
        // hijos -- lo usan el boton "Pantalla completa" (StreamPanel) y "Cerrar"
        // (overlay de pantalla completa).
        private static void PinTopRight(RectTransform rt, LayoutElement le, float marginX, float marginY)
        {
            le.ignoreLayout = true;
            rt.sizeDelta = new Vector2(le.preferredWidth, le.preferredHeight);
            rt.anchorMin = rt.anchorMax = new Vector2(1f, 1f);
            rt.pivot = new Vector2(1f, 1f);
            rt.anchoredPosition = new Vector2(-marginX, -marginY);
        }
    }
}
