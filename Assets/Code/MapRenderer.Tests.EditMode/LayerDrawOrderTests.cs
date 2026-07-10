using System;
using NUnit.Framework;
using MapRenderer.Core.Rendering;

namespace MapRenderer.Tests
{
    /// <summary>
    /// S07 — painter's-algorithm queue-assignment tests for <see cref="LayerDrawOrder"/>.
    ///
    /// Engine-free (pure C#) so it runs in BOTH the Unity EditMode runner and the fast
    /// dotnet core-tests project. Proves the MECHANISM owns the order: queues are strictly
    /// monotonic with declared index, distinct, all inside the transparent band, and exactly
    /// base + index (pinned values — no unbounded latitude).
    /// </summary>
    [TestFixture]
    public class LayerDrawOrderTests
    {
        [Test]
        public void Queues_AreExactly_BasePlusIndex_DefaultBase()
        {
            int[] q = LayerDrawOrder.ComputeQueues(5);
            Assert.That(q.Length, Is.EqualTo(5));
            // Default base = TransparentQueue (3000). Pin every value exactly.
            Assert.That(q[0], Is.EqualTo(3000));
            Assert.That(q[1], Is.EqualTo(3001));
            Assert.That(q[2], Is.EqualTo(3002));
            Assert.That(q[3], Is.EqualTo(3003));
            Assert.That(q[4], Is.EqualTo(3004));
        }

        [Test]
        public void Queues_AreExactly_BasePlusIndex_CustomBase()
        {
            const int baseQ = 2501; // band start
            int[] q = LayerDrawOrder.ComputeQueues(4, baseQ);
            for (int i = 0; i < q.Length; i++)
                Assert.That(q[i], Is.EqualTo(baseQ + i),
                    $"queue[{i}] must equal base ({baseQ}) + index ({i}).");
        }

        [Test]
        public void Queues_AreStrictlyMonotonicIncreasing_AndDistinct()
        {
            int[] q = LayerDrawOrder.ComputeQueues(8);
            for (int i = 1; i < q.Length; i++)
                Assert.That(q[i], Is.GreaterThan(q[i - 1]),
                    $"queue[{i}] ({q[i]}) must be strictly greater than queue[{i - 1}] ({q[i - 1]}) " +
                    "so higher-index layers draw on top (painter's algorithm).");

            // Distinctness: a HashSet of all values must have full cardinality.
            Assert.That(new System.Collections.Generic.HashSet<int>(q).Count, Is.EqualTo(q.Length),
                "All queue values must be distinct — two layers sharing a queue lose strict ordering.");
        }

        [Test]
        public void AllQueues_AreInsideTheTransparentBand()
        {
            // Every painter's flat layer must be >= 2501 (transparent band) so ZWrite-off ordering
            // is not pre-empted by the opaque phase (the keystone constraint).
            int[] q = LayerDrawOrder.ComputeQueues(10);
            foreach (int v in q)
                Assert.That(v, Is.GreaterThanOrEqualTo(LayerDrawOrder.TransparentBandStart),
                    $"queue value {v} must be >= {LayerDrawOrder.TransparentBandStart} (transparent band).");
        }

        [Test]
        public void TransparentBandStart_Is2501_TransparentQueue_Is3000()
        {
            // Pin the band constants — these mirror Unity's RenderQueue values and are load-bearing.
            Assert.That(LayerDrawOrder.TransparentBandStart, Is.EqualTo(2501));
            Assert.That(LayerDrawOrder.TransparentQueue, Is.EqualTo(3000));
        }

        [Test]
        public void ZeroLayers_ReturnsEmptyArray()
        {
            int[] q = LayerDrawOrder.ComputeQueues(0);
            Assert.That(q, Is.Not.Null);
            Assert.That(q.Length, Is.EqualTo(0));
        }

        [Test]
        public void NegativeLayerCount_Throws()
        {
            Assert.Throws<ArgumentOutOfRangeException>(() => LayerDrawOrder.ComputeQueues(-1));
        }

        [Test]
        public void BaseBelowTransparentBand_Throws()
        {
            // 2500 (opaque side) must be rejected — it would let the opaque phase pre-empt ordering.
            Assert.Throws<ArgumentOutOfRangeException>(() => LayerDrawOrder.ComputeQueues(3, 2500));
            Assert.Throws<ArgumentOutOfRangeException>(() => LayerDrawOrder.ComputeQueues(3, 2000));
        }

        [Test]
        public void LayerCountExceedingQueueCeiling_Throws()
        {
            // base 3000 + 2001 layers → top layer at 5000 is OK; +2002 would hit 5001 > ceiling.
            Assert.DoesNotThrow(() => LayerDrawOrder.ComputeQueues(2001, 3000)); // top = 5000
            Assert.Throws<ArgumentOutOfRangeException>(() => LayerDrawOrder.ComputeQueues(2002, 3000)); // top = 5001
        }
    }
}
