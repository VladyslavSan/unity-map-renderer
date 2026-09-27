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
    /// A legacy function object (<c>{"stops": …}</c>, or <c>"type":"identity"</c>) also parses here.
    ///
    /// Clean-room: operators, arities, and semantics are taken from the public MapLibre Style Spec
    /// "Expressions" page and its deprecations page (legacy <c>stops</c> functions) only.
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

        // Key layout: one entry per constant-key get/has FeatureKeyExpression, in parse order (its `slot`).
        // Only Parse(JsonValue, out) returns it; other overloads' nodes get no binding (string path).
        private readonly List<string> _keyLayout = new List<string>();

        // The Style Spec "interpolate" marker for the property being parsed: a legacy function with no
        // "type" ramps (exponential) when true, steps (interval) when false. Defaults true.
        private bool _interpolatable = true;

        /// <summary>Parse from a JSON string (a single expression).</summary>
        public static Expression Parse(string json) => Parse(JsonParser.Parse(json));

        /// <summary>Parse a JSON DOM node as an expression.</summary>
        public static Expression Parse(JsonValue json) => Parse(json, out _);

        /// <summary>Parse a JSON DOM node, naming the property's Style Spec "interpolate" marker: a legacy
        /// function with no explicit "type" ramps when true, steps when false.</summary>
        public static Expression Parse(JsonValue json, bool interpolatable) => Parse(json, interpolatable, out _);

        /// <summary>
        /// Parse a JSON DOM node as an expression, also yielding its constant-key <c>get</c>/<c>has</c>
        /// layout (<paramref name="keyLayout"/>) — the ordered key names a per-layer bind site (e.g.
        /// <c>FeatureSelector</c>) resolves once into an <see cref="EvaluationContext.KeyBinding"/> array.
        /// Empty when the expression has no constant-key <c>get</c>/<c>has</c> node.
        /// </summary>
        public static Expression Parse(JsonValue json, out IReadOnlyList<string> keyLayout)
            => Parse(json, interpolatable: true, out keyLayout);

        /// <summary>The <c>interpolatable</c> + key-layout overload every other <c>Parse</c> calls into.</summary>
        public static Expression Parse(JsonValue json, bool interpolatable, out IReadOnlyList<string> keyLayout)
        {
            var parser = new ExpressionParser { _interpolatable = interpolatable };
            var expr = parser.ParseNode(json, new Scope(null), zoomAllowed: false);
            keyLayout = parser._keyLayout;
            return expr;
        }

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
                    // A legacy function: "stops" (>=1 entry, except identity needs none), or "type":"identity".
                    // Anything else is a literal object value.
                    bool hasStops = node.TryGet("stops", out var stopsNode) && stopsNode.IsArray && stopsNode.Items.Count >= 1;
                    bool isIdentity = node.GetString("type", null) == "identity";
                    if (hasStops || isIdentity)
                        return ParseLegacyFunction(node, stopsNode, scope);
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

            // The first element must be the operator string: the spec allows a literal array only via
            // ["literal", [...]], so a bare [1,2,3] is an error.
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
                // They pass zoomAllowed to their value arg, so ["number",["zoom"]] is legal as a ramp input.
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
                // Constant-key fast form, gated on a bare JSON string: a dynamic key like ["get",["get","x"]]
                // stays on the closure below, with the same error and ordering behaviour.
                if (args[0].Kind == JsonKind.String)
                {
                    string name = args[0].AsString();
                    int slot = _keyLayout.Count;
                    _keyLayout.Add(name);
                    return new FeatureKeyExpression(name, slot, isHas: false);
                }

                // get(key) -> feature property (dynamic key form)
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
                // Constant-key fast form — see ParseGet's matching comment.
                if (args[0].Kind == JsonKind.String)
                {
                    string name = args[0].AsString();
                    int slot = _keyLayout.Count;
                    _keyLayout.Add(name);
                    return new FeatureKeyExpression(name, slot, isHas: true);
                }

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

        // ---- bare array literals ------------------------------------------------------------------

        /// <summary>
        /// Wrap a bare array (first element not an operator string) as <c>["literal", array]</c>. For an
        /// operator call, do the same to its direct arguments (one level — enough for step/interpolate
        /// stop outputs), except <c>"literal"</c> whose argument is a raw value, not an expression.
        /// Non-obvious why: array-typed properties (<c>line-dasharray</c>, …) are often written as bare
        /// arrays, which <see cref="ParseArray"/> rejects; calling this first lets them parse and classify.
        /// </summary>
        /// <param name="json">The property's raw JSON value (may itself be a bare array, an operator call,
        /// or neither — anything else passes through unchanged).</param>
        public static JsonValue WrapBareArrayLiterals(JsonValue json)
        {
            if (json == null || !json.IsArray || json.Items.Count == 0)
                return json;

            if (json.Items[0].Kind != JsonKind.String)
                return JsonValue.OfArray(new List<JsonValue>(2) { JsonValue.OfString("literal"), json });

            if (json.Items[0].AsString(null) == "literal")
                return json;

            var outItems = new List<JsonValue>(json.Items.Count) { json.Items[0] };
            for (int i = 1; i < json.Items.Count; i++)
            {
                JsonValue a = json.Items[i];
                outItems.Add(a.IsArray && a.Items.Count > 0 && a.Items[0].Kind != JsonKind.String
                    ? JsonValue.OfArray(new List<JsonValue>(2) { JsonValue.OfString("literal"), a })
                    : a);
            }
            return JsonValue.OfArray(outItems);
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

        // ---- legacy function format (MapLibre Style Spec deprecations page) ------------------------
        //
        // A legacy function is synthesised as the equivalent modern expression, as JSON, then re-parsed —
        // reusing ParseStep/ParseInterpolate/ParseCase/CoalesceExpression unchanged.

        private static readonly JsonValue NullLiteral = Op("literal", JsonValue.Null);

        private static JsonValue Op(string op, params JsonValue[] args)
        {
            var list = new List<JsonValue>(args.Length + 1) { JsonValue.OfString(op) };
            list.AddRange(args);
            return JsonValue.OfArray(list);
        }

        // Also wraps a stop OUTPUT: the spec says stop outputs are literals.
        private static JsonValue Lit(JsonValue raw) => Op("literal", raw);
        private static JsonValue Get(string property) => Op("get", JsonValue.OfString(property));

        private static JsonValue DefaultOrNull(bool hasDefault, JsonValue defaultNode)
            => hasDefault ? Lit(defaultNode) : NullLiteral;

        // "colorSpace" (rgb/lab/hcl) selects the interpolate variant; rgb (the default) is plain "interpolate".
        private static string InterpolateOp(string colorSpace)
            => colorSpace == "lab" ? "interpolate-lab" : colorSpace == "hcl" ? "interpolate-hcl" : "interpolate";

        /// <summary>
        /// Parses a legacy function object: <c>{"stops":[...], "type", "property", "default", "colorSpace",
        /// "base"}</c>, or a bare <c>"type":"identity"</c> (no stops needed). Dispatches on "type" to
        /// identity/categorical/exponential-or-interval, per the deprecations page. Malformed input (a
        /// missing property where one is required, or a non-numeric/non-ascending stop) gives constant null.
        /// </summary>
        private Expression ParseLegacyFunction(JsonValue node, JsonValue stopsNode, Scope scope)
        {
            string type = node.GetString("type", null);
            string property = node.GetString("property", null);
            bool hasDefault = node.TryGet("default", out JsonValue defaultNode);
            string colorSpace = node.GetString("colorSpace", null);
            double baseVal = node.GetDouble("base", 1.0);

            if (type == "identity")
                return property == null
                    ? (Expression)new LiteralExpression(Value.Null)
                    : ParseNode(Op("coalesce", Get(property), DefaultOrNull(hasDefault, defaultNode)), scope, zoomAllowed: false);

            IReadOnlyList<JsonValue> stops = stopsNode != null ? stopsNode.Items : System.Array.Empty<JsonValue>();
            if (stops.Count == 0)
                return new LiteralExpression(Value.Null); // no stops, and not identity: malformed
            foreach (var stop in stops)
                if (!stop.IsArray || stop.Items.Count < 2)
                    return new LiteralExpression(Value.Null); // every stop must be [input, output]

            // Zoom-and-property: the first stop's input is a {"zoom":z,"value":v} object, not a number or
            // label — checked BEFORE the "type" dispatch below, which a zoom-and-property categorical also matches.
            if (stops[0].Items[0].IsObject)
                return ParseZoomAndProperty(stops, type, property, hasDefault, defaultNode, colorSpace, baseVal, scope);

            if (type == "categorical")
                return property == null
                    ? (Expression)new LiteralExpression(Value.Null)
                    : ParseNode(BuildCategoricalNode(property, stops, hasDefault, defaultNode), scope, zoomAllowed: false);

            JsonValue synth = BuildRampNode(type, property, stops, hasDefault, defaultNode, colorSpace, baseVal, out bool malformed);
            return malformed ? new LiteralExpression(Value.Null) : ParseNode(synth, scope, zoomAllowed: false);
        }

        /// <summary>
        /// <c>["case", ["==", ["get",p], s0], out0, ..., F]</c> — spec "categorical". Labels compare by
        /// <c>==</c>, so a number/string/bool label needs no wrapping.
        /// </summary>
        private static JsonValue BuildCategoricalNode(
            string property, IReadOnlyList<JsonValue> stops, bool hasDefault, JsonValue defaultNode)
        {
            JsonValue input = Get(property);
            var args = new List<JsonValue> { JsonValue.OfString("case") };
            foreach (var stop in stops)
            {
                args.Add(Op("==", input, stop.Items[0]));
                args.Add(Lit(stop.Items[1]));
            }
            args.Add(DefaultOrNull(hasDefault, defaultNode));
            return JsonValue.OfArray(args);
        }

        /// <summary>
        /// Builds an exponential/interval ramp over <paramref name="property"/> (or <c>["zoom"]</c> when
        /// null): a single stop collapses to its literal output; otherwise <c>interpolate</c> (exponential,
        /// the "type" default when <see cref="_interpolatable"/>) or <c>step</c> (interval). A property ramp
        /// is wrapped <c>["case", ["==", ["typeof",I], "number"], ramp, F]</c>, so a non-numeric feature
        /// value takes the default instead of throwing inside interpolate/step.
        /// </summary>
        private JsonValue BuildRampNode(string type, string property, IReadOnlyList<JsonValue> stops,
            bool hasDefault, JsonValue defaultNode, string colorSpace, double baseVal, out bool malformed)
        {
            malformed = false;
            int n = stops.Count;
            var inputs = new double[n];
            double prev = double.NegativeInfinity;
            for (int i = 0; i < n; i++)
            {
                JsonValue inputNode = stops[i].Items[0];
                if (inputNode.Kind != JsonKind.Number || inputNode.AsDouble() <= prev) { malformed = true; return null; }
                prev = inputs[i] = inputNode.AsDouble();
            }

            if (n == 1) return Lit(stops[0].Items[1]); // one stop: constant, regardless of input

            JsonValue input = property != null ? Get(property) : Op("zoom");
            bool exponential = type == "exponential" || (type != "interval" && _interpolatable);
            JsonValue ramp = exponential
                ? BuildInterpolate(colorSpace, baseVal, input, stops, inputs)
                : BuildStep(input, stops, inputs);

            if (property == null) return ramp;
            JsonValue guard = Op("==", Op("typeof", input), JsonValue.OfString("number"));
            return Op("case", guard, ramp, DefaultOrNull(hasDefault, defaultNode));
        }

        private static JsonValue BuildInterpolate(
            string colorSpace, double baseVal, JsonValue input, IReadOnlyList<JsonValue> stops, double[] inputs)
        {
            string op = InterpolateOp(colorSpace);
            JsonValue curve = baseVal == 1.0
                ? Op("linear")
                : Op("exponential", JsonValue.OfNumber(baseVal));
            var args = new List<JsonValue> { JsonValue.OfString(op), curve, input };
            for (int i = 0; i < inputs.Length; i++)
            {
                args.Add(JsonValue.OfNumber(inputs[i]));
                args.Add(Lit(stops[i].Items[1]));
            }
            return JsonValue.OfArray(args);
        }

        // ["step", I, out0, s1, out1, ...]: below the first re-mapped stop input, out0 (the first legacy
        // stop's output) is the default — "returns the stop just less than the input" (deprecations page).
        private static JsonValue BuildStep(JsonValue input, IReadOnlyList<JsonValue> stops, double[] inputs)
        {
            var args = new List<JsonValue> { JsonValue.OfString("step"), input, Lit(stops[0].Items[1]) };
            for (int i = 1; i < inputs.Length; i++)
            {
                args.Add(JsonValue.OfNumber(inputs[i]));
                args.Add(Lit(stops[i].Items[1]));
            }
            return JsonValue.OfArray(args);
        }

        /// <summary>
        /// Zoom-and-property: groups stops by their <c>{"zoom":z,"value":v}</c> input, ascending, and builds
        /// each group as the property function above (categorical, or exponential/interval). The outer axis
        /// interpolates (or steps, when not <see cref="_interpolatable"/>) those results over <c>["zoom"]</c>,
        /// assuming the SAME base/colorSpace as the inner ramps — the spec does not say otherwise.
        /// </summary>
        private Expression ParseZoomAndProperty(IReadOnlyList<JsonValue> stops, string type, string property,
            bool hasDefault, JsonValue defaultNode, string colorSpace, double baseVal, Scope scope)
        {
            if (property == null) return new LiteralExpression(Value.Null);

            var groups = new SortedDictionary<double, List<JsonValue>>();
            foreach (var stop in stops)
            {
                JsonValue zoomValue = stop.Items[0];
                if (!zoomValue.IsObject) return new LiteralExpression(Value.Null);
                JsonValue zoomNode = zoomValue.Get("zoom");
                JsonValue valueNode = zoomValue.Get("value");
                if (zoomNode == null || zoomNode.Kind != JsonKind.Number || valueNode == null)
                    return new LiteralExpression(Value.Null);
                if (!groups.TryGetValue(zoomNode.AsDouble(), out var list))
                    groups[zoomNode.AsDouble()] = list = new List<JsonValue>();
                list.Add(JsonValue.OfArray(new List<JsonValue> { valueNode, stop.Items[1] }));
            }

            var zooms = new double[groups.Count];
            var inner = new JsonValue[groups.Count];
            int i = 0;
            foreach (var group in groups)
            {
                zooms[i] = group.Key;
                if (type == "categorical")
                    inner[i] = BuildCategoricalNode(property, group.Value, hasDefault, defaultNode);
                else
                {
                    inner[i] = BuildRampNode(type, property, group.Value, hasDefault, defaultNode, colorSpace, baseVal, out bool bad);
                    if (bad) return new LiteralExpression(Value.Null);
                }
                i++;
            }

            if (zooms.Length == 1) return ParseNode(inner[0], scope, zoomAllowed: false);

            string outerOp = InterpolateOp(colorSpace);
            var args = _interpolatable
                ? new List<JsonValue>
                {
                    JsonValue.OfString(outerOp),
                    baseVal == 1.0 ? Op("linear") : Op("exponential", JsonValue.OfNumber(baseVal)),
                    Op("zoom"),
                }
                : new List<JsonValue> { JsonValue.OfString("step"), Op("zoom"), inner[0] };
            int start = _interpolatable ? 0 : 1;
            for (int g = start; g < zooms.Length; g++)
            {
                args.Add(JsonValue.OfNumber(zooms[g]));
                args.Add(inner[g]);
            }
            return ParseNode(JsonValue.OfArray(args), scope, zoomAllowed: false);
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
