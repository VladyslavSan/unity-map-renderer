using Cysharp.Threading.Tasks;

namespace MapRenderer.Unity.Concurrency
{
    /// <summary>Pollable handle to scheduled work, backed by a <see cref="UniTaskCompletionSource{T}"/> the
    /// scheduler completes. Struct-over-source (mirrors UniTask's own shape). <see cref="ToUniTask"/> is the
    /// Stage-1 bridge into the I/O-composed await chains that (correctly) stay UniTask (design §2.4).
    /// <para>A <see langword="default"/> handle carries no source: every member throws. Validity is the
    /// owner's own flag, never the handle's — unlike <c>default(UniTask{T})</c>, which silently reports
    /// succeeded.</para></summary>
    internal readonly struct WorkHandle<T>
    {
        private readonly UniTaskCompletionSource<T> _source;

        internal WorkHandle(UniTaskCompletionSource<T> source) => _source = source;

        /// <summary>Terminal (succeeded / faulted / cancelled).</summary>
        public bool IsCompleted => _source.Task.Status != UniTaskStatus.Pending;

        /// <summary>The body threw (non-cancellation).</summary>
        public bool IsFaulted => _source.Task.Status == UniTaskStatus.Faulted;

        /// <summary>The token was signalled before/during the body.</summary>
        public bool IsCancelled => _source.Task.Status == UniTaskStatus.Canceled;

        /// <summary>Terminal AND the body returned a value (neither faulted nor cancelled) — the 1:1
        /// replacement for the <c>Status == UniTaskStatus.Succeeded</c> branch at the consume/dispose
        /// sites.</summary>
        public bool IsSucceeded => _source.Task.Status == UniTaskStatus.Succeeded;

        /// <summary>Repeatable once terminal (the source is not re-awaited destructively); throws if pending,
        /// surfaces the body's exception if faulted or cancelled — same contract as today's
        /// <c>GetAwaiter().GetResult()</c> on a completed task.</summary>
        public T GetResult() => _source.Task.GetAwaiter().GetResult();

        /// <summary>The bridge into the UniTask I/O chain — a UniTask-native handoff, NOT the banned
        /// Task↔UniTask interop, so it does not touch the #716 CS0012 trap (async-architecture.md).</summary>
        public UniTask<T> ToUniTask() => _source.Task;
    }
}
