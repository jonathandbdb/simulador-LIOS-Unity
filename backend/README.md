# backend/

Backend del simulador VR oftalmologico. Implementado en **Sprint 3**.

## Stack

| Capa | Tecnologia |
|------|------------|
| API | FastAPI 0.115 + uvicorn (Python 3.12) |
| ORM | SQLModel (SQLAlchemy 2 + Pydantic v2) |
| DB | Postgres 16 |
| Bucket | MinIO (S3-compatible) — para APK y PCK |
| Reverse proxy | Caddy 2 (TLS automatico via Let's Encrypt en prod) |
| Rate limiting | slowapi |
| Hashing passwords | passlib[bcrypt] |
| JWT | python-jose (Sprint 8) |
| S3 client | boto3 (Sprint 8) |

## Layout

```
backend/
├── docker-compose.yml      # api + db + bucket + caddy
├── Caddyfile               # reverse proxy + TLS
├── .env.example            # variables de entorno (copiar a .env)
└── api/
    ├── Dockerfile
    ├── requirements.txt
    └── app/
        ├── main.py         # FastAPI app + startup hook
        ├── config.py       # Settings (pydantic-settings)
        ├── database.py     # engine + sesiones SQLModel
        ├── models.py       # Device, Version, LensCatalog, UpdateLog, AdminUser
        ├── seed.py         # admin user, version dummy, catalogo, device de test
        └── routers.py      # endpoints publicos /api/*
```

## Endpoints publicos (Sprint 3)

| Metodo + path | Proposito |
|---------------|-----------|
| `GET /` | Info del servicio + listado de endpoints |
| `GET /healthz` | Health check (Caddy / Docker) |
| `GET /api/manifest.json` | Version activa (consumida por F5 UpdateManager) |
| `POST /api/verify` | Verificacion de licencia (consumida por F4 LicenseManager). Rate-limited a 10 req/min/IP |
| `GET /api/lenses` | Catalogo de lentes activo (consumido por F1 DataManager) |
| `POST /api/log` | Recepcion de logs del visor (UpdateManager + LicenseManager los envian) |
| `GET /docs` | Swagger UI generado por FastAPI |

Endpoints `/api/admin/*` (CRUD + JWT) se implementan en **Sprint 8**.

## Quick start — local

Requiere Docker Desktop (Windows/Mac) o Docker Engine + compose plugin (Linux).

```bash
cd backend
cp .env.example .env       # editar si queres, los defaults sirven para local
docker compose up -d
docker compose logs -f api # ver logs del seed y de uvicorn
```

Verificacion:
```bash
curl http://localhost:8080/healthz
curl http://localhost:8080/api/manifest.json
curl http://localhost:8080/api/lenses
curl -X POST http://localhost:8080/api/verify \
     -H "Content-Type: application/json" \
     -d '{"device_id":"DEV_TEST_001"}'
# Esperado: {"status":"ok","device_name":"Visor de desarrollo",...}

curl -X POST http://localhost:8080/api/verify \
     -H "Content-Type: application/json" \
     -d '{"device_id":"INVALIDO"}'
# Esperado: 403 con reason=DEVICE_NOT_FOUND
```

Swagger UI: http://localhost:8080/docs

MinIO console: http://localhost:9001 (usuario: minioadmin / minioadmin).

## Quick start — produccion en VPS

1. Apuntar el dominio (ej. `api.tu-dominio.com`) al IP del VPS via DNS A record.
2. Abrir puertos 80 y 443 en el firewall del VPS.
3. En el VPS:
   ```bash
   git clone <repo>
   cd simulador/backend
   cp .env.example .env
   # editar .env:
   #   DOMAIN=api.tu-dominio.com
   #   SCHEME=             (vacio — Caddy usa HTTPS automatico via Let's Encrypt)
   #   PORT=443
   #   POSTGRES_PASSWORD=<openssl rand -hex 24>
   #   JWT_SECRET=<openssl rand -hex 32>
   #   ADMIN_DEFAULT_PASS=<password fuerte>
   #   PUBLIC_BASE_URL=https://api.tu-dominio.com
   docker compose up -d
   ```
4. Caddy emite el certificado automaticamente la primera vez (toma ~30 s).
5. Verificar:
   ```bash
   curl https://api.tu-dominio.com/healthz
   ```

## Comandos utiles

```bash
# Reiniciar solo el api (despues de cambiar codigo)
docker compose restart api

# Ver schema actual de la BD
docker compose exec db psql -U simulador -c '\dt'

# Resetear todo (borra BD + bucket — DESTRUCTIVO)
docker compose down -v

# Ver logs solo de un servicio
docker compose logs -f db
docker compose logs -f caddy
```

## Seed inicial

Al primer arranque, `app/seed.py` crea:
- 1 `AdminUser` con las credenciales de `.env` (default: admin / admin123).
- 1 `LensCatalog` activo, leido de `../defaults/lentes.json` (montado como volumen).
- 1 `Version` dummy activa con SHA256 placeholder. **No tiene APK/PCK reales** — eso se sube via panel admin (Sprint 8) o CI/CD (Sprint 11).
- 1 `Device` de test con `device_id="DEV_TEST_001"`, licencia permanente. **Eliminar este device antes de produccion**.

El seed es idempotente: solo crea registros que no existen.

## Migraciones (Alembic)

El arranque del contenedor aplica migraciones Alembic (`app/migrations.py`)
en vez de `SQLModel.metadata.create_all`. Ver `docs/backend.md` para el
detalle de como adopta Alembic una BD ya existente y como generar nuevas
revisiones.

## Tests

```bash
pip install -r api/requirements-dev.txt
cd api && python -m pytest
```

Los tests usan SQLite en memoria (no dependen de Docker ni Postgres). Ver
`docs/backend.md` para el detalle.

## TODO / deuda pendiente

- UI de cambio de contrasena del admin (hoy solo via `.env` + reseed).
- Uploads del panel acumulan el archivo completo en memoria; multipart si los
  binarios crecen.
