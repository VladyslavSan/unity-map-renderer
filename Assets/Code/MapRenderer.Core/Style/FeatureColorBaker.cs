using System.Collections.Generic;
using MapRenderer.Core.Expressions;

namespace MapRenderer.Core.Style
{
    /// <summary>
    /// Engine-free bake helper: given a <see cref="StyleProperty{T}"/>, a zoom, and a sequence
    /// of <see cref="IFeature"/>, returns one typed value per feature.
    ///
    /// This is the single source of truth for "evaluate-and-bake" — tested without a GPU in both
    /// <c>dotnet test</c> and the Unity EditMode runner.  The Unity bootstrap (MapFillBootstrap)
    /// calls this helper per feature and maps the result to per-vertex mesh color.
    ///
    /// Color space: the returned <see cref="Color"/> values are in sRGB, matching the expression
    /// engine's color space.  The Unity caller (MeshBuilder) converts Core sRGB to UnityEngine.Color
    /// by casting the channels directly, then applies <c>Color.linear</c> before calling
    /// <c>Mesh.SetColors</c> (D2 fix). This linearisation step is required because
    /// <c>material.SetColor</c> linearises sRGB→linear at the material boundary (in Linear color
    /// space), but <c>Mesh.SetColors</c> does NOT — so the caller must linearise explicitly before
    /// upload to keep the vertex COLOR stream and <c>_BaseColor</c> in the same linear space for
    /// the shader multiply.
    ///
    /// Missing property: when ["get","key"] references a property absent on a feature, the expression
    /// engine returns Value.Null; a match/step/default branch covers it.  If the expression throws
    /// (no fallback), the fallback color is returned for that feature.
    ///
    /// Managed path only — must NOT be called from inside a Burst job.
    /// </summary>
    public static class FeatureColorBaker
    {
        /// <summary>
        /// Evaluate <paramref name="prop"/> for each feature at the given zoom and return a list of
        /// per-feature baked colors.  Each element corresponds to the same-index feature in
        /// <paramref name="features"/>.
        /// </summary>
        /// <param name="prop">Color <see cref="StyleProperty{T}"/> (may be Constant, Zoom, Feature, or Composite).</param>
        /// <param name="zoom">Map zoom level to pass to the evaluator (for zoom-dependent stops).</param>
        /// <param name="features">Sequence of features to evaluate against.</param>
        /// <param name="fallback">Color to use when evaluation fails (e.g. expression error, wrong type).</param>
        /// <returns>A new list with one <see cref="Color"/> per feature in input order.</returns>
        public static List<Color> BakeColors(
            StyleProperty<Color> prop,
            double zoom,
            IEnumerable<IFeature> features,
            Color fallback = default)
        {
            var result = new List<Color>();
            foreach (var feature in features)
            {
                if (prop.TryEvaluate(zoom, feature, out Color c))
                    result.Add(c);
                else
                    result.Add(fallback);
            }
            return result;
        }

        /// <summary>
        /// Evaluate <paramref name="prop"/> for each feature at the given zoom and return per-feature
        /// baked numbers as <c>double</c>.
        /// </summary>
        public static List<double> BakeNumbers(
            StyleProperty<float> prop,
            double zoom,
            IEnumerable<IFeature> features,
            double fallback = 0.0)
        {
            var result = new List<double>();
            foreach (var feature in features)
            {
                if (prop.TryEvaluate(zoom, feature, out float n))
                    result.Add((double)n);
                else
                    result.Add(fallback);
            }
            return result;
        }
    }
}
