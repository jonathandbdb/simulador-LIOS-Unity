using System;
using System.Collections.Generic;
using System.IO;
using Newtonsoft.Json.Linq;
using Simulador.Net;
using UnityEngine;

namespace Simulador.Tablet
{
    /// <summary>
    /// Capa de sesion/protocolo de la tablet, separada de la UI (P6.2). Plain C# (NO
    /// MonoBehaviour): posee el <see cref="WebSocketClient"/> y el
    /// <see cref="DiscoveryListener"/>, el flujo de conexion/emparejamiento (PIN de
    /// 6 digitos o token persistente, ver docs/networking.md "emparejamiento opcion
    /// B"), la maquina de reconexion automatica (P2.5) y el estado de sesion
    /// (vision_state, catalogo, escenarios, mapa host -> token persistido en
    /// pairing.json). Expone eventos tipados hacia la UI y no toca ningun componente
    /// de Unity UI directamente -- <see cref="Net.TabletController"/>
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

        // --- Emparejamiento persistente por token (opcion B, ver docs/networking.md) ---
        // Reemplaza el cache de PIN en memoria: el token que devuelve el visor tras
        // un PIN correcto se persiste en persistentDataPath/pairing.json (mapa
        // host -> token) y se reusa en reconexiones futuras (manuales o
        // automaticas) sin volver a pedir el PIN, incluso si la tablet o el visor
        // se reiniciaron. Gotcha: la clave es la IP del host, que puede cambiar con
        // DHCP -- degradacion aceptable, en ese caso vuelve a pedir el PIN una vez.
        private const string PairingFileName = "pairing.json";
        private readonly Dictionary<string, string> _tokenByHost = new();
        private string _pendingAuthPin;
        private string _pendingAuthToken;
        private bool _authFailed;
        private string _authFailReason; // "pin" | "token", valido solo junto con _authFailed
        private bool _authLocked;
        private int _authLockRetrySeconds;
        private string _manualDisconnectMessage = "Sesión finalizada.";

        private static string PairingPath => Path.Combine(Application.persistentDataPath, PairingFileName);

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
            LoadPairing();

            _ws = new WebSocketClient();
            _ws.Connected += OnWsConnected;
            _ws.Disconnected += OnWsDisconnected;
            _ws.TextReceived += OnText;
            _ws.BinaryReceived += OnBinary;

            _disc = new DiscoveryListener();
            _disc.VisorDiscovered += host => _seenHosts[host] = Time.time;
            _disc.Start();
        }

        // ---------------- Persistencia del emparejamiento (pairing.json) ----------------
        private void LoadPairing()
        {
            if (!File.Exists(PairingPath)) return;
            string text;
            try { text = File.ReadAllText(PairingPath); }
            catch (Exception) { return; } // archivo corrupto/ilegible: se ignora, arranca vacio
            if (PairingStore.TryParsePairingMap(text, out var parsed))
            {
                _tokenByHost.Clear();
                foreach (var kv in parsed) _tokenByHost[kv.Key] = kv.Value;
                Debug.Log($"Tablet: {_tokenByHost.Count} emparejamiento(s) cargados.");
            }
        }

        private void SavePairing()
        {
            try { File.WriteAllText(PairingPath, PairingStore.SerializePairingMap(_tokenByHost)); }
            catch (Exception) { Debug.LogWarning($"Tablet: no se pudo escribir {PairingPath}"); }
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
        /// <summary>Token persistente de un enlace previo con este host (pairing.json), si existe.</summary>
        public bool TryGetCachedToken(string host, out string token)
        {
            if (_tokenByHost.TryGetValue(host, out token) && !string.IsNullOrEmpty(token)) return true;
            token = null;
            return false;
        }

        private void ConnectInternal(string host, string pin, string token)
        {
            _currentHost = host;
            _pendingAuthPin = pin;
            _pendingAuthToken = token;
            _authFailed = false;
            _authFailReason = null;
            // MAYOR (revision post-split, heredado): _manualDisconnect puede haber
            // quedado en true si el usuario cancelo una reconexion automatica mientras
            // esperaba el backoff (sin conexion en vuelo, sin evento de socket que lo
            // consumiera). Sin este reset, la PROXIMA caida no manual de esta sesion
            // nueva se mostraria como "Sesion finalizada" en vez de reconectar.
            _manualDisconnect = false;
            _connecting = true;
            _ws.Connect(host, WsPort);
        }

        /// <summary>Abre el WebSocket hacia host y manda el auth con PIN al conectar (primer enlace, o PinScreen tras un token invalido/revocado). La UI ya debe haber mostrado el ConnectScreen "Conectando a host..." antes de llamar esto.</summary>
        public void Connect(string host, string pin) => ConnectInternal(host, pin, null);

        /// <summary>Abre el WebSocket hacia host y manda el auth con el token cacheado (reconexion sin pedir PIN, ver TryGetCachedToken).</summary>
        public void ConnectWithToken(string host, string token) => ConnectInternal(host, null, token);

        /// <summary>Desconexion manual (boton "Desconectar" de la UI, o Unpair). "message" es lo que ve el ConnectScreen al volver.</summary>
        public void Disconnect(string message = "Sesión finalizada.")
        {
            _manualDisconnect = true;
            _manualDisconnectMessage = message;
            _reconnecting = false; // defensivo: no hay Reconectar visible en MainScreen, pero por si acaso
            _ws.Close();
        }

        /// <summary>
        /// Boton "Desvincular" de la UI: pide al visor que revoque el token de ESTE
        /// cliente y borra el token local de _currentHost, sin esperar confirmacion
        /// del visor -- el comando sale por el mismo socket ANTES del Close() (mismo
        /// hilo, mismo orden de escritura), y aunque se perdiera, el peor caso es un
        /// token huerfano en paired_tokens.json que se limpia borrando ese archivo
        /// del lado visor (ver docs/networking.md).
        /// </summary>
        public void Unpair()
        {
            SendCommand(new JObject { ["cmd"] = "unpair" });
            if (!string.IsNullOrEmpty(_currentHost) && _tokenByHost.Remove(_currentHost))
                SavePairing();
            Disconnect("Desvinculado. Ingresá el PIN si querés volver a conectarte.");
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
            var auth = new JObject { ["type"] = "auth" };
            if (!string.IsNullOrEmpty(_pendingAuthToken)) auth["token"] = _pendingAuthToken;
            else auth["pin"] = _pendingAuthPin;
            SendCommand(auth);
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
                // PIN invalido (p.ej. el visor reinicio y genero un PIN nuevo), o token
                // invalido/revocado (_authFailReason == "token": visor reseteado o
                // Desvincular hecho desde otro lado, ver OnText) -- en ambos casos no
                // tiene sentido seguir reintentando solo, hace falta el PIN del clinico.
                _authFailed = false;
                _connecting = false;
                _reconnecting = false;
                string msg = _authFailReason == "token"
                    ? "El emparejamiento con este visor ya no es válido. Ingresá el PIN nuevamente."
                    : "PIN incorrecto. Volvé a intentarlo.";
                _authFailReason = null;
                PinScreenRequested?.Invoke(msg);
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
                if (_manualDisconnect) ShowConnectScreenRequested?.Invoke(_manualDisconnectMessage, false);
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
            if (!_tokenByHost.TryGetValue(_currentHost, out var token) || string.IsNullOrEmpty(token))
            {
                // Sin token cacheado no hay con que reintentar solo (no deberia pasar si
                // _sessionActive era true -- llegar a una sesion activa ya implica que
                // hubo un auth_ok con token, ver OnText -- pero por las dudas se degrada
                // al flujo manual).
                ShowConnectScreenRequested?.Invoke("Se perdió la conexión con el visor.", true);
                return;
            }
            _pendingAuthToken = token;
            _pendingAuthPin = null;
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
                // Token nuevo (viene SOLO de un auth por PIN -- primer enlace o
                // PinScreen tras un token invalido/revocado, ver HandleAuthAttempt
                // del lado visor): se persiste para no volver a pedir el PIN. Si el
                // auth fue por token (reconexion), el visor NO manda un token nuevo
                // (decision de protocolo, ver docs/networking.md) y el cache
                // existente sigue siendo valido -- no hay nada que actualizar aca.
                // El hello llega en un mensaje aparte inmediatamente despues.
                string newToken = (string)o["token"];
                if (!string.IsNullOrEmpty(newToken) && !string.IsNullOrEmpty(_currentHost))
                {
                    _tokenByHost[_currentHost] = newToken;
                    SavePairing();
                }
                AuthOk?.Invoke();
            }
            else if (type == "auth_fail")
            {
                // El visor ya cierra esta conexion; OnWsDisconnected dispara
                // PinScreenRequested para reintentar. reason=="token": el token
                // cacheado quedo invalido/revocado (visor reseteado o Desvincular
                // hecho desde otro lado) -- se borra, cae al flujo de PIN normal, y
                // NO consumio el lockout de PIN del visor (ver HandleTokenAuth).
                // reason default "pin": PIN puntual incorrecto, no hay token que borrar.
                _authFailed = true;
                _authFailReason = (string)o["reason"] ?? "pin";
                if (_authFailReason == "token" && !string.IsNullOrEmpty(_currentHost))
                {
                    _tokenByHost.Remove(_currentHost);
                    SavePairing();
                }
            }
            else if (type == "auth_locked")
            {
                // El visor agoto el tope de intentos fallidos de PIN de esta sesion y
                // esta en ventana de lockout (solo alcanza al flujo de PIN -- un auth
                // por token nunca dispara auth_locked, ver HandleTokenAuth): el PIN
                // que mandamos puede ser el correcto, asi que NO lo descartamos.
                // OnWsDisconnected usa el retry_in_s para el mensaje.
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
