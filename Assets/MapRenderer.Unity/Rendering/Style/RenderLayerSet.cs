using System.Collections.Generic;
using UnityEngine;
using MapRenderer.Core.Lifetime;
using MapRenderer.Core.Style;
using MapRenderer.Core.Rendering;
using MapRenderer.Core.View.Camera;

namespace MapRenderer.Unity.Rendering.Style
{
    /// <summary>
    /// The ordered set of runtime render layers built once from a <see cref="StyleDocument"/> and kept
    /// current per frame — the replacement for the retired <c>StyledLayerSet</c>.
    ///
    /// <para>ARCHITECTURE §"Layer ordering": the style is an ordered list of layers composited in declared
    /// order. This holds exactly that — ONE <see cref="List{IRenderLayer}"/> in declared order, where
    /// <c>index == draw order == material index</c>. The old fill/line split (<c>_fills</c>/<c>_lines</c>,
    /// the fills-then-lines <c>materialIndex = FillCount + li</c> flatten, and the "fills first" comments)
    /// is gone: fill and line are just two <see cref="IRenderLayer"/> implementations in one list, and a new
    /// static layer type drops in via <see cref="RenderLayerFactory"/> with no change here.</para>
    ///
    /// <para>Records are built ONCE (not per tile); each tile produces a mesh per layer and draws it with
    /// the matching layer's material, whose <c>renderQueue</c> encodes the layer's place in the draw order.
    /// Shared by reference with the tile pipeline; layers own their materials and destroy them on
    /// <see cref="Dispose"/>.</para>
    /// </summary>
    internal sealed class RenderLayerSet : VerifiedDisposable
    {
        private readonly List<IRenderLayer> _layers = new List<IRenderLayer>(16);

        /// <summary>Number of render layers (declared, renderable). <c>index == draw order == material index</c>.</summary>
        public int Count => _layers.Count;

        /// <summary>The render layer at declared-order <paramref name="index"/>.</summary>
        public IRenderLayer this[int index] => _layers[index];

        /// <summary>The render layers in declared order (read-only view).</summary>
        public IReadOnlyList<IRenderLayer> Layers => _layers;

        /// <summary>Snapshot copy for the background tessellation task (so the list can't mutate mid-flight).</summary>
        public IRenderLayer[] SnapshotLayers() => _layers.ToArray();

        /// <summary>
        /// Builds the render layers from <paramref name="style"/>. Draw order IS the style's declared layer
        /// order (MapLibre painter's algorithm): walk <c>style.Layers</c> ONCE and assign a single monotonic
        /// <c>renderQueue</c> across ALL renderable layers by their position, so an interleaved fill-over-line
        /// or line-over-fill composites exactly as declared (they share one transparent band, ZWrite off, so
        /// the renderQueue offset alone decides order — see <see cref="LayerDrawOrder"/>). Non-renderable
        /// layers (background/raster/symbol) and layers whose material set is unconfigured take no slot.
        /// Disposes any previously-built layers first (via <see cref="ClearLayers"/> — NOT <see cref="Dispose"/>:
        /// a restyle calls this repeatedly over the object's life, so the teardown must not be gated by the
        /// once-only disposed guard).
        /// </summary>
        public void Build(StyleDocument style, double initialZoom, Materials.MapMaterialSet settings = null)
        {
            ClearLayers();
            if (style == null) return;

            int drawIndex = 0;
            foreach (var sl in style.Layers)
            {
                IRenderLayer layer = RenderLayerFactory.Create(sl, settings, initialZoom);
                if (layer == null) continue; // not a static render layer, or unconfigured material — no slot

                layer.Material.renderQueue = LayerDrawOrder.TransparentQueue + drawIndex;
                _layers.Add(layer);
                drawIndex++;
            }
        }

        /// <summary>
        /// Pushes per-frame zoom-dependent uniforms to every layer. The live ground resolution
        /// (<c>_MetersPerPixel</c>, needed by pixel-mode line width) is computed once and handed to each
        /// layer; fills ignore it. Alloc-free: a plain <c>for</c> over the list (struct enumerator-free),
        /// each layer's <see cref="IRenderLayer.ApplyZoom"/> being alloc-free.
        /// </summary>
        public void ApplyZoom(double zoom)
        {
            float metersPerPixel = (float)CameraPoseMath.MetersPerPixel(zoom);
            for (int i = 0; i < _layers.Count; i++)
                _layers[i].ApplyZoom(zoom, metersPerPixel);
        }

        /// <summary>Disposes every render layer (each destroys its Material instance) and clears the list.
        /// Reusable afterwards (unlike <see cref="Dispose"/>'s teardown) — called by BOTH <see cref="Build"/>
        /// (every restyle) and <see cref="DoDispose"/> (real teardown), mirroring
        /// <c>PreparedTileCache</c>'s <c>Clear()</c>/<c>Dispose()</c> split.</summary>
        private void ClearLayers()
        {
            for (int i = 0; i < _layers.Count; i++)
                _layers[i].Dispose();
            _layers.Clear();
        }

        /// <inheritdoc cref="ClearLayers"/>
        protected override void DoDispose() => ClearLayers();

        /// <summary>Destroys a per-layer <see cref="Material"/> instance (play → Destroy, edit → DestroyImmediate).
        /// Shared by the <see cref="IRenderLayer"/> implementations, which own their materials.</summary>
        internal static void DestroyMaterialInstance(Material mat)
        {
            if (mat == null) return;
            if (Application.isPlaying) UnityEngine.Object.Destroy(mat);
            else                       UnityEngine.Object.DestroyImmediate(mat);
        }
    }
}
