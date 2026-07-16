using System;
using System.Collections;
using System.Text;
using UnityEngine;
using UnityEngine.Networking;

namespace Simulador.Data
{
    /// <summary>
    /// Cliente HTTP del CRUD de lentes custom (P7, <c>/api/lenses/custom</c> del
    /// backend -- ver docs/backend.md). Lo consume el VISOR (NetworkController)
    /// cuando la tablet manda create_lens/update_lens/delete_lens por WebSocket:
    /// el visor es quien conoce su device_id y quien habla con el backend.
    ///
    /// Mismo molde que <see cref="BackendTelemetry"/> (UnityWebRequest + coroutine,
    /// nunca tira excepcion), pero con resultado via callback (codigo HTTP + body)
    /// porque aca el caller SI necesita la respuesta (lens_id asignado, reason del
    /// rechazo) para contestarle a la tablet. responseCode 0 = backend inalcanzable
    /// (timeout/DNS/conexion rechazada), mismo criterio que LicenseManager.Verify.
    /// </summary>
    public static class CustomLensClient
    {
        private const int TimeoutSeconds = 8;

        /// <summary>POST /api/lenses/custom — crear lente (privada o generica).</summary>
        public static IEnumerator Create(string backendUrl, string bodyJson, Action<long, string> onDone)
            => Send(UnityWebRequest.kHttpVerbPOST,
                DataManagerLogic.BuildSyncUrl(backendUrl, "/api/lenses/custom"), bodyJson, onDone);

        /// <summary>PUT /api/lenses/custom/{lensId} — editar lente existente.</summary>
        public static IEnumerator Update(string backendUrl, string lensId, string bodyJson, Action<long, string> onDone)
            => Send(UnityWebRequest.kHttpVerbPUT,
                DataManagerLogic.BuildSyncUrl(backendUrl, "/api/lenses/custom/" + Uri.EscapeDataString(lensId ?? "")), bodyJson, onDone);

        /// <summary>DELETE /api/lenses/custom/{lensId}?device_id= — borrar lente.</summary>
        public static IEnumerator Delete(string backendUrl, string lensId, string deviceId, Action<long, string> onDone)
            => Send(UnityWebRequest.kHttpVerbDELETE,
                DataManagerLogic.BuildSyncUrl(backendUrl, "/api/lenses/custom/" + Uri.EscapeDataString(lensId ?? ""), deviceId),
                null, onDone);

        private static IEnumerator Send(string verb, string url, string bodyJson, Action<long, string> onDone)
        {
            using var req = new UnityWebRequest(url, verb);
            if (bodyJson != null)
            {
                req.uploadHandler = new UploadHandlerRaw(Encoding.UTF8.GetBytes(bodyJson));
                req.SetRequestHeader("Content-Type", "application/json");
            }
            req.downloadHandler = new DownloadHandlerBuffer();
            req.timeout = TimeoutSeconds;

            UnityWebRequestAsyncOperation op = null;
            try { op = req.SendWebRequest(); }
            catch (Exception e)
            {
                Debug.Log($"[Data] CustomLensClient: no se pudo enviar {verb} ({e.GetType().Name}).");
                onDone?.Invoke(0, null);
                yield break;
            }
            yield return op;

            // Igual que LicenseManager: el gate de "inalcanzable" es responseCode==0,
            // no req.result (ProtocolError tambien cubre 403/409 legitimos con body).
            onDone?.Invoke(req.responseCode, req.downloadHandler.text);
        }
    }
}
