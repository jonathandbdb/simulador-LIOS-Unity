using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using Newtonsoft.Json;
using UnityEngine;
using UnityEngine.Networking;

namespace Simulador.Data
{
    /// <summary>
    /// Singleton fuente-de-verdad del catalogo de lentes y del estado binocular.
    /// Port de autoloads/data_manager.gd (Godot). Cadena de carga:
    ///   1) cache en persistentDataPath/lentes.json
    ///   2) embebido en StreamingAssets/lentes.json
    ///   3) sync en background con el backend (/api/lenses)
    /// El sync NO bloquea el arranque: si el backend no esta, se sigue con cache/defaults.
    /// </summary>
    public class DataManager : MonoBehaviour
    {
        // --- Config backend (LAN de desarrollo). Cambiar segun la red del backend. ---
        // El backend hoy no esta levantado: el sync fallara y se usa el catalogo local.
        [SerializeField] private string backendUrl = "http://192.168.88.198:8080";
        private const string CatalogEndpoint = "/api/lenses";
        private const int SyncTimeoutSeconds = 5;

        private const string CatalogFileName = "lentes.json";
        private const string OverridesFileName = "lens_overrides.json";

        public static DataManager Instance { get; private set; }

        // ---------------- Eventos (equivalen a las signals de Godot) ----------------
        /// <summary>(version, source, lensCount). source: "cache" | "defaults" | "backend".</summary>
        public event Action<string, string, int> CatalogLoaded;
        public event Action<string> CatalogSyncedWithBackend;
        public event Action<string> CatalogSyncFailed;
        /// <summary>(eye, state). eye: "left" | "right".</summary>
        public event Action<string, EyeState> VisionStateChanged;

        // ---------------- Estado ----------------
        public LensCatalog Catalog { get; private set; }
        public string CatalogSource { get; private set; } = "";
        public double LastSyncTime { get; private set; }
        public EyeState Left { get; private set; } = new EyeState();
        public EyeState Right { get; private set; } = new EyeState();
        public bool BlendModeEnabled { get; private set; }

        private string _defaultsText;   // texto de StreamingAssets, para el merge
        private LensCatalog _defaults;   // catalogo embebido parseado, para el merge
        private readonly Dictionary<string, Dictionary<string, float>> _lensOverrides = new();
        private bool _overridesSavePending;
        private Coroutine _saveCo;

        private string UserCatalogPath => Path.Combine(Application.persistentDataPath, CatalogFileName);
        private string OverridesPath => Path.Combine(Application.persistentDataPath, OverridesFileName);

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
        private static void Bootstrap()
        {
            if (Instance != null) return;
            var go = new GameObject("DataManager");
            go.AddComponent<DataManager>();
        }

        private void Awake()
        {
            if (Instance != null && Instance != this) { Destroy(gameObject); return; }
            Instance = this;
            DontDestroyOnLoad(gameObject);
            StartCoroutine(InitializeAsync());
        }

        private IEnumerator InitializeAsync()
        {
            LoadLensOverrides();
            // Defaults embebidos primero: necesarios para el merge de cache/backend.
            yield return LoadStreamingText(CatalogFileName, t => _defaultsText = t);
            _defaults = CatalogParser.Parse(_defaultsText);

            if (!TryLoadFromCache())
                TryLoadFromDefaults();

            // Sync en background (no bloquea).
            yield return TrySyncWithBackend();
        }

        // ---------------- Carga local ----------------
        private bool TryLoadFromCache()
        {
            if (!File.Exists(UserCatalogPath)) return false;
            string text;
            try { text = File.ReadAllText(UserCatalogPath); }
            catch (Exception) { return false; }
            var parsed = CatalogParser.Parse(text);
            if (parsed == null)
            {
                Debug.LogWarning($"DataManager: catalogo cache invalido en {UserCatalogPath}, ignorando.");
                return false;
            }
            CatalogParser.MergeMissingParams(parsed, _defaults);
            SetCatalog(parsed, "cache");
            return true;
        }

        private bool TryLoadFromDefaults()
        {
            if (_defaults == null)
            {
                CatalogSyncFailed?.Invoke("No hay catalogo cache ni defaults disponibles.");
                Debug.LogError("DataManager: no se encontro ningun catalogo local.");
                return false;
            }
            SetCatalog(_defaults, "defaults");
            return true;
        }

        private void SetCatalog(LensCatalog cat, string source)
        {
            Catalog = cat;
            CatalogSource = source;
            string version = cat.Version ?? "unknown";
            int count = CatalogParser.CountLenses(cat);
            CatalogLoaded?.Invoke(version, source, count);
            Debug.Log($"DataManager: catalogo v{version} cargado desde {source} ({count} lentes).");
        }

        // ---------------- Sync backend ----------------
        public void RefreshFromBackend() => StartCoroutine(TrySyncWithBackend());

        private IEnumerator TrySyncWithBackend()
        {
            string url = backendUrl + CatalogEndpoint;
            Debug.Log($"DataManager: sync con backend -> {url}");
            using var req = UnityWebRequest.Get(url);
            req.timeout = SyncTimeoutSeconds;

            // SendWebRequest puede lanzar de forma sincrona (p.ej. HTTP cleartext
            // bloqueado): atraparlo para degradar a fallo de sync, no a excepcion.
            UnityWebRequestAsyncOperation op = null;
            try { op = req.SendWebRequest(); }
            catch (System.Exception e)
            {
                CatalogSyncFailed?.Invoke($"No se pudo iniciar la sync ({e.GetType().Name}). Usando catalogo local.");
                yield break;
            }
            yield return op;

            if (req.result != UnityWebRequest.Result.Success)
            {
                CatalogSyncFailed?.Invoke($"Backend inalcanzable ({req.result}). Usando catalogo local.");
                yield break;
            }
            if (req.responseCode != 200)
            {
                CatalogSyncFailed?.Invoke($"Backend respondio {req.responseCode}. Usando catalogo local.");
                yield break;
            }
            string text = req.downloadHandler.text;
            var parsed = CatalogParser.Parse(text);
            if (parsed == null)
            {
                CatalogSyncFailed?.Invoke("Backend devolvio JSON invalido. Usando catalogo local.");
                yield break;
            }
            SaveToCache(text);
            CatalogParser.MergeMissingParams(parsed, _defaults);
            Catalog = parsed;
            CatalogSource = "backend";
            LastSyncTime = (DateTime.UtcNow - new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc)).TotalSeconds;
            string version = parsed.Version ?? "unknown";
            int count = CatalogParser.CountLenses(parsed);
            CatalogLoaded?.Invoke(version, "backend", count);
            CatalogSyncedWithBackend?.Invoke(version);
            Debug.Log($"DataManager: catalogo v{version} sincronizado desde backend ({count} lentes).");
        }

        private void SaveToCache(string text)
        {
            try { File.WriteAllText(UserCatalogPath, text); }
            catch (Exception) { Debug.LogWarning($"DataManager: no se pudo escribir {UserCatalogPath}"); }
        }

        // ---------------- API publica de consulta ----------------
        /// <summary>Devuelve la lente por id, o null si no existe.</summary>
        public LensDef GetLens(string lensId)
        {
            if (Catalog?.Catalogo == null) return null;
            foreach (var l in Catalog.Catalogo)
                if (l != null && l.Id == lensId) return l;
            return null;
        }

        /// <summary>Ids del catalogo, en orden.</summary>
        public List<string> GetLensIds()
        {
            var ids = new List<string>();
            if (Catalog?.Catalogo == null) return ids;
            foreach (var l in Catalog.Catalogo)
                if (l != null && l.Id != null) ids.Add(l.Id);
            return ids;
        }

        // ---------------- Aplicacion de lentes ----------------
        /// <summary>Aplica una lente a un ojo o ambos. eye: "left" | "right" | "both".</summary>
        public void ApplyLens(string lensId, string eye = "both")
        {
            var lens = GetLens(lensId);
            if (lens == null)
            {
                Debug.LogWarning($"DataManager: lente '{lensId}' no encontrada");
                return;
            }
            _lensOverrides.TryGetValue(lensId, out var saved);
            var built = LensEngine.BuildEyeState(lens, saved);

            if (eye == "left" || eye == "both")
            {
                Left = built.Clone();
                VisionStateChanged?.Invoke("left", Left);
            }
            if (eye == "right" || eye == "both")
            {
                Right = built.Clone();
                VisionStateChanged?.Invoke("right", Right);
            }
            UpdateBlend();
        }

        /// <summary>Override de params en tiempo real (tablet). Persiste por lente.</summary>
        public void OverrideParams(IReadOnlyDictionary<string, float> paramsToSet, string eye = "both")
        {
            if (paramsToSet == null || paramsToSet.Count == 0) return;
            var targets = eye == "both" ? new[] { "left", "right" } : new[] { eye };
            foreach (var e in targets)
            {
                var state = e == "left" ? Left : (e == "right" ? Right : null);
                if (state == null || state.IsEmpty) continue;
                foreach (var kv in paramsToSet) state.Params[kv.Key] = kv.Value;
                VisionStateChanged?.Invoke(e, state);
                if (!string.IsNullOrEmpty(state.LensId))
                    StoreLensOverrides(state.LensId, paramsToSet);
            }
        }

        private void UpdateBlend() => BlendModeEnabled = LensEngine.ComputeBlend(Left.LensId, Right.LensId);

        // ---------------- Persistencia de overrides ----------------
        private void StoreLensOverrides(string lensId, IReadOnlyDictionary<string, float> paramsToSet)
        {
            var catParams = GetLens(lensId)?.Params;
            _lensOverrides.TryGetValue(lensId, out var saved);
            saved = LensEngine.CleanOverrides(saved, paramsToSet, catParams);
            if (saved.Count == 0) _lensOverrides.Remove(lensId);
            else _lensOverrides[lensId] = saved;
            ScheduleOverridesSave();
        }

        private void ScheduleOverridesSave()
        {
            // Debounce: 1 s tras el ultimo cambio (los sliders emiten muchos por segundo).
            if (_overridesSavePending) return;
            _overridesSavePending = true;
            _saveCo = StartCoroutine(DebouncedSave());
        }

        private IEnumerator DebouncedSave()
        {
            yield return new WaitForSeconds(1f);
            SaveLensOverrides();
            _saveCo = null;
        }

        private void SaveLensOverrides()
        {
            _overridesSavePending = false;
            try
            {
                File.WriteAllText(OverridesPath, JsonConvert.SerializeObject(_lensOverrides, Formatting.Indented));
                Debug.Log($"DataManager: overrides guardados ({_lensOverrides.Count} lentes).");
            }
            catch (Exception)
            {
                Debug.LogWarning($"DataManager: no se pudo escribir {OverridesPath}");
            }
        }

        private void LoadLensOverrides()
        {
            if (!File.Exists(OverridesPath)) return;
            try
            {
                var parsed = JsonConvert.DeserializeObject<Dictionary<string, Dictionary<string, float>>>(
                    File.ReadAllText(OverridesPath));
                if (parsed != null)
                {
                    _lensOverrides.Clear();
                    foreach (var kv in parsed) _lensOverrides[kv.Key] = kv.Value;
                    Debug.Log($"DataManager: overrides cargados ({_lensOverrides.Count} lentes).");
                }
            }
            catch (Exception) { /* archivo corrupto: se ignora */ }
        }

        // En Quest/Android la app puede morir al perder foco: persistir si hay pendiente.
        private void OnApplicationPause(bool pause)
        {
            if (pause && _overridesSavePending) SaveLensOverrides();
        }

        private void OnApplicationQuit()
        {
            if (_overridesSavePending) SaveLensOverrides();
        }

        // ---------------- Util ----------------
        private static IEnumerator LoadStreamingText(string fileName, Action<string> onDone)
        {
            string url = Path.Combine(Application.streamingAssetsPath, fileName);
            // En Android StreamingAssets vive dentro del APK (jar://) y SOLO se lee por
            // UnityWebRequest; en desktop/editor hace falta el prefijo file://.
            if (!url.Contains("://")) url = "file://" + url;
            using var req = UnityWebRequest.Get(url);
            yield return req.SendWebRequest();
            onDone(req.result == UnityWebRequest.Result.Success ? req.downloadHandler.text : null);
        }
    }
}
