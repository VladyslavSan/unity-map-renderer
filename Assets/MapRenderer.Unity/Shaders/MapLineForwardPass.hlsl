// MapLineForwardPass.hlsl — line-layer forward-lit pass for MapRenderer
//
// Origin:   Packages/com.unity.render-pipelines.universal/Shaders/LitForwardPass.hlsl
//           com.unity.render-pipelines.universal version 17.5.0 (package hash 0c18adc4ff89)
// Copyright © 2020 Unity Technologies ApS
// Licensed under the Unity Companion License — see THIRD-PARTY-NOTICES.txt
// Modified from upstream (S33 line deltas):
//   • Line Attributes: POSITION, NORMAL(+Y), TEXCOORD0(extrudeN), TEXCOORD1(side+dist),
//     TEXCOORD2(widthScale). NOT reusing MapLitForwardPass.hlsl Attributes (TEXCOORD0-2 clash).
//   • World-space extrusion performed directly in this vertex fn (cannot use shared
//     MapVertexModify hook — needs extrudeN/widthScale attributes unavailable there).
//   • Transparent fragment: InitializeStandardLitSurfaceData + UniversalFragmentPBR,
//     then alpha = fwidth-coverage * _Opacity (S05 smoothstep formula, not MapEdgeAA).
//   • No GBuffer/ShadowCaster/DepthOnly/DepthNormals passes — lines are forward-transparent
//     (Queue>=2501) and excluded from the opaque depth/GBuffer prepasses.
//
// Decisive S05 fix: world-space extrusion with normalize() strips parent scale from the
// direction vector so _Width is invariant under arbitrary object→world transforms.
//
// See THIRD-PARTY-NOTICES.txt for Unity Companion License attribution.
// See docs/lit-rendering-design.md §"Line specifics (S33)" for full design rationale.

#ifndef MAP_LINE_FORWARD_PASS_INCLUDED
#define MAP_LINE_FORWARD_PASS_INCLUDED

#include "MapLineInput.hlsl"
#include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"
#include "Packages/com.unity.render-pipelines.core/ShaderLibrary/Common.hlsl"

#if defined(LOD_FADE_CROSSFADE)
    #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/LODCrossFade.hlsl"
#endif

// ── Vertex attributes ─────────────────────────────────────────────────────────
// IMPORTANT: TEXCOORD0/1/2 carry line-specific data (NOT uv/lightmapUV/dynamicLightmapUV
// as in MapLitForwardPass.hlsl). This is why we cannot reuse that struct.
// S14: COLOR attribute added for per-feature data-driven color (baked by StyledLineTileBuilder).
//      Default white = identity multiply when no data-driven color is set.
struct LineAttributes
{
    float4 positionOS   : POSITION;
    float3 normalOS     : NORMAL;     // constant +Y lighting normal (stream 1)
    float2 extrudeN     : TEXCOORD0;  // extrusion normal in tile space (miter factor in |n|)
    float2 sideAndDist  : TEXCOORD1;  // (side ∈ {+1,−1}, distanceAlong)
    float  widthScale   : TEXCOORD2;  // per-feature width scale (default=1)
    float4 color        : COLOR;      // S14: per-vertex baked color (data-driven); white=identity
    UNITY_VERTEX_INPUT_INSTANCE_ID
};

// ── Varyings ──────────────────────────────────────────────────────────────────
struct LineVaryings
{
    float4 positionCS               : SV_POSITION;

#if defined(REQUIRES_WORLD_SPACE_POS_INTERPOLATOR)
    float3 positionWS               : TEXCOORD1;
#endif

    float3 normalWS                 : TEXCOORD2;

#ifdef _ADDITIONAL_LIGHTS_VERTEX
    half4 fogFactorAndVertexLight   : TEXCOORD5;
#else
    half  fogFactor                 : TEXCOORD5;
#endif

#if defined(REQUIRES_VERTEX_SHADOW_COORD_INTERPOLATOR)
    float4 shadowCoord              : TEXCOORD6;
#endif

    DECLARE_LIGHTMAP_OR_SH(staticLightmapUV, vertexSH, 8);

#ifdef DYNAMICLIGHTMAP_ON
    float2 dynamicLightmapUV        : TEXCOORD9;
#endif

#ifdef USE_APV_PROBE_OCCLUSION
    float4 probeOcclusion           : TEXCOORD10;
#endif

    // Line-specific: AA coverage value interpolated across the ribbon.
    float  side                     : TEXCOORD0;   // ∈ [−1, +1], used for edge AA

    // S14: per-vertex baked color (data-driven). White = identity multiply.
    float4 vColor                   : TEXCOORD3;
    // S14: gap-width inner fraction in [side]-space. 0 = solid (no gap). >0 = center hole.
    // innerFrac = (0.5 * gapM) / outerExtrudeM, where outerExtrudeM is the total extrude radius.
    // Fragment: discard pixels where |side| < innerFrac (the hollow center).
    float  innerFrac                : TEXCOORD4;
    // S43: dashU = distanceAlong / widthM — dimensionless position in line-width units.
    // Per-vertex: reads input.sideAndDist.y (cumulative arc-length in world meters) and divides
    // by widthM (world meters) — the same widthM used for extrusion. This makes dashU:
    //   • zoom-stable: widthM tracks px→m, so dashU / period is zoom-invariant.
    //   • width-coupled: doubling widthM halves dashU → doubles on/off lengths.
    // TEXCOORD7 is the only free slot after 0-6 (line-specific) and 8-10 (URP lightmap/SH).
    float  dashU                    : TEXCOORD7;

    UNITY_VERTEX_INPUT_INSTANCE_ID
    UNITY_VERTEX_OUTPUT_STEREO
};

// ── InitializeInputData ───────────────────────────────────────────────────────
// Simplified vs MapLitForwardPass: no tangent, no normal map (line has flat +Y normal),
// no parallax, no detail. Lighting normal is interpolated normalWS from the NORMAL stream.
void LineInitializeInputData(LineVaryings input, out InputData inputData)
{
    inputData = (InputData)0;

#if defined(REQUIRES_WORLD_SPACE_POS_INTERPOLATOR)
    inputData.positionWS = input.positionWS;
#endif

#if defined(DEBUG_DISPLAY)
    inputData.positionCS = input.positionCS;
#endif

    inputData.normalWS = NormalizeNormalPerPixel(input.normalWS);
    inputData.viewDirectionWS = GetWorldSpaceNormalizeViewDir(input.positionWS);

#if defined(REQUIRES_VERTEX_SHADOW_COORD_INTERPOLATOR)
    inputData.shadowCoord = input.shadowCoord;
#elif defined(MAIN_LIGHT_CALCULATE_SHADOWS)
    inputData.shadowCoord = TransformWorldToShadowCoord(inputData.positionWS);
#else
    inputData.shadowCoord = float4(0, 0, 0, 0);
#endif

#ifdef _ADDITIONAL_LIGHTS_VERTEX
    inputData.fogCoord = InitializeInputDataFog(float4(input.positionWS, 1.0), input.fogFactorAndVertexLight.x);
    inputData.vertexLighting = input.fogFactorAndVertexLight.yzw;
#else
    inputData.fogCoord = InitializeInputDataFog(float4(input.positionWS, 1.0), input.fogFactor);
#endif

#if defined(UNITY_PRETRANSFORM_TO_DISPLAY_ORIENTATION)
    float2 preRotatedScreenSpaceUV = GetNormalizedScreenSpaceUV(input.positionCS);
    switch (UNITY_DISPLAY_ORIENTATION_PRETRANSFORM)
    {
    default:
        case UNITY_DISPLAY_ORIENTATION_PRETRANSFORM_0:   inputData.normalizedScreenSpaceUV = preRotatedScreenSpaceUV; break;
        case UNITY_DISPLAY_ORIENTATION_PRETRANSFORM_90:  inputData.normalizedScreenSpaceUV = float2(1 - preRotatedScreenSpaceUV.y, preRotatedScreenSpaceUV.x); break;
        case UNITY_DISPLAY_ORIENTATION_PRETRANSFORM_180: inputData.normalizedScreenSpaceUV = float2(1 - preRotatedScreenSpaceUV.x, 1 - preRotatedScreenSpaceUV.y); break;
        case UNITY_DISPLAY_ORIENTATION_PRETRANSFORM_270: inputData.normalizedScreenSpaceUV = float2(preRotatedScreenSpaceUV.y, 1 - preRotatedScreenSpaceUV.x); break;
    }
#else
    inputData.normalizedScreenSpaceUV = GetNormalizedScreenSpaceUV(input.positionCS);
#endif

#ifdef DYNAMICLIGHTMAP_ON
    inputData.bakedGI = SAMPLE_GI(input.staticLightmapUV, input.dynamicLightmapUV, input.vertexSH, inputData.normalWS);
    inputData.shadowMask = SAMPLE_SHADOWMASK(input.staticLightmapUV);
#elif !defined(LIGHTMAP_ON) && (defined(PROBE_VOLUMES_L1) || defined(PROBE_VOLUMES_L2))
    inputData.bakedGI = SAMPLE_GI(input.vertexSH,
        GetAbsolutePositionWS(inputData.positionWS),
        inputData.normalWS,
        inputData.viewDirectionWS,
        input.positionCS.xy,
        input.probeOcclusion,
        inputData.shadowMask);
#else
    inputData.bakedGI = SAMPLE_GI(input.staticLightmapUV, input.vertexSH, inputData.normalWS);
    inputData.shadowMask = SAMPLE_SHADOWMASK(input.staticLightmapUV);
#endif
}

///////////////////////////////////////////////////////////////////////////////
//                  Vertex and Fragment functions                            //
///////////////////////////////////////////////////////////////////////////////

LineVaryings LinePassVertex(LineAttributes input)
{
    LineVaryings output = (LineVaryings)0;

    UNITY_SETUP_INSTANCE_ID(input);
    UNITY_TRANSFER_INSTANCE_ID(input, output);
    UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(output);

    // ── World-space extrusion (DECISIVE S05 fix) ──────────────────────────────
    // Problem S05 had: extrusion done in object space before transform → incorrect
    // under non-identity object→world (tile transforms, floating-origin rebasing).
    //
    // Fix: transform the unit extrusion direction to world space and NORMALIZE to strip
    // any scale from the object→world matrix. Then offset in world space by the outer radius.
    // Finally, round-trip back to object space so GetVertexPositionInputs works normally.
    //
    // extrudeN: 2D vector in tile space; |extrudeN| = miter factor (>1 for sharp corners).
    // The full miter-scaled offset = unit_dir * miter * outerRadiusM.
    //
    // S14 gap-width: outerRadiusM = 0.5*widthM + 0.5*gapM when gapM > 0, else 0.5*widthM.
    // This extrudes to the OUTER edge of the casing band; the fragment discards the inner hole.

    float miter  = length(input.extrudeN);
    // Avoid div-by-zero on degenerate vertices.
    float2 unitN = (miter > 1e-6) ? (input.extrudeN / miter) : float2(0, 0);

    // Resolve width in meters (widthScale = per-feature scale, default 1).
    float widthM = (_WidthIsPixels > 0.5)
        ? _Width * _MetersPerPixel
        : _Width;
    widthM *= input.widthScale;

    // S14: gap-width in meters (same unit conversion as width; NOT scaled by widthScale).
    float gapM = (_WidthIsPixels > 0.5)
        ? _GapWidth * _MetersPerPixel
        : _GapWidth;

    // Outer extrude radius: half the full outer band width.
    // gap=0 → outerM = 0.5*widthM (identical to previous solid path).
    // gap>0 → outerM = 0.5*gapM + widthM (gap half-width + full line width from outer edge).
    float outerM = (gapM > 1e-6) ? (0.5 * gapM + widthM) : (0.5 * widthM);

    // Object-space unit direction: 2D tile normal → 3D (x, 0, y).
    float3 unitDir_OS = float3(unitN.x, 0.0, unitN.y);

    // Transform to world space (rotation + scale) then NORMALIZE to remove scale.
    // The normalize() is the decisive step: strips parent scale → _Width is in world meters
    // regardless of the object→world scale, so acceptance #2 (non-identity scale) passes.
    float3x3 O2W = (float3x3)GetObjectToWorldMatrix();
    float3 unitDir_WS = normalize(mul(O2W, unitDir_OS));

    // Lateral world offset = unit world direction × miter factor × outerM.
    float3 offsetWS = unitDir_WS * (miter * outerM);

    // S44: line-offset — shift the band center perpendicular to the centerline by offsetM.
    // Band becomes [offsetM − ½widthM, offsetM + ½widthM] (thickness unchanged).
    //
    // Design: multiply by sideAndDist.x (∈{+1,−1}) so BOTH vertices of a station shift by
    // the same world vector (the extrusion normal flips between sides, cancelling the flip).
    //   displacement = unitDir_WS * side * (miter * offsetM)
    //
    // offsetM uses the same px→m branch as widthM (tooth 3: zoom-coupled).
    // NOT scaled by widthScale (layer-level offset, not per-feature) — same rationale as translate.
    //
    // CPU mirror: LineOffset.Displace / LineOffset.OffsetMeters (Assets/MapRenderer.Core/Style/LineOffset.cs).
    // Keep both in sync on any arithmetic change.
    //
    // Limitation: round join/cap fan vertices have per-vertex varying normals with one side sign
    // per half-fan — their displacement is non-uniform at large offset (MapLibre-parity limitation,
    // documented in LineOffset.cs). The fan pivot (normal=0) gets zero displacement → no NaN.
    // Large-offset sharp-corner miter explosion also matches MapLibre's known limitation.
    float offsetM = (_WidthIsPixels > 0.5) ? _LineOffset * _MetersPerPixel : _LineOffset;
    offsetWS += unitDir_WS * input.sideAndDist.x * (miter * offsetM);

    // Tiny +Y lift (0.001 world-meters) to prevent coplanar z-fighting with fills (req #7).
    offsetWS.y += 0.001;

    // S14: line-translate — shift the ribbon by the specified pixel offset.
    // anchor=0 (map): px→world via _MetersPerPixel. Applied in world space as XZ offset.
    // anchor=1 (viewport): approximated; apply same px→world path (documented approximation).
    // Both share the same code path here: screen-anchor approximation is acceptable per plan.
    // The translate is NOT scaled by widthScale (it is a layer-level offset, not per-feature).
    float translateScale = _MetersPerPixel;
    float3 translateWS = float3(_LineTranslate.x * translateScale, 0.0, _LineTranslate.y * translateScale);
    offsetWS += translateWS;

    // Round-trip offset back to object space, add to vertex position.
    float3x3 W2O = (float3x3)GetWorldToObjectMatrix();
    float3 posOS = input.positionOS.xyz + mul(W2O, offsetWS);

    // Standard URP vertex transform from here.
    VertexPositionInputs vertexInput = GetVertexPositionInputs(posOS);

    // S14: compute innerFrac for gap-width fragment clipping.
    // innerFrac = fraction of [0,outerM] that is the inner (gap) hole.
    // When gap=0, innerFrac=0 → no clipping in fragment.
    // innerFrac lives in [side]-space: |side| ∈ [0,1] maps to world [0, outerM].
    // Inner hole: |side| < innerFrac (in normalized side-space).
    float innerFrac = (gapM > 1e-6) ? (0.5 * gapM / outerM) : 0.0;

    // Light normal: transform +Y object-space to world space (don't normalize per-vertex —
    // GetVertexNormalInputs does this correctly; the +Y normal is constant so this is safe).
    VertexNormalInputs normalInput = GetVertexNormalInputs(input.normalOS, float4(1,0,0,1));

    half3 vertexLight = VertexLighting(vertexInput.positionWS, normalInput.normalWS);

    half fogFactor = 0;
    #if !defined(_FOG_FRAGMENT)
        fogFactor = ComputeFogFactor(vertexInput.positionCS.z);
    #endif

    output.normalWS = normalInput.normalWS;

    OUTPUT_LIGHTMAP_UV(float2(0,0), unity_LightmapST, output.staticLightmapUV);
#ifdef DYNAMICLIGHTMAP_ON
    output.dynamicLightmapUV = float2(0,0);
#endif
    OUTPUT_SH4(vertexInput.positionWS, output.normalWS.xyz,
               GetWorldSpaceNormalizeViewDir(vertexInput.positionWS),
               output.vertexSH, output.probeOcclusion);

#ifdef _ADDITIONAL_LIGHTS_VERTEX
    output.fogFactorAndVertexLight = half4(fogFactor, vertexLight);
#else
    output.fogFactor = fogFactor;
#endif

#if defined(REQUIRES_WORLD_SPACE_POS_INTERPOLATOR)
    output.positionWS = vertexInput.positionWS;
#endif

#if defined(REQUIRES_VERTEX_SHADOW_COORD_INTERPOLATOR)
    output.shadowCoord = GetShadowCoord(vertexInput);
#endif

    output.positionCS = vertexInput.positionCS;
    output.side       = input.sideAndDist.x;  // ∈ {+1,−1}, interpolated for AA

    // S14: pass per-vertex baked color and gap inner fraction to fragment.
    output.vColor     = input.color;
    output.innerFrac  = innerFrac;

    // S43: dashU = distanceAlong / widthM — reads per-vertex sideAndDist.y (world meters)
    // and divides by widthM (world meters). Result is dimensionless (line-width units).
    // widthM is already computed above (includes widthScale). Zero-guard avoids NaN.
    // Mirror of LineDash.DashCoverage's "u = distanceAlong / widthM" (CPU side of D1 formula).
    output.dashU = (widthM > 1e-6) ? (input.sideAndDist.y / widthM) : 0.0;

    return output;
}

// ── Fragment ──────────────────────────────────────────────────────────────────
// Transparent: blends with Blend SrcAlpha OneMinusSrcAlpha.
// Alpha = S05's fwidth smoothstep coverage × _Opacity (makes _Opacity functional, closes S34).
// Surface initialized via InitializeStandardLitSurfaceData (greppable, never hand-assembled).
void LinePassFragment(
    LineVaryings input
    , out half4 outColor : SV_Target0
#ifdef _WRITE_RENDERING_LAYERS
    , out uint outRenderingLayers : SV_Target1
#endif
)
{
    UNITY_SETUP_INSTANCE_ID(input);
    UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(input);

    // ── AA edge coverage (S05 formula — NOT MapEdgeAA; the ≤3px test calibrates to this) ──
    // side ∈ [−1,+1] across ribbon width. |side|=1 at edges, 0 at center.
    // S14 gap-width: also apply inner-edge AA when innerFrac > 0 (cased/hollow line).
    // Both edges feathered with fwidth(side) for symmetric ~1px AA.
    float absSide  = abs(input.side);
    float feather  = fwidth(input.side);

    // Outer edge: smoothstep from (|side|=1) inward. Identical to previous solid formula.
    float outerEdgeDist = 1.0 - absSide;
    float outerAA       = smoothstep(0.0, max(feather * _Blur, 1e-4), outerEdgeDist);

    // S14: inner edge (gap hole): smoothstep from (|side|=innerFrac) outward.
    // When innerFrac=0 (no gap), innerAA=1.0 → coverage = outerAA (unchanged, gap=0 path).
    float innerAA = (input.innerFrac > 1e-6)
        ? smoothstep(0.0, max(feather * _Blur, 1e-4), absSide - input.innerFrac)
        : 1.0;

    float coverage = outerAA * innerAA;

    // S43: dash coverage — in-shader modulo over cumulative dash-pattern length (D1).
    //
    // _DashCount == 0: identity guard (solid line, no dashing). Byte-identical to pre-S43 path.
    // _DashCount >= 2: walk on/off runs (even index=on, odd=off); AA-feather transitions with fwidth.
    //
    // dashU = distanceAlong / widthM (set in vertex, interpolated — NOT a constant).
    // period = sum of all _DashArray entries (in line-width units).
    // phase  = fmod(dashU, period) — position within one dash cycle.
    //
    // Feather: at each on/off boundary, smoothstep over a fwidth(dashU)-wide band so transitions
    // are ~1px anti-aliased. The smoothstep blends from the previous run's value to the current.
    //
    // CPU mirror: LineDash.DashCoverage (Assets/MapRenderer.Core/Style/LineDash.cs).
    // Keep both in sync on any arithmetic change.
    //
    // Deferred: round dash-caps (requires SDF or per-cap geometry). Ship butt-dash first (S43).
    //           Live zoom-step re-evaluation of _DashArray/_DashCount is handled C#-side per-frame;
    //           the HLSL reads whatever values are set by BindLinePaintToApplier at the current zoom.
    if (_DashCount >= 0.5)
    {
        // Compute period from the active entries only (unused slots are 0, contribute 0).
        float da0 = (_DashCount > 0.5) ? _DashArray.x : 0.0;
        float da1 = (_DashCount > 1.5) ? _DashArray.y : 0.0;
        float da2 = (_DashCount > 2.5) ? _DashArray.z : 0.0;
        float da3 = (_DashCount > 3.5) ? _DashArray.w : 0.0;
        float period = da0 + da1 + da2 + da3;

        if (period > 1e-5)
        {
            float dfw   = max(fwidth(input.dashU), 1e-5);
            float phase = fmod(input.dashU, period);
            if (phase < 0.0) phase += period;

            // Walk on/off runs, accumulating the coverage value.
            // Even slots (0,2) = on-run (value=1), odd slots (1,3) = off-run (value=0).
            // At each boundary we smoothstep from the previous run's value to the current.
            float dashCoverage = 1.0; // default (overwritten by first run found)
            float cursor = 0.0;
            float prevVal = 1.0; // value of the last slot in the cycle (wrapping) — slot 0 on, so last slot is off or on

            // Determine the value of the final run (for wrapping continuity).
            // With even count, last slot index = _DashCount-1. Even index=on, odd=off.
            // With 2 entries: last is slot 1 (off). With 4 entries: last is slot 3 (off).
            // Always even count (odd-count → _DashCount=0 from Pack), so last slot is off (index=odd).
            prevVal = 0.0; // last slot is always odd=off for even-count patterns

            // Unrolled over 4 slots. Break when slot index >= _DashCount.
            float spans[4];
            spans[0] = da0; spans[1] = da1; spans[2] = da2; spans[3] = da3;

            bool found = false;
            [unroll]
            for (int k = 0; k < 4; k++)
            {
                if ((float)k >= _DashCount) break;
                float segEnd = cursor + spans[k];
                float onVal = (k % 2 == 0) ? 1.0 : 0.0;

                // Is phase in this run (including feather overlap from neighbors)?
                if (!found && phase < segEnd + dfw)
                {
                    // Feather at entry edge (transition from prevVal to onVal).
                    float entryBlend = smoothstep(cursor - dfw, cursor + dfw, phase);
                    float val = lerp(prevVal, onVal, entryBlend);

                    // Feather at exit edge (transition from onVal to next run's value).
                    // Next run value: (k+1) % 2 == 0 ? 1 : 0, but since we have even count,
                    // k+1 is always valid if not at last slot. If at last slot, next is the
                    // start of the cycle (slot 0 = on = 1.0).
                    float nextVal = ((k + 1) % 2 == 0) ? 1.0 : 0.0;
                    if ((float)(k + 1) >= _DashCount) nextVal = 1.0; // wrap to slot 0 = on
                    float exitBlend = smoothstep(segEnd - dfw, segEnd + dfw, phase);
                    val = lerp(val, nextVal, exitBlend);

                    dashCoverage = val;
                    found = true;
                }
                prevVal = onVal;
                cursor = segEnd;
            }

            // If phase was not found (e.g. precision edge past all runs), treat as on.
            if (!found) dashCoverage = 1.0;

            coverage *= dashCoverage;
        }
    }

    // ── Surface data ──────────────────────────────────────────────────────────
    // uv=0 → _BaseMap="white" returns (1,1,1,1), so albedo is modulated purely by _MapColor × vColor.
    SurfaceData surfaceData;
    InitializeStandardLitSurfaceData(float2(0, 0), surfaceData);

    // [LINE DELTA S14] Modulate albedo by: vColor (per-vertex baked, data-driven) × _MapColor.
    // vColor default = white (identity) when not data-driven; _MapColor = white for data-driven layers.
    // init-then-modulate: never hand-assembled (S34 design rule + reviewer grep).
    // S14_LINE_PATTERN_HOOK: _LinePattern is read but only falls back to solid color (no sprite sampling
    // until S17). The multiply below covers both solid and pattern-hook paths with solid _MapColor.
    surfaceData.albedo *= input.vColor.rgb * _MapColor.rgb;
    // Alpha: coverage (outer+inner edge AA) × _Opacity × vertex alpha.
    // NOT surfaceData.alpha *= coverage (that multiplied the BaseMap alpha, always 1; multiply directly).
    float alpha = coverage * _Opacity * input.vColor.a;

    // ── Lighting ──────────────────────────────────────────────────────────────
    InputData inputData;
    LineInitializeInputData(input, inputData);
    SETUP_DEBUG_TEXTURE_DATA(inputData, float2(0, 0));

#if defined(_DBUFFER)
    ApplyDecalToSurfaceData(input.positionCS, surfaceData, inputData);
#endif

    half4 color = UniversalFragmentPBR(inputData, surfaceData);
    color.rgb = MixFog(color.rgb, inputData.fogCoord);

    // Output: lit RGB + transparent alpha (fwidth coverage × _Opacity).
    outColor = half4(color.rgb, alpha);

#ifdef _WRITE_RENDERING_LAYERS
    outRenderingLayers = EncodeMeshRenderingLayer();
#endif
}

#endif // MAP_LINE_FORWARD_PASS_INCLUDED
