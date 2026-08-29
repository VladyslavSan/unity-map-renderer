# Threading on the web (WebGL / WebGPU)

**What this is:** empirical learnings about what threading does and doesn't work on the web build — **facts
we measured, as of Unity 6.5** — not a design or a plan. Everything here is an observation; the one
consequence for our own code is called out as such.

**Measured:** 2026-08-29 on a WebGL **Development** build (WebGPU backend, Unity 6000.5.0f1,
`webGLThreadsSupport=1`, `WebGLExceptionSupport=FullWithoutStacktrace`), served cross-origin-isolated
(COOP `same-origin` + COEP `require-corp` on the main document, verified). May change with a future Unity
version — re-measure (see *How to reproduce*).

## TL;DR

On the web build there is **no background execution of any kind**: not the managed .NET ThreadPool, not a
raw `System.Threading.Thread`, not even Unity's own job system (jobs run inline on the main thread — the
worker pthreads never spawn). The only concurrency that works is **cooperation that Unity's scheduler drives
on the main thread**.

> **The rule:** what survives on the web is what **Unity's own scheduler drives on the main thread** —
> coroutines, UniTask PlayerLoop awaitables, Unity `Awaitable` main-thread ops, Unity async I/O. **Anything
> routed through the .NET thread _or timer_ pool is dead** — `Task.Run`, `ThreadPool`, `Awaitable.Background`,
> raw `Thread`, **and `Task.Delay`** (its BCL timer is serviced by the ThreadPool, which has no workers).

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
threading — and on this build even that did not dispatch to workers (suspected causes: WebGPU forcing
`kGfxThreadingModeDirect`, or cross-origin isolation not truly active at runtime — see Open questions).

## Why it breaks meshing (our pipeline)

The mesh build is a synchronous, thread-agnostic, Burst-`.Run()` pipeline — it deliberately never uses the
job system's `.Schedule()`; every stage runs `.Run()` (Burst-compiled, inline on the calling thread) so the
whole pipeline is callable off the main thread. See the explicit comments at `FillMeshPipeline.cs`
(`// Run, like every other stage — the pipeline must stay callable off the main thread`) and
`MvtGeometryMaterializer.cs` (`// Run (not Schedule) so the pipeline is callable off the main thread`).

All of its threading comes from one place: `TileManager.KickMeshBuild` / `KickSourcelessBackground` wrap the
build body in **`UniTask.RunOnThreadPool`** to spread *whole tiles* across ThreadPool threads. That ThreadPool
is dead on the web (row 5 above) ⇒ `MeshBuildTask` never completes ⇒ `ConsumeMeshBuild` never uploads ⇒ the
map is blank (no crash, no error). Symbol placement is unaffected — it already runs on the main thread.

## Things that follow from the above

- Anything that must "do work while the frame continues" cannot rely on the .NET thread or timer pool on the
  web; only Unity-scheduler-driven cooperation (coroutine / PlayerLoop) runs, and real CPU work lands on main.
- Because the mesh pipeline is already `.Run()`-based (thread-agnostic), the work itself would run fine on the
  main thread on the web — it is only the `UniTask.RunOnThreadPool` wrapper that is dead. Converting
  `.Run()` → `.Schedule()` would **not** buy web parallelism (the worker threads don't run here).

*(What to do about it is a separate, still-open decision — deliberately not recorded here.)*

## Open questions (not yet measured)

- The runtime value of `crossOriginIsolated` / `typeof SharedArrayBuffer` in the page was never captured
  (the serving headers are correct, so it *should* be `true`). If it is actually `false`, the native worker
  threads would be revivable at the hosting layer and the "even jobs run on main" result would not hold.
- A **WebGL2 (OpenGLES3) vs WebGPU** A/B was never run. The `kGfxThreadingModeDirect` hint suggests the
  single-threaded job behaviour may be WebGPU-specific; a WebGL2 build might spawn the workers.

## How to reproduce

A throwaway `ThreadingProbe` MonoBehaviour in a one-object scene, built for WebGL Development and loaded from
a cross-origin-isolated static server, fires every mechanism above and logs `thread id + isMain + elapsed`
per step. Any future re-measurement (e.g. a Unity upgrade, or the WebGL2 A/B) should reuse that shape.
