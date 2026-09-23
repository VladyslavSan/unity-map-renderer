namespace MapRenderer.Tests
{
    /// <summary>
    /// The full-tile-extent ring as an MVT command stream — <b>an encoding <c>BackgroundQuad</c> does not
    /// use</b>, kept here as a test-owned oracle.
    ///
    /// <para>At extent 4096 it decodes to the closed 4-point ring
    /// <c>(0,0)→(4096,0)→(4096,4096)→(0,4096)</c>: MoveTo×1 + LineTo×3 + ClosePath, zigzag-encoded per the
    /// MVT spec. Production now holds four plain <c>double2</c> corners; this constant survives because the
    /// differential between the two is the whole behaviour-preservation argument (see
    /// <c>TileBackgroundQuadProjectionTests.SyntheticRing_MaterializesIdenticallyToTheRetiredCommandStream</c>).
    /// It is <b>not</b> production input any more, so it must never be edited to match a new implementation —
    /// it is the frozen record of what the background quad was before.</para>
    /// </summary>
    internal static class FullExtentRingCommandStream
    {
        /// <summary>The tile-local extent the stream below is authored against.</summary>
        public const double Extent = 4096.0;

        public static readonly uint[] Commands = { 9, 0, 0, 26, 8192, 0, 0, 8192, 8191, 0, 15 };
    }
}
