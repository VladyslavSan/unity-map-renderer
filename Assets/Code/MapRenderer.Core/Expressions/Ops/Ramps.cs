using Unity.Mathematics;

namespace MapRenderer.Core.Expressions.Ops
{
    /// <summary>
    /// <c>step</c>: a piecewise-constant ramp. Returns the output of the stop whose input is the greatest
    /// value &lt;= the lookup input; below the first stop it returns the default output (the first output
    /// argument). Stop inputs must be strictly ascending (validated at parse time). Spec "step".
    /// </summary>
    public sealed class StepExpression : Expression
    {
        private readonly Expression _input;
        private readonly Expression _defaultOutput;
        private readonly double[] _stops;
        private readonly Expression[] _outputs;
        private readonly ExpressionKind _kind;

        public StepExpression(Expression input, Expression defaultOutput, double[] stops, Expression[] outputs)
        {
            _input = input;
            _defaultOutput = defaultOutput;
            _stops = stops;
            _outputs = outputs;
            var k = ExpressionKinds.Combine(input.Kind, defaultOutput.Kind);
            foreach (var o in outputs) k = ExpressionKinds.Combine(k, o.Kind);
            _kind = k;
        }

        public override ValueType ResultType => ValueType.Value;
        public override ExpressionKind Kind => _kind;

        public override Value Evaluate(in EvaluationContext context)
        {
            double x = _input.Evaluate(context).AsNumber();
            if (x < _stops[0])
                return _defaultOutput.Evaluate(context);
            int idx = 0;
            for (int i = 0; i < _stops.Length; i++)
                if (x >= _stops[i]) idx = i; else break;
            return _outputs[idx].Evaluate(context);
        }
    }

    /// <summary>The interpolation curve type for <c>interpolate</c>.</summary>
    public enum InterpolationKind { Linear, Exponential, CubicBezier }

    /// <summary>The color space an <c>interpolate*</c> blends in.</summary>
    public enum InterpolationSpace { Default, Lab, Hcl }

    /// <summary>
    /// <c>interpolate</c> / <c>interpolate-hcl</c> / <c>interpolate-lab</c>: produce a value interpolated
    /// between the outputs of the two stops bracketing the input. The progress between the two stop inputs
    /// is shaped by the curve (linear / exponential base / cubic-bezier), then applied to the two outputs.
    ///
    /// Numeric formulae (clean-room, first-principles):
    ///  - linear: t = (x - lo) / (hi - lo)
    ///  - exponential(base b): t = (b^(x-lo) - 1) / (b^(hi-lo) - 1), with b==1 the linear limit
    ///  - cubic-bezier(p1,p2): unit-square easing of the linear t, with control points (p1x,p1y),(p2x,p2y)
    /// Outputs interpolate component-wise: number → lerp; color → per-channel lerp in the selected space
    /// (Default = premultiplied-alpha sRGB; Lab / Hcl = the named CIE spaces); array of numbers → element-wise lerp.
    /// </summary>
    public sealed class InterpolateExpression : Expression
    {
        private readonly InterpolationKind _curve;
        private readonly InterpolationSpace _space;
        private readonly double _base;            // exponential base (1 for linear)
        private readonly double _p1x, _p1y, _p2x, _p2y; // cubic-bezier control points
        private readonly Expression _input;
        private readonly double[] _stops;
        private readonly Expression[] _outputs;
        private readonly ExpressionKind _kind;

        public InterpolateExpression(
            InterpolationKind curve, InterpolationSpace space, double baseValue,
            double p1x, double p1y, double p2x, double p2y,
            Expression input, double[] stops, Expression[] outputs)
        {
            _curve = curve;
            _space = space;
            _base = baseValue;
            _p1x = p1x; _p1y = p1y; _p2x = p2x; _p2y = p2y;
            _input = input;
            _stops = stops;
            _outputs = outputs;
            var k = input.Kind;
            foreach (var o in outputs) k = ExpressionKinds.Combine(k, o.Kind);
            _kind = k;
        }

        public override ValueType ResultType => ValueType.Value;
        public override ExpressionKind Kind => _kind;

        public override Value Evaluate(in EvaluationContext context)
        {
            double x = _input.Evaluate(context).AsNumber();

            if (x <= _stops[0]) return _outputs[0].Evaluate(context);
            int last = _stops.Length - 1;
            if (x >= _stops[last]) return _outputs[last].Evaluate(context);

            int hi = 1;
            while (hi < _stops.Length && _stops[hi] < x) hi++;
            int lo = hi - 1;

            double loStop = _stops[lo], hiStop = _stops[hi];
            double t = Progress(x, loStop, hiStop);

            Value a = _outputs[lo].Evaluate(context);
            Value b = _outputs[hi].Evaluate(context);
            return Lerp(a, b, t);
        }

        private double Progress(double x, double lo, double hi)
        {
            if (hi == lo) return 0.0;
            double normalized;
            switch (_curve)
            {
                case InterpolationKind.Exponential:
                    if (_base == 1.0)
                        normalized = (x - lo) / (hi - lo);
                    else
                        normalized = (math.pow(_base, x - lo) - 1.0) / (math.pow(_base, hi - lo) - 1.0);
                    return normalized;
                case InterpolationKind.CubicBezier:
                    normalized = (x - lo) / (hi - lo);
                    return CubicBezier.Solve(normalized, _p1x, _p1y, _p2x, _p2y);
                case InterpolationKind.Linear:
                default:
                    return (x - lo) / (hi - lo);
            }
        }

        private Value Lerp(Value a, Value b, double t)
        {
            if (a.Type == ValueType.Number && b.Type == ValueType.Number)
                return Value.Number(a.AsNumber() + (b.AsNumber() - a.AsNumber()) * t);

            // Color interpolation — accept Color stops OR CSS color strings (production styles like
            // "liberty" use string color stops in interpolate). Number is handled above, so a numeric
            // string won't reach here; ColorParser only matches real color syntax, so arbitrary strings
            // won't be mis-coerced.
            if (Coercions.TryToColor(a, out Color ca) && Coercions.TryToColor(b, out Color cb))
                return LerpColor(ca, cb, t);

            if (a.Type == ValueType.Array && b.Type == ValueType.Array)
            {
                var array = a.AsArray();
                var barr = b.AsArray();
                if (array.Count != barr.Count)
                    throw new ExpressionEvaluationException("interpolate: array stop lengths differ.");
                var result = new Value[array.Count];
                for (int i = 0; i < array.Count; i++)
                    result[i] = Value.Number(
                        array[i].AsNumber() + (barr[i].AsNumber() - array[i].AsNumber()) * t);
                return Value.Array(result);
            }

            throw new ExpressionEvaluationException(
                $"interpolate: cannot interpolate {ValueTypes.TypeOfName(a.Type)} outputs.");
        }

        private Value LerpColor(Color a, Color b, double t)
        {
            switch (_space)
            {
                case InterpolationSpace.Lab:
                {
                    var (l1, a1, b1, al1) = a.ToLab();
                    var (l2, a2, b2, al2) = b.ToLab();
                    return Value.OfColor(Color.FromLab(
                        Lin(l1, l2, t), Lin(a1, a2, t), Lin(b1, b2, t), Lin(al1, al2, t)));
                }
                case InterpolationSpace.Hcl:
                {
                    var (h1, c1, l1, al1) = a.ToHcl();
                    var (h2, c2, l2, al2) = b.ToHcl();
                    return Value.OfColor(Color.FromHcl(
                        LerpHue(h1, h2, t), Lin(c1, c2, t), Lin(l1, l2, t), Lin(al1, al2, t)));
                }
                default:
                    // Default interpolate: premultiplied-alpha sRGB (matching MapLibre semantics).
                    // MapLibre premultiplies alpha before blending, then unpremultiplies the result.
                    // Lab/Hcl branches are left as straight-alpha interpolation (scoped to default only).
                    // Hoisted to Color.MixPremultiplied: a style-transition ease is the same arithmetic
                    // over the same two colors, and now shares this one implementation.
                    return Value.OfColor(Color.MixPremultiplied(a, b, t));
            }
        }

        private static double Lin(double a, double b, double t) => a + (b - a) * t;

        /// <summary>Interpolate hue along the shortest arc on the 0..360 circle, then wrap to [0,360).</summary>
        private static double LerpHue(double h1, double h2, double t)
        {
            double delta = h2 - h1;
            if (delta > 180.0) delta -= 360.0;
            else if (delta < -180.0) delta += 360.0;
            double h = h1 + delta * t;
            h %= 360.0;
            if (h < 0.0) h += 360.0;
            return h;
        }
    }

    /// <summary>
    /// Cubic-bezier easing on the unit square: given control points (p1x,p1y) and (p2x,p2y) with the
    /// implicit endpoints (0,0) and (1,1), map a horizontal progress in [0,1] to the curve's y. Solves x(s)
    /// for the bezier parameter s by Newton/bisection, then returns y(s). Standard CSS timing-function math.
    /// </summary>
    public static class CubicBezier
    {
        public static double Solve(double x, double p1x, double p1y, double p2x, double p2y)
        {
            if (x <= 0.0) return 0.0;
            if (x >= 1.0) return 1.0;
            double s = SolveParameterForX(x, p1x, p2x);
            return SampleY(s, p1y, p2y);
        }

        private static double SampleX(double s, double p1x, double p2x)
        {
            double mt = 1.0 - s;
            return 3.0 * mt * mt * s * p1x + 3.0 * mt * s * s * p2x + s * s * s;
        }

        private static double SampleY(double s, double p1y, double p2y)
        {
            double mt = 1.0 - s;
            return 3.0 * mt * mt * s * p1y + 3.0 * mt * s * s * p2y + s * s * s;
        }

        private static double SampleDerivativeX(double s, double p1x, double p2x)
        {
            double mt = 1.0 - s;
            return 3.0 * mt * mt * p1x + 6.0 * mt * s * (p2x - p1x) + 3.0 * s * s * (1.0 - p2x);
        }

        private static double SolveParameterForX(double x, double p1x, double p2x)
        {
            double s = x; // initial guess
            for (int i = 0; i < 8; i++)
            {
                double xs = SampleX(s, p1x, p2x) - x;
                if (math.abs(xs) < 1e-9) return s;
                double d = SampleDerivativeX(s, p1x, p2x);
                if (math.abs(d) < 1e-9) break;
                s -= xs / d;
            }
            // Fallback: bisection on [0,1].
            double lo = 0.0, hi = 1.0;
            s = x;
            for (int i = 0; i < 40; i++)
            {
                double xs = SampleX(s, p1x, p2x);
                if (math.abs(xs - x) < 1e-9) break;
                if (xs < x) lo = s; else hi = s;
                s = 0.5 * (lo + hi);
            }
            return s;
        }
    }
}
