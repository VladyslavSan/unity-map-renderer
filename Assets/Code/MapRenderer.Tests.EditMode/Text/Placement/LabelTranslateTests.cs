// Engine-free: compiled verbatim by both the Unity EditMode runner and Tools/core-tests.
// Do NOT add any UnityEngine, MeshBuilder, NativeArray, or MonoBehaviour references.

using NUnit.Framework;
using Unity.Mathematics;
using MapRenderer.Core.Text;
using MapRenderer.Core.Text.Placement;

namespace MapRenderer.Tests.Text.Placement
{
    /// <summary>
    /// Slice C / #4 — the pure <c>text-translate</c> screen-delta math (the engine-free half; the
    /// <c>LabelPlacementSystem.Tick</c> integration is proven separately in EditMode). Viewport-anchor and
    /// map-anchor-at-bearing-0 are fully testable; the map-under-bearing SIGN is a visual-verify handoff
    /// (LabelBearing.MapAlignedSign), so the bearing tests assert structure (magnitude, ≠ viewport), not the
    /// exact rotated coordinates.
    /// </summary>
    [TestFixture]
    public class LabelTranslateTests
    {
        private const float Bearing0 = 0f;

        [Test]
        public void ApplyTranslate_Viewport_RightIsPlusX_DownIsMinusY()
        {
            // MapLibre text-translate [5,3] = right 5, DOWN 3. screenPx is y-up (bottom-left origin), so a
            // downward offset is -3 there; x (right) is unchanged.
            float2 result = LabelTranslate.ApplyTranslate(new float2(100f, 200f), new float2(5f, 3f), TextTranslateAnchor.Viewport, Bearing0);
            Assert.AreEqual(105f, result.x, 1e-6f, "text-translate x (right) adds to screen x");
            Assert.AreEqual(197f, result.y, 1e-6f, "text-translate y (down) SUBTRACTS from screen y (y-up)");
        }

        [Test]
        public void ApplyTranslate_Zero_IsIdentity()
        {
            var screen = new float2(12.5f, -7.25f);
            float2 result = LabelTranslate.ApplyTranslate(screen, float2.zero, TextTranslateAnchor.Viewport, Bearing0);
            Assert.AreEqual(screen.x, result.x, 1e-6f);
            Assert.AreEqual(screen.y, result.y, 1e-6f);
        }

        [Test]
        public void ApplyTranslate_NegativeComponents_MoveLeftAndUp()
        {
            // [-4,-6] = left 4, UP 6 → screen x-4, screen y+6.
            float2 result = LabelTranslate.ApplyTranslate(new float2(50f, 50f), new float2(-4f, -6f), TextTranslateAnchor.Viewport, Bearing0);
            Assert.AreEqual(46f, result.x, 1e-6f);
            Assert.AreEqual(56f, result.y, 1e-6f);
        }

        [Test]
        public void ApplyTranslate_MapAndViewport_CoincideAtBearingZero()
        {
            var screen = new float2(100f, 200f);
            var t = new float2(5f, 3f);
            float2 viewport = LabelTranslate.ApplyTranslate(screen, t, TextTranslateAnchor.Viewport, Bearing0);
            float2 map = LabelTranslate.ApplyTranslate(screen, t, TextTranslateAnchor.Map, Bearing0);
            Assert.AreEqual(viewport.x, map.x, 1e-6f, "map and viewport coincide at bearing 0 (north-up)");
            Assert.AreEqual(viewport.y, map.y, 1e-6f);
        }

        [Test]
        public void ApplyTranslate_Map_UnderBearing_RotatesTheOffset_PreservingMagnitude()
        {
            var screen = new float2(100f, 200f);
            var t = new float2(5f, 3f);
            float bearing = math.PI / 2f; // 90°

            float2 viewport = LabelTranslate.ApplyTranslate(screen, t, TextTranslateAnchor.Viewport, bearing);
            float2 map = LabelTranslate.ApplyTranslate(screen, t, TextTranslateAnchor.Map, bearing);

            // Viewport ignores the bearing; map rotates the delta about the anchor.
            float2 viewportDelta = viewport - screen;
            float2 mapDelta = map - screen;
            Assert.AreEqual(math.length(viewportDelta), math.length(mapDelta), 1e-4f, "rotation preserves the offset magnitude");
            Assert.That(math.distance(mapDelta, viewportDelta), Is.GreaterThan(1e-3f),
                "under a non-zero bearing, map-anchored translate must differ from viewport (the delta is rotated)");
        }
    }
}
