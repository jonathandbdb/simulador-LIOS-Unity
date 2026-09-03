using System.Globalization;
using System.Linq;
using System.Threading;
using NUnit.Framework;
using Simulador.Localization;
using UnityEngine;

namespace Simulador.Tests
{
    /// <summary>
    /// Tests del motor de localizacion (L10n/L10nTable, ver docs/localizacion.md). Mismo
    /// molde que LicenseLogicTests.cs. Los tests que necesitan una clave "rota" a proposito
    /// (falta en un idioma, formato invalido) agregan una entrada TEMPORAL a
    /// L10nTable.Es/En y la borran en el finally -- nunca dejan una clave huerfana para
    /// el test de completitud de abajo.
    /// </summary>
    public class L10nTests
    {
        // ---------------- ResolveFromSystem (logica pura) ----------------

        [Test]
        public void ResolveFromSystem_Spanish_DevuelveEs()
        {
            Assert.AreEqual("es", L10n.ResolveFromSystem(SystemLanguage.Spanish));
        }

        [Test]
        public void ResolveFromSystem_English_DevuelveEn()
        {
            Assert.AreEqual("en", L10n.ResolveFromSystem(SystemLanguage.English));
        }

        [Test]
        public void ResolveFromSystem_Portuguese_DevuelveEn()
        {
            // Default internacional: un idioma no-espanol (aunque sea otro idioma
            // latino) cae a ingles, no a espanol -- ver docs/localizacion.md.
            Assert.AreEqual("en", L10n.ResolveFromSystem(SystemLanguage.Portuguese));
        }

        [Test]
        public void ResolveFromSystem_German_DevuelveEn()
        {
            Assert.AreEqual("en", L10n.ResolveFromSystem(SystemLanguage.German));
        }

        [Test]
        public void ResolveFromSystem_Unknown_DevuelveEn()
        {
            Assert.AreEqual("en", L10n.ResolveFromSystem(SystemLanguage.Unknown));
        }

        // ---------------- Initialize (override > sistema) ----------------

        [Test]
        public void Initialize_OverrideValidoGana()
        {
            L10n.Initialize("en");
            Assert.AreEqual("en", L10n.Lang);
            L10n.Initialize("es");
            Assert.AreEqual("es", L10n.Lang);
        }

        [Test]
        public void Initialize_OverrideInvalidoCaeAlIdiomaDelSistema()
        {
            L10n.Initialize("fr"); // "fr" no es "es" ni "en" -> ResolveFromSystem
            Assert.AreEqual(L10n.ResolveFromSystem(Application.systemLanguage), L10n.Lang);
        }

        [Test]
        public void Initialize_OverrideNuloOVacioCaeAlIdiomaDelSistema()
        {
            L10n.Initialize(null);
            Assert.AreEqual(L10n.ResolveFromSystem(Application.systemLanguage), L10n.Lang);
            L10n.Initialize("");
            Assert.AreEqual(L10n.ResolveFromSystem(Application.systemLanguage), L10n.Lang);
        }

        // ---------------- T(key) ----------------

        [Test]
        public void T_ClaveFaltanteEnAmbasTablas_DevuelveLaClave()
        {
            const string key = "test.no_existe.en_ninguna_tabla";
            L10n.Initialize("es");
            Assert.AreEqual(key, L10n.T(key));
        }

        [Test]
        public void T_ClaveFaltanteEnEn_CaeAEs()
        {
            const string key = "test.solo_en_es";
            L10nTable.Es[key] = "Texto solo en español";
            try
            {
                L10n.Initialize("en");
                Assert.AreEqual("Texto solo en español", L10n.T(key));
            }
            finally
            {
                L10nTable.Es.Remove(key);
            }
        }

        [Test]
        public void T_ClaveExistenteEnAmbas_DevuelveLaDelIdiomaActivo()
        {
            L10n.Initialize("en");
            Assert.AreEqual("Cancel", L10n.T("common.cancel"));
            L10n.Initialize("es");
            Assert.AreEqual("Cancelar", L10n.T("common.cancel"));
        }

        // ---------------- T(key, args) ----------------

        [Test]
        public void T_ConArgs_FormateaElTexto()
        {
            const string key = "test.format.saludo";
            L10nTable.Es[key] = "Hola {0}";
            L10nTable.En[key] = "Hello {0}";
            try
            {
                L10n.Initialize("es");
                Assert.AreEqual("Hola Juan", L10n.T(key, "Juan"));
            }
            finally
            {
                L10nTable.Es.Remove(key);
                L10nTable.En.Remove(key);
            }
        }

        [Test]
        public void T_ConArgs_UsaInvariantCultureSinImportarLaDelHilo()
        {
            const string key = "test.format.numero";
            L10nTable.Es[key] = "{0}";
            var originalCulture = Thread.CurrentThread.CurrentCulture;
            try
            {
                // es-AR usa coma como separador decimal -- si T() usara la cultura del
                // hilo en vez de InvariantCulture, este test fallaria (dependencia de
                // entorno no-negociable, ver AGENTS.md "InvariantCulture se mantiene").
                Thread.CurrentThread.CurrentCulture = new CultureInfo("es-AR");
                L10nTable.Es[key] = "{0}";
                L10n.Initialize("es");
                Assert.AreEqual("3.5", L10n.T(key, 3.5f));
            }
            finally
            {
                Thread.CurrentThread.CurrentCulture = originalCulture;
                L10nTable.Es.Remove(key);
            }
        }

        [Test]
        public void T_ConArgsDeMas_NoTiraExcepcion()
        {
            const string key = "test.format.args_de_mas";
            L10nTable.Es[key] = "Hola {0}";
            try
            {
                L10n.Initialize("es");
                string result = null;
                Assert.DoesNotThrow(() => result = L10n.T(key, "Juan", "Sobra"));
                Assert.AreEqual("Hola Juan", result);
            }
            finally
            {
                L10nTable.Es.Remove(key);
            }
        }

        [Test]
        public void T_ConArgsDeMenos_NoTiraExcepcionYDevuelveElFormatoCrudo()
        {
            const string key = "test.format.args_de_menos";
            L10nTable.Es[key] = "Hola {0} y {1}";
            try
            {
                L10n.Initialize("es");
                string result = null;
                Assert.DoesNotThrow(() => result = L10n.T(key, "Juan"));
                // FormatException atrapada -> se devuelve el texto SIN formatear (nunca
                // una excepcion hacia la UI, ver L10n.T).
                Assert.AreEqual("Hola {0} y {1}", result);
            }
            finally
            {
                L10nTable.Es.Remove(key);
            }
        }

        // ---------------- Has(key) ----------------

        [Test]
        public void Has_ClaveExistente_DevuelveTrue()
        {
            Assert.IsTrue(L10n.Has("common.cancel"));
        }

        [Test]
        public void Has_ClaveInexistente_DevuelveFalse()
        {
            Assert.IsFalse(L10n.Has("test.no_existe.en_ninguna_tabla"));
        }

        // ---------------- Completitud de la tabla ----------------

        [Test]
        public void Tablas_EsYEnTienenExactamenteLasMismasClaves()
        {
            var esKeys = new System.Collections.Generic.HashSet<string>(L10nTable.Es.Keys);
            var enKeys = new System.Collections.Generic.HashSet<string>(L10nTable.En.Keys);
            var soloEnEs = esKeys.Except(enKeys).OrderBy(k => k).ToList();
            var soloEnEn = enKeys.Except(esKeys).OrderBy(k => k).ToList();
            Assert.IsTrue(soloEnEs.Count == 0 && soloEnEn.Count == 0,
                $"Claves solo en Es: [{string.Join(", ", soloEnEs)}]. Claves solo en En: [{string.Join(", ", soloEnEn)}].");
        }

        [Test]
        public void Tablas_NingunValorEsVacioOWhitespace()
        {
            var vaciosEs = L10nTable.Es.Where(kv => string.IsNullOrWhiteSpace(kv.Value)).Select(kv => kv.Key).ToList();
            var vaciosEn = L10nTable.En.Where(kv => string.IsNullOrWhiteSpace(kv.Value)).Select(kv => kv.Key).ToList();
            Assert.IsTrue(vaciosEs.Count == 0 && vaciosEn.Count == 0,
                $"Valores vacios en Es: [{string.Join(", ", vaciosEs)}]. Valores vacios en En: [{string.Join(", ", vaciosEn)}].");
        }

        [Test]
        public void Keys_ExponeLaUnionDeAmbasTablas()
        {
            Assert.IsTrue(L10n.Keys.Contains("common.cancel"));
            Assert.AreEqual(L10nTable.Es.Keys.Count, L10n.Keys.Count); // simetricas -> la union tiene el mismo tamaño que cada tabla
        }
    }
}
