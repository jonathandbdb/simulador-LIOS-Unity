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
        public void BuildEyeState_OverrideAboveMax_ClampsToMax()
        {
            var cat = CatalogParser.Parse(PartialJson);
            var lens = cat.Catalogo.Find(l => l.Id == "panoptix"); // contrast_loss: min 0, max 0.6
            var overrides = new Dictionary<string, float> { { "contrast_loss", 999f } };
            var state = LensEngine.BuildEyeState(lens, overrides);

            Assert.AreEqual(0.6f, state.Params["contrast_loss"], 1e-4f);
        }

        [Test]
        public void BuildEyeState_OverrideBelowMin_ClampsToMin()
        {
            var cat = CatalogParser.Parse(PartialJson);
            var lens = cat.Catalogo.Find(l => l.Id == "panoptix"); // foco_lejos_m: min 0, max 20
            var overrides = new Dictionary<string, float> { { "foco_lejos_m", -50f } };
            var state = LensEngine.BuildEyeState(lens, overrides);

            Assert.AreEqual(0.0f, state.Params["foco_lejos_m"], 1e-4f);
        }

        [Test]
        public void BuildEyeState_OverrideInRange_PassesIntact()
        {
            var cat = CatalogParser.Parse(PartialJson);
            var lens = cat.Catalogo.Find(l => l.Id == "panoptix"); // contrast_loss: min 0, max 0.6
            // Incluye los bordes del rango (inclusive) ademas de un valor intermedio.
            var overrides = new Dictionary<string, float> { { "contrast_loss", 0.45f } };
            var state = LensEngine.BuildEyeState(lens, overrides);
            Assert.AreEqual(0.45f, state.Params["contrast_loss"], 1e-4f);

            Assert.AreEqual(0f, LensEngine.ClampToSpec("contrast_loss", 0f, lens.Params), 1e-4f);
            Assert.AreEqual(0.6f, LensEngine.ClampToSpec("contrast_loss", 0.6f, lens.Params), 1e-4f);
        }

        [Test]
        public void ClampToSpec_UnknownParam_PassesThroughUnclamped()
        {
            var cat = CatalogParser.Parse(PartialJson);
            var lens = cat.Catalogo.Find(l => l.Id == "panoptix");

            // Clave sin spec conocido (param nuevo del backend, o typo): no explota, no clampea.
            float result = LensEngine.ClampToSpec("param_desconocido", 12345f, lens.Params);
            Assert.AreEqual(12345f, result, 1e-4f);

            // BuildEyeState tambien debe dejarlo pasar intacto dentro del override.
            var overrides = new Dictionary<string, float> { { "param_desconocido", -7f } };
            var state = LensEngine.BuildEyeState(lens, overrides);
            Assert.AreEqual(-7f, state.Params["param_desconocido"], 1e-4f);
        }

        [Test]
        public void ClampToSpec_MissingMinMax_PassesThroughUnclamped()
        {
            // Simula un ParamSpec sin 'min'/'max' en el JSON: al ser float no-nullable,
            // Newtonsoft los deserializa en 0f,0f (max <= min) -> no hay rango valido, no clampea.
            var specs = new Dictionary<string, ParamSpec>
            {
                { "sin_rango", new ParamSpec { Default = 5f } }
            };
            Assert.AreEqual(999f, LensEngine.ClampToSpec("sin_rango", 999f, specs), 1e-4f);
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
            Assert.AreEqual("0.5.1-clinical", cat.Version);
            Assert.AreEqual(3, cat.Catalogo.Count);

            var pan = cat.Catalogo.Find(l => l.Id == "panoptix");
            Assert.IsNotNull(pan);
            Assert.AreEqual(0.6f, pan.Params["halo_intensity"].Default, 1e-4f);
            Assert.AreEqual(9.0f, pan.Params["destello_rayos"].Default, 1e-4f);

            // P6.9: ventana clinica de los 3 focos (antes 0-20 sin discriminar). El
            // rango de cada foco ahora acota la ventana real donde ese foco tiene sentido
            // clinico; ver docs/catalogo-lentes.md "Rangos clinicos de los focos (P6.9)".
            foreach (var l in cat.Catalogo)
            {
                Assert.AreEqual(3.0f, l.Params["foco_lejos_m"].Min, 1e-4f, $"{l.Id}.foco_lejos_m min");
                Assert.AreEqual(9.0f, l.Params["foco_lejos_m"].Max, 1e-4f, $"{l.Id}.foco_lejos_m max");
                Assert.AreEqual(1.0f, l.Params["foco_intermedio_m"].Min, 1e-4f, $"{l.Id}.foco_intermedio_m min");
                Assert.AreEqual(3.0f, l.Params["foco_intermedio_m"].Max, 1e-4f, $"{l.Id}.foco_intermedio_m max");
                Assert.AreEqual(0.15f, l.Params["foco_cerca_m"].Min, 1e-4f, $"{l.Id}.foco_cerca_m min");
                Assert.AreEqual(1.0f, l.Params["foco_cerca_m"].Max, 1e-4f, $"{l.Id}.foco_cerca_m max");
            }
            // Defaults "off" (0) fuera del nuevo rango se conservan intactos (semantica
            // 0 = foco desactivado, ver ParamMeta.FormatValue) -- BuildEyeState nunca
            // clampea los defaults del catalogo, solo los overrides (LensEngine.cs).
            Assert.AreEqual(0f, cat.Catalogo.Find(l => l.Id == "monofocal").Params["foco_intermedio_m"].Default, 1e-4f);
            Assert.AreEqual(0f, cat.Catalogo.Find(l => l.Id == "monofocal").Params["foco_cerca_m"].Default, 1e-4f);
            Assert.AreEqual(0f, cat.Catalogo.Find(l => l.Id == "vivity").Params["foco_cerca_m"].Default, 1e-4f);
            // Defaults activos que quedaban por debajo del nuevo minimo (0.6/0.67 < 1.0)
            // se llevaron al borde mas cercano -- ver "Riesgos" en el envelope de la tarea:
            // el texto descriptivo de panoptix/vivity sigue mencionando 60cm/67cm.
            Assert.AreEqual(1.0f, pan.Params["foco_intermedio_m"].Default, 1e-4f);
            Assert.AreEqual(1.0f, cat.Catalogo.Find(l => l.Id == "vivity").Params["foco_intermedio_m"].Default, 1e-4f);
            // Las 3 lentes deben tener los 13 params clinicos (P4.4 agrego astig_magnitude
            // y astig_axis_deg a los 11 anteriores, incluyendo straylight).
            foreach (var l in cat.Catalogo)
                Assert.AreEqual(13, l.Params.Count, $"{l.Id} deberia tener 13 params");

            var mono = cat.Catalogo.Find(l => l.Id == "monofocal");
            var viv = cat.Catalogo.Find(l => l.Id == "vivity");
            Assert.AreEqual(0.15f, mono.Params["straylight"].Default, 1e-4f);
            Assert.AreEqual(1.0f, pan.Params["straylight"].Default, 1e-4f);
            Assert.AreEqual(0.45f, viv.Params["straylight"].Default, 1e-4f);

            // P4.4: astig_magnitude (0..1) y astig_axis_deg (0..180), default 0 en las 3
            // lentes (el astigmatismo residual es cero salvo que se override desde la tablet).
            foreach (var l in cat.Catalogo)
            {
                Assert.IsTrue(l.Params.ContainsKey("astig_magnitude"), $"{l.Id} deberia tener astig_magnitude");
                Assert.AreEqual(0f, l.Params["astig_magnitude"].Default, 1e-4f, $"{l.Id}.astig_magnitude default");
                Assert.AreEqual(0f, l.Params["astig_magnitude"].Min, 1e-4f, $"{l.Id}.astig_magnitude min");
                Assert.AreEqual(1f, l.Params["astig_magnitude"].Max, 1e-4f, $"{l.Id}.astig_magnitude max");

                Assert.IsTrue(l.Params.ContainsKey("astig_axis_deg"), $"{l.Id} deberia tener astig_axis_deg");
                Assert.AreEqual(0f, l.Params["astig_axis_deg"].Default, 1e-4f, $"{l.Id}.astig_axis_deg default");
                Assert.AreEqual(0f, l.Params["astig_axis_deg"].Min, 1e-4f, $"{l.Id}.astig_axis_deg min");
                Assert.AreEqual(180f, l.Params["astig_axis_deg"].Max, 1e-4f, $"{l.Id}.astig_axis_deg max");
            }
        }
    }
}
