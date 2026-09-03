using System;
using System.IO;
using System.Linq;
using System.Xml.Linq;
using UnityEditor.Android;
using UnityEngine;

namespace Simulador.EditorTools
{
    /// <summary>
    /// Post-procesador de Gradle que inyecta en el AndroidManifest.xml YA GENERADO
    /// (unityLibrary/src/main/AndroidManifest.xml, el mismo archivo intermedio que
    /// lee/escribe <see cref="TabletBootConfigPatcher"/> para boot.config) lo que
    /// hace falta para el modo kiosco de la tablet -- Fase A, ver
    /// docs/tablet.md "Decisiones" y docs/builds-deploy.md "Provisión de tablets
    /// (Device Owner)":
    ///   1. un segundo &lt;intent-filter&gt; MAIN+HOME+DEFAULT en la Activity de
    ///      Unity (UnityPlayerGameActivity, sin tocar el intent-filter LAUNCHER
    ///      existente) -- requisito para que Device Owner pueda fijar la app
    ///      como preferida de inicio via addPersistentPreferredActivity
    ///      (ver KioskManager.ApplyPolicies).
    ///   2. el &lt;receiver&gt; SimuladorDeviceAdminReceiver (Assets/Plugins/
    ///      Android/com/simulador/kiosk/, ver ese .java) con su meta-data de
    ///      policies (@xml/device_admin, en SimuladorUpdate.androidlib -- mismo
    ///      androidlib que file_paths.xml) y el intent-filter de
    ///      DEVICE_ADMIN_ENABLED/PROFILE_PROVISIONING_COMPLETE.
    ///   3. el &lt;receiver&gt; InstallResultReceiver (Fase C, updates
    ///      silenciosos -- mismo paquete, ver ese .java y
    ///      Simulador.Update.UpdateInstaller): sin permiso especial, solo
    ///      exported="false" (recibe el PendingIntent del commit de
    ///      PackageInstaller que lanza SilentInstaller.java, siempre dentro
    ///      del propio proceso).
    ///
    /// SOLO edita el manifest YA MERGEADO por Unity en el proyecto Gradle
    /// generado -- Assets/Plugins/Android/AndroidManifest.xml (la fuente,
    /// compartida con el visor) NO se toca: agregarle algo de kiosco ahi
    /// afectaria tambien al visor (ver ese archivo y el gotcha "Manifest custom
    /// incompleto rompe el merge del launcher" en docs/builds-deploy.md).
    ///
    /// Gateado por TabletBuild.IsTabletBuildInProgress, igual que
    /// TabletBootConfigPatcher: en un build del visor (sin kiosco, loader OpenXR
    /// ON a proposito) el flag esta en false y este patcher no toca nada.
    ///
    /// callbackOrder 9998 (no 9999, el de TabletBootConfigPatcher): no hay un
    /// orden real que respetar ENTRE ambos patchers -- editan archivos DISTINTOS
    /// del proyecto Gradle generado (AndroidManifest.xml vs boot.config), sin
    /// dependencia mutua entre si. Cualquier valor alto (para correr despues del
    /// merge de manifest que hace el propio Unity) es equivalente; se eligio
    /// 9998 simplemente para no pisar el 9999 ya usado.
    /// </summary>
    internal class TabletManifestPatcher : IPostGenerateGradleAndroidProject
    {
        public int callbackOrder => 9998;

        const string ManifestRelativePath = "src/main/AndroidManifest.xml";
        const string UnityActivityName = "com.unity3d.player.UnityPlayerGameActivity";
        const string ReceiverName = "com.simulador.kiosk.SimuladorDeviceAdminReceiver";
        const string InstallResultReceiverName = "com.simulador.kiosk.InstallResultReceiver";
        const string AndroidNs = "http://schemas.android.com/apk/res/android";

        public void OnPostGenerateGradleAndroidProject(string path)
        {
            // SIM: atajo deliberado -- return inmediato e inofensivo si no es un
            // build de tablet (el visor no lleva nada de kiosco en su manifest).
            if (!TabletBuild.IsTabletBuildInProgress)
                return;

            // Mismo layout alternativo que TabletBootConfigPatcher: el "path"
            // recibido suele ser el del modulo unityLibrary; si llega el del
            // modulo launcher (hermano de unityLibrary), probar ese layout antes
            // de rendirse.
            string manifestPath = Path.Combine(path, ManifestRelativePath);
            string alternativePath = Path.Combine(path, "..", "unityLibrary", ManifestRelativePath);
            if (!File.Exists(manifestPath) && File.Exists(alternativePath))
                manifestPath = alternativePath;

            if (!File.Exists(manifestPath))
            {
                Debug.LogError("[TabletBuild] No se encontro AndroidManifest.xml para inyectar el kiosco " +
                               $"(probado '{Path.Combine(path, ManifestRelativePath)}' y '{alternativePath}'). " +
                               "El modo kiosco (Device Owner) va a fallar en esta build -- ver docs/tablet.md.");
                return;
            }

            XDocument doc;
            try
            {
                doc = XDocument.Load(manifestPath);
            }
            catch (Exception e)
            {
                Debug.LogError($"[TabletBuild] No se pudo parsear '{manifestPath}' ({e.GetType().Name}: {e.Message}). " +
                               "El modo kiosco (Device Owner) va a fallar en esta build.");
                return;
            }

            XNamespace android = AndroidNs;
            var applicationEl = doc.Root?.Element("application");
            var activityEl = applicationEl?.Elements("activity")
                .FirstOrDefault(a => (string)a.Attribute(android + "name") == UnityActivityName);

            if (applicationEl == null || activityEl == null)
            {
                Debug.LogError($"[TabletBuild] No se encontro <application>/<activity android:name=\"{UnityActivityName}\"> " +
                               $"en '{manifestPath}'. El modo kiosco (Device Owner) va a fallar en esta build.");
                return;
            }

            bool changed = InjectHomeIntentFilter(activityEl, android);
            changed |= InjectDeviceAdminReceiver(applicationEl, android);
            changed |= InjectInstallResultReceiver(applicationEl, android);

            if (changed)
            {
                try
                {
                    doc.Save(manifestPath);
                }
                catch (Exception e)
                {
                    Debug.LogError($"[TabletBuild] No se pudo guardar '{manifestPath}' ({e.GetType().Name}: {e.Message}). " +
                                   "El modo kiosco (Device Owner) va a fallar en esta build.");
                    return;
                }
                Debug.Log($"[TabletBuild] Manifest de kiosco inyectado (HOME intent-filter + DeviceAdminReceiver + InstallResultReceiver) en '{manifestPath}'.");
            }
            else
            {
                Debug.Log("[TabletBuild] Manifest de kiosco: ya estaba inyectado, nada que hacer (idempotente).");
            }
        }

        // Segundo <intent-filter> MAIN+HOME+DEFAULT en la Activity de Unity, sin
        // tocar el intent-filter LAUNCHER existente. Idempotente: si ya hay un
        // intent-filter con category HOME, no duplica.
        static bool InjectHomeIntentFilter(XElement activityEl, XNamespace android)
        {
            bool alreadyHome = activityEl.Elements("intent-filter")
                .Any(f => f.Elements("category").Any(c => (string)c.Attribute(android + "name") == "android.intent.category.HOME"));
            if (alreadyHome)
                return false;

            activityEl.Add(new XElement("intent-filter",
                new XElement("action", new XAttribute(android + "name", "android.intent.action.MAIN")),
                new XElement("category", new XAttribute(android + "name", "android.intent.category.HOME")),
                new XElement("category", new XAttribute(android + "name", "android.intent.category.DEFAULT"))));
            return true;
        }

        // <receiver> del Device Admin, hermano de <activity> dentro de
        // <application>. Idempotente: si ya existe un receiver con ese
        // android:name, no duplica.
        static bool InjectDeviceAdminReceiver(XElement applicationEl, XNamespace android)
        {
            bool alreadyPresent = applicationEl.Elements("receiver")
                .Any(r => (string)r.Attribute(android + "name") == ReceiverName);
            if (alreadyPresent)
                return false;

            applicationEl.Add(new XElement("receiver",
                new XAttribute(android + "name", ReceiverName),
                new XAttribute(android + "permission", "android.permission.BIND_DEVICE_ADMIN"),
                new XAttribute(android + "exported", "true"),
                new XElement("meta-data",
                    new XAttribute(android + "name", "android.app.device_admin"),
                    new XAttribute(android + "resource", "@xml/device_admin")),
                new XElement("intent-filter",
                    new XElement("action", new XAttribute(android + "name", "android.app.action.DEVICE_ADMIN_ENABLED")),
                    new XElement("action", new XAttribute(android + "name", "android.app.action.PROFILE_PROVISIONING_COMPLETE")))));
            return true;
        }

        // <receiver> del resultado de instalacion silenciosa (Fase C, ver
        // SilentInstaller.java/InstallResultReceiver.java), hermano de
        // <activity> dentro de <application>. Sin permiso especial (a
        // diferencia del DeviceAdminReceiver de arriba): el PendingIntent que
        // lo dispara sale con la identidad de esta misma app, nunca cruza
        // procesos. Idempotente: si ya existe un receiver con ese
        // android:name, no duplica.
        static bool InjectInstallResultReceiver(XElement applicationEl, XNamespace android)
        {
            bool alreadyPresent = applicationEl.Elements("receiver")
                .Any(r => (string)r.Attribute(android + "name") == InstallResultReceiverName);
            if (alreadyPresent)
                return false;

            applicationEl.Add(new XElement("receiver",
                new XAttribute(android + "name", InstallResultReceiverName),
                new XAttribute(android + "exported", "false")));
            return true;
        }
    }
}
