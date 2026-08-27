using System.Collections.Generic;
using MapRenderer.Core.Expressions;
using MapRenderer.Core.Filters;
using MapRenderer.Core.Json;
using MapRenderer.Core.Tiles;
using Unity.Collections;

namespace MapRenderer.Jobs.Expressions
{
    /// <summary>
    /// Compiles a style layer's raw filter JSON into a <see cref="NativeFilterProgram"/> when it falls
    /// inside the narrow fast-path op subset — <c>all</c>, <c>==</c>/<c>!=</c>, <c>!</c>, constant-key
    /// <c>get</c>, <c>geometry-type</c> (only as a direct <c>==</c>/<c>!=</c> operand against a string
    /// literal), a restricted single-arm <c>match</c> (a pure membership test — rewritten to <c>!=</c>/
    /// <c>all</c>/<c>!</c> and re-emitted, no new VM ops), and string/number/boolean literals — with a
    /// statically-Boolean root (see the design doc's §5.4: this removes the top-level
    /// <c>Coercions.ToBoolean</c> the VM cannot replicate on a string root). Anything else, or a program
    /// exceeding the VM's bounded op-count/stack-depth capacity, is refused: the layer stays on the managed
    /// <see cref="CompiledFilter"/> path, unchanged.
    ///
    /// Consumes the same normalised expression-dialect JSON <see cref="CompiledFilter.Compile"/> feeds to
    /// <see cref="ExpressionParser"/> (via <see cref="FilterDialect"/>/<see cref="LegacyFilterTranslator"/>)
    /// — not the parsed <see cref="Expression"/> tree, which cannot distinguish <c>==</c>/<c>!=</c>/<c>!</c>
    /// (they are all opaque <c>FunctionExpression</c> closures; see the design doc §5.1).
    /// </summary>
    internal static class NativeFilterCompiler
    {
        /// <summary>The bounded op program's element capacity — read off the container type, never
        /// hardcoded, so a capacity change there can't silently drift from the refusal bound.</summary>
        private static readonly int MaxOps = default(FixedList512Bytes<NativeFilterOp>).Capacity;

        /// <summary>The VM's runtime operand-stack capacity (see <see cref="NativeFilterEvalJob"/>).</summary>
        private static readonly int MaxStackDepth = default(FixedList128Bytes<NativeValue>).Capacity;

        /// <summary>
        /// Attempts to compile <paramref name="rawFilter"/> (a style layer's raw <c>filter</c> JSON, as
        /// <c>StyleLayer.Filter</c> carries it — pre-normalisation). Returns false for anything outside the
        /// accepted subset (see the type summary); <paramref name="program"/> is null on refusal.
        /// </summary>
        internal static bool TryCompile(JsonValue rawFilter, out NativeFilterProgram program)
        {
            program = null;
            if (rawFilter == null || rawFilter.IsNull || !rawFilter.IsArray) return false;

            JsonValue normalised;
            try
            {
                normalised = FilterDialect.IsExpressionFilter(rawFilter)
                    ? rawFilter
                    : LegacyFilterTranslator.Translate(rawFilter);
            }
            catch (ExpressionParseException)
            {
                return false;
            }

            if (!IsBooleanRootHead(normalised)) return false;

            var builder = new Builder();
            if (!TryEmit(normalised, builder)) return false;
            if (builder.Ops.Count > MaxOps || builder.MaxDepth > MaxStackDepth) return false;

            // Fix up LitStr operands: emitted as a 0-based index into LiteralStrings, offset here by the
            // final KeyNames count so both tables share one binding array (see Mvt.NativeFilterRebind).
            int keyCount = builder.KeyNames.Count;
            for (int i = 0; i < builder.Ops.Count; i++)
            {
                NativeFilterOp op = builder.Ops[i];
                if (op.Op == NativeOp.LitStr)
                    builder.Ops[i] = new NativeFilterOp(NativeOp.LitStr, keyCount + op.Operand, op.Immediate);
            }

            var ops = new FixedList512Bytes<NativeFilterOp>();
            for (int i = 0; i < builder.Ops.Count; i++) ops.Add(builder.Ops[i]);

            program = new NativeFilterProgram(
                ops, builder.KeyNames.ToArray(), builder.LiteralStrings.ToArray());
            return true;
        }

        private static bool IsBooleanRootHead(JsonValue node)
        {
            if (!node.IsArray || node.Items.Count == 0 || node.Items[0].Kind != JsonKind.String) return false;
            string head = node.Items[0].AsString();
            return head == "all" || head == "==" || head == "!=" || head == "!" || head == "match";
        }

        // ---- recursive-descent emitter -------------------------------------------------------------

        /// <summary>Compile-time emission state: the growing op list, the constant-key/literal-string
        /// tables (rebound per tile-layer, see <c>MapRenderer.Jobs.Mvt.NativeFilterRebind.Rebind</c>), and
        /// a running operand-stack-depth simulation used to size the VM's runtime stack.</summary>
        private sealed class Builder
        {
            internal readonly List<NativeFilterOp> Ops = new List<NativeFilterOp>();
            internal readonly List<string> KeyNames = new List<string>();
            internal readonly List<string> LiteralStrings = new List<string>();
            internal int Depth;
            internal int MaxDepth;

            internal void Push()
            {
                Depth++;
                if (Depth > MaxDepth) MaxDepth = Depth;
            }

            internal void Pop(int n = 1) => Depth -= n;
        }

        private static bool TryEmit(JsonValue node, Builder b)
        {
            if (!node.IsArray)
                return TryEmitLiteral(node, b);

            var items = node.Items;
            if (items.Count == 0 || items[0].Kind != JsonKind.String) return false;
            string head = items[0].AsString();

            switch (head)
            {
                case "get": return TryEmitGet(items, b);
                case "all": return TryEmitAll(items, b);
                case "!": return TryEmitNot(items, b);
                case "==": return TryEmitEq(items, negate: false, b);
                case "!=": return TryEmitEq(items, negate: true, b);
                case "match": return TryEmitMatch(items, b);
                default: return false; // every other op, including a bare "geometry-type", refuses.
            }
        }

        private static bool TryEmitLiteral(JsonValue node, Builder b)
        {
            switch (node.Kind)
            {
                case JsonKind.String:
                    int slot = b.LiteralStrings.Count;
                    b.LiteralStrings.Add(node.AsString());
                    b.Ops.Add(new NativeFilterOp(NativeOp.LitStr, slot));
                    b.Push();
                    return true;
                case JsonKind.Number:
                    b.Ops.Add(new NativeFilterOp(NativeOp.LitNum, immediate: node.AsDouble()));
                    b.Push();
                    return true;
                case JsonKind.Bool:
                    b.Ops.Add(new NativeFilterOp(NativeOp.LitBool, node.AsBool() ? 1 : 0));
                    b.Push();
                    return true;
                default:
                    return false; // null / bare object / bare array: not in the accepted literal set.
            }
        }

        private static bool TryEmitGet(IReadOnlyList<JsonValue> items, Builder b)
        {
            if (items.Count != 2 || items[1].Kind != JsonKind.String) return false;
            int slot = b.KeyNames.Count;
            b.KeyNames.Add(items[1].AsString());
            b.Ops.Add(new NativeFilterOp(NativeOp.Get, slot));
            b.Push();
            return true;
        }

        /// <summary>Emits <c>all</c>'s real short-circuit form (design doc §5.8): each arg, then an
        /// <see cref="NativeOp.AllStep"/> that pops it, error-checks it, and on false jumps past the
        /// trailing <see cref="NativeOp.PushTrue"/>; falling off the end (every arg true) reaches
        /// <c>PushTrue</c>. Every arg's <c>AllStep</c> shares the same landing site — patched here once the
        /// final index is known.</summary>
        private static bool TryEmitAll(IReadOnlyList<JsonValue> items, Builder b)
        {
            var patchAt = new List<int>();
            for (int i = 1; i < items.Count; i++)
            {
                if (!TryEmit(items[i], b)) return false;
                patchAt.Add(b.Ops.Count);
                b.Ops.Add(new NativeFilterOp(NativeOp.AllStep));
                b.Pop(); // AllStep consumes the arg's boolean (fall-through net: -1)
            }
            b.Ops.Add(new NativeFilterOp(NativeOp.PushTrue));
            b.Push();
            int target = b.Ops.Count; // right after PushTrue — every AllStep's short-circuit landing site
            foreach (int idx in patchAt)
                b.Ops[idx] = new NativeFilterOp(NativeOp.AllStep, target);
            return true;
        }

        private static bool TryEmitNot(IReadOnlyList<JsonValue> items, Builder b)
        {
            if (items.Count != 2) return false;
            if (!TryEmit(items[1], b)) return false;
            b.Ops.Add(new NativeFilterOp(NativeOp.Not));
            // pop 1, push 1: net zero depth change.
            return true;
        }

        private static bool TryEmitEq(IReadOnlyList<JsonValue> items, bool negate, Builder b)
        {
            if (items.Count != 3) return false;
            JsonValue a = items[1], c = items[2];
            bool aGeom = IsGeometryTypeNode(a);
            bool cGeom = IsGeometryTypeNode(c);

            if (aGeom || cGeom)
            {
                JsonValue other = aGeom ? c : a;
                if (other.Kind != JsonKind.String) return false; // must pair with a string literal
                int targetKind = ResolveGeometryKind(other.AsString());
                b.Ops.Add(new NativeFilterOp(NativeOp.GeomEq, targetKind, negate ? 1.0 : 0.0));
                b.Push();
                return true;
            }

            // Refuse the two shapes where string equality by value-string ID is NOT byte-identical to
            // managed Value.Equals — both accepted by everything above but unsound, and BOTH absent from
            // every covered filter (all 44 are get/geometry-type vs a string literal), so refusing them
            // costs zero coverage:
            //  - literal == literal: two DIFFERENT string literals both absent from a layer's ValueStrings
            //    each rebind to the -1 never-equal sentinel (NativeFilterRebind), so the VM reports them
            //    EQUAL while managed compares bytes and reports unequal.
            //  - get == get: two string columns holding equal bytes at DISTINCT value-string ids (the
            //    decoder appends value strings without dedup) compare UNEQUAL by id while managed compares
            //    bytes and reports equal. Rebind's duplicate-refusal guards literal-vs-column only, never
            //    column-vs-column.
            // The safe generic-Eq shape is exactly one dynamic operand (get) against one literal.
            if (!a.IsArray && !c.IsArray) return false;
            if (IsGetNode(a) && IsGetNode(c)) return false;

            if (!TryEmit(a, b)) return false;
            if (!TryEmit(c, b)) return false;
            // Negate rides in Immediate for both Eq and GeomEq (uniform across the equality ops); Operand
            // is free here (Eq takes its operands off the stack, not from the binding).
            b.Ops.Add(new NativeFilterOp(NativeOp.Eq, immediate: negate ? 1.0 : 0.0));
            b.Pop(2);
            b.Push();
            return true;
        }

        /// <summary>
        /// Emits liberty.json's <c>match</c> shape — a single arm with a list (or scalar) label and
        /// complementary boolean outputs, which is exactly a membership test — by rewriting it to the
        /// existing <c>!=</c>/<c>all</c>/<c>!</c> ops and recursing through <see cref="TryEmit"/>, rather
        /// than hand-emitting: this keeps one emission path and inherits every refusal/rebind rule
        /// (design doc's match-widening note). Refuses (returns false, leaving the layer on the managed
        /// path) anything outside that restricted shape: a multi-arm match, a non-boolean or
        /// non-complementary output pair, an input that isn't <c>get</c>/<c>geometry-type</c>, or an empty
        /// label list / a label that isn't a string or number.
        /// </summary>
        private static bool TryEmitMatch(IReadOnlyList<JsonValue> items, Builder b)
        {
            // ["match", input, label, output, default] — exactly one arm; a multi-arm match stays managed.
            if (items.Count != 5) return false;
            JsonValue input = items[1], label = items[2], output = items[3], fallback = items[4];

            if (!IsGetNode(input) && !IsGeometryTypeNode(input)) return false; // computed input stays managed
            if (output.Kind != JsonKind.Bool || fallback.Kind != JsonKind.Bool) return false;
            if (output.AsBool() == fallback.AsBool()) return false; // must be complementary

            IReadOnlyList<JsonValue> labels = label.IsArray
                ? label.Items
                : (IReadOnlyList<JsonValue>)new[] { label };
            if (labels.Count == 0) return false;
            foreach (JsonValue l in labels)
                if (l.Kind != JsonKind.String && l.Kind != JsonKind.Number) return false;

            var allArgs = new List<JsonValue> { JsonValue.OfString("all") };
            foreach (JsonValue l in labels)
                allArgs.Add(JsonValue.OfArray(new List<JsonValue> { JsonValue.OfString("!="), input, l }));
            JsonValue all = JsonValue.OfArray(allArgs);

            // member (output == true): input ∈ labels ≡ !all(input != a, input != b, …)
            // otherwise:               input ∉ labels ≡  all(input != a, input != b, …)
            bool member = output.AsBool();
            JsonValue rewrite = member
                ? JsonValue.OfArray(new List<JsonValue> { JsonValue.OfString("!"), all })
                : all;
            return TryEmit(rewrite, b);
        }

        private static bool IsGeometryTypeNode(JsonValue node)
            => node.IsArray && node.Items.Count == 1 &&
               node.Items[0].Kind == JsonKind.String && node.Items[0].AsString() == "geometry-type";

        private static bool IsGetNode(JsonValue node)
            => node.IsArray && node.Items.Count == 2 &&
               node.Items[0].Kind == JsonKind.String && node.Items[0].AsString() == "get";

        /// <summary>Inverts <c>FeatureData.GeometryTypeName</c> at compile time. A name outside its four
        /// outputs (Point/LineString/Polygon/Unknown) resolves to -1 — no <c>TileGeometryType</c> value is
        /// negative, so the comparison is statically false, byte-identical to managed comparing an unknown
        /// string.</summary>
        private static int ResolveGeometryKind(string name)
        {
            switch (name)
            {
                case "Point": return (int)TileGeometryType.Point;
                case "LineString": return (int)TileGeometryType.LineString;
                case "Polygon": return (int)TileGeometryType.Polygon;
                case "Unknown": return (int)TileGeometryType.Unknown;
                default: return -1;
            }
        }
    }
}
