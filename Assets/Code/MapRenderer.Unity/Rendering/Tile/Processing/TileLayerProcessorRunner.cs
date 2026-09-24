using System.Threading;
using MapRenderer.Unity.Rendering.Meshing;
using MapRenderer.Unity.Rendering.Style;
using MapRenderer.Core.Lifetime;
using MapRenderer.Jobs.Tiles;

namespace MapRenderer.Unity.Rendering.Tile.Processing
{
    /// <summary>
    /// The dense fan-out boundary for one tile worker pass — two disjoint entries, one per cadence:
    /// <see cref="RunWorkerPass"/> (mesh, per-(tile, source) build task) and <see cref="RunSymbolWorkerPass"/>
    /// (symbol, per queued bytes push). A background tile schedules its measure graph directly
    /// (<c>TileManager.KickSourcelessBackground</c>). Stateless: retention lives in the caller-owned
    /// <see cref="SharedDisposable{T}"/>, which both cadences read instead of decoding for themselves.
    /// </summary>
    internal static class TileLayerProcessorRunner
    {
        /// <summary>Worker-thread entry point: read the already-decoded tile off the handle, run every
        /// <paramref name="processors"/> entry in order, then settle every one of them exactly once. Fault
        /// policy: a processor exception aborts the REMAINING invocations for this pass, but every
        /// processor — invoked or not — is still <c>Release</c>d, returning it to its pool. Does NOT
        /// release <paramref name="decode"/> — the caller owns that reference.
        ///
        /// <para>Non-local invariant: if <paramref name="token"/> is already cancelled, the processing block
        /// is skipped entirely, so a build queued but not yet started never touches <c>decode.Value</c> —
        /// <see cref="SharedDisposable{T}"/> guards a late read only with a DEBUG assertion, and its buffers
        /// may be racing teardown. A build already mid-pass aborts at the next per-iteration check. Either
        /// way, control falls through to the unconditional settle loop, which leaves a processor's slot
        /// <c>null</c> when it built nothing. The task still Succeeds either way, so
        /// <see cref="TilePrologueOutput.Dispose"/> (called once per prologue from the pen-drain funnel) is
        /// what frees whatever a released-before-consumed tile's requests still hold — no Canceled status,
        /// no second disposal path.</para></summary>
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

            // Rent this build's own buffers from the thread-safe pool for the whole pass; the finally returns
            // them, so a faulted or cancelled build still gives them back.
            TileBuildBuffers buffers = TileBuildBuffersPool.Rent();
            try
            {
                // Only Buffers differs. `context` is a readonly `in` struct, and this language version has no
                // `with` on plain structs, so the copy is field-for-field.
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
                        // No pass-scoped store: the decoded tile owns one buffer per source-layer, minted once
                        // inside the decode, so a source-layer named by N style layers is materialized once.
                        IDecodedTile tile = decode.Value;
                        for (int i = 0; i < count; i++)
                        {
                            // Teardown cancellation: abort the remaining layers of a build already mid-pass,
                            // falling through to the settle loop below, same as the top-of-function skip.
                            if (token.IsCancellationRequested) break;

                            ITileMeshLayerProcessor processor = processors[i];

                            // A WorkerThenMain processor here would run with no main-thread tail: a programming
                            // error, caught inside the same settlement boundary as any other fault.
                            if (processor.Phase != LayerPhase.WorkerOnly)
                                throw new System.NotSupportedException(
                                    $"{processor.Phase} is not supported by the mesh worker pass (reserved for the symbol worker pass).");

                            processor.ProcessOnWorker(tile, in passContext);
                        }
                    }
                    catch (System.Exception ex)
                    {
                        // Non-obvious why: this log is the only signal in a release player, which has no DEBUG
                        // assertion or NativeContainer check for a late read of freed decode buffers. The symbol
                        // cadence logs the same way (SymbolSubsystem.SymbolTileWorkerPass.RunWorkerAndHandoff).
                        UnityEngine.Debug.LogWarning(
                            $"[TileLayerProcessorRunner] mesh worker pass failed for tile {context.Tile}: {ex.Message}");
                    }
                }

                // Settle every processor exactly once, in dense order, regardless of how far the loop above
                // got. TryTakeGraphRequest fills each slot; the graph is the only mesher.
                var layers = new ILayerMeshBuild[count];
                for (int i = 0; i < count; i++)
                {
                    if (processors[i].TryTakeGraphRequest(out ILayerMeshBuild build))
                        layers[i] = build;

                    // Release() only returns itself to its pool; this guard keeps one throwing Release() from
                    // stranding its siblings' returns, at the cost of one leaked pooled instance.
                    try { processors[i].Release(); }
                    catch { /* keep whatever the slot already holds; the processor's own pool-return is skipped */ }
                }
                return new TilePrologueOutput { Layers = layers };
            }
            finally
            {
                // Runs on every exit, so a build never strands its rented buffers. A stranded instance
                // would starve the pool into minting fresh instances forever.
                TileBuildBuffersPool.Return(buffers);
            }
        }

        /// <summary>The symbol cadence's worker-pass entry — read the already-decoded tile off the handle,
        /// then invoke every
        /// <see cref="ITileWorkerThenMainLayerProcessor"/> entry's <see cref="ITileLayerProcessor.ProcessOnWorker"/>
        /// in dense order against the SAME decoded <see cref="IDecodedTile"/> reference the mesh pass reads
        /// via <paramref name="decode"/>, so there is no second decode. Non-local invariant: unlike
        /// <see cref="RunWorkerPass"/> this entry runs NO settlement loop and invokes NO tail (symbol holds
        /// only managed state, and the main-thread tail is the caller's own step after this returns). A
        /// processor exception PROPAGATES to the caller — swallowing it here would let a subsequent tail
        /// commit an empty symbol list over a partial extraction. Does NOT release <paramref name="decode"/>
        /// — the caller owns the reference.
        /// </summary>
        internal static void RunSymbolWorkerPass(
            SharedDisposable<IDecodedTile> decode, in TileLayerProcessContext context, ITileWorkerThenMainLayerProcessor[] processors)
        {
            // The buffers belong to the decoded tile. Both routes here (the kick lambda and the parked-sprite
            // PumpBuilds dispatch) hold a live reference, so the tile is decoded once on either route.
            IDecodedTile tile = decode.Value;
            for (int i = 0; i < processors.Length; i++)
            {
                ITileWorkerThenMainLayerProcessor processor = processors[i];

                // The symbol pass exists to feed main-thread tails — a WorkerOnly processor here is the
                // mirrored programming error of the mesh pass's guard.
                if (processor.Phase != LayerPhase.WorkerThenMain)
                    throw new System.NotSupportedException(
                        $"{processor.Phase} is not supported by the symbol worker pass (only WorkerThenMain processors have a tail this pass feeds).");

                processor.ProcessOnWorker(tile, in context);
            }
        }
    }
}
