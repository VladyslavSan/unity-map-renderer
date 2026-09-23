# The web target (WebGL / WebGPU)

**What this is:** everything we have measured about shipping this renderer to the web — how to build and
serve a player, which settings it must have or it silently never starts, what runs off the main thread and
what only appears to, and the package-level failures we hit getting there. **Facts, as of Unity 6.6**, not a
design or a plan. Where a conclusion has been overturned, the correction and the reasoning error that
produced it are kept rather than edited away — this doc has been wrong twice, both times by inferring past
what was measured.

**Read this before touching a web build.** Unity will hand you a player that silently is not the
configuration you asked for, and the build is not servable by a plain static server.

**Measured:** 2026-08-29, re-measured 2026-08-31, corrected and extended 2026-09-01, **re-measured on Unity
6000.6.0f1 2026-09-02**. WebGPU backend, `webGLThreadsSupport=1`, served cross-origin-isolated (COOP
`same-origin` + COEP `require-corp`, verified). The original threading matrix was taken on a **Development**
build. Findings dated 2026-09-01 and earlier were taken on Unity 6000.5.0f1 / Burst 1.8.29; where 6.6
changed the answer it is marked inline, and the old finding is kept because the failure modes it documents
are the ones a future regression will look like.

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

**Burst AOT and Entities both work on the web as of Unity 6000.6.0f1 / Burst 2.0 (measured 2026-09-02).**
Through Unity 6000.5 the web player could have Burst or `com.unity.entities`, not both — with Burst on it
trapped during static init before any managed code ran, so the web build shipped Burst-off and therefore
with every job inline. The 6.6 upgrade removed that constraint at no cost to us: a player with Burst AOT on,
the Entities package linked, **and** Entities Graphics actually driving the rendering starts and renders
correctly, colours included. The startup trap and a separate white-fill symptom both went away with the same
upgrade. That makes worker threads reachable in this project for the first time — see *Threading*.

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
Tools/build.sh web         # -> Builds/Web/UnityMapRenderer/               (~9 min, Editor closed)
Tools/serve-web.sh         # -> http://localhost:8080

Tools/build.sh web --dev   # -> Builds/Web-Development/UnityMapRenderer/  (the profileable player)
Tools/serve-web.sh --dev   # -> http://localhost:8080

Tools/build.sh web --dev --profiler   # ...and it auto-connects to the Profiler at startup
Tools/build.sh web --dev --clean --serve   # rebuild from scratch, then serve THAT variant
```

`--dev` compiles the `MapRenderer.*` counters into the player; `--profiler` is the separate question of
whether that player also goes looking for an Editor at every launch, and it is off unless asked for. Each
development build prints a `ConnectProfiler=` line reporting which it actually built.

`--serve` hands the finished build to `serve-web.sh` with the same `--dev`-ness it was built with, which
is the mismatch this page's next paragraph exists to warn about — using it means the two cannot disagree.
Where each variant lands is no longer written down in more than one place: `BuildScript` decides,
`Tools/lib.sh` mirrors it for the shell, and `build.sh` compares its own answer against the `BUILD OK`
path in the log and warns if they have drifted apart.

**`--dev` has to be passed to BOTH.** The two variants build to separate directories, so
`build.sh web --dev` followed by a bare `serve-web.sh` serves whatever release player was last built —
and the only symptom is `RuntimeDiagnostics` reporting `devBuild=False` in a session you believe is a
development build. Each invocation now prints which variant it is serving, so check that line first.
Which scene ships is Build Settings' job, as on every other target: the enabled entries in
`EditorBuildSettings` (currently `OpenStreetMapLiberty`, the scene carrying the attribution overlay).

**`Tools/serve-web.sh` is not a convenience.** A plain static server cannot serve this build: Unity
compresses with Brotli and the loader has no JS fallback decoder (`webGLDecompressionFallback: 0`), so the
*server* must declare `Content-Encoding: br`. Safari compounds it by only advertising `br` over HTTPS, so on
plain `http://` it never asks — the header has to be sent regardless. The script also sends COOP/COEP, and those are
**load-bearing**: this build has threads on, so its wasm imports `env.memory` with `shared=YES`, and that
`SharedArrayBuffer`-backed memory throws at construction unless the page is cross-origin-isolated. A server
that omits them does not serve a slower player — it serves one that never starts.

It writes response bodies to the socket itself rather than through `shutil`, because macOS fails a large
write to loopback with `ENOBUFS` *part way through* and `sendall` reports no progress when it raises — the
body is silently truncated and the browser blames the wasm. `Tools/serve-web-selftest.py` covers that loop
against an injected `ENOBUFS`; it runs in ~0.2 s, is not part of `run-tests.sh`, and is RED-verified
against three defects (a non-advancing buffer, unhandled `ENOBUFS`, and a retry that duplicates bytes).

### The three settings that decide whether a web player starts

| setting | value | why | cost |
|---|---|---|---|
| Burst AOT | **on** | the only route to a worker thread on web | none since 6.6; it was off through 6000.5, where Entities + Burst trapped during static init |
| managed stripping | **High** — same as every other target | it works again as of 6.6; it hung the player at 100% on 6000.5 | none; keeping Minimal cost 3.1 MB |
| threads support | **on** | grants the job system worker pthreads, which Burst can now actually use | requires a cross-origin-isolated host |

Burst and threads are set in `BuildScript.RunWeb`, stripping in `ApplyReleaseSettings`, with the reasoning
inline rather than left to whoever last opened the Editor. **Web no longer carries any settings exception**
— that is new as of 6.6, and both exceptions it used to carry were fixed by the same upgrade.
`UMR_WEB_BURST=off Tools/build.sh web` builds the Burst-off variant if the 6000.5 trap ever returns.

### Known-bad configurations, all measured on this project

On **Unity 6000.6.0f1 / Burst 2.0** (2026-09-02):

| Burst | stripping | threads | backend | result |
|---|---|---|---|---|
| on | Minimal | on | GameObjects | **renders** |
| on | Minimal | on | **Entities** | **renders** — the combination that used to trap |
| on | **High** | on | Entities | **renders**, 14.2 MB shippable (vs 17.3 MB at Minimal) |

**If fills ever render white, that is an open bug, not a known-fixed one.** White fills were seen once
under Burst 1.8 on an experimental Entities-removed tree, never root-caused, and never reproduced on 6.6.
Nothing here explains them, so treat a recurrence as live and unexplained.

On **Unity 6000.5.0f1 / Burst 1.8.29**, kept because a regression will look like this:

| Burst | stripping | threads | result |
|---|---|---|---|
| on | any | any | never finishes init (WebGPU) / OOB trap in `entryFunction` (WebGL2) |
| off | **High** | on | engine initialises, loader sticks at 100%, no error |
| off | **High** | off | same |
| off | Minimal | on | renders |

Two of the 6000.5 rows cost a build each to find because more than one variable moved at a time. Change one
thing per build here; each is ~1–9 minutes depending on what the incremental build can reuse.

### Proving a build is what it claims

Unity will hand you a player that silently is not the thing you asked for — see *Traps when
re-testing this*, under the Entities failure below, for the mechanisms. Before
drawing any conclusion from a web build:

- **`Tools/build.sh web` prints a `burst:` line after every web build**, reporting the setting that was
  requested against the artifacts Burst actually produced. `requested=True generated-artifacts=0` means the
  toggle never took; move `Library/Bee` aside and build again.
- The underlying check, by hand: `find Library/Bee -iname '*burst_generated*'` — **empty** for a Burst-off
  build, **non-empty** for a Burst-on one.
- **`bcl.exe` in the build log is a Burst 1.x signal only.** Burst 2.0 compiles in-process, so its absence
  says nothing; only the artifacts do.
- **Threads:** the wasm imports `env.memory` with `shared=YES` — parse it, do not trust the setting.
- Never conclude from wasm *size*: a one-line string constant moved it 21 KB and made a void build look
  valid.

**Not yet built — the reading the tile pipeline will need.** `job-scheduling-design.md`'s worker-index sample specifies a
worker-index sample: each mesh-build graph's last node records `[NativeSetThreadIndex]` into a
`NativeReference<int>`, and telemetry counts graphs that completed on thread 0 versus on a worker. **Nothing
implements this today** (no `[NativeSetThreadIndex]` anywhere in `Assets/Code`), so this paragraph is an
obligation on whoever lands it, not a description of an existing check.

Why it belongs here: a web player whose graphs all report thread 0 is running the pipeline **inline on the
main thread** — Burst off, or workers absent — and will merely render slowly rather than fail. That is the
same class of silent lie as a Burst-off build, and it needs the same treatment: read it beside
`Tools/build.sh web`'s `burst:` line, as a manual check, because the sample only exists in a running player.
Per `docs/job-scheduling-design.md` § "Safety — making the Editor's check sufficient" (rule 4), this is
the *only* reading that may be used to claim off-main execution — a build-step trail proves
scheduling order, never placement.

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

- `JobsUtility.JobWorkerCount` reported `5` and `SystemInfo.processorCount` reported `8`, while an
  `IJobParallelFor` reached only `[0]`. **Superseded 2026-09-02:** with Burst on, that same parallel job
  reaches all 5 workers, so the count was accurate and the Burst-off build was the lie. Keep reading the
  `IJobParallelFor` result rather than the count — but a mismatch now means Burst is off, not that the
  platform has no threads.
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
cannot be jobs yet — not the end state, and not evidence that the platform lacks threads. It also meant Burst
was **not** droppable for web to dodge the startup failure below: dropping it costs every worker thread. That
fork closed with Unity 6.6, which fixed the startup failure and left Burst on.

*Scope:* measured in a minimal Unity 6000.5.0f1 project (URP, one scene, 44.9 MB wasm), because our own
Burst-enabled web build did not start at the time. The mechanism is not project-specific. **That blocker is
gone as of Unity 6.6** — this project now ships a Burst-on web player — so the re-measurement inside this
repo is possible, and still owed: what has been confirmed here is that the player *renders*, not yet that
our jobs land on a worker. Re-run the probe shape in *How to reproduce* against this repo to close it.

### Measured in THIS project, in a real web player (2026-09-02)

The minimal-project result above reproduces here, in the shipped configuration (Burst on, Entities linked,
Entities backend rendering, stripping High, cross-origin-isolated):

| probe | result |
|---|---|
| serial chain of 8 Burst `IJob`s | `Schedule()` returned in **0.0 ms**, chain took 66 ms wall over **4 frames**, `ThreadIndex 3` |
| `IJobParallelFor`, 64 items | **5 distinct worker threads** (`1,2,3,4,5`), 22 ms wall for ~80 ms of serial work — **~3.6×** |
| Burst guard inside the job body | `ranAsManagedIL=0/8` |

Four frames advancing across 66 ms is ~60 fps: the main thread rendered normally while the chain ran
elsewhere. **So off-main execution and real multi-core parallelism are both available to this project on
the web.**

**The Burst guard is the part to copy, not the verdict.** The job body calls a `[BurstDiscard]` method that
can only run on the managed fallback, and reports per-job whether it fired. Without it an "inline" reading
is ambiguous — a managed body runs inline *by definition* — and that ambiguity is exactly what produced the
retracted 2026-08-31 conclusion. A probe that cannot tell those two apart should refuse to return a verdict.

**This corrects the "configured, not live" note above.** `JobsUtility.JobWorkerCount` reported 5, and 5 is
what the parallel job actually reached. The count was never the lie; the Burst-off build that made it look
like one was.

**What it does NOT mean: the renderer is not faster yet.** Nothing here asks for a worker. Managed offload
still routes to `InlineWorkScheduler` on web, and the mesh pipeline is `.Run()` at all 12 job sites with no
`.Schedule()` anywhere — deliberately, so it stays callable off the main thread. `.Run()` executes on the
calling thread even when the body is Burst-compiled and five workers sit idle. The capability is now proven;
spending it is separate work.

Note also what the two probes measure differently: the serial chain reached **one** worker, because a chain
is serial by construction. Multi-core wins come from either many tiles building concurrently on different
workers, or `IJobParallelFor` *within* a stage — not from converting a dependent chain to `.Schedule()`,
which buys off-main only.

## `com.unity.entities` + Burst AOT broke the web player — FIXED in Unity 6.6 (history)

**Resolved 2026-09-02 by upgrading Unity 6000.5.0f1 → 6000.6.0f1, which moves Burst 1.8.29 → 2.0.0.** A web
player with Burst AOT on, Entities linked and the Entities render backend active now starts and renders
correctly. Nothing in this repository changed to achieve it; no workaround was kept. The section below is
the original investigation, retained because it is the shape a regression would take, and because the
attribution (it is the package, not our code) is what made the upgrade worth trying rather than a
coincidence to be re-derived.

Two consequences of the fix are worth stating plainly:

- **The "ship web without Entities, or without Burst" fork is dead.** Both halves are now available at once,
  so neither the non-Entities backend nor an inline-everything player is forced on the web target.
- **A separate white-fill symptom disappeared with the same upgrade.** Fills rendering white had been seen
  only on an experimental Entities-removed tree under Burst 1.8; it was never reproduced on 6.6. It was
  never root-caused, so it is not *known* to be the same defect — if white fills reappear, treat that as a
  live unexplained bug, not a known-fixed one.

### The original investigation (2026-09-01, minimal repro, Unity 6000.5.0f1)

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

**Consequences (as they stood on 6000.5).** The web target could not have both Entities and Burst AOT, and
neither half was comfortably droppable: Burst is the only route to a worker thread on web (above), and
Entities is a render backend here. Worth a Unity bug report at the time; the repro above is small enough to
attach as-is, and is still worth filing against 6000.5 for anyone pinned there.

**Never independently confirmed:** that the package repro fully explained *our* failure. The mechanism and
the stack matched, but the confirming experiment — our build with Entities removed — was never completed
before the upgrade made it moot. The 6.6 result is consistent with the attribution without proving it.

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

- **Burst 2.0 moved its editor code, and the miss is silent.** `com.unity.burst` 2.0.0 is a `"type":
  "shim"` package with a Runtime folder and nothing else; the editor half now lives in the built-in
  `UnityEditor.BurstModule`. Any code that reaches for an assembly *named* `Unity.Burst.Editor` — as this
  repo's own build script did — finds nothing and, if it treats that as "no Burst here", produces a
  Burst-less player from a Burst-on request. `Unity.Burst.Editor.BurstPlatformAotSettings` itself is
  unchanged, so look the type up by name across loaded assemblies, and fail loudly when it is absent.
  `GetOrCreateSettings` also gained a parameter, so read arguments off the method rather than hard-coding an
  array.

**The decisive check is not the wasm size** — that moves with your own script edits too (a one-line `const
string` shifted it by 21 KB and made a void build look valid). Two checks cannot be fooled:

- `find Library/Bee -iname '*burst_generated*'` — empty means Burst produced nothing, whatever the settings say.
- the mtime of `Library/BurstCache/AotSettings_<Target>.hash`, which Burst writes from `PrepareOnMainThread`.
  Older than your build means the callback was never invoked.

`bcl.exe` in `Logs/Editor.log` is the same signal read from the other end.

**Do not bisect with per-assembly exclusion.**
`ProjectSettings/Burst_DisableAssembliesForPlayerCompilation_<Target>.json`
(`{"MonoBehaviour":{"DisabledAssemblies":["Some.Assembly"]}}`) looks like the instrument for finding *which*
assembly's Burst code breaks the player, and `BurstAotCompiler` does read it straight off disk at build time
— but excluding an assembly while Burst is globally on leaves the runtime believing Burst is enabled and
resolving function pointers that were never compiled. That state is arguably worse than either extreme, and
neither result it produced here is trustworthy: it was tried twice on 6000.5, once for the eight
`Unity.Entities*` / `Unity.Transforms` / `Unity.Scenes` assemblies and once for `MapRenderer.Jobs`. The
Entities run is the informative one — excluding them did **not** avoid the startup crash, so the fault was
never in Entities' own Burst-compiled code. Bisect by removing `[BurstCompile]` attributes from subsets of
the jobs instead, which leaves no inconsistency.

**Instrument before you bisect.** Five builds went into bisecting `[BurstCompile]` attributes against a
theory that the pipeline was producing wrong data. It was producing correct data the whole time. One build
that printed the real values eliminated the entire style/decode/index path at once. When the symptom is
"the output is wrong" and the pipeline is long, print before you halve.

**To recover a Burst-on build:** close the Editor, delete `Library/Bee`, reopen, build once. This does not
touch `Library/Artifacts`, so there is no asset reimport — it costs a full script/linker/IL2CPP pass and
nothing else (measured 8.5 min on 6.6, against ~1.5 min for an incremental build). Reach for it early; the
cheap-looking cache-busts above cost far more. This trap was still live on Unity 6.6: a Burst-on build with
a correct toggle produced no artifacts until `Library/Bee` was moved aside.

## Open questions (not yet measured)

- ~~Do this project's own jobs reach a worker on the web?~~ **Measured 2026-09-02 — yes. See below.**
- **What `IWorkScheduler`'s inline-on-web policy still costs.** It was adopted when nothing could reach a
  worker. Managed closures remain dead on the web regardless of Burst, so the seam is still needed — but the
  cost of each site that stays managed is now a real parallelism loss rather than a theoretical one.
- A **WebGL2 (OpenGLES3) vs WebGPU** A/B for the *threading* result was never run. Note this is a weak
  hypothesis, not a lead: the graphics-threading suspicion that motivated it has been ruled out (see above),
  so a backend difference would have to act through some other route. (Both backends *were* A/B'd for the
  Burst startup failure below, and behave the same.)

## How to reproduce

A throwaway `ThreadingProbe` MonoBehaviour in a one-object scene, built for WebGL Development and loaded from
a cross-origin-isolated static server, fires every mechanism above and logs `thread id + isMain + elapsed`
per step. Any future re-measurement (e.g. a Unity upgrade, or the WebGL2 A/B) should reuse that shape.
