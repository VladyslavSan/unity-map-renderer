# Async architecture — UniTask, no `System.Threading.Tasks`

**Status:** decided 2026-06-21. Drives stage **S51** (the migration) and constrains all later async work.
Clean-room: our own architecture + standard async patterns; UniTask is a vendored MIT third-party library.

## TL;DR (the decisions)

1. **No `System.Threading.Tasks.Task` in shipped code** — Core *or* Unity. `Task` (and `Task.Run`) is banned.
2. **UniTask is the async primitive**, not Unity's `Awaitable`. Reason below — it's the *only* option that keeps
   `MapRenderer.Core` engine-free **and** lets the async code compile/run in the headless `dotnet test` path.
3. **Threading lives in the Unity layer**; Core stays pure. `UnityEngine.Object` (`Mesh`, `GameObject`) is
   created and destroyed **only on the main thread**.
4. **Data source is dependency-inverted**: an engine-free `UniTask`-returning interface, a highly-efficient
   `UnityWebRequest` production implementation in the Unity layer, and a trivial engine-free test/fixture impl.
5. **The migration is one atomic stage** (S51), not an incremental split — see "Why one stage".

## Problem

- Raw `Task`/`Task.Run` in Unity is harmful: continuations don't marshal back to the main thread (you can
  touch Unity APIs off-thread by accident), it allocates/pressures GC, the .NET threadpool isn't Unity-aware
  (threads can outlive play-mode exit / domain reload; exceptions get swallowed), and there's no
  destroy-driven cancellation.
- But `MapRenderer.Core` is **engine-free by design** — its asmdef references only `Unity.Mathematics`, and
  `Tools/core-tests` compiles the *real* Core `.cs` files with a 2-field math shim and **no UnityEngine**, for
  a ~0.3 s headless test loop (`docs/` + `CLAUDE.md`). Today the Core data layer (`IDataSource.FetchAsync`,
  `FileDataSource`, `HttpDataSource`, `TileScheduler`) is `Task`-based precisely because `Task` is BCL
  (engine-free) and the obvious Unity replacement, `Awaitable`, is not.

## Why UniTask, not `Awaitable`

| | `UnityEngine.Awaitable` | **UniTask** |
|---|---|---|
| Lives in | `UnityEngine` only | `Cysharp.Threading.Tasks`; **NetCore NuGet build** exists (PlayerLoop-stripped subset, netstandard2.1/net6.0) |
| Usable in engine-free Core + `dotnet test`? | **No** — pulls UnityEngine, breaks the headless test path | **Yes** — Core compiles against the NetCore build headless and the vendored Unity build in-editor (same `UniTask<T>` API) |
| Allocation | pooled, but more than UniTask | allocation-free in typical use |
| Cancellation | always throws on cancel | first-class `CancellationToken` + `SuppressCancellationThrow()` |
| API breadth | small (no `WhenAll`/timing) | full (`WhenAll`/`WhenAny`, `RunOnThreadPool`, PlayerLoop timings) |
| License | — | MIT |

`Awaitable` *cannot* give us "zero-Task **and** engine-free Core" — it would force `Task` to stay in Core.
UniTask is the only primitive that satisfies every constraint. (Sources: Cysharp/UniTask repo; issue #33
"UniTask outside Unity"; issue #716 "engine-free `AsUniTask` CS0012"; Unity 6 Awaitable manual.)

### Caveats baked into the plan
- **Issue #716:** on UniTask 2.5.11+/Unity 2023.1+, the `Task↔UniTask` interop extensions share the
  `Cysharp.Threading.Tasks` namespace and drag `UnityEngine.Awaitable` into overload resolution for engine-free
  callers (`CS0012`). **Mitigation:** we remove `Task` entirely, so we never call the interop extensions.
- **`SwitchToMainThread`/PlayerLoop timings are Unity-only** (stripped from NetCore). Core may use **only** the
  PlayerLoop-independent subset (`UniTask<T>`, `RunOnThreadPool`/`SwitchToThreadPool`, `CancellationToken`,
  `UniTaskCompletionSource`, `WhenAll`). Main-thread marshalling is a Unity-layer concern.

## Target architecture

```
MapRenderer.Core (engine-free, headless-testable; UniTask via NetCore build)
  IDataSource : UniTask<TileResponse> FetchAsync(TileId, CancellationToken)   ← Task-free contract
  FileDataSource    : sync File.ReadAllBytes wrapped in UniTask.RunOnThreadPool (no Task)
  InMemory/Fixture  : UniTask.FromResult / UniTaskCompletionSource (tests)
  TileScheduler     : UniTask orchestration + cache
  decode/triangulate/geometry: pure, sync

MapRenderer.Unity (UniTask via vendored build; owns threading + UnityEngine.Object lifecycle)
  UnityWebRequestDataSource : UnityWebRequest + .ToUniTask()  ← efficient production HTTP, zero Task
  MapView mesh-build/consume : IWorkScheduler.Schedule → WorkHandle<T> (poll/consume; no PlayerLoop hop)
  Mesh/GameObject create + Object.Destroy : MAIN THREAD ONLY
  cancellation : destroyCancellationToken
```

**Dependency inversion for HTTP** resolves the last Task-in-Core problem: `HttpClient.GetAsync` is inherently
`Task` and there's no Task-free HTTP in engine-free BCL — so real HTTP moves to the Unity
`UnityWebRequestDataSource`, while Core defines only the `UniTask` contract and ships a Task-free file/fixture
impl. A test-only impl *may* fall back to `Task`/`HttpClient` as an explicit escape hatch, but we don't need it.

### CPU-offload update: `IWorkScheduler`/`WorkHandle<T>`, not unconditionally UniTask

This doc originally stated UniTask as the unconditional primitive for "threading lives in the Unity layer".
That no longer holds for CPU offload: `UnityEngine`'s managed ThreadPool is not wired to WebGL web workers
(`docs/web-target.md`), so `UniTask.RunOnThreadPool` silently never runs its body there. The tile
pipeline's CPU-offload sites (decode dispatch, then the mesh-build kicks) now go through
`MapRenderer.Unity/Concurrency/IWorkScheduler` — `ThreadPoolWorkScheduler` (desktop/editor, reproducing the
old `RunOnThreadPool` dispatch byte-for-byte) or `InlineWorkScheduler` (WebGL, runs the body synchronously on
the calling thread) — and the result travels as a `WorkHandle<T>`, not a bare `UniTask<T>`. Every I/O await
(HTTP fetch, `SwitchToMainThread`) is unaffected and still goes through UniTask exactly as designed above;
this is a CPU-offload-only correction, not a reopening of "why UniTask".

### `TileScheduler`'s negative-cache TTL

A fetch reporting `HasData=false` (HTTP 404/204, a missing file) is deliberately NOT written to the LRU
`TileCache`. Two simpler alternatives were rejected: caching the absent response in the LRU (no expiry —
a transient 404 would stick until LRU eviction, arbitrarily far in the future) and not caching it at all
(a permanently-missing *visible* tile would re-fetch every single frame). Instead it goes into a small
scheduler-level negative cache with a short, injectable-clock TTL: a re-request within the TTL returns
absent without hitting the source; after it expires, the tile is re-fetched. The short TTL is the middle
ground — a recovered tile reappears promptly, while per-frame re-fetch storms are suppressed. The clock is
injectable so tests advance time deterministically, with no wall-clock sleeps.

## Disposal & cancellation contract (where these renderers leak)

Two resource classes, two rules:
- **IDisposable unmanaged (`NativeArray`, `Mesh.MeshDataArray`):** dispose may happen on any thread, but it
  **must** happen on every exit path — wrap allocations in `try/finally`/`using` so a cancellation
  (`OperationCanceledException`) still frees them.
- **`UnityEngine.Object` (`Mesh`, `GameObject`, `Material`):** *not* IDisposable — needs `Object.Destroy`
  (play) / `DestroyImmediate` (edit), **main thread only**. Therefore **never created off-thread**: async work
  produces only disposable *data*; the Mesh/GameObject is created and destroyed exclusively on the main thread.

### The one rule, stated once — *data is a value type; the `Mesh` is a class with a single owner*

This is the codebase-wide invariant the two rules above imply — the framing to reach for whenever a `Mesh`
lifetime question comes up (it's forced by the platform, not a style choice: jobs *cannot* touch a
`UnityEngine.Object`, and only the main thread can create/destroy the GPU resource):

- **Blittable geometry *data* → value types, the job world.** `NativeArray` / `Mesh.MeshData` / `LayerMeshData`
  are **structs** a Burst/worker job writes. They are disposed **deterministically at the
  `ApplyAndDisposeWritableMeshData` boundary** and never held past consume — so they never need a dispose-once
  guard (and a mutable-state struct couldn't safely have one; a struct copy has its own flag — see the
  disposal-guard discussion). `ApplyAndDispose` is the exact point where the value-type *data* becomes the
  reference-type *resource*.
- **The `Mesh` GPU *resource* → a reference type, held by exactly ONE owner.** Created/destroyed only on the
  main thread, and owned by exactly one place at all times: in-cover meshes by `TileManager._loaded`,
  out-of-cover meshes by `PreparedTileCache` (**Model B**). An ownership transfer **nulls the source
  reference** so the mesh is destroyed exactly once by whoever currently owns it — that null-on-transfer *is*
  the double-free/leak guard. Teardown order is always **destroy meshes → then dispose the backend**.
- **Corollary — the dispose-guard machinery only ever touches the *class* side.** A `VerifiedDisposable`-style
  base / the `CountMeshObjects` leak baseline apply to the `Mesh`-owning **classes**; the job-side struct data
  stays trivial by construction. That's why there are **two** leak-guard systems, one per resource class:
  `NativeArray` alloc-vs-dispose counts (`LayerMeshData.DebugLiveAllocCount`) for the *data*, and `Mesh`
  created-vs-destroyed counts (`CountMeshObjects`) for the *resource*. Caching the *data* would tangle the two
  and invert the "arrays return to baseline after consume" invariant — which is exactly why S82 caches the
  `Mesh`, not the `NativeArray`.

**Cancellation ≠ cleanup.** A `CancellationToken` stops the *work*; allocated resources still need explicit
disposal at all four exits: (1) **consumed** → dispose after main-thread upload; (2) **released-while-in-flight**
→ the discard path must `Dispose()` the result, not drop it (today's generation check silently drops — safe
only because the result is managed; it becomes a leak the moment `NativeArray`s land); (3) **cancelled mid-work**
→ off-thread `try/finally` frees what was allocated; (4) **teardown** (`OnDestroy`) → await outstanding, then
dispose all pending data + destroy all Meshes/GameObjects + dispose Materials. With UniTask the await
continuation resumes on the main thread — the single choke-point that owns the upload-vs-dispose branch.

**Leak-guard test (teeth):** drive N tiles through load→release including the race (release a tile whose
mesh build result has completed but not yet been consumed); assert **zero leaked `NativeArray`** (Unity
`NativeLeakDetection`/alloc-vs-dispose counts) and **zero orphaned `Mesh`** (created-vs-destroyed count).

### `TileManager.LoadedTile.Decode`'s residency lifetime (moved from its field doc, UMR-118)

`Decode` is the decode-provisioning handle the fetch produced (`ITileFeatureSource.GetTile`'s result); set
when the fetch completes, null before that and null again once the record no longer owns it. **This field
IS one reference** to an already-decoded tile holding `Allocator.Persistent` buffers, and it is cleared
exactly ONE way — `TileManager.RenderTeardownRecord` RELEASES it (cover change, eviction, restyle,
teardown), for a kicked record precisely as much as a never-kicked one. The mesh kick no longer TRANSFERS
this reference: it takes its own separate one (`TileManager.KickMeshBuild`'s prologue `Acquire()`), so this
field stays live and unchanged across the whole kick. Dropping it any other way leaks the tile.

**Deliberate cost, recorded rather than tested** (no observing tooth exists for it). Because this field now
survives the kick instead of being released when the mesh build completes, a decoded tile's
`Allocator.Persistent` buffers live for the record's WHOLE in-cover lifetime, not just until its mesh is
built — a DURATION increase in peak resident decoded-tile memory on top of the eager-decode BREADTH
increase the prior stage already accepted (every fetched cover tile decodes, kicked or not). Rendered
output is unaffected — this is a resource-lifetime cost, not a behaviour change — and it was chosen
knowingly over the alternative (release at kick completion instead of at teardown), which would have partly
resurrected the transfer machinery this stage deletes. A future residency-ceiling tooth, if one is ever
added, is the thing that would stop this being deliberate.

### `TileManager.DrainMeshBuilds`'s off-PlayerLoop proof (moved from its method doc, UMR-118)

A full drain handles tiles at any stage of the pipeline:
1. Fetch in-flight: parks until the fetch `UniTask` completes, then kicks mesh build inline.
2. Mesh build in-flight: parks until the handle completes, then consumes inline.
3. Neither (tile not yet fetched): marks `Built = true` (nothing to do).

Safe: the fetch `UniTask`'s completion stays OFF the PlayerLoop (see `TileDecodeDispatch`'s class doc) —
supplied by the decode hop on the `HasData` path, and by synchronous/inline completion otherwise — so its
continuation fires without needing the Unity PlayerLoop to advance. The mesh build dispatches through
`IWorkScheduler`, whose completion fires on the COMPLETING thread and is never posted to the PlayerLoop —
the same non-blocking guarantee, by a different mechanism. Parking on either completion via
`UniTaskParkExtensions.WaitOffPlayerLoop` from the main thread therefore does not deadlock (no PlayerLoop
dependency to dead-end on).

### `TileManager.KickMeshBuild`'s off-PlayerLoop completion (moved from its method doc, UMR-118)

`KickMeshBuild` dispatches through `IWorkScheduler` — `ThreadPoolWorkScheduler` on desktop/editor,
`InlineWorkScheduler` on a WebGL player, where no worker ever picks a ThreadPool dispatch up
(`docs/web-target.md`). Under both policies, completion fires on the COMPLETING thread and is never posted
to the PlayerLoop, so `DrainMeshBuilds`'s and `Dispose`'s synchronous-spin polls — which never pump the
PlayerLoop — cannot dead-end waiting for a continuation that would only ever run there. The returned
`WorkHandle<T>` is pollable across frames with no `.Preserve()` needed — its backing completion source
never recycles. The task captures only value-type/immutable inputs (`decode`, layer records are read-only
after `Initialise`); no `UnityEngine.Object` is captured or touched off-main.

### `TileScheduler.Dispose` does not drain in-flight fetches

`TileScheduler.Dispose` cancels and disposes the per-tile CTSs and clears its maps, but does not block to
await outstanding fetches first. This is deliberate: `Dispose` can be called from the main thread, and a
blocking drain there risks deadlock if a fetch's completion needs that same thread to make progress.
In-flight fetches are cancelled best-effort instead; a late completion is harmless because the CTS-identity
guard skips the cache write. A future caller that genuinely needs to await outstanding fetches before
disposing should add an explicit `DrainAsync()` method rather than making `Dispose` itself block.

## Packaging

- **Editor build:** vendor UniTask under `Assets/Code/ThirdParty/UniTask/` (committed, version-pinned,
  self-contained) + `THIRD-PARTY-NOTICES.txt` entry. **Not UPM** — UPM resolves into the gitignored
  `Library/PackageCache` only when Unity runs, so a UPM dependency is unbuildable without Unity, defeating the
  engine-free goal.
- **`dotnet test` build:** UniTask **NetCore NuGet** `PackageReference` in `Tools/core-tests` (the Unity source
  won't compile headless — the NetCore build is the PlayerLoop-stripped subset built for exactly this).
- **Pin both distributions to the same UniTask version tag** (Cysharp releases UPM + NuGet in lockstep) so the
  headless and editor builds can't drift on API.

## Why one stage (no intermediate steps)

The chain `IDataSource.FetchAsync → TileScheduler → MapView consume` is one connected contract. Migrating Core
to `UniTask` while leaving the Unity consumer on `Task` would require a temporary `.AsUniTask()`/`.AsTask()`
bridge at the seam — which is throwaway **and** is the exact API that trips the #716 engine-free `CS0012` trap.
So the whole chain moves in one reviewed commit. (The developer may sequence internally — add the package, then
migrate — but it lands atomically.)

## Out of scope (separate, composes after)

- **S48** — advanced `NativeArray`/`Mesh.MeshDataArray` upload API for fills. Different axis (upload
  efficiency, not the async model); composes on the clean UniTask base. Its `NativeArray`s extend the
  leak-guard above.
- ECS/BRG batched rendering (S49); the deferred low-zoom frustum-precision decision.
