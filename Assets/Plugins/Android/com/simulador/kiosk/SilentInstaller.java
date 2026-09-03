package com.simulador.kiosk;

import android.app.PendingIntent;
import android.content.Context;
import android.content.Intent;
import android.content.pm.PackageInstaller;
import android.os.Build;
import android.util.Log;

import java.io.File;
import java.io.FileInputStream;
import java.io.IOException;
import java.io.InputStream;
import java.io.OutputStream;

/**
 * Fase C: instalacion TOTALMENTE silenciosa del APK de actualizacion en una
 * tablet provisionada como Android Device Owner (ver docs/tablet.md
 * "Decisiones" y docs/updates.md). A diferencia de UpdateInstaller (intent
 * ACTION_VIEW, que abre el instalador visible de Android y pide confirmacion),
 * esta clase usa PackageInstaller directo: siendo Device Owner, Android
 * concede el permiso INSTALL_PACKAGES implicito y permite commitear una
 * sesion sin ningun dialogo -- necesario porque las tablets se venden a
 * clinicas de otros paises y nadie las vuelve a tocar.
 *
 * Llamada desde Simulador.Update.UpdateInstaller (C#) via AndroidJavaClass/
 * AndroidJavaObject (JNI, nunca reflection de .NET -- IL2CPP-safe).
 */
public class SilentInstaller {

    private static final String TAG = "SimuladorKiosk";
    private static final int BUFFER_SIZE = 64 * 1024; // 64 KB por bloque

    /**
     * Crea una sesion de PackageInstaller, copia el APK adentro y la commitea.
     * Cualquier fallo (IO, PackageInstaller rechazando la sesion, etc.) se
     * propaga como IOException -- el lado C# (UpdateInstaller) lo captura y
     * reporta via telemetria, sin caer al intent ACTION_VIEW visible.
     */
    public static void install(Context context, String apkPath) throws IOException {
        File apkFile = new File(apkPath);

        PackageInstaller packageInstaller = context.getPackageManager().getPackageInstaller();
        PackageInstaller.SessionParams params =
                new PackageInstaller.SessionParams(PackageInstaller.SessionParams.MODE_FULL_INSTALL);

        // minSdk del proyecto es 29: en 29/30 el Device Owner ya instala sin
        // confirmacion del usuario por defecto. USER_ACTION_NOT_REQUIRED recien
        // existe desde API 31 (S) -- ahi hay que pedirlo explicitamente o
        // PackageInstaller puede exigir confirmacion igual.
        if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.S) {
            params.setRequireUserAction(PackageInstaller.SessionParams.USER_ACTION_NOT_REQUIRED);
        }

        int sessionId = packageInstaller.createSession(params);
        PackageInstaller.Session session = packageInstaller.openSession(sessionId);
        try {
            try (OutputStream out = session.openWrite("simulador-update.apk", 0, apkFile.length());
                 InputStream in = new FileInputStream(apkFile)) {
                byte[] buffer = new byte[BUFFER_SIZE];
                int read;
                while ((read = in.read(buffer)) != -1) {
                    out.write(buffer, 0, read);
                }
                session.fsync(out);
            }

            Intent resultIntent = new Intent(context, InstallResultReceiver.class);
            resultIntent.setAction("com.simulador.kiosk.INSTALL_RESULT");

            int flags = PendingIntent.FLAG_UPDATE_CURRENT;
            if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.S) {
                // Gotcha real: desde Android 12 el PendingIntent de un commit de
                // PackageInstaller DEBE ser MUTABLE -- el sistema le agrega los
                // extras de estado (EXTRA_STATUS, EXTRA_STATUS_MESSAGE, etc.)
                // antes de entregarlo. Con FLAG_IMMUTABLE la instalacion falla
                // en silencio (el broadcast nunca llega con esos extras). Es
                // seguro dejarlo MUTABLE porque el Intent es EXPLICITO (apunta
                // a la clase InstallResultReceiver de este mismo paquete), asi
                // que ninguna otra app puede secuestrarlo.
                flags |= PendingIntent.FLAG_MUTABLE;
            }

            PendingIntent pendingIntent = PendingIntent.getBroadcast(context, 0, resultIntent, flags);
            session.commit(pendingIntent.getIntentSender());
            Log.i(TAG, "Sesion de instalacion silenciosa commiteada: " + apkPath);
        } catch (Throwable t) {
            // MAYOR #5 (correcciones): sin abandon() explicito, una sesion fallida
            // (con el APK ya copiado adentro) queda "staged" en el sistema para
            // siempre -- PackageInstaller no las limpia solo, y en una tablet
            // Device Owner que nadie toca eso se acumula sesion tras sesion en
            // cada intento de update fallido. close() del finally de abajo NO
            // alcanza: cierra el handle pero no abandona la sesion "committed
            // pendiente"/a medio escribir.
            // MENOR (correcciones, revision final): abandon() en su propio
            // try/catch -- si tambien tira, no debe enmascarar la excepcion
            // original 't' (la que viaja a telemetria como
            // silent_install_failed: <Tipo>).
            try {
                session.abandon();
            } catch (Throwable ignored) {
                Log.w(TAG, "session.abandon() fallo tras el error original: " + ignored);
            }
            throw t;
        } finally {
            session.close();
        }
    }
}
