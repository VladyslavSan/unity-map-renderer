using System;
using Unity.Collections;
using Unity.Mathematics;
using MapRenderer.Core.Geo;
using MapRenderer.Core.Tiles;

namespace MapRenderer.Jobs
{
    /// <summary>
    /// Waist 1 of the tile-geometry IR: one tile's decoded rings, as blittable native buffers the Burst
    /// stages consume (<c>MvtDecodeJob</c> → <c>RingClipJob</c> → <c>RingAssemblyJob</c> → <c>EarcutJob</c>).
    /// Extracted from <see cref="FillMeshPipeline.Schedule"/>'s private locals.
    ///
    /// <para><b>THE NAMED FENCE — coordinate space (producer declaration).</b> <see cref="Vertices"/> is
    /// <b>tile-local <c>double2</c> in <c>[0, Extent]</c>, always</b>: origin top-left, <b>Y-down</b>.
    /// It is <b>never geodetic and never projected</b>. This type is deliberately
    /// <i>monomorphic</i> so that "just feed earcut/ring-assembly the geodetic version" is a <b>compile-time
    /// impossibility</b> rather than a rule someone has to remember: there is no coordinate-space tag, no
    /// geodetic overload, and this buffer is never reused to hold geo coordinates. Waist 2 —
    /// <c>TileToGeoJob</c> → <c>ProjectPointsJob&lt;TProj&gt;</c> — keeps its own
    /// <c>NativeArray&lt;GeoCoordinate&gt;</c> and stays exactly where it is.</para>
    ///
    /// <para><b>Winding is NOT part of this contract — deliberately.</b> A producer may fill
    /// <see cref="Vertices"/> with rings of either orientation. The pipeline is winding-agnostic by
    /// construction: <c>RingAssemblyJob</c> derives the exterior sign from the data <i>per feature</i> and
    /// classifies holes as the opposite sign, so nothing downstream requires or checks a fixed input winding,
    /// and the decoder performs no rewind. (The "canonical CCW" rule in
    /// <c>docs/coordinates-and-projections.md</c> §7.1 governs <i>tessellator triangle output</i> — earcut,
    /// line ribbon, globe subdivide — not decoded input rings; do not apply it here.) What a producer owes
    /// this buffer is only that a feature's own rings are <b>mutually consistent</b>: exteriors one sign,
    /// their holes the other.</para>
    ///
    /// <para><see cref="Tile"/> and <see cref="Extent"/> are <b>provenance metadata</b> — they say which tile
    /// and which quantization range these tile-local coordinates belong to, so a caller holding the buffer
    /// cannot silently pair it with the wrong <c>(TileId, extent)</c>. They are <b>not</b> a space
    /// discriminator; carrying them does not make the buffer able to represent anything but tile-local data.</para>
    ///
    /// <para><b>Two backing modes, one owner.</b> The three public array fields are the only read surface, but
    /// what backs them depends on how the buffer was minted:
    /// <list type="bullet">
    /// <item><b>Array-backed</b> (<see cref="Allocate"/>) — the struct owns three <see cref="NativeArray{T}"/>s
    /// and <see cref="Dispose"/> frees them.</item>
    /// <item><b>List-backed</b> (<see cref="AdoptDerivedLists"/>) — the struct owns three
    /// <see cref="NativeList{T}"/>s and the public fields are <c>AsArray()</c> <b>views</b> over them.
    /// <b>Disposing a view is invalid</b>, so <see cref="Dispose"/> frees the backing <i>lists</i> and never
    /// touches the views. That discriminator is the whole reason the two modes are distinguished.</item>
    /// </list>
    /// <see cref="FeatureGeometryType"/> is an <b>owned array in both modes</b> (never a view). A derived
    /// buffer gets its own <b>copy</b> of the column rather than the source's array — ring kind is not
    /// recoverable from coordinates, so it cannot be re-derived, and copying is what lets the source stay
    /// intact and keep owning everything it owns.</para>
    ///
    /// <para><b>Two-tier ownership (IR B7).</b> There is exactly one owner at any moment, but there are now
    /// two kinds of owner:
    /// <list type="bullet">
    /// <item>An <see cref="ITileGeometryMaterializer"/> still <b>transfers</b> the buffer it mints — to the
    /// decoded <c>ITileLayer</c>, which owns it for the life of the decoded tile (IR C1 P3; B7's pass-scoped
    /// <c>TileGeometryStore</c> is retired).</item>
    /// <item>The layer <b>lends</b>: every consumer <b>borrows</b> the buffer it reads off
    /// <c>ITileLayer.Geometry</c>. A borrower must never dispose it, never retain it past the decode scope,
    /// and never mutate it.</item>
    /// <item>The only buffer a consumer owns is one it <b>derived</b> for itself via
    /// <see cref="AdoptDerivedLists"/> (fill's per-layer visited-ring buffer) or minted itself because it
    /// <i>is</i> the producer (the background quad).</item>
    /// </list>
    /// <b>How getting it wrong fails depends on the backing mode</b> — do not read the two as one rule:
    /// an <b>array-backed</b> buffer (<see cref="Allocate"/>, i.e. every buffer a consumer borrows) frees
    /// three real <see cref="NativeArray{T}"/>s, so a second owner is a <b>loud double free</b> (measured in
    /// IR C1 P2: 32 failures across 19 fixtures, the heap-corruption signature). A <b>list-backed</b> buffer's
    /// public fields are <c>AsArray()</c> views, and disposing a view is a <b>silent no-op</b> under
    /// Collections 6.5.0 — that mistake leaks quietly with every test still green. Only fill's derived buffer
    /// is list-backed, and it is never lent.</para>
    ///
    /// <para><b>Lifetime.</b> <see cref="Allocator.Persistent"/>, single-owner, minted and disposed inside the
    /// same call — worker-thread decode <i>scratch</i>. This is a different disposal domain from the
    /// main-thread <c>MeshDataArray</c> / <c>ApplyAndDisposeWritableMeshData</c> GPU-output boundary that
    /// <see cref="TileMeshBuffers"/> feeds; the two never interact. Never dispose while a job referencing
    /// these buffers is in flight.</para>
    /// </summary>
    public struct TileGeometryBuffers : IDisposable
    {
        /// <summary>Provenance: the slippy-map address (z/x/y) whose tile-local space
        /// <see cref="Vertices"/> lives in.</summary>
        public TileId Tile;

        /// <summary>Provenance: the quantization range of <see cref="Vertices"/> (MVT extent, typically 4096).</summary>
        public double Extent;

        /// <summary>Flat ring vertices in tile-local coordinates — see THE NAMED FENCE on the type:
        /// <c>[0, Extent]</c>, Y-down, never geodetic.</summary>
        public NativeArray<double2> Vertices;

        /// <summary>Per-ring start offset into <see cref="Vertices"/>; length <see cref="RingCount"/> + 1
        /// (trailing sentinel), so ring <c>r</c> spans <c>[RingOffsets[r], RingOffsets[r + 1])</c>.</summary>
        public NativeArray<int> RingOffsets;

        /// <summary>Per-ring index into the caller's feature list, in <b>decode order and unrenumbered</b>
        /// (length <see cref="RingCount"/>). Consumers join back to per-feature data through this index, so it
        /// must never be compacted, sorted, deduped or re-based — the clip stage carries the original feature
        /// indices through for exactly this reason.</summary>
        public NativeArray<int> RingFeatureIdx;

        /// <summary>Per-FEATURE geometry kind, length <see cref="FeatureCount"/> — ring <c>r</c>'s kind is
        /// <c>FeatureGeometryType[RingFeatureIdx[r]]</c>. Per-feature rather than per-ring because no producer
        /// emits a mixed-kind feature (MVT <c>Feature.type</c> is singular; the GeoJSON slicer maps one source
        /// feature to one kind), so this column plus <see cref="RingFeatureIdx"/> fully determines ring kind.
        /// <para><b>Why it exists at all.</b> Ring kind cannot be recovered from the coordinates: a LineString
        /// ring and a polygon ring are the same shape of data, and an area-based classifier would treat the
        /// LineString as a spurious exterior or hole — silent triangulation corruption, not a crash. It is
        /// also blittable, so a Burst consumer needs no managed <c>IFeature</c>.</para>
        /// <para><b>Cleared on allocation, deliberately</b>: an unfilled element reads
        /// <see cref="TileGeometryType.Unknown"/>, which every consumer's kind gate rejects. A producer that
        /// forgets to fill this renders NOTHING (loud) rather than something WRONG (silent).</para></summary>
        public NativeArray<TileGeometryType> FeatureGeometryType;

        /// <summary>Number of rings actually produced — <b>the count the producing job reported</b>, not
        /// <c>RingOffsets.Length - 1</c>.</summary>
        /// <remarks>Deriving it from the buffer length would be wrong in the array-backed mode, where the
        /// buffers are <i>capacity</i>-sized from <see cref="FillMeshPipeline.PrecountRingsAndVertices"/>: the
        /// never-fired <see cref="FillMeshPipeline.EnsureCapacity"/> backstop compares the reported count
        /// against that capacity, so a derived count would make the comparison tautological and silently
        /// disarm the sizing-vs-decode desync it exists to catch.</remarks>
        public int RingCount;

        /// <summary>Number of vertices actually written into <see cref="Vertices"/> — the producing job's
        /// reported count, stored rather than derived, for the same reason as <see cref="RingCount"/>.</summary>
        public int VertexCount;

        /// <summary>True between minting and <see cref="Dispose"/>.</summary>
        public bool IsCreated;

        /// <summary>The producer's sized upper bound on rings — <b>capacity, never a count</b>; read
        /// <see cref="RingCount"/> for how many rings exist. Array-backed: the <c>maxRings</c>
        /// <see cref="Allocate"/> was given. List-backed: equal to <see cref="RingCount"/>, because a
        /// length-authoritative stage sizes exactly. Derived from the buffer, so it can never desync from
        /// it.</summary>
        public int RingCapacity => RingFeatureIdx.IsCreated ? RingFeatureIdx.Length : 0;

        /// <summary>Number of features this buffer's <see cref="RingFeatureIdx"/> values index into. Derived
        /// from the column, so it can never desync from it.</summary>
        public int FeatureCount => FeatureGeometryType.IsCreated ? FeatureGeometryType.Length : 0;

        // List-backed mode only: the buffers the public views are windows onto. _ownsBackingLists is the
        // discriminator Dispose() reads to decide whether to free the lists or the arrays.
        private NativeList<double2> _backingVertices;
        private NativeList<int>     _backingRingOffsets;
        private NativeList<int>     _backingRingFeatureIdx;
        private bool                _ownsBackingLists;

        /// <summary>Mints an <b>array-backed</b> buffer sized for a decode pass: <paramref name="maxVertices"/>
        /// vertices, <paramref name="maxRings"/> + 1 ring offsets (sentinel), <paramref name="maxRings"/>
        /// ring→feature entries and <paramref name="featureCount"/> per-feature kind slots.
        /// <see cref="RingCount"/>/<see cref="VertexCount"/> start at 0 — the producing job reports them.
        /// <para><paramref name="featureCount"/> sits before the two capacities so a positional mistake is a
        /// compile error at every call site rather than a silently swapped <c>int</c>.</para></summary>
        public static TileGeometryBuffers Allocate(TileId tile, double extent, int featureCount, int maxRings, int maxVertices)
        {
            return new TileGeometryBuffers
            {
                Tile           = tile,
                Extent         = extent,
                Vertices       = new NativeArray<double2>(maxVertices, Allocator.Persistent, NativeArrayOptions.UninitializedMemory),
                RingOffsets    = new NativeArray<int>(maxRings + 1,    Allocator.Persistent, NativeArrayOptions.UninitializedMemory),
                RingFeatureIdx = new NativeArray<int>(maxRings,        Allocator.Persistent, NativeArrayOptions.UninitializedMemory),
                // ClearMemory, unlike the three above: an unfilled kind must read Unknown (fail-closed), not
                // whatever the allocator handed back. See the field doc.
                FeatureGeometryType = new NativeArray<TileGeometryType>(featureCount, Allocator.Persistent, NativeArrayOptions.ClearMemory),
                RingCount      = 0,
                VertexCount    = 0,
                IsCreated      = true,
            };
        }

        /// <summary>Mints a <b>list-backed</b> buffer that takes <b>ownership</b> of the three lists a
        /// length-authoritative stage (fill's visited-ring derive, clipping or not) produced, exposing them
        /// as <c>AsArray()</c> views. The caller must not dispose the lists afterwards —
        /// <see cref="Dispose"/> does, and disposing the views instead would be invalid.
        /// <para><paramref name="featureGeometryTypeToCopy"/> is <b>copied</b>, not taken: the derive reads a
        /// buffer it only <b>borrows</b> (IR B7's two-tier contract above), so the source must be left
        /// completely intact and keep owning its own column. Copying <c>FeatureCount</c> enum values per fill
        /// layer is negligible next to the decode it replaces, and it keeps <see cref="Dispose"/>'s two-mode
        /// discriminator — the one thing an ownership tooth can actually observe — unchanged in shape.</para>
        /// <para>The derive renumbers no feature index (it selects and reorders whole rings, carrying each
        /// ring's <c>RingFeatureIdx</c> with it), so the copied column is still valid as-is.</para></summary>
        public static TileGeometryBuffers AdoptDerivedLists(
            TileId tile, double extent, NativeArray<TileGeometryType> featureGeometryTypeToCopy,
            NativeList<double2> vertices, NativeList<int> ringOffsets, NativeList<int> ringFeatureIdx)
        {
            int kindCount = featureGeometryTypeToCopy.IsCreated ? featureGeometryTypeToCopy.Length : 0;
            var kinds = new NativeArray<TileGeometryType>(
                kindCount, Allocator.Persistent, NativeArrayOptions.ClearMemory);
            if (kindCount > 0) kinds.CopyFrom(featureGeometryTypeToCopy);

            return new TileGeometryBuffers
            {
                Tile                   = tile,
                Extent                 = extent,
                FeatureGeometryType    = kinds,
                Vertices               = vertices.AsArray(),
                RingOffsets            = ringOffsets.AsArray(),
                RingFeatureIdx         = ringFeatureIdx.AsArray(),
                RingCount              = ringOffsets.Length - 1,  // trailing sentinel
                VertexCount            = vertices.Length,
                IsCreated              = true,
                _backingVertices       = vertices,
                _backingRingOffsets    = ringOffsets,
                _backingRingFeatureIdx = ringFeatureIdx,
                _ownsBackingLists      = true,
            };
        }

        /// <summary>Frees whatever this buffer actually owns — the backing <b>lists</b> in list-backed mode
        /// (never the <c>AsArray()</c> views over them), the three arrays otherwise. Idempotent.</summary>
        public void Dispose()
        {
            if (!IsCreated) return;
            IsCreated = false;

            // Owned outright in BOTH modes (never an AsArray() view): a derived buffer holds its own COPY of
            // the column, so freeing it here can never reach the buffer it was derived from.
            if (FeatureGeometryType.IsCreated) FeatureGeometryType.Dispose();

            if (_ownsBackingLists)
            {
                _backingVertices.Dispose();
                _backingRingOffsets.Dispose();
                _backingRingFeatureIdx.Dispose();
            }
            else
            {
                Vertices.Dispose();
                RingOffsets.Dispose();
                RingFeatureIdx.Dispose();
            }
        }
    }
}
