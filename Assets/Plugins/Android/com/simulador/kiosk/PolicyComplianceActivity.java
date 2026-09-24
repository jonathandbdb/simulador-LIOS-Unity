package com.simulador.kiosk;

import android.app.Activity;
import android.content.Intent;
import android.os.Bundle;
import android.os.PersistableBundle;
import android.util.Log;

/**
 * Segunda de las dos Activities obligatorias del flujo unificado de
 * provisioning de Android 12+ (junto con ProvisioningModeActivity, ver ese
 * archivo y docs/builds-deploy.md "Provisión de tablets (Device Owner)" >
 * "Recuperación remota por QR"). El asistente la invoca con la action
 * android.app.action.ADMIN_POLICY_COMPLIANCE
 * (DevicePolicyManager.ACTION_ADMIN_POLICY_COMPLIANCE, API 29, se dispara
 * DESPUES de ACTION_GET_PROVISIONING_MODE segun la referencia oficial),
 * dandole al DPC la chance de mostrar pantallas propias de cumplimiento de
 * politicas antes de que el asistente termine. Este proyecto no tiene nada
 * que mostrar ahi -- el kiosco se aplica en runtime recien cuando la app
 * arranca de verdad (KioskManager.ApplyPolicies(), ver
 * Assets/Scripts/Runtime/Tablet/KioskManager.cs, llamado desde
 * TabletController.Start()), no durante el asistente.
 *
 * IMPORTANTE -- no lanza la app: SimuladorDeviceAdminReceiver.
 * onProfileProvisioningComplete (ver ese archivo) ya la lanza cuando el
 * asistente termina de cerrar, DESPUES de que esta Activity devuelve su
 * resultado (la propia documentacion de ACTION_ADMIN_POLICY_COMPLIANCE dice
 * que se dispara "before the end of setup wizard"; onProfileProvisioningComplete
 * llega recien cuando el provisioning esta completo de verdad). Si esta
 * Activity tambien lanzara la app, arrancaria dos veces (segundo intent de
 * LAUNCHER en sucesion) con el mismo riesgo de doble instancia de Activity en
 * el mismo proceso que documenta docs/builds-deploy.md en el gotcha "carrera
 * tarea-standard + Home" de la provision por cable -- por eso esta Activity
 * SOLO devuelve RESULT_OK y se cierra, sin tocar ningun Intent de la app.
 *
 * Sin UI. Se compila tambien en el visor (igual que ProvisioningModeActivity)
 * pero queda INERTE ahi -- el manifest del visor nunca la declara.
 *
 * Telemetria (ver ProvisioningTelemetry): si "prov_get_mode" llego al backend
 * pero "prov_policy_compliance" no, el asistente se cayo ENTRE ambas etapas
 * (durante la creacion real del Device Owner / registro de admin activo).
 */
public class PolicyComplianceActivity extends Activity {

    private static final String TAG = "SimuladorPolicyCompl";

    @Override
    protected void onCreate(Bundle savedInstanceState) {
        super.onCreate(savedInstanceState);
        Intent intent = getIntent();
        PersistableBundle adminExtras = ProvisioningTelemetry.extractAdminExtras(intent);
        ProvisioningTelemetry.send(this, adminExtras, "prov_policy_compliance", "");
        Log.i(TAG, "ADMIN_POLICY_COMPLIANCE: sin pantallas propias, OK inmediato (no relanza la app).");
        setResult(RESULT_OK);
        finish();
    }
}
