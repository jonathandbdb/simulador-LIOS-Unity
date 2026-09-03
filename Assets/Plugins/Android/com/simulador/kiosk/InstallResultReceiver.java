package com.simulador.kiosk;

import android.content.BroadcastReceiver;
import android.content.Context;
import android.content.Intent;
import android.content.pm.PackageInstaller;
import android.util.Log;

/**
 * Recibe el resultado del commit de PackageInstaller lanzado por
 * SilentInstaller (Fase C, updates silenciosos en kiosco -- ver
 * docs/updates.md). NO puede ser SimuladorDeviceAdminReceiver como destino
 * del PendingIntent: ese receiver declara
 * android:permission="BIND_DEVICE_ADMIN", permiso que el SISTEMA exige del
 * EMISOR del broadcast -- el commit de PackageInstaller lo emite con la
 * identidad de esta app, que no tiene ese permiso, asi que el broadcast se
 * descartaria en silencio. Por eso este receiver es propio, sin permiso
 * especial, solo `exported="false"` (ver TabletManifestPatcher.cs).
 */
public class InstallResultReceiver extends BroadcastReceiver {

    private static final String TAG = "SimuladorKiosk";

    @Override
    public void onReceive(Context context, Intent intent) {
        int status = intent.getIntExtra(PackageInstaller.EXTRA_STATUS, PackageInstaller.STATUS_FAILURE);

        switch (status) {
            case PackageInstaller.STATUS_PENDING_USER_ACTION:
                // Red de seguridad: no deberia pasar siendo Device Owner
                // (SilentInstaller ya pide USER_ACTION_NOT_REQUIRED desde API
                // 31, y en 29/30 el Device Owner instala sin confirmacion),
                // pero si el sistema igual la exige, hay que reenviar el
                // Intent que trae para no dejar la instalacion colgada.
                // getParcelableExtra(String) esta deprecado desde API 33 (existe
                // la variante tipada getParcelableExtra(String, Class<T>)) --
                // aceptable con minSdk 29 (esta variante no existe todavia ahi).
                Intent confirmIntent = intent.getParcelableExtra(Intent.EXTRA_INTENT);
                if (confirmIntent != null) {
                    confirmIntent.addFlags(Intent.FLAG_ACTIVITY_NEW_TASK);
                    context.startActivity(confirmIntent);
                    Log.w(TAG, "Instalacion silenciosa exigio confirmacion del usuario (inesperado en Device Owner).");
                } else {
                    Log.e(TAG, "STATUS_PENDING_USER_ACTION sin EXTRA_INTENT -- no se pudo continuar la instalacion.");
                }
                break;
            case PackageInstaller.STATUS_SUCCESS:
                // El proceso muere aca (Android reemplaza el APK en caliente).
                // La HOME persistente (KioskManager.ApplyPolicies) relanza la
                // app sola; UpdateManager.CheckPendingUpdateMarker reporta
                // update_success al arrancar leyendo update_pending.json, que
                // UpdateInstaller ya escribio ANTES de este commit -- no hace
                // falta otro marker desde este receiver.
                Log.i(TAG, "Instalacion silenciosa OK.");
                break;
            default:
                String message = intent.getStringExtra(PackageInstaller.EXTRA_STATUS_MESSAGE);
                Log.e(TAG, "Instalacion silenciosa fallo, status=" + status + " mensaje=" + message);
                // CRITICO #2c (correcciones, ver docs/updates.md): sin esto, el
                // lado C# (TabletController) nunca se enteraba de un fallo
                // asincronico (INSTALL_FAILED_* por firma distinta, downgrade,
                // sin espacio -- el caso REAL de campo) y el modal "Instalando..."
                // quedaba puesto para siempre. TabletApp es el GameObject raiz de
                // Tablet.unity, que lleva TabletController (ver docs/tablet.md).
                // MENOR: este receiver esta declarado en el manifest, asi que Android
                // puede levantarlo con el proceso Unity ya muerto (sin player) --
                // UnitySendMessage no tendria a quien avisar. No alcanzable en el
                // flujo normal (el commit llega mientras la app sigue en foreground),
                // pero es la razon de ser del watchdog de 120s del lado C#.
                com.unity3d.player.UnityPlayer.UnitySendMessage("TabletApp", "OnSilentInstallResult",
                        status + "|" + (message == null ? "" : message));
                break;
        }
    }
}
