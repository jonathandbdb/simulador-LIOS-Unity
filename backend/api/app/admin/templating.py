"""Setup del entorno Jinja2 con i18n cableado y helpers comunes."""
from pathlib import Path

from fastapi import Request
from fastapi.templating import Jinja2Templates

from app.admin.i18n import Lang, SUPPORTED_LANGS, normalize_lang, t

TEMPLATES_DIR = Path(__file__).resolve().parent.parent / "templates"

templates = Jinja2Templates(directory=str(TEMPLATES_DIR))

LANG_COOKIE = "admin_lang"


def get_lang(request: Request) -> Lang:
    return normalize_lang(request.cookies.get(LANG_COOKIE))


def render(request: Request, template: str, **ctx) -> object:
    """Renderiza con `request`, `lang`, `t` y `flash` inyectados.

    El flash es un mensaje opcional de una sola vista (one-shot):
    se lee de la query string `?flash=...&flash_kind=ok|error` y se
    expone al template como variable `flash`.
    """
    lang = get_lang(request)
    flash_msg = request.query_params.get("flash") or ""
    flash_kind = request.query_params.get("flash_kind") or "ok"
    base_ctx = {
        "request": request,
        "lang": lang,
        "supported_langs": SUPPORTED_LANGS,
        "t": lambda key: t(key, lang),
        "flash": flash_msg,
        "flash_kind": flash_kind,
    }
    base_ctx.update(ctx)
    return templates.TemplateResponse(template, base_ctx)
