using System;
using System.Globalization;
using Newtonsoft.Json;

namespace Simulador.License
{
    /// <summary>
    /// Logica PURA (sin Unity, sin IO) del sistema de licenciamiento por dispositivo: arma
    /// el request de <c>POST /api/verify</c>, parsea sus respuestas (200 ok / 403 denied),
    /// mapea el motivo de rechazo a un resultado de gate, y decide si corresponde permitir
    /// el arranque en modo "gracia offline" a partir de un cache local cuando el backend no
    /// es alcanzable. Calca el patron de <c>UpdateLogic.cs</c> (Simulador.Update), que a su
    /// vez calca <c>DataManagerLogic.cs</c> (Simulador.Data): funciones estaticas
    /// testeables, DTOs con <see cref="JsonPropertyAttribute"/> snake_case, nunca tira
    /// excepcion (ver docs/updates.md, mismo molde para el tercer sistema).
    /// </summary>
    public static class LicenseLogic
    {
        /// <summary>Dias de gracia offline: si el ultimo verify OK cacheado tiene como maximo esta antiguedad, se permite arrancar sin backend.</summary>
        public const int GraceDays = 10;

        // ---------------- DTOs (Newtonsoft, JsonProperty snake_case) ----------------

        /// <summary>Cuerpo de <c>POST /api/verify</c> (ver docs/backend.md).</summary>
        public class VerifyRequestDto
        {
            [JsonProperty("device_id")] public string DeviceId;
            [JsonProperty("current_apk_version")] public string CurrentApkVersion;
        }

        /// <summary>Respuesta 200 de <c>/api/verify</c>: dispositivo autorizado.</summary>
        public class VerifyOkDto
        {
            [JsonProperty("status")] public string Status;
            [JsonProperty("device_name")] public string DeviceName;
            [JsonProperty("license_expiry")] public string LicenseExpiry;
            // P7: modo de app + flag admin por dispositivo. Un backend viejo (pre-P7)
            // no manda estos campos -> default "pro": ausencia de informacion preserva
            // la UI completa actual; "standard" SOLO si el backend lo dice explicito.
            [JsonProperty("app_mode")] public string AppMode = "pro";
            [JsonProperty("is_admin")] public bool IsAdmin;
            [JsonProperty("message")] public string Message;
        }

        /// <summary>Respuesta 403 de <c>/api/verify</c>: dispositivo bloqueado, con motivo.</summary>
        public class VerifyDeniedDto
        {
            [JsonProperty("status")] public string Status;
            [JsonProperty("reason")] public string Reason;
            [JsonProperty("message")] public string Message;
        }

        /// <summary>
        /// Contenido del cache local de licencia (ultimo verify OK persistido), usado para
        /// evaluar la gracia offline cuando el backend no responde al arrancar.
        /// </summary>
        public class LicenseCacheDto
        {
            [JsonProperty("device_name")] public string DeviceName;
            [JsonProperty("license_expiry")] public string LicenseExpiry;
            // P7: la gracia offline conserva el modo/admin del ultimo verify OK
            // (un cache pre-P7 no los trae -> default "pro", mismo criterio que el DTO).
            [JsonProperty("app_mode")] public string AppMode = "pro";
            [JsonProperty("is_admin")] public bool IsAdmin;
            [JsonProperty("verified_at")] public string VerifiedAt;
        }

        /// <summary>Serializa el cuerpo de <c>POST /api/verify</c>. Nunca tira excepcion: null se trata como cadena vacia.</summary>
        public static string SerializeVerifyRequest(string deviceId, string apkVersion)
        {
            var dto = new VerifyRequestDto
            {
                DeviceId = deviceId ?? string.Empty,
                CurrentApkVersion = apkVersion ?? string.Empty,
            };
            return JsonConvert.SerializeObject(dto);
        }

        /// <summary>
        /// Parsea una respuesta 200 de <c>/api/verify</c>. Exige <c>status == "ok"</c> (si
        /// el JSON parsea pero trae otro status, o falta, se considera que NO es esta forma
        /// de respuesta -&gt; false). Nunca tira excepcion: cubre el caso de un captive
        /// portal que devuelve HTML con codigo 200 -- el parseo Newtonsoft falla y el
        /// caller lo trata como error de red, no como "denied" ni como "ok".
        /// </summary>
        public static bool TryParseVerifyOk(string json, out VerifyOkDto result)
        {
            result = null;
            if (string.IsNullOrWhiteSpace(json)) return false;
            try
            {
                var dto = JsonConvert.DeserializeObject<VerifyOkDto>(json);
                if (dto == null) return false;
                if (!string.Equals(dto.Status, "ok", StringComparison.Ordinal)) return false;
                result = dto;
                return true;
            }
            catch (Exception)
            {
                return false;
            }
        }

        /// <summary>
        /// Parsea una respuesta 403 de <c>/api/verify</c>. Exige <c>status == "denied"</c>
        /// (mismo criterio que <see cref="TryParseVerifyOk"/>). Nunca tira excepcion.
        /// </summary>
        public static bool TryParseVerifyDenied(string json, out VerifyDeniedDto result)
        {
            result = null;
            if (string.IsNullOrWhiteSpace(json)) return false;
            try
            {
                var dto = JsonConvert.DeserializeObject<VerifyDeniedDto>(json);
                if (dto == null) return false;
                if (!string.Equals(dto.Status, "denied", StringComparison.Ordinal)) return false;
                result = dto;
                return true;
            }
            catch (Exception)
            {
                return false;
            }
        }

        /// <summary>Resultado final del gate de licenciamiento (que hacer con el arranque de la app).</summary>
        public enum LicenseGateResult
        {
            Allow,
            AllowOfflineGrace,
            BlockPending,
            BlockRejected,
            BlockSuspended,
            BlockExpired,
            BlockNotFound,
            BlockOffline,
            BlockUnknown,
        }

        /// <summary>
        /// Mapea el <c>reason</c> de una respuesta 403 al resultado de gate correspondiente.
        /// Cualquier motivo no reconocido (incluido null/vacio) -&gt; <see cref="LicenseGateResult.BlockUnknown"/>:
        /// el backend puede agregar reasons nuevos a futuro y el visor debe seguir
        /// bloqueando (fail-safe CERRADO, a diferencia del fail-safe abierto de
        /// <c>UpdateLogic.Decide</c> -- acá el default seguro es NO dejar pasar).
        /// </summary>
        public static LicenseGateResult MapDeniedReason(string reason)
        {
            switch (reason)
            {
                case "DEVICE_PENDING": return LicenseGateResult.BlockPending;
                case "DEVICE_REJECTED": return LicenseGateResult.BlockRejected;
                case "DEVICE_SUSPENDED": return LicenseGateResult.BlockSuspended;
                case "LICENSE_EXPIRED": return LicenseGateResult.BlockExpired;
                case "DEVICE_NOT_FOUND": return LicenseGateResult.BlockNotFound;
                default: return LicenseGateResult.BlockUnknown;
            }
        }

        /// <summary>
        /// Evalua si corresponde dejar pasar en modo gracia offline a partir del cache
        /// local, cuando <c>/api/verify</c> no fue alcanzable. Nunca tira excepcion:
        /// <list type="bullet">
        /// <item>Cache null/corrupto/no parseable -&gt; <see cref="LicenseGateResult.BlockOffline"/>.</item>
        /// <item><c>license_expiry</c> presente y corrupta (no <c>yyyy-MM-dd</c>) -&gt; <see cref="LicenseGateResult.BlockOffline"/> (mas seguro bloquear que ignorar un vencimiento ilegible).</item>
        /// <item><c>license_expiry</c> presente y anterior a la fecha de <paramref name="utcNow"/> -&gt; <see cref="LicenseGateResult.BlockExpired"/> (la licencia vencio, la gracia offline no la revive).</item>
        /// <item><c>verified_at</c> ausente/corrupto -&gt; <see cref="LicenseGateResult.BlockOffline"/>.</item>
        /// <item><c>verified_at</c> en el futuro (reloj del dispositivo mal seteado) se clampea a <paramref name="utcNow"/> -- no debe "regalar" mas dias de gracia, pero tampoco debe brickear la app por un reloj adelantado.</item>
        /// <item><c>utcNow - verified_at &lt;= <see cref="GraceDays"/></c> (dias) -&gt; <see cref="LicenseGateResult.AllowOfflineGrace"/>; si no, <see cref="LicenseGateResult.BlockOffline"/>.</item>
        /// </list>
        /// </summary>
        public static LicenseGateResult EvaluateOffline(string cacheJson, DateTime utcNow)
        {
            if (!TryParseCache(cacheJson, out var cache)) return LicenseGateResult.BlockOffline;

            if (!string.IsNullOrWhiteSpace(cache.LicenseExpiry))
            {
                if (!DateTime.TryParseExact(cache.LicenseExpiry, "yyyy-MM-dd", CultureInfo.InvariantCulture,
                        DateTimeStyles.None, out var expiry))
                {
                    return LicenseGateResult.BlockOffline;
                }
                if (expiry.Date < utcNow.Date) return LicenseGateResult.BlockExpired;
            }

            if (!DateTime.TryParse(cache.VerifiedAt, CultureInfo.InvariantCulture,
                    DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var verifiedAt))
            {
                return LicenseGateResult.BlockOffline;
            }

            if (verifiedAt > utcNow) verifiedAt = utcNow;

            double elapsedDays = (utcNow - verifiedAt).TotalDays;
            return elapsedDays <= GraceDays ? LicenseGateResult.AllowOfflineGrace : LicenseGateResult.BlockOffline;
        }

        private static bool TryParseCache(string json, out LicenseCacheDto cache)
        {
            cache = null;
            if (string.IsNullOrWhiteSpace(json)) return false;
            try
            {
                var dto = JsonConvert.DeserializeObject<LicenseCacheDto>(json);
                if (dto == null) return false;
                cache = dto;
                return true;
            }
            catch (Exception)
            {
                return false;
            }
        }

        /// <summary>
        /// Arma el JSON del cache local a partir de un verify OK recien recibido, con
        /// <c>verified_at</c> serializado en ISO-8601 UTC (formato "o" de .NET, round-trip
        /// exacto con <see cref="EvaluateOffline"/>). <paramref name="ok"/> null se trata
        /// como campos vacios (nunca tira excepcion).
        /// </summary>
        public static string BuildCacheJson(VerifyOkDto ok, DateTime utcNow)
        {
            var cache = new LicenseCacheDto
            {
                DeviceName = ok?.DeviceName,
                LicenseExpiry = ok?.LicenseExpiry,
                AppMode = string.IsNullOrEmpty(ok?.AppMode) ? "pro" : ok.AppMode,
                IsAdmin = ok?.IsAdmin ?? false,
                VerifiedAt = utcNow.ToString("o", CultureInfo.InvariantCulture),
            };
            return JsonConvert.SerializeObject(cache);
        }

        /// <summary>
        /// Extrae (modo, admin) de un cache local, con defaults ("pro"/false) ante
        /// cache null/corrupto/pre-P7 -- ausencia de informacion preserva la UI
        /// completa (mismo criterio que VerifyOkDto). Para la gracia offline: el
        /// modo del ultimo verify OK sigue valiendo sin backend.
        /// </summary>
        public static (string appMode, bool isAdmin) ReadModeFromCache(string cacheJson)
        {
            if (!TryParseCache(cacheJson, out var cache)) return ("pro", false);
            string mode = string.IsNullOrEmpty(cache.AppMode) ? "pro" : cache.AppMode;
            return (mode, cache.IsAdmin);
        }
    }
}
