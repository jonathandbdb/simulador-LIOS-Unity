# Localización (es/en)

## Qué es y por qué

Capa de localización mínima para vender el simulador a clínicas de varios países: el visor
Quest y la tablet Android muestran su UI en español o inglés según el idioma del dispositivo,
con la posibilidad de que el clínico fuerce un idioma desde la tablet (override persistido). El
español es la fuente histórica (`AGENTS.md` documentaba "textos de UI en español" como
convención fija); esta capa reemplaza esa convención por "vía `L10n` (es/en), español como
idioma fuente" sin reescribir ningún flujo de negocio — es una capa de PRESENTACIÓN pura, el
protocolo visor↔tablet (`docs/networking.md`) y el contrato del backend no cambian en absoluto.

**Estado actual: Fases D1, D2 y D3 completas** (infraestructura + tabla + tests + TODA la UI de
visor y tablet; no queda ningún literal de UI sin cablear). Cableado en D1: `TabletSession.cs`,
`ParamMeta.cs`, `LensCardView.cs`, `TabletUiKit.cs` (tablet), `UpdatePromptVR.cs` +
`UpdateManager.cs` (updates), `LicenseBlockScreenVR.cs` + `LicenseManager.cs` (licenciamiento).
Cableado en D2: **todos** los literales visibles de `Assets/Scripts/Runtime/Net/TabletController.cs`
(reemplazo mecánico literal→`L10n.T(...)`, 5 claves nuevas no cubiertas por el mapeo de D1 —
`connect.connecting_to`, `lens.status_no_response`, `lang.change_title`, `lang.change_body`,
`lang.change_confirm`), `L10n.Initialize(LoadLangPref())` en `TabletController.Start()`, y el
toggle de idioma del header + su popup de confirmación (ver `docs/tablet.md` "MainScreen /
Header" y Decisiones "Idioma fijo al arrancar, cambio por reinicio"). Cableado en D3:
`Vision/HudController.cs` (HUD de diagnóstico del visor) con las claves `hud.*` que D1 ya había
reservado — **cero claves nuevas**, y el escenario traducido por id con las mismas
`scenario.<id>` que usa la tablet (ver `docs/vision-optica.md` §HudController). 206 claves por
idioma (arrancó en 199 en D1; D3 no agregó ninguna; +2 en correcciones posteriores —
`kiosk.service_mode_banner`/`kiosk.service_mode_exit`, ver `docs/tablet.md` "Salida de
servicio del kiosco").

## Arquitectura actual

| Archivo | Rol |
|---|---|
| `Assets/Scripts/Runtime/Localization/L10n.cs` | Motor estático (namespace `Simulador.Localization`), sin `MonoBehaviour` — usable desde plain C# (`TabletSession`) además de `MonoBehaviour`s. Resuelve y fija el idioma UNA vez (`Initialize`), y expone `T(key)`/`T(key, args)`/`Has(key)` (D2). |
| `Assets/Scripts/Runtime/Localization/L10nTable.cs` | Las dos tablas (`Dictionary<string,string> Es`, `En`), separadas del motor para no enterrar la lógica bajo el volumen de strings. Namespaces de clave por sistema (`connect.*`, `pin.*`, `reconnect.*`, `main.*`, `lens.*`, `standard.*`, `kiosk.*`, `unpair.*`, `lang.*`, `param.<clave>.label\|hint`, `scenario.*`, `update.*`, `license.*`, `hud.*`, `common.*`). El bloque `// D2: claves pendientes...` (referencia literal→clave para `TabletController.cs`) ya cumplió su función y se borró al cerrar D2 — dejarlo hubiera sido drift. |

```
L10n.Lang (getter)
  │  _lang == null? -> Initialize(null) [auto-init con el idioma del sistema]
  ▼
L10n.Initialize(overrideCode)
  │  overrideCode "es"/"en" válido -> gana
  │  si no -> ResolveFromSystem(Application.systemLanguage)
  ▼
_lang fijo para TODA la sesión (no hay evento de cambio en caliente, ver Decisiones)
  │
  ▼
L10n.T(key) / L10n.T(key, args)
  │  busca en la tabla de _lang -> si falta, busca en Es -> si tampoco, devuelve key + warning (1 vez)
  ▼
texto final mostrado por el widget
```

- **`L10n.Lang`** (`"es"` | `"en"`) — auto-inicializa con el idioma del sistema si nadie llamó
  `Initialize` todavía (así el visor no necesita bootstrap: `UpdatePromptVR`/
  `LicenseBlockScreenVR` funcionan sin depender de un orden de arranque nuevo).
- **`L10n.Initialize(string overrideCode)`** — idempotente (puede volver a llamarse). La tablet
  lo llama en `Start()` (`L10n.Initialize(LoadLangPref())`) con el override de `ui_prefs.cfg`
  ANTES de construir la UI (D2, completo — ver `docs/tablet.md`); el visor nunca llama
  `Initialize` explícito, vive del auto-init de `T()`.
- **`L10n.ResolveFromSystem(SystemLanguage sys)`** — lógica PURA y testeable (`L10nTests.cs`):
  `Spanish` → `"es"`; CUALQUIER otro valor (incluidos portugués, alemán, francés, `Unknown`) →
  `"en"`. Ver Decisiones.
- **`L10n.T(string key)`** — texto en `Lang`; si falta ahí cae a `"es"` (idioma fuente); si
  tampoco existe devuelve la propia `key` y loguea `Debug.LogWarning("[L10n] clave faltante:
  ...")` UNA vez por clave (`HashSet` de claves ya avisadas, para no inundar la consola si un
  widget se repinta seguido).
- **`L10n.T(string key, params object[] args)`** — `string.Format(CultureInfo.InvariantCulture,
  T(key), args)`; un `FormatException` (placeholder mal puesto, args de más/menos) nunca llega a
  la UI: se loguea y se devuelve el texto SIN formatear.
- **`L10n.Keys`** — unión de claves `Es`/`En`, usada por el test de completitud.
- **`L10n.Has(string key)`** (D2, nuevo) — `true` si `key` existe en la tabla `Es` (fuente). Lo usan
  los DOS lados para traducir el escenario **por id**: `TabletController.ScenarioLabel` decide si
  hay traducción antes de reemplazar el `label` crudo que manda el visor
  (`L10n.Has("scenario." + id) ? L10n.T(...) : label`, ver `docs/tablet.md`) y
  `HudController.ScenarioLabel` hace lo mismo contra el id crudo de `ScenarioManager.Current`
  (D3, ver `docs/vision-optica.md`) — sin esto, un escenario nuevo sin entrada todavía en la
  tabla mostraría la propia clave (`"scenario.foo"`) en vez de degradar al label/id.

### ParamMeta: por qué las claves, no el texto, viven en el diccionario estático

`ParamMeta.META` es un `static readonly Dictionary<string, Entry>` inicializado en el
constructor estático del tipo. Si `Entry.Label`/`Entry.Hint` guardaran el texto YA resuelto
(`L10n.T("param.foco_lejos_m.label")` evaluado ahí mismo), ese texto quedaría fijado en el
idioma que estuviera activo la PRIMERA vez que algo tocara el tipo `ParamMeta` — frágil, porque
el orden de inicialización estática de C# no garantiza que eso pase DESPUÉS de que
`TabletController.Start()` haya llamado `L10n.Initialize(override)`. Se decidió que `Entry`
guarde `LabelKey`/`HintKey` (identificadores, no texto — agnósticos de idioma) y que
`ParamMeta.LabelFor`/`HintFor`/`FormatValue` resuelvan `L10n.T(...)` recién en cada llamada
(que ocurre en `RefreshParamsPanel`/`ParamRowView.Create`, muy después del `Start()` de la
tablet). Así el resultado es correcto sin importar cuándo se toca `ParamMeta` por primera vez.

## Decisiones y porqués

- **Idioma fijo al arrancar, sin cambio en caliente** → el clínico no cambia de idioma a mitad
  de una consulta; agregar un evento de cambio en vivo (repintar TODA la UI ya construida)
  hubiera sido complejidad sin caso de uso real (YAGNI, `minimal-footprint`). `Initialize` es
  idempotente por si hace falta re-evaluarlo (p. ej. un futuro toggle en Ajustes que solo aplica
  "al reiniciar la app"), pero nada en la UI actual re-lee `L10n.Lang` después de construirse.
- **Default internacional: idioma del sistema no-español → inglés** (`ResolveFromSystem`) → un
  Quest en portugués, alemán, francés o cualquier otro idioma ve la UI en INGLÉS, no en español.
  Es el default menos sorprendente para vender a clínicas de países no hispanohablantes: un
  clínico brasileño o alemán entiende mejor un simulador en inglés que uno en español que no
  pidió. El override manual (D2, `ui_prefs.cfg`) sigue existiendo para forzar cualquiera de los
  dos si el default no es el correcto para ese dispositivo.
- **Fallback es→key, no en→key** → el español es la fuente histórica del proyecto (todo el
  contenido pre-D1 nació en español); si una clave nueva se agrega sin su traducción al inglés
  todavía, mostrar el español (contenido real, aunque en el idioma equivocado) es menos malo que
  mostrar la clave cruda (`"param.foo.hint"` en pantalla). El test de completitud (`L10nTests.
  Tablas_EsYEnTienenExactamenteLasMismasClaves`) evita que este fallback se vuelva permanente.
- **Motor y tabla en archivos separados** (`L10n.cs` / `L10nTable.cs`) → la tabla es ~200 pares
  clave/valor por idioma; mezclarla con la lógica del motor la haría ilegible. Mismo criterio
  que `ParamMeta`/`LensDef` separando metadata de lógica.
- **`ParamMeta.Entry` guarda claves, no texto** → ver arriba ("por qué las claves").
- **Claves con namespace por sistema** (`connect.*`, `pin.*`, ...) → ver la tabla de prefijos en
  Arquitectura. Facilita ubicar de un vistazo a qué pantalla/sistema pertenece una clave y evita
  colisiones entre sistemas que reusan la misma palabra española con significados DISTINTOS en
  inglés (ver el punto siguiente).
- **"Actualizar" es DOS claves distintas, nunca una** (`main.refresh` = "Refresh"/refrescar
  catálogo vs. `update.button_update` = "Update"/actualizar la app) → son la misma palabra en
  español pero conceptos y traducciones al inglés DISTINTOS; una sola clave compartida hubiera
  forzado elegir una traducción incorrecta para uno de los dos casos.
- **OD/OI → OD/OS en inglés, nunca "OI"** (`common.od`/`common.os`) → "OI" (oculus izquierdo) es
  español; la convención clínica en inglés es OD/OS (*oculus dexter* / *oculus sinister*). Ver
  glosario abajo.
- **Claves reservadas para D2/D3 completas en la tabla desde D1** (decisión que se pagó sola) →
  la Fase D1 pobló `L10nTable` con TODAS las claves que necesitaban `TabletController.cs` (D2) y
  `HudController.cs` (D3), no solo las que esa fase cableaba — así D2 y D3 se limitaron a
  reemplazar el literal por `L10n.T("clave")` siguiendo el mapeo documentado, sin diseñar claves
  nuevas ni tocar el glosario clínico. D3 cerró efectivamente con **cero claves agregadas**.

## Glosario clínico (es → en)

Fuente única para toda clave nueva — no traducir literal, usar estos términos:

| Español | Inglés |
|---|---|
| Consultorio | Exam room |
| Ruta nocturna | Night road |
| Lente intraocular / LIO | Intraocular lens / IOL |
| OD / OI | OD / **OS** (oculus dexter / oculus sinister — nunca "OI") |
| Ambos ojos | Both eyes |
| Ojo a tratar | Eye to treat |
| Ajuste fino | Fine tuning |
| Agudeza (visual) | Visual acuity |
| Desenfoque | Defocus |
| Foco lejano / intermedio / cercano | Far / intermediate / near focus |
| Catarata | Cataract |
| Dispersión (intraocular) | Scatter (straylight) |
| Encandilamiento / deslumbramiento | Glare |
| Velo (glare) | Veiling glare |
| Halos | Halos |
| Astigmatismo, eje, magnitud | Astigmatism, axis, magnitude |
| Emparejar / desvincular | Pair / unpair |
| Visor | Headset |
| Recentrar | Recenter |
| Pantalla completa | Full screen |
| Actualizar (refresco de catálogo) | Refresh |
| Actualizar (update de la app) | Update |
| Propia / Genérica | Custom / Generic |
| Marca `IOLSIMULATOR` | `IOLSIMULATOR` (no se traduce — idéntica en `app.title` es/en, ver `docs/tablet.md` Decisiones) |

**Voz**: instrucciones al clínico en imperativo neutro ("Enter the PIN"), nunca "vos/tú" (el
español del proyecto SÍ usa voseo rioplatense — "Ingresá el PIN" — porque así nació el
proyecto; el inglés no tiene ese registro, se usa un imperativo neutro estándar).

## Qué NO se traduce (y por qué)

- **`changelog` del manifest de updates** (`docs/updates.md`) — texto libre que escribe quien
  publica una versión desde el panel admin; no hay traducción automática de contenido dinámico
  del servidor.
- **`message` del 403 de `/api/verify`** (`docs/licenciamiento.md`) — texto libre que escribe el
  administrador al rechazar/suspender un dispositivo desde el panel; sigue ganando tal cual
  sobre el mensaje genérico de `MessageFor` (sin cambios de esta fase).
- **`sha_mismatch`** (código de error interno entre `UpdateManager` y las dos UIs, ver
  `docs/updates.md`) — es un contrato de string entre capas, no prosa para el usuario; cada UI
  lo traduce a su propio texto amigable (`update.error_sha_mismatch`) ANTES de mostrarlo, el
  código en sí nunca llega a pantalla.
- **Contenido del catálogo de lentes** (`nombre`/`descripcion` de cada `LensDef`,
  `docs/catalogo-lentes.md`) — es contenido clínico que carga el administrador (nombre comercial
  de la lente, descripción a medida); traducirlo automáticamente es un cambio de contrato
  (¿el backend guarda `nombre_es`/`nombre_en`? ¿se traduce en el cliente?) que el usuario dejó
  explícitamente para una fase futura, fuera del alcance de D1.
- **Optotipo ETDRS** (si el escenario de lectura lo usa, ver `docs/vision-optica.md`) — las
  letras Sloan son un estándar oftalmológico universal, no texto de UI.
- **Claves de parámetro SIN entrada en `ParamMeta`** — un parámetro nuevo del catálogo que
  todavía no tiene metadata clínica cae a su clave cruda (`ParamMeta.LabelFor` devuelve el
  parámetro tal cual si no está en `META`, comportamiento preexistente sin cambios); agregar su
  traducción es agregarlo a `ParamMeta.META` + su par `param.<clave>.label/hint` en la tabla.

## Cómo agregar una clave nueva

1. Elegí el namespace correcto (tabla de prefijos en Arquitectura) o uno nuevo si es un sistema
   nuevo.
2. Agregá el par a **ambas** tablas (`L10nTable.Es` y `L10nTable.En`) — nunca solo una; el test
   de completitud (`L10nTests.Tablas_EsYEnTienenExactamenteLasMismasClaves`) falla si se
   olvida una, y el mensaje del assert lista exactamente qué clave falta en qué tabla.
3. Si el texto lleva variables, usá placeholders posicionales (`{0}`, `{1}`) y `L10n.T(key,
   args)` — nunca concatenación manual con `+` (eso rompe el orden de palabras al traducir).
4. Si es una traducción clínica (parámetro, escenario, término óptico), usá el glosario de
   arriba; si el término no está en el glosario, agregalo ahí también para la próxima clave.
5. Corré la suite EditMode (`L10nTests` + el resto) antes de dar la tarea por cerrada.

## Gotchas

- **Orden de inicialización estática y `ParamMeta`** — ver "ParamMeta: por qué las claves, no
  el texto" en Arquitectura. Cualquier otro `static readonly` que guarde metadata de catálogo a
  futuro debe seguir el mismo patrón (guardar la clave, resolver `L10n.T` en el getter/método,
  nunca en el inicializador del campo).
- **`Disconnect(string message = "...")` no puede tener un default de `L10n.T(...)`** — los
  parámetros por defecto de C# exigen una constante de tiempo de compilación;
  `TabletSession.Disconnect` pasó a `string message = null` y resuelve
  `message ?? L10n.T("connect.session_ended")` dentro del método. Cualquier otro método con un
  mensaje localizado como default de parámetro necesita el mismo ajuste.
- **`_warnedMissing` es un `HashSet` estático** — el warning de "clave faltante" sale UNA vez
  por clave por dominio (no por sesión de Play ni por frame); en tests EditMode esto significa
  que dos tests distintos que usen la MISMA clave faltante solo ven el log la primera vez — no
  afecta las aserciones (el valor de retorno de `T()` es siempre el mismo), solo el log.
- **Los tests que prueban fallback/formato agregan claves TEMPORALES a `L10nTable.Es`/`En`**
  (`T_ClaveFaltanteEnEn_CaeAEs`, los de `T(key, args)`) y las borran en un `finally` — si un
  test nuevo sigue el mismo patrón y se corta antes del `finally` (excepción no esperada), deja
  una clave huérfana que rompe el test de completitud de OTRO test en la misma corrida. Mantener
  el `try/finally` siempre.

## Pendientes / deuda

- **Sin selector de idioma en el visor** — el visor solo tiene el default automático
  (`ResolveFromSystem`), no hay UI para forzar un idioma distinto directamente en el Quest (el
  override vive del lado tablet). Si hiciera falta, es agregar un botón más al menú de
  diagnóstico/HUD, fuera del alcance de D1.
- **Catálogo de lentes sin traducir** (nombre/descripción) — ver "Qué NO se traduce"; decisión
  explícita del usuario, fase futura con cambio de contrato backend+Unity.
