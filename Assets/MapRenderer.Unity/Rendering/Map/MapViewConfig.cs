using System;
using UnityEngine;

namespace MapRenderer.Unity.Rendering.Map
{
    /// <summary>
    /// The Inspector-tunable knobs for a <see cref="MapView"/>. A plain <c>[Serializable]</c> bundle so the
    /// config can be <c>[SerializeField]</c>'d on <see cref="MapViewComponent"/> (only a MonoBehaviour can
    /// carry serialized Inspector fields) and handed — by reference — to the plain <see cref="MapView"/>.
    ///
    /// <para>Shared by reference: MapView reads live from this object, so Inspector tweaks during Play take
    /// effect the same frame (the pre-decomposition "read every Tick, never snapshotted" contract).</para>
    /// </summary>
    [Serializable]
    public sealed class MapViewConfig
    {
        [Tooltip("S71: fixed safety ring (in tiles) added around the viewport-derived cover. Coverage itself " +
                 "comes from unprojecting the viewport corners — this is slop margin, NOT the coverage knob.")]
        public int PadTiles = 1;

        [Tooltip("Zoom clamp for tile selection.")]
        public int MinZoom = 0;
        public int MaxZoom = 14;

        [Tooltip("S87: Per-frame MESH-upload count budget — max tile-layer meshes uploaded + registered per " +
                 "Tick (responsiveness knob: bounds AddLayer/entity-add + GPU upload per frame). S87 made " +
                 "consume MESH-by-mesh, so a single rich tile no longer lands in one frame. Pair with " +
                 "MaxVerticesPerTick (whichever binds first stops the frame). Raise for faster fill, lower " +
                 "for smoother FPS while loading. NOTE: 0 BLOCKS consume entirely (not 'uncapped').")]
        public int MaxBuildsPerTick = 4;

        [Tooltip("S55: Max tessellation kick-offs per Tick (Phase 1 throttle). " +
                 "Caps how many background tessellation tasks are started per frame. Default 2 — " +
                 "tuned against the live Profiler to spread decode/earcut cost across frames.")]
        public int MaxTessellationsPerTick = 2;

        [Tooltip("S55/S87: Per-frame VERTEX budget for Phase-2 consume (S87: per-MESH granularity). " +
                 "Default 50000. Layer meshes are consumed until the running vertex total hits this budget, " +
                 "then the rest defer to the next frame (one-mesh overshoot). 0 = uncapped.")]
        public int MaxVerticesPerTick = 50000;

        [Tooltip("Tile render backend. Entities (default) = per-tile entity hierarchy via Entities " +
                 "Graphics, inspectable in the Entities Hierarchy. Brg = hand-packed BatchRendererGroup, " +
                 "the zero-allocation production path. GameObject = one MeshFilter+MeshRenderer child per " +
                 "tile-layer (SRP Batcher), the simplest Inspector-debuggable path.")]
        public RenderBackend Backend = RenderBackend.Entities;

        [Tooltip("Optional: base materials per rendering technique (MapMaterialSet asset). When set, each " +
                 "per-layer material is a CLONE of the matching base — a Material Variant in the Editor, so " +
                 "editing the base .mat in Play live-tunes every layer. When unset, legacy shader-built defaults " +
                 "are used.")]
        public Materials.MapMaterialSet MaterialSet;
    }
}
