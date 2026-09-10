using System.Collections.Generic;
using UnityEngine;
using MapRenderer.Core.Lifetime;
using MapRenderer.Core.Style;
using MapRenderer.Core.Rendering;
using MapRenderer.Core.Text.Sprites;
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

        /// <summary>Style layers the last <see cref="Build"/> skipped, with why (UMR-116). Bounded to style
        /// load exactly like <see cref="_layers"/> — control-plane, not data-plane (rebuilt once per
        /// restyle, never touched per tile or per frame) — so a plain managed list is correct here; see
        /// docs/conventions-short.md, "New data-plane code is born native", for the discriminator.</summary>
        private readonly List<SkippedLayer> _skippedLayers = new List<SkippedLayer>();

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

        /// <summary>The style-load compatibility summary (UMR-116): every layer the last <see cref="Build"/>
        /// skipped, with its reason. Rebuilt from scratch on every <see cref="Build"/>, including a
        /// restyle — an old style's skips stop applying the moment a new style replaces it.</summary>
        public IReadOnlyList<SkippedLayer> SkippedLayers => _skippedLayers;

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
        /// its base material is configured (symbol as of E2/D11, background as of E3). A layer that takes no
        /// slot — an unsupported kind, an unconfigured material, or a by-design skip (a source-less symbol
        /// layer) — is recorded in <see cref="SkippedLayers"/> with which, instead of silently dropped
        /// (UMR-116; see <see cref="LayerSkipReason"/> for the three reasons). Disposes any previously-built
        /// layers AND clears the previous compatibility summary first (via <see cref="ClearLayers"/> — NOT
        /// <see cref="Dispose"/>: a restyle calls this repeatedly over the object's life, so the teardown
        /// must not be gated by the once-only disposed guard).
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
                IRenderLayer layer = RenderLayerFactory.Create(
                    sl, settings, initialZoom, drawIndex, out LayerSkipReason skipReason, _root.transform);
                if (layer == null)
                {
                    // Unsupported kind, unconfigured material, or genuinely unpainted by design — no slot;
                    // recorded instead of silently dropped (UMR-116).
                    _skippedLayers.Add(new SkippedLayer { Id = sl.Id, RawType = sl.RawType, Reason = skipReason });
                    continue;
                }

                if (layer.Material != null) // null only when that slot's own base material is unconfigured — skip the queue write
                    layer.Material.renderQueue = LayerDrawOrder.QueueFor(drawIndex, layer.MaterialSubSlot);
                _layers.Add(layer);
                drawIndex++;
            }
        }

        /// <summary>
        /// Pushes per-frame uniforms to every layer (fill/line zoom paint, zoom-step dasharrays, and the
        /// px→device conversion for the px-valued paint family), hence <paramref name="devicePixelRatio"/>
        /// (S107). Alloc-free: a plain <c>for</c> over the list (struct enumerator-free), each layer's
        /// <see cref="IRenderLayer.ApplyZoom"/> being alloc-free.
        ///
        /// <para><b>The frame's px→world RULER is NOT pushed here</b> (S116). The line shader converts every
        /// <c>px</c>-valued width property and its dash parameterisation with the
        /// <c>_MapFrameMetersPerDevicePixel</c> global, and that is a CAMERA quantity —
        /// <see cref="Map.MapCamera.SyncToCamera"/> owns the push, measuring it off the live camera
        /// (<see cref="Map.MapCamera.MetresPerDevicePixel"/>) instead of re-deriving it from a Web-Mercator
        /// zoom formula here. The two agreed only while the altitude was the canonical function of zoom, and a
        /// render path that never called this method read whatever an earlier one had left in that process
        /// global.</para>
        ///
        /// <para><see cref="Build"/> deliberately takes no ratio: its per-layer seeds run at dpr 1, and the
        /// caller re-applies at the live ratio immediately afterwards (<c>MapView.SetStyle</c>) so no frame
        /// is ever drawn from a seeded value. Threading an initial ratio through
        /// <see cref="RenderLayerFactory"/>'s four Create/TryCreate overloads would churn 8 call sites for
        /// the same guarantee.</para>
        /// </summary>
        public void ApplyZoom(double zoom, double devicePixelRatio)
        {
            for (int i = 0; i < _layers.Count; i++)
                _layers[i].ApplyZoom(zoom, devicePixelRatio);
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
            _skippedLayers.Clear(); // the compatibility summary belongs to the CURRENT style only
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

    /// <summary>One entry of <see cref="RenderLayerSet.SkippedLayers"/> — a style layer that took no draw
    /// slot, and why (UMR-116).</summary>
    internal readonly struct SkippedLayer
    {
        /// <summary>The skipped layer's <see cref="StyleLayer.Id"/> (Style Spec <c>id</c>; may be null — see
        /// that field's doc).</summary>
        public string Id { get; init; }

        /// <summary>The skipped layer's raw <c>type</c> string (<see cref="StyleLayer.RawType"/>), e.g.
        /// <c>"circle"</c> — reported verbatim rather than <see cref="StyleLayerType"/> so an unrecognized
        /// type still names itself instead of reading as <c>Unknown</c>.</summary>
        public string RawType { get; init; }

        /// <summary>Why <see cref="RenderLayerFactory"/> returned no layer for this entry.</summary>
        public LayerSkipReason Reason { get; init; }
    }
}
