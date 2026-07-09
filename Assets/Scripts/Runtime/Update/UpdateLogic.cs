using System;
using System.Collections.Generic;
using Newtonsoft.Json;

namespace Simulador.Update
{
    /// <summary>
    /// Logica PURA (sin Unity, sin IO) del sistema de updates semi-automaticos: parseo/
    /// comparacion de semver, parseo del manifest del backend (GET /api/manifest.json,
    /// ver docs/updates.md) y la decision de si corresponde ofrecer/forzar una
    /// actualizacion. Calca el patron de DataManagerLogic.cs (Simulador.Data): funciones
    /// estaticas testeables, DTO privado snake_case + mapeo con Newtonsoft, nunca tira
    /// excepcion.
    /// </summary>
    public static class UpdateLogic
    {
        /// <summary>
        /// Intenta parsear "major.minor.patch". Componentes faltantes se completan con 0
        /// ("1.2" -> (1,2,0), "1" -> (1,0,0)). Mas de 3 componentes ("1.2.3.4") se
        /// considera invalido -- FALSE: el contrato del backend (ver docs/updates.md) es
        /// estrictamente major.minor.patch, un cuarto componente es un dato con forma
        /// desconocida y es mas seguro fallar que adivinar cual descartar. Nunca tira
        /// excepcion: null/vacio/con letras/etc -> false, result en default.
        /// </summary>
        public static bool TryParseSemver(string version, out (int major, int minor, int patch) result)
        {
            result = default;
            if (string.IsNullOrWhiteSpace(version)) return false;

            string[] parts = version.Trim().Split('.');
            if (parts.Length == 0 || parts.Length > 3) return false;

            int[] nums = new int[3];
            for (int i = 0; i < parts.Length; i++)
            {
                if (!int.TryParse(parts[i], out int n) || n < 0) return false;
                nums[i] = n;
            }
            result = (nums[0], nums[1], nums[2]);
            return true;
        }

        /// <summary>
        /// Compara dos versiones semver: -1 si a &lt; b, 0 si son iguales, 1 si a &gt; b.
        /// Compara major, luego minor, luego patch. Si alguna de las dos NO parsea, se la
        /// trata como "0.0.0" para la comparacion (nunca tira excepcion, siempre devuelve
        /// un resultado) -- en la practica este fallback rara vez importa porque
        /// <see cref="Decide"/> ya filtra aparte el caso "version remota no parseable"
        /// antes de llegar a comparar.
        /// </summary>
        public static int CompareVersions(string a, string b)
        {
            if (!TryParseSemver(a, out var va)) va = (0, 0, 0);
            if (!TryParseSemver(b, out var vb)) vb = (0, 0, 0);

            if (va.major != vb.major) return va.major.CompareTo(vb.major);
            if (va.minor != vb.minor) return va.minor.CompareTo(vb.minor);
            return va.patch.CompareTo(vb.patch);
        }

        /// <summary>Resultado de parsear el manifest del backend (GET /api/manifest.json).</summary>
        public class UpdateManifest
        {
            public string App;
            public string ApkVersion;
            public string MinApkVersion;
            public string ApkUrl;
            public string ApkSha256;
            public string Changelog;
        }

        // DTO privado snake_case, mismo patron que BackendConfig en DataManagerLogic.cs.
        private class ManifestDto
        {
            public string app;
            public string apk_version;
            public string min_apk_version;
            public string apk_url;
            public string apk_sha256;
            public string changelog;
        }

        /// <summary>
        /// Parsea el JSON del manifest del backend. JSON invalido/vacio/nulo, o que
        /// deserializa a null -&gt; false, manifest null. Nunca tira excepcion. Claves
        /// FALTANTES (p.ej. <c>{"app":"visor"}</c> sin las demas) NO invalidan el parseo
        /// -- el JSON es valido y el objeto no es null, solo quedan esas propiedades en
        /// null; es <see cref="Decide"/> quien trata una version remota null/no-parseable
        /// como "no hay update" (fail-safe), asi que no hace falta duplicar esa validacion
        /// aca.
        /// </summary>
        public static bool TryParseManifest(string json, out UpdateManifest manifest)
        {
            manifest = null;
            if (string.IsNullOrWhiteSpace(json)) return false;
            try
            {
                var dto = JsonConvert.DeserializeObject<ManifestDto>(json);
                if (dto == null) return false;
                manifest = new UpdateManifest
                {
                    App = dto.app,
                    ApkVersion = dto.apk_version,
                    MinApkVersion = dto.min_apk_version,
                    ApkUrl = dto.apk_url,
                    ApkSha256 = dto.apk_sha256,
                    Changelog = dto.changelog,
                };
                return true;
            }
            catch (Exception)
            {
                return false;
            }
        }

        /// <summary>Que hacer con un manifest ya parseado, ver docs/updates.md §Flujo.</summary>
        public enum UpdateDecision { None, Optional, Forced }

        /// <summary>
        /// Decide si corresponde ofrecer (Optional) o forzar (Forced) una actualizacion,
        /// comparando la version instalada contra el manifest del backend. Fail-safe a
        /// <see cref="UpdateDecision.None"/> en cualquier caso ambiguo: manifest null,
        /// <c>ApkVersion</c> no parseable, o version remota <= instalada (cubre paridad,
        /// como el dummy 0.1.0==0.1.0, y downgrade). Si la remota es mayor: Forced cuando
        /// la instalada queda por debajo de <c>MinApkVersion</c> (solo si ese campo
        /// parsea -- si no parsea/esta ausente se trata como "sin minimo exigido", NUNCA
        /// se fuerza por un dato faltante); si no, Optional.
        /// </summary>
        public static UpdateDecision Decide(string installedVersion, UpdateManifest manifest)
        {
            if (manifest == null) return UpdateDecision.None;
            if (!TryParseSemver(manifest.ApkVersion, out _)) return UpdateDecision.None;
            if (CompareVersions(manifest.ApkVersion, installedVersion) <= 0) return UpdateDecision.None;

            if (TryParseSemver(manifest.MinApkVersion, out _) &&
                CompareVersions(installedVersion, manifest.MinApkVersion) < 0)
            {
                return UpdateDecision.Forced;
            }
            return UpdateDecision.Optional;
        }

        /// <summary>
        /// Deriva el canal ("app" del query <c>?app=visor|tablet</c>) desde
        /// Application.identifier. El visor es <c>com.simulador.vr</c>, la tablet
        /// <c>com.simulador.tablet</c> -- cualquier identifier que contenga ".tablet" se
        /// mapea a "tablet", cualquier otra cosa (incluido null/vacio) a "visor" (default
        /// del endpoint sin <c>?app</c>, ver docs/updates.md).
        /// </summary>
        public static string AppChannelFromIdentifier(string identifier)
        {
            return !string.IsNullOrEmpty(identifier) && identifier.Contains(".tablet") ? "tablet" : "visor";
        }

        /// <summary>
        /// Compara dos hashes SHA256 en hex, sin distinguir mayus/minus. Si
        /// <paramref name="expectedHex"/> es null/vacio/whitespace se considera "nada que
        /// verificar" -&gt; true (el manifest dummy manda <c>apk_sha256: ""</c>, ver
        /// docs/updates.md).
        /// </summary>
        public static bool Sha256Matches(string expectedHex, string actualHex)
        {
            if (string.IsNullOrWhiteSpace(expectedHex)) return true;
            return string.Equals(expectedHex.Trim(), (actualHex ?? string.Empty).Trim(),
                StringComparison.OrdinalIgnoreCase);
        }

        // ---------------- Telemetria (F6) ----------------
        /// <summary>Un evento individual del batch de telemetria hacia POST /api/log (ver docs/updates.md).</summary>
        public readonly struct LogEvent
        {
            public readonly string Event;
            public readonly string Detail;

            public LogEvent(string eventName, string detail)
            {
                Event = eventName ?? string.Empty;
                Detail = detail ?? string.Empty;
            }
        }

        // DTOs privados snake_case para el batch, mismo patron que ManifestDto. "event" es
        // palabra reservada de C#, por eso el mapeo va con JsonProperty en vez del nombre
        // del campo (evita tener que escapar el identificador con @event).
        private class LogEventDto
        {
            [JsonProperty("event")] public string EventName;
            [JsonProperty("detail")] public string Detail;
        }

        private class LogBatchDto
        {
            [JsonProperty("device_id")] public string DeviceId;
            [JsonProperty("events")] public List<LogEventDto> Events;
        }

        /// <summary>
        /// Serializa un batch de eventos de telemetria al JSON que espera POST /api/log del
        /// backend (<c>{"device_id":..., "events":[{"event":...,"detail":...}]}</c>, ver
        /// docs/updates.md). Nunca tira excepcion: deviceId/events nulos se tratan como
        /// vacios (events -&gt; lista vacia, nunca null en el JSON resultante).
        /// </summary>
        public static string SerializeLogBatch(string deviceId, IReadOnlyList<LogEvent> events)
        {
            var dto = new LogBatchDto
            {
                DeviceId = deviceId ?? string.Empty,
                Events = new List<LogEventDto>(events?.Count ?? 0),
            };
            if (events != null)
            {
                foreach (var e in events)
                    dto.Events.Add(new LogEventDto { EventName = e.Event, Detail = e.Detail });
            }
            return JsonConvert.SerializeObject(dto);
        }

        // ---------------- Marcador de install pendiente (F4) ----------------
        // UpdateInstaller (JNI) escribe este archivo justo antes de lanzar el intent de
        // instalacion; UpdateManager lo lee al arrancar para saber si el update anterior
        // se aplico. Nombre de archivo centralizado aca (unica fuente de verdad, lo usan
        // ambas clases) -- ver docs/updates.md.
        public const string PendingMarkerFileName = "update_pending.json";

        /// <summary>Contenido del marcador de install pendiente (F4).</summary>
        public class UpdatePendingMarker
        {
            public string TargetVersion;
        }

        // DTO privado snake_case, mismo patron que ManifestDto.
        private class UpdatePendingMarkerDto
        {
            public string target_version;
        }

        /// <summary>Serializa el marcador con la version objetivo del manifest aceptado.</summary>
        public static string SerializePendingMarker(string targetVersion)
        {
            return JsonConvert.SerializeObject(new UpdatePendingMarkerDto { target_version = targetVersion ?? "" });
        }

        /// <summary>
        /// Parsea el marcador de install pendiente. JSON invalido/vacio/nulo -&gt; false,
        /// marker null. Nunca tira excepcion (mismo contrato que TryParseManifest).
        /// </summary>
        public static bool TryParsePendingMarker(string json, out UpdatePendingMarker marker)
        {
            marker = null;
            if (string.IsNullOrWhiteSpace(json)) return false;
            try
            {
                var dto = JsonConvert.DeserializeObject<UpdatePendingMarkerDto>(json);
                if (dto == null) return false;
                marker = new UpdatePendingMarker { TargetVersion = dto.target_version };
                return true;
            }
            catch (Exception)
            {
                return false;
            }
        }
    }
}
