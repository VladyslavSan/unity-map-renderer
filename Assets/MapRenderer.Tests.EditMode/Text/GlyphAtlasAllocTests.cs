// Unity-only (UnityEngine.TestTools.Constraints.Is.Not.AllocatingGCMemory() — the ONLY trustworthy
// allocation meter on Unity Mono per docs/lessons-learned.md's "Measuring per-frame GC allocation" note).
// Excluded from core-tests.csproj.
//
// T4 (S18 §4, Slice 5): appending an already-decoded glyph to the atlas, and shaping a cached run into a
// caller-owned buffer, must allocate ZERO managed garbage on the steady path. Scope is deliberately
// narrow (first-time decode / texture upload / RTL joining-bidi may allocate — see each test's comment).

using System.Collections.Generic;
using NUnit.Framework;
using Unity.Mathematics;
using UnityEngine.TestTools.Constraints;
using Is = UnityEngine.TestTools.Constraints.Is;
using MapRenderer.Core.Text;

namespace MapRenderer.Tests.Text
{
    [TestFixture]
    public class GlyphAtlasAllocTests
    {
        // =========================================================================================
        // (a) Append an already-decoded glyph. Deterministic zero-alloc setup: a WIDE atlas (so many
        //     same-size cells fit on one shelf, never wrapping to a new -- taller-usedHeight -- row)
        //     plus re-appending the SAME codepoint (so the entries-dictionary write is an in-place
        //     overwrite of an EXISTING key, which can never trigger a Dictionary capacity resize).
        //     With both variables pinned, GrowToFit's buffer realloc genuinely never fires after the
        //     first (unmeasured) warm-up append -- no reliance on guessing CLR dictionary growth points.
        // =========================================================================================
        [Test]
        public void Append_AlreadyDecodedGlyph_AllocatesNoGCMemory()
        {
            var atlas = new GlyphAtlas(width: 4096); // wide enough that this glyph's cell never wraps shelves
            SdfGlyph glyph = MakeSyntheticGlyph(codepoint: 1u, width: 4, height: 4, advance: 5);

            // Warm-up: the FIRST Append legitimately allocates (buffer created from empty, dictionary
            // backing arrays initialized on the first Add).
            atlas.Append(glyph);

            // Block-bodied lambda (not an expression lambda): Append returns a value (GlyphAtlasEntry),
            // and Assert.That needs a void TestDelegate here — an expression lambda binds to the wrong
            // overload and fails with "actual value must be a TestDelegate" (see S95TileLoadMeasurementTests).
            Assert.That(() => { atlas.Append(glyph); }, Is.Not.AllocatingGCMemory(),
                "re-appending an already-decoded glyph (same codepoint, same cell height, same open shelf) " +
                "must not allocate: GrowToFit no-ops once the shelf's used height stops increasing, and " +
                "Dictionary[key]= on an EXISTING key overwrites in place without growing capacity.");
        }

        private static SdfGlyph MakeSyntheticGlyph(uint codepoint, int width, int height, int advance)
        {
            int2 cellSize = new int2(width + 2 * GlyphSdf.Buffer, height + 2 * GlyphSdf.Buffer);
            return new SdfGlyph
            {
                Codepoint = codepoint,
                Width = width,
                Height = height,
                Left = 0,
                Top = 0,
                Advance = advance,
                Bitmap = new byte[cellSize.x * cellSize.y],
            };
        }

        // =========================================================================================
        // (b) Shape a cached run (steady LTR path) into a caller-owned List<PositionedGlyph> buffer.
        //     Warm-up call lets the List's backing array reach its stable capacity; the measured call
        //     reuses it -- CodepointTextShaper.Shape(in request, output) writes PositionedGlyph VALUE
        //     structs directly into `output`, with no intermediate codepoint/cluster lists and no
        //     ShapedRun class allocation on this path (see its doc comment).
        // =========================================================================================
        [Test]
        public void Shape_CachedLatinRun_IntoCallerBuffer_AllocatesNoGCMemory()
        {
            var metrics = new FixedAdvanceMetrics(12f);
            var shaper = new CodepointTextShaper();
            var request = new ShapingRequest { Text = "Hello Map Labels", FontStack = null, Metrics = metrics };
            var output = new List<PositionedGlyph>(32);

            // Warm-up: stabilizes `output`'s backing array capacity.
            shaper.Shape(in request, output);

            Assert.That(() => { shaper.Shape(in request, output); }, Is.Not.AllocatingGCMemory(),
                "Shape(in request, output) must not allocate on the steady LTR path once the caller " +
                "buffer's capacity has stabilized from the warm-up call.");
        }

        private sealed class FixedAdvanceMetrics : IGlyphMetricsProvider
        {
            private readonly float _advance;
            public FixedAdvanceMetrics(float advance) => _advance = advance;
            public bool TryGetAdvance(uint codepoint, out float advance)
            {
                advance = _advance;
                return true;
            }
        }
    }
}
