using System;
using System.Linq;
using NUnit.Framework;
using MapRenderer.Core.Rendering;

namespace MapRenderer.Tests.Rendering
{
    /// <summary>
    /// Painter's-algorithm queues for <see cref="LayerDrawOrder"/> (engine-free, also in core-tests):
    /// strictly monotonic with declared index, distinct, and inside the transparent band. Each layer
    /// gets a sub-slot BAND (icon = <see cref="LayerSubSlot.Base"/>, text = <see cref="LayerSubSlot.Above"/>).
    /// The stride is read off <see cref="LayerDrawOrder.SubSlotsPerLayer"/>, so a non-uniform one fails.
    /// </summary>
    [TestFixture]
    public class LayerDrawOrderTests
    {
        // N1
        [Test]
        public void IconSubSlot_IsStrictlyBelow_ItsOwnLayersTextSubSlot()
        {
            for (int i = 0; i <= 7; i++)
                Assert.That(
                    LayerDrawOrder.QueueFor(i, LayerSubSlot.Base),
                    Is.LessThan(LayerDrawOrder.QueueFor(i, LayerSubSlot.Above)),
                    $"layer {i}'s Base (icon) sub-slot must be strictly below its own Above (text) sub-slot " +
                    "— otherwise the badge can paint over the number it frames.");
        }

        // N2 — the tooth that kills the naive "text = queue + 1" fix (SubSlotsPerLayer left at 1).
        [Test]
        public void LayerBands_AreDisjoint_AndOrdered()
        {
            int previousBase = LayerDrawOrder.QueueFor(0, LayerSubSlot.Base);
            Assert.That(previousBase, Is.EqualTo(LayerDrawOrder.TransparentQueue),
                "layer 0's Base sub-slot must anchor exactly at TransparentQueue.");

            for (int i = 0; i <= 7; i++)
            {
                int bandBase = LayerDrawOrder.QueueFor(i, LayerSubSlot.Base);
                int bandTop  = bandBase + LayerDrawOrder.SubSlotsPerLayer - 1;

                if (i > 0)
                    Assert.That(bandBase - previousBase, Is.EqualTo(LayerDrawOrder.SubSlotsPerLayer),
                        $"stride between layer {i - 1} and layer {i}'s Base sub-slot must equal " +
                        $"SubSlotsPerLayer ({LayerDrawOrder.SubSlotsPerLayer}) uniformly — a non-uniform " +
                        "stride would let two layers' bands overlap or leave a gap that silently swallows " +
                        "a sub-slot.");
                previousBase = bandBase;

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
        public void TransparentBandStart_Is2501_TransparentQueue_Is3000()
        {
            // Pin the band constants — these mirror Unity's RenderQueue values and are load-bearing.
            Assert.That(LayerDrawOrder.TransparentBandStart, Is.EqualTo(2501));
            Assert.That(LayerDrawOrder.TransparentQueue, Is.EqualTo(3000));
        }

        [Test]
        public void QueueFor_NegativeDrawIndex_Throws()
        {
            Assert.Throws<ArgumentOutOfRangeException>(() => LayerDrawOrder.QueueFor(-1));
        }

        // N7 — the ceiling must cover the BAND'S TOP (the last layer's Above sub-slot), not just its Base.
        // Verified RED against a stride-1 formula, which would accept drawIndex 1000 here.
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
