using System;
using System.Collections.Generic;
using System.Globalization;
using MapRenderer.Core.Json;
using MapRenderer.Core.Expressions.Ops;

namespace MapRenderer.Core.Expressions
{
    /// <summary>
    /// Parses a MapLibre expression (a <see cref="JsonValue"/>: a bare literal, or an array
    /// <c>[operator, ...args]</c>) into a typed <see cref="Expression"/> tree, evaluable many times.
    /// Classification (Constant/Zoom/Feature/Composite) is computed by the nodes as the tree is built.
    ///
    /// Clean-room: operators, arities, and semantics are taken from the public MapLibre Style Spec
    /// "Expressions" page only.
    /// </summary>
    public sealed class ExpressionParser
    {
        // Parse-time scope chain for let/var: a name -> the bound (already parsed) expression.
        private sealed class Scope
        {
            public readonly Scope Parent;
            public readonly Dictionary<string, Expression> Bindings = new Dictionary<string, Expression>();
            public Scope(Scope parent) { Parent = parent; }

            public bool TryResolve(string name, out Expression e)
            {
                for (var s = this; s != null; s = s.Parent)
                    if (s.Bindings.TryGetValue(name, out e)) return true;
                e = null;
                return false;
            }
        }

        /// <summary>Parse from a JSON string (a single expression).</summary>
        public static Expression Parse(string json) => Parse(JsonParser.Parse(json));

        /// <summary>Parse a JSON DOM node as an expression.</summary>
        public static Expression Parse(JsonValue json)
            => new ExpressionParser().ParseNode(json, new Scope(null), zoomAllowed: false);

        // --------------------------------------------------------------------------------------------

        private Expression ParseNode(JsonValue node, Scope scope, bool zoomAllowed)
        {
            if (node == null) return new LiteralExpression(Value.Null);

            switch (node.Kind)
            {
                case JsonKind.Null:
                    return new LiteralExpression(Value.Null);
                case JsonKind.Bool:
                    return new LiteralExpression(Value.Bool(node.AsBool()));
                case JsonKind.Number:
                    return new LiteralExpression(Value.Number(node.AsDouble()));
                case JsonKind.String:
                    return new LiteralExpression(Value.String(node.AsString()));
                case JsonKind.Object:
                    // MapLibre Style Spec v7 "legacy stops" format:
                    // { "stops": [[z0,v0],[z1,v1],...], "base": b } where each stop pair is [zoom, value].
                    // Equivalent modern expression: ["interpolate",["exponential",b],["zoom"],z0,v0,...].
                    // The parser converts this to an InterpolateExpression so the kind is correctly Zoom.
                    if (node.TryGet("stops", out var stopsNode) && stopsNode.IsArray && stopsNode.Items.Count >= 2)
                        return ParseLegacyStopsObject(node, stopsNode, scope);
                    // A bare object without "stops" is a literal object value.
                    return new LiteralExpression(JsonToValue(node));
                case JsonKind.Array:
                    return ParseArray(node, scope, zoomAllowed);
                default:
                    return new LiteralExpression(Value.Null);
            }
        }

        private Expression ParseArray(JsonValue node, Scope scope, bool zoomAllowed)
        {
            var items = node.Items;
            if (items.Count == 0)
                throw new ExpressionParseException("Empty expression array.");

            // First element must be the operator string. A non-string first element means this is a literal
            // array of values (the spec permits this only via ["literal", [...]]; a bare [1,2,3] is invalid
            // as an expression), so we treat it as an error to surface mistakes.
            if (items[0].Kind != JsonKind.String)
                throw new ExpressionParseException("Expression operator must be a string.");

            string op = items[0].AsString();
            var args = new List<JsonValue>(items.Count - 1);
            for (int i = 1; i < items.Count; i++) args.Add(items[i]);

            return Dispatch(op, args, scope, zoomAllowed);
        }

        // --------------------------------------------------------------------------------------------

        private Expression Dispatch(string op, List<JsonValue> args, Scope scope, bool zoomAllowed)
        {
            switch (op)
            {
                // ---- literal / type ------------------------------------------------------------------
                case "literal": return ParseLiteral(args);
                case "typeof": return Literals.TypeOf(One(op, args, scope));
                case "to-number": return Literals.ToNumber(Many(op, args, scope, 1));
                case "to-string": return Literals.ToString(One(op, args, scope));
                case "to-boolean": return Literals.ToBoolean(One(op, args, scope));
                case "to-color": return Literals.ToColor(Many(op, args, scope, 1));
                case "to-rgba": return Literals.ToRgba(One(op, args, scope));

                // ---- lookup --------------------------------------------------------------------------
                case "get": return ParseGet(args, scope);
                case "has": return ParseHas(args, scope);
                case "at": return Lookup.At(Two(op, args, scope));
                case "in": return Lookup.In(Two(op, args, scope));
                case "length": return Lookup.Length(One(op, args, scope));

                // ---- decision ------------------------------------------------------------------------
                case "case": return ParseCase(args, scope, zoomAllowed);
                case "match": return ParseMatch(args, scope, zoomAllowed);
                case "coalesce": return new CoalesceExpression(ParseAll(args, scope, zoomAllowed));
                case "==": return Decision.Eq(Two(op, args, scope), false);
                case "!=": return Decision.Eq(Two(op, args, scope), true);
                case "<": return Decision.Compare(op, Two(op, args, scope));
                case "<=": return Decision.Compare(op, Two(op, args, scope));
                case ">": return Decision.Compare(op, Two(op, args, scope));
                case ">=": return Decision.Compare(op, Two(op, args, scope));
                case "all": return new AllExpression(ParseAll(args, scope, zoomAllowed));
                case "any": return new AnyExpression(ParseAll(args, scope, zoomAllowed));
                case "!": return Decision.Not(One(op, args, scope));

                // ---- ramps / curves ------------------------------------------------------------------
                case "step": return ParseStep(args, scope);
                case "interpolate": return ParseInterpolate(InterpolationSpace.Default, args, scope);
                case "interpolate-lab": return ParseInterpolate(InterpolationSpace.Lab, args, scope);
                case "interpolate-hcl": return ParseInterpolate(InterpolationSpace.Hcl, args, scope);

                // ---- math ----------------------------------------------------------------------------
                case "+": case "*": return MathOps.Variadic(op, ParseAll(args, scope, zoomAllowed));
                case "-": return MathOps.Subtract(Many(op, args, scope, 1, 2));
                case "/": case "%": case "^":
                    return MathOps.Binary(op, Two(op, args, scope));
                case "abs": case "ceil": case "floor": case "round":
                case "sqrt": case "sin": case "cos": case "tan":
                case "asin": case "acos": case "atan":
                case "ln": case "log10": case "log2":
                    return MathOps.Unary(op, One(op, args, scope));
                case "min": case "max":
                    return MathOps.MinMax(op, ParseAll(args, scope, zoomAllowed));
                case "e": case "pi": case "ln2":
                    return MathOps.Constant(op, args);

                // ---- color constructors --------------------------------------------------------------
                case "rgb": return ColorCtors.Rgb(Many(op, args, scope, 3, 3));
                case "rgba": return ColorCtors.Rgba(Many(op, args, scope, 4, 4));

                // ---- feature data --------------------------------------------------------------------
                case "properties": return FeatureData.Properties(args);
                case "geometry-type": return FeatureData.GeometryType(args);
                case "id": return FeatureData.Id(args);

                // ---- type assertions (assert-and-return; distinct from to-* coercions) --------------
                // These must thread the caller's zoomAllowed flag to their value arg so that
                // ["number",["zoom"]] is legal as a ramp input (zoomAllowed=true from ParseInputAllowingZoom).
                case "boolean": return ParseAssert("boolean", ValueType.Boolean, args, scope, zoomAllowed);
                case "number":  return ParseAssert("number",  ValueType.Number,  args, scope, zoomAllowed);
                case "string":  return ParseAssert("string",  ValueType.String,  args, scope, zoomAllowed);
                case "object":  return ParseAssert("object",  ValueType.Object,  args, scope, zoomAllowed);
                case "array":   return ParseArrayAssert(args, scope, zoomAllowed);

                // ---- zoom ----------------------------------------------------------------------------
                case "zoom": return ParseZoom(args, zoomAllowed);

                // ---- string --------------------------------------------------------------------------
                case "concat": return Strings.Concat(ParseAll(args, scope, zoomAllowed));
                case "upcase": return Strings.Upcase(One(op, args, scope));
                case "downcase": return Strings.Downcase(One(op, args, scope));

                // ---- variable binding ----------------------------------------------------------------
                case "let": return ParseLet(args, scope, zoomAllowed);
                case "var": return ParseVar(args, scope);

                default:
                    throw new ExpressionParseException($"Unknown expression operator \"{op}\".");
            }
        }

        // ---- argument helpers ----------------------------------------------------------------------

        private Expression[] ParseAll(List<JsonValue> args, Scope scope, bool zoomAllowed)
        {
            var result = new Expression[args.Count];
            for (int i = 0; i < args.Count; i++)
                result[i] = ParseNode(args[i], scope, zoomAllowed: false);
            return result;
        }

        private Expression One(string op, List<JsonValue> args, Scope scope)
        {
            if (args.Count != 1)
                throw new ExpressionParseException($"\"{op}\" expects 1 argument, got {args.Count}.");
            return ParseNode(args[0], scope, zoomAllowed: false);
        }

        private Expression[] Two(string op, List<JsonValue> args, Scope scope)
        {
            if (args.Count != 2)
                throw new ExpressionParseException($"\"{op}\" expects 2 arguments, got {args.Count}.");
            return new[]
            {
                ParseNode(args[0], scope, zoomAllowed: false),
                ParseNode(args[1], scope, zoomAllowed: false)
            };
        }

        private Expression[] Many(string op, List<JsonValue> args, Scope scope, int min, int max = int.MaxValue)
        {
            if (args.Count < min || args.Count > max)
                throw new ExpressionParseException(
                    $"\"{op}\" expects {(max == int.MaxValue ? $"at least {min}" : $"{min}..{max}")} arguments, got {args.Count}.");
            var result = new Expression[args.Count];
            for (int i = 0; i < args.Count; i++)
                result[i] = ParseNode(args[i], scope, zoomAllowed: false);
            return result;
        }

        // ---- literal / get / has -------------------------------------------------------------------

        private Expression ParseLiteral(List<JsonValue> args)
        {
            if (args.Count != 1)
                throw new ExpressionParseException($"\"literal\" expects 1 argument, got {args.Count}.");
            return new LiteralExpression(JsonToValue(args[0]));
        }

        private Expression ParseGet(List<JsonValue> args, Scope scope)
        {
            if (args.Count == 1)
            {
                // get(key) -> feature property
                var key = ParseNode(args[0], scope, zoomAllowed: false);
                return new FeatureDataExpression((in EvaluationContext ctx) =>
                {
                    string name = EvalString(key, ctx, "get");
                    if (ctx.Feature == null)
                        throw new ExpressionEvaluationException("get: no feature in context.");
                    return ctx.Feature.TryGetProperty(name, out var v) ? v : Value.Null;
                }, ValueType.Value);
            }
            if (args.Count == 2)
            {
                // get(key, object) -> object member
                var pair = Two("get", args, scope);
                return new FunctionExpression((System.ReadOnlySpan<Value> vals, in EvaluationContext ctx) =>
                {
                    string name = vals[0].AsString();
                    var obj = vals[1].AsObject();
                    return obj.TryGetValue(name, out var v) ? v : Value.Null;
                }, pair, ValueType.Value);
            }
            throw new ExpressionParseException($"\"get\" expects 1 or 2 arguments, got {args.Count}.");
        }

        private Expression ParseHas(List<JsonValue> args, Scope scope)
        {
            if (args.Count == 1)
            {
                var key = ParseNode(args[0], scope, zoomAllowed: false);
                return new FeatureDataExpression((in EvaluationContext ctx) =>
                {
                    string name = EvalString(key, ctx, "has");
                    if (ctx.Feature == null)
                        throw new ExpressionEvaluationException("has: no feature in context.");
                    return Value.Bool(ctx.Feature.TryGetProperty(name, out _));
                }, ValueType.Boolean);
            }
            if (args.Count == 2)
            {
                var pair = Two("has", args, scope);
                return new FunctionExpression((System.ReadOnlySpan<Value> vals, in EvaluationContext ctx) =>
                {
                    string name = vals[0].AsString();
                    var obj = vals[1].AsObject();
                    return Value.Bool(obj.ContainsKey(name));
                }, pair, ValueType.Boolean);
            }
            throw new ExpressionParseException($"\"has\" expects 1 or 2 arguments, got {args.Count}.");
        }

        // ---- case / match --------------------------------------------------------------------------

        private Expression ParseCase(List<JsonValue> args, Scope scope, bool zoomAllowed)
        {
            // case: (cond, output)+ , fallback  -> odd count >= 3
            if (args.Count < 3 || args.Count % 2 == 0)
                throw new ExpressionParseException(
                    $"\"case\" expects an odd number (>=3) of arguments, got {args.Count}.");
            int pairs = (args.Count - 1) / 2;
            var conds = new Expression[pairs];
            var outs = new Expression[pairs];
            for (int i = 0; i < pairs; i++)
            {
                conds[i] = ParseNode(args[2 * i], scope, zoomAllowed: false);
                outs[i] = ParseNode(args[2 * i + 1], scope, zoomAllowed: false);
            }
            var fallback = ParseNode(args[args.Count - 1], scope, zoomAllowed: false);
            return new CaseExpression(conds, outs, fallback);
        }

        private Expression ParseMatch(List<JsonValue> args, Scope scope, bool zoomAllowed)
        {
            // match: input, (label, output)+ , default  -> count even and >= 4
            if (args.Count < 4 || args.Count % 2 != 0)
                throw new ExpressionParseException(
                    $"\"match\" expects input, label/output pairs, and a default (even count >=4), got {args.Count}.");
            var input = ParseNode(args[0], scope, zoomAllowed: false);
            var cases = new List<(Value[], Expression)>();
            for (int i = 1; i < args.Count - 1; i += 2)
            {
                var labelNode = args[i];
                Value[] labels;
                if (labelNode.IsArray)
                {
                    labels = new Value[labelNode.Items.Count];
                    for (int j = 0; j < labelNode.Items.Count; j++)
                        labels[j] = LiteralLabel(labelNode.Items[j]);
                }
                else
                {
                    labels = new[] { LiteralLabel(labelNode) };
                }
                var output = ParseNode(args[i + 1], scope, zoomAllowed: false);
                cases.Add((labels, output));
            }
            var def = ParseNode(args[args.Count - 1], scope, zoomAllowed: false);
            return new MatchExpression(input, cases, def);
        }

        private static Value LiteralLabel(JsonValue node)
        {
            switch (node.Kind)
            {
                case JsonKind.Number: return Value.Number(node.AsDouble());
                case JsonKind.String: return Value.String(node.AsString());
                default:
                    throw new ExpressionParseException("\"match\" labels must be numbers or strings.");
            }
        }

        // ---- step / interpolate --------------------------------------------------------------------

        private Expression ParseStep(List<JsonValue> args, Scope scope)
        {
            // step: input, default-output, (stop-input, stop-output)+
            if (args.Count < 4 || args.Count % 2 != 0)
                throw new ExpressionParseException(
                    $"\"step\" expects input, default, and stop pairs (even count >=4), got {args.Count}.");
            var input = ParseInputAllowingZoom(args[0], scope);
            var def = FoldConstant(ParseNode(args[1], scope, zoomAllowed: false));
            int n = (args.Count - 2) / 2;
            var stops = new double[n];
            var outs = new Expression[n];
            double prev = double.NegativeInfinity;
            for (int i = 0; i < n; i++)
            {
                var stopNode = args[2 + 2 * i];
                if (stopNode.Kind != JsonKind.Number)
                    throw new ExpressionParseException("\"step\" stop inputs must be literal numbers.");
                double stop = stopNode.AsDouble();
                if (stop <= prev)
                    throw new ExpressionParseException("\"step\" stop inputs must be strictly ascending.");
                prev = stop;
                stops[i] = stop;
                outs[i] = FoldConstant(ParseNode(args[2 + 2 * i + 1], scope, zoomAllowed: false));
            }
            return new StepExpression(input, def, stops, outs);
        }

        private Expression ParseInterpolate(InterpolationSpace space, List<JsonValue> args, Scope scope)
        {
            // interpolate args (operator excluded): interpolation, input, (stop, output)+
            // => even count >= 6 (interpolation + input + at least two stop/output pairs).
            if (args.Count < 6 || args.Count % 2 != 0)
                throw new ExpressionParseException(
                    $"\"interpolate\" expects interpolation, input, and at least 2 stop pairs (even count >=6), got {args.Count}.");

            var (curve, baseV, p1x, p1y, p2x, p2y) = ParseInterpolation(args[0]);
            var input = ParseInputAllowingZoom(args[1], scope);

            int n = (args.Count - 2) / 2;
            var stops = new double[n];
            var outs = new Expression[n];
            double prev = double.NegativeInfinity;
            for (int i = 0; i < n; i++)
            {
                var stopNode = args[2 + 2 * i];
                if (stopNode.Kind != JsonKind.Number)
                    throw new ExpressionParseException("\"interpolate\" stop inputs must be literal numbers.");
                double stop = stopNode.AsDouble();
                if (stop <= prev)
                    throw new ExpressionParseException("\"interpolate\" stop inputs must be strictly ascending.");
                prev = stop;
                stops[i] = stop;
                outs[i] = FoldConstant(ParseNode(args[2 + 2 * i + 1], scope, zoomAllowed: false));
            }
            return new InterpolateExpression(curve, space, baseV, p1x, p1y, p2x, p2y, input, stops, outs);
        }

        private static (InterpolationKind, double, double, double, double, double) ParseInterpolation(JsonValue node)
        {
            if (!node.IsArray || node.Items.Count == 0 || node.Items[0].Kind != JsonKind.String)
                throw new ExpressionParseException("\"interpolate\" interpolation type must be [\"linear\"|\"exponential\"|\"cubic-bezier\", ...].");
            string kind = node.Items[0].AsString();
            switch (kind)
            {
                case "linear":
                    return (InterpolationKind.Linear, 1.0, 0, 0, 0, 0);
                case "exponential":
                    if (node.Items.Count != 2 || node.Items[1].Kind != JsonKind.Number)
                        throw new ExpressionParseException("\"exponential\" interpolation needs a numeric base.");
                    return (InterpolationKind.Exponential, node.Items[1].AsDouble(), 0, 0, 0, 0);
                case "cubic-bezier":
                    if (node.Items.Count != 5)
                        throw new ExpressionParseException("\"cubic-bezier\" interpolation needs 4 control values.");
                    return (InterpolationKind.CubicBezier, 1.0,
                        node.Items[1].AsDouble(), node.Items[2].AsDouble(),
                        node.Items[3].AsDouble(), node.Items[4].AsDouble());
                default:
                    throw new ExpressionParseException($"Unknown interpolation type \"{kind}\".");
            }
        }

        // The input of step/interpolate is the only place a top-level "zoom" leaf is allowed.
        private Expression ParseInputAllowingZoom(JsonValue node, Scope scope)
            => ParseNode(node, scope, zoomAllowed: true);

        // ---- zoom ----------------------------------------------------------------------------------

        private Expression ParseZoom(List<JsonValue> args, bool zoomAllowed)
        {
            if (args.Count != 0)
                throw new ExpressionParseException($"\"zoom\" expects 0 arguments, got {args.Count}.");
            if (!zoomAllowed)
                throw new ExpressionParseException(
                    "\"zoom\" is only valid as the input of a top-level \"step\" or \"interpolate\".");
            return new ZoomExpression();
        }

        // ---- let / var -----------------------------------------------------------------------------

        private Expression ParseLet(List<JsonValue> args, Scope scope, bool zoomAllowed)
        {
            // let: (name, value)+ , body  -> odd count >= 3
            if (args.Count < 3 || args.Count % 2 == 0)
                throw new ExpressionParseException(
                    $"\"let\" expects name/value pairs and a body (odd count >=3), got {args.Count}.");
            var child = new Scope(scope);
            int pairs = (args.Count - 1) / 2;
            var bindings = new (string, Expression)[pairs];
            for (int i = 0; i < pairs; i++)
            {
                var nameNode = args[2 * i];
                if (nameNode.Kind != JsonKind.String)
                    throw new ExpressionParseException("\"let\" binding names must be strings.");
                string name = nameNode.AsString();
                // Bindings may reference earlier bindings in the same let (sequential scope).
                var valueExpr = ParseNode(args[2 * i + 1], child, zoomAllowed: false);
                child.Bindings[name] = valueExpr;
                bindings[i] = (name, valueExpr);
            }
            var body = ParseNode(args[args.Count - 1], child, zoomAllowed: false);
            return new LetExpression(bindings, body);
        }

        private Expression ParseVar(List<JsonValue> args, Scope scope)
        {
            if (args.Count != 1 || args[0].Kind != JsonKind.String)
                throw new ExpressionParseException("\"var\" expects a single string name.");
            string name = args[0].AsString();
            if (!scope.TryResolve(name, out var bound))
                throw new ExpressionParseException($"\"var\" references unbound name \"{name}\".");
            return new VarExpression(name, bound);
        }

        // ---- JSON -> Value (for literal / match labels / object literals) --------------------------

        internal static Value JsonToValue(JsonValue node)
        {
            if (node == null) return Value.Null;
            switch (node.Kind)
            {
                case JsonKind.Null: return Value.Null;
                case JsonKind.Bool: return Value.Bool(node.AsBool());
                case JsonKind.Number: return Value.Number(node.AsDouble());
                case JsonKind.String: return Value.String(node.AsString());
                case JsonKind.Array:
                {
                    var items = new Value[node.Items.Count];
                    for (int i = 0; i < node.Items.Count; i++)
                        items[i] = JsonToValue(node.Items[i]);
                    return Value.Array(items);
                }
                case JsonKind.Object:
                {
                    var members = new Dictionary<string, Value>();
                    foreach (var kv in node.Members)
                        members[kv.Key] = JsonToValue(kv.Value);
                    return Value.Object(members);
                }
                default: return Value.Null;
            }
        }

        // ---- assertion ops -----------------------------------------------------------------------

        /// <summary>
        /// Parse <c>["boolean"|"number"|"string"|"object", arg1, ...]</c> assertion.
        /// Threads <paramref name="zoomAllowed"/> to every arg so <c>["number",["zoom"]]</c> is legal.
        /// </summary>
        private Expression ParseAssert(string typeName, ValueType assertedType,
            List<JsonValue> args, Scope scope, bool zoomAllowed)
        {
            if (args.Count < 1)
                throw new ExpressionParseException(
                    $"\"{typeName}\" expects at least 1 argument, got {args.Count}.");
            var exprs = new Expression[args.Count];
            for (int i = 0; i < args.Count; i++)
                exprs[i] = ParseNode(args[i], scope, zoomAllowed);
            return new Ops.AssertExpression(typeName, assertedType, exprs);
        }

        /// <summary>
        /// Parse the <c>array</c> assertion: <c>["array",v]</c>, <c>["array",type,v]</c>, or
        /// <c>["array",type,N,v]</c>.  The optional <c>type</c> and <c>N</c> are parse-time literals.
        /// </summary>
        private Expression ParseArrayAssert(List<JsonValue> args, Scope scope, bool zoomAllowed)
        {
            if (args.Count < 1 || args.Count > 3)
                throw new ExpressionParseException(
                    $"\"array\" expects 1–3 arguments, got {args.Count}.");

            ValueType? elementType = null;
            int expectedLength = -1;
            JsonValue valueNode;

            if (args.Count == 1)
            {
                // ["array", v]
                valueNode = args[0];
            }
            else if (args.Count == 2)
            {
                // ["array", type, v]
                elementType = ParseAssertedElementType(args[0]);
                valueNode = args[1];
            }
            else
            {
                // ["array", type, N, v]
                elementType = ParseAssertedElementType(args[0]);
                if (args[1].Kind != JsonKind.Number)
                    throw new ExpressionParseException("\"array\" length N must be a literal number.");
                expectedLength = (int)args[1].AsDouble();
                if (expectedLength < 0)
                    throw new ExpressionParseException("\"array\" length N must be non-negative.");
                valueNode = args[2];
            }

            var valueExpr = ParseNode(valueNode, scope, zoomAllowed);
            return new Ops.ArrayAssertExpression(elementType, expectedLength, valueExpr);
        }

        private static ValueType ParseAssertedElementType(JsonValue node)
        {
            if (node.Kind != JsonKind.String)
                throw new ExpressionParseException("\"array\" element type must be a string literal.");
            switch (node.AsString())
            {
                case "boolean": return ValueType.Boolean;
                case "number":  return ValueType.Number;
                case "string":  return ValueType.String;
                default:
                    throw new ExpressionParseException(
                        $"\"array\" element type must be \"boolean\", \"number\", or \"string\"; got \"{node.AsString()}\".");
            }
        }

        // ---- legacy stops-object format (MapLibre Style Spec v7 compatibility) --------------------

        /// <summary>
        /// Parses a MapLibre v7 legacy stops object <c>{ "stops": [[z0,v0],[z1,v1],...], "base": b }</c>
        /// as a modern <c>interpolate</c> / <c>step</c> expression with a <c>["zoom"]</c> input.
        ///
        /// Semantics (clean-room from the MapLibre Style Spec):
        ///   • Each stop is a [zoom, value] pair. Stops must be arrays of length ≥ 2.
        ///   • "base" (optional, default 1.0) is the exponential interpolation base:
        ///       base == 1.0 → linear interpolation (equivalent to ["interpolate",["linear"],["zoom"],...])
        ///       base != 1.0 → exponential interpolation with the given base.
        ///   • Output values may be numbers or colors (parsed via ParseNode with zoomAllowed=false).
        ///   • Invalid stops (non-number zoom key, non-ascending, &lt;2 stops) fall back to a constant null.
        /// </summary>
        private Expression ParseLegacyStopsObject(JsonValue node, JsonValue stopsArr, Scope scope)
        {
            double baseVal = node.GetDouble("base", 1.0);

            // Parse each [zoom, value] pair.
            var items = stopsArr.Items;
            int n = items.Count;

            var stopZooms  = new double[n];
            var stopOuts   = new Expression[n];
            double prev = double.NegativeInfinity;

            for (int i = 0; i < n; i++)
            {
                var pair = items[i];
                if (!pair.IsArray || pair.Items.Count < 2)
                    return new LiteralExpression(Value.Null); // malformed stop
                var zoomNode = pair.Items[0];
                if (zoomNode.Kind != JsonKind.Number)
                    return new LiteralExpression(Value.Null); // non-numeric zoom key
                double z = zoomNode.AsDouble();
                if (z <= prev)
                    return new LiteralExpression(Value.Null); // non-ascending
                prev = z;
                stopZooms[i] = z;
                stopOuts[i]  = FoldConstant(ParseNode(pair.Items[1], scope, zoomAllowed: false));
            }

            var zoom = new ZoomExpression();
            var curve = (baseVal != 1.0)
                ? InterpolationKind.Exponential
                : InterpolationKind.Linear;

            return new InterpolateExpression(
                curve, InterpolationSpace.Default,
                baseVal,
                0.0, 0.0, 0.0, 0.0,   // cubic-bezier control points (unused for linear/exponential)
                zoom, stopZooms, stopOuts);
        }

        // ---- constant-folding helper (used by step/interpolate for stop outputs) -----------------

        /// <summary>
        /// If <paramref name="expr"/> is a <see cref="ExpressionKind.Constant"/> expression, evaluate it
        /// eagerly and return a <see cref="LiteralExpression"/> wrapping the result — eliminating the
        /// per-call allocation that <see cref="FunctionExpression"/> would incur (it allocates a
        /// <c>Value[]</c> per call).  If the expression is not constant, or if it throws at evaluation
        /// (malformed stop), return <paramref name="expr"/> unchanged so errors still surface at run-time.
        /// </summary>
        private static Expression FoldConstant(Expression expr)
        {
            if (expr.Kind != ExpressionKind.Constant) return expr;
            try
            {
                Value v = expr.Evaluate(new EvaluationContext(0.0, null));
                return new LiteralExpression(v);
            }
            catch
            {
                // Keep the original — the error will surface when the expression is actually evaluated.
                return expr;
            }
        }

        // ---- string helper -----------------------------------------------------------------------

        private static string EvalString(Expression e, in EvaluationContext ctx, string op)
        {
            Value v = e.Evaluate(ctx);
            if (v.Type != ValueType.String)
                throw new ExpressionEvaluationException($"{op}: expected a string key.");
            return v.AsString();
        }
    }
}
