// UMR-151 T6/T7 — commit atomicity over MapView.SetStyle's full-rebuild arm
// (docs/tile-pipeline-design.md §1.10). One tooth per mutation site after which an exception would leave
// persistent state a later call or frame reads, plus the no-blank-window bound T7 holds.
//
// The six sites are NOT six properties. They are three (§3.0 of the plan):
//   A — the old style stays fully live         (site 1 only; sites 2-6 run at or after Layers.Build,
//                                               which has already destroyed every old Material)
//   B — an aborted rebuild is not absorbed by the retry's in-place gate   (sites 2-5, ONE mechanism)
//   C — no husk record, no leak, no double-free (site 6)

using System.Collections.Generic;
using NUnit.Framework;
using UnityEngine;
using MapRenderer.Core.Data;
using MapRenderer.Core.Geo;
using MapRenderer.Core.Lifetime;
using MapRenderer.Core.Style;
using MapRenderer.Jobs.Tiles;      // IDecodedTile
using MapRenderer.Unity.Rendering.Materials;
using MapRenderer.Unity.Rendering.Style;
using MapRenderer.Unity.Rendering.Tile;
// Two unambiguous aliases: CommitPhase is nested in the plain class MapView, which the MapView=MapViewComponent
// alias other restyle fixtures use would shadow.
using MapViewComponent = MapRenderer.Unity.Rendering.Map.MapViewComponent;
using CommitPhase = MapRenderer.Unity.Rendering.Map.MapView.CommitPhase;

namespace MapRenderer.Tests.Style
{
    [TestFixture]
    internal class RestyleCommitAtomicityTests
    {
        /// <summary>Fixture-private, so <c>Assert.Throws&lt;ProbeAbort&gt;</c> cannot be satisfied by an
        /// unrelated throw from material validation, the parser, or a decode fault.</summary>
        private sealed class ProbeAbort : System.Exception { }

        // ── Fixtures ──────────────────────────────────────────────────────────────────────────

        /// <summary>F1 — three fill layers over ONE vector source, at slots [a, b, c].</summary>
        private static StyleDocument ThreeFillsAbc() => StyleParser.Parse(@"{
            ""version"": 8,
            ""sources"": { ""maplibre"": { ""type"": ""vector"", ""tiles"": [""https://example.invalid/{z}/{x}/{y}.pbf""] } },
            ""layers"": [
                { ""id"": ""a"", ""type"": ""fill"", ""source"": ""maplibre"", ""source-layer"": ""countries"", ""paint"": { ""fill-color"": [""rgba"", 200, 0, 0, 1] } },
                { ""id"": ""b"", ""type"": ""fill"", ""source"": ""maplibre"", ""source-layer"": ""countries"", ""paint"": { ""fill-color"": [""rgba"", 0, 200, 0, 1] } },
                { ""id"": ""c"", ""type"": ""fill"", ""source"": ""maplibre"", ""source-layer"": ""countries"", ""paint"": { ""fill-color"": [""rgba"", 0, 0, 200, 1] } }
            ]
        }");

        /// <summary>F1's B variant — layer <c>b</c> gains a filter. Mesh-affecting, so
        /// <c>SurvivingLayerGate.LayerSurvives</c> refuses and the REBUILD arm (the only arm with probes) is
        /// taken; the SOURCE set is identical, which is what makes property B's hazard live. The filter is
        /// <c>["has","NAME"]</c> — every country feature in the sample tile carries NAME, so <c>b</c> stays
        /// drawable and the mesh count does not silently change.</summary>
        private static StyleDocument ThreeFillsAbc_FilterChangedOnB() => StyleParser.Parse(@"{
            ""version"": 8,
            ""sources"": { ""maplibre"": { ""type"": ""vector"", ""tiles"": [""https://example.invalid/{z}/{x}/{y}.pbf""] } },
            ""layers"": [
                { ""id"": ""a"", ""type"": ""fill"", ""source"": ""maplibre"", ""source-layer"": ""countries"", ""paint"": { ""fill-color"": [""rgba"", 200, 0, 0, 1] } },
                { ""id"": ""b"", ""type"": ""fill"", ""source"": ""maplibre"", ""source-layer"": ""countries"", ""filter"": [""has"", ""NAME""], ""paint"": { ""fill-color"": [""rgba"", 0, 200, 0, 1] } },
                { ""id"": ""c"", ""type"": ""fill"", ""source"": ""maplibre"", ""source-layer"": ""countries"", ""paint"": { ""fill-color"": [""rgba"", 0, 0, 200, 1] } }
            ]
        }");

        /// <summary>F2 — a fill-extrusion layer BEFORE a fill layer, same source/source-layer. With
        /// <c>MapMaterialSet.FillExtrusionMaterial</c> null, <c>Build</c> skips the extrusion layer.</summary>
        private static StyleDocument ExtrusionThenFillStyle() => StyleParser.Parse(@"{
            ""version"": 8,
            ""sources"": { ""maplibre"": { ""type"": ""vector"", ""tiles"": [""https://example.invalid/{z}/{x}/{y}.pbf""] } },
            ""layers"": [
                { ""id"": ""extrusion-layer"", ""type"": ""fill-extrusion"", ""source"": ""maplibre"",
                  ""source-layer"": ""countries"", ""paint"": { ""fill-extrusion-height"": 50 } },
                { ""id"": ""shape-layer"", ""type"": ""fill"", ""source"": ""maplibre"",
                  ""source-layer"": ""countries"", ""paint"": { ""fill-color"": [""rgba"", 200, 50, 50, 1] } }
            ]
        }");

        /// <summary>A view wired like <c>RestyleHarness.NewRestyleView</c> but over a caller-owned
        /// <see cref="MapMaterialSet"/> the test may mutate — never the committed production asset
        /// <c>WithTestMaterials</c> assigns.</summary>
        private static MapViewComponent NewExtrusionView(MapMaterialSet matSet, out GameObject go)
        {
            go = new GameObject("MapView_MaterialLever");
            var view = go.AddComponent<MapViewComponent>();
            view.Config.MaterialSet = matSet;
            view.Config.TileSelection.MinZoom = 4; view.Config.TileSelection.MaxZoom = 4;
            view.WithTestCamera();
            view.Config.MaxConsumesPerTick   = 64;
            view.Config.MaxMeshBuildsPerTick = 64;
            view.Config.MaxVerticesPerTick   = int.MaxValue;
            view.Config.MaxReleasesPerTick   = 0;
            view.View.TileSourceFactoryOverride = _ => TestDataSource.FromBytes(SampleTileFixture.Bytes());
            view.View.Camera.SetProperties(RestyleHarness.Cam(10, 10, 4.0));
            view.View.Camera.SyncToCamera();
            return view;
        }

        /// <summary>Installs a probe that throws at exactly <paramref name="target"/>.</summary>
        private static void AbortAt(MapViewComponent view, CommitPhase target)
            => view.View.CommitProbe = phase => { if (phase == target) throw new ProbeAbort(); };

        /// <summary>Live layer materials — slots whose <c>Material</c> is non-null under Unity's fake-null
        /// equality. A plain read of the production slot list, not a derived accessor.</summary>
        private static int LiveMaterialCount(MapViewComponent view)
        {
            int live = 0;
            for (int i = 0; i < view.Layers.Count; i++)
                if (view.Layers[i].Material != null) live++;
            return live;
        }

        // ── T6-1 — property A: the old style stays fully live ─────────────────────────────────

        /// <summary>T6-1: an abort at <c>IdentityCommitted</c> must leave the PREVIOUS style fully live —
        /// its layers, its materials, its baked meshes and its cache token. This is the only site where
        /// property A can be asserted: every later phase runs at or after <c>Layers.Build</c>, which has
        /// already destroyed the old materials.
        /// <para>The slot-id clause compares against A's ids captured before the call, not against
        /// <c>_style</c> — the field that just moved, which would make the assertion circular. The
        /// <c>GetTileMeshes</c> reads are not an accessor artefact: that walk hides a record only when
        /// <c>_sources.Count</c> has shrunk, and <c>_sources.Rebuild</c> is step 2 of
        /// <c>TileManager.SetSources</c>, which this abort never reaches.</para>
        /// <para><b>RED recipe:</b> insert
        /// <c>if (!inPlace) Layers.Build(style, Camera.CurrentProperties.Zoom, materialSet);</c> immediately
        /// before <c>_style = style;</c> in <c>MapView.SetStyle</c>. <c>Build</c> clears and disposes the old
        /// layers first, so every captured Material is destroyed by phase 1. Gating on the already-computed
        /// <c>inPlace</c> keeps every in-place test untouched.</para></summary>
        [Test]
        public void AbortAtIdentityCommit_LeavesTheOldStyleFullyLive()
        {
            var view = RestyleHarness.NewRestyleView(SampleTileFixture.Bytes(), out var go);
            try
            {
                RestyleHarness.SpinToCompleted(view.SetStyle(ThreeFillsAbc(), "A"));
                RestyleHarness.PumpUntilSettled(view);

                Assert.AreEqual(3, view.Layers.Count,
                    "drive precondition: A's three fill layers must all build.");
                int count = view.Layers.Count;
                var layersBefore    = new IRenderLayer[count];
                var materialsBefore = new Material[count];
                var slotIdsBefore   = new string[count];
                for (int i = 0; i < count; i++)
                {
                    layersBefore[i]    = view.Layers[i];
                    materialsBefore[i] = view.Layers[i].Material;
                    slotIdsBefore[i]   = view.Layers[i].StyleLayer?.Id;
                    Assert.IsFalse(materialsBefore[i] == null,
                        $"drive precondition: slot {i} must hold a live Material before the restyle.");
                }
                Mesh[] meshesBefore = view.GetTileMeshes(RestyleHarness.TrackedTile);
                Assert.IsNotNull(meshesBefore, "drive precondition: the tracked tile must be built.");
                Assert.Greater(meshesBefore.Length, 0,
                    "drive precondition: the tracked tile must hold baked meshes.");
                var backendBefore = view.EntitiesRenderer();
                Assert.IsNotNull(backendBefore,
                    "drive precondition: NewRestyleView leaves Config.Backend at Entities, so this is non-null.");
                var tokenBefore = view.TileManager.CurrentStyle;

                AbortAt(view, CommitPhase.IdentityCommitted);
                Assert.Throws<ProbeAbort>(
                    () => RestyleHarness.SpinToCompleted(view.SetStyle(ThreeFillsAbc_FilterChangedOnB(), "B")),
                    "the probe must abort the restyle at IdentityCommitted.");
                view.View.CommitProbe = null;

                for (int i = 0; i < count; i++)
                    Assert.IsFalse(materialsBefore[i] == null,
                        $"slot {i}: A's Material must still be ALIVE after an abort at IdentityCommitted.");
                Assert.AreEqual(count, view.Layers.Count,
                    "the layer set must be untouched — the abort precedes Layers.Build.");
                for (int i = 0; i < count; i++)
                {
                    Assert.AreSame(layersBefore[i], view.Layers[i],
                        $"slot {i}: the render layer instance must be the same object.");
                    Assert.AreEqual(slotIdsBefore[i], view.Layers[i].StyleLayer?.Id,
                        $"slot {i}: the slot must still carry A's style layer, not B's.");
                }
                Mesh[] meshesAfter = view.GetTileMeshes(RestyleHarness.TrackedTile);
                Assert.IsNotNull(meshesAfter, "A's tracked tile must still be loaded.");
                Assert.AreEqual(meshesBefore.Length, meshesAfter.Length,
                    "A's baked meshes must still be present, none added or removed.");
                for (int i = 0; i < meshesBefore.Length; i++)
                    Assert.AreSame(meshesBefore[i], meshesAfter[i],
                        $"mesh {i}: A's baked Mesh instance must survive the aborted restyle.");
                Assert.AreSame(backendBefore, view.EntitiesRenderer(),
                    "the backend instance must not be rebuilt by an aborted restyle.");
                Assert.AreEqual(tokenBefore, view.TileManager.CurrentStyle,
                    "the prepared-cache token must still be A's.");
            }
            finally
            {
                view.View.CommitProbe = null;
                view.Teardown();
                Object.DestroyImmediate(go);
            }
        }

        // ── T6-2..5 — property B: an aborted rebuild is not absorbed by the retry ─────────────

        /// <summary>T6-2: an abort at <c>MaterialMemoWritten</c> must not be absorbed by the NEXT call's
        /// in-place gate. Driven through the MaterialSet lever rather than a style edit, because that is the
        /// only lever that moves <c>MaterialSnapshot</c> under byte-identical style content — which is what
        /// makes the memo the deciding conjunct.
        /// <para>Clause 5 is what makes this a property tooth rather than a count tooth: the cache token
        /// encodes the layer-numbering fold, so it is red under absorption (never rewritten) and green under
        /// a real rebuild, whatever the memo's ordering.</para>
        /// <para><b>RED recipe:</b> none needed — this is RED against the un-fixed tree, on clauses 4 and 5.
        /// It goes green with <c>_committedStyle</c> nulled for the duration of the call, which forces the
        /// rebuild arm after any abort.</para></summary>
        [Test]
        public void AbortAtMaterialMemoWrite_IsNotAbsorbedByTheRetryInPlaceGate()
        {
            var litSet = MapMaterialSetTestUtil.Load();
            var matSet = ScriptableObject.CreateInstance<MapMaterialSet>();
            matSet.FillMaterial          = litSet.FillMaterial;
            matSet.LineMaterial          = litSet.LineMaterial;
            matSet.SymbolTextWorld       = litSet.SymbolTextWorld;
            matSet.FillExtrusionMaterial = litSet.FillMaterial; // assigned — both layers build on first load

            var view = NewExtrusionView(matSet, out var go);
            try
            {
                RestyleHarness.SpinToCompleted(view.SetStyle(ExtrusionThenFillStyle(), "A"));
                RestyleHarness.PumpUntilSettled(view);
                Assert.AreEqual(2, view.Layers.Count,
                    "drive precondition: extrusion+fill must both be present, at slots [0,1].");
                var tokenBefore = view.TileManager.CurrentStyle;

                // Same MapMaterialSet reference, one field nulled in place: MaterialSnapshot now differs
                // from the memo, so the next call must take the rebuild arm.
                matSet.FillExtrusionMaterial = null;
                AbortAt(view, CommitPhase.MaterialMemoWritten);
                Assert.Throws<ProbeAbort>(
                    () => RestyleHarness.SpinToCompleted(view.SetStyle(ExtrusionThenFillStyle(), "A")),
                    "the probe must abort the restyle at MaterialMemoWritten.");
                view.View.CommitProbe = null;

                // The retry: same content, same id, same (already nulled) material set.
                RestyleHarness.SpinToCompleted(view.SetStyle(ExtrusionThenFillStyle(), "A"));

                Assert.AreEqual(1, view.Layers.Count,
                    "clause 4: the extrusion layer must be GONE — the current MapMaterialSet no longer " +
                    "supplies its material, so a real rebuild drops it. Reading 2 means the retry was " +
                    "absorbed by the in-place gate.");
                Assert.AreNotEqual(tokenBefore, view.TileManager.CurrentStyle,
                    "clause 5: the cache token must be rewritten — its layer-numbering fold moved 2 -> 1. " +
                    "An unchanged token means the retry never reached the token write.");
            }
            finally
            {
                view.View.CommitProbe = null;
                view.Teardown();
                Object.DestroyImmediate(go);
                Object.DestroyImmediate(matSet);
            }
        }
        /// <summary>T6-3/4/5: an abort at any phase from <c>LayersBuilt</c> on must not be absorbed by the
        /// next call's in-place gate. One body over three phases — one property, one root cause, one
        /// mechanism: the live layers are the aborted call's, so the retry's per-layer gate compares the new
        /// document against itself and every layer "survives".
        /// <para>Which clause carries the RED differs by phase only because the amount of committed state
        /// does: at <c>LayersBuilt</c> both the token clause and the mesh clause red; at
        /// <c>StyleTokenWritten</c> and <c>SymbolStyleApplied</c> the token has already moved, so the mesh
        /// clause carries it alone.</para>
        /// <para><b>RED recipe:</b> none needed — RED against the un-fixed tree at all three phases.</para>
        /// </summary>
        [TestCase(CommitPhase.LayersBuilt)]
        [TestCase(CommitPhase.StyleTokenWritten)]
        [TestCase(CommitPhase.SymbolStyleApplied)]
        public void AbortedRebuild_IsNotAbsorbedByTheRetryInPlaceGate(CommitPhase abortAt)
        {
            var view = RestyleHarness.NewRestyleView(SampleTileFixture.Bytes(), out var go);
            try
            {
                RestyleHarness.SpinToCompleted(view.SetStyle(ThreeFillsAbc(), "A"));
                RestyleHarness.PumpUntilSettled(view);

                Assert.AreEqual(3, view.Layers.Count,
                    "drive precondition: A's three fill layers must all build.");
                Mesh[] meshesBefore = view.GetTileMeshes(RestyleHarness.TrackedTile);
                Assert.IsNotNull(meshesBefore, "drive precondition: the tracked tile must be built.");
                Assert.Greater(meshesBefore.Length, 0,
                    "drive precondition: the tracked tile must hold baked meshes.");
                foreach (Mesh m in meshesBefore)
                    Assert.IsFalse(m == null, "drive precondition: A's baked meshes must all be alive.");
                var tokenBefore = view.TileManager.CurrentStyle;

                AbortAt(view, abortAt);
                Assert.Throws<ProbeAbort>(
                    () => RestyleHarness.SpinToCompleted(view.SetStyle(ThreeFillsAbc_FilterChangedOnB(), "B")),
                    $"the probe must abort the restyle at {abortAt}.");
                view.View.CommitProbe = null;

                RestyleHarness.SpinToCompleted(view.SetStyle(ThreeFillsAbc_FilterChangedOnB(), "B"));
                RestyleHarness.PumpUntilSettled(view);

                foreach (Mesh m in meshesBefore)
                    Assert.IsTrue(m == null,
                        $"mesh clause ({abortAt}): every Mesh baked under A must be DESTROYED by the retry's " +
                        "rebuild. A surviving one means the retry was absorbed by the in-place gate and the " +
                        "old geometry is now drawn under the new style.");
                Assert.AreNotEqual(tokenBefore, view.TileManager.CurrentStyle,
                    $"token clause ({abortAt}): the prepared-cache token must have been rewritten by the retry.");

                Assert.IsTrue(view.TryGetBuiltTile(RestyleHarness.TrackedTile),
                    "positive control: the retry must actually rebuild the tracked tile, not merely destroy.");
                Mesh[] meshesAfter = view.GetTileMeshes(RestyleHarness.TrackedTile);
                Assert.IsNotNull(meshesAfter, "positive control: the rebuilt tile must expose meshes.");
                Assert.Greater(meshesAfter.Length, 0, "positive control: the rebuilt tile must be non-empty.");
                foreach (Mesh m in meshesAfter)
                {
                    Assert.IsFalse(m == null, "positive control: every rebuilt Mesh must be alive.");
                    foreach (Mesh old in meshesBefore)
                        Assert.AreNotSame(old, m, "positive control: no rebuilt Mesh may be one of A's.");
                }
            }
            finally
            {
                view.View.CommitProbe = null;
                view.Teardown();
                Object.DestroyImmediate(go);
            }
        }

        // ── T6-6 — property C: no husk, no leak, no double-free ───────────────────────────────

        /// <summary>T6-6: an abort inside <c>TileManager.SetSources</c>' per-record teardown loop must leave
        /// no HALF-torn-down record behind (C1), and must leak nothing (C2).
        /// <para><b>Both C1 clauses stay meaningful post-fix.</b> The fix drops each record from
        /// <c>_loaded</c> BEFORE tearing that record down, one at a time, so after an abort the records the
        /// loop never reached are still there and INTACT. C1a therefore inspects real entries rather than an
        /// empty set, and C1b pins exactly that: some records remain, and strictly fewer than before.</para>
        /// <para><b>C2 cannot stand in for C1:</b> the pre-fix husk's second <c>decode.Release()</c> takes
        /// <c>remaining == -1</c>, which leaves <c>DebugLiveCount</c> untouched and
        /// <c>DebugNegativeObservations</c> at 0, and its own assert is <c>System.Diagnostics.Debug</c>,
        /// invisible to NUnit.</para>
        /// <para><b>RED recipe for C2:</b> comment out <c>DestroyTrackedMeshes(ref lt);</c> in
        /// <c>TileManager.RenderTeardownRecord</c> — records leak their meshes and the post-teardown count
        /// exceeds the baseline. That is the single teardown funnel, so it is a broad site: run filtered.
        /// C1a is RED against the un-fixed tree with no injection.</para></summary>
        [Test]
        public void AbortMidRecordTeardown_LeavesNoHuskAndLeaksNothing()
        {
            int  meshBaseline   = RestyleHarness.CountMeshObjects();
            long decodeBaseline = SharedDisposable<IDecodedTile>.DebugLiveCount;

            var view = RestyleHarness.NewRestyleView(SampleTileFixture.Bytes(), out var go);
            System.Exception teardownFault = null;
            try
            {
                RestyleHarness.SpinToCompleted(view.SetStyle(ThreeFillsAbc(), "A"));
                RestyleHarness.PumpUntilSettled(view);

                var loadedBefore = new List<TileId>();
                view.CollectLoadedTileIds(loadedBefore);
                Assert.Greater(loadedBefore.Count, 0,
                    "drive precondition: the teardown loop runs once per LOADED record, so the cover must " +
                    "be non-empty or this tooth is about nothing.");
                Mesh[] meshesBefore = view.GetTileMeshes(RestyleHarness.TrackedTile);
                Assert.IsNotNull(meshesBefore, "drive precondition: the tracked tile must be built.");
                Assert.Greater(meshesBefore.Length, 0,
                    "drive precondition: the tracked tile must hold baked meshes.");
                foreach (Mesh m in meshesBefore)
                    Assert.IsFalse(m == null,
                        "drive precondition: a settled record must expose only LIVE meshes, or C1a cannot " +
                        "tell a destroyed one from a never-built one.");

                int records = 0;
                view.View.CommitProbe = phase =>
                {
                    if (phase == CommitPhase.SourcesTeardownRecord && records++ == 0) throw new ProbeAbort();
                };
                Assert.Throws<ProbeAbort>(
                    () => RestyleHarness.SpinToCompleted(view.SetStyle(ThreeFillsAbc_FilterChangedOnB(), "B")),
                    "the probe must abort the restyle on the FIRST record teardown.");
                view.View.CommitProbe = null;

                var loadedAfter = new List<TileId>();
                view.CollectLoadedTileIds(loadedAfter);

                // C1a — never half a record: nothing still in _loaded may expose a DESTROYED mesh.
                foreach (TileId id in loadedAfter)
                {
                    Mesh[] meshes = view.GetTileMeshes(id);
                    if (meshes == null) continue;
                    for (int i = 0; i < meshes.Length; i++)
                        Assert.IsFalse(meshes[i] == null,
                            $"C1a: tile {id.Z}/{id.X}/{id.Y} is still in _loaded but its mesh {i} is " +
                            "DESTROYED — a half-torn-down husk a later frame and DoDispose both read.");
                }

                // C1b — some records remain (anti-vacuity for C1a), and strictly fewer than before (the
                // husk shapes C1a cannot see: a null mesh array, or a zero-length one).
                Assert.Greater(loadedAfter.Count, 0,
                    "C1b: the records the abort never reached must still be in _loaded — otherwise C1a " +
                    "quantified over the empty set and this tooth inspected no record at all.");
                Assert.Less(loadedAfter.Count, loadedBefore.Count,
                    "C1b: the aborted teardown must have REMOVED the record it tore down — an unchanged " +
                    "count means it left a husk.");
            }
            finally
            {
                view.View.CommitProbe = null;
                try { view.Teardown(); }
                catch (System.Exception ex) { teardownFault = ex; }
                Object.DestroyImmediate(go);
            }

            // C2 — nothing leaks. Outside the try/finally so a body failure is never masked by these.
            Assert.IsNull(teardownFault,
                $"C2: teardown after an aborted restyle must not throw (got {teardownFault}).");
            Assert.AreEqual(0, SharedDisposable<IDecodedTile>.DebugNegativeObservations,
                "C2: no decode handle may be released more times than it was leased.");
            Assert.AreEqual(decodeBaseline, SharedDisposable<IDecodedTile>.DebugLiveCount,
                "C2: every decode lease taken during this test must be released by teardown.");
            Assert.LessOrEqual(RestyleHarness.CountMeshObjects(), meshBaseline,
                "C2: the mesh count must return to baseline — an aborted restyle must leak no Mesh.");
        }

        // ── T7 — the no-blank-window bound ────────────────────────────────────────────────────

        /// <summary>T7: across the rebuild-arm commit sequence, the live layer-material count never drops
        /// below <c>min(N_old, N_new)</c> — a LOWER BOUND at every phase, never an equality. This is the
        /// no-blank-window statement this seam can hold: the brief's "a correct implementation transiently
        /// holds both sets" is false here, because all six probes are on the rebuild arm and
        /// <c>RenderLayerSet.Build</c> calls <c>ClearLayers()</c> first.
        /// <para>On this drive <c>N_old == N_new == 3</c>, so the <c>min()</c> is decoration — the bound
        /// exercised is <c>&gt;= 3</c>. The <c>min</c> form is what makes the property true in general. If a
        /// fixture with <c>N_new &lt; N_old</c> ever reaches the rebuild arm, extend this drive rather than
        /// writing a second tooth.</para>
        /// <para><b>RED recipe</b>, two, one per clause. Clause 1: insert
        /// <c>Layers.Build(null, Camera.CurrentProperties.Zoom, materialSet);</c> immediately before the
        /// <c>MaterialMemoWritten</c> probe in <c>MapView.SetStyle</c> — the live count reads 0 at that phase
        /// while the END STATE is identical, so no other test observes it. Clause 2: delete the
        /// <c>StyleTokenWritten</c> probe invocation.</para></summary>
        [Test]
        public void EveryCommitPhase_KeepsAtLeastTheSmallerLiveMaterialCount()
        {
            var view = RestyleHarness.NewRestyleView(SampleTileFixture.Bytes(), out var go);
            try
            {
                RestyleHarness.SpinToCompleted(view.SetStyle(ThreeFillsAbc(), "A"));
                RestyleHarness.PumpUntilSettled(view);

                Assert.AreEqual(3, view.Layers.Count,
                    "drive precondition: A's three fill layers must all build.");
                Assert.AreEqual(3, LiveMaterialCount(view),
                    "drive precondition: all three slots must hold a live Material before the restyle.");
                var loaded = new List<TileId>();
                view.CollectLoadedTileIds(loaded);
                Assert.Greater(loaded.Count, 0,
                    "drive precondition: SourcesTeardownRecord fires once per LOADED record, so clause 2 " +
                    "needs a non-empty cover.");

                var seen = new List<(CommitPhase Phase, int Live)>();
                view.View.CommitProbe = phase => seen.Add((phase, LiveMaterialCount(view)));
                RestyleHarness.SpinToCompleted(view.SetStyle(ThreeFillsAbc_FilterChangedOnB(), "B"));
                RestyleHarness.PumpUntilSettled(view);
                view.View.CommitProbe = null;

                // Clause 1 — the bound, at EVERY recorded entry (SourcesTeardownRecord records several).
                foreach (var entry in seen)
                    Assert.GreaterOrEqual(entry.Live, 3,
                        $"no-blank-window: live layer materials fell to {entry.Live} at {entry.Phase} — the " +
                        "commit must never hold fewer than min(N_old, N_new) == 3 live materials.");

                // Clause 2 — anti-vacuity: the probe fired, at every phase the enum declares. A seventh
                // CommitPhase added without a tooth reds here.
                var distinct = new HashSet<CommitPhase>();
                foreach (var entry in seen) distinct.Add(entry.Phase);
                foreach (CommitPhase phase in System.Enum.GetValues(typeof(CommitPhase)))
                    Assert.IsTrue(distinct.Contains(phase),
                        $"anti-vacuity: no probe was recorded at {phase} — clause 1 says nothing about a " +
                        "phase it never observed.");
                Assert.AreEqual(System.Enum.GetValues(typeof(CommitPhase)).Length, distinct.Count,
                    "anti-vacuity: the recorded phases must be exactly the declared CommitPhase set.");

                // Clause 3 — anti-vacuity: the bound is a fact about the commit, not about an empty set.
                Assert.AreEqual(3, view.Layers.Count,
                    "anti-vacuity: the rebuild must end with three slots, so the >= 3 bound is not met " +
                    "trivially by an empty layer set.");
            }
            finally
            {
                view.View.CommitProbe = null;
                view.Teardown();
                Object.DestroyImmediate(go);
            }
        }
    }
}
