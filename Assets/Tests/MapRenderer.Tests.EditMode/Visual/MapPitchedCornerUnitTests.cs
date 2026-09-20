// Unity EditMode only — real TiltedGroundScene + a REAL SymbolPlacementSystem.Tick, read back off the built
// slot mesh. NOT registered in Tools/core-tests/core-tests.csproj.
//
// Stage W2 — the CORNER-UNIT teeth that need the RENDERER (W2-T7, W2-T8).
//
// PLACEMENT NOTE, deliberate and worth stating. The W2 plan filed T7 under `BillboardMathTests` and T8 under
// `MapPitchedWorldArcStagingTests`. Both of those are engine-free files that reach only `MapRenderer.Core`,
// and both of these teeth are claims about `WorldSymbolRenderer.Emit` — "the four WorldBillboardVertexs
// produced by the RENDERER", and "the renderer scales the translate delta by the corner unit". Asserting
// either one without the renderer in the loop would be asserting the test's own arithmetic, which is the
// self-referential-oracle failure this epic already made once. So they live here, on the real emit path, and
// the deviation from the plan's filing is recorded rather than silently taken.
//
// WHY tilt 0. Neither tooth reads a projected quantity — both read the emitted VERTEX STREAM off the built
// mesh — so the pose only has to be one where the symbols stage reliably. Tilt 0 is the simplest such pose and
// it is the one MapPitchedGlyphSizeTiltZeroTests already uses, so the two files share a shape.

#if UNITY_EDITOR
using System.Collections.Generic;
using System.Globalization;
using NUnit.Framework;
using Unity.Mathematics;
using UnityEngine;
using MapRenderer.Core.Style.Symbol;
using MapRenderer.Core.Text;
using MapRenderer.Core.Text.Placement;
using MapRenderer.Tests.Text.Placement;
using MapRenderer.Unity.Rendering.Backend;
using MapRenderer.Unity.Text;
using MapRenderer.Unity.Text.Placement;

namespace MapRenderer.Tests.Visual
{
    [TestFixture]
    public class MapPitchedCornerUnitTests
    {
        private const int   SizePx     = 512;
        private const float TextSizePx = 160f;

        /// <summary>`AlignFlags` bit1 — Stage AC's along-line rotation. Spelled out here rather than read from
        /// <c>WorldSymbolRenderer</c>'s own private constant: a tooth that compares a value to itself pins
        /// nothing (the same reason <c>WorldSymbolGroupingTests</c> asserts its hierarchy names literally).</summary>
        private const float AlongLineBit = 2f;

        /// <summary>`AlignFlags` bit2 — W2's map-pitch bit, same rationale.</summary>
        private const float MapPitchBit = 4f;

        // ═══════════════════════════════════════════════════════════════════════════════════════════════
        // W2-T7 — the flag and the unit cannot disagree (R3)
        // ═══════════════════════════════════════════════════════════════════════════════════════════════

        /// <summary>
        /// <b>W2-T7, clause 1 — the STRUCTURAL statement of R3: the non-map path is BITWISE unchanged.</b>
        /// Proves: for a curved symbol whose <c>CornerMetresPerLogicalPixel</c> is 0 (every viewport- and
        /// auto-pitched symbol, and every point symbol), the four <see cref="WorldBillboardVertex"/>s the
        /// renderer emitted are bit-for-bit equal to a <see cref="BillboardMath.BuildWorldQuad"/> call made
        /// with the PRE-W2 arguments — <c>q.TextSizePx</c> unscaled, <c>emit.TranslateDeltaPx</c> unscaled,
        /// <c>alignFlags = AlongLineAlignFlag</c>.
        ///
        /// <para><b>Compared as raw float BITS, not with a tolerance.</b> The claim W2's stage invariant
        /// makes is not "close enough" — it is that <c>cornerScale</c> is the literal <c>1f</c> on every
        /// non-map path and <c>x * 1f</c> is bitwise identity for every finite float and for ±0/±Inf/NaN
        /// payloads alike, so the vertex stream is EXACTLY what it was before this stage. A tolerance would
        /// let a real unit slip through as rounding.</para>
        ///
        /// <para><b>The reference does not come from the arm under test.</b> The expected quad is built from
        /// the symbol's own baked cell plus the mesh's own <c>AnchorLocal</c>/<c>Tangent</c>/<c>Up</c> — i.e.
        /// from the geometry, not from the corner offsets being checked.</para>
        /// </summary>
        [Test]
        public void NonMapPitchedCorners_AreBitwiseThePreW2Quad()
        {
            WorldBillboardVertex[] v = EmitAndRead(AlignmentMode.Viewport, float2.zero, out SymbolQuad cell, out _);

            // Hoisted into locals: an `in` parameter needs an addressable operand, and several of these are
            // property values or static members rather than fields.
            float3 anchor  = v[0].AnchorLocal;
            float3 colour  = v[0].ColorRGB;
            float3 tangent = v[0].Tangent;
            float3 up      = v[0].Up;
            float2 noTrans = float2.zero;
            BillboardMath.BuildWorldQuad(in cell, in anchor, TextSizePx, in colour,
                rotationRadians: 0f, in noTrans, in tangent, in up, alignFlags: AlongLineBit,
                out WorldBillboardVertex tl, out WorldBillboardVertex tr,
                out WorldBillboardVertex br, out WorldBillboardVertex bl);

            WorldBillboardVertex[] expected = { tl, tr, br, bl };
            string[] corner = { "topLeft", "topRight", "bottomRight", "bottomLeft" };
            for (int c = 0; c < 4; c++)
            {
                AssertBitwise(expected[c].Offset.x, v[c].Offset.x, $"W2-T7 {corner[c]}.Offset.x");
                AssertBitwise(expected[c].Offset.y, v[c].Offset.y, $"W2-T7 {corner[c]}.Offset.y");
                AssertBitwise(expected[c].AlignFlags, v[c].AlignFlags, $"W2-T7 {corner[c]}.AlignFlags");
            }
        }

        /// <summary>
        /// <b>W2-T7, clause 2 — the flag tracks the unit, and the scope FENCE is asserted.</b> Proves: when
        /// the corner unit is metres, every corner's <c>AlignFlags</c> has bit2 set — and bit1 as well,
        /// because in W2 map-pitch never occurs without along-line.
        ///
        /// <para>Bit1 is not decoration here. E7's <c>emit.AlongLine</c> conjunct is the scope fence the plan
        /// drew: point/icon map-pitch is a LATER stage. Asserting that bit2 never appears without bit1 is what
        /// makes that fence observable instead of merely commented — if a future stage lifts it, this tooth
        /// says so.</para>
        ///
        /// <para>A separate <c>[Test]</c> from clause 1 on purpose: NUnit throws on the first failure, so two
        /// clauses in one method means the second never runs. That has already cost this epic two teeth.</para>
        /// </summary>
        [Test]
        public void MapPitchedCorners_CarryBit2_AndNeverWithoutBit1()
        {
            WorldBillboardVertex[] v = EmitAndRead(AlignmentMode.Map, float2.zero, out _, out _);

            for (int c = 0; c < v.Length; c++)
            {
                float flags = v[c].AlignFlags;
                bool bit2 = math.fmod(math.floor(flags * 0.25f), 2f) >= 0.5f;
                bool bit1 = math.fmod(math.floor(flags * 0.5f),  2f) >= 0.5f;
                Assert.That(bit2 && bit1, Is.True,
                    $"W2-T7 (vertex {c}): a map-pitched corner must carry AlignFlags bit2 (metres) AND bit1 " +
                    $"(along-line) — flags read {flags}. Expected {AlongLineBit + MapPitchBit}. bit2 without " +
                    "bit1 would mean a point/icon label had crossed W2's scope fence; bit1 without bit2 means " +
                    "the shader will read metre offsets with the pixel formula.");
            }
        }

        // ═══════════════════════════════════════════════════════════════════════════════════════════════
        // W2-T8 — the recorded `text-translate` limitation's observer (R4 / followUp F-W2-2)
        // ═══════════════════════════════════════════════════════════════════════════════════════════════

        /// <summary>
        /// <b>W2-T8 — the observer for followUp F-W2-2.</b> Proves: under map pitch the <c>text-translate</c>
        /// delta rides the corner offsets in the SAME unit they do — i.e. it is scaled by
        /// <c>CornerMetresPerLogicalPixel</c> and becomes a WORLD translate.
        ///
        /// <para><b>Why this is a recorded limitation and not a design position.</b> The shader has ONE
        /// displacement path, so the corners and the translate must share whichever unit is in force; keeping
        /// the translate in screen px would mean a second, clip-space displacement path, which re-creates the
        /// two-rulers-in-one-shader shape that got three stages reverted. The Style Spec's proper control for
        /// whether a translate is a screen or a map offset is <c>text-translate-anchor</c>, not
        /// <c>text-pitch-alignment</c>, so the behaviour is recorded rather than defended.</para>
        ///
        /// <para><b>It is inert on every shipped style.</b> All seven line-placed symbol layers in
        /// <c>liberty.json</c> (<c>road_one_way_arrow</c>, <c>road_one_way_arrow_opposite</c>,
        /// <c>waterway_line_label</c>, <c>water_name_line_label</c>, <c>highway-name-path</c>,
        /// <c>highway-name-minor</c>, <c>highway-name-major</c>) set no <c>text-translate</c> /
        /// <c>icon-translate</c> / <c>text-translate-anchor</c>, so the delta is exactly <c>float2.zero</c> and
        /// <c>0 · k == 0</c>.</para>
        ///
        /// <para><b>This tooth is the answer to "which test goes RED if this stops being deliberate?"</b>
        /// A recorded limitation needs a tooth that observes it. Without one the scaling is an unobserved
        /// path, and this epic has already shipped one of those.</para>
        ///
        /// <para><b>Method: a DIFFERENCE, so the corner geometry cancels.</b> The same map-pitched symbol is
        /// emitted twice, once with a translate and once without, and the per-corner offset difference must
        /// have the MAGNITUDE of the delta times the corner unit, component by component.</para>
        ///
        /// <para><b>Component MAGNITUDES, not signed values — and that is a scope statement, not a
        /// weakening.</b> The Y sense of a translate is set by <c>SymbolTranslate.ApplyTranslate</c>'s own
        /// convention composed with <c>BuildWorldQuad</c>'s A0-F2 negation, both of which predate W2 and
        /// neither of which this stage touches; measured, the two compose to a POSITIVE Y here. W2's claim is
        /// exclusively about the UNIT, and the unit is what the magnitude states. Re-pinning the sign would
        /// duplicate a convention that already has its own owners, and would make this tooth go RED for a
        /// reason that has nothing to do with the corner unit. The discrimination is unaffected: injection I6
        /// leaves a delta of 7 and 11 raw PIXELS where this expects 2140 and 3363 METRES — a factor of ~306,
        /// against a 1 % bound.</para>
        ///
        /// <para>RED recipe: injection I6 — pass <c>emit.TranslateDeltaPx</c> unscaled (mixed units in one
        /// <c>off</c>). Everything else stays GREEN.</para>
        /// </summary>
        [Test]
        public void MapPitchedTranslate_MovesInMetres_NotPixels()
        {
            var translatePx = new float2(7f, 11f);
            WorldBillboardVertex[] plain      = EmitAndRead(AlignmentMode.Map, float2.zero, out _, out _);
            WorldBillboardVertex[] translated = EmitAndRead(AlignmentMode.Map, translatePx,
                out _, out double metresPerLogicalPixel);

            // Component MAGNITUDES — the Y sense belongs to ApplyTranslate ∘ A0-F2, not to W2 (see the doc).
            var expected = new float2(
                (float)(translatePx.x * metresPerLogicalPixel),
                (float)(translatePx.y * metresPerLogicalPixel));

            for (int c = 0; c < 4; c++)
            {
                float2 signed = translated[c].Offset - plain[c].Offset;
                float2 delta = math.abs(signed);
                TestContext.WriteLine(string.Format(CultureInfo.InvariantCulture,
                    "W2-T8  vertex {0}  delta=({1:F3}, {2:F3}) m  |delta|=({3:F3}, {4:F3})  " +
                    "expected magnitude=({5:F3}, {6:F3}) m  (translatePx=({7:F1}, {8:F1}) × mppLogical={9:F4})",
                    c, signed.x, signed.y, delta.x, delta.y, expected.x, expected.y,
                    translatePx.x, translatePx.y, metresPerLogicalPixel));

                Assert.That(math.length(delta - expected), Is.LessThan(0.01 * math.length(expected)),
                    $"W2-T8 (vertex {c}): under map pitch the text-translate delta must ride the corners in " +
                    $"METRES — measured magnitude ({delta.x:F3}, {delta.y:F3}), expected ({expected.x:F3}, " +
                    $"{expected.y:F3}) = |translatePx| × MetresPerLogicalPixel. A delta of the raw pixel " +
                    "magnitude means the corners and the translate are in DIFFERENT units inside one `off`, " +
                    "which the shader's single displacement path cannot represent. This behaviour is recorded " +
                    "as followUp F-W2-2 — a limitation, not a design position — and this tooth exists so it " +
                    "cannot rot into an unobserved path.");
            }
        }

        // ═══════════════════════════════════════════════════════════════════════════════════════════════
        // Harness
        // ═══════════════════════════════════════════════════════════════════════════════════════════════

        /// <summary>Stages ONE curved symbol through the real <see cref="SymbolPlacementSystem"/> and reads its
        /// first glyph's four vertices back off the built slot mesh — the same production path
        /// <c>OffLookAtSymbolScene</c> measures through.</summary>
        private static WorldBillboardVertex[] EmitAndRead(
            AlignmentMode pitch, float2 translatePx, out SymbolQuad cell, out double metresPerLogicalPixel)
        {
            var sceneConfig = new TiltedGroundSceneConfig
            {
                TiltDegrees = 0.0, SizePx = SizePx,
                BackgroundColor = Color.white, LitAmbient = false,
            };

            TiltedGroundScene    scene  = null;
            GlyphAtlasTexture    atlas  = null;
            SymbolPlacementSystem system = null;
            TestSymbolPlan       plan   = null;
            try
            {
                scene = TiltedGroundScene.Create(sceneConfig);
                (atlas, cell) = WorldCurvedAbRenderSnapshotTests.BuildGlyphF();
                metresPerLogicalPixel = scene.MetresPerDevicePixel * sceneConfig.DevicePixelRatio;

                SceneFrame frame = scene.BuildIdentityRebaseSceneFrame();
                double3 origin = frame.SceneOriginRender;
                double halfLen = 200.0 * scene.MetresPerDevicePixel; // no metre literals — see TiltFixtureSelfTests
                var dir = new double3(1.0, 0.0, 0.0);
                var up  = new double3(0.0, 1.0, 0.0);
                long tileKey = TestTileKeys.PackedContaining(sceneConfig.LookAt.Surface, zoom: 14);

                var buffer = new SymbolTileBuffer();
                var glyphs = new List<CurvedGlyph> { new CurvedGlyph { ArcCenter = 0f, Cell = cell } };
                TestSymbolTileBuffer.AddCurved(buffer, glyphs, new[] { new LineAnchor(0, 0.5f) },
                    new[] { origin - dir * halfLen, origin + dir * halfLen }, new[] { up, up },
                    placement: SymbolPlacement.LineCenter,
                    up: up,
                    pitchAlignment: pitch,
                    paint: SymbolPaint.Default,
                    translatePx: translatePx,
                    text: "T",
                    textSizePx: TextSizePx,
                    maxAngleDeg: 180f,
                    keepUpright: false,
                    allowOverlap: true,
                    featureIndex: 0,
                    tileKey: tileKey);

                system = new SymbolPlacementSystem(scene.MapCam,
                    worldTextBase: new Material(Shader.Find("Map/Symbol/TextWorld")));
                plan = new TestSymbolPlan(scene.MapCam.Projection);

                // R3: duplicate Tick — the collision verdict is harvested one Tick late.
                system.Tick(in frame, plan.Build(buffer), atlas);
                system.Tick(in frame, plan.Build(buffer), atlas);
                Assert.That(system.LastQuadCount, Is.EqualTo(1),
                    $"W2-T7/T8 precondition ({pitch}): the label must stage exactly one quad, got " +
                    $"{system.LastQuadCount}.");

                Assert.That(system.TryGetWorldSlotMesh(tileKey, 0, SymbolKind.Text, out Mesh mesh), Is.True,
                    "W2-T7/T8 precondition: the label emitted no world slot mesh.");
                WorldMeshReadback.Read(mesh, out WorldBillboardVertex[] vertices, out _);
                Assert.That(vertices.Length, Is.EqualTo(4),
                    $"W2-T7/T8 precondition: expected exactly one quad's 4 vertices, got {vertices.Length}.");
                return vertices;
            }
            finally
            {
                plan?.Dispose();
                system?.Dispose();
                atlas?.Dispose();
                scene?.Dispose();
            }
        }

        /// <summary>Compares two floats by their raw 32-bit patterns — the only comparison that can state
        /// "bit-for-bit unchanged" rather than "close".</summary>
        private static void AssertBitwise(float expected, float actual, string what)
        {
            uint e = math.asuint(expected), a = math.asuint(actual);
            Assert.That(a, Is.EqualTo(e),
                $"{what}: 0x{a:X8} against 0x{e:X8} ({actual} vs {expected}). W2's invariant is that the " +
                "non-map path is BIT-identical to the pre-W2 one — cornerScale is the literal 1f there and " +
                "`x * 1f` is bitwise identity, so any difference at all is a real behaviour change.");
        }
    }
}
#endif
