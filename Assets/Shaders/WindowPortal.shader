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
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            TEXTURE2D(_MainTex);
            SAMPLER(sampler_MainTex);

            CBUFFER_START(UnityPerMaterial)
                float _YawCenterDeg;
                float _HFovDeg;
                float _VFovDeg;
                float _HorizonV;
                float _Exposure;
            CBUFFER_END

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
                return half4(c, 1.0);
            }
            ENDHLSL
        }
    }
}
