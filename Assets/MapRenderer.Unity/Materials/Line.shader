// GPU-driven line shader for unity-map-renderer (S05).
// Centerline mesh: vertex shader extrudes each vertex laterally by ½·width along the stored
// extrusion normal. Fragment shader adds ~1px AA edge feather via fwidth.
//
// Vertex attribute layout (matches LineMeshBuilder):
//   POSITION   float3  — centerline: (east, 0, north) in world meters.
//   TEXCOORD0  float2  — extrusion normal in meter space (|n|=1 for straight/bevel/round; |n|>1 for miter).
//   TEXCOORD1  float2  — (side ∈ {+1,−1}, distanceAlong). Side used for AA; distanceAlong reserved for S14.
//   TEXCOORD2  float   — widthScale (reserved for S12 per-feature width). Default = 1.
//
// Width units: _WidthIsPixels=0 → _Width is world meters; _WidthIsPixels=1 → _Width * _MetersPerPixel.
// For a top-down orthographic camera: _MetersPerPixel = 2 * orthographicSize / pixelHeight (exact).
//
// Clean-room: implemented from first principles; not derived from MapLibre source.
Shader "MapRenderer/Line"
{
    Properties
    {
        _Width          ("Width", Float) = 2.0
        [Toggle]
        _WidthIsPixels  ("Width In Pixels", Float) = 0.0
        _MetersPerPixel ("Meters Per Pixel", Float) = 1.0
        _Color          ("Color", Color) = (1, 1, 1, 1)
        _Opacity        ("Opacity", Range(0, 1)) = 1.0
        _Blur           ("Blur (AA feather)", Range(0, 4)) = 1.0
    }

    SubShader
    {
        // Painter's algorithm: ZWrite Off so coplanar layers sort correctly (ARCHITECTURE §2).
        // Cull Off: ribbon is two-sided.
        // Transparent queue so ZWrite Off doesn't conflict with depth writes.
        Tags
        {
            "RenderType"      = "Transparent"
            "Queue"           = "Transparent"
            "RenderPipeline"  = "UniversalPipeline"
            "IgnoreProjector" = "True"
        }

        Pass
        {
            Name "LineFill"

            Blend SrcAlpha OneMinusSrcAlpha
            ZWrite Off
            Cull Off

            HLSLPROGRAM
            #pragma vertex   vert
            #pragma fragment frag
            #pragma target   3.5

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            // ── Uniforms ──────────────────────────────────────────────────────────────────
            CBUFFER_START(UnityPerMaterial)
                float _Width;
                float _WidthIsPixels;
                float _MetersPerPixel;
                float4 _Color;
                float  _Opacity;
                float  _Blur;
            CBUFFER_END

            // ── Vertex attributes ─────────────────────────────────────────────────────────
            struct Attributes
            {
                float3 positionOS  : POSITION;   // centerline (object space = world space here)
                float2 extrudeN    : TEXCOORD0;  // extrusion normal (xy, meter space)
                float2 sideAndDist : TEXCOORD1;  // (side ∈ {+1,−1}, distanceAlong)
                float  widthScale  : TEXCOORD2;  // per-feature width scale (S12 reserved)
            };

            struct Varyings
            {
                float4 positionCS  : SV_POSITION;
                float  side        : TEXCOORD0;  // interpolated ∈ [−1, +1] across ribbon width
            };

            // ── Vertex shader ─────────────────────────────────────────────────────────────
            Varyings vert(Attributes IN)
            {
                Varyings OUT;

                // Resolve width in meters.
                float widthM = (_WidthIsPixels > 0.5)
                    ? _Width * _MetersPerPixel
                    : _Width;

                // Apply per-feature width scale (reserved S12; currently =1).
                widthM *= IN.widthScale;

                // Extrude the centerline position laterally by ½·widthM along the extrusion normal.
                // The normal encodes the miter factor for miter joins (|n|>1), so this single formula
                // handles all join types uniformly.
                float3 worldPos = IN.positionOS;
                worldPos.x += IN.extrudeN.x * 0.5 * widthM;
                worldPos.z += IN.extrudeN.y * 0.5 * widthM;  // 2D normal (nx,ny) → world (nx,0,ny)

                OUT.positionCS = TransformObjectToHClip(worldPos);
                OUT.side       = IN.sideAndDist.x;
                return OUT;
            }

            // ── Fragment shader ───────────────────────────────────────────────────────────
            half4 frag(Varyings IN) : SV_Target
            {
                // AA edge feather: |side| interpolates 0→1 from center to edge.
                // fwidth gives the screen-space derivative (~1px gradient) → feather spans ~1px.
                float edgeDist = 1.0 - abs(IN.side);   // 0 at edge, 1 at center
                float feather  = fwidth(IN.side);
                float alpha    = smoothstep(0.0, max(feather * _Blur, 1e-4), edgeDist);

                half4 col = half4(_Color.rgb, _Color.a * _Opacity * alpha);
                return col;
            }

            ENDHLSL
        }
    }

    // Fallback for non-URP (renders invisible rather than magenta — safer than erroring).
    FallBack Off
}
