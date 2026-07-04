using System.Collections.Generic;
using NUnit.Framework;
using Simulador.Net;

namespace Simulador.Tests
{
    /// <summary>
    /// Tests de la logica PURA del emparejamiento persistente por token (opcion B,
    /// ver docs/networking.md): generacion de token y round-trip de serializacion de
    /// las dos formas que persiste el protocolo (lista de tokens del visor, mapa
    /// host-token de la tablet). Mismo patron que DataManagerLogicTests
    /// (ver Assets/Tests/EditMode/DataLogicTests.cs / docs/catalogo-lentes.md): JSON
    /// invalido/vacio no debe tirar excepcion, debe degradar a "sin nada guardado".
    /// </summary>
    public class PairingStoreTests
    {
        [Test]
        public void GenerateToken_IsLongAndUnique()
        {
            string a = PairingStore.GenerateToken();
            string b = PairingStore.GenerateToken();

            // 2x Guid en hex ("N") = 32+32 caracteres.
            Assert.AreEqual(64, a.Length);
            Assert.AreNotEqual(a, b, "cada token generado debe ser distinto");
        }

        [Test]
        public void Tokens_RoundTrip_SerializeThenParse()
        {
            var original = new List<string> { "token-uno", PairingStore.GenerateToken() };

            string json = PairingStore.SerializeTokens(original);
            bool ok = PairingStore.TryParseTokens(json, out var parsed);

            Assert.IsTrue(ok);
            CollectionAssert.AreEqual(original, parsed);
        }

        [Test]
        public void TryParseTokens_InvalidOrEmptyJson_ReturnsFalse()
        {
            Assert.IsFalse(PairingStore.TryParseTokens("", out var r1));
            Assert.IsNull(r1);
            Assert.IsFalse(PairingStore.TryParseTokens("no soy json", out var r2));
            Assert.IsNull(r2);
            Assert.IsFalse(PairingStore.TryParseTokens("null", out var r3));
            Assert.IsNull(r3);
        }

        [Test]
        public void PairingMap_RoundTrip_SerializeThenParse()
        {
            var original = new Dictionary<string, string>
            {
                ["192.168.1.10"] = PairingStore.GenerateToken(),
                ["192.168.1.20"] = "otro-token",
            };

            string json = PairingStore.SerializePairingMap(original);
            bool ok = PairingStore.TryParsePairingMap(json, out var parsed);

            Assert.IsTrue(ok);
            Assert.AreEqual(original.Count, parsed.Count);
            foreach (var kv in original) Assert.AreEqual(kv.Value, parsed[kv.Key]);
        }

        [Test]
        public void TryParsePairingMap_InvalidOrEmptyJson_ReturnsFalse()
        {
            Assert.IsFalse(PairingStore.TryParsePairingMap("", out var r1));
            Assert.IsNull(r1);
            Assert.IsFalse(PairingStore.TryParsePairingMap("{invalido", out var r2));
            Assert.IsNull(r2);
        }
    }
}
