#if ENABLE_PROFILER
namespace MapRenderer.Unity.Rendering.Map
{
    /// <summary>
    /// The <see cref="ProfilerCounterTelemetry"/> half of <see cref="MapViewComponent"/>, kept out of the
    /// glue class so that class carries no conditional compilation. The whole file is inside
    /// <c>ENABLE_PROFILER</c>; in a release player the partial methods have no implementation, so the
    /// compiler erases their call sites. Owned by the component so it is never missing when a build
    /// misbehaves; <see cref="ProfilerCounterTelemetry.Mirror"/> early-outs unless the profiler is recording.
    /// </summary>
    public sealed partial class MapViewComponent
    {
        private ProfilerCounterTelemetry _counters;

        partial void AttachTelemetryCounters() => _counters = new ProfilerCounterTelemetry(View);

        // Drops the consumer's reference to a view that is being torn down or replaced, so a later frame cannot
        // mirror off a dead view. Nothing to unsubscribe — the consumer pulls.
        partial void ReleaseTelemetryCounters() => _counters = null;

        partial void MirrorTelemetryCounters() => _counters?.Mirror();
    }
}
#endif
