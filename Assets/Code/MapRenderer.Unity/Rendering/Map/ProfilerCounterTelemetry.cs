#if ENABLE_PROFILER
using Unity.Profiling;
using MapRenderer.Unity.View;

namespace MapRenderer.Unity.Rendering.Map
{
    /// <summary>
    /// The telemetry consumer that makes the map's levels readable OUTSIDE the Editor: it mirrors each
    /// published snapshot into a <see cref="ProfilerCounterValue{T}"/> under a <c>MapRenderer</c> profiler
    /// category, so every count charts over time in the Profiler window AND in a **Development standalone
    /// build** — the only measurement the Editor cannot contaminate (<c>docs/telemetry-design.md</c>,
    /// the reason this consumer exists at all).
    ///
    /// <para><b>Costs nothing when nobody is profiling.</b> Two layers: the whole file compiles out of a
    /// release player (<c>ENABLE_PROFILER</c>), and <see cref="Mirror"/> early-outs on <c>Profiler.enabled</c>
    /// before touching a single provider — so the tile provider's cover-stats scan is never triggered. A static
    /// bool read per frame is the whole price of leaving this wired permanently.</para>
    ///
    /// <para><b>What is NOT charted:</b> <c>PreparedCacheEnabled</c>, <c>PreparedCacheMaxCount</c> and
    /// <c>PreparedCacheByteBudget</c>. They are configured limits, not levels — a flat line per frame telling
    /// you what you already set. The panel shows them; a chart would only add noise.</para>
    /// </summary>
    internal sealed class ProfilerCounterTelemetry
    {
        /// <summary>Counter name constants (SSOT), dotted + grouped to match
        /// <c>MapView.ProfilerMarkerNames</c>' existing <c>MapRenderer.View.LateUpdate</c> style so the
        /// Profiler's flat search groups markers and counters the same way.</summary>
        internal static class CounterNames
        {
            internal const string VisibleTiles      = "MapRenderer.Tiles.Visible";
            internal const string CoverColumns      = "MapRenderer.Tiles.CoverColumns";
            internal const string CoverRows         = "MapRenderer.Tiles.CoverRows";
            internal const string SelectionZoom     = "MapRenderer.Tiles.SelectionZoom";
            internal const string CoverMinZoom      = "MapRenderer.Tiles.CoverMinZoom";
            internal const string CoverMaxZoom      = "MapRenderer.Tiles.CoverMaxZoom";
            internal const string MixedZoom         = "MapRenderer.Tiles.MixedZoom";
            internal const string FractionalZoom    = "MapRenderer.Tiles.FractionalZoom";
            internal const string LoadedTiles       = "MapRenderer.Tiles.Loaded";
            internal const string PendingTiles      = "MapRenderer.Tiles.Pending";
            internal const string ConsumeBacklog    = "MapRenderer.Tiles.ConsumeBacklog";
            internal const string PrologueInFlight     = "MapRenderer.Tiles.PrologueInFlight";
            internal const string GraphMeasureInFlight = "MapRenderer.Tiles.GraphMeasureInFlight";
            internal const string GraphWriteInFlight   = "MapRenderer.Tiles.GraphWriteInFlight";
            internal const string InFlightFetches   = "MapRenderer.Tiles.InFlightFetches";
            internal const string ReleasedMidFlight = "MapRenderer.Tiles.ReleasedMidFlight";
            internal const string ReleasedMidFetch  = "MapRenderer.Tiles.ReleasedMidFetch";
            internal const string FetchErrors       = "MapRenderer.Tiles.FetchErrors";

            internal const string CacheHits         = "MapRenderer.Cache.Hits";
            internal const string CacheMisses       = "MapRenderer.Cache.Misses";
            internal const string CacheEntries      = "MapRenderer.Cache.Entries";
            internal const string CacheBytesHeld    = "MapRenderer.Cache.BytesHeld";
            internal const string CacheEvictions    = "MapRenderer.Cache.Evictions";

            internal const string ActiveSymbolTiles  = "MapRenderer.Symbols.ActiveSymbolTiles";
            internal const string CachedSymbolTiles  = "MapRenderer.Symbols.CachedSymbolTiles";
            internal const string InputSymbols       = "MapRenderer.Symbols.InputSymbols";
            internal const string DistanceCulled    = "MapRenderer.Symbols.DistanceCulled";
            internal const string HorizonCulled     = "MapRenderer.Symbols.HorizonCulled";
            internal const string ZoomCulled        = "MapRenderer.Symbols.ZoomCulled";
            internal const string CoverageDropped   = "MapRenderer.Symbols.CoverageDropped";
            internal const string CoverageFading    = "MapRenderer.Symbols.CoverageFading";
            internal const string Candidates        = "MapRenderer.Symbols.CollisionCandidates";
            internal const string Survivors         = "MapRenderer.Symbols.CollisionSurvivors";
            internal const string PlacedQuads       = "MapRenderer.Symbols.PlacedQuads";
            internal const string LiveFadeRecords   = "MapRenderer.Symbols.LiveFadeRecords";
            internal const string MirrorRebuilds    = "MapRenderer.Symbols.MirrorRebuilds";
        }

        private static readonly ProfilerCategory Category = new("MapRenderer");

        // Static (not per-instance): a counter is a named profiler slot registered with the profiler at
        // construction, so a rebuilt MapView must REUSE the slot rather than register a second counter under
        // the same name. Written every published frame, hence plain fields (not `readonly`).
        private static ProfilerCounterValue<int>    _visibleTiles      = Count(CounterNames.VisibleTiles);
        private static ProfilerCounterValue<int>    _coverColumns      = Count(CounterNames.CoverColumns);
        private static ProfilerCounterValue<int>    _coverRows         = Count(CounterNames.CoverRows);
        private static ProfilerCounterValue<int>    _selectionZoom     = Level(CounterNames.SelectionZoom);
        private static ProfilerCounterValue<int>    _coverMinZoom      = Level(CounterNames.CoverMinZoom);
        private static ProfilerCounterValue<int>    _coverMaxZoom      = Level(CounterNames.CoverMaxZoom);
        private static ProfilerCounterValue<int>    _mixedZoom         = Level(CounterNames.MixedZoom);
        private static ProfilerCounterValue<double> _fractionalZoom    = LevelDouble(CounterNames.FractionalZoom);
        private static ProfilerCounterValue<int>    _loadedTiles       = Count(CounterNames.LoadedTiles);
        private static ProfilerCounterValue<int>    _pendingTiles      = Count(CounterNames.PendingTiles);
        private static ProfilerCounterValue<int>    _consumeBacklog    = Count(CounterNames.ConsumeBacklog);
        private static ProfilerCounterValue<int>    _prologueInFlight     = Count(CounterNames.PrologueInFlight);
        private static ProfilerCounterValue<int>    _graphMeasureInFlight = Count(CounterNames.GraphMeasureInFlight);
        private static ProfilerCounterValue<int>    _graphWriteInFlight   = Count(CounterNames.GraphWriteInFlight);
        private static ProfilerCounterValue<int>    _inFlightFetches   = Count(CounterNames.InFlightFetches);
        private static ProfilerCounterValue<int>    _releasedMidFlight = Count(CounterNames.ReleasedMidFlight);
        private static ProfilerCounterValue<int>    _releasedMidFetch  = Count(CounterNames.ReleasedMidFetch);
        private static ProfilerCounterValue<int>    _fetchErrors       = Count(CounterNames.FetchErrors);

        private static ProfilerCounterValue<int>    _cacheHits         = Count(CounterNames.CacheHits);
        private static ProfilerCounterValue<int>    _cacheMisses       = Count(CounterNames.CacheMisses);
        private static ProfilerCounterValue<int>    _cacheEntries      = Count(CounterNames.CacheEntries);
        private static ProfilerCounterValue<long>   _cacheBytesHeld    = Bytes(CounterNames.CacheBytesHeld);
        private static ProfilerCounterValue<int>    _cacheEvictions    = Count(CounterNames.CacheEvictions);

        private static ProfilerCounterValue<int>    _activeSymbolTiles  = Count(CounterNames.ActiveSymbolTiles);
        private static ProfilerCounterValue<int>    _cachedSymbolTiles  = Count(CounterNames.CachedSymbolTiles);
        private static ProfilerCounterValue<int>    _inputSymbols       = Count(CounterNames.InputSymbols);
        private static ProfilerCounterValue<int>    _distanceCulled    = Count(CounterNames.DistanceCulled);
        private static ProfilerCounterValue<int>    _horizonCulled     = Count(CounterNames.HorizonCulled);
        private static ProfilerCounterValue<int>    _zoomCulled        = Count(CounterNames.ZoomCulled);
        private static ProfilerCounterValue<int>    _coverageDropped   = Count(CounterNames.CoverageDropped);
        private static ProfilerCounterValue<int>    _coverageFading    = Count(CounterNames.CoverageFading);
        private static ProfilerCounterValue<int>    _candidates        = Count(CounterNames.Candidates);
        private static ProfilerCounterValue<int>    _survivors         = Count(CounterNames.Survivors);
        private static ProfilerCounterValue<int>    _placedQuads       = Count(CounterNames.PlacedQuads);
        private static ProfilerCounterValue<int>    _liveFadeSymbols   = Count(CounterNames.LiveFadeRecords);
        private static ProfilerCounterValue<int>    _mirrorRebuilds    = Count(CounterNames.MirrorRebuilds);

        // FlushOnEndOfFrame WITHOUT ResetToZeroOnFlush: these are LEVELS (the snapshot contract), so a frame
        // that publishes nothing should hold the last value, not read as a zero the map never had.
        private static ProfilerCounterValue<int> Count(string name)
            => new(Category, name, ProfilerMarkerDataUnit.Count, ProfilerCounterOptions.FlushOnEndOfFrame);

        private static ProfilerCounterValue<int> Level(string name)
            => new(Category, name, ProfilerMarkerDataUnit.Undefined, ProfilerCounterOptions.FlushOnEndOfFrame);

        private static ProfilerCounterValue<double> LevelDouble(string name)
            => new(Category, name, ProfilerMarkerDataUnit.Undefined, ProfilerCounterOptions.FlushOnEndOfFrame);

        private static ProfilerCounterValue<long> Bytes(string name)
            => new(Category, name, ProfilerMarkerDataUnit.Bytes, ProfilerCounterOptions.FlushOnEndOfFrame);

        private readonly MapView _view;

        internal ProfilerCounterTelemetry(MapView view) => _view = view;

        /// <summary>
        /// Mirrors this frame's levels into the counters — call once per frame, after the providers have run.
        /// Returns immediately unless the profiler is recording, which is what keeps "costs nothing when
        /// nobody is looking" literally true: not profiling ⇒ the provider accessors are never touched ⇒ the tile
        /// provider's cover-stats scan never runs.
        ///
        /// <para>Reading through <c>ref readonly</c> keeps the whole path copy-free — the <c>in</c> parameters
        /// below are not decoration, they are what stops each snapshot being copied into these three methods.</para>
        /// </summary>
        internal void Mirror()
        {
            if (!UnityEngine.Profiling.Profiler.enabled) return;

            OnTileTelemetry(in _view.TileManager.Telemetry);
            OnSymbolStoreTelemetry(in _view.SymbolSubsystem.Telemetry);
            OnSymbolPlacementTelemetry(in _view.SymbolPlacementSystem.Telemetry);
        }

        private void OnTileTelemetry(in TileTelemetrySnapshot snap)
        {
            _visibleTiles.Value      = snap.VisibleTileCount;
            _coverColumns.Value      = snap.CoverColumns;
            _coverRows.Value         = snap.CoverRows;
            _selectionZoom.Value     = snap.SelectionZoom;
            _coverMinZoom.Value      = snap.CoverMinZoom;
            _coverMaxZoom.Value      = snap.CoverMaxZoom;
            _mixedZoom.Value         = snap.IsMixedZoom ? 1 : 0;   // no bool counter type; charted as 0/1
            _fractionalZoom.Value    = snap.FractionalZoom;
            _loadedTiles.Value       = snap.LoadedTileCount;
            _pendingTiles.Value      = snap.PendingTileCount;
            _consumeBacklog.Value    = snap.ConsumeBacklog;
            _prologueInFlight.Value     = snap.PrologueInFlight;
            _graphMeasureInFlight.Value = snap.GraphMeasureInFlight;
            _graphWriteInFlight.Value   = snap.GraphWriteInFlight;
            _inFlightFetches.Value   = snap.InFlightFetches;
            _releasedMidFlight.Value = snap.ReleasedMidFlightCount;
            _releasedMidFetch.Value  = snap.ReleasedMidFetchCount;
            _fetchErrors.Value       = snap.FetchErrorCount;

            _cacheHits.Value      = snap.PreparedCacheHits;
            _cacheMisses.Value    = snap.PreparedCacheMisses;
            _cacheEntries.Value   = snap.PreparedCacheEntryCount;
            _cacheBytesHeld.Value = snap.PreparedCacheBytesHeld;
            _cacheEvictions.Value = snap.PreparedCacheEvictions;
        }

        private void OnSymbolStoreTelemetry(in SymbolStoreTelemetrySnapshot store)
        {
            _activeSymbolTiles.Value = store.ActiveSymbolTiles;
            _cachedSymbolTiles.Value = store.CachedSymbolTiles;
            _coverageDropped.Value  = store.CoverageDroppedSymbols;
        }

        private void OnSymbolPlacementTelemetry(in SymbolPlacementTelemetrySnapshot placement)
        {
            _inputSymbols.Value     = placement.InputSymbolCount;
            _distanceCulled.Value  = placement.DistanceCulledSymbols;
            _horizonCulled.Value   = placement.HorizonCulledSymbols;
            _zoomCulled.Value      = placement.ZoomCulledSymbols;
            _coverageFading.Value  = placement.CoverageFadingSymbols;
            _candidates.Value      = placement.CollisionCandidateCount;
            _survivors.Value       = placement.CollisionSurvivorCount;
            _placedQuads.Value     = placement.PlacedQuadCount;
            _liveFadeSymbols.Value = placement.LiveFadeSymbolCount;
            _mirrorRebuilds.Value  = placement.MirrorRebuildCount;
        }
    }
}
#endif
