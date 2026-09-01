# The web target (WebGL / WebGPU)

**What this is:** everything we have measured about shipping this renderer to the web — how to build and
serve a player, which settings it must have or it silently never starts, what runs off the main thread and
what only appears to, and the package-level failures we hit getting there. **Facts, as of Unity 6.5**, not a
design or a plan. Where a conclusion has been overturned, the correction and the reasoning error that
produced it are kept rather than edited away — this doc has been wrong twice, both times by inferring past
what was measured.

**Read this before touching a web build.** Two settings are load-bearing (Burst AOT and managed stripping),
Unity will hand you a player that silently is not the configuration you asked for, and the build is not
servable by a plain static server.

**Measured:** 2026-08-29, re-measured 2026-08-31, corrected and extended 2026-09-01. Unity 6000.5.0f1,
WebGPU backend, `webGLThreadsSupport=1`, served cross-origin-isolated (COOP `same-origin` + COEP
`require-corp`, verified). The original threading matrix was taken on a **Development** build. May change
with a future Unity version — re-measure (see *How to reproduce*).

## Current state

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

**Measured 2026-08-31, corrected 2026-09-01. A job DOES reach a worker pthread — but only when it is
Burst-compiled.** The wasm IS compiled for threads (`-pthread`,
`__EMSCRIPTEN_SHARED_MEMORY__=1`, artifacts under `…_wasm_mt`) and the page IS isolated — the binary
*imports* `env.memory` with `shared=YES`, which the JS glue can only satisfy by constructing a
`SharedArrayBuffer`-backed `WebAssembly.Memory`; that constructor throws without `SharedArrayBuffer`, and
the player reached `INSTANCE CREATED` and ran user script, so the shared heap was live. A chain of eight
**non-Burst** `IJob`s ran entirely inside `Schedule()` on `ThreadIndex 0`, while the same
chain **Burst-compiled** returned from `Schedule()` in 0.0 ms and completed on `ThreadIndex 2` over five
frames. Burst AOT is the discriminator, not isolation. See *Burst is what buys worker threads*.

## Building and running the web player

```bash
Tools/build.sh web        # -> Builds/Web/UnityMapRenderer/   (~9 min, Editor closed)
Tools/serve-web.sh        # -> http://localhost:8080
```

Which scene ships is Build Settings' job, as on every other target: the enabled entries in
`EditorBuildSettings` (currently `OpenStreetMapLiberty`, the scene carrying the attribution overlay).

**`Tools/serve-web.sh` is not a convenience.** A plain static server cannot serve this build: Unity
compresses with Brotli and the loader has no JS fallback decoder (`webGLDecompressionFallback: 0`), so the
*server* must declare `Content-Encoding: br`. Safari compounds it by only advertising `br` over HTTPS, so on
plain `http://` it never asks — the header has to be sent regardless. The script also sends COOP/COEP, which
this build does not need but will the moment threads matter.

### Three settings the web build forces, and what each one costs

| setting | value | why | cost |
|---|---|---|---|
| Burst AOT | **off** | `com.unity.entities` + Burst traps during static init (see above) | every job runs inline on the main thread — no worker threads at all |
| managed stripping | **Minimal** (High elsewhere) | High builds clean and then hangs at 100% | 18.6 MB shippable instead of 14.0 MB |
| threads support | **on** | it is the configuration observed rendering | requires a cross-origin-isolated host |

The first two are not preferences — with either one wrong the player does not start. Both are set in
`BuildScript.RunWeb` / `ApplyReleaseSettings` with the reasoning inline, not left to whoever last opened the
Editor.

### Known-bad configurations, all measured on this project

| Burst | stripping | threads | result |
|---|---|---|---|
| on | any | any | never finishes init (WebGPU) / OOB trap in `entryFunction` (WebGL2) |
| off | **High** | on | engine initialises, loader sticks at 100%, no error |
| off | **High** | off | same |
| off | Minimal | on | **renders** |

Two of these cost a build each to find because more than one variable moved at a time. Change one thing per
build here; each is ~4–9 minutes.

### Proving a build is what it claims

Unity will hand you a player that silently is not the thing you asked for — see *Traps when
re-testing this*, under the Entities failure below, for the mechanisms. Before
drawing any conclusion from a web build:

- **Burst off:** `find Library/Bee -iname '*burst_generated*'` must be **empty**.
- **Burst on:** it must be **non-empty**, and `bcl.exe` must appear in the build log. A build that reports
  Burst enabled and has neither ran without it.
- **Threads:** the wasm imports `env.memory` with `shared=YES` — parse it, do not trust the setting.
- Never conclude from wasm *size*: a one-line string constant moved it 21 KB and made a void build look
  valid.

## Threading

What runs off the main thread here, what silently does not, and what that costs this
renderer. Measured, not inferred — the matrix below is a probe's output.

### TL;DR

**Burst jobs are the only mechanism in the matrix below that executes on the web build.** Every *managed*
background mechanism — the .NET ThreadPool, `Task.Run`, `Awaitable.BackgroundThreadAsync`, a raw
`System.Threading.Thread`, even `Task.Delay` — does not run **at all**: the body is never invoked, silently,
with no throw and no hang. An `IJob` **does** run — and when it is Burst-compiled it runs on a **worker
pthread**, off the main thread, with real parallelism. A job that is not Burst-compiled still runs, but
inline on the caller. So Burst buys two distinct things here: the job runs at all, and it runs off-main.

**Read that distinction before designing anything.** "Runs serially" and "never runs" are not two degrees of
one problem: the first is a performance characteristic, the second is a blank map. Work that lives in a job
is correct on every platform with no `#if` at all; work that lives in a managed closure needs a platform
branch to survive here, which is the entire reason `IWorkScheduler` exists (below) — a symptom of code that
is not yet in jobs, not an architectural layer worth keeping.

> **The rule:** put CPU work in **Burst jobs** — they execute everywhere, web included, with no platform
> branch, and on the web they are the *only* way to reach a worker thread at all. Cooperation that Unity's own scheduler drives on the main thread also works: coroutines, UniTask
> PlayerLoop awaitables, Unity `Awaitable` main-thread ops, Unity async I/O. **Anything routed through the
> .NET thread _or timer_ pool is dead** — `Task.Run`, `ThreadPool`, `Awaitable.Background`, raw `Thread`,
> **and `Task.Delay`** (its BCL timer is serviced by the ThreadPool, which has no workers).

### The measured matrix

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
| `IJob` / `IJobParallelFor`, **Burst-compiled** | ✅ **runs on a worker pthread** — `Schedule()` returns in ~0 ms, `ThreadIndex != 0`, main thread keeps rendering |
| `IJob` / `IJobParallelFor`, **not** Burst-compiled | ⚠️ completes, but **inline on the calling thread** — `Schedule()` blocks for the whole chain, `ThreadIndex == 0` |

The dead ones do not throw and do not hang the tab — the main thread keeps running; their work simply never
progresses.

#### Two "multi-threaded" signals that are lies here

- `JobsUtility.JobWorkerCount` reported `5` and `SystemInfo.processorCount` reported `8` — both are
  **configured** values, not counts of live pthreads. The `IJobParallelFor` result (`[0]` only) is the truth.
- The engine logs `[Physics::Module] Threading Mode: Multi-Threaded` — a backend config string, not evidence
  of running threads.

`webGLThreadsSupport` only ever grants worker threads to Unity's **native** job system, never to managed .NET
threading — which is exactly why the job rows run and every managed row does not. It also explains the split
between the two job rows: a Burst-compiled job body IS native code and can be handed to a worker, while an
IL2CPP-compiled managed body cannot, so the job system runs it inline on the caller. Cross-origin isolation is
not involved — it was verified active for every run above.

> **Not a suspect:** the graphics threading mode. It is single-threaded on the web regardless, and
> `webGLThreadsSupport` governs Burst/job worker pthreads only — the two are unrelated. An earlier revision
> of this doc listed `kGfxThreadingModeDirect` as a possible cause; that was a red herring, ruled out by the
> maintainer 2026-08-30. Do not re-derive it.

### Why it breaks meshing (our pipeline)

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

### Why it breaks the tile fetch

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

### Things that follow from the above

- **CPU work belongs in Burst jobs, and that conclusion does not depend on the parallelism question.** A job
  executes on every platform with no platform branch; a managed closure needs one to run on the web at all.
  The direction of travel is therefore: nativize a body's data → it becomes a job → the platform branch for
  that site disappears, along with its need for `IWorkScheduler`.
- Anything that must "do work while the frame continues" cannot rely on the .NET thread or timer pool on the
  web; only Unity-scheduler-driven cooperation (coroutine / PlayerLoop) runs, and real CPU work lands on main.
- Because the mesh pipeline is already `.Run()`-based (thread-agnostic), the work itself runs fine on the
  main thread on the web — it was only the `UniTask.RunOnThreadPool` wrapper that was dead, which is exactly
  what `InlineWorkScheduler` replaces it with above.
- Converting `.Run()` → `.Schedule()` **does** buy web parallelism, provided the job is Burst-compiled.
  A non-Burst job is run inline by the job system, so the conversion buys nothing on its own. This is the
  opposite of what the 2026-08-31 revision of this doc concluded; see below for how that error was made.

### Burst is what buys worker threads (2026-09-01)

**A Burst-compiled job runs on a worker pthread on the web. A managed one does not.** Measured with one
variable, in one project, on the same scene, both builds cross-origin-isolated and both with
`webGLThreadsSupport` on (wasm imports `env.memory shared=YES`, 512 pages):

| | Burst AOT ON | Burst AOT OFF |
|---|---|---|
| `Schedule()` returns in | **0.0 ms** | 80.0 ms |
| chain of 8 completes over | **5 frames** | 0 frames |
| `JobsUtility.ThreadIndex` | **2** | 0 |
| `lib_burst_generated.wasm` | present | absent |

`Schedule()` returning immediately while the main thread advances five frames is dispatch to a worker. The
Burst-off column is the whole chain executing inside the scheduling loop on the calling thread.

The mechanism is the one the matrix already implied: `webGLThreadsSupport` grants workers to the **native**
job system. Burst-compiled job bodies are native code and can be handed to a worker; an IL2CPP-compiled
managed body cannot, so the job system runs it inline on the caller.

#### How the previous conclusion got this backwards

The 2026-08-31 revision recorded "no worker takes the job, and isolation is not the reason" as **settled**.
The measurement was real and is reproduced exactly by the Burst-off column above. The error was in the
inference: that run was built with Burst AOT **disabled** — a detail recorded at the time as a caveat, with
the note that "job dispatch should not depend on whether a body is Burst-compiled, but that is an assumption,
not a measurement." The assumption was the entire finding, and it was wrong.

Two things made the mistake easy to miss, both worth avoiding again:
- Burst had been switched off only because a Burst-enabled build of **our** project does not start (below).
  A workaround adopted to get *any* measurement silently became a variable in it.
- An earlier 2026-08-29 run with Burst ON reported the same inline result, which looked like agreement across
  the variable. That run predates the probe used here and did not read `ThreadIndex`; it is not evidence.

**Consequence for this project:** off-main work on the web is real, and jobification buys parallelism there,
not merely "runs at all". `IWorkScheduler`'s inline-on-web policy is a stopgap for the managed closures that
cannot be jobs yet — not the end state, and not evidence that the platform lacks threads. It also means Burst
is **not** droppable for web to dodge the startup failure below: dropping it costs every worker thread.

*Scope:* measured in a minimal Unity 6000.5.0f1 project (URP, one scene, 44.9 MB wasm), because our own
Burst-enabled web build does not start. The mechanism is not project-specific, but the re-measurement inside
this repo is still owed once that is fixed.

## `com.unity.entities` + Burst AOT breaks the web player (2026-09-01, minimal repro)

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

**Excluded by direct A/B (before the cause was known, all on this project):** worker threads (reproduces with `webGLThreadsSupport` off — wasm verified
`shared=NO`), heap size (32 MB vs 512 MB), graphics backend (WebGPU hangs, WebGL2 traps — both fail), scene,
splash screen, managed stripping level, and browser (Safari 26.6.2 and Chrome).

**Attributed 2026-09-01: it is `com.unity.entities`.** Reproduced from a stock Unity 6000.5.0f1 URP
project (the 2D Platformer microgame) containing **no ECS code at all** — no system, no baker, no entity.
Adding the package is sufficient:

| packages added | Burst AOT | wasm | result |
|---|---|---|---|
| burst, collections, mathematics | on | 44.9 MB / 120,672 fns | loads in ~1 s, job runs on a worker |
| burst, collections, mathematics | off | 44.6 MB / 120,501 fns | loads, job runs inline |
| **+ entities + entities.graphics** | on | 55.4 MB / 147,138 fns | **`Out of bounds memory access` in `entryFunction`** |
| **+ entities only** | on | 53.5 MB / 141,523 fns | **same crash** (0 `EntitiesGraphics` symbols in the wasm) |

`entities.graphics` is not involved — removing it changes nothing but the function count. The failure is
`RuntimeError: Out of bounds memory access (evaluating 'entryFunction(argc,argv)')` from
`callMain → doRun → run`, i.e. during static init, before any managed entry point. Identical stack shape to
this project's WebGL2 failure.

**What this rules out for us:** our own job code, our scale (the repro is 53.5 MB against our ~79 MB), our
scene, and Entities Graphics. Nothing in this repository is implicated — the same crash reproduces with the
package alone.

**Consequences.** The web target cannot currently have both Entities and Burst AOT. Neither half is
comfortably droppable: Burst is the only route to a worker thread on web (above), and Entities is a render
backend here. The realistic options are to ship web with the non-Entities backend, or to drop Burst for web
and accept every job running inline on the main thread. Worth a Unity bug report; the repro above is small
enough to attach as-is.

**Still unverified:** that this fully explains our own failure. The mechanism matches and the stack matches,
but our build has not been re-tested with Entities removed. That is the confirming experiment, and it is now
cheap.

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
