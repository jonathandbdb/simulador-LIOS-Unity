using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.UI;
using Simulador.Data;
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
    /// OCULTAMIENTO DE LA ESCENA (rediseño posterior a un bug real en Quest,
    /// ver docs/updates.md Gotchas): un fix anterior tapaba la simulacion con
    /// un canvas OPACO pegado a 0.15 m de la camara -- eso causaba DIPLOPIA
    /// real en el visor (se veia un cartel por ojo, sin fusion estereo: a esa
    /// distancia la disparidad binocular exige una convergencia imposible para
    /// el ojo humano). Fix: el canvas volvio a una distancia estereo comoda
    /// (1.5 m, la que ya se sabia que fusionaba bien) y el ocultamiento de la
    /// escena de fondo se resuelve a nivel de CAMARA, no de geometria --
    /// <see cref="CameraSceneOcclusionGate"/> (Assets/Scripts/Runtime/Data/,
    /// compartido con <see cref="Simulador.License.LicenseBlockScreenVR"/>,
    /// mismo criterio que <c>BackendTelemetry</c>) restringe el
    /// <c>cullingMask</c> de <c>Camera.main</c> a la capa <c>UI</c> y fuerza
    /// <c>clearFlags = SolidColor</c> mientras el cartel este visible,
    /// restaurando el estado original al cerrarse. El gate usa refcount porque
    /// este cartel y <see cref="Simulador.License.LicenseBlockScreenVR"/>
    /// PUEDEN coexistir (los updates siguen funcionando con la licencia
    /// bloqueada, ver docs/licenciamiento.md): la escena solo vuelve cuando el
    /// ULTIMO consumidor se cierra. Este cartel sigue un poco mas CERCA que
    /// License (1.5 m vs 2.0 m) para ganar el orden de dibujado en la cola
    /// transparente si ambos coexisten (mismo criterio que antes del rediseño,
    /// solo que ahora sin el riesgo de diplopia). El ocultamiento se adquiere
    /// en <see cref="BuildCanvas"/> y se libera en <see cref="OnDestroy"/> --
    /// 1:1 con el ciclo de vida del canvas.
    ///
    /// GOTCHA critico (input): los botones A/B del mando derecho YA ciclan
    /// lentes via <see cref="SimuladorInput"/> (Assets/Scripts/Runtime/Vision/
    /// SimuladorInput.cs). Mientras este cartel esta visible se deshabilita ese
    /// componente (Find + enabled=false) para que aceptar/cerrar el update no
    /// dispare de paso un ciclo de lente de fondo -- se restaura SIEMPRE en
    /// OnDestroy (no se edita SimuladorInput.cs, frontera de @unity-dev). Este
    /// rediseño NO toca esa logica de input (ortogonal) ni la de
    /// <see cref="Simulador.License.LicenseBlockScreenVR"/> (guard anti-restore
    /// ahi, sin cambios).
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
        private bool _occlusionAcquired;

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
            if (_occlusionAcquired) { CameraSceneOcclusionGate.Release(); _occlusionAcquired = false; }
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
            // Distancia estereo comoda (rediseño posterior al bug de diplopia, ver
            // docstring de la clase): 1.5 m es la distancia que ya se sabia que
            // fusionaba bien antes del fix roto que la acerco a 0.15 m. Ya NO hace
            // falta agrandar el canvas para tapar el campo visual completo -- eso lo
            // resuelve CameraSceneOcclusionGate a nivel de camara -- asi que vuelve a
            // ser una tarjeta de tamaño normal (700x420) en vez de un plano gigante.
            _canvasGo.transform.localPosition = new Vector3(0f, 0f, 1.5f);
            _canvasGo.transform.localRotation = Quaternion.identity;
            _canvasGo.transform.localScale = new Vector3(0.0015f, 0.0015f, 0.0015f);

            var canvas = _canvasGo.GetComponent<Canvas>();
            canvas.renderMode = RenderMode.WorldSpace;
            var rt = _canvasGo.GetComponent<RectTransform>();
            rt.sizeDelta = new Vector2(700, 420);

            var font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");

            var panelGo = new GameObject("Panel", typeof(RectTransform), typeof(CanvasRenderer), typeof(Image));
            panelGo.transform.SetParent(_canvasGo.transform, false);
            Stretch(panelGo.GetComponent<RectTransform>());
            // Panel de la tarjeta (ya no necesita ser opaco para tapar nada -- ese
            // trabajo lo hace CameraSceneOcclusionGate sobre el fondo de la camara,
            // que queda solido detras). Un tono apenas mas claro que ese fondo para
            // que la tarjeta se distinga como un elemento propio.
            panelGo.GetComponent<Image>().color = new Color(0.08f, 0.09f, 0.11f, 0.95f);

            var layoutGo = new GameObject("Layout", typeof(RectTransform));
            layoutGo.transform.SetParent(_canvasGo.transform, false);
            var lrt = layoutGo.GetComponent<RectTransform>();
            Stretch(lrt);
            lrt.offsetMin = new Vector2(36, 28);
            lrt.offsetMax = new Vector2(-36, -28);
            var vlg = layoutGo.AddComponent<VerticalLayoutGroup>();
            vlg.spacing = 16;
            vlg.childControlWidth = true; vlg.childControlHeight = true;
            vlg.childForceExpandWidth = true; vlg.childForceExpandHeight = false;
            vlg.childAlignment = TextAnchor.MiddleCenter;

            _titleText = MakeLabel(layoutGo.transform, font, 30, FontStyle.Bold, Color.white);
            _bodyText = MakeLabel(layoutGo.transform, font, 21, FontStyle.Normal, new Color(0.85f, 0.9f, 0.95f));
            _legendText = MakeLabel(layoutGo.transform, font, 19, FontStyle.Italic, new Color(0.6f, 0.85f, 0.8f));

            // Ocultar la escena de fondo a nivel de camara (no de geometria, ver
            // docstring) mientras el canvas exista -- se libera en OnDestroy, 1:1 con
            // el ciclo de vida de este GameObject. El canvas entero (y sus hijos)
            // necesita estar en la capa UI para sobrevivir al cullingMask del gate.
            CameraSceneOcclusionGate.ApplyOverlayLayer(_canvasGo);
            CameraSceneOcclusionGate.Acquire();
            _occlusionAcquired = true;
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
