using System;
using System.Collections;
using System.Text;
using UnityEngine;
using UnityEngine.Networking;

namespace Simulador.Data
{
    /// <summary>
    /// Helper compartido para POSTs fire-and-forget de telemetria/log al backend
    /// (mismo patron que ya usaba <c>UpdateManager.SendTelemetryAsync</c>: JSON via
    /// <see cref="UploadHandlerRaw"/>, timeout corto, degradacion sin excepcion --
    /// nunca bloquea ni propaga el fallo del caller). Extraido aca para que
    /// <c>Simulador.Update.UpdateManager</c> y <c>Simulador.License.LicenseManager</c>
    /// compartan el mismo cuerpo en vez de duplicarlo (ver docs/updates.md /
    /// docs/licenciamiento.md).
    /// </summary>
    public static class BackendTelemetry
    {
        private const int DefaultTimeoutSeconds = 5;

        /// <summary>
        /// POST de <paramref name="json"/> a <paramref name="url"/> con
        /// <c>Content-Type: application/json</c>. Nunca tira excepcion ni deja
        /// nada pendiente: un fallo de red (excepcion sincrona de
        /// <see cref="UnityWebRequest.SendWebRequest"/> o <c>result != Success</c>)
        /// solo se loguea con el prefijo dado (<paramref name="logPrefix"/> + "no se
        /// pudo enviar"/"fallo al enviar", mismo texto que el caller ya logueaba).
        /// </summary>
        public static IEnumerator PostJson(string url, string json, string logPrefix, int timeoutSeconds = DefaultTimeoutSeconds)
        {
            byte[] bodyRaw = Encoding.UTF8.GetBytes(json ?? string.Empty);

            using var req = new UnityWebRequest(url, UnityWebRequest.kHttpVerbPOST);
            req.uploadHandler = new UploadHandlerRaw(bodyRaw);
            req.downloadHandler = new DownloadHandlerBuffer();
            req.SetRequestHeader("Content-Type", "application/json");
            req.timeout = timeoutSeconds;

            UnityWebRequestAsyncOperation op = null;
            try { op = req.SendWebRequest(); }
            catch (Exception e)
            {
                Debug.Log($"{logPrefix} no se pudo enviar ({e.GetType().Name}).");
                yield break;
            }
            yield return op;

            if (req.result != UnityWebRequest.Result.Success)
                Debug.Log($"{logPrefix} fallo al enviar ({req.result}).");
        }
    }
}
