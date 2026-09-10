// The guard GlobeFillVertexKey's doc names: the key is enumerated FIELD BY FIELD, so a column added to
// GlobeFillVertex is NOT picked up automatically. Nothing would fail to compile — two vertices differing
// only in the new column would simply merge, silently. This is the tooth that makes that loud instead.
//
// It already caught one: the band branch added `Band` and the key had to be extended by hand.
using NUnit.Framework;
using Unity.Collections.LowLevel.Unsafe;
using MapRenderer.Jobs.Fill;

namespace MapRenderer.Tests.Structure
{
    public sealed class GlobeFillVertexKeySizeTests
    {
        /// <summary>The bytes GlobeFillVertexKey covers, field by field: World+Up+East (3 x double3),
        /// Tile (double2), Band (float3), Feature (int).</summary>
        private const int CoveredBytes = 3 * 24 + 16 + 12 + 4;

        [Test]
        public void EveryFieldOfTheVertexParticipatesInItsKey()
        {
            Assert.AreEqual(CoveredBytes, UnsafeUtility.SizeOf<GlobeFillVertex>(),
                "GlobeFillVertex's size no longer matches the fields GlobeFillVertexKey hashes. A column was " +
                "added or removed. The key is hand-enumerated, so it does NOT pick that up: extend " +
                "GlobeFillVertexKey's ctor, Equals and GetHashCode with the new column and update CoveredBytes. " +
                "Leaving it means two vertices differing ONLY in the new column merge into one — for the band " +
                "attribute that collapses the antialiasing skirt, with nothing failing to compile.");
        }
    }
}
