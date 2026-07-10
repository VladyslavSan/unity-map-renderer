using System.Collections.Generic;
using MapRenderer.Core.Mvt;

namespace MapRenderer.Core.Expressions.Ops
{
    /// <summary>
    /// Feature-data operators per the Style Spec "Feature data" section: <c>properties</c> (the feature's
    /// property map as an object), <c>geometry-type</c> (Point/LineString/Polygon), and <c>id</c> (the
    /// feature id, or null if absent). Each is classified <see cref="ExpressionKind.Feature"/>.
    /// </summary>
    public static class FeatureData
    {
        public static Expression Properties(List<Json.JsonValue> args)
        {
            if (args.Count != 0)
                throw new ExpressionParseException($"\"properties\" expects 0 arguments, got {args.Count}.");
            return new FeatureDataExpression((in EvaluationContext ctx) =>
            {
                if (ctx.Feature == null)
                    throw new ExpressionEvaluationException("properties: no feature in context.");
                return Value.Object(ctx.Feature.Properties);
            }, ValueType.Object);
        }

        public static Expression GeometryType(List<Json.JsonValue> args)
        {
            if (args.Count != 0)
                throw new ExpressionParseException($"\"geometry-type\" expects 0 arguments, got {args.Count}.");
            return new FeatureDataExpression((in EvaluationContext ctx) =>
            {
                if (ctx.Feature == null)
                    throw new ExpressionEvaluationException("geometry-type: no feature in context.");
                return Value.String(GeometryTypeName(ctx.Feature.GeometryType));
            }, ValueType.String);
        }

        public static Expression Id(List<Json.JsonValue> args)
        {
            if (args.Count != 0)
                throw new ExpressionParseException($"\"id\" expects 0 arguments, got {args.Count}.");
            return new FeatureDataExpression((in EvaluationContext ctx) =>
            {
                if (ctx.Feature == null)
                    throw new ExpressionEvaluationException("id: no feature in context.");
                return ctx.Feature.HasId ? ctx.Feature.Id : Value.Null;
            }, ValueType.Value);
        }

        // Spec geometry-type strings.
        public static string GeometryTypeName(MvtGeometryType t)
        {
            switch (t)
            {
                case MvtGeometryType.Point: return "Point";
                case MvtGeometryType.LineString: return "LineString";
                case MvtGeometryType.Polygon: return "Polygon";
                default: return "Unknown";
            }
        }
    }
}
