"""Tests de `_RedactWifiPasswordFilter` (`app/main.py`).

MAYOR #11 de la revision de `/admin/provisioning`: el filtro sobre
`uvicorn.access` es la defensa en profundidad para el caso de que alguien
pegue `?wifi_password=...` a mano en la URL (el fix de raiz es que el form
real es `POST`, ver `test_admin_provisioning.py`). Estos tests no levantan
un server: construyen `logging.LogRecord`s con la MISMA forma que arma el
`AccessFormatter` de uvicorn (`record.args = (client_addr, method,
full_path, http_version, status_code)`) y ejercitan el filtro directo.
"""
import logging

from app.main import _RedactWifiPasswordFilter


def _access_record(full_path: str) -> logging.LogRecord:
    """Un `LogRecord` con la forma real de `uvicorn.access`."""
    return logging.LogRecord(
        name="uvicorn.access",
        level=logging.INFO,
        pathname=__file__,
        lineno=1,
        msg='%s - "%s %s HTTP/%s" %d',
        args=("127.0.0.1:54321", "GET", full_path, "1.1", 200),
        exc_info=None,
    )


def test_redacts_wifi_password_at_start_of_query():
    record = _access_record("/admin/provisioning?wifi_password=secreta123&wifi_ssid=Clinica")
    filt = _RedactWifiPasswordFilter()

    assert filt.filter(record) is True
    full_path = record.args[2]
    assert "wifi_password=REDACTED" in full_path
    assert "secreta123" not in full_path
    assert "wifi_ssid=Clinica" in full_path  # el resto de la query no se toca


def test_redacts_wifi_password_in_middle_of_query():
    record = _access_record(
        "/admin/provisioning?wifi_ssid=Clinica&wifi_password=secreta123&locale=es_UY"
    )
    filt = _RedactWifiPasswordFilter()

    assert filt.filter(record) is True
    full_path = record.args[2]
    assert "wifi_password=REDACTED" in full_path
    assert "secreta123" not in full_path
    assert "wifi_ssid=Clinica" in full_path
    assert "locale=es_UY" in full_path


def test_redacts_wifi_password_at_end_of_query():
    record = _access_record("/admin/provisioning?wifi_ssid=Clinica&wifi_password=secreta123")
    filt = _RedactWifiPasswordFilter()

    assert filt.filter(record) is True
    full_path = record.args[2]
    assert full_path.endswith("wifi_password=REDACTED")
    assert "secreta123" not in full_path


def test_redacts_url_encoded_value():
    """Password URL-encoded (espacio como `+`, `@` como `%40`): el patron
    corta en `&`/whitespace, asi que el valor entero (incluidos `%40`/`+`)
    debe desaparecer, no solo un prefijo."""
    record = _access_record(
        "/admin/provisioning?wifi_password=abc%40def+ghi&wifi_ssid=Clinica"
    )
    filt = _RedactWifiPasswordFilter()

    assert filt.filter(record) is True
    full_path = record.args[2]
    assert "wifi_password=REDACTED" in full_path
    assert "%40" not in full_path
    assert "abc" not in full_path


def test_filter_never_discards_record():
    """El filtro SIEMPRE devuelve True (nunca descarta el record), tenga o
    no `wifi_password` la query — un `Filter.filter()` que devuelva False
    tira el mensaje de log entero."""
    with_password = _access_record("/admin/provisioning?wifi_password=x")
    without_password = _access_record("/admin/lenses")
    filt = _RedactWifiPasswordFilter()

    assert filt.filter(with_password) is True
    assert filt.filter(without_password) is True


def test_record_without_wifi_password_passes_through_unchanged():
    record = _access_record("/admin/devices?flash=OK")
    original_args = record.args
    filt = _RedactWifiPasswordFilter()

    assert filt.filter(record) is True
    assert record.args == original_args


def test_record_with_non_tuple_args_passes_through_without_exception():
    """Otros loggers (o un `LogRecord` armado a mano, o con `%`-formatting
    de un solo valor) pueden traer `args` como dict o valor suelto, no la
    tupla de 5 posiciones de `uvicorn.access`. El filtro no debe asumir la
    forma y romper con `IndexError`/`TypeError`."""
    record = logging.LogRecord(
        name="uvicorn.access",
        level=logging.INFO,
        pathname=__file__,
        lineno=1,
        msg="algo distinto: %(key)s",
        # Tupla de UN elemento con un dict adentro: es como el propio
        # modulo `logging` arma `args` cuando el caller hace
        # `logger.debug(msg, {"key": ...})` — `LogRecord.__init__` lo
        # "desenvuelve" a `record.args = {"key": ...}` (dict a secas).
        args=({"key": "wifi_password=secreta"},),
        exc_info=None,
    )
    filt = _RedactWifiPasswordFilter()

    assert filt.filter(record) is True
    assert record.args == {"key": "wifi_password=secreta"}


def test_record_without_args_passes_through_without_exception():
    record = logging.LogRecord(
        name="uvicorn.access",
        level=logging.INFO,
        pathname=__file__,
        lineno=1,
        msg="mensaje sin argumentos",
        args=None,
        exc_info=None,
    )
    filt = _RedactWifiPasswordFilter()

    assert filt.filter(record) is True
    assert record.args is None


def test_record_with_short_tuple_args_passes_through_without_exception():
    """Tupla de menos de 3 elementos (no la forma de `uvicorn.access`): el
    filtro chequea `len(args) >= 3` antes de indexar `args[2]`."""
    record = logging.LogRecord(
        name="some.other.logger",
        level=logging.INFO,
        pathname=__file__,
        lineno=1,
        msg="%s %s",
        args=("a", "b"),
        exc_info=None,
    )
    filt = _RedactWifiPasswordFilter()

    assert filt.filter(record) is True
    assert record.args == ("a", "b")
