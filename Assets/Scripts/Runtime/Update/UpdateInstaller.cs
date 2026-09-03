using System;
using System.IO;
using Simulador.Tablet;
using UnityEngine;

namespace Simulador.Update
{
    /// <summary>
    /// F4: dispara el intent Android que instala el APK ya descargado y verificado por
    /// <see cref="UpdateManager"/> (evento <c>ReadyToInstall</c>). Toda la implementacion
    /// JNI vive detras de <c>#if UNITY_ANDROID &amp;&amp; !UNITY_EDITOR</c> (no-op + log en
    /// Editor/otras plataformas). Usa <c>AndroidJavaClass</c>/<c>AndroidJavaObject</c>
    /// (nunca reflection de C#) -- mismo patron que
    /// <see cref="Simulador.Tablet.TabletController"/> (TryGetWifiSsid), IL2CPP-safe.
    /// Ver docs/updates.md (flujo del intent, permiso de fuentes desconocidas, marcador
    /// <c>update_pending.json</c>).
    /// Fase C (kiosco): si <see cref="KioskManager.IsDeviceOwner"/>, en vez del intent
    /// ACTION_VIEW visible se instala TOTALMENTE en silencio via
    /// <c>com.simulador.kiosk.SilentInstaller</c> (PackageInstaller) -- nadie va a tocar
    /// una tablet vendida a una clinica de otro pais para aceptar un dialogo ni conceder
    /// el permiso de "fuentes desconocidas". Ver <see cref="InstallLaunchResult.StartedSilent"/>.
    /// </summary>
    public static class UpdateInstaller
    {
        /// <summary>Resultado de intentar lanzar el instalador.</summary>
        public enum InstallLaunchResult
        {
            /// <summary>Intent ACTION_VIEW de instalacion lanzado.</summary>
            Started,
            /// <summary>No habia permiso de fuentes desconocidas; se abrio el ajuste del
            /// sistema para concederlo. El caller debe reintentar al volver a foco.</summary>
            PermissionRequested,
            /// <summary>Fallo (excepcion atrapada); ya se reporto via el callback de fallo.</summary>
            Failed,
            /// <summary>Fase C: instalado en background via PackageInstaller, sin dialogo
            /// ni intervencion humana (tablet Device Owner, ver
            /// <see cref="KioskManager.IsDeviceOwner"/>).</summary>
            StartedSilent,
        }

#if UNITY_ANDROID && !UNITY_EDITOR
        private const int FlagGrantReadUriPermission = 0x00000001;
        private const int FlagActivityNewTask = 0x10000000;

        /// <summary>
        /// Lanza el instalador del APK en <paramref name="apkPath"/>. Si Android no
        /// permite instalar de "fuentes desconocidas" para esta app, en cambio abre el
        /// ajuste correspondiente y devuelve <see cref="InstallLaunchResult.PermissionRequested"/>
        /// (el caller reintenta cuando la app vuelve a foco). <paramref name="onFailed"/>
        /// se invoca con un mensaje corto si algo falla -- try/catch total, nunca deja
        /// escapar una excepcion (JNI en runtime Android es fragil: version de
        /// androidx.core, permisos, OEM, etc.).
        /// </summary>
        public static InstallLaunchResult LaunchInstall(string apkPath, string targetVersion, Action<string> onFailed)
        {
            try
            {
                using var unityPlayer = new AndroidJavaClass("com.unity3d.player.UnityPlayer");
                using var activity = unityPlayer.GetStatic<AndroidJavaObject>("currentActivity");

                // Fase C: tablet Device Owner -> instalacion TOTALMENTE
                // silenciosa via PackageInstaller, sin pasar por el intent
                // ACTION_VIEW de abajo (que abre un dialogo visible) ni por el
                // permiso de "fuentes desconocidas". Si esto falla, NO caemos
                // al ACTION_VIEW como fallback a proposito: mostraria un
                // dialogo del sistema en una tablet kiosco sin nadie delante
                // para tocarlo -- mejor fallar, reportarlo por telemetria
                // (update_install_launched result=Failed) y dejar que el
                // proximo arranque de la app reintente el chequeo de manifest.
                if (KioskManager.IsDeviceOwner)
                {
                    try
                    {
                        WritePendingMarker(targetVersion);
                        using var installer = new AndroidJavaClass("com.simulador.kiosk.SilentInstaller");
                        installer.CallStatic("install", activity, apkPath);
                        Debug.Log("Update: instalacion silenciosa lanzada (Device Owner) para " + apkPath);
                        return InstallLaunchResult.StartedSilent;
                    }
                    catch (Exception silentEx)
                    {
                        Debug.LogWarning("Update: fallo la instalacion silenciosa (" + silentEx.GetType().Name + "): " + silentEx.Message);
                        onFailed?.Invoke("silent_install_failed: " + silentEx.GetType().Name);
                        return InstallLaunchResult.Failed;
                    }
                }

                using var packageManager = activity.Call<AndroidJavaObject>("getPackageManager");

                bool canInstall = packageManager.Call<bool>("canRequestPackageInstalls");
                if (!canInstall)
                {
                    using var uriClass = new AndroidJavaClass("android.net.Uri");
                    using var settingsUri = uriClass.CallStatic<AndroidJavaObject>("parse", "package:" + Application.identifier);
                    using var permissionIntent = new AndroidJavaObject("android.content.Intent", "android.settings.MANAGE_UNKNOWN_APP_SOURCES");
                    permissionIntent.Call<AndroidJavaObject>("setData", settingsUri);
                    activity.Call("startActivity", permissionIntent);
                    Debug.Log("Update: sin permiso de fuentes desconocidas -- se abrio el ajuste del sistema.");
                    return InstallLaunchResult.PermissionRequested;
                }

                WritePendingMarker(targetVersion);

                using var fileProviderClass = new AndroidJavaClass("androidx.core.content.FileProvider");
                using var apkFile = new AndroidJavaObject("java.io.File", apkPath);
                string authority = Application.identifier + ".fileprovider";
                using var apkUri = fileProviderClass.CallStatic<AndroidJavaObject>("getUriForFile", activity, authority, apkFile);

                using var installIntent = new AndroidJavaObject("android.content.Intent", "android.intent.action.VIEW");
                installIntent.Call<AndroidJavaObject>("setDataAndType", apkUri, "application/vnd.android.package-archive");
                installIntent.Call<AndroidJavaObject>("setFlags", FlagGrantReadUriPermission | FlagActivityNewTask);
                activity.Call("startActivity", installIntent);

                Debug.Log("Update: intent de instalacion lanzado para " + apkPath);
                return InstallLaunchResult.Started;
            }
            catch (Exception e)
            {
                Debug.LogWarning("Update: fallo al lanzar el intent de instalacion (" + e.GetType().Name + "): " + e.Message);
                onFailed?.Invoke("install_intent_failed: " + e.GetType().Name);
                return InstallLaunchResult.Failed;
            }
        }

        // Best-effort: si no se puede escribir el marcador, se loguea pero NO se aborta
        // el intent de instalacion (la telemetria de "aplico OK" de F6 es deseable, no
        // bloqueante).
        private static void WritePendingMarker(string targetVersion)
        {
            try
            {
                string path = Path.Combine(Application.persistentDataPath, UpdateLogic.PendingMarkerFileName);
                File.WriteAllText(path, UpdateLogic.SerializePendingMarker(targetVersion));
            }
            catch (Exception e)
            {
                Debug.LogWarning("Update: no se pudo escribir el marcador de update pendiente (" + e.GetType().Name + ").");
            }
        }
#else
        // SIM: atajo deliberado -- fuera de Android (Editor) no hay PackageManager/
        // FileProvider real; no-op logueado. F7 valida el flujo real en dispositivo.
        public static InstallLaunchResult LaunchInstall(string apkPath, string targetVersion, Action<string> onFailed)
        {
            Debug.Log("Update: LaunchInstall no-op fuera de Android (Editor). apkPath=" + apkPath);
            return InstallLaunchResult.Started;
        }
#endif
    }
}
