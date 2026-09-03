using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.UI;
using Simulador.Data;
using Simulador.Localization;
using Simulador.Vision;

namespace Simulador.License
{
    /// <summary>
    /// Cartel de bloqueo de licencia en el visor (F3, ver docs/licenciamiento.md). Molde
    /// calcado de <see cref="Simulador.Update.UpdatePromptVR"/>: canvas world-space 100%
    /// por codigo, child de Camera.main, sin GraphicRaycaster/EventSystem (solo boton de
    /// mando). A diferencia del cartel de update, este NO tiene salida: mientras
    /// <see cref="LicenseManager.IsBlocked"/> sea true no hay forma de cerrarlo salvo un
    /// verify OK (lo destruye <see cref="LicenseManager"/>, nunca este mismo).
    ///
    /// Lo crea/destruye <see cref="LicenseManager"/> (AddComponent/Destroy en
    /// Block()/Unblock()), nunca se referencia desde una escena.
    ///
    /// OCULTAMIENTO DE LA ESCENA (rediseño posterior a un bug real de diplopia en Quest,
    /// ver docs/licenciamiento.md Gotchas y docs/updates.md -- este cartel usaba el MISMO
    /// mecanismo roto que <see cref="Simulador.Update.UpdatePromptVR"/>, canvas opaco a
    /// 0.2 m de la camara, y sufria el mismo problema aunque nadie lo habia evaluado en
    /// dispositivo todavia): el canvas volvio a una distancia estereo comoda (2.0 m) y el
    /// ocultamiento de la simulacion de fondo pasa a <see cref="CameraSceneOcclusionGate"/>
    /// (Assets/Scripts/Runtime/Data/, compartido con UpdatePromptVR) -- restringe el
    /// cullingMask de Camera.main a la capa UI + clearFlags=SolidColor mientras el cartel
    /// exista, con refcount porque este cartel y UpdatePromptVR PUEDEN coexistir (ver
    /// Decisiones en docs/licenciamiento.md). Este cartel queda un poco MAS LEJOS que
    /// UpdatePromptVR (2.0 m vs 1.5 m) a proposito -- si ambos estan visibles, Update debe
    /// ganar el orden de dibujado (mismo criterio que antes del rediseño, ahora sin riesgo
    /// de diplopia porque ninguno de los dos esta pegado al ojo). El gate se adquiere en
    /// <see cref="BuildCanvas"/> y se libera en <see cref="OnDestroy"/>.
    ///
    /// GOTCHA de input: mientras este cartel esta vivo deshabilita
    /// <see cref="Vision.SimuladorInput"/> (mismo patron que UpdatePromptVR) PERO ademas
    /// se re-asegura en <see cref="Update"/> mientras <see cref="LicenseManager.IsBlocked"/>
    /// -- si el cartel de UPDATE (<see cref="Simulador.Update.UpdatePromptVR"/>) se abre
    /// por encima (los updates siguen funcionando aunque el dispositivo este bloqueado,
    /// deliberado, ver docs/licenciamiento.md) y luego se cierra, su OnDestroy restaura
    /// SimuladorInput.enabled=true; sin este guard, cerrar el cartel de update
    /// "reactivaria" el ciclo de lentes de fondo mientras la licencia sigue bloqueada.
    /// Guard simetrico en el otro sentido: si ESTE cartel se destruye (verify OK) con
    /// UpdatePromptVR todavia visible encima, <see cref="RestoreGameplayInput"/> NO
    /// reactiva SimuladorInput -- deja que sea UpdatePromptVR quien lo haga al cerrarse.
    /// </summary>
    public class LicenseBlockScreenVR : MonoBehaviour
    {
        private GameObject _canvasGo;
        private Text _titleText;
        private Text _bodyText;
        private Text _deviceIdText;
        private Text _legendText;

        private InputAction _a;
        private SimuladorInput _simuladorInput;
        private bool _occlusionAcquired;

        private string _message = "";

        /// <summary>Muestra (o actualiza, si ya estaba visible) el cartel con el mensaje actual del gate.</summary>
        public void Show(LicenseLogic.LicenseGateResult result, string message)
        {
            _message = message ?? "";

            if (_canvasGo == null) BuildCanvas();
            EnsureGameplayInputDisabled();
            EnableOwnInput();
            Refresh();
        }

        private void Update()
        {
            // Guard anti-restore (ver docstring de la clase): mientras el dispositivo
            // siga bloqueado, re-asegurar SimuladorInput deshabilitado en cada frame --
            // cubre que otro cartel (UpdatePromptVR) se haya abierto encima y, al
            // cerrarse, restaurado el input de gameplay.
            if (LicenseManager.IsBlocked) EnsureGameplayInputDisabled();
            UpdateLegend();
        }

        private void OnDestroy()
        {
            // A diferencia de UpdatePromptVR.Close() (que hace Destroy(_canvasGo) +
            // Destroy(this) juntos, desde ADENTRO de la clase): a este componente lo
            // destruye LicenseManager.Unblock() desde AFUERA con Destroy(_blockScreen)
            // (destruye solo el componente, no el GameObject del canvas -- son
            // GameObjects distintos, _canvasGo es hijo de la camara, no de este
            // MonoBehaviour). Sin este cleanup el canvas quedaba huerfano en la escena
            // (bug real hallado en el gate de esta tarea: el cartel seguia RENDERIZANDO
            // despues de desbloquear, aunque IsBlocked ya fuera false).
            if (_canvasGo != null) { Destroy(_canvasGo); _canvasGo = null; }
            DisableOwnInput();
            RestoreGameplayInput();
            if (_occlusionAcquired) { CameraSceneOcclusionGate.Release(); _occlusionAcquired = false; }
        }

        // ---------------- Input propio (boton A, patron UpdatePromptVR) ----------------
        private void EnableOwnInput()
        {
            if (_a != null) return; // ya habilitado (Show() llamado de nuevo, p.ej. mensaje actualizado)
            _a = new InputAction("LicenseBlockA", InputActionType.Button, "<XRController>{RightHand}/primaryButton");
            _a.performed += _ => OnAPressed();
            _a.Enable();
        }

        private void DisableOwnInput()
        {
            _a?.Disable(); _a?.Dispose(); _a = null;
        }

        private void OnAPressed()
        {
            // Durante el cooldown no se dispara -- LicenseManager.RetryVerify() ya lo
            // ignoraria igual, pero cortar aca evita generar un log de "ignorado" por
            // cada toque durante la cuenta regresiva (ver docs/licenciamiento.md).
            var lm = LicenseManager.Instance;
            if (lm == null || lm.RetryCooldownRemaining > 0f) return;
            lm.RetryVerify();
        }

        // ---------------- Gameplay input (SimuladorInput) ----------------
        private void EnsureGameplayInputDisabled()
        {
            if (_simuladorInput == null) _simuladorInput = FindFirstObjectByType<SimuladorInput>();
            if (_simuladorInput != null) _simuladorInput.enabled = false;
        }

        private void RestoreGameplayInput()
        {
            if (_simuladorInput == null) return;
            // Guard simetrico al anti-restore de Update(): si UpdatePromptVR sigue
            // vivo en escena (se abrio encima de este cartel y todavia no se cerro),
            // NO restaurar el input de gameplay aca -- es UpdatePromptVR quien lo hace
            // en su propio OnDestroy cuando se cierre. Sin este guard, un
            // LicenseBlockScreenVR que se destruye (verify OK) mientras el cartel de
            // update sigue tapando la pantalla reactivaria el ciclo de lentes de
            // fondo antes de tiempo.
            if (FindFirstObjectByType<Simulador.Update.UpdatePromptVR>() != null) return;
            _simuladorInput.enabled = true;
            _simuladorInput = null;
        }

        // ---------------- Estado / textos ----------------
        private void Refresh()
        {
            if (_titleText == null) return; // BuildCanvas fallo (sin Camera.main, ver Gotchas)
            _titleText.text = L10n.T("license.title");
            _bodyText.text = _message;
            if (_deviceIdText != null) _deviceIdText.text = L10n.T("license.device_id_prefix", SystemInfo.deviceUniqueIdentifier);
            UpdateLegend();
        }

        private void UpdateLegend()
        {
            if (_legendText == null) return;
            var lm = LicenseManager.Instance;
            float remaining = lm != null ? lm.RetryCooldownRemaining : 0f;
            _legendText.text = remaining > 0f
                ? L10n.T("license.retry_in", Mathf.CeilToInt(remaining))
                : L10n.T("license.retry_button_legend");
        }

        // ---------------- Construccion del canvas world-space ----------------
        private void BuildCanvas()
        {
            var cam = Camera.main;
            if (cam == null)
            {
                // SIM: atajo deliberado -- sin camara no hay donde anclar el cartel (mismo
                // caso que UpdatePromptVR.BuildCanvas); en Main.unity Camera.main siempre
                // existe. El gate sigue bloqueando el resto de la app igual (IsBlocked
                // sigue true), solo falta el cartel visual.
                Debug.LogWarning("License: no se encontro Camera.main; no se puede mostrar el cartel de bloqueo de licencia.");
                return;
            }

            _canvasGo = new GameObject("LicenseBlockScreenVR", typeof(RectTransform), typeof(Canvas));
            _canvasGo.transform.SetParent(cam.transform, false);
            // Distancia estereo comoda (rediseño posterior al bug de diplopia, ver
            // docstring de la clase): 2.0 m, un poco mas lejos que los 1.5 m de
            // UpdatePromptVR a proposito -- si ambos cartels coexisten, el mas cerca de
            // la camara gana el orden de dibujado en la cola transparente de Unity UI
            // (Update por encima de License, mismo criterio documentado antes del
            // rediseño). Ya NO hace falta un canvas gigante pegado al ojo para tapar la
            // escena -- eso lo resuelve CameraSceneOcclusionGate a nivel de camara.
            _canvasGo.transform.localPosition = new Vector3(0f, 0f, 2.0f);
            _canvasGo.transform.localRotation = Quaternion.identity;
            _canvasGo.transform.localScale = new Vector3(0.002f, 0.002f, 0.002f);

            var canvas = _canvasGo.GetComponent<Canvas>();
            canvas.renderMode = RenderMode.WorldSpace;
            var rt = _canvasGo.GetComponent<RectTransform>();
            // Un poco mas alto que el de UpdatePromptVR (760x520 vs 700x420): este
            // cartel tiene una linea de texto mas (device_id).
            rt.sizeDelta = new Vector2(760, 520);

            var font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");

            var panelGo = new GameObject("Panel", typeof(RectTransform), typeof(CanvasRenderer), typeof(Image));
            panelGo.transform.SetParent(_canvasGo.transform, false);
            Stretch(panelGo.GetComponent<RectTransform>());
            // Panel de la tarjeta (ya no necesita ser opaco para tapar nada -- eso lo
            // hace CameraSceneOcclusionGate sobre el fondo de la camara). Mismo tono que
            // UpdatePromptVR para que ambos cartels de "pantalla completa" compartan el
            // mismo lenguaje visual.
            panelGo.GetComponent<Image>().color = new Color(0.08f, 0.09f, 0.11f, 0.95f);

            var layoutGo = new GameObject("Layout", typeof(RectTransform));
            layoutGo.transform.SetParent(_canvasGo.transform, false);
            var lrt = layoutGo.GetComponent<RectTransform>();
            Stretch(lrt);
            lrt.offsetMin = new Vector2(40, 32);
            lrt.offsetMax = new Vector2(-40, -32);
            var vlg = layoutGo.AddComponent<VerticalLayoutGroup>();
            vlg.spacing = 18;
            vlg.childControlWidth = true; vlg.childControlHeight = true;
            vlg.childForceExpandWidth = true; vlg.childForceExpandHeight = false;
            vlg.childAlignment = TextAnchor.MiddleCenter;

            _titleText = MakeLabel(layoutGo.transform, font, 34, FontStyle.Bold, Color.white);
            _bodyText = MakeLabel(layoutGo.transform, font, 22, FontStyle.Normal, new Color(0.9f, 0.85f, 0.8f));
            _deviceIdText = MakeLabel(layoutGo.transform, font, 15, FontStyle.Italic, new Color(0.55f, 0.55f, 0.6f));
            _legendText = MakeLabel(layoutGo.transform, font, 19, FontStyle.Italic, new Color(0.6f, 0.85f, 0.8f));

            // Ocultar la escena de fondo a nivel de camara (no de geometria, ver
            // docstring) mientras el canvas exista -- se libera en OnDestroy, 1:1 con el
            // ciclo de vida de este GameObject.
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
