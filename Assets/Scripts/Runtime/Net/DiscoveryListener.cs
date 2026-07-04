using System;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace Simulador.Net
{
    /// <summary>
    /// Escucha beacons UDP del visor (:9091) y reporta el host (IP) descubierto,
    /// junto con el "device_label" informativo del payload (nombre amigable +
    /// nonce de sesion, ver DiscoveryBeacon/NetworkController.GenerateBeaconLabel)
    /// para que la UI pueda mostrar un nombre en vez de la IP. Lado tablet de
    /// discovery_beacon.gd. (El MulticastLock de Android se omite: en la mayoria
    /// de redes el broadcast dirigido llega; si hiciera falta se agrega.)
    /// </summary>
    public class DiscoveryListener
    {
        private const int BeaconPort = 9091;
        private const string AppTag = "simulador-vr";

        private UdpClient _udp;
        private Thread _thread;
        private volatile bool _running;
        private readonly ConcurrentQueue<(string Host, string Label)> _hosts = new();

        /// <summary>Host (IP) descubierto + su device_label (puede ser null si el payload no lo trae/no parseo).</summary>
        public event Action<string, string> VisorDiscovered;

        public void Start()
        {
            try
            {
                _udp = new UdpClient();
                _udp.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
                _udp.Client.Bind(new IPEndPoint(IPAddress.Any, BeaconPort));
                _running = true;
                _thread = new Thread(Loop) { IsBackground = true, Name = "Discovery" };
                _thread.Start();
                Debug.Log($"DiscoveryListener: escuchando :{BeaconPort}");
            }
            catch (Exception e) { Debug.LogWarning("DiscoveryListener: " + e.Message); }
        }

        public void Stop() { _running = false; try { _udp?.Close(); } catch { } }

        private void Loop()
        {
            var ep = new IPEndPoint(IPAddress.Any, 0);
            while (_running)
            {
                try
                {
                    var data = _udp.Receive(ref ep);
                    var text = Encoding.UTF8.GetString(data);
                    if (!text.Contains("\"app\"") || !text.Contains(AppTag)) continue;
                    string label = null;
                    // Parseo defensivo: es solo para mostrar un nombre en la UI, un
                    // payload malformado no debe tirar abajo el thread de discovery.
                    try { label = JObject.Parse(text)["device_label"]?.ToString(); }
                    catch (Exception) { /* sin nombre amigable: la UI cae al generico */ }
                    _hosts.Enqueue((ep.Address.ToString(), label));
                }
                catch (Exception) { if (_running) Thread.Sleep(50); }
            }
        }

        /// <summary>Drena hosts descubiertos en el hilo principal.</summary>
        public void PumpEvents()
        {
            while (_hosts.TryDequeue(out var h)) VisorDiscovered?.Invoke(h.Host, h.Label);
        }
    }
}
