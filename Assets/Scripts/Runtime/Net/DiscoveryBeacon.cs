using System;
using System.Net;
using System.Net.Sockets;
using System.Text;
using UnityEngine;

namespace Simulador.Net
{
    /// <summary>
    /// Beacon UDP de descubrimiento LAN (lado visor). Port de discovery_beacon.gd:
    /// emite cada 2 s un broadcast a 255.255.255.255:9091 con
    /// {"app","device_label","ws_port","ts"} para que la tablet lo descubra sin IP
    /// manual. El receptor (DiscoveryListener/TabletController) identifica al visor
    /// por la IP de ORIGEN del paquete, no por este campo (ver DiscoveryListener):
    /// "device_label" es solo informativo, nunca parseado — por eso es seguro que
    /// sea un nombre amigable + nonce de sesion (P1.5) en vez de un identificador de
    /// hardware estable como SystemInfo.deviceUniqueIdentifier (fuga innecesaria a
    /// toda la subred via broadcast, sin auth ni cifrado).
    /// </summary>
    public class DiscoveryBeacon
    {
        private const int BeaconPort = 9091;
        private const string AppTag = "simulador-vr";
        private const float Interval = 2f;

        private UdpClient _udp;
        private IPEndPoint _dest;
        private float _timer;
        private string _label;
        private bool _ready;

        /// <param name="label">
        /// Etiqueta informativa a emitir en el beacon (nombre amigable + nonce de
        /// sesion, generada por NetworkController.Start() — NUNCA un identificador
        /// de hardware estable ni nada derivado del PIN de emparejamiento).
        /// </param>
        public void Start(string label)
        {
            _label = label;
            try
            {
                _udp = new UdpClient { EnableBroadcast = true };
                _dest = new IPEndPoint(IPAddress.Broadcast, BeaconPort);
                _ready = true;
                _timer = Interval; // primer beacon inmediato
                Debug.Log($"DiscoveryBeacon: broadcasting a 255.255.255.255:{BeaconPort} cada {Interval}s");
            }
            catch (Exception e) { Debug.LogWarning("DiscoveryBeacon: no se pudo iniciar: " + e.Message); }
        }

        public void Stop() { try { _udp?.Close(); } catch { } _ready = false; }

        /// <summary>Llamar desde Update con el timestamp unix actual.</summary>
        public void Tick(float dt, double unixTime)
        {
            if (!_ready) return;
            _timer += dt;
            if (_timer < Interval) return;
            _timer = 0f;
            try
            {
                string json = "{\"app\":\"" + AppTag + "\",\"device_label\":\"" + _label +
                              "\",\"ws_port\":9090,\"ts\":" + unixTime.ToString(System.Globalization.CultureInfo.InvariantCulture) + "}";
                var bytes = Encoding.UTF8.GetBytes(json);
                _udp.Send(bytes, bytes.Length, _dest);
            }
            catch (Exception) { /* sin red: no spamear */ }
        }
    }
}
