package com.simulador.deck;

import android.app.Activity;
import android.os.Bundle;
import android.view.KeyEvent;
import android.view.View;
import android.view.WindowManager;
import android.webkit.WebSettings;
import android.webkit.WebView;

/**
 * Visor a pantalla completa del deck comercial (Assets/StreamingAssets/
 * iol-simulator-deck.html, generado por docs/comercial/build-deck.py -- ver
 * docs/comercial/README.md y docs/tablet.md). StreamingAssets en Android
 * termina empaquetado en assets/ del APK, asi que se carga directo por
 * file:///android_asset/ sin pasar por UnityWebRequest ni por red: el HTML
 * es 100% autocontenido (JS + imagenes en base64).
 *
 * Activity propia (no un panel dentro de la Activity de Unity) porque es la
 * forma mas simple de pausar por completo el render/input de Unity mientras
 * el vendedor muestra el deck -- lanzada por un Intent EXPLICITO desde
 * TabletDeckLauncher.cs (Assets/Scripts/Runtime/Tablet/), mismo molde que
 * KioskManager.OpenSettingsIntent. Declarada SOLO para el build de tablet
 * via TabletManifestPatcher.cs (Unity compila este .java tambien en el
 * visor, por estar bajo Plugins/Android/, pero queda INERTE ahi porque su
 * manifest nunca la declara -- mismo patron que SimuladorDeviceAdminReceiver).
 */
public class DeckActivity extends Activity {

    private static final String DECK_ASSET_URL = "file:///android_asset/iol-simulator-deck.html";

    @Override
    protected void onCreate(Bundle savedInstanceState) {
        super.onCreate(savedInstanceState);

        // Pantalla completa real (sin barra de estado/navegacion): el deck es
        // 16:9 y se muestra apoyado en la mesa frente al cliente, sin chrome
        // de Android alrededor.
        getWindow().setFlags(WindowManager.LayoutParams.FLAG_FULLSCREEN,
                WindowManager.LayoutParams.FLAG_FULLSCREEN);
        hideSystemUi();

        WebView webView = new WebView(this);
        WebSettings settings = webView.getSettings();
        // JavaScript habilitado: el deck se renderiza 100% por JS (slides,
        // selector de idioma, atajos de teclado) -- sin esto se ve una
        // pagina en blanco. No hace falta ningun otro permiso: el HTML no
        // pide red (imagenes embebidas en base64).
        settings.setJavaScriptEnabled(true);
        settings.setLoadWithOverviewMode(true);
        settings.setUseWideViewPort(true);

        setContentView(webView);
        webView.loadUrl(DECK_ASSET_URL);
    }

    @Override
    public void onWindowFocusChanged(boolean hasFocus) {
        super.onWindowFocusChanged(hasFocus);
        // El modo inmersivo se pierde al perder foco (dialogos del sistema,
        // notificaciones); se reaplica cada vez que la ventana lo recupera.
        if (hasFocus) hideSystemUi();
    }

    private void hideSystemUi() {
        getWindow().getDecorView().setSystemUiVisibility(
                View.SYSTEM_UI_FLAG_LAYOUT_STABLE
                        | View.SYSTEM_UI_FLAG_LAYOUT_HIDE_NAVIGATION
                        | View.SYSTEM_UI_FLAG_LAYOUT_FULLSCREEN
                        | View.SYSTEM_UI_FLAG_HIDE_NAVIGATION
                        | View.SYSTEM_UI_FLAG_FULLSCREEN
                        | View.SYSTEM_UI_FLAG_IMMERSIVE_STICKY);
    }

    @Override
    public boolean onKeyDown(int keyCode, KeyEvent event) {
        // Boton atras: cierra la pantalla y vuelve a la app de Unity (que
        // quedo pausada mientras esta Activity estaba en foreground -- ver
        // TabletDeckLauncher.cs).
        if (keyCode == KeyEvent.KEYCODE_BACK) {
            finish();
            return true;
        }
        return super.onKeyDown(keyCode, event);
    }
}
