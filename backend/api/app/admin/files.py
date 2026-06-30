"""Proxy publico para servir archivos almacenados en MinIO.

Sprint 8: el visor (y el admin que prueba) descargan APK/PCK desde
`/files/<key>` en lugar de hablar directo con MinIO. Asi:
- el endpoint MinIO interno no necesita exponerse al exterior;
- las URLs publicadas en `manifest.json` no requieren tokens.

No rompemos nada del lado del visor: el manifest contiene URLs absolutas
construidas con `settings.public_base_url + /files/<key>`.
"""
from fastapi import APIRouter, HTTPException
from fastapi.responses import StreamingResponse

from app.admin.storage import open_object_stream

router = APIRouter(prefix="/files", tags=["files"])


@router.get("/{key:path}")
def serve_file(key: str):
    try:
        body, content_type, content_length = open_object_stream(key)
    except Exception as e:
        raise HTTPException(status_code=404, detail=f"Not found: {e}")

    def _iter():
        try:
            for chunk in body.iter_chunks(chunk_size=1024 * 1024):
                yield chunk
        finally:
            body.close()

    headers = {}
    if content_length:
        headers["Content-Length"] = str(content_length)
    return StreamingResponse(_iter(), media_type=content_type, headers=headers)
