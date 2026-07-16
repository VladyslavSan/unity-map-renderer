namespace MapRenderer.Unity.Rendering.Style
{
    /// <summary>
    /// Who re-draws an <see cref="IRenderLayer"/> each camera render (the
    /// render-layer model). Orthogonal to <see cref="RenderLayerBuild"/>.
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
