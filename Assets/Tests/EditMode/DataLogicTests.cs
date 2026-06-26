using System.Collections.Generic;
using System.IO;
using NUnit.Framework;
using Simulador.Data;
using UnityEngine;

namespace Simulador.Tests
{
    /// <summary>
    /// Tests de la logica PURA de la capa de datos (parser, merge, motor de lentes,
    /// limpieza de overrides). No tocan IO de Unity, salvo un test de integracion que
    /// lee el lentes.json real de StreamingAssets.
    /// </summary>
    public class DataLogicTests
    {
        // Catalogo minimo donde 'panoptix' NO trae destello_* (para probar el merge).
        private const string PartialJson = @"{
          ""version"": ""test-1"",
          ""catalogo"": [
            { ""id"": ""monofocal"", ""nombre"": ""M"", ""params"": {
                ""foco_lejos_m"": { ""default"": 6.0, ""min"": 0.0, ""max"": 20.0 },
                ""contrast_loss"": { ""default"": 0.0, ""min"": 0.0, ""max"": 0.6 } } },
            { ""id"": ""panoptix"", ""nombre"": ""P"", ""params"": {
                ""foco_lejos_m"": { ""default"": 6.0, ""min"": 0.0, ""max"": 20.0 },
                ""contrast_loss"": { ""default"": 0.2, ""min"": 0.0, ""max"": 0.6 } } }
          ]
        }";

        private static LensCatalog MakeDefaults()
        {
            // Defaults que SI traen destello_intensity en panoptix.
            var p = new LensDef { Id = "panoptix", Nombre = "P" };
            p.Params["foco_lejos_m"] = new ParamSpec { Default = 6.0f, Min = 0f, Max = 20f };
            p.Params["contrast_loss"] = new ParamSpec { Default = 0.2f, Min = 0f, Max = 0.6f };
            p.Params["destello_intensity"] = new ParamSpec { Default = 0.5f, Min = 0f, Max = 1f };
            var cat = new LensCatalog { Version = "def", Catalogo = new List<LensDef>() };
            cat.Catalogo.Add(p);
            return cat;
        }

        [Test]
        public void Parse_ValidCatalog_ReturnsLenses()
        {
            var cat = CatalogParser.Parse(PartialJson);
            Assert.IsNotNull(cat);
            Assert.AreEqual("test-1", cat.Version);
            Assert.AreEqual(2, cat.Catalogo.Count);
            Assert.AreEqual("monofocal", cat.Catalogo[0].Id);
        }

        [Test]
        public void Parse_Invalid_ReturnsNull()
        {
            Assert.IsNull(CatalogParser.Parse(""));
            Assert.IsNull(CatalogParser.Parse("no soy json"));
            Assert.IsNull(CatalogParser.Parse("{\"version\":\"x\"}")); // sin 'catalogo'
        }

        [Test]
        public void MergeMissingParams_FillsMissing_DoesNotOverwrite()
        {
            var cat = CatalogParser.Parse(PartialJson);
            var defaults = MakeDefaults();
            var panoptix = cat.Catalogo.Find(l => l.Id == "panoptix");
            Assert.IsFalse(panoptix.Params.ContainsKey("destello_intensity"), "precondicion");

            int added = CatalogParser.MergeMissingParams(cat, defaults);

            Assert.AreEqual(1, added);
            Assert.IsTrue(panoptix.Params.ContainsKey("destello_intensity"));
            Assert.AreEqual(0.5f, panoptix.Params["destello_intensity"].Default, 1e-4f);
            // No se pisa lo que ya existia.
            Assert.AreEqual(0.2f, panoptix.Params["contrast_loss"].Default, 1e-4f);
        }

        [Test]
        public void BuildEyeState_AppliesDefaults_AndSetsLensId()
        {
            var cat = CatalogParser.Parse(PartialJson);
            var lens = cat.Catalogo.Find(l => l.Id == "panoptix");
            var state = LensEngine.BuildEyeState(lens, null);

            Assert.AreEqual("panoptix", state.LensId);
            Assert.AreEqual(6.0f, state.Params["foco_lejos_m"], 1e-4f);
            Assert.AreEqual(0.2f, state.Params["contrast_loss"], 1e-4f);
        }

        [Test]
        public void BuildEyeState_OverridesApplyOnTopOfDefaults()
        {
            var cat = CatalogParser.Parse(PartialJson);
            var lens = cat.Catalogo.Find(l => l.Id == "panoptix");
            var overrides = new Dictionary<string, float> { { "contrast_loss", 0.45f } };
            var state = LensEngine.BuildEyeState(lens, overrides);

            Assert.AreEqual(0.45f, state.Params["contrast_loss"], 1e-4f);
            Assert.AreEqual(6.0f, state.Params["foco_lejos_m"], 1e-4f); // intacto
        }

        [Test]
        public void ComputeBlend_TrueOnlyWhenBothSetAndDifferent()
        {
            Assert.IsTrue(LensEngine.ComputeBlend("monofocal", "panoptix"));
            Assert.IsFalse(LensEngine.ComputeBlend("panoptix", "panoptix"));
            Assert.IsFalse(LensEngine.ComputeBlend("", "panoptix"));
            Assert.IsFalse(LensEngine.ComputeBlend("monofocal", ""));
        }

        [Test]
        public void CleanOverrides_RemovesValueBackToDefault_KeepsDifferent_IgnoresLensId()
        {
            var catParams = new Dictionary<string, ParamSpec>
            {
                { "contrast_loss", new ParamSpec { Default = 0.2f } },
                { "halo_intensity", new ParamSpec { Default = 0.6f } }
            };
            var saved = new Dictionary<string, float>();

            // Valor distinto al default -> se guarda.
            LensEngine.CleanOverrides(saved,
                new Dictionary<string, float> { { "contrast_loss", 0.45f }, { "lens_id", 0f } },
                catParams);
            Assert.IsTrue(saved.ContainsKey("contrast_loss"));
            Assert.IsFalse(saved.ContainsKey("lens_id"), "lens_id no es un override");

            // Vuelve al default (dentro de epsilon) -> se elimina.
            LensEngine.CleanOverrides(saved,
                new Dictionary<string, float> { { "contrast_loss", 0.2f } },
                catParams);
            Assert.IsFalse(saved.ContainsKey("contrast_loss"));
        }

        [Test]
        public void StreamingAssets_RealCatalog_ParsesWithExpectedClinicalValues()
        {
            // Test de integracion: valida el lentes.json REAL que se embebe en el build.
            string path = Path.Combine(Application.streamingAssetsPath, "lentes.json");
            Assert.IsTrue(File.Exists(path), $"Falta {path}");
            var cat = CatalogParser.Parse(File.ReadAllText(path));
            Assert.IsNotNull(cat);
            Assert.AreEqual("0.3.0-clinical", cat.Version);
            Assert.AreEqual(3, cat.Catalogo.Count);

            var pan = cat.Catalogo.Find(l => l.Id == "panoptix");
            Assert.IsNotNull(pan);
            Assert.AreEqual(0.6f, pan.Params["halo_intensity"].Default, 1e-4f);
            Assert.AreEqual(9.0f, pan.Params["destello_rayos"].Default, 1e-4f);
            // Las 3 lentes deben tener los 10 params clinicos.
            foreach (var l in cat.Catalogo)
                Assert.AreEqual(10, l.Params.Count, $"{l.Id} deberia tener 10 params");
        }
    }
}
