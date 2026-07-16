using UnityEngine;
using MapRenderer.Core.Json;
using MapRenderer.Core.Mvt;
using MapRenderer.Core.Tiles;
using MapRenderer.Unity.Rendering.Meshing;
using MapRenderer.Unity.Rendering.Style;
using Fill = MapRenderer.Core.Style.Fill;

namespace MapRenderer.Unity.Rendering.Tile.Processing
{
    /// <summary>
    /// Epic A / A2: the source-less sibling of <see cref="TileMeshLayerProcessor"/> — the per-covered-tile
    /// background quad. Mirrors <see cref="TileMeshLayerProcessor"/>'s structure (retained
    /// <see cref="Mesh.MeshDataArray"/>, <c>_completedNormally</c>/<c>_vertexCount</c>/<c>_bounds</c>,
    /// infallible <see cref="Complete"/> wrapping the array as a <see cref="MeshDataPayload"/> — zero-vertex
    /// on fault), but takes no <see cref="ITileMeshRenderLayer"/> — the tile passed to
    /// <see cref="ProcessOnWorker"/> is always <c>null</c> and ignored (design §B Q2): this processor
    /// SYNTHESIZES a full-tile-extent polygon feature (an <see cref="InMemoryTileFeature"/>, A6) rather than
    /// selecting one from a decoded tile layer.
    ///
    /// <para>Reuses <see cref="StyledFillTileBuilder.WriteMeshData"/> (design §B "Geometry, winding,
    /// material, draw order") rather than hand-building a quad, so the background quad gets earcut's
    /// cull-correct winding and the fill path's globe subdivision for free — dodging the <c>_Cull</c>
    /// winding bug that bit E3's hand-wound world-quad.</para>
    /// </summary>
    internal sealed class TileBackgroundLayerProcessor : ITileMeshLayerProcessor
    {
        // The synthetic full-tile-extent ring, EXTENT = 4096 tile units: a closed 4-point ring
        // (0,0)→(4096,0)→(4096,4096)→(0,4096), MoveTo×1 + LineTo×3 + ClosePath, zigzag-encoded per the MVT
        // spec (MvtGeometry.cs). Hoisted static — no per-tile allocation. Decoded corners are asserted by
        // TileBackgroundQuadProjectionTests.SyntheticRing_DecodesToFourTileCorners (§F tooth 8).
        internal const double Extent = 4096.0;

        // internal (not private): TileBackgroundQuadProjectionTests.SyntheticRing_DecodesToFourTileCorners
        // decodes this EXACT array (no test-owned duplicate) — a mis-encoded zigzag/command integer must
        // fail there rather than producing a subtly-wrong background downstream (§F tooth 8).
        internal static readonly uint[] FullExtentRingGeometry =
            { 9, 0, 0, 26, 8192, 0, 0, 8192, 8191, 0, 15 };

        private static readonly InMemoryTileFeature FullExtentRingFeature = new InMemoryTileFeature
        {
            GeometryType = TileGeometryType.Polygon,
            Geometry     = FullExtentRingGeometry,
        };

        private static readonly ITileFeature[] FullExtentRingFeatures = { FullExtentRingFeature };

        // Constant white — vertex colour is white by construction; the background colour comes from the
        // material uniform (_BaseColor/_Opacity), bound by MaterialFactory.BindBackgroundPaintToApplier
        // over the fill-base clone (design §B). Clean-room: public Style Spec, no MapLibre source.
        private static readonly Fill.PaintProperties WhitePaint =
            new Fill.PaintProperties(JsonParser.Parse("{\"fill-color\":\"#ffffff\"}"));

        private readonly int                _materialIndex;
        private readonly string             _payloadName;
        private readonly Mesh.MeshDataArray _mda;

        // Committed by ProcessOnWorker ONLY after WriteMeshData returns successfully — mirrors
        // TileMeshLayerProcessor's fault contract exactly (fault ⇒ zero-vertex on Complete()).
        private bool   _completedNormally;
        private int    _vertexCount;
        private Bounds _bounds;

        private TileBackgroundLayerProcessor(int materialIndex, string payloadName, Mesh.MeshDataArray mda)
        {
            _materialIndex = materialIndex;
            _payloadName   = payloadName;
            _mda           = mda;
        }

        /// <summary>Main-thread-only allocation factory, called from <c>TileManager.KickSourcelessBackground</c>'s
        /// <c>PmMeshDataAllocate</c> block — mirrors <see cref="TileMeshLayerProcessor.AllocateForKick"/>.</summary>
        internal static TileBackgroundLayerProcessor AllocateForKick(BackgroundRenderLayer layer, int materialIndex)
        {
            Mesh.MeshDataArray mda = MeshDataPayload.AllocateTracked(1);
            string name = layer?.StyleLayer?.Id ?? "background";
            return new TileBackgroundLayerProcessor(materialIndex, name, mda);
        }

        public LayerPhase Phase => LayerPhase.WorkerOnly;

        /// <summary><paramref name="tile"/> is always <c>null</c> (source-less — see
        /// <see cref="TileLayerProcessorRunner.RunSourcelessWorkerPass"/>) and ignored: the geometry is the
        /// hoisted synthetic full-extent ring, not anything selected from a decoded tile.</summary>
        public void ProcessOnWorker(IDecodedTile tile, in TileLayerProcessContext context)
        {
            // If WriteMeshData throws partway through, control never reaches the two lines below — the
            // processor completes as zero-vertex (Complete()'s _completedNormally == false branch), matching
            // TileMeshLayerProcessor's fallback rather than uploading partially-written data.
            StyledFillTileBuilder.WriteMeshData(
                _mda[0], FullExtentRingFeatures, WhitePaint, context.Zoom, Extent, context.Tile,
                context.TileOriginRender, out int verts, out Bounds bounds, context.Projection);
            _vertexCount = verts;
            _bounds      = bounds;

            // Reached only if the above didn't throw.
            _completedNormally = true;
        }

        /// <summary>Infallible: only wraps the already-allocated <see cref="Mesh.MeshDataArray"/> into a
        /// <see cref="MeshDataPayload"/> (zero-vertex on the fault path).</summary>
        public IRenderLayerPayload Complete()
        {
            if (_completedNormally)
                return new MeshDataPayload(_mda, _vertexCount, _bounds, _payloadName, _materialIndex);

            // A throwing WriteMeshData, or this processor was never reached because an earlier processor in
            // the same dense pass threw — either way, the kick-allocated array must still be wrapped and
            // disposed via consume/discard (no native leak).
            return new MeshDataPayload(_mda, 0, default, _payloadName, _materialIndex);
        }
    }
}
