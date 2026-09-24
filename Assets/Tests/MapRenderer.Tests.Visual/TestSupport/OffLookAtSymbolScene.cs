// Unity EditMode only — real TiltedGroundScene (MapCamera + Camera/RenderTexture) + a REAL
// SymbolPlacementSystem.Tick. NOT registered in Tools/core-tests/core-tests.csproj.
//
// THE OFF-LOOK-AT, MULTI-DEPTH MEASUREMENT FIXTURE: SIX real symbols at TWO view depths — a curved symbol at
// the look-at (depth d) and its twin at ~2d, from the SAME cell, TextSizePx and baked advances; the same pair
// along the RECEDING direction; and a point symbol at each anchor. It reads each glyph's WORLD anchor off the
// built mesh, its screen position through the LIVE camera, and its view depth.
//
// Non-obvious why: at the look-at a screen-constant ruler and a world-welded ruler coincide, so only an
// off-look-at anchor tells them apart. The curved symbols are map-pitched, so the LineCenter anchor resolves
// to the road's WORLD midpoint; the receding roads are symmetric in metres so it lands where the fixture aims.
//   • The RECEDING arm is the measurement arm (asserted in MapPitchedWorldArcLayoutTests).
//   • The CROSS-AZIMUTH arm sits at one depth `w`: a pure 1/w law, and the anchor is walk-invariant because
//     the projection is affine there. So it alone cannot tell a world walk from a scaled screen walk.
//   • The headline is a RATIO of far/near spacing in one frame, so any uniform scale error cancels.
// NO METRE LITERALS: every world size is `k · scene.MetresPerDevicePixel`; a bare metre is sub-pixel here.
// ModelPredictedScreenSpacingPx, the oracle, calls none of the staging, layout or rendering code.

#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using NUnit.Framework;
using Unity.Collections;
using Unity.Mathematics;
using UnityEngine;
using MapRenderer.Core.Geo;
using MapRenderer.Core.Style.Symbol;
using MapRenderer.Core.Text;
using MapRenderer.Core.Text.Placement;
using MapRenderer.Tests.Text.Placement;
using MapRenderer.Unity.Rendering.Backend;
using MapRenderer.Unity.Text;
using MapRenderer.Unity.Text.Placement;

namespace MapRenderer.Tests
{
    /// <summary>The six symbols this fixture stages, each on its OWN tile key (hence its own slot mesh, so no
    /// two symbols' glyph vertices ever share a buffer).</summary>
    internal enum OffLookAtSymbolId
    {
        CrossNear, CrossFar, RecedingNear, RecedingFar, PointNear, PointFar,
    }

    /// <summary>
    /// Configuration for <see cref="OffLookAtSymbolScene"/>. A plain data carrier with
    /// object-initializer construction.
    /// </summary>
    internal sealed class OffLookAtSymbolSceneConfig
    {
        // Plain { get; set; }, not init: MapRenderer.Tests.EditMode has no IsExternalInit polyfill of its
        // own, by the same documented choice TiltedGroundSceneConfig states.

        public double TiltDegrees { get; set; } = 55.0;

        public double Zoom { get; set; } = 8.0;

        public int SizePx { get; set; } = 512;

        /// <summary>The designed far/near VIEW-depth ratio. 2.0: large enough that the two competing rulers
        /// differ by a factor of two, small enough that the far anchor sits on-screen with ~100 px of margin
        /// at this pose (the horizon is OFF-frame here, so the whole ground plane is visible).</summary>
        public double TargetDepthRatio { get; set; } = 2.0;

        public int GlyphCount { get; set; } = 5;

        /// <summary>`text-size`. 48 with <see cref="AdvanceBakedPx"/> 20 gives scale = 2 and a 40-screen-px
        /// glyph gap — comfortably wider than the 'F' cell's ~19 px screen width, so the ink runs are
        /// separable (asserted, not assumed).</summary>
        public float TextSizePx { get; set; } = 48f;

        /// <summary>The fixture's OWN per-glyph em advance, in baked px (<c>TextQuadLayout.OneEm</c> = 24).
        /// A fixture constant on purpose: it is never read out of <c>CurvedTextLayout</c> /
        /// <c>TextQuadLayout.Layout</c>, so the oracle has one less self-reference.</summary>
        public float AdvanceBakedPx { get; set; } = 20f;

        /// <summary>How much longer than the symbol each road is, per side — guards
        /// <c>SymbolStagingMath.StageCurved</c>'s <c>centerArc ± halfSpan</c> spill return. That gate is in
        /// world METRES for a map-pitched symbol, so this multiplies the symbol's world span; the
        /// cross-azimuth roads still reach it through a screen solve (see <see cref="Build"/>).</summary>
        public double SpillMargin { get; set; } = 1.5;

        /// <summary>The device-pixel ratio the whole scene is built at. Passed straight through to
        /// <c>TiltedGroundSceneConfig</c> and so to <c>MapCamera</c>. Non-obvious why: the world geometry is
        /// DPR-INVARIANT. At DPR 2 <c>MetresPerDevicePixel</c> halves, so
        /// <see cref="OffLookAtSymbolScene.AdvanceWorldMetres"/> stays the same metres. A ruler that dropped
        /// the ratio would use per-DEVICE-px metres and fail that same expectation.</summary>
        public double DevicePixelRatio { get; set; } = 1.0;
    }

    /// <summary>One glyph's measured position, all three readings taken from the SAME world point.</summary>
    internal readonly struct GlyphMeasurement
    {
        /// <summary>Unity-world position of the glyph's staged anchor —
        /// <c>WorldSlotTransform(...).TransformPoint(AnchorLocal)</c>, so no RTC, tile origin or
        /// floating-origin rebase is re-derived by this fixture.</summary>
        public readonly double3 WorldUnity;

        /// <summary>Its projection through the LIVE camera, in device px.</summary>
        public readonly double2 ScreenPx;

        /// <summary>Its VIEW depth in metres (<see cref="GroundRuler.ViewDepthMetres"/>) — not NDC depth.</summary>
        public readonly double ViewDepthMetres;

        public GlyphMeasurement(double3 worldUnity, double2 screenPx, double viewDepthMetres)
        {
            WorldUnity      = worldUnity;
            ScreenPx        = screenPx;
            ViewDepthMetres = viewDepthMetres;
        }
    }

    /// <summary>One symbol's per-glyph measurements plus the consecutive-glyph spacings derived from them.</summary>
    internal readonly struct SymbolMeasurement
    {
        public readonly GlyphMeasurement[] Glyphs;

        /// <summary>Metres between consecutive glyph anchors. The RTC translation cancels in the difference,
        /// so this reading survives even a misread transform chain.</summary>
        public readonly double[] WorldSpacingM;

        /// <summary>Device px between consecutive glyph anchors' projections.</summary>
        public readonly double[] ScreenSpacingPx;

        /// <summary>The FIXTURE'S OWN intended anchor for this symbol (not a measured value) — the point the
        /// oracle is evaluated at, and what the placement teeth compare the staged geometry against. It holds on
        /// all four curved arms: the WORLD arc walk anchors by world arc length, and the receding roads are
        /// symmetric in metres.</summary>
        public readonly double3 AnchorWorldUnity;

        public readonly double2 AnchorScreenPx;
        public readonly double  AnchorViewDepthMetres;

        public SymbolMeasurement(GlyphMeasurement[] glyphs, double[] worldSpacingM, double[] screenSpacingPx,
            double3 anchorWorldUnity, double2 anchorScreenPx, double anchorViewDepthMetres)
        {
            Glyphs                = glyphs;
            WorldSpacingM         = worldSpacingM;
            ScreenSpacingPx       = screenSpacingPx;
            AnchorWorldUnity      = anchorWorldUnity;
            AnchorScreenPx        = anchorScreenPx;
            AnchorViewDepthMetres = anchorViewDepthMetres;
        }
    }

    /// <summary>
    /// The off-look-at fixture. Build with <see cref="Create"/>, read <see cref="Measure"/> /
    /// <see cref="ModelPredictedScreenSpacingPx"/> / <see cref="InkPixels"/>, dispose.
    /// </summary>
    internal sealed class OffLookAtSymbolScene : IDisposable
    {
        public readonly OffLookAtSymbolSceneConfig Config;
        public readonly TiltedGroundScene Scene;

        // ── Frame geometry, all derived from the LIVE camera and asserted, never assumed ────────────────

        /// <summary>ĝ — the receding ground direction (world XZ, y = 0). Derived from the camera's own
        /// forward, never hard-coded to a world axis.</summary>
        public readonly double3 RecedingDir;

        /// <summary>ĉ = normalize(cross(worldUp, ĝ)) — the cross-azimuth ground direction. Perpendicular to
        /// the view axis, so view depth is CONSTANT along it (a tooth pins that).</summary>
        public readonly double3 CrossAzimuthDir;

        public readonly double3 NearAnchorWorldUnity;
        public readonly double3 FarAnchorWorldUnity;
        public readonly double  NearAnchorViewDepthMetres;
        public readonly double  FarAnchorViewDepthMetres;

        /// <summary>The ACHIEVED far/near view-depth ratio; a tooth asserts it against
        /// <see cref="OffLookAtSymbolSceneConfig.TargetDepthRatio"/>.</summary>
        public double AchievedDepthRatio => FarAnchorViewDepthMetres / NearAnchorViewDepthMetres;

        public readonly double MetresPerDevicePixel;

        /// <summary>
        /// The ruler the settled model is expressed in: metres per LOGICAL screen pixel. `text-size` and the
        /// baked advances are logical px, and <c>MapCamera.MetresPerDevicePixel</c> is per DEVICE px.
        /// Non-obvious why: it must stay a product of the fixture's DPR constant and the camera's ruler, never a
        /// production value that already combines them, or both sides drop the ratio together. Limitation: an
        /// error inside <c>MetresPerDevicePixel</c> is invisible here; <see cref="DevicePixelRatioSnapshotTests"/>
        /// pins it.</summary>
        public double MetresPerLogicalPixel => MetresPerDevicePixel * Config.DevicePixelRatio;

        /// <summary>THE MODEL, in one line: one glyph advance is a fixed WORLD length,
        /// <c>(AdvanceBakedPx / OneEm) · TextSizePx · metresPerLogicalPixel</c>. Two fixture constants, a unit
        /// definition that cancels in every ratio, and the frame ruler above. Nothing measured; this is what
        /// the world-spacing tooth asserts the staged spacing against.</summary>
        public double AdvanceWorldMetres =>
            Config.AdvanceBakedPx / TextQuadLayout.OneEm * Config.TextSizePx * MetresPerLogicalPixel;

        /// <summary><c>|projectionMatrix.m11|</c> of the live camera.</summary>
        public readonly double AbsP11;

        /// <summary><c>camera.pixelHeight</c> of the live camera.</summary>
        public readonly double ViewportHeightPx;

        /// <summary>The glyph advance in SCREEN px under today's screen-arc layout:
        /// <c>AdvanceBakedPx · TextSizePx / OneEm</c>. A fixture constant, printed for the report.</summary>
        public readonly double GlyphGapScreenPx;

        /// <summary>The whole symbol's screen span, <c>(GlyphCount − 1) · GlyphGapScreenPx</c>.</summary>
        public readonly double SymbolSpanScreenPx;

        /// <summary>The 'F' cell's own screen width at <see cref="OffLookAtSymbolSceneConfig.TextSizePx"/> —
        /// must stay below <see cref="GlyphGapScreenPx"/> or the ink runs merge.</summary>
        public readonly double GlyphCellScreenWidthPx;

        /// <summary>The 'F' cell's own width in BAKED px, recovered from
        /// <see cref="GlyphCellScreenWidthPx"/> by dividing out the one scale that produced it. A tooth divides
        /// <see cref="OffLookAtSymbolSceneConfig.AdvanceBakedPx"/> by it to DERIVE the advance-to-cell constant,
        /// in which every scale cancels, so the constant tracks a re-tuned cell or advance.</summary>
        public double GlyphCellWidthBakedPx
            => GlyphCellScreenWidthPx / (Config.TextSizePx / TextQuadLayout.OneEm);

        /// <summary>Per-symbol road half-lengths in metres, <c>[negative side, positive side]</c> — reported
        /// because the receding roads are capped by reachability (see <see cref="SolveGroundOffsetForScreenPx"/>).</summary>
        public readonly Dictionary<OffLookAtSymbolId, double2> RoadHalfLengthsM = new();

        /// <summary>Per-symbol achieved road half-extents in SCREEN px, <c>[negative side, positive side]</c>.</summary>
        public readonly Dictionary<OffLookAtSymbolId, double2> RoadHalfExtentsPx = new();

        /// <summary>The rendered frame, already <c>FlipRowsVertically</c>'d — row 0 is the TOP scanline, so a
        /// screen y maps to row <c>SizePx − 1 − y</c>.</summary>
        public readonly Color32[] InkPixels;

        /// <summary>Half-height, in rows, of the band the ink arm reads around each cross-azimuth symbol's
        /// projected anchor. Wide enough to contain the whole ~34-row glyph cell, narrow enough that the two
        /// bands (≈158 rows apart at this pose) stay disjoint.</summary>
        public const int InkBandHalfRows = 45;

        /// <summary>The COLUMN analogue of <see cref="InkBandHalfRows"/>, for a RECEDING symbol. A
        /// receding symbol runs screen-VERTICALLY, so its ink is segmented along ROWS inside a column band.
        /// The band spans the cell's cross-road arm, which is not foreshortened (~34 px near, smaller far), so
        /// the cross arm's 45 half-width is generous here too.</summary>
        public const int InkBandHalfCols = 45;

        private readonly Dictionary<OffLookAtSymbolId, SymbolMeasurement> _measurements = new();
        private readonly Dictionary<OffLookAtSymbolId, int> _vertexCounts = new();
        // The raw stream-0 vertices each symbol's slot mesh carried in the GEOMETRY pass, captured there
        // so a later re-tick cannot change what a tooth reads. See Vertices(id) / RenderIsolated(id).
        private readonly Dictionary<OffLookAtSymbolId, WorldBillboardVertex[]> _vertices = new();
        private readonly Dictionary<OffLookAtSymbolId, SymbolTileBuffer> _symbolsById = new();
        private readonly SymbolPlacementSystem _system;
        private readonly TestSymbolPlan _plan;
        // The construction passes' frame (a struct copy), so RenderIsolated re-Ticks with the same camera and
        // floating origin.
        private SceneFrame _frame;
        private readonly GlyphAtlasTexture _atlasF;
        private readonly GlyphAtlasTexture _atlasA;
        private readonly SnapshotRenderer _snapshot;

        private OffLookAtSymbolScene(
            OffLookAtSymbolSceneConfig config, TiltedGroundScene scene,
            double3 recedingDir, double3 crossAzimuthDir,
            double3 nearAnchor, double3 farAnchor, double nearDepth, double farDepth,
            double metresPerDevicePixel, double absP11, double viewportHeightPx,
            double glyphGapScreenPx, double symbolSpanScreenPx, double glyphCellScreenWidthPx,
            Color32[] inkPixels, SymbolPlacementSystem system, TestSymbolPlan plan,
            GlyphAtlasTexture atlasF, GlyphAtlasTexture atlasA, SnapshotRenderer snapshot)
        {
            Config                    = config;
            Scene                     = scene;
            RecedingDir               = recedingDir;
            CrossAzimuthDir           = crossAzimuthDir;
            NearAnchorWorldUnity      = nearAnchor;
            FarAnchorWorldUnity       = farAnchor;
            NearAnchorViewDepthMetres = nearDepth;
            FarAnchorViewDepthMetres  = farDepth;
            MetresPerDevicePixel      = metresPerDevicePixel;
            AbsP11                    = absP11;
            ViewportHeightPx          = viewportHeightPx;
            GlyphGapScreenPx          = glyphGapScreenPx;
            SymbolSpanScreenPx         = symbolSpanScreenPx;
            GlyphCellScreenWidthPx    = glyphCellScreenWidthPx;
            InkPixels                 = inkPixels;
            _system                   = system;
            _plan                     = plan;
            _atlasF                   = atlasF;
            _atlasA                   = atlasA;
            _snapshot                 = snapshot;
        }

        public UnityEngine.Camera UnityCamera => Scene.UnityCamera;

        /// <summary>This symbol's per-glyph measurements, taken from the GEOMETRY pass (see
        /// <see cref="Create"/>'s two-pass note).</summary>
        public SymbolMeasurement Measure(OffLookAtSymbolId id) => _measurements[id];

        /// <summary>Stream-0 vertex count on this symbol's slot mesh — 4 per staged glyph.</summary>
        public int VertexCount(OffLookAtSymbolId id) => _vertexCounts[id];

        /// <summary>This symbol's raw stream-0 <see cref="WorldBillboardVertex"/>s, in TL/TR/BR/BL order
        /// per glyph, as <c>WorldSymbolRenderer.Emit</c> wrote them, for teeth about the corner OFFSETS.
        /// Non-obvious why: the ink pass re-Ticks and overwrites the slot meshes, so they are captured in the
        /// GEOMETRY pass; a lazy read would return another frame's geometry.</summary>
        public WorldBillboardVertex[] Vertices(OffLookAtSymbolId id) => _vertices[id];

        /// <summary>The inclusive ink COLUMN band around <paramref name="id"/>'s projected anchor — the
        /// column analogue of <see cref="RowBandFor"/>, for a RECEDING symbol (which runs screen-vertically,
        /// so its ink runs are segmented along ROWS within a band of COLUMNS).</summary>
        public void ColumnBandFor(OffLookAtSymbolId id, out int colFrom, out int colTo)
        {
            int centreCol = (int)math.round(_measurements[id].AnchorScreenPx.x);
            colFrom = centreCol - InkBandHalfCols;
            colTo   = centreCol + InkBandHalfCols;
        }

        /// <summary>
        /// Re-Ticks the scene with ONLY <paramref name="id"/> and renders it, returning a fresh
        /// row-flipped frame (row 0 = the TOP scanline, same convention as <see cref="InkPixels"/>).
        /// Non-obvious why: the two receding symbols project into nearly the same columns, so no band can
        /// attribute their ink; one symbol per render removes the question. It runs only when a tooth asks,
        /// and every other reading was captured in the geometry pass, so the re-Tick changes none of them.
        /// </summary>
        public Color32[] RenderIsolated(OffLookAtSymbolId id)
            => RenderIsolated(id, out _, out _, out _);

        /// <summary>
        /// The same isolated pass, also returning the COLLISION BOXES, the RAW VERTICES and the slot transform
        /// from the same Tick, so a box and its quad come from one staging pass. Non-local invariant: with one
        /// symbol, <c>boxes[g]</c> pairs with <c>vertices[4g … 4g+3]</c>, because staging appends both in one
        /// iteration and <c>CollisionJob</c> sorts candidates, not boxes. Callers must still ASSERT
        /// <c>boxes.Length == vertices.Length / 4</c>.
        /// </summary>
        public Color32[] RenderIsolated(OffLookAtSymbolId id, out SymbolBox[] boxes,
            out WorldBillboardVertex[] vertices, out Transform slot)
        {
            SymbolTileBuffer buffer = _symbolsById[id];
            // Duplicate Tick — the collision verdict is harvested one Tick late.
            _system.Tick(in _frame, _plan.Build(buffer), _atlasF);
            _system.Tick(in _frame, _plan.Build(buffer), _atlasF);
            int expected = buffer.Symbols[0].GlyphCount > 0 ? Config.GlyphCount : 1;
            Assert.That(_system.LastQuadCount, Is.EqualTo(expected),
                $"P-M/W2 precondition ({id}): the isolated ink pass must stage exactly {expected} quads, got " +
                $"{_system.LastQuadCount}. A zero means this label spilled its road or was culled when it was " +
                "the only one in the frame, which the geometry pass did not see.");

            NativeArray<SymbolBox> staged = _system.LastStagedBoxes();
            boxes = new SymbolBox[_system.LastBoxCount];
            for (int b = 0; b < boxes.Length; b++) boxes[b] = staged[b];

            long tileKey = buffer.Symbols[0].TileKey;
            Assert.That(_system.TryGetWorldSlotMesh(tileKey, 0, SymbolKind.Text, out Mesh mesh), Is.True,
                $"W3 precondition ({id}): no world slot mesh for tile key {tileKey} — the label emitted nothing.");
            WorldMeshReadback.Read(mesh, out vertices, out _);
            slot = _system.WorldSlotTransform(tileKey, 0, SymbolKind.Text);
            Assert.That(slot, Is.Not.Null, $"W3 precondition ({id}): slot {tileKey} was never presented.");

            Scene.Render(_snapshot);
            var pixels = (Color32[])_snapshot.Pixels.Pixels.Clone();
            WorldSymbolInkAnalysis.FlipRowsVertically(pixels, Config.SizePx, Config.SizePx);
            return pixels;
        }

        /// <summary>
        /// THE ORACLE. The screen spacing the settled model requires between consecutive glyphs of
        /// <paramref name="id"/>: a glyph advance is fixed ONCE in world metres at the top-down ruler,
        /// <c>Δ_world = (Δ_baked / OneEm) · TextSizePx · mpp</c>, and a world-fixed displacement perpendicular
        /// to the view axis at view depth <c>w</c> projects to <c>Δ_world · |P11| · H / (2w)</c>.
        /// Non-obvious why: it may read no MEASURED value except the anchor's <c>w</c>, or it becomes
        /// self-referential. For a receding symbol it is a single-depth linearisation, reported, never
        /// asserted.
        /// </summary>
        public double ModelPredictedScreenSpacingPx(OffLookAtSymbolId id)
            => OracleAtDepth(_measurements[id].AnchorViewDepthMetres);

        /// <summary>The same oracle at an arbitrary view depth — the form the report's per-gap column uses.
        /// <paramref name="viewDepthMetres"/> is the ONLY input this may take from a measurement. The world
        /// length <see cref="AdvanceWorldMetres"/> carries <c>Config.DevicePixelRatio</c>, which keeps the
        /// expectation in the same metres at DPR 2.</summary>
        public double OracleAtDepth(double viewDepthMetres)
            => GroundRuler.ClosedFormPerpendicularSpanPx(
                AdvanceWorldMetres, viewDepthMetres, AbsP11, ViewportHeightPx);

        /// <summary>The inclusive ink row band around <paramref name="id"/>'s projected anchor, in
        /// <see cref="InkPixels"/>' top-left-origin row coordinates.</summary>
        public void RowBandFor(OffLookAtSymbolId id, out int rowFrom, out int rowTo)
        {
            int centreRow = (int)math.round(Config.SizePx - 1 - _measurements[id].AnchorScreenPx.y);
            rowFrom = centreRow - InkBandHalfRows;
            rowTo   = centreRow + InkBandHalfRows;
        }

        // ═══════════════════════════════════════════════════════════════════════════════════════════════
        // Construction
        // ═══════════════════════════════════════════════════════════════════════════════════════════════

        /// <summary>
        /// Builds the scene, solves the two anchors, stages all six symbols through the REAL
        /// <c>SymbolPlacementSystem.Tick</c>, measures every slot mesh, and renders the ink frame.
        /// Non-obvious why: pass 1 measures all six meshes; pass 2 renders only the cross-azimuth
        /// pair, because receding ink crosses their row bands. Unchanged cross geometry between the passes is
        /// ASSERTED. The ink is coarse corroboration only; it conflates spacing with glyph size.
        /// </summary>
        public static OffLookAtSymbolScene Create(OffLookAtSymbolSceneConfig config)
        {
            var sceneConfig = new TiltedGroundSceneConfig
            {
                TiltDegrees      = config.TiltDegrees,
                Zoom             = config.Zoom,
                SizePx           = config.SizePx,
                DevicePixelRatio = config.DevicePixelRatio,
                BackgroundColor  = Color.white, // WorldSymbolInkAnalysis.InkThreshold reads dark ink on white.
                LitAmbient       = false,       // the symbol arm needs no lit recipe.
            };

            // Any precondition Assert below can throw before the returned object exists, so this method is its
            // own Dispose until it returns (as in TiltedGroundScene.Create).
            TiltedGroundScene    scene    = null;
            GlyphAtlasTexture    atlasF   = null;
            GlyphAtlasTexture    atlasA   = null;
            SymbolPlacementSystem system   = null;
            TestSymbolPlan       plan     = null;
            SnapshotRenderer     snapshot = null;
            try
            {
                return Build(config, sceneConfig, ref scene, ref atlasF, ref atlasA, ref system, ref plan,
                    ref snapshot);
            }
            catch
            {
                snapshot?.Dispose();
                plan?.Dispose();
                system?.Dispose();
                atlasF?.Dispose();
                atlasA?.Dispose();
                scene?.Dispose();
                throw;
            }
        }

        private static OffLookAtSymbolScene Build(
            OffLookAtSymbolSceneConfig config, TiltedGroundSceneConfig sceneConfig,
            ref TiltedGroundScene scene, ref GlyphAtlasTexture atlasF, ref GlyphAtlasTexture atlasA,
            ref SymbolPlacementSystem system, ref TestSymbolPlan plan, ref SnapshotRenderer snapshot)
        {
            scene = TiltedGroundScene.Create(sceneConfig);
            UnityEngine.Camera cam = scene.UnityCamera;
            double mpp = scene.MetresPerDevicePixel;

            // ── ĝ and ĉ, from the LIVE camera ──────────────────────────────────────────────────────────
            Vector3 fwd = cam.transform.forward;
            var fwdGround = new double3(fwd.x, 0.0, fwd.z);
            Assert.That(math.length(fwdGround), Is.GreaterThan(1e-3),
                "P-M precondition: the camera's forward has no horizontal component — the receding ground " +
                "direction ĝ is undefined at this pose (a straight-down camera).");
            double3 gHat = math.normalize(fwdGround);
            var worldUp = new double3(0.0, 1.0, 0.0);
            double3 cHat = math.normalize(math.cross(worldUp, gHat));

            double wNear = GroundRuler.ViewDepthMetres(cam, double3.zero);
            double probeM = 100.0 * mpp;
            double wProbe = GroundRuler.ViewDepthMetres(cam, gHat * probeM);
            Assert.That(wProbe, Is.GreaterThan(wNear),
                $"P-M precondition: view depth must INCREASE along ĝ={gHat} (w(0)={wNear:F1} m, " +
                $"w({probeM:F0} m)={wProbe:F1} m) — the sign of the receding direction is derived, not " +
                "hard-coded, so a failure here means the derivation is wrong, not the pose.");

            // View depth is AFFINE along ĝ (w(s) = d + s·sin(tilt)), so two samples determine it exactly and
            // the far anchor is solved, not searched. The affine law itself is never written down here.
            double depthSlope = (wProbe - wNear) / probeM;
            double sFar = (config.TargetDepthRatio - 1.0) * wNear / depthSlope;
            double3 nearAnchor = double3.zero;
            double3 farAnchor  = gHat * sFar;
            double wFar = GroundRuler.ViewDepthMetres(cam, farAnchor);

            double absP11 = math.abs(cam.projectionMatrix.m11);
            double viewportHeightPx = cam.pixelHeight;

            // ── the glyph run: N copies of ONE 'F' cell, at fixture-owned em advances ──────────────────
            // Non-obvious why: the point arm's 'A' layout meets the 'F' atlas, which Tick uses only as a
            // texture; point ink is never read, so the mismatch reaches no reading.
            SymbolQuad cell;
            (atlasF, cell) = WorldCurvedAbRenderSnapshotTests.BuildGlyphF();
            List<SymbolQuad> pointQuadsA;
            TextLayoutBounds pointBoundsA;
            (atlasA, pointQuadsA, pointBoundsA) = WorldPointEmitRenderTests.BuildGlyphA();

            double scale             = config.TextSizePx / TextQuadLayout.OneEm;
            double glyphGapScreenPx  = config.AdvanceBakedPx * scale;
            double symbolSpanScreenPx = (config.GlyphCount - 1) * glyphGapScreenPx;
            double cellWidthScreenPx = (cell.BottomRight.x - cell.TopLeft.x) * scale;
            Assert.That(cellWidthScreenPx, Is.LessThan(glyphGapScreenPx),
                $"P-M precondition: the glyph gap ({glyphGapScreenPx:F1} px) must exceed the cell's own " +
                $"screen width ({cellWidthScreenPx:F1} px) or consecutive glyphs' ink runs merge and the " +
                "measurement cannot resolve individual gaps. Re-tune TextSizePx / AdvanceBakedPx and report " +
                "the values used.");

            var glyphs = new List<CurvedGlyph>(config.GlyphCount);
            for (int g = 0; g < config.GlyphCount; g++)
                glyphs.Add(new CurvedGlyph { ArcCenter = g * config.AdvanceBakedPx, Cell = cell, CellSkirt = 0f });

            // ── the six symbols ─────────────────────────────────────────────────────────────────────────
            SceneFrame frame = scene.BuildIdentityRebaseSceneFrame();
            double3 origin = frame.SceneOriginRender;
            TileId baseTile = TestTileKeys.Containing(sceneConfig.LookAt.Surface, zoom: 14);

            var roadHalfLengths = new Dictionary<OffLookAtSymbolId, double2>();
            var roadHalfExtents = new Dictionary<OffLookAtSymbolId, double2>();
            var anchors = new Dictionary<OffLookAtSymbolId, double3>
            {
                [OffLookAtSymbolId.CrossNear]    = nearAnchor,
                [OffLookAtSymbolId.CrossFar]     = farAnchor,
                [OffLookAtSymbolId.RecedingNear] = nearAnchor,
                [OffLookAtSymbolId.RecedingFar]  = farAnchor,
                [OffLookAtSymbolId.PointNear]    = nearAnchor,
                [OffLookAtSymbolId.PointFar]     = farAnchor,
            };

            var buffer = new SymbolTileBuffer();
            // A single-record buffer per id, so RenderIsolated can re-Tick ONE of them without depending on the
            // combined buffer's order. Each is copied into `buffer`.
            var symbolsById = new Dictionary<OffLookAtSymbolId, SymbolTileBuffer>();
            var curvedIds = new[]
            {
                OffLookAtSymbolId.CrossNear, OffLookAtSymbolId.CrossFar,
                OffLookAtSymbolId.RecedingNear, OffLookAtSymbolId.RecedingFar,
            };
            // The symbol's span in WORLD metres, as the staging gate measures it: LOGICAL px times the fixture's
            // own metres per logical px (see MetresPerLogicalPixel).
            double metresPerLogicalPx = mpp * config.DevicePixelRatio;
            double symbolSpanArcM      = symbolSpanScreenPx * metresPerLogicalPx;

            // The CROSS roads are sized by a SCREEN solve in DEVICE px, so their extents meet the ink bands and
            // keep the same METRE length at any DPR.
            double targetHalfRoadPx = config.SpillMargin * symbolSpanScreenPx * config.DevicePixelRatio;
            // Non-obvious why: LineCenter anchors at the WORLD midpoint, so the RECEDING roads are symmetric in
            // WORLD metres. A screen-solved receding road is so asymmetric in metres that the anchor would sit far
            // up-screen, deeper than aimed, and shrink the measured depth ratio.
            double recedingHalfLenM = config.SpillMargin * symbolSpanArcM;
            // Non-obvious why: every road stays inside this ground radius. It bounds the receding roads and leaves
            // the cross roads (~0.7× the radius) untouched; the linear-agreement assert fails if it binds there.
            // The orbit-radius floor lets a small TargetDepthRatio still build.
            double roadGroundCapM = math.max(2.0 * math.length(farAnchor), 4.0 * wNear);

            foreach (OffLookAtSymbolId id in curvedIds)
            {
                bool cross = id == OffLookAtSymbolId.CrossNear || id == OffLookAtSymbolId.CrossFar;
                double3 dir = cross ? cHat : gHat;
                double3 anchor = anchors[id];
                double anchorDepth = cross
                    ? (id == OffLookAtSymbolId.CrossNear ? wNear : wFar)
                    : (id == OffLookAtSymbolId.RecedingNear ? wNear : wFar);
                double slopePlus = cross ? 0.0 : depthSlope;

                double tPlus, tMinus, reachedPlusPx, reachedMinusPx;
                if (cross)
                {
                    tPlus = SolveGroundOffsetForScreenPx(cam, anchor, dir, targetHalfRoadPx, slopePlus,
                        anchorDepth, mpp, roadGroundCapM, out reachedPlusPx);
                    tMinus = SolveGroundOffsetForScreenPx(cam, anchor, -dir, targetHalfRoadPx, -slopePlus,
                        anchorDepth, mpp, roadGroundCapM, out reachedMinusPx);
                }
                else
                {
                    // Symmetric in WORLD metres (see recedingHalfLenM). The screen extents are only reported, so a
                    // capped or degenerate projection stays legible in the table.
                    tPlus = tMinus = recedingHalfLenM;
                    double2 anchorPx = GroundRuler.ProjectPx(cam, anchor);
                    reachedPlusPx  = math.length(GroundRuler.ProjectPx(cam, anchor + dir * tPlus) - anchorPx);
                    reachedMinusPx = math.length(GroundRuler.ProjectPx(cam, anchor - dir * tMinus) - anchorPx);

                    // SolveGroundOffsetForScreenPx's two guards, explicit: inside the ground cap (or the distance
                    // cull drops the symbol), and the up-screen end at positive view depth.
                    double farthestGroundM = math.max(
                        math.length(anchor + dir * tPlus), math.length(anchor - dir * tMinus));
                    Assert.That(farthestGroundM, Is.LessThan(roadGroundCapM),
                        $"P-M precondition ({id}): the world-symmetric receding road reaches " +
                        $"{farthestGroundM:F0} m from the look-at, past the {roadGroundCapM:F0} m ground cap " +
                        "— the production gather's B-3 distance cull would drop the label before staging.");
                    double nearEndDepthM = anchorDepth - recedingHalfLenM * depthSlope;
                    Assert.That(nearEndDepthM, Is.GreaterThan(0.1 * anchorDepth),
                        $"P-M precondition ({id}): the road's near end sits at view depth " +
                        $"{nearEndDepthM:F1} m against an anchor depth of {anchorDepth:F1} m — the road runs " +
                        "back past (or nearly past) the camera, so its projection is not a usable measurement.");
                }

                if (cross)
                {
                    // At constant depth the projection is linear, so the solve MUST equal the closed form
                    // `SpillMargin · labelSpanPx · metresPerScreenPx` (measured). This validates the solver.
                    double probeSpanM  = 30.0 * mpp;
                    double probeSpanPx = GroundRuler.GroundSegmentSpanPx(
                        cam, anchor, new double2(dir.x, dir.z), probeSpanM);
                    double linearHalfLenM = targetHalfRoadPx * (probeSpanM / probeSpanPx);
                    Assert.That(tPlus, Is.EqualTo(linearHalfLenM).Within(0.5).Percent,
                        $"P-M precondition ({id}): the screen-solved road half-length ({tPlus:F1} m) must " +
                        $"equal the linear construction ({linearHalfLenM:F1} m) on a constant-depth line, " +
                        "where the projection is exactly affine.");
                    Assert.That(tMinus, Is.EqualTo(linearHalfLenM).Within(0.5).Percent,
                        $"P-M precondition ({id}): same, on the negative side ({tMinus:F1} m vs " +
                        $"{linearHalfLenM:F1} m).");
                }

                // This mirrors StageCurved's span gates, which are in METRES for map-pitched symbols. A road
                // too short fails HERE with its numbers, not as a silently missing symbol.
                Assert.That(tMinus + tPlus, Is.GreaterThan(1.05 * symbolSpanArcM),
                    $"P-M precondition ({id}): the road's total world arc " +
                    $"({tMinus:F0} + {tPlus:F0} m) must exceed the label's world span " +
                    $"({symbolSpanArcM:F0} m) or StageCurved drops the label at its only anchor. Achieved " +
                    $"screen extents for reference: {reachedMinusPx:F1} / {reachedPlusPx:F1} px.");

                roadHalfLengths[id] = new double2(tMinus, tPlus);
                roadHalfExtents[id] = new double2(reachedMinusPx, reachedPlusPx);

                double3 pathA = anchor - dir * tMinus;
                double3 pathB = anchor + dir * tPlus;
                var curvedBuffer = new SymbolTileBuffer();
                TestSymbolTileBuffer.AddCurved(curvedBuffer, glyphs, new[] { new LineAnchor(0, 0.5f) },
                    new[] { origin + pathA, origin + pathB },
                    // SymbolWorldGroundFrame builds the glyph plane from this normal; a zero would fall back to
                    // camera-facing. The scene is Web-Mercator, so up IS (0,1,0).
                    new[] { worldUp, worldUp },
                    placement: SymbolPlacement.LineCenter,
                    up: worldUp,
                    // The RESOLVED pitch alignment (production resolves it in AlignmentResolution.ResolvePitch).
                    // Without it the symbols take the screen walk and the fixture measures nothing.
                    pitchAlignment: AlignmentMode.Map,
                    paint: SymbolPaint.Default,
                    text: id.ToString(),
                    textSizePx: config.TextSizePx,
                    maxAngleDeg: 180f,
                    keepUpright: false,
                    // At coarse zoom the dedup/collision machinery decides who emits, and a fixture silently
                    // loses symbols.
                    allowOverlap: true,
                    featureIndex: (int)id,
                    tileKey: TileKeyFor(baseTile, id));
                symbolsById[id] = curvedBuffer;
                TestSymbolTileBuffer.CopySymbolInto(buffer, curvedBuffer, 0);
            }

            foreach (OffLookAtSymbolId id in new[] { OffLookAtSymbolId.PointNear, OffLookAtSymbolId.PointFar })
            {
                var pointBuffer = new SymbolTileBuffer();
                TestSymbolTileBuffer.AddPoint(pointBuffer, origin + anchors[id], pointQuadsA, pointBoundsA.Min, pointBoundsA.Max,
                    up: worldUp,
                    paint: SymbolPaint.Default,
                    text: id.ToString(),
                    textSizePx: config.TextSizePx,
                    allowOverlap: true,
                    featureIndex: (int)id,
                    tileKey: TileKeyFor(baseTile, id));
                symbolsById[id] = pointBuffer;
                TestSymbolTileBuffer.CopySymbolInto(buffer, pointBuffer, 0);
            }

            // ── stage + measure ────────────────────────────────────────────────────────────────────────
            system = new SymbolPlacementSystem(scene.MapCam,
                worldTextBase: new Material(Shader.Find("Map/Symbol/TextWorld")));
            // No far-distance cull: the "deep" pose's far anchors sit at ~8× the near depth, and the cull would
            // drop them. The symbol-placement teeth pin the cull itself.
            system.SymbolMaxDistanceFraction = double.PositiveInfinity;
            plan = new TestSymbolPlan(scene.MapCam.Projection);
            snapshot = new SnapshotRenderer(config.SizePx, config.SizePx);
            var measurements = new Dictionary<OffLookAtSymbolId, SymbolMeasurement>();
            var vertexCounts = new Dictionary<OffLookAtSymbolId, int>();
            var slotVerticesById = new Dictionary<OffLookAtSymbolId, WorldBillboardVertex[]>();
            Color32[] inkPixels;
            {
                // PASS 1 — geometry. Duplicate Tick: the collision verdict is harvested one Tick late.
                system.Tick(in frame, plan.Build(buffer), atlasF);
                system.Tick(in frame, plan.Build(buffer), atlasF);
                Assert.That(plan.CollectedCount, Is.EqualTo(buffer.Symbols.Count),
                    $"P-M precondition: the collect must yield all {buffer.Symbols.Count} symbols (got " +
                    $"{plan.CollectedCount}) — a dedup/coverage drop must show up here, not as missing ink.");
                int expectedQuads = curvedIds.Length * config.GlyphCount + 2;
                var perSymbol = new StringBuilder();
                foreach (OffLookAtSymbolId id in (OffLookAtSymbolId[])Enum.GetValues(typeof(OffLookAtSymbolId)))
                {
                    int quads = system.TryGetWorldSlotMesh(TileKeyFor(baseTile, id), 0, SymbolKind.Text,
                        out Mesh slotMesh) && slotMesh != null ? slotMesh.vertexCount / 4 : 0;
                    perSymbol.Append(" ").Append(id.ToString()).Append("=").Append(quads);
                    if (roadHalfExtents.TryGetValue(id, out double2 ext))
                        perSymbol.Append(string.Format(CultureInfo.InvariantCulture,
                            " (road -{0:F1}/+{1:F1} px, label span {2:F1} px)", ext.x, ext.y, symbolSpanScreenPx));
                }
                Assert.That(system.LastQuadCount, Is.EqualTo(expectedQuads),
                    $"P-M precondition: every label must stage every glyph — expected {expectedQuads} quads " +
                    $"({curvedIds.Length}×{config.GlyphCount} curved + 2 point), got {system.LastQuadCount}. " +
                    $"Per label:{perSymbol}. A zero means that label spilled its road " +
                    "(StageCurved's centerArc ± halfSpan gate) or was culled.");

                foreach (OffLookAtSymbolId id in (OffLookAtSymbolId[])Enum.GetValues(typeof(OffLookAtSymbolId)))
                {
                    double3 anchor = anchors[id];
                    measurements[id] = MeasureSlot(system, cam, TileKeyFor(baseTile, id), anchor,
                        out int vertexCount, out WorldBillboardVertex[] slotVertices);
                    vertexCounts[id] = vertexCount;
                    // Captured HERE, in the geometry pass, because the ink pass below re-Ticks and
                    // overwrites the cross-azimuth slot meshes.
                    slotVerticesById[id] = slotVertices;
                }

                // World-sized receding roads need not land in frame, and an off-screen symbol still yields
                // numbers about undrawn geometry. Same 32 px margin as the in-frame precondition.
                const double recedingMarginPx = 32.0;
                foreach (OffLookAtSymbolId id in new[] { OffLookAtSymbolId.RecedingNear, OffLookAtSymbolId.RecedingFar })
                {
                    GlyphMeasurement[] recedingGlyphs = measurements[id].Glyphs;
                    double worstMarginPx = double.MaxValue;
                    var positions = new StringBuilder();
                    for (int g = 0; g < recedingGlyphs.Length; g++)
                    {
                        double2 px = recedingGlyphs[g].ScreenPx;
                        positions.Append(string.Format(CultureInfo.InvariantCulture,
                            " [{0}]=({1:F1}, {2:F1})", g, px.x, px.y));
                        worstMarginPx = math.min(worstMarginPx, math.min(
                            math.min(px.x - recedingMarginPx, config.SizePx - recedingMarginPx - px.x),
                            math.min(px.y - recedingMarginPx, config.SizePx - recedingMarginPx - px.y)));
                    }
                    Assert.That(worstMarginPx, Is.GreaterThan(0.0),
                        $"P-M precondition ({id}): every staged glyph must project inside " +
                        $"[{recedingMarginPx:F0}, {config.SizePx - recedingMarginPx:F0}]² device px — worst " +
                        $"margin {worstMarginPx:F1} px. All {recedingGlyphs.Length} positions:{positions}. " +
                        "STOP RULE 2 applies here too: lower the ZOOM and report, never the depth ratio.");
                }

                // PASS 2 — ink, from the cross-azimuth pair only: symbols 0/1, CrossNear/CrossFar, the first two
                // appended above.
                var crossOnly = new SymbolTileBuffer();
                TestSymbolTileBuffer.CopySymbolInto(crossOnly, buffer, 0);
                TestSymbolTileBuffer.CopySymbolInto(crossOnly, buffer, 1);
                system.Tick(in frame, plan.Build(crossOnly), atlasF);
                system.Tick(in frame, plan.Build(crossOnly), atlasF);
                Assert.That(system.LastQuadCount, Is.EqualTo(2 * config.GlyphCount),
                    $"P-M precondition: the ink pass must stage exactly the two cross-azimuth symbols " +
                    $"({2 * config.GlyphCount} quads), got {system.LastQuadCount}.");

                // The ink pass must not have moved the geometry the headline reading came from.
                foreach (OffLookAtSymbolId id in new[] { OffLookAtSymbolId.CrossNear, OffLookAtSymbolId.CrossFar })
                {
                    SymbolMeasurement again = MeasureSlot(system, cam, TileKeyFor(baseTile, id), anchors[id],
                        out _, out _);
                    for (int g = 0; g < again.Glyphs.Length; g++)
                        Assert.That(math.all(again.Glyphs[g].WorldUnity == measurements[id].Glyphs[g].WorldUnity),
                            Is.True,
                            $"P-M precondition ({id}, glyph {g}): the ink pass re-staged this glyph at " +
                            $"{again.Glyphs[g].WorldUnity} but the geometry pass measured " +
                            $"{measurements[id].Glyphs[g].WorldUnity} — the two passes must be the same frame " +
                            "geometrically, or the ink corroborates something the numbers did not measure.");
                }

                scene.Render(snapshot);
                inkPixels = (Color32[])snapshot.Pixels.Pixels.Clone();
                WorldSymbolInkAnalysis.FlipRowsVertically(inkPixels, config.SizePx, config.SizePx);
            }

            var built = new OffLookAtSymbolScene(config, scene, gHat, cHat, nearAnchor, farAnchor, wNear, wFar,
                mpp, absP11, viewportHeightPx, glyphGapScreenPx, symbolSpanScreenPx, cellWidthScreenPx,
                inkPixels, system, plan, atlasF, atlasA, snapshot);
            built._frame = frame;
            foreach (KeyValuePair<OffLookAtSymbolId, SymbolTileBuffer> kv in symbolsById)
                built._symbolsById[kv.Key] = kv.Value;
            foreach (KeyValuePair<OffLookAtSymbolId, WorldBillboardVertex[]> kv in slotVerticesById)
                built._vertices[kv.Key] = kv.Value;
            foreach (KeyValuePair<OffLookAtSymbolId, SymbolMeasurement> kv in measurements)
                built._measurements[kv.Key] = kv.Value;
            foreach (KeyValuePair<OffLookAtSymbolId, int> kv in vertexCounts)
                built._vertexCounts[kv.Key] = kv.Value;
            foreach (KeyValuePair<OffLookAtSymbolId, double2> kv in roadHalfLengths)
                built.RoadHalfLengthsM[kv.Key] = kv.Value;
            foreach (KeyValuePair<OffLookAtSymbolId, double2> kv in roadHalfExtents)
                built.RoadHalfExtentsPx[kv.Key] = kv.Value;
            return built;
        }

        /// <summary>Each symbol gets its OWN tile key (the containing z14 tile's X + the symbol ordinal), so the
        /// six symbols land in six separate slot meshes and no two symbols' glyph vertices ever interleave in
        /// one buffer. The far symbols additionally carry a genuinely large Level-1 RTC bake — their anchors
        /// sit ~100 tile widths from their nominal tile origin (the exact
        /// <c>WorldCurvedAbRenderSnapshotTests..._NonzeroAnchorLocal</c> pattern, taken further).</summary>
        private static long TileKeyFor(TileId baseTile, OffLookAtSymbolId id)
            => SymbolTileKey.Pack(
                new TileId { X = baseTile.X + (int)id, Y = baseTile.Y, Z = baseTile.Z });

        /// <summary>
        /// THE MEASUREMENT PATH (mesh readback), which is where the CPU layout — the fix site — is visible.
        /// The four vertices of each glyph must share ONE <c>AnchorLocal</c>; that is ASSERTED, not assumed.
        /// <c>TransformPoint</c> walks the presenter hierarchy the renderer itself built, so this fixture
        /// re-derives NO RTC, NO tile origin and NO floating-origin rebase.
        /// </summary>
        private static SymbolMeasurement MeasureSlot(SymbolPlacementSystem system, UnityEngine.Camera cam,
            long tileKey, double3 intendedAnchorUnity, out int vertexCount,
            out WorldBillboardVertex[] slotVertices)
        {
            Assert.That(system.TryGetWorldSlotMesh(tileKey, 0, SymbolKind.Text, out Mesh mesh), Is.True,
                $"P-M: no world slot mesh for tile key {tileKey} — the label emitted nothing.");
            WorldMeshReadback.Read(mesh, out WorldBillboardVertex[] vertices, out _);
            vertexCount = vertices.Length;
            slotVertices = vertices;
            Assert.That(vertexCount % 4, Is.EqualTo(0),
                $"P-M: slot {tileKey} has {vertexCount} vertices, not a whole number of quads.");

            Transform slot = system.WorldSlotTransform(tileKey, 0, SymbolKind.Text);
            Assert.That(slot, Is.Not.Null, $"P-M: slot {tileKey} was never presented — no transform to read.");

            int glyphCount = vertexCount / 4;
            var glyphs = new GlyphMeasurement[glyphCount];
            for (int g = 0; g < glyphCount; g++)
            {
                float3 anchorLocal = vertices[4 * g].AnchorLocal;
                for (int c = 1; c < 4; c++)
                    Assert.That(math.all(vertices[4 * g + c].AnchorLocal == anchorLocal), Is.True,
                        $"P-M precondition (tile {tileKey}, glyph {g}): all four corner vertices must share " +
                        $"ONE AnchorLocal — corner {c} carries {vertices[4 * g + c].AnchorLocal}, corner 0 " +
                        $"carries {anchorLocal}. The per-glyph anchor is the measurand; if the corners " +
                        "disagree there is no single glyph position to read.");

                Vector3 world = slot.TransformPoint(
                    new Vector3(anchorLocal.x, anchorLocal.y, anchorLocal.z));
                var worldUnity = new double3(world.x, world.y, world.z);
                glyphs[g] = new GlyphMeasurement(
                    worldUnity,
                    GroundRuler.ProjectPx(cam, worldUnity),
                    GroundRuler.ViewDepthMetres(cam, worldUnity));
            }

            var worldSpacing = new double[math.max(0, glyphCount - 1)];
            var screenSpacing = new double[math.max(0, glyphCount - 1)];
            for (int g = 0; g + 1 < glyphCount; g++)
            {
                worldSpacing[g]  = math.length(glyphs[g + 1].WorldUnity - glyphs[g].WorldUnity);
                screenSpacing[g] = math.length(glyphs[g + 1].ScreenPx - glyphs[g].ScreenPx);
            }

            return new SymbolMeasurement(glyphs, worldSpacing, screenSpacing, intendedAnchorUnity,
                GroundRuler.ProjectPx(cam, intendedAnchorUnity),
                GroundRuler.ViewDepthMetres(cam, intendedAnchorUnity));
        }

        /// <summary>
        /// The signed ground offset (metres) along unit <paramref name="dir"/> from
        /// <paramref name="anchor"/> whose PROJECTION sits <paramref name="targetPx"/> from the anchor's —
        /// bisection on the LIVE camera, since the screen offset is monotone in the offset along a ground ray.
        /// At constant depth it equals <c>targetPx · metresPerScreenPx</c>, which <see cref="Create"/> asserts;
        /// a receding direction is too non-linear to extrapolate. Limitation: past the vanishing line the target
        /// is unreachable, so the solve returns 90 % of the reachable maximum and reports it in
        /// <paramref name="reachedPx"/>.
        /// </summary>
        private static double SolveGroundOffsetForScreenPx(UnityEngine.Camera cam, double3 anchor, double3 dir,
            double targetPx, double depthSlopeAlongDir, double anchorDepthM, double mpp,
            double maxGroundFromLookAtM, out double reachedPx)
        {
            double tCap = 1.0e4 * mpp;
            if (depthSlopeAlongDir < 0.0)
                tCap = math.min(tCap, 0.9 * anchorDepthM / -depthSlopeAlongDir);
            // …and never past the ground-distance cap (the positive root of |anchor + t·dir| = cap), or a far
            // receding road runs thousands of km and the distance cull drops the symbol before staging.
            double along = math.dot(anchor, dir);
            double disc  = along * along - math.lengthsq(anchor) + maxGroundFromLookAtM * maxGroundFromLookAtM;
            Assert.That(disc, Is.GreaterThan(0.0),
                $"P-M precondition: the anchor {anchor} already lies outside the road ground cap " +
                $"({maxGroundFromLookAtM:F0} m) — no road can be built from it.");
            tCap = math.min(tCap, -along + math.sqrt(disc));

            double2 anchorPx = GroundRuler.ProjectPx(cam, anchor);
            double Offset(double t) => math.length(GroundRuler.ProjectPx(cam, anchor + dir * t) - anchorPx);

            double capPx = Offset(tCap);
            double want = math.min(targetPx, 0.9 * capPx);

            double lo = 0.0, hi = tCap;
            for (int i = 0; i < 60; i++)
            {
                double mid = 0.5 * (lo + hi);
                if (Offset(mid) < want) lo = mid; else hi = mid;
            }
            double t = 0.5 * (lo + hi);
            reachedPx = Offset(t);
            return t;
        }

        // ═══════════════════════════════════════════════════════════════════════════════════════════════
        // Reporting
        // ═══════════════════════════════════════════════════════════════════════════════════════════════

        /// <summary>The stage's evidence table, for <c>TestContext.WriteLine</c>: per gap, for every symbol,
        /// world spacing (m), screen spacing (px), the gap's own view depth (m), the model-oracle prediction
        /// (px) and the residual (%); then the four headline ratios.</summary>
        public string FormatMeasurementTable()
        {
            var sb = new StringBuilder();
            CultureInfo c = CultureInfo.InvariantCulture;

            sb.AppendLine("=== Stage P-M / W1 — off-look-at, multi-depth label measurement (POST-FIX TREE) ===");
            sb.AppendLine(string.Format(c,
                "pose: tilt={0:F1}deg zoom={1:F1} size={2}px  mpp={3:F3} m/devicePx  |P11|={4:F5}  H={5:F0}  d/mpp={6:F1}",
                Config.TiltDegrees, Config.Zoom, Config.SizePx, MetresPerDevicePixel, AbsP11, ViewportHeightPx,
                NearAnchorViewDepthMetres / MetresPerDevicePixel));
            // The ruler row: the three numbers the world-arc expectation is built from, so the metre
            // column below can be checked by hand without re-deriving anything.
            sb.AppendLine(string.Format(c,
                "W1 ruler: DevicePixelRatio={0:F2}  metresPerLogicalPixel={1:F4} m  AdvanceWorldMetres={2:F4} m " +
                "(= AdvanceBakedPx/OneEm × TextSizePx × metresPerLogicalPixel)",
                Config.DevicePixelRatio, MetresPerLogicalPixel, AdvanceWorldMetres));
            sb.AppendLine(string.Format(c,
                "depths: w_near={0:F1} m  w_far={1:F1} m  achieved ratio={2:F4} (target {3:F3})",
                NearAnchorViewDepthMetres, FarAnchorViewDepthMetres, AchievedDepthRatio, Config.TargetDepthRatio));
            sb.AppendLine(string.Format(c,
                "glyphs: N={0}  TextSizePx={1:F1}  AdvanceBakedPx={2:F1}  scale={3:F3}  gap={4:F2} screen px  " +
                "span={5:F2} px  cell width={6:F2} px  SpillMargin={7:F2}",
                Config.GlyphCount, Config.TextSizePx, Config.AdvanceBakedPx,
                Config.TextSizePx / TextQuadLayout.OneEm, GlyphGapScreenPx, SymbolSpanScreenPx,
                GlyphCellScreenWidthPx, Config.SpillMargin));
            sb.AppendLine(string.Format(c,
                "anchors (device px): near=({0:F1}, {1:F1})  far=({2:F1}, {3:F1})",
                Measure(OffLookAtSymbolId.CrossNear).AnchorScreenPx.x,
                Measure(OffLookAtSymbolId.CrossNear).AnchorScreenPx.y,
                Measure(OffLookAtSymbolId.CrossFar).AnchorScreenPx.x,
                Measure(OffLookAtSymbolId.CrossFar).AnchorScreenPx.y));
            sb.AppendLine();

            foreach (OffLookAtSymbolId id in (OffLookAtSymbolId[])Enum.GetValues(typeof(OffLookAtSymbolId)))
            {
                SymbolMeasurement m = Measure(id);
                bool cross = id == OffLookAtSymbolId.CrossNear || id == OffLookAtSymbolId.CrossFar;
                bool point = id == OffLookAtSymbolId.PointNear || id == OffLookAtSymbolId.PointFar;
                sb.AppendLine(string.Format(c, "-- {0} ({1}) --", id,
                    point ? "point arm" : cross ? "constant view depth — the ISO-DEPTH arm"
                                               : "receding — the DEPTH-SPANNING arm, W1's falsifiability"));
                if (RoadHalfLengthsM.TryGetValue(id, out double2 half))
                    sb.AppendLine(string.Format(c,
                        "   road half-lengths: -{0:F0} m / +{1:F0} m  (label world span {4:F0} m, spill " +
                        "margin {5:F2}×; achieved screen extents {2:F1} px / {3:F1} px)",
                        half.x, half.y, RoadHalfExtentsPx[id].x, RoadHalfExtentsPx[id].y,
                        (Config.GlyphCount - 1) * AdvanceWorldMetres, Config.SpillMargin));
                double anchorOracle = ModelPredictedScreenSpacingPx(id);
                sb.AppendLine(string.Format(c,
                    "   oracle at the label's own anchor depth ({0:F1} m) = {1:F3} px",
                    m.AnchorViewDepthMetres, anchorOracle));
                // The oracle column uses each gap's OWN view depth, the only form a receding symbol supports.
                // `worldResid%` (measured WORLD gap vs AdvanceWorldMetres) separates the walks on receding rows.
                sb.AppendLine(
                    "   gap   worldM        worldResid%   screenPx    viewDepthM   oraclePx@gap    residual%");
                for (int g = 0; g < m.ScreenSpacingPx.Length; g++)
                {
                    double gapDepth = 0.5 * (m.Glyphs[g].ViewDepthMetres + m.Glyphs[g + 1].ViewDepthMetres);
                    double gapOracle = OracleAtDepth(gapDepth);
                    double residual = 100.0 * (m.ScreenSpacingPx[g] / gapOracle - 1.0);
                    double worldResidual = 100.0 * (m.WorldSpacingM[g] / AdvanceWorldMetres - 1.0);
                    sb.AppendLine(string.Format(c,
                        "   {0,-5} {1,12:F1} {2,13:F3} {3,11:F3} {4,13:F1} {5,14:F3} {6,11:F2}",
                        g, m.WorldSpacingM[g], worldResidual, m.ScreenSpacingPx[g], gapDepth, gapOracle,
                        residual));
                }
                if (m.ScreenSpacingPx.Length == 0)
                    sb.AppendLine(string.Format(c,
                        "   (single quad — anchor at ({0:F2}, {1:F2}) px, intended ({2:F2}, {3:F2}) px)",
                        m.Glyphs[0].ScreenPx.x, m.Glyphs[0].ScreenPx.y,
                        m.AnchorScreenPx.x, m.AnchorScreenPx.y));
                sb.AppendLine();
            }

            SymbolMeasurement near = Measure(OffLookAtSymbolId.CrossNear);
            SymbolMeasurement far  = Measure(OffLookAtSymbolId.CrossFar);
            double nearScreen = Mean(near.ScreenSpacingPx);
            double farScreen  = Mean(far.ScreenSpacingPx);
            double nearWorld  = Mean(near.WorldSpacingM);
            double farWorld   = Mean(far.WorldSpacingM);
            double nearOracle = ModelPredictedScreenSpacingPx(OffLookAtSymbolId.CrossNear);
            double farOracle  = ModelPredictedScreenSpacingPx(OffLookAtSymbolId.CrossFar);

            sb.AppendLine("HEADLINE RATIOS (cross-azimuth pair, one frame, one label pair)");
            sb.AppendLine(string.Format(c,
                "  screen spacing far/near = {0:F4}   settled model requires 0.500  (pre-W1 screen-constant read 1.000)",
                farScreen / nearScreen));
            sb.AppendLine(string.Format(c,
                "  world  spacing far/near = {0:F4}   settled model requires 1.000  (pre-W1 screen-constant read 2.000)",
                farWorld / nearWorld));
            sb.AppendLine(string.Format(c,
                "  far  measured/oracle    = {0:F4}   settled model requires 1.00 — the number W1 moved",
                farScreen / farOracle));
            sb.AppendLine(string.Format(c,
                "  near measured/oracle    = {0:F4}   the LOOK-AT CONTROL — 1.00 under BOTH models, which is " +
                "exactly why a look-at-only fixture discriminated nothing",
                nearScreen / nearOracle));
            return sb.ToString();
        }

        public static double Mean(double[] values)
        {
            double sum = 0.0;
            for (int i = 0; i < values.Length; i++) sum += values[i];
            return values.Length == 0 ? 0.0 : sum / values.Length;
        }

        public void Dispose()
        {
            _snapshot.Dispose();
            _plan.Dispose();
            _system.Dispose();
            _atlasF.Dispose();
            _atlasA.Dispose();
            Scene.Dispose();
        }
    }
}
#endif // UNITY_EDITOR
