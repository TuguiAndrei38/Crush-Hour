// Crowd character shader using vertex animation textures (VAT).
// Each texel row of _PositionTex/_NormalTex is one baked animation frame; each column is one vertex.
// Per-person data comes from _Instances (filled by CrowdRenderer, drawn with Graphics.RenderMeshPrimitives):
//   objectToWorld: placement, anim.x: frame within the clip, anim.y: first row of the clip, anim.z: frames in the clip.
Shader "CrushHour/CrowdVAT"
{
    Properties
    {
        [MainTexture] _BaseMap ("Texture", 2D) = "white" {}
        [MainColor] _BaseColor ("Color", Color) = (1, 1, 1, 1)
        _Ambient ("Ambient light", Color) = (0.45, 0.47, 0.52, 1)
        [NoScaleOffset] _PositionTex ("Baked positions", 2D) = "black" {}
        [NoScaleOffset] _NormalTex ("Baked normals", 2D) = "black" {}
    }

    SubShader
    {
        Tags { "RenderType" = "Opaque" "RenderPipeline" = "UniversalPipeline" "Queue" = "Geometry" }

        HLSLINCLUDE
        #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

        struct CrowdInstance
        {
            float4x4 objectToWorld;
            float4 anim;
        };
        StructuredBuffer<CrowdInstance> _Instances;

        TEXTURE2D(_BaseMap);
        SAMPLER(sampler_BaseMap);
        TEXTURE2D(_PositionTex);
        TEXTURE2D(_NormalTex);

        CBUFFER_START(UnityPerMaterial)
            float4 _BaseMap_ST;
            half4 _BaseColor;
            half4 _Ambient;
        CBUFFER_END

        // Animated object-space position and normal of one vertex, blended between two baked frames.
        void SampleVAT(uint vertexID, float4 anim, out float3 positionOS, out float3 normalOS)
        {
            float frames = max(anim.z, 1.0);
            float f = fmod(max(anim.x, 0.0), frames);
            int f0 = (int)f;
            int f1 = (f0 + 1) % (int)frames;
            float t = f - f0;
            int row0 = (int)anim.y + f0;
            int row1 = (int)anim.y + f1;
            int column = (int)vertexID;
            positionOS = lerp(LOAD_TEXTURE2D_LOD(_PositionTex, int2(column, row0), 0).xyz,
                              LOAD_TEXTURE2D_LOD(_PositionTex, int2(column, row1), 0).xyz, t);
            normalOS = lerp(LOAD_TEXTURE2D_LOD(_NormalTex, int2(column, row0), 0).xyz,
                            LOAD_TEXTURE2D_LOD(_NormalTex, int2(column, row1), 0).xyz, t);
        }
        ENDHLSL

        Pass
        {
            Name "ForwardLit"
            Tags { "LightMode" = "UniversalForward" }

            HLSLPROGRAM
            #pragma target 4.5
            #pragma vertex Vert
            #pragma fragment Frag
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"

            struct Attributes
            {
                float3 positionOS : POSITION;
                float2 uv : TEXCOORD0;
                uint vertexID : SV_VertexID;
                uint instanceID : SV_InstanceID;
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float2 uv : TEXCOORD0;
                float3 normalWS : TEXCOORD1;
            };

            Varyings Vert(Attributes input)
            {
                CrowdInstance inst = _Instances[input.instanceID];
                float3 positionOS, normalOS;
                SampleVAT(input.vertexID, inst.anim, positionOS, normalOS);

                Varyings output;
                float3 positionWS = mul(inst.objectToWorld, float4(positionOS, 1.0)).xyz;
                output.positionCS = TransformWorldToHClip(positionWS);
                output.normalWS = normalize(mul((float3x3)inst.objectToWorld, normalOS));
                output.uv = TRANSFORM_TEX(input.uv, _BaseMap);
                return output;
            }

            half4 Frag(Varyings input) : SV_Target
            {
                half4 albedo = SAMPLE_TEXTURE2D(_BaseMap, sampler_BaseMap, input.uv) * _BaseColor;
                Light light = GetMainLight();
                half diffuse = saturate(dot(normalize(input.normalWS), light.direction));
                return half4(albedo.rgb * (_Ambient.rgb + light.color * diffuse), 1.0);
            }
            ENDHLSL
        }

        // So the crowd also shows up in the depth texture / depth prepass.
        Pass
        {
            Name "DepthOnly"
            Tags { "LightMode" = "DepthOnly" }
            ColorMask R

            HLSLPROGRAM
            #pragma target 4.5
            #pragma vertex Vert
            #pragma fragment Frag

            struct Attributes
            {
                float3 positionOS : POSITION;
                uint vertexID : SV_VertexID;
                uint instanceID : SV_InstanceID;
            };

            float4 Vert(Attributes input) : SV_POSITION
            {
                CrowdInstance inst = _Instances[input.instanceID];
                float3 positionOS, normalOS;
                SampleVAT(input.vertexID, inst.anim, positionOS, normalOS);
                return TransformWorldToHClip(mul(inst.objectToWorld, float4(positionOS, 1.0)).xyz);
            }

            half Frag() : SV_Target { return 0; }
            ENDHLSL
        }
    }
}
