# Backend FastAPI

## Qué es y por qué

Servicio central del simulador: sirve el catálogo de lentes al visor/tablet, verifica licencias por `device_id`, publica el manifiesto de actualizaciones (APK/PCK) y recibe logs de los visores. Incluye un panel de administración web. Es **opcional en runtime**: el visor funciona sin backend gracias al catálogo embebido y la caché local (`Assets/Scripts/Runtime/Data/DataManager.cs`); el sync es en segundo plano y nunca bloquea el arranque.

## Arquitectura actual

Stack: FastAPI 0.115 + uvicorn (Python 3.12), SQLModel, Postgres 16, MinIO, Caddy 2. Todo orquestado con Docker Compose.

| Archivo | Rol |
|---------|-----|
| `backend/docker-compose.yml` | 4 servicios: `api` (FastAPI), `db` (postgres:16-alpine), `bucket` (MinIO, consola en `127.0.0.1:9001`), `caddy` (reverse proxy/TLS). Monta `defaults/lentes.json` como volumen read-only en `/seed/lentes.json` del `api`. |
| `backend/Caddyfile` | Site address `{$SCHEME}{$DOMAIN}:{$PORT}`; `/healthz` respondido por Caddy, el resto `reverse_proxy api:8000`. Con `DOMAIN` vacío escucha en cualquier hostname (acceso por IP de LAN desde Quest/tablet). |
| `backend/api/Dockerfile` | python:3.12-slim + libpq5/curl, uvicorn en :8000 con `--proxy-headers`, healthcheck a `/healthz`. |
| `backend/api/app/main.py` | App FastAPI; monta routers público/admin/files y `/static`; en startup: `init_db()` (create_all), `seed()`, `ensure_bucket()`. Handler global que convierte `HTTPException(303, Location=...)` en redirect (mecanismo del login admin). CORS abierto (`*`). |
| `backend/api/app/config.py` | `Settings` (pydantic-settings) leídos de entorno/.env: `database_url`, `s3_*`, `public_base_url`, `jwt_secret`, `admin_default_user/pass`, `api_key_ci`, `log_level`. |
| `backend/api/app/database.py` | Engine SQLAlchemy (`pool_pre_ping=True`), `init_db()` = `SQLModel.metadata.create_all` (sin Alembic todavía), dependency `get_session`. |
| `backend/api/app/models.py` | Modelos SQLModel: `Device` (device_id único, status active/suspended/pending, `license_expiry` NULL = permanente), `Version` (apk/asset version, URLs, `pck_sha256`, una sola `is_active`), `LensCatalog` (JSON string versionado, una sola activa), `UpdateLog` (eventos de update por device), `AdminUser` (bcrypt hash, rol). |
| `backend/api/app/routers.py` | Endpoints públicos `/api/*` + rate limiter (slowapi). |
| `backend/api/app/seed.py` | Seed idempotente en startup: admin user, catálogo desde `/seed/lentes.json`, `Version` dummy, device de test `DEV_TEST_001`. |
| `backend/api/app/admin/` | Panel admin: `router.py` (login, dashboard, CRUD devices/lenses/versions, logs con filtros/CSV), `auth.py` (JWT en cookie httpOnly), `templating.py` + `i18n.py` (Jinja2, es/en), `storage.py` (boto3 → MinIO), `files.py` (proxy público `/files/<key>`). |
| `backend/api/app/templates/` | `base/login/dashboard/devices/lenses/versions/logs.html` (Jinja2 + HTMX). |
| `defaults/lentes.json` | Semilla del catálogo (v`0.3.0-clinical`, 3 lentes: monofocal, panoptix, vivity; 10 params clínicos por lente con default/min/max). |
| `backend/.env.example` | Plantilla de `.env`: DOMAIN/SCHEME/PORT, PUBLIC_BASE_URL, POSTGRES_*, MINIO_*, S3_BUCKET, JWT_SECRET, ADMIN_DEFAULT_*, API_KEY_CI, LOG_LEVEL. |

```
Quest / Tablet ──HTTP──▶ caddy :8080/:443 ──▶ api :8000 ──▶ db (Postgres 16)
                              │                  │
                              └── /files/<key> ──┴──▶ bucket (MinIO :9000)
Browser admin ──▶ /admin (Jinja2+HTMX, cookie JWT)
```

### Endpoints públicos (consumidos por Unity)

| Endpoint | Consumidor | Notas |
|----------|-----------|-------|
| `GET /api/lenses` | `DataManager.TrySyncWithBackend()` — hace GET a `backendUrl + "/api/lenses"` con timeout 5 s | Devuelve `{version, catalogo:[...]}` del `LensCatalog` activo; 503 si no hay activo. |
| `GET /api/manifest.json` | UpdateManager (visor) | Versión activa: `min_apk_version`, URLs de APK/PCK, `pck_sha256`. 503 sin versión activa. |
| `POST /api/verify` | LicenseManager (visor) | Body `{device_id,...}`; 403 con `reason` (`DEVICE_NOT_FOUND` / `DEVICE_SUSPENDED` / `LICENSE_EXPIRED`) o `status: ok`. **Rate-limited 1 req/min/IP.** Actualiza `last_seen`/`last_ip`. |
| `POST /api/log` | visor | Batch de eventos; acepta devices desconocidos (debugging). Sin rate limit. |
| `GET /files/{key}` | visor (descarga APK/PCK) | Proxy streaming a MinIO; así el manifest publica URLs `public_base_url/files/...` sin exponer MinIO ni firmar tokens. |
| `GET /healthz`, `GET /`, `GET /docs` | humanos/infra | Health, índice, Swagger. |

La URL que usa Unity está hardcodeada en `Assets/Scripts/Runtime/Data/DataManager.cs`: `backendUrl = "http://192.168.88.198:8080"` (IP de LAN de desarrollo) — no coincide con el `http://localhost:8080` del compose; ajustar según red.

### Auth y panel admin

- Login en `/admin/login` (form). `authenticate_user` → bcrypt (passlib); si ok, `create_session_token` emite un **JWT HS256** (`sub`=username, TTL 8 h, firmado con `jwt_secret`) guardado en cookie `admin_session` httpOnly/samesite=lax (`secure` solo bajo HTTPS).
- La dependency `get_current_admin` valida cookie+JWT+usuario en BD; si falla lanza `HTTPException(303, Location=/admin/login)` que el handler de `main.py` convierte en redirect.
- Secciones: dashboard (contadores, versión/catálogo activos, últimos logs, avisos si `admin123`/JWT secret por defecto siguen configurados), devices (CRUD), lenses (crear/activar catálogos, editor visual sobre el JSON activo), versions (upload APK+PCK a MinIO con SHA256 al vuelo, activar/borrar), logs (filtros, paginación 50, export CSV).
- i18n es/en propio (diccionario en `admin/i18n.py`, cookie `admin_lang`), sin gettext.

### Seed del catálogo

`_seed_lens_catalog` lee `/seed/lentes.json` (volumen desde `defaults/lentes.json`; fallback inline mínimo si no está montado). Lógica de promoción: si el catálogo activo en BD tiene una versión listada en `_KNOWN_SEED_VERSIONS` (`0.0.1-seed`, `0.1.0-fallback`, `0.2.0-noche`, `0.3.0-clinical`) se considera seed no editado y se reemplaza por la versión nueva del JSON; si NO está en esa lista, se asume edición manual del admin y **no se pisa**.

## Decisiones y porqués

- **Compose con 4 servicios y Caddy delante** → TLS automático (Let's Encrypt) en prod sin tocar la app; en local, `DOMAIN` vacío + `SCHEME=http://` permiten acceder por IP de LAN desde el Quest.
- **`${DOMAIN-localhost}` (guion, no `:-`) en el compose** → un `DOMAIN=` vacío en `.env` NO cae al default; ese vacío es justamente el modo "escuchar en cualquier hostname".
- **SQLModel + `create_all` en vez de Alembic** → schema aún inestable; migrar a Alembic cuando se congele (comentario en `database.py`).
- **Proxy `/files/<key>` en vez de URLs directas a MinIO** → MinIO no se expone al exterior y las URLs del manifest no necesitan tokens firmados.
- **JWT en cookie httpOnly para el admin (no Bearer)** → el panel es server-rendered (Jinja2+HTMX); la cookie evita manejar tokens en JS y `httponly` mitiga XSS.
- **`/api/verify` rate-limited a 1/min/IP** → anti brute-force de device_ids; `/api/log` sin límite porque un update legítimo emite varios eventos.
- **Licencias permanentes por defecto (`license_expiry` NULL)** y **pre-registro manual de devices** (desconocido = 403) → decisiones de Sprint 0.
- **Seed idempotente con lista de versiones "conocidas"** → permite actualizar el catálogo por git pull + restart sin pisar recalibraciones hechas desde el panel.
- **Uploads acumulados en memoria (`storage.py`)** → simplicidad; APK+PCK ~150 MB es aceptable, migrar a multipart si crece.

## Gotchas

- **`defaults/lentes.json` es contrato compartido con Unity.** `CatalogParser.cs` (Unity) parsea el mismo schema `{version, catalogo:[{id, nombre, descripcion, params:{clave:{default,min,max}}}]}` que sirve `/api/lenses` y que siembra el seed. Cambiar claves o estructura exige tocar **ambos lados** (backend/defaults y `Assets/Scripts/Runtime/Data/CatalogParser.cs` + `CatalogModel.cs` + `Assets/StreamingAssets/lentes.json`). Unity tolera params NUEVOS sin recompilar (`MergeMissingParams` completa faltantes desde los defaults embebidos), pero renombrar o cambiar tipos rompe.
- **`admin/admin123` y `dev-jwt-secret-change-me` jamás a producción.** Son los defaults de `config.py`/compose/`.env.example`. El dashboard muestra un aviso si siguen activos, pero nada lo impide. No hay UI de cambio de contraseña: rotar vía `.env` **antes del primer arranque** (el seed solo crea el user si no existe; cambiar `.env` después no actualiza el hash en BD — hay que borrar el user o la BD).
- **Si el catálogo activo fue editado desde el panel, el seed nunca más lo actualiza** (versión fuera de `_KNOWN_SEED_VERSIONS`). Deseado, pero sorprende: subir un `defaults/lentes.json` nuevo no cambia nada hasta activar manualmente un catálogo en `/admin/lenses`. Además, cada versión de seed nueva debe agregarse a `_KNOWN_SEED_VERSIONS` o no se auto-promoverá en la siguiente.
- **Deriva de versiones del catálogo:** el embebido de Unity (`Assets/StreamingAssets/lentes.json`) va por `0.4.0-clinical` mientras `defaults/lentes.json` sigue en `0.3.0-clinical`. Un visor con backend accesible pisará su catálogo 0.4.0 por el 0.3.0 del backend (el merge repone params faltantes, no valores recalibrados). Mantenerlos sincronizados.
- **El device de test `DEV_TEST_001` y la `Version` dummy** los crea el seed siempre; eliminarlos antes de producción (el dummy tiene URLs `/dummy/...` inexistentes y SHA256 de archivo vacío).
- **`/api/verify` devuelve 403 al segundo intento en menos de 1 min** desde la misma IP (429 de slowapi en realidad); al probar con curl repetido parece "roto" sin serlo.
- **`backend/README.md` menciona `AUTO_HTTPS=on`**, variable que no existe en el compose ni en `.env.example`; el mecanismo real de HTTPS es `DOMAIN=<dominio>` + `SCHEME=` (vacío) + `PORT=443`.
- **CORS está abierto (`*`)** con `allow_credentials=True`; pendiente de restringir (comentario "Sprint 9+" en `main.py`).

## Cómo probar

```bash
cd backend
cp .env.example .env
docker compose up -d
docker compose logs -f api        # ver "[seed] ..." y uvicorn arriba

curl http://localhost:8080/healthz                  # ok (lo responde Caddy)
curl http://localhost:8080/api/lenses               # catálogo activo (3 lentes)
curl http://localhost:8080/api/manifest.json        # versión dummy del seed
curl -X POST http://localhost:8080/api/verify \
     -H "Content-Type: application/json" -d '{"device_id":"DEV_TEST_001"}'
# → {"status":"ok","device_name":"Visor de desarrollo",...}
curl -X POST http://localhost:8080/api/verify \
     -H "Content-Type: application/json" -d '{"device_id":"NOEXISTE"}'
# → 403 DEVICE_NOT_FOUND (esperar 1 min entre llamadas por el rate limit)
```

- Panel admin: `http://localhost:8080/admin` → login `admin`/`admin123` → dashboard con avisos de credenciales inseguras.
- Swagger: `http://localhost:8080/docs`. Consola MinIO: `http://localhost:9001` (`minioadmin`/`minioadmin`).
- Contra Unity: poner la IP de la máquina que corre Docker en `backendUrl` de `DataManager.cs`, entrar a Play y buscar en consola `DataManager: catalogo v... sincronizado desde backend`.
- Producción (VPS): DNS A record → IP del VPS, puertos 80/443 abiertos, `.env` con `DOMAIN`, `SCHEME=` vacío, `PORT=443`, `PUBLIC_BASE_URL=https://...` y secrets regenerados (`openssl rand -hex 32`); `docker compose up -d` y verificar `curl https://<dominio>/healthz` (Caddy tarda ~30 s en emitir el certificado).

## Pendientes / deuda

- Migrar de `SQLModel.metadata.create_all` a Alembic cuando el schema se estabilice.
- Restringir CORS al dominio del panel (hoy `*`).
- Endpoint `/api/admin/versions` con API key (`API_KEY_CI`) para CI/CD — la variable existe pero nada la consume aún.
- UI de cambio de contraseña del admin (hoy solo vía `.env` + reseed).
- `backend/README.md` desactualizado: no lista el subpaquete `admin/`, marca como "Sprint 8 pendiente" el panel ya implementado y referencia `AUTO_HTTPS` inexistente.
- Sincronizar `defaults/lentes.json` (0.3.0-clinical) con el embebido de Unity (0.4.0-clinical) y agregar la versión nueva a `_KNOWN_SEED_VERSIONS`.
- Uploads del panel acumulan el archivo completo en memoria; multipart si los binarios crecen.
