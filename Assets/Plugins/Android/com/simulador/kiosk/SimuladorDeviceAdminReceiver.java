package com.simulador.kiosk;

import android.app.admin.DeviceAdminReceiver;
import android.content.Context;
import android.content.Intent;
import android.util.Log;

/**
 * Receiver de Device Admin del Simulador de LIOs (tablet, com.simulador.tablet).
 * Es el componente que habilita el modo kiosco (Android Device Owner, ver
 * docs/tablet.md "Decisiones" y docs/builds-deploy.md "Provisión de tablets
 * (Device Owner)"). La Fase A provisiona por cable con
 * `adb shell dpm set-device-owner com.simulador.tablet/com.simulador.kiosk.
 * SimuladorDeviceAdminReceiver` (scripts/provision-tablet.sh) -- ese camino NO
 * pasa por onProfileProvisioningComplete (ese callback es el de QR/NFC
 * provisioning). Se implementa igual acá para que la Fase C (updates
 * silenciosos, aprovisionamiento por QR) no tenga que volver a tocar Java.
 *
 * Unity compila TODO ".java" suelto bajo Plugins/Android/ en AMBOS builds
 * (visor y tablet comparten el mismo target Android; el paquete Java es
 * independiente del applicationId de cada app), asi que esta clase tambien
 * queda compilada dentro del APK del visor -- pero INERTE ahi: el manifest del
 * visor nunca declara el <receiver> (TabletManifestPatcher.cs solo lo inyecta
 * durante el build de tablet, gateado por TabletBuild.IsTabletBuildInProgress),
 * asi que Android nunca instancia este receiver en el visor.
 */
public class SimuladorDeviceAdminReceiver extends DeviceAdminReceiver {

    private static final String TAG = "SimuladorDeviceAdmin";

    @Override
    public void onEnabled(Context context, Intent intent) {
        super.onEnabled(context, intent);
        Log.i(TAG, "Device admin habilitado para " + context.getPackageName());
    }

    /**
     * Camino de QR/NFC provisioning (Fase C -- la Fase A por cable no dispara
     * esto). Cuando Android termina de aprovisionar el dispositivo, lanza la
     * Activity principal del propio paquete para que la tablet arranque
     * directo en la app sin intervencion manual del clinico.
     */
    @Override
    public void onProfileProvisioningComplete(Context context, Intent intent) {
        super.onProfileProvisioningComplete(context, intent);
        Log.i(TAG, "Provisioning completo, lanzando la app.");
        Intent launchIntent = context.getPackageManager().getLaunchIntentForPackage(context.getPackageName());
        if (launchIntent != null) {
            launchIntent.addFlags(Intent.FLAG_ACTIVITY_NEW_TASK);
            context.startActivity(launchIntent);
        } else {
            Log.w(TAG, "No se encontro el launch intent del propio paquete.");
        }
    }
}
