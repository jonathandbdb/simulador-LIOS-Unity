// VisionPostProcess.shader — Simulacion de visualizacion por IOL (post-proceso URP).
// Port de features/vision_shaders/sprint2_blur_test.gdshader (Godot).
//
// Hace: depth->metros (proyeccion inversa) + blur dioptrico ESFERICO + dispersion
// intraocular de catarata + astigmatismo (cilindro) + perdida de contraste + velo de
// encandilamiento, BIFURCADO por ojo (unity_StereoEyeIndex).
//
// CUATRO passes (etapa C). Los indices 0 y 1 NO se mueven: los referencia
// VisionRendererFeature; los del tier de baja resolucion van al final del SubShader.
//   pass 2 FragLowDown   : source -> _VisionLowA   a 1/16 (box 4x4, 4 taps bilineales)
//   pass 3 FragLowGather : _VisionLowA -> _VisionLowB a 1/16 (espiral de 24 taps, radio
//                          variable por pixel) -> se publica como global _VisionLowBlur
//   pass 0 FragEffect    : source -> _VisionTemp  full-res (disco de 13 taps + tier de baja)
//   pass 1 FragPost      : _VisionTemp -> source  full-res (cilindro + contraste + velos)
// El tier de baja existe porque un kernel de N taps full-res solo produce un desenfoque
// LIMPIO mientras la separacion entre taps sea del orden del pixel; por encima de eso da
// copias fantasma (poliopia), no desenfoque. Ver DiscBlur13 / LowGather.
// Asi el smear astigmatico opera SOBRE la esfera (correctitud optica) sin re-samplear el
// original.
// Halo / starburst los dibujan los billboards de GlareSource (F4). El astigmatismo
// se REFUERZA aca con un desenfoque DIRECCIONAL por ojo (la imagen se borronea a lo
// largo del eje, como el astigmatismo optico real) ademas del trazo sobre las luces;
// lo manejan los globals glare_astig_l/r (0..1) y glare_astig_angle_l/r (rad).
//
// MODELO FISICO DEL RADIO (etapa B, reemplaza el viejo BLUR_RADIUS_PX/MAX_DEFOCUS_D):
// el radio del circulo de desenfoque es una magnitud ANGULAR, no un numero de pixeles:
// beta [rad] = p * errD (geometria del blur circle, p = diametro pupilar en m,
// errD en dioptrias), radio = beta/2. Se convierte a pixeles con _VisionPxPerDeg
// (px/grado por ojo, lo publica VisionRendererFeature desde la matriz de proyeccion):
// sin eso el mismo efecto se veia distinto en el Game View (~7 px/grado) que en Quest 3
// (~17 px/grado). desenfoque_max pasa a ser MULTIPLICADOR del radio fisico (1 = optica
// real, >1 exagera, 0 = nunca borroso) y NO hay mas mezcla con la imagen nitida salvo
// en el regimen sub-pixel (ver FragEffect).
//
// Multiview (Single Pass Instanced / Vulkan): se samplea SIEMPRE con las macros
// _X (SAMPLE_TEXTURE2D_X) y SampleSceneDepth, que indexan el slice del ojo correcto.
// NUNCA samplear _CameraDepthTexture plano: en el ojo derecho devuelve el depth del
// izquierdo (bug Vulkan+multiview).
Shader "Simulador/VisionPostProcess"
{
    SubShader
    {
        Tags { "RenderPipeline" = "UniversalPipeline" }
        ZWrite Off ZTest Always Cull Off

        HLSLINCLUDE
        #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
        #include "Packages/com.unity.render-pipelines.core/Runtime/Utilities/Blit.hlsl"
        #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/DeclareDepthTexture.hlsl"

        // === Modelo de focos por ojo (metros). Foco = 0 => no usado ===
        float _FocoLejosL, _FocoIntermedioL, _FocoCercaL;
        float _FocoLejosR, _FocoIntermedioR, _FocoCercaR;
        float _ProfundidadFocoL, _ProfundidadFocoR;   // ancho de zona nitida (m)
        float _DesenfoqueMaxL, _DesenfoqueMaxR;        // multiplicador del radio fisico (0 = nunca borroso)
        float _ContrastLossL, _ContrastLossR;          // 0..0.6
        float _CataractL, _CataractR;                  // 0..1 tinte amarillo de catarata (transmitancia)
        float _CataractScatterL, _CataractScatterR;    // 0..1 dispersion intraocular (catarata)

        // === Libro en la mano (Sprint 10 / F5). 0 => sin libro (no-op). ===
        float _BookDistanceM;
        float2 _BookScreenUV;
        float _BookScreenRadius;

        // === Astigmatismo POR OJO (lo setea GlareController.SetAstigmatism via
        // Shader.SetGlobalFloat). glare_astig_l/r 0..1 = magnitud; angle en radianes. ===
        float glare_astig_l, glare_astig_r;
        float glare_astig_angle_l, glare_astig_angle_r;

        // === Override de ojo para el stream de la tablet (camara mono). La setea
        // StreamingCapture: 0 = normal (usa unity_StereoEyeIndex), 1 = forzar izq,
        // 2 = forzar der. Default 0 => NO afecta el render de los ojos XR. ===
        float _StreamForceEye;

        // === Pupila por escenario (la setea ScenarioManager). 0 = dia (pupila chica),
        // 1 = noche (dilatada). De noche el circulo de desenfoque crece => mas blur en
        // lo DESENFOCADO (no toca lo enfocado). Default 0 = sin efecto. ===
        float _PupilScene;

        // === Encandilamiento (disability glare / straylight). Lo setea DisabilityGlare
        // Controller: velo de luminancia POR OJO (la difractiva dispersa mas) calculado
        // como straylight(lente) x fuentes(brillo/angulo²) x pupila. _GlareVeilL/R 0..1;
        // _GlareVeilUV.xy = posicion en pantalla de la fuente dominante (para concentrar
        // el velo cerca de la luz); _GlareVeilTint = color calido. Default 0 = sin efecto. ===
        float _GlareVeilL, _GlareVeilR;
        float4 _GlareVeilUV;
        float4 _GlareVeilTint;

        // === Pixeles por GRADO del render target, POR OJO (x = izq, y = der). Lo publica
        // VisionRendererFeature (pass "VisionPublishGlobals") desde la matriz de proyeccion del
        // ojo y el alto del cameraTargetDescriptor. El desenfoque se calcula en GRADOS (magnitud
        // angular, propiedad del ojo) y se convierte a pixeles aca: sin esto el efecto se ve
        // distinto en el Game View chico que en Quest 3. Default (0,0) => radio 0 (nitido). ===
        float2 _VisionPxPerDeg;

        // === Tier de baja resolucion (1/16 = 1/4 por eje). xy = TEXEL de la textura de baja
        // (1/ancho, 1/alto); zw = RELACION REAL baja/full por eje (ancho_baja/ancho_full,
        // alto_baja/alto_full) — real y no "1/4 asumido" porque la division entera del
        // descriptor puede no ser exacta con resoluciones impares.
        // OJO (error silencioso de 4x): NO usar _ScreenSize.zw como texel de la textura de
        // baja ni .xy para su tamano — _ScreenSize es un global de CAMARA (resolucion FULL por
        // ojo) y sigue valiendo lo mismo dentro de los passes que dibujan a 1/16. ===
        float4 _VisionLowTexel;

        // === Salida del tier de baja (pass 3), publicada como global de RenderGraph con
        // SetGlobalTextureAfterPass. La lee el pass 0 para radios grandes. ===
        TEXTURE2D_X(_VisionLowBlur);

        // === Constantes ===
        // Sobreviven del original: DOF_M_TO_D, y CONTRAST_PIVOT (que ahora es el PAR
        // CONTRAST_PIVOT_DAY/NIGHT: el pivote es el nivel de adaptacion del campo, no una
        // constante — ver su bloque abajo).
        // ELIMINADAS: BLUR_RADIUS_PX (7 px) y MAX_DEFOCUS_D (1.5 D) — el radio ahora es
        // fisico/angular (ver cabecera). MAX_DEFOCUS_D saturaba el blur a 1.5 D de error, o
        // sea que de ~60 cm hacia adentro el desenfoque era CONSTANTE (bug reportado).
        #define DOF_M_TO_D      0.5    // mapea profundidad_foco_m a tolerancia (D)
        // PIVOTE DE LA COMPRESION DE CONTRASTE = NIVEL DE ADAPTACION DEL CAMPO (en LINEAR;
        // el proyecto es Linear + HDR, ver doc viva). Un solo numero fijo NO alcanza: era
        // CONTRAST_PIVOT 0.22 con el comentario "pivote bajo: no levanta los negros", y eso era
        // cierto de DIA y FALSO de noche. En ruta_noche casi todo el frame esta POR DEBAJO de
        // 0.22, asi que el operador dejaba de comprimir desde arriba y EMPUJABA el piso hacia
        // arriba: medido en el recorte del tablero (encuadre `tablero`, ppd 24, params de PROD
        // 0.8.1-clinical.a1), luminancia media del display1 joven 43.8 -> vivity 55.8 (+27%) ->
        // panoptix 66.1 (+51%) -> catarata 104.5 (+139%), y la CABINA entera joven 30.4 ->
        // panoptix 58.8 (+93%) -> catarata 102.0 (+235%): niebla gris uniforme sobre todo el
        // campo sin un solo pixel de desenfoque. Clinicamente al reves: una trifocal de noche da
        // halos y MENOS contraste, no bruma. Un pedestal ADITIVO uniforme es, por definicion,
        // VELO (disability glare / straylight) y en este modelo ya lo aportan DOS terminos
        // propios con su propia fisica: el velo CIE de las fuentes [1][2] y el pedestal de
        // scatter del cristalino [13]. contrast_loss tiene que ser SOLO perdida de MODULACION.
        //
        // FISIOLOGIA: el contraste se define como modulacion alrededor de la luminancia de
        // ADAPTACION (Weber: el umbral escala con L), y la funcion de sensibilidad al contraste
        // se mide a una luminancia MEDIA fija [15]; perder sensibilidad al contraste comprime la
        // modulacion HACIA la media de adaptacion, no levanta el piso de negros. O sea: el pivote
        // no es una constante del shader, es el nivel de adaptacion del CAMPO.
        //
        // SENAL DE ADAPTACION = _PupilScene (0 = dia/fotopico, 1 = noche/mesopico). No es un
        // atajo: el diametro pupilar ES una funcion monotona de la luminancia de adaptacion del
        // campo [12][14], asi que la MISMA senal que dilata la pupila fija el nivel de adaptacion.
        // Costo cero (uniforme ya publicado por ScenarioManager), nada nuevo en C#, y hereda las
        // taus asimetricas del reflejo fotomotor [9] => la transicion de escenario es un
        // transitorio de adaptacion suave en vez de un salto.
        //
        // ANCLA DE LOS DOS VALORES — MEDIDA, no elegida a ojo: luminancia media LINEAL del campo
        // (BT.709 [6] sobre el frame completo, lente `paciente_joven` = sin efectos, ppd 24):
        //   consultorio dia, encuadre optotipo 4 m ....... 0.30312
        //   consultorio dia, encuadre libro 0.55 m ....... 0.18773
        //   ruta_noche, encuadre frente .................. 0.02331
        //   ruta_noche, encuadre tablero ................. 0.03025
        // El 0.22 historico cae DENTRO del rango diurno [0.188, 0.303] => era, sin saberlo, el
        // nivel de adaptacion del consultorio de dia; de ahi que "funcionara" de dia. El nocturno
        // se fija con el mismo criterio dentro de [0.023, 0.030] => 0.025 (razon dia/noche 8.8x).
        // OJO: el render NO tiene ancla fotometrica absoluta (misma limitacion que el velo), asi
        // que estos numeros son del CAMPO RENDERIZADO, no cd/m2. La razon real fotopico/mesopico
        // seria mayor (sala ~20-100 cd/m2 vs ruta de noche ~1-3 cd/m2 => 10-50x), o sea 8.8x es
        // el extremo CONSERVADOR: el pivote nocturno queda si acaso alto (levanta un poco los
        // negros) y nunca los hunde.
        // NO TOCAR CONTRAST_PIVOT_DAY: los contrast_loss del catalogo (panoptix 0.1149, vivity
        // 0.0503, catarata 0.5924) estan calibrados contra el optotipo ETDRS de DIA con ese
        // pivote. Interpolar desde el, en vez de reemplazarlo, es lo que deja el dia intacto
        // (pow(x, 0) == 1 exacto => pivote 0.22 EXACTO con _PupilScene = 0) y por eso este fix
        // NO recalibra la escala de contrast_loss del catalogo.
        #define CONTRAST_PIVOT_DAY    0.22
        #define CONTRAST_PIVOT_NIGHT  0.025
        // Largo maximo del smear astigmatico (a magnitud 1), en GRADOS. Era ASTIG_BLUR_PX 22.0
        // y arrastraba el MISMO bug de resolucion que tenia el defocus (el astigmatismo se veia
        // distinto segun el alto del render target). 1.3 grados = los 22 px originales a ppd 17.
        // OJO (hallazgo MENOR de review): esos ppd 17 corresponden a renderScale 1.0, NO a la
        // config real del visor (Mobile_RPAsset tiene renderScale 1.4 => ppd ~24) => en el
        // dispositivo el smear pasa de 22 a ~31 px, o sea el look del astigmatismo SI cambio,
        // ~+40% de largo. Es la consecuencia CORRECTA del fix (el largo es angular y antes estaba
        // sub-especificado), no una regresion; se documenta porque el comentario anterior decia
        // "el look a esa resolucion se preserva" y eso solo vale a renderScale 1.0.
        #define ASTIG_BLUR_DEG  1.3
        // Pupila (diametro, mm) — entra al radio del circulo de desenfoque por la fisica, ya
        // NO por el fudge lerp(1.0, 1.35, _PupilScene) que habia antes. Fotopica ~3 mm a
        // ~100 cd/m2 [Watson & Yellott 2012, "A unified formula for light-adapted pupil size"].
        // Mesopica 4.0 mm en un paciente de ~70 anos (miosis senil: NO 7 mm, y tampoco 5.5):
        // Winn et al. 1994, el dataset canonico de diametro pupilar vs edad y luminancia, ubica
        // a esa edad entre 4.0 y 4.8 mm en TODO el rango mesopico (~4.5 mm a 0.44 cd/m2,
        // ~4.8 mm a 0.09 cd/m2) y nunca por encima de ~5 mm. ruta_noche es mesopico ALTO
        // (6 faroles + faros del trafico) => extremo luminoso del rango => 4.0 mm.
        // [Winn, B., Whitaker, D., Elliott, D.B., Phillips, N.J. (1994), "Factors affecting
        // light-adapted pupil size in normal human subjects", IOVS 35(3):1132-1137].
        // ANCLA de calibracion dia<->noche (por que NO volver a subirla): con 5.5 el tablero de
        // ruta_noche (0.841 m, 1.023 D de error) recibia 3.05 px de radio contra 1.82 px del
        // libro al brazo estirado del consultorio (0.78 m, 1.116 D) — o sea 68% MAS desenfoque
        // estando MAS lejos y con MENOS error dioptrico: el orden de distancias INVERTIDO. Con
        // 4.0 el tablero da 2.22 px (+22% sobre el libro), que es monotono y coherente.
        #define PUPIL_DAY_MM    3.0
        #define PUPIL_NIGHT_MM  4.0
        // Tope del radio angular. 2.0 grados = 4 grados de diametro.
        // DENSIDAD DE LA ESPIRAL EN EL TECHO DEL RANGO (hallazgo N3 de review — la version
        // anterior de este comentario decia "la espiral de 24 taps lo cubre densamente" y es
        // FALSO): la separacion media de un muestreo de area uniforme de N taps sobre un disco
        // de radio r es 2*r/sqrt(N) = 0.408*r. A 48 px full-res (2 grados a ppd 24) el radio de
        // baja es 12 px => separacion 4.9 px de baja (~20 px full-res) contra un footprint
        // bilineal de ~1-1.5 px de baja: SUB-MUESTREO de 3-4x. La densidad solo alcanza hasta
        // rLow ~3.5-4 px de baja, o sea radiusPx <~ 15 px full-res.
        // Por que se acepta: la fase de la espiral se dithera por pixel (ver FragLowGather), asi
        // que el sub-muestreo NO sale como copias fantasma coherentes (que es el artefacto que
        // motivo M1) sino como RUIDO de alta frecuencia; el box 4x4 del pass 2 suaviza cada copia
        // y el tent del upsample promedia 4 vecinos. El residuo esperado es un moteado de 4-8 px
        // full-res que puede "hervir" con el movimiento de cabeza — solo verificable en el visor.
        // Config de PROD que excede el techo: catarata (desenfoque_max 2.0, profundidad_foco_m 0)
        // con el libro cerca => 27 px de dia y 48 px de noche (clamp). Palancas si el moteado
        // molesta: LOW_TAPS -> 32 (mejora la separacion solo un 13%: escala 1/sqrt(N)) o bajar
        // MAX_BLUR_DEG. Llegar a separacion <= 1.5 px de baja a rLow 12 exigiria N ~256 taps:
        // el sub-muestreo en el techo es ESTRUCTURAL, no un parametro mal elegido.
        #define MAX_BLUR_DEG    2.0
        // Early-out del kernel full-res. NO es una mezcla optica: DiscBlur13 con radio -> 0
        // converge EXACTAMENTE a la imagen original (sus 13 pesos suman 1 y todos los taps caen
        // sobre uv), asi que mezclar con la nitida por debajo de ~medio pixel no agrega ni quita
        // informacion — solo permite saltear 13 taps. Por eso la ventana es tan baja: por encima
        // de 0.45 px el peso de la imagen nitida es 0 y NO queda ningun delta nitido residual
        // (con SUBPIXEL_HI_PX = 1.5 quedaba: a radio 1.35 px se colaba un ~6% de imagen original,
        // y el umbral pasaba a ser un acoplamiento CLINICO con el renderScale — hallazgo M9).
        // La transicion sigue siendo smoothstep (C1) para no popear.
        #define SUBPIXEL_LO_PX  0.15
        #define SUBPIXEL_HI_PX  0.45
        // Cruce de TIERS (mezcla de dos imagenes BORROSAS, nunca con la nitida). Por debajo de
        // TIER_LO_PX manda el disco full-res; por encima de TIER_HI_PX manda el gather a 1/16.
        //
        // HUECO DE CALIDAD 2.6-6.3 px — EXISTE Y ES ESTRUCTURAL (hallazgo N1 de review). Los dos
        // kernels no se solapan: el disco es limpio hasta ~2.6 px (separacion de taps, ver
        // DiscBlur13) y el tier no puede bajar de ~6.3 px (piso de varianza, ver LOW_PSF_VAR).
        // En la banda del medio NINGUNO de los dos es correcto y el smoothstep solo elige QUE
        // error se paga. Se eligio pagar SOBRE-BORRONEO y no fantasmas, con este criterio:
        // el sobre-borroneo es un sesgo CUANTITATIVO, monotono, medible y PESIMISTA (el paciente
        // ve algo peor de lo que el modelo pide); las copias fantasma son un error CUALITATIVO y
        // OPTIMISTA (preservan el trazo de alta frecuencia => hacen legible texto que no deberia
        // leerse, que es exactamente el bug de DiscBlur9 de la etapa B). Ante la duda, conservador.
        // Por eso TIER_LO_PX se deja BAJO (el disco pierde peso rapido) en vez de subirlo a 6.
        //
        // SESGO RESIDUAL MEDIDO (radio efectivo compuesto vs radio pedido, proxy por varianzas:
        // R_eq = sqrt((1-lowW)*R^2 + lowW*R_tier^2), con R_tier = max(R, 6.32)):
        //   R pedido    antes de N1     con N1      caso clinico
        //   2.5 px      2.50 (+0%)      2.50 (+0%)  lowW = 0, disco puro
        //   3.80 px     5.03 (+32%)     4.73 (+25%) libro 40 cm @ ppd 24  <-- peor zona
        //   4.50 px     6.42 (+43%)     5.67 (+26%) peor punto de la banda
        //   6.24 px     8.42 (+35%)     6.32 (+1.3%) libro 25 cm @ ppd 24
        //   7.13 px     9.24 (+30%)     7.13 (+0%)  optotipo scatter 0.6 @ ppd 90
        //   10.58 px    12.13 (+15%)    10.58 (+0%) libro 15 cm @ ppd 24
        //   >= 6.32 px  +3% a +35%      EXACTO      todo el regimen lowW = 1
        // O sea: contabilizar el piso completo deja EXACTO todo el regimen del tier puro y baja el
        // peor caso de la banda de +43% a +26% (<= 0.10 logMAR de sesgo pesimista, solo en 2.6-6 px).
        //
        // POR QUE NO SE CIERRA DEL TODO (opciones evaluadas y rechazadas con numeros):
        //  - Subir TIER_LO_PX a ~6 con el disco actual: a 6 px el anillo interno deja 4 copias al
        //    12.5% separadas 4.24 px => POLIOPIA franca. Peor que el sesgo. Rechazado.
        //  - Subir TIER_LO_PX a ~6 agrandando el disco: para separacion <= 2 px a 6 px de radio el
        //    anillo externo necesita 19 taps (2*6*sin(pi/N) <= 2) mas los internos => ~38 taps
        //    full-res. Y el peor caso NO es raro: catarata de PROD de noche deja la sala entera a
        //    ~2.5 px, o sea 38 taps a pantalla completa (~19 Gtex/s en Quest 3) con la medicion de
        //    frame time (captura L) todavia PENDIENTE. Rechazado por presupuesto no medido.
        //  - Un disco intermedio de 25 taps (limpio a ~4.1 px) bajaria el peor caso a ~+10% por
        //    +12 taps/px a pantalla completa en esa misma config. Es la mejor relacion de las tres
        //    y queda como la PRIMERA palanca a considerar DESPUES de la captura L, no antes.
        //  - Bajar el piso con upsample de 1 tap en vez del tent: 6.32 -> 5.16 px (el tent aporta
        //    0.375 de los 0.625). No cierra el hueco y reintroduce el bloqueo de 4 px que el tent
        //    existe para tapar. Rechazado.
        //  - Un tier intermedio a 1/4 (÷2 por eje, piso 3.16 px) SI cerraria el hueco, pero son 2
        //    passes mas y ~13 taps-equiv/px/ojo SIEMPRE que el gate este ON (4x el tier actual).
        //    Rechazado con la captura L pendiente.
        //
        //  - TIER_HI_PX no puede ser mucho mas alto: DiscBlur13 es limpio hasta ~2.6 px y en el
        //    medio del cruce todavia aporta (peso 0.39 a 4.5 px — ver DiscBlur13).
        //  - TIER_LO_PX no puede ser mucho mas bajo: por debajo del piso del tier se estaria
        //    mezclando una imagen ~2.5x mas borrosa que la pedida (a 2.5 px el tier da 6.3).
        #define TIER_LO_PX      2.5
        #define TIER_HI_PX      6.0
        // Espiral del gather de baja resolucion: 24 taps de area uniforme (r_i = sqrt((i+.5)/N))
        // con incremento de angulo dorado, rotada por un hash de coordenadas de PIXEL (sin frame
        // counter => estable en el tiempo, no titila).
        #define LOW_TAPS        24
        #define GOLDEN_ANG      2.39996323
        // PISO DE VARIANZA DEL TIER DE BAJA (px de baja al cuadrado, POR EJE). Se descuenta EN
        // CUADRATURA del radio pedido al gather para que la PSF TOTAL del tier sea el disco
        // pedido y no uno mas ancho.
        // Antes se llamaba LOW_BOX_VAR y valia 1/12: eso contaba SOLO el box 4x4 del pass 2 y
        // dejaba afuera dos terminos de la misma cadena (hallazgo N1 de review). Balance completo,
        // todo en px de baja^2 por eje:
        //   box 4x4 del pass 2 (box de ancho 1 px de baja)            1/12  = 0.0833
        //   reconstruccion BILINEAL de cada tap del gather            1/6   = 0.1667
        //     (bilineal a fase f = filtro de 2 taps con pesos (1-f, f) a distancia 1 => varianza
        //      f(1-f), media 1/6 sobre f uniforme. Se aplica a CADA tap, asi que se suma a la PSF
        //      del conjunto; no se promedia con los 24 taps.)
        //   LowUpsample: tent de 4 taps a +-0.5 texel                 0.25-0.5, media 0.375
        //     (a fase 0 el kernel discreto compuesto es (0.25, 0.5, 0.25) => varianza 0.5; a fase
        //      0.5 es (0.5, 0.5) => 0.25. Ya incluye el bilineal de sus propios taps.)
        //   TOTAL                                                     ~0.625  (rango 0.54-0.79)
        // Un disco de radio g tiene varianza g^2/4, asi que el radio EFECTIVO minimo del tier
        // (con g = 0) es 2*sqrt(0.625) = 1.58 px de baja = ~6.3 px full-res (rango 5.9-6.9).
        // CONSECUENCIA CLAVE: el tier NO puede producir un desenfoque mas angosto que ~6.3 px,
        // pero TIER_LO_PX lo empieza a mezclar desde 2.5 px => queda un HUECO DE CALIDAD en la
        // banda 2.6-6.3 px (el disco es limpio hasta 2.6, el tier no baja de 6.3). Ver TIER_LO_PX
        // para el porque de no cerrarlo y el sesgo residual medido.
        #define LOW_PSF_VAR     0.625
        // Dispersion intraocular de la catarata (cataract_scatter 0..1). El straylight de van
        // den Berg crece log-lineal con el grado de catarata (C-Quant: log s ~0.9-1.0 joven
        // normal, ~1.4-1.7 nuclear moderada, >=2.0 avanzada); mapeando scatter 0..1 -> log s
        // 1..2, el exceso normalizado (10^s - 1)/9 se aproxima por scatter^2 (error < 0.04).
        // SCATTER_BLUR_DEG = radio del disco a scatter 1. CALIBRADO con capturas del optotipo
        // ETDRS a 4 m (etapa B): 0.42 dejaba la ultima fila legible en ~20/135 (demasiado);
        // 0.22 da scatter 0.3 -> ~20/20 apenas, 0.6 -> ~20/80 (nuclear moderada), 1.0 -> ~20/200
        // (avanzada), que es la tabla clinica buscada. Regla empirica medida en las capturas: la
        // ultima fila legible cae en logMAR ~= log10(diametro_del_disco_en_arcmin) - 0.38 (mas
        // pesimista que el criterio ingenuo "diametro = altura de la letra", que se queda ~0.3
        // logMAR corto). [van den Berg 1995; Franssen, Coppens & van den Berg 2006, C-Quant]
        #define SCATTER_BLUR_DEG    0.22
        // Pedestal de velo difuso del scatter. VALOR EN LINEAR (el proyecto es Linear + HDR
        // B10G11R11): 0.05 lineal sube un negro sRGB 0.05 a sRGB ~0.25 => lavado perceptual
        // fuerte con casi nula perdida sobre el blanco. Es el numero mas sensible del diseno.
        #define SCATTER_VEIL        0.05
        // Factor de campo nocturno: proxy de iluminancia del campo. De noche hay poca luz que
        // dispersar (y el velo que domina es el CIE de los faros), asi que el pedestal cae.
        #define SCATTER_VEIL_NIGHT  0.30
        // Tinte propio del pedestal (casi neutro, apenas calido). NO se reusa _GlareVeilTint:
        // puede valer (0,0,0,0) si DisabilityGlareController todavia no publico => el pedestal
        // desapareceria.
        #define SCATTER_TINT        half3(1.0, 0.97, 0.92)
        // Transmitancia del cristalino amarillo, normalizada a rojo=1 [Pokorny, Smith & Lutze
        // 1987, "Aging of the human lens", Applied Optics 26(8):1437-1440]. UN solo lugar porque
        // filtra TRES cosas que fisicamente atraviesan el MISMO medio absorbente: la imagen
        // (pass 1, filtro multiplicativo), el pedestal de straylight del scatter y el velo CIE de
        // encandilamiento (los dos mas abajo, en el pass 1).
        // Si se re-calibra el amarillo, los tres tienen que moverse juntos o los velos se despegan
        // del tinte de la imagen.
        // GEMELO EN OTRO ARCHIVO (regla del patron duplicado de docs/vision-optica.md): el MISMO
        // triple vive tambien en Assets/Shaders/GlareBillboard.shader, porque los billboards de
        // glare se dibujan en cola Transparent DESPUES de este post-proceso (inyectado en
        // BeforeRenderingTransparents) y no hay include compartido. La luz DIRECTA de un faro
        // cruza el mismo cristalino ambar que la imagen => el halo tiene que salir ambar. Si se
        // recalibra el triple, TOCAR LOS DOS ARCHIVOS en la misma tanda.
        // (WindowPortal.shader NO lo lleva a proposito: es opaco (Queue Geometry) y ya pasa por
        // este pass, agregarselo lo doble-amarillearia. Ver el comentario en ese archivo.)
        #define CATARACT_YELLOW     half3(1.0, 0.86, 0.55)

        // Dioptrias de una distancia (1/m). Clamp a 5 cm para evitar division por ~0.
        float Diopters(float d) { return 1.0 / max(d, 0.05); }

        // Error de enfoque (D) respecto al foco mas cercano que este activo.
        float DefocusDiopters(float d, float fFar, float fInt, float fNear)
        {
            float dd = Diopters(d);
            float best = 1.0e9;
            if (fFar  > 0.001) best = min(best, abs(dd - Diopters(fFar)));
            if (fInt  > 0.001) best = min(best, abs(dd - Diopters(fInt)));
            if (fNear > 0.001) best = min(best, abs(dd - Diopters(fNear)));
            // Ningun foco definido (los 3 en <=0.001; la UI de la tablet lo muestra como "off",
            // es un estado alcanzable con una lente custom/admin): sin foco definido => SIN
            // defocus, no defocus INFINITO. Sin esta guarda el centinela 1e9 se propagaba a
            // 'over' y el radio quedaba clampeado a MAX_BLUR_DEG => pantalla entera al peor
            // radio posible (hallazgo M8).
            if (best > 1.0e8) return 0.0;
            return best;
        }

        // === FUENTE UNICA de la optica del desenfoque: radio del circulo de desenfoque en
        // GRADOS para el pixel 'uv' y el ojo 'eyeIdx' (0 = izq, 1 = der).
        // Encapsula depth->metros, la mascara del libro, la seleccion de params por ojo y la
        // formula fisica. NO duplicar ni inlinear: la llaman el pass 0 (una vez por pixel
        // full-res) Y el pass 3 (una vez por pixel de baja + una por cada tap del gather); si
        // las dos formulas divergen el composite entre tiers rompe (el repo ya arrastra esa
        // deuda con GlareBillboard/WindowPortal — no repetirla).
        //
        //   tolD       = profundidad_foco_m * DOF_M_TO_D                              [D]
        //   over       = max(errD - tolD, 0)                                          [D]
        //   pupilM     = lerp(PUPIL_DAY_MM, PUPIL_NIGHT_MM, _PupilScene) * 1e-3       [m]
        //   defocusDeg = 0.5 * pupilM * over * (180/PI) * desenfoque_max              [grados]
        //   scatterDeg = SCATTER_BLUR_DEG * cataract_scatter^2                        [grados]
        //   radiusDeg  = min(sqrt(defocusDeg^2 + scatterDeg^2), MAX_BLUR_DEG)
        //
        // Suma en CUADRATURA (no max ni suma lineal): son dos PSF independientes, se suman las
        // varianzas. Ademas es C1 (sin codo => sin popping) y el piso de scatter nunca REDUCE
        // el defocus.
        // Ancla de sanidad: pupila 3 mm y 1 D de error => beta = 10.3 arcmin de DIAMETRO, o sea
        // 5.16 arcmin de RADIO — que es lo que devuelve esta funcion (0.0860 grados). Ojo con
        // "corregir" el 0.5 de la formula: duplicaria todo el desenfoque.
        // El modelo es PARAXIAL: el radio se expresa en grados sobre el eje visual y se convierte
        // a pixeles con un ppd constante, pero el ppd real de una proyeccion en perspectiva crece
        // como sec^2(theta) (~+33% a 30 grados del eje) => el desenfoque queda algo SUBestimado en
        // la periferia. Aceptado: la region clinicamente relevante (optotipo, libro, cartel) esta
        // a pocos grados del eje. ===
        float BlurRadiusDeg(float2 uv, int eyeIdx)
        {
            // depth -> distancia real en metros (proyeccion inversa, radial como Godot)
            float rawDepth = SampleSceneDepth(uv);
            float3 posWS = ComputeWorldSpacePosition(uv, rawDepth, UNITY_MATRIX_I_VP);
            float distM = distance(posWS, GetCameraPositionWS());

            // Mascara del libro: su depth no es confiable; usar book_distance_m (CPU).
            float bookMask = 0.0;
            if (_BookScreenRadius > 0.0001 && _BookDistanceM > 0.0001)
            {
                float bd = distance(uv, _BookScreenUV);
                bookMask = 1.0 - smoothstep(_BookScreenRadius * 0.65, _BookScreenRadius, bd);
            }
            float effDist = lerp(distM, _BookDistanceM, bookMask);

            float fFar, fInt, fNear, prof, desMax, scatter;
            UNITY_BRANCH
            if (eyeIdx == 0)
            {
                fFar = _FocoLejosL; fInt = _FocoIntermedioL; fNear = _FocoCercaL;
                prof = _ProfundidadFocoL; desMax = _DesenfoqueMaxL; scatter = _CataractScatterL;
            }
            else
            {
                fFar = _FocoLejosR; fInt = _FocoIntermedioR; fNear = _FocoCercaR;
                prof = _ProfundidadFocoR; desMax = _DesenfoqueMaxR; scatter = _CataractScatterR;
            }

            float over = max(DefocusDiopters(effDist, fFar, fInt, fNear) - prof * DOF_M_TO_D, 0.0);
            // Radio geometrico del circulo de desenfoque (beta = p * D, radio = beta/2). La
            // pupila entra por la FISICA: de noche dilatada => circulo mas grande.
            float pupilM = lerp(PUPIL_DAY_MM, PUPIL_NIGHT_MM, saturate(_PupilScene)) * 1.0e-3;
            // desenfoque_max multiplica SOLO el termino dioptrico: es param de la LIO, no del
            // cristalino (el scatter es del cristalino cataratoso).
            float defocusDeg = 0.5 * pupilM * over * 57.29578 * max(desMax, 0.0);

            // Piso de radio por dispersion intraocular: perdida de MTF a TODA distancia,
            // independiente del foco (por eso la catarata degrada tambien la vision lejana).
            float sc = saturate(scatter);
            float scatterDeg = SCATTER_BLUR_DEG * sc * sc;

            return min(sqrt(defocusDeg * defocusDeg + scatterDeg * scatterDeg), MAX_BLUR_DEG);
        }

        // === Disco full-res de 13 taps: centro + anillo interno de 4 a 0.5r (ejes) + anillo
        // externo de 8 a 1.0r (ejes + diagonales).
        //
        // PESOS POR AREA (de verdad, esta vez): con taps a radio normalizado 0 / 0.5 / 1.0 las
        // fronteras equidistantes caen en 0.25 y 0.75, asi que las areas relativas son
        //   centro   pi*0.25^2                      = 0.0625
        //   interno  pi*(0.75^2 - 0.25^2) / 4 taps  = 0.5000 / 4  = 0.125
        //   externo  pi*(1.00^2 - 0.75^2) / 8 taps  = 0.4375 / 8  = 0.0546875
        // Suman exactamente 1.0 => a radio -> 0 la funcion devuelve la imagen original EXACTA
        // (por eso el early-out sub-pixel no necesita mezclar con la nitida).
        // El DiscBlur9 anterior decia "pesos por area" pero ponia 0.50 en el anillo externo
        // (por area serian ~0.075/0.13/0.10): eso era una PSF tipo DONA, y es lo que producia el
        // "relieve hueco" de los glifos en las capturas de la etapa B (hallazgo MENOR de review).
        //
        // LIMITE DE VALIDEZ (hallazgo M1 — el artefacto es POLIOPIA, no desenfoque): un kernel de
        // taps discretos solo produce un disco sin huecos mientras la separacion entre taps
        // vecinos sea del orden del footprint bilineal (~1.5-2 px). La separacion critica es la
        // del anillo externo: 8 taps a radio r => 2*r*sin(22.5 grados) = 0.765*r, o sea limpio
        // hasta r ~ 2.6 px. (El DiscBlur9 tenia 4 taps a 90 grados => 1.41*r, limpio solo hasta
        // r ~ 1.4 px, y el modelo genera 20+ px: de ahi los glifos repetidos.) MAX_BLUR_DEG NO
        // acota esto porque el tope esta en GRADOS y el artefacto depende de PIXELES: el que lo
        // acota es el cruce de tiers.
        // PERO OJO, el cruce NO lo elimina (hallazgo MENOR de review: la version anterior decia
        // "a TIER_HI_PX = 6 px ya dio peso 0" — cierto SOLO en 6.0 exacto). Peso real del disco
        // en el medio del cruce, con lowW = smoothstep(2.5, 6, r):
        //   r = 4.5 px => peso del disco 0.39; separacion del anillo externo 0.765*4.5 = 3.44 px
        //                 (limite limpio 2.6) => 8 copias de 0.39*0.0546875 = 2.2% cada una;
        //                 y el anillo INTERNO, que es el peor: 4 copias a 3.18 px de separacion
        //                 con 0.39*0.125 = 4.9% cada una.
        //   r = 5.0 px => peso del disco 0.21 => copias de 1.1% (externo) y 2.6% (interno).
        // Se acepta porque 2-5% por copia es perceptualmente MENOR (el DiscBlur9 dejaba 12.5%,
        // que es lo que hacia legible el texto que no debia leerse), pero NO es cero: es la mitad
        // "optimista" del hueco de calidad 2.6-6.3 px documentado en LOW_PSF_VAR / doc viva.
        half3 DiscBlur13(float2 uv, float2 texel, float radiusPx)
        {
            float2 e = texel * (radiusPx * 0.5);       // anillo interno (ejes, |off| = 0.5r)
            float2 o = texel * radiusPx;               // anillo externo (ejes, |off| = r)
            float2 d = texel * (radiusPx * 0.70711);   // anillo externo (diagonales, |off| = r)
            #define VP_TAP(off) SAMPLE_TEXTURE2D_X(_BlitTexture, sampler_LinearClamp, uv + (off)).rgb
            half3 s = VP_TAP(float2(0.0, 0.0)) * 0.0625;
            s += (VP_TAP(float2( e.x, 0.0)) + VP_TAP(float2(-e.x, 0.0))
               +  VP_TAP(float2(0.0,  e.y)) + VP_TAP(float2(0.0, -e.y))) * 0.125;
            s += (VP_TAP(float2( o.x, 0.0)) + VP_TAP(float2(-o.x, 0.0))
               +  VP_TAP(float2(0.0,  o.y)) + VP_TAP(float2(0.0, -o.y))
               +  VP_TAP(float2( d.x,  d.y)) + VP_TAP(float2(-d.x,  d.y))
               +  VP_TAP(float2( d.x, -d.y)) + VP_TAP(float2(-d.x, -d.y))) * 0.0546875;
            #undef VP_TAP
            return s;
        }

        // === Upsample del tier de baja: tent de 4 taps bilineales a media distancia de texel.
        // Un solo tap bilineal desde 1/16 reconstruye una superficie C0 con quiebres cada 4 px
        // full-res (bloques/pliegues visibles en gradientes suaves); el tent de 4 los suaviza y
        // ademas promedia el ruido residual del dithering de la espiral. Solo se paga en los
        // pixeles donde el tier de baja tiene peso (que son justo los que NO pagan DiscBlur13).
        // COSTO OPTICO: este tent es el termino MAS GRANDE del piso de PSF del tier (0.375 de los
        // 0.625 de LOW_PSF_VAR, o sea ~5.0 de los ~6.3 px full-res del piso). Se descuenta en
        // cuadratura en FragLowGather, pero por debajo del piso el descuento se agota. Cambiarlo a
        // 1 tap bajaria el piso a ~5.2 px a cambio de devolver el bloqueo de 4 px: ver TIER_LO_PX.
        half3 LowUpsample(float2 uv)
        {
            float2 h = _VisionLowTexel.xy * 0.5;
            half3 s  = SAMPLE_TEXTURE2D_X(_VisionLowBlur, sampler_LinearClamp, uv + float2(-h.x, -h.y)).rgb;
            s += SAMPLE_TEXTURE2D_X(_VisionLowBlur, sampler_LinearClamp, uv + float2( h.x, -h.y)).rgb;
            s += SAMPLE_TEXTURE2D_X(_VisionLowBlur, sampler_LinearClamp, uv + float2(-h.x,  h.y)).rgb;
            s += SAMPLE_TEXTURE2D_X(_VisionLowBlur, sampler_LinearClamp, uv + float2( h.x,  h.y)).rgb;
            return s * 0.25;
        }

        // Ojo efectivo: _StreamForceEye permite que la captura mono del stream fuerce el ojo
        // (1 = izq, 2 = der); 0 = usa el indice estereo real.
        int EffectiveEye()
        {
            int eyeIdx = (int)unity_StereoEyeIndex;
            int forcedEye = (int)_StreamForceEye;
            if (forcedEye != 0) eyeIdx = forcedEye - 1;
            return eyeIdx;
        }

        // Desenfoque DIRECCIONAL (astigmatismo): 7 muestras gaussianas a lo largo de
        // 'step' (eje del astigmatismo). Smear de toda la imagen en una direccion.
        half3 DirBlur(float2 uv, float2 step)
        {
            half3 s = 0;
            s += SAMPLE_TEXTURE2D_X(_BlitTexture, sampler_LinearClamp, uv - step       ).rgb * 0.05;
            s += SAMPLE_TEXTURE2D_X(_BlitTexture, sampler_LinearClamp, uv - step * 0.667).rgb * 0.10;
            s += SAMPLE_TEXTURE2D_X(_BlitTexture, sampler_LinearClamp, uv - step * 0.333).rgb * 0.20;
            s += SAMPLE_TEXTURE2D_X(_BlitTexture, sampler_LinearClamp, uv                ).rgb * 0.30;
            s += SAMPLE_TEXTURE2D_X(_BlitTexture, sampler_LinearClamp, uv + step * 0.333).rgb * 0.20;
            s += SAMPLE_TEXTURE2D_X(_BlitTexture, sampler_LinearClamp, uv + step * 0.667).rgb * 0.10;
            s += SAMPLE_TEXTURE2D_X(_BlitTexture, sampler_LinearClamp, uv + step       ).rgb * 0.05;
            return s;
        }
        ENDHLSL

        // Pass 0: blur dioptrico ESFERICO (defocus), por ojo. Escribe la imagen
        // desenfocada por la esfera a _VisionTemp; el pass 1 le aplica el cilindro
        // (astigmatismo) ENCIMA + contraste + velo. Separar asi permite que el smear
        // astigmatico opere sobre la imagen ya desenfocada (ver Frag del pass 1).
        Pass
        {
            Name "VisionSim"
            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment FragEffect

            half4 FragEffect(Varyings input) : SV_Target
            {
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(input);
                float2 uv = input.texcoord;
                float2 texel = _ScreenSize.zw;   // 1/ancho, 1/alto FULL-RES (por ojo)

                int eyeIdx = EffectiveEye();

                // Radio ANGULAR del circulo de desenfoque (fuente unica) -> pixeles del target.
                float radiusPx = BlurRadiusDeg(uv, eyeIdx) *
                                 (eyeIdx == 0 ? _VisionPxPerDeg.x : _VisionPxPerDeg.y);

                half3 base = SAMPLE_TEXTURE2D_X(_BlitTexture, sampler_LinearClamp, uv).rgb;
                // CERO PASSTHROUGH NITIDO: la fuerza del desenfoque la da el RADIO, no una mezcla
                // con la imagen original (el lerp viejo con desenfoque_max = 0.79 dejaba pasar el
                // 21% de imagen nitida, y eso era justo lo que salvaba los bordes del texto).
                // 'sharpW' NO es una mezcla optica sino un EARLY-OUT de 13 taps: DiscBlur13 con
                // radio -> 0 converge exactamente a 'base', asi que por debajo de medio pixel dan
                // el mismo resultado. Por eso la ventana esta en 0.15-0.45 px: cualquier pixel con
                // radio >= 0.45 px lleva peso 0 de imagen nitida.
                float sharpW = 1.0 - smoothstep(SUBPIXEL_LO_PX, SUBPIXEL_HI_PX, radiusPx);
                half3 color = base;
                if (sharpW < 0.999)
                {
                    // Cruce de TIERS: dos imagenes BORROSAS, nunca con la nitida. El disco
                    // full-res es exacto pero solo hasta ~2.6 px de radio; por encima manda el
                    // gather a 1/16, que a esos radios ya es denso.
                    float lowW = smoothstep(TIER_LO_PX, TIER_HI_PX, radiusPx);
                    half3 blurred;
                    if (lowW < 0.999)
                    {
                        // Clamp del kernel full-res EN PIXELES (no en grados): garantiza que
                        // DiscBlur13 nunca se evalue en regimen de copias fantasma aunque alguien
                        // suba MAX_BLUR_DEG. En la practica el branch ya lo acota a TIER_HI_PX.
                        blurred = DiscBlur13(uv, texel, min(radiusPx, TIER_HI_PX));
                        if (lowW > 0.001) blurred = lerp(blurred, LowUpsample(uv), lowW);
                    }
                    else
                    {
                        blurred = LowUpsample(uv);   // radio grande: 13 taps full-res de menos
                    }
                    color = lerp(blurred, base, sharpW);
                }

                // Solo la esfera aca; el cilindro (astig), contraste y velos van en el pass 1.
                return half4(color, 1.0);
            }
            ENDHLSL
        }

        // Pass 1: astigmatismo (cilindro) + contraste + velo, por ojo. _BlitTexture aca
        // es _VisionTemp = salida del pass 0 (imagen ya desenfocada por la ESFERA). El smear
        // astigmatico opera sobre esa imagen (el cilindro se aplica sobre la esfera, no sobre
        // la imagen nitida): correctitud optica, y sin re-samplear la imagen original.
        Pass
        {
            Name "AstigContrastVeil"
            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment FragPost

            half4 FragPost(Varyings input) : SV_Target
            {
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(input);
                float2 uv = input.texcoord;
                float2 texel = _ScreenSize.zw;

                int eyeIdx = EffectiveEye();

                // Imagen ya desenfocada por la esfera (defocus dioptrico) = salida del pass 0.
                half3 color = SAMPLE_TEXTURE2D_X(_BlitTexture, sampler_LinearClamp, uv).rgb;

                // Astigmatismo: desenfoque DIRECCIONAL a lo largo del eje, POR OJO, SOBRE la
                // imagen ya desenfocada (DirBlur samplea _VisionTemp). Se nota en toda la
                // imagen, no solo en las luces; se suma al trazo de los billboards de glare.
                // El largo esta en GRADOS y se convierte a pixeles con _VisionPxPerDeg, igual que
                // el defocus: un smear astigmatico tambien es una magnitud ANGULAR.
                // SIM: atajo deliberado — DirBlur son 7 taps sobre TODO el largo del smear, asi
                // que a ppd altas (visor con renderScale 1.4: 1.3 grados = ~31 px) la separacion
                // entre taps pasa de ~2 px y el smear se ve como 7 copias en vez de un trazo
                // continuo (mismo tipo de sub-muestreo que se corrigio para el defocus con el
                // tier de baja). Se acepta porque el astigmatismo no es el foco de esta tanda;
                // el fix seria escalar la cantidad de taps con el largo en px o pasarlo por el
                // tier de baja. Ver docs/vision-optica.md §Pendientes.
                float astig = saturate(eyeIdx == 0 ? glare_astig_l : glare_astig_r);
                if (astig > 0.001)
                {
                    float a = eyeIdx == 0 ? glare_astig_angle_l : glare_astig_angle_r;
                    float2 dir = float2(cos(a), sin(a));
                    float ppd = (eyeIdx == 0 ? _VisionPxPerDeg.x : _VisionPxPerDeg.y);
                    float2 step = dir * texel * (ASTIG_BLUR_DEG * ppd * astig);
                    half3 astigCol = DirBlur(uv, step);
                    color = lerp(color, astigCol, astig);
                }

                // Perdida de contraste: compresion de la MODULACION hacia el nivel de ADAPTACION
                // del campo (no hacia un numero fijo). El pivote interpola DIA->NOCHE con
                // _PupilScene, que es la senal de adaptacion del modelo (el diametro pupilar es
                // funcion de la luminancia de adaptacion [12][14]). Ver el bloque de
                // CONTRAST_PIVOT_DAY/NIGHT arriba: por que, la fisiologia [15], los numeros
                // medidos del campo en cada escenario y el bug que corrige.
                // Interpolacion GEOMETRICA y no lineal porque la adaptacion es logaritmica en
                // luminancia (Weber): pivot = DAY * (NIGHT/DAY)^t. El compilador plega
                // log2(constante) => cuesta un exp2. A t = 0 vale 0.22 EXACTO (pow(x,0) == 1),
                // asi que el dia queda bit a bit como antes de este fix.
                float pivot = CONTRAST_PIVOT_DAY *
                              pow(CONTRAST_PIVOT_NIGHT / CONTRAST_PIVOT_DAY, saturate(_PupilScene));
                float contrast = eyeIdx == 0 ? _ContrastLossL : _ContrastLossR;
                color = (color - pivot) * (1.0 - contrast) + pivot;

                // Tinte amarillo de catarata (transmitancia del cristalino envejecido/brunescente):
                // filtro de ABSORCION multiplicativo, fuerte en azul y casi nulo en rojo (el cristalino
                // amarillea con la edad). Triple (1.0, 0.86, 0.55) = proyeccion perceptual a sRGB
                // normalizada a rojo=1 [Pokorny, Smith & Lutze 1987, "Aging of the human lens",
                // Applied Optics 26(8):1437-1440]. Implica ~13% de caida de luminancia Rec.709: modela
                // la perdida de transmitancia TOTAL, por eso NO se agrega termino extra de luminancia.
                // MULTIPLICATIVO, no aditivo: el cristalino no emite; la luz dispersada ya la modela el
                // velo/straylight. Va DESPUES del contraste y ANTES de los dos velos aditivos porque
                // este multiply filtra la IMAGEN; cada velo lleva su PROPIO factor de transmitancia
                // explicito (pedestal de scatter abajo, velo CIE al final) con el MISMO
                // CATARACT_YELLOW. No es que ponerlo despues "doble-amarillearia" el velo: los cuatro
                // terminos del modelo (imagen, pedestal, halo del billboard, velo CIE) cruzan el mismo
                // medio absorbente y llevan la transmitancia UNA vez cada uno. _GlareVeilTint
                // (1, 0.95, 0.85) es tinte de LOOK del faro, no transmitancia: multiplicarlo por
                // CATARACT_YELLOW no dobla nada, compone fuente x medio.
                float cataract = saturate(eyeIdx == 0 ? _CataractL : _CataractR);
                color *= lerp(half3(1.0, 1.0, 1.0), CATARACT_YELLOW, cataract);

                // Velo difuso por DISPERSION INTRAOCULAR de la catarata (cataract_scatter):
                // pedestal ADITIVO que NO necesita ninguna fuente puntual en el campo, a
                // diferencia del disability glare CIE de abajo (_GlareVeil), que exige una
                // GlareBillboardInstance y decae como 1/theta^2. La luz de TODO el campo se
                // dispersa en el cristalino cataratoso y baja el contraste de la escena
                // completa [van den Berg 1995; Franssen, Coppens & van den Berg 2006 (C-Quant)].
                // Cuadratico en scatter: mismo mapeo log-lineal del straylight que el radio.
                // Va DESPUES del tinte amarillo y ANTES del bloque _GlareVeil por la misma razon
                // ya documentada arriba: el filtro amarillo filtra la IMAGEN, no la luz parasita.
                // Los dos velos se componen aditivamente. Sin desaturacion extra: un pedestal
                // casi blanco aditivo ya desatura.
                float scat = saturate(eyeIdx == 0 ? _CataractScatterL : _CataractScatterR);
                if (scat > 0.001)
                {
                    // Factor de campo: proxy de iluminancia. De dia hay mucha luz que dispersar;
                    // de noche el campo es oscuro y el pedestal cae (ahi domina el velo CIE).
                    float field = lerp(1.0, SCATTER_VEIL_NIGHT, saturate(_PupilScene));
                    // El pedestal se TINE con el mismo ambar que la imagen: la luz que se dispersa
                    // DENTRO de un cristalino brunescente atraviesa el mismo medio absorbente antes
                    // de llegar a la retina, asi que el straylight sale AMBAR, no blanco. Es el
                    // mismo CATARACT_YELLOW de arriba a proposito (una sola transmitancia).
                    // Sin tinte, el pedestal casi neutro (B/R 0.92) se suma sobre una imagen ya
                    // desaturada en azul y la DES-amarillea: medido en consultorio con la catarata
                    // de PROD, el B/R del frame subia de 0.7243 (scatter 0) a 0.7755 (scatter 0.9),
                    // +7.1 % — el velo estaba deshaciendo el trabajo del filtro.
                    // lerp sobre `cataract`: con cataract_yellow = 0 (nuclear dispersora sin
                    // brunescencia) el pedestal queda EXACTAMENTE en SCATTER_TINT => ese caso del
                    // catalogo no se toca, y los dos params siguen siendo independientes.
                    half3 pedTint = lerp(SCATTER_TINT, SCATTER_TINT * CATARACT_YELLOW, cataract);
                    color += pedTint * (SCATTER_VEIL * scat * scat * field);
                }

                // Encandilamiento (disability glare): velo de luminancia ADITIVO (straylight)
                // por ojo. Aditivo => levanta los negros y baja el contraste como el velo real;
                // mas intenso CERCA de la fuente (la luz "florece") que en el resto del campo.
                float veilAmt = saturate(eyeIdx == 0 ? _GlareVeilL : _GlareVeilR);
                if (veilAmt > 0.001)
                {
                    float2 dd = uv - _GlareVeilUV.xy;
                    dd.x *= _ScreenSize.x / max(_ScreenSize.y, 1.0);   // aspecto -> glow circular
                    float concentrated = exp(-dot(dd, dd) / 0.05);     // glow centrado en la fuente
                    float L = veilAmt * (0.35 + 0.65 * concentrated);  // pedestal uniforme + glow
                    // El velo CIE tambien cruza el cristalino ambar: la luz del faro se dispersa
                    // DENTRO del medio absorbente, asi que el straylight llega a la retina AMBAR,
                    // no blanco (mismo argumento fisico que tine el pedestal de scatter y el halo
                    // del billboard). _GlareVeilTint (1, 0.95, 0.85) es el tinte de LOOK de la
                    // fuente; CATARACT_YELLOW es la TRANSMITANCIA del medio => se componen, no se
                    // doblan. Con una fuente de frente este termino DOMINA (nightPupilFactor 3 de
                    // noche, tope maxVeil 0.6): medido con un faro de frente a 4 m, el velo aporta
                    // el 88 % de la energia R del nucleo y sin el filtro dejaba el B/R del nucleo
                    // en 0.808 (blanco grisaceo) contra 0.488 con filtro. Pasa lo mismo de DIA
                    // mirando al sol del consultorio (el velo NO esta gateado por escenario).
                    // Ver docs/vision-optica.md §El velo CIE tambien pasa por el filtro ambar.
                    // Con cataract_yellow = 0 el lerp da 1.0 EXACTO => bit-identico.
                    half3 veil = _GlareVeilTint.rgb * lerp(half3(1.0, 1.0, 1.0), CATARACT_YELLOW, cataract);
                    color += veil * L;                                  // straylight aditivo (HDR cerca -> bloom)
                    // leve perdida de saturacion del velo (la luz parasita "lava" el color).
                    // Luminancia FOTOPICA de la imagen mostrada: base unica Rec.709 (ITU-R BT.709,
                    // primarios sRGB). Distinta a los pesos ESCOTOPICOS del velo (esos miden cuanto
                    // dispersa la FUENTE de noche; esto mide el gris percibido de la imagen).
                    half lum = dot(color, half3(0.2126, 0.7152, 0.0722));
                    color = lerp(color, lum.xxx, veilAmt * 0.12);
                }

                return half4(color, 1.0);
            }
            ENDHLSL
        }

        // Pass 2: DOWNSAMPLE a 1/16 (1/4 por eje), source -> _VisionLowA. Box 4x4 exacto con
        // solo 4 taps bilineales: cada tap se pide a +-1 texel FULL-RES del centro del pixel de
        // baja, o sea exactamente sobre la esquina de un bloque 2x2 => el filtro bilineal
        // promedia esos 4 texels. Cuatro cuadrantes de 2x2 = el bloque 4x4 completo.
        // Este box es el "piso" de desenfoque del tier: el gather no puede producir una PSF mas
        // angosta que esto (de ahi la cota inferior de TIER_LO_PX).
        Pass
        {
            Name "VisionLowDown"
            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment FragLowDown

            half4 FragLowDown(Varyings input) : SV_Target
            {
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(input);
                float2 uv = input.texcoord;
                // Texel de la FUENTE (full-res). _ScreenSize sigue siendo el de la camara aunque
                // el destino de este blit sea 1/16 — que es justo lo que hace falta aca.
                float2 t = _ScreenSize.zw;
                half3 s  = SAMPLE_TEXTURE2D_X(_BlitTexture, sampler_LinearClamp, uv + float2(-t.x, -t.y)).rgb;
                s += SAMPLE_TEXTURE2D_X(_BlitTexture, sampler_LinearClamp, uv + float2( t.x, -t.y)).rgb;
                s += SAMPLE_TEXTURE2D_X(_BlitTexture, sampler_LinearClamp, uv + float2(-t.x,  t.y)).rgb;
                s += SAMPLE_TEXTURE2D_X(_BlitTexture, sampler_LinearClamp, uv + float2( t.x,  t.y)).rgb;
                return half4(s * 0.25, 1.0);
            }
            ENDHLSL
        }

        // Pass 3: GATHER de radio variable por pixel a 1/16, _VisionLowA -> _VisionLowB.
        // Espiral de LOW_TAPS taps de AREA UNIFORME (r_i = sqrt((i+0.5)/N), incremento de angulo
        // dorado) escalada al radio del pixel, con la fase rotada por un hash de coordenadas de
        // PIXEL — sin frame counter, asi que el patron es estable en el tiempo (no titila) y el
        // error de muestreo residual queda como ruido de alta frecuencia entre pixeles vecinos en
        // vez de estructura coherente (fantasmas).
        //
        // Por que un gather de radio variable y no una gaussiana separable H+V: una separable
        // tiene UN solo sigma para toda la pantalla, pero el radio del defocus varia por pixel
        // (depende del depth); compositar contra un sigma fijo reintroduce el problema del
        // passthrough nitido con un piso mas alto. El gather es correcto y son 2 passes, no 3.
        //
        // Peso "scatter-as-gather": w_i = saturate(radio_del_tap - |offset| + 1). Un objeto
        // borroso derrama sobre su entorno; uno nitido no contamina el fondo borroso. El CoC de
        // cada tap se RECALCULA con BlurRadiusDeg (misma fuente unica que el pass 0) en vez de
        // empaquetarse en alfa: el formato HDR de Quest es B10G11R11, sin alfa.
        Pass
        {
            Name "VisionLowGather"
            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment FragLowGather

            half4 FragLowGather(Varyings input) : SV_Target
            {
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(input);
                float2 uv = input.texcoord;
                float2 lowTexel = _VisionLowTexel.xy;
                int eyeIdx = EffectiveEye();

                // px/grado del TIER DE BAJA = px/grado full-res * (alto_baja / alto_full).
                // _VisionLowTexel.w es esa relacion REAL (no un 1/4 asumido). Usar _ScreenSize.zw
                // aca como texel de baja seria un error silencioso de 4x.
                float ppdLow = (eyeIdx == 0 ? _VisionPxPerDeg.x : _VisionPxPerDeg.y) * _VisionLowTexel.w;

                float rLow = BlurRadiusDeg(uv, eyeIdx) * ppdLow;
                // La cadena del tier (box 4x4 + bilineal de cada tap + tent del upsample) ya
                // aporta LOW_PSF_VAR de varianza por eje. Se descuenta en cuadratura (varianza de
                // un disco de radio g = g^2/4) para que la PSF TOTAL sea el disco pedido:
                //   g^2/4 + LOW_PSF_VAR = rLow^2/4  =>  g = sqrt(rLow^2 - 4*LOW_PSF_VAR)
                // El recorte a 0 no es solo una guarda numerica: por debajo del piso (rLow < 1.58 px
                // de baja) g = 0, los 24 taps colapsan sobre el centro y el tier devuelve el box
                // del pass 2 — que ES su PSF minima. Ahi el tier sobre-borronea por construccion
                // y por eso solo se lo mezcla con peso alto por encima de ~6 px full-res. Ese caso
                // degenerado es ademas la mayoria del campo con catarata => tiene early-out abajo
                // (antes se escribia sqrt(max(rLow*rLow - 4*LOW_PSF_VAR, 0)) en una sola linea; el
                // max se reemplaza por el if, que es la MISMA condicion y ahorra tambien la sqrt).
                float g2 = rLow * rLow - 4.0 * LOW_PSF_VAR;

                // EARLY-OUT DEGENERADO (perf pura, SIN cambio optico — F1 del plan de FPS).
                // La condicion NO es un umbral elegido a ojo: sqrt(max(x, 0)) == 0 <=> x <= 0, o
                // sea "g2 <= 0" es EXACTAMENTE el conjunto donde g = 0. Y con g = 0 el gather ya
                // devolvia el tap central, por construccion:
                //   off  = float2(cos(ang), sin(ang)) * (ri * 0) = (0,0) EXACTO
                //   tuv  = uv + (0,0) * lowTexel = uv           => los 24 taps caen sobre uv
                //   rt   = BlurRadiusDeg(uv, eyeIdx) * ppdLow   = rLow (misma expresion arriba)
                //   w    = saturate(rt - length(off) + 1) = saturate(rLow + 1) = 1.0 EXACTO
                //          (BlurRadiusDeg devuelve min(sqrt(...), MAX_BLUR_DEG) >= 0 y ppdLow >= 0
                //           => rLow >= 0 => rLow + 1 >= 1 => el saturate clampea a 1.0)
                // Los 24 pesos son IGUALES entre si y al del centro (el centro no es un tap
                // aparte: los 24 SON el centro), asi que sum/wsum es el promedio de 24 copias del
                // mismo color = ese color. Se devuelve directo => 1 sample en vez de 24, y 0
                // reconstrucciones de mundo en vez de 25 (ComputeWorldSpacePosition + distance +
                // sqrt), que es el termino ALU dominante del pass. Con la catarata de PROD
                // (cataract_scatter 0.9 => rLow ~1.06 < sqrt(4*LOW_PSF_VAR) = 1.58) esta rama cubre
                // la MAYOR PARTE del campo. Unica diferencia numerica: la rama larga acumulaba 24
                // sumas en half antes de dividir por 24, asi que el early-out es el resultado MAS
                // exacto, no otro resultado (ver doc viva §Coste).
                if (g2 <= 0.0)
                    return half4(SAMPLE_TEXTURE2D_X(_BlitTexture, sampler_LinearClamp, uv).rgb, 1.0);

                float g = sqrt(g2);

                // Rotacion ditherada, estable por pixel (hash clasico sobre la coordenada entera).
                // PENDIENTE DE VALIDAR EN EL VISOR (hallazgo MENOR de review, junto con R1): el
                // hash es de COORDENADA DE PANTALLA, o sea IDENTICO en los dos ojos, pero se
                // aplica a contenidos distintos (cada ojo ve la escena desde su posicion) => el
                // ruido residual del gather queda DESCORRELACIONADO entre ojos. Candidato a
                // rivalidad binocular / shimmer estereo, que el Game View mono NO puede detectar.
                // Si aparece, la opcion es sumar unity_StereoEyeIndex al hash (los desacopla del
                // todo, no los correlaciona) o bajar el residuo con LOW_TAPS -> 32.
                float2 pix = floor(uv / max(lowTexel, 1.0e-6));
                float a0 = frac(sin(dot(pix, float2(12.9898, 78.233))) * 43758.5453) * 6.2831853;

                half3 sum = half3(0.0, 0.0, 0.0);
                float wsum = 0.0;
                UNITY_LOOP
                for (int i = 0; i < LOW_TAPS; i++)
                {
                    float ri = sqrt((i + 0.5) / (float)LOW_TAPS);
                    float ang = a0 + i * GOLDEN_ANG;
                    float2 off = float2(cos(ang), sin(ang)) * (ri * g);   // px de baja
                    float2 tuv = uv + off * lowTexel;
                    float rt = BlurRadiusDeg(tuv, eyeIdx) * ppdLow;
                    float w = saturate(rt - length(off) + 1.0);
                    sum += SAMPLE_TEXTURE2D_X(_BlitTexture, sampler_LinearClamp, tuv).rgb * w;
                    wsum += w;
                }
                // Fallback al tap central si ningun vecino alcanza (pixel nitido rodeado de
                // nitidos: g ~ 0 y el propio centro ya pesa 1, pero la guarda evita 0/0).
                half3 outc = (wsum > 1.0e-4)
                    ? sum / wsum
                    : SAMPLE_TEXTURE2D_X(_BlitTexture, sampler_LinearClamp, uv).rgb;
                return half4(outc, 1.0);
            }
            ENDHLSL
        }
    }
}
