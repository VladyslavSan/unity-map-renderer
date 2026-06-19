// DEPRECATED: This shader (S32 implementation) has been superseded by S34.
// The authoritative MapRenderer/Fill shader is now at:
//   Assets/MapRenderer.Unity/Shaders/Fill.shader
//
// This file is kept to avoid breaking .meta GUIDs already referenced in the project.
// It redirects to "Hidden/MapRenderer/Fill_S32_Deprecated" so the name "MapRenderer/Fill"
// resolves to the new Shaders/Fill.shader instead of this file.
//
// DO NOT use this shader directly. It uses the S32 stripped CBUFFER and hand-assembled
// SurfaceData, which S34 supersedes. See docs/stages/S34.md.
Shader "Hidden/MapRenderer/Fill_S32_Deprecated"
{
    Properties
    {
        _Color          ("Color", Color) = (0.4, 0.7, 0.4, 1)
        _Opacity        ("Opacity", Range(0, 1)) = 1.0
        _Metallic       ("Metallic", Range(0, 1)) = 0.0
        _Smoothness     ("Smoothness", Range(0, 1)) = 0.3
        [HDR] _EmissionColor ("Emission", Color) = (0, 0, 0, 0)
    }

    SubShader
    {
        Tags
        {
            "RenderType"             = "Opaque"
            "Queue"                  = "Geometry"
            "RenderPipeline"         = "UniversalPipeline"
            "UniversalMaterialType"  = "Lit"
            "IgnoreProjector"        = "True"
        }

        Cull Off
        ZWrite On
        ZTest LEqual

        Pass
        {
            Name "ForwardLit"
            Tags { "LightMode" = "UniversalForward" }

            HLSLPROGRAM
            #pragma target 3.5
            #pragma vertex   MapFillVert
            #pragma fragment MapFillFrag

            #pragma multi_compile _ _MAIN_LIGHT_SHADOWS _MAIN_LIGHT_SHADOWS_CASCADE _MAIN_LIGHT_SHADOWS_SCREEN
            #pragma multi_compile _ _ADDITIONAL_LIGHTS_VERTEX _ADDITIONAL_LIGHTS
            #pragma multi_compile _ EVALUATE_SH_MIXED EVALUATE_SH_VERTEX
            #pragma multi_compile_fragment _ _ADDITIONAL_LIGHT_SHADOWS
            #pragma multi_compile_fragment _ _REFLECTION_PROBE_BLENDING
            #pragma multi_compile_fragment _ _REFLECTION_PROBE_BOX_PROJECTION
            #pragma multi_compile_fragment _ _SHADOWS_SOFT _SHADOWS_SOFT_LOW _SHADOWS_SOFT_MEDIUM _SHADOWS_SOFT_HIGH
            #pragma multi_compile_fragment _ _SCREEN_SPACE_OCCLUSION
            #pragma multi_compile_fragment _ _DBUFFER_MRT1 _DBUFFER_MRT2 _DBUFFER_MRT3
            #pragma multi_compile_fragment _ _LIGHT_COOKIES
            #pragma multi_compile _ _LIGHT_LAYERS
            #pragma multi_compile_fragment _ DEBUG_DISPLAY
            #pragma multi_compile _ LIGHTMAP_SHADOW_MIXING
            #pragma multi_compile _ SHADOWS_SHADOWMASK
            #pragma multi_compile _ DIRLIGHTMAP_COMBINED
            #pragma multi_compile _ LIGHTMAP_ON
            #pragma multi_compile _ LOD_FADE_CROSSFADE
            #pragma multi_compile_instancing
            #pragma instancing_options renderinglayer
            #include_with_pragmas "Packages/com.unity.render-pipelines.universal/ShaderLibrary/DOTS.hlsl"
            #include_with_pragmas "Packages/com.unity.render-pipelines.universal/ShaderLibrary/RenderingLayers.hlsl"
            #include_with_pragmas "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Fog.hlsl"

            #include "MapLitCore.hlsl"
            #include "Fill_Input.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"

            struct Attributes
            {
                float3 positionOS : POSITION;
                float3 normalOS   : NORMAL;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct Varyings
            {
                float4 positionCS  : SV_POSITION;
                float3 positionWS  : TEXCOORD0;
                float3 normalWS    : TEXCOORD1;
                half   fogFactor   : TEXCOORD2;
                DECLARE_LIGHTMAP_OR_SH(staticLightmapUV, vertexSH, 3);
                UNITY_VERTEX_INPUT_INSTANCE_ID
                UNITY_VERTEX_OUTPUT_STEREO
            };

            Varyings MapFillVert(Attributes IN)
            {
                Varyings OUT = (Varyings)0;
                UNITY_SETUP_INSTANCE_ID(IN);
                UNITY_TRANSFER_INSTANCE_ID(IN, OUT);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(OUT);
                SetupDOTSMapLitMaterialPropertyCaches();

                MapVertexModify(IN.positionOS);

                VertexPositionInputs posInputs = GetVertexPositionInputs(IN.positionOS);
                VertexNormalInputs   normInputs = GetVertexNormalInputs(IN.normalOS);

                OUT.positionCS  = posInputs.positionCS;
                OUT.positionWS  = posInputs.positionWS;
                OUT.normalWS    = normInputs.normalWS;
                OUT.fogFactor   = ComputeFogFactor(posInputs.positionCS.z);
                OUTPUT_SH(normInputs.normalWS, OUT.vertexSH);
                return OUT;
            }

            half4 MapFillFrag(Varyings IN) : SV_Target
            {
                UNITY_SETUP_INSTANCE_ID(IN);
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(IN);
                SetupDOTSMapLitMaterialPropertyCaches();

                SurfaceData surfaceData = (SurfaceData)0;
                surfaceData.albedo        = _Color.rgb;
                surfaceData.metallic      = _Metallic;
                surfaceData.smoothness    = _Smoothness;
                surfaceData.emission      = _EmissionColor.rgb;
                surfaceData.occlusion     = 1.0;
                surfaceData.alpha         = 1.0;

                InputData inputData = (InputData)0;
                inputData.positionWS           = IN.positionWS;
                inputData.positionCS           = IN.positionCS;
                inputData.normalWS             = NormalizeNormalPerPixel(IN.normalWS);
                inputData.viewDirectionWS      = GetWorldSpaceNormalizeViewDir(IN.positionWS);
                inputData.shadowCoord          = float4(0, 0, 0, 0);
                inputData.fogCoord             = IN.fogFactor;
                inputData.vertexLighting       = half3(0, 0, 0);
                inputData.bakedGI              = SAMPLE_GI(0, IN.vertexSH, inputData.normalWS);
                inputData.normalizedScreenSpaceUV = GetNormalizedScreenSpaceUV(IN.positionCS);
                inputData.shadowMask           = SAMPLE_SHADOWMASK(0);

                half4 color = UniversalFragmentPBR(inputData, surfaceData);
                color.rgb = MixFog(color.rgb, IN.fogFactor);
                return color;
            }
            ENDHLSL
        }
    }

    FallBack "Hidden/Universal Render Pipeline/FallbackError"
}
