using System;
using System.Collections.Generic;
using Newtonsoft.Json.Linq;
using Simulador.Net;
using UnityEngine;

namespace Simulador.Tablet
{
    /// <summary>
    /// Capa de sesion/protocolo de la tablet, separada de la UI (P6.2). Plain C# (NO
    /// MonoBehaviour): posee el <see cref="WebSocketClient"/> y el
    /// <see cref="DiscoveryListener"/>, el flujo de conexion/emparejamiento por PIN,
    /// la maquina de reconexion automatica (P2.5) y el estado de sesion (vision_state,
    /// catalogo, escenarios, cache de PIN por host). Expone eventos tipados hacia la
    /// UI y no toca ningun componente de Unity UI directamente -- <see cref="Net.TabletController"/>
    /// (MonoBehaviour) es quien construye pantallas/widgets y reacciona a estos eventos.
    ///
    /// Port 1:1 (refactor mecanico, ver docs/tablet.md) del codigo que antes vivia
    /// inline en TabletController: mismos nombres de metodo/campo donde fue posible,
    /// mismas ramas de decision. Los pumps de WS/discovery se siguen drenando desde el
    /// Update() del MonoBehaviour via <see cref="Update"/> -- esta clase no crea
    /// threads propios ni toca API de Unity fuera del hilo principal (los threads de
    /// socket/UDP siguen siendo responsabilidad de WebSocketClient/DiscoveryListener).
    /// </summary>
    public class TabletSession
    {
        private const int WsPort = 9090;
        private const float HostTimeout = 6f; // s: hosts vistos hace mas de esto se descartan

        // --- Red ---
        private DiscoveryListener _disc;
        private WebSocketClient _ws;
        private bool _connecting, _sessionActive, _manualDisconnect;
        private string _currentHost = "";
        private readonly Dictionary<string, float> _seenHosts = new();

        // --- Emparejamiento por PIN (en memoria, nunca a disco) ---
        private readonly Dictionary<string, string> _pinByHost = new();
        private string _pendingAuthPin = "";
        private bool _authFailed;
        private bool _authLocked;
        private int _authLockRetrySeconds;

        // --- Reconexion automatica (P2.5) ---
        // Ante una caida NO manual de una sesion activa, reintenta solo a la ultima
        // sesion (host + PIN cacheado) con backoff 2/4/8/15s (tope 15s, indefinido)
        // hasta que el usuario cancela o el visor devuelve auth_fail/hello.
        private bool _reconnecting;
        private int _reconnectAttempt;
        private float _reconnectCountdown;
        private const float ReconnectBaseDelayS = 2f;
        private const float ReconnectMaxDelayS = 15f;

        // --- Catalogo / estado ---
        private readonly Dictionary<string, JObject> _lensesById = new();
        private JObject _visionState = new();
        private List<string> _scenarios = new();
        private readonly Dictionary<string, string> _scenarioLabels = new();

        // ============================================================
        // Estado publico read-only para la UI
        // ============================================================
        public bool IsConnecting => _connecting;
        public bool IsSessionActive => _sessionActive;
        public bool IsReconnecting => _reconnecting;
        public bool IsWsOpen => _ws != null && _ws.IsOpen;
        public string CurrentHost => _currentHost;
        /// <summary>Hosts vistos por el beacon UDP en los ultimos <c>HostTimeout</c> segundos.</summary>
        public IEnumerable<string> DiscoveredHosts => _seenHosts.Keys;
        public IReadOnlyDictionary<string, JObject> LensesById => _lensesById;
        /// <summary>
        /// Estado de vision_state (left/right/blend_active) TAL COMO lo manda el
        /// visor. Se expone la referencia mutable (no una copia): la UI la lee para
        /// pintar widgets y tambien la muta para la actualizacion optimista al tocar
        /// una lente (mismo patron que tenia TabletController antes del split, ver
        /// OnLensSelected en Net/TabletController.cs).
        /// </summary>
        public JObject VisionState => _visionState;
        public IReadOnlyList<string> Scenarios => _scenarios;
        public IReadOnlyDictionary<string, string> ScenarioLabels => _scenarioLabels;
        /// <summary>
        /// Escenario seleccionado. La UI lo actualiza OPTIMISTAMENTE al tocar un
        /// boton de escenario (el protocolo no confirma load_scenario con un mensaje
        /// propio) -- por eso tiene setter publico, igual que _visionState.
        /// </summary>
        public string CurrentScenario { get; set; } = "";

        // ============================================================
        // Eventos hacia la UI
        // ============================================================
        /// <summary>El WebSocket conecto (TCP) y ya se mando el "auth"; falta la respuesta.</summary>
        public event Action Connected;
        /// <summary>PIN aceptado por el visor; falta el "hello".</summary>
        public event Action AuthOk;
        /// <summary>Hay que mostrar/actualizar el PinScreen con este mensaje (PIN incorrecto o lockout sin reconexion en curso).</summary>
        public event Action<string> PinScreenRequested;
        /// <summary>Hay que mostrar el ConnectScreen con este mensaje (y si es error).</summary>
        public event Action<string, bool> ShowConnectScreenRequested;
        /// <summary>Arranca un ciclo de reconexion automatica: mostrar el ReconnectScreen con este mensaje inicial.</summary>
        public event Action<string> ReconnectStarted;
        /// <summary>Actualiza el texto de estado del ReconnectScreen ya visible.</summary>
        public event Action<string> ReconnectStatusChanged;
        /// <summary>"hello" recibido (catalogo/vision_state/escenarios ya actualizados en el estado de la sesion). Payload: el JArray crudo de lentes (nombre/descripcion para las cards).</summary>
        public event Action<JArray> HelloReceived;
        /// <summary>Llego un "vision_state" fuera de un hello (confirmacion de un comando).</summary>
        public event Action VisionStateChanged;
        /// <summary>Frame de stream recibido: header ('B'/'L'/'R') ya separado del JPG.</summary>
        public event Action<char, byte[]> FrameReceived;

        // ============================================================
        public void Begin()
        {
            _ws = new WebSocketClient();
            _ws.Connected += OnWsConnected;
            _ws.Disconnected += OnWsDisconnected;
            _ws.TextReceived += OnText;
            _ws.BinaryReceived += OnBinary;

            _disc = new DiscoveryListener();
            _disc.VisorDiscovered += host => _seenHosts[host] = Time.time;
            _disc.Start();
        }

        /// <summary>Llamar desde el Update() del MonoBehaviour de UI (drena WS/discovery, cuenta atras de reconexion).</summary>
        public void Update(float deltaTime)
        {
            _disc?.PumpEvents();
            _ws?.PumpEvents();

            // Poda de hosts viejos. En el TabletController original esto corria dentro
            // de RefreshDiscovered (solo con el ConnectScreen visible, 1 vez/seg); acá
            // se hace en cada Update sin ese guard -- equivalente observable (nadie lee
            // DiscoveredHosts salvo el ConnectScreen, así que podar más seguido no
            // cambia qué ve el usuario, solo lo mantiene más al día).
            var stale = new List<string>();
            foreach (var kv in _seenHosts) if (Time.time - kv.Value > HostTimeout) stale.Add(kv.Key);
            foreach (var h in stale) _seenHosts.Remove(h);

            // P2.5: cuenta atras del backoff. No tickear mientras hay un intento de
            // conexion en vuelo (_connecting) -- el propio Connect/Disconnected decide
            // cuando programar el siguiente.
            if (_reconnecting && !_connecting)
            {
                _reconnectCountdown -= deltaTime;
                if (_reconnectCountdown <= 0f) DoReconnectAttempt();
            }
        }

        public void Shutdown()
        {
            _disc?.Stop();
            _ws?.Close();
        }

        // ============================================================
        // Conexion / PIN
        // ============================================================
        public bool TryGetCachedPin(string host, out string pin)
        {
            if (_pinByHost.TryGetValue(host, out pin) && !string.IsNullOrEmpty(pin)) return true;
            pin = null;
            return false;
        }

        /// <summary>Abre el WebSocket hacia host y manda el auth con pin al conectar. La UI ya debe haber mostrado el ConnectScreen "Conectando a host..." antes de llamar esto.</summary>
        public void Connect(string host, string pin)
        {
            _currentHost = host;
            _pendingAuthPin = pin;
            _authFailed = false;
            // MAYOR (revision post-split, heredado): _manualDisconnect puede haber
            // quedado en true si el usuario cancelo una reconexion automatica mientras
            // esperaba el backoff (sin conexion en vuelo, sin evento de socket que lo
            // consumiera). Sin este reset, la PROXIMA caida no manual de esta sesion
            // nueva se mostraria como "Sesion finalizada" en vez de reconectar.
            _manualDisconnect = false;
            _connecting = true;
            _ws.Connect(host, WsPort);
        }

        /// <summary>Desconexion manual (boton "Desconectar" de la UI).</summary>
        public void Disconnect()
        {
            _manualDisconnect = true;
            _reconnecting = false; // defensivo: no hay Reconectar visible en MainScreen, pero por si acaso
            _ws.Close();
        }

        public bool SendCommand(JObject cmd)
        {
            if (_ws == null || !_ws.IsOpen) return false;
            _ws.SendText(cmd.ToString(Newtonsoft.Json.Formatting.None));
            return true;
        }

        private void OnWsConnected()
        {
            _connecting = false;
            Connected?.Invoke();
            SendCommand(new JObject { ["type"] = "auth", ["pin"] = _pendingAuthPin });
        }

        private void OnWsDisconnected()
        {
            if (_authLocked)
            {
                // Lockout del visor (demasiados PIN incorrectos acumulados en su
                // sesion): distinto de un PIN puntual mal tipeado, así que el
                // mensaje NO dice "PIN incorrecto" (el PIN puede ser el correcto).
                _authLocked = false;
                _connecting = false;
                string msg = _authLockRetrySeconds > 0
                    ? "Demasiados intentos. Esperá " + _authLockRetrySeconds + "s y volvé a intentarlo."
                    : "Demasiados intentos. Esperá un momento y volvé a intentarlo.";
                if (_reconnecting)
                {
                    // P2.5: durante una reconexion automatica NO se abandona el loop -- el
                    // PIN cacheado puede ser el correcto (el visor ni lo evaluo). Se espera
                    // el retry_in_s indicado por el visor y se reintenta solo.
                    _reconnectCountdown = _authLockRetrySeconds > 0 ? _authLockRetrySeconds : ReconnectBaseDelayS;
                    ReconnectStatusChanged?.Invoke(msg);
                }
                else
                {
                    PinScreenRequested?.Invoke(msg);
                }
                return;
            }
            if (_authFailed)
            {
                // PIN invalido (p.ej. el visor reinicio y genero un PIN nuevo): no tiene
                // sentido seguir reintentando solo, hace falta el PIN nuevo del clinico.
                _authFailed = false;
                _connecting = false;
                _reconnecting = false;
                PinScreenRequested?.Invoke("PIN incorrecto. Volvé a intentarlo.");
                return;
            }
            if (_connecting)
            {
                _connecting = false;
                if (_manualDisconnect)
                {
                    _reconnecting = false;
                    _manualDisconnect = false;
                    ShowConnectScreenRequested?.Invoke("Tocá un visor para conectarte.", false);
                    return;
                }
                if (_reconnecting) { ScheduleNextReconnectAttempt("No se pudo conectar."); return; }
                ShowConnectScreenRequested?.Invoke("No se pudo conectar con " + _currentHost + ".", true);
            }
            else if (_sessionActive)
            {
                _sessionActive = false;
                if (_manualDisconnect) ShowConnectScreenRequested?.Invoke("Sesión finalizada.", false);
                else StartReconnectLoop(); // P2.5: caida no manual -> reconexion automatica
            }
            _manualDisconnect = false;
        }

        // ============================================================
        // Reconexion automatica (P2.5)
        // ============================================================
        // Backoff exponencial 2/4/8/15/15... (tope ReconnectMaxDelayS) para el intento
        // N (1-based): 2,4,8,16->15,15,...
        private static float DelayForAttempt(int attemptNumber) =>
            Mathf.Min(ReconnectBaseDelayS * Mathf.Pow(2f, attemptNumber - 1), ReconnectMaxDelayS);

        private void StartReconnectLoop()
        {
            if (!_pinByHost.TryGetValue(_currentHost, out var pin) || string.IsNullOrEmpty(pin))
            {
                // Sin PIN cacheado no hay con que reintentar solo (no deberia pasar si
                // _sessionActive era true, pero por las dudas se degrada al flujo manual).
                ShowConnectScreenRequested?.Invoke("Se perdió la conexión con el visor.", true);
                return;
            }
            _pendingAuthPin = pin;
            _reconnecting = true;
            _reconnectAttempt = 0;
            ReconnectStarted?.Invoke("Se perdió la conexión con el visor.");
            ScheduleNextReconnectAttempt("Se perdió la conexión con el visor.");
        }

        private void ScheduleNextReconnectAttempt(string reasonMessage)
        {
            float delay = DelayForAttempt(_reconnectAttempt + 1);
            _reconnectCountdown = delay;
            ReconnectStatusChanged?.Invoke($"{reasonMessage} Reintentando en {Mathf.CeilToInt(delay)}s…");
        }

        private void DoReconnectAttempt()
        {
            _reconnectAttempt++;
            // CRITICO (revision, preservado tal cual tras el split): neutralizar el
            // countdown ACA, no solo confiar en el guard "!_connecting" de Update().
            // Secuencia real sin esto: este metodo conecta -> OnWsConnected pone
            // _connecting=false y manda el auth -> en el MISMO Update, _reconnecting
            // && !_connecting vuelve a ser true y el countdown (que nunca se reseteo)
            // sigue en <=0 -> se dispara OTRO DoReconnectAttempt -> Connect() hace
            // Close() del socket recien conectado ANTES de que llegue el hello ->
            // livelock (cada intento se autoderriba y nunca hay tiempo de autenticar).
            // Con el countdown en +Infinity, el guard de Update() no vuelve a disparar
            // aunque _connecting se ponga en false de nuevo antes de resolver
            // auth/hello; el UNICO que lo re-arma con un valor finito es
            // ScheduleNextReconnectAttempt (fallo de conexion) o la rama auth_locked
            // de OnWsDisconnected (espera explicita del retry_in_s).
            _reconnectCountdown = float.PositiveInfinity;
            ReconnectStatusChanged?.Invoke($"Reconectando… (intento {_reconnectAttempt})");
            _connecting = true;
            _ws.Connect(_currentHost, WsPort);
        }

        /// <summary>
        /// Cancela la reconexion automatica en curso. Devuelve true si habia un
        /// intento de conexion en vuelo (la UI debe esperar a que se resuelva via
        /// ShowConnectScreenRequested); false si estaba en la espera del backoff (sin
        /// conexion abierta) y la UI debe volver al discovery YA.
        /// </summary>
        public bool CancelReconnect()
        {
            _reconnecting = false;
            if (_connecting)
            {
                // MAYOR (revision, preservado tras el split): solo seteamos
                // _manualDisconnect si hay un intento en vuelo -- ese es el unico caso
                // en que Close() dispara un Disconnected real que lo consume (ver
                // OnWsDisconnected). Si estamos en la espera del backoff (sin conexion
                // abierta) no hay evento que lo resetee, y quedaria en true "filtrando"
                // a la proxima sesion (la siguiente caida no manual se mostraria como
                // "Sesion finalizada" en vez de reconectar). Connect() tambien lo
                // resetea como red de seguridad adicional.
                _manualDisconnect = true;
                _ws.Close(); // aborta el intento en curso; cae en la rama _manualDisconnect de OnWsDisconnected
                return true;
            }
            return false;
        }

        // ============================================================
        // Protocolo
        // ============================================================
        private void OnText(string text)
        {
            JObject o;
            try { o = JObject.Parse(text); } catch { return; }
            string type = (string)o["type"] ?? "";
            if (type == "auth_ok")
            {
                // PIN valido: lo dejamos en memoria para esta sesion (reconexion al
                // mismo host sin volver a pedirlo). El hello llega en un mensaje
                // aparte inmediatamente despues.
                if (!string.IsNullOrEmpty(_currentHost)) _pinByHost[_currentHost] = _pendingAuthPin;
                AuthOk?.Invoke();
            }
            else if (type == "auth_fail")
            {
                // El visor ya cierra esta conexion; OnWsDisconnected dispara
                // PinScreenRequested para reintentar.
                _authFailed = true;
                if (!string.IsNullOrEmpty(_currentHost)) _pinByHost.Remove(_currentHost);
            }
            else if (type == "auth_locked")
            {
                // El visor agoto el tope de intentos fallidos de esta sesion y esta
                // en ventana de lockout: el PIN que mandamos puede ser el correcto,
                // asi que NO lo descartamos (no tocamos _pinByHost). OnWsDisconnected
                // usa el retry_in_s para el mensaje.
                _authLocked = true;
                _authLockRetrySeconds = (int?)o["retry_in_s"] ?? 0;
            }
            else if (type == "hello")
            {
                _lensesById.Clear();
                var lenses = o["lenses"] as JArray ?? new JArray();
                foreach (var l in lenses)
                    if (l is JObject lo && lo["id"] != null)
                        _lensesById[(string)lo["id"]] = lo;
                _visionState = o["vision_state"] as JObject ?? new JObject();
                // P2.3: "scenarios" es [{id,label},...] (la lista de ids viene de
                // ScenarioManager.ScenarioOrder del lado visor, ver docs/networking.md).
                _scenarios = new List<string>();
                _scenarioLabels.Clear();
                foreach (var s in (o["scenarios"] as JArray ?? new JArray()))
                {
                    if (!(s is JObject so)) continue;
                    string sid = (string)so["id"] ?? "";
                    if (string.IsNullOrEmpty(sid)) continue;
                    _scenarios.Add(sid);
                    _scenarioLabels[sid] = (string)so["label"] ?? sid;
                }
                CurrentScenario = (string)o["scenario"] ?? "";
                _sessionActive = true;
                _reconnecting = false; // P2.5: hello == reconexion exitosa (si venia de ahi)
                HelloReceived?.Invoke(lenses);
            }
            else if (type == "vision_state")
            {
                _visionState = o["vision_state"] as JObject ?? new JObject();
                VisionStateChanged?.Invoke();
            }
        }

        private void OnBinary(byte[] data)
        {
            if (data == null || data.Length < 2) return;
            char eye = (char)data[0];
            var jpg = new byte[data.Length - 1];
            Buffer.BlockCopy(data, 1, jpg, 0, jpg.Length);
            FrameReceived?.Invoke(eye, jpg);
        }
    }
}
