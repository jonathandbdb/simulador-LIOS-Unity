---
name: minimal-footprint
description: Disciplina anti-over-engineering del enjambre - escalera de decisión antes de escribir código nuevo y NO-negociables que nunca se recortan. Inyectar a los agentes dev; lente del reviewer.
---

# Minimal footprint — no sobre-construir lo nuevo

Complementa el "diff mínimo" de `AGENTS.md`: *diff mínimo* = no reformatear lo existente;
*minimal footprint* = no **sobre-construir** lo nuevo. Sesga cada decisión hacia el menor cambio
que cumpla — sin recortar los NO-negociables de abajo.

## La escalera de decisión (antes de escribir lógica nueva)

1. **¿Necesita existir?** — ¿lo pidió el usuario o lo estás inventando? (YAGNI: nada de
   "ya que estamos", configurabilidad especulativa, abstracciones para un solo caso).
2. **¿Ya lo resuelve el proyecto?** — LensEngine/CatalogParser (lógica de lentes),
   TabletUiKit (widgets UI), NetworkController + WS/UDP existentes (mensajería),
   GlareSource/GlareBillboardInstance (halos), ScenarioManager (escenarios), TabletBuild
   (builds). Extender lo que hay > construir paralelo.
3. **¿Lo da Unity/URP/un paquete YA instalado?** — Input System, XRI, Newtonsoft, TMP, URP.
   (Paquete NUEVO = decisión del usuario, no tuya.)
4. **¿Puede ser más chico?** — ¿un campo en vez de una clase? ¿un parámetro en el catálogo en
   vez de un sistema nuevo? ¿un uniform más en el shader existente en vez de otro pass?
5. **Recién ahí: construí** — lo mínimo que cumple, con nombre y lugar consistentes con el
   sistema donde vive.

## NO-negociables (NUNCA se recortan en nombre de la simplicidad)

- **Compile-gate**: `unity_get_compilation_errors` limpio antes de reportar OK.
- **`.meta` pareados** y nada de editar generados.
- **Doc viva actualizada** en sitio si el cambio altera arquitectura/comportamiento/gotchas.
- **Comentarios en español** y cita al `.gd` de origen preservada en ports.
- **DataLogicTests extendido** si se tocó LensEngine/CatalogParser.
- **Invariantes IL2CPP** (threads→cola→Update, nada de System.Net.WebSockets).
- **Invariantes XR** (efectos por ojo correctos, RenderGraph, tier Mobile para Quest).
- **Restaurar el estado del Editor**: salir de play mode, guardar escena, loader OpenXR
  restaurado tras builds.
- **Anclaje bibliográfico** de fórmulas ópticas nuevas (comentario + doc viva).

## Atajos deliberados

Recortar algo que NO es no-negociable está permitido si se marca en el código, greppable:

```csharp
// SIM: atajo deliberado — <razón> (<qué faltaría para hacerlo completo>)
```

y se lista en "Riesgos / pendientes" del envelope.

## Cuándo NO aplica

- A lo que el usuario ya decidió explícitamente (no "simplifiques" su pedido).
- A la estructura obligatoria del proyecto (asmdefs, docs, tests de lógica pura).
- Linaje: adaptación de la skill homónima del enjambre original (a su vez inspirada en el
  plugin *ponytail*, MIT).
