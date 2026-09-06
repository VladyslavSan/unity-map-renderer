// Unity EditMode only — reads source files under Application.dataPath. NOT registered in core-tests.csproj.

using System;
using System.IO;
using System.Text.RegularExpressions;
using NUnit.Framework;
using UnityEngine;

namespace MapRenderer.Tests.Structure
{
    /// <summary>
    /// IR stage B3: the structural teeth on the line consumer of Waist 1.
    ///
    /// <para><b>Landmine #5 — the fused-<c>RingAssemblyJob</c> fence.</b> <c>RingAssemblyJob</c> is a fused,
    /// <b>fill-only</b> stage: one pass applies fill's <c>rLen &lt; 3</c> filter, a degenerate-<b>area</b>
    /// filter, and exterior/hole classification. None of the three is meaningful for a line — a straight
    /// polyline has exactly zero area and would be dropped, and lines have no outer/hole concept at all. The
    /// behavioural falsifier is <c>StraightZeroAreaPolyline_StillRenders</c>; this is the structural one, and
    /// the two observe different things: a shortcut that reused the filter's <i>threshold</i> without naming
    /// the type would evade this file, and a shortcut that scheduled the job on a fixture whose geometry
    /// happens to survive would evade the behavioural one.</para>
    ///
    /// <para><b>Ownership, INVERTED by IR C1 P2.</b> The buffer is no longer transferred to the line builder —
    /// it is <b>BORROWED</b> from the pass-scoped <c>TileGeometryStore</c>, which lends the same instance to
    /// every style layer naming that source-layer. The builder must therefore mint <b>zero</b> buffers and
    /// dispose <b>zero</b>. A consumer that freed a borrowed buffer would free geometry its sibling layers are
    /// still reading, and on this path that second free is <b>loud</b>: the store hands back an array-backed
    /// buffer whose <c>Dispose</c> frees three real <c>NativeArray</c>s, and P2's R6 sweep measured the double
    /// free as <b>32 failures across 19 fixtures</b> — two with no line involvement at all, the
    /// heap-corruption signature. (The "disposing it is a silent no-op" caveat belongs to the
    /// <c>AsArray()</c>-view, list-backed mode, which line never borrows.) This instrument is structural not
    /// because the behavioural signal is absent, but because it names the rule at the source and fails
    /// deterministically on the one offending line instead of as a scatter of unrelated red fixtures.</para>
    /// </summary>
    [TestFixture]
    public class StyledLineBuilderStructureTests
    {
        /// <summary>Types that belong to the FILL assembly path. Line must reference none of them.</summary>
        private static readonly string[] ForbiddenFillStageTokens =
        {
            "RingAssemblyJob", "EarcutJob", "RingClipJob", "FillMeshPipeline",
            "GlobeFillSubdivide", "PolygonAssembler", "AdoptClippedLists",
            "DegenerateThreshold", "SignedArea",
        };

        /// <summary>Tokens that must be PRESENT. Without them a renamed, gutted or deleted file would satisfy
        /// every "count is zero" claim above trivially — the easiest kind of tooth to make vacuous.</summary>
        // IR C1 P2: "MvtGeometryMaterializer" left this set with the mint it named. Its replacements are the
        // identifiers of the mechanism the file uses NOW — the borrowed buffer type and the ordinal join —
        // so a gutted or re-pointed file still cannot satisfy the zero-counts trivially.
        //
        // job-scheduling-design.md §8 stage 5 Group B: RingFeatureIdx/FeatureGeometryType/RibbonJob/
        // RingOffsets retired from this SET (though the first three still appear in prose comments) — the
        // per-ring buffer read and the ribbon build both moved into the Burst job graph
        // (RingGatherJob/RibbonBatchJob, MapRenderer.Jobs/LineMeshGraph.cs), which this file no
        // longer touches by name; it schedules and completes the graph, then writes. The replacements name
        // THAT mechanism, so a gutted file still cannot satisfy the zero-counts trivially.
        private static readonly string[] RequiredTokens =
        {
            "TileGeometryBuffers", "LineMeshGraph", "LineGraphOutput", "new LayerInput", "LineStreamWriteJob",
        };

        [Test]
        public void LineBuilderDoesNotTouchTheFillAssemblyStages()
        {
            string source = LineBuilderSource();
            string code   = StripLineComments(source);

            Assert.Greater(code.Trim().Length, 0, "precondition: the line builder source is non-empty");
            foreach (string token in RequiredTokens)
            {
                Assert.Greater(CountOccurrences(code, token), 0,
                    $"precondition: StyledLineTileBuilder must still reference '{token}' — without it this " +
                    "test's zero-counts would be satisfied by a gutted file");
            }

            foreach (string token in ForbiddenFillStageTokens)
            {
                Assert.AreEqual(0, CountOccurrences(code, token),
                    $"StyledLineTileBuilder must reference ZERO fill-assembly symbols — '{token}' found. " +
                    "Line iterates the shared buffer itself: it has no polygon or hole concept, and its " +
                    "filter is a COUNT threshold (>= 2), never an area threshold. A straight polyline has " +
                    "exactly zero signed area and RingAssemblyJob would drop it.");
            }
        }

        /// <summary>
        /// Vestige sweep (ordinal 7): re-founded file-wide. The retired synchronous <c>WriteMeshData</c> was
        /// this tooth's whole subject — its per-ring loop was the only place a mint/dispose could have crept
        /// back in. <c>WriteMeshData</c> moved to the test assembly (zero production callers), so extracting
        /// its body is no longer meaningful; the fence now scans BOTH partial files that make up
        /// <c>StyledLineTileBuilder</c> (it is declared <c>partial</c> across
        /// <c>StyledLineTileBuilder.cs</c> and <c>StyledLineTileBuilder.WriteJob.cs</c>), comments stripped.
        /// This is STRICTLY STRONGER than the method-body version: a mint or a <c>geometry.Dispose()</c>
        /// ANYWHERE in the type now reds it, not only inside one method.
        /// </summary>
        [Test]
        public void LineBuilderMintsNoBufferAndDisposesNone_ItBorrows()
        {
            string body = StripLineComments(LineBuilderSource() + LineBuilderWriteJobSource());

            // Non-vacuity anchors, re-pointed from the retired method-body extraction: a wrong or gutted
            // scan would still need to explain away BOTH of these, present across the two files today.
            // LineMeshGraph.Schedule is deliberately NOT an anchor here — precondition 13 measured its one
            // occurrence to be INSIDE the method that moved out of production, so keeping it would red this
            // fence immediately against otherwise-correct code.
            Assert.GreaterOrEqual(CountOccurrences(body, "BuildLayerInput("), 1,
                "precondition: the builder still passes the borrowed `geometry` on to build the graph's " +
                "input — without this, a gutted file would satisfy both zero-counts below vacuously");
            Assert.GreaterOrEqual(CountOccurrences(body, "new LayerInput"), 1,
                "precondition: the builder still constructs a LayerInput from the borrowed geometry — " +
                "without this, a gutted file would satisfy both zero-counts below vacuously. (Not the bare " +
                "word `LayerInput`: it is a substring of `BuildLayerInput` — both the method's own name and " +
                "its return-type token in the signature — so a stub with a gutted body but an intact " +
                "signature would satisfy that trivially and make the check no longer independent of the " +
                "`BuildLayerInput(` anchor above. `new LayerInput` appears only at the real construction " +
                "site, `return new LayerInput { ... }`, deep in the body, which a gutted implementation " +
                "cannot reach.)");

            // Three mint APIs exist on the buffer type: .Materialize(), TileGeometryBuffers.Allocate(...) and
            // TileGeometryBuffers.AdoptDerivedLists(...). Greping only the first would leave a per-layer
            // Allocate(...) clone (all-native, so also invisible to the runtime Is.Not.AllocatingGCMemory()
            // tooth) undetected — matched with the trailing '(' so both no-arg and arg'd call forms count.
            foreach (string mintToken in new[]
                     { ".Materialize(", "TileGeometryBuffers.Allocate(", "TileGeometryBuffers.AdoptDerivedLists(" })
            {
                Assert.AreEqual(0, CountOccurrences(body, mintToken),
                    $"IR C1 P2 INVERTED this from 1 to 0. StyledLineTileBuilder must mint NOTHING — " +
                    $"'{mintToken}' found. The source-layer's buffer is materialized once per worker pass by " +
                    "TileGeometryStore and lent to every style layer naming that source-layer. A mint here is " +
                    "the 61-decodes-per-pass regression B7 retired, re-introduced one layer down.");
            }
            Assert.AreEqual(0, CountOccurrences(body, "geometry.Dispose()"),
                "…and must free NOTHING. Disposing a BORROWED buffer frees geometry sibling layers are still " +
                "reading. That double free is LOUD here — the store lends an array-backed buffer, and R6 " +
                "measured it as 32 failures across 19 fixtures, two with no line involvement (heap " +
                "corruption). This tooth is structural to fail on the offending LINE, not because the " +
                "behavioural signal is missing.");
        }

        // job-scheduling-design.md §8 stage 5 Group B: WriteMeshData_StagesRibbonInNativeScratch_
        // NotManagedLists is RETIRED here, not "made to pass". Its subject was the cross-ring staging
        // accumulators (NativeList<LinePositionNormal>/<LineWidthColor>/<Vector2>) that used to live INSIDE
        // WriteMeshData's own per-ring loop, block-copied into the Mesh.MeshData at the end. B.5 deleted
        // that loop: the ribbon is now built by RibbonBatchJob (MapRenderer.Jobs/LineMeshGraph.cs) and
        // written straight into the Mesh.MeshData by LineStreamWriteJob — there is no cross-ring staging
        // step left anywhere for a managed List<> to sneak into.
        //
        // NOT because a managed List<> would fail to compile here: a `new List<T>()` local in WriteMeshData's
        // own (managed, non-Burst) C# body is perfectly legal — the deleted fence counted a token in a
        // METHOD BODY, not a field on a job struct, and even for a job struct the guard is a RUNTIME
        // reflection check at Schedule()/Run() (thrown only when Burst is actually invoked), not a compile
        // error. The real replacement coverage is a runtime measurement, not a compiler guarantee:
        // LineBuildAllocTests.WriteMeshData_OverAConstantStyle_AllocatesNoGCMemory (LineBuildAllocTests.cs:49)
        // is a live Is.Not.AllocatingGCMemory() check over this exact method, now routed through the graph —
        // Burst-toggle-independent (it measures the managed call site, not a job-compile artifact) and it
        // would catch a reintroduced managed List<> the same way it already catches any other GC-heap
        // regression on this path.

        // Vestige sweep (ordinal 7): WriteMeshData_DisposesNativeScratchViaUsing_NotHandRolledTryFinally is
        // RETIRED here, not re-founded. All five `using var` sites this tooth pinned were INSIDE the retired
        // synchronous WriteMeshData's own body — after it moved to the test assembly, this file has ZERO
        // `using var` sites left, so the tooth's own non-vacuity precondition
        // (`Assert.GreaterOrEqual(CountOccurrences(body, "using var "), 1)`) would fail on correct code if
        // re-founded file-wide. The `using`-based-disposal idiom left production entirely with the method
        // that used it; there is nothing left here to fence. (A weaker second reason also holds: BuildLayerInput's
        // deliberate `catch { …Dispose(); throw; }` would red a file-wide no-manual-`.Dispose()` clause — but
        // `finally {` is already zero file-wide and the `= default;` regex matches nothing, so this is not the
        // deciding reason, only a note against re-founding the clause as-is.) The property this tooth
        // protected — no handle stranded by a throw partway through construction — is now carried by the
        // language wherever a `using var` remains elsewhere in the codebase, and by the runtime
        // alloc/disposal suites (LineBuildAllocTests, DisposalLeakGuardTests) rather than by a structural
        // fence.

        private static string LineBuilderSource()
        {
            string path = Path.Combine(
                Application.dataPath, "Code", "MapRenderer.Unity", "Rendering", "Meshing",
                "StyledLineTileBuilder.cs");
            Assert.IsTrue(File.Exists(path), $"expected source file to exist at {path}");
            return File.ReadAllText(path);
        }

        /// <summary>The type's second partial file (job-scheduling-design.md §8 stage 5) — the write-step
        /// job lives here. Ordinal 7's re-founded fence must scan both.</summary>
        private static string LineBuilderWriteJobSource()
        {
            string path = Path.Combine(
                Application.dataPath, "Code", "MapRenderer.Unity", "Rendering", "Meshing",
                "StyledLineTileBuilder.WriteJob.cs");
            Assert.IsTrue(File.Exists(path), $"expected source file to exist at {path}");
            return File.ReadAllText(path);
        }

        private static int CountOccurrences(string text, string token)
            => Regex.Matches(text, Regex.Escape(token)).Count;

        /// <summary>Strips everything from <c>//</c> to end of line, so a symbol merely NAMED in a comment is
        /// not counted as a reference. Deliberately narrow — a grep guard, not a C# parser.</summary>
        private static string StripLineComments(string text)
        {
            string[] lines = text.Split('\n');
            for (int i = 0; i < lines.Length; i++)
            {
                int idx = lines[i].IndexOf("//", StringComparison.Ordinal);
                if (idx >= 0) lines[i] = lines[i].Substring(0, idx);
            }
            return string.Join('\n', lines);
        }

    }
}
