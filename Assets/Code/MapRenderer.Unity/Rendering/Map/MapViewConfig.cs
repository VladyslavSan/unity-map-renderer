using System;
using UnityEngine;
using MapRenderer.Unity.View;

namespace MapRenderer.Unity.Rendering.Map
{
    /// <summary>Who owns <see cref="MapViewConfig.DevicePixelRatio"/> at startup.</summary>
    public enum DevicePixelRatioMode
    {
        /// <summary>The host derives the ratio from the platform policy.</summary>
        Auto,

        /// <summary>The host keeps the serialized ratio.</summary>
        Manual,
    }

    /// <summary>
    /// The Inspector-tunable knobs for a <see cref="MapView"/>. A plain <c>[Serializable]</c> bundle so the
    /// config can be <c>[SerializeField]</c>'d on <see cref="MapViewComponent"/> (only a MonoBehaviour can
    /// carry serialized Inspector fields) and handed — by reference — to the plain <see cref="MapView"/>.
    ///
    /// <para>Shared by reference: MapView reads live from this object, so Inspector tweaks during Play take
    /// effect the same frame (the pre-decomposition "read every Update, never snapshotted" contract).</para>
    /// </summary>
    [Serializable]
    public sealed class MapViewConfig
    {
        [Tooltip("Frustum tile-selection, LOD, and far-plane tuning — the visible-tile cover knobs. Collapsible.")]
        public TileSelectionSettings TileSelection = new TileSelectionSettings();

        [Header("Display Scaling")]
        [Tooltip("Auto: the host sets the ratio at startup from the platform policy. " +
                 "Manual: the host keeps the value below (e.g. to force a value on a non-Apple HiDPI monitor).")]
        public DevicePixelRatioMode DevicePixelRatioMode = DevicePixelRatioMode.Auto;

        [Tooltip("Device-pixel-ratio used to normalise the live framebuffer to LOGICAL " +
                 "pixels for framing/selection (logicalPx = physicalPx / dpr), so an on-screen tile is the " +
                 "same PHYSICAL size across panel densities. In Auto mode the host overwrites this at startup " +
                 "from the platform policy (docs/device-pixel-ratio-design.md). In Manual mode, and in " +
                 "tests/headless (which drive Wire, not Start), this value is used as set. A value " +
                 "outside the plausible band (roughly a quarter to eight) degrades to 1 at the conversion. " +
                 "Default 1.")]
        public double DevicePixelRatio = 1.0;

        [Header("Performance Budgets")]
        [Tooltip("Per-frame MESH-upload count budget — max tile-layer meshes uploaded + registered per " +
                 "Update (responsiveness knob: bounds AddLayer/entity-add + GPU upload per frame). This budget makes " +
                 "consume MESH-by-mesh, so a single rich tile can span several frames. Pair with " +
                 "MaxVerticesPerTick (whichever binds first stops the frame). Raise for faster fill, lower " +
                 "for smoother FPS while loading. NOTE: 0 BLOCKS consume entirely (not 'uncapped').")]
        public int MaxConsumesPerTick = 4;

        [Tooltip("Max tiles admitted per Update (build throttle). " +
                 "Caps how many tiles are newly started per frame. Default 2 — " +
                 "tuned against the live Profiler to spread decode/earcut cost across frames.")]
        public int MaxMeshBuildsPerTick = 2;

        [Tooltip("Per-frame VERTEX budget for consume, at per-MESH granularity. " +
                 "Default 50000. Layer meshes are consumed until the running vertex total hits this budget, " +
                 "then the rest defer to the next frame (one-mesh overshoot). 0 = uncapped.")]
        public int MaxVerticesPerTick = 50000;

        [Tooltip("Stall #2: Per-frame budget of (tile, source) records fully RELEASED per Update (backend " +
                 "removal + mesh destroy/transfer + scheduler release). A zoom-out/fast-pan otherwise frees " +
                 "the whole departing cover in one frame — the mirror image of the budgeted consume. Records " +
                 "queued for release linger (still pumped) a few frames until drained. Default 4. 0 = uncapped.")]
        public int MaxReleasesPerTick = 4;

        [Tooltip("Tile-load smoothness: CONCURRENCY cap on admitted, not-yet-Built (tile,source) records — " +
                 "the whole request→consumed span (fetch/decode/kicked-or-in-flight-build/partial-consume). " +
                 "Distinct from the per-tick RATE caps above (MaxMeshBuildsPerTick/MaxConsumesPerTick/" +
                 "MaxVerticesPerTick/MaxReleasesPerTick bound work STARTED or FINISHED per frame; this bounds " +
                 "the total ACTIVE set at once). Without this cap, a cover-wide cover/zoom transition fetches " +
                 "every newly-entering tile in one burst. A tunable starting value — too low starves the " +
                 "fill (center paints, edges lag); too high reapproaches the old unbounded burst. Default 12. " +
                 "0 or negative = uncapped (reverts to today's unbounded admission).")]
        public int MaxConcurrentTileLoads = 12;

        [Tooltip("Tile-load smoothness: which render-space distance ranks not-yet-admitted tiles for " +
                 "loading (both admission order AND the PumpPending build/consume order use this — a " +
                 "corner tile must never win the per-tick kick/consume race just because of Dictionary " +
                 "enumeration order). GroundDistanceToLookAt (default) prioritizes what's actually centred " +
                 "on screen. CameraDistance prioritizes whatever the camera is nearest to — under tilt this " +
                 "favours the near/bottom edge of the frustum instead of the visual centre.")]
        public TilePriorityStrategy PriorityStrategy = TilePriorityStrategy.GroundDistanceToLookAt;

        [Header("Meshing")]
        [Tooltip("How much of each tile's MVT buffer the FILL meshes keep, in tile units at extent 4096 " +
                 "(scaled to the layer's own extent). Tiles carry geometry past their edge so neighbours " +
                 "join seamlessly, but drawing all of it makes adjacent tiles double-paint the overlap " +
                 "strip — a brighter band along every seam wherever the fill is translucent, plus ~6% " +
                 "overdraw. 0 = cut exactly at the tile boundary; 64 = keep the full standard buffer " +
                 "(pre-clip behaviour); NEGATIVE = disable the clip stage entirely, so no clip job runs. " +
                 "DEFAULT 0: the hairline crack a non-zero margin would hedge against was measured at " +
                 "ZERO pixels headlessly and confirmed on a real basemap, so there is nothing to hedge. " +
                 "Read live: a change starts a new bake revision, so a mesh baked under the OLD value is " +
                 "never served from the prepared-tile cache again. A tile already in cover rebuilds in the " +
                 "background, within the build and load caps, and swaps in when ready; each tile's upload " +
                 "lands in one frame. Old and new tiles can briefly show together.")]
        public double FillTileBufferClip = 0.0;

        [Tooltip("Default for fill antialiasing (the outward boundary band), applied to every fill layer " +
                 "whose style does NOT set fill-antialias. ON by default, matching the style spec. A layer " +
                 "that DOES set fill-antialias always wins - false stays band-free with this ON, true keeps " +
                 "its band with this OFF. The band is baked into the mesh, so a change takes effect on the " +
                 "next tile build, not on tiles already in cover.")]
        public bool FillAntialiasing = true;

        [Header("Style transitions")]
        [Min(0f)]
        [Tooltip("Seconds a restyled paint value takes to ease from its old value to its new one. 0 = snap. " +
                 "Read live every Update. Default 0.3.")]
        public double StyleTransitionDurationSeconds = Rendering.Layers.StyleTransition.Default.DurationSeconds;

        [Min(0f)]
        [Tooltip("Seconds a restyled paint value holds its old value before the ease starts. Read live every " +
                 "Update. Default 0.")]
        public double StyleTransitionDelaySeconds = Rendering.Layers.StyleTransition.Default.DelaySeconds;

        [Header("Symbols")]
        [Range(0f, 1f)]
        [Tooltip("Per-label far-distance cull, as a FRACTION of the camera far plane. Each label whose anchor is " +
                 "farther from the camera than this fraction × the far distance is skipped before project/collide/" +
                 "build (a previously-visible one fades out in place, same as every other cull). Read live every " +
                 "Update → tweak in Play to eyeball it. Default 1.0 (cull at the far plane " +
                 "— near-inert, since tile selection already frustum-bounds tiles by the same far); lower it to " +
                 "pull distant labels in closer than the full frustum depth. 0 = OFF: the cull distance collapses " +
                 "to zero, which the cull reads as its non-positive disable and keeps ALL labels (so the slider " +
                 "runs tightest just above 0, then flips to off at 0).")]
        public double SymbolMaxDistanceFraction = 1.0;

        [Header("Rendering")]
        [Tooltip("Tile render backend. Entities (default) = per-tile entity hierarchy via Entities " +
                 "Graphics, inspectable in the Entities Hierarchy. Brg = hand-packed BatchRendererGroup, " +
                 "the zero-allocation production path. GameObject = one MeshFilter+MeshRenderer child per " +
                 "tile-layer (SRP Batcher), the simplest Inspector-debuggable path.")]
        public RenderBackend Backend = RenderBackend.Entities;

        [Tooltip("Optional: base materials per rendering technique (MapMaterialSet asset). When set, each " +
                 "per-layer material is a CLONE of the matching base — a Material Variant in the Editor, so " +
                 "editing the base .mat in Play live-tunes every layer. When unset, legacy shader-built defaults " +
                 "are used. S4 (unlit epic): the referenced set's own RenderMode (Lit/Unlit) is what puts the " +
                 "whole view in lit or unlit mode — assign a Lit set for lit, an Unlit set for unlit.")]
        public Materials.MapMaterialSet MaterialSet;

        [Tooltip("PreparedTileCache knobs — Enabled (master toggle) + ByteBudget/MaxCount (LRU bounds). " +
                 "See PreparedTileCacheConfig's own field tooltips for detail.")]
        public PreparedTileCacheConfig PreparedCache = new PreparedTileCacheConfig
        {
            Enabled    = true,
            ByteBudget = 128L * 1024 * 1024,
            MaxCount   = 1024,
        };
    }

    /// <summary>
    /// The frustum tile-selection + LOD + far-plane knobs, grouped into a nested <c>[Serializable]</c> class so
    /// they render as one collapsible foldout in the Inspector. <see cref="MapView"/> reads them live every Update.
    /// This is the static, Inspector-authored tuning, not the per-frame <c>TileManager.TileSelectionConfig</c>
    /// (viewport + projection + budgets) the selector consumes.
    /// </summary>
    [Serializable]
    public sealed class TileSelectionSettings
    {
        [Tooltip("Zoom clamp for tile selection.")]
        public int MinZoom = 0;
        public int MaxZoom = 14;

        [Tooltip("The logical-pixel size a selected tile should occupy on screen — the field-standard " +
                 "512 convention MapLibre vector tiles are authored for. Enters ONLY as a selection-zoom " +
                 "offset (log2(TilePixelSize/OnScreenTilePx)); TilePixelSize is unified on 512, so the " +
                 "default 512 ⇒ offset 0 ⇒ camera zoom == tile zoom, ~4× fewer/larger tiles than the old 256 " +
                 "convention. Set 256 for the dense legacy density (offset +1, one level finer).")]
        public int OnScreenTilePx = 512;

        [Tooltip("Tile detail policy for the frustum selector. Flat = uniform single-zoom cover (previous " +
                 "behaviour). ScreenSpaceLod (DEFAULT) = near full-detail, far progressively coarser; best " +
                 "looking under tilt, most tiles. ProjectedArea = stops on the tile's true on-screen size; " +
                 "fewest tiles under tilt, visibly coarser — trades quality for frame time.")]
        public TileLodMode LodMode = TileLodMode.ScreenSpaceLod;

        [Tooltip("How far past a whole zoom level the camera must go before the map switches to the next level's " +
                 "tiles, in zoom levels (the map also holds the lower level that far below). Stops the tiles " +
                 "switching level on zoom jitter. 0 = switch exactly at the whole level; clamped to [0, 0.5]. " +
                 "Above 0.1 the tiles can look one level off the style zoom.")]
        public double ZoomLevelHysteresis = 0.05;

        [Tooltip("ScreenSpaceLod only. How far past its switch point a tile's size on screen must go before the " +
                 "tile changes detail level, in zoom levels. Stops boundary tiles flickering between two detail " +
                 "levels during a pan under tilt. 0 = switch exactly at the switch point; clamped to [0, 0.5].")]
        public double TileDetailHysteresis = 0.05;

        [Tooltip("How many zoom levels before a whole level the map starts preparing the next level's tiles (and the " +
                 "parent's on the way out). They load hidden and show in one step when the level switches. 0.05 " +
                 "covers a slow wheel zoom; a larger value prepares earlier and holds more tiles at once. Negative " +
                 "starts after the whole level, -1 never prepares. Clamped to [-1, 1].")]
        public double ZoomLevelPreload = 0.05;

        [Tooltip("Most prepared-ahead tiles loading at once, apart from the tiles the map is drawing. Lower keeps " +
                 "preparing from competing with the screen; higher prepares a whole level sooner. At least 1: a prepared " +
                 "tile is never cancelled, so the set must stay bounded.")]
        public int MaxConcurrentPrepareLoads = 4;

        [Tooltip("ProjectedArea only. 1.0 stops exactly at the target on-screen size; higher = coarser cover, " +
                 "fewer tiles, lower visual quality. It cannot be tuned to match ScreenSpaceLod — the two " +
                 "rules differ per tile, not by a constant.")]
        public double ProjectedAreaAggressiveness = 1.0;

        [Tooltip("FLAT Web-Mercator far-plane cap (GeometryAwareFarPlane): the render + selection far distance " +
                 "grows with tilt but is clamped to altitude × this. Higher = see/select farther under tilt " +
                 "(more tiles); lower = tighter cover. The camera and the tile selector share ONE policy, so " +
                 "this moves both together. Default 4.")]
        public double MercatorFarPlaneCap = 4.0;

        [Tooltip("GLOBE far-plane cap (RaySphereFarPlane): the horizon-aware far is clamped to altitude × this. " +
                 "The globe reaches farther under tilt than the flat plane, so its default is higher (8). LOWER " +
                 "this to cut globe over-cover under tilt (the Mercator-vs-globe tile-count gap at high pitch). " +
                 "Shared by the camera and the selector. Default 8.")]
        public double GlobeFarPlaneCap = 8.0;
    }
}
