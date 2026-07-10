"""FastAPI app entry point."""
import logging
from contextlib import asynccontextmanager
from pathlib import Path

from fastapi import FastAPI, Request
from fastapi.exceptions import HTTPException
from fastapi.exception_handlers import http_exception_handler
from fastapi.middleware.cors import CORSMiddleware
from fastapi.responses import RedirectResponse
from fastapi.staticfiles import StaticFiles
from slowapi import _rate_limit_exceeded_handler
from slowapi.errors import RateLimitExceeded
from sqlmodel import Session

from app.admin.files import router as files_router
from app.admin.router import router as admin_router
from app.admin.storage import ensure_bucket
from app.config import settings
from app.database import engine
from app.migrations import run_migrations
from app.routers import _maybe_purge_logs, limiter, router as public_router
from app.seed import seed

logging.basicConfig(level=settings.log_level.upper())
logger = logging.getLogger(__name__)


@asynccontextmanager
async def lifespan(app: FastAPI):
    logger.info("Aplicando migraciones Alembic...")
    run_migrations()
    logger.info("Ejecutando seed inicial...")
    with Session(engine) as session:
        seed(session)
    logger.info("Purgando logs viejos (retention=%d dias)...", settings.log_retention_days)
    with Session(engine) as session:
        _maybe_purge_logs(session)
    logger.info("Asegurando bucket MinIO/S3...")
    try:
        ensure_bucket()
    except Exception as e:  # noqa: BLE001
        logger.warning("ensure_bucket fallo (no fatal): %s", e)
    logger.info("Backend listo en %s", settings.public_base_url)
    yield


app = FastAPI(
    title="Simulador VR — Backend",
    description="API publica + panel admin del simulador oftalmologico.",
    version="0.1.0",
    lifespan=lifespan,
)

# Rate limiting (afecta los endpoints decorados con @limiter.limit).
app.state.limiter = limiter
app.add_exception_handler(RateLimitExceeded, _rate_limit_exceeded_handler)

# CORS: /admin es server-rendered (mismo origen, cookie httpOnly) y no lo
# necesita; /api/* lo consume Unity (UnityWebRequest, no un browser, CORS no
# le aplica). allow_credentials=False habilita usar wildcard sin violar la
# restriccion de Starlette (wildcard + credentials no es valido).
app.add_middleware(
    CORSMiddleware,
    allow_origins=settings.cors_origins_list,
    allow_credentials=False,
    allow_methods=["*"],
    allow_headers=["*"],
)


# Las dependencies del panel admin levantan HTTPException(303, Location=...)
# para forzar redirect al login. Este handler las convierte en RedirectResponse.
@app.exception_handler(HTTPException)
async def _redirect_or_default_handler(request: Request, exc: HTTPException):
    if exc.status_code == 303 and exc.headers and exc.headers.get("Location"):
        return RedirectResponse(url=exc.headers["Location"], status_code=303)
    return await http_exception_handler(request, exc)


app.include_router(public_router)
app.include_router(admin_router)
app.include_router(files_router)

# Estaticos del panel admin (CSS, fuentes, favicon) — sin CDNs externos.
app.mount("/static", StaticFiles(directory=Path(__file__).parent / "static"), name="static")


@app.get("/healthz")
def healthz() -> dict:
    """Health check para Docker / load balancer / Caddy."""
    return {"status": "ok"}


@app.get("/")
def root() -> dict:
    return {
        "name": "Simulador VR Backend",
        "version": app.version,
        "docs": "/docs",
        "admin": "/admin/login",
        "endpoints": {
            "manifest": "/api/manifest.json",
            "verify": "POST /api/verify",
            "lenses": "/api/lenses",
            "log": "POST /api/log",
            "healthz": "/healthz",
        },
    }
