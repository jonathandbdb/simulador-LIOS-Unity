// GlareBillboard.shader — Halo difractivo + starburst + astigmatismo PROCEDURALES
// anclados a una fuente de luz real. Port de glare_billboard.gdshader (Godot).
//
// Por que billboards y no screen-space: el gather de mips del backbuffer NO funciona
// en Quest multiview (los halos desaparecian). Esto dibuja el glare con matematica
// pura sobre un quad que sigue a la camara con tamano ANGULAR constante. Aditivo.
//
// Per-eye via unity_StereoEyeIndex + globals glare_* (modo Blend). Color/energia/
// direccion de la fuente por instancia (MaterialPropertyBlock). Astigmatismo global.
Shader "Simulador/GlareBillboard"
{
    SubShader
    {
        Tags { "RenderPipeline" = "UniversalPipeline" "Queue" = "Transparent" "RenderType" = "Transparent" }

        Pass
        {
            Name "GlareBillboard"
            Tags { "LightMode" = "UniversalForward" }
            Blend One One          // aditivo (blend_add)
            ZWrite Off
            ZTest LEqual           // se ocluye tras geometria mas cercana
            Cull Off

            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment Frag
            #pragma multi_compile_instancing
            #pragma multi_compile _ STEREO_INSTANCING_ON STEREO_MULTIVIEW_ON

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            // === Globals de lente por ojo (los setea GlareController via Shader.SetGlobalFloat) ===
            float glare_halo_l, glare_halo_r;
            float glare_pupil_l, glare_pupil_r;
            float glare_star_l, glare_star_r;
            float glare_rays_l, glare_rays_r;
            // Astigmatismo: ajuste GLOBAL (un solo valor para ambos ojos).
            float glare_astig, glare_astig_angle;
            // Override de ojo para el stream (camara mono). 0=normal, 1=izq, 2=der.
            float _StreamForceEye;

            // === Por instancia (MaterialPropertyBlock; material compartido) ===
            float4 src_color;   // .rgb color de la fuente
            float src_energy;   // brillo relativo (faro = 1.0)
            float seed;         // varia los rayos entre fuentes
            float4 src_dir;     // .xyz direccion local del haz; 0 = omnidireccional

            // === Calibracion angular (radianes), verbatim del original ===
            #define HALO_ANG_RADIUS  0.10
            #define PUPIL_GAIN       1.7
            #define STAR_ANG_RADIUS  0.22
            #define ASTIG_ANG_RADIUS 0.12
            #define ASTIG_WIDTH      0.02
            #define ASTIG_GAIN       2.2
            #define RING_POS         0.62
            #define RING_WIDTH       0.11
            #define DIST_REF_M       8.0
            #define TOWARD_CAM_FRAC  0.10

            float hash11(float p)
            {
                p = frac(p * 0.1031);
                p *= p + 33.33;
                return frac(p * (p + p));
            }

            struct Attributes
            {
                float3 positionOS : POSITION;
                float2 uv : TEXCOORD0;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float2 uv : TEXCOORD0;
                float4 p0 : TEXCOORD1;   // v_halo_frac, v_star_frac, v_astig_frac, v_fade
                float4 p1 : TEXCOORD2;   // v_halo, v_star, v_rays, v_pupil
                float3 p2 : TEXCOORD3;   // v_astig, v_astig_angle, seed
                UNITY_VERTEX_OUTPUT_STEREO
            };

            Varyings Vert(Attributes IN)
            {
                Varyings o = (Varyings)0;
                UNITY_SETUP_INSTANCE_ID(IN);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(o);

                int forcedEye = (int)_StreamForceEye;
                int eyeIdx = forcedEye != 0 ? forcedEye - 1 : (int)unity_StereoEyeIndex;
                bool left = (eyeIdx == 0);
                float v_halo  = saturate(left ? glare_halo_l  : glare_halo_r);
                float v_star  = saturate(left ? glare_star_l  : glare_star_r);
                float v_rays  = left ? glare_rays_l : glare_rays_r;
                float v_pupil = saturate(left ? glare_pupil_l : glare_pupil_r);
                float v_astig = saturate(glare_astig);

                float3 origin = float3(unity_ObjectToWorld._m03, unity_ObjectToWorld._m13, unity_ObjectToWorld._m23);
                float3 camPos = _WorldSpaceCameraPos;
                float dist = max(distance(origin, camPos), 0.2);
                float3 toward = (camPos - origin) / dist;

                float facing = 1.0;
                if (dot(src_dir.xyz, src_dir.xyz) > 0.25)
                {
                    float3 beam = normalize(mul((float3x3)unity_ObjectToWorld, src_dir.xyz));
                    facing = smoothstep(0.05, 0.35, dot(beam, toward));
                }

                float pupilScale = lerp(1.0, PUPIL_GAIN, v_pupil);
                float haloR  = HALO_ANG_RADIUS * v_halo * pupilScale;
                float starR  = STAR_ANG_RADIUS * v_star;
                float astigR = ASTIG_ANG_RADIUS * v_astig;
                float angMax = max(max(haloR, starR), astigR);

                if (angMax < 0.004 || facing < 0.01)
                {
                    o.positionCS = float4(0.0, 0.0, 2.0, 1.0); // colapsa (clipped)
                    return o;
                }

                float v_fade = saturate(src_energy * DIST_REF_M / dist) * facing;
                float radiusW = dist * angMax;
                float3 right = normalize(float3(UNITY_MATRIX_I_V._m00, UNITY_MATRIX_I_V._m10, UNITY_MATRIX_I_V._m20));
                float3 up    = normalize(float3(UNITY_MATRIX_I_V._m01, UNITY_MATRIX_I_V._m11, UNITY_MATRIX_I_V._m21));
                float3 wpos = origin
                    + (right * IN.positionOS.x + up * IN.positionOS.y) * radiusW
                    + toward * (dist * TOWARD_CAM_FRAC);

                o.positionCS = mul(UNITY_MATRIX_VP, float4(wpos, 1.0));
                o.uv = IN.uv;
                o.p0 = float4(haloR / angMax, starR / angMax, astigR / angMax, v_fade);
                o.p1 = float4(v_halo, v_star, v_rays, v_pupil);
                o.p2 = float3(v_astig, glare_astig_angle, seed);
                return o;
            }

            half4 Frag(Varyings IN) : SV_Target
            {
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(IN);

                float v_halo_frac  = IN.p0.x;
                float v_star_frac  = IN.p0.y;
                float v_astig_frac = IN.p0.z;
                float v_fade       = IN.p0.w;
                float v_halo  = IN.p1.x;
                float v_star  = IN.p1.y;
                float v_rays  = IN.p1.z;
                float v_pupil = IN.p1.w;
                float v_astig       = IN.p2.x;
                float v_astig_angle = IN.p2.y;
                float sd            = IN.p2.z;

                float2 p = IN.uv * 2.0 - 1.0;
                float r = length(p);
                float total = 0.0;

                // --- Halo: glow gaussiano + ANILLOS difractivos concentricos ---
                // La trifocal difractiva (PanOptix) muestra varios anillos, no uno.
                // Los anillos pesan ~v_halo^2: en monofocal (halo casi nulo) no aparecen.
                if (v_halo_frac > 0.001)
                {
                    float rh = r / v_halo_frac;
                    float glow = exp(-rh * rh * 3.2);
                    float d1 = (rh - 0.45) / 0.09;
                    float d2 = (rh - 0.68) / 0.10;
                    float d3 = (rh - 0.90) / 0.11;
                    float rings = exp(-d1 * d1) * 0.70 + exp(-d2 * d2) * 0.55 + exp(-d3 * d3) * 0.40;
                    total += (glow * 0.85 + rings * 0.80 * v_halo) * v_halo;
                }

                // --- Starburst: rayos radiales finos con variacion por rayo ---
                if (v_star_frac > 0.001 && v_rays >= 1.0)
                {
                    float n = clamp(v_rays, 1.0, 16.0);
                    float ang = atan2(p.y, p.x);
                    float sector_f = frac(ang / 6.28318530718 + 1.0) * n;
                    float k = floor(sector_f + 0.5);
                    float kk = fmod(k, n);
                    float h1 = hash11(kk * 12.9898 + sd * 7.31);
                    float h2 = hash11(kk * 3.17 + sd * 19.1);
                    float d_sec = sector_f - k - (h1 - 0.5) * 0.35;
                    float width = 0.055 + 0.05 * h2;
                    float spoke = exp(-(d_sec * d_sec) / (width * width));
                    float ray_len = v_star_frac * lerp(0.55, 1.0, h1);
                    float rs = r / max(ray_len, 0.001);
                    float falloff = pow(max(1.0 - rs, 0.0), 1.3);
                    total += spoke * falloff * lerp(0.5, 1.0, h2) * v_star * 1.3;
                }

                // --- Astigmatismo: trazo direccional fino (gaussiana a lo largo del eje) ---
                if (v_astig_frac > 0.001)
                {
                    float a = v_astig_angle;
                    float2 q = float2(p.x * cos(a) + p.y * sin(a), -p.x * sin(a) + p.y * cos(a));
                    float along  = q.x / max(v_astig_frac, 0.001);
                    float across = q.y;
                    float prof = exp(-along * along * 2.469) * (1.0 - smoothstep(0.95, 1.0, abs(along)));
                    float thin = exp(-(across * across) / (ASTIG_WIDTH * ASTIG_WIDTH));
                    total += prof * thin * v_astig * ASTIG_GAIN;
                }

                float edge_fade = 1.0 - smoothstep(0.80, 0.98, r);
                float3 col = lerp(src_color.rgb, src_color.rgb * float3(0.85, 0.95, 1.15), v_pupil * 0.45);
                return half4(col * (total * v_fade * edge_fade), 1.0);
            }
            ENDHLSL
        }
    }
}
