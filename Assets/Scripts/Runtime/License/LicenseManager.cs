using System;
using System.Collections;
using System.Globalization;
using System.IO;
using Newtonsoft.Json;
using UnityEngine;
using UnityEngine.Networking;
using Simulador.Data;
using Simulador.Net;
using Simulador.Tablet;
using Simulador.Update;

namespace Simulador.License
{
    /// <summary>
    /// Gate de licenciamiento por dispositivo (F3, ver docs/licenciamiento.md). Bootstrap
    /// calcado de <see cref="Simulador.Data.DataManager"/>/<see cref="Simulador.Update.UpdateManager"/>
    /// (RuntimeInitializeOnLoadMethod + singleton + DontDestroyOnLoad), pero SOLO corre en
    /// el visor (guard <see cref="TabletController"/> en escena, mismo criterio que
    /// <see cref="NetworkController.EnsureCreated"/> -- la tablet no tiene este gate).
    ///
    /// Flujo (corrutina desde <see cref="Start"/>): espera a que
    /// <see cref="DataManager.BackendConfigReady"/>, evalua el cache local con
    /// <see cref="LicenseLogic.EvaluateOffline"/> (gracia de <see cref="LicenseLogic.GraceDays"/>
    /// dias) y, si el resultado NO es <see cref="LicenseLogic.LicenseGateResult.AllowOfflineGrace"/>,
    /// bloquea YA (mensaje generico "Verificando licencia...") ANTES de intentar el verify
    /// real -- nunca deja pasar a un dispositivo que la evaluacion offline ya rechazo. En
    /// paralelo (o si la gracia permitio arrancar) siempre dispara un <c>POST /api/verify</c>
    /// contra el backend para confirmar/corregir esa decision offline.
    /// </summary>
    public class LicenseManager : MonoBehaviour
    {
        private const string CacheFileName = "license_cache.json";
        private const string VerifyEndpoint = "/api/verify";
        private const string LogEndpoint = "/api/log";
        private const int VerifyTimeoutSeconds = 10;

        /// <summary>Cooldown minimo entre verifies manuales (boton "reintentar" del cartel de bloqueo).</summary>
        public const float RetryCooldownSeconds = 15f;

        public static LicenseManager Instance { get; private set; }

        /// <summary>True mientras el dispositivo esta bloqueado (gate cerrado).</summary>
        public static bool IsBlocked { get; private set; }

        /// <summary>Modo de app del dispositivo ("standard" | "pro"), del ultimo verify OK
        /// o del cache en gracia offline (P7). La tablet lo recibe via hello. Default
        /// "pro" (UI completa) hasta tener informacion explicita del backend.</summary>
        public static string AppMode { get; private set; } = "pro";

        /// <summary>True si el dispositivo puede crear/editar lentes GENERICAS (P7).</summary>
        public static bool IsAdmin { get; private set; }

        /// <summary>Se dispara cada vez que el gate (re)bloquea, con el resultado y el mensaje a mostrar.</summary>
        public event Action<LicenseLogic.LicenseGateResult, string> OnBlocked;
        /// <summary>Se dispara al desbloquear (verify OK tras haber estado bloqueado).</summary>
        public event Action OnUnblocked;

        private LicenseBlockScreenVR _blockScreen;
        private LicenseLogic.LicenseGateResult _currentBlockResult = LicenseLogic.LicenseGateResult.BlockUnknown;
        private float _lastVerifyRealtime = -RetryCooldownSeconds;
        private bool _verifyInFlight;

        private string CachePath => Path.Combine(Application.persistentDataPath, CacheFileName);

        /// <summary>Segundos restantes del cooldown de <see cref="RetryVerify"/>, para que el cartel muestre la cuenta regresiva.</summary>
        public float RetryCooldownRemaining => Mathf.Max(0f, RetryCooldownSeconds - (Time.realtimeSinceStartup - _lastVerifyRealtime));

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void Bootstrap()
        {
            if (Instance != null) return;
            // Igual que NetworkController.EnsureCreated: en la app tablet (escena con
            // TabletController) este gate no corre -- es exclusivo del visor.
            if (FindFirstObjectByType<TabletController>() != null) return;
            var go = new GameObject("LicenseManager");
            go.AddComponent<LicenseManager>();
            DontDestroyOnLoad(go);
        }

        private void Awake()
        {
            if (Instance != null && Instance != this) { Destroy(gameObject); return; }
            Instance = this;
        }

        private void Start()
        {
            StartCoroutine(InitializeAsync());
        }

        private IEnumerator InitializeAsync()
        {
            // El BackendUrl efectivo depende de que DataManager ya resolvio sus capas de
            // config (override > streaming > default) -- mismo guard que UpdateManager.
            yield return new WaitUntil(() => DataManager.Instance != null && DataManager.Instance.BackendConfigReady);

            string cacheJson = ReadCache();
            var offlineResult = LicenseLogic.EvaluateOffline(cacheJson, DateTime.UtcNow);
            if (offlineResult == LicenseLogic.LicenseGateResult.AllowOfflineGrace)
            {
                Debug.Log("License: cache local dentro de la gracia offline, arrancando normal; verificando en background.");
                // P7: en gracia offline el modo/admin valen los del ultimo verify OK
                // cacheado (defaults seguros si el cache es pre-P7).
                (AppMode, IsAdmin) = LicenseLogic.ReadModeFromCache(cacheJson);
                // Fail-closed (ver docs/licenciamiento.md): la red del visor (WS/beacon)
                // ya NO se auto-crea al cargar la escena -- solo el gate de licencia
                // decide cuando levantarla. La lectura del cache es sincrona, asi que
                // en el caso comun (dispositivo activo, cache fresco) esto levanta la
                // red enseguida, sin esperar al verify HTTP.
                NetworkController.EnsureCreated();
            }
            else
            {
                // Bloquear YA con un mensaje generico -- el verify real (abajo) recien
                // reemplaza este mensaje por el definitivo (Sin conexion / rechazado /
                // etc.) cuando termine, sea cual sea el resultado.
                Block(offlineResult, "Verificando licencia...");
            }

            yield return Verify(offlineResultForTelemetry: offlineResult, cacheJsonForTelemetry: cacheJson);
        }

        /// <summary>
        /// Reintenta el verify manualmente (boton "A: reintentar" del cartel de bloqueo).
        /// Ignora la llamada si todavia no paso <see cref="RetryCooldownSeconds"/> desde el
        /// ultimo intento, o si ya hay uno en vuelo.
        /// </summary>
        public void RetryVerify()
        {
            if (_verifyInFlight) return;
            if (RetryCooldownRemaining > 0f)
            {
                Debug.Log($"License: RetryVerify ignorado (cooldown, {RetryCooldownRemaining:F0}s restantes).");
                return;
            }
            StartCoroutine(Verify(offlineResultForTelemetry: null, cacheJsonForTelemetry: null));
        }

        private IEnumerator Verify(LicenseLogic.LicenseGateResult? offlineResultForTelemetry, string cacheJsonForTelemetry)
        {
            _verifyInFlight = true;
            _lastVerifyRealtime = Time.realtimeSinceStartup;

            string url = DataManagerLogic.BuildSyncUrl(DataManager.Instance.BackendUrl, VerifyEndpoint);
            string body = LicenseLogic.SerializeVerifyRequest(SystemInfo.deviceUniqueIdentifier, Application.version);
            byte[] bodyRaw = System.Text.Encoding.UTF8.GetBytes(body);

            using var req = new UnityWebRequest(url, UnityWebRequest.kHttpVerbPOST);
            req.uploadHandler = new UploadHandlerRaw(bodyRaw);
            req.downloadHandler = new DownloadHandlerBuffer();
            req.SetRequestHeader("Content-Type", "application/json");
            req.timeout = VerifyTimeoutSeconds;

            UnityWebRequestAsyncOperation op = null;
            try { op = req.SendWebRequest(); }
            catch (Exception e)
            {
                Debug.Log($"License: verify no se pudo enviar ({e.GetType().Name}).");
                _verifyInFlight = false;
                HandleUnreachable(offlineResultForTelemetry, cacheJsonForTelemetry);
                yield break;
            }
            yield return op;
            _verifyInFlight = false;

            // OJO: UnityWebRequest.result NO es Success para ningun codigo HTTP >= 400 --
            // reporta ProtocolError igual para un 403 "legitimo" (con body parseable) que
            // para un 500 sin body. La unica senal confiable de "no hubo respuesta en
            // absoluto" (timeout, conexion rechazada, DNS, backend caido) es
            // responseCode == 0; por eso el gate de "inalcanzable" es por code, no por
            // result. Si se gatea por result se pierde el 403 (nunca se llega a
            // parsear como denied) -- bug real encontrado validando esta tarea.
            long code = req.responseCode;
            if (code == 0)
            {
                Debug.Log($"License: verify inalcanzable ({req.result}).");
                HandleUnreachable(offlineResultForTelemetry, cacheJsonForTelemetry);
                yield break;
            }

            string text = req.downloadHandler.text;

            if (code == 200 && LicenseLogic.TryParseVerifyOk(text, out var ok))
            {
                HandleOk(ok);
                yield break;
            }
            if (code == 403 && LicenseLogic.TryParseVerifyDenied(text, out var denied))
            {
                HandleDenied(denied);
                yield break;
            }
            if (code == 429)
            {
                // Transitorio (rate limit del backend, 10/min/IP): NO tocar cache ni
                // estado, solo refrescar el mensaje si ya estaba bloqueado.
                Debug.Log("License: verify rate-limited (429), sin tocar cache/estado.");
                if (IsBlocked) Block(_currentBlockResult, "Demasiados intentos, esperá un momento.");
                yield break;
            }

            // 200/403 que no parsean (captive portal, forma inesperada) u otro codigo:
            // se trata igual que "inalcanzable" -- nunca se toca el cache, queda lo que
            // ya decidio EvaluateOffline.
            Debug.Log($"License: verify respondio {code} no reconocible, sin tocar cache/estado.");
            HandleUnreachable(offlineResultForTelemetry, cacheJsonForTelemetry);
        }

        private void HandleOk(LicenseLogic.VerifyOkDto ok)
        {
            WriteCache(LicenseLogic.BuildCacheJson(ok, DateTime.UtcNow));
            // P7: modo/admin del dispositivo, propagados a la tablet via hello.
            AppMode = string.IsNullOrEmpty(ok.AppMode) ? "pro" : ok.AppMode;
            IsAdmin = ok.IsAdmin;
            Debug.Log($"License: verify OK ({ok.DeviceName ?? "?"}, vence {ok.LicenseExpiry ?? "sin vencimiento"}, modo {AppMode}{(IsAdmin ? "+admin" : "")}).");
            bool wasBlocked = IsBlocked;
            if (wasBlocked)
            {
                Unblock();
                SendTelemetry("license_recovered", $"device_name={ok.DeviceName}");
            }
            // EnsureCreated() es idempotente (no-op si ya hay Instance): se llama
            // SIEMPRE en el camino OK, no solo si wasBlocked -- cubre cualquier caso
            // donde el arranque fail-closed no hubiera levantado la red todavia (ver
            // docs/licenciamiento.md) y sirve tanto para crear como para recrear tras
            // un bloqueo previo.
            NetworkController.EnsureCreated();
        }

        private void HandleDenied(LicenseLogic.VerifyDeniedDto denied)
        {
            DeleteCache();
            var result = LicenseLogic.MapDeniedReason(denied.Reason);
            string message = !string.IsNullOrWhiteSpace(denied.Message) ? denied.Message : MessageFor(result);
            // El servidor dijo NO. Block() corta la red en CUALQUIER bloqueo (ver su
            // propio comentario) -- no hace falta destruir NetworkController aca aparte.
            Block(result, message);
            SendTelemetry("license_denied", $"reason={denied.Reason ?? ""}");
        }

        // Cubre red inalcanzable, timeout, excepcion sincrona y respuestas 200/403 que no
        // parsean: en TODOS esos casos no se toca el cache ni se reevalua nada nuevo,
        // queda vigente lo que ya decidio EvaluateOffline en InitializeAsync. Solo manda
        // telemetria (best-effort, puede no llegar si de verdad no hay red) en el intento
        // INICIAL (offlineResultForTelemetry != null); un RetryVerify manual no repite el
        // mismo evento de arranque.
        private void HandleUnreachable(LicenseLogic.LicenseGateResult? offlineResult, string cacheJson)
        {
            if (!offlineResult.HasValue) return;
            if (offlineResult.Value == LicenseLogic.LicenseGateResult.AllowOfflineGrace)
            {
                SendTelemetry("license_offline_grace", $"days_left={ComputeDaysLeft(cacheJson)}");
                return;
            }
            // Reemplaza el mensaje generico "Verificando licencia..." puesto en
            // InitializeAsync por el definitivo (Sin conexion / rechazado / etc.).
            Block(offlineResult.Value, MessageFor(offlineResult.Value));
            SendTelemetry("license_blocked_offline", "");
        }

        private void Block(LicenseLogic.LicenseGateResult result, string message)
        {
            IsBlocked = true;
            _currentBlockResult = result;
            Debug.LogWarning($"License: bloqueado ({result}). {message}");
            if (_blockScreen == null) _blockScreen = gameObject.AddComponent<LicenseBlockScreenVR>();
            _blockScreen.Show(result, message);
            // Fail-closed (ver docs/licenciamiento.md): la premisa es "bloqueo de app
            // completa", asi que CUALQUIER bloqueo corta la red del visor -- no solo el
            // 403/denied explicito. Antes de este cambio un BlockOffline/BlockExpired
            // local (sin red, o cache vencido detectado offline) dejaba el server WS/
            // beacon arriba, todavia descubrible/conectable por una tablet. Su
            // OnDestroy limpia sockets/beacon (ver docs/networking.md); el desbloqueo
            // la recrea via NetworkController.EnsureCreated() (idempotente).
            if (NetworkController.Instance != null) Destroy(NetworkController.Instance.gameObject);
            OnBlocked?.Invoke(result, message);
        }

        private void Unblock()
        {
            if (!IsBlocked) return;
            IsBlocked = false;
            Debug.Log("License: desbloqueado.");
            if (_blockScreen != null) { Destroy(_blockScreen); _blockScreen = null; }
            OnUnblocked?.Invoke();
        }

        // ---------------- Cache local ----------------
        private string ReadCache()
        {
            try { return File.Exists(CachePath) ? File.ReadAllText(CachePath) : null; }
            catch (Exception) { return null; } // archivo corrupto/ilegible: se ignora, EvaluateOffline lo trata como "sin cache"
        }

        private void WriteCache(string json)
        {
            try { File.WriteAllText(CachePath, json); }
            catch (Exception) { Debug.LogWarning($"License: no se pudo escribir {CachePath}"); }
        }

        private void DeleteCache()
        {
            try { if (File.Exists(CachePath)) File.Delete(CachePath); }
            catch (Exception) { Debug.LogWarning($"License: no se pudo borrar {CachePath}"); }
        }

        // Recalcula dias de gracia restantes a partir del cache, solo para el detail de
        // telemetria license_offline_grace -- no es logica de gate (esa es
        // LicenseLogic.EvaluateOffline, ya evaluada); un fallo de parseo aca nunca debe
        // tirar, en el peor caso el detail queda en 0.
        // SIM: atajo deliberado -- re-parsea el cache en vez de que EvaluateOffline
        // devuelva tambien los dias restantes; separa "logica pura de gate" (LicenseLogic,
        // testeada) de "detalle informativo de telemetria" (aca), aceptable porque un
        // error de calculo solo afecta un numero en un log, nunca la decision de bloqueo.
        private static int ComputeDaysLeft(string cacheJson)
        {
            try
            {
                var cache = JsonConvert.DeserializeObject<LicenseLogic.LicenseCacheDto>(cacheJson);
                if (cache == null) return 0;
                if (!DateTime.TryParse(cache.VerifiedAt, CultureInfo.InvariantCulture,
                        DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var verifiedAt))
                    return 0;
                var utcNow = DateTime.UtcNow;
                if (verifiedAt > utcNow) verifiedAt = utcNow;
                double elapsed = (utcNow - verifiedAt).TotalDays;
                int left = LicenseLogic.GraceDays - (int)Math.Ceiling(elapsed);
                return Math.Max(left, 0);
            }
            catch (Exception)
            {
                return 0;
            }
        }

        // ---------------- Mensajes ----------------
        /// <summary>Texto en español para cada resultado de bloqueo (LicenseBlockScreenVR lo muestra tal cual salvo que el server mande su propio "message").</summary>
        public static string MessageFor(LicenseLogic.LicenseGateResult result)
        {
            switch (result)
            {
                case LicenseLogic.LicenseGateResult.BlockPending:
                    return "Esperando aprobación del administrador.";
                case LicenseLogic.LicenseGateResult.BlockRejected:
                    return "Dispositivo rechazado. Contacte al administrador.";
                case LicenseLogic.LicenseGateResult.BlockSuspended:
                    return "Dispositivo suspendido.";
                case LicenseLogic.LicenseGateResult.BlockExpired:
                    return "Licencia vencida.";
                case LicenseLogic.LicenseGateResult.BlockNotFound:
                    return "Dispositivo no encontrado. Contacte al administrador.";
                case LicenseLogic.LicenseGateResult.BlockOffline:
                    return "Sin conexión. Conecte el visor a internet y reintente.";
                default:
                    return "No se pudo verificar la licencia de este dispositivo.";
            }
        }

        // ---------------- Telemetria (POST /api/log, mismo contrato que Update, F6) ----------------
        // Reusa UpdateLogic.LogEvent/SerializeLogBatch (logica PURA ya testeada en
        // UpdateLogicTests) en vez de duplicar el mismo DTO/serializacion -- el batch de
        // /api/log no es especifico de Update, es {device_id, events[{event,detail}]}
        // para cualquier evento del proyecto (ver docs/updates.md).
        private void SendTelemetry(string eventName, string detail)
        {
            if (DataManager.Instance == null) return; // sin backend resuelto, no hay a donde mandar
            StartCoroutine(SendTelemetryAsync(eventName, detail));
        }

        private IEnumerator SendTelemetryAsync(string eventName, string detail)
        {
            string url = DataManagerLogic.BuildSyncUrl(DataManager.Instance.BackendUrl, LogEndpoint);
            string json = UpdateLogic.SerializeLogBatch(SystemInfo.deviceUniqueIdentifier,
                new[] { new UpdateLogic.LogEvent(eventName, detail) });
            yield return BackendTelemetry.PostJson(url, json, "License: telemetria");
        }
    }
}
