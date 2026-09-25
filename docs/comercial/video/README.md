# Video tutorial de uso — IOLSIMULATOR

Video corto (~3:48, español) que enseña **cómo se usa el equipo en la consulta**: pantalla partida,
a la izquierda lo que ve el paciente dentro del visor, a la derecha la tablet del clínico, para que
se vea que lo que se toca de un lado cambia lo que se ve del otro. Lleva **voz y subtítulos**: se
entiende viéndolo sin leer, y también con el sonido apagado.

> **No es el deck.** El deck comercial vende (por qué incorporarlo, qué límites tiene); esto enseña
> a operarlo. **No reciclar texto entre los dos.** Comparten la identidad visual y la leyenda de
> fines educativos, nada más.

| Archivo | Qué es |
|---------|--------|
| `storyboard.es.json` | **La fuente única.** Beats, tiempos, escenario, lente por ojo, curvas de cabeza y de libro, capturas de tablet, toques y subtítulos. **Acá se edita todo.** |
| `build-video.py` | Todas las etapas del pipeline (`plan`, `smoke`, `subs`, `voice`, `retime`, `audio`, `preview`, `compose`). |
| `build/video/` (fuera de esta carpeta) | Toda la salida: frames, plan, `.ass` y el MP4. **Gitignorada** (`.gitignore:17`), como los APK. |

**El MP4 no se commitea**, por la misma razón que los PDF del deck: es un derivado que envejece en
silencio contra su fuente. Se versiona el guion y los scripts, que es lo que permite regenerarlo.

## El pipeline

```
storyboard.es.json
   │
   ├─ plan ──────> build/video/plan.tsv ──> [Unity] TutorialCapture ──> build/video/visor/*.jpg
   │                                                                              │
   ├─ subs ──────> build/video/subs.es.ass ─────────────────────┐                 │
   │                                                            v                 v
   └─ compose ─────────────────────────────> Pillow compone ──> ffmpeg ──> IOLSIMULATOR-tutorial-es.mp4
                                                  ^
                          build/video/tablet/*.png (capturas del panel, Editor clon)
```

### 1. `plan` — el guion aplanado a un estado por frame

```bash
python docs/comercial/video/build-video.py plan
```

Escribe `build/video/plan.tsv`: una fila por frame, con todo ya interpolado.

| Columna | Qué es |
|---|---|
| `frame` | Índice, y también el nombre del `.jpg` que se escribe |
| `render` | `0` en las placas de título y cierre: el arnés avanza el tiempo pero no captura |
| `scenario` | `consultorio` \| `ruta_noche` — **son los dos únicos que existen** |
| `lens_l` / `lens_r` | Id de lente por ojo; si son iguales se aplica con `"both"`, una sola llamada |
| `focus` | Pantalla de chequeo de calce visible |
| `yaw` / `pitch` | Rotación de la cabeza, en grados. `pitch` positivo = mirar hacia abajo |
| `book` | Distancia del libro al ojo, en metros; `-` = no tocarlo (de noche no existe) |
| `booktilt` | Inclinación del libro, en grados |
| `beat` | Solo para leer el TSV con ojos humanos |

**El arnés parsea por NOMBRE de columna**, no por posición: agregar una columna nueva no rompe nada,
y si falta una obligatoria aborta diciendo cuál. `pitch` y `booktilt` son opcionales y su default
`0` reproduce el comportamiento previo a que existieran.

**Por qué un TSV y no el JSON directo**: el arnés de Unity lo lee con `File.ReadAllLines` +
`Split('\t')`. Así no hay que agregarle Newtonsoft al asmdef `Simulador.Editor` ni duplicar la
matemática de interpolación en C#. Toda la lógica de curvas vive en Python, donde se toca fácil —
que es justo lo que permitió calibrar el encuadre del libro sin recompilar.

### Calibrar el encuadre (`BOOK_PARK_M`, `BOOK_TILT_DEG`, `pitch`)

Estos valores **se calibran mirando frames reales**, no razonando. La receta: escribir a mano un
`plan.tsv` con tres o cuatro variantes del mismo estado en frames distintos, correr el arnés (tarda
segundos) y comparar los `.jpg`. Lo que salió de hacerlo:

- `booktilt` **negativo** pone el libro en posición de lectura; positivo lo vuelca al revés y se ve
  el canto. `-35` es el punto justo: `-40` tapaba casi todo el cuadro.
- Sin `pitch` la cabeza mira al frente y la ventana del consultorio se lleva el cuadro. Entre `10` y
  `14` grados de cabeceo el libro queda al centro y la habitación sigue a la vista.
- El ángulo del libro bajo la línea de los ojos es `atan(|y|)` de `BookHoldDirLocal` en
  `TutorialCapture.cs`; el semi-FOV vertical de la cámara es ~30°, así que ese ángulo dividido 30 da
  directamente la altura en el cuadro.

### 2. Captura del visor — `Assets/Scripts/Editor/TutorialCapture.cs`

Menú **`Simulador → Capturar video tutorial`**. Entra en play mode, ejecuta el plan y escribe
`build/video/visor/######.jpg` (~5.900 frames, ~600 MB, unos 4 minutos de corrida).

Es **Editor-only a propósito**: el MonoBehaviour vive en la assembly de Editor y se agrega por
código al entrar en play mode, así no agrega nada a `Simulador.Runtime` ni viaja en ningún APK.

Renderiza por el mismo camino que `Net/StreamingCapture.cs` usa en producción para el stream de la
tablet: cámara auxiliar mono → `RenderTexture` propia. Ese camino ya aplica el post-proceso de
visión, así que los frames salen con la óptica de la lente puesta.

**Probá siempre con el smoke antes de la corrida completa:**

```bash
python docs/comercial/video/build-video.py smoke   # ~10 estados, segundos
# Simulador -> Capturar video tutorial ; revisar build/video/visor/
python docs/comercial/video/build-video.py plan    # restaurar el plan completo
```

#### Las cuatro cosas que el arnés tiene que corregir de la escena

Ninguna es un bug del producto: son consecuencias de correr una app de VR en el Editor, sin casco
ni mandos. Todas se descubrieron mirando frames reales, no leyendo código.

1. **El libro aterriza sobre la cámara.** `ReadingBook` cuelga del `Right Controller`, que sin mando
   se queda en el origen del rig — o sea, pegado a la cara, tapando toda la escena. El arnés lo
   desemparenta y lo ancla a una pose del mundo (`_anchorPos` + `_baseRot`), **nunca a la cámara
   viva**: si se calcula desde `camT` el libro orbita con la mirada y queda siempre centrado, lo que
   arruina justamente el beat que quiere mostrar que el entorno es 3D.
2. **La rotación del libro es la autorada, no un `LookRotation`.** Se guarda
   `_bookAuthoredRot` antes de desemparentarlo. Un `LookRotation` apuntando a la cámara muestra la
   **tapa**; las páginas escritas —lo único que sirve para ver el desenfoque de cerca— solo salen
   con la rotación que le dio el autor de la escena.
3. **La pantalla de calce muestra el PIN de emparejamiento** mientras no haya tablet vinculada, y en
   una corrida de captura nunca la hay. El arnés llama `FocusCheckScreenVR.SetPairingPin(null)`
   **cada frame** en que el calce está visible: `NetworkController` lo vuelve a poner por su cuenta,
   así que un llamado único se pierde.
4. **El `DebugHUD` está prendido** y se cuela en todos los frames. El arnés lo apaga en `Awake`.

### 3. Captura del panel de la tablet — segunda instancia del Editor

Requiere **dos Editors**: uno con `Main.unity` en play (el visor) y otro con `Tablet.unity` en play
(la tablet), que se descubren por UDP en la red local. Es el flujo de prueba de
`docs/networking.md` §Cómo probar.

Se capturan pantallas **fijas**, una por estado que el guion nombra en su campo `tablet`, a
1280×800 (la resolución de referencia del canvas, `TabletController.cs:1250-1253`). Van a
`build/video/tablet/`.

**La app se maneja por código, no a mano.** Los controles no son `UnityEngine.UI.Button` sino
`Simulador.Tablet.TabletButton` (un `Selectable` propio), así que un `onClick.Invoke()` no los
encuentra. El camino que funciona —y que además dispara los estados visuales del botón— es
`OnPointerClick`, buscando por el texto del `TMP_Text` hijo:

```csharp
foreach (var b in Object.FindObjectsByType<Simulador.Tablet.TabletButton>(
             FindObjectsInactive.Exclude, FindObjectsSortMode.None)) {
    var lab = b.GetComponentInChildren<TMPro.TMP_Text>(true);
    if (lab != null && lab.text.Contains("Monofocal"))
        b.OnPointerClick(new PointerEventData(EventSystem.current));
}
```

Después de cada click hay que **bombear frames** (ver gotcha 2 abajo) o el layout no se rehace y
todos los `RectTransform` devuelven el mismo centro.

**Manejar el escenario desde la TABLET, no desde el visor.** Aplicar una lente con
`DataManager.ApplyLens` sí se refleja en el panel (vuelve por `vision_state`), pero cambiar el
escenario con `ScenarioManager.SwitchTo` **no** actualiza el chip de la tablet, y la captura sale
mostrando el escenario equivocado como activo.

**Las coordenadas de los toques se vuelcan, no se estiman.** `TAP_TARGETS` en `build-video.py` sale
de leer los `RectTransform` reales y normalizarlos (con origen arriba-izquierda, como PIL):

```csharp
var c = new Vector3[4]; rt.GetWorldCorners(c);   // canvas overlay: world == pixeles de pantalla
float cx = (c[0].x + c[2].x) / 2f / Screen.width;
float cy = 1f - (c[0].y + c[2].y) / 2f / Screen.height;
```

**El selector de lente tiene dos pasos**: primero la lente, después *«¿A qué ojo se aplica?»*
(Ambos / OD / OI). Ese segundo paso es lo que hace posible la combinación entre ojos, y existe
también en el modo **Standard** — que es el que corre con la licencia del producto, así que es el
modo que el tutorial muestra.

**Por qué fijas y no video**: la tablet corre en tiempo real y el visor se captura en tiempo
virtual; no hay forma de sincronizarlos. Y para un tutorial es mejor: una imagen quieta con un
toque animado encima se lee, una UI moviéndose mientras se leen subtítulos no.

El compositor **reemplaza el stream dentro de la captura por el frame del visor**. No es un truco:
es literalmente lo que la tablet muestra (`MakeStreamView`, `TabletController.cs:2994-3009`). Sin
eso, el lado derecho quedaría congelado contradiciendo al izquierdo.

#### Por qué cada panel se captura DOS veces (`_k` y `_w`)

La barra superior y el carrusel inferior de la app son **semitransparentes**: se ve el escenario a
través de ellos. Un primer intento pegaba el frame del visor solo en una banda "segura" para no
tapar esos controles — y esas zonas quedaban **congeladas** con el escenario del momento de la
captura, con los apliques de luz y el fondo de los botones quietos mientras el resto se movía.

La solución es separar la interfaz del stream con un matte exacto de dos fondos. Cada panel se
captura con el stream forzado a **negro** (`panel_x_k.png`) y a **blanco** (`panel_x_w.png`):

```
sobre negro:  N = a·C            (color de la interfaz, premultiplicado por su alfa)
sobre blanco: W = a·C + (1-a)    (lo mismo, más el fondo que se cuela)
  =>  (1-a) = W - N     y      sobre cualquier fondo F:   N + (1-a)·F
```

Para forzar el fondo plano, en la tablet se apunta el `RawImage` del stream a
`Texture2D.whiteTexture` y se usa su `color`. **Antes hay que deshabilitar `StreamingCapture` en
el visor**: si siguen llegando frames, la app rehace el panel y pisa el override.

#### Gotchas de la captura de tablet (cuestan una tarde cada uno)

- **`QueuePlayerLoopUpdate` no corre frames de forma sincrónica**: encola. Todo lo que se pida
  dentro de un mismo `execute_code` ocurre recién cuando la llamada retorna. Por eso **no se
  pueden encadenar dos capturas en una sola llamada** (la segunda pisa a la primera) y hay que
  alternar: una llamada prepara el estado, la siguiente captura.
- **Sin `Repaint()` del Game View, la captura sale con un frame viejo.** Bombear el player loop no
  alcanza; hay que repintar la ventana:
  `Resources.FindObjectsOfTypeAll(gameViewType)` → `((EditorWindow)w).Repaint()`.
- **El puerto 9090 queda tomado entre sesiones de play.** Si `WebSocketServer.Start` tira
  `SocketException: solo se permite un uso de cada dirección de socket`, `NetworkController.Start`
  aborta **antes** de levantar el beacon y la tablet nunca descubre al visor. Un domain reload no
  lo libera: hay que **cerrar y reabrir el Editor**. Se diagnostica con
  `netstat -ano | grep :9090`.

### 4. Narración (`voice`, `retime`, `audio`)

El video lleva **voz y subtítulos**: se puede ver sin leer, o con el sonido apagado.

```bash
python docs/comercial/video/build-video.py voice    # un .mp3 por subtítulo (TTS)
python docs/comercial/video/build-video.py retime   # acomoda los tiempos a la voz
python docs/comercial/video/build-video.py plan     # re-aplanar con los tiempos nuevos
#   -> volver a capturar en Unity (los beats cambiaron de duración)
```

- **La voz dice exactamente el subtítulo.** Una sola fuente de texto, imposible que se
  desincronicen.
- **Voz `es-UY-MateoNeural`** (Microsoft Edge TTS). Es uruguaya: el guion está en voseo
  rioplatense y una voz `es-ES` lo lee con la prosodia equivocada. La alternativa 100 % local
  (Microsoft Helena, SAPI) es `es-ES` y suena claramente sintética. **Requiere internet**: el
  texto del guion se envía al servicio de Microsoft. Cambiar de voz es una constante
  (`VOICE`) y re-encodear, sin volver a capturar.
- **`retime` acomoda el guion a la voz, no al revés.** Los subtítulos se leen más rápido de lo
  que se habla, así que los tiempos originales quedaban cortos. En vez de acelerar la locución
  (suena mal), empuja los subtítulos dentro de su beat y, si no alcanza, alarga el beat. Por eso
  **después de `retime` hay que volver a capturar**: los visuales siguen al beat.
- **`audio` ensambla una sola pista** colocando cada clip en su instante, escribiendo bytes PCM
  en su offset. Es más simple y legible que un `amix` de 50 entradas en ffmpeg.

### 5. `compose` — composición y encode

```bash
python docs/comercial/video/build-video.py compose
```

Pillow arma cada frame 1920×1080 y se lo pasa a ffmpeg **por stdin como rawvideo**: no escribe los
~6.500 frames compuestos a disco (serían varios GB que nadie vuelve a mirar). ffmpeg quema los
subtítulos y encodea H.264.

Para revisar sin encodear todo:

```bash
python docs/comercial/video/build-video.py preview            # un frame por beat
python docs/comercial/video/build-video.py preview 3400 5800  # frames puntuales
```

## Gotchas del entorno

1. **El puerto MCP del Editor cambia al entrar en play mode.** El clon saltó de 7891 a 7892 en un
   domain reload. Una llamada dirigida al puerto viejo **se cuelga para siempre**, no falla.
   Correr `unity_list_instances` después de cada cambio de play mode, nunca cachear el puerto.
2. **El Game View no repinta si el Editor no tiene foco**, así que `unity_screenshot_game` queda
   esperando un frame que nunca llega. Se destraba sin tocar el mouse:

   ```csharp
   for (int i = 0; i < 12; i++) {
       UnityEditor.EditorApplication.QueuePlayerLoopUpdate();
       foreach (var w in Resources.FindObjectsOfTypeAll(gameViewType))
           ((UnityEditor.EditorWindow)w).Repaint();
   }
   ```

   Por eso el arnés del visor **no depende del Game View**: renderiza a su propia `RenderTexture` y
   pone `Application.runInBackground = true`.
3. **El tamaño del Game View se fija por reflexión** (`UnityEditor.GameViewSizes`), agregando una
   medida `Tutorial1280x800` al grupo Android. Sin eso el panel de la tablet sale con la proporción
   de la ventana del Editor (salió 1702×796) en vez de la del dispositivo.

## Regenerar el video completo

```bash
python docs/comercial/video/build-video.py voice     # 1. narración (solo las líneas que cambiaron)
python docs/comercial/video/build-video.py retime    # 2. acomodar los tiempos a la voz
python docs/comercial/video/build-video.py plan      # 3. guion -> plan.tsv
# 4. En Unity: Simulador -> Capturar video tutorial   (deja correr; escribe build/video/visor/)
# 5. Capturar los paneles de tablet a build/video/tablet/ (ver arriba) si cambiaron
python docs/comercial/video/build-video.py compose   # 6. compone, quema subtítulos, mezcla y encodea
```

Si solo cambió un **texto**, alcanza con `voice` + `retime` + `plan` + volver a capturar. Si no
cambió ningún texto ni ningún tiempo (por ejemplo, solo se retocó el layout del compositor),
`compose` solo recompone sobre los frames que ya están en `build/video/visor/`.

## Defectos del producto que salieron de hacer el video

Mirar 6.000 frames seguidos de la escena encuentra cosas que el uso normal no:

- **La pantalla de la smart TV del consultorio se veía rota** (panel azul con rayas verticales en
  vez del pronóstico). El mesh `SmartTV` trae DOS submeshes con materiales `TvBody` y `TvScreen`;
  ese segundo submesh mapea la textura del clima a una franja y queda casi coplanar con el quad
  hijo `SmartTV/Screen`, que es el que la muestra bien. Arreglado poniendo `TvBody` en los dos
  slots del renderer del FBX: el submesh malo pasa a ser plástico oscuro detrás del quad, y el
  chasis se conserva. **Pasaba también en el Quest**, no solo en el video.
- **`WebSocketServer` filtra el `TcpListener` entre sesiones de play** (ver gotchas arriba). En el
  Editor obliga a reiniciarlo; en el dispositivo no se nota porque el proceso muere, pero es un
  dispose que falta.

## Decisiones

- **Nada se graba en vivo.** Todo se renderiza determinísticamente y se compone después. Es lo
  único que permite corregir un subtítulo sin volver a filmar, y lo que hace repetible una toma:
  el arnés fija `Time.captureFramerate` (reloj virtual) y `Random.InitState` (el tráfico nocturno
  usa `Random.Range` sin semilla, así que sin eso dos corridas no coinciden).
- **Subtítulos quemados, vía `.ass` y libass, no dibujados con Pillow.** Se corrige una línea y se
  re-encodea en un minuto sin recomponer 6.500 frames, y la tipografía sale con contorno decente.
- **El video lleva una pista de audio silenciosa.** Hay reproductores y plataformas que se portan
  mal con un MP4 sin audio.
- **La ficha "En este momento"** (escenario y lente por ojo, abajo a la derecha) existe porque sin
  ella quien mira pierde de vista cuál lente está puesta justo cuando aparece el efecto que las
  distingue.
- **Un solo idioma por ahora (español).** El material visual se renderiza una sola vez. Para otro
  idioma hay que traducir los subtítulos, regenerar la voz con una `VOICE` de ese idioma y correr
  `retime` (los tiempos cambian con la locución), así que sí hay que volver a capturar — pero el
  guion, el encuadre y la calibración se reusan enteros.
