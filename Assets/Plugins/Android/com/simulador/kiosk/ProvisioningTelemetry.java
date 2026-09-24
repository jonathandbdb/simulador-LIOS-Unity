package com.simulador.kiosk;

import android.app.admin.DevicePolicyManager;
import android.content.Context;
import android.content.Intent;
import android.os.BaseBundle;
import android.os.Build;
import android.os.PersistableBundle;
import android.provider.Settings;
import android.util.Log;

import java.io.OutputStream;
import java.net.HttpURLConnection;
import java.net.URL;
import java.nio.charset.StandardCharsets;

/**
 * Telemetria de diagnostico del flujo de provisioning por QR (Android
 * Enterprise, ver docs/tablet.md y docs/builds-deploy.md "Recuperación
 * remota por QR"). Sin adb durante el asistente de configuracion no hay
 * logcat -- esta clase manda un POST best-effort a {backend}/api/log
 * (mismo contrato que usan UpdateManager/LicenseManager del lado C#, ver
 * Assets/Scripts/Runtime/Update/UpdateManager.cs y
 * Assets/Scripts/Runtime/License/LicenseManager.cs: JSON
 * {"device_id": str(1..128), "events": [{"event": str(1..64),
 * "detail": str(&lt;=2048)}]}, sin auth) desde cada etapa Java del
 * provisioning, para ver hasta donde llega un dispositivo que se cae con
 * "Something went wrong" DESPUES de bajar el APK completo.
 *
 * Java puro, sin dependencias nuevas (Newtonsoft/OkHttp no existen en este
 * codigo nativo -- HttpURLConnection + escape de JSON a mano, mismo espiritu
 * que UpdateLogic.SerializeLogBatch del lado C#, pero sin poder compartir
 * codigo entre C# y Java).
 */
public final class ProvisioningTelemetry {

    private static final String TAG = "SimuladorProvTelemetry";

    // SIM: atajo deliberado — telemetría de diagnóstico de provisioning; la URL real
    // viaja en el admin extras bundle del QR (clave "backend_url", cargada por el
    // operador en /admin/provisioning, ver docs/backend.md). Este fallback solo
    // importa si el QR se generó sin esa key o si el asistente no propaga el bundle
    // a la etapa que está llamando (falta el sistema de configuración remota general
    // que resolvería esto sin hardcodear un dominio).
    private static final String FALLBACK_BACKEND_URL = "https://vr.conecta.sh";

    private static final int TIMEOUT_MS = 4000;
    private static final long JOIN_WAIT_MS = 4500;
    private static final int MAX_DETAIL_LEN = 2000; // margen bajo el limite de 2048 del backend

    private ProvisioningTelemetry() {
    }

    /**
     * Extrae el PersistableBundle de EXTRA_PROVISIONING_ADMIN_EXTRAS_BUNDLE del
     * intent de provisioning, con null-safety total. Las cuatro etapas
     * (ProvisioningModeActivity, PolicyComplianceActivity,
     * SimuladorDeviceAdminReceiver.onEnabled/onProfileProvisioningComplete) llaman
     * este mismo helper en vez de repetir el cast. Devuelve null si el intent es
     * null, si el extra no esta presente o si no es del tipo esperado -- send()
     * tolera adminExtras == null (cae al fallback de URL).
     */
    public static PersistableBundle extractAdminExtras(Intent intent) {
        if (intent == null) return null;
        try {
            Object extra = intent.getParcelableExtra(DevicePolicyManager.EXTRA_PROVISIONING_ADMIN_EXTRAS_BUNDLE);
            return (extra instanceof PersistableBundle) ? (PersistableBundle) extra : null;
        } catch (Throwable t) {
            return null;
        }
    }

    /**
     * Manda un evento de telemetria de provisioning. Nunca lanza excepcion hacia
     * afuera (best-effort total, try/catch envolvente): un problema de red NUNCA
     * debe interrumpir el flujo real de provisioning.
     *
     * @param adminExtras bundle de PROVISIONING_ADMIN_EXTRAS_BUNDLE (Bundle o
     *                     PersistableBundle segun quien llame -- ambos heredan
     *                     BaseBundle.getString, evita duplicar el metodo) o null
     *                     si no esta disponible en esta etapa.
     * @param event        nombre corto del evento (prov_get_mode, prov_complete...).
     * @param detail        detalle libre del llamador; se le antepone
     *                      modelo/SDK del dispositivo.
     */
    public static void send(Context ctx, BaseBundle adminExtras, String event, String detail) {
        try {
            sendInternal(ctx, adminExtras, event, detail);
        } catch (Throwable t) {
            Log.w(TAG, "send() fallo de forma inesperada, ignorado", t);
        }
    }

    private static void sendInternal(Context ctx, BaseBundle adminExtras, String event, String detail) {
        final String backendUrl = resolveBackendUrl(adminExtras);
        final String deviceId = "prov-" + safeAndroidId(ctx);
        String fullDetail = "model=" + Build.MODEL + " sdk=" + Build.VERSION.SDK_INT
                + (detail == null || detail.isEmpty() ? "" : " " + detail);
        if (fullDetail.length() > MAX_DETAIL_LEN) {
            fullDetail = fullDetail.substring(0, MAX_DETAIL_LEN) + "...(truncated)";
        }
        final String json = "{\"device_id\":\"" + jsonEscape(deviceId) + "\",\"events\":[{\"event\":\""
                + jsonEscape(event == null ? "" : event) + "\",\"detail\":\"" + jsonEscape(fullDetail) + "\"}]}";
        final String url = backendUrl + "/api/log";

        Thread worker = new Thread(new Runnable() {
            @Override
            public void run() {
                postJson(url, json);
            }
        }, "ProvTelemetry");
        worker.start();
        try {
            // Esperamos a que el POST salga antes de que la Activity/receiver que nos
            // llamo termine su ciclo de vida -- el proceso puede morir apenas retorna
            // onCreate()/onEnabled()/onProfileProvisioningComplete() (goAsync() sería
            // mas correcto en el receiver, pero un join corto alcanza dentro de la
            // ventana de ~10s que tiene un BroadcastReceiver, y evita duplicar el
            // codigo de espera entre Activities y receiver).
            worker.join(JOIN_WAIT_MS);
        } catch (InterruptedException e) {
            Thread.currentThread().interrupt();
        }
    }

    private static void postJson(String urlStr, String json) {
        HttpURLConnection conn = null;
        try {
            URL url = new URL(urlStr);
            conn = (HttpURLConnection) url.openConnection();
            conn.setRequestMethod("POST");
            conn.setConnectTimeout(TIMEOUT_MS);
            conn.setReadTimeout(TIMEOUT_MS);
            conn.setDoOutput(true);
            conn.setRequestProperty("Content-Type", "application/json; charset=utf-8");
            byte[] body = json.getBytes(StandardCharsets.UTF_8);
            conn.setFixedLengthStreamingMode(body.length);
            OutputStream os = conn.getOutputStream();
            os.write(body);
            os.flush();
            os.close();
            int code = conn.getResponseCode();
            Log.i(TAG, "POST " + urlStr + " -> " + code);
        } catch (Throwable t) {
            Log.w(TAG, "POST " + urlStr + " fallo: " + t);
        } finally {
            if (conn != null) {
                conn.disconnect();
            }
        }
    }

    private static String resolveBackendUrl(BaseBundle adminExtras) {
        if (adminExtras != null) {
            try {
                String url = adminExtras.getString("backend_url");
                if (url != null && !url.trim().isEmpty()) {
                    String trimmed = url.trim();
                    // Sin barra final: se concatena "/api/log" tal cual mas abajo.
                    return trimmed.endsWith("/") ? trimmed.substring(0, trimmed.length() - 1) : trimmed;
                }
            } catch (Throwable t) {
                Log.w(TAG, "No se pudo leer backend_url del admin extras bundle", t);
            }
        }
        return FALLBACK_BACKEND_URL;
    }

    private static String safeAndroidId(Context ctx) {
        try {
            String id = Settings.Secure.getString(ctx.getContentResolver(), Settings.Secure.ANDROID_ID);
            return (id == null || id.isEmpty()) ? "unknown" : id;
        } catch (Throwable t) {
            return "unknown";
        }
    }

    /**
     * Escape de JSON a mano (comillas, backslash y caracteres de control) --
     * sin dependencias, mismo motivo que arriba: este codigo Java es nativo,
     * no puede reusar Newtonsoft.Json del lado C# (UpdateLogic.SerializeLogBatch).
     */
    private static String jsonEscape(String s) {
        if (s == null) return "";
        StringBuilder sb = new StringBuilder(s.length() + 16);
        for (int i = 0; i < s.length(); i++) {
            char c = s.charAt(i);
            switch (c) {
                case '"':
                    sb.append("\\\"");
                    break;
                case '\\':
                    sb.append("\\\\");
                    break;
                case '\n':
                    sb.append("\\n");
                    break;
                case '\r':
                    sb.append("\\r");
                    break;
                case '\t':
                    sb.append("\\t");
                    break;
                default:
                    if (c < 0x20) {
                        sb.append(String.format("\\u%04x", (int) c));
                    } else {
                        sb.append(c);
                    }
            }
        }
        return sb.toString();
    }
}
