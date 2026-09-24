namespace MapRenderer.Tests
{
    /// <summary>
    /// The full-tile-extent ring as an MVT command stream, a test-owned oracle; <c>BackgroundQuad</c> holds
    /// four <c>double2</c> corners instead. At extent 4096 it decodes to the closed ring
    /// <c>(0,0)→(4096,0)→(4096,4096)→(0,4096)</c> (MoveTo×1 + LineTo×3 + ClosePath, MVT zigzag encoding).
    /// It is a frozen reference for <c>TileBackgroundQuadProjectionTests</c>' differential, so never edit it
    /// to match a new implementation.
    /// </summary>
    internal static class FullExtentRingCommandStream
    {
        /// <summary>The tile-local extent the stream below is authored against.</summary>
        public const double Extent = 4096.0;

        public static readonly uint[] Commands = { 9, 0, 0, 26, 8192, 0, 0, 8192, 8191, 0, 15 };
    }
}
