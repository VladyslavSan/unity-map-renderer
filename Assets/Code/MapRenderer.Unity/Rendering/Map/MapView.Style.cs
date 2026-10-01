using System.Collections.Generic;
using System.Text;
using System.Threading;
using System;
using Cysharp.Threading.Tasks;
using UnityEngine;
using MapRenderer.Core.Data;
using MapRenderer.Core.Json;
using MapRenderer.Unity.Rendering.Source;
using MapRenderer.Unity.Style;
using Symbol = MapRenderer.Unity.Style.Symbol;

namespace MapRenderer.Unity.Rendering.Map
{
    public sealed partial class MapView
    {
        private StyleDocument _style;

        /// <summary>The document the live layers were last SUCCESSFULLY built or restyled from —
        /// the in-place gate's <c>previous</c>. Distinct from <see cref="_style"/>, which commits early and
        /// stays advanced after an aborted rebuild; this field is null from the gate until either arm's end,
        /// so an abort in between forces the next call down the full-rebuild arm.</summary>
        private StyleDocument _committedStyle;

        // The FillAntialiasing the TileManager.CurrentStyle token was folded from. It changes vertices, so a live
        // toggle between two SetStyle calls must fail the in-place gate rather than leave the token stale.
        private bool _committedFillAntialiasing;

        // The base material references the last full Layers.Build ran against. Non-obvious why: an in-place
        // restyle only re-binds existing appliers, so it cannot find a layer that a mutated MapMaterialSet
        // field now builds or skips; the gate refuses on any reference change (Unity's Equals), and
        // PreparedCacheTests' FillExtrusionMaterial*InPlace_ tests pin it.
        private (Material fill, Material line, Material fillExtrusion, Material symbolText, Material symbolIcon)
            _committedMaterials;

        private static (Material, Material, Material, Material, Material) MaterialSnapshot(Materials.MapMaterialSet set)
            => (set.FillMaterial, set.LineMaterial, set.FillExtrusionMaterial, set.SymbolTextWorld, set.SymbolIconWorld);

        // Reused scratch for SetStyle's symbol-layer derivation (below) — a restyle never allocates a
        // fresh list; the single registry (RenderLayerFactory) is walked once via Layers.Layers.
        private readonly List<Symbol.StyleLayer> _symbolStyleLayers = new List<Symbol.StyleLayer>();

        // ── SetStyle — the style is the single source of truth ─────────────────────────────────
        // No separate "Initialise": the map is valid at construction, and SetStyle loads or changes the data.


        /// <summary>The id of the active style.
        /// For <see cref="SetStyle(string,CancellationToken)"/> it is the style URI; for the
        /// <see cref="StyleDocument"/> overload it is the caller-supplied id.</summary>
        internal string StyleId { get; private set; }

        // Loader seams — production defaults; tests inject counting/offline fakes via InternalsVisibleTo.
        internal System.Func<string, CancellationToken, UniTask<string>> DocumentLoaderOverride;
        internal System.Func<TileUrlTemplate, IDataSource>                TileSourceFactoryOverride;

        /// <summary>The six mutation sites of <see cref="SetStyle(StyleDocument,string,CancellationToken)"/>'s
        /// full-rebuild arm after which an exception would leave persistent state a later call or frame
        /// reads (the predicate `docs/tile-pipeline-design.md` enumerates). Named for the site,
        /// in commit order.</summary>
        internal enum CommitPhase
        {
            /// <summary>After <see cref="_style"/>/<see cref="StyleId"/> commit — old style must stay fully live.</summary>
            IdentityCommitted,
            /// <summary>After the <see cref="_committedFillAntialiasing"/>/<see cref="_committedMaterials"/> memo
            /// write — the in-place gate's own memo must not outrun the build.</summary>
            MaterialMemoWritten,
            /// <summary>After <see cref="Rendering.Layers.RenderLayerSet.Build"/> — leak baseline from here on.</summary>
            LayersBuilt,
            /// <summary>After the <see cref="Tile.TileManager.CurrentStyle"/> token write.</summary>
            StyleTokenWritten,
            /// <summary>After <see cref="Text.SymbolSubsystem.SetStyle"/>.</summary>
            SymbolStyleApplied,
            /// <summary>Inside <see cref="Tile.TileManager.SetSources"/>'s teardown loop, once per record —
            /// the one phase whose OWN interior can throw mid-teardown.</summary>
            SourcesTeardownRecord,
        }

        /// <summary>Test seam: null in production (a per-call delegate check, not a per-frame one —
        /// this method is not a hot path). Set by a test to throw at a chosen <see cref="CommitPhase"/> and
        /// observe what the full-rebuild arm leaves behind. Reached via the existing
        /// <c>InternalsVisibleTo("MapRenderer.Tests.EditMode")</c> (<c>MapRenderer.Unity/AssemblyInfo.cs</c>).</summary>
        internal Action<CommitPhase> CommitProbe;

        /// <summary>
        /// Load a style from <paramref name="styleUri"/> (file:// or http(s)://), resolve each of its
        /// sources (inline <c>tiles[]</c>, else TileJSON), wire one data pipeline per source-id, and
        /// build the render layers — each fetching from ITS OWN source. <c>styleId == styleUri</c>.
        /// </summary>
        public async UniTask SetStyle(string styleUri, CancellationToken ct = default)
        {
            var           loader = DocumentLoaderOverride ?? StyleDocumentLoader.LoadTextAsync;
            string        json   = await loader(styleUri, ct);
            StyleDocument style;
            try
            {
                // Malformed JSON throws here, before the other overload commits anything. A malformed
                // expression does not: its property takes the default and lands in StyleDocument.Errors.
                style = StyleParser.Parse(json, _config.FillAntialiasing);
            }
            catch (Exception ex)
            {
                Debug.LogError($"[MapView.SetStyle] failed to parse style '{styleUri}' — keeping the previous style live. {ex}");
                return;
            }
            await SetStyle(style, styleUri, ct);
        }

        /// <summary>
        /// Applies an already-parsed <paramref name="style"/> with a caller-supplied
        /// <paramref name="styleId"/>. A second call RESTYLES: <see cref="RenderLayerSet"/> rebuilds and the
        /// source registry diffs (unchanged sources keep their warm pipeline; removed are torn down).
        /// </summary>
        public async UniTask SetStyle(StyleDocument style, string styleId, CancellationToken ct = default)
        {
            // Transactional restyle: the one await runs before any mutation, so a delayed or cancelled restyle
            // leaves the old style's layers, materials, backend and identity live and rendering.
            var specs = await MapViewSourceSpecs.Build(style, DocumentLoaderOverride, TileSourceFactoryOverride, ct);
            ct.ThrowIfCancellationRequested(); // last safe abort — nothing mutated yet (old style stays intact)
            foreach (string warning in style.Warnings) Debug.LogWarning($"[MapView.SetStyle] {warning}");
            foreach (string error in style.Errors) Debug.LogError($"[MapView.SetStyle] {error}");

            // TOCTOU: _config.MaterialSet is live-mutable, so capture it once and validate that same reference;
            // no await separates validation from Layers.Build. Across style loads the token's numbering fold guards.
            var materialSet = _config.MaterialSet;
            materialSet.Validate();

            // Fail loud HERE (before any commit) — a null Root at Digest's site (after Build) would
            // leave the new style/id/layers installed under the OLD token. Digest's own check must never fire.
            if (style.Root == null)
                throw new InvalidOperationException("StyleDocument.Root is null — the prepared cache's " +
                    "cache-key digest needs it (a hand-built StyleDocument must set Root).");

            // Nulled for this call: an abort below leaves it null, so the next call takes the full rebuild arm.
            // See docs/tile-pipeline-design.md § "Partial-survival restyle".
            StyleDocument   previous   = _committedStyle; // null on the first load — inPlace is false, as it must be
            _committedStyle            = null;
            Rendering.Layers.StyleTransition transition = StyleTransition;
            double          now        = NowSeconds;
            bool inPlace = previous != null
                        && _config.FillAntialiasing == _committedFillAntialiasing
                        && MaterialSnapshot(materialSet).Equals(_committedMaterials)
                        && TileManager.SourcesUnchanged(specs)
                        && Layers.TryRestyleInPlace(previous, style, transition, now);

            // Identity commits HERE (not before the await, HIGH b) — a delayed restyle must not run the OLD
            // layers/pipelines under the NEW cache token, and a cancel above must not report the new identity.
            _style  = style;
            StyleId = styleId;
            CommitProbe?.Invoke(CommitPhase.IdentityCommitted);

            // Re-applied on EVERY style change (in-place or full rebuild), clearing any runtime override. They ease
            // on the layer paint's transition and clock; LateUpdate moves them.
            Environment?.ApplyStyle(style.Light, style.Sky, Camera.CurrentProperties.Zoom, transition, now);

            if (inPlace)
            {
                // Unconditional here, including a pure reorder — docs/tile-pipeline-design.md,
                // "Partial-survival restyle".
                ApplyVisibilityGroups();
                TileManager.RestyleSourcesInPlace(specs, _config.Backend);

                // The in-place arm skips Layers.Build, the style token, SymbolSubsystem.SetStyle and the symbol
                // lists; see the same section.
                Layers.ApplyZoom(new Rendering.Layers.StyleFrameInputs(
                    Camera.CurrentProperties.Zoom, _config.DevicePixelRatio, now, transition));
                TileManager.PushLayerDrawGates(); // the restyle may have moved a layer's zoom range
                _committedStyle = style; // the in-place patch completed — the gate may trust it again
                return;
            }

            _committedFillAntialiasing = _config.FillAntialiasing;
            _committedMaterials        = MaterialSnapshot(materialSet);
            CommitProbe?.Invoke(CommitPhase.MaterialMemoWritten);

            Layers.Build(_style, Camera.CurrentProperties.Zoom, materialSet);
            CommitProbe?.Invoke(CommitPhase.LayersBuilt);
            // See StyleToken's own doc for what the digest folds in and why; set AFTER Build, since it
            // needs the built layer numbering.
            TileManager.CurrentStyle = new Tile.StyleToken(JsonCanonical.CacheKey(
                string.Empty, Rendering.Layers.MeshSignature.Document(_style, RenderedStyleLayers(Layers)), LayerNumbering(Layers) + "|aa=" + _config.FillAntialiasing
                + "|src=" + MapViewSourceSpecs.ResolvedSourceIdentity(specs)));
            CommitProbe?.Invoke(CommitPhase.StyleTokenWritten);
            LogSkippedLayers(Layers.SkippedLayers); // once per style load, never per tile/frame
            // Non-obvious why: Build seeds px uniforms at ratio 1, and this async continuation can resume after this
            // frame's LateUpdate, so restyled layers would draw loaded tiles once at the wrong device-pixel ratio.
            Layers.ApplyZoom(new Rendering.Layers.StyleFrameInputs(Camera.CurrentProperties.Zoom, _config.DevicePixelRatio, now, transition));
            TileManager.PushLayerDrawGates(); // fade advanced above; the gate must not lag it by a frame
            // Derive the symbol layers from the just-built set in one walk, filling two lists in the same order
            // so the subsystem's layer ordinal maps 1:1 to the SymbolRenderLayer that draws it.
            _symbolStyleLayers.Clear();
            _symbolRenderLayers.Clear();
            foreach (var layer in Layers.Layers)
                if (layer is Rendering.Layers.SymbolRenderLayer s)
                {
                    _symbolStyleLayers.Add(s.SymbolLayer);
                    _symbolRenderLayers.Add(s);
                }

            SymbolSubsystem.DevicePixelRatio = _config.DevicePixelRatio;
            SymbolSubsystem.SetStyle(_style,
                _symbolStyleLayers); // group symbol layers + (re)build the shared glyph pipeline
            CommitProbe?.Invoke(CommitPhase.SymbolStyleApplied);

            ApplyVisibilityGroups();
            TileManager.SetSources(specs, _config.Backend, RecordProbeOrNull());
            _committedStyle = style; // the rebuild completed — the gate may trust it again
        }

        /// <summary>The <see cref="CommitPhase.SourcesTeardownRecord"/> half of <see cref="CommitProbe"/>
        /// — its ONE phase whose site lives inside <see cref="Tile.TileManager.SetSources"/>, not here, so it
        /// has to cross the call as a delegate rather than an inline invoke. Null when no probe is installed
        /// (production; also a per-call, not per-frame, allocation when one is).</summary>
        private Action RecordProbeOrNull()
            => CommitProbe != null ? () => CommitProbe(CommitPhase.SourcesTeardownRecord) : null;

        /// <summary>Warns once, naming every layer <see cref="Rendering.Layers.RenderLayerSet.Build"/> skipped
        /// for a compatibility reason (unsupported kind / unconfigured material / unsupported filter). By-design
        /// skips stay silent: <see cref="Rendering.Layers.LayerSkipReason.GenuinelyUnpainted"/>,
        /// <see cref="Rendering.Layers.LayerSkipReason.Hidden"/> and <see cref="Rendering.Layers.LayerSkipReason.FullyTransparent"/>.
        /// Internal so a test can exercise the suppression directly.</summary>
        internal static void LogSkippedLayers(IReadOnlyList<Rendering.Layers.SkippedLayer> skipped)
        {
            var problems = new List<string>();
            for (int i = 0; i < skipped.Count; i++)
            {
                Rendering.Layers.SkippedLayer s = skipped[i];
                if (s.Reason is Rendering.Layers.LayerSkipReason.GenuinelyUnpainted
                             or Rendering.Layers.LayerSkipReason.Hidden
                             or Rendering.Layers.LayerSkipReason.FullyTransparent) continue;
                string detail = s.Detail != null ? $" — {s.Detail}" : "";
                problems.Add($"'{s.Id}' ({s.RawType}): {s.Reason}{detail}");
            }
            if (problems.Count > 0)
                Debug.LogWarning(
                    $"[MapView.SetStyle] {problems.Count} style layer(s) not rendered: {string.Join(", ", problems)}");
        }

        /// <summary>The style layer behind each slot of <paramref name="layers"/>, in slot order.</summary>
        private static List<StyleLayer> RenderedStyleLayers(Rendering.Layers.RenderLayerSet layers)
        {
            var styleLayers = new List<StyleLayer>(layers.Count);
            for (int li = 0; li < layers.Count; li++) styleLayers.Add(layers[li].StyleLayer);
            return styleLayers;
        }

        /// <summary>A plain-text encoding of the dense (index, id) pairs the layer set just built —
        /// folded into the cache token so a numbering shift (a skipped/added layer, from EITHER the style or a
        /// slot-dropping <c>MapMaterialSet</c> field) changes the token even under unchanged style content.
        /// <c>RenderLayerCompatibilitySummaryTests</c> pins that it folds the (index, id) PAIRS, not just
        /// <see cref="Rendering.Layers.RenderLayerSet.Count"/> — why per-index not count is in <c>docs/tile-pipeline-design.md</c>.</summary>
        internal static string LayerNumbering(Rendering.Layers.RenderLayerSet layers)
        {
            var sb = new StringBuilder();
            for (int li = 0; li < layers.Count; li++)
                sb.Append(li).Append(':').Append(layers[li].StyleLayer?.Id).Append('|');
            return sb.ToString();
        }
    }
}
