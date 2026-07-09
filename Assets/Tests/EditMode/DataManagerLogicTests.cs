using System.Collections.Generic;
using NUnit.Framework;
using Simulador.Data;

namespace Simulador.Tests
{
    /// <summary>
    /// Tests de la logica PURA extraida de DataManager (P6.5): armado de la URL de
    /// sync (DataManagerLogic.BuildSyncUrl) y round-trip de lens_overrides.json
    /// (SerializeLensOverrides / TryParseLensOverrides). DataManager EN SI (el
    /// MonoBehaviour orquestador: cadena defaults->cache->backend por corrutinas +
    /// UnityWebRequest, debounce de guardado, eventos) queda FUERA de esta suite --
    /// exige interfaz de IO + mocks pesados para testear sin Play Mode real; ver
    /// "Limite de cobertura" en docs/catalogo-lentes.md. Se sigue validando por play
    /// mode (logs de DataManager en consola, ver docs/catalogo-lentes.md §Cómo probar).
    /// </summary>
    public class DataManagerLogicTests
    {
        // ---------------- BuildSyncUrl ----------------

        [Test]
        public void BuildSyncUrl_TrailingSlashOnBackendUrl_AvoidsDoubleSlash()
        {
            // Config.json tipeado a mano con "/" al final (P2.4): no debe producir "//".
            string url = DataManagerLogic.BuildSyncUrl("http://192.168.1.10:8080/", "/api/lenses");
            Assert.AreEqual("http://192.168.1.10:8080/api/lenses", url);
        }

        [Test]
        public void BuildSyncUrl_NoTrailingSlash_ConcatenatesCleanly()
        {
            string url = DataManagerLogic.BuildSyncUrl("http://192.168.1.10:8080", "/api/lenses");
            Assert.AreEqual("http://192.168.1.10:8080/api/lenses", url);
        }

        [Test]
        public void BuildSyncUrl_EndpointMissingLeadingSlash_GetsOneAdded()
        {
            string url = DataManagerLogic.BuildSyncUrl("http://host:8080", "api/lenses");
            Assert.AreEqual("http://host:8080/api/lenses", url);
        }

        // ---------------- TryParseLensOverrides ----------------

        [Test]
        public void TryParseLensOverrides_ValidJson_ReturnsExpectedValues()
        {
            string json = @"{""panoptix"":{""contrast_loss"":0.45},""monofocal"":{""halo_intensity"":0.1}}";
            bool ok = DataManagerLogic.TryParseLensOverrides(json, out var result);

            Assert.IsTrue(ok);
            Assert.AreEqual(2, result.Count);
            Assert.AreEqual(0.45f, result["panoptix"]["contrast_loss"], 1e-4f);
            Assert.AreEqual(0.1f, result["monofocal"]["halo_intensity"], 1e-4f);
        }

        [Test]
        public void TryParseLensOverrides_InvalidInput_ReturnsFalseAndNullOut()
        {
            // Mismos casos que antes ignoraba en silencio el try/catch inline de
            // DataManager.LoadLensOverrides: JSON invalido, vacio, null y "null" (json
            // valido que deserializa a null).
            Assert.IsFalse(DataManagerLogic.TryParseLensOverrides("no soy json", out var r1));
            Assert.IsNull(r1);
            Assert.IsFalse(DataManagerLogic.TryParseLensOverrides("", out var r2));
            Assert.IsNull(r2);
            Assert.IsFalse(DataManagerLogic.TryParseLensOverrides(null, out var r3));
            Assert.IsNull(r3);
            Assert.IsFalse(DataManagerLogic.TryParseLensOverrides("null", out var r4));
            Assert.IsNull(r4);
        }

        // ---------------- Round-trip (misma serializacion/parseo que usa DataManager) ----------------

        [Test]
        public void LensOverrides_RoundTrip_SerializeThenParse_PreservesAllLensesAndParams()
        {
            var original = new Dictionary<string, Dictionary<string, float>>
            {
                ["panoptix"] = new Dictionary<string, float> { ["contrast_loss"] = 0.45f, ["halo_intensity"] = 0.7f },
                ["vivity"] = new Dictionary<string, float> { ["foco_intermedio_m"] = 0.55f },
            };

            string json = DataManagerLogic.SerializeLensOverrides(original);
            bool ok = DataManagerLogic.TryParseLensOverrides(json, out var roundTripped);

            Assert.IsTrue(ok);
            Assert.AreEqual(2, roundTripped.Count);
            Assert.AreEqual(0.45f, roundTripped["panoptix"]["contrast_loss"], 1e-4f);
            Assert.AreEqual(0.7f, roundTripped["panoptix"]["halo_intensity"], 1e-4f);
            Assert.AreEqual(0.55f, roundTripped["vivity"]["foco_intermedio_m"], 1e-4f);
        }

        // ---------------- ResolveBackendUrl (config por capas: override > streaming > default) ----------------

        [Test]
        public void ResolveBackendUrl_SoloStreaming_GanaStreaming()
        {
            string url = DataManagerLogic.ResolveBackendUrl(
                "https://vr.conecta.sh",
                @"{""backend_url"":""http://192.168.1.10:8080""}",
                null,
                out string source);

            Assert.AreEqual("http://192.168.1.10:8080", url);
            Assert.AreEqual("streaming", source);
        }

        [Test]
        public void ResolveBackendUrl_StreamingYOverride_GanaOverride()
        {
            string url = DataManagerLogic.ResolveBackendUrl(
                "https://vr.conecta.sh",
                @"{""backend_url"":""http://192.168.1.10:8080""}",
                @"{""backend_url"":""http://10.0.0.5:9000""}",
                out string source);

            Assert.AreEqual("http://10.0.0.5:9000", url);
            Assert.AreEqual("override", source);
        }

        [Test]
        public void ResolveBackendUrl_OverrideCorrupto_IgnoraYSigueConStreaming()
        {
            string url = DataManagerLogic.ResolveBackendUrl(
                "https://vr.conecta.sh",
                @"{""backend_url"":""http://192.168.1.10:8080""}",
                "no soy json",
                out string source);

            Assert.AreEqual("http://192.168.1.10:8080", url);
            Assert.AreEqual("streaming", source);
        }

        [Test]
        public void ResolveBackendUrl_AmbosVacios_UsaDefault()
        {
            string url = DataManagerLogic.ResolveBackendUrl(
                "https://vr.conecta.sh",
                null,
                "",
                out string source);

            Assert.AreEqual("https://vr.conecta.sh", url);
            Assert.AreEqual("default", source);
        }

        [Test]
        public void ExtractBackendUrl_JsonInvalidoOSinClave_DevuelveNull()
        {
            Assert.IsNull(DataManagerLogic.ExtractBackendUrl(null));
            Assert.IsNull(DataManagerLogic.ExtractBackendUrl(""));
            Assert.IsNull(DataManagerLogic.ExtractBackendUrl("no soy json"));
            Assert.IsNull(DataManagerLogic.ExtractBackendUrl(@"{""otra_clave"":""x""}"));
            Assert.IsNull(DataManagerLogic.ExtractBackendUrl(@"{""backend_url"":""   ""}"));
        }
    }
}
