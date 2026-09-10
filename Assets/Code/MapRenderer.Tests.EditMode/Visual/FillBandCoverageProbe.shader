// TEST-ONLY throwaway probe. Not referenced by any production asset, not in the Always-Included list, and
// `Hidden/` so it never appears in a material picker — it exists to answer one question on real pixels:
// does LineCoverage's outer-edge ramp hold up when the interior carries a CONSTANT `side`, whose screen
// derivative is exactly 0?
//
// The fragment body below is a VERBATIM copy of the two lines Line_VertexExtrude.hlsl computes for the
// default (non-hairline, AA-on) path. It is a copy rather than an #include because pulling the real
// LineCoverage in drags the whole line CBUFFER, its keywords and its dash machinery into a probe that
// needs none of them — and the copy is pinned against drift by
// FillOutwardBandProbeTests.ProbeFormula_IsVerbatimTheShippedLineCoverageExpression, which reads both
// files and fails if they ever diverge.
Shader "Hidden/MapRenderer/Tests/FillBandCoverageProbe"
{
    Properties
    {
        _Color("Color", Color) = (1,1,1,1)
        // Exposed so one shader answers both the coverage question and the depth-write one (P3): the fill's
        // own depth/shadow passes hardcode ZWrite On, and this reproduces a coverage-0 fragment under it.
        [Enum(Off,0,On,1)] _ProbeZWrite("ZWrite", Float) = 0
    }

    SubShader
    {
        Tags { "RenderPipeline" = "UniversalPipeline" "RenderType" = "Transparent" "Queue" = "Transparent" }

        Pass
        {
            Blend SrcAlpha OneMinusSrcAlpha
            ZWrite [_ProbeZWrite]
            ZTest LEqual
            Cull Off

            HLSLPROGRAM
            #pragma vertex   vert
            #pragma fragment frag
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            CBUFFER_START(UnityPerMaterial)
                float4 _Color;
                float  _ProbeZWrite;
            CBUFFER_END

            struct Attributes
            {
                float4 positionOS : POSITION;
                // `side` rides TEXCOORD0.x — the band coordinate the outward-band mechanism would carry.
                float2 texcoord   : TEXCOORD0;
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float  side       : TEXCOORD0;
            };

            Varyings vert(Attributes input)
            {
                Varyings output;
                output.positionCS = TransformObjectToHClip(input.positionOS.xyz);
                output.side       = input.texcoord.x;
                return output;
            }

            half4 frag(Varyings input) : SV_Target
            {
                float absSide  = abs(input.side);
                float sideGrad = max(length(float2(ddx(input.side), ddy(input.side))), 1e-6);
                float coverage = saturate((1.0 - absSide) / sideGrad);
                return half4(_Color.rgb, coverage);
            }
            ENDHLSL
        }
    }
}
