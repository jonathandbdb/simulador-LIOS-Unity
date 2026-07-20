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
StreamingAssets/config.json ─┐ (default de produccion: backend_url)   │
persistentDataPath/config.json ─┤ (override opcional, dev, vía adb) ──┤ ResolveBackendUrl
[SerializeField] backendUrl ─┘ (fallback de ultima instancia)  ───────┘ (override > streaming > default)
        │
StreamingAssets/lentes.json ─┐ (defaults embebidos, base del merge)  │
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
  en vivo (`OverrideParams`) y su persistencia con debounce. **Config backend por capas** (P2.4 +
  config-layers): el `[SerializeField] backendUrl = "https://vr.conecta.sh"` es el fallback de
  **última instancia** (en la práctica inconfigurable en el Inspector: `DataManager` se crea por
  código, no hay instancia en la escena). `LoadBackendConfig()` resuelve la URL efectiva con
  precedencia **override (`persistentDataPath/config.json`) > streaming
  (`StreamingAssets/config.json`) > default serializado**, vía la función pura
  `DataManagerLogic.ResolveBackendUrl(defaultUrl, streamingJson, overrideJson, out source)`; corre
  ANTES del sync para que ya use la URL ganadora. Endpoint `/api/lenses`, timeout 5 s.
- `Assets/StreamingAssets/config.json` — default de **producción** empaquetado en el build. Único
  campo soportado: `backend_url` (hoy `https://vr.conecta.sh`). Se lee con el MISMO mecanismo
  (`LoadStreamingText` / `UnityWebRequest`) con que ya se lee `lentes.json` de `StreamingAssets` en
  Android (`jar://`).
- `Application.persistentDataPath/config.json` — override **opcional de desarrollo**, incluso schema
  (`{"backend_url": "..."}`), leído con `File.Exists`/`File.ReadAllText` + try/catch (mismo patrón
  que `TryLoadFromCache`). Se sube por `adb` **sin recompilar** el APK — ver gotcha más abajo. Pisa
  al default de `StreamingAssets` si existe y parsea; ausente o inválido → se ignora esa capa y se
  sigue con la siguiente en la precedencia (streaming, y si tampoco, el fallback serializado).
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
- `Assets/Scripts/Runtime/Data/DataManagerLogic.cs` (P6.5 + config-layers) — lógica pura extraída de
  `DataManager` para poder testearla sin corrutinas/IO: `BuildSyncUrl(backendUrl, endpoint)`
  (concatena normalizando la barra, evita `"//"` si `backendUrl` trae trailing slash),
  `SerializeLensOverrides`/`TryParseLensOverrides` (round-trip de `lens_overrides.json`; el parseo
  nunca tira excepción, devuelve `false` ante JSON inválido/vacío/nulo), `ExtractBackendUrl(json)`
  (extrae `backend_url` de un JSON de config; null si vacío/inválido/sin la clave, nunca tira) y
  `ResolveBackendUrl(defaultUrl, streamingJson, overrideJson, out source)` (precedencia
  override > streaming > default; `source` ∈ `"default"|"streaming"|"override"`, para que
  `DataManager` loguee sin duplicar el parseo). `DataManager` llama a estas mismas funciones — no
  hay una reimplementación paralela para los tests.
- `Assets/StreamingAssets/lentes.json` — catálogo embebido en el build (v `0.6.1-clinical`,
  4 lentes: `monofocal`, `panoptix`, `vivity`, `paciente_joven`).
- `Assets/Tests/EditMode/DataLogicTests.cs` — tests NUnit EditMode de `CatalogParser`/`LensEngine`
  + un test de integración sobre el JSON real.
- `Assets/Tests/EditMode/DataManagerLogicTests.cs` (P6.5 + config-layers) — tests de
  `DataManagerLogic` (URL de sync + round-trip de overrides + `ExtractBackendUrl`/
  `ResolveBackendUrl`). Ver "Límite de cobertura" más abajo para lo que queda deliberadamente
  FUERA de esta suite.

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

> **(P8) El orden del array `catalogo` es admin-reordenable desde la tablet** (drag-reorder con
> long-press, ver `docs/tablet.md` §"P8" y el comando `reorder_lenses` en `docs/networking.md`):
> `POST /api/lenses/reorder` persiste una permutación de los ids de catálogo (las lentes
> `"custom"` no participan, siempre quedan después). Como ese orden define el ciclado A/B del
> visor (comentario de arriba), reordenar desde la tablet cambia en qué secuencia el visor cicla
> las lentes con los botones físicos — no solo el orden de la lista en la UI de la tablet.

Params clínicos actuales (13 por lente en `Assets/StreamingAssets/lentes.json`):

| Clave | Unidad / rango | Significado |
|---|---|---|
| `foco_lejos_m` | m, 2–9 (0 ⇒ foco no usado, ver P6.9 abajo) | distancia del foco lejano |
| `foco_intermedio_m` | m, 0–2 (0 ⇒ sin foco intermedio) | foco intermedio (1.0 panoptix/vivity, ver P6.9) |
| `foco_cerca_m` | m, 0–0.6 (0 ⇒ sin foco cercano) | foco cercano (0.4 panoptix) |
| `profundidad_foco_m` | m, 0–4 | ancho de zona nítida (el shader la mapea ×0.5 a D) |
| `desenfoque_max` | 0–2 (>1: la rampa de blur satura antes; el blur final igual se satura a 1 en el shader) | blur máximo fuera de foco |
| `halo_intensity` | 0–1 | glow + anillos difractivos |
| `halo_extra_rings` | **mm de pupila, 1–6** (v0.6.0 — `GlareController` normaliza `(v-1)/5` a 0–1 antes de publicar `glare_pupil_*`; el shader sigue consumiendo 0–1) | diámetro pupilar (prominencia de anillos / pupila del billboard) |
| `contrast_loss` | 0–1 | compresión de contraste |
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

> **Rangos clínicos ampliados + pupila en mm (v0.6.0):** ventanas pedidas por el usuario clínico:
> `foco_lejos_m` **2–9** m, `foco_intermedio_m` **0–2** m, `foco_cerca_m` **0–0.6** m,
> `profundidad_foco_m` **0–4**, `desenfoque_max` **0–2**, `contrast_loss` **0–1**, `straylight`
> **0–1** (sin cambio). `halo_extra_rings` cambia de UNIDAD: ahora es **diámetro pupilar en mm
> (1–6, rango fisiológico)** — defaults remapeados `mm = 1 + old×5` (monofocal 1.0, panoptix 5.0,
> vivity 2.0), visual idéntico al de v0.5.1. La normalización a 0–1 vive en UN solo punto:
> `GlareController.SetEyeGlobals` (`Mathf.Clamp01((v-1)/5)`) antes de publicar `glare_pupil_*` —
> los shaders (`GlareBillboard`, `WindowPortal`) saturan a 0–1 y NO se tocaron. Gotcha de
> migración: un catálogo viejo (0–1) cacheado en el device normaliza a ~0 (pupila mínima) hasta
> que el sync con el backend actualice la cache — autocorregible, no rompe. `ParamMeta` muestra
> la unidad ("mm", F1). El fallback inline de `backend/api/app/seed.py` sigue driftado (rangos
> 0–20 pre-P6.9, solo se usa con DB vacía y sin seed montado) — deuda conocida, no se tocó.
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

> **4ª lente base `paciente_joven` (v0.6.1):** representa la visión neutra (ojo sano, sin LIO
> implantada) para volver a un punto de partida / comparación desde la tablet, sin necesitar
> código de UI nuevo — basta agregarla como lente BASE más al catálogo: aparece sola en el
> ciclado del visor y en la tablet (Pro y Standard). `id: "paciente_joven"`. Todos sus params de
> disfotopsia/blur están en su valor "apagado" (`desenfoque_max`, `halo_intensity`,
> `contrast_loss`, `destello_intensity`, `destello_rayos`, `straylight`, `astig_magnitude` = 0;
> `halo_extra_rings` = 1, el mínimo de pupila) y los 3 focos quedan abiertos (`foco_lejos_m` 6.0,
> `foco_intermedio_m` 1.0, `foco_cerca_m` 0.35) con `profundidad_foco_m` al máximo del rango (4.0)
> para que toda distancia caiga dentro de la zona nítida — visión útil a cualquier distancia, sin
> desenfoque simulado. Con estos valores neutros el gate de `VisionRendererFeature.AddRenderPasses`
> (`Assets/Scripts/Runtime/Vision/VisionRendererFeature.cs:48`, log `[Vision] Post-proceso gate OFF
> (todo en cero: se saltea)`) se comporta igual que sin ninguna lente aplicada: saltea los blits de
> post-proceso, costo de GPU cero. `min`/`max` son los mismos rangos clínicos estándar (v0.6.0) que
> las otras 3 lentes — no se introdujo ningún rango nuevo. Agregada al FINAL del array `catalogo`
> (después de `vivity`) para no alterar el ciclado existente de `SimuladorInput`.
> **Versión bumpeada `0.6.0-clinical` → `0.6.1-clinical`** en AMBOS archivos (mismo mecanismo de
> siempre: dispara la re-promoción del seed en un backend ya corrido). El catálogo pasa de **3 a
> 4 lentes base**. Backend (`backend/api/app/seed.py` y `_KNOWN_SEED_VERSIONS`) actualizado en la
> misma tarea global por @backend-dev — no tocado desde este lado (`Simulador.Runtime`).

### Orden de carga (`InitializeAsync`)

1. `LoadLensOverrides()` desde `persistentDataPath/lens_overrides.json` (corrupto ⇒ se ignora).
2. Defaults embebidos: `StreamingAssets/lentes.json` por UnityWebRequest (en Android vive dentro
   del APK, `jar://`; en desktop se antepone `file://`). Se parsean y guardan para el merge.
3. **(P2.4 + config-layers)** `LoadBackendConfig()`: lee `StreamingAssets/config.json` (mismo
   mecanismo que `lentes.json`) y, si existe, `persistentDataPath/config.json` (`File.Exists` +
   `File.ReadAllText` + try/catch). Resuelve la URL efectiva con
   `DataManagerLogic.ResolveBackendUrl(backendUrl, streamingText, overrideText, out source)`
   (precedencia override > streaming > default) y la asigna a `backendUrl`. Log según quién ganó:
   `DataManager: backendUrl desde override (...) -> ...` o `... desde config.json -> ...`; si
   `source == "default"` no se loguea nada (mismo comportamiento silencioso que antes cuando
   ninguna capa aplica). Cualquier capa presente pero inválida ⇒ warning propio y se ignora esa
   capa (se sigue con la siguiente en la precedencia).
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
- **`backendUrl` se resuelve por capas (config-layers): override (`persistentDataPath/config.json`)
  > streaming (`StreamingAssets/config.json`, default de producción `https://vr.conecta.sh`) >
  `[SerializeField]` (mismo valor, fallback de última instancia).** Cambiar de backend en producción
  no exige recompilar: alcanza con reemplazar `StreamingAssets/config.json` (nuevo build) o subir un
  override por `adb` (sin build nuevo). **Cómo subir el override (modo desarrollo)**: el visor
  (Quest) buildea con `applicationIdentifier` `com.simulador.vr` (`ProjectSettings.asset`;
  `com.simulador.tablet` es el de la tablet, ver `Assets/Scripts/Editor/TabletBuild.cs` — el
  override de backend solo tiene sentido en el visor, la tablet no habla con el backend). En
  Android, `Application.persistentDataPath` resuelve al external files dir de la app
  (`getExternalFilesDir`), es decir:
  ```
  adb push config.json /sdcard/Android/data/com.simulador.vr/files/config.json
  ```
  Si el push falla por permisos (Android 11+ restringe `Android/data/` a otras apps, aunque `adb`
  como shell suele poder escribir ahí): probar `adb shell run-as com.simulador.vr` para confirmar el
  path exacto, o usar `adb push` seguido de `adb shell am force-stop com.simulador.vr` + reabrir la
  app para que `DataManager` relea en el próximo `Awake`. El archivo NO se borra solo — para volver
  a producción hay que `adb shell rm` el override o reinstalar (limpia `persistentDataPath`).
  Si el `backend_url` resuelto sigue siendo HTTP (LAN de desarrollo, no el HTTPS de producción):
  Android puede lanzar excepción síncrona al iniciar el request (atrapada, degrada a
  `CatalogSyncFailed`) y requiere permitir cleartext para esa URL — el HTTPS de producción
  (`vr.conecta.sh`) no tiene ese problema.
- **`config.json` (ambas capas) se lee ANTES de la cache/defaults pero el log de la URL efectiva
  puede confundirse con la del sync**: si alguna capa gana, el log `backendUrl desde override
  (...) -> ...` o `... desde config.json -> ...` sale primero; el log de sync (`sync con backend ->
  {url}/api/lenses`) sale después y ya usa esa URL — son logs distintos, no una contradicción.
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

1. Editor: Window → General → Test Runner → EditMode → correr `Simulador.Tests.EditMode`:
   `DataLogicTests` (13 — parseo válido/inválido, merge sin pisar existentes, defaults + overrides
   con clamp, blend, limpieza de overrides e integración contra el JSON real `0.6.1-clinical`/13
   params por lente, 4 lentes: `monofocal`/`panoptix`/`vivity`/`paciente_joven`) + `DataManagerLogicTests` (**11**, P6.5 + config-layers — armado de URL de
   sync con/sin trailing slash, round-trip de `lens_overrides.json` con JSON válido e inválido,
   `ExtractBackendUrl` con JSON inválido/sin la clave, y `ResolveBackendUrl` con los 4 casos de
   precedencia: solo streaming, streaming+override, override corrupto, ambos vacíos) = **24 tests
   de este documento, todos verdes** (la suite completa de `Simulador.Tests.EditMode` puede reportar
   más si corre junto a `PairingStoreTests`, de networking, fuera de este documento). Sin ventana de
   Test Runner: `Simulador → Run EditMode Tests` (`Assets/Scripts/Editor/EditModeTestRunner.cs`)
   loguea el resumen `passed/failed/skipped` + el detalle de cada falla a la consola — útil para
   verificar desde MCP (`unity_execute_menu_item` + `unity_console_log`) sin abrir la ventana.
2. Play mode: en consola debe aparecer `DataManager: catalogo vX cargado desde defaults|cache (4
   lentes)` y luego `sync con backend -> https://vr.conecta.sh/api/lenses` (si el backend de
   producción no responde desde el entorno de desarrollo, el fallo de sync es esperado y no
   bloquea).
3. Backend local: levantar `backend/docker-compose.yml` y apuntar a él SIN tocar el
   `StreamingAssets/config.json` de producción: escribir un override en
   `persistentDataPath/config.json` con la IP/puerto de ese backend (en Editor, `persistentDataPath`
   es una carpeta de `%APPDATA%`/`~/Library`; ver `Application.persistentDataPath` en consola o
   `Debug.Log` temporal) y verificar en consola `DataManager: backendUrl desde override (...) -> ...`
   seguido de `catalogo vX sincronizado desde backend` + que se escribió
   `persistentDataPath/lentes.json`.
4. Overrides: llamar `DataManager.Instance.OverrideParams(...)` (o mover sliders desde la tablet),
   esperar 1 s y verificar `lens_overrides.json` en `persistentDataPath`; volver el valor al
   default y comprobar que la clave desaparece del archivo.
5. **`config.json` por capas (P2.4 + config-layers):** con SOLO `StreamingAssets/config.json`
   presente (default del repo: `https://vr.conecta.sh`), Play mode debe loguear `DataManager:
   backendUrl desde config.json -> https://vr.conecta.sh` antes del log de sync. Agregando además
   un override en `persistentDataPath/config.json` con otra URL, el log pasa a `DataManager:
   backendUrl desde override (...) -> <la del override>` y el sync usa esa URL. Borrando ambos
   archivos → no debe aparecer ningún log de `backendUrl desde ...` ni ningún error, y el sync debe
   usar el default serializado (`https://vr.conecta.sh`). En Quest: subir/quitar el override con
   `adb push`/`adb shell rm` (ver gotcha de `adb push` arriba) y reabrir la app para ver el cambio
   sin rebuildear.

## P7: catálogo mergeado por device (lentes custom/genéricas)

- El backend ahora sirve `GET /api/lenses?device_id=` con **merge**: blob base + lentes
  custom del device solicitante (histórico: hasta P7.1, las lentes "genéricas" de admin
  también entraban acá como un tercer grupo del merge — **P7.2 las fusionó con el blob
  base**, ya NO son un grupo aparte del merge; ver §P7.2 más abajo). `DataManager` del
  **visor** manda `?device_id=` (`DataManagerLogic.BuildSyncUrl(url, ep, deviceId)`, guard:
  presencia de `TabletController` en escena ⇒ app tablet ⇒ sync anónima con solo base).
- **Versión mergeada**: `"{base}+x{hash}"` solo si hay extras; sin extras el string es la
  versión base literal (los caches existentes no se invalidan gratis). Cualquier
  alta/edición/borrado de lentes CUSTOM cambia el hash ⇒ el próximo sync reemplaza cache
  (una alta/edición/borrado de lente de ADMIN, P7.2, cambia la versión BASE en sí, no el hash
  de extras).
- **`LensDef.Origen`** (`origen` en JSON): `null`/ausente = blob base (P7.2: incluye las
  lentes de admin, que hasta P7.1 llevaban `"generic"` — ese valor **ya no se emite**),
  `"custom"` = propia de ESTE visor. La tablet gatea la UI con esto (badge en la card, gating
  del Ajuste fino, botones guardar/eliminar — ver docs/tablet.md; la UI que asumía
  `"generic"` ya se actualizó en P7.2, ver §P7.2 más abajo).
- El flujo de creación/edición va por WS (`create/update/delete_lens`, ver docs/networking.md):
  el visor hace el HTTP (`Data/CustomLensClient.cs`), re-sincroniza
  (`DataManager.RefreshFromBackend()`, que ahora SÍ tiene caller) y el hello re-broadcasteado
  lleva el catálogo nuevo.
- Gotcha: los `lens_overrides.json` de una lente custom borrada quedan huérfanos (keyed por un
  id que ya no existe) — inofensivos; si la lente se recrea, el id nuevo es otro
  (`custom_<hex>` regenerado).
- Gotcha: un device suspendido/vencido sincroniza como anónimo ⇒ sus customs desaparecen del
  cache local hasta reactivarlo (decisión deliberada, no bug).

## P7.1: edición de lentes BASE por un admin (nota de contrato)

- Un visor **admin** ahora puede EDITAR una lente BASE (monofocal/panoptix/vivity) desde la
  tablet — el cambio se persiste en el backend (`PUT /api/lenses/custom/{lens_id}` con el
  `id` de una lente base en vez de un `custom_xxxxxxxx`/`generic_xxxxxxxx`) y queda visible
  para TODOS los devices en el próximo sync. (Histórico P7.1: las bases nunca se borraban,
  `DELETE` rechazaba siempre con `reason:"BASE_LENS"` — **P7.2 cambió esto: un admin SI puede
  borrar cualquier lente del catálogo**, ver §P7.2 más abajo.)
  **El schema JSON del contrato NO cambia**: la lente editada se sigue sirviendo con la
  misma forma `{id, nombre, descripcion, params}` y SIN campo `origen` (sigue siendo una
  lente base, no una custom/genérica) — `CatalogParser`/`CatalogModel` de Unity no
  necesitan ningún cambio para esto.
- **`defaults/lentes.json` y `Assets/StreamingAssets/lentes.json` pasan a ser "defaults de
  fábrica", no la verdad viva**: a partir de P7.1, el catálogo realmente activo en un
  backend en producción puede DIVERGIR de estos dos archivos si un admin editó una base
  desde la tablet (versión `.aN`, ver `docs/backend.md` §P7.1) — el backend es quien manda
  en runtime (`GET /api/lenses`), estos JSON siguen siendo el punto de partida para un
  backend nuevo/reseteado y la base de comparación en diff/MD5 entre Unity y el repo, pero
  ya no son garantía de "lo que el visor ve hoy" si el backend tiene ediciones de admin
  encima.

## P7.2: las lentes "genéricas" se fusionan con el catálogo BASE — CAMBIO DE CONTRATO (backend + Unity, resuelto)

> Esta sección documenta el cambio de contrato hecho del lado backend
> (`backend/api/app/routers.py`, ver `docs/backend.md` §P7.2) y su consumo del lado Unity
> (`TabletController.cs`/`LensCardView.cs`, ver `docs/tablet.md` §"P7.1→P7.2 — gating por
> procedencia × admin"), ya implementado por @unity-dev. `CatalogParser.cs`/`CatalogModel.cs` NO
> necesitaron cambios (el schema JSON no cambió de forma, `origen` sigue siendo un string
> opcional) — el trabajo fue enteramente en la UI de la tablet que ramificaba sobre
> `origen == "generic"`.

- **Qué cambia para el consumidor de `GET /api/lenses`**: el campo `"origen":"generic"`
  **deja de aparecer** en cualquier lente. Antes, una lente creada por un admin (visible para
  todos) llevaba `origen:"generic"`; ahora, esa misma acción (crear con `scope:"generic"` vía
  `POST /api/lenses/custom`) la agrega directo al array `catalogo` del blob BASE — se sirve
  **sin campo `origen`**, indistinguible de `monofocal`/`panoptix`/etc. `"origen":"custom"`
  (lentes privadas de UN visor) sigue existiendo igual que antes.
- **Qué se resolvió en el lado Unity** (tarea de seguimiento de @unity-dev, ya cerrada):
  - `CatalogParser`/`CatalogModel.cs`: sin cambios, tal como se anticipó (`origen` sigue siendo
    un string opcional; el schema no cambió de forma).
  - Tablet (`docs/tablet.md` §"P7.1→P7.2"): la decisión de producto fue que **YA NO hace falta
    distinguir** "base de fábrica" de "genérica de admin" — el admin gestiona el catálogo
    entero por igual. `TabletController.BuildParamsEditor` simplificó `canDelete` a `ownCustom
    || isAdmin` (antes exigía además `origen == "generic"`); el toggle del alta se renombró
    "Agregar al catálogo (para todos)" (protocolo sin cambios, sigue mandando
    `scope:"generic"`); `LensCardView` mantiene la rama `origen == "generic"` solo como
    tolerancia con un backend viejo no migrado (nunca la emite un backend P7.2).
  - `DELETE` de una lente de catálogo (antes rechazaba siempre con `reason:"BASE_LENS"`,
    P7.1): la tablet ya ofrece "Eliminar lente" sobre CUALQUIER lente de catálogo si el visor
    es admin (incluidas las bases de fábrica); `OnLensError` generalizó el mensaje de
    `NOT_ADMIN` para cubrir editar/eliminar/crear-para-todos y mantiene `BASE_LENS` mapeado
    solo por compatibilidad con un backend viejo.
- **Qué NO cambia**: el shape de `POST`/`PUT`/`DELETE /api/lenses/custom` (`{status, lens?,
  catalog_version}`), los códigos de error existentes (`DEVICE_NOT_FOUND`,
  `DEVICE_NOT_AUTHORIZED`, `MODE_NOT_PRO`, `NOT_ADMIN`, `NOT_OWNER`, `LENS_LIMIT_REACHED`),
  el flujo por WS (`create/update/delete_lens`) ni la mecánica de versión mergeada
  (`"{base}+x{hash}"`).

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
