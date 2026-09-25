using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using Simulador.Data;
using Simulador.Onboarding;
using Simulador.Vision;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;

namespace Simulador.EditorTools
{
    /// <summary>
    /// Disparador del arnes de captura del video tutorial (visor). Lee
    /// <c>build/video/plan.tsv</c> (generado APARTE, este script no lo genera) y por
    /// cada fila reproduce escenario/lente/calce/cabeza/libro con un reloj virtual
    /// para que dos corridas den EXACTAMENTE los mismos frames. El trabajo real lo hace
    /// <see cref="TutorialCaptureRunner"/> (mismo archivo).
    ///
    /// Deliberadamente EDITOR-ONLY: el runner vive en este asmdef (Simulador.Editor,
    /// solo carga en el Editor) y se agrega por codigo SOLO al entrar en Play Mode desde
    /// aca -- nunca viaja en un build (las assemblies de Editor no se empaquetan).
    ///
    /// Disparo: <see cref="EditorApplication.EnterPlaymode"/> dispara (si esta prendido
    /// "Enter Play Mode Options") un domain reload, que se come cualquier estado que no
    /// sea <see cref="SessionState"/> -- de ahi que el flag de armado viva ahi. El
    /// <see cref="InitializeOnLoadMethodAttribute"/> vuelve a registrar el handler de
    /// <see cref="EditorApplication.playModeStateChanged"/> en CADA recarga de dominio
    /// (incluida la que dispara este mismo EnterPlaymode), y ese handler crea el runner
    /// apenas el estado llega a <see cref="PlayModeStateChange.EnteredPlayMode"/> si el
    /// flag sigue puesto.
    /// </summary>
    public static class TutorialCapture
    {
        private const string SessionFlagKey = "Simulador.TutorialCapture.Armed";

        [MenuItem("Simulador/Capturar video tutorial")]
        public static void StartCapture()
        {
            string planPath = Path.Combine(GetProjectRoot(), TutorialCaptureRunner.PlanRelativePath);
            if (!File.Exists(planPath))
            {
                Debug.LogError($"[TutorialCapture] No se encontro el plan en '{planPath}'. Generalo antes de capturar.");
                return;
            }

            SessionState.SetBool(SessionFlagKey, true);
            EditorApplication.EnterPlaymode();
        }

        [InitializeOnLoadMethod]
        private static void RegisterPlayModeHook()
        {
            // -= antes de += : InitializeOnLoadMethod puede correr mas de una vez por
            // sesion de Editor (cada domain reload) y un evento estatico duplicado
            // crearia DOS runners al entrar en Play.
            EditorApplication.playModeStateChanged -= OnPlayModeStateChanged;
            EditorApplication.playModeStateChanged += OnPlayModeStateChanged;
        }

        private static void OnPlayModeStateChanged(PlayModeStateChange change)
        {
            if (change != PlayModeStateChange.EnteredPlayMode) return;
            if (!SessionState.GetBool(SessionFlagKey, false)) return;
            // Se desarma inmediatamente: un Play manual posterior (F5 normal) no debe
            // volver a disparar la captura.
            SessionState.SetBool(SessionFlagKey, false);

            var go = new GameObject("TutorialCaptureRunner");
            go.AddComponent<TutorialCaptureRunner>();
        }

        /// <summary>Raiz del proyecto (padre de Assets/), para resolver build/video/... a disco.</summary>
        internal static string GetProjectRoot() => Path.GetFullPath(Path.Combine(Application.dataPath, ".."));
    }

    /// <summary>
    /// Corre DENTRO de Play Mode (agregado por <see cref="TutorialCapture"/>) y reproduce
    /// <c>build/video/plan.tsv</c> frame a frame con un reloj virtual
    /// (<see cref="Time.captureFramerate"/>) + semilla fija (<see cref="Random.InitState"/>)
    /// para que el trafico nocturno (Vision/NightTraffic.cs, que usa <c>Random.Range</c>
    /// sin semilla en varios puntos) sea reproducible entre corridas. Vuelca los frames con
    /// <c>render=1</c> a <c>build/video/visor/{frame:000000}.jpg</c>.
    ///
    /// Camara auxiliar mono siguiendo el MISMO patron que
    /// <see cref="Simulador.Net.StreamingCapture"/> (render on-demand via
    /// <see cref="RenderPipeline.SubmitRenderRequest"/>, con fallback a
    /// <see cref="Camera.Render()"/>) -- la diferencia es <see cref="Texture2D.ReadPixels"/>
    /// SINCRONICO en vez de <c>AsyncGPUReadback</c>: aca importa mas el determinismo que la
    /// velocidad, y con el reloj virtual no hay apuro. El post-proceso de vision se aplica
    /// solo por ir por ese camino (una camara auxiliar mono, ver
    /// Vision/VisionRendererFeature.cs).
    ///
    /// Todas las filas del plan se aplican contra un estado "sentinela" (null/vacio), NUNCA
    /// contra lo que haya dejado el arranque normal de la escena (<c>ScenarioManager.Start()</c>
    /// siempre hace <c>SwitchTo(startScenario)</c>, que en <c>Main.unity</c> es
    /// <c>ruta_noche</c> -- distinto de la fila 0 tipica del plan): asi la fila 0 SIEMPRE
    /// reaplica escenario/lentes/calce sin importar el orden relativo entre el <c>Start()</c>
    /// de este runner y el de los componentes de la escena.
    /// </summary>
    internal class TutorialCaptureRunner : MonoBehaviour
    {
        internal const string PlanRelativePath = "build/video/plan.tsv";
        private const string OutputRelativeDir = "build/video/visor";
        private const int JpgQuality = 95;
        private const int ProgressLogEvery = 150;
        private const float CatalogWaitTimeoutS = 20f;

        // Direccion (espacio de camara) de un libro sostenido: un poco hacia abajo, al
        // frente. Ver docstring de la clase / handoff: dir =~ (0, -0.32, 1).
        // SIM: -0.32 en vez de -0.5 -- verificado en frames reales del beat de lectura.
        // El angulo bajo la linea de ojos es atan(|y|) (con x,z como estan, x=0):
        // -0.5 daba ~26.6 grados y, con el semi-FOV vertical de la camara en ~30 grados,
        // el libro caia al ~89% de la altura del cuadro -- asomaba apenas por el borde
        // inferior y no se apreciaba el desenfoque de lectura, que es lo que el video
        // tiene que mostrar. -0.32 da ~17.7 grados (~59% de la altura): centrado en la
        // mitad inferior, entero en cuadro con las paginas a la vista, tanto a 0.38 m
        // como a 0.72 m. Para recalibrar: angulo = atan(|y|), objetivo ~60% de la altura
        // con semi-FOV vertical ~30 grados -- no probar a ciegas, medir sobre frames reales.
        private static readonly Vector3 BookHoldDirLocal = new Vector3(0f, -0.32f, 1f).normalized;

        private struct PlanRow
        {
            public int Frame;
            public bool Render;
            public string Scenario;
            public string LensL;
            public string LensR;
            public bool Focus;
            public float YawDeg;
            public float PitchDeg;
            public float? BookDistanceM;
            public float BookTiltDeg;
            public string Beat;
        }

        // Columnas obligatorias del plan (P-video-2): si el header no trae alguna, se
        // aborta con el nombre que falta. "pitch" y "booktilt" son OPCIONALES (mas abajo,
        // via TryGetValue) para que un plan.tsv viejo sin ellas siga andando con 0.
        private static readonly string[] RequiredPlanColumns =
        {
            "frame", "render", "scenario", "lens_l", "lens_r", "focus", "yaw", "book", "beat"
        };

        // ---------------- Config leida del header del plan (#clave=valor) ----------------
        private int _fps = 30;
        private int _seed;
        private int _width = 1280;
        private int _height = 720;
        private int _totalFrames;

        private List<PlanRow> _plan;
        private string _outDir;

        // ---------------- Refs de escena (via ScenarioManager, minimal footprint: ya
        // expone book/xrOrigin/xrCamera publicos, no hace falta buscarlos por nombre) ----------------
        private ScenarioManager _scenarioManager;
        private Transform _xrOrigin;
        private Camera _headCam;
        private Transform _bookTransform;

        // ---------------- Camara de captura (patron StreamingCapture) ----------------
        private Camera _captureCam;
        private RenderTexture _rt;
        private Texture2D _readTex;

        // ---------------- Estado de reproduccion ----------------
        private Quaternion _baseRot;
        // SIM: ancla de POSICION del libro, capturada junto con _baseRot -- verificado en
        // frames reales (yaw -38/+38): el libro quedaba centrado en pantalla en las dos
        // capturas pese a que el fondo rotaba bien, porque la posicion se recalculaba cada
        // frame contra camT.position (camara viva). Fijando posicion Y rotacion base en el
        // mismo instante, el libro queda quieto en el mundo mientras la cabeza gira.
        private Vector3 _anchorPos;
        // SIM: rotacion AUTORADA del libro (la que ya tenia en la escena antes de
        // desemparentarlo) -- verificado en frames reales del beat de lectura: el
        // LookRotation(pos-cam) mostraba la TAPA (emblema de portada), no las paginas
        // abiertas. La pose original del autor de la escena SI muestra las paginas de
        // frente. No "corregir" esto de vuelta a LookRotation sin volver a mirar frames.
        private Quaternion _bookAuthoredRot;
        private string _appliedScenario;   // null = sentinela, fuerza aplicar la fila 0
        private string _appliedLensL;
        private string _appliedLensR;
        private bool? _appliedFocus;       // null = sentinela (FocusCheckScreenVR arranca VISIBLE)
        private int _currentRow = -1;
        private bool _aborted;

        private void Awake()
        {
            // Critico: el player loop de este proyecto se congela cuando el Editor pierde
            // foco, y esta corrida dura minutos (miles de frames).
            Application.runInBackground = true;

            if (!TryLoadPlan(out _plan, out string loadError))
            {
                Debug.LogError($"[TutorialCapture] {loadError}");
                Abort();
                return;
            }
            _totalFrames = _totalFrames > 0 ? _totalFrames : _plan.Count;

            _scenarioManager = FindFirstObjectByType<ScenarioManager>(FindObjectsInactive.Include);
            if (_scenarioManager == null)
            {
                Debug.LogError("[TutorialCapture] No se encontro ScenarioManager en la escena.");
                Abort();
                return;
            }
            _xrOrigin = _scenarioManager.xrOrigin;
            _headCam = _scenarioManager.xrCamera;
            if (_xrOrigin == null || _headCam == null)
            {
                Debug.LogError("[TutorialCapture] ScenarioManager sin xrOrigin/xrCamera asignados.");
                Abort();
                return;
            }
            _baseRot = _xrOrigin.rotation;
            _anchorPos = _headCam.transform.position;

            // Los ids de escenario no dependen del catalogo de lentes -- se validan ya,
            // sin esperar a DataManager (los ids de lente se validan en RunCapture, una
            // vez que el catalogo este listo).
            if (!ValidateScenarios(out string scenarioError))
            {
                Debug.LogError($"[TutorialCapture] {scenarioError}");
                Abort();
                return;
            }

            // Desemparentar el libro (ver docstring de la clase): ReadingBook cuelga del
            // Right Controller, que tiene su propio TrackedPoseDriver -- si no se
            // desemparenta, ese driver le pisa la pose cada frame. BookHolder solo lee
            // book.position, no le importa la jerarquia; ScenarioManager guarda la
            // referencia al GameObject, asi que reparentar no rompe el prendido/apagado
            // por escenario. Solo en Play Mode, no toca la escena en disco.
            if (_scenarioManager.book != null)
            {
                _bookTransform = _scenarioManager.book.transform;
                // Capturar la rotacion AUTORADA antes de desemparentar (ver campo
                // _bookAuthoredRot): es la rotacion mundial "conocida-buena" que le dio el
                // autor de la escena.
                _bookAuthoredRot = _bookTransform.rotation;
                _bookTransform.SetParent(null, true);
            }

            // Apagar el HUD (no debe aparecer en el video).
            var hud = FindFirstObjectByType<HudController>(FindObjectsInactive.Include);
            if (hud != null) hud.gameObject.SetActive(false);

            // Reloj virtual + semilla: lo antes posible, para que el trafico nocturno
            // (Vision/NightTraffic.cs) quede fijado desde el primer Random.Range que corra.
            Time.captureFramerate = _fps;
            Random.InitState(_seed);

            SetupCaptureCamera();

            _outDir = Path.Combine(TutorialCapture.GetProjectRoot(), OutputRelativeDir);
            Directory.CreateDirectory(_outDir);

            StartCoroutine(RunCapture());
        }

        private void Update()
        {
            // Cabeza (yaw) y libro: TODOS los frames mientras haya una fila activa, no
            // solo cuando cambian (ver handoff). Se hace en Update(), no en LateUpdate(),
            // para que el LateUpdate de BookHolder mida la posicion nueva ESE MISMO frame.
            if (_aborted || _plan == null || _currentRow < 0 || _currentRow >= _plan.Count) return;
            var row = _plan[_currentRow];

            // NUNCA rotar la Main Camera: tiene TrackedPoseDriver, que le reescribe
            // localRotation cada frame y pisaria cualquier valor que le pongamos aca.
            // PitchDeg (P-video-2, columna opcional "pitch"): cabeceo de cabeza para el
            // beat de lectura -- positivo = mirar hacia abajo (convencion de
            // Quaternion.Euler en Unity). El libro NO depende de este valor (ver mas
            // abajo: su pose sale de _anchorPos/_baseRot, fijados ANTES de este pitch), asi
            // que el efecto es la cabeza bajando la mirada hacia un libro quieto.
            _xrOrigin.rotation = _baseRot * Quaternion.Euler(row.PitchDeg, row.YawDeg, 0f);

            // SIM: ocultar el PIN de emparejamiento -- verificado en frames reales del beat
            // de calce: aparecia "PIN de emparejamiento: NNNNNN" porque en una sesion de
            // captura no hay tablet vinculada (ruido en el video; en uso real ya esta
            // emparejada). Hay que llamarlo CADA frame mientras el calce este visible:
            // NetworkController lo vuelve a setear por su cuenta cuando no hay tablet
            // autenticada, asi que un llamado unico se pierde.
            if (row.Focus) FocusCheckScreenVR.SetPairingPin(null);

            if (row.BookDistanceM.HasValue && _bookTransform != null && _bookTransform.gameObject.activeInHierarchy)
            {
                // SIM: pose ANCLADA (_anchorPos + _baseRot), NO camT.position/rotation
                // vivos -- el fix anterior (rotacion base pero posicion viva de camara)
                // no alcanzo: verificado en frames reales de yaw -38/+38, el libro seguia
                // centrado en pantalla en los dos porque la posicion se recalculaba contra
                // la camara cada frame. Con ancla fija en el mundo el libro queda quieto
                // mientras la cabeza gira, que es lo que el beat quiere mostrar.
                Vector3 dir = _baseRot * BookHoldDirLocal;
                Vector3 pos = _anchorPos + dir * row.BookDistanceM.Value;
                _bookTransform.position = pos;
                // SIM: rotacion AUTORADA fija (_bookAuthoredRot), NO LookRotation --
                // verificado en frames reales del beat de lectura: LookRotation(pos-cam)
                // mostraba la TAPA (emblema de portada), no las paginas. La pose original
                // de la escena SI muestra las paginas abiertas de frente.
                // BookTiltDeg (P-video-2, columna opcional "booktilt"): inclina el libro
                // hacia el lector (posicion de lectura en vez de "apoyado en una mesa"),
                // rotando alrededor del eje derecha del rig ANTES de la rotacion autorada.
                // Con booktilt=0 el resultado es IDENTICO al original (_bookAuthoredRot
                // solo) -- no tocar el eje/orden sin volver a verificar esa igualdad. El
                // signo que levanta el borde lejano hacia el lector se calibra desde el
                // plan (valores positivos/negativos), no aca.
                _bookTransform.rotation =
                    Quaternion.AngleAxis(row.BookTiltDeg, _baseRot * Vector3.right) * _bookAuthoredRot;
            }
        }

        private void LateUpdate()
        {
            // La camara de captura espeja el estado de la Main Camera CADA frame, no solo
            // la pose: con la pantalla de calce visible, CameraSceneOcclusionGate restringe
            // cullingMask/clearFlags de la Main Camera -- sin espejar eso, capturariamos la
            // escena de fondo detras del cartel modal.
            if (_aborted || _captureCam == null || _headCam == null) return;
            Transform camT = _headCam.transform;
            _captureCam.transform.SetPositionAndRotation(camT.position, camT.rotation);
            _captureCam.fieldOfView = _headCam.fieldOfView;
            _captureCam.nearClipPlane = _headCam.nearClipPlane;
            _captureCam.farClipPlane = _headCam.farClipPlane;
            _captureCam.cullingMask = _headCam.cullingMask;
            _captureCam.clearFlags = _headCam.clearFlags;
            _captureCam.backgroundColor = _headCam.backgroundColor;
        }

        private IEnumerator RunCapture()
        {
            // Esperar al catalogo de lentes antes de validar/aplicar lentes:
            // DataManager.InitializeAsync es asincronico (streaming + config + cache o
            // backend), asi que GetLens() puede devolver null solo por una carrera de
            // arranque, no porque el id sea invalido.
            float deadline = Time.realtimeSinceStartup + CatalogWaitTimeoutS;
            while ((DataManager.Instance == null || DataManager.Instance.Catalog == null)
                   && Time.realtimeSinceStartup < deadline)
                yield return null;

            if (DataManager.Instance == null || DataManager.Instance.Catalog == null)
            {
                Debug.LogError("[TutorialCapture] Timeout esperando el catalogo de lentes (DataManager).");
                Abort();
                yield break;
            }

            if (!ValidateLensIds(out string lensError))
            {
                Debug.LogError($"[TutorialCapture] {lensError}");
                Abort();
                yield break;
            }

            for (int i = 0; i < _plan.Count; i++)
            {
                PlanRow row = _plan[i];
                _currentRow = i;

                if (_appliedScenario != row.Scenario)
                {
                    _scenarioManager.SwitchTo(row.Scenario);
                    _appliedScenario = row.Scenario;
                    // SwitchTo reposiciona el rig a la pose de diseno del escenario:
                    // recachear la rotacion/posicion base ANTES de que Update() las use
                    // este frame (ver _anchorPos).
                    _baseRot = _xrOrigin.rotation;
                    _anchorPos = _headCam.transform.position;
                }

                if (_appliedLensL != row.LensL || _appliedLensR != row.LensR)
                {
                    ApplyLens(row.LensL, row.LensR);
                    _appliedLensL = row.LensL;
                    _appliedLensR = row.LensR;
                }

                if (_appliedFocus != row.Focus)
                {
                    FocusCheckScreenVR.SetVisible(row.Focus);
                    _appliedFocus = row.Focus;
                }

                yield return new WaitForEndOfFrame();

                if (row.Render) CaptureFrame(row.Frame);
                if (row.Frame % ProgressLogEvery == 0)
                    Debug.Log($"[TutorialCapture] frame {row.Frame}/{_totalFrames - 1} beat={row.Beat}");
            }

            Debug.Log("[TutorialCapture] Captura completa.");
            Finish();
        }

        // Aplica lente(s) del catalogo: una sola llamada "both" si coinciden (ver
        // handoff), o por ojo si difieren -- solo el/los lado(s) que efectivamente
        // cambiaron respecto del estado ya aplicado.
        private void ApplyLens(string lensL, string lensR)
        {
            var dm = DataManager.Instance;
            if (lensL == lensR)
            {
                dm.ApplyLens(lensL, "both");
            }
            else
            {
                if (_appliedLensL != lensL) dm.ApplyLens(lensL, "left");
                if (_appliedLensR != lensR) dm.ApplyLens(lensR, "right");
            }
        }

        private void CaptureFrame(int frameIndex)
        {
            RenderNow();

            RenderTexture prevActive = RenderTexture.active;
            RenderTexture.active = _rt;
            _readTex.ReadPixels(new Rect(0, 0, _width, _height), 0, 0, false);
            _readTex.Apply(false);
            RenderTexture.active = prevActive;

            byte[] jpg = ImageConversion.EncodeToJPG(_readTex, JpgQuality);
            string path = Path.Combine(_outDir, $"{frameIndex:000000}.jpg");
            File.WriteAllBytes(path, jpg);
        }

        // Render on-demand con la API soportada de URP; fallback a Camera.Render() --
        // mismo patron que Net/StreamingCapture.cs.
        private void RenderNow()
        {
            var request = new RenderPipeline.StandardRequest { destination = _rt };
            if (RenderPipeline.SupportsRenderRequest(_captureCam, request))
                RenderPipeline.SubmitRenderRequest(_captureCam, request);
            else
            {
                _captureCam.targetTexture = _rt;
                _captureCam.Render();
            }
        }

        private void SetupCaptureCamera()
        {
            _rt = new RenderTexture(_width, _height, 16, RenderTextureFormat.ARGB32) { name = "TutorialCaptureRT" };
            _rt.Create();
            _readTex = new Texture2D(_width, _height, TextureFormat.RGB24, false);

            var go = new GameObject("TutorialCaptureCam");
            go.transform.SetParent(transform, false);
            _captureCam = go.AddComponent<Camera>();
            _captureCam.stereoTargetEye = StereoTargetEyeMask.None;
            _captureCam.aspect = (float)_width / _height;
            _captureCam.enabled = false; // render on-demand, no cada frame de Unity
        }

        // ---------------- Parseo de build/video/plan.tsv ----------------
        private bool TryLoadPlan(out List<PlanRow> rows, out string error)
        {
            rows = null;
            string path = Path.Combine(TutorialCapture.GetProjectRoot(), PlanRelativePath);
            if (!File.Exists(path))
            {
                error = $"No se encontro el plan en '{path}'.";
                return false;
            }

            string[] lines = File.ReadAllLines(path);
            var list = new List<PlanRow>(lines.Length);
            // Mapa nombre->indice construido desde la linea de encabezado (P-video-2): el
            // plan va a sumar columnas, parsear por posicion fija lo hubiera roto cada vez.
            Dictionary<string, int> colIndex = null;
            int minCols = 0;

            foreach (string raw in lines)
            {
                string line = raw.TrimEnd('\r');
                if (line.Length == 0) continue;

                if (line[0] == '#')
                {
                    int eq = line.IndexOf('=');
                    if (eq < 0) continue;
                    string key = line.Substring(1, eq - 1);
                    string val = line.Substring(eq + 1);
                    switch (key)
                    {
                        case "fps": _fps = int.Parse(val, CultureInfo.InvariantCulture); break;
                        case "seed": _seed = int.Parse(val, CultureInfo.InvariantCulture); break;
                        case "width": _width = int.Parse(val, CultureInfo.InvariantCulture); break;
                        case "height": _height = int.Parse(val, CultureInfo.InvariantCulture); break;
                        case "frames": _totalFrames = int.Parse(val, CultureInfo.InvariantCulture); break;
                    }
                    continue;
                }

                if (colIndex == null)
                {
                    // Linea de encabezado ("frame  render  scenario  ..."): arma el mapa y
                    // valida que esten las columnas OBLIGATORIAS antes de leer ninguna fila.
                    string[] headerCols = line.Split('\t');
                    colIndex = new Dictionary<string, int>(headerCols.Length);
                    for (int i = 0; i < headerCols.Length; i++) colIndex[headerCols[i]] = i;
                    minCols = headerCols.Length;

                    foreach (string required in RequiredPlanColumns)
                    {
                        if (!colIndex.ContainsKey(required))
                        {
                            error = $"plan.tsv: falta la columna obligatoria '{required}' en el encabezado.";
                            rows = null;
                            return false;
                        }
                    }
                    continue;
                }

                string[] cols = line.Split('\t');
                if (cols.Length < minCols)
                {
                    error = $"Fila de plan.tsv con columnas insuficientes (se esperaban {minCols}): '{line}'.";
                    rows = null;
                    return false;
                }

                list.Add(new PlanRow
                {
                    Frame = int.Parse(cols[colIndex["frame"]], CultureInfo.InvariantCulture),
                    Render = cols[colIndex["render"]] == "1",
                    Scenario = cols[colIndex["scenario"]],
                    LensL = cols[colIndex["lens_l"]],
                    LensR = cols[colIndex["lens_r"]],
                    Focus = cols[colIndex["focus"]] == "1",
                    YawDeg = float.Parse(cols[colIndex["yaw"]], CultureInfo.InvariantCulture),
                    BookDistanceM = cols[colIndex["book"]] == "-" ? (float?)null : float.Parse(cols[colIndex["book"]], CultureInfo.InvariantCulture),
                    Beat = cols[colIndex["beat"]],
                    // Opcionales (P-video-2): si el plan es viejo y no las trae, quedan en
                    // 0 sin error -- 0 es "sin pitch"/"sin inclinacion", el comportamiento
                    // previo a esta tarea.
                    PitchDeg = colIndex.TryGetValue("pitch", out int pitchIdx)
                        ? float.Parse(cols[pitchIdx], CultureInfo.InvariantCulture) : 0f,
                    BookTiltDeg = colIndex.TryGetValue("booktilt", out int tiltIdx)
                        ? float.Parse(cols[tiltIdx], CultureInfo.InvariantCulture) : 0f,
                });
            }

            if (colIndex == null)
            {
                error = "plan.tsv no tiene linea de encabezado.";
                return false;
            }

            if (list.Count == 0)
            {
                error = "plan.tsv no tiene filas de datos.";
                return false;
            }

            rows = list;
            error = null;
            return true;
        }

        private bool ValidateScenarios(out string error)
        {
            foreach (PlanRow row in _plan)
            {
                if (row.Scenario != "consultorio" && row.Scenario != "ruta_noche")
                {
                    error = $"Escenario '{row.Scenario}' invalido en frame {row.Frame} (solo 'consultorio' o 'ruta_noche').";
                    return false;
                }
            }
            error = null;
            return true;
        }

        private bool ValidateLensIds(out string error)
        {
            var dm = DataManager.Instance;
            var checkedIds = new HashSet<string>();
            foreach (PlanRow row in _plan)
            {
                if (checkedIds.Add(row.LensL) && dm.GetLens(row.LensL) == null)
                {
                    error = $"Lente '{row.LensL}' (lens_l) no existe en el catalogo (frame {row.Frame}).";
                    return false;
                }
                if (checkedIds.Add(row.LensR) && dm.GetLens(row.LensR) == null)
                {
                    error = $"Lente '{row.LensR}' (lens_r) no existe en el catalogo (frame {row.Frame}).";
                    return false;
                }
            }
            error = null;
            return true;
        }

        // ---------------- Cierre ----------------
        private void Abort()
        {
            _aborted = true;
            ReleaseResources();
            if (EditorApplication.isPlaying) EditorApplication.ExitPlaymode();
        }

        private void Finish()
        {
            ReleaseResources();
            EditorApplication.ExitPlaymode();
        }

        private void ReleaseResources()
        {
            Time.captureFramerate = 0;
            if (_rt != null) { _rt.Release(); _rt = null; }
            if (_readTex != null) { Object.Destroy(_readTex); _readTex = null; }
        }

        private void OnDestroy()
        {
            // Defensivo: si el usuario corta Play Mode a mano a mitad de una corrida (sin
            // pasar por Abort()/Finish()), liberar igual la RenderTexture.
            if (_rt != null) _rt.Release();
        }
    }
}
