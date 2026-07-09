"""Tests de los endpoints publicos /api/* (manifest, lenses, verify)."""


def test_manifest_ok(client):
    """Sin ?app devuelve el canal 'visor' con el shape nuevo (sin PCK)."""
    r = client.get("/api/manifest.json")
    assert r.status_code == 200
    body = r.json()
    assert body["app"] == "visor"
    assert body["apk_version"] == "0.1.0"
    assert body["min_apk_version"] == "0.1.0"
    assert body["apk_url"]
    assert "apk_sha256" in body
    assert "changelog" in body
    assert "pck_url" not in body
    assert "pck_sha256" not in body
    assert "current_apk_version" not in body


def test_manifest_tablet_channel(client):
    r = client.get("/api/manifest.json", params={"app": "tablet"})
    assert r.status_code == 200
    body = r.json()
    assert body["app"] == "tablet"
    assert body["apk_version"]


def test_manifest_invalid_app_is_422(client):
    r = client.get("/api/manifest.json", params={"app": "phone"})
    assert r.status_code == 422


def test_lenses_ok(client):
    r = client.get("/api/lenses")
    assert r.status_code == 200
    body = r.json()
    assert body["version"]
    assert isinstance(body["catalogo"], list)
    assert len(body["catalogo"]) >= 1
    assert "id" in body["catalogo"][0]


def test_verify_valid_invalid_and_rate_limit(client):
    """Un solo test para no repartir la cuota de 10/min entre varios tests."""
    # 1) Device registrado por el seed (DEV_TEST_001): ok.
    r = client.post("/api/verify", json={"device_id": "DEV_TEST_001"})
    assert r.status_code == 200
    assert r.json()["status"] == "ok"

    # 2) Device inexistente: 403 DEVICE_NOT_FOUND.
    r = client.post("/api/verify", json={"device_id": "NOEXISTE"})
    assert r.status_code == 403
    assert r.json()["reason"] == "DEVICE_NOT_FOUND"

    # Van 2 requests consumidas de la cuota de 10/min. Agotamos las 8 que
    # quedan y confirmamos que la 11a cae en 429 (slowapi).
    for _ in range(8):
        client.post("/api/verify", json={"device_id": "NOEXISTE"})
    r = client.post("/api/verify", json={"device_id": "NOEXISTE"})
    assert r.status_code == 429
