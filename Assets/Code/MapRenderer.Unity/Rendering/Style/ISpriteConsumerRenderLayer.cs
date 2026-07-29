using UnityEngine;
using MapRenderer.Core.Text.Sprites;

namespace MapRenderer.Unity.Rendering.Style
{
    /// <summary>
    /// An <see cref="IRenderLayer"/> that paints from the style's sprite sheet — today <c>fill-pattern</c>,
    /// later <c>line-pattern</c> and <c>background-pattern</c>, which resolve a sprite name the same way.
    ///
    /// <para><b>Why this exists as a seam rather than a constructor argument:</b> the sheet is fetched
    /// asynchronously (it is one keyless request per style, owned by <c>SymbolLabelSubsystem</c>), while
    /// layer materials are built eagerly and synchronously inside <c>MapView.SetStyle</c>. So at
    /// <c>TryCreate</c> time the sheet provably does not exist yet, and a pattern-bearing layer must be able
    /// to start unresolved and be told later. <see cref="RenderLayerSet.SetSprites"/> is the push.</para>
    ///
    /// <para>Resolving late is cheap precisely because it is a pure material-uniform change: the fill mesh
    /// already carries tile-normalized UVs in stream 1 (both the flat and the subdivided globe path), so no
    /// tile is re-meshed and no restyle is triggered when the sheet lands. That claim is pinned by
    /// <c>FillPatternResolveTests</c>.</para>
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
