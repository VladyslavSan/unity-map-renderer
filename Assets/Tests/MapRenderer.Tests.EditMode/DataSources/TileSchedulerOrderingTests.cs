// Engine-free: this file is compiled verbatim by both the Unity EditMode runner
// (Assets/Tests/MapRenderer.Tests.EditMode/) and the fast dotnet test project (Tools/core-tests/).
// Do NOT add any UnityEngine, MeshBuilder, NativeArray, or MonoBehaviour references, or a dependency
// on MapRenderer.Jobs.Mvt — see DataSourceTests.cs's header for why that file cannot make this claim.
//
// The scheduler-ordering invariant this file owns: TileScheduler.Request reserves the _inFlight/_cts
// slot synchronously, under its own lock, BEFORE a source's fetch can complete — so a synchronously-
// completing source's cleanup can never observe a slot nothing has assigned yet. No thread-pool hop is
// needed to establish that ordering, and none may be reintroduced.

using System;
using System.IO;
using System.Threading.Tasks;
using Cysharp.Threading.Tasks;
using NUnit.Framework;
using MapRenderer.Core.Data;
using MapRenderer.Core.Geo;

namespace MapRenderer.Tests.DataSources
{
    [TestFixture]
    public class TileSchedulerOrderingTests
    {
        // -----------------------------------------------------------------------------------------
        // Repo-root locator — same walk as DataSourceTests.LoadFixtureBytes, duplicated here rather
        // than hoisted into MapRenderer.Tests.Shared — a deliberate, deferred follow-up, not an oversight.
        // Do NOT use Application.dataPath — that would make this file engine-bound.
        // -----------------------------------------------------------------------------------------

        private static string FindRepoRoot()
        {
            string[] starts = new[]
            {
                Directory.GetCurrentDirectory(),         // Unity batch-mode cwd = project root
                AppContext.BaseDirectory,                // dotnet test output dir
            };

            foreach (string start in starts)
            {
                string dir = start;
                for (int i = 0; i < 16 && dir != null; i++)
                {
                    string candidate = Path.Combine(dir, "Assets", "Fixtures", "sample-tile.bytes");
                    if (File.Exists(candidate))
                        return dir;
                    dir = Directory.GetParent(dir)?.FullName;
                }
            }

            throw new FileNotFoundException(
                $"repo root not found. Tried walking up 16 levels from cwd={Directory.GetCurrentDirectory()}" +
                $" and AppContext.BaseDirectory={AppContext.BaseDirectory}");
        }

        private static string ReadSourceFile(params string[] relativeParts)
        {
            string root = FindRepoRoot();
            string path = Path.Combine(root, Path.Combine(relativeParts));
            FileAssert.Exists(path);
            return File.ReadAllText(path);
        }

        private static int CountOccurrences(string haystack, string needle)
        {
            int count = 0, index = 0;
            while ((index = haystack.IndexOf(needle, index, StringComparison.Ordinal)) >= 0)
            {
                count++;
                index += needle.Length;
            }
            return count;
        }

        private static TileResponse MakeResponse(byte seed)
            => new TileResponse(new byte[] { seed, (byte)(seed + 1), (byte)(seed + 2) }, TileEncoding.Mvt);

        // -----------------------------------------------------------------------------------------
        // T1 — the ordering invariant that holds with no thread-pool hop in TileScheduler.
        // -----------------------------------------------------------------------------------------

        /// <summary>
        /// When a source returns an already-completed UniTask (UniTask.FromResult), the sync-completion
        /// path must NOT leave a stale in-flight entry after Request() returns. The ordering invariant
        /// holds with no thread-pool hop present. Passing alone proves nothing without the
        /// reverse-injection RED-verify.
        /// </summary>
        [Test]
        public async Task SyncCompletingSource_NoStaleInFlightEntry()
        {
            int fetchCount = 0;
            var fakeSource = TestDataSource.FromFetch(id =>
            {
                System.Threading.Interlocked.Increment(ref fetchCount);
                // Sync completion: returns an already-resolved UniTask.
                return UniTask.FromResult(MakeResponse(77));
            });

            var cache     = new TileCache(capacity: 10);
            var scheduler = new TileScheduler(fakeSource, cache);
            var tileId    = new TileId { Z = 6, X = 6, Y = 6 };

            // Start the fetch (sync-completing source).
            var fetchTask = scheduler.Request(tileId);

            // Await completion so the async continuation has run.
            await fetchTask;

            // After completion the in-flight map must be empty for this tile.
            Assert.AreEqual(0, scheduler.InFlightCount,
                "In-flight map must be empty after a sync-completing fetch completes. A stale entry means " +
                "a sync-completing source's reentrant cleanup ran before Request()'s _inFlight assignment " +
                "was conditioned on the CTS reservation still being live.");
        }

        /// <summary>
        /// A source that THROWS synchronously from <c>FetchAsync</c> (not merely returns a faulted
        /// <c>UniTask</c>) exercises the catch block's cleanup reentrantly, the same way a
        /// synchronously-completing source exercises the success path above. Before the fix, the
        /// unconditional <c>_inFlight[id] = fetchTask</c> assignment ran AFTER the catch block had
        /// already removed and disposed the CTS, stamping a permanently stale FAULTED entry — every
        /// later <c>Request</c> for that tile would replay that same faulted task forever, never
        /// re-fetching. Checked in the order that matters most first: the registered task must fault;
        /// a second <c>Request</c> for the same tile must issue a fresh fetch rather than being handed
        /// the stale faulted task (the user-visible symptom, and strictly stronger than an entry-count
        /// check — a future change could clear <c>InFlightCount</c> correctly and still hand back a
        /// stale task); only then, <c>InFlightCount</c> must return to zero.
        /// </summary>
        [Test]
        public async Task SyncThrowingSource_NoStaleFaultedInFlightEntry_SubsequentRequestRefetches()
        {
            int fetchCount = 0;
            var fakeSource = TestDataSource.FromFetch(id =>
            {
                fetchCount++;
                throw new InvalidOperationException("synchronous fetch failure");
            });

            var cache     = new TileCache(capacity: 10);
            var scheduler = new TileScheduler(fakeSource, cache);
            var tileId    = new TileId { Z = 13, X = 1, Y = 1 };

            // (a) The first request faults.
            try
            {
                await scheduler.Request(tileId);
                Assert.Fail("a synchronously-throwing source must fault the returned task.");
            }
            catch (InvalidOperationException) { /* expected */ }

            // (c) The symptom that matters: a second Request must re-fetch, not replay the stale
            // faulted task from the first attempt.
            try
            {
                await scheduler.Request(tileId);
                Assert.Fail("the second request's source call also throws — it must be a genuine second call.");
            }
            catch (InvalidOperationException) { /* expected */ }

            Assert.AreEqual(2, fetchCount,
                "A second Request for the same tile must re-fetch, not be handed the stale faulted task " +
                "from the first attempt.");

            // (b) The in-flight map must be clean afterward.
            Assert.AreEqual(0, scheduler.InFlightCount,
                "In-flight map must be empty after a synchronously-faulted fetch completes.");
        }

        // -----------------------------------------------------------------------------------------
        // T3 — no thread-pool hop anywhere in TileScheduler.cs, comments included.
        // -----------------------------------------------------------------------------------------

        /// <summary>
        /// Named-API regression guard: <c>TileScheduler.cs</c> must contain ZERO occurrences of
        /// <c>SwitchToThreadPool</c> or <c>RunOnThreadPool</c>, in code OR comments — the ordering
        /// invariant is now a lock-and-conditional-registration fact, not a scheduling one, and no
        /// replacement comment may reintroduce the false justification. Does NOT prove no thread hop exists
        /// by any mechanism — a different API, or a hop inside the injected <see cref="IDataSource"/>, would
        /// pass this. It is a regression guard on this one file, by path; <c>FileDataSource.cs</c>
        /// legitimately keeps a guarded occurrence (see T4).
        /// </summary>
        [Test]
        public void TileScheduler_IntroducesNoThreadPoolHop()
        {
            string source = ReadSourceFile("Assets", "Code", "MapRenderer.Core", "Data", "TileScheduler.cs");

            Assert.AreEqual(0, CountOccurrences(source, "SwitchToThreadPool"),
                "TileScheduler.cs must contain ZERO 'SwitchToThreadPool' occurrences (code or comments) — " +
                "the scheduler introduces no thread hop of its own.");
            Assert.AreEqual(0, CountOccurrences(source, "RunOnThreadPool"),
                "TileScheduler.cs must contain ZERO 'RunOnThreadPool' occurrences (code or comments).");
        }

        // -----------------------------------------------------------------------------------------
        // T4 — FileDataSource's hop stays, but only inside its WebGL-excluding guard.
        // -----------------------------------------------------------------------------------------

        /// <summary>
        /// <c>FileDataSource.cs</c>'s <c>await UniTask.SwitchToThreadPool();</c> is a genuine offload
        /// (blocking <c>File.ReadAllBytes</c>), not an ordering guard, and stays — but only inside
        /// <c>#if !UNITY_WEBGL || UNITY_EDITOR</c>, or a WebGL player hangs on it — the same hazard
        /// <see cref="TileScheduler"/>'s own hop no longer carries. Keyed on the CALL STATEMENT, never the bare token —
        /// the file names <c>SwitchToThreadPool</c> in prose too (its rewritten class doc explains the
        /// guard, which naturally names what is being guarded), so a token-count assertion would fail on
        /// this plan's own edit. Index-ordering, not mere presence: three unordered greps would pass on a
        /// guard that wraps nothing.
        /// Does NOT prove the WebGL path works — nothing in the desktop suite can. It pins that the
        /// branch stays deliberate.
        /// <para><b>Known limitation:</b> <c>endGuardIndex</c> takes the FIRST <c>#endif</c> after the
        /// guard opens. A future nested <c>#if</c> inside the guarded block would satisfy the ordering
        /// checks against that inner block's <c>#endif</c> for the wrong reason. Recorded, not fixed.</para>
        /// </summary>
        [Test]
        public void FileDataSource_ThreadPoolHop_IsGuardedForWebGl()
        {
            string source = ReadSourceFile("Assets", "Code", "MapRenderer.Core", "Data", "FileDataSource.cs");

            const string callStatement = "await UniTask.SwitchToThreadPool();";
            const string guard         = "#if !UNITY_WEBGL || UNITY_EDITOR";
            const string endGuard      = "#endif";

            Assert.AreEqual(1, CountOccurrences(source, callStatement),
                $"FileDataSource.cs must contain the call statement '{callStatement}' exactly once.");

            // Line-anchored: a directive starts at column 0, so a "\n" prefix rules out matching the
            // class doc's own <c>#if !UNITY_WEBGL || UNITY_EDITOR</c> prose mention, which is preceded
            // by "(<c>" rather than a newline.
            int guardIndex = source.IndexOf("\n" + guard, StringComparison.Ordinal);
            Assert.Greater(guardIndex, -1,
                $"FileDataSource.cs must contain the exact guard line '{guard}'.");

            int callIndex = source.IndexOf(callStatement, StringComparison.Ordinal);
            int endGuardIndex = source.IndexOf(endGuard, guardIndex, StringComparison.Ordinal);
            Assert.Greater(endGuardIndex, -1,
                $"FileDataSource.cs must contain a matching '{endGuard}' after the guard.");

            Assert.Greater(callIndex, guardIndex,
                "the SwitchToThreadPool call must occur AFTER the #if guard opens.");
            Assert.Less(callIndex, endGuardIndex,
                "the SwitchToThreadPool call must occur BEFORE the guard's #endif — i.e. inside the guarded block.");
        }
    }
}
