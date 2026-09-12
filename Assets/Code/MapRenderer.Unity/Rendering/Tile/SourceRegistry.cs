using System.Collections.Generic;
using MapRenderer.Core.Geo;
using MapRenderer.Unity.Rendering.Tile.Processing;

namespace MapRenderer.Unity.Rendering.Tile
{
    /// <summary>Owns the per-source pipeline registry — one <see cref="ITileFeatureSource"/> slot per
    /// rendered source-id, plus the synthetic source-less slot for backgrounds. Callers address a
    /// pipeline by slot; <see cref="TileManager.LoadedKey"/> stores that slot, never a pipeline reference.
    /// <para><b>Contract: <see cref="Rebuild"/> assigns each pipeline a slot equal to its index.</b> A
    /// caller iterating <c>[0, Count)</c> may use the loop index as a slot directly.</para></summary>
    internal sealed class SourceRegistry : System.IDisposable
    {
        /// <summary>One data pipeline per rendered source-id — its slot is its index in <c>_pipelines</c>
        /// (the class-level contract), which keys <see cref="TileManager.LoadedKey"/> so the loop never
        /// hashes a string.</summary>
        private sealed class SourcePipeline
        {
            public string                SourceId; // the StyleLayer.Source this pipeline serves (normalized, never null)
            public ITileFeatureSource    FeatureSource;
            public int                   MinZoom; // resolved source minzoom — admission clamp
            public int                   MaxZoom; // resolved source maxzoom
            public TileManager.SourceKey DefKey;  // resolved-definition identity — restyle "unchanged?" diff

            /// <summary>True for the one synthetic pipeline serving a background layer — derived from
            /// <see cref="FeatureSource"/> so it can't desync.</summary>
            public bool IsSourceless => FeatureSource == null;
        }

        private readonly List<SourcePipeline> _pipelines = new(4);

        /// <summary>Slot count, including the synthetic source-less slot if present.</summary>
        public int Count => _pipelines.Count;

        /// <summary>Pipelines that actually own a feature source, excluding the synthetic background pipeline.</summary>
        public int RealSourceCount
        {
            get
            {
                int n = 0;
                for (int i = 0; i < _pipelines.Count; i++)
                    if (!_pipelines[i].IsSourceless) n++;
                return n;
            }
        }

        /// <summary>The in-flight fetch count summed across every source pipeline.</summary>
        public int TotalInFlight
        {
            get
            {
                int n = 0;
                for (int i = 0; i < _pipelines.Count; i++)
                {
                    if (_pipelines[i].IsSourceless) continue;
                    n += _pipelines[i].FeatureSource.InFlightCount;
                }

                return n;
            }
        }

        /// <summary>The source-id a slot's pipeline serves (empty for the source-less slot).</summary>
        public string SourceIdOf(int slot) => _pipelines[slot].SourceId;

        /// <summary>True for the synthetic background slot, which has no feature source.</summary>
        public bool IsSourceless(int slot) => _pipelines[slot].IsSourceless;

        /// <summary>True if a slot's source serves zoom <paramref name="z"/>.</summary>
        public bool AdmitsZoom(int slot, int z)
        {
            var p = _pipelines[slot];
            return z >= p.MinZoom && z <= p.MaxZoom;
        }

        /// <summary>The feature source at a slot, or null for the source-less slot.</summary>
        public ITileFeatureSource SourceAt(int slot) => _pipelines[slot].FeatureSource;

        /// <summary>Routes a tile release to the slot's owning pipeline — a no-op on the source-less slot.</summary>
        public void ReleaseTile(int slot, TileId id) => _pipelines[slot].FeatureSource?.Release(id);

        /// <summary>Diffs the registry against <paramref name="specs"/> by (SourceId, resolved Key),
        /// keeping unchanged pipelines and disposing removed ones, then commits stable slots
        /// <c>0..N-1</c> and appends the source-less slot last if <paramref name="hasBackground"/>.</summary>
        public void Rebuild(IReadOnlyList<TileManager.SourceSpec> specs, bool hasBackground)
        {
            var kept    = new List<SourcePipeline>(specs.Count);
            var keptOld = new HashSet<SourcePipeline>();
            for (int i = 0; i < specs.Count; i++)
            {
                var            spec     = specs[i];
                SourcePipeline existing = Find(spec.SourceId, spec.Key);
                if (existing != null)
                {
                    existing.MinZoom = spec.MinZoom; // zoom may be re-read from a re-resolved def; identity kept
                    existing.MaxZoom = spec.MaxZoom;
                    kept.Add(existing);
                    keptOld.Add(existing);
                }
                else
                {
                    kept.Add(new SourcePipeline
                    {
                        SourceId = spec.SourceId, FeatureSource = spec.CreateSource(),
                        MinZoom  = spec.MinZoom, MaxZoom        = spec.MaxZoom, DefKey = spec.Key,
                    });
                }
            }

            // Pipeline-teardown removed sources: dispose the feature source — fetch, scheduler and cache all live inside it now.
            for (int i = 0; i < _pipelines.Count; i++)
            {
                var p = _pipelines[i];
                if (keptOld.Contains(p)) continue;
                p.FeatureSource?.Dispose();
            }

            // Commit the new registry with stable slots 0..N-1 — the slot IS the index, nothing stores it twice.
            _pipelines.Clear();
            for (int i = 0; i < kept.Count; i++)
                _pipelines.Add(kept[i]);

            if (hasBackground)
            {
                _pipelines.Add(new SourcePipeline
                {
                    // Source left null ⇒ IsSourceless is true (computed). The synthetic background pipeline.
                    SourceId = string.Empty,
                    MinZoom  = int.MinValue, MaxZoom = int.MaxValue,
                });
            }
        }

        /// <summary>The existing pipeline with this source-id AND matching resolved key, or null.</summary>
        private SourcePipeline Find(string sourceId, TileManager.SourceKey key)
        {
            for (int i = 0; i < _pipelines.Count; i++)
                if (_pipelines[i].SourceId == sourceId && _pipelines[i].DefKey.Equals(key))
                    return _pipelines[i];

            return null;
        }

        /// <summary>Disposes and clears every source pipeline — one <see cref="ITileFeatureSource.Dispose"/>
        /// call per pipeline covers scheduler + owned source together. Idempotent.</summary>
        public void Dispose()
        {
            for (int i = 0; i < _pipelines.Count; i++)
                _pipelines[i].FeatureSource?.Dispose();
            _pipelines.Clear();
        }
    }
}
