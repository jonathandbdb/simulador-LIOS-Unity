using System;
using System.IO;
using UnityEngine;

namespace Simulador.Tablet
{
    /// <summary>
    /// Modo kiosco de la tablet (Fase A, ver docs/tablet.md "Decisiones" y
    /// docs/builds-deploy.md "Provisión de tablets (Device Owner)"). Envuelve el
    /// JNI de <c>android.app.admin.DevicePolicyManager</c> para una tablet
    /// provisionada como Android Device Owner (<c>adb shell dpm
    /// set-device-owner</c>, scripts/provision-tablet.sh) con
    /// <c>SimuladorDeviceAdminReceiver</c> (Assets/Plugins/Android/com/simulador/
    /// kiosk/) como admin activo. Plain C# estático (no MonoBehaviour) -- lo
    /// llama <see cref="Simulador.Net.TabletController"/> (Start/
    /// OnApplicationPause/gesto de salida de servicio), la escena no cambia.
    ///
    /// Todo detrás de <c>#if UNITY_ANDROID &amp;&amp; !UNITY_EDITOR</c> con
    /// gemelos no-op (mismo patrón que <c>TabletController.TryGetWifiSsid</c> y
    /// <see cref="Simulador.Update.UpdateInstaller"/>): <c>AndroidJavaClass</c>/
    /// <c>AndroidJavaObject</c> (JNI, no reflection de .NET -- no lo toca el
    /// stripping de IL2CPP). En una tablet de desarrollo SIN Device Owner,
    /// <see cref="IsDeviceOwner"/> da <c>false</c> y todas las policies/lock
    /// task son no-op -- no rompe el flujo normal de trabajo del equipo.
    /// </summary>
    public static class KioskManager
    {
        const string ReceiverClassName = "com.simulador.kiosk.SimuladorDeviceAdminReceiver";
        const string SettingsPackage = "com.android.settings";
        const string UnityActivityClassName = "com.unity3d.player.UnityPlayerGameActivity";
        const string ServiceModeFlagFileName = "kiosk_service_mode";

        // Correcciones (ver docs/tablet.md "Salida de servicio"): ruta del flag de
        // modo servicio. Fuera de los #if de abajo porque System.IO funciona igual
        // en Editor/Android -- solo el JNI de EnterServiceMode/LeaveServiceMode
        // difiere por plataforma. TabletController.Start() lo consulta ANTES de
        // decidir si reaplica el kiosco.
        static string ServiceModeFlagPath => Path.Combine(Application.persistentDataPath, ServiceModeFlagFileName);

        /// <summary>True si la tablet quedo en modo servicio (kiosco desactivado hasta <see cref="LeaveServiceMode"/>).</summary>
        public static bool IsInServiceMode => File.Exists(ServiceModeFlagPath);

#if UNITY_ANDROID && !UNITY_EDITOR
        const int LockTaskFeatureSystemInfo = 1;
        const int LockTaskFeatureGlobalActions = 16;
        const int PermissionGrantStateGranted = 1;
        const int LockTaskModeNone = 0;
        const int FlagActivityNewTask = 0x10000000;

        static bool? _isDeviceOwnerCache;

        /// <summary>Cacheado: la condicion de Device Owner no cambia durante la vida del proceso.</summary>
        public static bool IsDeviceOwner
        {
            get
            {
                if (_isDeviceOwnerCache.HasValue)
                    return _isDeviceOwnerCache.Value;

                bool result = false;
                try
                {
                    using var dpm = GetDevicePolicyManager();
                    result = dpm.Call<bool>("isDeviceOwnerApp", Application.identifier);
                }
                catch (Exception e)
                {
                    Debug.LogWarning("[Kiosk] No se pudo consultar isDeviceOwnerApp: " + e.Message);
                }
                _isDeviceOwnerCache = result;
                return result;
            }
        }

        /// <summary>
        /// Aplica las policies del kiosco. No-op si <see cref="IsDeviceOwner"/> es
        /// false (tablet de desarrollo sin provisionar). Cada policy en su propio
        /// try/catch: que una falle en una ROM particular no debe impedir las demas.
        /// </summary>
        public static void ApplyPolicies()
        {
            if (!IsDeviceOwner)
            {
                Debug.Log("[Kiosk] ApplyPolicies: no-op (esta tablet no es Device Owner).");
                return;
            }

            AndroidJavaObject dpm = null;
            AndroidJavaObject admin = null;
            try
            {
                dpm = GetDevicePolicyManager();
                admin = new AndroidJavaObject("android.content.ComponentName", Application.identifier, ReceiverClassName);

                // com.android.settings en el allowlist: sin esto, el panel de
                // ajustes de WiFi (KioskManager.OpenWifiSettings, Fase B) no
                // podria abrir bajo lock task.
                TryPolicy("setLockTaskPackages", () =>
                    dpm.Call("setLockTaskPackages", admin, new[] { Application.identifier, SettingsPackage }));

                // GLOBAL_ACTIONS es OBLIGATORIO: sin el, el menu de apagado
                // (long-press power) queda bloqueado bajo lock task y el cliente
                // no puede apagar la tablet.
                TryPolicy("setLockTaskFeatures", () =>
                    dpm.Call("setLockTaskFeatures", admin, LockTaskFeatureGlobalActions | LockTaskFeatureSystemInfo));

                TryPolicy("addPersistentPreferredActivity", () =>
                {
                    using var filter = new AndroidJavaObject("android.content.IntentFilter", "android.intent.action.MAIN");
                    filter.Call("addCategory", "android.intent.category.HOME");
                    filter.Call("addCategory", "android.intent.category.DEFAULT");
                    using var activityComponent = new AndroidJavaObject(
                        "android.content.ComponentName", Application.identifier, UnityActivityClassName);
                    dpm.Call("addPersistentPreferredActivity", admin, filter, activityComponent);
                });

                // CRITICO #3/#4 (correcciones): setKeyguardDisabled/setStatusBarDisabled
                // devuelven boolean -- Call(...) sin tipo generico solo resuelve
                // metodos Java void (ReflectionHelper.getMethodID puntua 0 al
                // candidato y tira NoSuchMethodError, que este mismo try/catch
                // tragaba en silencio). Sin Call<bool>, la policy NUNCA se aplicaba
                // y la tablet conservaba pantalla de bloqueo y barra de estado.
                TryPolicy("setKeyguardDisabled", () => dpm.Call<bool>("setKeyguardDisabled", admin, true));
                TryPolicy("setStatusBarDisabled", () => dpm.Call<bool>("setStatusBarDisabled", admin, true));

                // no_debugging_features NO se agrega: cortaria el adb de soporte.
                // no_install_apps NO se agrega: puede interferir con la Fase C
                // (updates/OTA). Ver docs/tablet.md Decisiones.
                TryPolicy("addUserRestriction(no_factory_reset)", () => dpm.Call("addUserRestriction", admin, "no_factory_reset"));
                TryPolicy("addUserRestriction(no_safe_boot)", () => dpm.Call("addUserRestriction", admin, "no_safe_boot"));
                TryPolicy("addUserRestriction(no_add_user)", () => dpm.Call("addUserRestriction", admin, "no_add_user"));

                TryPolicy("setGlobalSetting(stay_on_while_plugged_in)", () =>
                    dpm.Call("setGlobalSetting", admin, "stay_on_while_plugged_in", "3"));

                // El SSID se lee sin el dialogo de permiso (ver TabletController.
                // TryGetWifiSsid/RequestLocationPermissionOnce, que siguen
                // funcionando igual en una tablet SIN Device Owner).
                TryPolicy("setPermissionGrantState(ACCESS_FINE_LOCATION)", () =>
                    dpm.Call<bool>("setPermissionGrantState", admin, Application.identifier,
                        "android.permission.ACCESS_FINE_LOCATION", PermissionGrantStateGranted));

                Debug.Log("[Kiosk] ApplyPolicies: politicas de Device Owner aplicadas.");
            }
            catch (Exception e)
            {
                Debug.LogWarning("[Kiosk] ApplyPolicies: fallo inesperado (" + e.GetType().Name + "): " + e.Message);
            }
            finally
            {
                admin?.Dispose();
                dpm?.Dispose();
            }
        }

        static void TryPolicy(string name, Action action)
        {
            try
            {
                action();
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[Kiosk] Policy '{name}' fallo ({e.GetType().Name}): {e.Message}");
            }
        }

        /// <summary>Entra a lock task (pantalla de inicio fijada, sin gestos de salida). No-op si no es Device Owner. Idempotente.</summary>
        public static void EnterLockTask()
        {
            if (!IsDeviceOwner)
                return;

            RunOnUiThread(() =>
            {
                try
                {
                    using var activity = GetCurrentActivity();
                    activity.Call("startLockTask");
                    Debug.Log("[Kiosk] startLockTask() OK.");
                }
                catch (Exception e)
                {
                    // Tira IllegalArgumentException si el paquete no esta en el
                    // allowlist (setLockTaskPackages) -- o si ya estaba en lock
                    // task (Android tolera el re-entrar, pero por las dudas).
                    Debug.LogWarning("[Kiosk] startLockTask() fallo (" + e.GetType().Name + "): " + e.Message);
                }
            });
        }

        /// <summary>
        /// MAYOR #12 (correcciones, ver docs/tablet.md "Salida de servicio"): saca
        /// la tablet del kiosco de verdad y deja una ventana real para el
        /// operador -- reemplaza el <c>ExitLockTask() + Application.Quit()</c>
        /// anterior (carrera runOnUiThread/Quit, y aunque ganara la HOME
        /// persistente relanzaba la app cuyo Start() volvia a EnterLockTask()).
        /// Sale de lock task, libera la HOME persistente
        /// (<c>clearPackagePersistentPreferredActivities</c>, asi Home vuelve al
        /// launcher del sistema) y reactiva keyguard/barra de estado -- cada paso
        /// en su propio try/catch, igual que <see cref="ApplyPolicies"/>. Escribe
        /// el flag de <see cref="IsInServiceMode"/> para que
        /// <c>TabletController.Start()</c> no vuelva a fijar el kiosco si Android
        /// relanza la app mientras el flag sigue puesto.
        /// </summary>
        public static void EnterServiceMode()
        {
            RunOnUiThread(() =>
            {
                AndroidJavaObject dpm = null;
                AndroidJavaObject admin = null;
                try
                {
                    using var activity = GetCurrentActivity();
                    TryPolicy("stopLockTask", () => activity.Call("stopLockTask"));

                    dpm = GetDevicePolicyManager();
                    admin = new AndroidJavaObject("android.content.ComponentName", Application.identifier, ReceiverClassName);
                    TryPolicy("clearPackagePersistentPreferredActivities", () =>
                        dpm.Call("clearPackagePersistentPreferredActivities", admin, Application.identifier));
                    TryPolicy("setStatusBarDisabled(false)", () => dpm.Call<bool>("setStatusBarDisabled", admin, false));
                    TryPolicy("setKeyguardDisabled(false)", () => dpm.Call<bool>("setKeyguardDisabled", admin, false));
                }
                catch (Exception e)
                {
                    Debug.LogWarning("[Kiosk] EnterServiceMode: fallo inesperado (" + e.GetType().Name + "): " + e.Message);
                }
                finally
                {
                    admin?.Dispose();
                    dpm?.Dispose();
                }
            });

            try
            {
                File.WriteAllText(ServiceModeFlagPath, "");
                Debug.Log("[Kiosk] EnterServiceMode: modo servicio activado.");
            }
            catch (Exception e)
            {
                Debug.LogWarning("[Kiosk] EnterServiceMode: no se pudo escribir el flag (" + e.GetType().Name + "): " + e.Message);
            }
        }

        /// <summary>
        /// Vuelve del modo servicio: borra el flag, reaplica las policies (HOME
        /// persistente incluida) y reinicia el proceso para que la app renazca en
        /// una tarea "home" -- NO llama <see cref="EnterLockTask()"/> directo
        /// sobre la tarea actual. Motivo (misma carrera que la de
        /// docs/builds-deploy.md "Provision de tablets"): mientras estaba en modo
        /// servicio el operador pudo haber relanzado la app desde el launcher del
        /// sistema (tarea type=standard, persistent-preferred estaba limpio); si
        /// esta tarea standard se bloquea con <c>startLockTask</c> y despues se
        /// pulsa Home, Android crea una SEGUNDA instancia de la Activity en el
        /// MISMO proceso (UnityFoldingFeaturesWrapper es estatico por proceso) y
        /// crashea. En vez de arriesgarse, <c>ApplyPolicies()</c> deja el HOME
        /// persistente puesto de nuevo y el proceso se reinicia solo -- la HOME
        /// persistente relanza la Activity via un intent HOME real (misma forma
        /// en que nace la app tras un reboot, nunca type=standard), y su propio
        /// <c>Start()</c> vuelve a llamar <see cref="ApplyPolicies()"/> +
        /// <see cref="EnterLockTask()"/> ya sobre la tarea "home" correcta.
        /// </summary>
        public static void LeaveServiceMode()
        {
            try
            {
                File.Delete(ServiceModeFlagPath);
            }
            catch (Exception e)
            {
                Debug.LogWarning("[Kiosk] LeaveServiceMode: no se pudo borrar el flag (" + e.GetType().Name + "): " + e.Message);
            }
            ApplyPolicies();
            RestartProcess();
        }

        // SIM: atajo deliberado -- reinicio de proceso para nacer en tarea HOME;
        // Unity no tolera dos instancias de la Activity por proceso (ver el
        // <summary> de LeaveServiceMode). finishAndRemoveTask() + killProcess()
        // corren en el hilo de UI de Android, uno despues del otro: quitar la
        // tarea actual de Recents primero y despues matar el proceso deja a
        // Android sin "top activity" que reintentar -- resuelve HOME de nuevo
        // contra el persistent-preferred que ApplyPolicies() acaba de reponer,
        // igual que arranca la app despues de un reboot real.
        //
        // Publico (hallazgo 2026-09-03, PHILCO TP10A46414379100691): bajo lock
        // task, Android bloquea finish()/Application.Quit() de la Activity raiz
        // de la tarea bloqueada ("Not finishing task in lock task mode" en
        // logcat -- el PID no cambia, la UI sigue como estaba). Matar el proceso
        // es la UNICA forma de reiniciar la app en kiosco; la HOME persistente
        // (ApplyPolicies) la relanza sola. Lo usa tambien TabletController tras
        // confirmar el cambio de idioma (ver OnLangConfirmPressed), no solo
        // LeaveServiceMode.
        public static void RestartProcess()
        {
            RunOnUiThread(() =>
            {
                try
                {
                    using var activity = GetCurrentActivity();
                    activity.Call("finishAndRemoveTask");
                }
                catch (Exception e)
                {
                    Debug.LogWarning("[Kiosk] finishAndRemoveTask() fallo (" + e.GetType().Name + "): " + e.Message);
                }

                try
                {
                    using var processClass = new AndroidJavaClass("android.os.Process");
                    int pid = processClass.CallStatic<int>("myPid");
                    processClass.CallStatic("killProcess", pid);
                }
                catch (Exception e)
                {
                    Debug.LogWarning("[Kiosk] killProcess() fallo (" + e.GetType().Name + "): " + e.Message);
                }
            });
        }

        public static bool IsLockTaskActive
        {
            get
            {
                try
                {
                    using var activity = GetCurrentActivity();
                    using var am = activity.Call<AndroidJavaObject>("getSystemService", "activity");
                    int state = am.Call<int>("getLockTaskModeState");
                    return state != LockTaskModeNone;
                }
                catch (Exception e)
                {
                    Debug.LogWarning("[Kiosk] getLockTaskModeState() fallo: " + e.Message);
                    return false;
                }
            }
        }

        /// <summary>
        /// Abre el panel/pantalla de ajustes de WiFi (Fase B). NO gateado por
        /// <see cref="IsDeviceOwner"/> a proposito -- sirve igual en una tablet
        /// SIN Device Owner (solo que ahi no hay lock task del que "escapar").
        /// Intenta primero el panel liviano (sheet sobre la app, API 29+) y si
        /// tira excepcion cae a la pantalla completa de ajustes de WiFi.
        /// </summary>
        public static void OpenWifiSettings()
        {
            RunOnUiThread(() =>
            {
                try
                {
                    OpenSettingsIntent("android.settings.panel.action.WIFI");
                }
                catch (Exception e)
                {
                    Debug.LogWarning("[Kiosk] Panel WiFi liviano fallo (" + e.GetType().Name + "), cae a WIFI_SETTINGS: " + e.Message);
                    try
                    {
                        OpenSettingsIntent("android.settings.WIFI_SETTINGS");
                    }
                    catch (Exception e2)
                    {
                        Debug.LogWarning("[Kiosk] WIFI_SETTINGS tambien fallo: " + e2.Message);
                    }
                }
            });
        }

        static void OpenSettingsIntent(string action)
        {
            using var activity = GetCurrentActivity();
            using var intent = new AndroidJavaObject("android.content.Intent", action);
            // CRITICO #1 (correcciones): Intent.setFlags(int) devuelve Intent, no
            // void -- Call(...) sin tipo generico tiraba NoSuchMethodError (tragado
            // por el catch de OpenWifiSettings) y este era el UNICO camino que
            // tiene la clinica para cambiar de red Wi-Fi bajo lock task. Misma
            // firma que UpdateInstaller.cs (setFlags con Call<AndroidJavaObject>).
            intent.Call<AndroidJavaObject>("setFlags", FlagActivityNewTask);
            activity.Call("startActivity", intent);
        }

        static AndroidJavaObject GetCurrentActivity()
        {
            using var unityPlayer = new AndroidJavaClass("com.unity3d.player.UnityPlayer");
            return unityPlayer.GetStatic<AndroidJavaObject>("currentActivity");
        }

        static AndroidJavaObject GetDevicePolicyManager()
        {
            using var activity = GetCurrentActivity();
            return activity.Call<AndroidJavaObject>("getSystemService", "device_policy");
        }

        static void RunOnUiThread(Action action)
        {
            try
            {
                using var activity = GetCurrentActivity();
                activity.Call("runOnUiThread", new AndroidJavaRunnable(action));
            }
            catch (Exception e)
            {
                Debug.LogWarning("[Kiosk] runOnUiThread() fallo (" + e.GetType().Name + "): " + e.Message);
            }
        }
#else
        // SIM: atajo deliberado -- fuera de Android (Editor) no hay
        // DevicePolicyManager/Activity real; no-ops logueados para que
        // TabletController pueda llamar esta API sin #if propios.
        public static bool IsDeviceOwner => false;
        public static void ApplyPolicies() { }
        public static void EnterLockTask() { }
        public static void EnterServiceMode() { }
        public static void LeaveServiceMode() { }
        public static void RestartProcess() { }
        public static bool IsLockTaskActive => false;
        public static void OpenWifiSettings() => Debug.Log("[Kiosk] OpenWifiSettings no-op fuera de Android (Editor).");
#endif
    }
}
