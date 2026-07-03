using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;
using UnityEditor.XR.Management;
using UnityEngine;
using UnityEngine.XR.Management;

namespace Simulador.EditorTools
{
    /// <summary>
    /// Build de la app TABLET (Assets/Scenes/Tablet.unity) para Android.
    ///
    /// La app tablet es plana (sin VR). El target Android del proyecto esta configurado
    /// para Quest: tiene el loader de OpenXR activo. Si ese loader queda activo en una
    /// tablet sin runtime VR, el subsistema XR secuestra el present y la pantalla queda
    /// completamente negra (la app corre, pero no presenta ningun frame).
    ///
    /// Como el target Android es compartido con el build de Quest, este script apaga el
    /// loader de OpenXR SOLO durante el build del tablet y lo restaura SIEMPRE despues
    /// (incluso si el build falla), dejando la config de Quest intacta. Mismo patron
    /// (setear -> try/finally restaurar) para el applicationIdentifier/productName
    /// (P6.7): visor y tablet comparten el mismo Player Settings del target Android, asi
    /// que sin esto ambos APKs saldrian con el package del visor (com.simulador.vr) y no
    /// podrian convivir instalados en el mismo dispositivo.
    ///
    /// Uso:
    ///   - Menu: Simulador > Build Tablet (Android)
    ///   - CLI:  -executeMethod Simulador.EditorTools.TabletBuild.BuildTablet
    /// </summary>
    public static class TabletBuild
    {
        const string ScenePath = "Assets/Scenes/Tablet.unity";
        const string OutputPath = "Builds/Android/Simulador.apk";
        const string XrConfigKey = "com.unity.xr.management.loader_settings";

        // P6.7: identifier/nombre propios de la tablet. El visor (build normal, fuera
        // de este script) sigue con com.simulador.vr / "Simulador" (Project Settings).
        const string TabletApplicationIdentifier = "com.simulador.tablet";
        const string TabletProductName = "Simulador Tablet";

        [MenuItem("Simulador/Build Tablet (Android)")]
        public static void BuildTabletMenu()
        {
            var report = BuildTablet();
            if (report == null)
                return;

            var s = report.summary;
            Debug.Log($"[TabletBuild] {s.result} — {s.totalErrors} errores, {s.totalWarnings} warnings, " +
                      $"{s.totalSize / (1024 * 1024)} MB en {s.totalTime.TotalSeconds:F1}s -> {s.outputPath}");
        }

        /// <summary>
        /// Buildea el APK del tablet con XR desactivado y restaura la config XR al terminar.
        /// Devuelve el BuildReport, o null si no se pudo iniciar (target no-Android).
        /// </summary>
        public static BuildReport BuildTablet()
        {
            if (EditorUserBuildSettings.activeBuildTarget != BuildTarget.Android)
            {
                Debug.LogError($"[TabletBuild] El build target activo es {EditorUserBuildSettings.activeBuildTarget}. " +
                               "Cambialo a Android (File > Build Profiles) antes de buildear el tablet.");
                return null;
            }

            var manager = GetAndroidXrManager();
            var savedLoaders = manager != null ? GetLoaders(manager) : null;

            // P6.7: guardar el identifier/nombre ANTES de tocarlos (mismo momento que
            // los loaders XR) para poder restaurarlos pase lo que pase.
            var androidTarget = NamedBuildTarget.Android;
            string savedApplicationIdentifier = PlayerSettings.GetApplicationIdentifier(androidTarget);
            string savedProductName = PlayerSettings.productName;

            try
            {
                // Apagar XR para Android: vaciar la lista de loaders.
                if (manager != null)
                    SetLoaders(manager, new List<Object>());

                // Identifier propio de la tablet: sin esto, el APK sale con
                // com.simulador.vr (el del visor) y no pueden convivir instalados en
                // el mismo dispositivo -- instalar uno reemplaza al otro.
                PlayerSettings.SetApplicationIdentifier(androidTarget, TabletApplicationIdentifier);
                PlayerSettings.productName = TabletProductName;

                Directory.CreateDirectory(Path.GetDirectoryName(OutputPath));

                var options = new BuildPlayerOptions
                {
                    scenes = new[] { ScenePath },
                    locationPathName = OutputPath,
                    target = BuildTarget.Android,
                    targetGroup = BuildTargetGroup.Android,
                    options = BuildOptions.None,
                };
                return BuildPipeline.BuildPlayer(options);
            }
            finally
            {
                // Restaurar SIEMPRE los loaders XR y el identifier/nombre originales
                // (el proyecto vuelve a quedar configurado para el visor, Quest intacto).
                if (manager != null)
                    SetLoaders(manager, savedLoaders);
                PlayerSettings.SetApplicationIdentifier(androidTarget, savedApplicationIdentifier);
                PlayerSettings.productName = savedProductName;
            }
        }

        static XRManagerSettings GetAndroidXrManager()
        {
            EditorBuildSettings.TryGetConfigObject(XrConfigKey, out XRGeneralSettingsPerBuildTarget perBuildTarget);
            if (perBuildTarget == null)
                return null;

            var settings = perBuildTarget.SettingsForBuildTarget(BuildTargetGroup.Android);
            return settings != null ? settings.Manager : null;
        }

        static List<Object> GetLoaders(XRManagerSettings manager)
        {
            var result = new List<Object>();
            var loaders = new SerializedObject(manager).FindProperty("m_Loaders");
            for (int i = 0; i < loaders.arraySize; i++)
                result.Add(loaders.GetArrayElementAtIndex(i).objectReferenceValue);
            return result;
        }

        static void SetLoaders(XRManagerSettings manager, List<Object> loaderAssets)
        {
            var so = new SerializedObject(manager);
            var loaders = so.FindProperty("m_Loaders");
            loaders.ClearArray();
            for (int i = 0; i < loaderAssets.Count; i++)
            {
                loaders.InsertArrayElementAtIndex(i);
                loaders.GetArrayElementAtIndex(i).objectReferenceValue = loaderAssets[i];
            }
            so.ApplyModifiedProperties();
            EditorUtility.SetDirty(manager);
            AssetDatabase.SaveAssets();
        }
    }
}
