#if ENABLE_PROFILER
namespace MapRenderer.Unity.Rendering.Map
{
    /// <summary>
    /// The <see cref="ProfilerCounterTelemetry"/> half of <see cref="MapViewComponent"/> — everything the
    /// counter consumer needs to be owned and driven, kept out of the glue class so that class carries no
    /// conditional compilation at all. The WHOLE file is inside <c>ENABLE_PROFILER</c>: in a release player it
    /// contributes nothing, the partial methods declared on the other half have no implementation, and the C#
    /// compiler erases their call sites. That is what "stripped from release" means here — not a runtime flag.
    ///
    /// <para>Owned by the component rather than dropped into a scene deliberately: a consumer that exists only
    /// when someone remembered to add a component is missing exactly when a build misbehaves. It is affordable
    /// permanently because <see cref="ProfilerCounterTelemetry.Mirror"/> early-outs unless the profiler is
    /// recording, so an unprofiled frame never touches a provider.</para>
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
