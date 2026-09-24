using System.Collections.Generic;
using Unity.Mathematics;
using UnityEngine;
using MapRenderer.Core.Json;
using MapRenderer.Core.Lifetime;
using MapRenderer.Core.Style;
using MapRenderer.Core.Rendering;
using MapRenderer.Core.Text.Sprites;
using MapRenderer.Unity.Common;

namespace MapRenderer.Unity.Rendering.Style
{
    /// <summary>
    /// The ordered set of runtime render layers, built once per style and kept current per frame. ONE list
    /// holds ALL painted layers, with <c>index == DrawIndex == SLOT == material index</c>; draw order rides
    /// <c>renderQueue</c>. A new layer type drops in via <see cref="RenderLayerFactory"/>. A null
    /// <see cref="IRenderLayer.Material"/> means only that slot's base material is unconfigured. Shared by
    /// reference with the tile pipeline; each layer destroys its own material on dispose.
    /// </summary>
    internal sealed class RenderLayerSet : VerifiedDisposable
    {
        private readonly List<IRenderLayer> _layers = new List<IRenderLayer>(16);

        /// <summary>Style layers the last <see cref="Build"/> skipped, with why. Bounded to style
        /// load exactly like <see cref="_layers"/> — control-plane, not data-plane (rebuilt once per
        /// restyle, never touched per tile or per frame) — so a plain managed list is correct here; see
        /// docs/conventions-short.md, "New data-plane code is born native", for the discriminator.</summary>
        private readonly List<SkippedLayer> _skippedLayers = new List<SkippedLayer>();

        /// <summary>The Hierarchy root that <see cref="Build"/> passes to layer creation as <c>parent</c>, created
        /// on the first Build; <see cref="HideFlags.DontSave"/> keeps it out of scenes and builds.
        /// <c>SymbolRenderLayer</c> takes <c>parent</c> but does not use it, so nothing is parented here.</summary>
        private GameObject _root;

        /// <summary>Last pair pushed through <see cref="SetSprites"/> — the per-frame no-op memo. Reset by
        /// <see cref="ClearLayers"/>, since a rebuilt layer starts unresolved and must be re-told.</summary>
        private SpriteAtlasView _spriteAtlas;
        private Texture2D       _spriteTexture;

        /// <summary>Number of render-layer SLOTS — every consumer indexes by slot, including a vacated one
        /// (a <see cref="TombstoneRenderLayer"/>). <c>index == slot == material index</c>, but not
        /// <c>== draw order</c> after a partial-survival reorder (see <see cref="TryRestyleInPlace"/>).</summary>
        public int Count => _layers.Count;

        /// <summary>The shared Hierarchy parent for the layers' scene GameObjects (null before the first
        /// <see cref="Build"/> / after <see cref="Dispose"/>). Tests read the live grouping through it.</summary>
        internal Transform Root => _root != null ? _root.transform : null;

        /// <summary>The render layer at SLOT <paramref name="index"/>.</summary>
        public IRenderLayer this[int index] => _layers[index];

        /// <summary>The render layers in SLOT order (read-only view).</summary>
        public IReadOnlyList<IRenderLayer> Layers => _layers;

        /// <summary>Snapshot copy for an async mesh-build task (so the list can't mutate mid-flight).</summary>
        public IRenderLayer[] SnapshotLayers() => _layers.ToArray();

        /// <summary>The style-load compatibility summary: every layer the last <see cref="Build"/>
        /// skipped, with its reason. Rebuilt from scratch on every <see cref="Build"/>, including a
        /// restyle — an old style's skips stop applying the moment a new style replaces it.</summary>
        public IReadOnlyList<SkippedLayer> SkippedLayers => _skippedLayers;

        /// <summary>
        /// One slot's fade ease. Lives HERE, not on the layer: the target comes from the style layer's zoom
        /// range, the duration from the frame's <see cref="StyleFrameInputs.Transition"/> and the instant
        /// from the frame — none of the three is any one layer's, so no transition and no clock cross
        /// <see cref="IFadeableRenderLayer"/>.
        /// </summary>
        private struct LayerFade
        {
            public float  Current;
            public float  Origin;
            public float  Target;
            public double StartSeconds;      // armed-at + delay
            public double DurationSeconds;
        }

        // Index-aligned with _layers. Build appends one per slot and ClearLayers empties it; a restyle
        // replaces a slot IN PLACE (including with a tombstone), so the two lists can never drift.
        private readonly List<LayerFade> _fades = new List<LayerFade>();

        /// <summary>
        /// Builds the render layers from <paramref name="style"/> in declared order (painter's algorithm).
        /// Each layer gets one <see cref="LayerDrawOrder"/> queue band; its <see cref="IRenderLayer.MaterialSubSlot"/>
        /// picks the sub-slot, so a symbol's text draws over its own icon. A layer that takes no slot is
        /// recorded in <see cref="SkippedLayers"/>. It first calls <see cref="ClearLayers"/>, not
        /// <see cref="Dispose"/>, because a restyle rebuilds past the once-only disposed guard.
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
                    // recorded instead of silently dropped.
                    _skippedLayers.Add(new SkippedLayer { Id = sl.Id, RawType = sl.RawType, Reason = skipReason });
                    continue;
                }

                layer.SetDrawOrder(drawIndex); // no-op when that slot's own base material is unconfigured
                _layers.Add(layer);
                // Seeded from the SAME predicate each TryCreate hands ZoomStyleApplier.SeedFade, so this
                // ease and the material's multiplier agree on the first frame.
                float seed = sl.IsVisibleAtZoom(initialZoom) ? 1f : 0f;
                _fades.Add(new LayerFade { Current = seed, Origin = seed, Target = seed });
                drawIndex++;
            }
        }

        /// <summary>
        /// Pushes per-frame uniforms to every layer (zoom paint, zoom-step dasharrays, px→device conversion);
        /// alloc-free. The px→world ruler <c>_MapFrameMetersPerDevicePixel</c> is a camera quantity, so
        /// <see cref="Map.MapCamera.SyncToCamera"/> pushes it, not this. Non-local invariant: <see cref="Build"/>
        /// seeds at dpr 1, and <c>MapView.SetStyle</c> calls this at the live ratio right after it, so no frame
        /// draws a seeded value.
        /// </summary>
        public void ApplyZoom(in StyleFrameInputs inputs)
        {
            for (int i = 0; i < _layers.Count; i++)
            {
                // The minzoom/maxzoom/visibility draw gate. The `is` test excludes a tombstone without a null
                // check, and no real kind carries a null StyleLayer; Restyle moves StyleLayer forward.
                if (_layers[i] is IFadeableRenderLayer fadeable)
                    fadeable.SetFade(AdvanceFade(i, fadeable, inputs.Zoom, inputs.NowSeconds, inputs.Transition));
                _layers[i].ApplyZoom(inputs);
            }
        }

        /// <summary>
        /// Moves slot <paramref name="index"/>'s fade toward the amount its zoom range implies, and returns
        /// the resolved value. Re-arms only when the target actually changes, so a settled frame costs one
        /// float compare. A layer that declares <see cref="IFadeableRenderLayer.FadesGradually"/> false eases
        /// over no duration at all, so it lands on its target the frame the target moves.
        /// </summary>
        /// <param name="index">The slot, which is also this fade's index.</param>
        /// <param name="layer">The slot's own layer, read for its <see cref="IRenderLayer.StyleLayer"/>
        /// zoom range and its <see cref="IFadeableRenderLayer.FadesGradually"/> declaration.</param>
        /// <param name="zoom">The live camera zoom the target is evaluated at.</param>
        /// <param name="nowSeconds">The live wall clock, for arming and advancing the ease.</param>
        /// <param name="transition">This frame's ease duration/delay, from <see cref="StyleFrameInputs.Transition"/>.</param>
        /// <returns>The fade amount to apply this frame, 0 to 1.</returns>
        private float AdvanceFade(
            int index, IFadeableRenderLayer layer, double zoom, double nowSeconds, StyleTransition transition)
        {
            LayerFade f = _fades[index];
            float target = layer.StyleLayer.IsVisibleAtZoom(zoom) ? 1f : 0f;

            if (target != f.Target)
            {
                StyleTransition arm = layer.FadesGradually ? transition : StyleTransition.Instant;
                f.Origin          = f.Current;
                f.Target          = target;
                f.StartSeconds    = nowSeconds + arm.DelaySeconds;
                f.DurationSeconds = arm.DurationSeconds;
            }

            if (f.Current != f.Target)
            {
                double elapsed = nowSeconds - f.StartSeconds;
                if (elapsed >= 0.0)   // delay holds on the wall clock, as the paint bindings' ease does
                {
                    double t = f.DurationSeconds <= 0.0 ? 1.0 : math.saturate(elapsed / f.DurationSeconds);
                    f.Current = t >= 1.0
                        ? f.Target    // settle EXACTLY on the target, never a lerp endpoint
                        : math.lerp(f.Origin, f.Target, (float)math.smoothstep(0.0, 1.0, t));
                }
            }

            _fades[index] = f;
            return f.Current;
        }

        /// <summary>Entries currently easing, summed across every layer. No production consumer — see
        /// <see cref="IRenderLayer.TransitioningCount"/> for why it stays anyway.</summary>
        internal int TransitioningCount
        {
            get
            {
                int n = 0;
                for (int i = 0; i < _layers.Count; i++)
                    n += _layers[i].TransitioningCount;
                return n;
            }
        }

        /// <summary>
        /// An ID-KEYED diff — the exits are named and explained in `docs/tile-pipeline-design.md`.
        /// Returns <c>false</c> (unmodified) on ANY refusal — the
        /// two-pass shape below (classify fully, THEN mutate) is what makes that contract hold.
        /// </summary>
        /// <param name="oldStyle">The document these render layers were last built (or restyled) from.</param>
        /// <param name="newStyle">The candidate replacement document.</param>
        /// <param name="transition">The duration/delay newly-differing uniforms ease over.</param>
        /// <param name="nowSeconds">The restyle's wall-clock instant (armed-at, before any delay).</param>
        internal bool TryRestyleInPlace(StyleDocument oldStyle, StyleDocument newStyle,
            in StyleTransition transition, double nowSeconds)
        {
            if (!SurvivingLayerGate.RootMatches(oldStyle, newStyle)) return false;

            // Old side: id -> slot, over RENDERED layers only — _layers is shorter than oldStyle.Layers
            // whenever Build skipped one. A null id cannot be matched, so refuse closed.
            var oldSlotById = new Dictionary<string, int>(_layers.Count);
            for (int i = 0; i < _layers.Count; i++)
            {
                StyleLayer sl = _layers[i].StyleLayer;
                if (sl == null) continue; // an already-vacated slot (a prior restyle's tombstone)
                if (sl.Id == null) return false;
                oldSlotById[sl.Id] = i;
            }

            // Old side, DECLARED but never rendered (e.g. `raster`) — keyed the same way so an unchanged
            // skipped layer is told apart from a genuine ADD below. No slot to track.
            var oldDeclaredById = new Dictionary<string, StyleLayer>(oldStyle.Layers.Count);
            for (int i = 0; i < oldStyle.Layers.Count; i++)
            {
                StyleLayer sl = oldStyle.Layers[i];
                if (sl.Id != null) oldDeclaredById[sl.Id] = sl;
            }

            // New side, in declared order, classified against the old set. READ-ONLY: only the pass below
            // writes _layers, which is what makes a mid-walk refusal leave it unmodified.
            var survivors = new List<(int slot, int declaredOrder, StyleLayer newLayer)>(newStyle.Layers.Count);
            var claimedSlots = new HashSet<int>();
            for (int declaredOrder = 0; declaredOrder < newStyle.Layers.Count; declaredOrder++)
            {
                StyleLayer newLayer = newStyle.Layers[declaredOrder];
                if (newLayer.Id != null && oldSlotById.TryGetValue(newLayer.Id, out int slot))
                {
                    if (!SurvivingLayerGate.LayerSurvives(_layers[slot].StyleLayer, newLayer))
                        return false; // a MESH-AFFECTING change — falls through to the full rebuild
                    survivors.Add((slot, declaredOrder, newLayer));
                    claimedSlots.Add(slot);
                    continue;
                }

                // Not a rendered survivor: ignore an unrendered layer carried over unchanged, refuse a
                // genuine ADD or a changed unrendered one — this diff cannot tell if it would now render.
                if (newLayer.Id == null
                 || !oldDeclaredById.TryGetValue(newLayer.Id, out StyleLayer oldDeclared)
                 || JsonCanonical.Write(oldDeclared.Raw) != JsonCanonical.Write(newLayer.Raw))
                    return false;
            }

            // Removal fence, by predicate (still read-only): refuse when a removed slot's layer is referenced
            // by a list this arm never refreshes — MapView._symbolRenderLayers is the only one.
            for (int i = 0; i < _layers.Count; i++)
                if (_layers[i] is SymbolRenderLayer && !claimedSlots.Contains(i)) return false;

            // Only now mutate. Any old slot not claimed above is a removal.
            foreach (var (slot, declaredOrder, newLayer) in survivors)
            {
                _layers[slot].Restyle(newLayer, transition, nowSeconds);
                _layers[slot].SetDrawOrder(declaredOrder);
            }
            for (int i = 0; i < _layers.Count; i++)
            {
                if (_layers[i].StyleLayer == null) continue; // already a tombstone
                if (claimedSlots.Contains(i)) continue;
                _layers[i].Dispose();
                _layers[i] = new TombstoneRenderLayer(i);
            }

            // Re-push the sheet now: re-binding zeroed _PatternRect, and the memo would otherwise suppress the
            // re-resolve, leaving a pattern layer clipped until the sheet reference changes.
            SpriteAtlasView atlas = _spriteAtlas;
            Texture2D       tex   = _spriteTexture;
            _spriteAtlas   = null;
            _spriteTexture = null;
            SetSprites(atlas, tex);

            return true;
        }

        /// <summary>
        /// Pushes the style's sprite sheet to every layer that paints from it (see
        /// <see cref="ISpriteConsumerRenderLayer"/>). Called each frame from the map's tick, because the sheet
        /// can arrive, or be dropped by a restyle, at any point after <see cref="Build"/>. Early-outs on an
        /// unchanged reference pair. The memo lives here, so <see cref="ClearLayers"/> resets it for the
        /// rebuilt layers, which start unresolved.
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
            _fades.Clear();
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
    /// slot, and why.</summary>
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
