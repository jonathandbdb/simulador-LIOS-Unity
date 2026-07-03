// VisionPostProcess.shader — Simulacion de visualizacion por IOL (post-proceso URP).
// Port de features/vision_shaders/sprint2_blur_test.gdshader (Godot).
//
// Hace: depth->metros (proyeccion inversa) + blur dioptrico ESFERICO + astigmatismo
// (cilindro) + perdida de contraste + velo de encandilamiento, BIFURCADO por ojo
// (unity_StereoEyeIndex). Dos passes: pass 0 = solo esfera (defocus) -> _VisionTemp;
// pass 1 = cilindro + contraste + velo sobre esa imagen ya desenfocada. Asi el smear
// astigmatico opera SOBRE la esfera (correctitud optica) sin re-samplear el original.
// Halo / starburst los dibujan los billboards de GlareSource (F4). El astigmatismo
// se REFUERZA aca con un desenfoque DIRECCIONAL por ojo (la imagen se borronea a lo
// largo del eje, como el astigmatismo optico real) ademas del trazo sobre las luces;
// lo manejan los globals glare_astig_l/r (0..1) y glare_astig_angle_l/r (rad).
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
        float _DesenfoqueMaxL, _DesenfoqueMaxR;        // 0..1
        float _ContrastLossL, _ContrastLossR;          // 0..0.6

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

        // === Constantes (verbatim del original) ===
        #define BLUR_RADIUS_PX  7.0
        #define MAX_DEFOCUS_D   1.5    // error de enfoque (D) que satura el blur
        #define DOF_M_TO_D      0.5    // mapea profundidad_foco_m a tolerancia (D)
        #define CONTRAST_PIVOT  0.22   // pivote bajo: no levanta los negros
        #define ASTIG_BLUR_PX   22.0   // largo maximo del smear direccional (a magnitud 1)

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
            return best;
        }

        // Nitido dentro de la profundidad de foco (tolerancia dioptrica); sube
        // proporcional al error hasta max_blur a MAX_DEFOCUS_D de todo foco.
        float BlurFromFocus(float d, float fFar, float fInt, float fNear,
                             float depthOfFocusM, float maxBlur)
        {
            float errD = DefocusDiopters(d, fFar, fInt, fNear);
            float tolD = depthOfFocusM * DOF_M_TO_D;
            float over = max(errD - tolD, 0.0);
            return maxBlur * saturate(over / MAX_DEFOCUS_D);
        }

        // Box blur OPTIMIZADO: 4 muestras bilineales en diagonales (= 4-tap del original).
        half3 BoxBlur4tap(float2 uv, float2 texel, float radiusPx)
        {
            float2 o = texel * radiusPx * 0.75;
            half3 sum  = SAMPLE_TEXTURE2D_X(_BlitTexture, sampler_LinearClamp, uv + float2( o.x,  o.y)).rgb;
            sum += SAMPLE_TEXTURE2D_X(_BlitTexture, sampler_LinearClamp, uv + float2(-o.x,  o.y)).rgb;
            sum += SAMPLE_TEXTURE2D_X(_BlitTexture, sampler_LinearClamp, uv + float2( o.x, -o.y)).rgb;
            sum += SAMPLE_TEXTURE2D_X(_BlitTexture, sampler_LinearClamp, uv + float2(-o.x, -o.y)).rgb;
            return sum * 0.25;
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
                float2 texel = _ScreenSize.zw;   // 1/ancho, 1/alto (por ojo)

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

                // Parametros por ojo. _StreamForceEye permite que la captura mono del
                // stream fuerce el ojo (1=izq, 2=der); 0 = usa el indice estereo real.
                int eyeIdx = (int)unity_StereoEyeIndex;
                int forcedEye = (int)_StreamForceEye;
                if (forcedEye != 0) eyeIdx = forcedEye - 1;

                float fFar, fInt, fNear, prof, desMax;
                UNITY_BRANCH
                if (eyeIdx == 0)
                {
                    fFar = _FocoLejosL; fInt = _FocoIntermedioL; fNear = _FocoCercaL;
                    prof = _ProfundidadFocoL; desMax = _DesenfoqueMaxL;
                }
                else
                {
                    fFar = _FocoLejosR; fInt = _FocoIntermedioR; fNear = _FocoCercaR;
                    prof = _ProfundidadFocoR; desMax = _DesenfoqueMaxR;
                }

                float blurAmount = saturate(BlurFromFocus(effDist, fFar, fInt, fNear, prof, desMax));
                // Pupila dilatada de noche agranda el circulo de desenfoque (mas blur
                // en lo borroso; lo enfocado sigue nitido porque blurAmount alli es 0).
                blurAmount = saturate(blurAmount * lerp(1.0, 1.35, saturate(_PupilScene)));

                half3 base = SAMPLE_TEXTURE2D_X(_BlitTexture, sampler_LinearClamp, uv).rgb;
                half3 color = base;
                if (blurAmount > 0.001)
                {
                    half3 blurred = BoxBlur4tap(uv, texel, BLUR_RADIUS_PX * blurAmount);
                    color = lerp(base, blurred, blurAmount);
                }

                // Solo la esfera aca; el cilindro (astig), contraste y velo van en el pass 1.
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

                int eyeIdx = (int)unity_StereoEyeIndex;
                int forcedEye = (int)_StreamForceEye;
                if (forcedEye != 0) eyeIdx = forcedEye - 1;

                // Imagen ya desenfocada por la esfera (defocus dioptrico) = salida del pass 0.
                half3 color = SAMPLE_TEXTURE2D_X(_BlitTexture, sampler_LinearClamp, uv).rgb;

                // Astigmatismo: desenfoque DIRECCIONAL a lo largo del eje, POR OJO, SOBRE la
                // imagen ya desenfocada (DirBlur samplea _VisionTemp). Se nota en toda la
                // imagen, no solo en las luces; se suma al trazo de los billboards de glare.
                float astig = saturate(eyeIdx == 0 ? glare_astig_l : glare_astig_r);
                if (astig > 0.001)
                {
                    float a = eyeIdx == 0 ? glare_astig_angle_l : glare_astig_angle_r;
                    float2 dir = float2(cos(a), sin(a));
                    float2 step = dir * texel * (ASTIG_BLUR_PX * astig);
                    half3 astigCol = DirBlur(uv, step);
                    color = lerp(color, astigCol, astig);
                }

                // Perdida de contraste: compresion alrededor de pivote BAJO (no levanta negros).
                float contrast = eyeIdx == 0 ? _ContrastLossL : _ContrastLossR;
                color = (color - CONTRAST_PIVOT) * (1.0 - contrast) + CONTRAST_PIVOT;

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
                    half3 veil = _GlareVeilTint.rgb;
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
    }
}
