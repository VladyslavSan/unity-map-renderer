// Unity EditMode only — SymbolTileBlock is Unity.Collections-side (NativeArray), reached here via
// InternalsVisibleTo("MapRenderer.Tests.EditMode") from MapRenderer.Unity/AssemblyInfo.cs. Deliberately in
// THIS assembly, not MapRenderer.Tests.Shared — that assembly also links PlayMode, where the NativeArray
// access this helper does is unwanted (presence-check-cannot-detect-misplacement: the wrong PLACE, not the
// wrong content, is the risk).

using System;
using NUnit.Framework;
using Unity.Collections;
using Unity.Mathematics;
using MapRenderer.Core.Text;
using MapRenderer.Core.Text.Placement;
using MapRenderer.Unity.Text.Placement;

namespace MapRenderer.Tests.Text.Placement
{
    /// <summary>
    /// Symbol-label perf Phase 1 (block-byte-identity golden, design §5 B): a shared column-by-column
    /// digest + equality helper over a baked <see cref="SymbolTileBlock"/> — the single place every
    /// bake-invariant test (the 4.4a golden here; the 4.4b prod-vs-oracle block compare) reads the block's
    /// arrays from, so the two cannot drift into checking different columns.
    ///
    /// <para><b>Two different jobs, two different methods.</b> <see cref="Hash"/> folds every column into a
    /// compact per-column digest — cheap to pin as a handful of committed <c>int</c> constants, but a
    /// mismatch only says WHICH COLUMN moved, not which element. <see cref="AssertColumnsEqual"/> is the
    /// diagnosable counterpart: element-wise equality between two blocks with the column name and the first
    /// differing index in the failure message — the shape a prod-vs-oracle compare (4.4b) needs to localize
    /// a divergence.</para>
    ///
    /// <para><b>TextIds/IconImageIds are deliberately EXCLUDED from <see cref="Hash"/></b> and compared by
    /// <see cref="AssertColumnsEqual"/> only via EQUIVALENCE CLASS, never raw id value: those two columns are
    /// bake-order ids from a caller-supplied <see cref="SymbolStringTable"/>, so two independently-captured
    /// blocks (a fresh intern table each) can assign the SAME string a DIFFERENT id without any real
    /// divergence. Folding the raw id into a hash — or asserting raw equality — would make the guard brittle
    /// to interning order, not to an actual bake defect.</para>
    ///
    /// <para><b>Bit-pattern, not value, for float/double.</b> Every float/double column hashes (and
    /// compares) the IEEE bit pattern (<c>math.asuint(float)</c> / <see cref="BitConverter.DoubleToInt64Bits"/>),
    /// not the numeric value — the byte-identity invariant this helper guards means <c>-0.0</c> and
    /// <c>+0.0</c> ARE different, and a future change that starts landing on the "other" zero is exactly the
    /// kind of drift the golden exists to catch. Do not loosen this to a tolerance comparison.</para>
    /// </summary>
    internal static class BlockColumnHash
    {
        /// <summary>One digest per hashed column (see the type doc for what "digest" buys you over a single
        /// combined value: <see cref="ToString"/> below names which column moved without a second run).
        /// <c>TextIds</c>/<c>IconImageIds</c> are deliberately absent — see the type doc.</summary>
        internal readonly struct ColumnHashes
        {
            internal readonly int Kinds, Detail, WorldStart, WorldCount, RepAnchor, MaterialIndexes,
                PairRoles, Points, PointQuadStart, PointQuadCount, Curveds, CurvedGlyphStart, CurvedGlyphCount,
                CurvedAnchorStart, CurvedAnchorCount, CurvedAnchorFadeStart, Quads, Glyphs, Anchors, WorldPoints,
                WorldUps, AnchorFadeIds, MaxBoxes, MaxQuads, MaxCandidates, TileKey;

            internal ColumnHashes(
                int kinds, int detail, int worldStart, int worldCount, int repAnchor, int materialIndexes,
                int pairRoles, int points, int pointQuadStart, int pointQuadCount, int curveds,
                int curvedGlyphStart, int curvedGlyphCount, int curvedAnchorStart, int curvedAnchorCount,
                int curvedAnchorFadeStart, int quads, int glyphs, int anchors, int worldPoints, int worldUps,
                int anchorFadeIds, int maxBoxes, int maxQuads, int maxCandidates, int tileKey)
            {
                Kinds = kinds; Detail = detail; WorldStart = worldStart; WorldCount = worldCount;
                RepAnchor = repAnchor; MaterialIndexes = materialIndexes; PairRoles = pairRoles;
                Points = points; PointQuadStart = pointQuadStart; PointQuadCount = pointQuadCount;
                Curveds = curveds; CurvedGlyphStart = curvedGlyphStart; CurvedGlyphCount = curvedGlyphCount;
                CurvedAnchorStart = curvedAnchorStart; CurvedAnchorCount = curvedAnchorCount;
                CurvedAnchorFadeStart = curvedAnchorFadeStart; Quads = quads; Glyphs = glyphs; Anchors = anchors;
                WorldPoints = worldPoints; WorldUps = worldUps; AnchorFadeIds = anchorFadeIds;
                MaxBoxes = maxBoxes; MaxQuads = maxQuads; MaxCandidates = maxCandidates; TileKey = tileKey;
            }

            /// <summary>Every column's digest on its own line, named — printed on a golden-mismatch failure
            /// so the orchestrator's ONE gate run yields every value there is to paste, per-column.</summary>
            public override string ToString() =>
                $"Kinds={Kinds} Detail={Detail} WorldStart={WorldStart} WorldCount={WorldCount} " +
                $"RepAnchor={RepAnchor} MaterialIndexes={MaterialIndexes} PairRoles={PairRoles} " +
                $"Points={Points} PointQuadStart={PointQuadStart} PointQuadCount={PointQuadCount} " +
                $"Curveds={Curveds} CurvedGlyphStart={CurvedGlyphStart} CurvedGlyphCount={CurvedGlyphCount} " +
                $"CurvedAnchorStart={CurvedAnchorStart} CurvedAnchorCount={CurvedAnchorCount} " +
                $"CurvedAnchorFadeStart={CurvedAnchorFadeStart} Quads={Quads} Glyphs={Glyphs} Anchors={Anchors} " +
                $"WorldPoints={WorldPoints} WorldUps={WorldUps} AnchorFadeIds={AnchorFadeIds} " +
                $"MaxBoxes={MaxBoxes} MaxQuads={MaxQuads} MaxCandidates={MaxCandidates} TileKey={TileKey}";
        }

        /// <summary>Digests every column of <paramref name="block"/> EXCEPT <c>TextIds</c>/<c>IconImageIds</c>
        /// (bake-order intern ids, not stable across captures) — see the type doc.</summary>
        internal static ColumnHashes Hash(SymbolTileBlock block) => new ColumnHashes(
            kinds: HashArray(block.Kinds, e => (int)e),
            detail: HashArray(block.Detail, e => e),
            worldStart: HashArray(block.WorldStart, e => e),
            worldCount: HashArray(block.WorldCount, e => e),
            repAnchor: HashArray(block.RepAnchor, HashDouble3),
            materialIndexes: HashArray(block.MaterialIndexes, e => e),
            pairRoles: HashArray(block.PairRoles, e => (int)e),
            points: HashArray(block.Points, HashPointStageInput),
            pointQuadStart: HashArray(block.PointQuadStart, e => e),
            pointQuadCount: HashArray(block.PointQuadCount, e => e),
            curveds: HashArray(block.Curveds, HashCurvedStageInput),
            curvedGlyphStart: HashArray(block.CurvedGlyphStart, e => e),
            curvedGlyphCount: HashArray(block.CurvedGlyphCount, e => e),
            curvedAnchorStart: HashArray(block.CurvedAnchorStart, e => e),
            curvedAnchorCount: HashArray(block.CurvedAnchorCount, e => e),
            curvedAnchorFadeStart: HashArray(block.CurvedAnchorFadeStart, e => e),
            quads: HashArray(block.Quads, HashSymbolQuad),
            glyphs: HashArray(block.Glyphs, HashCurvedGlyph),
            anchors: HashArray(block.Anchors, HashLineAnchor),
            worldPoints: HashArray(block.WorldPoints, HashDouble3),
            worldUps: HashArray(block.WorldUps, HashFloat3),
            anchorFadeIds: HashArray(block.AnchorFadeIds, HashLong),
            maxBoxes: block.MaxBoxes,
            maxQuads: block.MaxQuads,
            maxCandidates: block.MaxCandidates,
            tileKey: HashLong(block.TileKey));

        /// <summary>Element-wise equality between two blocks, column by column — the diagnosable counterpart
        /// to <see cref="Hash"/> (a failure names the column AND the first differing index/field). Every
        /// column <see cref="Hash"/> covers is compared exactly; <c>TextIds</c>/<c>IconImageIds</c> are
        /// compared by EQUIVALENCE CLASS (see the type doc), never raw id.</summary>
        internal static void AssertColumnsEqual(SymbolTileBlock a, SymbolTileBlock b)
        {
            AssertArrayEqual(a.Kinds, b.Kinds, "Kinds", (x, y, msg) => Assert.AreEqual(x, y, msg));
            AssertArrayEqual(a.Detail, b.Detail, "Detail", (x, y, msg) => Assert.AreEqual(x, y, msg));
            AssertArrayEqual(a.WorldStart, b.WorldStart, "WorldStart", (x, y, msg) => Assert.AreEqual(x, y, msg));
            AssertArrayEqual(a.WorldCount, b.WorldCount, "WorldCount", (x, y, msg) => Assert.AreEqual(x, y, msg));
            AssertArrayEqual(a.RepAnchor, b.RepAnchor, "RepAnchor", AssertDouble3Equal);
            AssertArrayEqual(a.MaterialIndexes, b.MaterialIndexes, "MaterialIndexes", (x, y, msg) => Assert.AreEqual(x, y, msg));
            AssertArrayEqual(a.PairRoles, b.PairRoles, "PairRoles", (x, y, msg) => Assert.AreEqual(x, y, msg));
            AssertArrayEqual(a.Points, b.Points, "Points", AssertPointStageInputEqual);
            AssertArrayEqual(a.PointQuadStart, b.PointQuadStart, "PointQuadStart", (x, y, msg) => Assert.AreEqual(x, y, msg));
            AssertArrayEqual(a.PointQuadCount, b.PointQuadCount, "PointQuadCount", (x, y, msg) => Assert.AreEqual(x, y, msg));
            AssertArrayEqual(a.Curveds, b.Curveds, "Curveds", AssertCurvedStageInputEqual);
            AssertArrayEqual(a.CurvedGlyphStart, b.CurvedGlyphStart, "CurvedGlyphStart", (x, y, msg) => Assert.AreEqual(x, y, msg));
            AssertArrayEqual(a.CurvedGlyphCount, b.CurvedGlyphCount, "CurvedGlyphCount", (x, y, msg) => Assert.AreEqual(x, y, msg));
            AssertArrayEqual(a.CurvedAnchorStart, b.CurvedAnchorStart, "CurvedAnchorStart", (x, y, msg) => Assert.AreEqual(x, y, msg));
            AssertArrayEqual(a.CurvedAnchorCount, b.CurvedAnchorCount, "CurvedAnchorCount", (x, y, msg) => Assert.AreEqual(x, y, msg));
            AssertArrayEqual(a.CurvedAnchorFadeStart, b.CurvedAnchorFadeStart, "CurvedAnchorFadeStart", (x, y, msg) => Assert.AreEqual(x, y, msg));
            AssertArrayEqual(a.Quads, b.Quads, "Quads", AssertSymbolQuadEqual);
            AssertArrayEqual(a.Glyphs, b.Glyphs, "Glyphs", AssertCurvedGlyphEqual);
            AssertArrayEqual(a.Anchors, b.Anchors, "Anchors", AssertLineAnchorEqual);
            AssertArrayEqual(a.WorldPoints, b.WorldPoints, "WorldPoints", AssertDouble3Equal);
            AssertArrayEqual(a.WorldUps, b.WorldUps, "WorldUps", AssertFloat3Equal);
            AssertArrayEqual(a.AnchorFadeIds, b.AnchorFadeIds, "AnchorFadeIds", (x, y, msg) => Assert.AreEqual(x, y, msg));

            Assert.AreEqual(a.MaxBoxes, b.MaxBoxes, "MaxBoxes");
            Assert.AreEqual(a.MaxQuads, b.MaxQuads, "MaxQuads");
            Assert.AreEqual(a.MaxCandidates, b.MaxCandidates, "MaxCandidates");
            Assert.AreEqual(a.TileKey, b.TileKey, "TileKey");

            AssertIdColumnEquivalent(a.TextIds, b.TextIds, "TextIds");
            AssertIdColumnEquivalent(a.IconImageIds, b.IconImageIds, "IconImageIds");
        }

        // ── generic array folding (Hash side) ──

        private static int HashArray<T>(NativeArray<T> array, Func<T, int> elementHash) where T : struct
        {
            int h = Combine(17, array.Length);
            for (int i = 0; i < array.Length; i++) h = Combine(h, elementHash(array[i]));
            return h;
        }

        private static int Combine(int hash, int value) => unchecked(hash * 31 + value);

        // ── generic array element-wise compare (AssertColumnsEqual side) ──

        private static void AssertArrayEqual<T>(
            NativeArray<T> a, NativeArray<T> b, string column, Action<T, T, string> assertElement) where T : struct
        {
            Assert.AreEqual(a.Length, b.Length, $"{column} length");
            for (int i = 0; i < a.Length; i++)
                assertElement(a[i], b[i], $"{column}[{i}]");
        }

        private static void AssertIdColumnEquivalent(NativeArray<int> a, NativeArray<int> b, string column)
        {
            Assert.AreEqual(a.Length, b.Length, $"{column} length");
            // Same null-vs-real class at every index (Intern(null)==0 — a null slot and a real symbol with a
            // null field must line up between the two blocks, else the check below is vacuous there).
            for (int i = 0; i < a.Length; i++)
                Assert.AreEqual(a[i] == 0, b[i] == 0, $"{column}[{i}] null-vs-interned mismatch");
            // Equivalence class, both directions: a[i]==a[j] must hold in `a` EXACTLY when b[i]==b[j] holds
            // in `b` — the raw ids may differ (independent intern tables), the PARTITION must not.
            for (int i = 0; i < a.Length; i++)
            for (int j = 0; j < a.Length; j++)
                Assert.AreEqual(a[i] == a[j], b[i] == b[j], $"{column} equivalence mismatch at ({i},{j})");
        }

        // ── per-struct field hashers/comparers. One line per field — kept in exact sync with the type's
        // field list; SymbolTileBlockGoldenTests carries a reflection field-count guard so a field added
        // to one of these structs without a matching line here fails the gate rather than silently hashing
        // fewer fields than the type has. ──

        // 26 Combine calls == PointStageInput's 26 public fields (SymbolTileBlockGoldenTests' reflection
        // guard pins the field count so a field silently added to the struct without a matching line here
        // fails the gate instead of hashing fewer fields than the type has).
        private static int HashPointStageInput(PointStageInput p)
        {
            int h = 17;
            h = Combine(h, HashFloat2(p.ScreenPx));
            h = Combine(h, HashFloat(p.Depth));
            h = Combine(h, HashBool(p.Projected));
            h = Combine(h, HashFloat3(p.SurfaceUp));
            h = Combine(h, HashFloat2(p.BoundsMin));
            h = Combine(h, HashFloat2(p.BoundsMax));
            h = Combine(h, HashFloat(p.TextSizePx));
            h = Combine(h, HashFloat(p.PaddingPx));
            h = Combine(h, HashFloat(p.SortKey));
            h = Combine(h, p.FeatureIndex);
            h = Combine(h, HashLong(p.TileKey));
            h = Combine(h, p.Slot);
            h = Combine(h, HashFloat3(p.AnchorLocal));
            h = Combine(h, HashDouble3(p.TileOriginRender));
            h = Combine(h, HashBool(p.AllowOverlap));
            h = Combine(h, HashBool(p.IgnorePlacement));
            h = Combine(h, HashFloat2(p.TranslatePx));
            h = Combine(h, (int)p.TranslateAnchor);
            h = Combine(h, (int)p.RotationAlignment);
            h = Combine(h, HashFloat4(p.Color));
            h = Combine(h, HashLong(p.FadeId));
            h = Combine(h, HashBool(p.WasPlacedLastFrame));
            h = Combine(h, (int)p.AtlasKind);
            h = Combine(h, (int)p.PairRole);
            h = Combine(h, HashBool(p.PairOptional));
            h = Combine(h, HashFloat(p.IconRotateRadians));
            return h;
        }

        private static void AssertPointStageInputEqual(PointStageInput a, PointStageInput b, string msg)
        {
            AssertFloat2Equal(a.ScreenPx, b.ScreenPx, msg + ".ScreenPx");
            AssertFloatEqual(a.Depth, b.Depth, msg + ".Depth");
            Assert.AreEqual(a.Projected, b.Projected, msg + ".Projected");
            AssertFloat3Equal(a.SurfaceUp, b.SurfaceUp, msg + ".SurfaceUp");
            AssertFloat2Equal(a.BoundsMin, b.BoundsMin, msg + ".BoundsMin");
            AssertFloat2Equal(a.BoundsMax, b.BoundsMax, msg + ".BoundsMax");
            AssertFloatEqual(a.TextSizePx, b.TextSizePx, msg + ".TextSizePx");
            AssertFloatEqual(a.PaddingPx, b.PaddingPx, msg + ".PaddingPx");
            AssertFloatEqual(a.SortKey, b.SortKey, msg + ".SortKey");
            Assert.AreEqual(a.FeatureIndex, b.FeatureIndex, msg + ".FeatureIndex");
            Assert.AreEqual(a.TileKey, b.TileKey, msg + ".TileKey");
            Assert.AreEqual(a.Slot, b.Slot, msg + ".Slot");
            AssertFloat3Equal(a.AnchorLocal, b.AnchorLocal, msg + ".AnchorLocal");
            AssertDouble3Equal(a.TileOriginRender, b.TileOriginRender, msg + ".TileOriginRender");
            Assert.AreEqual(a.AllowOverlap, b.AllowOverlap, msg + ".AllowOverlap");
            Assert.AreEqual(a.IgnorePlacement, b.IgnorePlacement, msg + ".IgnorePlacement");
            AssertFloat2Equal(a.TranslatePx, b.TranslatePx, msg + ".TranslatePx");
            Assert.AreEqual(a.TranslateAnchor, b.TranslateAnchor, msg + ".TranslateAnchor");
            Assert.AreEqual(a.RotationAlignment, b.RotationAlignment, msg + ".RotationAlignment");
            AssertFloat4Equal(a.Color, b.Color, msg + ".Color");
            Assert.AreEqual(a.FadeId, b.FadeId, msg + ".FadeId");
            Assert.AreEqual(a.WasPlacedLastFrame, b.WasPlacedLastFrame, msg + ".WasPlacedLastFrame");
            Assert.AreEqual(a.AtlasKind, b.AtlasKind, msg + ".AtlasKind");
            Assert.AreEqual(a.PairRole, b.PairRole, msg + ".PairRole");
            Assert.AreEqual(a.PairOptional, b.PairOptional, msg + ".PairOptional");
            AssertFloatEqual(a.IconRotateRadians, b.IconRotateRadians, msg + ".IconRotateRadians");
        }

        // 18 Combine calls == CurvedStageInput's 18 public fields (same reflection-guard reasoning as
        // HashPointStageInput above).
        private static int HashCurvedStageInput(CurvedStageInput c)
        {
            int h = 17;
            h = Combine(h, HashFloat(c.TextSizePx));
            h = Combine(h, HashFloat(c.PaddingPx));
            h = Combine(h, HashFloat(c.SortKey));
            h = Combine(h, c.FeatureIndex);
            h = Combine(h, HashLong(c.TileKey));
            h = Combine(h, c.Slot);
            h = Combine(h, HashBool(c.AllowOverlap));
            h = Combine(h, HashBool(c.IgnorePlacement));
            h = Combine(h, HashFloat2(c.TranslatePx));
            h = Combine(h, (int)c.TranslateAnchor);
            h = Combine(h, HashFloat(c.MaxAngleDeg));
            h = Combine(h, HashBool(c.KeepUpright));
            h = Combine(h, HashFloat4(c.Color));
            h = Combine(h, HashDouble3(c.TileOriginRender));
            h = Combine(h, (int)c.AtlasKind);
            h = Combine(h, HashFloat(c.IconRotateRadians));
            h = Combine(h, (int)c.PitchAlignment);
            h = Combine(h, HashFloat(c.MetresPerLogicalPixel));
            return h;
        }

        private static void AssertCurvedStageInputEqual(CurvedStageInput a, CurvedStageInput b, string msg)
        {
            AssertFloatEqual(a.TextSizePx, b.TextSizePx, msg + ".TextSizePx");
            AssertFloatEqual(a.PaddingPx, b.PaddingPx, msg + ".PaddingPx");
            AssertFloatEqual(a.SortKey, b.SortKey, msg + ".SortKey");
            Assert.AreEqual(a.FeatureIndex, b.FeatureIndex, msg + ".FeatureIndex");
            Assert.AreEqual(a.TileKey, b.TileKey, msg + ".TileKey");
            Assert.AreEqual(a.Slot, b.Slot, msg + ".Slot");
            Assert.AreEqual(a.AllowOverlap, b.AllowOverlap, msg + ".AllowOverlap");
            Assert.AreEqual(a.IgnorePlacement, b.IgnorePlacement, msg + ".IgnorePlacement");
            AssertFloat2Equal(a.TranslatePx, b.TranslatePx, msg + ".TranslatePx");
            Assert.AreEqual(a.TranslateAnchor, b.TranslateAnchor, msg + ".TranslateAnchor");
            AssertFloatEqual(a.MaxAngleDeg, b.MaxAngleDeg, msg + ".MaxAngleDeg");
            Assert.AreEqual(a.KeepUpright, b.KeepUpright, msg + ".KeepUpright");
            AssertFloat4Equal(a.Color, b.Color, msg + ".Color");
            AssertDouble3Equal(a.TileOriginRender, b.TileOriginRender, msg + ".TileOriginRender");
            Assert.AreEqual(a.AtlasKind, b.AtlasKind, msg + ".AtlasKind");
            AssertFloatEqual(a.IconRotateRadians, b.IconRotateRadians, msg + ".IconRotateRadians");
            Assert.AreEqual(a.PitchAlignment, b.PitchAlignment, msg + ".PitchAlignment");
            AssertFloatEqual(a.MetresPerLogicalPixel, b.MetresPerLogicalPixel, msg + ".MetresPerLogicalPixel");
        }

        // 6 Combine calls == SymbolQuad's 6 public properties.
        private static int HashSymbolQuad(SymbolQuad q)
        {
            int h = 17;
            h = Combine(h, HashFloat2(q.TopLeft));
            h = Combine(h, HashFloat2(q.BottomRight));
            h = Combine(h, HashFloat2(q.UvTopLeft));
            h = Combine(h, HashFloat2(q.UvBottomRight));
            h = Combine(h, q.LineIndex);
            h = Combine(h, q.Page);
            return h;
        }

        private static void AssertSymbolQuadEqual(SymbolQuad a, SymbolQuad b, string msg)
        {
            AssertFloat2Equal(a.TopLeft, b.TopLeft, msg + ".TopLeft");
            AssertFloat2Equal(a.BottomRight, b.BottomRight, msg + ".BottomRight");
            AssertFloat2Equal(a.UvTopLeft, b.UvTopLeft, msg + ".UvTopLeft");
            AssertFloat2Equal(a.UvBottomRight, b.UvBottomRight, msg + ".UvBottomRight");
            Assert.AreEqual(a.LineIndex, b.LineIndex, msg + ".LineIndex");
            Assert.AreEqual(a.Page, b.Page, msg + ".Page");
        }

        // 3 Combine calls == CurvedGlyph's 3 public properties.
        private static int HashCurvedGlyph(CurvedGlyph g)
        {
            int h = 17;
            h = Combine(h, HashFloat(g.ArcCenter));
            h = Combine(h, HashSymbolQuad(g.Cell));
            h = Combine(h, HashFloat(g.CellSkirt));
            return h;
        }

        private static void AssertCurvedGlyphEqual(CurvedGlyph a, CurvedGlyph b, string msg)
        {
            AssertFloatEqual(a.ArcCenter, b.ArcCenter, msg + ".ArcCenter");
            AssertSymbolQuadEqual(a.Cell, b.Cell, msg + ".Cell");
            AssertFloatEqual(a.CellSkirt, b.CellSkirt, msg + ".CellSkirt");
        }

        // 2 Combine calls == LineAnchor's 2 public fields.
        private static int HashLineAnchor(LineAnchor l) => Combine(Combine(17, l.Segment), HashFloat(l.T));

        private static void AssertLineAnchorEqual(LineAnchor a, LineAnchor b, string msg)
        {
            Assert.AreEqual(a.Segment, b.Segment, msg + ".Segment");
            AssertFloatEqual(a.T, b.T, msg + ".T");
        }

        // ── bit-pattern primitives (see the type doc: byte-identity, not numeric tolerance) ──

        private static int HashFloat(float f) => (int)math.asuint(f);
        private static int HashFloat2(float2 v) => Combine(HashFloat(v.x), HashFloat(v.y));
        private static int HashFloat3(float3 v) => Combine(Combine(HashFloat(v.x), HashFloat(v.y)), HashFloat(v.z));
        private static int HashFloat4(float4 v) => Combine(Combine(HashFloat(v.x), HashFloat(v.y)), Combine(HashFloat(v.z), HashFloat(v.w)));
        private static int HashDouble(double d) { long bits = BitConverter.DoubleToInt64Bits(d); return (int)(bits ^ (bits >> 32)); }
        private static int HashDouble3(double3 v) => Combine(Combine(HashDouble(v.x), HashDouble(v.y)), HashDouble(v.z));
        private static int HashLong(long v) => (int)(v ^ (v >> 32));
        private static int HashBool(bool b) => b ? 1 : 0;

        private static void AssertFloatEqual(float a, float b, string msg) =>
            Assert.AreEqual(math.asuint(a), math.asuint(b), msg + " (bit pattern)");
        private static void AssertFloat2Equal(float2 a, float2 b, string msg)
        {
            AssertFloatEqual(a.x, b.x, msg + ".x"); AssertFloatEqual(a.y, b.y, msg + ".y");
        }
        private static void AssertFloat3Equal(float3 a, float3 b, string msg)
        {
            AssertFloatEqual(a.x, b.x, msg + ".x"); AssertFloatEqual(a.y, b.y, msg + ".y"); AssertFloatEqual(a.z, b.z, msg + ".z");
        }
        private static void AssertFloat4Equal(float4 a, float4 b, string msg)
        {
            AssertFloatEqual(a.x, b.x, msg + ".x"); AssertFloatEqual(a.y, b.y, msg + ".y");
            AssertFloatEqual(a.z, b.z, msg + ".z"); AssertFloatEqual(a.w, b.w, msg + ".w");
        }
        private static void AssertDoubleEqual(double a, double b, string msg) =>
            Assert.AreEqual(BitConverter.DoubleToInt64Bits(a), BitConverter.DoubleToInt64Bits(b), msg + " (bit pattern)");
        private static void AssertDouble3Equal(double3 a, double3 b, string msg)
        {
            AssertDoubleEqual(a.x, b.x, msg + ".x"); AssertDoubleEqual(a.y, b.y, msg + ".y"); AssertDoubleEqual(a.z, b.z, msg + ".z");
        }
    }
}
