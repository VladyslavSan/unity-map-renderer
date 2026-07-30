// Line_VertexExtrude.hlsl — bespoke map helper: world-space ribbon extrusion + coverage.
//
// This is NOT a <Layer>_<OriginalUnityName> file — it is a map-specific helper named for its
// function, following the Fill_VertexModify.hlsl precedent. URP exposes no vertex hook that
// accommodates the line's per-vertex coverage inputs (side / innerFrac / dashU), so this helper
// owns all of: the LineAttributes struct, the extrusion function, and the LineCoverage formula.
//
// Every line pass (ForwardLit, ShadowCaster, DepthOnly, DepthNormals, GBuffer) includes this file.
// Include order: Line_LitInput.hlsl → Line_VertexExtrude.hlsl → Line_<Pass>.hlsl.
//
// Line_LitInput.hlsl must be included BEFORE this file (reads CBUFFER props:
//   _Width, _WidthIsPixels, _GapWidth, _LineOffset, _LineTranslate).
//
// Authored for URP 17.5 / Unity 6000.x. Clean-room map logic, not MapLibre or Unity source.

#ifndef MAP_LINE_VERTEX_EXTRUDE_INCLUDED
#define MAP_LINE_VERTEX_EXTRUDE_INCLUDED

// DUPLICATED, deliberately: the fill layer carries a character-identical copy of the block below, in
// Map/Fill/Fill_VertexModify.hlsl. Sharing it via an include would mean reaching into the shared Common
// folder (spelled without the trailing slash on purpose — MapLayerFiles_DoNotIncludeCommonFolder greps for
// that literal substring anywhere in the file, comments included), which S66
// removed on purpose (every layer folder is self-contained; ShaderStructureTests pins it). The copies are
// kept honest by a test rather than by hand —
// ShaderStructureTests.SharedShaderBlocks_AreIdenticalAcrossLayers extracts the text between the
// MAP-SHARED-BEGIN/END sentinels in each file and requires it to match character for character. Any change
// here must be pasted verbatim into the fill copy or the gate fails; text OUTSIDE the sentinels (this
// comment included) is free to differ.

// MAP-SHARED-BEGIN: PixelsToWorld
// World metres per screen pixel at `centerWS`, measured along the UNIT direction `dirWS`.
//
// Method: pick a reference world length that projects to ~2% of NDC height at this depth
// (worldPerNdcY = |clip.w| / P[1][1] — depth-scaled under perspective, constant under ortho), then measure
// how many device pixels it actually spans along `dirWS`. Asking the projection matrix instead of modelling
// it makes this correct under foreshortening, tilt, any latitude, and either projection.
//
// Per-vertex AND per-direction, both of which matter: |clip.w| is view depth, so a far vertex probes with a
// longer ruler; and because the probe steps along `dirWS`, a tilted view measures the hard-foreshortened
// screen-down axis differently from the barely-foreshortened screen-right one. A single frame-wide
// metres-per-pixel scalar (the pre-S104 _MetersPerPixel uniform) cannot express either.
float MapPixelsToWorld(float3 centerWS, float3 dirWS)
{
    float4 clipCenter = TransformWorldToHClip(centerWS);

    float projY  = max(abs(UNITY_MATRIX_P._m11), 1e-6);
    float refMag = (abs(clipCenter.w) / projY) * 0.02;

    float4 clipRef = TransformWorldToHClip(centerWS + dirWS * refMag);

    // Fallback (~the un-foreshortened target) when either point is behind the camera and the perspective
    // divide would be meaningless.
    float refPx = 0.01 * _ScreenParams.y;
    if (clipCenter.w > 1e-5 && clipRef.w > 1e-5)
    {
        float2 ndcDelta = (clipRef.xy / clipRef.w) - (clipCenter.xy / clipCenter.w);
        refPx = length(ndcDelta * 0.5 * _ScreenParams.xy);
    }

    // Clamp the measured span so an edge-on direction (refPx → 0) cannot send the scale to infinity. This
    // is a real limit, not just a NaN guard: for a direction nearly parallel to the view axis (a southward
    // offset with the camera tilted at the horizon) the offset falls SHORT of the styled pixel count rather
    // than exploding.
    return refMag / max(refPx, 0.1);
}
// MAP-SHARED-END: PixelsToWorld

// ── Vertex attributes ─────────────────────────────────────────────────────────
// IMPORTANT: TEXCOORD0/1/2 carry line-specific data (NOT uv/lightmapUV/dynamicLightmapUV
// as in Fill_LitForwardPass.hlsl Attributes). This is why we cannot reuse that struct.
// All five line passes consume this byte-identical input — silhouette guarantee across passes.
//
// S14: COLOR attribute added for per-feature data-driven color (baked by StyledLineTileBuilder).
//      Default white = identity multiply when no data-driven color is set.
struct LineAttributes
{
    float4 positionOS   : POSITION;
    float3 normalOS     : NORMAL;     // per-vertex surface up: +Y for Mercator, geodetic normal on the globe
    float3 extrudeN     : TEXCOORD0;  // 3D across-direction (tangent-plane; Y=0 Mercator); miter factor in |n|
    float2 sideAndDist  : TEXCOORD1;  // (side ∈ {+1,−1}, distanceAlong)
    float  widthScale   : TEXCOORD2;  // per-feature width scale (default=1)
    float4 color        : COLOR;      // S14: per-vertex baked color (data-driven); white=identity
    UNITY_VERTEX_INPUT_INSTANCE_ID
};

// ── Hairline strategy constants (compile-time; deliberately NOT material properties) ──────────
// At a styled width of one device pixel the straddle's solid core vanishes: the profile is a tent peaking
// at 1.0 only where a pixel centre lands on the centreline, so a hairline reads as one bright pixel or two
// half-bright ones depending on sub-pixel phase, and shimmers under motion. There is no tunable knob and no
// successor to _AaEdgeWidth — widening a fade blurs, it never adds resolution.
#define HAIRLINE_CRISP_WIDTH_PX 1.0   // at or below this band width: effectively a step edge
#define HAIRLINE_FULL_WIDTH_PX  2.0   // at or above this: the ramp is algebraically today's
#define HAIRLINE_MIN_RAMP_PX    0.05  // narrowest ramp; 0 would divide by zero
#define HAIRLINE_MIN_WIDTH_PX   2.0   // _HAIRLINE_SOLID_CORE: floor for the RENDERED band, device px

// ── Line_VertexExtrude ────────────────────────────────────────────────────────
// Performs the S05 world-space extrusion. Called by the vertex entry point of EVERY line pass
// so all five passes (ForwardLit, ShadowCaster, DepthOnly, DepthNormals, GBuffer) share exactly
// one extrusion site — silhouette divergence is impossible by construction.
//
// Returns: extruded OBJECT-SPACE position (ready for GetVertexPositionInputs or TransformObjectToHClip).
// Out params: the per-vertex coverage inputs that must be interpolated across every pass's Varyings
//             (LineCoverage takes screen-space derivatives of them), plus the hairline energy scalar.
//
// Signature intentionally does NOT follow MapVertexModify(inout float3) — the line must emit
// three additional per-vertex outputs that a simple inout-position hook cannot express.
float3 Line_VertexExtrude(
    LineAttributes input,
    out float side,
    out float innerFrac,
    out float dashU,
    out float4 tangentOS,
    out float hairlineScale)
{
    // ── Miter / unit extrusion direction ─────────────────────────────────────
    // extrudeN: 3D across-direction in the surface tangent plane (Y=0 for the flat Mercator frame;
    // a globe projection bakes a non-zero Y). |extrudeN| = miter factor (>1 for sharp corners).
    float miter = length(input.extrudeN);
    // Avoid div-by-zero on degenerate (cap) vertices.
    float3 unitDir_OS = (miter > 1e-6) ? (input.extrudeN / miter) : float3(0, 0, 0);

    // ── S104: width & pixel-based line props resolved in SCREEN space — NO _MetersPerPixel uniform ─────────
    // For pixel widths we MEASURE the local world-metres-per-screen-pixel along the across direction (the
    // px→world scale — foreshortening-correct at any latitude/tilt/projection) instead of reading a per-frame
    // CPU uniform. width, gap, line-offset and line-translate all convert through it.

    // World-space frame. NORMALIZE strips parent scale (the S05 fix) so extrusion is scale-invariant.
    //
    // The degenerate case has to be carried through the NORMALIZE too, not just the divide above:
    // normalize(float3(0,0,0)) is NaN, a NaN position discards every triangle referencing the vertex, and
    // every round-cap fan triangle references the zero-extrudeN pivot — so the entire cap silently vanished
    // and `line-cap: round` rendered identical to butt. Zero is the right value here: it keeps the pivot on
    // the centerline, because both places it is consumed (the lateral extrusion below and the line-offset
    // shift) multiply by it.
    float3x3 objectToWorld = (float3x3)GetObjectToWorldMatrix();
    float3 acrossWS   = mul(objectToWorld, unitDir_OS);
    float3 unitDir_WS = (miter > 1e-6) ? normalize(acrossWS) : float3(0, 0, 0);
    // Per-vertex surface up from the mesh NORMAL stream (+Y for Mercator, radial for a globe) — no
    // flat-ground assumption.
    float3 upWS = normalize(TransformObjectToWorldNormal(input.normalOS));
    float3 centerWS = TransformObjectToWorld(input.positionOS.xyz);

    // px→world scale ALONG THE ACROSS-DIRECTION, for the width-family properties below. Non-pixel widths are
    // already world metres (×1); pixel widths measure it. This is the S104 measurement, extracted verbatim
    // into MapPixelsToWorld above — same call, same direction, same result.
    //
    // NOTE the scope: this scalar is correct for width/gap/offset, all of which act along `across`. It is NOT
    // a general metres-per-pixel and must not be reused for an offset in some other direction — that was the
    // line-translate bug (see below).
    float pxToWorld = (_WidthIsPixels > 0.5) ? MapPixelsToWorld(centerWS, unitDir_WS) : 1.0;

#if !defined(_EDGE_ANTIALIASING_OFF)
    // ── AA straddle pad ───────────────────────────────────────────────────────
    // HALF A DEVICE PIXEL, in world metres, ALWAYS MEASURED. `pxToWorld` above is a unit-CONVERSION
    // factor — a literal 1.0 when the width is already in world metres — not a metres-per-pixel scale,
    // so `0.5 * pxToWorld` would pad a world-unit layer by half a METRE (~1.8 px at the test camera)
    // while looking correct on a pixel-width one. For a pixel-width layer the measurement is exactly what
    // pxToWorld already holds, so it is reused rather than taken a second time.
    float aaPadWorld = 0.5 * ((_WidthIsPixels > 0.5) ? pxToWorld
                                                     : MapPixelsToWorld(centerWS, unitDir_WS));
#endif

    // Width / gap / outer radius in world metres (widthScale = per-feature; gap is layer-level).
    // widthWorld stays the STYLED width — dashU keys on it, so a clamp must not reach it.
    float widthWorld = _Width * input.widthScale * pxToWorld;

    float renderWidthWorld = widthWorld;
    hairlineScale          = 1.0;
#if defined(_HAIRLINE_SOLID_CORE) && !defined(_EDGE_ANTIALIASING_OFF)
    // ── Hairline strategy: clamp the band, pay it back in alpha ───────────────────────────────
    // Floor the rendered band at HAIRLINE_MIN_WIDTH_PX device pixels so a hairline always has a solid
    // core, then scale coverage by the true width over the clamped width so the coverage INTEGRAL is
    // still the styled width. BOTH HALVES OR NEITHER: the clamp alone renders a 1 px road twice as
    // prominent as the style asked for (tooth T8), and the compensation alone leaves the phase-dependent
    // tent it was meant to remove (tooth T7).
    //
    // aaPadWorld is half a device pixel in world metres, so 2·aaPadWorld is one device pixel — which is
    // why the pad block above had to move ahead of this one. It is measured in BOTH width modes, so
    // unlike the min-width floor this works for world-unit widths too.
    float minWidthWorld = HAIRLINE_MIN_WIDTH_PX * (2.0 * aaPadWorld);
    renderWidthWorld    = max(widthWorld, minWidthWorld);
    hairlineScale       = saturate(widthWorld / max(renderWidthWorld, 1e-9));
#endif

    // Clamping the BAND rather than outerWorld is what keeps a hollow line's gap the size the style asked
    // for; the gap term below is untouched.
    float gapWorld   = _GapWidth * pxToWorld;
    float outerWorld = (gapWorld > 1e-6) ? (0.5 * gapWorld + renderWidthWorld) : (0.5 * renderWidthWorld);

    // Min-width floor (pixel widths only): half-width never below 0.5 px ⇒ a stable 1 px hairline.
    // Still needed with the straddle: it floors the STYLED half-width, which is what the ramp's 50%
    // contour sits on.
    //
    // NOTE it cannot bind under _HAIRLINE_SOLID_CORE: that clamp already forces the extruded half-width to
    // at least HAIRLINE_MIN_WIDTH_PX/2 + 0.5 = 1.5 px, above this floor's 1.0 px, so the max() below always
    // takes the miter branch. That is deliberate — proportionality below 1 px is exactly what SolidCore
    // buys with it — but it means a sub-pixel line genuinely fades there instead of holding a 1 px
    // hairline. Recorded in the shader README's strategy comparison; do not "fix" it here.
    float minHalfWorld = (_WidthIsPixels > 0.5) ? (0.5 * pxToWorld) : 0.0;
#if defined(_EDGE_ANTIALIASING_OFF)
    float3 lateralWS = unitDir_WS * max(miter * outerWorld, minHalfWorld);
#else
    // The pad goes INSIDE the miter multiply. The miter factor is 1/cos(θ/2)
    // (LineRibbonJob.ComputeMiterNormals), defined so the PERPENDICULAR distance equals the multiplied
    // value — so miter*(outer + pad) holds the perpendicular pad at exactly 0.5 px at any corner. Padding
    // AFTER the multiply would give a perpendicular pad of pad*cos(θ/2), which SHRINKS toward zero as the
    // corner sharpens; the ramp would then have nowhere to land precisely where geometry is tightest.
    // The min-width-floor branch takes the pad un-miter'd, matching how that branch already ignores miter.
    float3 lateralWS = unitDir_WS * max(miter * (outerWorld + aaPadWorld), minHalfWorld + aaPadWorld);
#endif
    float3 offsetWS  = lateralWS;

    // ── S44: line-offset ──────────────────────────────────────────────────────
    // Shift the band centre perpendicular to the centerline. ×sideAndDist.x so both station vertices shift by
    // the same world vector. Layer-level (not per-feature). CPU mirror: LineOffset (Core/Style/LineOffset.cs).
    offsetWS += unitDir_WS * input.sideAndDist.x * (miter * _LineOffset * pxToWorld);

    // ── Surface-normal lift (0.001 world-meters) ─────────────────────────────
    // Lift along the per-vertex surface up (NOT world +Y) to avoid coplanar z-fighting with fills under
    // any projection. Applied in every pass via this single helper — the silhouette single-site guarantee.
    offsetWS += upWS * 0.001;

    // ── line-translate ── a SCREEN-PIXEL offset, converted per-axis. Layer-level, not per-feature.
    //
    // Spec: _LineTranslate.xy is in screen pixels and "negatives indicate left and up", so +x is EAST/right
    // and +y is SOUTH/down. _LineTranslateAnchor: 0 = "map" (the offset rides the map, rotating with it),
    // 1 = "viewport" (pinned to the screen). Mirrors Fill_VertexModify's MapVertexModify.
    //
    // This replaced four defects at once, all invisible to the old top-down / pixel-width tooth: the scale
    // was skipped entirely unless _WidthIsPixels (so a world-unit width layer offset by raw METRES); it was
    // measured along `across`, an unrelated direction; the offset was hardcoded into world XZ (a flat-ground
    // assumption that breaks on the globe); and +y pointed NORTH. See docs/line-translate-parity-design.md.
    //
    // The early-out is not merely an optimisation: [0,0] is the spec default, so nearly every layer takes it
    // and skips two projection round-trips per vertex.
    if (any(abs(_LineTranslate.xy) > 1e-6))
    {
        float3 axisRightWS;
        float3 axisDownWS;
        if (_LineTranslateAnchor > 0.5)
        {
            // "viewport": camera right/up in world space are the inverse-view matrix's first two basis
            // columns; screen-down is -up, matching the spec's +y = down. Exact under every projection.
            axisRightWS =  normalize(UNITY_MATRIX_I_V._m00_m10_m20);
            axisDownWS  = -normalize(UNITY_MATRIX_I_V._m01_m11_m21);
        }
        else
        {
            // "map": needs EAST at this vertex. The fill carries real per-vertex geodetic east in its Tangent
            // stream; the line has no east stream — only its own road-relative across/along axes, and using
            // THOSE would silently reimplement line-offset, which is a different property.
            //
            // So east is approximated from the scene frame: the backend rebases every tile by
            // transpose(TangentBasisAt(lookAt)) — columns east/up/north — which puts east-at-the-look-at-point
            // on world +X. One rebase serves all tiles, so the frame is continuous and there is no per-tile
            // seam. Projecting it onto THIS vertex's true tangent plane (its own geodetic normal) keeps the
            // offset in-surface, leaving a purely azimuthal residual: meridian convergence over the vertex's
            // angular distance from the look-at point, ~dLon*sin(lat). That is exactly 0 for Mercator
            // (identity rebase) and ~0 near screen centre, growing only toward the limb of a zoomed-out
            // globe. Trading it away costs 8 B on every line vertex — see the design doc's §4.1.
            float3 eastRefWS   = float3(1.0, 0.0, 0.0);
            float3 eastInPlane = eastRefWS - upWS * dot(upWS, eastRefWS);
            float  eastLen     = length(eastInPlane);
            // Degenerate only where up is parallel to the reference east — ~90° from the look-at point, i.e.
            // the very limb at z0/z1, where the direction is meaningless anyway. In that case up is
            // perpendicular to world +Z (north-at-look-at), which is therefore a valid in-plane fallback.
            float3 eastWS = (eastLen > 1e-4) ? (eastInPlane / eastLen) : float3(0.0, 0.0, 1.0);
            axisRightWS =  eastWS;
            axisDownWS  = -cross(eastWS, upWS); // north = cross(east, up); screen-down on a north-up map is south
        }

        offsetWS += axisRightWS * (_LineTranslate.x * MapPixelsToWorld(centerWS, axisRightWS))
                  + axisDownWS  * (_LineTranslate.y * MapPixelsToWorld(centerWS, axisDownWS));
    }

    // ── Round-trip to object space ────────────────────────────────────────────
    // Add world-space offset back to object-space position so GetVertexPositionInputs /
    // TransformObjectToHClip can work normally downstream.
    float3x3 worldToObject = (float3x3)GetWorldToObjectMatrix();
    float3 posOS = input.positionOS.xyz + mul(worldToObject, offsetWS);

    // ── S14: innerFrac for gap-width fragment clipping ────────────────────────
    // innerFrac = fraction of the extruded half-width that is the inner (gap) hole, in [side]-space.
    // When gap=0, innerFrac=0 → no clipping in fragment (solid line path, unchanged).
    // Inner hole: |side| < innerFrac (in normalized side-space). |side| == 1 is the PADDED edge, not the
    // styled one, so the ratio is taken against the padded outer — otherwise the gap hole would be sized
    // against a half-width the ribbon no longer has.
#if defined(_EDGE_ANTIALIASING_OFF)
    innerFrac = (gapWorld > 1e-6) ? (0.5 * gapWorld / outerWorld) : 0.0;
#else
    innerFrac = (gapWorld > 1e-6) ? (0.5 * gapWorld / (outerWorld + aaPadWorld)) : 0.0;
#endif

    // ── Out parameters ────────────────────────────────────────────────────────
    side  = input.sideAndDist.x;  // ∈ {+1,−1}, interpolated for AA
    // S43: dashU = distanceAlong / widthWorld — dimensionless position in line-width units.
    // Mirror of LineDash.DashCoverage's "u = distanceAlong / widthWorld" (CPU D1 formula).
    dashU = (widthWorld > 1e-6) ? (input.sideAndDist.y / widthWorld) : 0.0;

    // ── Tangent (along the line) — derived, projection-agnostic ───────────────
    // Built from the surface up (normalOS) and the across-direction — no extra vertex stream and no
    // flat-ground assumption. sideAndDist.x keeps it consistent across the ribbon (extrudeN flips per
    // side). Gives the line a real tangent frame (T=along, B=across, N=up) so normal-map/detail/parallax
    // work. w=-1 orients the bitangent toward +across (a 1-bit handedness calibration). Degenerate cap
    // vertices (across≈0) fall back to a stable default.
    float3 alongOS  = cross(input.normalOS, unitDir_OS * input.sideAndDist.x);
    float  alongLen = length(alongOS);
    tangentOS = (alongLen > 1e-6) ? float4(alongOS / alongLen, -1.0) : float4(1.0, 0.0, 0.0, -1.0);

    return posOS;
}

// ── LineCoverage ──────────────────────────────────────────────────────────────
// Computes the ribbon alpha from the three interpolated coverage inputs: a one-pixel STRADDLE on the outer
// edge, a matching straddle on the gap-hole cut, opt-in line-blur, and dash coverage. Used as the alpha
// multiplier in the forward pass and as the binary clip threshold (clip(LineCoverage(...) - 0.5)) in every
// depth-writing pass.
//
// FRAGMENT-STAGE function: takes screen-space derivatives of `side` and `dashU`. This is WHY all three
// inputs MUST be interpolated Varyings in every pass that calls this, never by-value constants — a constant
// has a zero derivative and the ramps would collapse to a hard edge.
//
// The two AA coverage ramps use the EUCLIDEAN gradient of `side`; `_Blur` and dash keep `fwidth`, which is
// their own features' business and not antialiasing.
float LineCoverage(float side, float innerFrac, float dashU)
{
    // ── Outer + inner edges: a strict one-pixel STRADDLE + opt-in line-blur ─────────────────────────────
    // Coverage ramps linearly 1 → 0 across exactly one device pixel CENTRED on the styled edge: half a
    // pixel inside, half a pixel outside. The vertex stage extrudes that outer half-pixel (aaPadWorld), so
    // |side| == 1 is the padded edge and the styled edge sits at 1 − 0.5·|∇side|, where the ramp below
    // reads exactly 0.5. Apparent width is therefore unchanged, and the interior is a == 1 — which is what
    // lets a cased road's fill sit on its casing without bleeding it (the failure that got the previous,
    // INSET, fade removed). The ramp width is a compile-time constant; there is nothing bindable to widen.
    //
    // `_Blur` (MapLibre line-blur) stays a SEPARATE, opt-in soft edge (default 0 ⇒ no-op) — it is a style
    // property, not antialiasing, and it multiplies on top of this.
    float absSide = abs(side);

#if !defined(_EDGE_ANTIALIASING_OFF)
    // ── How wide "one device pixel" is, in side-units — the EUCLIDEAN (L2) gradient, NOT fwidth ────
    // `side` spans ±1 over the padded half-width H, so the true gradient magnitude is exactly 1/H and a
    // ramp divided by it is exactly one device pixel wide, whatever the silhouette's screen angle.
    //
    // fwidth(x) is abs(ddx(x)) + abs(ddy(x)) — the L1/MANHATTAN length — which over-reads the true length
    // by |cos θ| + |sin θ| ∈ [1, √2]. Dividing by that stretches the ramp to 1.41 px on a 45° diagonal
    // while an axis-aligned line keeps 1.00 px, so the same road renders softer AND thinner where it runs
    // diagonally: the integral is W + 1 − c, i.e. it loses 0.41 px of ink at 45°. Direction-dependent
    // antialiasing quality is invisible to a horizontal-fixture tooth and very visible to the eye — it is
    // the second, never-diagnosed defect of the AA removed in 0b910c7, and it survived into the rebuild.
    //
    // Computed once and shared by both ramps below. `_Blur` and dash deliberately keep fwidth: they are
    // separate style features, not antialiasing.
    float sideGrad = max(length(float2(ddx(side), ddy(side))), 1e-6);
#endif

#if defined(_HAIRLINE_HARD) && !defined(_EDGE_ANTIALIASING_OFF)
    // ── Styled band width, from the gradient above — no new vertex data ───────────────────────
    // |side| = 1 at the padded lateral edge, so 1/|∇side| IS the padded half-width in device pixels and
    // the styled half-width is that minus the fixed 0.5 px pad.
    float halfPadPx    = 1.0 / sideGrad;
    float halfStyledPx = max(halfPadPx - 0.5, 0.0);
    // Thickness of what is actually PAINTED. Solid line: the full styled width. Hollow/cased line
    // (line-gap-width): the ring runs from |side| = innerFrac out to the styled edge, so measure THAT —
    // the outer radius would badly over-read a thin casing ring. innerFrac = 0 reduces to 2·halfStyledPx.
    float bandPx = (innerFrac > 1e-6) ? max(halfStyledPx - innerFrac * halfPadPx, 0.0)
                                      : 2.0 * halfStyledPx;
    // 1 ⇒ today's ramp exactly; → 0 ⇒ a step. smoothstep returns exactly 1 above its upper edge, so the
    // transition band is closed rather than asymptotic and wide lines are untouched.
    float rampPx = lerp(HAIRLINE_MIN_RAMP_PX, 1.0,
                        smoothstep(HAIRLINE_CRISP_WIDTH_PX, HAIRLINE_FULL_WIDTH_PX, bandPx));
#endif

    // Outer edge.
#if defined(_EDGE_ANTIALIASING_OFF)
    float coverage = 1.0;
#elif defined(_HAIRLINE_HARD)
    // Narrowed, but still CENTRED on the styled edge at |side| = 1 − 0.5·|∇side| — the same centring the
    // gap-hole cut below uses. Narrowing without re-centring leaves the hard edge half a pixel out and
    // renders the line a pixel fat: the S70 outset artefact, which tooth T6b catches. rampPx = 1 reduces
    // this to saturate((1 − |side|)/sideGrad), the default expression, algebraically.
    float coverage = saturate(((1.0 - 0.5 * sideGrad) - absSide) / (sideGrad * rampPx) + 0.5);
#else
    float coverage = saturate((1.0 - absSide) / sideGrad);
#endif

    // S14 inner edge (gap hole) for cased/hollow lines: drop the inner |side| < innerFrac region.
    if (innerFrac > 1e-6)
    {
#if defined(_EDGE_ANTIALIASING_OFF)
        coverage *= step(innerFrac, absSide);
#elif defined(_HAIRLINE_HARD)
        // Same rampPx as the outer edge. Hardening only the outer silhouette would leave a hairline-thin
        // casing ring crisp outside and still shimmering inside — on the same ring — which is worse than
        // either consistent choice, and cased roads are exactly where a thin ring occurs.
        coverage *= saturate((absSide - innerFrac) / (sideGrad * rampPx) + 0.5);
#else
        // NOT the outer formula. The outer edge needs the geometric pad because no triangle exists beyond
        // |side| = 1; the inner edge has ribbon on BOTH sides of it, so a symmetric ±0.5 px straddle centred
        // on |side| = innerFrac is reachable directly — and `+ 0.5` is exactly what centres it. Dropping it
        // shifts the gap hole half a pixel outward.
        coverage *= saturate((absSide - innerFrac) / sideGrad + 0.5);
#endif
    }

    // line-blur (MapLibre line-blur; opt-in soft edge): feathers the outer _Blur px inward. 0 ⇒ no-op (hard).
    if (_Blur > 1e-6)
    {
        float feather = max(fwidth(side), 1e-6);
        coverage *= smoothstep(0.0, feather * _Blur, 1.0 - absSide);
    }

    // ── S43: dash coverage ────────────────────────────────────────────────────
    // _DashCount == 0: identity guard (solid line, no dashing). Byte-identical to pre-S43 path.
    // _DashCount >= 2: walk on/off runs (even index=on, odd=off); AA-feather transitions with fwidth.
    //
    // dashU = distanceAlong / widthWorld (set in vertex, interpolated — NOT a constant).
    // period = sum of all _DashArray entries (in line-width units).
    // phase  = fmod(dashU, period) — position within one dash cycle.
    //
    // CPU mirror: LineDash.DashCoverage (Assets/Code/MapRenderer.Core/Style/LineDash.cs).
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
            float dfw   = max(fwidth(dashU), 1e-5);
            float phase = fmod(dashU, period);
            if (phase < 0.0) phase += period;

            // Walk on/off runs, accumulating the coverage value.
            // Even slots (0,2) = on-run (value=1), odd slots (1,3) = off-run (value=0).
            // At each boundary we smoothstep from the previous run's value to the current.
            float dashCoverage = 1.0;
            float cursor = 0.0;
            float prevVal = 0.0; // last slot is always odd=off for even-count patterns

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

    return coverage;
}

#endif // MAP_LINE_VERTEX_EXTRUDE_INCLUDED
