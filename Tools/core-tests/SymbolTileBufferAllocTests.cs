// Engine-free (pure Core types + NUnit) — a core-tests-ONLY file (lives in Tools/core-tests/, NOT the Assets
// EditMode tree, so Unity never compiles it). SymbolTileBuffer/ShapedSymbol have no UnityEngine/
// Unity.Collections dependency, so this runs in the fast dotnet project. It stays out of EditMode on purpose:
// GC.GetAllocatedBytesForCurrentThread AND GC.GetTotalMemory are both dead/coarse in the Unity Mono EditMode
// runner (a 400 KB calibration alloc read 16 KB via GetTotalMemory there), so this byte-delta tooth can only
// discriminate in the CoreCLR dotnet runner. The EditMode
// zero-alloc coverage for this subsystem uses the Recorder-based Is.Not.AllocatingGCMemory() instead.

using System;
using NUnit.Framework;
using Unity.Mathematics;
using MapRenderer.Core.Text;
using MapRenderer.Core.Text.Placement;

namespace MapRenderer.Tests.Text.Placement
{
    /// <summary>
    /// Symbol-label perf Phase 1 / Stage 1 (docs/symbol-label-perf-design.md §4, §5 B): a
    /// <see cref="SymbolTileBuffer"/> reused across builds (<c>SymbolSubsystem</c>'s pool: rent
    /// → <see cref="SymbolTileBuffer.Clear"/> → repopulate) must not measurably grow the managed
    /// heap once its pooled lists' backing capacity has stabilized — the entire point of replacing a fresh
    /// per-symbol managed carrier list (plus a per-symbol layout-result object graph) per tile with one
    /// reused buffer.
    ///
    /// <para><b>Scope note</b> (the zero-alloc claim is NOT "all of <c>Shape</c>"): <c>ShapedRun</c>
    /// (<c>StyledSymbolTileBuilder.Shape</c>, `:268`) is a genuine, deliberate per-symbol allocation with
    /// no caller-buffer variant — out of scope, and <c>StyledSymbolTileBuilder</c> itself needs
    /// <c>Unity.Collections</c> transitively so it cannot run here anyway. This tooth targets exactly what
    /// IS reused: the buffer's own <see cref="SymbolTileBuffer.AddSymbol"/> +
    /// <see cref="SymbolTileBuffer.AppendQuads"/> pool-append path, which is the piece Shape's four
    /// emit sites and the baker both drive.</para>
    /// </summary>
    [TestFixture]
    public class SymbolTileBufferAllocTests
    {
        // GC.GetTotalMemory's own noise floor (brief: only trustworthy at >= ~100 KB/op).
        private const long CalibrationFloor = 100_000;

        private static readonly SymbolQuad[] SampleQuads =
        {
            new SymbolQuad { TopLeft = new float2(-8f, 8f), BottomRight = new float2(8f, -8f) },
        };

        private static void FillOnce(SymbolTileBuffer buffer, int symbolCount)
        {
            buffer.Clear();
            for (int i = 0; i < symbolCount; i++)
            {
                int quadStart = buffer.AppendQuads(SampleQuads, out int quadCount);
                buffer.AddSymbol(new ShapedSymbol
                {
                    Placement = SymbolPlacement.Point,
                    Kind = SymbolKind.Text,
                    AnchorRender = new double3(i, 0, i),
                    QuadStart = quadStart,
                    QuadCount = quadCount,
                    TextId = 1,
                    TextSizePx = 16f,
                    FeatureIndex = i,
                    TileKey = 7L,
                });
            }
        }

        /// <summary>Proves GC.GetTotalMemory is a LIVE meter in this run before the warm-reuse tooth below
        /// trusts it — a silently-dead meter would make that assertion vacuous.</summary>
        [Test]
        public void GetTotalMemory_IsALiveMeterInThisRun()
        {
            GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
            long before = GC.GetTotalMemory(true);
            var boxes = new object[2000];
            for (int i = 0; i < boxes.Length; i++) boxes[i] = new byte[200]; // ~400 KB, well clear of the floor
            long after = GC.GetTotalMemory(false);
            GC.KeepAlive(boxes);
            Assert.Greater(after - before, CalibrationFloor,
                "calibration canary: GetTotalMemory must be a live, discriminating meter in this run");
        }

        /// <summary>The 3b tooth: once <see cref="SymbolTileBuffer"/>'s pools have grown to a batch's steady
        /// size (warm-up loop below), repeated <see cref="SymbolTileBuffer.Clear"/> + repopulate over the
        /// SAME batch size must not measurably allocate — <c>List&lt;T&gt;.Clear</c> keeps its backing array,
        /// and every <c>Add</c> below writes a value-type <see cref="ShapedSymbol"/>/<see cref="SymbolQuad"/>
        /// into that array in place. RED-verify: re-introduce a per-symbol `new List&lt;SymbolQuad&gt;()` (the
        /// pre-4.4c per-symbol carrier's `Layout.Quads` shape) inside the loop and this reads orders of
        /// magnitude over the ceiling.</summary>
        [Test]
        public void WarmClearAndRepopulate_OverLargeBatch_AllocatesNoMeasurableManagedMemory()
        {
            var buffer = new SymbolTileBuffer();
            const int symbolCount = 500;

            // Warm-up: let every pooled List's backing array grow to its steady-state capacity (and let the
            // JIT settle) BEFORE the metered iteration — mirrors DecodeGeometryFlattenAllocTests' warmed loop.
            for (int w = 0; w < 5; w++) FillOnce(buffer, symbolCount);

            GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
            long before = GC.GetTotalMemory(true);
            const int iterations = 20;
            for (int it = 0; it < iterations; it++) FillOnce(buffer, symbolCount);
            long after = GC.GetTotalMemory(false);
            GC.KeepAlive(buffer);

            long bytesPerOp = (after - before) / iterations;
            Assert.LessOrEqual(bytesPerOp, 4096,
                $"warm SymbolTileBuffer reuse must not measurably allocate — read {bytesPerOp} B/op over " +
                $"{symbolCount} labels/op (a re-introduced per-label managed object would read orders of " +
                "magnitude higher, not a few KB)");
        }
    }
}
