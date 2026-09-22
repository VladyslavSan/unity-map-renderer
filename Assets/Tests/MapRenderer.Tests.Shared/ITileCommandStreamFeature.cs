// A test-only carrier, so it lives in the test assembly. Engine-free, so core-tests compiles it too.

namespace MapRenderer.Tests
{
    /// <summary>
    /// A synthetic feature that can hand back an MVT geometry command stream, so a fixture can build a
    /// decoded-layer-shaped <c>TileGeometryBuffers</c> through the REAL producer
    /// (<c>MvtGeometryMaterializer</c>) instead of hand-rolling one.
    ///
    /// <para><b>Why this is in the TEST assembly and must stay there.</b> Production had exactly this
    /// interface once — <c>IMvtGeometryCarrier</c>, a sidecar hung off the neutral feature surface so a
    /// materializer could downcast to it. It is gone: geometry belongs to the LAYER, and a production
    /// feature carries none. Fixtures still need a way to say "this synthetic feature's shape is
    /// these rings", which is a test-authoring convenience and nothing more — it reaches no production type
    /// and is fenced by location (<c>NeutralGeometryPathTests</c> scans production assemblies only).</para>
    /// </summary>
    public interface ITileCommandStreamFeature
    {
        /// <summary>This feature's MVT command stream, or null (zero commands ⇒ no rings).</summary>
        uint[] Geometry { get; }
    }
}
