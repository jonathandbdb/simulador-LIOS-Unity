// VisionPostProcess.shader — Simulacion de visualizacion por IOL (post-proceso URP).
// Port de features/vision_shaders/sprint2_blur_test.gdshader (Godot).
//
// Hace (1:1 con el original): depth->metros (proyeccion inversa) + blur dioptrico
// + perdida de contraste, BIFURCADO por ojo (unity_StereoEyeIndex).
// Halo / starburst los dibujan los billboards de GlareSource (F4). El astigmatismo
// se REFUERZA aca con un desenfoque DIRECCIONAL global (la imagen se borronea a lo
// largo del eje, como el astigmatismo optico real) ademas del trazo sobre las luces;
// lo manejan los globals glare_astig (0..1) y glare_astig_angle (rad).
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

        // === Astigmatismo GLOBAL (lo setea GlareController.SetAstigmatism via
        // Shader.SetGlobalFloat). glare_astig 0..1 = magnitud; angle en radianes. ===
        float glare_astig, glare_astig_angle;

        // === Override de ojo para el stream de la tablet (camara mono). La setea
        // StreamingCapture: 0 = normal (usa unity_StereoEyeIndex), 1 = forzar izq,
        // 2 = forzar der. Default 0 => NO afecta el render de los ojos XR. ===
        float _StreamForceEye;

        // === Pupila por escenario (la setea ScenarioManager). 0 = dia (pupila chica),
        // 1 = noche (dilatada). De noche el circulo de desenfoque crece => mas blur en
        // lo DESENFOCADO (no toca lo enfocado). Default 0 = sin efecto. ===
        float _PupilScene;

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

        // Pass 0: efecto (blur dioptrico + contraste, por ojo).
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

                float fFar, fInt, fNear, prof, desMax, contrast;
                UNITY_BRANCH
                if (eyeIdx == 0)
                {
                    fFar = _FocoLejosL; fInt = _FocoIntermedioL; fNear = _FocoCercaL;
                    prof = _ProfundidadFocoL; desMax = _DesenfoqueMaxL; contrast = _ContrastLossL;
                }
                else
                {
                    fFar = _FocoLejosR; fInt = _FocoIntermedioR; fNear = _FocoCercaR;
                    prof = _ProfundidadFocoR; desMax = _DesenfoqueMaxR; contrast = _ContrastLossR;
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

                // Astigmatismo: desenfoque DIRECCIONAL global a lo largo del eje. Es
                // GLOBAL (mismo valor ambos ojos); se nota en toda la imagen, no solo
                // en las luces. Se suma al trazo de los billboards de glare.
                float astig = saturate(glare_astig);
                if (astig > 0.001)
                {
                    float a = glare_astig_angle;
                    float2 dir = float2(cos(a), sin(a));
                    float2 step = dir * texel * (ASTIG_BLUR_PX * astig);
                    half3 astigCol = DirBlur(uv, step);
                    color = lerp(color, astigCol, astig);
                }

                // Perdida de contraste: compresion alrededor de pivote BAJO (no levanta negros).
                color = (color - CONTRAST_PIVOT) * (1.0 - contrast) + CONTRAST_PIVOT;

                return half4(color, 1.0);
            }
            ENDHLSL
        }

        // Pass 1: copia simple (para devolver el temp al color de camara).
        Pass
        {
            Name "Copy"
            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment FragCopy

            half4 FragCopy(Varyings input) : SV_Target
            {
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(input);
                return SAMPLE_TEXTURE2D_X(_BlitTexture, sampler_LinearClamp, input.texcoord);
            }
            ENDHLSL
        }
    }
}
