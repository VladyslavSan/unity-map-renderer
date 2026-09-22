namespace MapRenderer.Unity.Concurrency
{
    /// <summary>The single platform-selection SSOT: Inline on a real WebGL player (no background threads),
    /// ThreadPool everywhere else (editor, desktop, standalone). Schedulers are stateless, so one cached
    /// instance per policy is correct. <c>TileManager</c>'s selection reuses this — one #if, one place.</summary>
    internal static class WorkSchedulerFactory
    {
        public static IWorkScheduler ForCurrentPlatform() => Instance;
#if UNITY_WEBGL && !UNITY_EDITOR
        private static readonly IWorkScheduler Instance = new InlineWorkScheduler();
#else
        private static readonly IWorkScheduler Instance = new ThreadPoolWorkScheduler();
#endif
    }
}
