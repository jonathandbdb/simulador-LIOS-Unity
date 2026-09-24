package com.simulador.kiosk;

import android.app.Activity;
import android.app.admin.DevicePolicyManager;
import android.content.Intent;
import android.os.Bundle;
import android.os.PersistableBundle;
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
 *
 * Telemetria (ver ProvisioningTelemetry): esta es la PRIMERA etapa de
 * provisioning que corre codigo nuestro -- si el POST "prov_get_mode" nunca
 * llega al backend, el asistente se cayo ANTES de invocarnos (por ejemplo,
 * durante la descarga/verificacion del APK).
 */
public class ProvisioningModeActivity extends Activity {

    private static final String TAG = "SimuladorProvMode";

    @Override
    protected void onCreate(Bundle savedInstanceState) {
        super.onCreate(savedInstanceState);

        Intent intent = getIntent();
        PersistableBundle adminExtras = ProvisioningTelemetry.extractAdminExtras(intent);
        StringBuilder detail = new StringBuilder();
        Bundle extras = intent.getExtras();
        detail.append("extras=").append(extras != null ? extras.keySet() : "none");
        // API 31+ (Android S); en API 29/30 el extra simplemente no viene, getIntArray
        // devuelve null y no se agrega nada -- ver docs/builds-deploy.md por que se usa
        // el literal en vez de DevicePolicyManager.EXTRA_PROVISIONING_ALLOWED_PROVISIONING_MODES.
        int[] allowedModes = extras != null
                ? extras.getIntArray("android.app.extra.PROVISIONING_ALLOWED_PROVISIONING_MODES")
                : null;
        if (allowedModes != null) {
            detail.append(" allowedModes=").append(java.util.Arrays.toString(allowedModes));
        }
        ProvisioningTelemetry.send(this, adminExtras, "prov_get_mode", detail.toString());

        Intent result = new Intent();
        result.putExtra(DevicePolicyManager.EXTRA_PROVISIONING_MODE,
                DevicePolicyManager.PROVISIONING_MODE_FULLY_MANAGED_DEVICE);
        setResult(RESULT_OK, result);
        Log.i(TAG, "GET_PROVISIONING_MODE respondido: PROVISIONING_MODE_FULLY_MANAGED_DEVICE.");
        finish();
    }
}
