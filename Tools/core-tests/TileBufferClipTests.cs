// T0 — the tile-buffer clip knob's UNIT conversion. Engine-free, so it runs in the fast loop
// (`dotnet test Tools/core-tests`); the clipping arithmetic itself is Burst and lives in RingClipJobTests.

using NUnit.Framework;
using MapRenderer.Core.Tiles;

namespace MapRenderer.Tests
{
    [TestFixture]
    public class TileBufferClipTests
    {
        // The discriminating value is extent 512, NOT 4096: at the reference extent a raw-tile-unit knob and
        // the scaled one agree, so a 4096 test cannot tell them apart. At 512 the unscaled knob is 8× wrong.
        [Test]
        public void KeepTileUnits_ScalesTheMarginToTheLayerExtent()
        {
            Assert.IsTrue(TileBufferClip.KeepTileUnits(64.0).TryWindow(512.0, out var min, out var max));

            Assert.AreEqual(-8.0, min.x, 1e-12, "64 units at extent 4096 is 8 units at extent 512 " +
                                                "(-64 here means the extent/ReferenceExtent scaling was dropped).");
            Assert.AreEqual(-8.0, min.y, 1e-12);
            Assert.AreEqual(520.0, max.x, 1e-12);
            Assert.AreEqual(520.0, max.y, 1e-12);
        }

        [Test]
        public void KeepTileUnits_AtTheReferenceExtent_IsTheAuthoredMargin()
        {
            Assert.IsTrue(TileBufferClip.KeepTileUnits(64.0).TryWindow(4096.0, out var min, out var max));
            Assert.AreEqual(-64.0, min.x, 1e-12);
            Assert.AreEqual(4160.0, max.x, 1e-12);
        }

        [Test]
        public void Disabled_HasNoWindow()
        {
            Assert.IsFalse(TileBufferClip.Disabled.IsEnabled);
            Assert.IsFalse(TileBufferClip.Disabled.TryWindow(4096.0, out _, out _),
                "Disabled must report no window — that is what makes `default` mean 'do not clip'.");
            Assert.IsFalse(default(TileBufferClip).TryWindow(4096.0, out _, out _),
                "default(TileBufferClip) must be Disabled: every unset knob on the wiring path relies on it.");
        }

        [Test]
        public void KeepTileUnits_ClampsNegativeToTheTileBoundary()
        {
            var clip = TileBufferClip.KeepTileUnits(-5.0);
            Assert.IsTrue(clip.IsEnabled, "a negative margin is still an ENABLED clip — clamped, not ignored.");
            Assert.IsTrue(clip.TryWindow(4096.0, out var min, out var max));
            Assert.AreEqual(0.0, min.x, 1e-12);
            Assert.AreEqual(0.0, min.y, 1e-12);
            Assert.AreEqual(4096.0, max.x, 1e-12, "a negative margin must never erode INTO the tile.");
            Assert.AreEqual(4096.0, max.y, 1e-12);
        }

        [Test]
        public void TryWindow_RefusesANonPositiveExtent()
        {
            Assert.IsFalse(TileBufferClip.KeepTileUnits(64.0).TryWindow(0.0, out _, out _));
            Assert.IsFalse(TileBufferClip.KeepTileUnits(64.0).TryWindow(-4096.0, out _, out _));
        }

        [Test]
        public void KeepTileUnits_RejectsNaN_RatherThanPropagatingItIntoTheWindow()
        {
            // NaN is the one input that fails DANGEROUSLY rather than merely wrongly: math.max(0, NaN) is
            // NaN (the comparison is false, so the second operand wins), a NaN margin makes a NaN window,
            // every vertex tests outside it, and the map renders BLANK with no error anywhere. Clamping is
            // not enough — the clamp is what silently passes it through.
            var clip = TileBufferClip.KeepTileUnits(double.NaN);

            Assert.IsFalse(double.IsNaN(clip.KeepAtReferenceExtent),
                "a NaN margin must not survive construction");
            Assert.IsTrue(clip.TryWindow(4096.0, out var min, out var max),
                "the degraded value must still produce a usable window");
            Assert.IsFalse(double.IsNaN(min.x) || double.IsNaN(min.y) || double.IsNaN(max.x) || double.IsNaN(max.y),
                "no window component may be NaN — that clips the entire map away");
            Assert.AreEqual(0.0, min.x, 1e-12, "NaN degrades to the tile boundary, the safe end of the range");
            Assert.AreEqual(4096.0, max.x, 1e-12);
        }
    }
}
