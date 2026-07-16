using System.IO;
using System.Linq;
using UnityEditor.Android;
using UnityEngine;

namespace Simulador.EditorTools
{
    /// <summary>
    /// Post-procesador de Gradle que borra del boot.config YA GENERADO los flags "xr-*" que
    /// el hook interno del paquete OpenXR (MetaQuestFeatureBuildHooks.OnProcessBootConfigExt,
    /// via BootConfigBuilder) escribe durante el build Android -- incluido el de la tablet,
    /// pese a que TabletBuild.BuildTablet() apaga el loader OpenXR (SetLoaders + TrySetLoaders,
    /// "cinturon" del gate IsExtensionEnabled).
    ///
    /// Historia (ver docs/builds-deploy.md Gotchas para el detalle completo): un primer intento
    /// deshabilito ademas MetaQuestFeature.enabled en memoria durante el build ("tiradores" del
    /// mismo gate) para anularlo aunque la cache runtime del loader quedara stale. NO fue
    /// confiable en el build real -- el APK sigue saliendo con xr-keyboard-overlay-enabled=1 en
    /// boot.config -- y encima el hook del propio paquete (ApplySettingsOverride ->
    /// AssetDatabase.SaveAssetIfDirty) persistio m_enabled: 0 del feature A DISCO en
    /// "Assets/XR/Settings/OpenXR Package Settings.asset", ensuciando un asset compartido con
    /// riesgo para el visor. Pelear contra el estado interno del paquete OpenXR durante el
    /// BuildPipeline resulto fragil. Esta clase es la via determinista: en vez de evitar que el
    /// hook escriba los flags, los borra del archivo YA GENERADO, DESPUES de todos los
    /// escritores (callbackOrder alto).
    ///
    /// Gateada por TabletBuild.IsTabletBuildInProgress: SOLO actua durante un build de tablet
    /// (TabletBuild.BuildTablet(), true en su try, false en su finally); en un build del visor
    /// (Main.unity, loader OpenXR ON a proposito, esos flags SI deben quedar) el flag esta en
    /// false y este patcher no toca nada.
    /// </summary>
    internal class TabletBootConfigPatcher : IPostGenerateGradleAndroidProject
    {
        // Alto a proposito: correr DESPUES de cualquier otro postprocesador que pudiera
        // tocar el proyecto Gradle generado (incluido el propio hook de OpenXR).
        public int callbackOrder => 9999;

        const string BootConfigRelativePath = "src/main/assets/bin/Data/boot.config";

        public void OnPostGenerateGradleAndroidProject(string path)
        {
            // SIM: atajo deliberado -- return inmediato e inofensivo si no es un build de
            // tablet (no hay nada que limpiar en el build del visor).
            if (!TabletBuild.IsTabletBuildInProgress)
                return;

            // El "path" recibido suele ser el del modulo unityLibrary (donde vive el
            // boot.config real); si este callback llega con el path del modulo launcher
            // (hermano de unityLibrary en la raiz del proyecto Gradle), probar ese layout
            // alternativo antes de rendirse.
            string bootConfigPath = Path.Combine(path, BootConfigRelativePath);
            string alternativePath = Path.Combine(path, "..", "unityLibrary", BootConfigRelativePath);
            if (!File.Exists(bootConfigPath) && File.Exists(alternativePath))
                bootConfigPath = alternativePath;

            if (!File.Exists(bootConfigPath))
            {
                Debug.LogError("[TabletBuild] No se encontro boot.config para parchear los flags xr-* " +
                               $"(probado '{Path.Combine(path, BootConfigRelativePath)}' y '{alternativePath}'). " +
                               "El teclado Android puede volver a romperse en el APK de la tablet -- " +
                               "ver docs/builds-deploy.md.");
                return;
            }

            var lines = File.ReadAllLines(bootConfigPath);
            var kept = lines.Where(l => !l.TrimStart().StartsWith("xr-")).ToArray();
            int removed = lines.Length - kept.Length;
            File.WriteAllLines(bootConfigPath, kept);

            Debug.Log($"[TabletBuild] boot.config: {removed} flags xr-* eliminados ('{bootConfigPath}').");
        }
    }
}
