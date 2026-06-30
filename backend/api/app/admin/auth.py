"""Auth del panel admin: JWT en cookie httpOnly + dependency require_admin."""
from datetime import datetime, timedelta, timezone
from typing import Annotated

from fastapi import Depends, HTTPException, Request, status
from fastapi.responses import RedirectResponse
from jose import JWTError, jwt
from passlib.context import CryptContext
from sqlmodel import Session, select

from app.config import settings
from app.database import get_session
from app.models import AdminUser

# bcrypt esta pineado a 4.0.1 por compatibilidad con passlib 1.7.4.
pwd_context = CryptContext(schemes=["bcrypt"], deprecated="auto")

COOKIE_NAME = "admin_session"
COOKIE_TTL_HOURS = 8
JWT_ALG = "HS256"


def verify_password(plain: str, hashed: str) -> bool:
    try:
        return pwd_context.verify(plain, hashed)
    except Exception:
        return False


def hash_password(plain: str) -> str:
    return pwd_context.hash(plain)


def create_session_token(username: str) -> str:
    """Crea un JWT firmado con `jwt_secret` que vence en COOKIE_TTL_HOURS."""
    now = datetime.now(tz=timezone.utc)
    payload = {
        "sub": username,
        "iat": int(now.timestamp()),
        "exp": int((now + timedelta(hours=COOKIE_TTL_HOURS)).timestamp()),
    }
    return jwt.encode(payload, settings.jwt_secret, algorithm=JWT_ALG)


def decode_session_token(token: str) -> str | None:
    """Devuelve el username (`sub`) si el token es valido, sino None."""
    try:
        payload = jwt.decode(token, settings.jwt_secret, algorithms=[JWT_ALG])
        return payload.get("sub")
    except JWTError:
        return None


def authenticate_user(session: Session, username: str, password: str) -> AdminUser | None:
    user = session.exec(select(AdminUser).where(AdminUser.username == username)).first()
    if user is None:
        return None
    if not verify_password(password, user.password_hash):
        return None
    return user


def get_current_admin(
    request: Request,
    session: Annotated[Session, Depends(get_session)],
) -> AdminUser:
    """Dependency: requiere cookie de sesion valida. Si no, redirige al login."""
    token = request.cookies.get(COOKIE_NAME)
    if not token:
        raise _redirect_to_login()
    username = decode_session_token(token)
    if not username:
        raise _redirect_to_login()
    user = session.exec(select(AdminUser).where(AdminUser.username == username)).first()
    if user is None:
        raise _redirect_to_login()
    return user


def _redirect_to_login() -> HTTPException:
    # FastAPI no tiene una manera directa de "redirigir desde una dependency".
    # Usamos un HTTPException(303) con header Location, el handler global
    # convierte cualquier 303 levantado asi en redirect.
    raise HTTPException(
        status_code=status.HTTP_303_SEE_OTHER,
        headers={"Location": "/admin/login"},
    )


def set_session_cookie(response, token: str, secure: bool) -> None:
    response.set_cookie(
        key=COOKIE_NAME,
        value=token,
        max_age=COOKIE_TTL_HOURS * 3600,
        httponly=True,
        secure=secure,
        samesite="lax",
        path="/",
    )


def clear_session_cookie(response) -> None:
    response.delete_cookie(key=COOKIE_NAME, path="/")
