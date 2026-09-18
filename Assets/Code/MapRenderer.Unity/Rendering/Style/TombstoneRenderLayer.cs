using UnityEngine;
using UnityEngine.Rendering;
using MapRenderer.Core.Rendering;
using MapRenderer.Core.Style;

namespace MapRenderer.Unity.Rendering.Style
{
    /// <summary>The vacated slot a layer leaves behind when <see cref="RenderLayerSet.TryRestyleInPlace"/>
    /// retires it — material-less, mesh-less, not <c>null</c> (several live sites would NRE on a raw null).
    /// Implements neither <see cref="ITileMeshRenderLayer"/> nor <see cref="ISpriteConsumerRenderLayer"/>, so
    /// <c>ComputeDenseLayerIds</c> and <c>SetSprites</c> skip it for free; the mid-flight consume guard tests
    /// for THIS TYPE instead — `docs/tile-pipeline-design.md` §1.10 says why.</summary>
    internal sealed class TombstoneRenderLayer : IRenderLayer
    {
        public TombstoneRenderLayer(int slot) => DrawIndex = slot;

        public StyleLayer        StyleLayer  => null;
        public RenderLayerBuild  Build       => RenderLayerBuild.TileMesh;
        public DrawPersistence   Persistence => DrawPersistence.Persistent;
        public int               DrawIndex   { get; }
        public LayerSubSlot      MaterialSubSlot => LayerSubSlot.Base;
        public ShadowCastingMode CastShadows => ShadowCastingMode.Off;
        public Material          Material    => null;
        public int               TransitioningCount => 0;

        public void ApplyZoom(in StyleFrameInputs inputs) { }
        public void Restyle(StyleLayer layer, in StyleTransition transition, double nowSeconds) { }
        public void SetDrawOrder(int declaredOrder) { }
        public void Dispose() { }
    }
}
