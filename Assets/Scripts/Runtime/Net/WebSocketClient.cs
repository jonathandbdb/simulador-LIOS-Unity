using System;
using System.Collections.Concurrent;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using UnityEngine;

namespace Simulador.Net
{
    /// <summary>
    /// Cliente WebSocket minimal (lado tablet). Espejo de WebSocketServer: hace el
    /// handshake como cliente, lee frames (texto JSON + binario JPG) y envia texto
    /// enmascarado (client->server DEBE ir enmascarado). Entrega los mensajes en el
    /// hilo principal via PumpEvents().
    /// </summary>
    public class WebSocketClient
    {
        private const string WsGuid = "258EAFA5-E914-47DA-95CA-C5AB0DC85B11";

        private TcpClient _tcp;
        private NetworkStream _stream;
        private Thread _readThread;
        private volatile bool _open;
        private readonly object _writeLock = new object();

        private readonly ConcurrentQueue<string> _textIn = new();
        private readonly ConcurrentQueue<byte[]> _binIn = new();
        private volatile bool _connectedFlag;
        private volatile bool _closedFlag;

        public event Action Connected;
        public event Action Disconnected;
        public event Action<string> TextReceived;
        public event Action<byte[]> BinaryReceived;

        public bool IsOpen => _open;

        public void Connect(string host, int port)
        {
            Close();
            _readThread = new Thread(() => Run(host, port)) { IsBackground = true, Name = "WSClient" };
            _readThread.Start();
        }

        private void Run(string host, int port)
        {
            try
            {
                _tcp = new TcpClient();
                _tcp.Connect(host, port);
                _stream = _tcp.GetStream();

                string key = Convert.ToBase64String(Guid.NewGuid().ToByteArray());
                string req = "GET / HTTP/1.1\r\nHost: " + host + ":" + port + "\r\n" +
                             "Upgrade: websocket\r\nConnection: Upgrade\r\n" +
                             "Sec-WebSocket-Key: " + key + "\r\nSec-WebSocket-Version: 13\r\n\r\n";
                var rb = Encoding.ASCII.GetBytes(req);
                _stream.Write(rb, 0, rb.Length);

                // leer respuesta handshake hasta \r\n\r\n
                var sb = new StringBuilder(); int consec = 0; var b = new byte[1];
                while (consec < 4)
                {
                    if (_stream.Read(b, 0, 1) <= 0) throw new Exception("handshake EOF");
                    char ch = (char)b[0]; sb.Append(ch);
                    if (ch == '\r' || ch == '\n') consec++; else consec = 0;
                    if (sb.Length > 4096) throw new Exception("handshake too long");
                }
                if (!sb.ToString().Contains("101")) throw new Exception("no 101");

                _open = true; _connectedFlag = true;
                ReadLoop();
            }
            catch (Exception) { }
            _open = false; _closedFlag = true;
            try { _stream?.Close(); } catch { }
            try { _tcp?.Close(); } catch { }
        }

        private void ReadLoop()
        {
            while (_open)
            {
                var h = ReadExact(2); if (h == null) break;
                int opcode = h[0] & 0x0F;
                bool masked = (h[1] & 0x80) != 0;
                long len = h[1] & 0x7F;
                if (len == 126) { var e = ReadExact(2); if (e == null) break; len = (e[0] << 8) | e[1]; }
                else if (len == 127) { var e = ReadExact(8); if (e == null) break; len = 0; for (int i = 0; i < 8; i++) len = (len << 8) | e[i]; }
                byte[] mask = null;
                if (masked) { mask = ReadExact(4); if (mask == null) break; }
                var payload = len > 0 ? ReadExact((int)len) : Array.Empty<byte>();
                if (payload == null) break;
                if (masked && mask != null) for (int i = 0; i < payload.Length; i++) payload[i] ^= mask[i % 4];

                if (opcode == 0x8) break;
                if (opcode == 0x1) _textIn.Enqueue(Encoding.UTF8.GetString(payload));
                else if (opcode == 0x2) _binIn.Enqueue(payload);
            }
        }

        private byte[] ReadExact(int n)
        {
            var buf = new byte[n]; int got = 0;
            while (got < n) { int r = _stream.Read(buf, got, n - got); if (r <= 0) return null; got += r; }
            return buf;
        }

        public void SendText(string text)
        {
            if (!_open) return;
            try
            {
                var payload = Encoding.UTF8.GetBytes(text);
                int n = payload.Length;
                var mask = Guid.NewGuid().ToByteArray();
                byte[] header;
                if (n <= 125) header = new byte[] { 0x81, (byte)(0x80 | n) };
                else if (n <= 65535) header = new byte[] { 0x81, (byte)(0x80 | 126), (byte)(n >> 8), (byte)n };
                else header = new byte[] { 0x81, (byte)(0x80 | 127), 0, 0, 0, 0, (byte)(n >> 24), (byte)(n >> 16), (byte)(n >> 8), (byte)n };
                var masked = new byte[n];
                for (int i = 0; i < n; i++) masked[i] = (byte)(payload[i] ^ mask[i % 4]);
                lock (_writeLock)
                {
                    _stream.Write(header, 0, header.Length);
                    _stream.Write(mask, 0, 4);
                    _stream.Write(masked, 0, n);
                }
            }
            catch (Exception) { _open = false; }
        }

        public void Close()
        {
            _open = false;
            try { _stream?.Close(); } catch { }
            try { _tcp?.Close(); } catch { }
        }

        /// <summary>Drena eventos en el hilo principal (llamar desde Update).</summary>
        public void PumpEvents()
        {
            if (_connectedFlag) { _connectedFlag = false; Connected?.Invoke(); }
            while (_textIn.TryDequeue(out var t)) TextReceived?.Invoke(t);
            while (_binIn.TryDequeue(out var b)) BinaryReceived?.Invoke(b);
            if (_closedFlag) { _closedFlag = false; Disconnected?.Invoke(); }
        }
    }
}
