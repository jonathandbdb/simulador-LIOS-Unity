#!/usr/bin/env python3
"""
Genera el deck comercial autocontenido a partir de la plantilla.

    python docs/comercial/build-deck.py

Lee `deck.template.html`, sustituye cada marcador `__IMG_*__` por un data URI
con la captura ya redimensionada y recomprimida, y escribe
`iol-simulator-deck.html` — un archivo unico, sin dependencias externas, que se
puede enviar por mail o abrir desde un pendrive.

Las capturas viven en `capturas/`, que esta gitignoreada: por eso se embeben en
vez de referenciarse por ruta relativa. El HTML generado es la unica copia
durable del material.

Requiere Pillow (ya presente en el entorno del proyecto).
"""

import base64
import io
import sys
from pathlib import Path

from PIL import Image

ROOT = Path(__file__).resolve().parents[2]
HERE = Path(__file__).resolve().parent
TEMPLATE = HERE / "deck.template.html"
OUTPUT = HERE / "iol-simulator-deck.html"

# Marcador de la plantilla -> captura de origen.
# Hoy vacio: el deck quedo sin comparativas (ver README). Para reincorporar una,
# agregar aca el par marcador/captura y usar el marcador en el objeto IMG de la
# plantilla; el resto del pipeline no cambia.
IMAGES = {}
LOGO = ("__IMG_LOGO__", "Assets/Resources/TabletBrand/logo_mark.png")

MAX_WIDTH = 1600   # suficiente para proyectar a 1080p sin inflar el archivo
JPEG_QUALITY = 86


def as_jpeg_data_uri(path: Path) -> str:
    im = Image.open(path)
    if im.width > MAX_WIDTH:
        h = round(im.height * MAX_WIDTH / im.width)
        im = im.resize((MAX_WIDTH, h), Image.LANCZOS)
    buf = io.BytesIO()
    im.convert("RGB").save(buf, "JPEG", quality=JPEG_QUALITY, optimize=True, progressive=True)
    return "data:image/jpeg;base64," + base64.b64encode(buf.getvalue()).decode()


def as_png_data_uri(path: Path, size: int) -> str:
    # El logo conserva PNG por el canal alfa (va sobre fondo oscuro).
    im = Image.open(path).convert("RGBA").resize((size, size), Image.LANCZOS)
    buf = io.BytesIO()
    im.save(buf, "PNG", optimize=True)
    return "data:image/png;base64," + base64.b64encode(buf.getvalue()).decode()


def main() -> int:
    html = TEMPLATE.read_text(encoding="utf-8")

    missing = [p for p in list(IMAGES.values()) + [LOGO[1]] if not (ROOT / p).is_file()]
    if missing:
        print("Faltan capturas de origen:", *missing, sep="\n  ", file=sys.stderr)
        return 1

    html = html.replace(LOGO[0], as_png_data_uri(ROOT / LOGO[1], 256))
    for marker, rel in IMAGES.items():
        html = html.replace(marker, as_jpeg_data_uri(ROOT / rel))

    leftover = [m for m in list(IMAGES) + [LOGO[0]] if m in html]
    if leftover:
        print("Marcadores sin sustituir:", *leftover, sep="\n  ", file=sys.stderr)
        return 1

    OUTPUT.write_text(html, encoding="utf-8")
    print(f"{OUTPUT.relative_to(ROOT)} -> {OUTPUT.stat().st_size / 1024:.0f} KB")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
