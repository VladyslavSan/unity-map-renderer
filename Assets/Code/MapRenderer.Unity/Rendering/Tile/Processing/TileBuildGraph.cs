using System;
using System.Threading;
using UnityEngine;
using Unity.Jobs;
using MapRenderer.Core.Lifetime;
using MapRenderer.Jobs.Geometry;
using MapRenderer.Jobs.Tiles;
using MapRenderer.Unity.Rendering.Meshing;
using MapRenderer.Unity.Rendering.Style;

namespace MapRenderer.Unity.Rendering.Tile.Processing
{
    /// <summary>
    /// The per-tile graph-arm build (job-scheduling-design.md) — every tile's owner from measure to
    /// consume. <see cref="TilePrologueOutput"/> is the hand-off into it: its dense <c>ILayerMeshBuild[]</c>
    /// becomes this graph's own <see cref="_layers"/> ALIAS — not a copy — the moment
    /// <see cref="ScheduleMeasure"/> is called. This type dispatches nothing per kind: every layer is an
    /// <see cref="ILayerMeshBuild"/>, which owns its own kind's request columns, measure output and its
    /// own <see cref="MeshWriteOutput"/>. Non-local invariant: the ownership unit — buffers, the handle of
    /// the last job referencing them, and the inputs keeping them alive — releases in ONE order:
    /// <c>Complete()</c> the handle, then read or upload, then dispose the buffers, then release the
    /// input. One <see cref="Mesh.MeshDataArray"/> per layer, exact-sized to its vertex/index count.
    /// </summary>
    internal sealed class TileBuildGraph
    {
        /// <summary>Dense over the kick's <c>ITileMeshRenderLayer</c>s, in SLOT order — ALIASES the
        /// caller's own array (<see cref="TilePrologueOutput.Layers"/> or <c>TileManager.KickSourcelessBackground</c>'s
        /// own build array), never a copy: this graph owns each non-null element from the moment it is
        /// handed over. A <c>null</c> slot means a layer with nothing to build.</summary>
        private ILayerMeshBuild[] _layers;

        /// <summary>The ONE per-tile geometry buffer every layer's own request input
        /// borrows (<c>FillMeshPipeline.LayerInput.Geometry</c>'s own documented contract) — owned here, not
        /// per-layer, and disposed exactly once in <see cref="Dispose"/>. A background tile's every dense
        /// layer draws the identical full-extent quad, so sharing one allocation across them (rather than
        /// each layer minting and owning its own copy) is both cheaper and the only shape a build's own
        /// per-layer BORROWED contract can support without double-freeing it once per layer.</summary>
        private TileGeometryBuffers _ownedGeometry;

        /// <summary>The consumer-kind sibling of <see cref="_ownedGeometry"/>: a SOURCE tile's every layer's
        /// own request input borrows its geometry straight from the decoded tile this reference keeps
        /// alive, rather than from a buffer this graph mints itself. Transferred to this graph the moment
        /// <see cref="ScheduleMeasureFromDecode"/> is called, including on that call's own throw path.
        /// Non-local invariant: released exactly once, LAST in <see cref="Dispose"/> (after every layer
        /// and after <see cref="_ownedGeometry"/>). Every layer is disposed first, so no layer still reads
        /// the decoded tile when the reference that backs it is released.</summary>
        private SharedDisposable<IDecodedTile> _decode;

        /// <summary>The terminal handle for whichever step is currently in flight — the measure graphs'
        /// combined handle until <see cref="CompleteMeasureAndScheduleWrite"/> runs, then the write jobs'
        /// combined handle. Private: <see cref="IsStepComplete"/> and <see cref="Complete"/> are the only
        /// ways in — two ways to read one thing is one too many.</summary>
        private JobHandle _handle;

        private bool _writeScheduled;
        private bool _payloadsTaken;
        private bool _disposed;

        /// <summary>The array <see cref="CompleteWriteAndTakePayloads"/> built on its first call — cached so
        /// this graph, not the caller, owns it from here on. A repeat call returns this SAME instance
        /// (idempotent, never throws): the caller may resume a budget-bound partial consume against it
        /// without holding its own copy, and <see cref="Dispose"/> sweeps whatever is left in it (a released
        /// mid-consume tile's not-yet-taken slots) through the same funnel a completed consume already nulled
        /// slots through, so neither path can double-free the other's work.</summary>
        private MeshDataPayload[] _takenPayloads;

        private static long _liveCount;
        private static long _negativeObservations;

        /// <summary>Net live <see cref="TileBuildGraph"/> instances — incremented at
        /// <see cref="ScheduleMeasure"/>, decremented at <see cref="Dispose"/>. Mirrors
        /// <see cref="MeshDataPayload.DebugLiveAllocCount"/>'s idiom.</summary>
        internal static long DebugLiveCount => Interlocked.Read(ref _liveCount);

        /// <summary>Non-zero iff <see cref="Dispose"/>'s decrement ever took <see cref="DebugLiveCount"/>
        /// below zero despite the idempotency guard — i.e. the guard itself failed. A "back to zero" reading
        /// is only honest when this is also zero: a negative decrement can wrap back through zero on a later
        /// leak and read as clean.</summary>
        internal static long DebugNegativeObservations => Interlocked.Read(ref _negativeObservations);

        private TileBuildGraph() { }

        /// <summary>Schedules the measure graph for every requested layer. Returns UNCOMPLETED; the caller
        /// polls <see cref="IsStepComplete"/> or calls <see cref="Complete"/> before calling
        /// <see cref="CompleteMeasureAndScheduleWrite"/>. Non-local invariant: a throw partway through
        /// disposes every already-scheduled layer's build AND every not-yet-reached one — ownership
        /// transfers the moment the caller hands over <paramref name="layers"/>, not incrementally per element.
        /// </summary>
        /// <param name="layers">One build per background layer, in SLOT order — this graph ALIASES the
        /// array, it does not copy it. A <c>null</c> element is a layer with nothing to build. Every
        /// non-null build's own input is expected to borrow <paramref name="ownedGeometry"/> itself (or
        /// another buffer this graph does not own) — a build's own <c>Dispose</c> never disposes it.</param>
        /// <param name="ownedGeometry">The ONE per-tile geometry buffer this graph takes ownership of,
        /// disposed exactly once in <see cref="Dispose"/> — see that field's own doc for why a per-layer
        /// buffer would be wrong here. <c>default</c> (uncreated) is a valid "nothing to own" value.</param>
        /// <param name="deps">Upstream dependency every layer's graph must wait for — the seam a test uses
        /// to hold this genuinely in flight (job-scheduling-design.md: a delay job's handle passed here,
        /// not a test-only production hook), and in production simply <c>default</c>.</param>
        internal static TileBuildGraph ScheduleMeasure(
            ILayerMeshBuild[] layers, TileGeometryBuffers ownedGeometry, JobHandle deps = default)
        {
            try
            {
                JobHandle combined = ScheduleLayers(layers, deps);
                Interlocked.Increment(ref _liveCount);
                return new TileBuildGraph { _layers = layers, _handle = combined, _ownedGeometry = ownedGeometry };
            }
            catch
            {
                // ScheduleLayers has already freed every layer's own build; this overload's extra owned
                // resource (the producer-kind buffer) is this catch's to free.
                ownedGeometry.Dispose();
                throw;
            }
        }

        /// <summary>The consumer-kind sibling of <see cref="ScheduleMeasure"/> — a distinct NAME because
        /// <c>ScheduleMeasure(layers, default)</c> is ambiguous (CS0121) between <c>TileGeometryBuffers</c>
        /// and <c>SharedDisposable{IDecodedTile}</c>. A SOURCE tile's layers borrow their geometry from a
        /// decoded tile instead of a buffer this graph mints itself. Non-local invariant:
        /// <paramref name="decode"/> is transferred on entry, including on this call's own throw path (the
        /// catch releases it after <see cref="ScheduleLayers"/> has freed what it built) — the caller must
        /// not release it too.</summary>
        /// <param name="layers">See <see cref="ScheduleMeasure"/>.</param>
        /// <param name="decode">The kick's own reference; moves to this graph the moment this method is
        /// called and is released exactly once, last, in <see cref="Dispose"/>.</param>
        /// <param name="deps">See <see cref="ScheduleMeasure"/>.</param>
        internal static TileBuildGraph ScheduleMeasureFromDecode(
            ILayerMeshBuild[] layers, SharedDisposable<IDecodedTile> decode, JobHandle deps = default)
        {
            try
            {
                JobHandle combined = ScheduleLayers(layers, deps);
                Interlocked.Increment(ref _liveCount);
                return new TileBuildGraph { _layers = layers, _handle = combined, _decode = decode };
            }
            catch
            {
                decode?.Release();
                throw;
            }
        }

        /// <summary>The scheduling loop shared by <see cref="ScheduleMeasure"/> and
        /// <see cref="ScheduleMeasureFromDecode"/> — schedules every non-null layer's own measure graph.
        /// This type dispatches nothing per kind: each <see cref="ILayerMeshBuild"/> knows its own kind's
        /// graph. Non-local invariant: on a throw, every layer is this graph's to free from the moment
        /// <paramref name="layers"/> is handed over, including ones past the throw that were never reached
        /// — a build's own <see cref="ILayerMeshBuild.Dispose"/> is safe on an instance whose
        /// <see cref="ILayerMeshBuild.ScheduleMeasure"/> was never invoked (only the request columns it
        /// already owns from construction are freed). This is safe as ONE loop because each build's
        /// in-flight jobs read only its own owned columns plus the shared borrowed geometry/decode
        /// reference, which the caller's own catch frees strictly AFTER this loop returns.
        /// </summary>
        private static JobHandle ScheduleLayers(ILayerMeshBuild[] layers, JobHandle deps)
        {
            JobHandle combined = default;
            try
            {
                for (int d = 0; d < layers.Length; d++)
                {
                    ILayerMeshBuild build = layers[d];
                    if (build == null) continue;
                    combined = JobHandle.CombineDependencies(combined, build.ScheduleMeasure(deps));
                }
            }
            catch
            {
                for (int d = 0; d < layers.Length; d++) layers[d]?.Dispose();
                throw;
            }
            return combined;
        }

        /// <summary>Whether the currently in-flight step (measure or write) has completed. Never advances a
        /// step by itself — the caller decides when to act on it.</summary>
        internal bool IsStepComplete => _handle.IsCompleted;

        /// <summary>Completes the currently in-flight step without consuming or advancing anything —
        /// <see cref="TileManager.AwaitInFlightMeshBuilds"/>'s contract.</summary>
        internal void Complete() => _handle.Complete();

        /// <summary>Completes the measure graphs, then asks each non-null layer to schedule its own write
        /// step — <see cref="ILayerMeshBuild.TryScheduleWrite"/> reads its own kind's error flag, settles a
        /// faulted or empty layer as zero-vertex (no payload, no array — the observable half of
        /// job-scheduling-design.md's "settles a faulted layer as zero-vertex, mirroring a faulting
        /// processor"), and returns <c>false</c> for one. This type itself dispatches nothing per kind.</summary>
        /// <param name="meshDataArraysAllocated">Count of layers that produced a real array this call.</param>
        internal void CompleteMeasureAndScheduleWrite(out int meshDataArraysAllocated)
        {
            if (_writeScheduled)
                throw new InvalidOperationException("CompleteMeasureAndScheduleWrite called twice on the same TileBuildGraph.");
            _writeScheduled = true;

            _handle.Complete();
            meshDataArraysAllocated = 0;
            JobHandle combined = default;

            for (int i = 0; i < _layers.Length; i++)
            {
                ILayerMeshBuild build = _layers[i];
                if (build == null) continue;

                if (build.TryScheduleWrite(out JobHandle writeHandle))
                {
                    meshDataArraysAllocated++;
                    combined = JobHandle.CombineDependencies(combined, writeHandle);
                }
            }

            _handle = combined;
        }

        /// <summary>Completes the write jobs and takes every layer's <see cref="MeshDataPayload"/>, in dense
        /// SLOT order (one exact-sized array). Non-local invariant, idempotent: a repeat call returns the
        /// SAME array instance rather than throwing — this graph, not the caller, owns it from the first
        /// call on. A caller may resume a budget-bound partial consume across Ticks without holding its
        /// own copy, and a caller whose earlier consume attempt threw before storing anything simply calls
        /// again and gets back the one array with whatever slots that attempt already nulled — the tile
        /// recovers instead of throwing forever.</summary>
        internal MeshDataPayload[] CompleteWriteAndTakePayloads()
        {
            if (!_writeScheduled)
                throw new InvalidOperationException(
                    "CompleteWriteAndTakePayloads called before CompleteMeasureAndScheduleWrite — no layer " +
                    "has a write step to complete, so this would silently return an empty array and settle " +
                    "the tile with no mesh.");
            // Idempotent by design, NOT a throw: a caller can lose its reference to the returned array (the
            // pump takes it into a local LoadedTile copy and only writes that back AFTER ConsumeMeshBuild
            // returns, so a throw from consume discards it). Returning the cached array lets the caller
            // resume. A throw here would fail that tile on every later Tick and leak its MeshDataArrays.
            if (_payloadsTaken) return _takenPayloads;
            _payloadsTaken = true;

            _handle.Complete();

            // ONE SLOT PER LAYER, in request order — not one per produced mesh. A null slot means an
            // empty or faulted layer, and the consume loop treats it as a free advance. Compacting instead
            // would silently renumber the slots a caller joins against.
            var payloads = new MeshDataPayload[_layers.Length];
            for (int i = 0; i < _layers.Length; i++)
            {
                ILayerMeshBuild build = _layers[i];
                if (build != null) payloads[i] = build.TakePayload();
            }

            _takenPayloads = payloads;
            return payloads;
        }

        /// <summary><c>Handle.Complete()</c>, then free every owned container: each non-null layer's own
        /// build (measure output, write output and request columns, all in one <see cref="Dispose"/> call)
        /// and this graph's own owned input. Idempotent — a second call is a no-op, guarding the
        /// <see cref="DebugLiveCount"/> invariant this type's pen callers rely on. The payload sweep never
        /// re-disposes a consumed payload: the consume loop nulls each slot of the same array right after
        /// its <c>Dispose</c>.</summary>
        internal void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            _handle.Complete();

            for (int i = 0; i < _layers.Length; i++)
                _layers[i]?.Dispose();

            // The one shared per-tile geometry every layer's Input.Geometry borrowed — disposed exactly
            // once, after every layer, never per-layer (see _ownedGeometry's own doc).
            _ownedGeometry.Dispose();

            // Whatever the caller never consumed — a tile released mid-consume, or never consumed at all.
            // Null-slot-safe: the caller's own consume loop nulls a slot the instant it disposes that
            // payload, through the SAME array (CompleteWriteAndTakePayloads never copies it), so an
            // already-consumed slot is a no-op here rather than a double free.
            if (_takenPayloads != null)
            {
                for (int i = 0; i < _takenPayloads.Length; i++)
                    _takenPayloads[i]?.Dispose();
                _takenPayloads = null;
            }

            // LAST — see _decode's own doc: the layers' borrowed reads of the decoded tile, and the
            // (consumer-kind) borrowed buffer itself, are both done by this point.
            _decode?.Release();

            long after = Interlocked.Decrement(ref _liveCount);
            if (after < 0) Interlocked.Increment(ref _negativeObservations);
        }
    }
}
