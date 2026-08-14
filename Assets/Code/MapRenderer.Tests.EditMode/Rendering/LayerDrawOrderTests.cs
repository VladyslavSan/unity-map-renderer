using System;
using System.Linq;
using NUnit.Framework;
using MapRenderer.Core.Rendering;

namespace MapRenderer.Tests.Rendering
{
    /// <summary>
    /// S07 — painter's-algorithm queue-assignment tests for <see cref="LayerDrawOrder"/>.
    ///
    /// Engine-free (pure C#) so it runs in BOTH the Unity EditMode runner and the fast
    /// dotnet core-tests project. Proves the MECHANISM owns the order: queues are strictly
    /// monotonic with declared index, distinct, all inside the transparent band.
    ///
    /// <para><b>Stage 2 (G7/D7, road-shields):</b> <see cref="LayerDrawOrder"/> grew a sub-slot BAND per
    /// declared layer (a symbol layer's icon = <see cref="LayerSubSlot.Base"/>, text =
    /// <see cref="LayerSubSlot.Above"/>, so the badge never paints over its own number).
    /// <c>Queues_AreExactly_BasePlusIndex_{DefaultBase,CustomBase}</c> pinned exact values under the OLD
    /// stride-1 formula (<c>3000,3001,3002…</c>) — they are REPLACED here by
    /// <see cref="BandStart_AndUniformStride_AreCorrect"/> (N5), which reads the stride off the named
    /// <see cref="LayerDrawOrder.SubSlotsPerLayer"/> constant rather than a literal. Restating the old pair
    /// at the new stride-2 values would be a pure re-bake with no added falsifying power; N5 additionally
    /// catches a NON-UNIFORM stride, which an exact-value list cannot distinguish from a deliberate change.
    /// N1/N2/N3/N4/N6 are new, pinning the sub-slot band itself.</para>
    /// </summary>
    [TestFixture]
    public class LayerDrawOrderTests
    {
        // N5 — replaces Queues_AreExactly_BasePlusIndex_{DefaultBase,CustomBase} (see class doc above).
        [Test]
        public void BandStart_AndUniformStride_AreCorrect()
        {
            AssertBandStartAndStride(LayerDrawOrder.TransparentQueue); // default base
            AssertBandStartAndStride(2501);                            // custom base — the band start

            static void AssertBandStartAndStride(int baseQueue)
            {
                int[] q = LayerDrawOrder.ComputeQueues(6, baseQueue);
                Assert.That(q[0], Is.EqualTo(baseQueue),
                    $"the first layer's Base sub-slot must equal baseQueue ({baseQueue}) exactly.");
                for (int i = 1; i < q.Length; i++)
                    Assert.That(q[i] - q[i - 1], Is.EqualTo(LayerDrawOrder.SubSlotsPerLayer),
                        $"stride between layer {i - 1} and layer {i} must equal SubSlotsPerLayer " +
                        $"({LayerDrawOrder.SubSlotsPerLayer}) uniformly — a non-uniform stride would let two " +
                        "layers' bands overlap or leave a gap that silently swallows a sub-slot.");
            }
        }

        // N1
        [Test]
        public void IconSubSlot_IsStrictlyBelow_ItsOwnLayersTextSubSlot()
        {
            for (int i = 0; i <= 7; i++)
                Assert.That(
                    LayerDrawOrder.QueueFor(i, LayerSubSlot.Base),
                    Is.LessThan(LayerDrawOrder.QueueFor(i, LayerSubSlot.Above)),
                    $"layer {i}'s Base (icon) sub-slot must be strictly below its own Above (text) sub-slot " +
                    "(G7/D7) — otherwise the badge can paint over the number it frames.");
        }

        // N2 — the tooth that kills the naive "text = queue + 1" fix (SubSlotsPerLayer left at 1).
        [Test]
        public void LayerBands_AreDisjoint_AndOrdered()
        {
            for (int i = 0; i <= 7; i++)
            {
                int bandBase = LayerDrawOrder.QueueFor(i, LayerSubSlot.Base);
                int bandTop  = bandBase + LayerDrawOrder.SubSlotsPerLayer - 1;

                Assert.That(
                    LayerDrawOrder.QueueFor(i, LayerSubSlot.Above),
                    Is.LessThan(LayerDrawOrder.QueueFor(i + 1, LayerSubSlot.Base)),
                    $"layer {i}'s Above sub-slot must be strictly below layer {i + 1}'s Base sub-slot — " +
                    "bands must not overlap.");

                foreach (LayerSubSlot subSlot in
                         Enum.GetValues(typeof(LayerSubSlot)))
                {
                    int value = LayerDrawOrder.QueueFor(i, subSlot);
                    Assert.That(value, Is.InRange(bandBase, bandTop),
                        $"layer {i}'s {subSlot} sub-slot ({value}) must lie inside its own band [{bandBase}, {bandTop}].");
                }
            }
        }

        // N3 — no drift between the batch form and the single-sub-slot form.
        [Test]
        public void ComputeQueues_AgreesWith_QueueFor_BaseSubSlot()
        {
            int[] q = LayerDrawOrder.ComputeQueues(9);
            for (int i = 0; i < q.Length; i++)
                Assert.That(q[i], Is.EqualTo(LayerDrawOrder.QueueFor(i, LayerSubSlot.Base)),
                    $"ComputeQueues(n)[{i}] must agree with QueueFor({i}, Base) — one formula home, no drift " +
                    "between the batch and single forms.");
        }

        // N4
        [Test]
        public void SubSlotsPerLayer_MatchesTheEnum()
        {
            var values = Enum.GetValues(typeof(LayerSubSlot)).Cast<int>().OrderBy(v => v).ToArray();
            Assert.That(LayerDrawOrder.SubSlotsPerLayer, Is.EqualTo(values.Length),
                "SubSlotsPerLayer must equal the LayerSubSlot value count — a future sub-slot added without " +
                "bumping this constant would silently under-reserve the stride.");
            for (int i = 0; i < values.Length; i++)
                Assert.That(values[i], Is.EqualTo(i), "LayerSubSlot values must be contiguous 0..S-1 — they are queue offsets.");
        }

        // N6 — the constant pinned exactly once, with its reason, so a stride change is never silently
        // absorbed by a re-baked value list elsewhere.
        [Test]
        public void SubSlotsPerLayer_Is2_IconThenText()
        {
            Assert.That(LayerDrawOrder.SubSlotsPerLayer, Is.EqualTo(2),
                "exactly two sub-slots per layer today: icon (Base) then text (Above) for a symbol layer; " +
                "every other kind uses Base only. This is the ONE place the stride is pinned as a literal.");
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

        // N7 — the ceiling must cover the BAND'S TOP (the last layer's Above sub-slot), not just its Base.
        // Verified RED against TODAY'S code with NO injection at all (the pre-D7 stride-1 formula accepts
        // 1001 layers here) — see the dev report for the recorded failure text.
        [Test]
        public void LayerCountExceedingQueueCeiling_Throws()
        {
            // base 3000 + 1000 layers: the LAST layer's Above sub-slot is 3000 + 999*2 + 1 = 4999 (OK).
            int[] q = LayerDrawOrder.ComputeQueues(1000, 3000);
            Assert.That(
                LayerDrawOrder.QueueFor(999, LayerSubSlot.Above),
                Is.EqualTo(4999),
                "the 1000th layer's (index 999) Above sub-slot must land exactly on the derived boundary value.");
            Assert.That(q[999], Is.EqualTo(4998), "and its own Base sub-slot sits one below that.");

            // +1 more layer: the new last layer's Above sub-slot would need 3000 + 1000*2 + 1 = 5001 > ceiling.
            Assert.Throws<ArgumentOutOfRangeException>(() => LayerDrawOrder.ComputeQueues(1001, 3000));
        }

        [Test]
        public void QueueFor_NegativeDrawIndex_Throws()
        {
            Assert.Throws<ArgumentOutOfRangeException>(() => LayerDrawOrder.QueueFor(-1));
        }

        [Test]
        public void QueueFor_AboveSubSlotExceedingCeiling_Throws()
        {
            // drawIndex 1000's Above sub-slot is 5001 — over the ceiling — even though its own Base
            // sub-slot (5000) does not throw. The two must be checked independently.
            Assert.DoesNotThrow(() => LayerDrawOrder.QueueFor(1000, LayerSubSlot.Base));
            Assert.Throws<ArgumentOutOfRangeException>(
                () => LayerDrawOrder.QueueFor(1000, LayerSubSlot.Above));
        }
    }
}
