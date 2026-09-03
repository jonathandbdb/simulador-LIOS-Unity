using System;
using System.Collections;
using System.IO;
using System.Security.Cryptography;
using UnityEngine;
using UnityEngine.Networking;
using Simulador.Data;
using Simulador.Localization;
using Simulador.Tablet;

namespace Simulador.Update
{
    /// <summary>
    /// Singleton que chequea, descarga y verifica actualizaciones semi-automaticas del
    /// APK (visor o tablet, mismo endpoint parametrizado por canal). Bootstrap y ciclo de
    /// vida calcados de <see cref="Simulador.Data.DataManager"/> (RuntimeInitializeOnLoad
    /// + singleton + DontDestroyOnLoad); la corrutina de red usa el mismo patron de
    /// degradacion sin excepciones (try/catch sincrono alrededor de SendWebRequest).
    /// F3: check de manifest + descarga + verificacion SHA256 chunked. F4 (esta clase +
    /// <see cref="UpdateInstaller"/>): <see cref="LaunchInstall"/> cuelga el intent de
    /// instalacion Android del evento <c>ReadyToInstall</c>, con reintento automatico del
    /// permiso de fuentes desconocidas al volver a foco. F5: <see cref="CancelDownload"/>
    /// (boton "Cancelar" del cartel) y <see cref="MaybeShowVrPrompt"/> (crea
    /// <see cref="UpdatePromptVR"/> SOLO si no hay TabletController en escena -- la
    /// tablet arma su propia UI en TabletController, ver docs/updates.md).
    /// </summary>
    public class UpdateManager : MonoBehaviour
    {
        private const string ManifestEndpoint = "/api/manifest.json";
        private const int ManifestTimeoutSeconds = 5;
        private const string UpdatesFolderName = "updates";
        private const string ApkFileName = "simulador-update.apk";
        private const int Sha256ChunkBytes = 1024 * 1024; // 1 MB por yield, evita freeze con un archivo grande

        public static UpdateManager Instance { get; private set; }

        // ---------------- Eventos ----------------
        /// <summary>manifest recibido, forced = true solo si UpdateLogic.Decide dio Forced.</summary>
        public event Action<UpdateLogic.UpdateManifest, bool> UpdateAvailable;
        /// <summary>Progreso de descarga en [0,1].</summary>
        public event Action<float> DownloadProgress;
        public event Action<string> UpdateFailed;
        /// <summary>Path local del APK descargado y verificado, listo para el intent de instalacion (F4).</summary>
        public event Action<string> ReadyToInstall;

        private UpdateLogic.UpdateManifest _lastManifest;
        private Coroutine _downloadCo;
        // Referencia a la request activa mientras dura DownloadApk -- permite que
        // CancelDownload() (F5, boton "Cancelar" del cartel) la aborte desde
        // afuera de la corutina. Null fuera de una descarga en curso.
        private UnityWebRequest _activeDownloadReq;

        // ---------------- Estado del instalador (F4) ----------------
        // true entre "ReadyToInstall se disparo" y "arranco una descarga nueva" -- guarda
        // a LaunchInstall() de lanzar el intent sobre un archivo que no existe/ya fue
        // limpiado (p.ej. si UI llama LaunchInstall antes de que termine la descarga).
        private bool _readyToInstall;
        // true si el ultimo LaunchInstall() abrio el ajuste de "fuentes desconocidas" en
        // vez de instalar -- se reintenta solo, una vez, al volver del ajuste (ver
        // OnApplicationPause). Resultado accesible para telemetria (F6).
        private bool _permissionPendingRetry;

        /// <summary>
        /// Resultado del ultimo update aplicado, calculado al arrancar comparando
        /// Application.version contra el marcador dejado por UpdateInstaller antes del
        /// intent de instalacion previo. Vacio si no habia marcador (no hubo instalacion
        /// pendiente de verificar). Formato simple pensado para telemetria (F6), no UI.
        /// </summary>
        public string LastUpdateOutcome { get; private set; } = "";

        // Correcciones (CRITICO #2a, ver docs/updates.md): resultado del ultimo
        // LaunchInstall(), para que el caller (TabletController, en kiosco) pueda
        // decidir DESPUES de llamarlo si corresponde mostrar el modal "Instalando..."
        // -- antes se mostraba a ciegas ANTES de saber si la instalacion silenciosa
        // arranco de verdad. Default Started (valor 0 del enum) es inofensivo: solo
        // se lee inmediatamente despues de un LaunchInstall(), nunca antes.
        public UpdateInstaller.InstallLaunchResult LastInstallLaunchResult { get; private set; }

        // Evento update_success/update_incomplete calculado en CheckPendingUpdateMarker
        // (Awake, antes de que DataManager haya resuelto el backend) pero recien enviado
        // en InitializeAsync una vez pasado el WaitUntil -- mandarlo antes reventaria
        // contra un BackendUrl todavia con el default serializado (F6, ver docs/updates.md).
        private UpdateLogic.LogEvent? _pendingOutcomeEvent;

        private string AppChannel => UpdateLogic.AppChannelFromIdentifier(Application.identifier);

        private string UpdatesDir => Path.Combine(Application.persistentDataPath, UpdatesFolderName);
        private string ApkPath => Path.Combine(UpdatesDir, ApkFileName);
        private string PendingMarkerPath => Path.Combine(Application.persistentDataPath, UpdateLogic.PendingMarkerFileName);

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
        private static void Bootstrap()
        {
            if (Instance != null) return;
            var go = new GameObject("UpdateManager");
            go.AddComponent<UpdateManager>();
        }

        private void Awake()
        {
            if (Instance != null && Instance != this) { Destroy(gameObject); return; }
            Instance = this;
            DontDestroyOnLoad(gameObject);
            CheckPendingUpdateMarker();
            CleanupResidualUpdates();
            StartCoroutine(InitializeAsync());
        }

        // Lee (y borra) el marcador de install pendiente dejado por UpdateInstaller antes
        // del intent anterior. Independiente de CleanupResidualUpdates: el marcador vive
        // directo en persistentDataPath, no dentro de updates/ (que si se borra ahi).
        private void CheckPendingUpdateMarker()
        {
            string path = PendingMarkerPath;
            try
            {
                if (!File.Exists(path)) return;
                if (UpdateLogic.TryParsePendingMarker(File.ReadAllText(path), out var marker))
                {
                    string target = marker.TargetVersion;
                    if (!string.IsNullOrEmpty(target) && UpdateLogic.CompareVersions(Application.version, target) >= 0)
                    {
                        LastUpdateOutcome = $"ok:{Application.version}";
                        Debug.Log($"Update: update aplicado OK ({Application.version}).");
                        _pendingOutcomeEvent = new UpdateLogic.LogEvent("update_success", $"expected={target} actual={Application.version}");
                    }
                    else
                    {
                        LastUpdateOutcome = $"incompleto:sigue={Application.version};esperaba={target}";
                        Debug.Log($"Update: update incompleto (sigue {Application.version}, esperaba {target}).");
                        _pendingOutcomeEvent = new UpdateLogic.LogEvent("update_incomplete", $"expected={target} actual={Application.version}");
                    }
                }
                File.Delete(path);
            }
            catch (Exception e)
            {
                Debug.LogWarning($"Update: no se pudo leer/borrar el marcador de update pendiente ({e.GetType().Name}).");
            }
        }

        // Cierre explicito de ciclo de vida (revision pre-F7): si el singleton se destruye
        // con una descarga en vuelo (dominio poco probable dado el DontDestroyOnLoad, pero
        // no imposible -- p.ej. Editor saliendo de Play Mode), abortar/disponer la request
        // activa en vez de dejarla huerfana.
        private void OnDestroy()
        {
            try { _activeDownloadReq?.Abort(); _activeDownloadReq?.Dispose(); }
            catch (Exception) { /* SIM: atajo deliberado -- cierre best-effort, no debe tirar durante el destroy */ }
        }

        // Borra residuos de una corrida anterior (p.ej. un APK a medio descargar si la
        // app murio durante la descarga): silencioso, no critico para el arranque.
        private void CleanupResidualUpdates()
        {
            try
            {
                if (Directory.Exists(UpdatesDir)) Directory.Delete(UpdatesDir, recursive: true);
            }
            catch (Exception)
            {
                // SIM: atajo deliberado -- limpieza best-effort, un fallo aca no debe
                // bloquear el arranque de la app.
            }
        }

        private IEnumerator InitializeAsync()
        {
            // El BackendUrl efectivo depende de que DataManager ya resolvio sus capas de
            // config (override > streaming > default, ver docs/catalogo-lentes.md).
            yield return new WaitUntil(() => DataManager.Instance != null && DataManager.Instance.BackendConfigReady);
            if (_pendingOutcomeEvent.HasValue)
            {
                SendTelemetry(_pendingOutcomeEvent.Value);
                _pendingOutcomeEvent = null;
            }
            yield return CheckManifest();
        }

        // ---------------- Check de manifest ----------------
        private IEnumerator CheckManifest()
        {
            string channel = UpdateLogic.AppChannelFromIdentifier(Application.identifier);
            string url = DataManagerLogic.BuildSyncUrl(DataManager.Instance.BackendUrl, ManifestEndpoint) + "?app=" + channel;
            Debug.Log($"Update: chequeando manifest -> {url}");

            using var req = UnityWebRequest.Get(url);
            req.timeout = ManifestTimeoutSeconds;

            // Igual que TrySyncWithBackend (DataManager.cs): SendWebRequest puede lanzar
            // de forma sincrona (p.ej. cleartext HTTP bloqueado); se atrapa para degradar,
            // nunca se deja propagar.
            UnityWebRequestAsyncOperation op = null;
            try { op = req.SendWebRequest(); }
            catch (Exception e)
            {
                Debug.Log($"Update: no se pudo chequear el manifest ({e.GetType().Name}).");
                yield break;
            }
            yield return op;

            if (req.result != UnityWebRequest.Result.Success)
            {
                Debug.Log($"Update: manifest inalcanzable ({req.result}).");
                yield break;
            }
            // 503 es la respuesta ESPERADA cuando el backend no tiene version activa
            // publicada para este canal -- no es un error, es un "no hay update".
            if (req.responseCode == 503)
            {
                Debug.Log("Update: sin version activa publicada para este canal (503).");
                yield break;
            }
            if (req.responseCode != 200)
            {
                Debug.Log($"Update: manifest respondio {req.responseCode}.");
                yield break;
            }

            if (!UpdateLogic.TryParseManifest(req.downloadHandler.text, out var manifest))
            {
                Debug.Log("Update: manifest devolvio JSON invalido.");
                yield break;
            }

            var decision = UpdateLogic.Decide(Application.version, manifest);
            Debug.Log($"Update: manifest {manifest.App} v{manifest.ApkVersion} disponible (actual {Application.version}), decision={decision}");
            var checkEvent = new UpdateLogic.LogEvent("update_check",
                $"app={channel} installed={Application.version} remote={manifest.ApkVersion} decision={decision}");
            if (decision == UpdateLogic.UpdateDecision.None)
            {
                SendTelemetry(checkEvent);
                yield break;
            }

            _lastManifest = manifest;
            bool forced = decision == UpdateLogic.UpdateDecision.Forced;
            UpdateAvailable?.Invoke(manifest, forced);
            MaybeShowVrPrompt(manifest, forced);
            SendTelemetry(checkEvent,
                new UpdateLogic.LogEvent("update_prompt_shown", $"app={channel} remote={manifest.ApkVersion} forced={forced}"));
        }

        // ---------------- UI del cartel (F5, ver docs/updates.md) ----------------
        // La UI depende de la app: en la tablet la construye TabletController
        // (UpdateScreen, suscripto a los mismos eventos de arriba) y esta clase no
        // tiene que hacer nada mas. En el visor NO hay TabletController en escena,
        // asi que UpdateManager crea el cartel world-space (UpdatePromptVR) el
        // mismo. IMPORTANTE: la señal para elegir NO es Application.identifier
        // (en el Editor da SIEMPRE com.simulador.vr sin importar que escena este
        // abierta -- Tablet.unity incluida) sino la PRESENCIA de TabletController
        // en la escena, mismo criterio que NetworkController.Bootstrap (ver
        // docs/tablet.md). El identifier sigue siendo la fuente correcta para el
        // ?app= del manifest (CheckManifest arriba); eso no cambia.
        private void MaybeShowVrPrompt(UpdateLogic.UpdateManifest manifest, bool forced)
        {
            if (FindFirstObjectByType<TabletController>() != null) return; // la tablet ya tiene su propia UI (UpdateScreen)
            var prompt = GetComponent<UpdatePromptVR>();
            if (prompt == null) prompt = gameObject.AddComponent<UpdatePromptVR>();
            prompt.Show(manifest, forced);
        }

        // ---------------- API publica (F4/F5 le cuelgan la UI y el intent) ----------------
        /// <summary>Acepta la actualizacion ofrecida y arranca la descarga del ultimo manifest recibido.</summary>
        public void AcceptUpdate()
        {
            SendTelemetry(new UpdateLogic.LogEvent("update_accepted", $"app={AppChannel} version={_lastManifest?.ApkVersion}"));
            StartDownload();
        }

        /// <summary>El usuario pospone: no hay reintentos/recordatorios propios aca (decision de F4/F5).</summary>
        public void PostponeUpdate()
        {
            Debug.Log("Update: actualizacion pospuesta por el usuario.");
            SendTelemetry(new UpdateLogic.LogEvent("update_postponed", $"app={AppChannel} version={_lastManifest?.ApkVersion}"));
        }

        /// <summary>Reintenta la descarga del ultimo manifest recibido (mismo path que AcceptUpdate).</summary>
        public void RetryDownload() => StartDownload();

        /// <summary>
        /// Cancela una descarga en curso (F5, boton "Cancelar" del cartel de
        /// update). Aborta la UnityWebRequest activa (el DownloadHandlerFile ya
        /// tiene removeFileOnAbort=true, pero se limpia el parcial tambien por
        /// las dudas -- IL2CPP/Android a veces no llega a correr ese callback),
        /// corta la corutina de descarga y deja el estado como si nunca se
        /// hubiera empezado. A proposito NO dispara UpdateFailed: es una
        /// cancelacion del usuario, no un fallo. No-op si no hay descarga en
        /// curso.
        /// </summary>
        public void CancelDownload()
        {
            if (_downloadCo == null) return;
            AbortActiveDownload();
            _readyToInstall = false;
            Debug.Log("Update: descarga cancelada por el usuario.");
        }

        /// <summary>
        /// Aborta la request/corutina de descarga activa (si hay alguna) y limpia el
        /// parcial. Compartido por <see cref="CancelDownload"/> (cancelacion explicita
        /// del usuario) y <see cref="StartDownload"/> -- <c>StopCoroutine</c> NO ejecuta
        /// el <c>finally</c>/dispose del <c>using var req</c> dentro de <see cref="DownloadApk"/>
        /// (Unity no dispone el enumerator cortado), asi que sin este abort explicito la
        /// request vieja seguiria viva escribiendo sobre el MISMO <see cref="ApkPath"/> que
        /// la descarga nueva (carrera + leak de socket, hallado en revision pre-F7, ver
        /// docs/updates.md).
        /// </summary>
        private void AbortActiveDownload()
        {
            if (_downloadCo == null) return;
            try { _activeDownloadReq?.Abort(); }
            catch (Exception) { /* SIM: atajo deliberado -- best-effort, no bloquea el abort */ }
            StopCoroutine(_downloadCo);
            _downloadCo = null;
            _activeDownloadReq = null;
            CleanupPartialFile(ApkPath);
        }

        /// <summary>
        /// Dispara el intent de instalacion Android del APK descargado y verificado (F4).
        /// No-op si el estado actual no es ReadyToInstall (todavia no termino una
        /// descarga, o fallo desde entonces y el archivo ya no esta garantizado). Si
        /// Android pide el permiso de "fuentes desconocidas", queda armado el reintento
        /// automatico en <see cref="OnApplicationPause"/> al volver del ajuste del sistema.
        /// </summary>
        public void LaunchInstall()
        {
            if (!_readyToInstall)
            {
                Debug.LogWarning("Update: LaunchInstall llamado sin un APK listo para instalar.");
                return;
            }
            string targetVersion = _lastManifest?.ApkVersion ?? "";
            var result = UpdateInstaller.LaunchInstall(ApkPath, targetVersion, msg => UpdateFailed?.Invoke(msg));
            LastInstallLaunchResult = result;
            _permissionPendingRetry = result == UpdateInstaller.InstallLaunchResult.PermissionRequested;
            SendTelemetry(new UpdateLogic.LogEvent("update_install_launched", $"version={targetVersion} result={result}"));
        }

        // En Android, si LaunchInstall() abrio el ajuste de "fuentes desconocidas" (sin
        // permiso), reintentar una vez al volver a foco -- el usuario puede haber
        // concedido el permiso en Settings. pause=false es "la app volvio a foco" (mismo
        // evento que usa DataManager.OnApplicationPause para persistir, ver docs/catalogo-lentes.md).
        private void OnApplicationPause(bool pause)
        {
            if (pause || !_permissionPendingRetry) return;
            _permissionPendingRetry = false;
            LaunchInstall();
        }

        private void StartDownload()
        {
            if (_lastManifest == null)
            {
                Debug.LogWarning("Update: no hay manifest pendiente para descargar.");
                return;
            }
            // Aborta cualquier descarga en vuelo ANTES de arrancar la nueva -- ver
            // AbortActiveDownload (evita dos requests escribiendo al mismo ApkPath).
            AbortActiveDownload();
            _downloadCo = StartCoroutine(DownloadApk(_lastManifest));
        }

        // ---------------- Descarga ----------------
        private IEnumerator DownloadApk(UpdateLogic.UpdateManifest manifest)
        {
            // Una descarga nueva invalida cualquier estado "listo para instalar" previo
            // (podria ser un manifest/version distinta) -- ver LaunchInstall.
            _readyToInstall = false;
            _permissionPendingRetry = false;
            float startTime = Time.realtimeSinceStartup;

            try { Directory.CreateDirectory(UpdatesDir); }
            catch (Exception e)
            {
                UpdateFailed?.Invoke(L10n.T("update.err_create_folder", e.GetType().Name));
                SendTelemetry(new UpdateLogic.LogEvent("update_download_failed", e.GetType().Name));
                yield break;
            }

            string path = ApkPath;
            using var req = new UnityWebRequest(manifest.ApkUrl, UnityWebRequest.kHttpVerbGET);
            req.downloadHandler = new DownloadHandlerFile(path) { removeFileOnAbort = true };
            // Sin timeout a proposito: el APK puede ser grande, no queremos cortar una
            // descarga lenta pero en progreso (default de UnityWebRequest.timeout, 0 = sin limite).

            UnityWebRequestAsyncOperation op = null;
            try { op = req.SendWebRequest(); }
            catch (Exception e)
            {
                CleanupPartialFile(path);
                UpdateFailed?.Invoke(L10n.T("update.err_start_download", e.GetType().Name));
                SendTelemetry(new UpdateLogic.LogEvent("update_download_failed", e.GetType().Name));
                yield break;
            }

            _activeDownloadReq = req; // permite CancelDownload() abortarla desde afuera de esta corutina
            while (!op.isDone)
            {
                DownloadProgress?.Invoke(req.downloadProgress);
                yield return null;
            }
            _activeDownloadReq = null;
            DownloadProgress?.Invoke(req.downloadProgress);

            if (req.result != UnityWebRequest.Result.Success)
            {
                CleanupPartialFile(path);
                UpdateFailed?.Invoke(L10n.T("update.err_download_failed", req.result));
                SendTelemetry(new UpdateLogic.LogEvent("update_download_failed", req.result.ToString()));
                yield break;
            }
            if (req.responseCode != 200)
            {
                CleanupPartialFile(path);
                UpdateFailed?.Invoke(L10n.T("update.err_server_response", req.responseCode));
                SendTelemetry(new UpdateLogic.LogEvent("update_download_failed", $"http_{req.responseCode}"));
                yield break;
            }

            Debug.Log($"Update: APK descargado en {path}.");
            float elapsed = Time.realtimeSinceStartup - startTime;
            SendTelemetry(new UpdateLogic.LogEvent("update_download_ok", $"bytes={req.downloadedBytes} seconds={elapsed:F1}"));

            if (string.IsNullOrWhiteSpace(manifest.ApkSha256))
            {
                // El dummy manda apk_sha256 "" -- nada que verificar (Sha256Matches ya
                // devuelve true en ese caso, pero evitamos leer el archivo entero al pedo).
                _readyToInstall = true;
                ReadyToInstall?.Invoke(path);
                yield break;
            }
            yield return VerifySha256(path, manifest.ApkSha256);
        }

        private static void CleanupPartialFile(string path)
        {
            try { if (File.Exists(path)) File.Delete(path); }
            catch (Exception) { /* silencioso: best-effort, no bloquea el reporte del fallo */ }
        }

        // ---------------- Verificacion SHA256 (chunked, sin threads) ----------------
        // Se procesa en bloques de ~1 MB con yield return null entre cada uno para no
        // congelar el hilo principal leyendo un APK grande de un tirón.
        private IEnumerator VerifySha256(string path, string expectedHex)
        {
            string actualHex = null;
            bool ioError = false;

            using (var sha = SHA256.Create())
            {
                FileStream stream = null;
                try { stream = File.OpenRead(path); }
                catch (Exception e)
                {
                    ioError = true;
                    Debug.LogWarning($"Update: no se pudo abrir el APK para verificar SHA256 ({e.GetType().Name}).");
                }

                if (stream != null)
                {
                    var buffer = new byte[Sha256ChunkBytes];
                    while (true)
                    {
                        int read;
                        try { read = stream.Read(buffer, 0, buffer.Length); }
                        catch (Exception e)
                        {
                            ioError = true;
                            Debug.LogWarning($"Update: error leyendo el APK ({e.GetType().Name}).");
                            break;
                        }
                        if (read <= 0) break;
                        sha.TransformBlock(buffer, 0, read, buffer, 0);
                        yield return null;
                    }
                    stream.Dispose();

                    if (!ioError)
                    {
                        sha.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
                        actualHex = BitConverter.ToString(sha.Hash).Replace("-", "");
                    }
                }
            }

            if (ioError || actualHex == null)
            {
                CleanupPartialFile(path);
                UpdateFailed?.Invoke(L10n.T("update.err_verify_integrity"));
                SendTelemetry(new UpdateLogic.LogEvent("update_download_failed", "verify_io_error"));
                yield break;
            }

            if (!UpdateLogic.Sha256Matches(expectedHex, actualHex))
            {
                CleanupPartialFile(path);
                UpdateFailed?.Invoke("sha_mismatch");
                SendTelemetry(new UpdateLogic.LogEvent("update_sha_mismatch", $"expected={expectedHex} actual={actualHex}"));
                yield break;
            }

            Debug.Log("Update: SHA256 verificado, APK listo para instalar.");
            _readyToInstall = true;
            ReadyToInstall?.Invoke(path);
        }

        /// <summary>
        /// CRITICO #2c (correcciones, ver docs/updates.md): reporta un fallo de
        /// instalacion silenciosa detectado FUERA del flujo sincrono de
        /// <see cref="LaunchInstall"/> -- el resultado async del commit de
        /// PackageInstaller (<c>InstallResultReceiver.java</c> vía
        /// <c>TabletController.OnSilentInstallResult</c>) o el watchdog de timeout.
        /// Mismo evento <c>update_install_failed</c> que usa el resto del sistema
        /// de telemetria, expuesto publico porque <c>SendTelemetry</c> es privado.
        /// </summary>
        public void ReportInstallFailure(string detail) =>
            SendTelemetry(new UpdateLogic.LogEvent("update_install_failed", detail));

        // ---------------- Telemetria (F6) ----------------
        private const string LogEndpoint = "/api/log";

        /// <summary>
        /// Encola un batch de eventos <c>update_*</c> para POST /api/log (fire-and-forget:
        /// nunca bloquea ni reintenta el flujo de updates -- un fallo de red se loguea y
        /// nada mas, ver docs/updates.md). No-op si no hay eventos.
        /// </summary>
        private void SendTelemetry(params UpdateLogic.LogEvent[] events)
        {
            if (events == null || events.Length == 0) return;
            StartCoroutine(SendTelemetryAsync(events));
        }

        private IEnumerator SendTelemetryAsync(UpdateLogic.LogEvent[] events)
        {
            if (DataManager.Instance == null) yield break; // sin backend resuelto todavia, no hay a donde mandar
            string url = DataManagerLogic.BuildSyncUrl(DataManager.Instance.BackendUrl, LogEndpoint);
            string json = UpdateLogic.SerializeLogBatch(SystemInfo.deviceUniqueIdentifier, events);
            // Cuerpo del POST extraido a BackendTelemetry.PostJson (compartido con
            // Simulador.License.LicenseManager, ver docs/updates.md) -- mismo timeout
            // que el check de manifest (batch chico), mismo criterio de degradacion.
            yield return BackendTelemetry.PostJson(url, json, "Update: telemetria", ManifestTimeoutSeconds);
        }
    }
}
