// Namespace-collision guard (see GlyphAtlasTexture.cs's header): this file is in MapRenderer.Unity.Text.
// It uses NO Unity.Mathematics types, so there is no float2/double3 trap to avoid here.

using System;
using System.Collections.Generic;
using Cysharp.Threading.Tasks;
using Unity.Profiling;
using UnityEngine;
using MapRenderer.Core.Geo;
using MapRenderer.Core.Mvt;
using MapRenderer.Core.Style;
using MapRenderer.Core.Text;
using MapRenderer.Core.Text.Placement;
using MapRenderer.Unity.Rendering.Map;
using MapRenderer.Unity.Rendering.Materials;
using MapRenderer.Unity.Rendering.Source;
using MapRenderer.Unity.Rendering.Tile;
using MapRenderer.Unity.Common;
using SymbolStyle = MapRenderer.Core.Style.Symbol;

namespace MapRenderer.Unity.Text
{
    /// <summary>
    /// S105 — the DECOUPLED production symbol-label subsystem: it owns the shared production
    /// <see cref="GlyphManager"/> + fixed-size <see cref="GlyphAtlasTexture"/> +
    /// <see cref="StyledSymbolTileBuilder"/>. A-1 split of concerns: label DATA arrives via the
    /// <see cref="Tile.TileManager"/> <c>SymbolTileBytesReady</c> push (already-fetched MVT bytes — no double
    /// download, never touching the mesh/disposal pipeline), while the tile LIFECYCLE is PULLED — each frame
    /// <see cref="ReconcileLoadedTiles"/> takes TileManager's current loaded set and reconciles which labels are
    /// active/kept-warm (retiring the fragile release/restore push-callbacks). Labels are the placed-every-frame
    /// class, so this feeds <see cref="LabelPlacementSystem"/> via <see cref="CollectInto"/>, never the static
    /// tile-render backend (S20 T5).
    ///
    /// <para><b>Fixed atlas.</b> The glyph atlas is allocated big and FIXED (<see cref="AtlasDimension"/>,
    /// clamped to the GPU max) so its <c>Size</c> never changes as tiles append glyphs — a growing atlas
    /// would invalidate earlier tiles' baked UVs (glyph-atlas-uv-growth-staleness lesson). Overflow (a
    /// glyph set larger than the fixed atlas) degrades gracefully and is logged, never silent.</para>
    /// </summary>
    internal sealed class SymbolLabelSubsystem : IDisposable
    {
        /// <summary>Target atlas edge in px, clamped to the GPU's max texture size. R8, so 4096² ≈ 16 MB.</summary>
        private const int AtlasDimension = 4096;

        private readonly MapCamera _camera;
        private readonly MapMaterialSet _materialSet;

        private GlyphManager _glyphManager;
        private GlyphAtlasTexture _atlasTexture;
        private StyledSymbolTileBuilder _builder;

        // Flat symbol-layer list (index == LabelInstance.MaterialIndex), one per-layer material each (a
        // SymbolText clone with this layer's text-halo-* bound), and a source id → its layers' GLOBAL
        // indices map (only sources with symbol layers are observed).
        private readonly List<SymbolStyle.StyleLayer> _allSymbolLayers = new();
        private Material[] _layerMaterials = Array.Empty<Material>();
        private Dictionary<string, List<int>> _layersBySource;

        private static readonly int HaloColorId = Shader.PropertyToID("_HaloColor");
        private static readonly int HaloWidthId = Shader.PropertyToID("_HaloWidthPx");
        private static readonly int HaloBlurId = Shader.PropertyToID("_HaloBlurPx");

        // Per-tile build markers (Profiler window → "MapRenderer.Symbol"). Only the SYNCHRONOUS main-thread
        // stages are marked — the shaping BuildAsync is awaited (its wall-clock includes glyph-fetch
        // suspension, not CPU), so it is deliberately left unmarked to avoid polluting the timeline.
        private static readonly ProfilerMarker PmTileDecode =
            new ProfilerMarker(ProfilerCategory.Scripts, "MapRenderer.Symbol.TileDecode");
        private static readonly ProfilerMarker PmAtlasUpload =
            new ProfilerMarker(ProfilerCategory.Scripts, "MapRenderer.Symbol.AtlasUpload");

        // Per-(source, tile) built labels, with the active/cached lifecycle that mirrors the tile MESH cache
        // (Model B) so labels survive a leave-cover → cache-hit → re-enter-cover round trip. Sized to the
        // prepared mesh cache's count cap so a cached tile's labels always outlive its meshes.
        private readonly SymbolTileLabelStore _store;
        // A-1: whether the prepared mesh cache is enabled — drives keep-warm-on-release. Enabled ⇒ a released
        // tile can return via a cache HIT (no re-fetch), so keep its labels warm to restore them; disabled ⇒
        // a revisit always re-fetches (→ rebuild), so keeping warm is pointless → drop on release.
        private readonly bool _cacheEnabled;
        // A-1: reused scratch for the per-frame reconcile — LoadedTileKey (source, tile) mapped to store keys,
        // filtered to sources that actually have symbol layers. Never reallocated in steady state.
        private readonly List<SymbolTileLabelStore.Key> _reconcileKeys = new();
        private int _lastUploadedGlyphCount;
        private bool _loggedOverflow;

        /// <param name="preparedCacheMaxCount">The <c>PreparedTileCache</c>'s entry cap — bounds how many
        /// out-of-cover tiles' labels are kept warm (clamped to a finite hard cap inside the store even when
        /// this is 0/unbounded).</param>
        /// <param name="cacheEnabled">The prepared mesh cache's master toggle — see <see cref="_cacheEnabled"/>.</param>
        public SymbolLabelSubsystem(MapCamera camera, MapMaterialSet materialSet,
            int preparedCacheMaxCount = 0, bool cacheEnabled = true)
        {
            _camera = camera ?? throw new ArgumentNullException(nameof(camera));
            _materialSet = materialSet;
            _cacheEnabled = cacheEnabled;
            _store = new SymbolTileLabelStore(preparedCacheMaxCount);
        }

        /// <summary>The per-symbol-layer materials indexed by <see cref="LabelInstance.MaterialIndex"/> —
        /// handed to <c>LabelPlacementSystem.Tick</c> so each layer's survivors draw with their own halo.</summary>
        public IReadOnlyList<Material> LayerMaterials => _layerMaterials;

        /// <summary>True once <see cref="SetStyle"/> found at least one symbol layer — MapView prefers this
        /// subsystem over the demo <c>LabelInstances</c> seam only when true.</summary>
        public bool HasSymbolLayers => _layersBySource != null && _layersBySource.Count > 0;

        /// <summary>The shared SDF atlas texture backing every collected label's UVs (null before the first
        /// glyphs upload).</summary>
        public GlyphAtlasTexture Atlas => _atlasTexture;

        /// <summary>B-1: the collected label set's monotonic version (<see cref="SymbolTileLabelStore.Version"/>),
        /// threaded into <see cref="LabelPlacementSystem.Tick"/> so a static frame (unchanged set + camera + fades)
        /// re-submits the cached meshes instead of re-projecting. Bumped by every set-changing store mutation.</summary>
        public long Version => _store.Version;

        /// <summary>Active (in-cover) label-tile count — telemetry.</summary>
        public int ActiveTileCount => _store.ActiveTileCount;

        /// <summary>Cached (out-of-cover, kept-warm) label-tile count — telemetry (the labels held so a
        /// prepared-cache hit re-shows them without a re-fetch).</summary>
        public int CachedTileCount => _store.CachedTileCount;

        /// <summary>
        /// Rebuild for a new style: group its symbol layers by source and (re)create the shared glyph
        /// pipeline from the style's <c>glyphs</c> URL. Idempotent — safe to call on every restyle.
        /// </summary>
        public void SetStyle(StyleDocument style)
        {
            _store.Clear();
            _lastUploadedGlyphCount = 0;
            _loggedOverflow = false;
            DisposePipeline();

            _allSymbolLayers.Clear();
            _layersBySource = new Dictionary<string, List<int>>();
            if (style?.Layers != null)
            {
                foreach (StyleLayer layer in style.Layers)
                {
                    if (!(layer is SymbolStyle.StyleLayer symbol) || symbol.Source == null) continue;
                    int index = _allSymbolLayers.Count; // == this layer's MaterialIndex
                    _allSymbolLayers.Add(symbol);
                    if (!_layersBySource.TryGetValue(symbol.Source, out List<int> indices))
                        _layersBySource[symbol.Source] = indices = new List<int>();
                    indices.Add(index);
                }
            }
            if (_layersBySource.Count == 0) return; // no symbol layers — stay idle (demo seam still works)

            // Per-layer materials: one SymbolText clone each, with this layer's text-halo-* bound by name
            // (F1). text-color/opacity still bake per-vertex (LabelPaint). Halo is evaluated at the current
            // zoom — constant halo is exact; a zoom-expression halo won't track zoom (a first-cut limit).
            // Built INDEPENDENTLY of the glyph pipeline so a style without a glyphs URL still wires cleanly.
            Material baseMat = _materialSet != null ? _materialSet.SymbolText : null;
            if (baseMat == null)
                Debug.LogWarning("[SymbolLabelSubsystem] MapMaterialSet.SymbolText unassigned — labels will not render.");
            _layerMaterials = new Material[_allSymbolLayers.Count];
            double zoom = _camera.CurrentProperties.Zoom;
            for (int i = 0; i < _allSymbolLayers.Count; i++)
            {
                if (baseMat == null) { _layerMaterials[i] = null; continue; }
                Material m = baseMat.CloneWithParent();
                m.name = $"MapSymbolText_Layer{i}";
                BindHalo(m, _allSymbolLayers[i].Paint, zoom);
                _layerMaterials[i] = m;
            }

            // The glyph pipeline needs the style's glyphs URL. Without it there are no glyphs to shape, so
            // leave _builder null (OnTileBytesReady no-ops) rather than throw — the labels just don't render.
            if (string.IsNullOrEmpty(style.Glyphs))
            {
                Debug.LogWarning("[SymbolLabelSubsystem] style has no 'glyphs' URL — symbol labels will not render.");
                return;
            }
            int dim = Math.Min(SystemInfo.maxTextureSize, AtlasDimension);
            _glyphManager = new GlyphManager(GlyphSourceFactory.Create(style), new GlyphAtlas(dim, dim));
            _atlasTexture = new GlyphAtlasTexture();
            _builder = new StyledSymbolTileBuilder(_glyphManager);
        }

        private static void BindHalo(Material material, SymbolStyle.PaintProperties paint, double zoom)
        {
            // Constant/zoom halo only (the locked first-cut scope). A data-driven (Feature/Composite) halo
            // would throw from Evaluate(zoom) — leave the material's inherited base halo rather than fault
            // the whole style load (data-driven halo is a documented follow-up).
            try
            {
                var haloColor = paint.HaloColor.Evaluate(zoom); // MapRenderer.Core.Expressions.Color (sRGB)
                // sRGB→linear (project is Linear color space; the shader consumes _HaloColor directly, and
                // SetColor uploads raw floats with no gamma conversion) — mirrors the vertex text-color bake
                // in LabelPlacementSystem and StyledFill/LineTileBuilder's Color.linear convention.
                material.SetColor(HaloColorId,
                    new Color((float)haloColor.R, (float)haloColor.G, (float)haloColor.B, (float)haloColor.A).linear);
                material.SetFloat(HaloWidthId, paint.HaloWidth.Evaluate(zoom));
                material.SetFloat(HaloBlurId, paint.HaloBlur.Evaluate(zoom));
            }
            catch (System.ArgumentException)
            {
                // data-driven halo not supported yet — keep the base material's halo.
            }
        }

        /// <summary>TileManager hook (MAIN THREAD): a tile's MVT bytes are ready — kick a deferred build for
        /// its source's symbol layers, if any. Never throws (TileManager also isolates, belt and braces).</summary>
        public void OnTileBytesReady(string sourceId, TileId tile, byte[] bytes)
        {
            if (_builder == null || bytes == null) return;
            if (!_layersBySource.TryGetValue(sourceId, out List<int> layerIndices)) return;
            BuildTileAsync(sourceId, tile, bytes, layerIndices).Forget();
        }

        /// <summary>
        /// A-1 PULL reconcile (MAIN THREAD, once per frame): given the tile pipeline's current loaded
        /// <c>(source, tile)</c> membership (from <see cref="TileManager.CollectLoadedTileKeys"/>), reconcile the
        /// label store — release tiles that left cover (kept warm iff the mesh cache is enabled), restore
        /// kept-warm labels for tiles that re-entered via a cache hit. Replaces the retired release/restore
        /// push-callbacks: self-healing (a membership change is corrected next frame) and reentrancy-free
        /// (nothing mutates mid-callback). Only keys for sources that actually have symbol layers are forwarded
        /// — a non-symbol source's tiles can never match a label entry, so they are filtered out here.
        /// </summary>
        public void ReconcileLoadedTiles(IReadOnlyList<LoadedTileKey> loaded)
        {
            if (_layersBySource == null) return; // no style set yet
            _reconcileKeys.Clear();
            for (int i = 0; i < loaded.Count; i++)
            {
                LoadedTileKey k = loaded[i];
                if (_layersBySource.ContainsKey(k.SourceId))
                    _reconcileKeys.Add(new SymbolTileLabelStore.Key(k.SourceId, k.Tile));
            }
            _store.ReconcileActiveSet(_reconcileKeys, _cacheEnabled);
        }

        private async UniTaskVoid BuildTileAsync(string sourceId, TileId tile, byte[] bytes, List<int> layerIndices)
        {
            var key = new SymbolTileLabelStore.Key(sourceId, tile);
            int gen = _store.BeginBuild(key); // reserve the active slot (collected as empty until committed)

            try
            {
                MvtTile mvt;
                using (PmTileDecode.Auto())
                    mvt = MvtDecoder.Decode(bytes); // decode-on-main (fast); a first-cut per F5 threading note
                double zoom = _camera.CurrentProperties.Zoom;

                // The source's layers + their global material indices (parallel lists), so each built label
                // is stamped with its owning layer's MaterialIndex for the per-material draw grouping.
                var layers = new List<SymbolStyle.StyleLayer>(layerIndices.Count);
                for (int k = 0; k < layerIndices.Count; k++) layers.Add(_allSymbolLayers[layerIndices[k]]);

                var labels = new List<LabelInstance>();
                await _builder.BuildAsync(mvt, tile, layers, zoom, _camera.Projection, labels, layerIndices);

                // Commit — unless superseded by a newer build OR dropped mid-build. A tile RELEASED-to-cache
                // mid-build is NOT stale: the store moved its entry to the cached side and CompleteBuild writes
                // the labels there, so a later cache hit restores them (the released-mid-build variant of the bug).
                if (!_store.CompleteBuild(key, gen, labels)) return;

                // Re-upload only when new glyphs actually landed in the shared atlas (most tiles after the
                // first few add none, since names repeat within a script).
                int glyphCount = _glyphManager.Atlas.Count;
                if (glyphCount > _lastUploadedGlyphCount)
                {
                    _lastUploadedGlyphCount = glyphCount;
                    using (PmAtlasUpload.Auto())
                        _atlasTexture.Upload(_glyphManager.Atlas);
                }
                WarnOnAtlasOverflow();
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[SymbolLabelSubsystem] label build failed for tile {tile} (source '{sourceId}'): {ex.Message}");
            }
        }

        /// <summary>Aggregate every loaded tile's labels into <paramref name="output"/> for this frame's
        /// <see cref="LabelPlacementSystem.Tick"/> (which then projects/collides/billboards them). A-3: point
        /// labels are deduped across tiles at a grid of one logical pixel at the CURRENT display zoom
        /// (<see cref="WebMercator.GroundResolution"/>) — so the same symbol from a parent + child tile during a
        /// zoom transition collapses to one, and the grid tracks zoom (a fixed grid cannot serve all zooms).</summary>
        public void CollectInto(List<LabelInstance> output)
            => _store.CollectInto(output, WebMercator.GroundResolution(_camera.CurrentProperties.Zoom));

        private void WarnOnAtlasOverflow()
        {
            if (_loggedOverflow || _glyphManager.Atlas.OverflowCount == 0) return;
            _loggedOverflow = true;
            Debug.LogWarning($"[SymbolLabelSubsystem] glyph atlas full ({_glyphManager.Atlas.OverflowCount} glyph(s) " +
                             $"dropped) — increase AtlasDimension beyond {Math.Min(SystemInfo.maxTextureSize, AtlasDimension)}px.");
        }

        public void Dispose()
        {
            _store.Clear();
            DisposePipeline();
        }

        private void DisposePipeline()
        {
            for (int i = 0; i < _layerMaterials.Length; i++)
                _layerMaterials[i].DestroySafely();
            _layerMaterials = Array.Empty<Material>();

            _atlasTexture?.Dispose();
            _atlasTexture = null;
            _glyphManager?.Dispose();
            _glyphManager = null;
            _builder = null;
        }
    }
}
