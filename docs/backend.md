# Backend FastAPI

## Qué es y por qué

Servicio central del simulador: sirve el catálogo de lentes al visor/tablet, verifica licencias por `device_id`, publica el manifiesto de actualizaciones (APK, **una versión activa por app**: `visor` | `tablet`) y recibe logs de los visores. Incluye un panel de administración web. Es **opcional en runtime**: el visor funciona sin backend gracias al catálogo embebido y la caché local (`Assets/Scripts/Runtime/Data/DataManager.cs`); el sync es en segundo plano y nunca bloquea el arranque.

## Arquitectura actual

Stack: FastAPI 0.115 + uvicorn (Python 3.12), SQLModel, Postgres 16, MinIO, Caddy 2. Todo orquestado con Docker Compose.

| Archivo | Rol |
|---------|-----|
| `backend/docker-compose.yml` | 4 servicios: `api` (FastAPI), `db` (postgres:16-alpine), `bucket` (MinIO, consola en `127.0.0.1:9001`), `caddy` (reverse proxy/TLS). Monta `defaults/lentes.json` como volumen read-only en `/seed/lentes.json` del `api`. |
| `backend/docker-compose.prod.yml` | Override de producción (VPS con dominio real): pisa `caddy.ports` con `!override` a `80:80` + `443:443` (el compose base mapea `${PORT}:${PORT}` + `${HTTPS_PORT}:443`, que en prod con `PORT=443` deja el 80 sin publicar — sin redirect HTTP→HTTPS ni challenge ACME HTTP-01). Requiere Docker Compose ≥ 2.24 (soporte de `!override`). Se aplica con `-f docker-compose.yml -f docker-compose.prod.yml`. |
| `backend/Caddyfile` | Site address `{$SCHEME}{$DOMAIN}:{$PORT}`; `/healthz` respondido por Caddy, el resto `reverse_proxy api:8000`. Con `DOMAIN` vacío escucha en cualquier hostname (acceso por IP de LAN desde Quest/tablet). |
| `backend/api/Dockerfile` | python:3.12-slim + libpq5/curl, uvicorn en :8000 con `--proxy-headers`, healthcheck a `/healthz`. |
| `backend/api/app/main.py` | App FastAPI; monta routers público/admin/files y `/static`. Arranque vía `lifespan` (no `@app.on_event`, deprecado): `run_migrations()` (Alembic), `seed()`, `ensure_bucket()`. Handler global que convierte `HTTPException(303, Location=...)` en redirect (mecanismo del login admin). CORS configurable (`CORS_ORIGINS`, default `*`) con `allow_credentials=False`. |
| `backend/api/app/config.py` | `Settings` (pydantic-settings) leídos de entorno/.env: `database_url`, `s3_*`, `public_base_url`, `jwt_secret`, `admin_default_user/pass`, `cors_origins` (+ property `cors_origins_list`), `log_level`. |
| `backend/api/app/database.py` | Engine SQLAlchemy (`pool_pre_ping=True`; para SQLite —solo tests— usa `StaticPool` + `check_same_thread=False`), `init_db()` = `SQLModel.metadata.create_all` (fallback solo para tests, ver más abajo), dependency `get_session`. |
| `backend/api/app/migrations.py` | `run_migrations()`: aplica Alembic (`upgrade head`) en el arranque normal del contenedor. Si detecta tablas de la app sin `alembic_version` (BD creada antes por `create_all`), hace `stamp` a `_INITIAL_REVISION` (`"0001"`, NO `head`) antes del `upgrade head` — así, si ya existe una revisión posterior a la inicial, el `upgrade` sí la aplica en vez de saltearla. |
| `backend/api/app/utils.py` | `utcnow()`: helper que devuelve `datetime.now(timezone.utc)` sin tzinfo (naive UTC) — reemplaza a `datetime.utcnow()` (deprecado desde Python 3.12) sin cambiar el formato de columnas `timestamp without time zone` ya existentes en Postgres. |
| `backend/api/alembic/`, `backend/api/alembic.ini` | Migraciones Alembic. `env.py` lee la URL desde `app.config.settings` (no del `.ini`) e importa `app.models` para poblar `target_metadata`. **A propósito NO llama `logging.config.fileConfig()`** (ver Gotchas — pisaba el logging de la app). Escritas a mano (no autogeneradas): `0001_initial_schema` (schema que ya generaba `create_all`) y `0002_versions_per_app` (partición de `versions` por canal — ver más abajo). |
| `backend/api/app/models.py` | Modelos SQLModel: `Device` (device_id único, status active/suspended/pending, `license_expiry` NULL = permanente), `Version` (**`app: "visor"\|"tablet"` + `apk_version`, `min_apk_version`, `apk_url`, `apk_sha256`, `changelog`, `is_active` — una activa POR APP, no global; sin PCK**), `LensCatalog` (JSON string versionado, una sola activa), `UpdateLog` (eventos de update por device), `AdminUser` (bcrypt hash, rol). |
| `backend/api/app/routers.py` | Endpoints públicos `/api/*` + rate limiter (slowapi). |
| `backend/api/app/seed.py` | Seed idempotente en startup: admin user, catálogo desde `/seed/lentes.json`, `Version` dummy **por cada app** (`visor`, `tablet`), device de test `DEV_TEST_001`. Logging (`logging`, no `print()`). |
| `backend/api/app/admin/` | Panel admin: `router.py` (login, dashboard, CRUD devices/lenses/versions, logs con filtros/CSV), `auth.py` (JWT en cookie httpOnly), `templating.py` + `i18n.py` (Jinja2, es/en), `storage.py` (boto3 → MinIO), `files.py` (proxy público `/files/<key>`). |
| `backend/api/app/templates/` | `base/login/dashboard/devices/lenses/versions/logs.html` (Jinja2 + HTMX). |
| `defaults/lentes.json` | Semilla del catálogo (v`0.5.1-clinical`, P6.9: rangos clínicos por foco — `foco_cerca_m` 0.15–1m, `foco_intermedio_m` 1–3m, `foco_lejos_m` 3–9m, antes 0–20 los tres —, 3 lentes: monofocal, panoptix, vivity; 13 params clínicos por lente con default/min/max, incluye `straylight`, `astig_magnitude`, `astig_axis_deg`). Idéntico en contenido al embebido de Unity `Assets/StreamingAssets/lentes.json` (verificado por diff/MD5 en cada actualización). Detalle clínico de P6.9 (incluida la discrepancia deliberada con el texto descriptivo de panoptix/vivity) en `docs/catalogo-lentes.md`. |
| `backend/.env.example` | Plantilla de `.env`: DOMAIN/SCHEME/PORT, PUBLIC_BASE_URL, POSTGRES_*, MINIO_*, S3_BUCKET, JWT_SECRET, ADMIN_DEFAULT_*, CORS_ORIGINS, LOG_LEVEL. |
| `backend/api/requirements-dev.txt` | Deps de test (`pytest`, `httpx`) además de `requirements.txt`. No se instala en la imagen de producción. |
| `backend/api/tests/` | Tests pytest + `TestClient` contra SQLite en memoria (sin Docker): `test_public_api.py` (manifest, lenses, verify válido/inválido/rate-limit), `test_admin_smoke.py` (login admin), `test_migrations.py` (adopción de Alembic: estampa `_INITIAL_REVISION`, no `head`). `conftest.py` fuerza `DATABASE_URL=sqlite:///:memory:` y noopea `run_migrations`/`ensure_bucket` (usa `init_db()` en su lugar); `seed()` sí corre real. |

```
Quest / Tablet ──HTTP──▶ caddy :8080/:443 ──▶ api :8000 ──▶ db (Postgres 16)
                              │                  │
                              └── /files/<key> ──┴──▶ bucket (MinIO :9000)
Browser admin ──▶ /admin (Jinja2+HTMX, cookie JWT)
```

### Endpoints públicos (consumidos por Unity)

| Endpoint | Consumidor | Notas |
|----------|-----------|-------|
| `GET /api/lenses` | `DataManager.TrySyncWithBackend()` — hace GET a `backendUrl + "/api/lenses"` con timeout 5 s | Devuelve `{version, catalogo:[...]}` del `LensCatalog` activo; 503 (`HTTPException`, `{"detail": "..."}`) si no hay activo. |
| `GET /api/manifest.json` | Futuro `UpdateManager` (visor y tablet, todavía no implementado en Unity) | **Una versión activa por app.** Query param `app: "visor"\|"tablet"` (default `"visor"` si se omite; cualquier otro valor → 422 automático por `Literal`). Shape: `{app, apk_version, min_apk_version, apk_url, apk_sha256, changelog}` — **sin PCK** (ver Decisiones). 503 (`HTTPException`, `{"detail": "..."}`) si el canal pedido no tiene versión activa. |
| `POST /api/verify` | LicenseManager (visor, no implementado aún en Unity) | Body `{device_id,...}`; 403 plano `{status, reason, message}` (`DEVICE_NOT_FOUND` / `DEVICE_SUSPENDED` / `LICENSE_EXPIRED`) o `status: ok`. **Sin `response_model` a propósito** (ver Decisiones) — no confundir con las rutas `response_model` de arriba. **Rate-limited 10 req/min/IP.** Actualiza `last_seen`/`last_ip`. |
| `POST /api/log` | visor | Batch de eventos; acepta devices desconocidos (debugging). Sin rate limit. |
| `GET /files/{key}` | visor (descarga APK/PCK) | Proxy streaming a MinIO; así el manifest publica URLs `public_base_url/files/...` sin exponer MinIO ni firmar tokens. |
| `GET /healthz`, `GET /`, `GET /docs` | humanos/infra | Health, índice, Swagger. |

La URL que usa Unity está hardcodeada en `Assets/Scripts/Runtime/Data/DataManager.cs`: `backendUrl = "http://192.168.88.198:8080"` (IP de LAN de desarrollo) — no coincide con el `http://localhost:8080` del compose; ajustar según red.

### Auth y panel admin

- Login en `/admin/login` (form). `authenticate_user` → bcrypt (passlib); si ok, `create_session_token` emite un **JWT HS256** (`sub`=username, TTL 8 h, firmado con `jwt_secret`) guardado en cookie `admin_session` httpOnly/samesite=lax (`secure` solo bajo HTTPS).
- La dependency `get_current_admin` valida cookie+JWT+usuario en BD; si falla lanza `HTTPException(303, Location=/admin/login)` que el handler de `main.py` convierte en redirect.
- Secciones: dashboard (contadores, versión activa **de cada app** (visor/tablet) + catálogo activo, últimos logs, avisos si `admin123`/JWT secret por defecto siguen configurados), devices (CRUD), lenses (crear/activar catálogos, editor visual sobre el JSON activo), versions (selector de `app` visor/tablet, upload de APK a MinIO con SHA256 al vuelo — persistido en `apk_sha256` —, activar/borrar; activar/subir desactiva solo las versiones previas del MISMO canal), logs (filtros, paginación 50, export CSV).
- i18n es/en propio (diccionario en `admin/i18n.py`, cookie `admin_lang`), sin gettext.

### Seed del catálogo

`_seed_lens_catalog` lee `/seed/lentes.json` (volumen desde `defaults/lentes.json`; fallback inline mínimo si no está montado). Lógica de promoción: si el catálogo activo en BD tiene una versión listada en `_KNOWN_SEED_VERSIONS` (`0.0.1-seed`, `0.1.0-fallback`, `0.2.0-noche`, `0.3.0-clinical`, `0.4.0-clinical`, `0.4.0-fallback`, `0.5.0-clinical`) se considera seed no editado y se reemplaza por la versión nueva del JSON; si NO está en esa lista, se asume edición manual del admin y **no se pisa**. El fallback inline (1 lente, sin `straylight` ni `astig_*`) usa su propia versión `0.4.0-fallback` — nunca la versión clínica real (`0.5.0-clinical`) — precisamente para que, si el volumen aparece más tarde con el catálogo completo, la promoción se dispare (versiones distintas) en vez de hacer short-circuit por igualdad de versión con contenido mentido. **Cada versión nueva de `defaults/lentes.json` debe agregarse a `_KNOWN_SEED_VERSIONS`** (`backend/api/app/seed.py`) o no se auto-promueve (verificado en vivo al pasar de `0.4.0-clinical` a `0.5.0-clinical`: `docker compose logs api` mostró el reemplazo del catálogo y `GET /api/lenses` devolvió la versión nueva con `astig_magnitude`/`astig_axis_deg`).
**(P6.9, pendiente) `defaults/lentes.json` ya se bumpeó a `0.5.1-clinical` (rangos de foco, ver
tabla arriba) pero `_KNOWN_SEED_VERSIONS` todavía NO incluye esa versión** — la promoción de ESTE
cambio en un backend que ya corrió (activo `0.5.0-clinical`) funciona igual, porque el chequeo usa
la versión VIEJA activa (que sí está en la lista); pero un bump FUTURO se va a frenar si
`0.5.1-clinical` no se agrega al set antes. Falta agregar `"0.5.1-clinical"` a
`_KNOWN_SEED_VERSIONS` (1 línea en `seed.py`) — fuera del alcance de la tarea que solo tocó datos
(@unity-dev no edita `backend/api/`).

## Decisiones y porqués

- **Compose con 4 servicios y Caddy delante** → TLS automático (Let's Encrypt) en prod sin tocar la app; en local, `DOMAIN` vacío + `SCHEME=http://` permiten acceder por IP de LAN desde el Quest.
- **`${DOMAIN-localhost}` (guion, no `:-`) en el compose** → un `DOMAIN=` vacío en `.env` NO cae al default; ese vacío es justamente el modo "escuchar en cualquier hostname".
- **Alembic en vez de `create_all`** → el arranque corre `run_migrations()` (`app/migrations.py`). Adopta BDs existentes creadas por versiones previas (`create_all`, sin Alembic) detectando tablas de la app sin `alembic_version` y haciendo `stamp` a `_INITIAL_REVISION` (`"0001"`) — **no a `head`** — antes de un `upgrade head` que aplica cualquier migración posterior a esa. Estampar `head` directamente sería incorrecto en cuanto exista una revisión posterior a la inicial: la BD vieja quedaría marcada como si ya tuviera ese DDL sin haberlo corrido (columnas faltantes silenciosas). Esto dejó de ser hipotético con `0002_versions_per_app`: verificado en vivo sobre una BD real ya estampada a `0001` (`docker compose logs api` mostró `Running upgrade 0001 -> 0002, versions per app` y el resto del arranque siguió normal), confirmando que el mecanismo de adopción sigue funcionando ahora que `head != "0001"`. `init_db()` (`create_all`) sigue existiendo solo como fallback para los tests con SQLite en memoria.
- **Proxy `/files/<key>` en vez de URLs directas a MinIO** → MinIO no se expone al exterior y las URLs del manifest no necesitan tokens firmados.
- **JWT en cookie httpOnly para el admin (no Bearer)** → el panel es server-rendered (Jinja2+HTMX); la cookie evita manejar tokens en JS y `httponly` mitiga XSS.
- **CORS con `allow_origins` configurable (`CORS_ORIGINS`, default `*`) y `allow_credentials=False` siempre** → Starlette no permite combinar wildcard con credenciales (era la incoherencia previa). El panel `/admin` es server-rendered y va por cookie de mismo origen (no necesita CORS); la API pública la consume Unity (`UnityWebRequest`), que no es un browser y no aplica CORS. Por eso `allow_credentials=False` es seguro incluso con wildcard; si algún día un browser cross-origin necesita pegarle a `/api/*` con credenciales, hay que fijar `CORS_ORIGINS` a orígenes explícitos (no basta con la lista, Starlette también exige `allow_credentials=True` en ese caso).
- **`/api/verify` rate-limited a 10/min/IP** (antes 1/min) → 1/min rompía con NAT: varios visores de una misma clínica comparten IP pública y se bloqueaban entre sí. 10/min sigue siendo anti brute-force razonable; `/api/log` sin límite porque un update legítimo emite varios eventos.
- **`/api/verify` sin `response_model` (deliberado)** → a diferencia de `/api/manifest.json` y `/api/lenses` (que sí tienen `response_model` y por eso sus 503 se convirtieron de `JSONResponse` cruda a `HTTPException` para que el schema de OpenAPI sea fiel), `/api/verify` devuelve formas distintas en éxito (`VerifyResponse`) y en 403 (`VerifyDenied`, plano, sin nested `detail`) — es el contrato documentado para el futuro `LicenseManager` de Unity. Convertirlo a `HTTPException(403, detail=...)` anidaría la respuesta bajo `"detail"` y rompería esa forma, así que se dejó como estaba (deuda de fidelidad de OpenAPI, no de comportamiento).
- **Licencias permanentes por defecto (`license_expiry` NULL)** y **pre-registro manual de devices** (desconocido = 403) → decisiones de Sprint 0.
- **Seed idempotente con lista de versiones "conocidas"** → permite actualizar el catálogo por git pull + restart sin pisar recalibraciones hechas desde el panel.
- **`datetime.utcnow()` reemplazado por `utils.utcnow()` (naive UTC), no por `datetime.now(timezone.utc)` aware** → evita mezclar naive/aware en las columnas `timestamp without time zone` ya existentes en Postgres sin cambiar el formato guardado ni arriesgar comparaciones (ninguna hoy compara estos campos, pero es la opción de menor riesgo hacia adelante).
- **Uploads acumulados en memoria (`storage.py`)** → simplicidad; un APK ~50-100 MB es aceptable, migrar a multipart si crece.
- **`Version` partida por `app` ("visor"/"tablet"), PCK eliminado (migración `0002_versions_per_app`)** → el modelo original (una sola versión activa global, con `asset_version`/`pck_url`/`pck_sha256`) era herencia directa del prototipo Godot, que empaqueta assets en un `.pck` separado del ejecutable. Unity no tiene ese concepto (todo va en el APK/AAB) y el simulador pasó a ser DOS apps Android independientes con ciclos de release propios (visor Quest y tablet), así que "una versión activa" dejó de tener sentido sin decir de qué app. Se resolvió con **una versión activa por canal** (`Version.app`, index) en vez de arrastrar un campo `pck_*` muerto. Fue un breaking change deliberado del shape de `/api/manifest.json`: se aprovechó porque **nada consume el manifest todavía** (Unity aún no tiene `UpdateManager`) — riesgo cero, sin necesidad de versionar el endpoint. `ManifestResponse` ahora es `{app, apk_version, min_apk_version, apk_url, apk_sha256, changelog}` (antes tenía `current_apk_version`/`current_asset_version`/`pck_url`/`pck_sha256`, sin `app`). El panel (`/admin/versions`) subió un selector de canal y el upload/activate/delete solo tocan versiones del MISMO `app` (antes desactivaban TODAS globalmente). El SHA256 del APK, que antes se calculaba y se **descartaba** (`_apk_sha256` sin usar; solo el PCK se persistía), ahora se guarda en `apk_sha256`.

## Gotchas

- **`defaults/lentes.json` es contrato compartido con Unity.** `CatalogParser.cs` (Unity) parsea el mismo schema `{version, catalogo:[{id, nombre, descripcion, params:{clave:{default,min,max}}}]}` que sirve `/api/lenses` y que siembra el seed. Cambiar claves o estructura exige tocar **ambos lados** (backend/defaults y `Assets/Scripts/Runtime/Data/CatalogParser.cs` + `CatalogModel.cs` + `Assets/StreamingAssets/lentes.json`). Unity tolera params NUEVOS sin recompilar (`MergeMissingParams` completa faltantes desde los defaults embebidos), pero renombrar o cambiar tipos rompe.
- **`admin/admin123` y `dev-jwt-secret-change-me` jamás a producción.** Son los defaults de `config.py`/compose/`.env.example`. El dashboard muestra un aviso si siguen activos, pero nada lo impide. No hay UI de cambio de contraseña: rotar vía `.env` **antes del primer arranque** (el seed solo crea el user si no existe; cambiar `.env` después no actualiza el hash en BD — hay que borrar el user o la BD).
- **Si el catálogo activo fue editado desde el panel, el seed nunca más lo actualiza** (versión fuera de `_KNOWN_SEED_VERSIONS`). Deseado, pero sorprende: subir un `defaults/lentes.json` nuevo no cambia nada hasta activar manualmente un catálogo en `/admin/lenses`. Además, cada versión de seed nueva debe agregarse a `_KNOWN_SEED_VERSIONS` o no se auto-promoverá en la siguiente.
- **El device de test `DEV_TEST_001` y las `Version` dummy (una por app: visor/tablet)** las crea el seed siempre; eliminarlas antes de producción (los dummy tienen URLs `/dummy/...` inexistentes y `apk_sha256` vacío).
- **El dummy de `Version` usa a propósito `apk_version == min_apk_version == "0.1.0"` (el mismo `bundleVersion` que hoy tiene el proyecto Unity)** para que, si el futuro `UpdateManager` llegara a consultar el manifest contra un dummy sin querer, nunca dispare el cartel de "hay una actualización" (versión instalada == versión mínima requerida). Si el `bundleVersion` base de Unity cambia, hay que actualizar `_DUMMY_APK_VERSION` en `seed.py` a mano.
- **En un backend que ya corrió antes de la migración `0002`, la fila vieja de `versions` queda con `app="visor"`** (el `server_default` de la migración, no una decisión de negocio) — el seed post-migración entonces solo agrega la dummy que falta (`tablet`), nunca toca la fila `visor` existente. Verificado en vivo: `docker compose logs api` mostró `[seed] version dummy creada: app=tablet APK v0.1.0` sin ninguna línea equivalente para `visor` (porque ya tenía una activa).
- **`/api/verify` devuelve 429 después del 10º intento en menos de 1 min** desde la misma IP (slowapi); al probar con curl en loop rápido parece "roto" sin serlo.
- **Adopción de Alembic en BD existente**: la detección es "hay tablas de la app pero no `alembic_version`" → `stamp` a `_INITIAL_REVISION` (`"0001"`, constante en `app/migrations.py`), seguido de `upgrade head`. Esto asume que toda BD sin `alembic_version` tiene el schema exacto de `0001` (cierto hoy, es lo único que generaba `create_all`). Si algún día existiera una BD pre-Alembic con un schema *distinto* al de `0001` (no debería pasar, pero), este mecanismo no lo detecta — asume `0001` siempre.
- **Tests (`backend/api/tests/`) fuerzan `DATABASE_URL=sqlite:///:memory:` en `conftest.py` antes de importar `app.*`** — si se agregan nuevos tests que importan módulos de la app fuera de ese conftest (o se reordena la colección de pytest), revisar que el env var siga fijándose primero; `Settings()` es un singleton leído una sola vez al importar `app.config`.
- **`alembic/env.py` NO llama `logging.config.fileConfig(config.config_file_name)`, a propósito.** Es el default del template de Alembic, pero `run_migrations()` corre `env.py` desde DENTRO del proceso de la app (no como CLI standalone): `fileConfig()` reconfigura el logger root con el nivel/handler de `[logger_root]` en `alembic.ini` (nivel `WARNING`) y pisa el `logging.basicConfig(level=...)` de `main.py` para el resto de la vida del proceso — silenciaba TODO log de la app (`seed`, `ensure_bucket`, "Backend listo en...") después del primer "Aplicando migraciones Alembic..." de cada arranque. Detectado en vivo: el catálogo se promovía bien (`0.4.0-clinical → 0.5.0-clinical`) pero sin ningún log después de esa línea. Si se necesita el formato lindo de `alembic.ini` al correr `alembic <cmd>` como CLI suelto (fuera de la app), hoy no lo tiene — usa el logger root desnudo (solo WARNING+, sin handler).

## Cómo probar

```bash
cd backend
cp .env.example .env
docker compose up -d
docker compose logs -f api        # ver "[seed] ..." y uvicorn arriba

curl http://localhost:8080/healthz                  # ok (lo responde Caddy)
curl http://localhost:8080/api/lenses               # catálogo activo (3 lentes)
curl http://localhost:8080/api/manifest.json        # canal "visor" (default), version dummy del seed
curl "http://localhost:8080/api/manifest.json?app=tablet"   # canal "tablet"
curl "http://localhost:8080/api/manifest.json?app=phone"    # 422 (Literal invalido)
curl -X POST http://localhost:8080/api/verify \
     -H "Content-Type: application/json" -d '{"device_id":"DEV_TEST_001"}'
# → {"status":"ok","device_name":"Visor de desarrollo",...}
curl -X POST http://localhost:8080/api/verify \
     -H "Content-Type: application/json" -d '{"device_id":"NOEXISTE"}'
# → 403 DEVICE_NOT_FOUND (10 req/min/IP de cuota antes del 429 de slowapi)
```

Tests (SQLite en memoria, sin Docker):
```bash
cd backend/api
python -m venv .venv && .venv/Scripts/activate   # o source .venv/bin/activate
pip install -r requirements-dev.txt
python -m pytest -v
```

- Panel admin: `http://localhost:8080/admin` → login `admin`/`admin123` → dashboard con avisos de credenciales inseguras.
- Swagger: `http://localhost:8080/docs`. Consola MinIO: `http://localhost:9001` (`minioadmin`/`minioadmin`).
- Contra Unity: poner la IP de la máquina que corre Docker en `backendUrl` de `DataManager.cs`, entrar a Play y buscar en consola `DataManager: catalogo v... sincronizado desde backend`.
- Producción (VPS): DNS A record → IP del VPS, puertos 80/443 abiertos, `.env` con `DOMAIN`, `SCHEME=` vacío, `PORT=443`, `PUBLIC_BASE_URL=https://...` y secrets regenerados (`openssl rand -hex 32`); levantar con el override `docker compose -f docker-compose.yml -f docker-compose.prod.yml up -d` (necesario para que Caddy publique el 80 además del 443 — el compose base solo mapea `${PORT}:${PORT}`, ver `backend/docker-compose.prod.yml`) y verificar `curl https://<dominio>/healthz` (Caddy tarda ~30 s en emitir el certificado).

## Pendientes / deuda

- `/api/verify` no tiene `response_model` (por diseño, ver Decisiones) — su 403/200 no aparecen tipados en el OpenAPI generado; documentado ahí, no se resuelve sin tocar el contrato con el futuro `LicenseManager` de Unity.
- **`/api/manifest.json` con shape nuevo (por app) todavía no tiene consumidor en Unity** — el futuro `UpdateManager` (visor y tablet) debe leer `{app, apk_version, min_apk_version, apk_url, apk_sha256, changelog}` y pedir el canal correcto (`?app=visor` desde el visor, `?app=tablet` desde la tablet); implementarlo es la próxima tarea de Unity que toca este contrato.
- Endpoint `/api/admin/versions` con API key para CI/CD (Sprint 11) — no existe todavía; el `API_KEY_CI` que quedaba sin consumidor se quitó de `config.py`/compose/`.env.example` (si se implementa, agregar el setting de nuevo).
- UI de cambio de contraseña del admin (hoy solo vía `.env` + reseed).
- Uploads del panel acumulan el archivo completo en memoria; multipart si los binarios crecen.
- **(P6.9) Agregar `"0.5.1-clinical"` a `_KNOWN_SEED_VERSIONS` (`backend/api/app/seed.py`)** — 1
  línea, para que la cadena de auto-promoción del seed no se corte en el próximo bump de
  `defaults/lentes.json`. Ver §Seed del catálogo arriba.
