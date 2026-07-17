using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using Newtonsoft.Json.Linq;
using Simulador.Data;
using Simulador.Tablet;
using Simulador.Vision;
using UnityEngine;

namespace Simulador.Net
{
    /// <summary>
    /// Orquesta el networking del visor (F6). Levanta el WebSocketServer (:9090), el
    /// DiscoveryBeacon (:9091) y la captura de streaming. Exige emparejamiento antes
    /// de operar el canal: cada cliente debe mandar {"type":"auth","pin":"NNNNNN"}
    /// (PIN de 6 digitos, primer enlace) o {"type":"auth","token":"..."} (token
    /// persistente de un enlace previo, opcion B de emparejamiento -- ver
    /// docs/networking.md) como primer mensaje; recien autenticado recibe el "hello"
    /// (catalogo + estado), puede mandar comandos (apply_lens, override_params,
    /// set_astigmatism, load_scenario, refresh, unpair, set_hud) y recibe el
    /// vision_state/stream. Un PIN correcto emite un token nuevo (persistido en
    /// persistentDataPath/paired_tokens.json) que la tablet reusa en reconexiones
    /// futuras sin volver a pedir el PIN; el token NO consume el lockout de PIN si
    /// es invalido (espacio de ~256 bits, un stale token no es fuerza bruta).
    /// Port de la parte de red de main.gd + streaming_server.gd.
    /// </summary>
    public class NetworkController : MonoBehaviour
    {
        public static NetworkController Instance { get; private set; }

        /// <summary>
        /// PIN de 6 digitos generado al iniciar la sesion del visor. La tablet debe
        /// mandarlo en el primer mensaje del canal para poder operarlo. Lo muestra el
        /// HUD del visor (fuera de alcance de este cambio: es responsabilidad de
        /// Vision/, que solo LEE esta propiedad).
        /// </summary>
        public string PairingPin { get; private set; } = "";

        /// <summary>
        /// Passthrough read-only del gate real del servidor: cuantos clientes hay
        /// abiertos Y autenticados. Lo consume el HUD (Vision/) para mostrar si hay
        /// una tablet emparejada. Null-check porque el server puede no haber
        /// arrancado todavia (antes de Start, o tras OnDestroy).
        /// </summary>
        public int AuthenticatedClientCount => _server?.AuthenticatedClientCount ?? 0;

        // Tope de intentos fallidos de PIN acumulados durante la sesion del visor
        // (no por conexion: cada PIN incorrecto cierra esa conexion, asi que esto
        // limita cuantas VECES se puede reintentar reconectando). Agotado el tope,
        // se bloquea todo intento nuevo por LockWindowMs sin ni evaluar el PIN
        // (auth_locked); al expirar la ventana el contador se resetea solo. Tambien
        // se resetea en un auth exitoso. Ventana con reset > lockout permanente:
        // 3 intentos/min sobre un espacio de 10^6 sigue siendo seguro en LAN, y no
        // exige reiniciar la app del visor en plena consulta.
        private const int MaxAuthFailures = 3;
        private const int LockWindowMs = 60000;
        private int _authFailCount;
        private int _lockUntilTicks;

        // Emparejamiento persistente por token (opcion B, ver docs/networking.md):
        // lista de tokens validos (multiples tablets posibles) + que token quedo
        // asociado a cada cliente conectado (para poder revocar el propio en
        // "unpair" sin que el cliente tenga que reenviarlo). _pairedTokens se
        // persiste en cada alta/baja; _tokenByClientId es puramente en memoria (no
        // tiene sentido persistirlo, se reconstruye en cada auth).
        private const string PairedTokensFileName = "paired_tokens.json";
        private readonly List<string> _pairedTokens = new();
        private readonly Dictionary<int, string> _tokenByClientId = new();
        private string PairedTokensPath => Path.Combine(Application.persistentDataPath, PairedTokensFileName);

        private WebSocketServer _server;
        private DiscoveryBeacon _beacon;
        private StreamingCapture _capture;
        private ScenarioManager _scenarios;
        private GlareController _glare;
        private DataManager _dm;

        // Referencia al HUD del visor (Vision/, frontera: no se edita
        // HudController.cs desde Net/) para el comando "set_hud" (ver
        // docs/networking.md). Cacheada la PRIMERA vez que se resuelve (no en
        // DiscoverSceneRefs: alcanza con resolverla on-demand) con
        // FindObjectsInactive.Include -- una vez oculta (SetActive(false)) un
        // FindFirstObjectByType comun (activos solamente) ya no la encontraria para
        // poder volver a mostrarla.
        private HudController _hud;

        // Descubrimiento acotado de refs que pueden aparecer tras cargar escena
        // (P3.4): antes se hacia FindFirstObjectByType SIN limite en cada Update()
        // hasta encontrarlas, costo por frame indefinido si la escena nunca las
        // tiene (p.ej. smoke test sin ScenarioManager). Reintenta a 1 Hz durante
        // como mucho RefDiscoveryRetries intentos y despues se da por vencido.
        private const float RefDiscoveryIntervalS = 1f;
        private const int RefDiscoveryRetries = 10;
        private float _refDiscoveryTimer;
        private int _refDiscoveryAttemptsLeft = RefDiscoveryRetries;

        // Labels de escenario para el "hello" (P2.3, cierre). La LISTA/ORDEN de ids ya
        // NO se duplica en Net: se lee de ScenarioManager.ScenarioOrder (accessor
        // publico agregado por @vision-optics en Vision/), asi que no puede divergir
        // del root real de escenarios. Solo el TEXTO del label (que no vive en
        // Vision/) sigue mapeado a mano aca; un id sin entrada cae al fallback de
        // BuildScenarioList (capitaliza el id).
        private static readonly Dictionary<string, string> ScenarioLabels = new()
        {
            ["consultorio"] = "Consultorio",
            ["ruta_noche"] = "Ruta nocturna",
        };

        // NOTA fail-closed (ver docs/licenciamiento.md): este controller YA NO se
        // auto-crea al cargar la escena. Antes habia un [RuntimeInitializeOnLoadMethod]
        // que llamaba EnsureCreated() directo, y eso dejaba la red del visor (server WS
        // + beacon UDP) arriba y descubrible/conectable por una tablet durante los
        // segundos que tarda el gate de licencia en decidir (WaitUntil de config +
        // verify HTTP), incluso si el dispositivo terminaba denegado/bloqueado. Ahora la
        // creacion queda CONDICIONADA a la licencia: es
        // <c>Simulador.License.LicenseManager</c> quien llama <see cref="EnsureCreated"/>
        // -- al arrancar si la gracia offline lo permite, tras un verify 200 OK, o al
        // desbloquear. La app tablet (con <see cref="TabletController"/> en escena, sin
        // <c>LicenseManager</c>) nunca necesito este bootstrap: sigue sin levantar server.

        /// <summary>
        /// Crea el <see cref="NetworkController"/> si todavia no existe (idempotente:
        /// no-op si ya hay una <see cref="Instance"/>, o si la escena tiene un
        /// <see cref="TabletController"/> -- ahi la app es cliente y no se levanta
        /// server). La llama exclusivamente <c>Simulador.License.LicenseManager</c>
        /// (ver nota arriba y docs/licenciamiento.md): al arrancar por gracia offline,
        /// tras un verify 200 OK (idempotente, sirve tanto para crear como para
        /// RECREAR tras un bloqueo previo que la destruyo), o al desbloquear.
        /// </summary>
        public static void EnsureCreated()
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
            PairingPin = GeneratePin();
            Debug.Log($"Net: PIN de emparejamiento de esta sesion: {PairingPin}");
            LoadPairedTokens();

            _server = new WebSocketServer();
            _server.ClientConnected += OnClientConnected;
            _server.TextReceived += OnTextReceived;
            _server.ClientDisconnected += OnClientDisconnected;
            _server.Start(9090);

            _beacon = new DiscoveryBeacon();
            _beacon.Start(GenerateBeaconLabel());

            // Captura de streaming (sigue la camara XR)
            var cam = Camera.main;
            _capture = gameObject.AddComponent<StreamingCapture>();
            _capture.Server = _server;
            if (cam != null) _capture.headToFollow = cam.transform;

            _dm = DataManager.Instance;
            if (_dm != null)
            {
                _dm.VisionStateChanged += OnVisionStateChanged;
                // P7: cuando una re-sync trae catalogo nuevo (p.ej. tras un
                // create/update/delete_lens), re-broadcastear el hello para que
                // todas las tablets autenticadas reciban la lista actualizada.
                _dm.CatalogSyncedWithBackend += OnCatalogSynced;
            }
        }

        private void OnCatalogSynced(string version)
        {
            if (_server == null || _server.AuthenticatedClientCount == 0) return;
            Debug.Log($"Net: catalogo v{version} re-sincronizado; re-broadcast de hello.");
            _server.BroadcastText(BuildHello());
        }

        private void Update()
        {
            _server?.PumpEvents();
            double unix = (DateTime.UtcNow - new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc)).TotalSeconds;
            _beacon?.Tick(Time.deltaTime, unix);
            DiscoverSceneRefs();
            if (_capture != null && _capture.headToFollow == null && Camera.main != null)
                _capture.headToFollow = Camera.main.transform;
        }

        // Ver comentario del campo _refDiscoveryAttemptsLeft (P3.4): busca a 1 Hz,
        // maximo RefDiscoveryRetries veces, y para de intentar (con o sin exito).
        private void DiscoverSceneRefs()
        {
            if (_refDiscoveryAttemptsLeft <= 0) return;
            if (_scenarios != null && _glare != null) { _refDiscoveryAttemptsLeft = 0; return; }
            _refDiscoveryTimer += Time.deltaTime;
            if (_refDiscoveryTimer < RefDiscoveryIntervalS) return;
            _refDiscoveryTimer = 0f;
            _refDiscoveryAttemptsLeft--;
            if (_scenarios == null) _scenarios = FindFirstObjectByType<ScenarioManager>();
            if (_glare == null) _glare = FindFirstObjectByType<GlareController>();
            if (_refDiscoveryAttemptsLeft <= 0 && (_scenarios == null || _glare == null))
                Debug.LogWarning("Net: no se encontraron ScenarioManager/GlareController en la escena tras varios intentos; load_scenario/set_astigmatism podrian quedar sin efecto.");
        }

        private void OnDestroy()
        {
            if (_dm != null)
            {
                _dm.VisionStateChanged -= OnVisionStateChanged;
                _dm.CatalogSyncedWithBackend -= OnCatalogSynced;
            }
            _server?.Stop();
            _beacon?.Stop();
        }

        // Ver comentario del campo _hud: resuelta lazy, cacheada, con
        // FindObjectsInactive.Include para poder re-mostrar el HUD tras ocultarlo.
        private HudController ResolveHud()
        {
            if (_hud == null) _hud = FindFirstObjectByType<HudController>(FindObjectsInactive.Include);
            return _hud;
        }

        // ---------------- Mensajes salientes ----------------
        private void OnClientConnected(int id)
        {
            // Ya NO se manda el hello automatico: el cliente debe autenticarse
            // primero (ver HandleAuthAttempt). Recien ahi se le manda el hello.
            Debug.Log($"Net: cliente {id} conectado; esperando PIN o token de emparejamiento.");
        }

        private void OnClientDisconnected(int id)
        {
            // Red de seguridad del HUD (ver "set_hud" en docs/networking.md): una
            // tablet en modo Standard fuerza el HUD oculto en cada hello (ver
            // TabletController.OnSessionHello); si se desconecta sin que quede
            // ninguna otra tablet autenticada, el HUD (y el PIN que muestra) se
            // volveria invisible para el PROXIMO emparejamiento. _tokenByClientId
            // todavia tiene la entrada de este cliente en este punto (recien se
            // borra abajo), asi que sirve para saber si estaba autenticado.
            // PumpEvents ya llama esto desde el hilo principal (WebSocketServer.
            // PumpEvents via Update()), asi que tocar la API de Unity aca es seguro
            // sin encolar nada nuevo.
            bool wasAuthenticated = _tokenByClientId.ContainsKey(id);
            // _tokenByClientId es puramente informativo mientras la conexion esta
            // abierta (para poder resolver el token propio en "unpair" sin que el
            // cliente lo reenvie); no hace falta persistir su remocion, el token
            // sigue siendo valido en paired_tokens.json hasta un unpair explicito.
            _tokenByClientId.Remove(id);
            if (wasAuthenticated && (_server == null || _server.AuthenticatedClientCount == 0))
                ResolveHud()?.gameObject.SetActive(true);
            Debug.Log($"Net: cliente {id} desconectado");
        }

        private void OnVisionStateChanged(string eye, EyeState state)
        {
            if (_server == null || _server.AuthenticatedClientCount == 0) return;
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
                ["scenarios"] = BuildScenarioList(),
                // P7: modo de app del visor (del verify de licencia) -- la tablet
                // decide su UI (Standard/Pro) con esto. Una tablet vieja ignora
                // los campos; un visor viejo no los manda (la tablet asume "pro").
                ["mode"] = License.LicenseManager.AppMode,
                ["is_admin"] = License.LicenseManager.IsAdmin,
            };
            return hello.ToString(Newtonsoft.Json.Formatting.None);
        }

        private static JArray BuildScenarioList()
        {
            var arr = new JArray();
            foreach (var id in ScenarioManager.ScenarioOrder)
            {
                string label = ScenarioLabels.TryGetValue(id, out var l) ? l
                    : (id.Length > 0 ? char.ToUpper(id[0]) + id.Substring(1) : id);
                arr.Add(new JObject { ["id"] = id, ["label"] = label });
            }
            return arr;
        }

        private JObject BuildVisionState()
        {
            var dm = DataManager.Instance;
            var vs = new JObject();
            if (dm != null)
            {
                vs["left"] = EyeToJson(dm.Left);
                vs["right"] = EyeToJson(dm.Right);
                // blend_active (P2.1): fuente unica de verdad LensEngine.ComputeBlend
                // (via DataManager.BlendModeEnabled); la tablet lo usa para decidir el
                // split de panes del stream en vez de su propia heuristica leftId!=rightId
                // (que mostraba 2 panes, uno vacio, con un solo ojo con lente).
                vs["blend_active"] = dm.BlendModeEnabled;
            }
            return vs;
        }

        private static JObject EyeToJson(EyeState e)
        {
            var o = new JObject { ["lens_id"] = e?.LensId ?? "" };
            if (e != null) foreach (var kv in e.Params) o[kv.Key] = kv.Value;
            return o;
        }

        // ---------------- Discovery (beacon) ----------------
        /// <summary>
        /// Etiqueta no sensible para el beacon UDP (P1.5): nombre amigable del
        /// dispositivo + un nonce generado por esta corrida. Reemplaza a
        /// SystemInfo.deviceUniqueIdentifier, que se emitia en broadcast a toda la
        /// subred sin auth ni cifrado (fuga innecesaria de un identificador de
        /// hardware estable). El nonce es independiente del PairingPin — no se
        /// deriva de el ni permite reconstruirlo. Si el visor reinicia, cambian
        /// PIN y nonce por igual: coherente, no hace falta que persista.
        /// </summary>
        private static string GenerateBeaconLabel()
        {
            string name = SystemInfo.deviceName;
            if (string.IsNullOrEmpty(name)) name = "Visor";
            // Defensivo: DiscoveryBeacon.Tick construye el JSON a mano (sin
            // escaping), y a diferencia de deviceUniqueIdentifier, deviceName no
            // esta garantizado alfanumerico en todas las plataformas.
            name = name.Replace("\"", "").Replace("\\", "");
            string nonce = Guid.NewGuid().ToString("N").Substring(0, 8);
            return name + "-" + nonce;
        }

        // ---------------- Emparejamiento por PIN / token ----------------
        private static string GeneratePin()
        {
            var rng = new System.Random();
            return rng.Next(0, 1_000_000).ToString("D6", CultureInfo.InvariantCulture);
        }

        private void LoadPairedTokens()
        {
            if (!File.Exists(PairedTokensPath)) return;
            string text;
            try { text = File.ReadAllText(PairedTokensPath); }
            catch (Exception) { return; } // archivo corrupto/ilegible: se ignora, arranca vacio
            if (PairingStore.TryParseTokens(text, out var parsed))
            {
                _pairedTokens.Clear();
                _pairedTokens.AddRange(parsed);
                Debug.Log($"Net: {_pairedTokens.Count} token(s) de emparejamiento cargados.");
            }
        }

        private void SavePairedTokens()
        {
            try { File.WriteAllText(PairedTokensPath, PairingStore.SerializeTokens(_pairedTokens)); }
            catch (Exception) { Debug.LogWarning($"Net: no se pudo escribir {PairedTokensPath}"); }
        }

        private void AddPairedToken(string token)
        {
            if (!_pairedTokens.Contains(token)) _pairedTokens.Add(token);
            SavePairedTokens();
        }

        private void RemovePairedToken(string token)
        {
            if (_pairedTokens.Remove(token)) SavePairedTokens();
        }

        /// <summary>
        /// Primer mensaje esperado de cada cliente sin autenticar: bien
        /// {"type":"auth","pin":"NNNNNN"} (primer enlace), bien
        /// {"type":"auth","token":"..."} (reconexion con un token de un enlace
        /// previo, ver HandleTokenAuth -- se evalua ANTES del PIN y nunca toca el
        /// lockout). PIN correcto -> autentica, EMITE un token nuevo (persistido) y
        /// manda el hello; incorrecto -> auth_fail y cierra esa conexion (la tablet
        /// debe reconectar para reintentar). Lockout de PIN activo -> auth_locked
        /// (con retry_in_s) sin evaluar el PIN. Cualquier otro mensaje antes de
        /// autenticar se ignora y cierra sin responder.
        /// </summary>
        private void HandleAuthAttempt(int id, string text)
        {
            JObject msg = null;
            try { msg = JObject.Parse(text); } catch (Exception) { }
            string msgType = msg != null ? (string)msg["type"] ?? "" : "";
            if (msg == null || msgType != "auth")
            {
                Debug.LogWarning($"Net: cliente {id} mando un comando sin autenticar; cerrando.");
                _server.ForceDisconnect(id);
                return;
            }

            string token = (string)msg["token"];
            if (!string.IsNullOrEmpty(token))
            {
                HandleTokenAuth(id, token);
                return;
            }

            if (_authFailCount >= MaxAuthFailures)
            {
                int remainingMs = unchecked(_lockUntilTicks - Environment.TickCount);
                if (remainingMs > 0)
                {
                    int retrySec = (remainingMs + 999) / 1000; // redondeo hacia arriba
                    Debug.LogWarning($"Net: cliente {id} intento autenticarse durante el lockout ({retrySec}s restantes).");
                    _server.SendTextTo(id, new JObject { ["type"] = "auth_locked", ["retry_in_s"] = retrySec }.ToString(Newtonsoft.Json.Formatting.None));
                    _server.ForceDisconnect(id);
                    return;
                }
                // La ventana de lockout expiro: resetear y seguir evaluando el PIN normal.
                _authFailCount = 0;
            }

            string pin = (string)msg["pin"] ?? "";
            if (!string.IsNullOrEmpty(PairingPin) && pin == PairingPin)
            {
                _authFailCount = 0; // reset en auth exitoso
                string newToken = PairingStore.GenerateToken();
                AddPairedToken(newToken);
                _tokenByClientId[id] = newToken;
                _server.MarkAuthenticated(id);
                Debug.Log($"Net: cliente {id} autenticado por PIN, enviando hello.");
                _server.SendTextTo(id, new JObject { ["type"] = "auth_ok", ["token"] = newToken }.ToString(Newtonsoft.Json.Formatting.None));
                _server.SendTextTo(id, BuildHello());
            }
            else
            {
                _authFailCount++;
                if (_authFailCount >= MaxAuthFailures)
                    _lockUntilTicks = unchecked(Environment.TickCount + LockWindowMs);
                Debug.LogWarning($"Net: cliente {id} mando un PIN incorrecto ({_authFailCount}/{MaxAuthFailures}); cerrando.");
                _server.SendTextTo(id, new JObject { ["type"] = "auth_fail" }.ToString(Newtonsoft.Json.Formatting.None));
                _server.ForceDisconnect(id);
            }
        }

        /// <summary>
        /// Auth por token persistente (emparejamiento opcion B, ver
        /// docs/networking.md): a diferencia del PIN, NUNCA toca
        /// _authFailCount/_lockUntilTicks -- un token invalido/revocado (visor
        /// reseteado, Desvincular previo) no es indicio de fuerza bruta, el espacio
        /// de ~256 bits (PairingStore.GenerateToken) hace ese ataque irrelevante. Si
        /// el token es valido NO se emite uno nuevo: el mismo sigue siendo la
        /// credencial de esa tablet hasta que se revoque explicitamente.
        /// </summary>
        private void HandleTokenAuth(int id, string token)
        {
            if (_pairedTokens.Contains(token))
            {
                _tokenByClientId[id] = token;
                _server.MarkAuthenticated(id);
                Debug.Log($"Net: cliente {id} autenticado por token, enviando hello.");
                _server.SendTextTo(id, new JObject { ["type"] = "auth_ok" }.ToString(Newtonsoft.Json.Formatting.None));
                _server.SendTextTo(id, BuildHello());
            }
            else
            {
                Debug.LogWarning($"Net: cliente {id} mando un token de emparejamiento invalido o revocado; cerrando (no cuenta para el lockout de PIN).");
                _server.SendTextTo(id, new JObject { ["type"] = "auth_fail", ["reason"] = "token" }.ToString(Newtonsoft.Json.Formatting.None));
                _server.ForceDisconnect(id);
            }
        }

        // ---------------- Comandos entrantes ----------------
        private void OnTextReceived(int id, string text)
        {
            if (_server == null) return;
            if (!_server.IsAuthenticated(id))
            {
                // El ReadLoop puede haber encolado varios mensajes de este cliente
                // ANTES de que un ForceDisconnect previo (mismo Update, misma pasada
                // de PumpEvents) cerrara la conexion — ej. un burst de "auth" con PIN
                // incorrecto. Si ya esta cerrado, ignorar: cuenta como UN solo fallo
                // por conexion, no uno por mensaje.
                if (!_server.IsClientOpen(id)) return;
                HandleAuthAttempt(id, text);
                return;
            }

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
                    // P2.2: la tablet ya manda "eye" (selector "Ojo a tratar"); antes se
                    // ignoraba y GlareController.SetAstigmatism aplicaba siempre a "both".
                    if (_glare != null)
                        _glare.SetAstigmatism((string)cmd["eye"] ?? "both", (bool?)cmd["enabled"] ?? false,
                            (float?)cmd["magnitude"] ?? 0f, (float?)cmd["angle"] ?? 0f);
                    break;
                case "load_scenario":
                    // P2.3: selecciona por id, no por texto de label. "id" es el campo
                    // nuevo del protocolo; "scenario" se sigue aceptando por compat con
                    // el nombre de campo anterior (mismo significado: siempre fue un id).
                    if (_scenarios != null)
                    {
                        string sid = (string)cmd["id"] ?? (string)cmd["scenario"] ?? "";
                        _scenarios.SwitchTo(sid);
                    }
                    break;
                case "refresh":
                    // P5.4: refresh en caliente. Reusa BuildHello() (mismo payload EXACTO
                    // que el "hello" inicial: catalogo + vision_state + escenarios) para
                    // que la tablet pueda reconstruir todo sin reconectar/re-autenticar
                    // -- OnText del lado tablet ya sabe procesar un "hello" en cualquier
                    // momento (lo reusa tambien tras una reconexion, ver docs/tablet.md
                    // P2.5), asi que no hizo falta un mensaje de respuesta nuevo. Se
                    // responde SOLO al cliente que lo pidio (SendTextTo, no broadcast).
                    _server.SendTextTo(id, BuildHello());
                    break;
                case "unpair":
                    // Boton "Desvincular" de la tablet: revoca el token de ESTE
                    // cliente (asociado al autenticar, ver HandleAuthAttempt/
                    // HandleTokenAuth) de la lista persistida. La tablet ya borra su
                    // token local y cierra la conexion por su cuenta apenas manda
                    // este comando (ver TabletSession.Unpair) -- no hace falta
                    // responder nada aca, ver docs/networking.md.
                    if (_tokenByClientId.TryGetValue(id, out var revoked))
                    {
                        RemovePairedToken(revoked);
                        _tokenByClientId.Remove(id);
                        Debug.Log($"Net: cliente {id} se desvinculo (token revocado).");
                        // Misma red de seguridad del HUD que OnClientDisconnected: el
                        // "unpair" borra la entrada de _tokenByClientId ANTES de que
                        // llegue el disconnect real del socket (la tablet cierra la
                        // conexion por su cuenta apenas manda este comando, ver
                        // TabletSession.Unpair), asi que ese chequeo ya no lo
                        // detectaria -- se resuelve aca. AuthenticatedClientCount
                        // todavia cuenta a ESTE cliente (sigue Open/Authenticated
                        // hasta que se desconecte), de ahi el <= 1.
                        if (_server == null || _server.AuthenticatedClientCount <= 1)
                            ResolveHud()?.gameObject.SetActive(true);
                    }
                    break;
                case "set_hud":
                    // Toggle del HUD de diagnostico (Vision/) desde la tablet, ver
                    // docs/networking.md. Fire-and-forget como set_astigmatism/
                    // load_scenario: sin ack ni campo en vision_state que confirme
                    // el estado real del HUD.
                    bool setHudVisible = (bool?)cmd["visible"] ?? true;
                    var targetHud = ResolveHud();
                    if (targetHud != null) targetHud.gameObject.SetActive(setHudVisible);
                    else Debug.LogWarning("Net: set_hud recibido pero no se encontro HudController en la escena.");
                    break;
                case "create_lens":
                case "update_lens":
                case "delete_lens":
                    // P7: CRUD de lentes custom. La tablet manda la definicion; el
                    // visor hace el HTTP al backend con SU device_id (unica identidad
                    // autenticable) y le contesta lens_saved/lens_error al cliente.
                    StartCoroutine(RunLensCommand(id, type, cmd));
                    break;
                default:
                    Debug.LogWarning("Net: comando desconocido: " + type);
                    break;
            }
        }

        // ---------------- Lentes custom (P7) ----------------

        /// <summary>
        /// Ejecuta un comando create/update/delete_lens contra el backend y responde
        /// al cliente. Al exito ademas re-sincroniza el catalogo (RefreshFromBackend);
        /// el hello con el catalogo nuevo se re-broadcastea cuando la sync termina
        /// (suscripcion a CatalogSyncedWithBackend en Start).
        /// </summary>
        private IEnumerator RunLensCommand(int clientId, string type, JObject cmd)
        {
            var dm = DataManager.Instance;
            if (dm == null)
            {
                _server?.SendTextTo(clientId, BuildLensError("no_data_manager"));
                yield break;
            }

            string deviceId = SystemInfo.deviceUniqueIdentifier;
            long code = 0;
            string body = null;
            void OnDone(long c, string b) { code = c; body = b; }

            if (type == "create_lens")
            {
                var payload = new JObject
                {
                    ["device_id"] = deviceId,
                    ["scope"] = (string)cmd["scope"] ?? "private",
                    ["nombre"] = (string)cmd["nombre"] ?? "",
                    ["descripcion"] = (string)cmd["descripcion"] ?? "",
                    ["params"] = cmd["params"] as JObject ?? new JObject(),
                };
                yield return CustomLensClient.Create(dm.BackendUrl, payload.ToString(Newtonsoft.Json.Formatting.None), OnDone);
            }
            else if (type == "update_lens")
            {
                var payload = new JObject
                {
                    ["device_id"] = deviceId,
                    ["nombre"] = (string)cmd["nombre"] ?? "",
                    ["descripcion"] = (string)cmd["descripcion"] ?? "",
                    ["params"] = cmd["params"] as JObject ?? new JObject(),
                };
                yield return CustomLensClient.Update(dm.BackendUrl, (string)cmd["lens_id"] ?? "",
                    payload.ToString(Newtonsoft.Json.Formatting.None), OnDone);
            }
            else // delete_lens
            {
                yield return CustomLensClient.Delete(dm.BackendUrl, (string)cmd["lens_id"] ?? "", deviceId, OnDone);
            }

            if (code == 200 || code == 201)
            {
                string lensId = null;
                try
                {
                    var resp = JObject.Parse(body ?? "{}");
                    lensId = (string)resp.SelectToken("lens.id") ?? (string)cmd["lens_id"];
                }
                catch (Exception) { lensId = (string)cmd["lens_id"]; }
                Debug.Log($"Net: {type} OK (lente {lensId ?? "?"}).");
                _server?.SendTextTo(clientId, new JObject
                {
                    ["type"] = "lens_saved",
                    ["op"] = type,
                    ["lens_id"] = lensId ?? "",
                }.ToString(Newtonsoft.Json.Formatting.None));
                // Catalogo nuevo (con la lente creada/editada/borrada): re-sync; el
                // hello se re-broadcastea al terminar (CatalogSyncedWithBackend).
                dm.RefreshFromBackend();
            }
            else
            {
                // Rechazo (403/404/409/422 con reason) o backend inalcanzable (code 0).
                string reason = "offline";
                if (code != 0)
                {
                    reason = $"http_{code}";
                    try
                    {
                        var resp = JObject.Parse(body ?? "{}");
                        reason = (string)resp["reason"] ?? (string)resp["detail"] ?? reason;
                    }
                    catch (Exception) { /* body no-JSON: queda http_<code> */ }
                }
                Debug.LogWarning($"Net: {type} fallo ({reason}).");
                _server?.SendTextTo(clientId, BuildLensError(reason, type));
            }
        }

        private static string BuildLensError(string reason, string op = "")
        {
            return new JObject
            {
                ["type"] = "lens_error",
                ["op"] = op,
                ["reason"] = reason ?? "unknown",
            }.ToString(Newtonsoft.Json.Formatting.None);
        }
    }
}
