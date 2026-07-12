namespace MapRenderer.Unity.Rendering.Style
{
    /// <summary>
    /// Who re-draws an <see cref="IRenderLayer"/> each camera render (design
    /// <c>docs/render-layer-unification.md</c> §3.4). Orthogonal to <see cref="RenderLayerBuild"/>.
    /// </summary>
    internal enum DrawPersistence
    {
        /// <summary>A backend redraws it every render on its own (BRG <c>OnPerformCulling</c> / Entities
        /// Graphics / GameObject <c>MeshRenderer</c>s) — no per-render help needed.</summary>
        Persistent,

        /// <summary>Someone must re-issue the draw for every camera render.</summary>
        Immediate,
    }
}
