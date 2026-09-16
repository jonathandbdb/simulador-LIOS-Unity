# Material comercial — IOLSIMULATOR

Presentación para **vendedores, distribuidores y médicos**: qué es el producto, cómo se usa en el
día a día y qué ventajas ofrece. Trilingüe (ES / EN / 中文 简体) con selector en la propia pieza.

| Archivo | Qué es |
|---------|--------|
| `iol-simulator-deck.html` | **El entregable** para enviar por mail o pendrive. 7 diapositivas, un solo archivo autocontenido (~66 KB). Se abre en cualquier navegador, sin internet ni dependencias. |
| `deck.template.html` | La fuente editable: mismo HTML pero con marcadores `__IMG_*__` en vez de las imágenes. **Acá se edita el texto.** |
| `build-deck.py` | Sustituye los marcadores por las imágenes redimensionadas y embebidas en base64, y escribe **dos copias idénticas** del mismo HTML en la misma corrida (ver "Doble salida" abajo). Hoy solo embebe el logo. |
| `Assets/StreamingAssets/iol-simulator-deck.html` (fuera de esta carpeta) | **Segunda salida de `build-deck.py`**, no de esta carpeta — la copia que viaja dentro del APK de la tablet. Ver "Doble salida" y `docs/tablet.md` (`DeckActivity`/`TabletDeckLauncher`). |

## Doble salida (`build-deck.py`)

Desde que la tablet puede abrir el deck sin salir de la app (botón "Presentación" de
`ConnectScreen`, ver `docs/tablet.md`), `build-deck.py` escribe el HTML generado en **dos
rutas, en la misma corrida**, a partir del mismo `html` en memoria — nunca hay una segunda
pasada de sustitución de marcadores ni una segunda fuente:

1. `docs/comercial/iol-simulator-deck.html` — el entregable de siempre (mail/pendrive).
2. `Assets/StreamingAssets/iol-simulator-deck.html` — la copia que Unity empaqueta dentro del
   APK de la tablet (`StreamingAssets` termina en `assets/` del APK en Android); `DeckActivity`
   (Java) la carga con un `WebView` por `file:///android_asset/iol-simulator-deck.html`, sin
   red. Su `.meta` lo generó el Editor (`AssetDatabase.Refresh()` vía MCP), nunca a mano.

**Nunca editar ninguna de las dos salidas a mano.** Si quedan desincronizadas (por ejemplo,
alguien edita solo una a mano tras un hotfix apurado), la tablet mostraría un deck viejo o
distinto del que se manda por mail sin que nadie lo note — el único camino correcto es editar
`deck.template.html` y correr `python docs/comercial/build-deck.py`, que regenera ambas.

## Cómo se usa

- **Presentar**: abrir `iol-simulator-deck.html`. Flechas ←/→, barra espaciadora o clic para
  avanzar; `Home`/`End` para ir al principio o al final. El selector de idioma está arriba a la
  derecha y conmuta la presentación entera sin recargar.
- **Desde la tablet, sin salir de la app**: botón "Presentación" en la pantalla de conexión de
  la app (`ConnectScreen`, antes de emparejar con un visor) — abre el mismo HTML dentro de un
  `WebView` a pantalla completa, embebido en el APK, sin red. Detalle técnico en `docs/tablet.md`
  (`DeckActivity`/`TabletDeckLauncher`) y "Doble salida" más abajo.
- **Exportar a PDF**: `Ctrl+P` → *Guardar como PDF*, con **gráficos de fondo activados**. Sale una
  diapositiva por página, en 16:9 (338 × 190 mm).
- **Enviar por mail o dejar en un pendrive**: un solo archivo, nada que acompañar.

## Cómo se edita

1. Editar el texto en `deck.template.html`, dentro del objeto `C` (`C.es`, `C.en`, `C.zh`). Las
   tres claves tienen la **misma estructura**: si se agrega un campo, se agrega en los tres
   idiomas o esa diapositiva queda vacía en el idioma que falte.
2. Regenerar:

   ```bash
   python docs/comercial/build-deck.py
   ```

3. Verificar. Sin abrir un navegador a mano, alcanza con Chrome en headless:

   ```bash
   chrome --headless --window-size=1600,900 --screenshot=out.png file:///.../iol-simulator-deck.html
   chrome --headless --no-pdf-header-footer --print-to-pdf=deck.pdf file:///.../iol-simulator-deck.html
   ```

   Chequear sobre todo el **chino** y el **inglés**: son los que cambian más el largo del texto y
   los que pueden desbordar una diapositiva.

## Decisiones

- **El deck es solo texto.** Hubo una versión con pares de capturas del simulador (monofocal vs
  trifocal, de día y de noche) y se sacaron: las comparativas se muestran mejor con el simulador
  en vivo que con una captura en una diapositiva. La única imagen que queda es el logo.
- **Toda imagen va embebida en base64, nunca referenciada.** `capturas/` está gitignoreada: un
  deck que apuntara a rutas relativas se rompería en cualquier clon del repositorio. Para
  reincorporar una comparativa: agregar el par marcador/captura al dict `IMAGES` de
  `build-deck.py`, declarar el marcador en el objeto `IMG` de la plantilla y usarlo en la
  diapositiva. El HTML generado es la única copia durable del material.
- **El disclaimer de fines educativos aparece en tres niveles**: una línea en la portada, una
  micro-línea en el pie de *todas* las diapositivas, y la diapositiva 6 completa con los límites
  del modelo declarados. Ante un oftalmólogo, declarar los límites fortalece la propuesta.
- **Las ventajas van en UNA sola diapositiva** (la 5, seis tarjetas). Hubo una versión con una
  diapositiva por ventaja y era demasiado larga para una reunión de venta: el detalle lo pone el
  vendedor de palabra, el deck solo tiene que dejar los seis títulos a la vista.
- **El visor se nombra de forma genérica** ("visor de realidad virtual"), nunca por marca: la
  plataforma puede cambiar por mercado (p. ej. Pico en China).
- **No se usa el encuadre de "influir en el paciente"**: el ángulo es *decisión informada y
  expectativas alineadas*. Es más defendible ante un médico y es el argumento comercialmente más
  fuerte — el simulador muestra el compromiso completo, beneficios y efectos no deseados.

## Pendiente

- El producto **no tiene ninguna leyenda legal en su UI** (ni el visor, ni la tablet, ni el panel
  admin). El disclaimer de este deck existe solo acá. Conviene evaluar llevarlo también a la
  aplicación — candidatos naturales: la pantalla de conexión de la tablet y el pie del panel.
