using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.UI;
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
            _titleText.text = "Licencia del dispositivo";
            _bodyText.text = _message;
            if (_deviceIdText != null) _deviceIdText.text = $"ID del dispositivo: {SystemInfo.deviceUniqueIdentifier}";
            UpdateLegend();
        }

        private void UpdateLegend()
        {
            if (_legendText == null) return;
            var lm = LicenseManager.Instance;
            float remaining = lm != null ? lm.RetryCooldownRemaining : 0f;
            _legendText.text = remaining > 0f
                ? $"Reintentar en {Mathf.CeilToInt(remaining)}s..."
                : "A: reintentar";
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
            // A diferencia de UpdatePromptVR (tarjeta chica a 1.5 m, deja ver la escena
            // alrededor): este cartel debe TAPAR la vista por completo (spec: "fondo
            // opaco que tape la escena, sin salida"). Ajustes sobre el molde de
            // UpdatePromptVR, verificados con captura en el gate de esta tarea:
            // 1) MUY cerca de la camara (0.2 m, bien por encima del near clip de 0.01 m)
            //    -- en Main.unity hay geometria de cockpit (volante/tablero) a menos de
            //    0.9 m de la camara que quedaba POR DELANTE del panel y se seguia viendo;
            //    a 0.2 m no hay nada de la escena mas cerca que el propio cartel.
            // 2) sizeDelta grande (2600x1900) para subtender un angulo bien por encima
            //    del FOV tipico (Quest ~100-110 grados) sin depender de calcular el
            //    exacto -- PERO la escala se achica en la MISMA proporcion en que se
            //    achico la distancia (0.0015 * 0.2/1.5 = 0.0002) para que el tamaño
            //    ANGULAR del texto (lo que importa para legibilidad) quede igual que en
            //    UpdatePromptVR; sin este ajuste el primer intento (misma escala 0.0015
            //    a 0.2 m) dejaba el texto gigante/recortado -- world size = sizeDelta *
            //    escala, y lo que se ve depende de world size / distancia, asi que achicar
            //    distancia sin achicar la escala en la misma proporcion agranda todo.
            _canvasGo.transform.localPosition = new Vector3(0f, 0f, 0.2f);
            _canvasGo.transform.localRotation = Quaternion.identity;
            _canvasGo.transform.localScale = new Vector3(0.0002f, 0.0002f, 0.0002f);

            var canvas = _canvasGo.GetComponent<Canvas>();
            canvas.renderMode = RenderMode.WorldSpace;
            var rt = _canvasGo.GetComponent<RectTransform>();
            rt.sizeDelta = new Vector2(2600, 1900);

            var font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");

            var panelGo = new GameObject("Panel", typeof(RectTransform), typeof(CanvasRenderer), typeof(Image));
            panelGo.transform.SetParent(_canvasGo.transform, false);
            Stretch(panelGo.GetComponent<RectTransform>());
            // Fondo OPACO (a diferencia del semi-transparente de UpdatePromptVR): tapa la
            // escena por completo mientras el gate este bloqueado, sin salida.
            panelGo.GetComponent<Image>().color = new Color(0.03f, 0.03f, 0.04f, 1f);

            var layoutGo = new GameObject("Layout", typeof(RectTransform));
            layoutGo.transform.SetParent(_canvasGo.transform, false);
            var lrt = layoutGo.GetComponent<RectTransform>();
            Stretch(lrt);
            lrt.offsetMin = new Vector2(48, 40);
            lrt.offsetMax = new Vector2(-48, -40);
            var vlg = layoutGo.AddComponent<VerticalLayoutGroup>();
            vlg.spacing = 22;
            vlg.childControlWidth = true; vlg.childControlHeight = true;
            vlg.childForceExpandWidth = true; vlg.childForceExpandHeight = false;
            vlg.childAlignment = TextAnchor.MiddleCenter;

            _titleText = MakeLabel(layoutGo.transform, font, 46, FontStyle.Bold, Color.white);
            _bodyText = MakeLabel(layoutGo.transform, font, 30, FontStyle.Normal, new Color(0.9f, 0.85f, 0.8f));
            _deviceIdText = MakeLabel(layoutGo.transform, font, 20, FontStyle.Italic, new Color(0.55f, 0.55f, 0.6f));
            _legendText = MakeLabel(layoutGo.transform, font, 26, FontStyle.Italic, new Color(0.6f, 0.85f, 0.8f));
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
