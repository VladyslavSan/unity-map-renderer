using System.Collections.Generic;
using UnityEngine;
using MapRenderer.Core.Lifetime;
using MapRenderer.Core.Style;
using MapRenderer.Core.Rendering;
using MapRenderer.Core.Text.Sprites;
using MapRenderer.Core.View.Camera;
using MapRenderer.Unity.Common;

namespace MapRenderer.Unity.Rendering.Style
{
    /// <summary>
    /// The ordered set of runtime render layers built once from a <see cref="StyleDocument"/> and kept
    /// current per frame — the replacement for the retired <c>StyledLayerSet</c>.
    ///
    /// <para>ARCHITECTURE §"Layer ordering": the style is an ordered list of layers composited in declared
    /// order. This holds exactly that — ONE <see cref="List{IRenderLayer}"/> containing ALL painted layers
    /// (fill, line, symbol, background — D7 global numbering, the render-layer model), in declared order,
    /// where <c>index == DrawIndex == draw order == material index</c>. The old
    /// fill/line split (<c>_fills</c>/<c>_lines</c>, the fills-then-lines <c>materialIndex = FillCount + li</c>
    /// flatten, and the "fills first" comments) is gone: every kind is just an <see cref="IRenderLayer"/>
    /// implementation in one list, and a new layer type drops in via <see cref="RenderLayerFactory"/> with no
    /// change here. Every painted kind is material-bearing when its base material is configured (symbol as
    /// of E2/D11, background as of E3); a null <see cref="IRenderLayer.Material"/> means only that ONE
    /// slot's own base material is unconfigured (<see cref="Materials.MapMaterialSet"/>), never a kind that
    /// hasn't migrated into the model.</para>
    ///
    /// <para>Records are built ONCE (not per tile); each tile-mesh layer produces a mesh per tile and draws
    /// it with the matching layer's material, whose <c>renderQueue</c> encodes the layer's place in the draw
    /// order. Shared by reference with the tile pipeline; layers that own a material destroy it when
    /// disposed (a null-material layer's own <c>Dispose</c> is a no-op).</para>
    /// </summary>
    internal sealed class RenderLayerSet : VerifiedDisposable
    {
        private readonly List<IRenderLayer> _layers = new List<IRenderLayer>(16);

        /// <summary>The shared Hierarchy parent for the layers' scene GameObjects (the symbol presenters and
        /// the background quad). Visible + inspectable, but <see cref="HideFlags.DontSave"/> — a runtime
        /// artifact, never serialised into a scene or build — mirroring the GameObject tile backend's
        /// "MapTiles" root. Fill/line layers have no GameObject and ignore it. Lazily created on the first
        /// <see cref="Build"/>; kept at world identity and never moved, so a child at local identity stays at
        /// world identity — load-bearing, since the background quad's fill shader and the symbol shader both
        /// assume an identity object-to-world.</summary>
        private GameObject _root;

        /// <summary>Last pair pushed through <see cref="SetSprites"/> — the per-frame no-op memo. Reset by
        /// <see cref="ClearLayers"/>, since a rebuilt layer starts unresolved and must be re-told.</summary>
        private SpriteAtlasView _spriteAtlas;
        private Texture2D       _spriteTexture;

        /// <summary>Number of render layers (declared, renderable). <c>index == draw order == material index</c>.</summary>
        public int Count => _layers.Count;

        /// <summary>The shared Hierarchy parent for the layers' scene GameObjects (null before the first
        /// <see cref="Build"/> / after <see cref="Dispose"/>). Tests read the live grouping through it.</summary>
        internal Transform Root => _root != null ? _root.transform : null;

        /// <summary>The render layer at declared-order <paramref name="index"/>.</summary>
        public IRenderLayer this[int index] => _layers[index];

        /// <summary>The render layers in declared order (read-only view).</summary>
        public IReadOnlyList<IRenderLayer> Layers => _layers;

        /// <summary>Snapshot copy for an async mesh-build task (so the list can't mutate mid-flight).</summary>
        public IRenderLayer[] SnapshotLayers() => _layers.ToArray();

        /// <summary>
        /// Builds the render layers from <paramref name="style"/>. Draw order IS the style's declared layer
        /// order (MapLibre painter's algorithm): walk <c>style.Layers</c> ONCE and assign each a queue BAND
        /// via <see cref="LayerDrawOrder"/> (D7) — one band per declared layer, monotonic across bands — so
        /// an interleaved fill-over-symbol or line-over-fill composites exactly as declared (they share one
        /// transparent band, ZWrite off, so the renderQueue offset alone decides order). A layer's own
        /// <see cref="IRenderLayer.MaterialSubSlot"/> selects WHICH sub-slot of its band this write targets —
        /// <see cref="LayerSubSlot.Base"/> for fill/line/background, <see cref="LayerSubSlot.Above"/> for a
        /// symbol layer's text, so it draws over that same layer's icon (G7/D7 — written separately by
        /// <see cref="SymbolRenderLayer.Create"/>, since the icon has no Build-time free ride).
        /// The list contains ALL painted layers — fill, line, symbol, background — with
        /// <c>index == DrawIndex == draw order == material index</c>; every slot is material-bearing when
        /// its base material is configured (symbol as of E2/D11, background as of E3). Only genuinely
        /// unpainted/unsupported types (raster, circle, unknown) and layers whose material set is
        /// unconfigured take no slot. Disposes
        /// any previously-built layers first (via
        /// <see cref="ClearLayers"/> — NOT <see cref="Dispose"/>: a restyle calls this repeatedly over the
        /// object's life, so the teardown must not be gated by the once-only disposed guard).
        /// </summary>
        public void Build(StyleDocument style, double initialZoom, Materials.MapMaterialSet settings = null)
        {
            ClearLayers();
            if (style == null) return;

            if (_root == null)
                _root = new GameObject("Map Render Layers") { hideFlags = HideFlags.DontSave };

            int drawIndex = 0;
            foreach (var sl in style.Layers)
            {
                IRenderLayer layer = RenderLayerFactory.Create(sl, settings, initialZoom, drawIndex, _root.transform);
                if (layer == null) continue; // genuinely unpainted, or unconfigured material — no slot

                if (layer.Material != null) // null only when that slot's own base material is unconfigured — skip the queue write
                    layer.Material.renderQueue = LayerDrawOrder.QueueFor(drawIndex, layer.MaterialSubSlot);
                _layers.Add(layer);
                drawIndex++;
            }
        }

        /// <summary>
        /// Pushes per-frame zoom-dependent uniforms to every layer (fill/line zoom paint, zoom-step
        /// dasharrays). Line width is resolved in screen space by the shader (S104), so no ground resolution
        /// is threaded through. Alloc-free: a plain <c>for</c> over the list (struct enumerator-free),
        /// each layer's <see cref="IRenderLayer.ApplyZoom"/> being alloc-free.
        /// </summary>
        public void ApplyZoom(double zoom)
        {
            for (int i = 0; i < _layers.Count; i++)
                _layers[i].ApplyZoom(zoom);
        }

        /// <summary>
        /// Pushes the style's sprite sheet to every layer that paints from it (see
        /// <see cref="ISpriteConsumerRenderLayer"/>). Called each frame from the map's tick, because the sheet
        /// is fetched asynchronously and can arrive — or be dropped by a restyle — at any point after
        /// <see cref="Build"/>.
        ///
        /// <para>Early-outs on an unchanged pair, so the steady state is one reference compare per frame
        /// rather than a walk plus a <c>SetTexture</c> per pattern layer. The comparison is on the CALLER's
        /// references, which is why it lives here and not in each layer: <see cref="Build"/> replaces every
        /// layer on a restyle, so the memo must be reset there too (a rebuilt layer starts unresolved and
        /// would otherwise never be told about a sheet that had already arrived).</para>
        /// </summary>
        public void SetSprites(SpriteAtlasView atlas, Texture2D texture)
        {
            if (ReferenceEquals(atlas, _spriteAtlas) && ReferenceEquals(texture, _spriteTexture))
                return;
            _spriteAtlas   = atlas;
            _spriteTexture = texture;

            for (int i = 0; i < _layers.Count; i++)
                if (_layers[i] is ISpriteConsumerRenderLayer consumer)
                    consumer.SetSprites(atlas, texture);
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
            // Drop the memo with the layers it described: the replacements start unresolved, so a sheet that
            // arrived before this restyle must be pushed again rather than compared away as "unchanged".
            _spriteAtlas   = null;
            _spriteTexture = null;
        }

        /// <inheritdoc cref="ClearLayers"/>
        protected override void DoDispose()
        {
            ClearLayers();
            _root.DestroySafely(); // null-guarded (never built ⇒ no-op); its children were destroyed by ClearLayers
            _root = null;
        }

        /// <summary>Destroys a per-layer <see cref="Material"/> instance (play → Destroy, edit → DestroyImmediate).
        /// Shared by the <see cref="IRenderLayer"/> implementations, which own their materials.</summary>
        internal static void DestroyMaterialInstance(Material mat) => mat.DestroySafely();
    }
}
