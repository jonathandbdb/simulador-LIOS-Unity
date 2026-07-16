using System;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using Simulador.License;

namespace Simulador.Tests
{
    /// <summary>
    /// Tests de la logica PURA de licenciamiento (LicenseLogic): serializacion del
    /// request de verify, parseo de las respuestas 200/403, mapeo de reasons y la
    /// evaluacion de gracia offline. Mismo estilo que UpdateLogicTests.cs (molde
    /// identico, ver docs/updates.md).
    /// </summary>
    public class LicenseLogicTests
    {
        // ---------------- SerializeVerifyRequest ----------------

        [Test]
        public void SerializeVerifyRequest_MapeaDeviceIdYVersion()
        {
            string json = LicenseLogic.SerializeVerifyRequest("device-abc", "0.1.0");
            var obj = JObject.Parse(json);

            Assert.AreEqual("device-abc", (string)obj["device_id"]);
            Assert.AreEqual("0.1.0", (string)obj["current_apk_version"]);
        }

        [Test]
        public void SerializeVerifyRequest_NullsNoTiranExcepcionYQuedanVacios()
        {
            string json = LicenseLogic.SerializeVerifyRequest(null, null);
            var obj = JObject.Parse(json);

            Assert.AreEqual("", (string)obj["device_id"]);
            Assert.AreEqual("", (string)obj["current_apk_version"]);
        }

        // ---------------- TryParseVerifyOk ----------------

        [Test]
        public void TryParseVerifyOk_ExpiryNull_ParseaOk()
        {
            string json = @"{""status"":""ok"",""device_name"":""Visor Consultorio 1"",""license_expiry"":null,""message"":""todo en orden""}";
            bool ok = LicenseLogic.TryParseVerifyOk(json, out var result);

            Assert.IsTrue(ok);
            Assert.AreEqual("Visor Consultorio 1", result.DeviceName);
            Assert.IsNull(result.LicenseExpiry);
            Assert.AreEqual("todo en orden", result.Message);
        }

        [Test]
        public void TryParseVerifyOk_ExpiryConFecha_ParseaOk()
        {
            string json = @"{""status"":""ok"",""device_name"":""Tablet Sala 2"",""license_expiry"":""2026-12-31"",""message"":""ok""}";
            bool ok = LicenseLogic.TryParseVerifyOk(json, out var result);

            Assert.IsTrue(ok);
            Assert.AreEqual("2026-12-31", result.LicenseExpiry);
        }

        // ---------------- P7: app_mode / is_admin ----------------

        [Test]
        public void TryParseVerifyOk_ConModoYAdmin_ParseaCampos()
        {
            string json = @"{""status"":""ok"",""device_name"":""V"",""license_expiry"":null,""app_mode"":""pro"",""is_admin"":true,""message"":""ok""}";
            Assert.IsTrue(LicenseLogic.TryParseVerifyOk(json, out var result));
            Assert.AreEqual("pro", result.AppMode);
            Assert.IsTrue(result.IsAdmin);
        }

        [Test]
        public void TryParseVerifyOk_BackendViejoSinModo_DefaultsPro()
        {
            // Un backend pre-P7 no manda app_mode/is_admin: default "pro"/false --
            // ausencia de informacion preserva la UI completa actual.
            string json = @"{""status"":""ok"",""device_name"":""V"",""license_expiry"":null,""message"":""ok""}";
            Assert.IsTrue(LicenseLogic.TryParseVerifyOk(json, out var result));
            Assert.AreEqual("pro", result.AppMode);
            Assert.IsFalse(result.IsAdmin);
        }

        [Test]
        public void BuildCacheJson_RoundTripConservaModoYAdmin()
        {
            var ok = new LicenseLogic.VerifyOkDto
            {
                Status = "ok", DeviceName = "V", LicenseExpiry = null,
                AppMode = "pro", IsAdmin = true, Message = "ok",
            };
            string cache = LicenseLogic.BuildCacheJson(ok, new System.DateTime(2026, 7, 15, 12, 0, 0, System.DateTimeKind.Utc));
            var (mode, admin) = LicenseLogic.ReadModeFromCache(cache);
            Assert.AreEqual("pro", mode);
            Assert.IsTrue(admin);
        }

        [Test]
        public void ReadModeFromCache_CachePreP7ONuloOCorrupto_DefaultsPro()
        {
            // Cache escrito antes de P7 (sin app_mode/is_admin): default "pro".
            string old = @"{""device_name"":""V"",""license_expiry"":null,""verified_at"":""2026-07-01T00:00:00Z""}";
            var (mode, admin) = LicenseLogic.ReadModeFromCache(old);
            Assert.AreEqual("pro", mode);
            Assert.IsFalse(admin);

            Assert.AreEqual(("pro", false), LicenseLogic.ReadModeFromCache(null));
            Assert.AreEqual(("pro", false), LicenseLogic.ReadModeFromCache("no soy json"));
        }

        [Test]
        public void TryParseVerifyOk_StatusDistinto_DevuelveFalse()
        {
            string json = @"{""status"":""denied"",""device_name"":""x""}";
            Assert.IsFalse(LicenseLogic.TryParseVerifyOk(json, out var result));
            Assert.IsNull(result);
        }

        [Test]
        public void TryParseVerifyOk_HtmlBasuraOVacio_NuncaTiraYDevuelveFalse()
        {
            Assert.IsFalse(LicenseLogic.TryParseVerifyOk("<html><body>captive portal</body></html>", out var r1));
            Assert.IsNull(r1);
            Assert.IsFalse(LicenseLogic.TryParseVerifyOk("no soy json", out var r2));
            Assert.IsNull(r2);
            Assert.IsFalse(LicenseLogic.TryParseVerifyOk("", out var r3));
            Assert.IsNull(r3);
            Assert.IsFalse(LicenseLogic.TryParseVerifyOk(null, out var r4));
            Assert.IsNull(r4);
        }

        // ---------------- TryParseVerifyDenied ----------------

        [Test]
        public void TryParseVerifyDenied_ParseaReasonYMessage()
        {
            string json = @"{""status"":""denied"",""reason"":""DEVICE_PENDING"",""message"":""pendiente de aprobacion""}";
            bool ok = LicenseLogic.TryParseVerifyDenied(json, out var result);

            Assert.IsTrue(ok);
            Assert.AreEqual("DEVICE_PENDING", result.Reason);
            Assert.AreEqual("pendiente de aprobacion", result.Message);
        }

        [Test]
        public void TryParseVerifyDenied_StatusDistinto_DevuelveFalse()
        {
            string json = @"{""status"":""ok"",""reason"":""DEVICE_PENDING""}";
            Assert.IsFalse(LicenseLogic.TryParseVerifyDenied(json, out var result));
            Assert.IsNull(result);
        }

        [Test]
        public void TryParseVerifyDenied_HtmlBasuraOVacio_NuncaTiraYDevuelveFalse()
        {
            Assert.IsFalse(LicenseLogic.TryParseVerifyDenied("<html>captive portal</html>", out var r1));
            Assert.IsNull(r1);
            Assert.IsFalse(LicenseLogic.TryParseVerifyDenied("no soy json", out var r2));
            Assert.IsNull(r2);
            Assert.IsFalse(LicenseLogic.TryParseVerifyDenied("", out var r3));
            Assert.IsNull(r3);
            Assert.IsFalse(LicenseLogic.TryParseVerifyDenied(null, out var r4));
            Assert.IsNull(r4);
        }

        // ---------------- MapDeniedReason ----------------

        [Test]
        public void MapDeniedReason_DevicePending_DevuelveBlockPending()
        {
            Assert.AreEqual(LicenseLogic.LicenseGateResult.BlockPending, LicenseLogic.MapDeniedReason("DEVICE_PENDING"));
        }

        [Test]
        public void MapDeniedReason_DeviceRejected_DevuelveBlockRejected()
        {
            Assert.AreEqual(LicenseLogic.LicenseGateResult.BlockRejected, LicenseLogic.MapDeniedReason("DEVICE_REJECTED"));
        }

        [Test]
        public void MapDeniedReason_DeviceSuspended_DevuelveBlockSuspended()
        {
            Assert.AreEqual(LicenseLogic.LicenseGateResult.BlockSuspended, LicenseLogic.MapDeniedReason("DEVICE_SUSPENDED"));
        }

        [Test]
        public void MapDeniedReason_LicenseExpired_DevuelveBlockExpired()
        {
            Assert.AreEqual(LicenseLogic.LicenseGateResult.BlockExpired, LicenseLogic.MapDeniedReason("LICENSE_EXPIRED"));
        }

        [Test]
        public void MapDeniedReason_DeviceNotFound_DevuelveBlockNotFound()
        {
            Assert.AreEqual(LicenseLogic.LicenseGateResult.BlockNotFound, LicenseLogic.MapDeniedReason("DEVICE_NOT_FOUND"));
        }

        [Test]
        public void MapDeniedReason_DesconocidoONull_DevuelveBlockUnknown()
        {
            Assert.AreEqual(LicenseLogic.LicenseGateResult.BlockUnknown, LicenseLogic.MapDeniedReason("REASON_FUTURO_INVENTADO"));
            Assert.AreEqual(LicenseLogic.LicenseGateResult.BlockUnknown, LicenseLogic.MapDeniedReason(null));
            Assert.AreEqual(LicenseLogic.LicenseGateResult.BlockUnknown, LicenseLogic.MapDeniedReason(""));
        }

        // ---------------- EvaluateOffline ----------------

        [Test]
        public void EvaluateOffline_SinCache_DevuelveBlockOffline()
        {
            var utcNow = new DateTime(2026, 7, 9, 12, 0, 0, DateTimeKind.Utc);
            Assert.AreEqual(LicenseLogic.LicenseGateResult.BlockOffline, LicenseLogic.EvaluateOffline(null, utcNow));
            Assert.AreEqual(LicenseLogic.LicenseGateResult.BlockOffline, LicenseLogic.EvaluateOffline("", utcNow));
        }

        [Test]
        public void EvaluateOffline_CacheFrescoUnDia_PermiteGraciaOffline()
        {
            var verifiedAt = new DateTime(2026, 7, 8, 12, 0, 0, DateTimeKind.Utc);
            var utcNow = verifiedAt.AddDays(1);
            string cacheJson = $@"{{""device_name"":""x"",""license_expiry"":null,""verified_at"":""{verifiedAt:o}""}}";

            Assert.AreEqual(LicenseLogic.LicenseGateResult.AllowOfflineGrace, LicenseLogic.EvaluateOffline(cacheJson, utcNow));
        }

        [Test]
        public void EvaluateOffline_BordeDiaDiez_PermiteGraciaOffline()
        {
            var verifiedAt = new DateTime(2026, 7, 1, 0, 0, 0, DateTimeKind.Utc);
            var utcNow = verifiedAt.AddDays(LicenseLogic.GraceDays);
            string cacheJson = $@"{{""device_name"":""x"",""license_expiry"":null,""verified_at"":""{verifiedAt:o}""}}";

            Assert.AreEqual(LicenseLogic.LicenseGateResult.AllowOfflineGrace, LicenseLogic.EvaluateOffline(cacheJson, utcNow));
        }

        [Test]
        public void EvaluateOffline_BordeDiaOnce_Bloquea()
        {
            var verifiedAt = new DateTime(2026, 7, 1, 0, 0, 0, DateTimeKind.Utc);
            var utcNow = verifiedAt.AddDays(LicenseLogic.GraceDays + 1);
            string cacheJson = $@"{{""device_name"":""x"",""license_expiry"":null,""verified_at"":""{verifiedAt:o}""}}";

            Assert.AreEqual(LicenseLogic.LicenseGateResult.BlockOffline, LicenseLogic.EvaluateOffline(cacheJson, utcNow));
        }

        [Test]
        public void EvaluateOffline_LicenseExpiryVencidaAunDentroDeGracia_DevuelveBlockExpired()
        {
            // verified_at es de ayer (dentro de la gracia de 10 dias), pero license_expiry
            // ya paso -- la gracia offline no revive una licencia vencida.
            var verifiedAt = new DateTime(2026, 7, 8, 12, 0, 0, DateTimeKind.Utc);
            var utcNow = verifiedAt.AddDays(1);
            string cacheJson = $@"{{""device_name"":""x"",""license_expiry"":""2026-07-01"",""verified_at"":""{verifiedAt:o}""}}";

            Assert.AreEqual(LicenseLogic.LicenseGateResult.BlockExpired, LicenseLogic.EvaluateOffline(cacheJson, utcNow));
        }

        [Test]
        public void EvaluateOffline_VerifiedAtFuturo_SeClampeaYPermiteGracia()
        {
            // Reloj del dispositivo mal seteado: verified_at "en el futuro" respecto de
            // utcNow no debe brickear la app -- se clampea a utcNow (0 dias transcurridos).
            var utcNow = new DateTime(2026, 7, 9, 12, 0, 0, DateTimeKind.Utc);
            var verifiedAt = utcNow.AddDays(5);
            string cacheJson = $@"{{""device_name"":""x"",""license_expiry"":null,""verified_at"":""{verifiedAt:o}""}}";

            Assert.AreEqual(LicenseLogic.LicenseGateResult.AllowOfflineGrace, LicenseLogic.EvaluateOffline(cacheJson, utcNow));
        }

        [Test]
        public void EvaluateOffline_JsonCorrupto_DevuelveBlockOffline()
        {
            var utcNow = new DateTime(2026, 7, 9, 12, 0, 0, DateTimeKind.Utc);
            Assert.AreEqual(LicenseLogic.LicenseGateResult.BlockOffline, LicenseLogic.EvaluateOffline("no soy json", utcNow));
            Assert.AreEqual(LicenseLogic.LicenseGateResult.BlockOffline, LicenseLogic.EvaluateOffline("<html>captive portal</html>", utcNow));
        }

        [Test]
        public void EvaluateOffline_VerifiedAtCorrupto_DevuelveBlockOffline()
        {
            var utcNow = new DateTime(2026, 7, 9, 12, 0, 0, DateTimeKind.Utc);
            string cacheJson = @"{""device_name"":""x"",""license_expiry"":null,""verified_at"":""no-es-una-fecha""}";

            Assert.AreEqual(LicenseLogic.LicenseGateResult.BlockOffline, LicenseLogic.EvaluateOffline(cacheJson, utcNow));
        }

        // ---------------- BuildCacheJson + EvaluateOffline (roundtrip) ----------------

        [Test]
        public void BuildCacheJson_RoundtripConEvaluateOffline_PermiteGraciaElMismoInstante()
        {
            var okResponse = new LicenseLogic.VerifyOkDto
            {
                Status = "ok",
                DeviceName = "Visor Consultorio 1",
                LicenseExpiry = null,
                Message = "todo en orden",
            };
            var verifiedAt = new DateTime(2026, 7, 9, 12, 0, 0, DateTimeKind.Utc);

            string cacheJson = LicenseLogic.BuildCacheJson(okResponse, verifiedAt);

            Assert.AreEqual(LicenseLogic.LicenseGateResult.AllowOfflineGrace,
                LicenseLogic.EvaluateOffline(cacheJson, verifiedAt));
            Assert.AreEqual(LicenseLogic.LicenseGateResult.AllowOfflineGrace,
                LicenseLogic.EvaluateOffline(cacheJson, verifiedAt.AddDays(LicenseLogic.GraceDays)));
            Assert.AreEqual(LicenseLogic.LicenseGateResult.BlockOffline,
                LicenseLogic.EvaluateOffline(cacheJson, verifiedAt.AddDays(LicenseLogic.GraceDays + 1)));
        }

        [Test]
        public void BuildCacheJson_ConExpiryVencida_RoundtripDaBlockExpired()
        {
            var okResponse = new LicenseLogic.VerifyOkDto
            {
                Status = "ok",
                DeviceName = "Tablet Sala 2",
                LicenseExpiry = "2026-07-01",
                Message = "ok",
            };
            var verifiedAt = new DateTime(2026, 7, 8, 12, 0, 0, DateTimeKind.Utc);
            string cacheJson = LicenseLogic.BuildCacheJson(okResponse, verifiedAt);

            Assert.AreEqual(LicenseLogic.LicenseGateResult.BlockExpired,
                LicenseLogic.EvaluateOffline(cacheJson, verifiedAt.AddDays(1)));
        }

        [Test]
        public void BuildCacheJson_OkNull_NuncaTiraExcepcion()
        {
            string json = LicenseLogic.BuildCacheJson(null, DateTime.UtcNow);
            Assert.IsNotNull(json);
            var obj = JObject.Parse(json);
            Assert.IsNull((string)obj["device_name"]);
        }
    }
}
