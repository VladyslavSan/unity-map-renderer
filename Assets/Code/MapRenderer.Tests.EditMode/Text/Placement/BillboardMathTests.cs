// Engine-free: shared verbatim between the Unity EditMode runner and Tools/core-tests (registered in
// core-tests.csproj). Top-level `using Unity.Mathematics;` + unqualified float2/float4 (namespace-
// collision trap — see LabelScreenProjection.cs's header comment).

using NUnit.Framework;
using Unity.Mathematics;
using MapRenderer.Core.Text;
using MapRenderer.Core.Text.Placement;

namespace MapRenderer.Tests.Text.Placement
{
    /// <summary>
    /// Epic A / A1 (design §11 A1 D2/D3/D4/D6): <see cref="BillboardMath.BuildWorldQuad"/> golden
    /// (element-by-element corners/UVs, up to the A0-F2 Y-negation) + AnchorLocal/Tangent/AlignFlags carry-
    /// through. The screen-space <c>BuildQuad</c> this class tested pre-Epic-A is retired with the dead
    /// screen render path it built for; <c>BuildWorldQuad</c> is its only surviving production consumer.
    /// </summary>
    [TestFixture]
    public class BillboardMathTests
    {
        // `in default(float3)` isn't addressable (CS8156) — a static readonly field is.
        private static readonly float3 ZeroFloat3 = new float3(0f, 0f, 0f);

        private static SymbolQuad MakeQuad()
            => new SymbolQuad
            {
                TopLeft = new float2(-6f, 18f),
                BottomRight = new float2(12f, 0f),
                UvTopLeft = new float2(0.10f, 0.20f),
                UvBottomRight = new float2(0.30f, 0.50f),
                LineIndex = 0,
            };

        // ══════════════════════════════════════════════════════════════════════════════════════════════
        // Epic A / A1 (design §11 A1 D2/D3/D4/D6): BuildWorldQuad — the world-anchored sibling of BuildQuad.
        // ══════════════════════════════════════════════════════════════════════════════════════════════

        // ── Golden: unit scale, no rotation, no translate, anchor at 0 — the expected corners below are the
        //    same anchor-relative baked-px corners MakeQuad()'s TopLeft/BottomRight decompose into (no
        //    scale/rotation/anchor to apply at these params), up to the A0-F2 Y-negation (OffsetPx.y is the
        //    corner's Y negated). Pins the corner math is REUSED, not re-derived, and that
        //    AnchorLocal/Page/AlignFlags carry through. Inlined literals — the old BuildQuad oracle this test
        //    used is retired with the dead screen render path it built. ──
        [Test]
        public void BuildWorldQuad_UnitScale_NoRotationNoTranslate_MatchesBuildQuad_WithA0F2YNegation()
        {
            SymbolQuad quad = MakeQuad();
            var anchorLocal = new float3(10f, 20f, 30f);
            var colorRgb = new float3(0.2f, 0.4f, 0.6f);

            // The anchor-relative corners BuildQuad would have produced for MakeQuad() at unit scale, zero
            // rotation, anchor (0,0): TopLeft/BottomRight verbatim, TopRight/BottomLeft cross the two.
            var expectedTl = quad.TopLeft;
            var expectedTr = new float2(quad.BottomRight.x, quad.TopLeft.y);
            var expectedBr = quad.BottomRight;
            var expectedBl = new float2(quad.TopLeft.x, quad.BottomRight.y);

            BillboardMath.BuildWorldQuad(in quad, in anchorLocal, TextQuadLayout.OneEm, in colorRgb, 0f, in float2.zero,
                in ZeroFloat3, 0f,
                out WorldBillboardVertex worldTl, out WorldBillboardVertex worldTr,
                out WorldBillboardVertex worldBr, out WorldBillboardVertex worldBl);

            foreach ((float2 expectedScreenPx, WorldBillboardVertex world, float2 expectedUv, string label) in new[]
                     {
                         (expectedTl, worldTl, quad.UvTopLeft, "topLeft"),
                         (expectedTr, worldTr, new float2(quad.UvBottomRight.x, quad.UvTopLeft.y), "topRight"),
                         (expectedBr, worldBr, quad.UvBottomRight, "bottomRight"),
                         (expectedBl, worldBl, new float2(quad.UvTopLeft.x, quad.UvBottomRight.y), "bottomLeft"),
                     })
            {
                Assert.AreEqual(expectedScreenPx.x, world.OffsetPx.x, 1e-5f, $"{label}: OffsetPx.x matches BuildQuad's ScreenPx.x (no translate offset here — anchor is 0)");
                Assert.AreEqual(-expectedScreenPx.y, world.OffsetPx.y, 1e-5f, $"{label}: OffsetPx.y is the A0-F2 NEGATION of BuildQuad's ScreenPx.y");
                Assert.AreEqual(expectedUv.x, world.Uv.x, 1e-6f, $"{label}: UV stays attached to its ORIGINAL corner (no flip)");
                Assert.AreEqual(expectedUv.y, world.Uv.y, 1e-6f, $"{label}: UV.y unflipped too");
                Assert.AreEqual(anchorLocal, world.AnchorLocal, $"{label}: AnchorLocal is the same on every corner");
                Assert.AreEqual(0f, world.AlignFlags, $"{label}: AlignFlags stays 0 in A1 (viewport-aligned only; A3 wires the map-bearing bit)");
                Assert.AreEqual(ZeroFloat3, world.Tangent, $"{label}: Tangent stays zero for a point/icon caller (Stage AC, unread by the shader when AlignFlags bit1 is clear)");
            }
        }

        // ── D4: text-translate rides as an ADDITIVE, UNROTATED corner offset (with the SAME Y-negation as
        //    the corner) — every corner shifts by translateDeltaPx identically, no double-apply, no rotation
        //    of the translate itself. ──
        [Test]
        public void BuildWorldQuad_TranslateDelta_ShiftsEveryCorner_UnrotatedWithSameYNegation()
        {
            SymbolQuad quad = MakeQuad();
            var translateDeltaPx = new float2(7f, -3f);

            BillboardMath.BuildWorldQuad(in quad, in ZeroFloat3, TextQuadLayout.OneEm, in ZeroFloat3, 0f, in float2.zero,
                in ZeroFloat3, 0f,
                out WorldBillboardVertex tl0, out WorldBillboardVertex tr0, out WorldBillboardVertex br0, out WorldBillboardVertex bl0);
            BillboardMath.BuildWorldQuad(in quad, in ZeroFloat3, TextQuadLayout.OneEm, in ZeroFloat3, 0f, in translateDeltaPx,
                in ZeroFloat3, 0f,
                out WorldBillboardVertex tl1, out WorldBillboardVertex tr1, out WorldBillboardVertex br1, out WorldBillboardVertex bl1);

            var expectedDelta = new float2(translateDeltaPx.x, -translateDeltaPx.y); // same negation as the corner
            foreach ((WorldBillboardVertex before, WorldBillboardVertex after, string label) in new[]
                     {
                         (tl0, tl1, "topLeft"), (tr0, tr1, "topRight"), (br0, br1, "bottomRight"), (bl0, bl1, "bottomLeft"),
                     })
            {
                Assert.AreEqual(expectedDelta.x, after.OffsetPx.x - before.OffsetPx.x, 1e-5f, $"{label}: translate delta x");
                Assert.AreEqual(expectedDelta.y, after.OffsetPx.y - before.OffsetPx.y, 1e-5f, $"{label}: translate delta y");
            }
        }

        // ── NEW-F2: colour carries VERBATIM — no sRGB→linear (or any other) conversion. A non-trivial,
        //    non-black/non-white colour in must come out byte-identical, on every corner. ──
        [Test]
        public void BuildWorldQuad_ColorRgb_CarriesVerbatim_NoGammaConversion()
        {
            SymbolQuad quad = MakeQuad();
            var colorRgb = new float3(0.73f, 0.11f, 0.42f); // deliberately non-trivial — a double-convert would move this

            BillboardMath.BuildWorldQuad(in quad, in ZeroFloat3, TextQuadLayout.OneEm, in colorRgb, 0f, in float2.zero,
                in ZeroFloat3, 0f,
                out WorldBillboardVertex tl, out WorldBillboardVertex tr, out WorldBillboardVertex br, out WorldBillboardVertex bl);

            foreach (WorldBillboardVertex v in new[] { tl, tr, br, bl })
            {
                Assert.AreEqual(colorRgb.x, v.ColorRGB.x, 1e-6f, "R carries verbatim");
                Assert.AreEqual(colorRgb.y, v.ColorRGB.y, 1e-6f, "G carries verbatim");
                Assert.AreEqual(colorRgb.z, v.ColorRGB.z, 1e-6f, "B carries verbatim");
            }
        }

        // ── Winding: TL/TR/BR/BL matches BuildQuad's convention exactly (same corner→attribute mapping) —
        //    WorldLabelRenderer's index emit (TL,TR,BR / TL,BR,BL) depends on this. ──
        [Test]
        public void BuildWorldQuad_Winding_MatchesBuildQuadConvention()
        {
            SymbolQuad quad = MakeQuad();
            BillboardMath.BuildWorldQuad(in quad, in ZeroFloat3, TextQuadLayout.OneEm, in ZeroFloat3, 0f, in float2.zero,
                in ZeroFloat3, 0f,
                out WorldBillboardVertex tl, out WorldBillboardVertex tr, out WorldBillboardVertex br, out WorldBillboardVertex bl);

            // TR shares BR's baked x / TL's baked y (mirrors BuildQuad's trLocal = (brLocal.x, tlLocal.y)) —
            // after negation, TR.OffsetPx.y == TL.OffsetPx.y (both share the top edge).
            Assert.AreEqual(tl.OffsetPx.y, tr.OffsetPx.y, 1e-5f, "TL/TR share the top edge (same Y)");
            Assert.AreEqual(br.OffsetPx.y, bl.OffsetPx.y, 1e-5f, "BR/BL share the bottom edge (same Y)");
            Assert.AreEqual(tl.OffsetPx.x, bl.OffsetPx.x, 1e-5f, "TL/BL share the left edge (same X)");
            Assert.AreEqual(tr.OffsetPx.x, br.OffsetPx.x, 1e-5f, "TR/BR share the right edge (same X)");
        }

        // ── Stage AC (curved-world) D-I/D-A: tangentLocal/alignFlags ride VERBATIM onto every corner —
        //    curved's discriminator (bit1 set, a nonzero Tangent) must not be dropped or blended per-corner. ──
        [Test]
        public void BuildWorldQuad_TangentLocalAndAlignFlags_CarryVerbatimToEveryCorner()
        {
            SymbolQuad quad = MakeQuad();
            var tangentLocal = new float3(0.6f, 0.0f, 0.8f); // a unit-ish along-line direction
            const float alongLineAlignFlags = 2f; // D-I bit1

            BillboardMath.BuildWorldQuad(in quad, in ZeroFloat3, TextQuadLayout.OneEm, in ZeroFloat3, 0f, in float2.zero,
                in tangentLocal, alongLineAlignFlags,
                out WorldBillboardVertex tl, out WorldBillboardVertex tr, out WorldBillboardVertex br, out WorldBillboardVertex bl);

            foreach ((WorldBillboardVertex v, string label) in new[] { (tl, "topLeft"), (tr, "topRight"), (br, "bottomRight"), (bl, "bottomLeft") })
            {
                Assert.AreEqual(tangentLocal, v.Tangent, $"{label}: Tangent carries verbatim to every corner");
                Assert.AreEqual(alongLineAlignFlags, v.AlignFlags, $"{label}: AlignFlags carries verbatim to every corner");
            }
        }
    }
}
