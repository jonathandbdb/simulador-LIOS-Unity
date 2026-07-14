# Simulador VR de Lentes Intraoculares (LIOS)

> Trabajo de Fin de Máster — Simulador de realidad virtual que permite **experimentar en primera persona cómo ve un paciente** tras una cirugía de catarata según el tipo de lente intraocular (LIO) implantada.

---

## Tabla de contenidos

1. [Descripción general](#1-descripción-general)
2. [Stack tecnológico](#2-stack-tecnológico)
3. [Instalación y ejecución](#3-instalación-y-ejecución)
4. [Estructura del proyecto](#4-estructura-del-proyecto)
5. [Funcionalidades principales](#5-funcionalidades-principales)
6. [Usuario y contraseña de prueba](#6-usuario-y-contraseña-de-prueba)
7. [Despliegue, presentación y vídeo](#7-despliegue-presentación-y-vídeo)

---

## 1. Descripción general

La elección de una lente intraocular es una de las decisiones clínicas más relevantes en la cirugía de catarata, y una de las más difíciles de **comunicar al paciente**: cada tipo de lente (monofocal, trifocal, EDOF…) ofrece un compromiso distinto entre nitidez a distintas distancias, sensibilidad al contraste y efectos visuales no deseados (halos, deslumbramientos, *starbursts* nocturnos). Hasta operarse, el paciente no tiene forma real de saber *cómo va a ver*.

**LIOS** resuelve ese problema. Es un simulador de realidad virtual para **Meta Quest** que reproduce, en estéreo y de forma independiente por cada ojo, la visión resultante de cada lente intraocular sobre escenarios realistas:

- **Consultorio** (escenario diurno): un entorno de lectura para evaluar la visión cercana e intermedia.
- **Ruta nocturna** (escenario mesópico/escotópico): conducción de noche con tráfico bidireccional, donde se aprecian halos, deslumbramientos y la pérdida de contraste característica de las lentes multifocales.

El sistema se compone de **tres piezas que cooperan en red local**:

| Pieza | Plataforma | Rol |
|-------|-----------|-----|
| **Visor** | Meta Quest 2/3 (Android + OpenXR) | Ejecuta la simulación en VR estéreo, con post-procesado de visión por ojo. |
| **Tablet de control** | Android (tablet, sin VR) | Mando a distancia del clínico: selecciona lentes por ojo, ajusta parámetros en tiempo real, cambia de escenario y **ve en directo** lo que ve el paciente (streaming de vídeo). |
| **Backend** | Docker (FastAPI + Postgres + MinIO + Caddy) | Sirve el catálogo de lentes, panel de administración, verificación de licencias y manifiesto de actualizaciones. |

El clínico se sienta junto al paciente con la tablet; el paciente lleva el visor. El clínico cambia de lente o ajusta un parámetro en la tablet y el paciente ve el cambio al instante, mientras el clínico monitoriza el resultado en la pantalla de la tablet. La comunicación visor↔tablet es **totalmente local** (WebSocket + autodescubrimiento UDP), sin depender de internet.

> El proyecto se originó como prototipo en Godot y se ha **reimplementado y ampliado en Unity 6 + URP**, que es la versión presentada en este TFM. El catálogo de lentes embebido (v`0.5.1-clinical`) está calibrado con valores clínicos para tres lentes representativas: **Monofocal estándar**, **PanOptix Pro** (trifocal difractiva) y **Vivity EDOF**.
>
> El backend está **desplegado en producción**: `https://vr.conecta.sh` (API pública + panel de administración).

---

## 2. Stack tecnológico

### Aplicación VR / Tablet (cliente)

| Capa | Tecnología |
|------|-----------|
| Motor | **Unity 6000.5.1f1** (Unity 6) |
| Render | **Universal Render Pipeline (URP) 17.5** con tiers separados PC / Mobile |
| VR | **OpenXR 1.17** + **XR Interaction Toolkit 3.5** + XR Management (objetivo: Meta Quest 2/3) |
| Entrada | **Input System 1.19** (acciones OpenXR; sin API legacy) |
| Lenguaje | **C#** (IL2CPP en Android) |
| Serialización | **Newtonsoft.Json** |
| Post-procesado | **ScriptableRendererFeature** propia + shaders HLSL (`VisionPostProcess`, `GlareBillboard`) |
| Red | WebSocket **escrito a mano** (RFC 6455 sobre `System.Net.Sockets`, compatible con IL2CPP/Android) + descubrimiento UDP |
| UI tablet | Generada por código en runtime (TextMeshPro + sprites procedurales, temas claro/oscuro) |
| Tests | **Unity Test Framework** (NUnit, EditMode) |

### Backend (servidor)

| Capa | Tecnología |
|------|-----------|
| API | **FastAPI 0.115** + Uvicorn (Python 3.12) |
| ORM | **SQLModel** (SQLAlchemy 2 + Pydantic v2) |
| Base de datos | **PostgreSQL 16** |
| Almacenamiento de binarios | **MinIO** (S3-compatible) — para los APK del sistema de actualizaciones OTA |
| Reverse proxy / TLS | **Caddy 2** (HTTPS automático vía Let's Encrypt en producción) |
| Panel admin | Jinja2 + HTMX + Tailwind (CDN), i18n es/en |
| Auth | JWT (python-jose) + passlib/bcrypt |
| Orquestación | **Docker Compose** (servicios: `api`, `db`, `bucket`, `caddy`) |

---

## 3. Instalación y ejecución

El proyecto tiene dos partes que se instalan por separado: el **backend** (Docker, opcional para desarrollo) y la **aplicación Unity** (visor + tablet).

### 3.1. Backend (Docker)

Requiere **Docker Desktop** (Windows/Mac) o Docker Engine + plugin compose (Linux).

```bash
cd backend
cp .env.example .env        # los valores por defecto sirven para desarrollo local
docker compose up -d        # levanta api + db + bucket + caddy
docker compose logs -f api  # ver el seed inicial y los logs de uvicorn
```

Verificación:

```bash
curl http://localhost:8080/healthz
curl http://localhost:8080/api/lenses          # catálogo de lentes
curl http://localhost:8080/api/manifest.json   # versión activa
```

- **API pública:** `http://localhost:8080`
- **Panel de administración:** `http://localhost:8080/admin`
- **Swagger / OpenAPI:** `http://localhost:8080/docs`
- **Consola MinIO:** `http://localhost:9001`

En el **primer arranque**, `app/seed.py` siembra de forma idempotente: un usuario administrador, el catálogo de lentes (leído de `defaults/lentes.json`, montado como volumen), una versión *dummy* y un dispositivo de prueba (`DEV_TEST_001`).

> **Producción**: el backend de este TFM corre en `https://vr.conecta.sh` (VPS con Docker Compose + Caddy/Let's Encrypt, repo clonado y actualizable por `git pull`). Instrucciones detalladas de despliegue en [`backend/README.md`](backend/README.md) y [`docs/builds-deploy.md`](docs/builds-deploy.md).

### 3.2. Aplicación Unity

Requisitos:

- **Unity 6000.5.1f1** (instalar vía Unity Hub).
- Para compilar a Quest/tablet: **módulo Android Build Support** (con OpenJDK, SDK & NDK), Quest en modo desarrollador.

Abrir el proyecto:

1. Unity Hub → *Add* → seleccionar la carpeta raíz de este repositorio.
2. Abrir la escena **`Assets/Scenes/Main.unity`** (visor) o **`Assets/Scenes/Tablet.unity`** (tablet).
3. *Play* en el editor para probar (en escritorio se puede recorrer la simulación sin casco; con un Quest conectado por Link se ejecuta en VR).

#### Compilar el VISOR (Quest, OpenXR)

Build estándar de Unity para Android con el target Quest:

- Package name: `com.simulador.vr`, arm64-v8a, OpenXR activado, min SDK 29.
- Desde *File → Build Settings → Android* (escena `Main.unity`).

> ⚠️ **Nota para builds Android desde un clon fresco:** el proyecto está configurado para firmar con
> un keystore propio (`keystore/simulador.keystore`) que **no se distribuye en el repositorio** (está
> gitignoreado; la firma es la identidad de la app para el sistema de actualizaciones). Para compilar
> localmente: *Project Settings → Player → Publishing Settings* → desmarcar **Custom Keystore** (firma
> con el debug keystore de tu máquina) o crear un keystore propio.

Instalar en el Quest:

```bash
adb install -r <ruta-al-apk>
adb logcat -s Unity         # ver logs desde el visor
```

#### Compilar la TABLET (Android, sin VR)

La tablet **no** usa OpenXR (si no, pantalla negra en un dispositivo sin VR). Hay un build script dedicado que desactiva temporalmente los XR loaders y usa un `applicationIdentifier` propio:

- Package name: `com.simulador.tablet` (distinto del visor — pueden convivir instalados en el mismo dispositivo).
- Desde el editor: menú **`Simulador → Build Tablet (Android)`**.
- Por CLI (headless):

```bash
Unity -batchmode -quit -projectPath "." \
  -executeMethod Simulador.EditorTools.TabletBuild.BuildTablet
# salida: Builds/Android/Simulador.apk
```

#### Puesta en marcha conjunta

1. Visor y tablet deben estar en la **misma red Wi-Fi local**.
2. Arrancar el visor: inicia el servidor WebSocket (TCP **9090**), el *beacon* de descubrimiento (UDP **9091**) y el streaming de vídeo.
3. Arrancar la tablet: detecta el visor automáticamente (o introducir su IP a mano) y conectar.
4. El catálogo se carga sin conexión (catálogo embebido en `StreamingAssets/lentes.json`); si el backend está accesible, se sincroniza en segundo plano sin bloquear el arranque.

> **Nota de red:** la URL del backend se resuelve por capas: `persistentDataPath/config.json`
> (override de desarrollo, se sube por `adb push` sin recompilar) > `Assets/StreamingAssets/config.json`
> (default de producción: `https://vr.conecta.sh`) > constante de respaldo en código. Si no hay backend
> accesible, la sincronización del catálogo degrada al catálogo embebido y la caché local — y la
> licencia del visor tiene un período de gracia offline (ver §5).

### 3.3. Tests

Desde el editor: *Window → General → Test Runner → EditMode → Run All*. **5 suites / 86 tests** de la
lógica pura del proyecto: catálogo y motor de lentes (`DataLogicTests`), resolución de configuración
(`DataManagerLogicTests`), tokens de emparejamiento (`PairingStoreTests`), sistema de actualizaciones
(`UpdateLogicTests`: semver, decisión de update, manifiesto, SHA-256) y licenciamiento
(`LicenseLogicTests`: contrato de verificación, política de gracia offline). El backend tiene además su
propia suite (`backend/api/tests/`, pytest, 22 tests — sin Docker, SQLite en memoria).

---

## 4. Estructura del proyecto

```
.
├── Assets/
│   ├── Scenes/
│   │   ├── Main.unity            # Escena del VISOR (VR)
│   │   └── Tablet.unity          # Escena de la TABLET de control
│   ├── Scripts/
│   │   ├── Runtime/
│   │   │   ├── Data/             # Catálogo y motor de lentes
│   │   │   │   ├── CatalogModel.cs     # Modelos: LensDef, LensCatalog, EyeState…
│   │   │   │   ├── CatalogParser.cs    # Parseo + validación + merge (estático, testeable)
│   │   │   │   ├── LensEngine.cs       # Lógica pura: estado por ojo, blend, overrides
│   │   │   │   └── DataManager.cs      # Singleton: carga catálogo, sync backend, estado de visión
│   │   │   ├── Net/             # Comunicación visor ↔ tablet
│   │   │   │   ├── WebSocketServer.cs  # Servidor WS (visor) RFC 6455 a mano
│   │   │   │   ├── WebSocketClient.cs  # Cliente WS (tablet)
│   │   │   │   ├── DiscoveryBeacon.cs  # Beacon UDP (visor anuncia)
│   │   │   │   ├── DiscoveryListener.cs# Listener UDP (tablet descubre)
│   │   │   │   ├── NetworkController.cs# Orquesta servidor + protocolo de comandos
│   │   │   │   ├── TabletController.cs # App tablet: UI + cliente + descubrimiento
│   │   │   │   └── StreamingCapture.cs # Captura de vídeo 768×576 @20 Hz → JPG a la tablet
│   │   │   ├── Tablet/          # UI procedural de la tablet (temas, tarjetas, sliders)
│   │   │   ├── Update/          # Actualizaciones OTA: UpdateLogic (pura), UpdateManager,
│   │   │   │                    #   UpdateInstaller (intent+FileProvider), UpdatePromptVR
│   │   │   ├── License/         # Licenciamiento: LicenseLogic (pura), LicenseManager,
│   │   │   │                    #   LicenseBlockScreenVR (pantalla de bloqueo fail-closed)
│   │   │   └── Vision/          # Render y óptica
│   │   │       ├── VisionRendererFeature.cs # Feature URP (post-proceso por ojo)
│   │   │       ├── VisionParamsBinder.cs    # Mapea parámetros de catálogo → uniforms
│   │   │       ├── GlareController.cs        # Halos, starbursts, astigmatismo (globals)
│   │   │       ├── GlareSource/Instance/...  # Billboards de deslumbramiento
│   │   │       ├── ScenarioManager.cs        # Consultorio (día) / Ruta noche (noche)
│   │   │       ├── NightTraffic.cs           # Tráfico nocturno bidireccional
│   │   │       ├── BookHolder.cs             # Distancia libro→cámara (foco de lectura)
│   │   │       ├── SimuladorInput.cs         # Mandos VR (A/B/X/Y)
│   │   │       └── HudController.cs          # HUD diagnóstico (FPS, lente, escenario)
│   │   └── Editor/             # CarLightTool, TabletBuild (build tablet: XR off, id, ícono)
│   ├── Shaders/                # VisionPostProcess.shader, GlareBillboard.shader
│   ├── Plugins/Android/        # AndroidManifest (permisos update) + FileProvider (.androidlib)
│   ├── StreamingAssets/
│   │   ├── lentes.json         # Catálogo de lentes embebido (fallback offline)
│   │   └── config.json         # URL del backend (default de producción)
│   ├── Textures/Icons/         # Íconos por app (visor / tablet)
│   ├── Settings/               # URP: tiers PC y Mobile (Renderer + RP Asset)
│   ├── XR/ · XRI/              # Configuración OpenXR + XR Interaction Toolkit
│   ├── Prefabs/ · Art/ · Meshes/ · Materials/ · Fonts/ · Resources/
│   └── Tests/EditMode/         # 5 suites NUnit (86 tests de lógica pura)
├── backend/                    # Backend FastAPI + Postgres + MinIO + Caddy (Docker)
│   ├── docker-compose.yml      # + docker-compose.prod.yml (override de producción)
│   ├── api/app/                # main, config, database, models, seed, routers, admin/
│   ├── api/tests/              # Suite pytest del backend (22 tests)
│   └── README.md               # Documentación específica del backend
├── defaults/
│   └── lentes.json             # Semilla del catálogo (montada por el backend)
├── docs/                       # Docs vivas por sistema (arquitectura, decisiones y gotchas):
│                               #   vision-optica, networking, tablet, catalogo-lentes,
│                               #   updates, licenciamiento, builds-deploy, backend
├── keystore/                   # Keystore de firma (GITIGNOREADO — ver nota en §3.2)
├── Packages/ · ProjectSettings/ # Configuración de Unity
└── README.md                   # (este archivo)
```

### Arquitectura en una imagen

```
        Meta Quest (VISOR) — Main.unity                     Tablet Android — Tablet.unity
  ┌──────────────────────────────────────┐           ┌──────────────────────────────────────┐
  │ DataManager (catálogo + estado ojo)   │           │ TabletController (UI procedural)       │
  │ VisionRendererFeature (post-proc URP) │           │   ├─ DiscoveryListener (UDP 9091)      │
  │ GlareController (halos/destellos)     │   WS 9090 │   ├─ WebSocketClient (TCP 9090)        │
  │ ScenarioManager (día / noche)         │ ◀───────▶ │   └─ Tarjetas de lente + sliders       │
  │ NetworkController                     │  UDP 9091 │ Recibe: catálogo, estado de visión,    │
  │   ├─ WebSocketServer (TCP 9090)       │ ◀───────  │         stream de vídeo (JPG)          │
  │   ├─ DiscoveryBeacon (UDP 9091)       │           │ Envía: apply_lens, override_params,    │
  │   └─ StreamingCapture (vídeo→tablet)  │           │        set_astigmatism, load_scenario  │
  └──────────────────────────────────────┘           └──────────────────────────────────────┘
                    │  HTTP (opcional)
                    ▼
        Backend Docker — FastAPI :8080
   /api/lenses · /api/manifest.json · /api/verify · /api/log · /admin
```

---

## 5. Funcionalidades principales

### Simulación de visión por ojo (núcleo)

- **Post-procesado estéreo independiente por ojo:** cada ojo puede llevar una lente distinta (simulación de *monovisión* / *blend*). Modela desenfoque por distancia (focos lejos/intermedio/cerca + profundidad de foco), pérdida de contraste y desenfoque máximo.
- **Disfotopsias realistas:** halos concéntricos, *starbursts* (destellos radiales con nº de rayos configurable) y dilatación pupilar nocturna, renderizados con *billboards* de deslumbramiento que se componen **encima** del desenfoque (inyección en URP antes de transparentes).
- **Astigmatismo:** magnitud y eje (ángulo) configurables desde la tablet.
- **Catálogo de lentes calibrado:** Monofocal estándar, PanOptix Pro (trifocal) y Vivity EDOF, con ~10 parámetros clínicos cada una. Extensible: el catálogo tolera parámetros nuevos del backend sin recompilar.

### Escenarios

- **Consultorio (día):** entorno de lectura; un libro anclado a la mano mide su distancia a la cámara para simular el foco de lectura con máscara en pantalla. Pupila contraída, sin halos.
- **Ruta nocturna (noche):** conducción con tráfico bidireccional (faros que se acercan / pilotos que se alejan), iluminación mesópica, pupila dilatada y halos activos — el escenario donde más se notan las diferencias entre lentes.

### Tablet de control (clínico)

- **Autodescubrimiento del visor** por UDP en la red local (o entrada manual de IP).
- **Selección de lente por ojo** (OD/OI) desde tarjetas con descripción clínica.
- **Ajuste de parámetros en vivo** con sliders (focos, desenfoque, halos, destellos, contraste, astigmatismo).
- **Streaming de vídeo en directo** de lo que ve el paciente (768×576 @ 20 Hz, JPG), con vista por ojo en modo *blend*.
- **Cambio de escenario** y **tema claro/oscuro** persistente.

### Controles VR (visor)

Mandos OpenXR: **A** cicla la lente del ojo izquierdo, **B** la del derecho, **X** activa/desactiva halos, **Y** cambia de escenario. HUD diagnóstico con FPS, lente por ojo y escenario actual.

### Robustez y red

- **Funciona sin conexión:** catálogo embebido + caché local; la sincronización con el backend es en segundo plano y nunca bloquea el arranque.
- **WebSocket propio** compatible con IL2CPP/Android (la implementación de `System.Net.WebSockets` no es fiable en ese runtime).
- **Persistencia local** de overrides de parámetros (con *debounce*) y preferencias de UI.

### Actualizaciones OTA semi-automáticas

- Al abrir, **cada app consulta el manifiesto de versión de su canal** (`visor` / `tablet`) contra el backend.
- Si hay una versión más nueva: **cartel de actualización** (en la tablet, overlay táctil; en el visor, panel VR con botones del mando) con el *changelog* → **descarga con progreso** → verificación **SHA-256** → instalador de Android (vía `FileProvider`).
- El backend soporta **versión mínima forzada** (bloqueo hasta actualizar), versiones activas independientes por app, y calcula el hash automáticamente al subir el APK desde el panel.
- **Telemetría del ciclo completo** (`update_check`, `update_accepted`, `update_success`…) visible en el panel de logs.

### Licenciamiento por dispositivo

- El visor **verifica su identidad** (`device_id`) contra el backend al arrancar. Un dispositivo desconocido se **auto-registra como "pendiente"** y queda en una pantalla de bloqueo *fail-closed*: sin input de gameplay y **sin red local** (la tablet ni siquiera puede descubrirlo).
- El administrador **aprueba o rechaza** con un clic desde el panel (los pendientes se destacan y el dashboard avisa). Un dispositivo rechazado no puede volver a auto-registrarse; también hay suspensión y licencias con vencimiento.
- **Gracia offline de 10 días**: un visor ya aprobado sigue funcionando sin internet con su última verificación cacheada — pensado para consultorios con conectividad intermitente.

### Backend y administración

- **API pública:** catálogo de lentes, manifiesto de versión por app, verificación de licencia (rate-limited, con auto-registro) y recepción de logs.
- **Panel de administración web** (`/admin`): login JWT, gestión de dispositivos (aprobar/rechazar/suspender), editor visual del catálogo de lentes, gestor de versiones por app con subida de APK a MinIO (SHA-256 automático), visor de logs con filtros/CSV e i18n es/en.

---

## 6. Usuario y contraseña de prueba

El **panel de administración del backend** tiene login.

| Entorno | URL | Usuario | Contraseña |
|---------|-----|---------|-----------|
| **Producción (demo del TFM)** | https://vr.conecta.sh/admin | _(entregado por canal privado)_ | _(entregado por canal privado)_ |
| Desarrollo local (`docker compose up`) | http://localhost:8080/admin | `admin` | `admin123` |

> Las credenciales locales se definen en `backend/.env` (`ADMIN_DEFAULT_USER` / `ADMIN_DEFAULT_PASS`);
> en producción están rotadas. Las credenciales de acceso al panel de producción para la corrección
> del TFM **no se publican en este repositorio**: se entregan junto con la documentación de la
> entrega, por el canal privado de la plataforma.

La **aplicación VR y la tablet no tienen login de usuario**: el control de acceso del visor es por
**licencia de dispositivo** — al arrancar se auto-registra como "pendiente" y queda bloqueado hasta que
un administrador lo aprueba desde el panel (`/api/verify`, ver §5 y [`docs/licenciamiento.md`](docs/licenciamiento.md)).

---

## 7. Despliegue, presentación y vídeo

| Recurso | Enlace |
|---------|--------|
| **Repositorio GitHub** | https://github.com/jonathandbdb/simulador-LIOS-Unity |
| **Despliegue (backend + panel admin)** | https://vr.conecta.sh — panel en [`/admin`](https://vr.conecta.sh/admin) (credenciales en §6), API interactiva en [`/docs`](https://vr.conecta.sh/docs) |
| **Presentación (slides)** | https://drive.google.com/file/d/12h8i-0dFvqWGQV6KG4d-I6HtJs3I5c4A/view?usp=sharing |
| **Vídeo de demostración** | https://youtu.be/zmguVpUjaKo |

**Notas sobre el despliegue:** el backend corre en un VPS con Docker Compose y HTTPS automático vía
Caddy (ver [`docs/builds-deploy.md`](docs/builds-deploy.md)). Las aplicaciones (visor Quest y tablet)
son APKs Android que se instalan por *sideload* en los dispositivos y a partir de ahí **se actualizan
solas** contra el backend mediante el sistema OTA propio (§5) — no se distribuyen públicamente porque
el visor requiere además la aprobación de licencia del dispositivo. La parte visitable de la demo sin
hardware VR es el **panel de administración** y la API; la experiencia VR completa se muestra en el
vídeo de demostración.

---

_Autor: Jonathan Varela · Máster — Trabajo de Fin de Máster (2026)._
