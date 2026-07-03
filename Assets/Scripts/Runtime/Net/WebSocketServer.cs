using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using UnityEngine;

namespace Simulador.Net
{
    /// <summary>
    /// Servidor WebSocket minimal sobre TcpListener + handshake/framing manual.
    /// Port de autoloads/streaming_server.gd (Godot usaba TCPServer + WebSocketPeer).
    /// Se hace a mano porque System.Net.WebSockets server-side (HttpListener) no es
    /// confiable en IL2CPP/Android (Quest). Esto solo usa System.Net.Sockets, que si
    /// funciona. Acepta clientes, broadcast binario (JPG) y texto (JSON), y entrega
    /// los mensajes de texto entrantes en el hilo principal via PumpEvents(). El
    /// estado de autenticacion (emparejamiento por PIN) lo decide la capa de
    /// protocolo (NetworkController); este servidor solo lo guarda por cliente y
    /// usa el flag para filtrar los broadcasts (BroadcastText/BroadcastBinary van
    /// SOLO a clientes autenticados) y para cerrar conexiones puntuales.
    /// </summary>
    public class WebSocketServer
    {
        private const string WsGuid = "258EAFA5-E914-47DA-95CA-C5AB0DC85B11";

        // Tope de frame entrante (mensajes de control/texto de la tablet). Evita que un
        // header con len==127 falseado intente un new byte[n] gigante antes de fallar.
        private const long MaxIncomingFrameLength = 1 * 1024 * 1024; // 1 MB

        // Keep-alive: ping periodico por cliente; si no hay ninguna actividad entrante
        // (pong u otro frame) dentro del timeout, se considera el peer muerto y se libera el slot.
        private const int PingIntervalMs = 5000;
        private const int PongTimeoutMs = 15000;

        // Emparejamiento por PIN: si el cliente no autentica (mensaje "auth") dentro de
        // este tope, se cierra la conexion (conexion ociosa sin auth; el keep-alive de
        // arriba ya cubre el caso de peer muerto).
        private const int AuthTimeoutMs = 30000;

        private TcpListener _listener;
        private Thread _acceptThread;
        private volatile bool _running;
        private int _nextId = 1;

        private class Client
        {
            public int Id;
            public TcpClient Tcp;
            public NetworkStream Stream;
            public Thread ReadThread;
            public Thread PingThread;
            public volatile bool Open;
            public volatile int LastRecvTicks;
            public volatile bool Authenticated;
            // No hace falta volatile: se escribe una sola vez en AcceptLoop, ANTES de
            // Thread.Start() de ReadThread/PingThread (el arranque de un thread es un
            // happens-before), y nunca se vuelve a mutar despues. PingLoop solo lo LEE.
            public int ConnectedAtTicks;
            public readonly object WriteLock = new object();
        }

        private readonly ConcurrentDictionary<int, Client> _clients = new();

        // Eventos drenados en el hilo principal (PumpEvents).
        private readonly ConcurrentQueue<int> _connected = new();
        private readonly ConcurrentQueue<int> _disconnected = new();
        private readonly ConcurrentQueue<(int id, string text)> _textIn = new();
        private readonly ConcurrentQueue<string> _logQueue = new();

        public event Action<int> ClientConnected;
        public event Action<int> ClientDisconnected;
        public event Action<int, string> TextReceived;

        public bool IsListening => _running;

        public int OpenClientCount
        {
            get { int n = 0; foreach (var c in _clients.Values) if (c.Open) n++; return n; }
        }

        /// <summary>Clientes abiertos Y autenticados (gate real del stream y de los broadcasts).</summary>
        public int AuthenticatedClientCount
        {
            get { int n = 0; foreach (var c in _clients.Values) if (c.Open && c.Authenticated) n++; return n; }
        }

        public void Start(int port)
        {
            if (_running) return;
            _listener = new TcpListener(IPAddress.Any, port);
            _listener.Start();
            _running = true;
            _acceptThread = new Thread(AcceptLoop) { IsBackground = true, Name = "WSAccept" };
            _acceptThread.Start();
            Debug.Log($"WebSocketServer: escuchando en :{port}");
        }

        public void Stop()
        {
            _running = false;
            try { _listener?.Stop(); } catch { }
            foreach (var c in _clients.Values) CloseClient(c);
            _clients.Clear();
        }

        private void AcceptLoop()
        {
            while (_running)
            {
                try
                {
                    var tcp = _listener.AcceptTcpClient();
                    var c = new Client { Id = Interlocked.Increment(ref _nextId), Tcp = tcp, Stream = tcp.GetStream(), LastRecvTicks = Environment.TickCount, ConnectedAtTicks = Environment.TickCount };
                    if (!Handshake(c)) { try { tcp.Close(); } catch { } continue; }
                    c.Open = true;
                    _clients[c.Id] = c;
                    _connected.Enqueue(c.Id);
                    c.ReadThread = new Thread(() => ReadLoop(c)) { IsBackground = true, Name = "WSRead" + c.Id };
                    c.ReadThread.Start();
                    c.PingThread = new Thread(() => PingLoop(c)) { IsBackground = true, Name = "WSPing" + c.Id };
                    c.PingThread.Start();
                }
                catch (Exception) { if (_running) Thread.Sleep(50); }
            }
        }

        private bool Handshake(Client c)
        {
            try
            {
                var sb = new StringBuilder();
                var buf = new byte[1];
                // leer hasta \r\n\r\n
                int consecutive = 0;
                while (consecutive < 4)
                {
                    int r = c.Stream.Read(buf, 0, 1);
                    if (r <= 0) return false;
                    char ch = (char)buf[0];
                    sb.Append(ch);
                    if (ch == '\r' || ch == '\n') consecutive++; else consecutive = 0;
                    if (sb.Length > 4096) return false;
                }
                string req = sb.ToString();
                string key = null;
                foreach (var line in req.Split('\n'))
                {
                    int idx = line.IndexOf(':');
                    if (idx > 0 && line.Substring(0, idx).Trim().Equals("Sec-WebSocket-Key", StringComparison.OrdinalIgnoreCase))
                        key = line.Substring(idx + 1).Trim();
                }
                if (key == null) return false;
                string accept;
                using (var sha1 = SHA1.Create())
                    accept = Convert.ToBase64String(sha1.ComputeHash(Encoding.ASCII.GetBytes(key + WsGuid)));
                string resp = "HTTP/1.1 101 Switching Protocols\r\n" +
                              "Upgrade: websocket\r\n" +
                              "Connection: Upgrade\r\n" +
                              "Sec-WebSocket-Accept: " + accept + "\r\n\r\n";
                var rb = Encoding.ASCII.GetBytes(resp);
                c.Stream.Write(rb, 0, rb.Length);
                return true;
            }
            catch (Exception) { return false; }
        }

        private void ReadLoop(Client c)
        {
            try
            {
                while (_running && c.Open)
                {
                    var header = ReadExact(c.Stream, 2);
                    if (header == null) break;
                    int opcode = header[0] & 0x0F;
                    bool masked = (header[1] & 0x80) != 0;
                    long len = header[1] & 0x7F;
                    if (len == 126) { var ext = ReadExact(c.Stream, 2); if (ext == null) break; len = (ext[0] << 8) | ext[1]; }
                    else if (len == 127) { var ext = ReadExact(c.Stream, 8); if (ext == null) break; len = 0; for (int i = 0; i < 8; i++) len = (len << 8) | ext[i]; }
                    if (len > MaxIncomingFrameLength)
                    {
                        // len viene del peer sin validar: sin este tope, un valor gigante en el
                        // largo de 64 bits intenta un new byte[n] enorme antes de fallar (DoS barato).
                        _logQueue.Enqueue($"Net: cliente {c.Id} mando un frame de {len} bytes (> {MaxIncomingFrameLength}); cerrando conexion.");
                        break;
                    }
                    byte[] mask = null;
                    if (masked) { mask = ReadExact(c.Stream, 4); if (mask == null) break; }
                    var payload = len > 0 ? ReadExact(c.Stream, (int)len) : Array.Empty<byte>();
                    if (payload == null) break;
                    if (masked && mask != null)
                        for (int i = 0; i < payload.Length; i++) payload[i] ^= mask[i % 4];
                    c.LastRecvTicks = Environment.TickCount;

                    if (opcode == 0x8) break;            // close
                    if (opcode == 0x9) { SendFrame(c, 0xA, payload); continue; } // ping -> pong
                    if (opcode == 0x1)                   // text
                        _textIn.Enqueue((c.Id, Encoding.UTF8.GetString(payload)));
                }
            }
            catch (Exception) { }
            c.Open = false;
            _clients.TryRemove(c.Id, out _);
            _disconnected.Enqueue(c.Id);
            CloseClient(c);
        }

        /// <summary>
        /// Keep-alive por cliente: pinguea cada PingIntervalMs; si no llego ninguna
        /// actividad (pong u otro frame) en PongTimeoutMs, el peer se considera muerto y se
        /// cierra el socket para desbloquear el Read bloqueado de ReadLoop, que hace la
        /// limpieza normal (libera el slot y encola el disconnect).
        /// </summary>
        private void PingLoop(Client c)
        {
            while (_running && c.Open)
            {
                Thread.Sleep(PingIntervalMs);
                if (!_running || !c.Open) break;
                if (!c.Authenticated && unchecked(Environment.TickCount - c.ConnectedAtTicks) > AuthTimeoutMs)
                {
                    // Conexion ociosa sin auth (nunca mando el mensaje "auth"): el keep-alive
                    // de mas abajo no la cubre porque puede seguir respondiendo pong.
                    _logQueue.Enqueue($"Net: cliente {c.Id} no autentico en {AuthTimeoutMs / 1000}s; cerrando.");
                    c.Open = false;
                    CloseClient(c);
                    break;
                }
                int idleMs = unchecked(Environment.TickCount - c.LastRecvTicks);
                if (idleMs > PongTimeoutMs)
                {
                    _logQueue.Enqueue($"Net: cliente {c.Id} sin actividad hace {idleMs} ms; cerrando por keep-alive.");
                    c.Open = false;
                    CloseClient(c);
                    break;
                }
                SendFrame(c, 0x9, Array.Empty<byte>());
            }
        }

        private static byte[] ReadExact(NetworkStream s, int n)
        {
            var buf = new byte[n];
            int got = 0;
            while (got < n)
            {
                int r = s.Read(buf, got, n - got);
                if (r <= 0) return null;
                got += r;
            }
            return buf;
        }

        private void SendFrame(Client c, int opcode, byte[] payload)
        {
            if (!c.Open) return;
            try
            {
                int len = payload.Length;
                byte[] header;
                if (len <= 125) header = new byte[] { (byte)(0x80 | opcode), (byte)len };
                else if (len <= 65535) header = new byte[] { (byte)(0x80 | opcode), 126, (byte)(len >> 8), (byte)len };
                else header = new byte[] { (byte)(0x80 | opcode), 127, 0, 0, 0, 0, (byte)(len >> 24), (byte)(len >> 16), (byte)(len >> 8), (byte)len };
                lock (c.WriteLock)
                {
                    c.Stream.Write(header, 0, header.Length);
                    if (len > 0) c.Stream.Write(payload, 0, len);
                }
            }
            catch (Exception) { c.Open = false; }
        }

        // Solo a clientes autenticados: el stream (JPG) y el vision_state son datos
        // clinicos, no deben llegar a un cliente que todavia no probo el PIN.
        public void BroadcastBinary(byte[] data)
        {
            if (data == null || data.Length == 0) return;
            foreach (var c in _clients.Values) if (c.Open && c.Authenticated) SendFrame(c, 0x2, data);
        }

        public void BroadcastText(string text)
        {
            var bytes = Encoding.UTF8.GetBytes(text);
            foreach (var c in _clients.Values) if (c.Open && c.Authenticated) SendFrame(c, 0x1, bytes);
        }

        // SendTextTo no chequea auth a proposito: lo usa la capa de protocolo para
        // mandar auth_ok/auth_fail (antes de, o en el momento de, autenticar) y el
        // hello (inmediatamente despues de marcar al cliente autenticado).
        public void SendTextTo(int id, string text)
        {
            if (_clients.TryGetValue(id, out var c) && c.Open) SendFrame(c, 0x1, Encoding.UTF8.GetBytes(text));
        }

        /// <summary>Marca al cliente como autenticado (la capa de protocolo ya valido el PIN).</summary>
        public void MarkAuthenticated(int id)
        {
            if (_clients.TryGetValue(id, out var c)) c.Authenticated = true;
        }

        public bool IsAuthenticated(int id) => _clients.TryGetValue(id, out var c) && c.Authenticated;

        /// <summary>
        /// True si el cliente sigue abierto. La capa de protocolo lo usa para no
        /// procesar mensajes de un cliente al que ya le mando ForceDisconnect: el
        /// ReadLoop puede haber encolado varios mensajes en _textIn ANTES del cierre
        /// (ej. un burst de "auth" con PIN incorrecto), y PumpEvents los drena todos
        /// en la misma pasada; sin este chequeo, cada uno se procesaria como un
        /// intento nuevo aunque la conexion ya este muerta.
        /// </summary>
        public bool IsClientOpen(int id) => _clients.TryGetValue(id, out var c) && c.Open;

        /// <summary>
        /// Cierra una conexion puntual desde la capa de protocolo (PIN incorrecto o
        /// comando recibido antes de autenticar). Desbloquea el Read bloqueado del
        /// ReadLoop de ese cliente, que hace la limpieza normal (libera el slot y
        /// encola el disconnect).
        /// </summary>
        public void ForceDisconnect(int id)
        {
            if (_clients.TryGetValue(id, out var c)) { c.Open = false; CloseClient(c); }
        }

        private static void CloseClient(Client c)
        {
            try { c.Stream?.Close(); } catch { }
            try { c.Tcp?.Close(); } catch { }
        }

        /// <summary>Drena los eventos de red en el hilo principal (llamar desde Update).</summary>
        public void PumpEvents()
        {
            while (_connected.TryDequeue(out int cid)) ClientConnected?.Invoke(cid);
            while (_textIn.TryDequeue(out var t)) TextReceived?.Invoke(t.id, t.text);
            while (_disconnected.TryDequeue(out int did)) ClientDisconnected?.Invoke(did);
            while (_logQueue.TryDequeue(out var msg)) Debug.LogWarning(msg);
        }
    }
}
