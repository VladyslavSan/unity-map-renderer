// Unity EditMode only — needs a real Camera/Mesh/Material (reads the built billboard mesh's vertex alpha).
// NOT registered in core-tests.csproj.

using System.Collections.Generic;
using NUnit.Framework;
using Unity.Mathematics;
using UnityEngine;
using MapRenderer.Core.Geo;
using MapRenderer.Core.Text;
using MapRenderer.Core.Text.Placement;
using MapRenderer.Core.View;
using MapRenderer.Core.View.Camera;
using MapRenderer.Unity.Rendering.Backend;
using MapRenderer.Unity.Rendering.Map;
using MapRenderer.Unity.Text;
using MapRenderer.Unity.Text.Placement;

namespace MapRenderer.Tests.Text.Placement
{
    /// <summary>
    /// A-4: the placement layer is a fade state machine. A label eases in/out (its opacity, carried on
    /// <c>PlacedQuad.Color.w</c> → the billboard vertex alpha) instead of popping. Teeth: the default (infinite)
    /// deltaTime snaps to full opacity (byte-parity with pre-A-4); a real small deltaTime fades IN over frames;
    /// a collision-suppressed label fades OUT while still drawn (both visible mid-transition); a stable label
    /// keeps its opacity across frames (no per-frame re-fade — the anti-blink property); and the point fade id
    /// is a FIXED-grid identity (independent of camera zoom, unlike the A-3 display-zoom dedup key).
    /// </summary>
    [TestFixture]
    public class LabelFadeTests
    {
        private static GlyphAtlasTexture BuildTinyAtlasTexture()
        {
            var glyph = new SdfGlyph { Codepoint = 65, Width = 10, Height = 10, Left = 0, Top = 8, Advance = 12,
                Bitmap = new byte[16 * 16] };
            var atlas = new GlyphAtlas();
            atlas.Append(glyph);
            var texture = new GlyphAtlasTexture();
            texture.Upload(atlas);
            return texture;
        }

        private static TextLayoutResult OneQuad() => new TextLayoutResult
        {
            Quads = new List<SymbolQuad>
            {
                new SymbolQuad
                {
                    TopLeft = new float2(-6f, 18f), BottomRight = new float2(12f, 0f),
                    UvTopLeft = new float2(0.1f, 0.1f), UvBottomRight = new float2(0.4f, 0.4f), LineIndex = 0,
                },
            },
            BoundsMin = float2.zero, BoundsMax = new float2(18f, 18f), LineCount = 1,
        };

        // R2: n copies of OneQuad()'s single glyph, sharing its BoundsMin/BoundsMax — identical bounds ⇒ identical
        // LabelBox ⇒ two labels built from NQuads always overlap, so only the quad COUNT distinguishes them
        // (used to tell which of two co-located labels won via LastQuadCount).
        private static TextLayoutResult NQuads(int n)
        {
            var quads = new List<SymbolQuad>(n);
            for (int i = 0; i < n; i++)
                quads.Add(new SymbolQuad
                {
                    TopLeft = new float2(-6f, 18f), BottomRight = new float2(12f, 0f),
                    UvTopLeft = new float2(0.1f, 0.1f), UvBottomRight = new float2(0.4f, 0.4f), LineIndex = 0,
                });
            return new TextLayoutResult { Quads = quads, BoundsMin = float2.zero, BoundsMax = new float2(18f, 18f), LineCount = 1 };
        }

        private static LabelInstance Point(double3 anchor, float sortKey, string text, int feature, TextLayoutResult layout = null)
            => new LabelInstance
            {
                AnchorRender = anchor, Placement = SymbolPlacement.Point, Layout = layout ?? OneQuad(), Paint = LabelPaint.Default,
                TextSizePx = 24f, PaddingPx = 2f, SortKey = sortKey, Text = text, FeatureIndex = feature, TileKey = 0L,
            };

        // Epic A / A1: point labels draw through the WORLD path now — the fade opacity (stream 1) no longer
        // rides system.Mesh's vertex-colour alpha (BillboardVertex.Color.a); it lives on the world slot's
        // Opacity stream (WorldMeshReadback.MaxOpacity). tileKey defaults to 0L — every label in this file
        // uses it (fade/opacity assertions are position-independent).
        private static float MaxAlpha(LabelPlacementSystem system, long tileKey = 0L)
            => system.TryGetWorldSlotMesh(tileKey, 0, LabelKind.Text, out Mesh mesh) ? WorldMeshReadback.MaxOpacity(mesh) : 0f;

        private sealed class Harness : System.IDisposable
        {
            public readonly LabelPlacementSystem System;
            public readonly SceneFrame Frame;
            public readonly GlyphAtlasTexture Atlas;
            public readonly double3 Origin;
            public readonly IProjection Projection;
            private readonly GameObject _go;

            public Harness()
            {
                _go = new GameObject("LabelFade_TestCamera");
                var uCam = _go.AddComponent<Camera>();
                uCam.targetTexture = new RenderTexture(320, 240, 0);
                var cam = new MapCamera(uCam, new CameraProperties(
                    new GeoCoordinate3D { Latitude = 10.0, Longitude = 10.0, Altitude = 0.0 }, zoom: 5.0, heading: 0.0, tilt: 0.0));
                Origin = cam.Projection.Project(new GeoCoordinate { Latitude = 10.0, Longitude = 10.0 });
                Projection = cam.Projection;
                Frame = new SceneFrame { SceneOriginRender = Origin, Rebase = float3x3.identity };
                Atlas = BuildTinyAtlasTexture();
                // Epic A / A1: point labels now draw through the world path — the demo tick needs its own
                // world base material for a live opacity/visibility read (see MaxAlpha's header).
                System = new LabelPlacementSystem(cam, worldTextBase: new Material(Shader.Find("Map/Symbol/TextWorld")));
            }

            public void Dispose()
            {
                System.Dispose();
                Atlas.Dispose();
                Object.DestroyImmediate(_go);
            }
        }

        // ── The default (infinite) deltaTime SNAPS to full opacity — a single-Tick test renders labels exactly
        //    as before A-4 (byte-parity). ──
        // ── Telemetry provider (docs/telemetry-design.md §3): the placement system OWNS its results as a struct
        //    field, refreshes it at the end of Tick, and hands it out BY REFERENCE. ──
        [Test]
        public void Tick_RefreshesItsOwnTelemetryStruct_HandedOutByReference()
        {
            using var h = new Harness();
            var labels = new List<LabelInstance> { Point(h.Origin, sortKey: 0f, text: "A", feature: 0) };
            using var plan = new TestSymbolPlan(h.Projection);

            Assert.AreEqual(0, h.System.Telemetry.PlacedQuadCount,
                "before any Tick the provider's struct is still default — it reports levels, it does not invent them.");

            // Bind ONCE, by reference, then Tick. This is the tooth for the ref-return itself: `live` aliases the
            // provider's own field, so a later refresh is visible through it. Were the accessor to return BY VALUE,
            // `live` would be a snapshot copy taken before the Tick and every assertion below would read 0.
            ref readonly LabelPlacementTelemetrySnapshot live = ref h.System.Telemetry;

            // TWO ticks before asserting a placed quad: R3 defers the collision verdict by one Tick (§2.6), so the
            // first pass places nothing. Asserting after one tick reads 0 and says nothing about the ref-return.
            h.System.Tick(in h.Frame, plan.Build(labels), h.Atlas);
            h.System.Tick(in h.Frame, plan.Build(labels), h.Atlas);

            Assert.AreEqual(1, h.System.LastQuadCount,
                "sanity: the label placed, so there is a non-zero level to carry.");
            Assert.AreEqual(h.System.LastQuadCount, live.PlacedQuadCount,
                "the struct must carry the pass's own results, seen through the reference taken BEFORE any Tick.");
            Assert.AreEqual(h.System.MirrorRebuildCount, live.MirrorRebuildCount);

            // And it keeps tracking: a further pass refreshes the same storage, still visible through `live`.
            int rebuildsSoFar = live.MirrorRebuildCount;
            h.System.Tick(in h.Frame, plan.Build(labels), h.Atlas);

            Assert.AreEqual(h.System.MirrorRebuildCount, live.MirrorRebuildCount,
                "every Tick refreshes the provider's own field — the reference never goes stale.");
            Assert.GreaterOrEqual(live.MirrorRebuildCount, rebuildsSoFar,
                "MirrorRebuildCount is CUMULATIVE; it must never go backwards.");
        }

        // ── Placement throttle (rapid-zoom stutter): the full place (TickCore, which bumps TickCount) runs only
        //    every Nth frame when PlacementThrottleFrames > 1; held frames skip TickCore. TickCount is the tooth
        //    — it counts full places, not Tick calls. The two tests are a matched pair: the control shows an
        //    un-throttled run is 7 places over 7 ticks, so the throttled run's 3 is a real, measured reduction
        //    (and would read 7, failing, if the throttle early-return were removed). ──
        [Test]
        public void PlacementThrottle_Disabled_RunsTheFullPlaceEveryFrame()
        {
            using var h = new Harness();
            var labels = new List<LabelInstance> { Point(h.Origin, sortKey: 0f, text: "A", feature: 0) };
            using var plan = new TestSymbolPlan(h.Projection);

            Assert.AreEqual(1, h.System.PlacementThrottleFrames, "precondition: the throttle is OFF by default (== 1).");
            for (int i = 0; i < 7; i++)
                h.System.Tick(in h.Frame, plan.Build(labels), h.Atlas, deltaTime: 0.016f);

            Assert.AreEqual(7, h.System.TickCount,
                "throttle OFF: every one of the 7 ticks runs the full place (TickCore), so TickCount == 7.");
        }

        [Test]
        public void PlacementThrottle_RunsTheFullPlaceOnlyEveryNthFrame_HoldingBetween()
        {
            using var h = new Harness();
            var labels = new List<LabelInstance> { Point(h.Origin, sortKey: 0f, text: "A", feature: 0) };
            using var plan = new TestSymbolPlan(h.Projection);

            h.System.PlacementThrottleFrames = 3;
            // 7 ticks, throttle 3: the full place runs on ticks 1 (first place always builds), 4 and 7 — three
            // times. Ticks 2,3,5,6 are held (WorldRenderer.Rebase only; TickCore, hence TickCount, is skipped).
            for (int i = 0; i < 7; i++)
                h.System.Tick(in h.Frame, plan.Build(labels), h.Atlas, deltaTime: 0.016f);

            Assert.AreEqual(3, h.System.TickCount,
                "throttle 3 over 7 ticks must run the full place exactly 3 times (ticks 1,4,7); the 4 held " +
                "frames skip TickCore. Reads 7 — i.e. == the disabled control — if the throttle stops working.");
        }

        [Test]
        public void Tick_DefaultDeltaTime_SnapsToFullOpacity()
        {
            using var h = new Harness();
            var label = Point(h.Origin, 0f, "A", 0);
            // R3: the collision verdict applies one Tick late (HarvestCollision consumes the PREVIOUS Tick's
            // scheduled job) — a fade-neutral duplicate tick (§2.6) is needed before placement can be asserted.
            h.System.TickLabels(in h.Frame, new List<LabelInstance> { label }, h.Atlas, h.Projection); // default deltaTime = +inf
            h.System.TickLabels(in h.Frame, new List<LabelInstance> { label }, h.Atlas, h.Projection);
            Assert.AreEqual(1, h.System.LastQuadCount, "the label places");
            Assert.Greater(MaxAlpha(h.System), 0.99f, "default deltaTime snaps the fade to full opacity");
        }

        // ── A real small deltaTime fades the label IN over successive frames (alpha rises 0 → 1). ──
        [Test]
        public void Tick_SmallDeltaTime_FadesInOverFrames()
        {
            using var h = new Harness();
            var labels = new List<LabelInstance> { Point(h.Origin, 0f, "A", 0) };

            h.System.TickLabels(in h.Frame, labels, h.Atlas, h.Projection, deltaTime: 0.05f); // R3: verdict is one Tick late (§2.6)
            h.System.TickLabels(in h.Frame, labels, h.Atlas, h.Projection, deltaTime: 0.05f);
            float first = MaxAlpha(h.System);
            Assert.Greater(first, 0f, "it has started to appear");
            Assert.Less(first, 0.5f, "…but is only partway in after one 0.05s step (fade ≈ 0.3s)");

            for (int i = 0; i < 20; i++) h.System.TickLabels(in h.Frame, labels, h.Atlas, h.Projection, deltaTime: 0.05f);
            Assert.Greater(MaxAlpha(h.System), 0.99f, "after > 0.3s of steps it is fully faded in");
        }

        // ── A stable label keeps its opacity across frames — it does NOT re-fade every frame (the persistent
        //    record is the anti-blink property; a one-frame flip becomes a sub-perceptual alpha step, not a pop). ──
        [Test]
        public void Tick_StableLabel_KeepsOpacity_NoReFade()
        {
            using var h = new Harness();
            var labels = new List<LabelInstance> { Point(h.Origin, 0f, "A", 0) };
            h.System.TickLabels(in h.Frame, labels, h.Atlas, h.Projection); // R3: verdict is one Tick late (§2.6)
            h.System.TickLabels(in h.Frame, labels, h.Atlas, h.Projection); // snap to full
            h.System.TickLabels(in h.Frame, labels, h.Atlas, h.Projection, deltaTime: 0.05f); // same id again, small step
            Assert.Greater(MaxAlpha(h.System), 0.99f, "a persistent label stays at full opacity — no re-fade");
        }

        // ── A collision-suppressed label fades OUT while still drawn: mid-transition BOTH the fading-out loser
        //    and the fading-in winner are emitted (2 quads), then it settles to just the winner (1). Pre-A-4 the
        //    loser popped instantly (1 quad throughout). ──
        [Test]
        public void Tick_CollisionSuppression_FadesOutWhileStillDrawn()
        {
            using var h = new Harness();
            // A wins alone first (lower sort key = higher priority). Then B (higher priority) appears on the SAME
            // anchor → A is suppressed and must fade out, not vanish.
            var aOnly = new List<LabelInstance> { Point(h.Origin, 10f, "A", 0) };
            var both = new List<LabelInstance>
            {
                Point(h.Origin, 10f, "A", 0),  // was placed → now the loser (fades out)
                Point(h.Origin, 5f, "B", 1),   // higher priority → the winner (fades in)
            };

            // R3: each candidate-set change needs its own extra Tick before its placement can be asserted —
            // the collision verdict a Tick's emit reads is the one harvested from the PREVIOUS Tick (§2.6).
            h.System.TickLabels(in h.Frame, aOnly, h.Atlas, h.Projection);
            h.System.TickLabels(in h.Frame, aOnly, h.Atlas, h.Projection); // A at full opacity
            Assert.AreEqual(1, h.System.LastQuadCount);

            h.System.TickLabels(in h.Frame, both, h.Atlas, h.Projection, deltaTime: 0.1f);
            h.System.TickLabels(in h.Frame, both, h.Atlas, h.Projection, deltaTime: 0.1f);
            Assert.AreEqual(2, h.System.LastQuadCount, "mid-transition both draw: A fading out + B fading in");

            for (int i = 0; i < 10; i++) h.System.TickLabels(in h.Frame, both, h.Atlas, h.Projection, deltaTime: 0.1f);
            Assert.AreEqual(1, h.System.LastQuadCount, "settled: only the winner B remains, A has faded to 0");
        }

        // ── A stable collision loser eases fully to 0 and STAYS hidden (no re-pump, no partial-opacity hover). The
        //    total-order collision tiebreak makes the survivor set a fixed point, so a deterministic loser fades out
        //    cleanly with a plain ease — no sticky/cooldown machinery. (The earlier "stuck bright" oscillation is
        //    fixed at the root in LabelCandidateCollisionTests.SelectSurvivors_SameFeatureAnchors_*.) ──
        [Test]
        public void Tick_StableCollisionLoser_FadesFullyOutAndStaysHidden()
        {
            using var h = new Harness();
            var bOnly = new List<LabelInstance> { Point(h.Origin, 10f, "B", 1) };
            // A deterministically beats B every frame (lower sort key) at the same anchor — a STABLE outcome.
            var aBeatsB = new List<LabelInstance> { Point(h.Origin, 5f, "A", 0), Point(h.Origin, 10f, "B", 1) };

            // R3: each candidate-set change needs its own extra Tick before its placement can be asserted (§2.6).
            h.System.TickLabels(in h.Frame, bOnly, h.Atlas, h.Projection);
            h.System.TickLabels(in h.Frame, bOnly, h.Atlas, h.Projection); // B alone, snap to full
            Assert.AreEqual(1, h.System.LastQuadCount, "B places alone");

            h.System.TickLabels(in h.Frame, aBeatsB, h.Atlas, h.Projection, deltaTime: 0.1f);
            h.System.TickLabels(in h.Frame, aBeatsB, h.Atlas, h.Projection, deltaTime: 0.1f);
            Assert.AreEqual(2, h.System.LastQuadCount, "mid-transition: A fading in, B fading out (both drawn briefly)");

            // B loses every frame → it eases to 0 and stays there; only A remains, and it settles at full opacity.
            for (int i = 0; i < 8; i++) h.System.TickLabels(in h.Frame, aBeatsB, h.Atlas, h.Projection, deltaTime: 0.1f);
            Assert.AreEqual(1, h.System.LastQuadCount, "a stable loser fully fades out — only the winner A remains");
            Assert.Greater(MaxAlpha(h.System), 0.99f, "…and the winner is at full opacity, not stuck partial");
        }

        // ── The fade map does not accumulate invisible identities. A candidate staged every frame and placed by
        //    none (the stable collision loser above) never parks a record AT ALL — its opacity never leaves 0, so
        //    EaseFade suppresses the store rather than writing a 0 that nothing would collect (the decay sweep
        //    skips ids seen this frame, and a staged candidate is always seen). This is a per-frame COST, not
        //    just memory: DecayUnseenFadeRecords walks every held record each Tick, so a dense view — where
        //    candidates outrun placed labels by an order of magnitude — would otherwise pay that sweep over
        //    labels nobody can see. Retention is invisible on screen, hence a state assertion rather than a
        //    rendered one. (The live-record-then-DROPPED transition is a different path, covered behaviourally
        //    by Tick_DepartingRecord_/Tick_CoverageFadingRecord_FadesOut_InsteadOfPopping.) ──
        [Test]
        public void Tick_StableCollisionLoser_LeavesNoFadeRecordBehind()
        {
            using var h = new Harness();
            // Same fixture as the test above: A beats B at the same anchor every frame (lower sort key), so B is
            // a candidate on every Tick and a survivor on none.
            var aBeatsB = new List<LabelInstance> { Point(h.Origin, 5f, "A", 0), Point(h.Origin, 10f, "B", 1) };

            for (int i = 0; i < 12; i++) h.System.TickLabels(in h.Frame, aBeatsB, h.Atlas, h.Projection, deltaTime: 0.1f);

            // PRECONDITION — without these the count assertion is vacuous. B must still be STAGED (so it still
            // eases, and could still park a record); it must simply never place. If a cull ever removed B from
            // staging instead, the assertion below would pass while proving nothing about retention.
            Assert.AreEqual(2, h.System.LastCandidateCount, "both labels are still staged — B was not culled away");
            Assert.AreEqual(1, h.System.LastQuadCount, "…but only the winner A draws; B lost every collision");

            Assert.AreEqual(1, h.System.LiveFadeRecordCount,
                "only the VISIBLE label keeps a fade record — a perpetual loser must never park one, because a " +
                "stored 0 is never collected: DecayUnseenFadeRecords skips ids seen this frame, and a staged " +
                "candidate is always seen.");

            // STABILITY — the count must STAY at 1, not merely reach it once. An implementation that periodically
            // cleared the map would satisfy a single sample and re-accumulate in between; this rejects it.
            for (int i = 0; i < 6; i++)
            {
                h.System.TickLabels(in h.Frame, aBeatsB, h.Atlas, h.Projection, deltaTime: 0.1f);
                Assert.AreEqual(1, h.System.LiveFadeRecordCount,
                    $"the fade map must stay bounded across frames (extra tick {i + 1})");
            }
        }

        // ── B-3: a label far past the horizon radius is culled BEFORE projection/collision; the near label
        //    still places. (The Core radius math is pinned in LabelViewDistanceTests; this proves the wiring.) ──
        [Test]
        public void Tick_FarLabel_IsDistanceCulled_WhileNearLabelPlaces()
        {
            using var h = new Harness();
            var near = Point(h.Origin, 0f, "N", 0);
            var far = Point(h.Origin + new double3(1e8, 0, 1e8), 0f, "F", 1); // far past any horizon radius
            var labels = new List<LabelInstance> { near, far };
            h.System.TickLabels(in h.Frame, labels, h.Atlas, h.Projection); // R3: verdict is one Tick late (§2.6)
            h.System.TickLabels(in h.Frame, labels, h.Atlas, h.Projection);
            Assert.AreEqual(1, h.System.LastDistanceCulledCount, "the far label is skipped pre-projection");
            Assert.AreEqual(1, h.System.LastQuadCount, "only the near label places");
        }

        // ── Retain-as-departing: when a tile leaves cover its labels are flagged DEPARTING (RecordDeparting, set
        //    from CollectInto's active/departing split) so they FADE OUT in place instead of popping (the B-3
        //    distance / S3 horizon culls' companion fade-out trigger — the tile-coverage pre-cull now runs
        //    upstream of the batch and pops instead, see LabelTileCoverageFilter). Simulated here by building
        //    the batch with activeCount: the same label is active first, then departing (activeCount excludes it). ──
        [Test]
        public void Tick_DepartingRecord_FadesOut_InsteadOfPopping()
        {
            using var h = new Harness();
            var labels = new List<LabelInstance> { Point(h.Origin, sortKey: 0f, text: "A", feature: 0) };

            // The DEPARTING flag is the subject, so these ticks go through the plan directly: production
            // carries it as SymbolGatherPlan.Departing (filled from the store's per-record IsDeparting),
            // which is what TestSymbolPlan's departingTiles stamps — the plan-path equivalent of the
            // retired batch builder's activeCount knob.
            using var plan = new TestSymbolPlan(h.Projection);
            var departingTiles = new HashSet<long> { 0L }; // the labels above carry TileKey = 0L

            // 1) Active (not departing) → it places and snaps to full opacity.
            h.System.Tick(in h.Frame, plan.Build(labels), h.Atlas); // R3: verdict is one Tick late (§2.6)
            h.System.Tick(in h.Frame, plan.Build(labels), h.Atlas); // default dt → snap to full
            Assert.AreEqual(1, h.System.LastQuadCount, "the active label places");
            Assert.Greater(MaxAlpha(h.System), 0.99f, "…at full opacity");

            // 2) The tile leaves cover → the SAME label is now departing. It must keep drawing while it
            //    fades, not vanish for a frame.
            SymbolGatherPlan departingPlan = plan.Build(labels, departingTiles: departingTiles);
            Assert.AreEqual(1, departingPlan.Departing[0],
                "sanity: the record is flagged departing — without this the rest of the test would be " +
                "asserting an ordinary fade and could not fail for the reason it names.");
            h.System.Tick(in h.Frame, departingPlan, h.Atlas, deltaTime: 0.1f);
            Assert.AreEqual(1, h.System.LastQuadCount, "a departing-but-visible label keeps drawing (fading, not popping)");
            float dim = MaxAlpha(h.System);
            Assert.Less(dim, 0.99f, "…its opacity has started to ease down");
            Assert.Greater(dim, 0f, "…but it is still visible mid-fade");

            // 3) After enough steps it finishes fading and is dropped — counted as departing (not coverage/distance).
            for (int i = 0; i < 10; i++)
                h.System.Tick(in h.Frame, plan.Build(labels, departingTiles: departingTiles), h.Atlas, deltaTime: 0.1f);
            Assert.AreEqual(0, h.System.LastQuadCount, "once faded out, the departing label is fully skipped");
            Assert.Greater(h.System.LastDepartingCulledCount, 0, "…and its skip is attributed to departing telemetry");
        }

        // ── REVISION 2: coverage-fading (a tile's on-screen coverage crossed below threshold, flagged
        //    RecordCoverageFading by LabelTileCoverageFilter/Build — a SEPARATE flag from RecordDeparting, since
        //    the tile is still ACTIVE, just small on screen) FADES OUT in place instead of popping — mirrors
        //    Tick_DepartingRecord_FadesOut_InsteadOfPopping above, one flag over. ──
        [Test]
        public void Tick_CoverageFadingRecord_FadesOut_InsteadOfPopping()
        {
            using var h = new Harness();
            var labels = new List<LabelInstance> { Point(h.Origin, sortKey: 0f, text: "A", feature: 0) }; // TileKey = 0L

            // The coverage-fading FLAG is the subject here, so these ticks go through the plan directly
            // rather than the TickLabels convenience — the production path carries the flag as a per-record
            // LabelTileCoverageFilter.Fade decision, which is what TestSymbolPlan's coverageFadingTiles sets.
            using var plan = new TestSymbolPlan(h.Projection);

            // 1) Not coverage-fading (no coverageFadingTiles) → places and snaps to full opacity.
            h.System.Tick(in h.Frame, plan.Build(labels), h.Atlas); // R3: verdict is one Tick late (§2.6)
            h.System.Tick(in h.Frame, plan.Build(labels), h.Atlas);
            Assert.AreEqual(1, h.System.LastQuadCount, "the label places");
            Assert.Greater(MaxAlpha(h.System), 0.99f, "…at full opacity");

            // 2) The SAME label's tile crosses below the coverage threshold — classified Fade. It
            //    must keep drawing while it fades, not vanish for a frame.
            var fadingTiles = new HashSet<long> { 0L };
            SymbolGatherPlan fadingPlan = plan.Build(labels, coverageFadingTiles: fadingTiles);
            Assert.AreEqual(1, fadingPlan.CoverageFading[0],
                "sanity: the record is flagged coverage-fading — without this the rest of the test would be " +
                "asserting an ordinary fade and could not fail for the reason it names.");
            h.System.Tick(in h.Frame, fadingPlan, h.Atlas, deltaTime: 0.1f);
            Assert.AreEqual(1, h.System.LastQuadCount, "a coverage-fading-but-visible label keeps drawing (fading, not popping)");
            float dim = MaxAlpha(h.System);
            Assert.Less(dim, 0.99f, "…its opacity has started to ease down");
            Assert.Greater(dim, 0f, "…but it is still visible mid-fade");

            // 3) After enough steps it finishes fading and is dropped — counted as coverage-fading (not departing).
            for (int i = 0; i < 10; i++)
                h.System.Tick(in h.Frame, plan.Build(labels, coverageFadingTiles: fadingTiles), h.Atlas, deltaTime: 0.1f);
            Assert.AreEqual(0, h.System.LastQuadCount, "once faded out, the coverage-fading label is fully skipped");
            Assert.Greater(h.System.LastCoverageFadingCulledCount, 0, "…and its skip is attributed to coverage-fading telemetry");
        }

        // ── The point fade id is a FIXED-grid identity: it collapses anchors within a few metres (a cross-tile
        //    no-op) and separates distinct ones — and it takes NO zoom parameter, so it cannot drift as the
        //    camera zooms (the bug a per-frame display-zoom grid would cause). ──
        [Test]
        public void PointFadeId_FixedGrid_CollapsesNearby_SeparatesFar()
        {
            double3 a = new double3(5_000_000.0, 0, 3_000_000.0);
            Assert.AreEqual(LabelPlacementSystem.PointFadeId(a, 0, "T"),
                            LabelPlacementSystem.PointFadeId(a + new double3(2, 0, -2), 0, "T"),
                            "anchors within the fixed fade grid share one identity (the cross-tile no-op)");
            Assert.AreNotEqual(LabelPlacementSystem.PointFadeId(a, 0, "T"),
                               LabelPlacementSystem.PointFadeId(a + new double3(50, 0, 0), 0, "T"),
                               "anchors many metres apart are distinct labels");
            Assert.AreNotEqual(LabelPlacementSystem.PointFadeId(a, 0, "T"),
                               LabelPlacementSystem.PointFadeId(a, 0, "U"),
                               "different text is a different label even at the same anchor");
        }

        // ── I6: the icon analogue — two co-located icon labels (text=null, distinct icon-image) must get
        //    DISTINCT fade ids (pre-fix they'd collide: text==null for both). Same icon-image at the same
        //    anchor shares an id (the seamless no-op icons now get too). A text label's id is unchanged when
        //    iconImage is omitted/explicitly-null (the #1 invariant: guard-skip, not `?? 0`). ──
        [Test]
        public void PointFadeId_IconIdentity_DistinctIconsSeparate_SameIconShares_TextUnaffected()
        {
            double3 a = new double3(5_000_000.0, 0, 3_000_000.0);

            Assert.AreNotEqual(LabelPlacementSystem.PointFadeId(a, 0, null, "a"),
                                LabelPlacementSystem.PointFadeId(a, 0, null, "b"),
                                "same cell/layer, text=null, different icon-image → distinct fade ids");

            Assert.AreEqual(LabelPlacementSystem.PointFadeId(a, 0, null, "a"),
                             LabelPlacementSystem.PointFadeId(a, 0, null, "a"),
                             "the same icon-image at the same anchor shares one fade id");

            Assert.AreEqual(LabelPlacementSystem.PointFadeId(a, 0, "Paris"),
                             LabelPlacementSystem.PointFadeId(a, 0, "Paris", null),
                             "a text label's fade id is unchanged whether iconImage is omitted or explicitly null");
        }

        // ── Stage 3b (ONE canonical identity): the point fade id partitions labels IDENTICALLY to the store's
        //    dedup cell — both quantize to CrossTileLabelKey.CanonicalGridMeters. A pair co-located within the
        //    canonical grid shares BOTH a PointFadeId and a dedup cell (CrossTileLabelKey.For equality); a pair
        //    further apart than the grid shares NEITHER. This binds the fade identity to the SAME constant the
        //    dedup uses, not merely to "some fixed grid": diverge the fade grid from CanonicalGridMeters and the
        //    beyond-grid pair would agree in fade but split in dedup — which this asserts cannot happen. ──
        [Test]
        public void PointFadeId_AgreesWithDedupCell()
        {
            const double grid = CrossTileLabelKey.CanonicalGridMeters;
            double3 a = new double3(5_000_000.0, 0, 3_000_000.0);
            double3 near = a + new double3(grid * 0.25, 0, -grid * 0.25);   // same canonical cell
            double3 far = a + new double3(grid * 4.0, 0, 0);                // clearly a different cell

            // The dedup cell for a point label (same layer/text/icon over these anchors) — the canonical identity
            // the store compares. Equality here IS the dedup grouping.
            static bool SameDedupCell(double3 p, double3 q)
                => CrossTileLabelKey.For(p, 0, "T", null, grid).Equals(CrossTileLabelKey.For(q, 0, "T", null, grid));
            static bool SameFadeId(double3 p, double3 q)
                => LabelPlacementSystem.PointFadeId(p, 0, "T") == LabelPlacementSystem.PointFadeId(q, 0, "T");

            // Co-located pair: shares BOTH — one canonical identity.
            Assert.IsTrue(SameDedupCell(a, near), "sanity: the near pair is one dedup cell");
            Assert.IsTrue(SameFadeId(a, near), "co-located labels share a fade id (agreeing with the dedup cell)");

            // Beyond-grid pair: shares NEITHER — fade partition tracks the dedup partition.
            Assert.IsFalse(SameDedupCell(a, far), "sanity: the far pair splits across dedup cells");
            Assert.IsFalse(SameFadeId(a, far), "labels beyond the canonical grid get distinct fade ids (no dedup, no shared fade)");

            // The proof, stated as an equivalence: fade grouping ⟺ dedup grouping for every pair.
            Assert.AreEqual(SameDedupCell(a, near), SameFadeId(a, near), "fade grouping == dedup grouping (near)");
            Assert.AreEqual(SameDedupCell(a, far), SameFadeId(a, far), "fade grouping == dedup grouping (far)");
        }

        // ── A-5 incumbency plumbing ──────────────────────────────────────────────────────────────────────────
        // R2: end-to-end coverage for the _placedLastFrame → WasPlacedLastFrame plumbing — previously untested
        // anywhere (LabelCandidateCollisionTests sets WasPlacedLastFrame by hand, never through
        // LabelPlacementSystem; LabelStageJobTests feeds it as a raw input; LabelProjectionJobTests deliberately
        // arranges distinct sort keys "so A-5 incumbency is a no-op"). X and Y sit at the SAME anchor with an
        // EQUAL SortKey, so LabelCollision.ComparePlacementOrder falls to its incumbency term (LabelCollision.cs:144)
        // strictly BEFORE the FeatureIndex term (:145) — if a future change reorders those two lines this test
        // breaks loudly, which is correct. Distinct Text ⇒ distinct PointFadeId even at a shared anchor
        // (LabelPlacementSystem.cs:1273-1303 folds Text.GetHashCode() into the FNV hash). NQuads(n) discriminates
        // the winner via LastQuadCount — a faded-to-0 loser is `continue`d before it can emit (LabelPlacementSystem.cs:608)
        // and contributes 0 quads to the total (WorldLabelRenderer.Emit's returned QuadCount, accumulated at :615/626).
        [Test]
        public void Tick_IncumbentKeepsSlot_OverEqualSortKeyNewcomer()
        {
            using var h = new Harness();
            var x = Point(h.Origin, sortKey: 0f, text: "A", feature: 0, layout: NQuads(1)); // newcomer
            var y = Point(h.Origin, sortKey: 0f, text: "B", feature: 1, layout: NQuads(2)); // incumbent

            // Tick 1: Y alone places (default deltaTime snaps it to full opacity) — _placedLastFrame == { fadeId(Y) }.
            // R3: the verdict a Tick's stage/emit reads is one Tick behind (§2.6) — duplicate (same args) so the
            // scheduled collision has been harvested before the assertion. This ALSO matters structurally here:
            // Y must be the harvested incumbent BEFORE the {X,Y} Tick below stages, or LabelStageJob never sees
            // WasPlacedLastFrame=true for Y and the tiebreak this test pins never engages.
            h.System.TickLabels(in h.Frame, new List<LabelInstance> { y }, h.Atlas, h.Projection);
            h.System.TickLabels(in h.Frame, new List<LabelInstance> { y }, h.Atlas, h.Projection);
            Assert.AreEqual(2, h.System.LastQuadCount, "Y places alone");

            // Tick 2: X and Y tie on SortKey. Y is the A-5 incumbent ⇒ it wins the tiebreak over the newcomer X ⇒
            // X (a brand-new fade id, never above FadeEpsilon) fades to 0 in the same tick and is skipped.
            // R3: the FIRST {X,Y} Tick here only re-emits Y off the PRIOR (Y-alone) harvested verdict — it is the
            // SECOND {X,Y} Tick (duplicated) whose harvest reflects the actual {X,Y} collision staged with Y's
            // A-5 incumbency bias, which is the real tiebreak this test proves.
            h.System.TickLabels(in h.Frame, new List<LabelInstance> { x, y }, h.Atlas, h.Projection, deltaTime: 0.1f);
            h.System.TickLabels(in h.Frame, new List<LabelInstance> { x, y }, h.Atlas, h.Projection, deltaTime: 0.1f);
            Assert.AreEqual(2, h.System.LastQuadCount, "the incumbent Y keeps its slot over the equal-sort-key newcomer X");

            // Control (falsifies the above): a FRESH harness/system has no incumbents at all, so with the SAME
            // { X, Y } the collision resolves purely on FeatureIndex — X (0 < 1) wins instead of Y. Two ticks:
            // the first only schedules (R3 §2.6 — nothing harvested yet on a virgin system); the second harvests
            // that FeatureIndex-only-tiebreak collision's verdict.
            using var fresh = new Harness();
            var x2 = Point(fresh.Origin, sortKey: 0f, text: "A", feature: 0, layout: NQuads(1));
            var y2 = Point(fresh.Origin, sortKey: 0f, text: "B", feature: 1, layout: NQuads(2));
            var controlLabels = new List<LabelInstance> { x2, y2 };
            fresh.System.TickLabels(in fresh.Frame, controlLabels, fresh.Atlas, fresh.Projection);
            fresh.System.TickLabels(in fresh.Frame, controlLabels, fresh.Atlas, fresh.Projection);
            Assert.AreEqual(1, fresh.System.LastQuadCount,
                "control: with no prior incumbent, FeatureIndex alone decides — X wins, proving tick 2's outcome above was incumbency, not a fixed bias");
        }
    }
}
