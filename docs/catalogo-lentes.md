# Catálogo y motor de lentes (capa de datos)

## Qué es y por qué

Fuente de verdad del catálogo de lentes intraoculares y del estado binocular del simulador.
`DataManager` (singleton auto-bootstrapeado) carga el catálogo (cache → StreamingAssets → sync
backend), resuelve el estado por ojo (defaults del catálogo + overrides de la tablet) y lo emite
por eventos a la capa de visión. La lógica de negocio (parseo, merge, construcción de estado,
limpieza de overrides) está separada en clases PURAS testeables en EditMode. Es un port de
`autoloads/data_manager.gd` del proyecto Godot original.

## Arquitectura actual

```
StreamingAssets/config.json ─┐ (opcional: backend_url) ─┐
StreamingAssets/lentes.json ─┤ (defaults embebidos, base del merge)  │
persistentDataPath/lentes.json ─┤ cache (última copia buena del backend) │
GET {backendUrl}/api/lenses ─┘ sync en background (no bloquea) ◄────────┘
        │
        ▼
  CatalogParser.Parse / MergeMissingParams   (lógica pura)
        │
        ▼
  DataManager (singleton, DontDestroyOnLoad)
   ├─ Catalog / CatalogSource / LastSyncTime
   ├─ Left / Right : EyeState        ◄─ LensEngine.BuildEyeState (lógica pura)
   ├─ _lensOverrides ⇄ persistentDataPath/lens_overrides.json (debounce 1 s)
   └─ eventos ──► VisionParamsBinder / GlareController / DisabilityGlareController / Net
```

- `Assets/Scripts/Runtime/Data/DataManager.cs` — MonoBehaviour singleton. Se auto-crea antes de
  cargar la escena (`[RuntimeInitializeOnLoadMethod(BeforeSceneLoad)]`). Orquesta carga, sync,
  aplicación de lentes (`ApplyLens(lensId, eye)` con eye `"left" | "right" | "both"`), overrides
  en vivo (`OverrideParams`) y su persistencia con debounce. Config backend (P2.4): el
  `[SerializeField] backendUrl = "http://192.168.88.198:8080"` es el default legacy (en la
  práctica inconfigurable en el Inspector: `DataManager` se crea por código, no hay instancia en
  la escena) — `LoadBackendConfig()` intenta sobreescribirlo con
  `Assets/StreamingAssets/config.json` opcional (`{"backend_url": "..."}`) ANTES del sync;
  archivo ausente o inválido → se mantiene el default. Endpoint `/api/lenses`, timeout 5 s.
- `Assets/StreamingAssets/config.json` — config opcional del visor (P2.4). Único campo soportado:
  `backend_url`. Se lee con el MISMO mecanismo (`LoadStreamingText` / `UnityWebRequest`) con que
  ya se lee `lentes.json` de `StreamingAssets` en Android (`jar://`). El archivo del repo trae la
  IP LAN de desarrollo actual (`http://192.168.88.198:8080`) — cambiarlo ahí para apuntar a otro
  backend sin recompilar.
- `Assets/Scripts/Runtime/Data/CatalogModel.cs` — POCOs Newtonsoft: `ParamSpec {default,min,max}`,
  `LensDef {id,nombre,descripcion,params}` (params como `Dictionary<string,ParamSpec>` para
  tolerar claves nuevas del backend sin recompilar), `LensCatalog {version,catalogo}` (sin
  inicializador en `catalogo`: si el JSON no trae la clave queda null ⇒ inválido) y
  `EyeState {LensId, Params}` con `Clone()` y `ToFlatDict()` (aplana a dict estilo Godot para
  red/JSON, agregando `lens_id`).
- `Assets/Scripts/Runtime/Data/CatalogParser.cs` — lógica pura: `Parse(json)` (null si es
  inválido o sin lista `catalogo`), `MergeMissingParams(target, defaults)` (completa params
  FALTANTES por lente desde los defaults embebidos; nunca pisa valores existentes; devuelve
  cuántos agregó) y `CountLenses`.
- `Assets/Scripts/Runtime/Data/LensEngine.cs` — lógica pura: `BuildEyeState(lens, savedOverrides)`
  (defaults del catálogo + overrides encima, clampeados con `ClampToSpec`), `ClampToSpec(key,
  value, specs)` (clamp a `[min, max]` del `ParamSpec` de esa clave; sin spec conocido o sin
  rango válido pasa sin clamp), `ComputeBlend(leftId, rightId)` (true si ambos ojos tienen lente
  y son distintas; solo informativo) y `CleanOverrides(saved, new, catalogParams, epsilon=0.0005)`
  (si un valor vuelve al default dentro de epsilon el override se ELIMINA; ignora la clave
  `lens_id`).
- `Assets/Scripts/Runtime/Data/DataManagerLogic.cs` (P6.5) — lógica pura extraída de
  `DataManager` para poder testearla sin corrutinas/IO: `BuildSyncUrl(backendUrl, endpoint)`
  (concatena normalizando la barra, evita `"//"` si `backendUrl` trae trailing slash) y
  `SerializeLensOverrides`/`TryParseLensOverrides` (round-trip de `lens_overrides.json`; el parseo
  nunca tira excepción, devuelve `false` ante JSON inválido/vacío/nulo). `DataManager` llama a
  estas mismas funciones — no hay una reimplementación paralela para los tests.
- `Assets/StreamingAssets/lentes.json` — catálogo embebido en el build (v `0.5.0-clinical`,
  3 lentes: `monofocal`, `panoptix`, `vivity`).
- `Assets/Tests/EditMode/DataLogicTests.cs` — tests NUnit EditMode de `CatalogParser`/`LensEngine`
  + un test de integración sobre el JSON real.
- `Assets/Tests/EditMode/DataManagerLogicTests.cs` (P6.5, nuevo) — tests de `DataManagerLogic`
  (URL de sync + round-trip de overrides). Ver "Límite de cobertura" más abajo para lo que
  queda deliberadamente FUERA de esta suite.

### Schema del JSON (contrato con el backend)

```json
{
  "version": "0.5.0-clinical",           // string
  "catalogo": [                           // lista ordenada (define el orden de ciclado A/B)
    {
      "id": "monofocal",                 // string, clave única
      "nombre": "Monofocal Estandar",    // string, para UI
      "descripcion": "...",              // string, para UI/tablet
      "params": {                         // clave → { "default": f, "min": f, "max": f }
        "foco_lejos_m":       { "default": 6.0,  "min": 0.0, "max": 20.0 },
        ...
      }
    }
  ]
}
```

Params clínicos actuales (13 por lente en `Assets/StreamingAssets/lentes.json`):

| Clave | Unidad / rango | Significado |
|---|---|---|
| `foco_lejos_m` | m, 3–9 (0 ⇒ foco no usado, ver P6.9 abajo) | distancia del foco lejano |
| `foco_intermedio_m` | m, 1–3 (0 ⇒ sin foco intermedio) | foco intermedio (1.0 panoptix/vivity, ver P6.9) |
| `foco_cerca_m` | m, 0.15–1 (0 ⇒ sin foco cercano) | foco cercano (0.4 panoptix) |
| `profundidad_foco_m` | m, 0.1–5 | ancho de zona nítida (el shader la mapea ×0.5 a D) |
| `desenfoque_max` | 0–1 | blur máximo fuera de foco |
| `halo_intensity` | 0–1 | glow + anillos difractivos |
| `halo_extra_rings` | 0–1 | prominencia de anillos (pupila del billboard) |
| `contrast_loss` | 0–0.6 | compresión de contraste |
| `destello_intensity` | 0–1 | intensidad del starburst |
| `destello_rayos` | 0–16 (cantidad) | número de rayos del starburst |
| `straylight` | 0–1 | escala del velo de disability glare por ojo |
| `astig_magnitude` | 0–1 (default 0) | magnitud normalizada del astigmatismo residual (misma escala del shader) |
| `astig_axis_deg` | grados, 0–180 (default 0) | eje del astigmatismo (notación oftálmica; C# lo pasa a radianes) |

> **Astigmatismo residual (P4.4, v0.5.0):** default **0 en las 3 lentes** — una LIO no genera
> astigmatismo per se; el knob representa el astigmatismo residual del PACIENTE (córneal/quirúrgico)
> que el clínico dializa con sliders. Alimenta el pipeline per-eye ya existente vía `GlareController`
> (grados→radianes → globals `glare_astig_l/r`, `glare_astig_angle_l/r`). Detalle óptico y
> precedencia catálogo vs comando live `set_astigmatism` en `docs/vision-optica.md`.

> **Rangos clínicos de los 3 focos (P6.9, v0.5.1):** antes los tres focos compartían el mismo
> rango ancho `min:0.0, max:20.0` (no discriminaba ventana clínica, dificultaba el ajuste fino
> desde la tablet — el slider de 20 m de rango apenas resolvía diferencias de centímetros). Ahora
> cada foco tiene su propia ventana: `foco_cerca_m` **0.15–1.0** m, `foco_intermedio_m`
> **1.0–3.0** m, `foco_lejos_m` **3.0–9.0** m. **Semántica "0 = foco desactivado" intacta**: el
> `default` de un foco apagado (p. ej. `foco_intermedio_m`/`foco_cerca_m` en `monofocal`,
> `foco_cerca_m` en `vivity`) se dejó en **0 aunque quede fuera del nuevo `min`** — no rompe nada
> porque `LensEngine.BuildEyeState` (`state.Params[kv.Key] = kv.Value.Default`) **nunca clampea el
> default del catálogo**, solo clampea `savedOverrides`/overrides en vivo vía `ClampToSpec`.
> Verificado además que `ParamRowView.Create` (tablet) fija `minValue`/`maxValue` del `Slider`
> ANTES de registrar el listener `onValueChanged.AddListener(...)` — el clamp interno de Unity al
> asignar `minValue`/`maxValue` (que puede disparar `Set()` con `sendCallback:true` si el valor
> previo del slider queda fuera del rango nuevo) ocurre sin listener conectado todavía, y el valor
> inicial se fija después con `SetValueWithoutNotify`: abrir "Ajuste fino" en una lente con un foco
> en 0 **nunca manda un `override_params` espurio** que lo active. Efecto secundario cosmético
> aceptado: el handle del slider se dibuja en el extremo izquierdo (posición del `min`, p. ej.
> 0.15) mientras el label sigue mostrando "off" (`ParamMeta.FormatValue` formatea el valor crudo,
> no el clampeado del widget) — no hay ambigüedad real porque el texto es la fuente de verdad.
> **Gotcha nuevo, irreversible desde la tablet:** con `min > 0`, una vez que el clínico mueve el
> slider de un foco que estaba en "off", **ya no hay forma de volver a 0 desde la tablet** (el
> slider no baja de su `min`). Para volver a "off" hay que reaplicar la lente (`apply_lens`,
> restaura los defaults del catálogo) o el botón "Restaurar valores" de la card.
> **Defaults activos que quedaron por debajo del nuevo `min` se llevaron al borde más cercano**
> (no son "off", son valores clínicos reales que el nuevo rango dejó afuera):
> `panoptix.foco_intermedio_m` 0.6→**1.0** y `vivity.foco_intermedio_m` 0.67→**1.0**. Discrepancia
> deliberada, no un bug: las descripciones de esas lentes (`descripcion` en `lentes.json`) siguen
> mencionando "intermedio 60cm" (panoptix) y "~67 cm" (vivity) — el texto NO se tocó en esta tarea
> (fuera de alcance de un cambio de rangos) y ahora no coincide con el `default` numérico real.
> Iteración experimental explícita del usuario ("luego veremos si con estas modificaciones logro
> regular cada foco") — revisar en una tarea futura si conviene bajar `foco_intermedio_m.min` a
> ~0.5 para no perder esos valores reales, o actualizar el texto descriptivo.
> **Versión bumpeada `0.5.0-clinical` → `0.5.1-clinical`** en AMBOS archivos
> (`Assets/StreamingAssets/lentes.json` y `defaults/lentes.json` — este último vive en la RAÍZ del
> repo, no en `backend/defaults/`, ver `docs/backend.md`), mismo mecanismo que cada cambio clínico
> anterior: solo un bump de versión dispara la re-promoción del seed en un backend que ya corrió
> (ver `docs/backend.md` §Seed del catálogo) — cambiar solo los valores sin tocar la versión habría
> dejado un backend ya seedeado silenciosamente con los rangos viejos.

### Orden de carga (`InitializeAsync`)

1. `LoadLensOverrides()` desde `persistentDataPath/lens_overrides.json` (corrupto ⇒ se ignora).
2. Defaults embebidos: `StreamingAssets/lentes.json` por UnityWebRequest (en Android vive dentro
   del APK, `jar://`; en desktop se antepone `file://`). Se parsean y guardan para el merge.
3. **(P2.4)** `LoadBackendConfig()`: intenta `StreamingAssets/config.json` con el mismo mecanismo;
   si existe y trae `backend_url` no vacío, sobreescribe `backendUrl` (log `DataManager: backendUrl
   desde config.json -> ...`). Archivo ausente/inválido ⇒ se mantiene el default serializado, sin
   loguear error (solo un warning si el archivo existe pero no parsea).
4. Cache: `persistentDataPath/lentes.json` si existe y parsea → `MergeMissingParams` con defaults
   → `CatalogLoaded(version, "cache", count)`. Si no, defaults → `CatalogLoaded(..., "defaults", ...)`.
5. Sync en background (no bloquea el arranque): `GET {backendUrl}/api/lenses` (con el `backendUrl`
   ya resuelto en el paso 3), timeout 5 s. Éxito (200 + JSON válido) ⇒ guarda el texto crudo en
   cache, merge con defaults, reemplaza el catálogo, `CatalogLoaded(..., "backend", ...)` +
   `CatalogSyncedWithBackend(version)`. Cualquier fallo (inalcanzable, no-200, JSON inválido,
   excepción síncrona por cleartext bloqueado) ⇒ `CatalogSyncFailed(mensaje)` y se sigue con el
   catálogo local.

### Eventos de DataManager

- `CatalogLoaded(string version, string source, int lensCount)` — source: `"cache" | "defaults" | "backend"`.
- `CatalogSyncedWithBackend(string version)` / `CatalogSyncFailed(string message)`.
- `VisionStateChanged(string eye, EyeState state)` — eye `"left" | "right"`; se emite por ojo en
  `ApplyLens` y `OverrideParams`. Es el evento que consume toda la capa de visión.

### Estado binocular y overrides

`Left` / `Right` son `EyeState` independientes (`ApplyLens` clona el estado construido para cada
ojo). `BlendModeEnabled` se recalcula tras cada `ApplyLens`. `OverrideParams(dict, eye)` pisa
params del estado vivo, re-emite `VisionStateChanged` y persiste por lente (`CleanOverrides`
mantiene el archivo mínimo: solo lo que difiere del default). Guardado con debounce de 1 s
(los sliders de la tablet emiten muchos cambios por segundo) + flush en `OnApplicationPause(true)`
y `OnApplicationQuit` (en Quest/Android la app puede morir al perder foco).

## Decisiones y porqués

- Lógica pura (`CatalogParser`, `LensEngine`, `DataManagerLogic`) separada del MonoBehaviour →
  testeable en EditMode sin escena ni IO.
- **Límite de cobertura de tests (P6.5)** → `DataManager` es un orquestador STATEFUL con
  corrutinas + `UnityWebRequest` + debounce por `Coroutine`/`WaitForSeconds`; testearlo de verdad
  exigiría una interfaz de IO/red inyectable y mocks pesados (fake `UnityWebRequest`, fake reloj
  para el debounce) — redisñar `DataManager` solo para poder mockearlo no es minimal-footprint
  para lo que aporta. Estrategia elegida: extraer a `DataManagerLogic` (P6.5, arriba) SOLO lo que
  sale barato y es genuinamente lógica pura (sin cambiar el control de flujo de `DataManager`, que
  sigue llamando a esas mismas funciones) — el resto (la cadena defaults→cache→backend en sí, la
  aplicación del debounce, los eventos disparándose en el momento correcto) sigue validándose por
  play mode (ver Cómo probar) y no tiene tests unitarios. Esto es una decisión de costo/beneficio,
  no un descuido: no hay plan de agregar mocks de IO a este proyecto.
- `params` como Dictionary dinámico → el backend puede agregar params nuevos sin recompilar la app.
- `MergeMissingParams` con los defaults embebidos → un catálogo viejo (cache o backend sin migrar)
  sin claves nuevas (p.ej. `destello_*`, `straylight`) no deja efectos apagados que el shader soporta.
- Sync no bloqueante con degradación cache → defaults → el simulador arranca sin red/backend.
- Se cachea el TEXTO crudo del backend (no el objeto mergeado) → la cache refleja fielmente lo que
  devolvió el backend; el merge se re-aplica en cada carga.
- Overrides por lente que se eliminan al volver al default (epsilon 0.0005) → el "reset" de la
  tablet limpia de verdad y el archivo queda mínimo.
- Bootstrap por `RuntimeInitializeOnLoadMethod` → no hace falta colocar DataManager en la escena.

## Gotchas

- **El schema es un CONTRATO COMPARTIDO con el backend**: `backend/api/app/routers.py` sirve
  `GET /api/lenses` y `defaults/lentes.json` es el catálogo semilla del backend. Cambiar claves,
  tipos o rangos exige tocar AMBOS lados (Unity + backend); `CatalogModel.cs` lo advierte
  ("NO cambiar claves ni rangos").
- **`LensEngine`, `CatalogParser` y `DataManagerLogic` son lógica pura testeable: tocarlas ⇒
  extender `Assets/Tests/EditMode/DataLogicTests.cs` (los dos primeros) o
  `DataManagerLogicTests.cs` (el tercero)** en el mismo cambio.
- **`backendUrl` es configurable vía `StreamingAssets/config.json`, default legacy la IP LAN**
  (`http://192.168.88.198:8080`, P2.4): cambiar de backend ya no exige recompilar, solo editar
  ese JSON (o generar builds con configs distintas). Sigue siendo HTTP cleartext: en Android puede
  lanzar excepción síncrona al iniciar el request (está atrapada y degrada a `CatalogSyncFailed`),
  y requiere permitir cleartext o cambiar a HTTPS para producción — eso no cambió.
- **`config.json` se lee ANTES de la cache/defaults pero el log de la URL efectiva puede
  confundirse con la del sync**: si el archivo existe, el log `backendUrl desde config.json -> ...`
  sale primero; el log de sync (`sync con backend -> {url}/api/lenses`) sale después y ya usa esa
  URL — son dos logs distintos, no una contradicción.
- **`min`/`max` de `ParamSpec` SI se aplican en runtime** (defensa en profundidad aunque el canal
  tiene auth por PIN, P1.1: un cliente ya autenticado igual podria inyectar `override_params`
  fuera de rango — bug, version distinta, etc.). El clamp
  vive en `LensEngine.ClampToSpec(key, value, specs)` (logica pura, testeable) y se invoca en
  DOS puntos: `LensEngine.BuildEyeState` (overrides guardados en `lens_overrides.json`, por si el
  archivo esta corrupto o editado a mano) y `DataManager.OverrideParams` (overrides en vivo desde
  la tablet/WS, el vector real). Reglas del clamp: si la clave no tiene `ParamSpec` conocido
  (param nuevo/desconocido) pasa SIN clamp, igual que antes; si el spec no define un rango valido
  (`max <= min` — pasa cuando `min`/`max` faltan en el JSON, ya que son `float` no-nullable en
  `CatalogModel.cs` y Newtonsoft los deja en `0,0`) tambien pasa sin clamp, para no aplastar el
  valor a cero. La UI de la tablet sigue siendo la primera linea de defensa (UX); esto es la
  segunda linea, en el servidor.
- **Una cache corrupta no se borra**: si `persistentDataPath/lentes.json` no parsea se ignora en
  cada arranque (warning), pero el archivo queda hasta que un sync exitoso lo sobrescriba.
- **`ApplyLens` con id inexistente solo loguea warning** y no toca el estado: el ciclado de
  `SimuladorInput` depende de que los ids del catálogo sean estables.

## Cómo probar

1. Editor: Window → General → Test Runner → EditMode → correr `Simulador.Tests.EditMode`
   (**19 tests, todos verdes**): `DataLogicTests` (13 — parseo válido/inválido, merge sin pisar
   existentes, defaults + overrides con clamp, blend, limpieza de overrides e integración contra
   el JSON real `0.5.0-clinical`/13 params por lente) + `DataManagerLogicTests` (6, P6.5 — armado
   de URL de sync con/sin trailing slash, round-trip de `lens_overrides.json` con JSON válido e
   inválido). Sin ventana de Test Runner: `Simulador → Run EditMode Tests`
   (`Assets/Scripts/Editor/EditModeTestRunner.cs`) loguea el resumen `passed/failed/skipped` + el
   detalle de cada falla a la consola — útil para verificar desde MCP (`unity_execute_menu_item` +
   `unity_console_log`) sin abrir la ventana.
2. Play mode: en consola debe aparecer `DataManager: catalogo vX cargado desde defaults|cache (3
   lentes)` y luego `sync con backend -> http://192.168.88.198:8080/api/lenses` (con el backend
   apagado, el fallo de sync es esperado y no bloquea).
3. Backend local: levantar `backend/docker-compose.yml`, editar `Assets/StreamingAssets/config.json`
   con la IP de ese backend (o apuntar `backendUrl` directo si se prefiere sin el config) y
   verificar en consola `DataManager: backendUrl desde config.json -> ...` seguido de
   `catalogo vX sincronizado desde backend` + que se escribió `persistentDataPath/lentes.json`.
4. Overrides: llamar `DataManager.Instance.OverrideParams(...)` (o mover sliders desde la tablet),
   esperar 1 s y verificar `lens_overrides.json` en `persistentDataPath`; volver el valor al
   default y comprobar que la clave desaparece del archivo.
5. **`config.json` (P2.4):** con el archivo presente (default del repo:
   `http://192.168.88.198:8080`), Play mode debe loguear `DataManager: backendUrl desde
   config.json -> http://192.168.88.198:8080` antes del log de sync. Borrar/renombrar el archivo
   temporalmente y volver a Play → no debe aparecer ese log ni ningún error, y el sync debe seguir
   usando el default serializado (mismo comportamiento que antes de esta tarea).

## Pendientes / deuda

- ~~Contrato compartido: `astig_magnitude`/`astig_axis_deg` aún no en `defaults/lentes.json`~~ —
  **resuelto**: ambos archivos ya traen esos 2 params (verificado al tocar P6.9). `MergeMissingParams`
  sigue siendo la red de seguridad si algún catálogo viejo (cache/backend sin migrar) no los trae.
- **(P6.9) `_KNOWN_SEED_VERSIONS` (`backend/api/app/seed.py`) sigue listando hasta `0.5.0-clinical`,
  no `0.5.1-clinical`** — la re-promoción de ESTE cambio (rangos de foco) funciona igual en un
  backend que ya corrió, porque el chequeo mira la versión VIEJA activa en BD (`0.5.0-clinical`,
  que sí está en la lista) para decidir si puede pisarla. Pero si se agrega `0.5.1-clinical` al
  set, cualquier bump FUTURO (`0.5.2-...`) va a chequear que `0.5.1-clinical` esté en la lista para
  auto-promoverse — si no se agrega ahora, ese futuro bump se va a frenar creyendo que el admin
  editó el catálogo a mano. Pendiente para @backend-dev: agregar `"0.5.1-clinical"` al set (1 línea,
  fuera del alcance de @unity-dev/`Simulador.Runtime`).
- **(P6.9) Descripción clínica de panoptix/vivity desactualizada tras clamear sus defaults de
  `foco_intermedio_m`** (0.6→1.0 y 0.67→1.0, ver nota arriba): el texto sigue diciendo "60cm"/"~67
  cm". Evaluar si conviene bajar `foco_intermedio_m.min` en una iteración futura (el usuario ya
  anticipó que esto es experimental) o actualizar el texto para que coincida con el nuevo default.
- **(P6.5) `DataManager` en sí sigue sin tests unitarios** (ver "Límite de cobertura" en
  Decisiones): la cadena defaults→cache→backend, el debounce de guardado y los eventos solo se
  validan por play mode. Aceptado como límite deliberado, no como deuda a resolver — solo
  reconsiderar si el proyecto adopta una capa de IO inyectable/mockeable más en general.
