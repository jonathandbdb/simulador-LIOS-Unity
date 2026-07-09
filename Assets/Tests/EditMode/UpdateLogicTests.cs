using Newtonsoft.Json.Linq;
using NUnit.Framework;
using Simulador.Update;

namespace Simulador.Tests
{
    /// <summary>
    /// Tests de la logica PURA de updates (UpdateLogic): parseo/comparacion de semver,
    /// parseo del manifest del backend y la decision de actualizar. Mismo estilo que
    /// DataManagerLogicTests.cs (P6.5).
    /// </summary>
    public class UpdateLogicTests
    {
        // ---------------- TryParseSemver ----------------

        [Test]
        public void TryParseSemver_TresComponentes_ParseaLosTres()
        {
            bool ok = UpdateLogic.TryParseSemver("1.2.3", out var v);
            Assert.IsTrue(ok);
            Assert.AreEqual((1, 2, 3), v);
        }

        [Test]
        public void TryParseSemver_DosComponentes_PatchQuedaEnCero()
        {
            bool ok = UpdateLogic.TryParseSemver("1.2", out var v);
            Assert.IsTrue(ok);
            Assert.AreEqual((1, 2, 0), v);
        }

        [Test]
        public void TryParseSemver_UnComponente_MinorYPatchQuedanEnCero()
        {
            bool ok = UpdateLogic.TryParseSemver("1", out var v);
            Assert.IsTrue(ok);
            Assert.AreEqual((1, 0, 0), v);
        }

        [Test]
        public void TryParseSemver_InvalidoOAmbiguo_DevuelveFalse()
        {
            // Vacio, null, con letras y con un cuarto componente (fuera del contrato
            // major.minor.patch del backend, ver docs/updates.md) se consideran invalidos.
            Assert.IsFalse(UpdateLogic.TryParseSemver("", out _));
            Assert.IsFalse(UpdateLogic.TryParseSemver(null, out _));
            Assert.IsFalse(UpdateLogic.TryParseSemver("abc", out _));
            Assert.IsFalse(UpdateLogic.TryParseSemver("1.2.3.4", out _));
        }

        // ---------------- CompareVersions ----------------

        [Test]
        public void CompareVersions_Iguales_DevuelveCero()
        {
            Assert.AreEqual(0, UpdateLogic.CompareVersions("0.1.0", "0.1.0"));
        }

        [Test]
        public void CompareVersions_MayorPorMajor_DevuelvePositivo()
        {
            Assert.Greater(UpdateLogic.CompareVersions("1.0.0", "0.9.9"), 0);
        }

        [Test]
        public void CompareVersions_MayorPorMinor_DevuelvePositivo()
        {
            Assert.Greater(UpdateLogic.CompareVersions("1.2.0", "1.1.9"), 0);
        }

        [Test]
        public void CompareVersions_MayorPorPatch_DevuelvePositivo()
        {
            Assert.Greater(UpdateLogic.CompareVersions("1.1.2", "1.1.1"), 0);
        }

        [Test]
        public void CompareVersions_Menor_DevuelveNegativo()
        {
            Assert.Less(UpdateLogic.CompareVersions("0.1.0", "0.2.0"), 0);
        }

        // ---------------- AppChannelFromIdentifier ----------------

        [Test]
        public void AppChannelFromIdentifier_Visor_DevuelveVisor()
        {
            Assert.AreEqual("visor", UpdateLogic.AppChannelFromIdentifier("com.simulador.vr"));
        }

        [Test]
        public void AppChannelFromIdentifier_Tablet_DevuelveTablet()
        {
            Assert.AreEqual("tablet", UpdateLogic.AppChannelFromIdentifier("com.simulador.tablet"));
        }

        // ---------------- Sha256Matches ----------------

        [Test]
        public void Sha256Matches_MismoHashDistintoCase_DevuelveTrue()
        {
            Assert.IsTrue(UpdateLogic.Sha256Matches("ABCDEF01", "abcdef01"));
        }

        [Test]
        public void Sha256Matches_ExpectedVacioONull_DevuelveTrue()
        {
            // El dummy del backend manda apk_sha256: "" -- nada que verificar.
            Assert.IsTrue(UpdateLogic.Sha256Matches("", "cualquiercosa"));
            Assert.IsTrue(UpdateLogic.Sha256Matches(null, "cualquiercosa"));
            Assert.IsTrue(UpdateLogic.Sha256Matches("   ", "cualquiercosa"));
        }

        [Test]
        public void Sha256Matches_HashesDistintos_DevuelveFalse()
        {
            Assert.IsFalse(UpdateLogic.Sha256Matches("abcdef01", "12345678"));
        }

        // ---------------- TryParseManifest ----------------

        [Test]
        public void TryParseManifest_JsonRealDelBackend_MapeaTodosLosCampos()
        {
            string json = @"{""app"":""visor"",""apk_version"":""0.1.0"",""min_apk_version"":""0.1.0"",""apk_url"":""https://vr.conecta.sh/apks/visor-0.1.0.apk"",""apk_sha256"":"""",""changelog"":""primera version""}";
            bool ok = UpdateLogic.TryParseManifest(json, out var manifest);

            Assert.IsTrue(ok);
            Assert.AreEqual("visor", manifest.App);
            Assert.AreEqual("0.1.0", manifest.ApkVersion);
            Assert.AreEqual("0.1.0", manifest.MinApkVersion);
            Assert.AreEqual("https://vr.conecta.sh/apks/visor-0.1.0.apk", manifest.ApkUrl);
            Assert.AreEqual("", manifest.ApkSha256);
            Assert.AreEqual("primera version", manifest.Changelog);
        }

        [Test]
        public void TryParseManifest_JsonInvalido_DevuelveFalse()
        {
            Assert.IsFalse(UpdateLogic.TryParseManifest("no soy json", out var manifest));
            Assert.IsNull(manifest);
        }

        [Test]
        public void TryParseManifest_VacioONull_DevuelveFalse()
        {
            Assert.IsFalse(UpdateLogic.TryParseManifest("", out _));
            Assert.IsFalse(UpdateLogic.TryParseManifest(null, out _));
        }

        [Test]
        public void TryParseManifest_ClavesFaltantes_ParseaConCamposEnNull()
        {
            // JSON valido pero incompleto: el parseo en si NO falla (es Decide quien
            // trata una version remota null/no-parseable como "no hay update").
            bool ok = UpdateLogic.TryParseManifest(@"{""app"":""visor""}", out var manifest);

            Assert.IsTrue(ok);
            Assert.AreEqual("visor", manifest.App);
            Assert.IsNull(manifest.ApkVersion);
        }

        // ---------------- Decide ----------------

        [Test]
        public void Decide_InstaladaIgualARemota_DevuelveNone()
        {
            var manifest = new UpdateLogic.UpdateManifest { ApkVersion = "0.1.0", MinApkVersion = "0.1.0" };
            Assert.AreEqual(UpdateLogic.UpdateDecision.None, UpdateLogic.Decide("0.1.0", manifest));
        }

        [Test]
        public void Decide_RemotaMayorSinMinimoSuperado_DevuelveOptional()
        {
            var manifest = new UpdateLogic.UpdateManifest { ApkVersion = "0.2.0", MinApkVersion = "0.1.0" };
            Assert.AreEqual(UpdateLogic.UpdateDecision.Optional, UpdateLogic.Decide("0.1.5", manifest));
        }

        [Test]
        public void Decide_InstaladaPorDebajoDelMinimo_DevuelveForced()
        {
            var manifest = new UpdateLogic.UpdateManifest { ApkVersion = "0.3.0", MinApkVersion = "0.2.0" };
            Assert.AreEqual(UpdateLogic.UpdateDecision.Forced, UpdateLogic.Decide("0.1.0", manifest));
        }

        [Test]
        public void Decide_ApkVersionInvalido_DevuelveNone()
        {
            var manifest = new UpdateLogic.UpdateManifest { ApkVersion = "no-semver", MinApkVersion = "0.1.0" };
            Assert.AreEqual(UpdateLogic.UpdateDecision.None, UpdateLogic.Decide("0.1.0", manifest));
        }

        [Test]
        public void Decide_Downgrade_RemotaMenorQueInstalada_DevuelveNone()
        {
            var manifest = new UpdateLogic.UpdateManifest { ApkVersion = "0.1.0", MinApkVersion = "0.1.0" };
            Assert.AreEqual(UpdateLogic.UpdateDecision.None, UpdateLogic.Decide("0.2.0", manifest));
        }

        [Test]
        public void Decide_ManifestNull_DevuelveNone()
        {
            Assert.AreEqual(UpdateLogic.UpdateDecision.None, UpdateLogic.Decide("0.1.0", null));
        }

        [Test]
        public void Decide_MinApkVersionInvalido_NoFuerzaSoloOptional()
        {
            // MinApkVersion ausente/invalido no debe forzar la actualizacion por un dato
            // faltante -- se trata como "sin minimo exigido".
            var manifest = new UpdateLogic.UpdateManifest { ApkVersion = "0.2.0", MinApkVersion = null };
            Assert.AreEqual(UpdateLogic.UpdateDecision.Optional, UpdateLogic.Decide("0.1.0", manifest));
        }

        // ---------------- Marcador de install pendiente (F4) ----------------

        [Test]
        public void PendingMarker_SerializaYParseaRedondo()
        {
            string json = UpdateLogic.SerializePendingMarker("0.2.0");
            bool ok = UpdateLogic.TryParsePendingMarker(json, out var marker);

            Assert.IsTrue(ok);
            Assert.AreEqual("0.2.0", marker.TargetVersion);
        }

        [Test]
        public void TryParsePendingMarker_JsonInvalido_DevuelveFalse()
        {
            Assert.IsFalse(UpdateLogic.TryParsePendingMarker("no soy json", out var marker));
            Assert.IsNull(marker);
        }

        [Test]
        public void TryParsePendingMarker_VacioONull_DevuelveFalse()
        {
            Assert.IsFalse(UpdateLogic.TryParsePendingMarker("", out _));
            Assert.IsFalse(UpdateLogic.TryParsePendingMarker(null, out _));
        }

        // ---------------- SerializeLogBatch (F6, telemetria) ----------------

        [Test]
        public void SerializeLogBatch_UnEvento_MapeaDeviceIdYCamposDelEvento()
        {
            var events = new[] { new UpdateLogic.LogEvent("update_check", "app=visor installed=0.1.0 remote=0.2.0 decision=Optional") };
            string json = UpdateLogic.SerializeLogBatch("abc-123", events);

            var obj = JObject.Parse(json);
            Assert.AreEqual("abc-123", (string)obj["device_id"]);
            var arr = (JArray)obj["events"];
            Assert.AreEqual(1, arr.Count);
            Assert.AreEqual("update_check", (string)arr[0]["event"]);
            Assert.AreEqual("app=visor installed=0.1.0 remote=0.2.0 decision=Optional", (string)arr[0]["detail"]);
        }

        [Test]
        public void SerializeLogBatch_MultiplesEventos_PreservaElOrden()
        {
            var events = new[]
            {
                new UpdateLogic.LogEvent("update_check", "d1"),
                new UpdateLogic.LogEvent("update_prompt_shown", "d2"),
            };
            string json = UpdateLogic.SerializeLogBatch("device-1", events);

            var arr = (JArray)JObject.Parse(json)["events"];
            Assert.AreEqual(2, arr.Count);
            Assert.AreEqual("update_check", (string)arr[0]["event"]);
            Assert.AreEqual("update_prompt_shown", (string)arr[1]["event"]);
        }

        [Test]
        public void SerializeLogBatch_DeviceIdOEventsNull_NoTiraExcepcionYQuedaVacio()
        {
            string json = UpdateLogic.SerializeLogBatch(null, null);
            var obj = JObject.Parse(json);

            Assert.AreEqual("", (string)obj["device_id"]);
            Assert.AreEqual(0, ((JArray)obj["events"]).Count);
        }
    }
}
