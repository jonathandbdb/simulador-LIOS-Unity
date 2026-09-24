package com.simulador.kiosk;

import android.app.Activity;
import android.app.admin.DevicePolicyManager;
import android.content.Intent;
import android.os.Bundle;
import android.util.Log;

/**
 * Primera de las dos Activities obligatorias del flujo unificado de
 * provisioning de Android 12+ (Android Enterprise QR provisioning, ver
 * docs/builds-deploy.md "Provisión de tablets (Device Owner)" >
 * "Recuperación remota por QR"). El asistente de configuración la invoca con
 * la action android.app.action.GET_PROVISIONING_MODE
 * (DevicePolicyManager.ACTION_GET_PROVISIONING_MODE, API 29 -- verificado
 * contra la referencia oficial, no hace falta literal de fallback: coincide
 * con el minSdk 29 del proyecto) para preguntarle al DPC (este APK) que modo
 * de provisioning quiere. Este proyecto solo soporta un dispositivo
 * completamente administrado (tablet de kiosco, nunca un perfil de trabajo
 * separado), asi que siempre responde PROVISIONING_MODE_FULLY_MANAGED_DEVICE.
 *
 * Sin declarar esta Activity (y su hermana PolicyComplianceActivity, ver ese
 * archivo) el asistente corta con "No se puede configurar el dispositivo" en
 * Android 12+ -- reproducido en campo con el QR real (PHILCO Android 13,
 * Lenovo Android 16).
 *
 * Sin UI: responde el resultado en onCreate() y se cierra sola. Se compila
 * tambien en el visor (Unity compila TODO ".java" suelto bajo Plugins/
 * Android/ en ambos targets, mismo patron que SimuladorDeviceAdminReceiver)
 * pero queda INERTE ahi -- el manifest del visor nunca la declara
 * (TabletManifestPatcher.cs solo la inyecta durante el build de tablet), asi
 * que Android nunca la invoca en el visor.
 */
public class ProvisioningModeActivity extends Activity {

    private static final String TAG = "SimuladorProvMode";

    @Override
    protected void onCreate(Bundle savedInstanceState) {
        super.onCreate(savedInstanceState);

        Intent result = new Intent();
        result.putExtra(DevicePolicyManager.EXTRA_PROVISIONING_MODE,
                DevicePolicyManager.PROVISIONING_MODE_FULLY_MANAGED_DEVICE);
        setResult(RESULT_OK, result);
        Log.i(TAG, "GET_PROVISIONING_MODE respondido: PROVISIONING_MODE_FULLY_MANAGED_DEVICE.");
        finish();
    }
}
