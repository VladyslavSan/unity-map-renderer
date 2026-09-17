using System.Threading;
using MapRenderer.Unity.Rendering.Meshing;
using MapRenderer.Unity.Rendering.Style;
using MapRenderer.Core.Lifetime;
using MapRenderer.Jobs.Tiles;

namespace MapRenderer.Unity.Rendering.Tile.Processing
{
    /// <summary>
    /// Epic A: the dense fan-out boundary for one tile worker pass — two disjoint entries, one per cadence:
    /// <see cref="RunWorkerPass"/> (A1, mesh, per-(tile, source) build task) and <see cref="RunSymbolWorkerPass"/>
    /// (A3, symbol, per queued bytes push). job-scheduling-design.md §8 stage 3 retired the source-less
    /// (background) entry — a background tile schedules its measure graph directly
    /// (<c>TileManager.KickSourcelessBackground</c>), no worker pass of its own. A4: the runner itself stays
    /// stateless — no <see cref="IDecodedTile"/> cache, no refcount here — retention lives in the
    /// caller-owned <see cref="SharedDisposable{T}"/>, which the mesh cadence reads through instead of
    /// decoding for itself.
    ///
    /// Stateless: no decoded-tile retention, cache, refcount, budget, or source abstraction.
    /// </summary>
    internal static class TileLayerProcessorRunner
    {
        /// <summary>Worker-thread entry point: read the already-decoded tile off the handle, run every
        /// <paramref name="processors"/> entry in order, then settle every one of them exactly once.
        /// Preserves the pre-A1 fault policy exactly: a processor exception aborts the REMAINING invocations
        /// for this pass, but every processor — invoked or not — is still <c>Release</c>d, returning it to its
        /// pool. Does NOT release <paramref name="decode"/> — the runner does not own the reference; the
        /// caller does.
        ///
        /// <para>Teardown cancellation (constraint 1, no new disposal path): if <paramref name="token"/> is
        /// already cancelled, the processing block above is skipped entirely — a build queued in the
        /// ThreadPool but not yet started never touches <c>decode.Value</c>, which matters because
        /// <see cref="SharedDisposable{T}"/> is undefended by design and its buffers may be racing teardown.
        /// A build already mid-pass aborts at the next per-iteration check. Either way control falls straight
        /// through to the unconditional settle loop below, which returns every processor's own slot as
        /// <c>null</c> when it never built a request (job-scheduling-design.md §8 stage 5 Group B: there is
        /// no kick-allocated array any more to wrap zero-vertex — a graph-arm processor with nothing to settle
        /// simply leaves its slot empty). The task still Succeeds either way, so
        /// <see cref="TilePrologueOutput.Dispose"/> — called from the pen-drain funnel
        /// (<c>PendingDisposalQueue.DrainCompleted</c>) exactly once per prologue — is what frees whatever a
        /// released-before-consumed tile's requests still hold. No Canceled status, no second disposal
        /// path.</para></summary>
        /// <param name="decode">The caller-owned shared decode lease this pass reads (never released here).</param>
        /// <param name="context">Per-tile projection/origin/zoom context, unchanged by cancellation.</param>
        /// <param name="processors">One this-source processor per dense layer id, in SLOT order.</param>
        /// <param name="token">The teardown/lifetime token; defaults to <see cref="CancellationToken.None"/>
        /// for callers outside <c>KickMeshBuild</c> (existing tests) that have no lifetime to observe.</param>
        internal static TilePrologueOutput RunWorkerPass(
            SharedDisposable<IDecodedTile> decode, in TileLayerProcessContext context, ITileMeshLayerProcessor[] processors,
            CancellationToken token = default)
        {
            int count = processors.Length;

            // perf/gc-elimination: rent THIS build's buffers once, for its whole worker pass — every layer/
            // feature this pass processes reuses the same instance (sequential within a build) — and return it
            // unconditionally so a faulted or cancelled build still gives it back (never stranded, never leaked
            // to a build that never returns it). Two RunWorkerPass calls never share one instance: each runs on
            // its own ThreadPool task and rents its OWN buffers from the thread-safe pool.
            TileBuildBuffers buffers = TileBuildBuffersPool.Rent();
            try
            {
                // Only the Buffers field differs from the caller's context — copied field-for-field rather
                // than mutating `context` (a `readonly struct` taken `in`), and rather than `with` (C# 9's
                // record-only form; this project's language version does not extend it to plain structs).
                var passContext = new TileLayerProcessContext
                {
                    Tile             = context.Tile,
                    Zoom             = context.Zoom,
                    TileOriginRender = context.TileOriginRender,
                    Projection       = context.Projection,
                    BufferClip       = context.BufferClip,
                    Buffers          = buffers,
                };

                if (!token.IsCancellationRequested)
                {
                    try
                    {
                        // IR C1 P3: no pass-scoped store any more. The decoded tile owns one buffer per source-layer,
                        // minted once inside the decode, so a source-layer named by N style layers — across BOTH
                        // cadences of this kick, not just this pass — is materialized once. Lifetime is the caller's
                        // REFERENCE, not this method.
                        IDecodedTile tile = decode.Value;
                        for (int i = 0; i < count; i++)
                        {
                            // Teardown cancellation: abort the remaining layers of a build already mid-pass
                            // (real multi-layer covers). Falls through to the settle loop below, same as the
                            // top-of-function skip.
                            if (token.IsCancellationRequested) break;

                            ITileMeshLayerProcessor processor = processors[i];

                            // A1 only choreographs WorkerOnly. WorkerThenMain is reserved for A3; running it here
                            // would silently execute a phase this runner has no main-thread tail for — treat it as
                            // a programming error inside the SAME settlement boundary as any other fault (still
                            // caught below, still settled in the loop after).
                            if (processor.Phase != LayerPhase.WorkerOnly)
                                throw new System.NotSupportedException(
                                    $"{processor.Phase} is not supported by A1's worker pass (reserved for A3).");

                            processor.ProcessOnWorker(tile, in passContext);
                        }
                    }
                    catch (System.Exception ex)
                    {
                        // A processor exception or an unsupported phase aborts the remaining invocations for this
                        // pass — matching the pre-A1 KickMeshBuild catch. Fall through so EVERY processor still
                        // settles below — Release()d, returning it to its pool, whatever graph request it
                        // already built (or none) left in its own slot.
                        //
                        // The LOG is not decoration. An ordinal out-of-range from the IR C1 P2 re-base lands here, and
                        // so would a processor read against a decoded tile's NativeArray after its buffers were
                        // freed — R2: SharedDisposable is undefended by design (no throw on a released `Value`), so
                        // that fault now surfaces from Unity's own NativeContainer safety checks rather than a lease
                        // guard, but the settle-everything-and-log posture is unchanged. (A malformed tile's decode
                        // fault never reaches here either: the decode happens in the source's GetTile task and faults
                        // THAT, so nothing is ever minted and this pass never runs for it.) The symbol cadence already
                        // logs (SymbolSubsystem.SymbolTileWorkerPass.RunWorkerAndHandoff); this is the same
                        // shape, so the two cadences agree. Control flow is UNCHANGED: settle-everything below,
                        // exactly as before.
                        UnityEngine.Debug.LogWarning(
                            $"[TileLayerProcessorRunner] mesh worker pass failed for tile {context.Tile}: {ex.Message}");
                    }
                }

                // Moved form of the pre-A1 ensure-wrapped loop: settle every processor exactly once, in dense
                // order, regardless of how far the loop above got. job-scheduling-design.md §8 stage 5 Group
                // B: every processor's slot is filled by TryTakeGraphRequest — the graph is the only mesher
                // now, so there is no second (seam-arm) source to wrap into the same slot any more.
                var layers = new ILayerMeshBuild[count];
                for (int i = 0; i < count; i++)
                {
                    if (processors[i].TryTakeGraphRequest(out ILayerMeshBuild build))
                        layers[i] = build;

                    // Per-processor guard (reconciliation refinement #1): UNREACHABLE in production — the real
                    // adapter's Release() only returns itself to its pool and cannot throw. It exists so a
                    // hypothetical contract-violating throw from one processor's Release() does not strand its
                    // SIBLINGS' pool returns (each still settles). Residual risk if it ever did fire: only the
                    // throwing processor's own return to its pool is skipped — a bounded leak of ONE pooled
                    // instance, never a crash. The slot keeps whatever TryTakeGraphRequest already gave it (or
                    // default).
                    try { processors[i].Release(); }
                    catch { /* keep whatever the slot already holds; the processor's own pool-return is skipped */ }
                }
                return new TilePrologueOutput { Layers = layers };
            }
            finally
            {
                // Runs on every exit — success, an aborted pass, or (in principle) a throw that unwinds past
                // the settle loop above — so a build never strands its rented buffers: the NEXT Rent() would
                // otherwise starve the pool into minting a fresh instance forever instead of reusing this one.
                TileBuildBuffersPool.Return(buffers);
            }
        }

        /// <summary>Epic A / A3 (design §B Q1/Q2): the symbol cadence's worker-pass entry — read the
        /// shared decode (decoding it if this is the first cadence to arrive — A4), then invoke every
        /// <see cref="ITileWorkerThenMainLayerProcessor"/> entry's <see cref="ITileLayerProcessor.ProcessOnWorker"/>
        /// in dense (declared) order against that same decoded <see cref="IDecodedTile"/> reference. As of A4
        /// this reads the SAME <see cref="IDecodedTile"/> the mesh pass reads via <paramref name="decode"/> —
        /// no second decode.
        ///
        /// <para>Unlike <see cref="RunWorkerPass"/> this entry runs NO settlement loop and invokes NO tail —
        /// there is no kick-allocated native memory to strand (symbol
        /// holds only managed state), and the main-thread tail is by definition the caller's step, run after
        /// this method returns. A processor exception PROPAGATES to the caller (design §B "fault policy:
        /// propagate, don't settle") — swallowing it here would let a subsequent tail run over an
        /// empty/partial extraction and commit an empty symbol list, an observable behaviour change from the
        /// fault → no-store-commit path. Does NOT release <paramref name="decode"/> — the caller owns the
        /// reference.</para>
        /// </summary>
        internal static void RunSymbolWorkerPass(
            SharedDisposable<IDecodedTile> decode, in TileLayerProcessContext context, ITileWorkerThenMainLayerProcessor[] processors)
        {
            // IR C1 P3: no pass-scoped store. The buffers belong to the decoded tile, and the lifetime
            // argument moved with them — to the caller's REFERENCE, which is live on BOTH routes into this
            // method: the un-parked kick lambda holds the transferred one, and the parked-sprite PumpBuilds
            // dispatch holds the one the park acquired. That is what makes the mesh and symbol passes of one
            // kick share a single decode — and, since D1, makes the PARKED symbol pass share it too, so the
            // tile is decoded exactly once no matter which route runs.
            IDecodedTile tile = decode.Value;
            for (int i = 0; i < processors.Length; i++)
            {
                ITileWorkerThenMainLayerProcessor processor = processors[i];

                // The symbol pass exists to feed main-thread tails — a WorkerOnly processor here is the
                // mirrored programming error of A1/A2's guard (a WorkerThenMain processor with no tail to
                // run there).
                if (processor.Phase != LayerPhase.WorkerThenMain)
                    throw new System.NotSupportedException(
                        $"{processor.Phase} is not supported by A3's symbol worker pass (only WorkerThenMain processors have a tail this pass feeds).");

                processor.ProcessOnWorker(tile, in context);
            }
        }
    }
}
