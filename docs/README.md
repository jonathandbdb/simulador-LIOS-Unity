# Docs vivas — índice y protocolo

Documentación técnica **viva** del Simulador de LIOs: un documento por sistema, que describe el
estado **actual** del código (no su historia). Sirve a humanos (incl. anexos del TFM) y es la
primera fuente de contexto de los agentes del enjambre.

## Índice

| Doc | Sistema | Código principal |
|-----|---------|------------------|
| [vision-optica.md](vision-optica.md) | Visión / óptica clínica / shaders | `Assets/Scripts/Runtime/Vision/`, `Assets/Shaders/` |
| [networking.md](networking.md) | Comunicación visor↔tablet (UDP + WS) | `Assets/Scripts/Runtime/Net/` |
| [tablet.md](tablet.md) | App de control (UI procedural) | `TabletController.cs`, `Assets/Scripts/Runtime/Tablet/` |
| [catalogo-lentes.md](catalogo-lentes.md) | Catálogo y motor de lentes | `Assets/Scripts/Runtime/Data/`, `StreamingAssets/lentes.json` |
| [builds-deploy.md](builds-deploy.md) | Builds visor/tablet, adb, deploy | `Assets/Scripts/Editor/TabletBuild.cs` |
| [backend.md](backend.md) | Backend FastAPI/Docker | `backend/`, `defaults/lentes.json` |

## Formato común

100–250 líneas, secciones fijas:

```
# <Sistema>
## Qué es y por qué          (2-5 líneas)
## Arquitectura actual        (cada archivo con su rol; diagrama ASCII si ayuda)
## Decisiones y porqués       (bullets: decisión → razón; se edita EN SITIO, sin changelog)
## Gotchas                    (lo que muerde, con su porqué)
## Cómo probar                (receta concreta de validación)
## Pendientes / deuda         (bullets cortos)
```

## Protocolo (loop anti-drift)

1. **Leer primero**: todo agente (y humano) lee la doc del sistema **ANTES de grepear el
   código** — es el resumen curado (arquitectura, decisiones, gotchas) y orienta más rápido que
   el código crudo.
2. **Actualizar al cerrar**: todo cambio que altere arquitectura, comportamiento o gotchas
   actualiza la doc **EN SITIO, en la misma tarea** — se edita la sección afectada, no se apila
   changelog. El hook `post_edit.sh` lo recuerda; el recordatorio no es opcional.
3. **El drift es un hallazgo**: si una doc contradice el código, se reporta (y se corrige la
   doc) — una doc que miente envenena el contexto del próximo agente y del próximo lector.

El código alimenta la doc; la doc alimenta el contexto. Ese es el loop.
