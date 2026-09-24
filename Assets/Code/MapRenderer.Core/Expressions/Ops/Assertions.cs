using System.Collections.Generic;

namespace MapRenderer.Core.Expressions.Ops
{
    /// <summary>
    /// <c>boolean</c> / <c>number</c> / <c>string</c> / <c>object</c> type-assertion expressions (spec
    /// "Types / Assertion" section).  Distinct from the <c>to-*</c> coercions: these return the input
    /// unchanged when the type matches and throw <see cref="ExpressionEvaluationException"/> when it does
    /// not. The multi-arg form returns the first argument whose type matches. A dedicated node evaluates
    /// its sub-expressions into stack locals, so the assertion allocates no <c>Value[]</c> per call.
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
            // Single-arg form: assert and return. Multi-arg: first match. `last` reports the type in the
            // error path without re-evaluating the sub-expression.
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
    /// <c>array</c> type-assertion expression (spec "Types / Assertion" section): <c>["array", v]</c> asserts
    /// any array; <c>["array", type, v]</c> also asserts every element is "boolean" | "number" | "string";
    /// <c>["array", type, N, v]</c> also asserts exactly N elements. <c>type</c> and <c>N</c> are parse-time
    /// literals; <c>v</c> is evaluated at runtime into a stack local, with no per-call allocation.
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
