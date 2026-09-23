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
    /// inside the VM's fast-path subset (the <c>TryEmit*</c> methods are the accepted shapes); anything
    /// else — or a program over the VM's op-count/stack-depth capacity — is refused and the layer stays on
    /// the managed <see cref="CompiledFilter"/> path.
    /// <para>Two non-obvious facts: it compiles from the normalised expression-dialect JSON, not the parsed
    /// <see cref="Expression"/> tree — whose <c>==</c>/<c>!=</c>/<c>!</c> are indistinguishable opaque
    /// closures — and it requires a statically-Boolean root.</para>
    /// </summary>
    internal static class NativeFilterCompiler
    {
        /// <summary>The bounded op program's element capacity — read off the container type, never
        /// hardcoded, so a capacity change there can't silently drift from the refusal bound.</summary>
        private static readonly int MaxOperations = default(FixedList512Bytes<NativeFilterOperation>).Capacity;

        /// <summary>The VM's runtime operand-stack capacity (see <c>NativeFilterEvaluationJob</c>).</summary>
        private static readonly int MaxStackDepth = default(FixedList128Bytes<NativeValue>).Capacity;

        /// <summary>Caps distinct literal-string operands, bounding the binding array, the per-layer
        /// <c>Rebind</c> scan, and the per-feature <c>InStringSet</c> loop. Needed because <c>InStringSet</c>
        /// emits O(1) ops regardless of label count, so <c>MaxOperations</c> does not cap a match's label
        /// list ("always bound loops"). Far above any real style (liberty's largest match is ~15
        /// labels).</summary>
        private const int MaxLiterals = 256;

        // The expression-dialect operator/keyword heads this compiler accepts, each named once so the
        // accept-list (IsBooleanRootHead), the dispatch switch (TryEmit) and the compare-code map
        // (CompareOperatorCode) reference one literal apiece and cannot drift out of step.
        private const string OpGet = "get";
        private const string OpGeometryType = "geometry-type";
        private const string OpAll = "all";
        private const string OpNot = "!";
        private const string OpEqual = "==";
        private const string OpNotEqual = "!=";
        private const string OpMatch = "match";
        private const string OpHas = "has";
        private const string OpLess = "<";
        private const string OpLessOrEqual = "<=";
        private const string OpGreater = ">";
        private const string OpGreaterOrEqual = ">=";

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
            if (builder.Operations.Count > MaxOperations || builder.MaxDepth > MaxStackDepth) return false;
            if (builder.LiteralStrings.Count > MaxLiterals) return false; // bound the InStringSet loop / Rebind / binding

            // Fix up LiteralString/InStringSet operands: both emitted as a 0-based index into LiteralStrings
            // (InStringSet's is its first label's index), offset here by the final KeyNames count so both
            // tables share one binding array (see Mvt.NativeFilterRebind).
            int keyCount = builder.KeyNames.Count;
            for (int i = 0; i < builder.Operations.Count; i++)
            {
                NativeFilterOperation operation = builder.Operations[i];
                if (operation.Operation == NativeOperation.LiteralString || operation.Operation == NativeOperation.InStringSet)
                    builder.Operations[i] = new NativeFilterOperation(operation.Operation, keyCount + operation.Operand, operation.Immediate);
            }

            var operations = new FixedList512Bytes<NativeFilterOperation>();
            for (int i = 0; i < builder.Operations.Count; i++) operations.Add(builder.Operations[i]);

            program = new NativeFilterProgram(
                operations, builder.KeyNames.ToArray(), builder.LiteralStrings.ToArray());
            return true;
        }

        private static bool IsBooleanRootHead(JsonValue node)
        {
            if (!node.IsArray || node.Items.Count == 0 || node.Items[0].Kind != JsonKind.String) return false;
            string head = node.Items[0].AsString();
            return head == OpAll || head == OpEqual || head == OpNotEqual || head == OpNot || head == OpMatch
                || head == OpHas || head == OpLess || head == OpLessOrEqual || head == OpGreater || head == OpGreaterOrEqual;
        }

        // ---- recursive-descent emitter -------------------------------------------------------------

        /// <summary>Compile-time emission state: the growing op list, the constant-key/literal-string
        /// tables (rebound per tile-layer, see <c>MapRenderer.Jobs.Mvt.NativeFilterRebind.Rebind</c>), and
        /// a running operand-stack-depth simulation used to size the VM's runtime stack.</summary>
        private sealed class Builder
        {
            internal readonly List<NativeFilterOperation> Operations = new List<NativeFilterOperation>();
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
                case OpGet: return TryEmitGet(items, b);
                case OpAll: return TryEmitAll(items, b);
                case OpNot: return TryEmitNot(items, b);
                case OpEqual: return TryEmitEq(items, negate: false, b);
                case OpNotEqual: return TryEmitEq(items, negate: true, b);
                case OpMatch: return TryEmitMatch(items, b);
                case OpHas: return TryEmitHas(items, b);
                case OpLess:
                case OpLessOrEqual:
                case OpGreater:
                case OpGreaterOrEqual:
                    return TryEmitCompare(items, head, b);
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
                    b.Operations.Add(new NativeFilterOperation(NativeOperation.LiteralString, slot));
                    b.Push();
                    return true;
                case JsonKind.Number:
                    b.Operations.Add(new NativeFilterOperation(NativeOperation.LiteralNumber, immediate: node.AsDouble()));
                    b.Push();
                    return true;
                case JsonKind.Bool:
                    b.Operations.Add(new NativeFilterOperation(NativeOperation.LiteralBoolean, node.AsBool() ? 1 : 0));
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
            b.Operations.Add(new NativeFilterOperation(NativeOperation.Get, slot));
            b.Push();
            return true;
        }

        /// <summary>Emits the constant-key <c>has</c> form (mirrors <c>FeatureKeyExpression(isHas:
        /// true)</c>'s constant-key path): requires a bare-string key, same as <c>TryEmitGet</c> —
        /// the dynamic-key (<c>["has",["get","x"]]</c>) and 2-arg forms are separate managed closures and
        /// stay refused.</summary>
        private static bool TryEmitHas(IReadOnlyList<JsonValue> items, Builder b)
        {
            if (items.Count != 2 || items[1].Kind != JsonKind.String) return false;
            int slot = b.KeyNames.Count;
            b.KeyNames.Add(items[1].AsString());
            b.Operations.Add(new NativeFilterOperation(NativeOperation.Has, slot));
            b.Push();
            return true;
        }

        /// <summary>Emits an ordered comparison (<c>&lt;</c>/<c>&lt;=</c>/<c>&gt;</c>/<c>&gt;=</c>),
        /// restricted to exactly one <c>get</c> operand and one JSON number literal, for byte identity:
        /// any other shape (two <c>get</c>s, <c>get</c> vs a string, a
        /// <c>geometry-type</c> operand, or literal-vs-literal) could compare strings, whose ordinal
        /// bytes the VM's string-id representation cannot reproduce, so it stays refused (managed
        /// path).</summary>
        private static bool TryEmitCompare(IReadOnlyList<JsonValue> items, string comparisonOperator, Builder b)
        {
            if (items.Count != 3) return false;
            JsonValue a = items[1], c = items[2];

            bool aIsGet = IsGetNode(a);
            bool cIsGet = IsGetNode(c);
            bool aIsNumberLiteral = a.Kind == JsonKind.Number;
            bool cIsNumberLiteral = c.Kind == JsonKind.Number;
            if (!((aIsGet && cIsNumberLiteral) || (aIsNumberLiteral && cIsGet))) return false;

            if (!TryEmit(a, b)) return false;
            if (!TryEmit(c, b)) return false;
            b.Operations.Add(new NativeFilterOperation(NativeOperation.Compare, CompareOperatorCode(comparisonOperator)));
            b.Pop(2);
            b.Push();
            return true;
        }

        /// <summary>The operator code a <c>Compare</c> op carries in its <c>Operand</c> — see
        /// <see cref="NativeFilterOperation"/>'s summary.</summary>
        private static int CompareOperatorCode(string comparisonOperator)
        {
            switch (comparisonOperator)
            {
                case OpLess: return 0;
                case OpLessOrEqual: return 1;
                case OpGreater: return 2;
                default: return 3; // OpGreaterOrEqual
            }
        }

        /// <summary>Emits <c>all</c>'s short-circuit form. Every arg's <c>AllStep</c>
        /// shares one landing site, back-patched here once the final index is known.</summary>
        private static bool TryEmitAll(IReadOnlyList<JsonValue> items, Builder b)
        {
            var patchAt = new List<int>();
            for (int i = 1; i < items.Count; i++)
            {
                if (!TryEmit(items[i], b)) return false;
                patchAt.Add(b.Operations.Count);
                b.Operations.Add(new NativeFilterOperation(NativeOperation.AllStep));
                b.Pop(); // AllStep consumes the arg's boolean (fall-through net: -1)
            }
            b.Operations.Add(new NativeFilterOperation(NativeOperation.PushTrue));
            b.Push();
            int target = b.Operations.Count; // right after PushTrue — every AllStep's short-circuit landing site
            foreach (int idx in patchAt)
                b.Operations[idx] = new NativeFilterOperation(NativeOperation.AllStep, target);
            return true;
        }

        private static bool TryEmitNot(IReadOnlyList<JsonValue> items, Builder b)
        {
            if (items.Count != 2) return false;
            if (!TryEmit(items[1], b)) return false;
            b.Operations.Add(new NativeFilterOperation(NativeOperation.Not));
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
                b.Operations.Add(new NativeFilterOperation(NativeOperation.GeometryEqual, targetKind, negate ? 1.0 : 0.0));
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
            // The safe generic-Equal shape is exactly one dynamic operand (get) against one literal.
            if (!a.IsArray && !c.IsArray) return false;
            if (IsGetNode(a) && IsGetNode(c)) return false;

            if (!TryEmit(a, b)) return false;
            if (!TryEmit(c, b)) return false;
            // Negate rides in Immediate for both Equal and GeometryEqual (uniform across the equality ops);
            // Operand is free here (Equal takes its operands off the stack, not from the binding).
            b.Operations.Add(new NativeFilterOperation(NativeOperation.Equal, immediate: negate ? 1.0 : 0.0));
            b.Pop(2);
            b.Push();
            return true;
        }

        /// <summary>Emits a single-arm <c>match</c> (a membership test with complementary boolean outputs).
        /// A <c>get</c>-input, all-string-label match takes the compact <c>InStringSet</c> path; every other
        /// accepted shape rewrites to <c>!=</c>/<c>all</c>/<c>!</c> and recurses through the emitter,
        /// inheriting its refusal and rebind rules. The body's guards are what stays managed.</summary>
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

            bool member = output.AsBool();

            bool allStringLabels = true;
            foreach (JsonValue l in labels)
                if (l.Kind != JsonKind.String) { allStringLabels = false; break; }

            if (IsGetNode(input) && allStringLabels)
            {
                if (!TryEmitGet(input.Items, b)) return false;
                int firstLabel = b.LiteralStrings.Count;
                foreach (JsonValue l in labels) b.LiteralStrings.Add(l.AsString());
                b.Operations.Add(new NativeFilterOperation(NativeOperation.InStringSet, firstLabel, labels.Count));
                b.Pop();
                b.Push(); // InStringSet pops the Get's value and pushes the bool: net depth unchanged from after Get.
                if (!member) b.Operations.Add(new NativeFilterOperation(NativeOperation.Not)); // depth-neutral
                return true;
            }

            var allArgs = new List<JsonValue> { JsonValue.OfString(OpAll) };
            foreach (JsonValue l in labels)
                allArgs.Add(JsonValue.OfArray(new List<JsonValue> { JsonValue.OfString(OpNotEqual), input, l }));
            JsonValue all = JsonValue.OfArray(allArgs);

            // member (output == true): input ∈ labels ≡ !all(input != a, input != b, …)
            // otherwise:               input ∉ labels ≡  all(input != a, input != b, …)
            JsonValue rewrite = member
                ? JsonValue.OfArray(new List<JsonValue> { JsonValue.OfString(OpNot), all })
                : all;
            return TryEmit(rewrite, b);
        }

        private static bool IsGeometryTypeNode(JsonValue node)
            => node.IsArray && node.Items.Count == 1 &&
               node.Items[0].Kind == JsonKind.String && node.Items[0].AsString() == OpGeometryType;

        private static bool IsGetNode(JsonValue node)
            => node.IsArray && node.Items.Count == 2 &&
               node.Items[0].Kind == JsonKind.String && node.Items[0].AsString() == OpGet;

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
