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
            // Astigmatismo POR OJO (los setea GlareController.SetAstigmatism).
            float glare_astig_l, glare_astig_r;
            float glare_astig_angle_l, glare_astig_angle_r;
            // Transmitancia ambar del cristalino cataratoso POR OJO (0..1). Los publica
            // GlareController desde cataract_yellow del catalogo, por el mismo camino per-eye
            // glare_*_l/r que el resto. Ver CATARACT_YELLOW abajo.
            float glare_cataract_l, glare_cataract_r;
            // Override de ojo para el stream (camara mono). 0=normal, 1=izq, 2=der.
            float _StreamForceEye;
            // Umbrales de facing UNIFICADOS con el velo (los publica GlareController desde
            // FacingLo/FacingHi; fuente unica C#). smoothstep(Lo, Hi, dot(haz, haciaCamara)).
            float _GlareFacingLo, _GlareFacingHi;

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
            #define DIST_REF_M       8.0
            #define TOWARD_CAM_FRAC  0.10

            // Transmitancia del cristalino amarillo/brunescente, normalizada a rojo=1 [Pokorny,
            // Smith & Lutze 1987, "Aging of the human lens", Applied Optics 26(8):1437-1440].
            // POR QUE ESTA ACA: este shader es Queue=Transparent con Blend One One y el pass de
            // vision se inyecta en BeforeRenderingTransparents => los billboards se dibujan
            // DESPUES de todo el post-proceso y NO los alcanza el filtro ambar de
            // VisionPostProcess. Sin esto, un paciente con catarata brunescente veia la escena
            // ambar y los halos de los faros BLANCOS — y la luz de un faro es luz DIRECTA
            // cruzando el mismo cristalino absorbente que la imagen (el mismo argumento fisico
            // que ya justifica tenir el pedestal de scatter, aca con mas fuerza todavia).
            // GEMELO EN OTRO ARCHIVO (regla del patron duplicado de docs/vision-optica.md): el
            // MISMO triple vive en Assets/Shaders/VisionPostProcess.shader (filtro de la imagen
            // + pedestal de scatter). NO hay include compartido: si se recalibra el amarillo,
            // TOCAR LOS DOS ARCHIVOS en la misma tanda.
            // (WindowPortal.shader NO lo lleva a proposito: es opaco y ya pasa por el pass de
            // vision; agregarselo lo doble-amarillearia. Ver el comentario en ese archivo.)
            #define CATARACT_YELLOW  half3(1.0, 0.86, 0.55)

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
                float4 p2 : TEXCOORD3;   // v_astig, v_astig_angle, seed, v_cataract
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
                float v_astig = saturate(left ? glare_astig_l : glare_astig_r);
                float v_astig_angle = left ? glare_astig_angle_l : glare_astig_angle_r;
                // Ambar del cristalino POR OJO: no entra en angMax (no es un patron, es un
                // filtro), asi que NO puede resucitar un billboard colapsado ni cambiar su
                // geometria. Se resuelve en el vertex (uniforme por instancia) y viaja en p2.w.
                float v_cataract = saturate(left ? glare_cataract_l : glare_cataract_r);

                float3 origin = float3(unity_ObjectToWorld._m03, unity_ObjectToWorld._m13, unity_ObjectToWorld._m23);
                float3 camPos = _WorldSpaceCameraPos;
                float dist = max(distance(origin, camPos), 0.2);
                float3 toward = (camPos - origin) / dist;

                float facing = 1.0;
                if (dot(src_dir.xyz, src_dir.xyz) > 0.25)
                {
                    float3 beam = normalize(mul((float3x3)unity_ObjectToWorld, src_dir.xyz));
                    facing = smoothstep(_GlareFacingLo, _GlareFacingHi, dot(beam, toward));
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
                o.p2 = float4(v_astig, v_astig_angle, seed, v_cataract);
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
                float v_cataract    = IN.p2.w;

                float2 p = IN.uv * 2.0 - 1.0;
                float r = length(p);

                // CLIP TEMPRANO DE LAS ESQUINAS (perf pura, SIN cambio del patron clinico — F1 del
                // plan de FPS). El quad es CUADRADO (positionOS -1..1, uv 0..1 => r llega a
                // sqrt(2) = 1.414 en las esquinas) pero el patron es CIRCULAR: el edge_fade de
                // abajo es 1 - smoothstep(0.80, 0.98, r), o sea EXACTAMENTE 0 para todo r >= 0.98,
                // y el color emitido es col * (total * v_fade * edge_fade) => esos fragmentos
                // devuelven (0,0,0) y con Blend One One suman NADA al framebuffer. Descartarlos es
                // bit-identico en RGB y ahorra el resto del Frag (atan2 del starburst, cuatro exp
                // del halo, dos hash11, el seno/coseno del astig). Fraccion del quad afectada:
                // 1 - pi*0.98^2/4 = ~23 %. El ahorro es por WAVE (una wave con algun lane dentro
                // del circulo sigue ejecutando), pero las esquinas son regiones contiguas grandes
                // => la mayoria de esas waves esta enteramente afuera.
                // El umbral 0.98 es el MISMO borde del smoothstep, no un valor nuevo: si alguien
                // toca el edge_fade, tiene que tocar este clip en la misma linea de razonamiento.
                // Alpha: el fragmento escribia alfa 1.0 aditivo tambien en las esquinas y eso deja
                // de pasar. No lo consume nadie — el color HDR del visor es B10G11R11 (sin canal
                // alfa) y el stream de la tablet se decodifica a RGB24 (ver StreamingCapture).
                clip(0.98 - r);

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
                // Filtro de ABSORCION del cristalino ambar sobre la luz de la fuente. Va sobre el
                // color EMITIDO (antes del Blend One One), que es lo correcto: el framebuffer con
                // el que se mezcla ya viene filtrado por el pass de vision, asi que este multiply
                // pone el halo en la misma transmitancia que la imagen. Multiplicativo (el
                // cristalino absorbe, no emite). Con cataract_yellow = 0 el lerp da 1.0 EXACTO
                // => toda lente sin brunescencia queda bit a bit igual que antes del fix.
                col *= lerp(half3(1.0, 1.0, 1.0), CATARACT_YELLOW, v_cataract);
                return half4(col * (total * v_fade * edge_fade), 1.0);
            }
            ENDHLSL
        }
    }
}
