using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using Newtonsoft.Json.Linq;
using Simulador.Localization;
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
    /// split en modo blend, cards de ojo/lentes/ajuste fino/astigmatismo) con tema
    /// oscuro/claro Inter.
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
        // Sprite del simbolo de marca (rebranding IOLSIMULATOR, 2026-09-03), cargado una vez en
        // Start() igual que las fuentes de TabletFonts/ (Resources.Load nuevo justificado: la UI
        // es 100% por codigo sin prefabs, ver docs/tablet.md).
        private Sprite _brandMark;
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

        // --- Toggle de HUD del visor (comando "set_hud", ver docs/networking.md) ---
        // Estado puramente local/optimista: no hay campo en vision_state que
        // confirme el estado real del HUD (fire-and-forget, igual que
        // set_astigmatism). Arranca en "visible" y se resetea ahi en cada conexion
        // nueva (ver OnSessionConnected) para no arrastrar el toggle de una sesion
        // anterior.
        private TabletButton _hudToggleBtn;
        private bool _hudVisible = true;

        // --- Confirmacion de "Desvincular" (header Pro, overlay modal) ---
        // Revocar el emparejamiento por token exige volver a pedir el PIN -- un tap
        // accidental en el boton discreto del header ya no dispara _session.Unpair()
        // directo (ver OnUnpairPressed/BuildUnpairConfirm).
        private GameObject _unpairConfirm;

        // --- Confirmacion de cambio de idioma (D2, header) ---
        // Cambiar el idioma reinicia la app (la UI se construye UNA vez en Start(),
        // no hay repintado en caliente -- ver docs/localizacion.md/docs/tablet.md
        // "Idioma fijo al arrancar"). El codigo destino se calcula al abrir el
        // popup (el idioma activo no cambia durante la sesion) y se persiste
        // recien al confirmar.
        private GameObject _langConfirm;
        private string _pendingLangCode;

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
        // D1: nombre editable de la lente en edicion (visible solo si canSave, ver
        // BuildParamsEditor); reemplaza el "nombre" crudo del catalogo al guardar.
        private TMP_InputField _lensNameEdit;
        private string _editingLensId = "";
        private readonly Dictionary<string, ParamRowView> _paramRows = new();
        private readonly Dictionary<string, float> _paramDefaults = new();

        // --- Lentes custom (P7, solo modo Pro) ---
        private TabletButton _saveLensButton, _deleteLensButton;
        private bool _deleteArmed;                 // doble tap para confirmar Eliminar
        private TMP_InputField _createNameEdit, _createDescEdit;
        private TabletButton _createGenericToggle; // solo visible si el visor es admin
        private GameObject _createGenericRow;
        private TMP_Text _createStatus;
        // Status inline de "Guardar en la lente"/"Eliminar lente" (Ajuste fino),
        // mismo patron visual que _createStatus. Coroutines: cada label tiene a lo
        // sumo UNA pendiente (busy-timeout o auto-limpieza), ver SetLensStatus.
        private TMP_Text _ownLensStatus;
        private Coroutine _createStatusRoutine, _ownLensStatusRoutine;

        // --- Modo Standard (P7): stream fullscreen + carrusel de 5 parametros ---
        private GameObject _standardScreen;
        private RectTransform _stdScenarioList;
        private readonly Dictionary<string, TabletButton> _stdScenarioButtons = new();
        private GameObject _stdRightPane;
        private RawImage _stdStreamLeft, _stdStreamRight;
        private TMP_Text _stdLeftLabel, _stdRightLabel;
        private readonly Dictionary<string, (TabletButton btn, Image ring)> _stdIcons = new();
        private string _stdSelectedKey = "";
        private GameObject _stdSliderPanel;
        private TMP_Text _stdSliderTitle, _stdSliderValue, _stdAxisValue;
        private Slider _stdSlider, _stdAxisSlider;
        private GameObject _stdAxisRow;
        private GameObject _stdLensOverlay;
        private RectTransform _stdLensListBox;
        private GameObject _stdEyePickRow;
        private string _stdPendingLensId = "";

        // Carrusel Standard: (clave de catalogo, etiqueta corta). El astigmatismo
        // abre ademas el slider secundario del eje (astig_axis_deg).
        private static readonly (string key, string label)[] StandardCarousel =
        {
            ("astig_magnitude", "Astigmatismo"),
            ("halo_intensity", "Halos"),
            ("halo_extra_rings", "Dilatación"),
            ("destello_intensity", "Destellos"),
            ("destello_rayos", "Rayos"),
        };

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
        // Fase C (kiosco): true cuando ReadyToInstall llego con una sesion activa con el
        // visor (clinico en consulta) -- se instala recien cuando la tablet vuelva a
        // quedar ociosa, ver OnUpdateReadyToInstall/ShowConnectScreen.
        private bool _installWhenIdle;
        // CRITICO #2 (correcciones, ver docs/updates.md): watchdog de la instalacion
        // silenciosa -- si StartedSilent no llega a un resultado (STATUS_SUCCESS mata
        // el proceso; cualquier otro estado llama OnSilentInstallResult via
        // InstallResultReceiver.java) en este plazo, se reporta como fallo y se
        // destraba el modal "Instalando...". Cancelado si OnSilentInstallResult
        // llega antes (ver HandleSilentInstallFailure).
        private const float SilentInstallWatchdogSeconds = 120f;
        private Coroutine _silentInstallWatchdogCo;
        // MENOR (correcciones, revision final): true apenas HandleSilentInstallFailure
        // resuelve el resultado de ESTA instalacion silenciosa (watchdog o
        // OnSilentInstallResult, lo que llegue primero) -- hace no-op al segundo
        // llamado si el watchdog dispara justo antes de que llegue el
        // INSTALL_FAILED_* real (o viceversa), evitando un update_install_failed
        // duplicado. Se resetea en LaunchSilentInstall() al arrancar cada intento.
        private bool _silentInstallResolved;

        // --- Stream a pantalla completa ---
        // Overlay que reusa las MISMAS Texture2D del stream normal (_texLeft/
        // _texRight, ver OnSessionFrame): RawImage nuevo por ojo apuntando a la
        // misma textura, sin decodificar el JPG una segunda vez. El modo (1 imagen
        // o 2 lado a lado) sigue a blend_active, igual que el panel normal -- ver
        // RefreshFullscreenUI.
        private GameObject _fullscreenStream, _fsRightPane;
        private RawImage _fsStreamLeft, _fsStreamRight;
        private TMP_Text _fsLeftLabel, _fsRightLabel;

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
            _brandMark = Resources.Load<Sprite>("TabletBrand/logo_mark");
            _isDark = LoadThemePref();
            _kit = new TabletUiKit(TabletPalette.For(_isDark), regular, semibold);
            // D2: fija el idioma ANTES de construir la UI (BuildUI ya toca
            // ParamMeta/L10n.T) -- override desde ui_prefs.cfg ("lang="); null/
            // invalido cae al idioma del sistema (ver docs/localizacion.md).
            L10n.Initialize(LoadLangPref());

            _texLeft = new Texture2D(2, 2, TextureFormat.RGB24, false);
            _texRight = new Texture2D(2, 2, TextureFormat.RGB24, false);

            BuildUI();
            ApplyTheme(_isDark);
            SubscribeUpdateEvents();

            // Kiosco (Fase A, ver docs/tablet.md): no-op en una tablet de
            // desarrollo sin Device Owner (KioskManager.IsDeviceOwner false).
            // MAYOR #12 (correcciones): si la tablet quedo en "modo servicio"
            // (flag persistido por KioskManager.EnterServiceMode -- ver
            // OnServiceExitConfirmPressed), NO reaplicar el kiosco: se queda
            // abierta con el banner de servicio hasta que el operador la saque
            // de ese modo (OnLeaveServiceModePressed).
            if (KioskManager.IsDeviceOwner && KioskManager.IsInServiceMode)
            {
                ShowServiceModeBanner();
            }
            else
            {
                KioskManager.ApplyPolicies();
                KioskManager.EnterLockTask();
            }

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
            _session.LensSaved += OnLensSaved;
            _session.LensError += OnLensError;
            _session.Begin();

            ShowConnectScreen(L10n.T("connect.searching"));
        }

        private void Update()
        {
            _session.Update(Time.deltaTime);

            _discoveryTimer += Time.deltaTime;
            if (_discoveryTimer >= 1f) { _discoveryTimer = 0f; RefreshDiscovered(); }

            _footerTimer += Time.deltaTime;
            if (_footerTimer >= 1f) { _footerTimer = 0f; UpdateFooter(); }
        }

        // Kiosco (Fase A): Android puede soltar el lock task tras ciertas
        // transiciones (volver de Ajustes, recibir una llamada, etc.) -- re-
        // entrar al volver a foco es el patron estandar. Tambien refresca el
        // label de red (Fase B, "Red Wi-Fi"): al volver de Ajustes de WiFi el
        // SSID pudo haber cambiado.
        private void OnApplicationPause(bool pause)
        {
            if (pause) return;
            // MAYOR #12 (correcciones): en modo servicio no hay que reentrar a
            // lock task al volver a foco -- ver Start()/EnterServiceMode.
            if (!KioskManager.IsInServiceMode) KioskManager.EnterLockTask();
            RefreshNetworkInfo();
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
            if (_session.IsReconnecting) SetReconnectStatus(L10n.T("connect.authenticating"));
            else SetConnectStatus(L10n.T("connect.authenticating"));
        }

        private void OnSessionAuthOk() => SetConnectStatus(L10n.T("connect.auth_ok_waiting_catalog"));

        private void OnSessionPinScreenRequested(string message) => ShowPinScreen(_session.CurrentHost, message);

        private void OnSessionShowConnectScreenRequested(string message, bool isError) => ShowConnectScreen(message, isError);

        private void OnSessionReconnectStarted(string message) => ShowReconnectScreen(_session.CurrentHost, message);

        private void OnSessionReconnectStatusChanged(string message) => SetReconnectStatus(message);

        private void OnSessionHello(JArray lenses)
        {
            RebuildLensList(lenses);
            RebuildScenarioList();
            RefreshVisionUI();
            // P7: el toggle "generica" del alta de lentes solo aparece si el visor
            // conectado es admin (el modo puede cambiar entre hellos: re-verify).
            if (_createGenericRow != null) _createGenericRow.SetActive(_session.IsAdmin);
            // P7: routing por modo del visor (hello). "standard" -> UI simplificada;
            // cualquier otro valor (o visor viejo sin campo) -> UI Pro completa.
            if (_session.Mode == "standard")
            {
                // El HUD de diagnostico (FPS/PIN, ver docs/networking.md "set_hud") no
                // tiene sentido en manos del paciente/operador de modo Standard -- se
                // fuerza oculto en CADA hello (conexion inicial, reconexion o refresh),
                // sin depender de un boton que Standard ni siquiera tiene.
                _hudVisible = false;
                _session.SendCommand(new JObject { ["cmd"] = "set_hud", ["visible"] = false });
                RebuildStandardLensList();
                ShowStandardScreen();
            }
            else
            {
                // Pro/admin: re-afirma el estado optimista de ESTA tablet (recien
                // reseteado a "visible" por OnSessionConnected) en cada hello -- cierra
                // parcialmente el mismatch de docs/networking.md Pendientes (una
                // tablet Standard que ocultaba el HUD y se desconectaba dejaba el
                // visor sin HUD para el proximo emparejamiento; ver tambien la red de
                // seguridad en NetworkController.OnClientDisconnected/"unpair").
                _session.SendCommand(new JObject { ["cmd"] = "set_hud", ["visible"] = _hudVisible });
                ShowMainScreen();
            }
        }

        private void OnSessionVisionStateChanged()
        {
            RefreshVisionUI();
            SyncParamRowsFromState();
            RefreshStandardSliders();
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
                    if (_stdStreamRight != null) { _stdStreamRight.texture = _texRight; _stdStreamRight.color = Color.white; }
                }
            }
            else if (eye == 'L')
            {
                if (ImageConversion.LoadImage(_texLeft, jpg))
                {
                    _streamLeft.texture = _texLeft; _streamLeft.color = Color.white;
                    _fsStreamLeft.texture = _texLeft; _fsStreamLeft.color = Color.white;
                    if (_stdStreamLeft != null) { _stdStreamLeft.texture = _texLeft; _stdStreamLeft.color = Color.white; }
                }
            }
            else // 'B' o desconocido -> mismo frame en ambos paneles
            {
                if (ImageConversion.LoadImage(_texLeft, jpg))
                {
                    _streamLeft.texture = _texLeft; _streamLeft.color = Color.white;
                    _fsStreamLeft.texture = _texLeft; _fsStreamLeft.color = Color.white;
                    if (_stdStreamLeft != null) { _stdStreamLeft.texture = _texLeft; _stdStreamLeft.color = Color.white; }
                    if (ImageConversion.LoadImage(_texRight, jpg))
                    {
                        _streamRight.texture = _texRight; _streamRight.color = Color.white;
                        _fsStreamRight.texture = _texRight; _fsStreamRight.color = Color.white;
                        if (_stdStreamRight != null) { _stdStreamRight.texture = _texRight; _stdStreamRight.color = Color.white; }
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
                _themeToggle.Label.text = dark ? L10n.T("main.theme_toggle_light") : L10n.T("main.theme_toggle_dark");
            SaveThemePref();
        }

        // D2: mini lector/escritor INI compartido por "dark_mode" y "lang" -- antes
        // SaveThemePref sobreescribia el archivo ENTERO con solo "dark_mode=", lo
        // que hubiera borrado "lang=" en cada toggle de tema (y viceversa). Formato
        // sin cambios ("[ui]" + una linea "clave=valor" por preferencia, ver
        // docs/tablet.md "Prefs"): un ui_prefs.cfg viejo con solo "dark_mode=" sigue
        // leyendose igual.
        private Dictionary<string, string> ReadPrefsFile()
        {
            var dict = new Dictionary<string, string>();
            try
            {
                if (System.IO.File.Exists(PrefsPath))
                    foreach (var line in System.IO.File.ReadAllLines(PrefsPath))
                    {
                        int eq = line.IndexOf('=');
                        if (eq <= 0) continue;
                        dict[line.Substring(0, eq).Trim()] = line.Substring(eq + 1).Trim();
                    }
            }
            catch { }
            return dict;
        }

        private void WritePrefsFile(string key, string value)
        {
            var dict = ReadPrefsFile();
            dict[key] = value;
            try
            {
                var sb = new System.Text.StringBuilder("[ui]\n");
                foreach (var kv in dict) sb.Append(kv.Key).Append('=').Append(kv.Value).Append('\n');
                System.IO.File.WriteAllText(PrefsPath, sb.ToString());
            }
            catch { }
        }

        private bool LoadThemePref()
        {
            if (ReadPrefsFile().TryGetValue("dark_mode", out var v)) return v.Contains("true") || v.Contains("1");
            return true;
        }

        private void SaveThemePref() => WritePrefsFile("dark_mode", _isDark ? "true" : "false");

        // D2: override de idioma persistido junto a dark_mode (ver
        // docs/localizacion.md / docs/tablet.md "Prefs"). null si no hay override
        // guardado (o el archivo no existe todavia) -- L10n.Initialize cae al
        // idioma del sistema en ese caso.
        private string LoadLangPref()
        {
            string v = ReadPrefsFile().TryGetValue("lang", out var lang) ? lang : null;
            return string.IsNullOrWhiteSpace(v) ? null : v;
        }

        private void SaveLangPref(string code) => WritePrefsFile("lang", code);

        // ============================================================
        // Pantallas
        // ============================================================
        private void ShowConnectScreen(string message, bool isError = false)
        {
            CloseFullscreenStream(); // sesion interrumpida: no dejar el overlay de stream congelado
            CloseUnpairConfirm();
            // MENOR (correcciones): tambien cerrar estos dos popups modales --
            // antes solo se cerraba UnpairConfirm, dejando LangConfirm/
            // ServiceExitConfirm potencialmente abiertos sobre una pantalla que
            // ya no es la que los abrio.
            CloseLangConfirm();
            CloseServiceExitConfirm();
            _connectScreen.SetActive(true);
            _pinScreen.SetActive(false);
            _reconnectScreen.SetActive(false);
            _mainScreen.SetActive(false);
            _standardScreen.SetActive(false);
            SetConnectStatus(message, isError);
            RefreshNetworkInfo();
            RequestLocationPermissionOnce();

            // Fase C (kiosco): este es el punto por el que pasa TODA vuelta a ocioso
            // (fin de sesion, PIN cancelado, reconexion agotada, etc.) -- si un update
            // quedo diferido por sesion activa (OnUpdateReadyToInstall), instalarlo
            // recien ahora que el clinico ya no esta en consulta.
            if (_installWhenIdle)
            {
                _installWhenIdle = false;
                LaunchSilentInstall();
            }
        }

        private void ShowMainScreen()
        {
            // MENOR (correcciones): ver comentario identico en ShowConnectScreen.
            CloseLangConfirm();
            CloseServiceExitConfirm();
            _connectScreen.SetActive(false);
            _pinScreen.SetActive(false);
            _reconnectScreen.SetActive(false);
            _standardScreen.SetActive(false);
            _mainScreen.SetActive(true);
        }

        // P7: pantalla del modo Standard (stream fullscreen + carrusel).
        private void ShowStandardScreen()
        {
            _connectScreen.SetActive(false);
            _pinScreen.SetActive(false);
            _reconnectScreen.SetActive(false);
            _mainScreen.SetActive(false);
            CloseFullscreenStream(); // el standard ya ES fullscreen
            _standardScreen.SetActive(true);
        }

        // Pantalla de PIN: se intercala entre ConnectScreen y MainScreen cuando hace
        // falta el PIN de emparejamiento (host sin token persistente valido, o
        // reintento tras auth_fail/auth_locked, ver OnSessionPinScreenRequested).
        private void ShowPinScreen(string host, string message = "")
        {
            CloseFullscreenStream();
            CloseUnpairConfirm();
            _pinPendingHost = host;
            _connectScreen.SetActive(false);
            _mainScreen.SetActive(false);
            _standardScreen.SetActive(false);
            _reconnectScreen.SetActive(false);
            _pinScreen.SetActive(true);
            _pinHostLabel.text = L10n.T("pin.host_prefix", host);
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
            CloseUnpairConfirm();
            _connectScreen.SetActive(false);
            _pinScreen.SetActive(false);
            _mainScreen.SetActive(false);
            _standardScreen.SetActive(false);
            _reconnectScreen.SetActive(true);
            _reconnectHostLabel.text = L10n.T("pin.host_prefix", host);
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
                if (!_session.IsConnecting) SetConnectStatus(L10n.T("connect.searching"));
                return;
            }
            if (!_session.IsConnecting) SetConnectStatus(L10n.T("connect.tap_to_connect"));
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
                SetConnectStatus(L10n.T("connect.enter_ip_or_tap"), true);
                return;
            }
            if (_session.TryGetCachedToken(host, out var savedToken))
                BeginConnectWithToken(host, savedToken);
            else
                ShowPinScreen(host);
        }

        private void OnPinCancelPressed() => ShowConnectScreen(L10n.T("connect.tap_to_connect"));

        private void OnPinConfirmPressed()
        {
            string pin = (_pinEdit.text ?? "").Trim();
            if (pin.Length != 6)
            {
                SetPinStatus(L10n.T("pin.must_be_six_digits"), true);
                return;
            }
            BeginConnect(_pinPendingHost, pin);
        }

        private void BeginConnect(string host, string pin)
        {
            ShowConnectScreen(L10n.T("connect.connecting_to", host));
            _session.Connect(host, pin);
        }

        private void BeginConnectWithToken(string host, string token)
        {
            ShowConnectScreen(L10n.T("connect.connecting_to", host));
            _session.ConnectWithToken(host, token);
        }

        private void OnDisconnectPressed() => _session.Disconnect();

        // Boton "Desvincular" (header): abre el popup de confirmacion (ver
        // BuildUnpairConfirm) en vez de desvincular directo -- revocar el
        // emparejamiento por token exige volver a pedir el PIN, no es una accion
        // que convenga disparar por un tap accidental sobre un boton discreto.
        private void OnUnpairPressed() => _unpairConfirm?.SetActive(true);

        // Confirmado en el popup: revoca el token de esta tablet en el visor y
        // olvida el emparejamiento local con el host actual (ver
        // TabletSession.Unpair) -- vuelve al ConnectScreen y la proxima conexion a
        // este visor va a pedir el PIN de nuevo.
        private void OnUnpairConfirmed()
        {
            CloseUnpairConfirm();
            _session.Unpair();
        }

        private void CloseUnpairConfirm() => _unpairConfirm?.SetActive(false);

        // D2: toggle de idioma del header -- abre el popup de confirmacion (mismo
        // patron que Desvincular) en vez de aplicar el cambio directo, porque
        // cambiar de idioma reinicia la app (ver comentario del campo
        // _langConfirm). El codigo destino se fija una sola vez al abrir (el
        // idioma activo no cambia durante la sesion).
        private void OnLangTogglePressed()
        {
            _pendingLangCode = L10n.Lang == "es" ? "en" : "es";
            _langConfirm?.SetActive(true);
        }

        private void OnLangConfirmPressed()
        {
            CloseLangConfirm();
            SaveLangPref(_pendingLangCode);
            // Reinicia la app para aplicar el idioma nuevo (la UI se construye UNA
            // vez en Start(), ver docs/tablet.md "Idioma fijo al arrancar"). Bajo
            // lock task, Application.Quit() es un no-op real -- Android bloquea el
            // finish() de la Activity raiz de la tarea bloqueada ("Not finishing
            // task in lock task mode", hallazgo 2026-09-03 en la PHILCO, ver
            // docs/tablet.md) -- asi que en kiosco (Device Owner) hay que matar el
            // proceso (KioskManager.RestartProcess()) para que la HOME persistente
            // relance la app ya en el idioma nuevo; en una tablet de desarrollo
            // (sin Device Owner) Application.Quit() cierra la app normalmente y hay
            // que reabrirla a mano; en el Editor sigue siendo un no-op documentado
            // de Unity (no sale de Play Mode).
            if (KioskManager.IsDeviceOwner)
                KioskManager.RestartProcess();
            else
                Application.Quit();
        }

        private void CloseLangConfirm() => _langConfirm?.SetActive(false);

        // P5.4: refresh en caliente -- pide {"cmd":"refresh"}; el visor responde con
        // el mismo payload del "hello" (BuildHello reusado del lado visor) y
        // OnSessionHello ya sabe reconstruir catalogo/escenarios/vision_state (la
        // misma rama que usa una reconexion exitosa, P2.5), asi que no hace falta
        // parsear nada nuevo del lado tablet.
        private void OnRefreshPressed()
        {
            if (!_session.IsWsOpen) return;
            _session.SendCommand(new JObject { ["cmd"] = "refresh" });
        }

        // B3: boton "Recentrar" (header Pro y StdTopBar de Standard) -- pide
        // {"cmd":"recenter"} para recalibrar la posicion del paciente en el
        // escenario actual. Fire-and-forget como refresh/set_hud (sin ack); accion
        // clinica no destructiva, sin gating de admin.
        private void OnRecenterPressed()
        {
            if (!_session.IsWsOpen) return;
            _session.SendCommand(new JObject { ["cmd"] = "recenter" });
        }

        // Boton "Ocultar HUD" / "Mostrar HUD" (header): comando "set_hud" (ver
        // docs/networking.md), togglea el HUD de diagnostico del visor (FPS/lentes/
        // halos/PIN). Fire-and-forget como set_astigmatism/load_scenario -- no hay
        // ack ni campo en vision_state que confirme el estado real del HUD, asi que
        // _hudVisible es puramente el estado optimista de ESTA tablet (se resetea a
        // "visible" en cada conexion nueva, ver OnSessionConnected).
        private void OnHudTogglePressed()
        {
            if (!_session.IsWsOpen) return;
            _hudVisible = !_hudVisible;
            UpdateHudToggleLabel();
            _session.SendCommand(new JObject { ["cmd"] = "set_hud", ["visible"] = _hudVisible });
        }

        private void UpdateHudToggleLabel()
        {
            if (_hudToggleBtn?.Label != null)
                _hudToggleBtn.Label.text = _hudVisible ? L10n.T("main.hud_hide") : L10n.T("main.hud_show");
        }

        private void OnReconnectCancelPressed()
        {
            if (!_session.CancelReconnect()) ShowConnectScreen(L10n.T("connect.tap_to_connect"));
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
                    (string)lo["descripcion"], OnLensSelected, (string)lo["origen"]);
                _lensCards[id] = card;
                // P8: drag-reorder con long-press (ver LensCardReorder), solo para
                // un visor ADMIN y solo sobre lentes de CATALOGO -- mismo criterio
                // que "ownCustom"/"fullEdit" de BuildParamsEditor (origen !=
                // "custom" trata la tolerancia legacy "generic" igual que
                // catalogo). Las lentes propias NUNCA reciben este componente:
                // quedan siempre despues en la lista y el drag las clampea afuera.
                if (_session.IsAdmin && card.Origen != "custom")
                    LensCardReorder.Attach(card.gameObject, OnLensesReordered);
            }
        }

        // P8: ack del drag-reorder de catalogo (admin). El nuevo orden ya se ve
        // en la UI (SetSiblingIndex en vivo durante el drag, ver
        // LensCardReorder); esto solo lo persiste server-side. El visor lo
        // traduce a POST /api/lenses/reorder y, al exito, re-sincroniza +
        // re-broadcastea el hello (mismo camino que create/update/delete_lens,
        // ver docs/networking.md) -- ese hello trae el orden YA confirmado por el
        // backend, asi que no hace falta un ack explicito de "reorder ok"
        // (silencioso, igual que apply_lens/load_scenario). Un lens_error
        // (p.ej. NOT_ADMIN si el modo cambio entre el hello y este drag, o
        // permutacion invalida) se muestra con el mecanismo existente
        // (OnLensError, cae a _ownLensStatus) y el rollback visual es gratis: el
        // proximo hello reconstruye RebuildLensList con el orden real.
        private void OnLensesReordered(List<string> order)
        {
            if (!_session.IsWsOpen) return;
            _session.SendCommand(new JObject
            {
                ["cmd"] = "reorder_lenses",
                ["order"] = JArray.FromObject(order),
            });
        }

        private void OnLensSelected(string lensId) => ApplyLensTo(lensId, _selectedEye);

        // P7: extraido de OnLensSelected con el ojo explicito -- el modo Standard
        // elige el ojo POR lente (overlay de seleccion), no con la card "Ojo a tratar".
        private void ApplyLensTo(string lensId, string eye)
        {
            if (!_session.IsWsOpen) return;
            _session.SendCommand(new JObject { ["cmd"] = "apply_lens", ["lens_id"] = lensId, ["eye"] = eye });
            // Actualizacion optimista del estado local (vision_state compartido con la sesion).
            if (eye == "left" || eye == "both")
                _session.VisionState["left"] = new JObject { ["lens_id"] = lensId };
            if (eye == "right" || eye == "both")
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
                _leftEyeLabel.text = L10n.T("lens.chip_prefix_os", LensDisplayName(leftId));
                _rightEyeLabel.text = L10n.T("lens.chip_prefix_od", LensDisplayName(rightId));
            }
            else
            {
                _rightEyePane.SetActive(false);
                _leftEyeLabel.text = string.IsNullOrEmpty(leftId)
                    ? L10n.T("common.both_eyes") : L10n.T("lens.both_eyes_with", LensDisplayName(leftId));
            }
            RefreshFullscreenUI(isBlend, leftId, rightId); // mantiene el overlay al dia con cualquier cambio de vision_state
            RefreshStandardUI(isBlend, leftId, rightId);   // idem para la pantalla del modo Standard (P7)
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
                _fsLeftLabel.text = L10n.T("lens.chip_dash_os", LensDisplayName(leftId));
                _fsRightLabel.text = L10n.T("lens.chip_dash_od", LensDisplayName(rightId));
            }
            else
            {
                _fsRightPane.SetActive(false);
                _fsLeftLabel.text = string.IsNullOrEmpty(leftId)
                    ? L10n.T("common.both_eyes") : L10n.T("lens.both_eyes_dash", LensDisplayName(leftId));
            }
        }

        // El overlay fullscreen es del modo Pro: en Standard el stream ya ES
        // fullscreen y este overlay encima solo confunde. El guard cubre el caso
        // observado en dispositivo de una restauracion de estado tras reconexion
        // que lo re-abria sobre la pantalla Standard (ver docs/tablet.md).
        private void OpenFullscreenStream()
        {
            if (_standardScreen != null && _standardScreen.activeSelf) return;
            _fullscreenStream.SetActive(true);
        }

        private void CloseFullscreenStream() => _fullscreenStream?.SetActive(false);

        // ============================================================
        // Ajuste fino de parametros
        // ============================================================
        private void BuildParamsEditor(string lensId)
        {
            _editingLensId = lensId;
            _paramRows.Clear();
            _paramDefaults.Clear();
            // Cambiar de lente en edicion desarma cualquier status/timeout pendiente
            // de la anterior (ver SetLensStatus) -- evita mostrar "Lente guardada" o
            // un timeout viejo sobre una lente distinta a la que se esta mirando.
            if (_ownLensStatus != null) SetLensStatus(_ownLensStatus, ref _ownLensStatusRoutine, "");
            for (int i = _paramsList.childCount - 1; i >= 0; i--) Destroy(_paramsList.GetChild(i).gameObject);

            if (!_session.LensesById.TryGetValue(lensId, out var lens) || !(lens["params"] is JObject paramsDef) || !paramsDef.HasValues)
            {
                _resetButton.interactable = false;
                _editingLensLabel.text = L10n.T("lens.no_editable_params");
                if (_lensNameEdit != null) _lensNameEdit.gameObject.SetActive(false);
                return;
            }

            // Orden clinico (focos primero); params extra al final, en orden del catalogo.
            var ordered = new List<string>();
            foreach (var k in ParamMeta.ORDER) if (paramsDef[k] != null) ordered.Add(k);
            foreach (var prop in paramsDef.Properties()) if (!ordered.Contains(prop.Name)) ordered.Add(prop.Name);

            // P7.2: ya no existe la categoria "generica" -- todas las lentes que
            // NO son propias del visor (custom) son ahora lentes de CATALOGO (de
            // fabrica o agregadas por un admin, indistinguibles por "origen"). Un
            // Pro sobre una lente de catalogo solo ajusta los parametros del modo
            // Standard; la lista completa queda reservada a sus lentes custom
            // ("Crear lente" duplica la actual para editarla entera). Un visor
            // ADMIN sigue teniendo el Ajuste fino completo tambien sobre lentes de
            // catalogo (con guardado Y borrado al backend, ver docs/tablet.md).
            string origen = (string)lens["origen"];
            bool ownCustom = origen == "custom";
            bool isAdmin = _session.IsAdmin;
            bool fullEdit = ownCustom || isAdmin;
            if (!fullEdit) ordered.RemoveAll(k => !ParamMeta.IsStandardParam(k));

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
                ? L10n.T("lens.no_editable_params")
                : ownCustom
                    ? L10n.T("lens.editable_custom")
                    : isAdmin
                        // P7.2: origen=="generic" ya no llega del backend (esas lentes
                        // se fusionaron con el catalogo base) -- el admin edita Y
                        // elimina CUALQUIER lente de catalogo, sin distincion.
                        ? L10n.T("lens.editable_admin")
                        : L10n.T("lens.editable_standard");

            // Botones de lente propia (guardar cambios / eliminar): las propias
            // siempre las tienen; P7.2 -- un visor ADMIN puede EDITAR y tambien
            // ELIMINAR cualquier lente de catalogo (antes, P7.1, solo dejaba
            // borrar las "genericas"; al fusionarse esa categoria con el catalogo
            // base, el admin pasa a gestionar el catalogo entero -- cada
            // operacion versiona el blob .aN del lado backend, con rollback
            // desde el panel admin).
            _deleteArmed = false;
            if (_saveLensButton != null)
            {
                bool canSave = ownCustom || isAdmin;
                bool canDelete = ownCustom || isAdmin;
                _saveLensButton.gameObject.SetActive(canSave);
                _deleteLensButton.gameObject.SetActive(canDelete);
                if (canDelete && _deleteLensButton.Label != null) _deleteLensButton.Label.text = L10n.T("lens.delete_button");
                if (_lensNameEdit != null)
                {
                    _lensNameEdit.gameObject.SetActive(canSave);
                    // Un "hello" (p.ej. refresh, re-sync tras otra operacion) puede
                    // re-invocar este editor mientras el usuario esta tipeando el
                    // nombre -- no pisar lo tipeado si el campo tiene foco.
                    if (canSave && !_lensNameEdit.isFocused)
                        _lensNameEdit.SetTextWithoutNotify((string)lens["nombre"] ?? _editingLensId);
                }
            }
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
            RebuildStandardScenarioList(); // P7: espejo en la barra del modo Standard
        }

        // D2: traducido por id, sin cambiar el protocolo -- el hello sigue trayendo
        // {id,label} tal cual (NetworkController.BuildScenarios). Si hay una clave
        // "scenario.<id>" en la tabla (ver docs/localizacion.md) gana sobre el
        // label crudo del visor; si no (escenario nuevo sin traduccion todavia, o
        // un visor viejo con un id no contemplado), cae al label del hello y, en
        // ultimo caso, al id capitalizado (no deberia pasar: NetworkController
        // siempre manda {id,label}).
        private string ScenarioLabel(string sid)
        {
            string key = "scenario." + sid;
            if (L10n.Has(key)) return L10n.T(key);
            if (_session.ScenarioLabels.TryGetValue(sid, out var label) && !string.IsNullOrEmpty(label)) return label;
            return sid.Length > 0 ? char.ToUpper(sid[0]) + sid.Substring(1) : sid;
        }

        private void OnScenarioPressed(string scenarioId)
        {
            if (!_session.IsWsOpen) return;
            _session.CurrentScenario = scenarioId;
            // P2.3: seleccion por id (clave del diccionario), no por comparar el texto
            // del label del boton -- dos escenarios con el mismo label ya no rompen esto.
            foreach (var kv in _scenarioButtons)
                kv.Value.SetOn(kv.Key == scenarioId, false);
            foreach (var kv in _stdScenarioButtons)
                kv.Value.SetOn(kv.Key == scenarioId, false);
            _session.SendCommand(new JObject { ["cmd"] = "load_scenario", ["id"] = scenarioId });
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
            if (!_session.IsWsOpen) return;
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
            _footer.text = L10n.T("main.footer_stats", fps, (_bytesReceived / 1048576.0).ToString("F1", CultureInfo.InvariantCulture));
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
            BuildUnpairConfirm(canvasGo.transform); // overlay de confirmacion (header Pro, boton Desvincular)
            BuildLangConfirm(canvasGo.transform); // D2: overlay de confirmacion del toggle de idioma (header)
            BuildServiceExitConfirm(canvasGo.transform); // overlay de salida de servicio del kiosco (gesto 7 taps, Fase A)
            BuildStandardScreen(canvasGo.transform); // P7: UI simplificada (modo standard del visor)
            BuildFullscreenStream(canvasGo.transform);
            BuildUpdateScreen(canvasGo.transform); // ULTIMO: debe quedar arriba de TODAS las demas pantallas/overlays
            BuildServiceModeBanner(canvasGo.transform); // correcciones (MAYOR #12): por encima incluso del update screen
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
            var title = _kit.Label(wrap.transform, L10n.T("app.title"), LabelKind.Title, TextAlignmentOptions.Center);
            AttachServiceExitGesture(title.gameObject); // 7 taps en el titulo -> salida de servicio (Fase A, ver mas abajo)
            _kit.Label(wrap.transform, L10n.T("connect.subtitle"), LabelKind.Subtitle, TextAlignmentOptions.Center);
            _kit.Spacer(wrap.transform, 12, false);
            _kit.Label(wrap.transform, L10n.T("connect.detected_headsets"), LabelKind.Section, TextAlignmentOptions.Left);
            _discoveredList = _kit.Box(wrap.transform, "DiscoveredList", true, 8, null, expandW: true);
            _connectStatus = _kit.Label(wrap.transform, L10n.T("connect.searching"), LabelKind.Hint, TextAlignmentOptions.Center);
            _networkInfoLabel = _kit.Label(wrap.transform, "", LabelKind.Hint, TextAlignmentOptions.Center);
            _kit.Label(wrap.transform,
                L10n.T("connect.wifi_help"),
                LabelKind.Hint, TextAlignmentOptions.Center);
            _kit.Spacer(wrap.transform, 12, false);

            // Fase B: sirve tambien en una tablet SIN Device Owner (no gateado
            // por KioskManager.IsDeviceOwner, ver ese metodo).
            var wifiBtn = _kit.Button(wrap.transform, L10n.T("connect.wifi_button"), BtnStyle.Ghost, false, 48, 16);
            wifiBtn.OnClick = KioskManager.OpenWifiSettings;

            // MENOR (correcciones): toggle de idioma tambien en la ConnectScreen --
            // antes solo estaba en el header de MainScreen, inalcanzable hasta
            // emparejar con un visor. Una clinica nueva necesita poder fijar el
            // idioma ANTES de conectar. Mismo handler/popup que el del header
            // (OnLangTogglePressed/BuildLangConfirm), mismo texto (codigo ISO del
            // idioma AL QUE CAMBIARIA).
            var langToggleConnect = _kit.Button(wrap.transform, L10n.Lang == "es" ? "EN" : "ES", BtnStyle.Ghost, false, 48, 16);
            langToggleConnect.OnClick = OnLangTogglePressed;

            var exitBtn = _kit.Button(wrap.transform, L10n.T("common.exit"), BtnStyle.Ghost, false, 48, 16);
            exitBtn.OnClick = () => Application.Quit();
            // En kiosco (Device Owner) no hay adonde "salir" -- Application.Quit()
            // es un no-op bajo lock task (ver OnLangConfirmPressed) y mostrar un
            // boton que no hace nada confunde al clinico. En una tablet de
            // desarrollo (sin Device Owner) el boton sigue igual.
            if (KioskManager.IsDeviceOwner)
                exitBtn.gameObject.SetActive(false);
        }

        // ------------------------------------------------------------
        // Salida de servicio del kiosco (Fase A, correcciones ver docs/tablet.md
        // "Salida de servicio"): 7 taps en el titulo de la ConnectScreen dentro
        // de 3s abre un popup con PIN de servicio; PIN correcto entra a "modo
        // servicio" (KioskManager.EnterServiceMode -- sale del lock task de
        // verdad, no cierra la app, y muestra un banner con boton para volver al
        // kiosco, ver ShowServiceModeBanner/OnLeaveServiceModePressed). Inerte
        // en tablets de desarrollo (KioskManager.IsDeviceOwner false): el gesto
        // no cuenta taps ahi, asi que el popup nunca aparece.
        // ------------------------------------------------------------
        private int _serviceExitTapCount;
        private float _serviceExitTapWindowStart;
        private const int ServiceExitTapsRequired = 7;
        private const float ServiceExitTapWindowSeconds = 3f;
        // SIM: atajo deliberado -- PIN de servicio hardcodeado; migrar a
        // BackendConfig cuando exista el canal (ver docs/tablet.md Decisiones).
        private const string ServicePin = "4127";
        private GameObject _serviceExitConfirm;
        private TMP_InputField _serviceExitPinEdit;
        private TMP_Text _serviceExitStatus;
        // Banner discreto de "modo servicio" (correcciones, MAYOR #12): activo
        // solo si la tablet arranco (o entro via el gesto) con el flag de
        // KioskManager.IsInServiceMode puesto. Ver BuildServiceModeBanner.
        private GameObject _serviceModeBanner;

        private void AttachServiceExitGesture(GameObject go)
        {
            var btn = go.AddComponent<Button>();
            btn.transition = Selectable.Transition.None;
            btn.onClick.AddListener(OnServiceExitGestureTap);
        }

        private void OnServiceExitGestureTap()
        {
            if (!KioskManager.IsDeviceOwner) return; // gesto inerte fuera de kiosco
            float now = Time.unscaledTime;
            if (now - _serviceExitTapWindowStart > ServiceExitTapWindowSeconds)
            {
                _serviceExitTapWindowStart = now;
                _serviceExitTapCount = 0;
            }
            _serviceExitTapCount++;
            if (_serviceExitTapCount >= ServiceExitTapsRequired)
            {
                _serviceExitTapCount = 0;
                OpenServiceExitConfirm();
            }
        }

        private void OpenServiceExitConfirm()
        {
            _serviceExitStatus.text = "";
            _serviceExitPinEdit.text = "";
            _serviceExitConfirm.SetActive(true);
        }

        private void CloseServiceExitConfirm() => _serviceExitConfirm?.SetActive(false);

        private void OnServiceExitConfirmPressed()
        {
            if (_serviceExitPinEdit.text == ServicePin)
            {
                CloseServiceExitConfirm();
                // MAYOR #12 (correcciones): antes esto hacia ExitLockTask()
                // (asincronico, runOnUiThread) + Application.Quit() -- una
                // carrera que ademas no serviria de nada, porque la HOME
                // persistente relanza la app y su Start() volvia a entrar a
                // lock task. Ahora la app se queda ABIERTA en "modo servicio":
                // sale del kiosco de verdad y le da al operador una ventana
                // real para ir a Home/Ajustes/adb.
                KioskManager.EnterServiceMode();
                ShowServiceModeBanner();
            }
            else
            {
                _serviceExitStatus.text = L10n.T("kiosk.pin_incorrect");
            }
        }

        private void ShowServiceModeBanner() => _serviceModeBanner?.SetActive(true);

        private void OnLeaveServiceModePressed()
        {
            KioskManager.LeaveServiceMode();
            _serviceModeBanner?.SetActive(false);
        }

        // Banner de "modo servicio": barra angosta anclada arriba de TODA la UI
        // (agregada al final de BuildUI, mismo criterio que BuildUpdateScreen --
        // se dibuja por encima de cualquier pantalla). Solo se activa si la
        // tablet arranca (o entra via el gesto de servicio) con el flag de
        // KioskManager.IsInServiceMode puesto -- ver Start()/
        // OnServiceExitConfirmPressed. No es capturable en el Editor
        // (KioskManager.IsDeviceOwner siempre false ahi).
        private void BuildServiceModeBanner(Transform parent)
        {
            _serviceModeBanner = new GameObject("ServiceModeBanner", typeof(RectTransform));
            _serviceModeBanner.transform.SetParent(parent, false);
            var rt = _serviceModeBanner.GetComponent<RectTransform>();
            rt.anchorMin = new Vector2(0f, 1f);
            rt.anchorMax = new Vector2(1f, 1f);
            rt.pivot = new Vector2(0.5f, 1f);
            rt.sizeDelta = new Vector2(0f, 56f);
            rt.anchoredPosition = Vector2.zero;
            _serviceModeBanner.SetActive(false);

            var bg = _serviceModeBanner.AddComponent<Image>();
            _kit.Tint(bg, p => p.Accent);

            var hlg = _serviceModeBanner.AddComponent<HorizontalLayoutGroup>();
            hlg.padding = new RectOffset(16, 16, 8, 8);
            hlg.spacing = 12;
            hlg.childAlignment = TextAnchor.MiddleLeft;
            hlg.childControlWidth = true; hlg.childControlHeight = true;
            hlg.childForceExpandWidth = false; hlg.childForceExpandHeight = false;

            _kit.Label(_serviceModeBanner.transform, L10n.T("kiosk.service_mode_banner"), LabelKind.Section, TextAlignmentOptions.Left);
            _kit.Spacer(_serviceModeBanner.transform, 0, true);
            var exitServiceBtn = _kit.Button(_serviceModeBanner.transform, L10n.T("kiosk.service_mode_exit"), BtnStyle.Ghost, false, 40, 14);
            exitServiceBtn.OnClick = OnLeaveServiceModePressed;
        }

        // Mismo patron scrim + card centrada que BuildUnpairConfirm.
        private void BuildServiceExitConfirm(Transform parent)
        {
            _serviceExitConfirm = new GameObject("ServiceExitConfirm", typeof(RectTransform));
            _serviceExitConfirm.transform.SetParent(parent, false);
            Stretch(_serviceExitConfirm.GetComponent<RectTransform>());
            _serviceExitConfirm.SetActive(false);

            var scrim = new GameObject("ServiceExitScrim", typeof(RectTransform), typeof(Image), typeof(Button));
            scrim.transform.SetParent(_serviceExitConfirm.transform, false);
            Stretch(scrim.GetComponent<RectTransform>());
            scrim.GetComponent<Image>().color = new Color(0f, 0f, 0f, 0.6f);
            var scrimBtn = scrim.GetComponent<Button>();
            scrimBtn.transition = Selectable.Transition.None;
            scrimBtn.onClick.AddListener(CloseServiceExitConfirm);

            var card = _kit.Card(_serviceExitConfirm.transform, "ServiceExitCard");
            card.anchorMin = card.anchorMax = new Vector2(0.5f, 0.5f);
            card.pivot = new Vector2(0.5f, 0.5f);
            card.sizeDelta = new Vector2(420, 0);
            var fit = card.gameObject.AddComponent<ContentSizeFitter>();
            fit.verticalFit = ContentSizeFitter.FitMode.PreferredSize;

            _kit.Label(card, L10n.T("kiosk.exit_title"), LabelKind.Title, TextAlignmentOptions.Center);
            _kit.Label(card, L10n.T("kiosk.exit_body"), LabelKind.Hint, TextAlignmentOptions.Center);
            _kit.Spacer(card, 4, false);

            _serviceExitPinEdit = _kit.LineEdit(card, L10n.T("kiosk.pin_placeholder"));
            _serviceExitPinEdit.contentType = TMP_InputField.ContentType.IntegerNumber;
            _serviceExitPinEdit.characterLimit = 8;

            _serviceExitStatus = _kit.Label(card, "", LabelKind.Hint, TextAlignmentOptions.Center);
            _kit.Spacer(card, 4, false);

            var row = _kit.Box(card, "ServiceExitButtons", false, 8, null, expandW: true);
            var cancelBtn = _kit.Button(row, L10n.T("common.cancel"), BtnStyle.Ghost, false, 48, 16);
            _kit.Size(cancelBtn.GetComponent<RectTransform>(), flexW: 1);
            cancelBtn.OnClick = CloseServiceExitConfirm;
            var confirmBtn = _kit.Button(row, L10n.T("kiosk.exit_title"), BtnStyle.Accent, false, 48, 16);
            _kit.Size(confirmBtn.GetComponent<RectTransform>(), flexW: 1);
            confirmBtn.OnClick = OnServiceExitConfirmPressed;
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
            _networkInfoLabel.text = string.IsNullOrEmpty(info) ? L10n.T("connect.network_unavailable") : L10n.T("connect.network_prefix", info);
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
            _kit.Label(wrap.transform, L10n.T("pin.title"), LabelKind.Title, TextAlignmentOptions.Center);
            _pinHostLabel = _kit.Label(wrap.transform, "", LabelKind.Subtitle, TextAlignmentOptions.Center);
            _kit.Label(wrap.transform, L10n.T("pin.instructions"), LabelKind.Hint, TextAlignmentOptions.Center);
            _kit.Spacer(wrap.transform, 8, false);

            _pinEdit = _kit.LineEdit(wrap.transform, L10n.T("pin.placeholder"));
            _pinEdit.contentType = TMP_InputField.ContentType.IntegerNumber;
            _pinEdit.characterLimit = 6;
            _pinEdit.onSubmit.AddListener(_ => OnPinConfirmPressed());

            _pinStatus = _kit.Label(wrap.transform, "", LabelKind.Hint, TextAlignmentOptions.Center);
            _kit.Spacer(wrap.transform, 8, false);

            var row = _kit.Box(wrap.transform, "PinButtons", false, 8, null, expandW: true);
            var cancelBtn = _kit.Button(row, L10n.T("common.cancel"), BtnStyle.Ghost, false, 48, 16);
            _kit.Size(cancelBtn.GetComponent<RectTransform>(), flexW: 1);
            cancelBtn.OnClick = OnPinCancelPressed;
            var confirmBtn = _kit.Button(row, L10n.T("pin.connect_button"), BtnStyle.Accent, false, 48, 16);
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
            _kit.Label(wrap.transform, L10n.T("reconnect.title"), LabelKind.Title, TextAlignmentOptions.Center);
            _reconnectHostLabel = _kit.Label(wrap.transform, "", LabelKind.Subtitle, TextAlignmentOptions.Center);
            _kit.Spacer(wrap.transform, 8, false);
            _reconnectStatus = _kit.Label(wrap.transform, "", LabelKind.Hint, TextAlignmentOptions.Center);
            _kit.Spacer(wrap.transform, 8, false);

            var cancelBtn = _kit.Button(wrap.transform, L10n.T("common.cancel"), BtnStyle.Ghost, false, 48, 16);
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

        // Overlay modal de confirmacion para "Desvincular" (header Pro): revocar el
        // emparejamiento por token es irreversible desde la tablet (hay que volver a
        // pedir el PIN), asi que un tap sobre el boton discreto del header ya no
        // dispara _session.Unpair() directo. Reusa el patron scrim + card centrada
        // de BuildUpdateScreen (fondo semi-opaco + ContentSizeFitter) y el cierre-al-
        // tocar-el-fondo de BuildStandardLensOverlay.
        private void BuildUnpairConfirm(Transform parent)
        {
            _unpairConfirm = new GameObject("UnpairConfirm", typeof(RectTransform));
            _unpairConfirm.transform.SetParent(parent, false);
            Stretch(_unpairConfirm.GetComponent<RectTransform>());
            _unpairConfirm.SetActive(false);

            var scrim = new GameObject("UnpairScrim", typeof(RectTransform), typeof(Image), typeof(Button));
            scrim.transform.SetParent(_unpairConfirm.transform, false);
            Stretch(scrim.GetComponent<RectTransform>());
            scrim.GetComponent<Image>().color = new Color(0f, 0f, 0f, 0.6f);
            var scrimBtn = scrim.GetComponent<Button>();
            scrimBtn.transition = Selectable.Transition.None;
            scrimBtn.onClick.AddListener(CloseUnpairConfirm);

            var card = _kit.Card(_unpairConfirm.transform, "UnpairCard");
            card.anchorMin = card.anchorMax = new Vector2(0.5f, 0.5f);
            card.pivot = new Vector2(0.5f, 0.5f);
            card.sizeDelta = new Vector2(420, 0);
            var fit = card.gameObject.AddComponent<ContentSizeFitter>();
            fit.verticalFit = ContentSizeFitter.FitMode.PreferredSize;

            _kit.Label(card, L10n.T("unpair.title"), LabelKind.Title, TextAlignmentOptions.Center);
            _kit.Label(card, L10n.T("unpair.body"),
                LabelKind.Hint, TextAlignmentOptions.Center);
            _kit.Spacer(card, 4, false);

            var row = _kit.Box(card, "UnpairButtons", false, 8, null, expandW: true);
            var cancelBtn = _kit.Button(row, L10n.T("common.cancel"), BtnStyle.Ghost, false, 48, 16);
            _kit.Size(cancelBtn.GetComponent<RectTransform>(), flexW: 1);
            cancelBtn.OnClick = CloseUnpairConfirm;
            var confirmBtn = _kit.Button(row, L10n.T("main.unpair"), BtnStyle.Accent, false, 48, 16);
            _kit.Size(confirmBtn.GetComponent<RectTransform>(), flexW: 1);
            confirmBtn.OnClick = OnUnpairConfirmed;
        }

        // D2: overlay modal de confirmacion para el toggle de idioma del header --
        // mismo patron scrim + card centrada que BuildUnpairConfirm de arriba.
        private void BuildLangConfirm(Transform parent)
        {
            _langConfirm = new GameObject("LangConfirm", typeof(RectTransform));
            _langConfirm.transform.SetParent(parent, false);
            Stretch(_langConfirm.GetComponent<RectTransform>());
            _langConfirm.SetActive(false);

            var scrim = new GameObject("LangScrim", typeof(RectTransform), typeof(Image), typeof(Button));
            scrim.transform.SetParent(_langConfirm.transform, false);
            Stretch(scrim.GetComponent<RectTransform>());
            scrim.GetComponent<Image>().color = new Color(0f, 0f, 0f, 0.6f);
            var scrimBtn = scrim.GetComponent<Button>();
            scrimBtn.transition = Selectable.Transition.None;
            scrimBtn.onClick.AddListener(CloseLangConfirm);

            var card = _kit.Card(_langConfirm.transform, "LangCard");
            card.anchorMin = card.anchorMax = new Vector2(0.5f, 0.5f);
            card.pivot = new Vector2(0.5f, 0.5f);
            card.sizeDelta = new Vector2(420, 0);
            var fit = card.gameObject.AddComponent<ContentSizeFitter>();
            fit.verticalFit = ContentSizeFitter.FitMode.PreferredSize;

            _kit.Label(card, L10n.T("lang.change_title"), LabelKind.Title, TextAlignmentOptions.Center);
            _kit.Label(card, L10n.T("lang.change_body"), LabelKind.Hint, TextAlignmentOptions.Center);
            _kit.Spacer(card, 4, false);

            var langRow = _kit.Box(card, "LangButtons", false, 8, null, expandW: true);
            var langCancelBtn = _kit.Button(langRow, L10n.T("common.cancel"), BtnStyle.Ghost, false, 48, 16);
            _kit.Size(langCancelBtn.GetComponent<RectTransform>(), flexW: 1);
            langCancelBtn.OnClick = CloseLangConfirm;
            var langConfirmBtn = _kit.Button(langRow, L10n.T("lang.change_confirm"), BtnStyle.Accent, false, 48, 16);
            _kit.Size(langConfirmBtn.GetComponent<RectTransform>(), flexW: 1);
            langConfirmBtn.OnClick = OnLangConfirmPressed;
        }

        private void BuildHeader(Transform parent)
        {
            var header = _kit.Panel(parent, "HeaderBar", p => p.Surface, 0, false, 12, new RectOffset(16, 16, 8, 8));
            var hlg = header.GetComponent<HorizontalLayoutGroup>();
            hlg.childForceExpandWidth = false; hlg.childAlignment = TextAnchor.MiddleLeft;
            _kit.Size(header, minH: 62);

            EyeGlyph(header, 26);
            var title = _kit.Label(header, L10n.T("app.title"), LabelKind.Title, TextAlignmentOptions.Left);
            title.fontSize = 19;
            _kit.Spacer(header, 0, true);
            _kit.Label(header, L10n.T("main.scenario_label"), LabelKind.Subtitle, TextAlignmentOptions.Right);
            _scenarioList = _kit.Box(header, "ScenarioList", false, 6, null, expandW: false);
            _scenarioList.GetComponent<HorizontalLayoutGroup>().childAlignment = TextAnchor.MiddleLeft;
            var recenterBtn = _kit.Button(header, L10n.T("main.recenter"), BtnStyle.Ghost, false, 44, 14);
            recenterBtn.OnClick = OnRecenterPressed;
            _kit.Spacer(header, 0, true);
            _themeToggle = _kit.Button(header, L10n.T("main.theme_toggle_light"), BtnStyle.Ghost, false, 44, 14);
            _themeToggle.OnClick = () => ApplyTheme(!_isDark);
            // D2: toggle de idioma, mismo estilo que el de tema, junto a el (ver
            // BuildLangConfirm/OnLangTogglePressed). El texto es el codigo ISO del
            // idioma AL QUE CAMBIARIA -- no se traduce (es un codigo universal,
            // igual sea cual sea el idioma activo).
            var langToggle = _kit.Button(header, L10n.Lang == "es" ? "EN" : "ES", BtnStyle.Ghost, false, 44, 14);
            langToggle.OnClick = OnLangTogglePressed;
            var refreshBtn = _kit.Button(header, L10n.T("main.refresh"), BtnStyle.Ghost, false, 44, 14);
            refreshBtn.OnClick = OnRefreshPressed;
            _hudToggleBtn = _kit.Button(header, L10n.T("main.hud_hide"), BtnStyle.Ghost, false, 44, 14);
            _hudToggleBtn.OnClick = OnHudTogglePressed;
            var disconnect = _kit.Button(header, L10n.T("main.disconnect"), BtnStyle.Ghost, false, 44, 14);
            disconnect.OnClick = OnDisconnectPressed;
            // Emparejamiento persistente por token (ver docs/networking.md): discreto,
            // al lado de Desconectar -- accion poco frecuente (revocar el
            // emparejamiento), no un boton principal del flujo clinico.
            var unpair = _kit.Button(header, L10n.T("main.unpair"), BtnStyle.Ghost, false, 44, 14);
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
            _rightEyeLabel = _kit.Label(_rightEyePane.transform, L10n.T("common.od"), LabelKind.StreamChip, TextAlignmentOptions.Center);
            _kit.Size(_rightEyeLabel.rectTransform, minH: 22, prefH: 22, flexH: 0);
            _streamRight = MakeStreamView(_rightEyePane.transform);
            _rightEyePane.SetActive(false);

            var leftPane = _kit.Box(eyes, "LeftEyePane", true, 6, null, expandW: true, expandH: false);
            _kit.Size(leftPane, flexW: 1);
            _leftEyeLabel = _kit.Label(leftPane, L10n.T("common.both_eyes"), LabelKind.StreamChip, TextAlignmentOptions.Center);
            _kit.Size(_leftEyeLabel.rectTransform, minH: 22, prefH: 22, flexH: 0);
            _streamLeft = MakeStreamView(leftPane);

            // Boton "Pantalla completa": overlay ignoreLayout anclado a la esquina
            // superior derecha del StreamPanel (no participa del HorizontalLayoutGroup
            // del panel, ver PinTopRight).
            var fullscreenBtn = _kit.Button(stream, L10n.T("main.fullscreen_button"), BtnStyle.Overlay, false, 36, 13);
            PinTopRight(fullscreenBtn.GetComponent<RectTransform>(), fullscreenBtn.GetComponent<LayoutElement>(), 8, 8);
            fullscreenBtn.OnClick = OpenFullscreenStream;

            // --- Scroll de controles (derecha) ---
            var scroll = _kit.ScrollColumn(body, out var content);
            _kit.Size(scroll.GetComponent<RectTransform>(), minW: 360, flexW: 1, flexH: 1);

            BuildEyeCard(content);
            BuildLensesCard(content);
            BuildParamsCard(content);
            BuildAstigCard(content);
            BuildCreateLensCard(content);
        }

        private void BuildEyeCard(Transform parent)
        {
            var card = _kit.Card(parent, "EyeCard");
            _kit.Label(card, L10n.T("main.eye_to_treat_title"), LabelKind.Section, TextAlignmentOptions.Left);
            var row = _kit.Box(card, "EyeSelector", false, 6, null, expandW: true);
            _eyeBoth = _kit.Button(row, L10n.T("common.both"), BtnStyle.Segment, true, 52, 16);
            _eyeOd = _kit.Button(row, L10n.T("main.eye_od_full"), BtnStyle.Segment, true, 52, 15);
            _eyeOi = _kit.Button(row, L10n.T("main.eye_os_full"), BtnStyle.Segment, true, 52, 15);
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
            _kit.Label(card, L10n.T("main.lenses_section_title"), LabelKind.Section, TextAlignmentOptions.Left);
            _lensList = _kit.Box(card, "LensList", true, 8, null, expandW: true);
        }

        private void BuildParamsCard(Transform parent)
        {
            var card = _kit.Card(parent, "ParamsCard");
            var paramsToggle = _kit.Button(card, L10n.T("main.fine_tuning"), BtnStyle.Ghost, true, 48, 16);
            _paramsContent = _kit.Box(card, "ParamsContent", true, 10, null, expandW: true);
            _editingLensLabel = _kit.Label(_paramsContent, L10n.T("lens.apply_to_adjust"), LabelKind.Hint, TextAlignmentOptions.Left);
            _paramsList = _kit.Box(_paramsContent, "ParamsList", true, 10, null, expandW: true);
            _resetButton = _kit.Button(_paramsContent, L10n.T("main.restore_values"), BtnStyle.Ghost, false, 44, 15);
            _resetButton.OnClick = OnResetParamsPressed;
            _resetButton.interactable = false;

            // D1: nombre editable de la lente en edicion -- mismo patron que
            // _createNameEdit (Crear lente), limite de caracteres igual al
            // max_length del backend. Solo visible si canSave (ver BuildParamsEditor).
            _lensNameEdit = _kit.LineEdit(_paramsContent, L10n.T("lens.name_placeholder"));
            _lensNameEdit.characterLimit = 80;
            _lensNameEdit.gameObject.SetActive(false);

            // P7: acciones sobre la lente custom PROPIA en edicion (ocultas para
            // lentes base/genericas; ver BuildParamsEditor).
            var ownRow = _kit.Box(_paramsContent, "OwnLensRow", false, 8, null, expandW: true);
            _saveLensButton = _kit.Button(ownRow, L10n.T("lens.save_button"), BtnStyle.Accent, false, 44, 15);
            _saveLensButton.OnClick = OnSaveLensPressed;
            _deleteLensButton = _kit.Button(ownRow, L10n.T("lens.delete_button"), BtnStyle.Neutral, false, 44, 15);
            _deleteLensButton.OnClick = OnDeleteLensPressed;
            _saveLensButton.gameObject.SetActive(false);
            _deleteLensButton.gameObject.SetActive(false);
            _ownLensStatus = _kit.Label(_paramsContent, "", LabelKind.Hint, TextAlignmentOptions.Left);

            _paramsContent.gameObject.SetActive(false);
            paramsToggle.OnToggled += on => _paramsContent.gameObject.SetActive(on);
        }

        private void BuildAstigCard(Transform parent)
        {
            var card = _kit.Card(parent, "AstigCard");
            var astigToggle = _kit.Button(card, L10n.T("main.astig_title"), BtnStyle.Ghost, true, 48, 16);
            _astigContent = _kit.Box(card, "AstigContent", true, 8, null, expandW: true);

            // P4.4: aclaracion de precedencia -- desde que el catalogo trae astig_magnitude/
            // astig_axis_deg (persistentes, por lente, editables en "Ajuste fino"), hay DOS
            // controles de astigmatismo. Este switch/sliders son el ajuste LIVE de
            // GlareController.SetAstigmatism (set_astigmatism, no persiste) y siempre pisa lo
            // que muestre el shader por encima del valor de catalogo mientras este activo;
            // cambiar de lente u override_params no lo apaga ni lo sincroniza. Ver
            // docs/tablet.md.
            _kit.Label(_astigContent, L10n.T("main.astig_precedence_hint"),
                LabelKind.Hint, TextAlignmentOptions.Left);

            _kit.CheckToggle(_astigContent, L10n.T("main.astig_simulate_toggle"), out _astigEnabled);
            _astigEnabled.OnToggled += _ => SendAstigmatism();

            var magHeader = _kit.Box(_astigContent, "MagHeader", false, 8, null, expandW: true);
            magHeader.GetComponent<HorizontalLayoutGroup>().childAlignment = TextAnchor.MiddleLeft;
            var magLabel = _kit.Label(magHeader, L10n.T("main.astig_magnitude_label"), LabelKind.Body, TextAlignmentOptions.Left);
            _kit.Size(magLabel.rectTransform, flexW: 1);
            _magValue = _kit.Label(magHeader, "25 px", LabelKind.Value, TextAlignmentOptions.Right);
            _magSlider = _kit.Slider(_astigContent);
            _magSlider.minValue = 0; _magSlider.maxValue = 50; _magSlider.wholeNumbers = true;
            _magSlider.SetValueWithoutNotify(25);
            _magSlider.onValueChanged.AddListener(_ => OnAstigChanged());

            var angleHeader = _kit.Box(_astigContent, "AngleHeader", false, 8, null, expandW: true);
            angleHeader.GetComponent<HorizontalLayoutGroup>().childAlignment = TextAnchor.MiddleLeft;
            var angleLabel = _kit.Label(angleHeader, L10n.T("main.astig_axis_label"), LabelKind.Body, TextAlignmentOptions.Left);
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

        // ============================================================
        // Lentes custom (P7, modo Pro)
        // ============================================================
        // Card "Crear lente": duplica la lente en edicion con los valores ACTUALES
        // (defaults del catalogo + overrides aplicados) como defaults de la lente
        // nueva; min/max se heredan del spec de origen. La lente se persiste en el
        // backend (privada del visor; agregada al CATALOGO -- visible para todos --
        // si el visor es admin y activa el toggle, P7.2) -- el visor hace el HTTP y
        // contesta lens_saved/lens_error.
        private void BuildCreateLensCard(Transform parent)
        {
            var card = _kit.Card(parent, "CreateLensCard");
            var toggle = _kit.Button(card, L10n.T("lens.create_title"), BtnStyle.Ghost, true, 48, 16);
            var content = _kit.Box(card, "CreateLensContent", true, 8, null, expandW: true);

            _kit.Label(content, L10n.T("lens.create_description"),
                LabelKind.Hint, TextAlignmentOptions.Left);
            _createNameEdit = _kit.LineEdit(content, L10n.T("lens.create_name_placeholder"));
            _createDescEdit = _kit.LineEdit(content, L10n.T("lens.create_desc_placeholder"));

            // Toggle "agregar al catalogo" (P7.2: reemplaza la nocion de
            // "generica" -- el protocolo NO cambia, sigue mandando
            // scope:"generic"; ver docs/catalogo-lentes.md §P7.2) -- solo visible
            // si el visor conectado es admin (se decide en cada hello, ver
            // OnSessionHello).
            _createGenericRow = _kit.Box(content, "GenericRow", false, 8, null, expandW: true).gameObject;
            _kit.CheckToggle(_createGenericRow.transform, L10n.T("lens.create_add_to_catalog"), out _createGenericToggle);
            _createGenericRow.SetActive(false);

            var createBtn = _kit.Button(content, L10n.T("lens.create_button"), BtnStyle.Accent, false, 44, 15);
            createBtn.OnClick = OnCreateLensPressed;
            _createStatus = _kit.Label(content, "", LabelKind.Hint, TextAlignmentOptions.Left);

            content.gameObject.SetActive(false);
            toggle.OnToggled += on => content.gameObject.SetActive(on);
        }

        /// <summary>Params de la lente en edicion con los valores ACTUALES como default (min/max del spec).</summary>
        private JObject BuildParamsSnapshot()
        {
            if (!_session.LensesById.TryGetValue(_editingLensId, out var lens) || !(lens["params"] is JObject paramsDef))
                return null;
            var result = new JObject();
            foreach (var prop in paramsDef.Properties())
            {
                if (!(prop.Value is JObject e) || e["default"] == null || e["min"] == null || e["max"] == null)
                    continue;
                float def = (float)e["default"];
                result[prop.Name] = new JObject
                {
                    ["default"] = CurrentParamValue(prop.Name, def),
                    ["min"] = (float)e["min"],
                    ["max"] = (float)e["max"],
                };
            }
            return result.HasValues ? result : null;
        }

        // Tiempo sin respuesta del visor antes de degradar "Guardando..."/"Creando..."
        // a un mensaje neutro (el visor puede seguir esperando al backend -- ver
        // CustomLensClient, timeout HTTP de 8 s -- asi que "sin respuesta" no es
        // necesariamente un fallo, solo que todavia no llego el lens_saved/lens_error).
        private const float LensStatusTimeoutS = 5f;
        // Tiempo que queda visible un resultado final (ok o error) antes de
        // limpiarse solo -- mismo patron visual que el status de presets retirado.
        private const float LensStatusClearS = 4f;

        /// <summary>
        /// Actualiza un label de status de lentes custom (Crear lente / Guardar en
        /// la lente) y cancela cualquier timeout pendiente del MISMO label antes de
        /// aplicar el texto nuevo -- evita que un timeout viejo pise un resultado que
        /// ya llego, o que dos acciones seguidas dejen dos coroutines compitiendo.
        /// Con <paramref name="delaySeconds"/> > 0 programa un texto de seguimiento
        /// (<paramref name="thenText"/>) tras ese lapso: se usa tanto para el
        /// timeout de "sin respuesta" (al enviar el comando) como para la
        /// auto-limpieza del resultado final (ok/error).
        /// </summary>
        private void SetLensStatus(TMP_Text label, ref Coroutine routine, string text, float delaySeconds = 0f, string thenText = null)
        {
            if (routine != null) { StopCoroutine(routine); routine = null; }
            if (label != null) label.text = text;
            if (delaySeconds > 0f && label != null)
                routine = StartCoroutine(SetLensStatusAfterDelay(label, delaySeconds, thenText ?? ""));
        }

        private IEnumerator SetLensStatusAfterDelay(TMP_Text label, float seconds, string text)
        {
            yield return new WaitForSeconds(seconds);
            label.text = text;
        }

        private void OnCreateLensPressed()
        {
            if (!_session.IsWsOpen) { SetLensStatus(_createStatus, ref _createStatusRoutine, L10n.T("lens.status_offline")); return; }
            string nombre = (_createNameEdit.text ?? "").Trim();
            if (nombre.Length == 0) { SetLensStatus(_createStatus, ref _createStatusRoutine, L10n.T("lens.status_need_name")); return; }
            var snapshot = BuildParamsSnapshot();
            if (snapshot == null) { SetLensStatus(_createStatus, ref _createStatusRoutine, L10n.T("lens.status_need_base_lens")); return; }

            bool generic = _session.IsAdmin && _createGenericToggle != null && _createGenericToggle.IsOn;
            _session.SendCommand(new JObject
            {
                ["cmd"] = "create_lens",
                ["scope"] = generic ? "generic" : "private",
                ["nombre"] = nombre,
                ["descripcion"] = (_createDescEdit.text ?? "").Trim(),
                ["params"] = snapshot,
            });
            SetLensStatus(_createStatus, ref _createStatusRoutine, L10n.T("lens.status_creating"),
                LensStatusTimeoutS, L10n.T("lens.status_no_response"));
        }

        private void OnSaveLensPressed()
        {
            if (!_session.IsWsOpen) { SetLensStatus(_ownLensStatus, ref _ownLensStatusRoutine, L10n.T("lens.status_offline")); return; }
            // D3: el nombre lo decide el campo editable (D1/D2), no el "nombre" crudo
            // del catalogo -- mismo patron de validacion que OnCreateLensPressed.
            string nombre = (_lensNameEdit.text ?? "").Trim();
            if (nombre.Length == 0) { SetLensStatus(_ownLensStatus, ref _ownLensStatusRoutine, L10n.T("lens.status_need_name")); return; }
            if (!_session.LensesById.TryGetValue(_editingLensId, out var lens)) return;
            var snapshot = BuildParamsSnapshot();
            if (snapshot == null) return;
            _session.SendCommand(new JObject
            {
                ["cmd"] = "update_lens",
                ["lens_id"] = _editingLensId,
                ["nombre"] = nombre,
                ["descripcion"] = (string)lens["descripcion"] ?? "",
                ["params"] = snapshot,
            });
            SetLensStatus(_ownLensStatus, ref _ownLensStatusRoutine, L10n.T("lens.status_saving"),
                LensStatusTimeoutS, L10n.T("lens.status_no_response"));
        }

        private void OnDeleteLensPressed()
        {
            if (!_session.IsWsOpen) { SetLensStatus(_ownLensStatus, ref _ownLensStatusRoutine, L10n.T("lens.status_offline")); return; }
            // Doble tap para confirmar (sin dialogo modal): el primer tap arma, el
            // segundo ejecuta. Cambiar de lente (BuildParamsEditor) desarma.
            if (!_deleteArmed)
            {
                _deleteArmed = true;
                if (_deleteLensButton.Label != null) _deleteLensButton.Label.text = L10n.T("lens.delete_confirm");
                return;
            }
            _deleteArmed = false;
            _session.SendCommand(new JObject { ["cmd"] = "delete_lens", ["lens_id"] = _editingLensId });
            SetLensStatus(_ownLensStatus, ref _ownLensStatusRoutine, L10n.T("lens.status_deleting"),
                LensStatusTimeoutS, L10n.T("lens.status_no_response"));
        }

        private void OnLensSaved(string op, string lensId)
        {
            if (op == "create_lens")
            {
                SetLensStatus(_createStatus, ref _createStatusRoutine,
                    L10n.T("lens.status_created"), LensStatusClearS, "");
                _createNameEdit.text = "";
                _createDescEdit.text = "";
            }
            else if (op == "update_lens")
            {
                SetLensStatus(_ownLensStatus, ref _ownLensStatusRoutine, L10n.T("lens.status_saved"), LensStatusClearS, "");
            }
            else if (op == "delete_lens")
            {
                SetLensStatus(_ownLensStatus, ref _ownLensStatusRoutine, L10n.T("lens.status_deleted"), LensStatusClearS, "");
            }
            // El catalogo actualizado llega solo: el visor re-sincroniza y
            // re-broadcastea el hello (RebuildLensList via OnSessionHello).
        }

        private void OnLensError(string op, string reason)
        {
            string msg = reason switch
            {
                "offline" => L10n.T("lens.error_offline"),
                "MODE_NOT_PRO" => L10n.T("lens.error_mode_not_pro"),
                // P7.2: NOT_ADMIN ahora cubre 3 casos -- crear "para todos"
                // (scope:"generic"), editar o eliminar cualquier lente del
                // catalogo sin ser admin. BASE_LENS queda sin uso en un backend
                // nuevo (P7.2 le permite al admin borrar cualquier lente de
                // catalogo) pero se mantiene mapeado por compat con un backend
                // viejo que todavia lo emita.
                "NOT_ADMIN" => L10n.T("lens.error_not_admin"),
                "NOT_OWNER" => L10n.T("lens.error_not_owner"),
                "BASE_LENS" => L10n.T("lens.error_base_lens"),
                "DEVICE_NOT_AUTHORIZED" => L10n.T("lens.error_not_authorized"),
                "LENS_LIMIT_REACHED" => L10n.T("lens.error_limit_reached"),
                _ => L10n.T("lens.error_generic", reason),
            };
            Debug.LogWarning($"[Tablet] {op}: {msg}");
            if (op == "create_lens")
                SetLensStatus(_createStatus, ref _createStatusRoutine, msg, LensStatusClearS, "");
            else
                SetLensStatus(_ownLensStatus, ref _ownLensStatusRoutine, msg, LensStatusClearS, "");
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
            _fsRightLabel = _kit.Label(_fsRightPane.transform, L10n.T("common.od"), LabelKind.StreamChip, TextAlignmentOptions.Center);
            _kit.Size(_fsRightLabel.rectTransform, minH: 26, prefH: 26, flexH: 0);
            _fsStreamRight = MakeStreamView(_fsRightPane.transform);
            _fsStreamRight.raycastTarget = false;
            _fsRightPane.SetActive(false);

            var leftPane = _kit.Box(row, "FsLeftPane", true, 6, null, expandW: true, expandH: false);
            _kit.Size(leftPane, flexW: 1);
            _fsLeftLabel = _kit.Label(leftPane, L10n.T("common.both_eyes"), LabelKind.StreamChip, TextAlignmentOptions.Center);
            _kit.Size(_fsLeftLabel.rectTransform, minH: 26, prefH: 26, flexH: 0);
            _fsStreamLeft = MakeStreamView(leftPane);
            _fsStreamLeft.raycastTarget = false; // deja pasar el tap al fondo (tambien cierra)

            var closeBtn = _kit.Button(_fullscreenStream.transform, L10n.T("common.close"), BtnStyle.Overlay, false, 40, 14);
            PinTopRight(closeBtn.GetComponent<RectTransform>(), closeBtn.GetComponent<LayoutElement>(), 16, 16);
            closeBtn.OnClick = CloseFullscreenStream;
        }

        // ============================================================
        // Modo Standard (P7): stream a pantalla completa + barra superior
        // (escenarios / lente->ojo / salir) + carrusel de 5 parametros
        // ============================================================
        // Fondo semi-transparente para los overlays del modo Standard (barra superior
        // y carrusel) que flotan sobre el stream a pantalla completa: mismo color de
        // panel (Surface) que ya usa el kit para paneles opacos (StdSliderPanel,
        // Card, HeaderBar...), con alpha ~0.6 para mantener legible el stream detras.
        // Se pasa como delegado a _kit.Panel asi que sigue registrado via Register:
        // se retematiza en caliente (oscuro/claro) igual que cualquier otro widget.
        private static Color OverlaySurface(TabletPalette p) => new Color(p.Surface.r, p.Surface.g, p.Surface.b, 0.6f);

        // Ancla un overlay de ancho completo (menos margen lateral simetrico) al
        // borde superior/inferior de la pantalla con altura fija -- lo usan
        // StdTopBar/StdCarousel/StdSliderPanel de BuildStandardScreen, que ya no
        // cuelgan de un ancestro con LayoutGroup que los dimensione (mismo motivo
        // que Stretch/PinTopRight mas abajo).
        private static void PinTop(RectTransform rt, float height, float marginSide, float marginTop)
        {
            rt.anchorMin = new Vector2(0f, 1f); rt.anchorMax = new Vector2(1f, 1f);
            rt.pivot = new Vector2(0.5f, 1f);
            rt.sizeDelta = new Vector2(-2f * marginSide, height);
            rt.anchoredPosition = new Vector2(0f, -marginTop);
        }

        private static void PinBottom(RectTransform rt, float height, float marginSide, float marginBottom)
        {
            rt.anchorMin = new Vector2(0f, 0f); rt.anchorMax = new Vector2(1f, 0f);
            rt.pivot = new Vector2(0.5f, 0f);
            rt.sizeDelta = new Vector2(-2f * marginSide, height);
            rt.anchoredPosition = new Vector2(0f, marginBottom);
        }

        // Chip de pane del Standard ("OD — ...", "Ambos ojos — ..."): fuera del
        // flow del VerticalLayoutGroup del pane (LayoutElement.ignoreLayout) para
        // que el stream ocupe el pane COMPLETO, y anclado justo debajo de la barra
        // superior flotante para no superponerse con sus botones (bug cosmetico
        // visto en dual-pane). raycastTarget off: flota sobre el stream y no debe
        // consumir toques.
        private static void FloatStdPaneChip(TMP_Text chip, float topOffset)
        {
            chip.raycastTarget = false;
            var le = chip.gameObject.AddComponent<LayoutElement>();
            le.ignoreLayout = true;
            var rt = chip.rectTransform;
            rt.anchorMin = new Vector2(0f, 1f); rt.anchorMax = new Vector2(1f, 1f);
            rt.pivot = new Vector2(0.5f, 1f);
            rt.sizeDelta = new Vector2(0f, 26f);
            rt.anchoredPosition = new Vector2(0f, -topOffset);
        }

        private void BuildStandardScreen(Transform parent)
        {
            _standardScreen = new GameObject("StandardScreen", typeof(RectTransform));
            _standardScreen.transform.SetParent(parent, false);
            Stretch(_standardScreen.GetComponent<RectTransform>());
            _standardScreen.SetActive(false);

            // Fondo lightbox (mismo criterio que FullscreenStream: no tematizado).
            var bg = new GameObject("StdBg", typeof(RectTransform), typeof(Image));
            bg.transform.SetParent(_standardScreen.transform, false);
            Stretch(bg.GetComponent<RectTransform>());
            bg.GetComponent<Image>().color = Color.black;

            // Medidas de los overlays flotantes (topBar/carrusel/slider) -- ver
            // PinTop/PinBottom arriba.
            const float margin = 16f, topBarH = 56f, topMargin = 12f;
            const float carouselH = 106f, carouselMargin = 12f, sliderGap = 10f, sliderH = 96f;

            // ---- Stream a pantalla completa (0,0->1,1): pedido del clinico de
            // aprovechar toda la pantalla de la tablet (ver docs/tablet.md P7/
            // Standard). Barra superior y carrusel pasan a flotar ENCIMA como
            // overlays translucidos en vez de reservarle franjas de layout -- este
            // _kit.Box necesita Stretch() explicito porque ya no cuelga de un
            // ancestro con LayoutGroup que lo dimensione (mismo gotcha de
            // FullscreenRow, ver Decisiones en docs/tablet.md).
            var streamRow = _kit.Box(_standardScreen.transform, "StdStreamRow", false, 0, null, expandW: true, expandH: true);
            Stretch(streamRow);
            // OD-primero (convencion clinica, ver docs/tablet.md): el pane derecho
            // se crea antes para quedar a la izquierda del StdStreamRow horizontal.
            _stdRightPane = _kit.Box(streamRow, "StdRightPane", true, 6, null, expandW: true, expandH: false).gameObject;
            _kit.Size(_stdRightPane.GetComponent<RectTransform>(), flexW: 1);
            _stdRightLabel = _kit.Label(_stdRightPane.transform, L10n.T("common.od"), LabelKind.StreamChip, TextAlignmentOptions.Center);
            FloatStdPaneChip(_stdRightLabel, topMargin + topBarH + 8f);
            _stdStreamRight = MakeStreamView(_stdRightPane.transform, envelope: true);
            // El chip flota SOBRE el stream: al frente en el orden de hermanos, si no
            // el StreamWrap (creado despues) lo tapa (visto en dispositivo, v0.4.0).
            _stdRightLabel.transform.SetAsLastSibling();
            _stdRightPane.SetActive(false);
            var stdLeftPane = _kit.Box(streamRow, "StdLeftPane", true, 6, null, expandW: true, expandH: false);
            _kit.Size(stdLeftPane, flexW: 1);
            _stdLeftLabel = _kit.Label(stdLeftPane, L10n.T("common.both_eyes"), LabelKind.StreamChip, TextAlignmentOptions.Center);
            FloatStdPaneChip(_stdLeftLabel, topMargin + topBarH + 8f);
            _stdStreamLeft = MakeStreamView(stdLeftPane, envelope: true);
            _stdLeftLabel.transform.SetAsLastSibling();

            // ---- Barra superior: escenarios | lente | salir (overlay flotante
            // sobre el stream, fondo semi-transparente -- ver OverlaySurface) ----
            var topBar = _kit.Panel(_standardScreen.transform, "StdTopBar", OverlaySurface, 14, false, 8,
                new RectOffset(14, 14, 0, 0));
            PinTop(topBar, topBarH, margin, topMargin);
            topBar.GetComponent<HorizontalLayoutGroup>().childAlignment = TextAnchor.MiddleLeft;
            _stdScenarioList = _kit.Box(topBar, "StdScenarios", false, 6, null, expandW: false);
            _kit.Spacer(topBar, 0, true);
            var lensBtn = _kit.Button(topBar, L10n.T("standard.lens_button"), BtnStyle.Accent, false, 48, 15);
            _kit.Size(lensBtn.GetComponent<RectTransform>(), minW: 140, prefW: 140, flexW: 0);
            lensBtn.OnClick = OpenStandardLensOverlay;
            // B3: mismo comando que el header Pro -- sin gating de admin, accion
            // clinica no destructiva.
            var recenterStdBtn = _kit.Button(topBar, L10n.T("main.recenter"), BtnStyle.Neutral, false, 48, 15);
            _kit.Size(recenterStdBtn.GetComponent<RectTransform>(), minW: 110, prefW: 110, flexW: 0);
            recenterStdBtn.OnClick = OnRecenterPressed;
            var exitBtn = _kit.Button(topBar, L10n.T("common.exit"), BtnStyle.Neutral, false, 48, 15);
            _kit.Size(exitBtn.GetComponent<RectTransform>(), minW: 110, prefW: 110, flexW: 0);
            // "Salir" en Standard desconecta y vuelve al discovery (ConnectScreen),
            // NO cierra la app: en dispositivo se observo un Application.Quit
            // disparado por un camino de UI no intencional en esta pantalla
            // (postmortem en docs/tablet.md). Sin Quit aca, ningun toque en
            // Standard puede matar la app en medio de una consulta; salir de la
            // app queda solo en el Salir de la ConnectScreen.
            exitBtn.OnClick = OnDisconnectPressed;

            // ---- Carrusel de iconos circulares (overlay flotante, mismo fondo) ----
            var carousel = _kit.Panel(_standardScreen.transform, "StdCarousel", OverlaySurface, 14, false, 16,
                new RectOffset(14, 14, 0, 0));
            PinBottom(carousel, carouselH, margin, carouselMargin);
            carousel.GetComponent<HorizontalLayoutGroup>().childAlignment = TextAnchor.MiddleCenter;
            _stdIcons.Clear();
            foreach (var (key, label) in StandardCarousel)
            {
                string k = key;
                var btn = _kit.CircleIcon(carousel, "Icon_" + key, out var ring, out var glyph);
                BuildStandardGlyph(k, glyph);
                btn.OnClick = () => OnStandardIconPressed(k);
                _stdIcons[k] = (btn, ring);
            }

            // ---- Panel del slider (visible al seleccionar un icono): se crea
            // DESPUES de topBar/carousel para seguir dibujandose por encima de
            // ambos (punto 4 del pedido), flotando justo arriba del carrusel. Su
            // fondo opaco (p => p.Surface) no se toca -- ya se dibujaba encima del
            // contenido antes de este cambio.
            var sliderPanel = _kit.Panel(_standardScreen.transform, "StdSliderPanel", p => p.Surface, 14, true, 4, new RectOffset(0, 0, 0, 0));
            _stdSliderPanel = sliderPanel.gameObject;
            var spCol = _kit.Box(sliderPanel, "StdSliderCol", true, 4, new RectOffset(18, 18, 10, 10), expandW: true);
            Stretch(spCol);
            var spHeader = _kit.Box(spCol, "StdSliderHeader", false, 8, null, expandW: true);
            spHeader.GetComponent<HorizontalLayoutGroup>().childAlignment = TextAnchor.MiddleLeft;
            _stdSliderTitle = _kit.Label(spHeader, "", LabelKind.Section, TextAlignmentOptions.Left);
            _kit.Size(_stdSliderTitle.rectTransform, flexW: 1);
            _stdSliderValue = _kit.Label(spHeader, "", LabelKind.Value, TextAlignmentOptions.Right);
            _stdSlider = _kit.Slider(spCol);
            _stdSlider.onValueChanged.AddListener(OnStandardSliderChanged);
            // Separacion visual entre el slider de magnitud y la fila del eje: el
            // spacing de 4 del StdSliderCol (compartido con el header de arriba) los
            // dejaba pegados, leyendose como un solo control en vez de dos.
            _kit.Spacer(spCol, 12, false);
            // Fila secundaria del eje (solo astigmatismo).
            _stdAxisRow = _kit.Box(spCol, "StdAxisRow", true, 2, null, expandW: true).gameObject;
            var axHeader = _kit.Box(_stdAxisRow.transform, "StdAxisHeader", false, 8, null, expandW: true);
            axHeader.GetComponent<HorizontalLayoutGroup>().childAlignment = TextAnchor.MiddleLeft;
            var axLabel = _kit.Label(axHeader, L10n.T("main.astig_axis_label"), LabelKind.Body, TextAlignmentOptions.Left);
            _kit.Size(axLabel.rectTransform, flexW: 1);
            _stdAxisValue = _kit.Label(axHeader, "", LabelKind.Value, TextAlignmentOptions.Right);
            _stdAxisSlider = _kit.Slider(_stdAxisRow.transform);
            _stdAxisSlider.onValueChanged.AddListener(OnStandardAxisChanged);
            PinBottom(sliderPanel, sliderH, margin, carouselMargin + carouselH + sliderGap);
            _stdSliderPanel.SetActive(false);

            BuildStandardLensOverlay();
        }

        // Glifos por codigo (sin PNGs, tematizados via kit.Tint): aproximaciones
        // geometricas simples de cada parametro clinico.
        private void BuildStandardGlyph(string key, RectTransform area)
        {
            Func<TabletPalette, Color> icon = p => p.Icon;
            Func<TabletPalette, Color> hole = p => p.SurfaceRaised;
            switch (key)
            {
                case "astig_magnitude": // anillo + eje inclinado
                    _kit.GlyphCircle(area, 36, Vector2.zero, icon);
                    _kit.GlyphCircle(area, 27, Vector2.zero, hole);
                    _kit.GlyphBar(area, 44, 4, -35f, icon);
                    break;
                case "halo_intensity": // anillos concentricos
                    _kit.GlyphCircle(area, 42, Vector2.zero, icon);
                    _kit.GlyphCircle(area, 33, Vector2.zero, hole);
                    _kit.GlyphCircle(area, 22, Vector2.zero, icon);
                    _kit.GlyphCircle(area, 12, Vector2.zero, hole);
                    break;
                case "halo_extra_rings": // pupila dilatada
                    _kit.GlyphCircle(area, 42, Vector2.zero, icon);
                    _kit.GlyphCircle(area, 34, Vector2.zero, hole);
                    _kit.GlyphCircle(area, 22, Vector2.zero, icon);
                    break;
                case "destello_intensity": // estrella de 4 puntas
                    _kit.GlyphBar(area, 46, 5, 0f, icon);
                    _kit.GlyphBar(area, 46, 5, 90f, icon);
                    _kit.GlyphCircle(area, 12, Vector2.zero, icon);
                    break;
                case "destello_rayos": // spokes radiales
                    _kit.GlyphBar(area, 46, 4, 0f, icon);
                    _kit.GlyphBar(area, 46, 4, 45f, icon);
                    _kit.GlyphBar(area, 46, 4, 90f, icon);
                    _kit.GlyphBar(area, 46, 4, 135f, icon);
                    _kit.GlyphCircle(area, 10, Vector2.zero, hole);
                    break;
            }
        }

        // Espejo de RefreshFullscreenUI para la pantalla Standard.
        private void RefreshStandardUI(bool isBlend, string leftId, string rightId)
        {
            if (_stdLeftLabel == null) return;
            if (isBlend)
            {
                _stdRightPane.SetActive(true);
                _stdLeftLabel.text = L10n.T("lens.chip_dash_os", LensDisplayName(leftId));
                _stdRightLabel.text = L10n.T("lens.chip_dash_od", LensDisplayName(rightId));
            }
            else
            {
                _stdRightPane.SetActive(false);
                _stdLeftLabel.text = string.IsNullOrEmpty(leftId)
                    ? L10n.T("lens.both_eyes_no_lens") : L10n.T("lens.both_eyes_dash", LensDisplayName(leftId));
            }
        }

        private void RebuildStandardScenarioList()
        {
            if (_stdScenarioList == null) return;
            for (int i = _stdScenarioList.childCount - 1; i >= 0; i--) Destroy(_stdScenarioList.GetChild(i).gameObject);
            _stdScenarioButtons.Clear();
            foreach (var sid in _session.Scenarios)
            {
                string id = sid;
                var btn = _kit.Button(_stdScenarioList, ScenarioLabel(id), BtnStyle.Segment, true, 46, 14);
                _kit.Size(btn.GetComponent<RectTransform>(), minW: 116, prefW: 116, flexW: 0);
                btn.SetOn(id == _session.CurrentScenario, false);
                btn.OnClick = () => OnScenarioPressed(id);
                _stdScenarioButtons[id] = btn;
            }
        }

        // Lente cuyo estado edita el carrusel: la aplicada (OI primero, OD si no).
        private string StandardEditingLensId()
        {
            string leftId = (string)(_session.VisionState["left"]?["lens_id"]) ?? "";
            string rightId = (string)(_session.VisionState["right"]?["lens_id"]) ?? "";
            return !string.IsNullOrEmpty(leftId) ? leftId : rightId;
        }

        private void OnStandardIconPressed(string key)
        {
            // Segundo tap sobre el mismo icono: cerrar el panel.
            if (_stdSelectedKey == key && _stdSliderPanel.activeSelf)
            {
                _stdSelectedKey = "";
                _stdSliderPanel.SetActive(false);
                foreach (var kv in _stdIcons) kv.Value.ring.enabled = false;
                return;
            }

            string lensId = StandardEditingLensId();
            if (string.IsNullOrEmpty(lensId)) { OpenStandardLensOverlay(); return; }
            if (!_session.LensesById.TryGetValue(lensId, out var lens) ||
                !(lens["params"]?[key] is JObject spec) ||
                spec["default"] == null || spec["min"] == null || spec["max"] == null)
            {
                SetStandardSliderHeader(key, null);
                _stdSliderPanel.SetActive(true);
                _stdSliderTitle.text = L10n.T("standard.param_unavailable", ParamMeta.LabelFor(key));
                return;
            }

            _editingLensId = lensId; // CurrentParamValue/SendParamOverride siguen a esta lente
            _stdSelectedKey = key;
            foreach (var kv in _stdIcons) kv.Value.ring.enabled = kv.Key == key;

            float def = (float)spec["default"];
            _stdSlider.minValue = (float)spec["min"];
            _stdSlider.maxValue = (float)spec["max"];
            _stdSlider.wholeNumbers = ParamMeta.IsInteger(key);
            _stdSlider.SetValueWithoutNotify(CurrentParamValue(key, def));
            SetStandardSliderHeader(key, _stdSlider.value);

            // Eje del astigmatismo: slider secundario solo para ese icono.
            bool axis = key == "astig_magnitude";
            _stdAxisRow.SetActive(axis);
            if (axis && lens["params"]?["astig_axis_deg"] is JObject axSpec &&
                axSpec["default"] != null && axSpec["min"] != null && axSpec["max"] != null)
            {
                _stdAxisSlider.minValue = (float)axSpec["min"];
                _stdAxisSlider.maxValue = (float)axSpec["max"];
                _stdAxisSlider.wholeNumbers = ParamMeta.IsInteger("astig_axis_deg");
                _stdAxisSlider.SetValueWithoutNotify(CurrentParamValue("astig_axis_deg", (float)axSpec["default"]));
                _stdAxisValue.text = ParamMeta.FormatValue("astig_axis_deg", _stdAxisSlider.value);
            }
            _stdSliderPanel.SetActive(true);
        }

        private void SetStandardSliderHeader(string key, float? value)
        {
            _stdSliderTitle.text = ParamMeta.LabelFor(key);
            _stdSliderValue.text = value.HasValue ? ParamMeta.FormatValue(key, value.Value) : "";
        }

        private void OnStandardSliderChanged(float v)
        {
            if (_stdSelectedKey == "") return;
            _stdSliderValue.text = ParamMeta.FormatValue(_stdSelectedKey, v);
            SendParamOverride(_stdSelectedKey, v);
        }

        private void OnStandardAxisChanged(float v)
        {
            _stdAxisValue.text = ParamMeta.FormatValue("astig_axis_deg", v);
            SendParamOverride("astig_axis_deg", v);
        }

        // Sincroniza los sliders visibles con un vision_state entrante (el visor
        // confirma/corrige) sin re-emitir override_params (SetValueWithoutNotify).
        private void RefreshStandardSliders()
        {
            if (_stdSliderPanel == null || !_stdSliderPanel.activeSelf || _stdSelectedKey == "") return;
            _stdSlider.SetValueWithoutNotify(CurrentParamValue(_stdSelectedKey, _stdSlider.value));
            _stdSliderValue.text = ParamMeta.FormatValue(_stdSelectedKey, _stdSlider.value);
            if (_stdAxisRow.activeSelf)
            {
                _stdAxisSlider.SetValueWithoutNotify(CurrentParamValue("astig_axis_deg", _stdAxisSlider.value));
                _stdAxisValue.text = ParamMeta.FormatValue("astig_axis_deg", _stdAxisSlider.value);
            }
        }

        // ---- Overlay de seleccion de lente (con eleccion de ojo) ----
        private void BuildStandardLensOverlay()
        {
            _stdLensOverlay = new GameObject("StdLensOverlay", typeof(RectTransform));
            _stdLensOverlay.transform.SetParent(_standardScreen.transform, false);
            Stretch(_stdLensOverlay.GetComponent<RectTransform>());
            _stdLensOverlay.SetActive(false);

            var scrim = new GameObject("StdLensScrim", typeof(RectTransform), typeof(Image), typeof(Button));
            scrim.transform.SetParent(_stdLensOverlay.transform, false);
            Stretch(scrim.GetComponent<RectTransform>());
            scrim.GetComponent<Image>().color = new Color(0f, 0f, 0f, 0.6f);
            var scrimBtn = scrim.GetComponent<Button>();
            scrimBtn.transition = Selectable.Transition.None;
            scrimBtn.onClick.AddListener(CloseStandardLensOverlay);

            var card = _kit.Card(_stdLensOverlay.transform, "StdLensCard");
            card.anchorMin = card.anchorMax = new Vector2(0.5f, 0.5f);
            card.pivot = new Vector2(0.5f, 0.5f);
            card.sizeDelta = new Vector2(560, 560);

            _kit.Label(card, L10n.T("standard.choose_lens_title"), LabelKind.Title, TextAlignmentOptions.Center);
            var scroll = _kit.ScrollColumn(card, out _stdLensListBox);
            _kit.Size(scroll.GetComponent<RectTransform>(), flexH: 1);

            _stdEyePickRow = _kit.Box(card, "StdEyePick", true, 6, null, expandW: true).gameObject;
            _kit.Label(_stdEyePickRow.transform, L10n.T("standard.which_eye_title"), LabelKind.Section, TextAlignmentOptions.Center);
            var eyeRow = _kit.Box(_stdEyePickRow.transform, "StdEyeButtons", false, 8, null, expandW: true);
            var bBoth = _kit.Button(eyeRow, L10n.T("common.both"), BtnStyle.Segment, false, 46, 15);
            var bOd = _kit.Button(eyeRow, L10n.T("standard.eye_od_paren"), BtnStyle.Segment, false, 46, 15);
            var bOi = _kit.Button(eyeRow, L10n.T("standard.eye_os_paren"), BtnStyle.Segment, false, 46, 15);
            _kit.Size(bBoth.GetComponent<RectTransform>(), flexW: 1);
            _kit.Size(bOd.GetComponent<RectTransform>(), flexW: 1);
            _kit.Size(bOi.GetComponent<RectTransform>(), flexW: 1);
            bBoth.OnClick = () => OnStandardEyePicked("both");
            bOd.OnClick = () => OnStandardEyePicked("right");
            bOi.OnClick = () => OnStandardEyePicked("left");
            _stdEyePickRow.SetActive(false);

            var closeBtn = _kit.Button(card, L10n.T("common.close"), BtnStyle.Ghost, false, 42, 14);
            closeBtn.OnClick = CloseStandardLensOverlay;
        }

        private void RebuildStandardLensList()
        {
            if (_stdLensListBox == null) return;
            for (int i = _stdLensListBox.childCount - 1; i >= 0; i--) Destroy(_stdLensListBox.GetChild(i).gameObject);
            foreach (var kv in _session.LensesById)
            {
                string id = kv.Key;
                string nombre = (string)kv.Value["nombre"] ?? id;
                var btn = _kit.Button(_stdLensListBox, nombre, BtnStyle.Card, false, 56, 16);
                btn.OnClick = () => { _stdPendingLensId = id; _stdEyePickRow.SetActive(true); };
            }
        }

        private void OpenStandardLensOverlay()
        {
            _stdPendingLensId = "";
            _stdEyePickRow.SetActive(false);
            _stdLensOverlay.SetActive(true);
        }

        private void CloseStandardLensOverlay() => _stdLensOverlay?.SetActive(false);

        private void OnStandardEyePicked(string eye)
        {
            if (string.IsNullOrEmpty(_stdPendingLensId)) return;
            ApplyLensTo(_stdPendingLensId, eye);
            CloseStandardLensOverlay();
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

            _updateTitleLabel = _kit.Label(card, L10n.T("update.available_title"), LabelKind.Title, TextAlignmentOptions.Center);
            _updateVersionLabel = _kit.Label(card, "", LabelKind.Subtitle, TextAlignmentOptions.Center);
            _updateChangelogLabel = _kit.Label(card, "", LabelKind.Hint, TextAlignmentOptions.Left);
            _updateStatusLabel = _kit.Label(card, "", LabelKind.Hint, TextAlignmentOptions.Center);
            _kit.Spacer(card, 4, false);

            var row = _kit.Box(card, "UpdateButtons", false, 8, null, expandW: true);
            _updateSecondaryBtn = _kit.Button(row, L10n.T("update.button_postpone"), BtnStyle.Ghost, false, 48, 16);
            _kit.Size(_updateSecondaryBtn.GetComponent<RectTransform>(), flexW: 1);
            _updatePrimaryBtn = _kit.Button(row, L10n.T("update.button_update"), BtnStyle.Accent, false, 48, 16);
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

            // Fase C (kiosco, ver docs/updates.md): nadie va a tocar la tablet para
            // aceptar el cartel -- auto-aceptar la descarga en background, sin UI. La
            // decision de CUANDO instalar (ahora vs. al quedar ociosa) se toma en
            // OnUpdateReadyToInstall, cuando la descarga+verificacion ya terminaron.
            if (KioskManager.IsDeviceOwner)
            {
                Debug.Log("[Tablet] update auto-aceptado (kiosco)");
                UpdateManager.Instance?.AcceptUpdate();
                return;
            }

            _updateTitleLabel.text = L10n.T("update.available_title");
            _updateVersionLabel.text = L10n.T("update.version_arrow", Application.version, manifest.ApkVersion);
            _updateChangelogLabel.text = manifest.Changelog ?? "";
            _updateStatusLabel.text = "";
            _updatePrimaryBtn.gameObject.SetActive(true);
            _updatePrimaryBtn.Label.text = L10n.T("update.button_update");
            _updatePrimaryBtn.OnClick = OnUpdateAcceptPressed;
            _updateSecondaryBtn.gameObject.SetActive(!forced); // "Ahora no" oculto si es forzada
            _updateSecondaryBtn.Label.text = L10n.T("update.button_postpone");
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
            _updateTitleLabel.text = L10n.T("update.downloading_title");
            _updateStatusLabel.text = L10n.T("update.downloading_progress", 0);
            _updatePrimaryBtn.gameObject.SetActive(false);
            _updateSecondaryBtn.gameObject.SetActive(true); // Cancelar siempre disponible, incluso si es forzada
            _updateSecondaryBtn.Label.text = L10n.T("common.cancel");
            _updateSecondaryBtn.OnClick = OnUpdateCancelPressed;
        }

        private void OnUpdateDownloadProgress(float progress)
        {
            if (_updateStatusLabel == null) return;
            _updateStatusLabel.text = L10n.T("update.downloading_progress", Mathf.RoundToInt(progress * 100f));
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
            // Fase C (kiosco): instalar YA si el update es forzado o si la tablet ya
            // esta ociosa (sin sesion con el visor); si el clinico esta en consulta,
            // diferir hasta que vuelva a quedar ociosa (ShowConnectScreen) -- un
            // paciente nunca ve reiniciarse la tablet a mitad de una consulta.
            if (KioskManager.IsDeviceOwner)
            {
                if (_updateForced || !_session.IsSessionActive)
                {
                    LaunchSilentInstall();
                }
                else
                {
                    _installWhenIdle = true;
                    Debug.Log("[Tablet] update listo, se instala al quedar ociosa (kiosco, sesion activa con el visor)");
                }
                return;
            }

            _updateTitleLabel.text = L10n.T("update.verified");
            _updateStatusLabel.text = L10n.T("update.verified");
            _updatePrimaryBtn.gameObject.SetActive(true);
            _updatePrimaryBtn.Label.text = L10n.T("update.button_install");
            _updatePrimaryBtn.OnClick = OnUpdateInstallPressed;
            _updateSecondaryBtn.gameObject.SetActive(false);
            _updateScreen.SetActive(true);
        }

        private void OnUpdateInstallPressed()
        {
            UpdateManager.Instance?.LaunchInstall();
            HideUpdateScreen();
        }

        // Fase C (kiosco): estado sin botones mientras PackageInstaller instala en
        // background -- no hay nada que el clinico deba tocar, solo informar que la
        // tablet se va a reiniciar sola (el proceso muere al instalar; la HOME
        // persistente la relanza, ver KioskManager.ApplyPolicies).
        private void ShowUpdateInstalling()
        {
            _updateTitleLabel.text = L10n.T("update.installing_title");
            _updateStatusLabel.text = L10n.T("update.installing_body_tablet");
            _updatePrimaryBtn.gameObject.SetActive(false);
            _updateSecondaryBtn.gameObject.SetActive(false);
            _updateScreen.SetActive(true);
        }

        // CRITICO #2a (correcciones, ver docs/updates.md): llama LaunchInstall()
        // PRIMERO y recien DESPUES decide si mostrar el modal "Instalando..." --
        // antes se mostraba a ciegas ANTES de saber si la instalacion silenciosa
        // arranco de verdad (un fallo sincrono de SilentInstaller.install() dejaba
        // el modal puesto para siempre, ver OnUpdateFailed). Arma tambien el
        // watchdog de 120s (CRITICO #2c) para el caso en que ni el commit
        // silencioso ni ningun fallo lleguen nunca a un resultado.
        private void LaunchSilentInstall()
        {
            _silentInstallResolved = false;
            UpdateManager.Instance?.LaunchInstall();
            if (UpdateManager.Instance?.LastInstallLaunchResult == UpdateInstaller.InstallLaunchResult.StartedSilent)
            {
                ShowUpdateInstalling();
                if (_silentInstallWatchdogCo != null) StopCoroutine(_silentInstallWatchdogCo);
                _silentInstallWatchdogCo = StartCoroutine(SilentInstallWatchdogCo());
            }
        }

        private IEnumerator SilentInstallWatchdogCo()
        {
            yield return new WaitForSecondsRealtime(SilentInstallWatchdogSeconds);
            _silentInstallWatchdogCo = null;
            Debug.LogWarning($"[Tablet] instalacion silenciosa sin resultado tras {SilentInstallWatchdogSeconds:F0}s.");
            HandleSilentInstallFailure("timeout");
        }

        // CRITICO #2c (correcciones): resultado ASINCRONICO del commit de
        // PackageInstaller -- InstallResultReceiver.java llama esto via
        // UnitySendMessage("TabletApp", "OnSilentInstallResult", ...) para
        // cualquier status distinto de STATUS_SUCCESS/STATUS_PENDING_USER_ACTION
        // (payload "<status>|<message o vacio>"). Es el caso REAL de campo: un
        // INSTALL_FAILED_* (firma distinta, downgrade, sin espacio) que hoy dejaba
        // el modal "Instalando..." puesto para siempre, sin telemetria.
        public void OnSilentInstallResult(string payload)
        {
            string detail = payload ?? "";
            int sep = detail.IndexOf('|');
            if (sep >= 0)
                detail = "status=" + detail.Substring(0, sep) + " message=" + detail.Substring(sep + 1);
            HandleSilentInstallFailure(detail);
        }

        private void HandleSilentInstallFailure(string detail)
        {
            // MENOR (correcciones): no-op si ya se resolvio (watchdog y
            // OnSilentInstallResult pueden dispararse los dos para el MISMO intento
            // -- ver el comentario de _silentInstallResolved).
            if (_silentInstallResolved) return;
            _silentInstallResolved = true;

            if (_silentInstallWatchdogCo != null)
            {
                StopCoroutine(_silentInstallWatchdogCo);
                _silentInstallWatchdogCo = null;
            }
            HideUpdateScreen();
            Debug.Log("[Tablet] instalacion silenciosa fallo (" + detail + "), reintenta en el proximo chequeo de manifest.");
            UpdateManager.Instance?.ReportInstallFailure(detail);
            DeletePendingUpdateMarker();
        }

        // Borra el marker que UpdateInstaller ya escribio ANTES del commit -- sin
        // esto, el proximo arranque lo leeria en CheckPendingUpdateMarker y
        // reportaria update_incomplete por un fallo que ya reportamos aca
        // (update_install_failed); borrarlo es lo coherente (ver docs/updates.md).
        private void DeletePendingUpdateMarker()
        {
            try
            {
                string path = System.IO.Path.Combine(Application.persistentDataPath, UpdateLogic.PendingMarkerFileName);
                if (System.IO.File.Exists(path)) System.IO.File.Delete(path);
            }
            catch (Exception e)
            {
                Debug.LogWarning("[Tablet] no se pudo borrar update_pending.json tras fallo silencioso (" + e.GetType().Name + ").");
            }
        }

        private void OnUpdateFailed(string message)
        {
            // Fase C (kiosco): nadie va a cerrar un cartel de error en una tablet sin
            // supervision -- solo log. El proximo arranque re-chequea el manifest y
            // reintenta solo; la telemetria update_download_failed ya sale de
            // UpdateManager (no hace falta duplicarla aca).
            if (KioskManager.IsDeviceOwner)
            {
                // CRITICO #2b (correcciones): antes hacia return ACA sin ocultar el
                // modal -- si LaunchInstall() fallaba SINCRONICAMENTE (ver
                // LaunchSilentInstall) el scrim de "Instalando..." podia quedar
                // puesto para siempre (si ya se habia mostrado por un intento
                // previo) o directamente no importaba porque nunca se llego a
                // mostrar (el orden nuevo evita eso) -- de cualquier forma, un
                // fallo de update NUNCA debe dejar un overlay bloqueando la UI.
                HideUpdateScreen();
                Debug.Log("[Tablet] update fallo en kiosco, sin cartel (reintenta en el proximo arranque): " + message);
                return;
            }

            _updateTitleLabel.text = L10n.T("update.error_title");
            _updateStatusLabel.text = FriendlyUpdateError(message);
            _updatePrimaryBtn.gameObject.SetActive(true);
            _updatePrimaryBtn.Label.text = L10n.T("common.retry");
            _updatePrimaryBtn.OnClick = OnUpdateRetryPressed;
            _updateSecondaryBtn.gameObject.SetActive(!_updateForced); // "Cerrar" oculto si es forzada
            _updateSecondaryBtn.Label.text = L10n.T("common.close");
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
            raw == "sha_mismatch" ? L10n.T("update.error_sha_mismatch") : raw;

        // Vista de stream por ojo: contenedor flexible (lo dimensiona la columna) con
        // un RawImage que se ajusta dentro preservando el aspecto 4:3 del visor (sin
        // distorsion / sin estirar). El placeholder oscuro = "sin señal".
        // envelope=true (modo Standard, ver BuildStandardScreen): en vez de encajar
        // DENTRO del area disponible (FitInParent, deja franjas vacias arriba/abajo
        // o a los costados), la imagen CRECE hasta cubrirla por completo
        // (AspectRatioFitter.EnvelopeParent) recortando lo que sobra -- por eso el
        // "wrap" necesita un RectMask2D: sin el, el RawImage agrandado se saldria de
        // su rect y pisaria el pane/label vecino. Panel normal y FullscreenStream NO
        // pasan este parametro: siguen con FitInParent de siempre (no se tocan).
        private RawImage MakeStreamView(Transform pane, bool envelope = false)
        {
            var wrap = new GameObject("StreamWrap", typeof(RectTransform));
            wrap.transform.SetParent(pane, false);
            _kit.Size(wrap.GetComponent<RectTransform>(), flexW: 1, flexH: 1);
            if (envelope) wrap.AddComponent<RectMask2D>();
            var img = _kit.RawImage(wrap.transform);
            img.color = new Color(0.03f, 0.04f, 0.06f, 1f);
            var rt = img.rectTransform;
            rt.anchorMin = rt.anchorMax = new Vector2(0.5f, 0.5f);
            rt.pivot = new Vector2(0.5f, 0.5f);
            var arf = img.gameObject.AddComponent<AspectRatioFitter>();
            arf.aspectMode = envelope ? AspectRatioFitter.AspectMode.EnvelopeParent : AspectRatioFitter.AspectMode.FitInParent;
            arf.aspectRatio = 768f / 576f;
            return img;
        }

        // Glifo de marca: sprite del simbolo IOLSIMULATOR (rebranding 2026-09-03,
        // Assets/Resources/TabletBrand/logo_mark.png), reemplaza el glifo "ojo" dibujado por
        // codigo (3 circulos concentricos) que había antes de tener arte propio. Sin Tint: el
        // sprite ya trae su propio gradiente/color, el repaint de tema no debe entintarlo.
        private void EyeGlyph(Transform parent, float size)
        {
            var root = new GameObject("BrandGlyph", typeof(RectTransform));
            root.transform.SetParent(parent, false);
            _kit.Size(root.GetComponent<RectTransform>(), minW: size, minH: size, prefW: size, prefH: size, flexW: 0);
            var img = root.AddComponent<Image>();
            img.sprite = _brandMark;
            img.preserveAspect = true;
            img.color = Color.white;
            img.raycastTarget = false;
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
