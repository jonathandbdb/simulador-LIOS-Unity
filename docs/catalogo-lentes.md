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
StreamingAssets/lentes.json ─┐ (defaults embebidos, base del merge)
persistentDataPath/lentes.json ─┤ cache (última copia buena del backend)
GET http://192.168.88.198:8080/api/lenses ─┘ sync en background (no bloquea)
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
  en vivo (`OverrideParams`) y su persistencia con debounce. Config backend hardcodeada:
  `backendUrl = "http://192.168.88.198:8080"` (LAN de desarrollo), endpoint `/api/lenses`,
  timeout 5 s.
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
  (defaults del catálogo + overrides encima), `ComputeBlend(leftId, rightId)` (true si ambos ojos
  tienen lente y son distintas; solo informativo) y `CleanOverrides(saved, new, catalogParams,
  epsilon=0.0005)` (si un valor vuelve al default dentro de epsilon el override se ELIMINA;
  ignora la clave `lens_id`).
- `Assets/StreamingAssets/lentes.json` — catálogo embebido en el build (v `0.4.0-clinical`,
  3 lentes: `monofocal`, `panoptix`, `vivity`).
- `Assets/Tests/EditMode/DataLogicTests.cs` — tests NUnit EditMode de la lógica pura + un test de
  integración sobre el JSON real.

### Schema del JSON (contrato con el backend)

```json
{
  "version": "0.4.0-clinical",           // string
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

Params clínicos actuales (11 por lente en `Assets/StreamingAssets/lentes.json`):

| Clave | Unidad / rango | Significado |
|---|---|---|
| `foco_lejos_m` | m, 0–20 (0 ⇒ foco no usado) | distancia del foco lejano |
| `foco_intermedio_m` | m, 0–20 | foco intermedio (0.6 panoptix, 0.67 vivity) |
| `foco_cerca_m` | m, 0–20 | foco cercano (0.4 panoptix) |
| `profundidad_foco_m` | m, 0.1–5 | ancho de zona nítida (el shader la mapea ×0.5 a D) |
| `desenfoque_max` | 0–1 | blur máximo fuera de foco |
| `halo_intensity` | 0–1 | glow + anillos difractivos |
| `halo_extra_rings` | 0–1 | prominencia de anillos (pupila del billboard) |
| `contrast_loss` | 0–0.6 | compresión de contraste |
| `destello_intensity` | 0–1 | intensidad del starburst |
| `destello_rayos` | 0–16 (cantidad) | número de rayos del starburst |
| `straylight` | 0–1 | escala del velo de disability glare por ojo |

### Orden de carga (`InitializeAsync`)

1. `LoadLensOverrides()` desde `persistentDataPath/lens_overrides.json` (corrupto ⇒ se ignora).
2. Defaults embebidos: `StreamingAssets/lentes.json` por UnityWebRequest (en Android vive dentro
   del APK, `jar://`; en desktop se antepone `file://`). Se parsean y guardan para el merge.
3. Cache: `persistentDataPath/lentes.json` si existe y parsea → `MergeMissingParams` con defaults
   → `CatalogLoaded(version, "cache", count)`. Si no, defaults → `CatalogLoaded(..., "defaults", ...)`.
4. Sync en background (no bloquea el arranque): `GET {backendUrl}/api/lenses`, timeout 5 s.
   Éxito (200 + JSON válido) ⇒ guarda el texto crudo en cache, merge con defaults, reemplaza el
   catálogo, `CatalogLoaded(..., "backend", ...)` + `CatalogSyncedWithBackend(version)`. Cualquier
   fallo (inalcanzable, no-200, JSON inválido, excepción síncrona por cleartext bloqueado) ⇒
   `CatalogSyncFailed(mensaje)` y se sigue con el catálogo local.

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

- Lógica pura (`CatalogParser`, `LensEngine`) separada del MonoBehaviour → testeable en EditMode
  sin escena ni IO.
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
- **`LensEngine` y `CatalogParser` son lógica pura testeable: tocarlas ⇒ extender
  `Assets/Tests/EditMode/DataLogicTests.cs`** en el mismo cambio.
- **Drift real hoy**: `defaults/lentes.json` (lado backend) sigue en `0.3.0-clinical` SIN
  `straylight`, mientras `Assets/StreamingAssets/lentes.json` está en `0.4.0-clinical` con 11
  params. Un sync exitoso serviría un catálogo sin `straylight`; lo salva `MergeMissingParams`,
  pero el contrato está desincronizado.
- **El test de integración está desactualizado**: `StreamingAssets_RealCatalog_...` asserta versión
  `"0.3.0-clinical"` y 10 params por lente; el JSON real tiene `"0.4.0-clinical"` y 11
  (`straylight`). Ese test FALLA contra el catálogo actual hasta que se actualice.
- **`backendUrl` está hardcodeada a una IP LAN** (`http://192.168.88.198:8080`); es HTTP cleartext:
  en Android puede lanzar excepción síncrona al iniciar el request (está atrapada y degrada a
  `CatalogSyncFailed`), pero requiere permitir cleartext o cambiar a HTTPS para producción.
- **`min`/`max` de `ParamSpec` no se aplican en runtime**: ni `BuildEyeState` ni `OverrideParams`
  clampean; hoy el clamp depende de la UI (tablet). Un valor fuera de rango entra tal cual.
- **Una cache corrupta no se borra**: si `persistentDataPath/lentes.json` no parsea se ignora en
  cada arranque (warning), pero el archivo queda hasta que un sync exitoso lo sobrescriba.
- **`ApplyLens` con id inexistente solo loguea warning** y no toca el estado: el ciclado de
  `SimuladorInput` depende de que los ids del catálogo sean estables.

## Cómo probar

1. Editor: Window → General → Test Runner → EditMode → correr `Simulador.Tests.EditMode`
   (`DataLogicTests`): parseo válido/inválido, merge sin pisar existentes, defaults + overrides,
   blend, limpieza de overrides e integración contra el JSON real (ver gotcha: hoy ese último
   está desactualizado y falla).
2. Play mode: en consola debe aparecer `DataManager: catalogo vX cargado desde defaults|cache (3
   lentes)` y luego `sync con backend -> http://192.168.88.198:8080/api/lenses` (con el backend
   apagado, el fallo de sync es esperado y no bloquea).
3. Backend local: levantar `backend/docker-compose.yml`, apuntar `backendUrl` a esa IP y verificar
   `catalogo vX sincronizado desde backend` + que se escribió `persistentDataPath/lentes.json`.
4. Overrides: llamar `DataManager.Instance.OverrideParams(...)` (o mover sliders desde la tablet),
   esperar 1 s y verificar `lens_overrides.json` en `persistentDataPath`; volver el valor al
   default y comprobar que la clave desaparece del archivo.

## Pendientes / deuda

- Actualizar `StreamingAssets_RealCatalog_ParsesWithExpectedClinicalValues` a `0.4.0-clinical` /
  11 params.
- Sincronizar `defaults/lentes.json` del backend con `straylight` (y versionar el contrato).
- Hacer configurable `backendUrl` (hoy IP LAN hardcodeada, HTTP cleartext).
- Clamp de overrides a `min`/`max` de `ParamSpec` en `LensEngine`/`OverrideParams`.
