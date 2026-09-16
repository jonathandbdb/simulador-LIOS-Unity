using System;
using UnityEngine;

namespace Simulador.Tablet
{
    /// <summary>
    /// Lanza la pantalla del deck comercial (Assets/StreamingAssets/
    /// iol-simulator-deck.html, embebido en el APK -- ver docs/comercial/
    /// README.md) desde dentro de la propia app, via un Intent EXPLICITO a
    /// <c>com.simulador.deck.DeckActivity</c> (Assets/Plugins/Android/com/
    /// simulador/deck/DeckActivity.java), declarada SOLO en el manifest de la
    /// tablet por <c>TabletManifestPatcher</c>.
    ///
    /// Vive ACA y no en <see cref="KioskManager"/> a proposito: KioskManager es
    /// sobre el modo kiosco (Device Owner/lock task) y esto no lo es -- abrir
    /// el deck no depende de si la tablet es Device Owner (la Activity
    /// comparte <c>applicationId</c> con la app, asi que ya cae dentro del
    /// allowlist de <c>setLockTaskPackages</c> sin tocar nada de eso, ver
    /// docs/tablet.md). Mismo molde que
    /// <see cref="KioskManager.OpenSettingsIntent"/> (AndroidJavaObject +
    /// Intent), pero con un Intent EXPLICITO por nombre de clase (no por
    /// action string, como hace ese metodo con "android.settings.*").
    /// </summary>
    public static class TabletDeckLauncher
    {
#if UNITY_ANDROID && !UNITY_EDITOR
        const string DeckActivityClassName = "com.simulador.deck.DeckActivity";

        /// <summary>Abre la pantalla del deck comercial (pausa Unity mientras esta en foreground).</summary>
        public static void OpenDeck()
        {
            try
            {
                using var unityPlayer = new AndroidJavaClass("com.unity3d.player.UnityPlayer");
                using var activity = unityPlayer.GetStatic<AndroidJavaObject>("currentActivity");
                using var intent = new AndroidJavaObject("android.content.Intent");
                // Intent EXPLICITO por nombre de clase: DeckActivity es propia
                // de esta app, no un componente del sistema (a diferencia de
                // KioskManager.OpenSettingsIntent, que usa un action string).
                // setClassName(String,String) devuelve Intent, no void -- mismo
                // gotcha que Intent.setFlags en KioskManager.OpenSettingsIntent
                // (Call(...) sin el generico correcto tira NoSuchMethodError
                // que este catch tragaria en silencio).
                intent.Call<AndroidJavaObject>("setClassName", Application.identifier, DeckActivityClassName);
                activity.Call("startActivity", intent);
                Debug.Log("[Tablet] Deck comercial: abierto.");
            }
            catch (Exception e)
            {
                Debug.LogWarning("[Tablet] No se pudo abrir el deck comercial (" + e.GetType().Name + "): " + e.Message);
            }
        }
#else
        // SIM: atajo deliberado -- fuera de Android (Editor) no hay Activity
        // real que lanzar; no-op logueado para que TabletController pueda
        // llamar esta API sin #if propios (mismo patron que KioskManager).
        public static void OpenDeck() => Debug.Log("[Tablet] OpenDeck no-op fuera de Android (Editor).");
#endif
    }
}
