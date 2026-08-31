# Threading on the web (WebGL / WebGPU)

**What this is:** empirical learnings about what threading does and doesn't work on the web build — **facts
we measured, as of Unity 6.5** — not a design or a plan. Everything here is an observation; the one
consequence for our own code is called out as such.

**Measured:** 2026-08-29, and re-measured 2026-08-31 (the isolation result and the Burst startup failure
below). Originally on a WebGL **Development** build (WebGPU backend, Unity 6000.5.0f1,
`webGLThreadsSupport=1`, `WebGLExceptionSupport=FullWithoutStacktrace`), served cross-origin-isolated
(COOP `same-origin` + COEP `require-corp` on the main document, verified). May change with a future Unity
version — re-measure (see *How to reproduce*).

## Current state of the web build

**The map renders on the web.** Confirmed by loading a WebGL Development player 2026-08-30, at commit
`268d608f`, served cross-origin-isolated on localhost — tiles fetch, decode, mesh and draw. Getting there
took five fixes, every one of them the same defect: a managed thread-pool dispatch that is silently never
picked up here (the decode hop, both mesh-build kicks, both symbol dispatch sites, and the tile fetch's own
post-fetch hop).

**It renders, and it renders badly: every one of those paths now runs inline on the main thread.** That is
what `InlineWorkScheduler` does, and it is the accepted cost of the bridge, not a bug to file. Tile decode,
mesh build and symbol shaping all land in the requesting frame. The fix is not a better scheduler — it is
moving those bodies into Burst jobs so they stop needing a scheduler at all (see the rule below). Do not
respond to the main-thread cost by adding a cleverer managed offload; there is nowhere for it to run.

**Measured 2026-08-31, and the answer is no: a job does not reach a worker pthread, and cross-origin
isolation is not the reason.** The wasm IS compiled for threads (`-pthread`,
`__EMSCRIPTEN_SHARED_MEMORY__=1`, artifacts under `…_wasm_mt`) and the page IS isolated — the binary
*imports* `env.memory` with `shared=YES`, which the JS glue can only satisfy by constructing a
`SharedArrayBuffer`-backed `WebAssembly.Memory`; that constructor throws without `SharedArrayBuffer`, and
the player reached `INSTANCE CREATED` and ran user script, so the shared heap was live. A chain of eight
`IJob`s still ran entirely inside `Schedule()` on `ThreadIndex 0`. See *The isolation question, settled*.

## TL;DR

**Burst jobs are the only mechanism in the matrix below that executes on the web build.** Every *managed*
background mechanism — the .NET ThreadPool, `Task.Run`, `Awaitable.BackgroundThreadAsync`, a raw
`System.Threading.Thread`, even `Task.Delay` — does not run **at all**: the body is never invoked, silently,
with no throw and no hang. An `IJob` **does** run. On the build measured it ran inline on the main thread
rather than on a worker pthread, so it bought no *parallelism* — but it ran, and that is the property that
separates it from everything else here.

**Read that distinction before designing anything.** "Runs serially" and "never runs" are not two degrees of
one problem: the first is a performance characteristic, the second is a blank map. Work that lives in a job
is correct on every platform with no `#if` at all; work that lives in a managed closure needs a platform
branch to survive here, which is the entire reason `IWorkScheduler` exists (below) — a symptom of code that
is not yet in jobs, not an architectural layer worth keeping.

> **The rule:** put CPU work in **Burst jobs** — they execute everywhere, web included, with no platform
> branch. Cooperation that Unity's own scheduler drives on the main thread also works: coroutines, UniTask
> PlayerLoop awaitables, Unity `Awaitable` main-thread ops, Unity async I/O. **Anything routed through the
> .NET thread _or timer_ pool is dead** — `Task.Run`, `ThreadPool`, `Awaitable.Background`, raw `Thread`,
> **and `Task.Delay`** (its BCL timer is serviced by the ThreadPool, which has no workers).

## The measured matrix

Each mechanism was fired at startup and logged the thread it ran on + the elapsed time to resume.

| Mechanism | Web result |
|---|---|
| Unity coroutine (`yield return new WaitForSeconds(1)`) | ✅ resumes on main at ~1.0 s |
| `UniTask.Delay`, `UniTask.NextFrame` | ✅ resumes on main |
| `Awaitable.NextFrameAsync`, `Awaitable.WaitForSecondsAsync` | ✅ resumes on main |
| `await UnityWebRequest.SendWebRequest()` (async I/O) | ✅ resumes on main (`200`) |
| `UniTask.RunOnThreadPool` | ❌ body never runs |
| `Task.Run` | ❌ body never runs |
| `ThreadPool.QueueUserWorkItem` | ❌ body never runs |
| `Awaitable.BackgroundThreadAsync` | ❌ continuation never runs |
| raw `new Thread(...).Start()` | ❌ body never runs |
| `Task.Delay` | ❌ never completes (BCL timer → ThreadPool → no workers) |
| `IJob` / `IJobParallelFor` (`.Schedule()` + `ScheduleBatchedJobs()`) | ⚠️ **completes, but inline on main** — `JobsUtility.ThreadIndex == 0` for every item; no worker ever takes it |

The dead ones do not throw and do not hang the tab — the main thread keeps running; their work simply never
progresses.

### Two "multi-threaded" signals that are lies here

- `JobsUtility.JobWorkerCount` reported `5` and `SystemInfo.processorCount` reported `8` — both are
  **configured** values, not counts of live pthreads. The `IJobParallelFor` result (`[0]` only) is the truth.
- The engine logs `[Physics::Module] Threading Mode: Multi-Threaded` — a backend config string, not evidence
  of running threads.

`webGLThreadsSupport` only ever grants worker threads to Unity's **native** job system, never to managed .NET
threading — which is exactly why the job row runs and every managed row does not. The jobs still did not
dispatch to *workers*, and cross-origin isolation has now been ruled out as the cause (see below).

> **Not a suspect:** the graphics threading mode. It is single-threaded on the web regardless, and
> `webGLThreadsSupport` governs Burst/job worker pthreads only — the two are unrelated. An earlier revision
> of this doc listed `kGfxThreadingModeDirect` as a possible cause; that was a red herring, ruled out by the
> maintainer 2026-08-30. Do not re-derive it.

## Why it breaks meshing (our pipeline)

The mesh build is a synchronous, thread-agnostic, Burst-`.Run()` pipeline — it deliberately never uses the
job system's `.Schedule()`; every stage runs `.Run()` (Burst-compiled, inline on the calling thread) so the
whole pipeline is callable off the main thread. See the explicit comments at `FillMeshPipeline.cs`
(`// Run, like every other stage — the pipeline must stay callable off the main thread`) and
`MvtGeometryMaterializer.cs` (`// Run (not Schedule) so the pipeline is callable off the main thread`).

All of its threading came from one place: `TileManager.KickMeshBuild` / `KickSourcelessBackground` used to
wrap the build body in **`UniTask.RunOnThreadPool`** to spread *whole tiles* across ThreadPool threads. That
ThreadPool is dead on the web (row 5 above) ⇒ the mesh-build task never completed ⇒ `ConsumeMeshBuild` never
uploaded ⇒ the map was blank (no crash, no error). The PER-FRAME symbol placement pass
(`SymbolPlacementSystem`) was unaffected — it already runs on the main thread. `SymbolSubsystem`'s own
build/reconcile pass (`PumpBuilds`'s parked drain and `ScheduleReconcileIfDirty`) was the last pair of raw `UniTask.RunOnThreadPool`
sites and dead on the web the same way; it has since been converted to `IWorkScheduler` too (see below).

**Fixed** (scoped to mesh-build kicks and symbol dispatch — this was NOT the whole blank-map story; see
*Why it breaks the tile fetch*, below): both mesh-build kicks and both symbol dispatch sites now go through
`IWorkScheduler` (`MapRenderer.Unity/Concurrency/`) — `ThreadPoolWorkScheduler` on desktop/editor
(reproducing the old `RunOnThreadPool` parallelism byte-for-byte) and `InlineWorkScheduler` on a WebGL
player, which runs the body synchronously on the calling (main) thread instead of dispatching it nowhere.
Because the mesh pipeline is already `.Run()`-based (thread-agnostic — see below), it runs correctly inline;
`LoadedTile.MeshBuildTask` and `SymbolSubsystem`'s own `_reconcileHandle` are `WorkHandle<T>`, not bare
`UniTask<T>`, so every consumer polls/consumes through that seam regardless of policy.

**But `IWorkScheduler` is a workaround, not the answer** — see the TL;DR. It exists because these bodies are
managed closures (they capture `Dictionary`/`List`/`SharedDisposable`/interfaces) and so cannot be jobs yet;
its `Schedule(Func<>)` signature is the shape being eliminated, and its `#if` is the platform branch a job
would not need. Each site that gets its data nativized becomes a job and *deletes* its use of this interface
rather than adopting it. Do not read the fix above as a pattern to extend to new code.

## Why it breaks the tile fetch

A second, independent bug lived downstream of the fetch, not the mesh build: `TileScheduler`'s
`FetchAndCacheAsync` (`MapRenderer.Core/Data/TileScheduler.cs`) awaited `UniTask.SwitchToThreadPool()` after
a successful fetch, purely as a **sync-completion ordering guard** (so a synchronously-completing source
couldn't race its own cleanup against `Request()`'s bookkeeping) — dead on web for the same reason as row 5
above. Only the *success* path hung; the exception path runs before the hop, so a failed fetch completed and
faulted correctly. Nothing surfaced the hang: `TileManager` polls `Status.IsCompleted()` with no timeout
(`:1753`), so a hung fetch just meant a tile that never appeared, with no log. `FileDataSource.cs` had the
same call independently, reachable on web through a `file://` tile template.

Fixed by ordering the `_inFlight` registration under `TileScheduler`'s own lock, conditional on the CTS
reservation still being live, instead of behind a thread-pool hop — no platform branch needed; the ordering
is now a lock fact, not a scheduling one. `FileDataSource`'s hop is a genuine I/O offload rather than an
ordering guard, so it keeps its `#if !UNITY_WEBGL || UNITY_EDITOR` guard (the one remaining platform branch
on this path): desktop/editor is byte-identical, and a WebGL player runs the read synchronously inline
instead of hanging.

## Things that follow from the above

- **CPU work belongs in Burst jobs, and that conclusion does not depend on the parallelism question.** A job
  executes on every platform with no platform branch; a managed closure needs one to run on the web at all.
  The direction of travel is therefore: nativize a body's data → it becomes a job → the platform branch for
  that site disappears, along with its need for `IWorkScheduler`.
- Anything that must "do work while the frame continues" cannot rely on the .NET thread or timer pool on the
  web; only Unity-scheduler-driven cooperation (coroutine / PlayerLoop) runs, and real CPU work lands on main.
- Because the mesh pipeline is already `.Run()`-based (thread-agnostic), the work itself runs fine on the
  main thread on the web — it was only the `UniTask.RunOnThreadPool` wrapper that was dead, which is exactly
  what `InlineWorkScheduler` replaces it with above.
- On the build measured, converting `.Run()` → `.Schedule()` would not have bought web *parallelism*, since
  no worker took a job. **That is a statement about parallelism only — read with the TL;DR, not instead of
  it.** It is not a reason to keep work out of jobs. It is now settled rather than contingent: isolation was
  verified active and the jobs still ran on main, so `.Schedule()` buys no parallelism here today.

## The isolation question, settled (2026-08-31)

The previous revision refused to call "jobs get no workers on web" a fact, because the runtime value of
`crossOriginIsolated` had never been read: without `SharedArrayBuffer` there is no shared heap,
`pthread_create` fails, and the inline-on-main result would be explained by the serving setup rather than by
Unity. That escape hatch is now closed.

Served cross-origin-isolated on localhost (COOP `same-origin` + COEP `require-corp` + CORP `cross-origin` on
**every** subresource), the player reported:

```
[jobprobe] platform=WebGLPlayer workers=5
[jobprobe] chain of 8: Schedule() took 82.0ms, wall 82.0ms over 0 frame(s),
           finishedOnItsOwn=True, threadIndices=0
```

`Schedule()` and wall-clock are the same 82 ms and `frames=0`, so the whole chain executed inside the
scheduling loop; `threadIndices={0}` is the main thread. Isolation was active for this run (the shared-memory
argument above), so **the absence of workers is a property of Unity's web job system, not of the page.**

*Caveat on this particular run:* it was built with Burst AOT **disabled**, because a Burst-enabled web player
does not start at all (see below). Job dispatch should not depend on whether a body is Burst-compiled, but
that is an assumption, not a measurement — the 2026-08-29 run that produced the same result had Burst on, so
the two agree across that variable even though neither run tested it directly.

## Burst AOT stops the web player from starting (2026-08-31, cause open)

**With Burst AOT enabled for Web, the player never finishes initialising.** The page loads, the wasm
instantiates, and `requestAnimationFrame` runs at a steady 60 fps (`scheduled=1479 fired=1478` over 26 s) —
but `createUnityInstance` never resolves, because native code never calls
`_JS_WebPlayer_FinishInitialization`. On the stock page that shows as a loading bar that never hides. **No
user script runs at all** — no `Awake`, no `RuntimeInitializeOnLoadMethod` — so the player is stuck inside
initialisation while frames pump. On WebGL2 the same build traps instead of hanging: `RuntimeError: Out of
bounds memory access` in `entryFunction(argc, argv)`, from `callMain`.

Setting `EnableBurstCompilation: false` in `ProjectSettings/BurstAotSettings_WebGL.json` makes the player
load in ~2 s. That was the only variable — the two builds were eight minutes apart on the same tree.

**Burst's contribution to the binary, measured by parsing both wasm files:** `bcl.exe` emits a separate
`lib_burst_generated.wasm` which is linked in, worth **+870 functions and +1.23 MB of code section**
(203,277 vs 202,407 functions; 63.07 vs 61.83 MB). So this is not a module-size problem — Burst is ~2% of
the player.

**Excluded by direct A/B:** worker threads (reproduces with `webGLThreadsSupport` off — wasm verified
`shared=NO`), heap size (32 MB vs 512 MB), graphics backend (WebGPU hangs, WebGL2 traps — both fail), scene,
splash screen, managed stripping level, and browser (Safari 26.6.2 and Chrome).

**Not yet attributed.** `MapRenderer.Jobs` — where essentially all of the renderer's Burst code lives — is
byte-unchanged since `268d608f`, the commit whose web build rendered with Burst on, so the renderer's own
job code is not a *new* suspect. Bisection is by per-assembly AOT exclusion (see below).

### Traps when re-testing this

Several separate mechanisms will hand you a green-looking build that tested nothing:

- **Editing `BurstAotSettings_WebGL.json` from outside the Editor does not invalidate Bee's cached link.**
  The value reads back correctly from every Burst API — `BurstPlatformAotSettings.GetOrCreateSettings`,
  `BurstCompiler.Options.EnableBurstCompilation` — and the build still reuses the previous `build.wasm`.
  Toggling it *off* does force a relink, because Burst's output file disappears; toggling it back *on* does
  not.
- **An incremental player build can take the `Run script only build` path** (~35 s), which skips IL2CPP and
  Burst AOT entirely and just repackages the previous output. `BuildOptions.CleanBuildCache` did not prevent
  this. Read the `PlayerBuildInfo` step list in `Logs/Editor.log` before believing a result.
- **`Data/Plugins/lib_burst_generated.cpp` at the repo root is a stray artifact**, not a live signal — it can
  be hours stale relative to the build you just ran.

- **Once the Editor has produced one Burst-off build, it may stop calling Burst's build callback at all.**
  This is the trap that defeats every one of the above. Burst hooks the player build through
  `IGenerateNativePluginsForAssemblies`; its `PrepareOnMainThread` declares the settings JSON as a watched
  input, so editing that file is *supposed* to force a rerun. Observed instead: after a Burst-off build, six
  consecutive builds — new output directories, `CleanBuildCache`, a changed settings file, and an IL change
  to a Bursted assembly — produced byte-for-byte Burst-off output, and `Library/Bee` contained no
  `lib_burst_generated.*` at all. Every Burst-side API still reported `EnableBurstCompilation = True` and
  `ForceDisableBurstCompilation = False`, and the settings path resolved correctly. Nothing that Burst
  watches can help, because the step doing the watching never runs.

**The decisive check is not the wasm size** — that moves with your own script edits too (a one-line `const
string` shifted it by 21 KB and made a void build look valid). Two checks cannot be fooled:

- `find Library/Bee -iname '*burst_generated*'` — empty means Burst produced nothing, whatever the settings say.
- the mtime of `Library/BurstCache/AotSettings_<Target>.hash`, which Burst writes from `PrepareOnMainThread`.
  Older than your build means the callback was never invoked.

`bcl.exe` in `Logs/Editor.log` is the same signal read from the other end.

**Per-assembly exclusion** —
`ProjectSettings/Burst_DisableAssembliesForPlayerCompilation_<Target>.json`
(`{"MonoBehaviour":{"DisabledAssemblies":["Some.Assembly"]}}`) — is the right instrument to bisect *which*
assembly's Burst code breaks the player, because `BurstAotCompiler` reads it straight off disk at build time.
It is worthless until Burst is actually running again; verify that first with the two checks above.

**To recover a Burst-on build:** close the Editor, delete `Library/Bee`, reopen, build once. This does not
touch `Library/Artifacts`, so there is no asset reimport — it costs a full script/linker/IL2CPP pass
(~20–30 min) and nothing else. Reach for it early; the cheap-looking cache-busts above cost far more.

## Open questions (not yet measured)

- A **WebGL2 (OpenGLES3) vs WebGPU** A/B for the *threading* result was never run. Note this is a weak
  hypothesis, not a lead: the graphics-threading suspicion that motivated it has been ruled out (see above),
  so a backend difference would have to act through some other route. (Both backends *were* A/B'd for the
  Burst startup failure below, and behave the same.)

## How to reproduce

A throwaway `ThreadingProbe` MonoBehaviour in a one-object scene, built for WebGL Development and loaded from
a cross-origin-isolated static server, fires every mechanism above and logs `thread id + isMain + elapsed`
per step. Any future re-measurement (e.g. a Unity upgrade, or the WebGL2 A/B) should reuse that shape.
