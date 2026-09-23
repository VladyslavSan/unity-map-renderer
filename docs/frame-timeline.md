# Frame timeline & main-thread present-slack (Unity 6 + URP)

*Scope:* the CPU/GPU frame pipeline as it applies to **this** project (Unity `6000.5.0f1`, URP,
multithreaded rendering), and specifically **where on the main thread we can run otherwise-mandatory
main-thread work — `Mesh.AllocateWritableMeshData` — without extending the presented frame time.**

This is the architectural foundation for the pooled-MeshData preallocation work — read it for the *why
the window exists and how to reach it*.

> **Confidence markers.** Claims are tagged:
> - **[MODEL]** — Unity's documented, stable threading/pacing model. Safe to rely on.
> - **[EMPIRICAL]** — plausible from the model but must be confirmed on *this* project with a Profiler
>   capture or a play-mode spike before any code depends on it. Never treated as settled here.

Related: `docs/async-architecture.md` (UniTask / thread-hopping in the tile pipeline),
`docs/lessons-learned.md` (allocation-measurement gotchas).

---

## 1. The pipeline — three stages, pipelined across frames

With multithreaded rendering (URP default on desktop) a single displayed frame flows through three
executors that run **concurrently on different frames**: **[MODEL]**

```
        frame N          frame N+1        frame N+2
Main:  [Update+submit]  [Update+submit]  [Update+submit]
Render:                 [consume N]       [consume N+1]
GPU:                                      [draw N]        [present N @ vblank]
```

1. **Main thread** — runs the `PlayerLoop`: `Update` → `LateUpdate` → the SRP render loop
   (`RenderPipeline.Render`, which culls and **records** draw commands). When `Render()` returns, the
   command stream is **handed to the render thread** — it is *not* GPU-complete. **[MODEL]**
2. **Render thread** (GfxDevice thread) — consumes that command stream and submits to the graphics
   driver. Runs *while the main thread is already working on the next frame*. **[MODEL]**
3. **GPU** — executes; the result is shown at the next presentation point.

In this project the per-frame map work (`TileManager.Tick`, incl. the mesh build **kick** that calls
`AllocateWritableMeshData`) runs inside MonoBehaviour **`Update`** — i.e. in the main-thread box *before*
render submission (`MapViewComponent.Update → MapView.Tick`; camera commit is in `LateUpdate`, just before
culling).

## 2. The invariant — slack exists when the main thread is NOT the bottleneck (vsync is incidental)

**[MODEL]** The one condition that governs whether there is a free window is:

> **Is the main thread the frame's bottleneck?**
> - **No** (the render thread + GPU are the heavy side — the *consumer-bound* / GPU-bound regime) → the main
>   thread finishes its frame faster than the pipeline can consume it, runs ahead until it hits
>   `maxQueuedFrames`, and **blocks**. That block is the slack. **This is a property of pipelined
>   multithreaded rendering, not of vsync** — it holds with vsync fully off.
> - **Yes** (CPU-main-bound — e.g. the `FrustumTileSelector` descent, `MapRenderer.Tile.CoverSelect`, eating
>   the budget every motion frame) → the main thread never catches the queue limit; it is always the critical
>   path; **no pacing mode conjures free time**.

For a fill-bound vector map (overdraw, transparent lines — the render/GPU side is reliably the heavy side),
the **consumer-bound regime is the normal case**, so the window is reliably present.

**`maxQueuedFrames`** (default **2**) is what actually decides *where* the block lands — how many frames the
main thread may run ahead before it stalls. **It, not vsync, is the mechanism.**

**Where vsync fits — one *sufficient* condition, not the enabler.** `vSyncCount ≥ 1` paces the *consumer*
(present) at the refresh rate, which is one way to make the main thread not-the-bottleneck (if its work fits
inside the v-blank interval). This project's `Bootstrapper.cs` sets `vSyncCount = 1`
(+ `Application.targetFrameRate = refreshRate`, which vSync ≥ 1 then **ignores**) — but that is just *our
current pacing choice*, not what creates the window. An uncapped, GPU-bound build has the same window; a
vsync-on but CPU-main-bound frame has none.

The idle surfaces in the Profiler as `WaitForTargetFPS` (main) and/or `Gfx.WaitForPresentOnGfxThread`
(render); the split depends on the regime. Whether a given frame has slack, and how much, is **[EMPIRICAL]** —
read it off a capture. Design the refill to key on **measured main-thread headroom**, never on "is vsync on."

## 3. Reaching the slack — schedule at the frame tail, don't hook the wait

**You cannot inject code *into* the native present/v-blank wait** — no callback fires while the main thread
is blocked there. **[MODEL]** The reachable equivalent:

> **Schedule the work at the *tail* of the frame, after render submission, so it runs on the main thread
> *in parallel with the render thread consuming this frame's commands*.** As long as
> `Update + submit + tail-work` stays under the consumer's frame time (the render/GPU pipeline, or the
> v-blank when vsync-paced), the tail-work consumes time the main thread would otherwise spend blocked — the
> presented frame rate (bounded by the GPU/consumer, *not* by the main thread) is unchanged.

The reframe matters: "after rendering is triggered" is right **not** because that's when the present-wait
is, but because submitting *first* lets the render thread + GPU run while we work. Doing the work *before*
submission would delay `Render()` and could *extend* the frame.

**Concretely — the overlap window.** Once the main thread hands frame N's command stream to the render
thread, the render thread spends **~4–7 ms** *(order-of-magnitude for this project — **[EMPIRICAL]**, read off
a capture)* consuming those commands and submitting to the driver. During that span the main thread is doing
our tail-work **in parallel** with the render thread — two threads busy at once — and only *then* does frame
N+1 begin:

```
Main N:   [Update+camera][SRP submit] ─hand off─▶ [tail-work: pool refill] │ [Update N+1 …]
Render N:                             [consume cmds → driver submit ~4-7ms]
```

Whether that ~4–7 ms is **free** or **stolen from N+1** is decided by whether the main thread is the
bottleneck (§ "The invariant — slack exists when the main thread is NOT the bottleneck" above) — via
`maxQueuedFrames`, not vsync:
- **Consumer/GPU-bound frame** (main thread not the bottleneck) — it would otherwise hit the queue limit and
  **block** before N+1; the tail-work fills that block → truly free. (Vsync-paced or uncapped-GPU-bound alike.)
- **CPU-main-bound frame** — the main thread would **start N+1 immediately**; the tail-work now *delays* N+1
  → added frame time, *not* free.

Hence the budget guardrail below: the refill must detect a no-slack (CPU-bound) frame and back off, or it
steals from N+1 instead of filling idle time.

**Hook: `RenderPipelineManager.endContextRendering`.** **[MODEL]** It fires **once per `RenderPipeline.Render`
call** (≈ once per frame) on the main thread, right after the frame's cameras are submitted, receiving the
camera list. Understand the three-tier SRP callback family and pick with care:
- `endCameraRendering` — **per camera**. Too granular; fires N times a frame.
- **`endContextRendering` — once per frame, ALLOCATION-FREE.** ✅ Use this.
- `endFrameRendering` — once per frame, **same functionality but heap-allocates every frame** (Unity's own
  docs say so and recommend `endContextRendering` instead). ❌ Avoid — an allocating hook would defeat the
  whole point, since the slack work must itself be GC-alloc-free (`TileLoadMeasurementTests`' teeth).
- `Application.onBeforeRender` / anything in `Update`/`LateUpdate` — *before* submission (would delay render).
- a custom `PlayerLoopSystem` at the loop tail — works too, but `endContextRendering` is the supported,
  allocation-free, URP-native seam.

**Non-negotiable: the tail-work must be budgeted/adaptive.** Because the window is real only when there's
slack (§ "The invariant — slack exists when the main thread is NOT the bottleneck" above), the tail routine
must cap its own cost and back off when the main thread has no headroom — never an unconditional fixed batch.
Otherwise, on a CPU-main-bound frame it directly extends frame time.

## 4. Where `Mesh` allocate/apply fit — and the retention risk

The two main-thread-only `Mesh.MeshData` operations, and what each costs:

| Op | Called on | What it *actually* costs | Where it should live |
|----|-----------|--------------------------|----------------------|
| `AllocateWritableMeshData(n)` | main-only *(`MeshDataPayload.AllocateTracked`)* | allocates **CPU** staging buffers; **no GPU resource** | **the frame-tail slack** (§ "Reaching the slack — schedule at the frame tail, don't hook the wait") — pre-allocate a pool, refill here |
| write geometry into the MeshData | **worker** (off-main) | pure CPU (`SetVertexBufferParams` + fill) | already background |
| `ApplyAndDisposeWritableMeshData` | main-only *(the call)* | **cheap on the main thread** — updates the CPU-side mesh, hands off the native buffer, **enqueues** a GPU upload; the **upload itself runs on the render thread**, deferred | frame-tail slack too — but the *upload load* it adds is render-thread budget (see below) |

The key correction: `ApplyAndDispose` is **not** a synchronous main-thread GPU transfer. The call must be
*made* from the main thread (API rule), but it only updates the CPU mesh + enqueues the upload; the actual
buffer creation/transfer executes **on the render thread**, often lazily. So its main-thread cost is small —
what it really spends is **render-thread time**, which matters because the render thread's busyness is what
*creates* the slack (§ "The invariant — slack exists when the main thread is NOT the bottleneck" above).
Uploading a lot there spends that same budget.

**How much that upload costs is backend-dependent — do NOT design to OpenGL's worst case.** **[EMPIRICAL]**
- **OpenGL / GLES** — the serialized floor: all GPU work funnels through the single render thread, so uploads
  sit directly on its budget. Legacy (older Android GLES3).
- **Vulkan / D3D12 / Metal** — multi-threaded command recording, free-threaded resource creation, and a
  **dedicated transfer/copy queue** that can run uploads *async off the graphics timeline*. The upload cost
  is small-to-negligible on the render thread's critical path. (Caveat: API *capability* ≠ automatic Unity
  exploitation — depends on backend + native graphics jobs + version; verify on the shipping backend.)
- Practical target split: desktop Vulkan/D3D12/Metal + iOS Metal → modern path (upload ≈ free); older
  Android GLES → the serialized floor. Keep the per-Tick upload caps (`MaxConsumesPerTick`,
  `MaxVerticesPerTick`) as the guardrail **for the floor**, not the assumed cost everywhere.

`AllocateWritableMeshData` produces only CPU staging memory (no GfxDevice resource), so relocating it to
`endContextRendering` is stall-free on any backend — **[EMPIRICAL]**, confirm on a capture.

### 4a. What's relocatable to the slack — classify on two axes

Not everything main-thread is safe to move to the post-submit slot. Test each action on **two independent
axes** (`endContextRendering` fires *after* frame N's cameras are culled + recorded — § "The pipeline —
three stages, pipelined across frames" and § "Reaching the slack — schedule at the frame tail, don't hook the
wait" above):

**Axis 1 — does it change what the cull sees ("the world")?** Frame N's scene is already extracted, so:
- **Cull-neutral → relocatable, invisible to N:** decode, build, project, cover-select, process the
  request queue, start pipelines, `AllocateWritableMeshData`, **and creating a `Mesh` + uploading its
  geometry** (a fresh Mesh nothing in N's draw list references).
- **Cull-modifying → the ordered "commit," lands in N+1:** create/activate a `GameObject`, enable a
  `Renderer`, (re)assign a mesh/material on an *already-active* renderer, register a draw with a backend.
  Doing these here is fine *as N+1 work* (tile appearance is async anyway) — but they are **not** invisible.

> The principle: **"produce resources/data" is freely relocatable; "commit to the scene" is the world-
> modifying step.** Split every tile-pipeline action on that line.

**Axis 2 — what thread bears the cost (once Axis 1 says cull-neutral)?**
- **Pure CPU** (decode, build, allocate staging, create the Mesh *object*, cover-select) → free on
  *both* threads. Move freely.
- **GPU upload** (`ApplyAndDispose`'s deferred transfer) → cheap on main, spends **render-thread** budget →
  only "free" if the render thread has headroom, and mostly-free on modern APIs (above). Keep it budgeted.

So "cull-neutral" (correctness) and "which thread pays" (performance) are **separate questions** — mesh
*creation* clears Axis 1 unconditionally; the *upload* clears Axis 1 too but still owes Axis 2 a check on the
GL/GLES floor.

**⚠ The load-bearing empirical risk — gate everything on this.** The pool's premise is **holding
pre-allocated `MeshDataArray`s across ≥1 frame** until a background worker pops and fills one. This repo's own
note calls an unapplied `MeshDataArray` "a native leak Unity tracks" (`MeshDataPayload.cs`). The API
contract permits a persistent allocation disposed whenever you like, but the **editor's leak/safety detection
may warn per-frame** about the held-but-unapplied arrays. **[EMPIRICAL]** — a play-mode spike (allocate, hold
N frames, watch the console + the safety-handle checks) must clear this **before** the pool is designed
around it. If it warns, the pool needs a different shape (e.g. allocate-and-fill-then-hold, or a shorter
hold).

## 5. The coupling to keep honest

The pool's payoff is **conditional**:
- It converts the mesh build into a fully-background step. The build's only main-thread anchor is the
  `AllocateWritableMeshData` call before the write graph (`MeshDataPayload.AllocateTracked`,
  `docs/job-scheduling-design.md` § "The tile build — three polled steps"); nothing else in the build
  touches a `Unity.Object` off-main. That architectural win stands **independently**.
- But it is "free" (no added frame time) **only if the main thread has slack** (§ "The invariant — slack
  exists when the main thread is NOT the bottleneck" above). If `CoverSelect` is
  saturating the main thread every motion frame, the slack isn't there — so the pool and
  `TileLoadMeasurementTests`' select-tax are **coupled**, and the Profiler capture in checklist item 2 tells
  you which regime you're in.

## 6. Empirical checklist (before code depends on any of this)

Run these from the `TileLoadingStressTest` scene (`TileLoadStressDriver`, Berlin sweep, vSync=1):

1. **[gating]** Hold a `MeshDataArray` unapplied across N frames — does Unity's leak/safety detection warn?
   (§ "Where `Mesh` allocate/apply fit — and the retention risk")
2. Capture the frame timeline — is the frame consumer/GPU-bound (main thread not the bottleneck ⇒ slack
   present), and where does the wait sit (`WaitForTargetFPS` vs `Gfx.WaitForPresentOnGfxThread`)?
   (§ "The invariant — slack exists when the main thread is NOT the bottleneck")
3. Does `AllocateWritableMeshData` at `endContextRendering` contend with the render thread?
   (§ "Where `Mesh` allocate/apply fit — and the retention risk")
4. Which regime does a motion frame sit in — CPU-main-bound (`CoverSelect` saturating the main thread, no
   slack) or consumer/GPU-bound (slack present)? (§ "The coupling to keep honest", and the Profiler capture
   in item 2)
