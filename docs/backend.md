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
| `backend/api/app/main.py` | App FastAPI; monta routers público/admin/files y `/static`. Arranque vía `lifespan` (no `@app.on_event`, deprecado): `run_migrations()` (Alembic), `seed()`, `_maybe_purge_logs()` (retención de logs, siempre ejecuta en el arranque — ver más abajo), `ensure_bucket()`. Handler global que convierte `HTTPException(303, Location=...)` en redirect (mecanismo del login admin). CORS configurable (`CORS_ORIGINS`, default `*`) con `allow_credentials=False`. |
| `backend/api/app/config.py` | `Settings` (pydantic-settings) leídos de entorno/.env: `database_url`, `s3_*`, `public_base_url`, `jwt_secret`, `admin_default_user/pass`, `cors_origins` (+ property `cors_origins_list`), `log_level`, `log_retention_days` (default 30, retención de `UpdateLog` — ver más abajo), `provisioning_signature_checksum` (default `""`, checksum del certificado de firma del proyecto para `/admin/provisioning` — ver más abajo). |
| `backend/api/app/database.py` | Engine SQLAlchemy (`pool_pre_ping=True`; para SQLite —solo tests— usa `StaticPool` + `check_same_thread=False`), `init_db()` = `SQLModel.metadata.create_all` (fallback solo para tests, ver más abajo), dependency `get_session`. |
| `backend/api/app/migrations.py` | `run_migrations()`: aplica Alembic (`upgrade head`) en el arranque normal del contenedor. Si detecta tablas de la app sin `alembic_version` (BD creada antes por `create_all`), hace `stamp` a `_INITIAL_REVISION` (`"0001"`, NO `head`) antes del `upgrade head` — así, si ya existe una revisión posterior a la inicial, el `upgrade` sí la aplica en vez de saltearla. |
| `backend/api/app/utils.py` | `utcnow()`: helper que devuelve `datetime.now(timezone.utc)` sin tzinfo (naive UTC) — reemplaza a `datetime.utcnow()` (deprecado desde Python 3.12) sin cambiar el formato de columnas `timestamp without time zone` ya existentes en Postgres. |
| `backend/api/alembic/`, `backend/api/alembic.ini` | Migraciones Alembic. `env.py` lee la URL desde `app.config.settings` (no del `.ini`) e importa `app.models` para poblar `target_metadata`. **A propósito NO llama `logging.config.fileConfig()`** (ver Gotchas — pisaba el logging de la app). Escritas a mano (no autogeneradas): `0001_initial_schema` (schema que ya generaba `create_all`), `0002_versions_per_app` (partición de `versions` por canal — ver más abajo), `0003_device_apk_version` (columna `devices.last_apk_version`, nullable sin default — ver más abajo), `0004_custom_lenses_device_mode` (tabla `custom_lenses` + `devices.app_mode`/`is_admin`, P7), `0005_generic_lenses_to_catalog` (migración de DATOS, no solo schema: mueve lentes "genéricas" preexistentes de `custom_lenses` al blob `lens_catalogs` — P7.2, ver más abajo; usa `sa.table`/`sa.column` en vez de los modelos SQLModel a propósito, para no atarse al ORM actual) y `0006_device_ota_enabled` (columna `devices.ota_enabled` bool, `server_default=false` — gate por-dispositivo del OTA del backend, ver §OTA por-dispositivo abajo). |
| `backend/api/app/models.py` | Modelos SQLModel: `Device` (device_id único, status **string libre** `active\|suspended\|pending\|rejected` — sin migración, es un `str` sin `CHECK`/enum —, `license_expiry` NULL = permanente, `last_apk_version` NULL-able: última versión de APK reportada por el visor en `POST /api/verify`, solo lectura desde el panel — ver más abajo —, `ota_enabled` bool default `False`: gate por-dispositivo del OTA del backend, ver §OTA por-dispositivo abajo), `Version` (**`app: "visor"\|"tablet"` + `apk_version`, `min_apk_version`, `apk_url`, `apk_sha256`, `changelog`, `is_active` — una activa POR APP, no global; sin PCK**), `LensCatalog` (JSON string versionado, una sola activa), `UpdateLog` (eventos de update por device), `AdminUser` (bcrypt hash, rol). |
| `backend/api/app/routers.py` | Endpoints públicos `/api/*` + rate limiter (slowapi). También vive acá la **retención de logs**: `purge_old_logs(session)` (borra `UpdateLog` más viejos que `settings.log_retention_days`, devuelve cantidad borrada) y `_maybe_purge_logs(session)` (la envuelve con throttle de 1h vía timestamp de módulo en memoria `_last_log_purge_at`). |
| `backend/api/app/seed.py` | Seed idempotente en startup: admin user, catálogo desde `/seed/lentes.json`, `Version` dummy **por cada app** (`visor`, `tablet`), device de test `DEV_TEST_001`. Logging (`logging`, no `print()`). |
| `backend/api/app/admin/` | Panel admin: `router.py` (login, dashboard, CRUD devices/lenses/versions, logs con filtros/CSV, provisioning), `auth.py` (JWT en cookie httpOnly), `templating.py` + `i18n.py` (Jinja2, es/en), `storage.py` (boto3 → MinIO), `files.py` (proxy público `/files/<key>`), `provisioning.py` (payload + QR SVG del provisioning Android Enterprise de la tablet — ver más abajo). |
| `backend/api/app/templates/` | `base/login/dashboard/devices/lenses/versions/logs/provisioning.html` (Jinja2 + forms + JS vanilla — **no hay HTMX real** pese a lo que decía el docstring de `router.py`, drift detectado y corregido acá). |
| `defaults/lentes.json` | Semilla del catálogo (v`0.8.0-clinical`: 5 lentes — monofocal, panoptix, vivity, paciente_joven, catarata —; 15 params clínicos por lente con default/min/max, incluye `straylight`, `astig_magnitude`, `astig_axis_deg`, `cataract_yellow` (filtro amarillo de catarata, 0–1) y el nuevo `cataract_scatter` (dispersión intraocular, 0–1 — mecanismo de degradación independiente de la distancia)). Idéntico en contenido al embebido de Unity `Assets/StreamingAssets/lentes.json` (verificado por diff/hash SHA256 en cada actualización). Detalle clínico en `docs/catalogo-lentes.md` (sección de @unity-dev — el shader/binder que consume `cataract_scatter` ya está implementado, etapa B de @vision-optics). |
| `backend/.env.example` | Plantilla de `.env`: DOMAIN/SCHEME/PORT, PUBLIC_BASE_URL, POSTGRES_*, MINIO_*, S3_BUCKET, JWT_SECRET, ADMIN_DEFAULT_*, CORS_ORIGINS, LOG_LEVEL, LOG_RETENTION_DAYS (default 30), PROVISIONING_SIGNATURE_CHECKSUM (default vacío/comentado — ver Provisioning más abajo). |
| `backend/api/requirements-dev.txt` | Deps de test (`pytest`, `httpx`) además de `requirements.txt`. No se instala en la imagen de producción. |
| `backend/api/tests/` | Tests pytest + `TestClient` contra SQLite en memoria (sin Docker): `test_public_api.py` (manifest, lenses, verify válido/inválido/rate-limit, persistencia de `last_apk_version` en device nuevo/existente y no-pisado con valor ausente/vacío), `test_admin_smoke.py` (login admin, aprobar/rechazar devices, smoke de `/admin/devices` mostrando `last_apk_version`), `test_admin_versions.py`, `test_migrations.py` (adopción de Alembic: estampa `_INITIAL_REVISION`, no `head`), `test_log_retention.py` (`purge_old_logs` borra viejos y conserva recientes, throttle de `_maybe_purge_logs`, `POST /api/log` dispara la purga), `test_custom_lenses.py` (P7/P7.1/P7.2: modo pro/admin, CRUD de lentes custom y su matriz de autorización, alta de lentes de admin directo al catálogo BASE (`scope="generic"`, P7.2) y borrado de CUALQUIER lente del catálogo por un admin con historial `.aN` — ver P7.2 abajo —, merge/versionado de `GET /api/lenses`, panel `/admin/custom-lenses` y reemplazo de hardware, edición de lentes BASE por un admin con encadenado `.aN`, y (P7.3) reorden del catálogo BASE (`POST /api/lenses/reorder`) con validación de permutación exacta, no-op sin gastar `.aN` y rollback por historial; sus tests de manipulación del catálogo BASE se ubican ANTES de `test_lenses_merge_skips_base_id_collision` a propósito, ver comentario en el archivo), `test_generic_lens_migration.py` (P7.2: la migración `0005` — movida/no-op idempotente/colisión saltada — cargada por ruta con `importlib` ya que el archivo empieza con un dígito, ejecutada directo contra una `Connection` sin correr Alembic de verdad), `test_admin_provisioning.py` (QR de Android Enterprise: requiere login, checksum del PAQUETE calculado desde un `apk_sha256` conocido creado en el test — el dummy del seed tiene `apk_sha256=""`, no sirve para verificar el checksum —, `GET` ignora por completo parámetros de WiFi aunque se peguen a mano en la query string, campos de WiFi presentes/ausentes según `wifi_ssid` vía `POST`, `PROVISIONING_WIFI_SECURITY_TYPE` `"WPA"`/`"NONE"` según haya o no password, banner `prov.no_checksum` sin `<svg>` de segno y sin 500 con `apk_sha256` vacío/no-hex/de largo incorrecto, ese mismo banner NO aparece si hay checksum de FIRMA configurado aunque el `apk_sha256` sea inválido, aviso `prov.no_active` sin QR si no hay versión activa de tablet, preferencia por el checksum de FIRMA cuando `provisioning_signature_checksum` está configurado), `test_log_redaction.py` (`_RedactWifiPasswordFilter` de `app/main.py` ejercitado directo sobre `logging.LogRecord`s con la forma de `uvicorn.access`: redacción con `wifi_password` al inicio/medio/final de la query y con valor URL-encoded, el filtro nunca descarta un record —siempre devuelve `True`—, y records con `args` de otra forma o sin `args` pasan intactos sin excepción). `conftest.py` fuerza `DATABASE_URL=sqlite:///:memory:` y noopea `run_migrations`/`ensure_bucket` (usa `init_db()` en su lugar); `seed()` sí corre real. **77 tests** tras la pasada de correcciones sobre `/admin/provisioning` (61 antes + 16 nuevos: 7 en `test_admin_provisioning.py`, 9 en `test_log_redaction.py` nuevo). |

```
Quest / Tablet ──HTTP──▶ caddy :8080/:443 ──▶ api :8000 ──▶ db (Postgres 16)
                              │                  │
                              └── /files/<key> ──┴──▶ bucket (MinIO :9000)
Browser admin ──▶ /admin (Jinja2 + forms + JS vanilla, cookie JWT)
```

### Endpoints públicos (consumidos por Unity)

| Endpoint | Consumidor | Notas |
|----------|-----------|-------|
| `GET /api/lenses` | `DataManager.TrySyncWithBackend()` — hace GET a `backendUrl + "/api/lenses"` con timeout 5 s | Devuelve `{version, catalogo:[...]}` del `LensCatalog` activo; 503 (`HTTPException`, `{"detail": "..."}`) si no hay activo. Query param opcional `device_id` mergea las CUSTOM privadas de ese device (P7/P7.2, detalle abajo — las lentes de admin ya son parte del blob base, sin merge). |
| `GET /api/manifest.json` | `UpdateManager` (visor y tablet, `docs/updates.md`) | **Una versión activa por app.** Query param `app: "visor"\|"tablet"` (default `"visor"` si se omite; cualquier otro valor → 422 automático por `Literal`). Shape: `{app, apk_version, min_apk_version, apk_url, apk_sha256, changelog}` — **sin PCK** (ver Decisiones). 503 (`HTTPException`, `{"detail": "..."}`) si el canal pedido no tiene versión activa. **Query param opcional `device_id`** (gate por-dispositivo del OTA, ver `Device.ota_enabled` abajo): `app == "tablet"` → siempre 200 (la tablet nunca está en `devices`, no tiene gate de licencia); `device_id` ausente/vacío → 200 (compat retro, ver gotcha); `device_id` presente y el device existe con `ota_enabled=True` → 200; en cualquier otro caso (`ota_enabled=False`, o `device_id` no existe en la tabla) → 503, mismo status code que el 503 de "sin versión activa" (el `detail` difiere pero el cliente no lo mira). |
| `POST /api/verify` | LicenseManager (visor, licenciamiento por dispositivo — en implementación paralela en Unity) | Body `{device_id, current_apk_version?}` (`current_asset_version` retirado — vestigio Godot, Pydantic v2 ignora extras así que un cliente viejo que lo siga mandando no rompe). 403 plano `{status, reason, message}` con 5 `reason` posibles: `DEVICE_PENDING` / `DEVICE_REJECTED` / `DEVICE_NOT_FOUND` / `DEVICE_SUSPENDED` / `LICENSE_EXPIRED`; o `status: ok`. **Sin `response_model` a propósito** (ver Decisiones) — no confundir con las rutas `response_model` de arriba. **Rate-limited 10 req/min/IP.** Actualiza `last_seen`/`last_ip` y, si `current_apk_version` viene no-vacío, también `Device.last_apk_version` (en TODOS los caminos posteriores al lookup: pending/rejected/suspended/expired/ok y la fila reledía tras una carrera de auto-registro) — un valor ausente o `""` **no pisa** la última versión conocida (evita que un verify sin el campo "borre" el dato ya bueno). El shape de la respuesta no cambia; el dato es solo para el panel (`/admin/devices`). Orden de evaluación: `device_id` desconocido → **auto-registro** (ver abajo) → `pending` → `rejected` → `suspended` → `license_expiry` vencido → `ok`. **Auto-registro**: si el `device_id` no existe en BD y hay menos de `MAX_PENDING_DEVICES` (50, constante en `routers.py`) devices en estado `pending`, se crea uno nuevo (`name=f"Visor {device_id[:8]}"`, `status="pending"`, `notes="auto-registrado por verify"`, `last_apk_version` desde el body si vino) y se responde `DEVICE_PENDING`; si el tope está alcanzado, no se crea nada y responde `DEVICE_NOT_FOUND` (igual que antes de esta feature). Un device en `rejected` **nunca vuelve a auto-registrarse** (la fila ya existe, así que la rama de auto-registro no se ejecuta) — es terminal hasta que un admin lo edite a mano desde el panel. |
| `POST /api/log` | visor | Batch de eventos; acepta devices desconocidos (debugging). Sin rate limit. |
| `GET /files/{key}` | visor (descarga APK/PCK) | Proxy streaming a MinIO; así el manifest publica URLs `public_base_url/files/...` sin exponer MinIO ni firmar tokens. |
| `GET /healthz`, `GET /`, `GET /docs` | humanos/infra | Health, índice, Swagger. |

La URL que usa Unity está hardcodeada en `Assets/Scripts/Runtime/Data/DataManager.cs`: `backendUrl = "http://192.168.88.198:8080"` (IP de LAN de desarrollo) — no coincide con el `http://localhost:8080` del compose; ajustar según red.

### Auth y panel admin

- Login en `/admin/login` (form). `authenticate_user` → bcrypt (passlib); si ok, `create_session_token` emite un **JWT HS256** (`sub`=username, TTL 8 h, firmado con `jwt_secret`) guardado en cookie `admin_session` httpOnly/samesite=lax (`secure` solo bajo HTTPS).
- La dependency `get_current_admin` valida cookie+JWT+usuario en BD; si falla lanza `HTTPException(303, Location=/admin/login)` que el handler de `main.py` convierte en redirect.
- Secciones: dashboard (contadores, versión activa **de cada app** (visor/tablet) + catálogo activo, últimos logs, avisos si `admin123`/JWT secret por defecto siguen configurados, **aviso de devices `pending` con link a `/admin/devices` si hay al menos uno**), devices (CRUD + **aprobar/rechazar**, ver abajo), lenses (crear/activar catálogos, editor visual sobre el JSON activo), versions (selector de `app` visor/tablet, upload de APK a MinIO con SHA256 al vuelo — persistido en `apk_sha256` —, activar/borrar; activar/subir desactiva solo las versiones previas del MISMO canal), provisioning (QR de Android Enterprise para recuperación remota de tablets Device Owner — ver más abajo), logs (filtros, paginación 50, export CSV, nota de retención — ver más abajo).
- **`/admin/devices` (licenciamiento por dispositivo)**: listado ordenado con los `pending` primero (badge `Pendiente`), luego el resto por `created_at` desc; los `rejected` muestran badge `Rechazado`. Cada fila es una tarjeta con el patrón **resumen read-only + expandir para editar** (mismo patrón que `/admin/lenses`, ver abajo): la fila colapsada muestra `device_id`, nombre, badge de estado, última conexión/IP/**versión de APK reportada** (`Device.last_apk_version`, `—` si NULL — dato de solo lectura, reportado por el propio visor en `verify`, no aparece en el form de edición)/vencimiento y los botones de acción SIEMPRE visibles (no hace falta expandir para usarlos); al hacer click en el cuerpo de la fila se expande un panel con el form de edición (nombre, estado, vencimiento) + botón Guardar. Implementado con `<div onclick="toggleDeviceCard(this)">` (no `<details>`/`<summary>` nativo, para evitar el conflicto de un `<button type="submit">` disparando a la vez el submit y el toggle del acordeón) — los botones de acción viven en un contenedor con `onclick="event.stopPropagation()"` para no togglear el acordeón al usarlos. Filas `pending` tienen botones **Aprobar** (`POST /admin/devices/{id}/approve` → `status="active"`) y **Rechazar** (`POST /admin/devices/{id}/reject` → `status="rejected"`), siempre visibles en el resumen (no requieren expandir), además del form de edición en el panel expandido. El select de status (create y edit) incluye las 4 opciones: `active/pending/suspended/rejected`; la clase CSS `.select` tiene `min-width: 140px` para que nunca se vea recortado dentro de una celda angosta. Checkbox `ota_enabled` ("OTA del backend habilitado") en el form de alta y en el panel de edición, mismo patrón exacto que `is_admin` (checkbox HTML plano, `bool(form_value)` server-side, badge `badge-ok` en el resumen SOLO si está en `True` — a diferencia de `is_admin` no depende de `app_mode`, es independiente); hint debajo del form de edición aclarando que los devices en kiosco de Meta van con esto desactivado — ver §OTA por-dispositivo abajo para el mecanismo completo. Hint visible en la sección: *"Para deshabilitar un dispositivo use 'suspendido'; si lo borra, se re-registrará como pendiente al reconectar"* — importante porque **borrar no es lo mismo que suspender**: un device borrado, si el visor sigue mandando `POST /api/verify` con ese `device_id`, vuelve a pasar por la rama de auto-registro y reaparece como `pending` (no permanece "eliminado").
- **Retención de logs (`/admin/logs` y `POST /api/log`)**: `UpdateLog` se purga automáticamente cuando supera `LOG_RETENTION_DAYS` (default 30, `.env`). La purga corre (a) siempre en el arranque de la app (`main.py` lifespan) y (b) en cada `POST /api/log`, con throttle de 1h vía un timestamp de módulo en memoria (`_last_log_purge_at` en `app/routers.py`) — asume **proceso único** (un solo worker uvicorn); con múltiples workers cada uno tendría su propio timestamp y podría purgar más seguido de lo esperado (no es un problema hoy, el compose corre un solo worker). El panel `/admin/logs` muestra una nota discreta ("Se conservan los últimos N días...") con el valor de `LOG_RETENTION_DAYS` vigente.
- i18n es/en propio (diccionario en `admin/i18n.py`, cookie `admin_lang`), sin gettext.
- **`/admin/provisioning` (QR de Android Enterprise, recuperación remota de tablets)**: si una
  tablet del exterior (`com.simulador.tablet`, provisionada como Device Owner) sufre un factory
  reset, la recuperación remota es que el cliente haga 6 taps en la pantalla de bienvenida del
  asistente de Android y escanee un QR — esta página lo genera. `GET /admin/provisioning` (sin
  parámetros) renderiza la página **base** (info de la versión + form, sin QR de WiFi); el form
  manda por **`POST /admin/provisioning`** (ver el porqué del método más abajo). Ambos comparten
  `_provisioning_context()` (`app/admin/router.py`), que toma la `Version` **activa del canal
  `tablet`** y arma el JSON de provisioning
  (`app/admin/provisioning.py::build_provisioning_payload`):
  `PROVISIONING_DEVICE_ADMIN_COMPONENT_NAME` fijo
  (`com.simulador.tablet/com.simulador.kiosk.SimuladorDeviceAdminReceiver`, constante única en
  ese módulo), `PROVISIONING_DEVICE_ADMIN_PACKAGE_DOWNLOAD_LOCATION = version.apk_url` (el mismo
  `/files/apk/tablet/...` público, sin auth, que ya usa el manifest — cumple el requisito del
  asistente de descargar el APK sin login), `PROVISIONING_SKIP_ENCRYPTION=true` y
  `PROVISIONING_LEAVE_ALL_SYSTEM_APPS_ENABLED=true` (este último **obligatorio**: sin él Android
  deshabilita apps de sistema no esenciales, incluida `com.android.settings`, que la tablet
  necesita en el allowlist de lock task para el panel de WiFi — ver `docs/tablet.md`).
  **Dos checksums posibles, uno solo por QR**: si `settings.provisioning_signature_checksum`
  está configurado (`.env`, ver tabla arriba) se manda como
  `PROVISIONING_DEVICE_ADMIN_SIGNATURE_CHECKSUM` (checksum del **certificado de firma** del
  proyecto — constante entre releases, así un QR ya mandado por mail sigue siendo válido aunque
  se publique una versión nueva de la tablet); si NO está configurado, se calcula
  `PROVISIONING_DEVICE_ADMIN_PACKAGE_CHECKSUM` desde `version.apk_sha256` (hash del APK completo,
  hex) convertido a **Base64 URL-safe SIN padding**
  (`base64.urlsafe_b64encode(bytes.fromhex(sha)).rstrip(b"=")` — NO hex crudo, es el formato que
  exige el asistente), pero ese checksum caduca en cuanto se sube una versión nueva del APK.
  **Validación del checksum de paquete (nunca 500)**: `provisioning.py::has_usable_checksum()`
  exige que, si NO hay checksum de firma configurado, `version.apk_sha256` sea hex de 64
  caracteres (`_is_valid_apk_sha256`) — la `Version` dummy del seed trae `apk_sha256 == ""` y un
  valor corrupto/no-hex también es posible con la BD editada a mano; ambos casos, sin este
  chequeo, o renderizaban un QR con `PACKAGE_CHECKSUM` vacío sin avisar o tiraban `ValueError` →
  500 desde `bytes.fromhex`. El router llama `has_usable_checksum()` ANTES de
  `build_provisioning_payload`: si no hay ningún checksum usable, `_provisioning_context()` pone
  `ctx["no_checksum"] = True` y la plantilla muestra el banner `prov.no_checksum` ("La versión
  activa no tiene un checksum válido (subí un APK real o configurá
  PROVISIONING_SIGNATURE_CHECKSUM)") **sin generar QR** — mismo patrón que `prov.no_active`
  cuando no hay versión activa del canal tablet.
  Campos opcionales del form (**`POST`, nunca persistidos**): `wifi_ssid`/`wifi_password` →
  `PROVISIONING_WIFI_SSID`/`PROVISIONING_WIFI_PASSWORD` + `PROVISIONING_WIFI_SECURITY_TYPE`
  (solo si `wifi_ssid` viene no vacío; **`"WPA"` si además vino `wifi_password`, `"NONE"` si
  vino el SSID sin password** — mandar `"WPA"` sin `PROVISIONING_WIFI_PASSWORD` le pide al
  asistente una clave que nunca se le va a dar y la conexión falla), `locale` →
  `PROVISIONING_LOCALE` (importa: la tablet elige su idioma por el del sistema), `timezone` →
  `PROVISIONING_TIME_ZONE`. El contenido del QR es el JSON compacto
  (`json.dumps(payload, separators=(",", ":"))`) renderizado como **SVG inline** con la librería
  **`segno`** (pura Python, sin dependencias nativas, sin PIL — la única alternativa evaluada,
  `qrcode[qrcode.image.svg]`, también es pura Python pero `segno` ya cubre el caso sin sumar una
  segunda librería de imágenes; el SVG inline evita servir un archivo/JS extra solo para esto),
  corrección de errores `"q"` (quartile, ~25%) porque el payload es largo (URL absoluta +
  checksum + WiFi opcional) y ese nivel da margen razonable sin inflar demasiado la grilla.
  **Por qué el form es `POST` y no `GET`** (fix de raíz de un hallazgo de revisión): un `GET`
  pone `wifi_password` en la query string, que queda en el historial del navegador, en el
  `Referer` de cualquier request saliente desde esa página, y — el problema real que se pasó por
  alto la primera vez — en el **access log de Caddy**, que está DELANTE de esta app
  (`reverse_proxy api:8000` en `backend/Caddyfile`) y loguea la URI completa **sin que ningún
  filtro de la app pueda tocarlo** (Caddy nunca ve el body de un POST, así que ahí no hay nada
  que redactar). Mover el form a `POST /admin/provisioning` (campos en el body) saca el secreto
  de los tres lugares de una: es el fix de raíz, no un parche de logging. Verificado en vivo:
  `docker compose logs caddy` para un `POST /admin/provisioning` con `wifi_password` en el body
  mostró `"uri": "/admin/provisioning"` (sin query string, sin el password en ningún lado de la
  línea); `GET /admin/provisioning` (sin parámetros) sigue funcionando para la página base.
  **Gotcha de logging (ahora defensa en profundidad, no el fix)**: el access log DEFAULT de
  uvicorn imprime la URL completa; si alguien pega `?wifi_password=...` a mano en un `GET` (la
  ruta ya no declara esos parámetros, pero FastAPI no rechaza query params extra), `app/main.py`
  sigue registrando un `logging.Filter` sobre `uvicorn.access` (`_RedactWifiPasswordFilter`) que
  reemplaza `wifi_password=...` por `wifi_password=REDACTED` en la línea de acceso — no toca la
  request en sí, solo lo que queda en el log de la app (a Caddy, adelante, no le llega este
  filtro; por eso el fix real es el `POST`, esto es solo el cinturón además de las breteles).
  Verificado en vivo: un `GET /admin/provisioning?wifi_ssid=Foo&wifi_password=...` armado a mano
  mostró `wifi_password=REDACTED` en `docker compose logs api` (y ese `GET`, al no declarar los
  parámetros, ni siquiera arma los campos de WiFi en la respuesta).

### Seed del catálogo

`_seed_lens_catalog` lee `/seed/lentes.json` (volumen desde `defaults/lentes.json`; fallback inline mínimo si no está montado). Lógica de promoción: si el catálogo activo en BD tiene una versión listada en `_KNOWN_SEED_VERSIONS` (`0.0.1-seed`, `0.1.0-fallback`, `0.2.0-noche`, `0.3.0-clinical`, `0.4.0-clinical`, `0.4.0-fallback`, `0.5.0-clinical`, `0.5.1-clinical`, `0.6.0-clinical`, `0.6.1-clinical`, `0.7.0-clinical`) se considera seed no editado y se reemplaza por la versión nueva del JSON; si NO está en esa lista (p. ej. cualquier versión `.aN` de edición admin, ver P7.1 abajo), se asume edición manual del admin y **no se pisa**. El fallback inline (1 lente, sin `straylight` ni `astig_*`) usa su propia versión `0.4.0-fallback` — nunca la versión clínica real — precisamente para que, si el volumen aparece más tarde con el catálogo completo, la promoción se dispare (versiones distintas) en vez de hacer short-circuit por igualdad de versión con contenido mentido. **Cada versión nueva de `defaults/lentes.json` debe agregarse a `_KNOWN_SEED_VERSIONS`** (`backend/api/app/seed.py`) o no se auto-promueve (verificado en vivo al pasar de `0.4.0-clinical` a `0.5.0-clinical`: `docker compose logs api` mostró el reemplazo del catálogo y `GET /api/lenses` devolvió la versión nueva con `astig_magnitude`/`astig_axis_deg`).

## OTA por-dispositivo (kiosco Meta vs. OTA propio)

La flota real corre en su mayoría en modo kiosco gestionado por **Meta Horizon Managed
Services (HMS)**: se actualiza publicando el APK en el Admin Center de Meta (URL auto-hospedada
+ SHA256 fijado), fuera del control de este backend. Solo un puñado de Quest de desarrollo
deben seguir recibiendo el OTA propio (`UpdateManager` + `GET /api/manifest.json`,
`docs/updates.md`). `Device.ota_enabled` (bool, default `False`) es el gate: **la mayoría de la
flota va en `False` a propósito** — las excepciones se marcan a mano desde `/admin/devices`.

- **Mecanismo de supresión reusado, no inventado**: `UpdateManager.CheckManifest` (Unity) ya
  trataba un 503 como "no hay update disponible" **en silencio** (loguea con `Debug.Log`, sin UI
  ni error — ver `docs/updates.md` §Gotchas "`503` es un caso NORMAL, no un error"). Alcanza con
  que el backend devuelva ese MISMO 503 a los devices con `ota_enabled=False`; no hizo falta
  ningún cambio en Unity para el mecanismo de supresión en sí (el `&device_id=` en la URL sí es
  un cambio de Unity, coordinado aparte).
- **Tabla de decisión de `GET /api/manifest.json?device_id=`** (evaluada en ese orden):

  | Caso | Respuesta |
  |---|---|
  | `app == "tablet"` | **200** siempre — la tablet nunca está en `devices` (sin gate de licencia), se actualiza siempre por backend. |
  | `device_id` ausente o vacío | **200** (compat retro, ver gotcha abajo) |
  | `device_id` existe y `ota_enabled == True` | **200** |
  | `device_id` existe y `ota_enabled == False` | **503** |
  | `device_id` NO existe en `devices` | **503** |

  El 503 nuevo es indistinguible en status code del 503 preexistente de "no hay versión activa
  para este canal" (`detail` distinto, pero `UpdateManager` no lo lee). El 422 del `Literal` de
  `app` no se toca.
- **Gotcha no obvio — por qué "`device_id` ausente → 200" y no 503**: el APK 0.6.1 ya instalado
  en campo (build previa a esta feature) NO manda `device_id` en la URL del manifest — ese
  parámetro se agrega recién en el build siguiente (cambio coordinado del lado Unity,
  `DataManager.cs`/`UpdateManager.cs`). Si esa fila devolviera 503, ningún visor con 0.6.1
  llegaría a enterarse jamás del release que agrega el `device_id`, autobloqueando el propio
  mecanismo de OTA que se supone habilita esta feature. Es la fila de compatibilidad hacia atrás,
  temporal por naturaleza (una vez que toda la flota de excepción corre un build con
  `device_id`, deja de ser necesaria pero no hace daño dejarla).
- **Panel**: checkbox `ota_enabled` en `/admin/devices` (alta y edición), mismo patrón que
  `is_admin` — ver arriba.
- **Impacto en Unity**: NINGUNO en el schema de `lentes.json` ni en `/api/lenses`. El
  `&device_id=` que agrega `UpdateManager.CheckManifest` a la URL del manifest es un cambio
  aditivo (query param opcional) — un cliente que no lo mande sigue recibiendo 200 (fila de
  compat de arriba), así que no hay breaking change ni siquiera para builds ya desplegadas.

## Decisiones y porqués

- **Compose con 4 servicios y Caddy delante** → TLS automático (Let's Encrypt) en prod sin tocar la app; en local, `DOMAIN` vacío + `SCHEME=http://` permiten acceder por IP de LAN desde el Quest.
- **`${DOMAIN-localhost}` (guion, no `:-`) en el compose** → un `DOMAIN=` vacío en `.env` NO cae al default; ese vacío es justamente el modo "escuchar en cualquier hostname".
- **Alembic en vez de `create_all`** → el arranque corre `run_migrations()` (`app/migrations.py`). Adopta BDs existentes creadas por versiones previas (`create_all`, sin Alembic) detectando tablas de la app sin `alembic_version` y haciendo `stamp` a `_INITIAL_REVISION` (`"0001"`) — **no a `head`** — antes de un `upgrade head` que aplica cualquier migración posterior a esa. Estampar `head` directamente sería incorrecto en cuanto exista una revisión posterior a la inicial: la BD vieja quedaría marcada como si ya tuviera ese DDL sin haberlo corrido (columnas faltantes silenciosas). Esto dejó de ser hipotético con `0002_versions_per_app`: verificado en vivo sobre una BD real ya estampada a `0001` (`docker compose logs api` mostró `Running upgrade 0001 -> 0002, versions per app` y el resto del arranque siguió normal), confirmando que el mecanismo de adopción sigue funcionando ahora que `head != "0001"`. `init_db()` (`create_all`) sigue existiendo solo como fallback para los tests con SQLite en memoria.
- **Proxy `/files/<key>` en vez de URLs directas a MinIO** → MinIO no se expone al exterior y las URLs del manifest no necesitan tokens firmados.
- **JWT en cookie httpOnly para el admin (no Bearer)** → el panel es server-rendered (Jinja2+HTMX); la cookie evita manejar tokens en JS y `httponly` mitiga XSS.
- **CORS con `allow_origins` configurable (`CORS_ORIGINS`, default `*`) y `allow_credentials=False` siempre** → Starlette no permite combinar wildcard con credenciales (era la incoherencia previa). El panel `/admin` es server-rendered y va por cookie de mismo origen (no necesita CORS); la API pública la consume Unity (`UnityWebRequest`), que no es un browser y no aplica CORS. Por eso `allow_credentials=False` es seguro incluso con wildcard; si algún día un browser cross-origin necesita pegarle a `/api/*` con credenciales, hay que fijar `CORS_ORIGINS` a orígenes explícitos (no basta con la lista, Starlette también exige `allow_credentials=True` en ese caso).
- **`/api/verify` rate-limited a 10/min/IP** (antes 1/min) → 1/min rompía con NAT: varios visores de una misma clínica comparten IP pública y se bloqueaban entre sí. 10/min sigue siendo anti brute-force razonable; `/api/log` sin límite porque un update legítimo emite varios eventos.
- **`/api/verify` sin `response_model` (deliberado)** → a diferencia de `/api/manifest.json` y `/api/lenses` (que sí tienen `response_model` y por eso sus 503 se convirtieron de `JSONResponse` cruda a `HTTPException` para que el schema de OpenAPI sea fiel), `/api/verify` devuelve formas distintas en éxito (`VerifyResponse`) y en 403 (`VerifyDenied`, plano, sin nested `detail`) — es el contrato documentado para el futuro `LicenseManager` de Unity. Convertirlo a `HTTPException(403, detail=...)` anidaría la respuesta bajo `"detail"` y rompería esa forma, así que se dejó como estaba (deuda de fidelidad de OpenAPI, no de comportamiento).
- **Licencias permanentes por defecto (`license_expiry` NULL)** → decisión de Sprint 0. El **pre-registro manual de devices** de Sprint 0 (desconocido = 403 directo) se reemplazó por **auto-registro + aprobación** (ver más abajo): sigue siendo 403 para un device desconocido, pero ahora crea la fila en `pending` en vez de exigir que el admin la cree a mano antes de que el visor exista.
- **Auto-registro de devices (`status="pending"`) en `/api/verify` en vez de seguir exigiendo pre-registro manual** → con licenciamiento por dispositivo real (Unity llama `/api/verify` al arrancar y se bloquea si no está autorizado), forzar al admin a copiar a mano el `device_id` de cada visor nuevo antes de que pueda siquiera intentar conectarse era fricción operativa innecesaria; ahora el visor "se presenta" solo y el admin solo aprueba/rechaza desde el panel. `MAX_PENDING_DEVICES = 50` (constante en `routers.py`) evita que un actor malicioso bombardee el endpoint con `device_id`s aleatorios y llene la tabla `devices` sin límite — una vez alcanzado el tope, un `device_id` nuevo vuelve al `DEVICE_NOT_FOUND` de antes (sin crear fila) hasta que el admin libere cupo (aprobando/rechazando/borrando pendientes). El `reason` `DEVICE_REJECTED` es **terminal a propósito**: como el auto-registro solo dispara cuando la fila NO existe, un device ya `rejected` nunca vuelve a pasar por esa rama — queda bloqueado hasta que un admin lo edite manualmente (no hay forma de "reintentar" el registro sin intervención humana).
- **Seed idempotente con lista de versiones "conocidas"** → permite actualizar el catálogo por git pull + restart sin pisar recalibraciones hechas desde el panel.
- **`datetime.utcnow()` reemplazado por `utils.utcnow()` (naive UTC), no por `datetime.now(timezone.utc)` aware** → evita mezclar naive/aware en las columnas `timestamp without time zone` ya existentes en Postgres sin cambiar el formato guardado ni arriesgar comparaciones (ninguna hoy compara estos campos, pero es la opción de menor riesgo hacia adelante).
- **Uploads acumulados en memoria (`storage.py`)** → simplicidad; un APK ~50-100 MB es aceptable, migrar a multipart si crece.
- **`Version` partida por `app` ("visor"/"tablet"), PCK eliminado (migración `0002_versions_per_app`)** → el modelo original (una sola versión activa global, con `asset_version`/`pck_url`/`pck_sha256`) era herencia directa del prototipo Godot, que empaqueta assets en un `.pck` separado del ejecutable. Unity no tiene ese concepto (todo va en el APK/AAB) y el simulador pasó a ser DOS apps Android independientes con ciclos de release propios (visor Quest y tablet), así que "una versión activa" dejó de tener sentido sin decir de qué app. Se resolvió con **una versión activa por canal** (`Version.app`, index) en vez de arrastrar un campo `pck_*` muerto. Fue un breaking change deliberado del shape de `/api/manifest.json`: se aprovechó porque **nada consume el manifest todavía** (Unity aún no tiene `UpdateManager`) — riesgo cero, sin necesidad de versionar el endpoint. `ManifestResponse` ahora es `{app, apk_version, min_apk_version, apk_url, apk_sha256, changelog}` (antes tenía `current_apk_version`/`current_asset_version`/`pck_url`/`pck_sha256`, sin `app`). El panel (`/admin/versions`) subió un selector de canal y el upload/activate/delete solo tocan versiones del MISMO `app` (antes desactivaban TODAS globalmente). El SHA256 del APK, que antes se calculaba y se **descartaba** (`_apk_sha256` sin usar; solo el PCK se persistía), ahora se guarda en `apk_sha256`.

## Gotchas

- **El puerto `127.0.0.1:9001` (consola MinIO, mapeo fijo en `docker-compose.yml`) puede estar ocupado por un proceso ajeno a este proyecto en la máquina de desarrollo** — `docker compose up` falla con `ports are not available: ... bind: Only one usage of each socket address...` en el servicio `bucket`. No es un bug del compose; es una colisión de puerto local. Workaround verificado: crear un override **temporal** (ej. `docker-compose.override.local-tmp.yml`) que remapee `bucket.ports` a otro puerto host usando `!override` (el merge de listas de Compose por defecto **concatena** en vez de reemplazar — sin `!override` termina publicando AMBOS puertos y el original sigue fallando), levantar con `-f docker-compose.yml -f docker-compose.override.local-tmp.yml`, y **borrar el archivo al terminar** (no es config del repo, es solo para destrabar una corrida local puntual).

- **`defaults/lentes.json` es contrato compartido con Unity.** `CatalogParser.cs` (Unity) parsea el mismo schema `{version, catalogo:[{id, nombre, descripcion, params:{clave:{default,min,max}}}]}` que sirve `/api/lenses` y que siembra el seed. Cambiar claves o estructura exige tocar **ambos lados** (backend/defaults y `Assets/Scripts/Runtime/Data/CatalogParser.cs` + `CatalogModel.cs` + `Assets/StreamingAssets/lentes.json`). Unity tolera params NUEVOS sin recompilar (`MergeMissingParams` completa faltantes desde los defaults embebidos), pero renombrar o cambiar tipos rompe.
- **`admin/admin123` y `dev-jwt-secret-change-me` jamás a producción.** Son los defaults de `config.py`/compose/`.env.example`. El dashboard muestra un aviso si siguen activos, pero nada lo impide. No hay UI de cambio de contraseña: rotar vía `.env` **antes del primer arranque** (el seed solo crea el user si no existe; cambiar `.env` después no actualiza el hash en BD — hay que borrar el user o la BD).
- **Si el catálogo activo fue editado desde el panel, el seed nunca más lo actualiza** (versión fuera de `_KNOWN_SEED_VERSIONS`). Deseado, pero sorprende: subir un `defaults/lentes.json` nuevo no cambia nada hasta activar manualmente un catálogo en `/admin/lenses`. Además, cada versión de seed nueva debe agregarse a `_KNOWN_SEED_VERSIONS` o no se auto-promoverá en la siguiente.
- **El device de test `DEV_TEST_001` y las `Version` dummy (una por app: visor/tablet)** las crea el seed siempre; eliminarlas antes de producción (los dummy tienen URLs `/dummy/...` inexistentes y `apk_sha256` vacío).
- **El dummy de `Version` usa a propósito `apk_version == min_apk_version == "0.1.0"` (el mismo `bundleVersion` que hoy tiene el proyecto Unity)** para que, si el futuro `UpdateManager` llegara a consultar el manifest contra un dummy sin querer, nunca dispare el cartel de "hay una actualización" (versión instalada == versión mínima requerida). Si el `bundleVersion` base de Unity cambia, hay que actualizar `_DUMMY_APK_VERSION` en `seed.py` a mano.
- **En un backend que ya corrió antes de la migración `0002`, la fila vieja de `versions` queda con `app="visor"`** (el `server_default` de la migración, no una decisión de negocio) — el seed post-migración entonces solo agrega la dummy que falta (`tablet`), nunca toca la fila `visor` existente. Verificado en vivo: `docker compose logs api` mostró `[seed] version dummy creada: app=tablet APK v0.1.0` sin ninguna línea equivalente para `visor` (porque ya tenía una activa).
- **`/api/verify` devuelve 429 después del 10º intento en menos de 1 min** desde la misma IP (slowapi); al probar con curl en loop rápido parece "roto" sin serlo.
- **Auto-registro de devices: carrera resuelta con `try/except IntegrityError` sobre el `commit`, no con locking.** Dos requests concurrentes con el mismo `device_id` desconocido pueden pasar ambas el `SELECT` inicial (`device is None`) antes de que cualquiera haga `commit`; el `unique` de `Device.device_id` hace que el segundo `commit` falle con `IntegrityError`. El handler lo atrapa, hace `session.rollback()`, releé la fila que "ganó" la carrera y sigue el flujo normal evaluando SU status (en la práctica casi siempre `DEVICE_PENDING`, el estado inicial del auto-registro) — así el perdedor de la carrera responde igual que si el device ya hubiera existido, en vez de un 500. Verificado con un test que fuerza la interleaving parcheando `Session.commit` (`test_verify_auto_register_race_recovers_without_500` en `test_public_api.py`) para que una sesión aparte inserte y comitee la fila ganadora justo antes del `commit` del propio request — el `IntegrityError` que se dispara después es real (SQLite/Postgres), no simulado.
- **Adopción de Alembic en BD existente**: la detección es "hay tablas de la app pero no `alembic_version`" → `stamp` a `_INITIAL_REVISION` (`"0001"`, constante en `app/migrations.py`), seguido de `upgrade head`. Esto asume que toda BD sin `alembic_version` tiene el schema exacto de `0001` (cierto hoy, es lo único que generaba `create_all`). Si algún día existiera una BD pre-Alembic con un schema *distinto* al de `0001` (no debería pasar, pero), este mecanismo no lo detecta — asume `0001` siempre.
- **Tests (`backend/api/tests/`) fuerzan `DATABASE_URL=sqlite:///:memory:` en `conftest.py` antes de importar `app.*`** — si se agregan nuevos tests que importan módulos de la app fuera de ese conftest (o se reordena la colección de pytest), revisar que el env var siga fijándose primero; `Settings()` es un singleton leído una sola vez al importar `app.config`.
- **El fixture `client` de `conftest.py` llama `limiter.reset()` antes de cada test** — el `app` de FastAPI y el `Limiter` de slowapi son singletons de módulo (importados una sola vez por sesión de pytest, no recreados por test), así que su storage en memoria persistía entre tests: sin el reset, cualquier test que llamara `/api/verify` (rate-limited 10/min/IP, y `TestClient` siempre reporta la misma IP) heredaba la cuota ya consumida por tests anteriores en la misma sesión y podía fallar con 429 inesperado. Si se agrega un endpoint rate-limited nuevo con tests propios, tenerlo en cuenta.
- **El access log DEFAULT de uvicorn imprime la URL completa, incluida la query string — y Caddy, delante de la app, hace lo mismo con la SUYA propia y a esa la app no le llega ni con un `logging.Filter`.** Cualquier endpoint admin que recibiera un dato sensible por `GET ?query` quedaría en texto plano en `docker compose logs api` (uvicorn) Y en `docker compose logs caddy` (reverse proxy) si no se hace nada. Por eso `/admin/provisioning` resolvió esto en la RAÍZ: el form de WiFi es `POST` (body, no query string — ver "Auth y panel admin" > Provisioning más arriba), no un filtro de logging. El filtro que SÍ existe (`_RedactWifiPasswordFilter` en `app/main.py`, agregado al logger `uvicorn.access`, `record.args = (client_addr, method, full_path, http_version, status_code)`) es **defensa en profundidad** para el caso residual de un `GET` armado a mano con `?wifi_password=...` en la URL — cubre uvicorn, NUNCA Caddy (Caddy no re-envía sus logs por la app, así que no hay dónde engancharle un filtro Python). Si se agrega OTRO parámetro sensible por query en el futuro: primero preguntarse si puede ir por `POST` (fix de raíz); si de verdad tiene que ir por `GET` (ej. para ser bookmarkeable), extender el filtro de uvicorn sabiendo que Caddy queda sin cubrir.
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
curl "http://localhost:8080/api/manifest.json?app=tablet&device_id=NO_EXISTE"   # 200 (tablet siempre)
curl "http://localhost:8080/api/manifest.json?app=visor"                       # 200 (sin device_id, compat)
curl "http://localhost:8080/api/manifest.json?app=visor&device_id=DEV_TEST_001" # 503 (ota_enabled=False por default)
curl "http://localhost:8080/api/manifest.json?app=visor&device_id=NO_EXISTE"    # 503 (device no existe)
# tras UPDATE devices SET ota_enabled=true WHERE device_id='DEV_TEST_001' (o desde /admin/devices):
curl "http://localhost:8080/api/manifest.json?app=visor&device_id=DEV_TEST_001" # 200
curl -X POST http://localhost:8080/api/verify \
     -H "Content-Type: application/json" -d '{"device_id":"DEV_TEST_001"}'
# → {"status":"ok","device_name":"Visor de desarrollo",...}
curl -X POST http://localhost:8080/api/verify \
     -H "Content-Type: application/json" -d '{"device_id":"NOEXISTE"}'
# → 403 DEVICE_PENDING la 1ra vez (auto-registro, salvo tope de 50 pending
#   alcanzado → DEVICE_NOT_FOUND); llamadas repetidas del mismo device_id no
#   duplican fila y siguen DEVICE_PENDING hasta que un admin apruebe/rechace
#   desde /admin/devices (10 req/min/IP de cuota antes del 429 de slowapi)
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

## P7: modos Standard/Pro, lentes custom y administradores

- **`Device` suma `app_mode`** (`"standard"|"pro"`, default standard) **e `is_admin`** (bool):
  editables en `/admin/devices` (select + checkbox, badges en el resumen). El verify OK los
  devuelve (`app_mode`/`is_admin`) — contrato en `docs/licenciamiento.md`.
  **`is_admin` implica `pro`**: admin habilita crear lentes genéricas, algo que la UI standard
  del visor ni expone — un standard+admin es un estado incoherente. La UI de `/admin/devices`
  oculta el checkbox cuando el select está en standard (JS `syncAdminField`, lo destilda al
  cambiar) y el server lo FUERZA en `devices_create`/`devices_edit`
  (`is_admin = bool(form) and app_mode == "pro"`) — la regla de integridad vive en el server,
  la UI es comodidad. El badge del resumen tampoco se muestra si el device no es pro (datos
  pre-regla).
- **Tabla `custom_lenses`** (migración `0004`; **desde P7.2 (ver más abajo) solo guarda
  CUSTOMS por device — las "genéricas" con `owner_device_pk IS NULL` dejaron de crearse
  acá**, ver P7.2). `owner_device_pk` es FK a `devices.id` (el PK int, NO el string
  `device_id`). `lens_id` (`custom_xxxxxxxx`/`generic_xxxxxxxx` — el prefijo `generic_` se
  conserva por historia aunque ya no viva en esta tabla, ver P7.2 — generado por el server
  con `secrets.token_hex(4)`) tiene UNIQUE global: sin colisiones con el blob base por
  construcción. `updated_at` alimenta el fingerprint de versión.
- **`GET /api/lenses?device_id=`** (query param opcional): merge = blob base (P7.2: incluye
  las ex-lentes "genéricas") + customs del device (solo si existe y está efectivamente
  activo — status active y licencia vigente; si no, responde como anónimo, lo que purga sus
  customs del cache del visor). La tablet sigue anónima (solo base). Versionado del merge:
  `"{base}+x{sha256(lens_id|updated_at)[:10]}"`; **sin extras devuelve la versión base literal**
  (compat con caches). Las extras llevan campo `"origen": "custom"` (P7.2: `"generic"`
  desapareció del contrato — una lente de admin ya es BASE, sin `origen`); las base no.
- **CRUD `/api/lenses/custom`** (30/min/IP): `POST` (body `{device_id, scope, nombre,
  descripcion, params}`, 201 con `{lens, catalog_version}`), `PUT /{lens_id}`,
  `DELETE /{lens_id}?device_id=`. Autorización: device efectivamente activo; privadas exigen
  `app_mode=pro` (o admin); `scope="generic"` exige `is_admin` (P7.2: agrega al catálogo BASE,
  ver más abajo — ya no es una fila `custom_lenses`); editar/borrar una custom exige ser dueño
  (`NOT_OWNER`); editar/borrar una lente del catálogo BASE exige `is_admin` (`NOT_ADMIN` — un
  Pro no-admin no puede tocarlas). Rechazos con shape de verify (`{status:"denied", reason,
  message}`): `DEVICE_NOT_FOUND`, `DEVICE_NOT_AUTHORIZED`, `MODE_NOT_PRO`, `NOT_ADMIN`,
  `NOT_OWNER`, `LENS_LIMIT_REACHED` (tope 50/device en `custom_lenses`, 409 — **P7.2: el tope
  de 100 genéricas se eliminó, el blob base no tiene límite de tamaño**). El reason
  `BASE_LENS` (P7.1) quedó **sin uso** desde P7.2 (ver más abajo) pero no se retira del
  vocabulario del contrato. Validación de params: ≤20 claves, `min<=default<=max` numéricos.
- **(P7.1) Edición de lentes BASE por un admin.** `PUT /api/lenses/custom/{lens_id}` suma
  una rama: si `lens_id` NO está en `custom_lenses` pero SÍ es el id de una lente del
  catálogo BASE activo, un device efectivamente activo + `is_admin` puede editarla
  (nombre/descripción/params, misma validación que una custom: ≤20 claves,
  `min<=default<=max`; sin `is_admin` → 403 `NOT_ADMIN`, igual que las genéricas). La base
  **nunca se pisa in-place**: la edición clona el catálogo activo entero en una fila NUEVA
  de `LensCatalog` con una versión `.aN` automática y la activa — la fila vieja queda
  desactivada pero **nunca se borra** (rollback manual desde `/admin/lenses`, botón
  "Activar" sobre la versión anterior; verificado en vivo). Esquema de versión: `root =
  versión activa sin sufijo ".aN" final` (regex `^(.*?)(\.a(\d+))?$`, en
  `_version_root_and_suffix`); nueva versión = `{root}.a{N+1}` donde `N` es el mayor
  sufijo existente **entre TODAS las filas de `LensCatalog`** (activas o no) con esa misma
  raíz — así ediciones encadenadas dan `.a1`, `.a2`, ... y nunca `.a1.a1` (verificado en
  vivo con dos `PUT` sucesivos sobre `monofocal`: `0.6.0-clinical` → `.a1` → `.a2`).
  (P7.1, histórico) `DELETE /api/lenses/custom/{lens_id}` sobre un id de lente BASE
  devolvía **siempre** 403 `BASE_LENS` — **superado por P7.2 (ver más abajo): un admin SI
  puede borrar cualquier lente del catálogo desde P7.2**, este párrafo queda como registro
  de la decisión original. El shape de respuesta del `PUT` (`{status, lens,
  catalog_version}`) es idéntico al de una custom exitosa — visor/tablet no tocan su
  parsing.
- **(P7.1) Trade-off del seed con versiones `.aN`**: `_seed_lens_catalog` solo reemplaza
  el catálogo activo si su versión está en `_KNOWN_SEED_VERSIONS`; una versión `.aN` de
  admin nunca está en esa lista (no se agrega a mano), así que queda protegida por
  construcción — un release futuro que traiga un `defaults/lentes.json` nuevo **NO se
  auto-promueve** mientras la activa sea una edición de admin (`.aN`); hay que activarlo a
  mano desde `/admin/lenses` (mismo mecanismo de rollback de arriba). Verificado en vivo:
  con el catálogo en `v0.6.0-clinical.a2`, un `docker compose restart api` logueó
  `[seed] catalogo activo v0.6.0-clinical.a2 NO es seed conocido; se respeta. JSON del
  repo (v0.6.0-clinical) ignorado.`
- **(4ª lente base `paciente_joven`, rollout a prod sin pisar ediciones admin)**: cuando
  `defaults/lentes.json` sumó `paciente_joven` (bump `0.6.0-clinical` → `0.6.1-clinical`,
  ver `docs/catalogo-lentes.md`), el backend de producción (`vr.conecta.sh`) ya tenía el
  catálogo BASE editado por un admin (`0.6.0-clinical.a16`, 16 ediciones acumuladas sobre
  `monofocal`/`panoptix`/`vivity` vía `PUT /api/lenses/custom/{lens_id}`) — activar un
  catálogo generado desde el `defaults/lentes.json` limpio habría pisado esas 16
  ediciones. Se resolvió tomando el blob BASE ACTIVO de prod (`LensCatalog.data` de la
  fila `is_active=True`, leído server-side vía el propio ORM de la app dentro del
  contenedor `api`, sin pasar por HTTP/admin ni exponer credenciales) y agregándole la
  entrada `paciente_joven` (idéntica a `defaults/lentes.json`) al final del array
  `catalogo`, con versión **`0.6.0-clinical.a17`** (continúa el linaje `.aN` existente,
  root `0.6.0-clinical` — mismo esquema que `_next_admin_lens_version`, calculado como
  `max(sufijo .aN entre TODAS las filas con esa raíz) + 1`; NO se usó `0.6.1-clinical`
  "limpio" a propósito: esa versión SÍ está en `_KNOWN_SEED_VERSIONS`, así que activarla
  tal cual habría dejado el catálogo mergeado vulnerable a que un futuro bump de
  `defaults/lentes.json` la reconozca como "seed sin editar" y la pise, perdiendo las 17
  ediciones de admin). La fila vieja (`.a16`, id 18) se desactivó pero no se borró (mismo
  mecanismo de rollback de siempre desde `/admin/lenses`); la(s) lente(s) genérica(s) en
  `custom_lenses` no se tocaron (esa tabla es independiente del blob base). Verificado en
  vivo: `GET /api/lenses` pasó de 4 a 5 lentes (3 base + 1 genérica → 3 base +
  `paciente_joven` + 1 genérica), con los valores editados por admin intactos
  (`monofocal.foco_lejos_m.default == 6.201871`, `panoptix.astig_magnitude.default ==
  0.709078431`) y la versión mergeada pasando a `0.6.0-clinical.a17+x...`.
- **(5ª lente base `catarata` + param `cataract_yellow`, rollout a prod sin pisar ediciones
  admin, 2026-07-21)**: mismo problema y misma solución que el rollout de `paciente_joven`
  de arriba, con una variante: entre la verificación previa a la tarea (`0.6.0-clinical.a33`)
  y la migración real, el catálogo de prod siguió recibiendo ediciones en vivo (el drag-reorder
  P7.3/P8 desde la tablet) y quedó en `0.6.0-clinical.a35` (id 37, orden reordenado
  `paciente_joven, monofocal, generic_a209ba91, panoptix, vivity`) — la migración se calculó
  contra ese estado REAL leído en el momento de correr (no contra el `.a33` de la verificación
  previa), confirmando que el mecanismo de `_next_admin_lens_version` (root + mayor sufijo
  `.aN` existente + 1) es robusto a edición concurrente mientras se recalcule en el momento de
  escribir, nunca hardcodeado. Pasos: 1) `pg_dump` completo a `/root/backups/` del VPS antes de
  tocar nada; 2) deploy por SFTP de `defaults/lentes.json` (`0.7.0-clinical`) y
  `backend/api/app/seed.py` (con `0.7.0-clinical` en `_KNOWN_SEED_VERSIONS`) + rebuild de la
  imagen `api` (el `Dockerfile` hace `COPY app ./app` en build, así que un simple
  `restart` NO recoge un `seed.py` nuevo — hace falta `docker compose build api && docker
  compose up -d api`); log del arranque confirmó `[seed] catalogo activo v0.6.0-clinical.a35
  NO es seed conocido; se respeta` (el deploy de archivos por sí solo no tocó nada); 3) script
  Python one-shot (`docker exec backend-api-1 python ...`, mismo engine/modelos SQLModel que la
  app) que: calcula el diff completo EN MEMORIA antes de escribir nada en la BD (si hay
  cualquier modificación no esperada, aborta sin commit), agrega `cataract_yellow` (`{default:
  0.0, min: 0.0, max: 1.0}`) al FINAL de los `params` de cada lente que no lo tuviera
  (incluida `generic_a209ba91`, la lente creada por el profesional que no existe en los
  defaults — verificada intacta salvo esa adición), agrega la lente `catarata` copiada
  verbatim de `/seed/lentes.json` al final del array, y solo entonces desactiva la fila vieja y
  activa una fila nueva con versión `.aN` calculada en el momento (`0.6.0-clinical.a35` → `id=38,
  0.6.0-clinical.a36`, NO se usó `0.7.0-clinical` limpio por la misma razón que el rollout de
  `paciente_joven`: esa versión está en `_KNOWN_SEED_VERSIONS`). `custom_lenses` no se tocó
  (count antes/después: 1/1). Verificado en vivo: `GET /api/lenses` en prod pasó de 5 a 6
  lentes, las 5 preexistentes con `cataract_yellow=0.0` y `catarata` con `cataract_yellow=0.6`,
  ninguna con campo `origen`, orden preexistente preservado y `catarata` al final; sin errores
  en `docker compose logs api`. La fila `.a35` (id 37) queda desactivada para rollback manual
  desde `/admin/lenses`, igual que siempre.
- **Panel**: página nueva `/admin/custom-lenses` (listar/filtrar por device/borrar, params
  read-only; P7.2: perdió el filtro por scope generic/private — esa tabla ya solo tiene
  customs privadas, ver más abajo); `devices_delete` borra las customs del device (cascade
  app-level + ON DELETE CASCADE). **Reemplazo de hardware**: `POST /admin/devices/{pk}/replace` re-apunta
  `device_id` de la fila (licencia/modo/lentes se conservan solas por colgar del PK); si el
  visor nuevo ya se auto-registró como pending sin lentes, ese placeholder se borra; un destino
  real (activo o con lentes) se rechaza. Resetea `last_seen/last_ip/last_apk_version` y audita
  en `notes`. El flush del delete va ANTES del update (unique de device_id — gotcha SQLAlchemy).
- **Seed**: no toca `custom_lenses` jamás (opera solo sobre `lens_catalogs`). `DEV_TEST_001`
  ahora es pro+admin. `_KNOWN_SEED_VERSIONS` incluye 0.5.1/0.6.0-clinical.
- **Seguridad (limitación aceptada)**: el `device_id` es el secreto de facto para el CRUD —
  mitigado por TLS en prod, id opaco no enumerable, rate limit y daño acotado (solo lentes).
  Mejora futura: token por device emitido en verify (ver Pendientes).

## P7.2: las lentes "genéricas" dejan de ser una categoría aparte (decisión de producto)

- **Cambio de modelo**: las lentes "genéricas" (antes: filas de `custom_lenses` con
  `owner_device_pk IS NULL`, visibles para todos, solo admin) **dejaron de existir como
  categoría separada**. Un admin que crea una lente con `scope="generic"` ahora la AGREGA
  directamente al **catálogo BASE** (blob versionado `lens_catalogs`, la mecánica `.aN` de
  P7.1) — es una lente BASE más desde el instante en que se crea: se sirve por
  `GET /api/lenses` **sin el campo `origen`** (igual que `monofocal`/`panoptix`/etc.), no
  depende de ningún device y sobrevive al borrado del device admin que la creó. La tabla
  `custom_lenses` pasa a ser **exclusivamente** las lentes CUSTOM privadas por device
  (`owner_device_pk` NUNCA debería ser NULL en filas creadas después de esta versión).
- **Ruta de alta**: `POST /api/lenses/custom` con `scope="generic"` (sigue exigiendo
  `is_admin`, `_authorize_lens_write(need_admin=True)`) llama a `_add_base_lens`: clona el
  catálogo activo, hace `catalogo.append({id, nombre, descripcion, params})` con un id
  `generic_{token_hex(4)}` (mismo esquema de id que P7, generado chequeando colisión contra
  los ids YA en el catálogo activo — no contra `custom_lenses`, que ya no aplica), calcula la
  siguiente versión `.aN` (`_next_admin_lens_version`, igual que `_update_base_lens`) y activa
  la fila nueva (la vieja se desactiva, nunca se borra — mismo mecanismo de historial/rollback
  de P7.1). Respuesta: `{status:"ok", lens:{id,nombre,descripcion,params}, catalog_version}`
  — **sin `origen`**, a diferencia de una custom privada creada por la misma ruta con
  `scope="private"` (esa sí lleva `"origen":"custom"`). El tope `MAX_GENERIC_LENSES` (100)
  se **eliminó**: el blob base no tiene límite de tamaño.
- **Ruta de borrado (decisión de producto nueva)**: `DELETE /api/lenses/custom/{lens_id}`
  sobre un id que está en el catálogo BASE activo ya **NO** rechaza siempre con `BASE_LENS`
  (eso era P7.1) — ahora, si el device es admin, `_delete_base_lens` clona el catálogo
  activo SIN esa lente (`del catalogo[idx]`), calcula la siguiente `.aN` y activa esa
  versión (mismo mecanismo de clon-versionado; la fila vieja con la lente intacta queda
  desactivada — el rollback es "activar la versión anterior desde `/admin/lenses`", no hay
  un endpoint de "deshacer" dedicado). Sin `is_admin` → 403 `NOT_ADMIN` (mismo código que
  cualquier otra escritura admin-only), NO `BASE_LENS`. Esto aplica a CUALQUIER lente del
  catálogo, incluidas `monofocal`/`panoptix`/`vivity`/`paciente_joven` — un admin puede
  borrar una lente base "de fábrica" igual que una agregada por otro admin; el historial de
  versiones (`/admin/lenses`, botón "Activar" sobre una versión previa) es la única red de
  seguridad, no hay confirmación extra a nivel backend.
- **`PUT /api/lenses/custom/{lens_id}` (editar) NO cambia**: sigue cayendo a
  `_update_base_lens` (P7.1) para cualquier id que esté en el blob activo, ya sea una lente
  "de fábrica" o una agregada por `scope="generic"` — ambas son indistinguibles una vez
  creadas (son lentes BASE). Solo admin, mismo `NOT_ADMIN` si no.
- **Migración `0005`** (`backend/api/alembic/versions/0005_generic_lenses_to_catalog.py`):
  mueve, para instalaciones que ya tenían lentes genéricas en `custom_lenses` (con
  `owner_device_pk IS NULL`), cada una de esas filas al array `catalogo` del blob activo
  (conservando `lens_id` como `id`, `nombre`, `descripcion`, `params`), en **una sola**
  versión `.aN` nueva (aunque haya varias genéricas — todas entran en el mismo `catalogo.
  append`), y borra esas filas de `custom_lenses`. Es **idempotente**: sin filas genéricas
  preexistentes, no-op (no crea una versión `.aN` sin motivo). Colisión defensiva: si el
  `lens_id` de una genérica ya está presente en el blob activo (no debería pasar por
  construcción del id, pero es defensivo), esa lente se SALTEA con un warning y queda en
  `custom_lenses` para revisión manual — no se pierde, tampoco se pisa un id existente a
  ciegas. Implementada con `sa.table`/`sa.column` (no los modelos SQLModel ni
  `autoload_with`) y una copia LOCAL del regex `_version_root_and_suffix` de
  `app/routers.py` — a propósito: una migración de datos no debe depender de código de la
  app que puede cambiar en el futuro. Se aplica sola al arrancar el contenedor
  (`run_migrations()`, Alembic `upgrade head`, mismo mecanismo que cualquier otra
  migración) — no requiere ningún paso manual adicional en una instalación nueva ni en una
  que ya tuviera datos; **no se corrió todavía contra el backend de producción** (`vr.conecta.sh`)
  como parte de esta tarea — coordinar ese `docker compose up`/restart en producción es un
  paso aparte.
- **Panel**: `/admin/custom-lenses` pierde el filtro por scope (`generic`/`private`/`all`):
  solo queda el filtro por device, porque la tabla ya solo tiene customs privadas. El badge
  "Genérica" del template (`owner_device_pk is none`) queda como indicador defensivo por si
  una fila vieja sobrevive (p. ej. una colisión que la migración `0005` saltea a propósito).
  `/admin/lenses` no cambia: sigue siendo el único lugar para ver el historial de versiones
  `.aN` y hacer rollback (activar una versión anterior) — es la mitigación real de "un admin
  borró una lente por error".
- **Impacto en Unity (contrato)**: el campo `"origen":"generic"` **desaparece** de
  `GET /api/lenses` — ninguna lente vuelve a llevarlo. `CatalogModel.cs`/`CatalogParser.cs`
  no necesitan cambios de schema (siguen tolerando `origen` ausente, que ya era el caso para
  las bases), pero cualquier lógica en Unity/tablet que ramifique sobre `origen == "generic"`
  específicamente (distinto de `"custom"` o ausente) queda muerta y debe revisarse en el
  lado Unity (`docs/tablet.md`/`docs/catalogo-lentes.md` §P7). El shape de `POST`/`PUT`/
  `DELETE` (`{status, lens?, catalog_version}`) no cambia.

## P7.3: reorden del catálogo BASE (drag & drop desde la tablet)

- **Nuevo endpoint `POST /api/lenses/reorder`** (30/min/IP, mismo rate limit que
  `/api/lenses/custom`): permite a un admin fijar el ORDEN del array `catalogo` del blob BASE
  activo — es el orden en el que Unity recibe las lentes en `GET /api/lenses` (drag & drop
  desde el panel/tablet). Body `LensReorderRequest = {device_id, order}` donde `order` es la
  lista de ids del catálogo BASE en el nuevo orden (`list[str]`, `max_length=200` como tope
  sano de payload). Autorización idéntica a `_update_base_lens`/`_add_base_lens`/
  `_delete_base_lens`: `_authorize_lens_write(need_admin=True)` (mismo shape de denegación:
  `DEVICE_NOT_FOUND` / `DEVICE_NOT_AUTHORIZED` / `NOT_ADMIN`).
- **Validación de permutación exacta** (`_validate_lens_order`): `order` debe tener
  EXACTAMENTE los mismos ids que el catálogo activo — sin duplicados, sin ids desconocidos,
  sin faltantes. Cualquier desvío → 422 con `detail` describiendo la causa puntual (mensajes
  distintos para duplicado / desconocido / faltante, en ese orden de chequeo). Las lentes
  CUSTOM por device NO participan de este orden: no viven en este array, y `get_lenses`
  siempre las agrega DESPUES de las lentes base en el merge, sin importar cómo estén
  ordenadas en `custom_lenses`.
- **No-op sin gastar `.aN`**: si `order` ya coincide con el orden activo (comparación de listas
  ítem a ítem), responde `{status:"ok", catalog_version}` SIN clonar una versión nueva de
  `LensCatalog` — evita quemar historial por un reorden que en la práctica no cambia nada
  (p. ej. el admin abre y cierra el editor de drag & drop sin soltar ningún cambio). Si cambia,
  usa el MISMO mecanismo de clon-versionado que `_update_base_lens`/`_add_base_lens`/
  `_delete_base_lens` (`_next_admin_lens_version`): la fila vieja se desactiva pero nunca se
  borra (rollback manual desde `/admin/lenses`, igual que el resto de P7.1/P7.2). Respuesta:
  `{status:"ok", catalog_version}` (sin `lens`, mismo shape que `DELETE`).
- **Verificado en vivo** contra el backend local con Postgres real (no solo SQLite de tests):
  `POST /api/lenses/reorder` con el orden invertido de las 4 lentes activas (`monofocal`,
  `panoptix`, `vivity`, `paciente_joven`) → `0.6.1-clinical` pasó a `0.6.1-clinical.a1` y
  `GET /api/lenses` devolvió el array en el nuevo orden; repetir el mismo POST con el mismo
  orden respondió `ok` sin generar `.a2`; un `order` con un id desconocido → 422; un device no
  registrado → 403 `DEVICE_NOT_FOUND`; `/admin/lenses` muestra ambas versiones
  (`0.6.1-clinical` desactivada, `0.6.1-clinical.a1` activa) — mismo mecanismo de rollback
  que P7.1/P7.2.
- **Impacto en Unity (contrato)**: NINGUNO. No cambia el schema de `lentes.json` ni el shape
  de `GET /api/lenses` (sigue siendo `{version, catalogo:[...]}`); el array `catalogo` ya se
  serializaba en el orden en que vive dentro del blob, así que `CatalogParser.cs` no necesita
  ningún cambio de código — simplemente puede empezar a recibir un orden distinto de lentes
  si un admin lo pidió desde la tablet.

## Pendientes / deuda

- `/api/verify` no tiene `response_model` (por diseño, ver Decisiones) — su 403/200 no aparecen tipados en el OpenAPI generado; documentado ahí, no se resuelve sin tocar el contrato con el futuro `LicenseManager` de Unity.
- ~~`/api/manifest.json` con shape nuevo (por app) todavía no tiene consumidor en Unity~~ — **resuelto**: `UpdateManager` (visor y tablet) ya lo consume, ver `docs/updates.md`.
- Endpoint `/api/admin/versions` con API key para CI/CD (Sprint 11) — no existe todavía; el `API_KEY_CI` que quedaba sin consumidor se quitó de `config.py`/compose/`.env.example` (si se implementa, agregar el setting de nuevo).
- UI de cambio de contraseña del admin (hoy solo vía `.env` + reseed).
- Uploads del panel acumulan el archivo completo en memoria; multipart si los binarios crecen.
- ~~(P6.9) Agregar `"0.5.1-clinical"` a `_KNOWN_SEED_VERSIONS`~~ — **resuelto**: el set ya
  incluye `0.5.1-clinical`, `0.6.0-clinical`, `0.6.1-clinical` y `0.7.0-clinical` (este último
  agregado al sumar el param `cataract_yellow` y la 5ª lente base `catarata`). Ver §Seed del
  catálogo arriba.
- **Pendiente para @backend-dev (etapa D del fix de óptica `cataract_scatter`, ver el plan
  `en-la-escena-de-snug-hinton.md`): agregar `"0.8.0-clinical"` a `_KNOWN_SEED_VERSIONS`
  (`backend/api/app/seed.py`)** — `defaults/lentes.json` y `Assets/StreamingAssets/lentes.json`
  ya llevan la versión `0.8.0-clinical` con el param nuevo `cataract_scatter` (Etapa A, cerrada
  por @unity-dev), pero **el lado backend (`seed.py`, `_KNOWN_SEED_VERSIONS` y el rollout a la DB
  de prod) NO se tocó en esta tarea** — sigue el mismo procedimiento que el rollout de
  `cataract_yellow` (ver §Seed del catálogo arriba: `pg_dump` antes de tocar nada, SFTP +
  `docker compose build api && docker compose up -d api`, script one-shot que agrega
  `cataract_scatter {0.0, 0.0, 1.0}` a cada lente que no lo tenga — incluida cualquier lente
  `generic_*`/custom creada por un admin desde la tablet, que `MergeMissingParams` de Unity
  nunca cubre porque no está en los defaults — y calcula la versión con
  `_next_admin_lens_version()`, nunca `0.8.0-clinical` limpio).
