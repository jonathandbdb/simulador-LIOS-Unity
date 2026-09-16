using UnityEngine;
using UnityEngine.UI;
using Simulador.Data;
using Simulador.License;
using Simulador.Localization;
using Simulador.Vision;

namespace Simulador.Onboarding
{
    /// <summary>
    /// Pantalla de chequeo de calce del visor: un texto corto que el paciente debe poder leer
    /// NÍTIDO para confirmar que el casco esta bien colocado (no es un diagnostico de la LIO
    /// simulada, es un descarte de "esto es el hardware, no la lente"). Se auto-crea SOLA al
    /// arrancar el visor (<see cref="Bootstrap"/>, solo si hay un <see cref="ScenarioManager"/>
    /// en la escena -- asi no se dispara en Tablet.unity, que comparte el asmdef
    /// Simulador.Runtime pero no tiene ese componente) y arranca VISIBLE. El medico la
    /// muestra/oculta despues desde la tablet en cualquier momento de la sesion (comando
    /// "set_focus_check", ver docs/networking.md), tipicamente cuando el paciente dice "no veo
    /// bien" y hay que descartar que sea el casco mal puesto.
    ///
    /// Deliberadamente FUERA de Vision/ (Assets/Scripts/Runtime/Vision/): esto es UI de sesion
    /// (calce del hardware), no optica clinica -- esa carpeta es de @vision-optics.
    ///
    /// GOTCHA CENTRAL (por que el texto sale nitido SIN tocar el sistema de vision): el
    /// post-proceso (blur dioptrico, astigmatismo, perdida de contraste, velo) se inyecta en
    /// <see cref="VisionRendererFeature.injectionPoint"/> = RenderPassEvent.BeforeRenderingTransparents
    /// (Assets/Scripts/Runtime/Vision/VisionRendererFeature.cs:31-36); un Canvas world-space
    /// (renderQueue 3000, Transparent) se dibuja DESPUES de ese pass, asi que queda FUERA del
    /// blur/astigmatismo/contraste/velo POR CONSTRUCCION -- igual que los billboards de glare
    /// (aditivos, cola transparente, se componen encima de la imagen ya borroseada). Es
    /// intencional: la pantalla de calce debe leerse siempre nitida, sin importar la LIO
    /// aplicada, porque esta midiendo el HARDWARE, no la simulacion. Es el INVERSO EXACTO del
    /// gotcha del optotipo ETDRS (docs/vision-optica.md, "Optotipo ETDRS" -- P4.5-fix, lineas
    /// ~1553-1565), que tuvo que forzarse a renderQueue=2450 (cola opaca) para que el
    /// post-proceso SI lo alcanzara, porque ese texto SI debe leerse con la LIO puesta (mide
    /// agudeza funcional). Un agente futuro que note "este texto no tiene blur" NO debe
    /// "corregirlo" imitando el fix del optotipo -- seria exactamente el bug contrario. No hace
    /// falta bypass del VisionRendererFeature ni el escenario paciente_joven: la lente aplicada
    /// nunca se toca ni se pierde.
    ///
    /// GEOMETRIA: Canvas world-space hijo de Camera.main, a 2.0 m (localScale 0.002) -- misma
    /// distancia que <see cref="LicenseBlockScreenVR"/> (ver su BuildCanvas) por DOS motivos:
    /// (a) es la distancia de fusion estereo probada en dispositivo (hubo diplopia REAL en Quest
    /// con canvas a 0.15-0.2 m, ver <see cref="CameraSceneOcclusionGate"/> y docs/updates.md); (b)
    /// coincide con el plano focal fijo del Quest, que es justo donde el texto esta opticamente
    /// mas nitido -- lo que se esta midiendo.
    ///
    /// OCLUSION DE LA ESCENA: usa el mismo <see cref="CameraSceneOcclusionGate"/> que
    /// LicenseBlockScreenVR/UpdatePromptVR (refcount compartido, Acquire/Release 1:1 con la
    /// visibilidad de ESTE cartel -- un Acquire sin su Release deja la camara ocluida para
    /// siempre). A diferencia de esos dos cartels (que se crean/destruyen una vez por evento),
    /// este puede toggearse muchas veces en una sesion (boton de la tablet), asi que el canvas
    /// se arma UNA sola vez y despues solo se activa/desactiva (SetActive) -- Acquire/Release
    /// solo corren en las transiciones oculto-&gt;visible/visible-&gt;oculto, nunca en un toggle
    /// repetido al mismo estado (ver <see cref="SetVisibleInternal"/>).
    ///
    /// NO COEXISTE con <see cref="LicenseBlockScreenVR"/> (fail-closed, misma distancia/escala:
    /// dos canvases superpuestos a 2 m serian ilegibles). Resuelto sin tocar License/: SetVisible
    /// no hace nada si el bloqueo de licencia esta activo en ese momento, y mientras este cartel
    /// esta visible, Update() lo oculta solo si el bloqueo aparece despues (verify async que
    /// falla mientras el paciente esta haciendo el chequeo de calce). No hay un guard simetrico
    /// del otro lado (LicenseBlockScreenVR no sabe de este cartel) porque el bloqueo de licencia
    /// es fail-closed y de mayor prioridad: si aparece, gana siempre.
    ///
    /// GOTCHA de input: esta pantalla NO deshabilita <see cref="SimuladorInput"/> ni agrega
    /// InputActions propias -- se controla EXCLUSIVAMENTE desde la tablet (comando
    /// "set_focus_check"), asi que el riesgo de "tercera mano" sobre ese flag (ver el comentario
    /// de <c>AdminGate</c> en SimuladorInput.cs:54-57, que ya documenta la disputa entre
    /// LicenseBlockScreenVR y UpdatePromptVR) desaparece por diseño: no hay una tercera mano.
    /// </summary>
    public class FocusCheckScreenVR : MonoBehaviour
    {
        private static FocusCheckScreenVR _instance;

        /// <summary>True mientras el cartel este efectivamente visible (canvas activo).</summary>
        public static bool IsVisible { get; private set; }

        private GameObject _canvasGo;
        private Text _titleText, _line1Text, _line2Text, _line3Text, _centerMarkText;
        private bool _visible;
        private bool _occlusionAcquired;

        // PIN de emparejamiento (ver docs/pantalla-calce.md, gotcha "punto muerto de
        // emparejamiento"): CameraSceneOcclusionGate.Acquire() restringe el cullingMask
        // de la camara a la capa UI mientras este cartel esta visible, asi que el
        // DebugHUD (capa Default) -- donde normalmente se muestra el PIN -- queda
        // CULLED. Sin este PIN replicado aca, un emparejamiento nuevo es imposible
        // mientras el chequeo de calce esta visible (que es SIEMPRE al arrancar, antes
        // de que exista ninguna tablet). _pendingPin guarda el valor si SetPairingPin
        // se llama antes de que el canvas exista (el orden de arranque entre
        // NetworkController.Start() y el Bootstrap de esta clase no esta garantizado);
        // _pairingPinRow es un GameObject aparte (no directamente el Text) para poder
        // desactivarlo entero -- asi el VerticalLayoutGroup no le reserva espacio
        // cuando no hay PIN que mostrar (p.ej. ya hay una tablet autenticada).
        private static string _pendingPin;
        private GameObject _pairingPinRow;
        private Text _pairingPinText;

        // Guard anti-coexistencia (ver Update()): NO se chequea por frame -- mismo criterio que
        // NetworkController.DiscoverSceneRefs (Net/NetworkController.cs:201-213), que ya acota un
        // FindFirstObjectByType a 1 Hz por el mismo motivo. Acá el cartel puede quedar visible
        // minutos enteros (el paciente acomodándose la correa) a 72-90 Hz en el Quest, y
        // FindObjectsInactive.Include barre TODA la escena (incluida la jerarquía inactiva de
        // RutaNoche) -- sin acotar, ese barrido corría en cada frame de todo ese tiempo. 2 Hz
        // (cada 0.5 s) alcanza sobra para el caso de uso (una verificación de licencia que falla
        // async no necesita reaccionar en el frame exacto) sin el costo de un FindFirstObjectByType
        // por frame.
        private const float LicenseCheckIntervalS = 0.5f;
        private float _licenseCheckTimer;

        // Reset defensivo: RuntimeInitializeOnLoadMethod corre en CADA sesion de Play (Editor o
        // build), con o sin domain reload -- sin esto, un estado estatico residual de una sesion
        // de Play anterior con Domain Reload deshabilitado (Editor) dejaria IsVisible/_instance
        // apuntando a un objeto ya destruido en la sesion siguiente (mismo patron que
        // CameraSceneOcclusionGate.ResetStaticState).
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void ResetStaticState()
        {
            _instance = null;
            IsVisible = false;
            _pendingPin = null;
        }

        // Se auto-crea SOLO si hay un ScenarioManager en la escena (componente exclusivo del
        // visor, Vision/) -- asi Tablet.unity, que comparte este mismo asmdef
        // (Simulador.Runtime), nunca instancia este cartel. No se edita Vision/ ni la escena
        // para lograr esto.
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void Bootstrap()
        {
            if (_instance != null) return; // defensivo, no deberia poder pasar sin un reset de por medio
            if (FindFirstObjectByType<ScenarioManager>(FindObjectsInactive.Include) == null) return;

            var go = new GameObject("FocusCheckScreenVR");
            _instance = go.AddComponent<FocusCheckScreenVR>();
            // Arranca visible: el paciente debe poder confirmar el calce apenas se pone el visor,
            // antes incluso de que una tablet se conecte.
            _instance.SetVisibleInternal(true);
        }

        /// <summary>Muestra/oculta el cartel. No-op si nunca hubo bootstrap (p.ej. Tablet.unity).</summary>
        public static void SetVisible(bool visible)
        {
            if (_instance == null) return;
            _instance.SetVisibleInternal(visible);
        }

        /// <summary>
        /// Muestra/oculta la linea del PIN de emparejamiento en ESTE cartel (ver comentario del
        /// campo <see cref="_pendingPin"/> para el porque). <paramref name="pin"/> null o vacio
        /// oculta la linea (ya hay una tablet autenticada, o el visor todavia no genero PIN).
        /// Empujado por <see cref="Simulador.Net.NetworkController"/> (Net -&gt; Onboarding, nunca
        /// al reves) -- esta clase nunca resuelve el PIN por su cuenta. Seguro de llamar ANTES de
        /// que exista el canvas (o incluso antes de <see cref="Bootstrap"/>): el valor queda en
        /// <see cref="_pendingPin"/> y se aplica en cuanto el canvas se construye.
        /// </summary>
        public static void SetPairingPin(string pin)
        {
            _pendingPin = string.IsNullOrEmpty(pin) ? null : pin;
            _instance?.ApplyPendingPin();
        }

        private void Update()
        {
            // Guard anti-coexistencia (ver docstring de la clase): si el bloqueo de licencia
            // aparece MIENTRAS este cartel esta visible (verify async que falla a mitad del
            // chequeo de calce), se oculta solo -- el bloqueo de licencia es fail-closed y gana
            // siempre. No hay guard simetrico del otro lado (no se edita License/). Acotado a
            // ~2 Hz (ver el comentario de _licenseCheckTimer): un FindFirstObjectByType con
            // FindObjectsInactive.Include por frame, durante los minutos que el cartel puede
            // seguir visible, es un costo real en el Quest.
            if (!_visible) return;
            _licenseCheckTimer += Time.deltaTime;
            if (_licenseCheckTimer < LicenseCheckIntervalS) return;
            _licenseCheckTimer = 0f;

            if (IsLicenseBlocked()) SetVisibleInternal(false);
        }

        private void OnDestroy()
        {
            // Defensivo: este GameObject no se destruye en el flujo normal (vive toda la sesion
            // de Play), pero si algo lo destruyera con el gate adquirido, liberarlo evita dejar
            // la camara ocluida para siempre.
            if (_occlusionAcquired) { CameraSceneOcclusionGate.Release(); _occlusionAcquired = false; }
            // Revision (MENOR): sin esto, SetVisible() queda como no-op permanente (_instance
            // sigue apuntando a un objeto destruido) mientras BuildHello() sigue mandando
            // focus_check:true en cada hello -- el boton de la tablet mentiria "Ocultar calce"
            // para siempre.
            _visible = false;
            IsVisible = false;
            if (_instance == this) _instance = null;
        }

        private void SetVisibleInternal(bool visible)
        {
            if (visible)
            {
                if (_visible) return; // ya visible -- evita un doble Acquire
                if (IsLicenseBlocked())
                {
                    // Revision (MENOR): sin este log, el sintoma en campo es "toque el boton y
                    // no paso nada" sin rastro en logcat.
                    Debug.Log("[Onboarding] set_focus_check ignorado: pantalla de bloqueo de licencia activa.");
                    return;
                }

                if (_canvasGo == null) BuildCanvas();
                if (_canvasGo == null) return; // BuildCanvas fallo (sin Camera.main, ver Gotchas)

                _canvasGo.SetActive(true);
                Refresh();
                if (!_occlusionAcquired) { CameraSceneOcclusionGate.Acquire(); _occlusionAcquired = true; }
                _visible = true;
                IsVisible = true;
            }
            else
            {
                if (!_visible) return; // ya oculto -- evita un doble Release
                if (_canvasGo != null) _canvasGo.SetActive(false);
                if (_occlusionAcquired) { CameraSceneOcclusionGate.Release(); _occlusionAcquired = false; }
                _visible = false;
                IsVisible = false;
            }
        }

        private static bool IsLicenseBlocked() =>
            FindFirstObjectByType<LicenseBlockScreenVR>(FindObjectsInactive.Include) != null;

        // ---------------- Textos (localizados, ver docs/localizacion.md) ----------------
        private void Refresh()
        {
            if (_titleText == null) return;
            _titleText.text = L10n.T("focus.title");
            _line1Text.text = L10n.T("focus.line1");
            _line2Text.text = L10n.T("focus.line2");
            _line3Text.text = L10n.T("focus.line3");
            _centerMarkText.text = L10n.T("focus.center_mark");
            ApplyPendingPin();
        }

        // Aplica _pendingPin a la fila del PIN (ver comentario del campo). No-op si el canvas
        // todavia no existe -- BuildCanvas() y Refresh() la vuelven a llamar cuando corresponde,
        // asi que SetPairingPin() nunca pierde un valor por orden de arranque.
        private void ApplyPendingPin()
        {
            if (_pairingPinRow == null) return;
            bool show = !string.IsNullOrEmpty(_pendingPin);
            _pairingPinRow.SetActive(show);
            if (show) _pairingPinText.text = L10n.T("focus.pairing_pin", _pendingPin);
        }

        // ---------------- Construccion del canvas world-space ----------------
        private void BuildCanvas()
        {
            var cam = Camera.main;
            if (cam == null)
            {
                // SIM: atajo deliberado -- sin camara no hay donde anclar el cartel (mismo caso
                // que LicenseBlockScreenVR/UpdatePromptVR); en Main.unity Camera.main siempre
                // existe.
                Debug.LogWarning("[Onboarding] No se encontro Camera.main; no se puede mostrar la pantalla de chequeo de calce.");
                return;
            }

            _canvasGo = new GameObject("FocusCheckCanvas", typeof(RectTransform), typeof(Canvas));
            _canvasGo.transform.SetParent(cam.transform, false);
            // 2.0 m / scale 0.002: misma distancia estereo comoda que LicenseBlockScreenVR (ver
            // docstring de la clase para el porque -- fusion estereo + plano focal del Quest).
            _canvasGo.transform.localPosition = new Vector3(0f, 0f, 2.0f);
            _canvasGo.transform.localRotation = Quaternion.identity;
            _canvasGo.transform.localScale = new Vector3(0.002f, 0.002f, 0.002f);

            var canvas = _canvasGo.GetComponent<Canvas>();
            canvas.renderMode = RenderMode.WorldSpace;
            // Coplanar con LicenseBlockScreenVR (mismo z=2.0m/scale/sizeDelta, ver GEOMETRIA):
            // sin esto, el orden de dibujado entre los dos canvases es arbitrario durante los
            // <=500ms de la ventana del guard anti-coexistencia (revision, MAYOR). La licencia es
            // fail-closed y SIEMPRE debe ganar, asi que este cartel se manda detras a proposito.
            canvas.sortingOrder = -1;
            var rt = _canvasGo.GetComponent<RectTransform>();
            rt.sizeDelta = new Vector2(760, 520);

            var font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");

            var panelGo = new GameObject("Panel", typeof(RectTransform), typeof(CanvasRenderer), typeof(Image));
            panelGo.transform.SetParent(_canvasGo.transform, false);
            Stretch(panelGo.GetComponent<RectTransform>());
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

            // Escalera de tamaño decreciente (ver docstring/handoff): a 2 m con scale 0.002,
            // 1° subtiende ~17.4 px de canvas. La linea 3 (15 px ~ 0.86° de alto de linea,
            // x-height ~0.43°) es el umbral diagnostico -- verificado (revision): sobre un Quest 3
            // (~25 ppd) eso son ~10 px de x-height, sobre un Quest 2 (~20 ppd) ~8.6 px, por encima
            // del umbral de legibilidad con margen razonable. NO tocar este fontSize a ojo: el
            // parametro que varia en la escalera es el TAMAÑO, nunca el contraste (ver el
            // siguiente comentario).
            //
            // Las 3 lineas usan el MISMO color (revision, corregido): esta pantalla mide UNA sola
            // cosa -- si el casco esta bien calzado -- y el unico parametro que puede degradar la
            // legibilidad hacia abajo de la escalera es el tamaño. Si ademas bajara el contraste,
            // una linea 3 no leida seria ambigua (¿fallo el calce, o el contraste? -- el contraste
            // es justamente lo que las LIOs simuladas degradan en el resto de la app, la ambiguedad
            // exacta que esta pantalla existe para eliminar). El titulo queda en blanco puro; la
            // cruz de centrado (mas abajo) SI puede tener un color propio -- no es parte de la
            // escalera, no mide legibilidad.
            var lineColor = new Color(0.92f, 0.92f, 0.92f);
            _titleText = MakeLabel(layoutGo.transform, font, 40, FontStyle.Bold, Color.white);
            _line1Text = MakeLabel(layoutGo.transform, font, 30, FontStyle.Normal, lineColor);
            _line2Text = MakeLabel(layoutGo.transform, font, 22, FontStyle.Normal, lineColor);
            _line3Text = MakeLabel(layoutGo.transform, font, 15, FontStyle.Normal, lineColor);
            // Cruz de centrado: verifica el sweet spot optico de las lentes del visor (no
            // solo la nitidez del texto, tambien que este centrado en el campo visual). Color
            // propio (no forma parte de la escalera de contraste de arriba).
            _centerMarkText = MakeLabel(layoutGo.transform, font, 26, FontStyle.Bold, new Color(0.6f, 0.85f, 0.8f));

            // Fila del PIN de emparejamiento (ver comentario del campo _pendingPin): bloque
            // APARTE de la escalera de tamaños decrecientes de arriba -- ese instrumento de
            // medicion no puede tener una variable mas que el tamaño (ver el comentario de la
            // escalera), y un PIN grande al final la rompería. Fuente propia (~orden del titulo,
            // no la escalera), color claro para que se lea sin esfuerzo (es un dato que el
            // clinico tiene que tipear en la tablet), y un padding superior extra (en vez del
            // "spacing" uniforme del VerticalLayoutGroup) para separarla visualmente de la cruz
            // de centrado. GameObject propio (no solo el Text) para que quede TOTALMENTE
            // inactivo -- y sin reserva de espacio -- cuando no hay PIN que mostrar.
            _pairingPinRow = new GameObject("PairingPinRow", typeof(RectTransform), typeof(VerticalLayoutGroup));
            _pairingPinRow.transform.SetParent(layoutGo.transform, false);
            var pinRowLayout = _pairingPinRow.GetComponent<VerticalLayoutGroup>();
            pinRowLayout.padding = new RectOffset(0, 0, 24, 0);
            pinRowLayout.childAlignment = TextAnchor.MiddleCenter;
            pinRowLayout.childControlWidth = true; pinRowLayout.childControlHeight = true;
            pinRowLayout.childForceExpandWidth = true; pinRowLayout.childForceExpandHeight = false;
            _pairingPinText = MakeLabel(_pairingPinRow.transform, font, 36, FontStyle.Bold, new Color(1f, 0.87f, 0.4f));
            _pairingPinRow.SetActive(false); // ApplyPendingPin() decide el estado real mas abajo

            // Ocultar la escena de fondo a nivel de camara mientras el cartel este visible (ver
            // docstring "OCLUSION DE LA ESCENA"). El Acquire real ocurre en
            // SetVisibleInternal(true), no aca -- BuildCanvas solo arma la jerarquia (se llama
            // una sola vez, el canvas se reusa en toggles siguientes).
            CameraSceneOcclusionGate.ApplyOverlayLayer(_canvasGo);
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
