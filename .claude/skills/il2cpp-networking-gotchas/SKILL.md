---
name: il2cpp-networking-gotchas
description: Gotchas de networking en IL2CPP/Android/Quest - por qué el WebSocket es a mano, threading socket→main, stripping, permisos, lifecycle. Cargar antes de tocar Assets/Scripts/Runtime/Net/ o TabletController.
---

# IL2CPP / Android — gotchas de networking

Estado del sistema y protocolo de mensajes: `docs/networking.md` (leer primero). Código:
`Assets/Scripts/Runtime/Net/`.

## Por qué el WebSocket es a mano (decisión fundacional)

`System.Net.WebSockets.ClientWebSocket`/`HttpListener` **no son fiables en IL2CPP/Android**
(handshakes que cuelgan, streams que no cierran, comportamiento distinto Editor vs device). Por
eso el proyecto implementa **RFC 6455 a mano** sobre `System.Net.Sockets`:
`WebSocketServer.cs` (visor) / `WebSocketClient.cs` (tablet). **Nunca** "modernizar" a
System.Net.WebSockets ni meter una librería de WS sin decisión explícita del usuario — es un
no-negociable (pedido así → `BLOCKED`).

Detalles del framing que muerden:
- Cliente→servidor va **enmascarado** (masking key obligatoria por RFC); servidor→cliente NO.
- Frames de control (ping/pong/close) pueden intercalarse: no asumir solo frames de texto/binario.

## Threading — el patrón obligatorio

Los threads de socket (accept/read) **JAMÁS tocan API de Unity** (ni transform, ni UI, ni
Debug.Log confiable en device). Patrón del repo:

```
thread socket → ConcurrentQueue<evento> → PumpEvents()/drenaje en Update() (main thread)
```

- Todo callback de red que termina en algo visible pasa por la cola. Sin excepciones.
- Envíos: pueden originarse en main thread; los writes al stream se serializan (lock/cola de
  salida) — no escribir al mismo socket desde dos threads.
- Nada de `async void`; cuidado con `Task.Run` que capture objetos Unity (el encode JPG de
  `StreamingCapture` está bien porque trabaja sobre bytes ya copiados).

## Stripping (IL2CPP)

- Reflection, serialización por nombres y generics instanciados solo dinámicamente **funcionan
  en Editor y mueren en build** (código eliminado por el stripper).
- Si aparece `MissingMethodException`/tipos null solo en device: sospechar stripping →
  `link.xml` preservando el tipo, o evitar la reflection.
- Newtonsoft (paquete `com.unity.nuget.newtonsoft-json`) está probado en este proyecto para los
  tipos del protocolo — mantener los mensajes como tipos/campos usados estáticamente.

## Permisos y plataforma Android

- `INTERNET` es automático al usar sockets, pero el **discovery UDP** puede requerir
  `MulticastLock` en algunos dispositivos/redes (broadcast filtrado por el sistema).
- Wi-Fi: visor y tablet deben estar en la MISMA red y sin aislamiento de clientes (AP
  isolation) — primera causa de "no se descubren".
- Puertos del proyecto: WS **:9090**, beacon UDP **:9091** (cambiarlos = tocar ambos lados +
  doc viva).

## Lifecycle

- Sockets/threads con shutdown+dispose explícito en `OnDestroy` y pausa/reanudación consciente
  en `OnApplicationPause` (Quest suspende la app al sacarse el visor; la tablet al bloquear
  pantalla). Un socket zombie = reconexión que falla hasta matar la app.
- El streaming (768×576 @20 Hz JPG) tiene backpressure limitada: no subir resolución/fps sin
  medir — saturar el WS congela la UI de la tablet.

## Validación real

La validación completa requiere **2 dispositivos** (visor + tablet en la misma red). Lo que se
puede validar en Editor: compilación, handshake local (visor en Editor + tablet en device o
viceversa). Todo retorno de agente que toque Net/ debe listar los pasos de prueba manual.
