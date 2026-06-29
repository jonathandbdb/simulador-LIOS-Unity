using System.Collections.Generic;
using System.IO;
using UnityEditor;
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
    /// (incluso si el build falla), dejando la config de Quest intacta.
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

            try
            {
                // Apagar XR para Android: vaciar la lista de loaders.
                if (manager != null)
                    SetLoaders(manager, new List<Object>());

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
                // Restaurar SIEMPRE los loaders XR originales (Quest queda intacto).
                if (manager != null)
                    SetLoaders(manager, savedLoaders);
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
