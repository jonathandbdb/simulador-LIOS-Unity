"""Helpers para subir archivos a MinIO (o S3) directo desde el panel.

Sprint 8: upload sincronico via boto3. La instancia FastAPI ya bloquea
durante el upload del cliente, asi que mantenemos el flujo simple.
"""
import hashlib
import io
from typing import IO, Tuple

import boto3
from botocore.client import Config
from botocore.exceptions import ClientError

from app.config import settings


def get_s3_client():
    return boto3.client(
        "s3",
        endpoint_url=settings.s3_endpoint,
        aws_access_key_id=settings.s3_access_key,
        aws_secret_access_key=settings.s3_secret_key,
        config=Config(signature_version="s3v4"),
        region_name="us-east-1",
    )


def ensure_bucket() -> None:
    """Crea el bucket si no existe. Idempotente."""
    s3 = get_s3_client()
    try:
        s3.head_bucket(Bucket=settings.s3_bucket)
    except ClientError as e:
        code = int(e.response["Error"].get("Code", "0"))
        if code in (404, 403):
            try:
                s3.create_bucket(Bucket=settings.s3_bucket)
            except ClientError:
                pass


def upload_file_streaming(stream: IO[bytes], key: str, content_type: str) -> Tuple[str, str]:
    """Sube un stream de bytes a `s3://<bucket>/<key>`.

    Lee en chunks de 8 MB, calcula SHA256 al vuelo y arma una URL publica.
    Retorna (url_publica, sha256_hex).
    """
    s3 = get_s3_client()
    sha = hashlib.sha256()

    # Acumulamos en memoria por simplicidad (APK + PCK juntos ~150 MB).
    # Si llega a ser problema, migramos a multipart upload.
    buf = io.BytesIO()
    while True:
        chunk = stream.read(8 * 1024 * 1024)
        if not chunk:
            break
        sha.update(chunk)
        buf.write(chunk)
    buf.seek(0)

    s3.put_object(
        Bucket=settings.s3_bucket,
        Key=key,
        Body=buf,
        ContentType=content_type,
    )
    url = f"{settings.public_base_url.rstrip('/')}/files/{key}"
    return url, sha.hexdigest()


def delete_object(key: str) -> None:
    s3 = get_s3_client()
    try:
        s3.delete_object(Bucket=settings.s3_bucket, Key=key)
    except ClientError:
        pass


def open_object_stream(key: str):
    """Devuelve un body-stream del objeto en MinIO (para servirlo a Quest/curl)."""
    s3 = get_s3_client()
    obj = s3.get_object(Bucket=settings.s3_bucket, Key=key)
    return obj["Body"], obj.get("ContentType", "application/octet-stream"), obj.get("ContentLength", 0)
