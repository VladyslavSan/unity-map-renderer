using System;
using System.Collections.Generic;
using MapRenderer.Core.Json;

namespace MapRenderer.Core.Expressions.Ops
{
    /// <summary>
    /// Math operators per the Style Spec "Math" section. All operands coerce to number via
    /// <see cref="Value.AsNumber"/> (a non-number is an evaluation error). Constants <c>e</c>/<c>pi</c> take
    /// no arguments.
    /// </summary>
    public static class MathOps
    {
        // + and * are variadic (identity-seeded); - and / are binary (with - also unary negation).
        public static Expression Variadic(string op, Expression[] args)
            => new FunctionExpression((Value[] vals, in EvaluationContext ctx) =>
            {
                double acc = op == "+" ? 0.0 : 1.0;
                foreach (var v in vals)
                    acc = op == "+" ? acc + v.AsNumber() : acc * v.AsNumber();
                return Value.Number(acc);
            }, args, ValueType.Number);

        public static Expression Subtract(Expression[] args)
            => new FunctionExpression((Value[] vals, in EvaluationContext ctx) =>
            {
                if (vals.Length == 1) return Value.Number(-vals[0].AsNumber());
                return Value.Number(vals[0].AsNumber() - vals[1].AsNumber());
            }, args, ValueType.Number);

        public static Expression Binary(string op, Expression[] args)
            => new FunctionExpression((Value[] vals, in EvaluationContext ctx) =>
            {
                double a = vals[0].AsNumber(), b = vals[1].AsNumber();
                switch (op)
                {
                    case "/": return Value.Number(a / b);
                    case "%": return Value.Number(a % b);
                    case "^": return Value.Number(Math.Pow(a, b));
                    default: throw new ExpressionEvaluationException($"Unknown binary math op {op}.");
                }
            }, args, ValueType.Number);

        public static Expression Unary(string op, Expression arg)
            => new FunctionExpression((Value[] vals, in EvaluationContext ctx) =>
            {
                double a = vals[0].AsNumber();
                switch (op)
                {
                    case "abs": return Value.Number(Math.Abs(a));
                    case "ceil": return Value.Number(Math.Ceiling(a));
                    case "floor": return Value.Number(Math.Floor(a));
                    case "round": return Value.Number(RoundHalfAwayFromZero(a));
                    case "sqrt": return Value.Number(Math.Sqrt(a));
                    case "sin": return Value.Number(Math.Sin(a));
                    case "cos": return Value.Number(Math.Cos(a));
                    case "tan": return Value.Number(Math.Tan(a));
                    case "asin": return Value.Number(Math.Asin(a));
                    case "acos": return Value.Number(Math.Acos(a));
                    case "atan": return Value.Number(Math.Atan(a));
                    case "ln": return Value.Number(Math.Log(a));
                    case "log10": return Value.Number(Math.Log10(a));
                    case "log2": return Value.Number(Math.Log(a, 2.0));
                    default: throw new ExpressionEvaluationException($"Unknown unary math op {op}.");
                }
            }, new[] { arg }, ValueType.Number);

        public static Expression MinMax(string op, Expression[] args)
            => new FunctionExpression((Value[] vals, in EvaluationContext ctx) =>
            {
                if (vals.Length == 0)
                    throw new ExpressionEvaluationException($"{op}: requires at least one argument.");
                double best = vals[0].AsNumber();
                for (int i = 1; i < vals.Length; i++)
                {
                    double v = vals[i].AsNumber();
                    best = op == "min" ? Math.Min(best, v) : Math.Max(best, v);
                }
                return Value.Number(best);
            }, args, ValueType.Number);

        public static Expression Constant(string op, List<JsonValue> args)
        {
            if (args.Count != 0)
                throw new ExpressionParseException($"\"{op}\" expects 0 arguments, got {args.Count}.");
            double v;
            switch (op)
            {
                case "pi": v = Math.PI; break;
                case "ln2": v = Math.Log(2.0); break;
                default: v = Math.E; break; // "e"
            }
            return new LiteralExpression(Value.Number(v));
        }

        // The spec's round() ties round half away from zero (1.5 -> 2, -1.5 -> -2), unlike .NET banker's.
        private static double RoundHalfAwayFromZero(double a)
            => Math.Sign(a) * Math.Floor(Math.Abs(a) + 0.5);
    }
}
