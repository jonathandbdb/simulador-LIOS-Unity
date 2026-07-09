using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.UI;
using Simulador.Vision;

namespace Simulador.Update
{
    /// <summary>
    /// Cartel de update semi-automatico en el visor (F5, ver docs/updates.md).
    /// Canvas world-space creado 100% por codigo -- nada de escena -- child de
    /// Camera.main (mismo patron que Vision/HudController.cs, ver su GameObject
    /// "DebugHUD" en Main.unity: canvas World Space colgado de la camara, sin
    /// GraphicRaycaster/EventSystem porque no hay interaccion por puntero, solo
    /// por botones del mando). Solo lo instancia
    /// <see cref="UpdateManager.MaybeShowVrPrompt"/> cuando NO hay un
    /// TabletController en la escena (la tablet arma su propia UI,
    /// TabletController.UpdateScreen).
    ///
    /// Estados: Available (prompt "A: actualizar / B: ahora no") -&gt;
    /// Downloading (progreso, sin input propio -- se espera a Ready/Failed) -&gt;
    /// Ready ("A: instalar") / Failed ("A: reintentar / B: cerrar"). Forced:
    /// sin opcion B en Available/Failed (Cancelar no aplica en VR, a diferencia
    /// de la tablet: no hay boton de cancelar descarga acá, ver docs/updates.md).
    ///
    /// GOTCHA critico (input): los botones A/B del mando derecho YA ciclan
    /// lentes via <see cref="SimuladorInput"/> (Assets/Scripts/Runtime/Vision/
    /// SimuladorInput.cs). Mientras este cartel esta visible se deshabilita ese
    /// componente (Find + enabled=false) para que aceptar/cerrar el update no
    /// dispare de paso un ciclo de lente de fondo -- se restaura SIEMPRE en
    /// OnDestroy (no se edita SimuladorInput.cs, frontera de @unity-dev).
    /// </summary>
    public class UpdatePromptVR : MonoBehaviour
    {
        private enum State { Hidden, Available, Downloading, Ready, Failed }

        private State _state = State.Hidden;
        private UpdateLogic.UpdateManifest _manifest;
        private bool _forced;
        private string _lastError = "";

        private GameObject _canvasGo;
        private Text _titleText;
        private Text _bodyText;
        private Text _legendText;

        private InputAction _a, _b;
        private SimuladorInput _simuladorInput;
        private bool _subscribedToManager;

        /// <summary>Muestra (o reutiliza, si ya estaba visible) el cartel con el manifest recibido.</summary>
        public void Show(UpdateLogic.UpdateManifest manifest, bool forced)
        {
            _manifest = manifest;
            _forced = forced;

            if (_canvasGo == null) BuildCanvas();
            SubscribeToManager();
            DisableGameplayInput();
            EnableOwnInput();
            SetState(State.Available);
        }

        private void OnDestroy()
        {
            UnsubscribeFromManager();
            DisableOwnInput();
            RestoreGameplayInput();
        }

        // ---------------- UpdateManager -> este cartel ----------------
        private void SubscribeToManager()
        {
            if (_subscribedToManager) return; // idempotente -- Show() puede llamarse de nuevo sin haber cerrado antes
            var um = UpdateManager.Instance;
            if (um == null) return;
            um.DownloadProgress += OnDownloadProgress;
            um.UpdateFailed += OnUpdateFailedEvt;
            um.ReadyToInstall += OnReadyToInstall;
            _subscribedToManager = true;
        }

        private void UnsubscribeFromManager()
        {
            if (!_subscribedToManager) return;
            var um = UpdateManager.Instance;
            _subscribedToManager = false;
            if (um == null) return;
            um.DownloadProgress -= OnDownloadProgress;
            um.UpdateFailed -= OnUpdateFailedEvt;
            um.ReadyToInstall -= OnReadyToInstall;
        }

        private void OnDownloadProgress(float progress)
        {
            if (_state != State.Downloading || _bodyText == null) return;
            _bodyText.text = $"{Mathf.RoundToInt(progress * 100f)} %";
        }

        private void OnReadyToInstall(string path) => SetState(State.Ready);

        private void OnUpdateFailedEvt(string message)
        {
            _lastError = message;
            SetState(State.Failed);
        }

        // ---------------- Input propio (A/B mano derecha, patron SimuladorInput.cs) ----------------
        private void EnableOwnInput()
        {
            if (_a != null) return; // ya habilitado (Show() llamado de nuevo sin haber cerrado antes)
            _a = new InputAction("UpdatePromptA", InputActionType.Button, "<XRController>{RightHand}/primaryButton");
            _b = new InputAction("UpdatePromptB", InputActionType.Button, "<XRController>{RightHand}/secondaryButton");
            _a.performed += _ => OnAPressed();
            _b.performed += _ => OnBPressed();
            _a.Enable(); _b.Enable();
        }

        private void DisableOwnInput()
        {
            _a?.Disable(); _a?.Dispose(); _a = null;
            _b?.Disable(); _b?.Dispose(); _b = null;
        }

        private void OnAPressed()
        {
            switch (_state)
            {
                case State.Available:
                    UpdateManager.Instance?.AcceptUpdate();
                    SetState(State.Downloading);
                    break;
                case State.Ready:
                    UpdateManager.Instance?.LaunchInstall();
                    Close();
                    break;
                case State.Failed:
                    UpdateManager.Instance?.RetryDownload();
                    SetState(State.Downloading);
                    break;
            }
        }

        private void OnBPressed()
        {
            if (_forced) return; // sin opcion B si la actualizacion es forzada
            switch (_state)
            {
                case State.Available:
                    UpdateManager.Instance?.PostponeUpdate();
                    Close();
                    break;
                case State.Failed:
                    Close();
                    break;
            }
        }

        // Mientras el cartel esta visible, los botones A/B NO deben ciclar
        // lentes de fondo (SimuladorInput.OnEnable/OnDisable ya se encargan de
        // (des)registrar sus propias InputAction al togglear "enabled").
        private void DisableGameplayInput()
        {
            _simuladorInput = FindFirstObjectByType<SimuladorInput>();
            if (_simuladorInput != null) _simuladorInput.enabled = false;
        }

        private void RestoreGameplayInput()
        {
            if (_simuladorInput == null) return;
            _simuladorInput.enabled = true;
            _simuladorInput = null;
        }

        // ---------------- Estados / textos ----------------
        private void SetState(State s)
        {
            _state = s;
            if (_titleText == null) return; // BuildCanvas fallo (sin Camera.main, ver Gotchas)
            switch (s)
            {
                case State.Available:
                    _titleText.text = "Actualización disponible";
                    _bodyText.text = $"v{Application.version} → v{_manifest.ApkVersion}" +
                        (string.IsNullOrEmpty(_manifest.Changelog) ? "" : "\n" + _manifest.Changelog);
                    _legendText.text = _forced ? "A: actualizar" : "A: actualizar     B: ahora no";
                    break;
                case State.Downloading:
                    _titleText.text = "Descargando actualización";
                    _bodyText.text = "0 %";
                    _legendText.text = "";
                    break;
                case State.Ready:
                    _titleText.text = "Descarga verificada";
                    _bodyText.text = "Lista para instalar.";
                    _legendText.text = "A: instalar";
                    break;
                case State.Failed:
                    _titleText.text = "Error al actualizar";
                    _bodyText.text = FriendlyError(_lastError);
                    _legendText.text = _forced ? "A: reintentar" : "A: reintentar     B: cerrar";
                    break;
            }
        }

        private static string FriendlyError(string raw) =>
            raw == "sha_mismatch" ? "La descarga no pasó la verificación de integridad." : raw;

        private void Close()
        {
            if (_canvasGo != null) { Destroy(_canvasGo); _canvasGo = null; }
            Destroy(this); // dispara OnDestroy -> desuscribe, restaura input e input de gameplay
        }

        // ---------------- Construccion del canvas world-space ----------------
        private void BuildCanvas()
        {
            var cam = Camera.main;
            if (cam == null)
            {
                // SIM: atajo deliberado -- sin camara no hay donde anclar el cartel;
                // en Main.unity Camera.main siempre existe (XR Origin), esto solo
                // cubriria un smoke roto/escena atipica. El resto del flujo de
                // update sigue funcionando (descarga/instalacion), solo falta el
                // cartel visual.
                Debug.LogWarning("[Update] No se encontro Camera.main; no se puede mostrar el cartel VR de actualizacion.");
                return;
            }

            _canvasGo = new GameObject("UpdatePromptVR", typeof(RectTransform), typeof(Canvas));
            _canvasGo.transform.SetParent(cam.transform, false);
            _canvasGo.transform.localPosition = new Vector3(0f, 0f, 1.5f); // ~1.5 m frente a la camara
            _canvasGo.transform.localRotation = Quaternion.identity;
            _canvasGo.transform.localScale = new Vector3(0.0015f, 0.0015f, 0.0015f);

            var canvas = _canvasGo.GetComponent<Canvas>();
            canvas.renderMode = RenderMode.WorldSpace;
            var rt = _canvasGo.GetComponent<RectTransform>();
            rt.sizeDelta = new Vector2(700, 420); // px logicos; tamaño real = sizeDelta * localScale (~1.05 x 0.63 m)

            var font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");

            var panelGo = new GameObject("Panel", typeof(RectTransform), typeof(CanvasRenderer), typeof(Image));
            panelGo.transform.SetParent(_canvasGo.transform, false);
            Stretch(panelGo.GetComponent<RectTransform>());
            panelGo.GetComponent<Image>().color = new Color(0.05f, 0.06f, 0.08f, 0.88f);

            var layoutGo = new GameObject("Layout", typeof(RectTransform));
            layoutGo.transform.SetParent(_canvasGo.transform, false);
            var lrt = layoutGo.GetComponent<RectTransform>();
            Stretch(lrt);
            lrt.offsetMin = new Vector2(36, 32);
            lrt.offsetMax = new Vector2(-36, -32);
            var vlg = layoutGo.AddComponent<VerticalLayoutGroup>();
            vlg.spacing = 18;
            vlg.childControlWidth = true; vlg.childControlHeight = true;
            vlg.childForceExpandWidth = true; vlg.childForceExpandHeight = false;
            vlg.childAlignment = TextAnchor.UpperCenter;

            _titleText = MakeLabel(layoutGo.transform, font, 40, FontStyle.Bold, Color.white);
            _bodyText = MakeLabel(layoutGo.transform, font, 27, FontStyle.Normal, new Color(0.85f, 0.9f, 0.95f));
            _legendText = MakeLabel(layoutGo.transform, font, 25, FontStyle.Italic, new Color(0.6f, 0.85f, 0.8f));
        }

        private static Text MakeLabel(Transform parent, Font font, int size, FontStyle style, Color color)
        {
            var go = new GameObject("Label", typeof(RectTransform), typeof(CanvasRenderer), typeof(Text));
            go.transform.SetParent(parent, false);
            var t = go.GetComponent<Text>();
            t.font = font;
            t.fontSize = size;
            t.fontStyle = style;
            t.color = color;
            t.alignment = TextAnchor.MiddleCenter;
            t.horizontalOverflow = HorizontalWrapMode.Wrap;
            t.verticalOverflow = VerticalWrapMode.Overflow;
            return t;
        }

        private static void Stretch(RectTransform rt)
        {
            rt.anchorMin = Vector2.zero;
            rt.anchorMax = Vector2.one;
            rt.offsetMin = Vector2.zero;
            rt.offsetMax = Vector2.zero;
        }
    }
}
