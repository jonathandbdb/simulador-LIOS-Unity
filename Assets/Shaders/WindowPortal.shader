// WindowPortal.shader — "Portal" de paisaje para ventanas del consultorio.
// El quad NO muestra una textura plana: samplea el paisaje por DIRECCION DE VISTA,
// quedando a infinito (paralaje correcto de "vista por una abertura" en VR, no un cuadro).
// A diferencia de un skybox 360, la imagen se mapea a un SECTOR angular acotado
// (_HFovDeg x _VFovDeg centrado en _YawCenterDeg): como solo se ve por la ventana, no se
// desperdicia resolucion en direcciones invisibles ni se estira la imagen a la esfera
// completa (el mapeo 360 dejaba ~230 px visibles y pixelaba).
// _HorizonV = fila (v) de la imagen donde esta el horizonte (pitch 0).
// XR: GetCameraPositionWS() devuelve el ojo correcto en single-pass instanced.
// Sampleo con LOD 0 (sin costuras de mips; la textura se importa sin mipmaps).
//
// Ademas del PAISAJE, este quad opaco pinta POR DIRECCION DE VISTA todo el aporte del SOL:
//   1) el DISCO solar (nucleo + glow), y
//   2) el HALO difractivo + STARBURST + trazo astigmatico CLINICOS (traslado 1:1 de
//      GlareBillboard.shader — mismas curvas/constantes/energias, ver mas abajo).
// Al pintarse en el fragmento opaco, marco y paredes ocluyen disco Y destellos JUNTOS (antes
// los destellos eran billboards fisicos DENTRO de la sala que flotaban si el marco tapaba el
// sol) y todo queda a VERGENCIA INFINITA por ojo (cero disparidad binocular).
Shader "Simulador/WindowPortal"
{
    Properties
    {
        _MainTex ("Paisaje", 2D) = "white" {}
        _YawCenterDeg ("Yaw del centro de la imagen (grados mundo)", Range(-180, 180)) = 0
        _HFovDeg ("Cobertura horizontal (grados)", Range(30, 360)) = 120
        _VFovDeg ("Cobertura vertical (grados)", Range(20, 180)) = 67
        _HorizonV ("V del horizonte en la imagen", Range(0, 1)) = 0.49
        _Exposure ("Exposicion", Range(0, 3)) = 1
        // --- Sol anclado al cielo (por direccion de vista, como el paisaje) ---
        _SunDirWS ("Sol: direccion en el mundo (xyz)", Vector) = (-0.4149, 0.1908, 0.8897, 0)
        _SunColor ("Sol: color", Color) = (1, 0.96, 0.88, 1)
        _SunIntensity ("Sol: intensidad", Range(0, 8)) = 5
        _SunCoreDeg ("Sol: radio del nucleo (grados)", Range(0, 10)) = 0.35
        _SunGlowDeg ("Sol: caida del glow (grados)", Range(0, 20)) = 0.8
    }
    SubShader
    {
        Tags { "RenderType" = "Opaque" "RenderPipeline" = "UniversalPipeline" "Queue" = "Geometry" }
        Pass
        {
            Name "WindowPortal"
            ZWrite On
            Cull Back

            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment Frag
            #pragma multi_compile_instancing
            #pragma multi_compile _ STEREO_INSTANCING_ON STEREO_MULTIVIEW_ON
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            TEXTURE2D(_MainTex);
            SAMPLER(sampler_MainTex);

            CBUFFER_START(UnityPerMaterial)
                float _YawCenterDeg;
                float _HFovDeg;
                float _VFovDeg;
                float _HorizonV;
                float _Exposure;
                float4 _SunDirWS;
                float4 _SunColor;
                float _SunIntensity;
                float _SunCoreDeg;
                float _SunGlowDeg;
            CBUFFER_END

            // === Globals de lente por ojo (los MISMOS que setea GlareController para el billboard) ===
            // El halo/starburst del sol se pinta aca reusando estos globals: gating clinico intacto
            // (sin lente aplicada quedan en 0 => cero aporte, igual que el billboard).
            float glare_halo_l, glare_halo_r;
            float glare_pupil_l, glare_pupil_r;
            float glare_star_l, glare_star_r;
            float glare_rays_l, glare_rays_r;
            float glare_astig_l, glare_astig_r;
            float glare_astig_angle_l, glare_astig_angle_r;
            // Override de ojo para el stream (camara mono). 0=normal, 1=izq, 2=der. (igual que el billboard)
            float _StreamForceEye;

            // === Calibracion angular (radianes), VERBATIM de GlareBillboard.shader ===
            // NO tocar: es el mismo patron clinico, solo trasladado de sitio (billboard -> portal).
            // EXCEPCION DELIBERADA a la regla "todo cambio al patron clinico va en los DOS
            // shaders": el tinte ambar de catarata (CATARACT_YELLOW / glare_cataract_l/r) que
            // GlareBillboard.shader SI aplica a su color NO va aca. Este quad es OPACO
            // (Queue = Geometry) => el pass de vision, inyectado en BeforeRenderingTransparents,
            // ya lo filtra junto con el resto de la imagen; agregarle el triple lo
            // DOBLE-amarillearia (transmitancia al cuadrado). El billboard lo necesita justamente
            // porque es Queue = Transparent y se dibuja DESPUES del pass. Verificado por captura:
            // ver docs/vision-optica.md, §Tinte amarillo de catarata.
            #define HALO_ANG_RADIUS  0.10
            #define PUPIL_GAIN       1.7
            #define STAR_ANG_RADIUS  0.22
            #define ASTIG_ANG_RADIUS 0.12
            #define ASTIG_WIDTH      0.02
            #define ASTIG_GAIN       2.2
            // Colores/seeds de las dos fuentes que reemplaza (GlareBillboardInstance de
            // SunGlare y SunGlare2, ahora con MeshRenderer OFF): dos aportes additivos
            // coincidentes con seeds distintos = starburst mas rico (identico al look billboard).
            #define SUN_GLARE_COL_A  float3(2.2, 2.046, 1.716)   // srcColor de SunGlare  (seed 5)
            #define SUN_GLARE_COL_B  float3(2.2, 2.09,  1.804)   // srcColor de SunGlare2 (seed 23)

            float hash11(float p)   // verbatim de GlareBillboard.shader
            {
                p = frac(p * 0.1031);
                p *= p + 33.33;
                return frac(p * (p + p));
            }

            // Escalar del patron de glare (halo + starburst + astig) en el espacio ANGULAR del
            // portal. Traslado 1:1 del Frag de GlareBillboard.shader: 'rNorm' == r del billboard
            // (separacion angular / radio mayor, small-angle) y 'p' == p del billboard
            // (offset en pantalla del pixel respecto del sol). Las curvas/constantes/energias
            // son IDENTICAS a las del billboard (no se rediseña nada).
            float SunGlareTotal(float rNorm, float2 p,
                                float v_halo_frac, float v_star_frac, float v_astig_frac,
                                float v_halo, float v_star, float v_rays, float v_astig,
                                float v_astig_angle, float sd)
            {
                float r = rNorm;
                float total = 0.0;

                // --- Halo: glow gaussiano + ANILLOS difractivos concentricos ---
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
                return total;
            }

            struct Attributes
            {
                float4 positionOS : POSITION;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float3 positionWS : TEXCOORD0;
                UNITY_VERTEX_INPUT_INSTANCE_ID
                UNITY_VERTEX_OUTPUT_STEREO
            };

            Varyings Vert(Attributes input)
            {
                Varyings o;
                UNITY_SETUP_INSTANCE_ID(input);
                UNITY_TRANSFER_INSTANCE_ID(input, o);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(o);
                o.positionWS = TransformObjectToWorld(input.positionOS.xyz);
                o.positionCS = TransformWorldToHClip(o.positionWS);
                return o;
            }

            half4 Frag(Varyings input) : SV_Target
            {
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(input);
                float3 dir = normalize(input.positionWS - GetCameraPositionWS());
                // yaw relativo al centro de la imagen, envuelto a [-180,180]
                float yaw = degrees(atan2(dir.x, dir.z)) - _YawCenterDeg;
                yaw -= 360.0 * round(yaw / 360.0);
                float u = yaw / _HFovDeg + 0.5;
                float pitch = degrees(asin(clamp(dir.y, -1.0, 1.0)));
                float v = pitch / _VFovDeg + _HorizonV;
                float2 uv = clamp(float2(u, v), 0.002, 0.998);   // extiende bordes, sin wrap
                half3 c = SAMPLE_TEXTURE2D_LOD(_MainTex, sampler_MainTex, uv, 0).rgb * _Exposure;

                // --- Disco solar anclado al CIELO por direccion de vista (no a la sala) ---
                // Se pinta DENTRO del portal reusando la misma direccion 'dir' del paisaje:
                // el sol queda a distancia infinita (cero paralaje al mover/trasladar la cabeza,
                // solidario con el paisaje) y lo ocluyen marco y paredes igual que al paisaje,
                // porque este quad es OPACO.
                float3 sdir = normalize(_SunDirWS.xyz);
                float angDeg = degrees(acos(clamp(dot(dir, sdir), -1.0, 1.0)));           // separacion angular al sol (grados)
                float core = 1.0 - smoothstep(_SunCoreDeg * 0.65, _SunCoreDeg, angDeg);   // nucleo brillante (borde suave)
                float glow = exp(-(angDeg * angDeg) / max(_SunGlowDeg * _SunGlowDeg, 1e-4)); // glow gaussiano alrededor
                c += _SunColor.rgb * (_SunIntensity * saturate(core + glow * 0.6));

                // --- Halo difractivo + starburst + astig CLINICOS del sol (por direccion) ---
                // Traslado 1:1 de GlareBillboard.shader desde los billboards SunGlare/SunGlare2
                // (MeshRenderer OFF; sus GlareBillboardInstance siguen ACTIVOS solo para alimentar
                // el velo CIE). Per-eye con los MISMOS globals glare_* que el billboard. Beneficios:
                // (a) marco/pared ocluyen el patron junto con el disco (no mas destellos flotando
                //     en la sala si el marco tapa el sol); (b) queda a vergencia infinita por ojo
                //     (elimina la disparidad binocular de ~0.74 del transform a 4.9 m).
                int forcedEye = (int)_StreamForceEye;
                int eyeIdx = forcedEye != 0 ? forcedEye - 1 : (int)unity_StereoEyeIndex;
                bool leftEye = (eyeIdx == 0);
                float v_halo  = saturate(leftEye ? glare_halo_l  : glare_halo_r);
                float v_star  = saturate(leftEye ? glare_star_l  : glare_star_r);
                float v_rays  = leftEye ? glare_rays_l : glare_rays_r;
                float v_pupil = saturate(leftEye ? glare_pupil_l : glare_pupil_r);
                float v_astig = saturate(leftEye ? glare_astig_l : glare_astig_r);
                float v_astig_angle = leftEye ? glare_astig_angle_l : glare_astig_angle_r;

                float pupilScale = lerp(1.0, PUPIL_GAIN, v_pupil);
                float haloR  = HALO_ANG_RADIUS * v_halo * pupilScale;   // radianes (igual que el billboard)
                float starR  = STAR_ANG_RADIUS * v_star;
                float astigR = ASTIG_ANG_RADIUS * v_astig;
                float angMax = max(max(haloR, starR), astigR);
                if (angMax > 0.004)   // hay algo de glare que dibujar (branch uniforme, coherente)
                {
                    float angRad = acos(clamp(dot(dir, sdir), -1.0, 1.0));   // separacion angular en RADIANES
                    float rNorm = angRad / angMax;                          // == r del billboard (small-angle)
                    if (rNorm < 1.05)   // fuera del patron edge_fade ya es 0; recorta el 99% de los pixeles
                    {
                        // OJO — aca el recorte es un IF, no un clip(). GlareBillboard.shader si usa
                        // clip(0.98 - r) para descartar las esquinas de su quad (F1 del plan de FPS),
                        // pero ese quad es transparente aditivo y aporta (0,0,0) afuera del circulo.
                        // Este shader es OPACO (early-Z) y su "quad" es el portal/backdrop entero:
                        // afuera del patron del sol todavia tiene que escribir el PAISAJE. Un clip
                        // aca agujerearia la ventana y desactivaria el early-Z del draw. NO portar.
                        // Base de pantalla (camara), igual que el billboard (right/up de la inversa de la view)
                        float3 rightWS = normalize(float3(UNITY_MATRIX_I_V._m00, UNITY_MATRIX_I_V._m10, UNITY_MATRIX_I_V._m20));
                        float3 upWS    = normalize(float3(UNITY_MATRIX_I_V._m01, UNITY_MATRIX_I_V._m11, UNITY_MATRIX_I_V._m21));
                        float3 off = dir - sdir * dot(dir, sdir);            // componente perpendicular al sol (sol->pixel)
                        float2 pdir = float2(dot(off, rightWS), dot(off, upWS));
                        float pl = length(pdir);
                        pdir = pl > 1e-6 ? pdir / pl : float2(1.0, 0.0);
                        float2 p = pdir * rNorm;                             // == p del billboard (uv*2-1)

                        float v_halo_frac  = haloR  / angMax;
                        float v_star_frac  = starR  / angMax;
                        float v_astig_frac = astigR / angMax;

                        float edge_fade = 1.0 - smoothstep(0.80, 0.98, rNorm);
                        // v_fade del billboard = saturate(src_energy*DIST_REF_M/dist)*facing; para el
                        // sol (SunSkyAnchor distance=4.9 m, srcEnergy=1.8, omnidireccional) satura a 1.0.
                        float v_fade = 1.0;

                        // Dos aportes additivos: SunGlare (seed 5) + SunGlare2 (seed 23), como los dos
                        // billboards coincidentes que reemplaza. El tinte de pupila es el mismo lerp que
                        // el billboard (col = lerp(src, src*(0.85,0.95,1.15), v_pupil*0.45)).
                        float3 colA = lerp(SUN_GLARE_COL_A, SUN_GLARE_COL_A * float3(0.85, 0.95, 1.15), v_pupil * 0.45);
                        float3 colB = lerp(SUN_GLARE_COL_B, SUN_GLARE_COL_B * float3(0.85, 0.95, 1.15), v_pupil * 0.45);
                        float totA = SunGlareTotal(rNorm, p, v_halo_frac, v_star_frac, v_astig_frac,
                                                   v_halo, v_star, v_rays, v_astig, v_astig_angle, 5.0);
                        float totB = SunGlareTotal(rNorm, p, v_halo_frac, v_star_frac, v_astig_frac,
                                                   v_halo, v_star, v_rays, v_astig, v_astig_angle, 23.0);
                        c += (colA * totA + colB * totB) * (v_fade * edge_fade);
                    }
                }

                return half4(c, 1.0);
            }
            ENDHLSL
        }
    }
}
