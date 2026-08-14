// Engine-free: this file is compiled verbatim by both the Unity EditMode runner
// (Assets/Code/MapRenderer.Tests.EditMode/) and the fast dotnet test project (Tools/core-tests/).
// Do NOT add any UnityEngine, MeshBuilder, NativeArray, or MonoBehaviour references.

using System.Collections.Generic;
using MapRenderer.Core.Expressions;
using MapRenderer.Core.Tiles;

namespace MapRenderer.Tests
{
    /// <summary>Shared helpers for the S09 expression tests (parse + evaluate over the JSON form).</summary>
    internal static class Expr
    {
        /// <summary>Parse a JSON expression string into an evaluable tree.</summary>
        public static Expression Parse(string json) => ExpressionParser.Parse(json);

        /// <summary>Parse + evaluate (throws on a spec error) with no feature.</summary>
        public static Value Eval(string json, double zoom = 0.0)
            => Parse(json).Evaluate(new EvaluationContext(zoom));

        /// <summary>Parse + evaluate with a feature.</summary>
        public static Value Eval(string json, IFeature feature, double zoom = 0.0)
            => Parse(json).Evaluate(new EvaluationContext(zoom, feature));

        /// <summary>Parse + evaluate via the boundary: returns true on success, false on a spec error.</summary>
        public static bool TryEval(string json, out Value result, out string error, double zoom = 0.0,
            IFeature feature = null)
            => Parse(json).TryEvaluate(new EvaluationContext(zoom, feature), out result, out error);

        public static DictionaryFeature Feature(
            Dictionary<string, Value> props = null,
            TileGeometryType geom = TileGeometryType.Unknown,
            bool hasId = false,
            Value id = default)
            => new DictionaryFeature(props, geom, hasId, id);

        public static Dictionary<string, Value> Props(params (string, Value)[] entries)
        {
            var d = new Dictionary<string, Value>();
            foreach (var (k, v) in entries) d[k] = v;
            return d;
        }
    }
}
