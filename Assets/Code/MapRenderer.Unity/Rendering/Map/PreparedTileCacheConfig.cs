using System;
using UnityEngine;

namespace MapRenderer.Unity.Rendering.Map
{
    /// <summary>
    /// The Inspector-tunable knobs for the <see cref="Tile.PreparedTileCache"/> — grouped under one
    /// <see cref="MapViewConfig.PreparedCache"/> field instead of loose sibling fields on
    /// <see cref="MapViewConfig"/>.
    /// <c>[Serializable]</c> + plain public fields (NOT the init-only data-carrier convention) — this is an
    /// Inspector-tunable knob, mirroring how <see cref="MapViewConfig"/> itself is written (the
    /// Unity-serialization exception to the init-only-carriers rule).
    /// </summary>
    [Serializable]
    public struct PreparedTileCacheConfig
    {
        [Tooltip("Master toggle for the PreparedTileCache. Disabled reverts to the uncached behaviour " +
                 "exactly — every revisit re-fetches/re-builds/re-uploads, and a released tile's " +
                 "meshes are destroyed immediately instead of transferred to the cache. Default true.")]
        public bool Enabled;

        [Tooltip("Byte budget for the PreparedTileCache (built tile-layer Meshes kept for a revisit/" +
                 "style-toggle, evicted LRU once exceeded). Only consulted while Enabled. Placeholder " +
                 "default (128 MiB) pending the maintainer's in-editor VRAM profiling — tune " +
                 "once measured.")]
        public long ByteBudget;

        [Tooltip("Belt-and-suspenders entry-count cap for the PreparedTileCache, in addition to the " +
                 "byte budget (whichever binds first evicts). Only consulted while Enabled. Placeholder " +
                 "default pending profiling.")]
        public int MaxCount;
    }
}
