using UnityEngine;
using MapRenderer.Core.Text.Sprites;

namespace MapRenderer.Unity.Rendering.Style
{
    /// <summary>
    /// An <see cref="IRenderLayer"/> that paints from the style's sprite sheet — <c>fill-pattern</c>.
    /// <c>line-pattern</c> and <c>background-pattern</c> are not implemented.
    /// Non-obvious why: a seam, not a constructor argument, because <c>SymbolSubsystem</c> fetches the
    /// sheet asynchronously after <c>MapView.SetStyle</c> builds the layers, so
    /// <see cref="RenderLayerSet.SetSprites"/> delivers it later. That is a uniform change only: stream 1
    /// already holds world-unit pattern coordinates, so no tile re-meshes (<c>FillPatternResolveTests</c>).
    /// </summary>
    internal interface ISpriteConsumerRenderLayer : IRenderLayer
    {
        /// <summary>
        /// Binds (or re-binds) the style's sprite sheet. A <see langword="null"/> <paramref name="atlas"/> or
        /// <paramref name="texture"/> — no <c>sprite</c> URL, a 404, or a restyle that dropped the old sheet
        /// before the new one arrived — puts the layer back into the unresolved state, where a pattern layer
        /// paints nothing. Idempotent: safe to call every frame with the same references.
        /// </summary>
        void SetSprites(SpriteAtlasView atlas, Texture2D texture);
    }
}
