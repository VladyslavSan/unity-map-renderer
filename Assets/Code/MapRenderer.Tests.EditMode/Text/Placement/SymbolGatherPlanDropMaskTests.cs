// Unity EditMode only — needs LabelPlacementSystem/SymbolGatherPlan/SymbolTileLabelBlock (Unity.Collections) +
// a real Camera/Mesh (world-slot vertex/opacity readback). NOT registered in core-tests.csproj.

using System.Collections.Generic;
using NUnit.Framework;
using Unity.Mathematics;
using UnityEngine;
using MapRenderer.Core.Geo;
using MapRenderer.Core.Style.Symbol;
using MapRenderer.Core.Text;
using MapRenderer.Core.Text.Placement;
using MapRenderer.Core.View.Camera;
using MapRenderer.Unity.Rendering.Backend;
using MapRenderer.Unity.Rendering.Map;
using MapRenderer.Unity.Text;
using MapRenderer.Unity.Text.Placement;

namespace MapRenderer.Tests.Text.Placement
{
    /// <summary>
    /// D1-#2 (symbol-label native bake, Phase 2): the tile-coverage cull's Drop decision is now a per-record
    /// MASK (<see cref="SymbolGatherPlan.Dropped"/>, stamped onto the native mirror as <c>_mirrorRecordDropped</c>)
    /// instead of a physical compaction — a Dropped winner stays RESIDENT in the plan/mirror and is hard-skipped
    /// by <c>LabelPlacementSystem.GatherSymbolPoints</c>'s FIRST, unconditional check. This is the falsifiable
    /// proof that masking a Dropped record is bit-for-bit equivalent to it never having been collected — proven
    /// three ways, each independently RED-verifiable so a single defect can't hide behind another:
    ///
    /// <list type="bullet">
    ///   <item><see cref="MaskedDrop_PointOnly_MatchesReference_SurvivorContentIdentical"/> — a POINT-only
    ///     fixture (no curved label anywhere), so a point-path regression can't hide behind curved candidates.</item>
    ///   <item><see cref="MaskedDrop_CurvedOnly_MatchesReference_SurvivorContentIdentical"/> — a CURVED-only
    ///     fixture, with an explicit pre-Drop assertion that the curved record actually SURVIVED with positive
    ///     opacity (a genuinely live fade, not just "no assertion it ever placed") — this is what makes the
    ///     <c>MarkFadeOutIfAlive</c> "still alive ⇒ keep staging" path a real divergence risk if the hard-skip is
    ///     ever folded into that OR-chain instead of preceding it.</item>
    ///   <item><see cref="AllDropped_FadeStaysFrozen_ReappearOpacityMatchesReference"/> — the Blocker-1 regression:
    ///     a frame where EVERY resident record is Dropped must behave EXACTLY like the pre-D1 empty-after-
    ///     compaction mirror (placement/fade-decay block skipped entirely), so a live fade FREEZES instead of
    ///     decaying — <see cref="LabelPlacementSystem"/>'s <c>_mirrorNonDroppedCount</c> gate, not raw <c>_mirrorCount</c>.</item>
    /// </list>
    ///
    /// Each test compares the RESIDENT-MASKED production path (a plan carrying every winner, some flagged
    /// Dropped) against a REFERENCE plan that never includes the would-be-Dropped winners in the first place —
    /// not just candidate/quad COUNTS, but the surviving record's full world-mesh vertex + opacity byte content
    /// (<see cref="WorldMeshReadback"/>), so a wrong-candidate-survived defect (same count, different content)
    /// is caught too.
    ///
    /// <para><b>SF8 (vacuous-pass guard).</b> <c>LabelStagingMath.StagePoint</c> returns 0 candidates on a
    /// projection/viewport-margin failure — the KEEP anchor is the dead-centre point the test camera looks
    /// straight at (mirrors <c>LabelFadeTests</c>' pattern) and the DROP anchor is offset by <c>DropOffset</c>
    /// but stays well inside the 256px viewport, both guaranteed projectable and inside margin, so a removed
    /// hard-skip is never saved from detection by an unrelated off-viewport reject.</para>
    /// </summary>
    [TestFixture]
    public class SymbolGatherPlanDropMaskTests
    {
        private static readonly WebMercatorProjection P = new WebMercatorProjection();

        // Render-space offset separating the DROP label from the centre-anchored KEEP label so the two do NOT
        // collide (a same-point pair suppresses one to opacity 0). ~1600 render metres — large enough to clear the
        // 18px glyph box across the plausible metres-per-pixel range, small enough to stay inside the 256px viewport.
        private static readonly double3 DropOffset = new double3(0, 0, 1600);

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

        private static TextLayoutResult OneQuad(float u) => new TextLayoutResult
        {
            Quads = new List<SymbolQuad>
            {
                new SymbolQuad
                {
                    TopLeft = new float2(-6f, 18f), BottomRight = new float2(12f, 0f),
                    UvTopLeft = new float2(u, u), UvBottomRight = new float2(u + 0.2f, u + 0.2f), LineIndex = 0,
                },
            },
            BoundsMin = float2.zero, BoundsMax = new float2(18f, 18f), LineCount = 1,
        };

        private static LabelInstance PointLabel(double3 anchor, string text, int feature, long tileKey)
            => new LabelInstance
            {
                AnchorRender = anchor, Placement = SymbolPlacement.Point, Layout = OneQuad(0.1f), Paint = LabelPaint.Default,
                TextSizePx = 20f, PaddingPx = 2f, SortKey = 0f, Text = text, FeatureIndex = feature, TileKey = tileKey,
            };

        private static LabelInstance CurvedLabel(double3 anchor, string text, int feature, long tileKey)
            => new LabelInstance
            {
                AnchorRender = anchor, Placement = SymbolPlacement.LineCenter, Text = text, Paint = LabelPaint.Default,
                TextSizePx = 20f, PaddingPx = 2f, SortKey = 1f, FeatureIndex = feature, TileKey = tileKey,
                MaxAngleDeg = 180f, KeepUpright = false,
                // A fixed ~8m path is sub-pixel at z12 and never stages — mirror WorldCurvedAbRenderSnapshotTests'
                // altitude-relative sizing: a ~1600 render-metre span (anchor at path mid via LineAnchor 0.5, glyph
                // at ArcCenter 0 ⇒ at the anchor) places comfortably on-screen at this zoom.
                PathRender = new[] { anchor - new double3(800, 0, 0), anchor + new double3(800, 0, 0) },
                LineAnchors = new[] { new LineAnchor(0, 0.5f) },
                CurvedGlyphs = new List<CurvedGlyph> { new CurvedGlyph { ArcCenter = 0f, Cell = OneQuad(0.3f).Quads[0] } },
            };

        private static SymbolTileLabelStore.Key Key(TileId t) => new SymbolTileLabelStore.Key("s", t);
        private static long Tk(TileId t) => SymbolFeatureExtractor.PackTileKey(t);

        private static void SeedTile(SymbolTileLabelStore store, TileId tile, List<LabelInstance> labels)
        {
            int gen = store.BeginBuild(Key(tile));
            SymbolTileLabelBlock block = SymbolTileLabelBlockBaker.Bake(labels, slotCount: 1, TileRenderOrigin.Project(tile, P));
            Assert.IsTrue(store.CompleteBuild(Key(tile), gen, labels, block), "sanity: block committed");
        }

        // Owns the LPS + the throwaway Unity resources the fixture creates — mirrors SymbolGatherParityTests'
        // LpsHarness, plus a real look-at so StagePoint's projection/viewport-margin check genuinely passes (SF8).
        private sealed class Harness : System.IDisposable
        {
            public readonly LabelPlacementSystem System;
            public readonly SceneFrame Frame;
            public readonly double3 Origin;
            public readonly GlyphAtlasTexture Atlas;
            private readonly GameObject _camGo;
            private readonly RenderTexture _rt;
            private readonly Material _baseMaterial;

            public Harness()
            {
                _camGo = new GameObject("DropMask_TestCamera");
                var uCam = _camGo.AddComponent<Camera>();
                _rt = new RenderTexture(256, 256, 0); // headroom so KEEP (centre) + DROP (offset) both stay in-viewport
                uCam.targetTexture = _rt;
                var lookAt = new GeoCoordinate3D { Latitude = 10.0, Longitude = 10.0, Altitude = 0.0 };
                var mapCamera = new MapCamera(uCam, new CameraProperties(lookAt, zoom: 12.0, heading: 0.0, tilt: 0.0),
                    projection: P);
                Origin = mapCamera.Projection.Project(new GeoCoordinate { Latitude = 10.0, Longitude = 10.0 });
                Frame = new SceneFrame { SceneOriginRender = Origin, Rebase = float3x3.identity };
                Atlas = BuildTinyAtlasTexture();
                _baseMaterial = new Material(Shader.Find("Map/Symbol/TextWorld"));
                System = new LabelPlacementSystem(mapCamera, _baseMaterial);
            }

            public void Dispose()
            {
                System.Dispose();
                Atlas.Dispose();
                Object.DestroyImmediate(_camGo);
                Object.DestroyImmediate(_rt);
                Object.DestroyImmediate(_baseMaterial);
            }
        }

        // Fills `plan` from the store's real winner arrays (blockId/localIndex/isDeparting), stamping every winner
        // whose TileKey == dropTileKey Drop and everything else Keep — the RESIDENT-MASKED path (every winner
        // present in the plan, regardless of decision). dropTileKey == -1 (no tile ever packs to -1) ⇒ all Keep.
        // R1: `version` is threaded through to SymbolGatherPlan.Build — every test below rebuilds the SAME plan
        // object across frames, so each call passes a freshly incremented per-test counter (audited by READING
        // this call site, not by which tests happen to go RED — SHOULD-FIX 3).
        private static void BuildMaskedPlan(SymbolTileLabelStore store, SymbolGatherPlan plan, long dropTileKey, int version)
        {
            var collected = new List<LabelInstance>();
            var blockId = new List<int>();
            var localIndex = new List<int>();
            var isDeparting = new List<byte>();
            store.CollectInto(collected, blockId, localIndex, isDeparting, quantizeMeters: 1.0, out _);

            var decisions = new List<byte>(collected.Count);
            for (int i = 0; i < collected.Count; i++)
                decisions.Add(collected[i].TileKey == dropTileKey ? LabelTileCoverageFilter.Drop : LabelTileCoverageFilter.Keep);

            plan.Build(blockId, localIndex, collected, isDeparting, decisions, store.OrderedBlocks, version);
        }

        // Fills `plan` with ONLY the winners whose TileKey != excludedTileKey — the REFERENCE path (physical
        // absence, as if the excluded tile's build never happened / was never collected). excludedTileKey == -1
        // (no tile ever packs to -1) ⇒ everything included (a plain "Build the whole store" call).
        private static void BuildReferencePlan(SymbolTileLabelStore store, SymbolGatherPlan plan, long excludedTileKey, int version)
        {
            var collected = new List<LabelInstance>();
            var blockId = new List<int>();
            var localIndex = new List<int>();
            var isDeparting = new List<byte>();
            store.CollectInto(collected, blockId, localIndex, isDeparting, quantizeMeters: 1.0, out _);

            var refCollected = new List<LabelInstance>();
            var refBlockId = new List<int>();
            var refLocalIndex = new List<int>();
            var refIsDeparting = new List<byte>();
            var refDecisions = new List<byte>();
            for (int i = 0; i < collected.Count; i++)
            {
                if (collected[i].TileKey == excludedTileKey) continue;
                refCollected.Add(collected[i]); refBlockId.Add(blockId[i]); refLocalIndex.Add(localIndex[i]);
                refIsDeparting.Add(isDeparting[i]); refDecisions.Add(LabelTileCoverageFilter.Keep);
            }
            plan.Build(refBlockId, refLocalIndex, refCollected, refIsDeparting, refDecisions, store.OrderedBlocks, version);
        }

        private static float MaxAlpha(LabelPlacementSystem system, long tileKey)
            => system.TryGetWorldSlotMesh(tileKey, 0, LabelKind.Text, out Mesh mesh) ? WorldMeshReadback.MaxOpacity(mesh) : 0f;

        // Full vertex + opacity byte comparison of two world-slot meshes — identity+content, not just a count or
        // a single max-opacity scalar. Returns the first difference, or null if byte-identical.
        private static string FirstMeshDifference(Mesh a, Mesh b)
        {
            WorldMeshReadback.Read(a, out WorldBillboardVertex[] va, out float[] oa);
            WorldMeshReadback.Read(b, out WorldBillboardVertex[] vb, out float[] ob);
            if (va.Length != vb.Length) return $"vertex count {va.Length} vs {vb.Length}";
            for (int i = 0; i < va.Length; i++)
                if (!va[i].Equals(vb[i])) return $"vertex[{i}] differs ({va[i]} vs {vb[i]})";
            if (oa.Length != ob.Length) return $"opacity count {oa.Length} vs {ob.Length}";
            for (int i = 0; i < oa.Length; i++)
                if (oa[i] != ob[i]) return $"opacity[{i}] {oa[i]} vs {ob[i]}";
            return null;
        }

        [Test]
        public void MaskedDrop_PointOnly_MatchesReference_SurvivorContentIdentical()
        {
            var keepTile = new TileId { Z = 12, X = 2200, Y = 1500 };
            var dropTile = new TileId { Z = 12, X = 2201, Y = 1500 };
            long keepKey = Tk(keepTile), dropKey = Tk(dropTile);

            using var hMasked = new Harness();
            using var hRef = new Harness();
            var storeMasked = new SymbolTileLabelStore(cacheCap: 16);
            var storeRef = new SymbolTileLabelStore(cacheCap: 16);
            try
            {
                foreach (SymbolTileLabelStore store in new[] { storeMasked, storeRef })
                {
                    SeedTile(store, keepTile, new List<LabelInstance> { PointLabel(hMasked.Origin, "keep", 1, keepKey) });
                    // DROP anchor offset so it does NOT collide with KEEP (same-point ⇒ one is suppressed to 0 and the
                    // "genuinely live before Drop" sanity can't hold); still well inside the 256px viewport (SF8).
                    SeedTile(store, dropTile, new List<LabelInstance> { PointLabel(hMasked.Origin + DropOffset, "dropPoint", 2, dropKey) });
                }

                var planMasked = new SymbolGatherPlan();
                var planRef = new SymbolGatherPlan();
                int maskedVersion = 0, refVersion = 0; // R1: each rebuild of the SAME plan object gets a fresh version
                try
                {
                    // Frame 1: both tiles Keep on both harnesses — establishes a live (full-opacity) fade for the
                    // drop tile's point too (default +∞ deltaTime snaps to full). R3: the collision verdict a
                    // Tick's emit reads is harvested from the PREVIOUS Tick (§2.6) — duplicate each harness's
                    // Tick call (SAME plan+args) before an assertion reads placement output.
                    BuildMaskedPlan(storeMasked, planMasked, dropTileKey: -1, maskedVersion++);
                    hMasked.System.Tick(in hMasked.Frame, planMasked, hMasked.Atlas);
                    hMasked.System.Tick(in hMasked.Frame, planMasked, hMasked.Atlas);
                    BuildMaskedPlan(storeRef, planRef, dropTileKey: -1, refVersion++);
                    hRef.System.Tick(in hRef.Frame, planRef, hRef.Atlas);
                    hRef.System.Tick(in hRef.Frame, planRef, hRef.Atlas);
                    Assert.Greater(MaxAlpha(hMasked.System, dropKey), 0.99f, "sanity: the drop tile's point is genuinely live before the Drop");

                    // Frame 2: MASKED flags the drop tile's winner Dropped (resident); REFERENCE excludes it.
                    // R3: duplicate again — LastSurvivorCount/LastQuadCount otherwise still read frame 1's
                    // harvested (pre-Drop) verdict rather than the collision frame 2 itself just scheduled with
                    // the Drop applied, which would make the comparison below pass without exercising the Drop
                    // mask at all.
                    BuildMaskedPlan(storeMasked, planMasked, dropKey, maskedVersion++);
                    hMasked.System.Tick(in hMasked.Frame, planMasked, hMasked.Atlas);
                    hMasked.System.Tick(in hMasked.Frame, planMasked, hMasked.Atlas);
                    BuildReferencePlan(storeRef, planRef, dropKey, refVersion++);
                    hRef.System.Tick(in hRef.Frame, planRef, hRef.Atlas);
                    hRef.System.Tick(in hRef.Frame, planRef, hRef.Atlas);

                    Assert.AreEqual(hRef.System.LastCandidateCount, hMasked.System.LastCandidateCount,
                        "masked-Drop's candidate count must equal the reference's (the Dropped point is absent from collision)");
                    Assert.AreEqual(hRef.System.LastSurvivorCount, hMasked.System.LastSurvivorCount,
                        "masked-Drop's survivor count must equal the reference's");
                    Assert.AreEqual(hRef.System.LastQuadCount, hMasked.System.LastQuadCount,
                        "masked-Drop's emitted quad count must equal the reference's");
                    Assert.AreEqual(1, hRef.System.LastQuadCount, "sanity: only the KEEP point ever draws");

                    bool maskedHasKeep = hMasked.System.TryGetWorldSlotMesh(keepKey, 0, LabelKind.Text, out Mesh keepMeshMasked);
                    bool refHasKeep = hRef.System.TryGetWorldSlotMesh(keepKey, 0, LabelKind.Text, out Mesh keepMeshRef);
                    Assert.IsTrue(maskedHasKeep && refHasKeep, "the surviving KEEP point's world slot must exist on both sides");
                    Assert.IsNull(FirstMeshDifference(keepMeshRef, keepMeshMasked),
                        "the surviving KEEP point's full vertex+opacity content must be byte-identical whether the Drop tile is masked or absent");

                    // The Dropped point is hard-skipped BEFORE emit (masked) / absent (reference); either way its slot
                    // is not emitted this frame, so WorldLabelRenderer disables the Renderer but RETAINS the stale
                    // frame-1 mesh until idle-reclaim. Equivalence is therefore masked-drop-slot == reference-drop-slot
                    // (both hidden, both stale-identical), NOT "absolutely invisible" (the retained mesh reads its old
                    // opacity on both sides). This differential still fails loudly if residency changed the slot at all.
                    bool maskedHasDrop = hMasked.System.TryGetWorldSlotMesh(dropKey, 0, LabelKind.Text, out Mesh dropMeshMasked);
                    bool refHasDrop = hRef.System.TryGetWorldSlotMesh(dropKey, 0, LabelKind.Text, out Mesh dropMeshRef);
                    Assert.AreEqual(refHasDrop, maskedHasDrop, "the Dropped point's world slot must be present/absent identically masked vs reference");
                    if (maskedHasDrop && refHasDrop)
                        Assert.IsNull(FirstMeshDifference(dropMeshRef, dropMeshMasked),
                            "the Dropped point's world slot content must be byte-identical whether resident-masked or physically absent");

                    // Content equality alone can't catch a KEEP↔DROP swap (both slots hold identical stale/rebuilt
                    // meshes); visibility is carried separately by the presenter's MeshRenderer.enabled. Assert the
                    // RENDERED set — KEEP visible, DROP hidden, on BOTH paths — a swap flips these and fails here.
                    Assert.IsTrue(hMasked.System.IsWorldSlotVisible(keepKey, 0, LabelKind.Text), "masked: the surviving KEEP slot must be VISIBLE");
                    Assert.IsTrue(hRef.System.IsWorldSlotVisible(keepKey, 0, LabelKind.Text), "reference: the surviving KEEP slot must be VISIBLE");
                    Assert.IsFalse(hMasked.System.IsWorldSlotVisible(dropKey, 0, LabelKind.Text), "masked: the Dropped slot must be HIDDEN (Renderer disabled), not merely stale-mesh-identical");
                    Assert.IsFalse(hRef.System.IsWorldSlotVisible(dropKey, 0, LabelKind.Text), "reference: the absent Drop slot must be HIDDEN");
                }
                finally { planMasked.Dispose(); planRef.Dispose(); }
            }
            finally { storeMasked.Clear(); storeRef.Clear(); }
        }

        [Test]
        public void MaskedDrop_CurvedOnly_MatchesReference_SurvivorContentIdentical()
        {
            var keepTile = new TileId { Z = 12, X = 2300, Y = 1500 };
            var dropTile = new TileId { Z = 12, X = 2301, Y = 1500 };
            long keepKey = Tk(keepTile), dropKey = Tk(dropTile);

            using var hMasked = new Harness();
            using var hRef = new Harness();
            var storeMasked = new SymbolTileLabelStore(cacheCap: 16);
            var storeRef = new SymbolTileLabelStore(cacheCap: 16);
            try
            {
                foreach (SymbolTileLabelStore store in new[] { storeMasked, storeRef })
                {
                    SeedTile(store, keepTile, new List<LabelInstance> { CurvedLabel(hMasked.Origin, "keepCurved", 1, keepKey) });
                    // DROP anchor offset so it does NOT collide with KEEP (see the point-only test); in-viewport (SF8).
                    SeedTile(store, dropTile, new List<LabelInstance> { CurvedLabel(hMasked.Origin + DropOffset, "dropCurved", 2, dropKey) });
                }

                var planMasked = new SymbolGatherPlan();
                var planRef = new SymbolGatherPlan();
                int maskedVersion = 0, refVersion = 0; // R1: each rebuild of the SAME plan object gets a fresh version
                try
                {
                    // Frame 1: both tiles Keep — establishes a LIVE fade for the drop tile's CURVED record.
                    // Explicitly assert it actually survived + placed with positive opacity (Blocker-2 fix: the
                    // prior version never proved this) — this is what makes MarkFadeOutIfAlive's "still alive ⇒
                    // keep staging" branch a genuine divergence risk if Drop is ever folded into that OR-chain.
                    // R3: the collision verdict a Tick's emit reads is harvested from the PREVIOUS Tick (§2.6) —
                    // duplicate each harness's Tick call (SAME plan+args) before an assertion reads placement output.
                    BuildMaskedPlan(storeMasked, planMasked, dropTileKey: -1, maskedVersion++);
                    hMasked.System.Tick(in hMasked.Frame, planMasked, hMasked.Atlas);
                    hMasked.System.Tick(in hMasked.Frame, planMasked, hMasked.Atlas);
                    BuildMaskedPlan(storeRef, planRef, dropTileKey: -1, refVersion++);
                    hRef.System.Tick(in hRef.Frame, planRef, hRef.Atlas);
                    hRef.System.Tick(in hRef.Frame, planRef, hRef.Atlas);
                    Assert.Greater(hMasked.System.LastSurvivorCount, 0, "sanity: at least one curved placement survived frame 1");
                    Assert.Greater(MaxAlpha(hMasked.System, dropKey), 0.99f,
                        "the drop tile's curved record must be a genuinely LIVE (placed, positive-opacity) fade before the Drop");

                    // Frame 2: MASKED flags the drop tile's curved winner Dropped (resident); REFERENCE excludes it.
                    // R3: duplicate again — see the point-only test's identical comment for why (otherwise the
                    // comparison below reads frame 1's stale harvested verdict, not frame 2's own Drop-masked
                    // collision).
                    BuildMaskedPlan(storeMasked, planMasked, dropKey, maskedVersion++);
                    hMasked.System.Tick(in hMasked.Frame, planMasked, hMasked.Atlas);
                    hMasked.System.Tick(in hMasked.Frame, planMasked, hMasked.Atlas);
                    BuildReferencePlan(storeRef, planRef, dropKey, refVersion++);
                    hRef.System.Tick(in hRef.Frame, planRef, hRef.Atlas);
                    hRef.System.Tick(in hRef.Frame, planRef, hRef.Atlas);

                    Assert.AreEqual(hRef.System.LastCandidateCount, hMasked.System.LastCandidateCount,
                        "masked-Drop's candidate count must equal the reference's (the Dropped curved anchors are absent)");
                    Assert.AreEqual(hRef.System.LastSurvivorCount, hMasked.System.LastSurvivorCount,
                        "masked-Drop's survivor count must equal the reference's");
                    Assert.AreEqual(hRef.System.LastQuadCount, hMasked.System.LastQuadCount,
                        "masked-Drop's emitted quad count must equal the reference's");

                    bool maskedHasKeep = hMasked.System.TryGetWorldSlotMesh(keepKey, 0, LabelKind.Text, out Mesh keepMeshMasked);
                    bool refHasKeep = hRef.System.TryGetWorldSlotMesh(keepKey, 0, LabelKind.Text, out Mesh keepMeshRef);
                    Assert.IsTrue(maskedHasKeep && refHasKeep, "the surviving KEEP curved label's world slot must exist on both sides");
                    Assert.IsNull(FirstMeshDifference(keepMeshRef, keepMeshMasked),
                        "the surviving KEEP curved label's full vertex+opacity content must be byte-identical whether the Drop tile is masked or absent");

                    // Same equivalence as the point test, but the drop record had a genuinely LIVE curved fade before
                    // the Drop — so this proves masking a live curved record is bit-identical to its absence (it is NOT
                    // soft-faded via MarkFadeOutIfAlive, because the hard-skip precedes that chain).
                    bool maskedHasDrop = hMasked.System.TryGetWorldSlotMesh(dropKey, 0, LabelKind.Text, out Mesh dropMeshMasked);
                    bool refHasDrop = hRef.System.TryGetWorldSlotMesh(dropKey, 0, LabelKind.Text, out Mesh dropMeshRef);
                    Assert.AreEqual(refHasDrop, maskedHasDrop, "the Dropped curved record's world slot must be present/absent identically masked vs reference");
                    if (maskedHasDrop && refHasDrop)
                        Assert.IsNull(FirstMeshDifference(dropMeshRef, dropMeshMasked),
                            "the Dropped curved record's world slot content must be byte-identical whether resident-masked or physically absent");

                    // As in the point test: prove the RENDERED set, not just buffer content — KEEP visible, DROP
                    // hidden on BOTH paths (MeshRenderer.enabled), so a KEEP↔DROP swap can't pass on stale meshes.
                    Assert.IsTrue(hMasked.System.IsWorldSlotVisible(keepKey, 0, LabelKind.Text), "masked: the surviving KEEP curved slot must be VISIBLE");
                    Assert.IsTrue(hRef.System.IsWorldSlotVisible(keepKey, 0, LabelKind.Text), "reference: the surviving KEEP curved slot must be VISIBLE");
                    Assert.IsFalse(hMasked.System.IsWorldSlotVisible(dropKey, 0, LabelKind.Text), "masked: the Dropped curved slot must be HIDDEN (Renderer disabled)");
                    Assert.IsFalse(hRef.System.IsWorldSlotVisible(dropKey, 0, LabelKind.Text), "reference: the absent Drop curved slot must be HIDDEN");
                }
                finally { planMasked.Dispose(); planRef.Dispose(); }
            }
            finally { storeMasked.Clear(); storeRef.Clear(); }
        }

        // ── Blocker 1 regression: an ALL-Dropped frame must freeze live fades, not decay them ──────────────────
        [Test]
        public void AllDropped_FadeStaysFrozen_ReappearOpacityMatchesReference()
        {
            var soloTile = new TileId { Z = 12, X = 2400, Y = 1500 };
            long soloKey = Tk(soloTile);

            using var hMasked = new Harness();
            using var hRef = new Harness();
            var storeMasked = new SymbolTileLabelStore(cacheCap: 16);
            var storeRef = new SymbolTileLabelStore(cacheCap: 16);
            try
            {
                foreach (SymbolTileLabelStore store in new[] { storeMasked, storeRef })
                    SeedTile(store, soloTile, new List<LabelInstance> { PointLabel(hMasked.Origin, "solo", 1, soloKey) });

                var planMasked = new SymbolGatherPlan();
                var planRef = new SymbolGatherPlan();
                int maskedVersion = 0, refVersion = 0; // R1: each rebuild of the SAME plan object gets a fresh version
                try
                {
                    // Frame 1 (default +∞ deltaTime): visible, opacity snaps to 1.0 on both sides. R3: the
                    // collision verdict a Tick's emit reads is harvested from the PREVIOUS Tick (§2.6) —
                    // duplicate each harness's Tick call (SAME plan+args) before an assertion reads placement
                    // output.
                    BuildMaskedPlan(storeMasked, planMasked, dropTileKey: -1, maskedVersion++);
                    hMasked.System.Tick(in hMasked.Frame, planMasked, hMasked.Atlas);
                    hMasked.System.Tick(in hMasked.Frame, planMasked, hMasked.Atlas);
                    BuildMaskedPlan(storeRef, planRef, dropTileKey: -1, refVersion++);
                    hRef.System.Tick(in hRef.Frame, planRef, hRef.Atlas);
                    hRef.System.Tick(in hRef.Frame, planRef, hRef.Atlas);
                    Assert.Greater(MaxAlpha(hMasked.System, soloKey), 0.99f, "sanity: frame 1 is fully visible");

                    // Frame 2: EVERY resident record is Dropped (the only tile in the universe) — masked side keeps
                    // the winner resident+flagged Dropped; reference excludes it entirely (physically absent, the
                    // ground truth: 0 winners ⇒ _mirrorCount == 0 regardless of any gate, so the reference is
                    // fix-independent). A LARGE deltaTime here makes a wrongly-firing decay unambiguous (a step
                    // large enough to fully zero-and-remove the fade entry).
                    const float bigDeltaTime = 1.0f;
                    BuildMaskedPlan(storeMasked, planMasked, soloKey, maskedVersion++);
                    hMasked.System.Tick(in hMasked.Frame, planMasked, hMasked.Atlas, bigDeltaTime);
                    BuildReferencePlan(storeRef, planRef, soloKey, refVersion++);
                    hRef.System.Tick(in hRef.Frame, planRef, hRef.Atlas, bigDeltaTime);

                    // Frame 3: reappear (Keep again on both sides) with a SMALL deltaTime, so a frozen fade (current
                    // == 1.0) and a decayed-then-removed fade (current == 0, fading in fresh) land at visibly
                    // different opacities — not coincidentally re-converged by a single symmetric ease step.
                    // R3: F2 (all-Dropped / zero-winner) never reaches ScheduleCollision — the whole placement
                    // block, collision included, is gated on _mirrorNonDroppedCount > 0 (unchanged by R3) — so nothing
                    // is pending when F3 harvests, and F3's OWN emit reads an EMPTY _placedLastFrame (harvest's
                    // "no pending" branch), easing solo DOWN one smallDeltaTime step before its own newly-scheduled
                    // collision (solo alone, trivial winner) can be harvested. Duplicate F3 so that harvest lands
                    // (SAME args — still fade-neutral: solo is a live candidate throughout, never decays via
                    // DecayUnseenFadeRecords) — both sides dip identically then recover, so the comparison and the
                    // "reference never decayed" sanity both still hold once the second F3 Tick's harvest lands.
                    const float smallDeltaTime = 0.05f;
                    BuildMaskedPlan(storeMasked, planMasked, dropTileKey: -1, maskedVersion++);
                    hMasked.System.Tick(in hMasked.Frame, planMasked, hMasked.Atlas, smallDeltaTime);
                    hMasked.System.Tick(in hMasked.Frame, planMasked, hMasked.Atlas, smallDeltaTime);
                    BuildMaskedPlan(storeRef, planRef, dropTileKey: -1, refVersion++);
                    hRef.System.Tick(in hRef.Frame, planRef, hRef.Atlas, smallDeltaTime);
                    hRef.System.Tick(in hRef.Frame, planRef, hRef.Atlas, smallDeltaTime);

                    float reappearMasked = MaxAlpha(hMasked.System, soloKey);
                    float reappearRef = MaxAlpha(hRef.System, soloKey);
                    Assert.AreEqual(reappearRef, reappearMasked, 1e-6f,
                        "reappear opacity must match the reference (frozen fade) — an all-Dropped frame must not decay a live fade");
                    Assert.Greater(reappearRef, 0.99f, "sanity: the reference's fade never decayed (frozen), so it's still ~full opacity");
                }
                finally { planMasked.Dispose(); planRef.Dispose(); }
            }
            finally { storeMasked.Clear(); storeRef.Clear(); }
        }
    }
}
