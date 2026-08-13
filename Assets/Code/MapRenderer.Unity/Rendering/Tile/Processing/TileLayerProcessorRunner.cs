using MapRenderer.Unity.Rendering.Style;
using MapRenderer.Core.Lifetime;
using MapRenderer.Jobs.Tiles;

namespace MapRenderer.Unity.Rendering.Tile.Processing
{
    /// <summary>
    /// Epic A: the dense fan-out boundary for one tile worker pass — three disjoint entries, one per
    /// cadence: <see cref="RunWorkerPass"/> (A1, mesh, per-(tile, source) build task),
    /// <see cref="RunSourcelessWorkerPass"/> (A2, background, per covered tile — no bytes, no decode), and
    /// <see cref="RunSymbolWorkerPass"/> (A3, symbol, per queued bytes push). A4: the runner itself stays
    /// stateless — no <see cref="IDecodedTile"/> cache, no refcount here — retention lives in the
    /// caller-owned <see cref="SharedDisposable{T}"/>, which both byte-consuming entries read through instead
    /// of decoding for themselves.
    ///
    /// Stateless: no decoded-tile retention, cache, refcount, budget, or source abstraction.
    /// </summary>
    internal static class TileLayerProcessorRunner
    {
        /// <summary>Worker-thread entry point: read the already-decoded tile off the handle, run every
        /// <paramref name="processors"/> entry in order, then settle every one of them exactly once.
        /// Preserves the pre-A1 fault policy exactly: a processor exception aborts the REMAINING invocations
        /// for this pass, but every processor — invoked or not — is still completed, so its kick-allocated
        /// mesh array is never stranded. Does NOT release <paramref name="decode"/> — the runner does not own
        /// the reference; the caller does.</summary>
        internal static IRenderLayerPayload[] RunWorkerPass(
            SharedDisposable<IDecodedTile> decode, in TileLayerProcessContext context, ITileMeshLayerProcessor[] processors)
        {
            int count = processors.Length;

            try
            {
                // IR C1 P3: no pass-scoped store any more. The decoded tile owns one buffer per source-layer,
                // minted once inside the decode, so a source-layer named by N style layers — across BOTH
                // cadences of this kick, not just this pass — is materialized once. Lifetime is the caller's
                // REFERENCE, not this method.
                IDecodedTile tile = decode.Value;
                for (int i = 0; i < count; i++)
                {
                    ITileMeshLayerProcessor processor = processors[i];

                    // A1 only choreographs WorkerOnly. WorkerThenMain is reserved for A3; running it here
                    // would silently execute a phase this runner has no main-thread tail for — treat it as
                    // a programming error inside the SAME settlement boundary as any other fault (still
                    // caught below, still settled in the loop after).
                    if (processor.Phase != LayerPhase.WorkerOnly)
                        throw new System.NotSupportedException(
                            $"{processor.Phase} is not supported by A1's worker pass (reserved for A3).");

                    processor.ProcessOnWorker(tile, in context);
                }
            }
            catch (System.Exception ex)
            {
                // A processor exception or an unsupported phase aborts the remaining invocations for this
                // pass — matching the pre-A1 KickMeshBuild catch. Fall through so EVERY processor still
                // settles below (no stranded MeshDataArray).
                //
                // The LOG is not decoration. An ordinal out-of-range from the IR C1 P2 re-base lands here, and
                // so would a processor read against a decoded tile's NativeArray after its buffers were
                // freed — R2: SharedDisposable is undefended by design (no throw on a released `Value`), so
                // that fault now surfaces from Unity's own NativeContainer safety checks rather than a lease
                // guard, but the settle-everything-and-log posture is unchanged. (A malformed tile's decode
                // fault never reaches here either: the decode happens in the source's GetTile task and faults
                // THAT, so nothing is ever minted and this pass never runs for it.) The symbol cadence already
                // logs (SymbolLabelSubsystem.SymbolTileWorkerPass.RunWorkerAndHandoff); this is the same
                // shape, so the two cadences agree. Control flow is UNCHANGED: settle-everything below,
                // exactly as before.
                UnityEngine.Debug.LogWarning(
                    $"[TileLayerProcessorRunner] mesh worker pass failed for tile {context.Tile}: {ex.Message}");
            }

            // Moved form of the pre-A1 ensure-wrapped loop: settle every processor exactly once, in dense
            // order, regardless of how far the loop above got.
            var payloads = new IRenderLayerPayload[count];
            for (int i = 0; i < count; i++)
            {
                // Per-processor guard (reconciliation refinement #1): UNREACHABLE in production — the real
                // adapter's Complete() only wraps an already-allocated array and cannot throw. It exists so a
                // hypothetical contract-violating throw from one processor's Complete() does not strand its
                // SIBLINGS' arrays (each still settles). Residual risk if it ever did fire: only the throwing
                // processor's OWN array leaks (the wrap that would free it is exactly what threw) — a bounded
                // native leak, never a crash. The null slot it leaves is tolerated downstream
                // (ConsumeMeshBuild's `payload == null` guard + DisposeResult's `?.Dispose()`). A1 merge-step
                // follow-up: documented-as-unreachable rather than asserted, so the guard keeps tolerating.
                try { payloads[i] = processors[i].Complete(); }
                catch { payloads[i] = null; }
            }
            return payloads;
        }

        /// <summary>Epic A / A2 (design §B Q2): the decode-free sibling of <see cref="RunWorkerPass"/> for
        /// SOURCE-LESS processors (background) — no decode call at all, so a source-less style layer never
        /// fetches or decodes tile bytes (structurally asserted by
        /// <c>TileProcessingStructureTests.RunSourcelessWorkerPass_DoesNotDecode</c>). Invokes every
        /// <paramref name="processors"/> entry with a <c>null</c> <see cref="IDecodedTile"/> — source-less
        /// processors (<see cref="TileBackgroundLayerProcessor"/>) ignore it entirely — then settles every
        /// one of them exactly once, mirroring <see cref="RunWorkerPass"/>'s settlement/fault contract
        /// exactly (abort-remaining-on-fault, then unconditional per-processor settle).</summary>
        internal static IRenderLayerPayload[] RunSourcelessWorkerPass(
            in TileLayerProcessContext context, ITileMeshLayerProcessor[] processors)
        {
            int count = processors.Length;

            try
            {
                for (int i = 0; i < count; i++)
                {
                    ITileMeshLayerProcessor processor = processors[i];

                    // A2 only choreographs WorkerOnly, same as A1's RunWorkerPass.
                    if (processor.Phase != LayerPhase.WorkerOnly)
                        throw new System.NotSupportedException(
                            $"{processor.Phase} is not supported by A2's source-less worker pass.");

                    // null decoded tile — source-less processors (TileBackgroundLayerProcessor) ignore it;
                    // the two overloads serve disjoint processor sets (design §B Q2 / §G risk 6).
                    processor.ProcessOnWorker(null, in context);
                }
            }
            catch
            {
                // Abort the remaining invocations for this pass — matching RunWorkerPass. Fall through so
                // EVERY processor still settles below (no stranded MeshDataArray).
            }

            var payloads = new IRenderLayerPayload[count];
            for (int i = 0; i < count; i++)
            {
                // Same unreachable-in-production per-processor settle guard as RunWorkerPass (see its comment):
                // tolerates a hypothetical Complete() throw without stranding siblings; null slot handled downstream.
                try { payloads[i] = processors[i].Complete(); }
                catch { payloads[i] = null; }
            }
            return payloads;
        }

        /// <summary>Epic A / A3 (design §B Q1/Q2): the symbol cadence's worker-pass entry — read the
        /// shared decode (decoding it if this is the first cadence to arrive — A4), then invoke every
        /// <see cref="ITileWorkerThenMainLayerProcessor"/> entry's <see cref="ITileLayerProcessor.ProcessOnWorker"/>
        /// in dense (declared) order against that same decoded <see cref="IDecodedTile"/> reference. As of A4
        /// this reads the SAME <see cref="IDecodedTile"/> the mesh pass reads via <paramref name="decode"/> —
        /// no second decode.
        ///
        /// <para>Unlike <see cref="RunWorkerPass"/>/<see cref="RunSourcelessWorkerPass"/> this entry runs NO
        /// settlement loop and invokes NO tail: there is no kick-allocated native memory to strand (symbol
        /// holds only managed state), and the main-thread tail is by definition the caller's step, run after
        /// this method returns. A processor exception PROPAGATES to the caller (design §B "fault policy:
        /// propagate, don't settle") — swallowing it here would let a subsequent tail run over an
        /// empty/partial extraction and commit an empty label list, an observable behaviour change from the
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
