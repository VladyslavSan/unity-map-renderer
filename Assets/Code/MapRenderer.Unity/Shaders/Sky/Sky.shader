// Sky.shader — Map/Sky: the style's `sky` as a vertical gradient, drawn as a per-camera skybox.
//
// The colour is a function of the view ray's elevation e. It is horizon-color at and below
// _MapEdgeElevation (where the rendered map ends on screen), and blends linearly (in linear colour space)
// to sky-color over _SkyHorizonBlend of the visible sky strip, up to _SkyTopElevation (the top of the
// screen). With no sky on screen it is horizon-color. SkyGradient pushes both elevations per frame.
// World +Y is the look-at up on both projections (globe tiles are rebased into the look-at tangent frame).
// SkyGradient binds it through a camera Skybox component, never RenderSettings.skybox, so the ambient
// convolution and the default reflection keep reading the scene's own skybox.
// Build inclusion: listed in GraphicsSettings' Always Included Shaders (Shader.Find at runtime).
Shader "Map/Sky"
{
    Properties
    {
        _SkyColor ("Sky Color", Color) = (0.533, 0.776, 0.988, 1)
        _HorizonColor ("Horizon Color", Color) = (1, 1, 1, 1)
        _SkyHorizonBlend ("Sky Horizon Blend", Range(0, 1)) = 0.8
        _MapEdgeElevation ("Map Edge Elevation (radians)", Float) = 0
        _SkyTopElevation ("Sky Top Elevation (radians)", Float) = 1.5707963
    }

    SubShader
    {
        Tags
        {
            "RenderType"     = "Background"
            "Queue"          = "Background"
            "PreviewType"    = "Skybox"
            "RenderPipeline" = "UniversalPipeline"
        }
        Cull Off
        ZWrite Off

        Pass
        {
            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            CBUFFER_START(UnityPerMaterial)
                float4 _SkyColor;
                float4 _HorizonColor;
                float  _SkyHorizonBlend;
                float  _MapEdgeElevation;
                float  _SkyTopElevation;
            CBUFFER_END

            struct Attributes
            {
                float4 positionOS : POSITION;
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float3 direction  : TEXCOORD0;
            };

            Varyings vert(Attributes input)
            {
                Varyings output;
                output.positionCS = TransformObjectToHClip(input.positionOS.xyz);
                // The skybox mesh is centred on the camera and unrotated: its position is the view ray.
                output.direction = input.positionOS.xyz;
                return output;
            }

            half4 frag(Varyings input) : SV_Target
            {
                float elevation = asin(clamp(normalize(input.direction).y, -1.0, 1.0));
                float span = _SkyHorizonBlend * (_SkyTopElevation - _MapEdgeElevation);
                float t = span > 1e-5 ? saturate((elevation - _MapEdgeElevation) / span) : 0.0;
                return half4(lerp(_HorizonColor.rgb, _SkyColor.rgb, t), 1.0);
            }
            ENDHLSL
        }
    }
}
