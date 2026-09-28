package com.simulador.deck;

import android.app.Activity;
import android.graphics.Color;
import android.graphics.drawable.GradientDrawable;
import android.os.Bundle;
import android.view.Gravity;
import android.view.KeyEvent;
import android.view.View;
import android.view.WindowManager;
import android.webkit.WebSettings;
import android.webkit.WebView;
import android.widget.FrameLayout;
import android.widget.TextView;

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

        // El modo inmersivo oculta la nav bar del sistema -- sin nav bar el
        // unico cierre es KEYCODE_BACK, invisible para el vendedor. Se
        // superpone un boton "cerrar" nativo (por encima del WebView, en un
        // FrameLayout) que llama al mismo finish() del back key.
        FrameLayout root = new FrameLayout(this);
        root.addView(webView, new FrameLayout.LayoutParams(
                FrameLayout.LayoutParams.MATCH_PARENT,
                FrameLayout.LayoutParams.MATCH_PARENT));
        root.addView(buildCloseButton(), buildCloseButtonParams());

        setContentView(root);
        webView.loadUrl(DECK_ASSET_URL);
    }

    /**
     * Boton "cerrar" nativo: circulo semitransparente oscuro con una "X"
     * blanca, ~48dp de area tactil. Sin AndroidX ni drawables nuevos --
     * el circulo se arma con un GradientDrawable por codigo, mismo criterio
     * "cero dependencias nuevas" que el resto del deck.
     */
    private TextView buildCloseButton() {
        TextView close = new TextView(this);
        // Escape unicode (no el literal): el javac de Gradle puede no compilar en UTF-8.
        close.setText("\u2715");
        close.setTextColor(Color.WHITE);
        close.setTextSize(18);
        close.setGravity(Gravity.CENTER);
        GradientDrawable circle = new GradientDrawable();
        circle.setShape(GradientDrawable.OVAL);
        circle.setColor(Color.parseColor("#80000000"));
        close.setBackground(circle);
        close.setOnClickListener(new View.OnClickListener() {
            @Override
            public void onClick(View v) {
                // Misma ruta que el boton atras: vuelve a la app de Unity.
                finish();
            }
        });
        return close;
    }

    private FrameLayout.LayoutParams buildCloseButtonParams() {
        int sizePx = dp(48);
        int marginPx = dp(16);
        FrameLayout.LayoutParams params = new FrameLayout.LayoutParams(sizePx, sizePx);
        // Arriba-izquierda: el deck (docs/comercial/deck.template.html) dibuja
        // en la barra superior el selector de idioma + contador de slide
        // pegados al borde derecho (#bar > .langs + #count) -- arriba-derecha
        // arriesga tapar el selector de idioma (funcional) en pantallas
        // angostas. Arriba-izquierda solo cubre el wordmark de marca (texto
        // sin handler de click), asi que no rompe ningun control del deck.
        // Los botones prev/next (#nav) quedan centrados abajo, lejos de
        // cualquiera de las dos esquinas superiores.
        params.gravity = Gravity.TOP | Gravity.START;
        params.leftMargin = marginPx;
        params.topMargin = marginPx;
        return params;
    }

    private int dp(int value) {
        return Math.round(value * getResources().getDisplayMetrics().density);
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
