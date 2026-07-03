using UnityEditor;
using UnityEditor.TestTools.TestRunner.Api;
using UnityEngine;

namespace Simulador.EditorTools
{
    /// <summary>
    /// Dispara la suite EditMode (Assets/Tests/EditMode/, asmdef Simulador.Tests.EditMode)
    /// desde fuera de la ventana de Test Runner y loguea el resumen + cada falla a la
    /// consola. Existe porque `TestRunnerApi.Execute` es async por naturaleza (corre a lo
    /// largo de varios frames del Editor) y su unico mecanismo de resultado es
    /// `ICallbacks`, que no se puede declarar inline desde un snippet de C# arbitrario
    /// (unity_execute_code envuelve el snippet en el CUERPO de un metodo: no admite
    /// declaraciones de clase ahi). Este archivo es la forma minima y reusable de
    /// resolverlo.
    ///
    /// Uso:
    ///   - Menu: Simulador > Run EditMode Tests
    ///   - CLI:  -executeMethod Simulador.EditorTools.EditModeTestRunner.RunEditModeTests
    /// </summary>
    public static class EditModeTestRunner
    {
        [MenuItem("Simulador/Run EditMode Tests")]
        public static void RunEditModeTests()
        {
            var api = ScriptableObject.CreateInstance<TestRunnerApi>();
            var filter = new Filter
            {
                testMode = TestMode.EditMode,
                assemblyNames = new[] { "Simulador.Tests.EditMode" },
            };
            api.RegisterCallbacks(new ResultLogger());
            api.Execute(new ExecutionSettings(filter));
        }

        private class ResultLogger : ICallbacks
        {
            public void RunStarted(ITestAdaptor testsToRun) =>
                Debug.Log($"[TESTRUN] Arrancando {testsToRun.TestCaseCount} tests...");

            public void RunFinished(ITestResultAdaptor result) =>
                Debug.Log($"[TESTRUN] RunFinished: passed={result.PassCount} failed={result.FailCount} " +
                           $"skipped={result.SkipCount} inconclusive={result.InconclusiveCount}");

            public void TestStarted(ITestAdaptor test) { }

            public void TestFinished(ITestResultAdaptor result)
            {
                if (result.TestStatus == TestStatus.Failed)
                    Debug.LogError($"[TESTRUN] FAIL: {result.FullName} :: {result.Message}");
            }
        }
    }
}
