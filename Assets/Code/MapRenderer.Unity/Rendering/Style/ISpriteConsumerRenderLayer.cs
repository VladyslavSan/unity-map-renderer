using UnityEngine;
using MapRenderer.Core.Text.Sprites;

namespace MapRenderer.Unity.Rendering.Style
{
    /// <summary>
    /// An <see cref="IRenderLayer"/> that paints from the style's sprite sheet — <c>fill-pattern</c>.
    /// <c>line-pattern</c> and <c>background-pattern</c> are not implemented; they would resolve a sprite
    /// name the same way.
    /// A seam rather than a constructor argument because the sheet is fetched asynchronously (one keyless
    /// request per style, owned by <c>SymbolSubsystem</c>) while layer materials build eagerly and
    /// synchronously inside <c>MapView.SetStyle</c>: at <c>TryCreate</c> time the sheet provably does not
    /// exist yet, so a pattern-bearing layer starts unresolved and is told later via
    /// <see cref="RenderLayerSet.SetSprites"/>. Resolving late is cheap because it is a pure
    /// material-uniform change — the fill mesh already carries world-unit pattern coordinates in stream 1
    /// (flat and globe paths), so no tile is re-meshed and no restyle is triggered
    /// (<c>FillPatternResolveTests</c>).
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
