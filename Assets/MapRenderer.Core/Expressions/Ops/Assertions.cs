using System.Collections.Generic;

namespace MapRenderer.Core.Expressions.Ops
{
    /// <summary>
    /// <c>boolean</c> / <c>number</c> / <c>string</c> / <c>object</c> type-assertion expressions (spec
    /// "Types / Assertion" section).  Distinct from the <c>to-*</c> coercions: these return the input
    /// unchanged when the type matches and throw <see cref="ExpressionEvaluationException"/> when it does
    /// not.  Multi-arg first-match form: try each argument in order and return the first one whose type
    /// matches.
    ///
    /// <b>Alloc-free per-frame design</b> — accepting the restriction that produced the plan's no-GC
    /// guarantee: instead of sharing <see cref="FunctionExpression"/> (which allocates a <c>Value[]</c>
    /// on every call), each assertion op is a dedicated node that evaluates its sub-expression(s)
    /// directly via stack-local Values.  With Constant-folded color outputs the whole path from
    /// <c>["interpolate",…,["number",["zoom"]],…]</c> is allocation-free.
    /// </summary>
    public sealed class AssertExpression : Expression
    {
        /// <summary>The spec type name this assertion enforces ("boolean"/"number"/"string"/"object").</summary>
        private readonly string _assertedTypeName;
        private readonly ValueType _assertedType;
        private readonly Expression[] _args;
        private readonly ExpressionKind _kind;

        public AssertExpression(string typeName, ValueType assertedType, Expression[] args)
        {
            _assertedTypeName = typeName;
            _assertedType = assertedType;
            _args = args;
            var k = ExpressionKind.Constant;
            foreach (var a in args)
                k = ExpressionKinds.Combine(k, a.Kind);
            _kind = k;
        }

        public override ValueType ResultType => _assertedType;
        public override ExpressionKind Kind => _kind;

        public override Value Evaluate(in EvaluationContext context)
        {
            // Single-arg form: assert and return. Multi-arg: first match.
            // We track the last evaluated value so we can report its type in the single-arg error path
            // without re-evaluating the sub-expression.
            Value last = default;
            for (int i = 0; i < _args.Length; i++)
            {
                last = _args[i].Evaluate(context);
                if (last.Type == _assertedType)
                    return last;
            }
            // All args evaluated, none matched.
            if (_args.Length == 1)
                throw new ExpressionEvaluationException(
                    $"{_assertedTypeName}: expected {_assertedTypeName} but found {ValueTypes.TypeOfName(last.Type)}.");
            throw new ExpressionEvaluationException(
                $"{_assertedTypeName}: none of the inputs matched type {_assertedTypeName}.");
        }
    }

    /// <summary>
    /// <c>array</c> type-assertion expression (spec "Types / Assertion" section).  Three forms:
    /// <list type="bullet">
    ///   <item><c>["array", v]</c> — assert v is an array of any element type / any length.</item>
    ///   <item><c>["array", type, v]</c> — assert every element is of the given primitive type
    ///         ("boolean" | "number" | "string").</item>
    ///   <item><c>["array", type, N, v]</c> — as above, and assert the array has exactly N elements.</item>
    /// </list>
    /// <c>type</c> and <c>N</c> are parse-time literals; the value <c>v</c> is evaluated at runtime.
    /// No <c>Value[]</c> allocation per call — evaluates <c>v</c> into a stack local directly.
    /// </summary>
    public sealed class ArrayAssertExpression : Expression
    {
        /// <summary>null means "any element type".</summary>
        private readonly ValueType? _elementType;
        /// <summary>-1 means "any length".</summary>
        private readonly int _expectedLength;
        private readonly Expression _valueArg;
        private readonly ExpressionKind _kind;

        public ArrayAssertExpression(ValueType? elementType, int expectedLength, Expression valueArg)
        {
            _elementType = elementType;
            _expectedLength = expectedLength;
            _valueArg = valueArg;
            _kind = valueArg.Kind;
        }

        public override ValueType ResultType => ValueType.Array;
        public override ExpressionKind Kind => _kind;

        public override Value Evaluate(in EvaluationContext context)
        {
            Value v = _valueArg.Evaluate(context);
            if (v.Type != ValueType.Array)
                throw new ExpressionEvaluationException(
                    $"array: expected array but found {ValueTypes.TypeOfName(v.Type)}.");

            IReadOnlyList<Value> items = v.AsArray();

            if (_expectedLength >= 0 && items.Count != _expectedLength)
                throw new ExpressionEvaluationException(
                    $"array: expected array of length {_expectedLength} but found length {items.Count}.");

            if (_elementType.HasValue)
            {
                for (int i = 0; i < items.Count; i++)
                {
                    if (items[i].Type != _elementType.Value)
                        throw new ExpressionEvaluationException(
                            $"array: element {i} expected {ValueTypes.TypeOfName(_elementType.Value)} " +
                            $"but found {ValueTypes.TypeOfName(items[i].Type)}.");
                }
            }

            return v;
        }
    }
}
