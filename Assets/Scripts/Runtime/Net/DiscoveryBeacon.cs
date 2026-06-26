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
    /// {"app","device_id","ws_port","ts"} para que la tablet lo descubra sin IP manual.
    /// </summary>
    public class DiscoveryBeacon
    {
        private const int BeaconPort = 9091;
        private const string AppTag = "simulador-vr";
        private const float Interval = 2f;

        private UdpClient _udp;
        private IPEndPoint _dest;
        private float _timer;
        private string _deviceId;
        private bool _ready;

        public void Start(string deviceId)
        {
            _deviceId = deviceId;
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
                string json = "{\"app\":\"" + AppTag + "\",\"device_id\":\"" + _deviceId +
                              "\",\"ws_port\":9090,\"ts\":" + unixTime.ToString(System.Globalization.CultureInfo.InvariantCulture) + "}";
                var bytes = Encoding.UTF8.GetBytes(json);
                _udp.Send(bytes, bytes.Length, _dest);
            }
            catch (Exception) { /* sin red: no spamear */ }
        }
    }
}
