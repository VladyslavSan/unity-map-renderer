// One spy shared by MeshBuildWorkSchedulerTests and SymbolSubsystemWorkSchedulerTests instead of two
// copies that drift apart. NOT in core-tests.csproj — IWorkScheduler is engine-side.

using System;
using System.Collections.Generic;
using System.Threading;
using MapRenderer.Unity.Concurrency;

namespace MapRenderer.Tests
{
    /// <summary>Wraps a real <see cref="IWorkScheduler"/> and records at BOTH ends: that
    /// <see cref="Schedule{T}"/> was reached (<see cref="ScheduleCount"/> — placement) and which thread
    /// actually ran the body (<see cref="BodyThreadIds"/> — policy). A test that only asserted a scheduler
    /// exists would prove neither.</summary>
    internal sealed class RecordingWorkScheduler : IWorkScheduler
    {
        private readonly IWorkScheduler _inner;
        public readonly List<int> BodyThreadIds = new(); // one entry per body actually executed
        public int ScheduleCount;

        public RecordingWorkScheduler(IWorkScheduler inner) => _inner = inner;

        // Forwards, per IWorkScheduler.RunsInline's contract: a decorator that hard-coded it would defeat
        // the mutual-exclusion guards (MeshBuildGateForTest, SymbolReconciler.GateForTest) behind this spy.
        public bool RunsInline => _inner.RunsInline;

        public WorkHandle<T> Schedule<T>(Func<CancellationToken, T> body, CancellationToken ct = default)
        {
            ScheduleCount++;
            return _inner.Schedule(c =>
            {
                lock (BodyThreadIds) BodyThreadIds.Add(Environment.CurrentManagedThreadId);
                return body(c);
            }, ct);
        }
    }
}
