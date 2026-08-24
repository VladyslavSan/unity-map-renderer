// Engine-free: compiled verbatim by both the Unity EditMode runner and Tools/core-tests.
// Do NOT add any UnityEngine, MeshBuilder, NativeArray, or MonoBehaviour references.

using NUnit.Framework;
using MapRenderer.Core.Text;
using MapRenderer.Core.Text.Placement;

namespace MapRenderer.Tests.Text.Placement
{
    /// <summary>
    /// #4 — the alignment→billboard-rotation mapping. The mode selection (Map → bearing; Viewport/Auto → 0)
    /// is fully testable here; these assert the selection and the ×sign relation, and deliberately do NOT
    /// pin SymbolBearing.MapAlignedSign itself — multiplying by the constant under test can only restate it.
    /// The sign is pinned where a sign can actually be observed, by the rendered tooth
    /// SymbolIconRenderSnapshotTests.MapAlignedPointIcon_TurnsWithTheMap_UnderAnActiveBearing; every case
    /// below stays green against a fully inverted constant, which is exactly why that tooth exists.
    /// </summary>
    [TestFixture]
    public class SymbolBearingTests
    {
        [Test]
        public void BillboardRotation_Map_IsBearingTimesSign()
        {
            const float bearing = 0.7f;
            Assert.AreEqual(SymbolBearing.MapAlignedSign * bearing,
                SymbolBearing.BillboardRotationRadians(AlignmentMode.Map, bearing), 1e-6f);
        }

        [Test]
        public void BillboardRotation_ViewportAndAuto_AreZero_ForAnyBearing()
        {
            const float bearing = 1.23f;
            Assert.AreEqual(0f, SymbolBearing.BillboardRotationRadians(AlignmentMode.Viewport, bearing), 1e-6f,
                "viewport-aligned text never rotates with the map");
            Assert.AreEqual(0f, SymbolBearing.BillboardRotationRadians(AlignmentMode.Auto, bearing), 1e-6f,
                "auto resolves to viewport for point placement — no rotation");
        }

        [Test]
        public void BillboardRotation_Map_AtBearingZero_IsZero()
        {
            Assert.AreEqual(0f, SymbolBearing.BillboardRotationRadians(AlignmentMode.Map, 0f), 1e-6f,
                "a north-up map does not rotate even map-aligned text");
        }
    }
}
