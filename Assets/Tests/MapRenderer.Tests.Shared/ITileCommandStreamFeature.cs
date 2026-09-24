// A test-only carrier, so it lives in the test assembly. Engine-free, so core-tests compiles it too.

namespace MapRenderer.Tests
{
    /// <summary>
    /// A synthetic feature that hands back an MVT geometry command stream, so a fixture builds a
    /// decoded-layer-shaped <c>TileGeometryBuffers</c> through the real <c>MvtGeometryMaterializer</c>.
    /// Non-obvious why: it stays in the TEST assembly because geometry belongs to the LAYER, and a
    /// production feature carries none. <c>NeutralGeometryPathTests</c> scans production assemblies only,
    /// so the location is the fence.
    /// </summary>
    public interface ITileCommandStreamFeature
    {
        /// <summary>This feature's MVT command stream, or null (zero commands ⇒ no rings).</summary>
        uint[] Geometry { get; }
    }
}
