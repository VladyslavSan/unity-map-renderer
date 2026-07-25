// Namespace-collision guard (see GlyphAtlasTexture.cs's header comment for the full explanation): this
// file lives in MapRenderer.Unity.Text.Placement and uses Unity.Mathematics types (float2/float4/float4x4/
// double2/double3) — TOP-LEVEL `using Unity.Mathematics;` + unqualified types, NEVER an inline
// `Unity.Mathematics.X` (would bind to the nonexistent `MapRenderer.Unity.Text.Placement.Unity.Mathematics`,
// CS0234). `MapRenderer.Unity.Text` does not collide with any bare UnityEngine type.

using System;
using System.Collections.Generic;
using MapRenderer.Core.Geo;
using MapRenderer.Core.Lifetime;
using MapRenderer.Core.View.Camera;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Jobs;
using Unity.Mathematics;
using Unity.Profiling;
using UnityEngine;
using MapRenderer.Unity.Common;
using MapRenderer.Core.Text;
using MapRenderer.Core.Text.Placement;
using MapRenderer.Jobs;
using MapRenderer.Unity.Rendering.Backend;
using MapRenderer.Unity.Rendering.Map;
using MapRenderer.Unity.Rendering.Materials;
using MapRenderer.Unity.Rendering.Style;

namespace MapRenderer.Unity.Text.Placement
{
    /// <summary>
    /// S20 Slice 1: the dedicated PER-FRAME label renderer (F1, stage doc §6) — a plain class (NOT a
    /// MonoBehaviour), owned by <see cref="MapView"/>, ticked as the last step of <c>MapView.LateUpdate</c>
    /// AFTER <see cref="MapCamera.SyncToCamera"/> (the committed-camera seam). Every <see cref="Tick"/>
    /// re-projects every label's anchor and rebuilds the projection/staging/collision pools from scratch,
    /// then hands each surviving candidate's glyph corners to <see cref="WorldLabelRenderer"/> (Epic A / A1),
    /// which owns the persistent per-<c>(tile,slot,kind)</c> meshes and presenters a real camera render
    /// redraws BY ITSELF (no orchestrator, no <c>Graphics.RenderMesh</c>). This is the "placed every frame"
    /// path (<c>ARCHITECTURE.md</c> §"Two geometry classes"), structurally distinct from the static
    /// per-<c>(tile,layer)</c> <see cref="Backend.ITileRenderBackend"/> meshes (T5: this type never calls
    /// <c>AddTileLayer</c> — grep-checked by <c>LabelPlacementStructureTests</c>).
    ///
    /// <para><b>Collision (Slice 2).</b> Every on-screen label's screen-space AABB (<see cref="LabelBox"/>,
    /// <c>text-padding</c> applied) is run through <see cref="LabelCollision.SelectSurvivors"/> — greedy,
    /// sort-key-driven, permutation-invariant survivor selection (stage doc §4 T1) — BEFORE the world emit,
    /// so only survivors emit quads. The projection/collision/emit pass runs over reused arrays (no
    /// per-frame managed allocation — T4). Collision is GLOBAL across every symbol layer (D8) — only the
    /// DRAW is partitioned by material slot.</para>
    ///
    /// <para><b>Presence (E2, design §5).</b> A slot that produces no quads this Tick (no atlas, empty
    /// batch, everything culled/suppressed) is HIDDEN, not left drawing stale content — the mirror image
    /// of the pre-E2 Editor blink; <see cref="WorldLabelRenderer.EndFrame"/> owns this per-slot show/hide.</para>
    /// <para><c>internal</c> (not <c>public</c>): mirrors <c>Tile.TileManager</c>'s own visibility — this
    /// is an implementation detail <see cref="MapView"/> owns, not a public API surface. Its members stay
    /// declared <c>public</c> regardless (matching <c>TileManager</c>'s convention), which also keeps
    /// <see cref="Tick"/>'s effective accessibility domain aligned with its <c>internal</c>
    /// <see cref="Backend.SceneFrame"/> parameter (CS0051 would fire if this class were <c>public</c>).</para>
    /// </summary>
    internal sealed class LabelPlacementSystem : VerifiedDisposable
    {
        /// <summary>Profiler marker name constants (SSOT) for the per-frame label path — referenced by the
        /// <see cref="ProfilerMarker"/> fields below and by <c>ProfilerMarkerTests</c> (internal, via
        /// <c>InternalsVisibleTo</c>). Hierarchical names so the Profiler flat search reads as a tree.</summary>
        internal static class ProfilerMarkerNames
        {
            // Runs BEFORE Tick (native mirror compaction of the winners' baked slices — the Stage-2 residual
            // per-frame copy that replaced the managed SoA Build). Marked so it isn't invisible self-time in the
            // View.LateUpdate umbrella (it sits in the timeline gap between Symbol.BatchBuild and Symbol.LabelTick).
            // R1: the heavy compaction below now runs only on a front-set change (a reconcile swap / SetStyle /
            // Dispose) — a same-version frame runs only the three per-frame mask memcpys, so this marker now mostly
            // measures a memo HIT (that is the point of R1: see docs/symbol-label-perf-design.md §10.2).
            internal const string Gather      = "MapRenderer.Symbol.Gather";
            internal const string Tick        = "MapRenderer.Symbol.LabelTick";
            internal const string Project     = "MapRenderer.Symbol.Project";
            internal const string ProjectFill = "MapRenderer.Symbol.ProjectFill";
            internal const string Stage       = "MapRenderer.Symbol.Stage";
            // R3: grid sizing + Schedule only — the job's own wait no longer lives here (see CollideHarvest).
            internal const string Collide     = "MapRenderer.Symbol.Collide";
            // R3: Complete()-ing the PREVIOUS Tick's scheduled collision, at the top of THIS Tick. Must read ≈0 —
            // a non-zero value means the deferred job did not finish in the inter-frame gap and the wait has
            // merely moved, not gone (see the stage design doc §10.3's Gate step P).
            internal const string CollideHarvest = "MapRenderer.Symbol.CollideHarvest";
            internal const string Emit        = "MapRenderer.Symbol.Emit";
            // Emit splits into two costs with DIFFERENT shapes, and lumping them hid that: EmitLoop is
            // per-CANDIDATE (fade lookups + the per-quad world-vertex hand-off), while EmitDecay is per-LIVE-FADE-
            // IDENTITY — DecayUnseenFadeRecords sweeps every key in _fadeOpacity every frame regardless of how many
            // candidates there are. With tens of thousands of live identities the second can rival the first, so
            // an optimization aimed at "Emit" could attack the wrong half. Same reason Gather got its own marker.
            internal const string EmitLoop    = "MapRenderer.Symbol.EmitLoop";
            internal const string EmitDecay   = "MapRenderer.Symbol.EmitDecay";
        }

        // Per-frame profiler markers for the label path (Profiler window → search "MapRenderer.Symbol").
        // PmTick is the whole per-frame submit; the sub-markers break it into project→collide→emit.
        private static readonly ProfilerMarker PmGather =
            new(ProfilerCategory.Scripts, ProfilerMarkerNames.Gather);

        private static readonly ProfilerMarker PmTick =
            new(ProfilerCategory.Scripts, ProfilerMarkerNames.Tick);

        private static readonly ProfilerMarker PmProject =
            new(ProfilerCategory.Scripts, ProfilerMarkerNames.Project);

        // PmProject breakdown: ProjectFill = gather + projection (the SymbolProjectionJob wait above the threshold,
        // else the serial main-thread fill); Stage = the managed staging loop that reads the projected screen
        // positions and builds the collision candidates/boxes/quads. Splitting them answers the architecture
        // question the timeline can't at a glance: is the Project cost a JOB WAIT (ProjectFill) or MANAGED main-
        // thread work (Stage)? The two nest inside PmProject so the umbrella total is preserved.
        private static readonly ProfilerMarker PmProjectFill =
            new(ProfilerCategory.Scripts, ProfilerMarkerNames.ProjectFill);

        private static readonly ProfilerMarker PmStage =
            new(ProfilerCategory.Scripts, ProfilerMarkerNames.Stage);

        private static readonly ProfilerMarker PmCollide =
            new(ProfilerCategory.Scripts, ProfilerMarkerNames.Collide);

        // R3: this marker MUST read ≈0 — see ProfilerMarkerNames.CollideHarvest's comment.
        private static readonly ProfilerMarker PmCollideHarvest =
            new(ProfilerCategory.Scripts, ProfilerMarkerNames.CollideHarvest);

        // Emit = the A-4 fade + world-renderer hand-off (managed, main thread) that runs AFTER collision — it
        // is a candidate main-thread hot spot in its own right, not hidden inside the LabelTick umbrella.
        private static readonly ProfilerMarker PmEmit =
            new(ProfilerCategory.Scripts, ProfilerMarkerNames.Emit);

        // The two halves of PmEmit — see the ProfilerMarkerNames comment for why they are split.
        private static readonly ProfilerMarker PmEmitLoop =
            new(ProfilerCategory.Scripts, ProfilerMarkerNames.EmitLoop);

        private static readonly ProfilerMarker PmEmitDecay =
            new(ProfilerCategory.Scripts, ProfilerMarkerNames.EmitDecay);

        // Epic A / A1 (design §11 A1 D7): the world-anchored demo-path materials — clones of
        // MapMaterialSet.SymbolTextWorld/SymbolIconWorld, used for the demo path (no per-layer materials
        // passed) and as the fallback for any slot without a supplied world material. Null (no Shader.Find
        // fallback — D7 orchestrator decision) means that world draw path stays inert.
        private Material _worldTextMaterial;
        private Material _worldIconMaterial;

        // Epic A / A1 (design §3.3, §11 A1 D1): the dedicated world-anchored point/icon renderer — owns its
        // own per-(tile,slot,kind) meshes/presenters, ticked BeginFrame/Emit/EndFrame every Tick.
        private readonly WorldLabelRenderer _worldRenderer = new WorldLabelRenderer();

        // ── B-4a: collision runs as a Burst IJob (LabelCollisionJob) directly over the STAGE job's native output
        // pools (_sjCandidates/_sjBoxes — see below), with no managed round-trip. Every label, point (1 box) or
        // curved along-line (N glyph boxes), is ONE LabelCandidate spanning a contiguous range of the flat box
        // pool, so a road name and a city name compete in ONE greedy pass; the grid keeps it ~O(n·k), and the
        // greedy is inherently serial (each placement depends on all prior survivors) so it is ONE job. The job
        // sorts _sjCandidates in place into placement order and writes _nSurvivors; the emit loop reads the sorted
        // candidates (via LabelCandidate.LabelIndex, stable across the sort) + survivor flags + _sjEmit/_sjQuads.
        // The uniform grid is PRE-SIZED on the main thread (LabelCollisionGridSizing) each frame because a Burst
        // job cannot grow a NativeArray. Bit-identical to the managed LabelCollision reference (differential test).
        private NativeList<byte>           _nSurvivors;
        private NativeList<int>            _gridCellHead;
        private NativeList<int>            _gridNodeBox;
        private NativeList<int>            _gridNodeNext;
        private NativeArray<int>           _survivorCountOut;

        // R3 (deferred collision, design §10.3): the collision scheduled at the END of a Tick and consumed at the
        // START of the next one, so the main thread never blocks on the single-threaded greedy. _pendingCandidateCount
        // pins the candidate count the handle was scheduled over — the harvest must NOT read _sjCounts[0], which this
        // frame's stage job is about to overwrite.
        private JobHandle _collisionHandle;
        private bool      _collisionPending;
        private int       _pendingCandidateCount;

        // AssertFadeIdsUnique's scratch set — persistent + Cleared per call (not a fresh Allocator.Temp container
        // per Tick), so the debug-only duplicate check never allocates managed memory in the Editor (a per-call
        // NativeHashSet's AtomicSafetyHandle/DisposeSentinel bookkeeping under ENABLE_UNITY_COLLECTIONS_CHECKS
        // does — LabelPlacementAllocTests caught it as a periodic steady-state GC alloc). Allocated LAZILY, inside
        // AssertFadeIdsUnique itself (guarded on IsCreated), NOT here in the ctor: the method's own [Conditional]
        // strips its CALL sites in release, and the lazy-alloc guard means the method BODY never runs there either
        // — so a release build never executes `new NativeHashSet(...)` and this stays a zero-allocation default
        // (uncreated) struct. (A `#if UNITY_ASSERTIONS` field guard would also work, but is unnecessary — no
        // release-build reference to this field exists outside the [Conditional] method.)
        private NativeHashSet<long> _debugFadeIdSeen;

        // The batch the managed-list Tick overload (demo / test seam) builds each call from LabelInstances; the
        // production overload receives a pre-built, version-cached batch from SymbolLabelSubsystem instead.
        private readonly SymbolLabelBatch _demoBatch = new SymbolLabelBatch();

        // B-3: the pre-projection horizon/distance cull radius, in viewport-spans of ground around the look-at.
        // CONSERVATIVE by default — a top-down view's on-screen labels sit within ~one span, so this only trims
        // the far horizon band a tilted view piles up (where labels are discarded/unstable anyway). Raise to keep
        // more distant labels, lower to cull the horizon harder — a maintainer tunable (see LabelViewDistance).
        private const double LabelViewportSpans = 8.0;

        // ── A-4: fade state machine ──────────────────────────────────────────────────────────────────────
        // A persistent per-FADE-identity opacity (LabelCandidate.FadeId) eased toward 1 (collision-placed) or 0
        // (suppressed / gone) at 1/FadeDurationSeconds per second, so a label eases in/out instead of popping.
        // PlacedQuad.Color.w already carries the alpha the shader emits — fade is pure CPU state, no shader change.
        // internal (not private): SymbolLabelSubsystem derives its departing-tile grace window from this so the grace
        // always exceeds the fade — if this constant changes, the grace tracks it and a purge never drops a still-
        // fading (visible) departing label (which would pop).
        internal const float FadeDurationSeconds = 0.3f;
        private const float FadeEpsilon = 1e-3f; // below this, a record is invisible → not emitted / dropped
        // Holds identities that are visible or fading IN — nothing is parked at 0 once a fade-OUT reaches the
        // floor, because an invisible identity carries no state and nothing would ever collect it (see EaseFade).
        // Sized by VISIBLE labels, not by staged candidates: the two differ by an order of magnitude in a dense
        // view (~2.5k placed quads vs ~31k candidates), and DecayUnseenFadeRecords walks this map in full every
        // frame, so its size is a per-frame COST and not merely memory.
        private readonly Dictionary<long, float> _fadeOpacity = new Dictionary<long, float>();
        // The identities EaseFade STORED this frame — i.e. exactly the _fadeOpacity keys it touched, not every
        // candidate it looked at. The direction the decay sweep needs is `stored ⇒ seen`: a key it finds unseen
        // really was untouched, so nothing alive is swept. (The converse can fail harmlessly — two candidates
        // sharing one FadeId, where the second drops what the first stored — but the sweep only ever consults
        // this set for keys it enumerated out of _fadeOpacity, so a stale entry is never read.) Keeping it in
        // lockstep with the store is why both live inside EaseFade rather than in the emit loop.
        private readonly HashSet<long> _seenFade = new HashSet<long>();
        private readonly List<long> _fadeScratchKeys = new List<long>(); // reused decay-sweep buffer (no per-frame GC)

        // FadeIds of records the gather cull (tile-coverage / B-3 distance) hit THIS frame but whose fade is still
        // alive: instead of a hard skip (which would pop the label — a pre-cull produces no geometry, so nothing
        // draws its fade), gather keeps STAGING them and records their FadeIds here; the emit loop then eases them
        // toward 0 (a fade-OUT in place at their live position) regardless of collision survival. Once a record's
        // fade settles to <= epsilon the gather cull hard-skips it (the perf win returns in steady state). Cleared
        // + repopulated each frame in gather → zero per-frame GC.
        private readonly HashSet<long> _forceFadeOut = new HashSet<long>();

        // ── A-5: sticky-placement hysteresis ────────────────────────────────────────────────────────────────
        // FadeIds that SURVIVED last frame's collision. Staging looks each candidate up here to set
        // LabelCandidate.WasPlacedLastFrame, which biases the greedy sort so an incumbent keeps its slot over a
        // near-tied newcomer (killing the tile-churn/reprojection tiebreak flip that reads as flicker). Rebuilt
        // from the survivors AFTER each real collision. Reused across frames → zero per-frame GC (T4).
        // R2: NATIVE so LabelStageJob can read it from Burst (the A-5 resolve moved into the job). Sized up front
        // to the maintainer's scene order-of-magnitude so the first frames don't pay rehash growth; Clear() never
        // shrinks, so this only shapes startup. Holds SURVIVORS (shown candidates), not all candidates.
        private const int PlacedSetInitialCapacity = 16384;
        private NativeHashSet<long> _placedLastFrame; // NOT readonly — allocated in the ctor

        // ── B-2: parallel symbol projection ─────────────────────────────────────────────────────────────────
        // Every visible symbol's screen geometry this frame — a point symbol's anchor, a line symbol's path
        // vertices — is projected UP FRONT in one pass by the Burst SymbolProjectionJob, and the staging pass reads
        // the precomputed screen positions instead of projecting inline. Generic over what a symbol RENDERS (text
        // today, icon later): a symbol is projected as its anchor/path world points regardless. The flat world
        // points go in _symbolPoints; _sjPointOffset[r] is record r's start in it (-1 = B-3-culled → skipped);
        // the fill writes the parallel _symbolScreen/_symbolDepth/_symbolValid. The job is dispatched with .Run()
        // (Burst-compiled, executed inline on the caller — no Schedule/Complete round-trip, no worker hand-off, no
        // count threshold), so the projection is always Burst SIMD with zero managed fallback and zero per-frame GC.
        // All buffers are reused + grown geometrically.
        private NativeList<double3> _symbolPoints;
        private NativeList<float2>  _symbolScreen;
        private NativeList<float>   _symbolDepth;
        private NativeList<byte>    _symbolValid;

        // ── Lever C step 3b: the Burst LabelStageJob's native buffers ──────────────────────────────────────────
        // A native MIRROR of the batch's STAGE data (refreshed only when batch.BuildId changes — never per frame),
        // the per-frame job inputs (gather offsets + resolved incumbency), its pre-sized outputs, and reused
        // scratch. The job calls the SAME LabelStagingMath the differential test pins; its native outputs
        // (_sjBoxes/_sjQuads/_sjCandidates/_sjEmit) feed the collision + emit passes DIRECTLY — no managed round-trip.
        // §7.10 finding 1a, generalized for R1 (memoized native gather): this ONE system is ticked by BOTH the
        // demo (_demoBatch) and production (per-frame subsystem batch/plan) paths, and their version counters
        // (BuildId / WinnerSetVersion) are independent — a demo Tick can leave _mirrorVersion at the same value a
        // DIFFERENT production source's first build also reaches, so the version alone can't tell "this exact
        // source already mirrored" from "some other source happens to share the number". Instance identity closes
        // that: only skip the refresh when it is the SAME source object (a SymbolLabelBatch OR a SymbolGatherPlan)
        // AND its version hasn't moved since — covering both directions (a demo Tick invalidates a held production
        // gather memo, and vice versa) via one comparison instead of two independent sentinels.
        private object _mirrorSource;                  // the batch or the plan the mirror was last filled from
        private long   _mirrorVersion = long.MinValue;  // that source's version: batch.BuildId | plan.WinnerSetVersion
        // Burst-gather Stage 1 (design doc §10.9, §2 of the plan): a per-frame REUSED table of non-owning
        // SymbolBlockView pointer-views over plan.Blocks[0, plan.BlockCount) — built by BuildBlockViews just
        // before SymbolGatherJob.Run(), read only during that synchronous call, never held across a frame
        // boundary. Stage 2 (async gather) must pin the owning snapshot independently of the front pin before
        // these pointers are allowed to outlive a frame — see the design doc §2.4; NOT this stage's problem
        // (GatherIntoMirror's .Run() is synchronous, so a view can never outlive the block it points into).
        private NativeList<SymbolBlockView> _gatherBlockViews;
        private NativeArray<int> _gatherCounts; // SymbolGatherJob.OutCounts — see its Count* consts for the layout
        private NativeList<byte> _mKinds;
        private NativeList<int>  _mDetail, _mWorldCount, _mPointQuadStart, _mPointQuadCount;
        private NativeList<int>  _mCurvedGlyphStart, _mCurvedGlyphCount, _mCurvedAnchorStart, _mCurvedAnchorCount, _mCurvedAnchorFadeStart;
        private NativeList<PointStageInput>  _mPoints;
        private NativeList<CurvedStageInput> _mCurveds;
        private NativeList<SymbolQuad>  _mQuads;
        private NativeList<CurvedGlyph> _mGlyphs;
        private NativeList<LineAnchor>  _mAnchors;
        private NativeList<long>        _mFadeIds;
        // Stage-2 (symbol-label native gather): the RECORD-LEVEL fields the gather cull (GatherSymbolPoints) reads —
        // previously read off the managed batch, now native so the production path never touches a managed SoA.
        // _mWorldStart/_mWorldPoints are the (remapped) per-record world-point slice; _mRepAnchor the B-3 cull point;
        // _mRecordDeparting/_mRecordCoverageFading the per-frame fade-out flags (0/1). Both fill paths (RefreshBatchMirror
        // for the demo batch, GatherIntoMirror for the production plan) populate them; the shared core reads only these.
        private NativeList<int>     _mWorldStart;
        private NativeList<double3> _mRepAnchor, _mWorldPoints;
        private NativeList<byte>    _mRecordDeparting, _mRecordCoverageFading;
        // D1: the tile-coverage cull's Drop decision as a per-record MASK (LabelTileCoverageFilter.ClassifyActive
        // via SymbolGatherPlan.Dropped) — a Dropped winner stays resident in the mirror (never compacted out) and
        // GatherSymbolPoints hard-skips it as its FIRST, unconditional check (no fade — it was never on screen).
        private NativeList<byte>    _mRecordDropped;
        // Mirror-side COUNTS — the shared core reads these instead of a batch's counts, so both fill paths converge
        // on one native representation. Set by RefreshBatchMirror (from the batch) and GatherIntoMirror (accumulated).
        private int _mCount, _mPointCount, _mCurvedCount, _mQuadCount, _mGlyphCount, _mAnchorCount, _mFadeCount, _mWorldPointCount;
        // D1 fix-pass (Blocker 1): _mCount includes Dropped records (they stay RESIDENT, masked — never compacted
        // out), so it is no longer "was there any placement work this frame" — an all-Dropped mirror still has
        // _mCount > 0. _mNonDroppedCount = _mCount minus Dropped records is the pre-D1-equivalent count: TickCore's
        // placement/fade-decay gate reads THIS, not _mCount, so an all-Dropped frame behaves exactly like the
        // pre-D1 empty mirror (block skipped, no DecayUnseenFadeRecords — live fades stay frozen, not decayed).
        // Departing records are never Dropped (ClassifyActive never classifies them — Blocker 2's scope fence), so
        // _mNonDroppedCount == the pre-D1 post-compaction _mCount by construction (departing labels always counted).
        private int _mNonDroppedCount;
        private int _mMaxBoxes, _mMaxQuads, _mMaxCandidates;
        private NativeList<int>  _sjPointOffset;                 // gather output (-1 = culled)
        private NativeList<byte> _sjAnchorWasPlaced; // per-frame A-5 anchor incumbency — filled by LabelStageJob, sized here
        private NativeList<LabelBox>       _sjBoxes;             // job outputs (pre-sized to batch worst case)
        private NativeList<PlacedQuad>     _sjQuads;
        private NativeList<LabelCandidate> _sjCandidates;
        private NativeList<CandidateEmit>  _sjEmit;
        private NativeArray<int>           _sjCounts;            // [candidateCount, boxCount, quadCount]
        private NativeList<float2> _sjPath;                      // arc-walk scratch (>= max path length)
        private NativeList<float>  _sjCum;

        // Where a surviving candidate's already-built quads live in _sjQuads + which material slot they draw
        // in (Core.Text.Placement.CandidateEmit) — keyed by the candidate's creation ordinal
        // (LabelCandidate.LabelIndex) so it is stable across the in-place candidate sort; emission just copies the
        // [QuadStart, QuadStart+QuadCount) range. Filled by LabelStagingMath alongside the candidates.

        /// <summary>Number of <see cref="Tick"/> calls so far — T5 structural guard (the vertex buffer is
        /// rebuilt every Tick, not once at tile consume). Test surface.</summary>
        internal int TickCount { get; private set; }

        /// <summary>Heavy mirror FILLS so far — bumped once per real <see cref="RefreshBatchMirror"/> / <see
        /// cref="GatherIntoMirror"/> rebuild, never on a memo hit. Drives the telemetry panel's rebuilds/second
        /// readout (how often the winner set actually changes ⇒ whether the memo can help at all), and is what
        /// keeps the R1 memo tests from passing trivially on an unmemoized implementation.</summary>
        internal int MirrorRebuildCount { get; private set; }

        /// <summary>The quad count submitted on the LAST <see cref="Tick"/> (0 if nothing was visible). Test surface.</summary>
        internal int LastQuadCount { get; private set; }

        /// <summary>Labels fed into the LAST <see cref="Tick"/> (before any projection cull) — telemetry.</summary>
        internal int LastInputLabelCount { get; private set; }

        /// <summary>Collision CANDIDATES on the last Tick (labels that survived projection and entered the
        /// greedy pass — a point label is 1, a curved/repeated line label is 1 per anchor). Telemetry.</summary>
        internal int LastCandidateCount { get; private set; }

        /// <summary>Collision SURVIVORS. R3: the collision is deferred a Tick, so this is the survivor count of
        /// the collision run over the PREVIOUS Tick's candidates (raw <c>_survivorCountOut[0]</c>, unfiltered),
        /// not this Tick's — one Tick behind <see cref="LastCandidateCount"/>, which is always this Tick's.
        /// Telemetry.</summary>
        internal int LastSurvivorCount { get; private set; }

        /// <summary>B-3: labels skipped by the pre-projection horizon/distance cull on the last Tick (never
        /// projected or collided). Telemetry — a proxy for how much the tilted-view horizon pile-up was trimmed.</summary>
        internal int LastDistanceCulledCount { get; private set; }

        /// <summary>Retain-as-departing: labels skipped on the last Tick because their tile is leaving cover and the
        /// label has already faded out (before that it stays STAGED, fading — no pop). Telemetry — a proxy for how
        /// many tile-unload fade-outs completed this frame.</summary>
        internal int LastDepartingCulledCount { get; private set; }

        /// <summary>S3: labels skipped on the last Tick because their anchor is hidden behind the globe's own
        /// bulk (<see cref="HorizonCull"/>) — never projected or collided. Telemetry — always 0 under a planar
        /// projection (Mercator's <c>TryGetHorizonOccluder</c> returns false ⇒ the trigger is inert).</summary>
        internal int LastHorizonCulledCount { get; private set; }

        /// <summary>A-4 fade records held — the SIZE OF THE MAP <see cref="DecayUnseenFadeRecords"/> walks each
        /// Tick, which is the per-frame cost being measured. An identity that has finished fading OUT is dropped
        /// rather than parked at 0 (see <c>EaseFade</c>), so this tracks the drawn label count rather than
        /// <see cref="LastCandidateCount"/> — a view whose candidates outnumber its placed labels ten to one does
        /// not pay the sweep ten times over.
        /// <para>Do NOT redefine this as a filtered or epsilon-thresholded count: the RAW size is the cost, and a
        /// filtered count would read as healthy while invisible identities piled up again — silently disarming
        /// <c>LabelFadeTests.Tick_StableCollisionLoser_LeavesNoFadeRecordBehind</c>, which pins exactly this.
        /// (The map legitimately holds sub-epsilon values while a label fades IN.)</para></summary>
        internal int LiveFadeRecordCount => _fadeOpacity.Count;

        /// <summary>Coverage-fade: labels skipped on the last Tick because their tile's on-screen coverage crossed
        /// below threshold (<see cref="Core.Text.Placement.LabelTileCoverageFilter"/>) and have now fully faded out
        /// (before that they stay STAGED, fading — no pop). Telemetry — mirrors <see cref="LastDepartingCulledCount"/>,
        /// just for the coverage-crossing trigger.</summary>
        internal int LastCoverageFadingCulledCount { get; private set; }

        /// <summary>Epic A / A1 (design §11 A1 D9 §E-flip): the WORLD mesh bound to <c>(tileKey, slot, kind)</c>'s
        /// slot, or null if no such slot has been emitted to yet. Test surface — returns whatever the slot last
        /// built, regardless of current visibility (see <see cref="IsWorldSlotVisible"/> for the presenter's
        /// show/hide state).</summary>
        internal bool TryGetWorldSlotMesh(long tileKey, int slot, LabelKind kind, out Mesh mesh)
            => _worldRenderer.TryGetSlotMesh(tileKey, slot, kind, out mesh);

        /// <summary>Whether the WORLD presenter for <c>(tileKey, slot, kind)</c> is currently drawing. Test
        /// surface — the "exactly one presenter draws" check.</summary>
        internal bool IsWorldSlotVisible(long tileKey, int slot, LabelKind kind)
            => _worldRenderer.IsSlotVisible(tileKey, slot, kind);

        /// <summary>The label-draw-backend-rework grouping tooth's Hierarchy entry point: the label tree's
        /// root transform ("Map Labels"). Test surface.</summary>
        internal Transform WorldLabelTreeRoot => _worldRenderer.TreeRoot;

        /// <summary>The WORLD text/icon child transform for <c>(tileKey, slot, kind)</c>, or null if never
        /// presented. Test surface — the grouping tooth asserts root → tile container → symbol-layer node →
        /// this child.</summary>
        internal Transform WorldSlotTransform(long tileKey, int slot, LabelKind kind)
            => _worldRenderer.GetSlotTransform(tileKey, slot, kind);

        // The map view this system renders labels for — injected at construction (S20: one
        // LabelPlacementSystem per MapView). Read AFTER MapCamera.SyncToCamera has committed the frame's
        // transform: MapView.LateUpdate does SyncToCamera → tile rebase → places labels, in that order.
        private readonly MapCamera _camera;

        /// <summary>
        /// The default world materials are <see cref="MaterialExtensions.CloneWithParent"/> clones of the
        /// <see cref="MapMaterialSet.SymbolTextWorld"/>/<see cref="MapMaterialSet.SymbolIconWorld"/> bases
        /// (S58 pattern: materials reference the shader by GUID, no <c>Shader.Find</c>) — used for the demo /
        /// single-material path and as the fallback for any slot without a supplied per-layer material.
        /// Cloned (not the base asset itself) because <see cref="Tick"/> mutates the text clone every frame
        /// (atlas texture + screen params). Per-symbol-layer materials (per-layer <c>text-halo-*</c>) are
        /// owned by each <see cref="Style.SymbolRenderLayer"/> (D11/E2) and supplied to <see cref="Tick"/> as
        /// the <c>symbolLayers</c> list.
        /// </summary>
        /// <param name="worldTextBase">Epic A / A1 (design §11 A1 D7): the <c>MapMaterialSet.SymbolTextWorld</c>
        /// base — the world-anchored point-text draw path, and the ONLY point-text draw path since commit 1
        /// retired the screen path. No <c>Shader.Find</c> fallback (production is GUID-only, S58) — a caller
        /// must supply it.</param>
        /// <param name="worldIconBase">Epic A / A1 D7: the <c>MapMaterialSet.SymbolIconWorld</c> base — the
        /// world-anchored icon draw path. Null (default) → world icons stay inert, text unaffected.</param>
        public LabelPlacementSystem(MapCamera camera, Material worldTextBase, Material worldIconBase = null)
        {
            _camera = camera ?? throw new ArgumentNullException(nameof(camera));

            _symbolPoints = new NativeList<double3>(Allocator.Persistent); // B-2 projection scratch
            _symbolScreen = new NativeList<float2>(Allocator.Persistent);
            _symbolDepth  = new NativeList<float>(Allocator.Persistent);
            _symbolValid  = new NativeList<byte>(Allocator.Persistent);

            _nSurvivors       = new NativeList<byte>(Allocator.Persistent); // B-4a collision survivor flags
            _gridCellHead     = new NativeList<int>(Allocator.Persistent);
            _gridNodeBox      = new NativeList<int>(Allocator.Persistent);
            _gridNodeNext     = new NativeList<int>(Allocator.Persistent);
            _survivorCountOut = new NativeArray<int>(1, Allocator.Persistent);

            // Burst-gather Stage 1: the reusable block-view table + SymbolGatherJob's output counts.
            _gatherBlockViews = new NativeList<SymbolBlockView>(Allocator.Persistent);
            _gatherCounts = new NativeArray<int>(SymbolGatherJob.CountLength, Allocator.Persistent);

            // Lever C step 3b: the Burst stage job's native buffers.
            _mKinds = new NativeList<byte>(Allocator.Persistent);
            _mDetail = new NativeList<int>(Allocator.Persistent);
            _mWorldCount = new NativeList<int>(Allocator.Persistent);
            _mPointQuadStart = new NativeList<int>(Allocator.Persistent);
            _mPointQuadCount = new NativeList<int>(Allocator.Persistent);
            _mCurvedGlyphStart = new NativeList<int>(Allocator.Persistent);
            _mCurvedGlyphCount = new NativeList<int>(Allocator.Persistent);
            _mCurvedAnchorStart = new NativeList<int>(Allocator.Persistent);
            _mCurvedAnchorCount = new NativeList<int>(Allocator.Persistent);
            _mCurvedAnchorFadeStart = new NativeList<int>(Allocator.Persistent);
            _mPoints = new NativeList<PointStageInput>(Allocator.Persistent);
            _mCurveds = new NativeList<CurvedStageInput>(Allocator.Persistent);
            _mQuads = new NativeList<SymbolQuad>(Allocator.Persistent);
            _mGlyphs = new NativeList<CurvedGlyph>(Allocator.Persistent);
            _mAnchors = new NativeList<LineAnchor>(Allocator.Persistent);
            _mFadeIds = new NativeList<long>(Allocator.Persistent);
            _mWorldStart = new NativeList<int>(Allocator.Persistent);            // Stage-2 record-level fields
            _mRepAnchor = new NativeList<double3>(Allocator.Persistent);
            _mWorldPoints = new NativeList<double3>(Allocator.Persistent);
            _mRecordDeparting = new NativeList<byte>(Allocator.Persistent);
            _mRecordCoverageFading = new NativeList<byte>(Allocator.Persistent);
            _mRecordDropped = new NativeList<byte>(Allocator.Persistent);
            _sjPointOffset = new NativeList<int>(Allocator.Persistent);
            _sjAnchorWasPlaced = new NativeList<byte>(Allocator.Persistent);
            _placedLastFrame = new NativeHashSet<long>(PlacedSetInitialCapacity, Allocator.Persistent);
            // _debugFadeIdSeen is NOT allocated here — see its field doc: AssertFadeIdsUnique allocates it
            // lazily on first use, so a release build (where that [Conditional] method never runs) never pays for it.
            _sjBoxes = new NativeList<LabelBox>(Allocator.Persistent);
            _sjQuads = new NativeList<PlacedQuad>(Allocator.Persistent);
            _sjCandidates = new NativeList<LabelCandidate>(Allocator.Persistent);
            _sjEmit = new NativeList<CandidateEmit>(Allocator.Persistent);
            _sjCounts = new NativeArray<int>(3, Allocator.Persistent);
            _sjPath = new NativeList<float2>(Allocator.Persistent);
            _sjCum = new NativeList<float>(Allocator.Persistent);

            if (worldTextBase == null)
            {
                Debug.LogWarning("[LabelPlacementSystem] no base symbol-text material (MapMaterialSet.SymbolTextWorld " +
                                 "unassigned) — labels will not render.");
            }
            else
            {
                _worldTextMaterial      = worldTextBase.CloneWithParent();
                _worldTextMaterial.name = "LabelPlacementSystem_WorldTextMaterial";
            }

            // Epic A / A1 D7: the world-anchored demo icon material — same null-tolerant pattern as
            // worldTextBase above (no Shader.Find fallback; production passes MapMaterialSet.SymbolIconWorld).
            if (worldIconBase != null)
            {
                _worldIconMaterial      = worldIconBase.CloneWithParent();
                _worldIconMaterial.name = "LabelPlacementSystem_WorldIconMaterial";
            }
        }

        /// <summary>
        /// One frame of the per-label placement loop: project every label's anchor (culling behind-camera
        /// / far-outside-viewport anchors), rebuild slot 0's billboard vertex/index buffer from every
        /// surviving label's quads, and hand it to the demo fallback presenter — which HIDES when there is
        /// nothing to draw (empty <paramref name="labels"/>, no atlas texture yet, or every anchor culled;
        /// <see cref="TickCount"/> still advances either way). Demo/test seam — no per-layer materials; see
        /// the <see cref="SymbolLabelBatch"/> overload for the production entry.
        /// </summary>
        /// <param name="frame">This frame's floating-origin scene frame (<see cref="SceneFrame.SceneOriginRender"/> — the T2 rebase).</param>
        /// <param name="labels">Every candidate label this frame (collision selects the survivors).</param>
        /// <param name="atlas">The uploaded R8 SDF glyph atlas texture backing every label's <see cref="SymbolQuad"/> UVs.</param>
        /// <param name="deltaTime">Seconds since the last <see cref="Tick"/> — drives the A-4 fade ease. Default
        /// <see cref="float.PositiveInfinity"/> SNAPS every fade to its target (no animation), so a single-Tick
        /// test renders fully-placed labels exactly as before A-4 (byte-parity); production passes
        /// <c>Time.deltaTime</c>.</param>
        /// <param name="spriteTexture">I5b: the sprite sheet backing every ICON label's <see cref="SymbolQuad"/>
        /// UVs (<c>SymbolLabelSubsystem.IconTexture</c>). Null (default) → icons never build/present — every
        /// icon draw path is guarded on this being non-null, so an omitted/absent sprite sheet is byte-identical
        /// to before I5b (text-only).</param>
        public void Tick(in SceneFrame frame, IReadOnlyList<LabelInstance> labels, GlyphAtlasTexture atlas,
            float deltaTime = float.PositiveInfinity, Texture2D spriteTexture = null)
        {
            // Demo / test seam: convert the managed carriers into the blittable batch (the SAME conversion the
            // production subsystem does once per collected-set change), then tick it. Rebuilt every call here;
            // production threads a pre-built, version-cached batch instead. No layer list — every label draws
            // through the single default material/presenter (slot 0).
            SymbolLabelBatchBuilder.Build(_demoBatch, labels, 1, _camera.Projection);
            TickFromBatch(frame, _demoBatch, atlas, deltaTime, null, labels?.Count ?? 0, spriteTexture);
        }

        /// <summary>Demo / test entry: tick a pre-built <see cref="SymbolLabelBatch"/> (the managed SoA the demo /
        /// oracle path builds via <see cref="SymbolLabelBatchBuilder.Build"/>). Same placement as the production
        /// <see cref="SymbolGatherPlan"/> overload — both fill the SAME native mirror, then run the shared core.</summary>
        /// <param name="symbolLayers">Per-symbol-layer render layers, index == <see cref="LabelInstance.MaterialIndex"/>
        /// (production, each owning its own material + persistent presenter — D11/E2). Null / empty → the demo
        /// path: every label draws through the single default material/presenter. Collision is GLOBAL
        /// regardless; only the draw is partitioned by layer.</param>
        /// <param name="spriteTexture">I5b — see the managed-list overload's doc.</param>
        public void Tick(in SceneFrame frame, SymbolLabelBatch batch, GlyphAtlasTexture atlas,
            float deltaTime = float.PositiveInfinity, IReadOnlyList<SymbolRenderLayer> symbolLayers = null,
            Texture2D spriteTexture = null)
            => TickFromBatch(frame, batch, atlas, deltaTime, symbolLayers, batch?.Count ?? 0, spriteTexture);

        /// <summary>Stage-2 (symbol-label native gather) PRODUCTION entry: tick a per-frame
        /// <see cref="SymbolGatherPlan"/> (winners + their pre-baked <see cref="SymbolTileLabelBlock"/> slices,
        /// produced by <see cref="Text.SymbolLabelSubsystem.CurrentBatch"/>). <see cref="GatherIntoMirror"/>
        /// compacts each winner's baked slice straight into the native mirror — no per-frame managed SoA build —
        /// then the SHARED core (project → stage → collide → emit) runs exactly as the demo batch path.</summary>
        /// <param name="symbolLayers">See the <see cref="SymbolLabelBatch"/> overload.</param>
        /// <param name="spriteTexture">I5b — see the managed-list overload's doc.</param>
        public void Tick(in SceneFrame frame, SymbolGatherPlan plan, GlyphAtlasTexture atlas,
            float deltaTime = float.PositiveInfinity, IReadOnlyList<SymbolRenderLayer> symbolLayers = null,
            Texture2D spriteTexture = null)
        {
            using (PmGather.Auto())
                GatherIntoMirror(plan); // sets _mNonDroppedCount — read below, not plan.WinnerCount (Should-Fix 3:
                                        // WinnerCount includes Dropped records; telemetry/gating must not)
            TickCore(frame, atlas, deltaTime, symbolLayers, _mNonDroppedCount, spriteTexture);
        }

        // Demo / test batch path: fill the native mirror from the managed batch (RefreshBatchMirror), then run the
        // shared core off the mirror — the SAME core the production plan path runs.
        private void TickFromBatch(in SceneFrame frame, SymbolLabelBatch batch, GlyphAtlasTexture atlas,
            float deltaTime, IReadOnlyList<SymbolRenderLayer> symbolLayers, int inputLabelCount,
            Texture2D spriteTexture)
        {
            RefreshBatchMirror(batch);
            TickCore(frame, atlas, deltaTime, symbolLayers, inputLabelCount, spriteTexture);
        }

        // The shared per-frame core — reads ONLY the native mirror (_m* + _mCount/…), never a managed batch, so
        // both the demo batch path (RefreshBatchMirror) and the production plan path (GatherIntoMirror) converge here.
        private void TickCore(in SceneFrame frame, GlyphAtlasTexture atlas,
            float deltaTime, IReadOnlyList<SymbolRenderLayer> symbolLayers, int inputLabelCount,
            Texture2D spriteTexture = null)
        {
            TickCount++;
            LastInputLabelCount = inputLabelCount;

            using (PmTick.Auto())
            {
                // R3: complete the PREVIOUS Tick's scheduled collision and re-key its survivors into
                // _placedLastFrame — read by BOTH LabelStageJob (A-5 incumbency) and the emit loop below.
                using (PmCollideHarvest.Auto())
                    HarvestCollision();

                double2 viewportLogicalPx = _camera.ViewportLogicalPx;

                LastCandidateCount = 0;
                LastDistanceCulledCount = 0;
                LastDepartingCulledCount = 0;
                LastHorizonCulledCount = 0;
                LastCoverageFadingCulledCount = 0;

                _worldRenderer.BeginFrame(); // Epic A / A1: clear every live world slot's accumulators

                int totalQuads = 0;

                // D1 fix-pass (Blocker 1): gate on the EFFECTIVE non-Dropped count, not raw _mCount — an
                // all-Dropped mirror (every resident record masked) must skip this whole block exactly like the
                // pre-D1 empty-after-compaction mirror did, so live _fadeOpacity entries stay frozen rather than
                // decaying via DecayUnseenFadeRecords below (a Dropped tile was never on screen; nothing to decay).
                if (_mNonDroppedCount > 0 && atlas?.Texture != null && _worldTextMaterial != null)
                {
                    float4x4 viewProj = ViewProj(_camera.Camera);
                    double3 sceneOriginRender = frame.SceneOriginRender;
                    float3x3 rebase = frame.Rebase;

                    // S3: the globe far-side horizon cull's params, built ONCE per Tick. `occ == false` on a
                    // planar projection (WebMercatorProjection.TryGetHorizonOccluder) ⇒ globeRadiusSq = -1 ⇒
                    // HorizonCull.IsHiddenBeyondHorizon is an unconditional no-op for every record. `cameraRelative`
                    // is Stage U's SceneFrame.CameraRelativePosition (ComputeRelativePose's `pos`, folded in by
                    // MapView.BuildSceneFrame) — NOT a fresh pose computation and NOT transform.position.
                    bool occ = _camera.Projection.TryGetHorizonOccluder(out double3 occCentre, out double occRadius);
                    double3 cameraRelative = frame.CameraRelativePosition;
                    double  globeRadiusSq  = occ ? occRadius * occRadius : -1.0;

                    // B-3: the pre-projection horizon/distance cull radius (render metres around the look-at) —
                    // one logical pixel of ground = MetersPerPixel(zoom), so LabelViewportSpans screen-widths
                    // of ground. Labels beyond it are skipped BEFORE projection/collision (the horizon pile-up).
                    double cullRadius = LabelViewDistance.CullRadiusMeters(
                        viewportLogicalPx, CameraPoseMath.MetersPerPixel(_camera.CurrentProperties.Zoom), LabelViewportSpans);

                    // Map bearing (heading) — drives text-translate-anchor:map and text-rotation-alignment:map
                    // (#4). Read once per frame; zero for a north-up map, where map- and viewport-alignment
                    // coincide. The bearing sign lives in LabelBearing (the single visual-verify constant).
                    float bearingRadians = (float)_camera.CurrentProperties.Heading.Radians;

                    // (1) Project + STAGE every label into the unified native pools (the Burst LabelStageJob): one
                    //     LabelCandidate per label (point = 1 AABB box; curved = N rotated-glyph boxes) in _sjBoxes,
                    //     its drawn quads in _sjQuads. Point and curved share the collision pass from here, so a
                    //     road name and a city name compete for space (#5 B3).
                    int candidateCount = 0, boxCount = 0;
                    using (PmProject.Auto())
                    {
                        // B-2: gather every un-culled symbol's world points (anchor / line path) — cheap, no matrix
                        // mul, and it is where the B-3 distance cull now runs — then project them all in one pass
                        // (parallel Burst job above the threshold, else serial). Staging reads the precomputed
                        // screen positions below rather than projecting each anchor/path inline.
                        using (PmProjectFill.Auto())
                        {
                            // Tile-coverage pre-cull now runs upstream, in SymbolLabelSubsystem.CurrentBatch (via
                            // Core's LabelTileCoverageFilter). D1: a Dropped tile's labels stay RESIDENT in the mirror
                            // (flagged _mRecordDropped) and are hard-skipped below — a mask, not a compaction, but the
                            // same effect as removal. A Fading tile's records also ride (RecordCoverageFading), so gather
                            // eases them out instead of popping (the 4th fade-out trigger below).
                            GatherSymbolPoints(sceneOriginRender, cullRadius, rebase, cameraRelative, occCentre, globeRadiusSq);
                            ProjectSymbols(sceneOriginRender, viewProj, viewportLogicalPx, rebase);
                        }

                        using (PmStage.Auto())
                        {
                            // Stage the whole mirror in ONE Burst job (LabelStageJob) — same LabelStagingMath as the
                            // managed reference, SIMD-compiled. The mirror is already filled (RefreshBatchMirror /
                            // GatherIntoMirror, before this core); pre-size outputs (the job itself resolves this
                            // frame's A-5 incumbency, R2); run; read counts. Its native outputs feed the collision +
                            // emit passes directly.
                            PreSizeStageOutputs();
                            RunStageJob(bearingRadians, viewportLogicalPx);
                            candidateCount = _sjCounts[0]; boxCount = _sjCounts[1];
                        }
                    }

                    // Mark out-of-zoom candidates LabelCandidate.Suppressed BEFORE collision, so a label whose layer
                    // is outside the LIVE camera zoom's minzoom/maxzoom neither wins nor blocks the true winner (it
                    // still eases to 0 in the emit loop). Display-time gate → overzoom works: a z14 tile reveals
                    // poi_r1/r7/r20 as the camera passes 15/16/17, and hides them on zoom-out.
                    ApplySuppression(candidateCount, symbolLayers, _camera.CurrentProperties.Zoom);

                    LastCandidateCount = candidateCount;

                    // (2) A-4 FADE, reading LAST TICK's collision verdict (R3 — re-keyed into _placedLastFrame by
                    //     HarvestCollision at the top of this Tick): ease each candidate's persistent opacity toward
                    //     1 (shown) or 0 (not last Tick's survivor / suppressed / force-faded), then emit its staged
                    //     quads scaled by that opacity. A suppressed label is still staged this frame, so it fades
                    //     OUT in place from its live placement (no cross-frame quad cache); a new label fades IN
                    //     from 0. Records not seen this frame decay and are dropped (bounded). Emit no longer writes
                    //     _placedLastFrame — HarvestCollision is its sole writer now (R3 §2.4) — so emit order here
                    //     is STAGING order, not placement order (the collision below sorts _sjCandidates in place,
                    //     but that sort happens AFTER this loop reads it — see the schedule note below).
                    using (PmEmit.Auto())
                    {
                        using (PmEmitLoop.Auto())
                        {
                            _seenFade.Clear();
                            for (int s = 0; s < candidateCount; s++)
                            {
                                LabelCandidate cand = _sjCandidates[s]; // staging order — the collision job hasn't sorted yet
                                long fadeId = cand.FadeId;

                                // `WasPlacedLastFrame` IS `_placedLastFrame.Contains(fadeId)`, already resolved in
                                // Burst by LabelStageJob and carried on the candidate we just loaded — so read it
                                // instead of re-probing the set here (one native hash lookup, plus its Editor safety
                                // check, per candidate). Exact on both arms: the job fills AnchorWasPlaced over its
                                // whole range element-wise against AnchorFadeIds, slices both with the SAME
                                // (fadeStart, anchorCount + 1), and StageCurved indexes the pair at one `a` (and at
                                // `anchorCount` for the centred fallback). Safe because HarvestCollision is the set's
                                // sole writer and ran before the stage job — the contents cannot differ between them.
                                bool placed = cand.WasPlacedLastFrame;

                                // A candidate that is neither placed nor holds a fade record can produce nothing:
                                // EaseFade would ease 0 toward 0, store nothing (the drop branch, with no key to
                                // remove), add nothing to _seenFade, and the caller would `continue` on the returned
                                // 0. Skipping outright is exactly that, minus the probes — and it is the common case,
                                // since candidates outnumber placed labels roughly ten to one in a dense view.
                                if (!placed && !_fadeOpacity.ContainsKey(fadeId)) continue;

                                // (EaseFade below records this id in _seenFade iff it stores one — see its field doc.)
                                // Show iff this candidate's FadeId survived LAST Tick's collision (harvested above) and
                                // it isn't force-fading-out (gather-cull) or suppressed (out-of-zoom, evaluated LIVE
                                // this frame so the display-time gate stays same-frame — R3 §2.8). One-frame verdict
                                // latency: a brand-new candidate or a flipped verdict shows per last Tick's outcome
                                // for one Tick. `_forceFadeOut` is probed LAST — same boolean, but it is the rarest
                                // term, so ordering it after the two cheap ones skips it for most candidates.
                                bool show = placed && !cand.Suppressed && !_forceFadeOut.Contains(fadeId);
                                float opacity = EaseFade(fadeId, show ? 1f : 0f, deltaTime);
                                if (opacity <= FadeEpsilon) continue;

                                // Epic A / A1 (design §11 A1 D2/D6) + Stage AC: every candidate now emits into the
                                // world renderer — StagePoint and, since Stage AC, StageCurved both set
                                // CandidateEmit.IsWorld unconditionally (the old screen _slotQuads/_slotIconQuads
                                // bucket routing was retired with the dead render path it fed).
                                CandidateEmit emit = _sjEmit[cand.LabelIndex];
                                totalQuads += _worldRenderer.Emit(in emit, _sjQuads.AsArray(), opacity);
                            }
                        }

                        using (PmEmitDecay.Auto())
                            DecayUnseenFadeRecords(deltaTime);
                    }

                    // (3) R3: schedule the collision LAST — after emit, not before. The job sorts _sjCandidates in
                    //     place, so nothing downstream may read that array this frame once it's scheduled; emit
                    //     above is the only reader and it already ran. Complete()s at the top of the NEXT Tick
                    //     (HarvestCollision), so the greedy runs in the inter-frame gap instead of under a blocking
                    //     wait here.
                    using (PmCollide.Auto())
                        ScheduleCollision(candidateCount, boxCount);
                }

                LastQuadCount = totalQuads;

                // Epic A / A1: build + place every non-empty WORLD slot (point/icon), hide the rest, reclaim
                // idle ones — runs every Tick regardless of whether this Tick built anything (a Tick that placed
                // nothing this frame must still hide slots a PRIOR Tick left visible).
                _worldRenderer.EndFrame(in frame, symbolLayers, _worldTextMaterial, _worldIconMaterial,
                    atlas?.Texture, spriteTexture, viewportLogicalPx);
            }
        }

        // ── B-2: gather + project every symbol's screen geometry ──────────────────────────────────────────────
        // Flatten every un-culled record's world points (from the native mirror) into _symbolPoints — a point's
        // anchor, a line's path vertices — recording each record's start in the native _sjPointOffset (-1 = culled →
        // skipped by the stage job). Cheap: array reads + the coverage/distance/horizon fade triggers, no matrix
        // mul; the projection (matrix mul) happens once, in ProjectSymbols. Reads ONLY the mirror (_m*), which both
        // fill paths populate: the demo batch (RefreshBatchMirror) and the production plan (GatherIntoMirror).
        //
        // D1: the tile-coverage cull's DROP decision (LabelTileCoverageFilter.ClassifyActive, via
        // SymbolGatherPlan.Dropped) is now a per-record MASK (_mRecordDropped) — a Dropped tile's labels DO reach
        // the mirror (resident, never compacted out) but are hard-skipped here, FIRST and unconditionally: a
        // Dropped tile was never on screen, so — unlike departing/coverage-fading — there is nothing to ease out,
        // never mind its fade state. A FADING tile's records still gather (_mRecordCoverageFading), handled below.
        private void GatherSymbolPoints(double3 sceneOriginRender, double cullRadius,
            in float3x3 rebase, double3 cameraRelative, double3 globeCentreRelative, double globeRadiusSq)
        {
            _sjPointOffset.ResizeUninitialized(_mCount);
            _symbolPoints.Clear();
            _forceFadeOut.Clear();
            for (int r = 0; r < _mCount; r++)
            {
                if (_mRecordDropped[r] != 0) { _sjPointOffset[r] = -1; continue; } // D1 hard-skip — no fade, never on screen

                // Four fade-out triggers, cheapest first: the record's tile is LEAVING cover (retain-as-departing —
                // flagged from CollectInto's active/departing split), its tile's on-screen COVERAGE crossed below
                // threshold (_mRecordCoverageFading — LabelTileCoverageFilter's Fade classification), the B-3
                // distance cull (this label past the horizon radius), and S3's globe horizon cull (anchor hidden
                // behind the earth's own bulk — short-circuits to false on a planar projection via globeRadiusSq <
                // 0). Each trigger short-circuits the cheaper ones before it; its fade-out is unconditional.
                bool departing  = _mRecordDeparting[r] != 0;
                bool coverageFading = !departing && _mRecordCoverageFading[r] != 0;
                bool distCulled = !departing && !coverageFading &&
                    LabelViewDistance.IsCulled(_mRepAnchor[r], sceneOriginRender, cullRadius);
                bool horizonCulled = !departing && !coverageFading && !distCulled &&
                    HorizonCull.IsHiddenBeyondHorizon(_mRepAnchor[r], sceneOriginRender, rebase,
                                                      cameraRelative, globeCentreRelative, globeRadiusSq);

                if (departing || coverageFading || distCulled || horizonCulled)
                {
                    // Don't pop a label that was on screen last frame: if its fade is still alive, KEEP staging it
                    // (so it eases out in place at its live position) and force its fade-out in emit. Only once it
                    // has fully faded do we actually skip it — that is where the perf win lands (and, for a departing
                    // tile or a coverage-fading tile, where the store/filter then purges it: grace > fade, so it is
                    // already invisible).
                    if (MarkFadeOutIfAlive(r))
                    {
                        // fall through: gather it like a normal record; the emit loop drives its opacity to 0
                    }
                    else
                    {
                        if (departing) LastDepartingCulledCount++;
                        else if (coverageFading) LastCoverageFadingCulledCount++;
                        else if (distCulled) LastDistanceCulledCount++;
                        else LastHorizonCulledCount++;
                        _sjPointOffset[r] = -1;
                        continue;
                    }
                }

                _sjPointOffset[r] = _symbolPoints.Length;
                int ws = _mWorldStart[r], wc = _mWorldCount[r];
                for (int v = 0; v < wc; v++) _symbolPoints.Add(_mWorldPoints[ws + v]);
            }
        }

        // If record r's fade is still visible (any of its FadeIds has opacity > epsilon), record those FadeIds in
        // _forceFadeOut so the emit loop eases them toward 0, and return true (the gather cull then keeps staging
        // it for the fade-out). Returns false when the record has no live fade — never shown, or already faded —
        // so the caller hard-skips it (no pop; nothing was on screen to pop). Reads the native mirror (_m*).
        private bool MarkFadeOutIfAlive(int r)
        {
            int detail = _mDetail[r];
            if (_mKinds[r] == (byte)SymbolLabelBatch.Kind.Point)
                return TryForceFadeOut(_mPoints[detail].FadeId);

            // Curved: one candidate per anchor plus the centred-fallback slot. Force-fade EVERY anchor — not just
            // the ones already visible — so a previously-invisible anchor can't fade IN on a tile we are culling;
            // keep the record staged if ANY anchor is still visible.
            bool alive = false;
            int fadeStart = _mCurvedAnchorFadeStart[detail];
            int fadeCount = _mCurvedAnchorCount[detail] + 1; // + trailing centred-fallback fade id
            for (int i = 0; i < fadeCount; i++)
            {
                long fadeId = _mFadeIds[fadeStart + i];
                _forceFadeOut.Add(fadeId);
                if (_fadeOpacity.TryGetValue(fadeId, out float opacity) && opacity > FadeEpsilon) alive = true;
            }
            return alive;
        }

        private bool TryForceFadeOut(long fadeId)
        {
            if (!(_fadeOpacity.TryGetValue(fadeId, out float opacity) && opacity > FadeEpsilon)) return false;
            _forceFadeOut.Add(fadeId);
            return true;
        }

        // Project the gathered _symbolPoints to screen/depth/valid via the Burst SymbolProjectionJob, run inline
        // with .Run() — no managed serial fallback (see the .Run() comment below for why).
        private void ProjectSymbols(double3 sceneOriginRender, in float4x4 viewProj, double2 viewportLogicalPx, in float3x3 rebase)
        {
            int total = _symbolPoints.Length;
            _symbolScreen.Resize(total, NativeArrayOptions.UninitializedMemory);
            _symbolDepth.Resize(total,  NativeArrayOptions.UninitializedMemory);
            _symbolValid.Resize(total,  NativeArrayOptions.UninitializedMemory);
            if (total == 0) return;

            // .Run() Burst-compiles and executes the parallel-for inline on the calling thread: no Schedule/Complete
            // round-trip, no worker hand-off, no count threshold — and no managed serial fallback that would run
            // outside Burst. The caller blocks here regardless (staging reads the output immediately below), so an
            // inline .Run() is strictly cheaper than Schedule().Complete() for this synchronous consume.
            new SymbolProjectionJob
            {
                Points            = _symbolPoints.AsArray(),
                SceneOriginRender = sceneOriginRender,
                Rebase            = rebase,
                ViewProj          = viewProj,
                ViewportLogicalPx = viewportLogicalPx,
                OutScreen         = _symbolScreen.AsArray(),
                OutDepth          = _symbolDepth.AsArray(),
                OutValid          = _symbolValid.AsArray(),
            }.Run(total);
        }

        // R3: complete the collision scheduled at the END of the previous Tick and RE-KEY its survivors by FadeId
        // into _placedLastFrame. The job sorted _sjCandidates in place, so last frame's per-position survivor flags are
        // meaningless against this frame's candidates — FadeId is the only cross-frame identity (it must be unique per
        // live candidate: LabelCandidate.FadeId's contract, asserted in ScheduleCollision).
        //
        // The filter reproduces the pre-R3 emit expression exactly, on the frame the collision belonged to:
        // _forceFadeOut still holds THAT frame's content here, because GatherSymbolPoints clears it later this Tick.
        private void HarvestCollision()
        {
            if (!_collisionPending)
            {
                // No collision was in flight ⇒ last Tick produced no verdict (no candidates / no atlas / no labels)
                // ⇒ no incumbents and nothing to show — the same state the pre-R3 !didBuild clear produced.
                _placedLastFrame.Clear();
                LastSurvivorCount = 0;
                return;
            }

            _collisionHandle.Complete();
            _collisionPending = false;

            _placedLastFrame.Clear();
            for (int s = 0; s < _pendingCandidateCount; s++)
            {
                if (_nSurvivors[s] == 0) continue;
                long fadeId = _sjCandidates[s].FadeId;
                if (_forceFadeOut.Contains(fadeId)) continue;
                _placedLastFrame.Add(fadeId);
            }
            LastSurvivorCount = _survivorCountOut[0];
        }

        // B-4a: run the greedy collision as the Burst LabelCollisionJob directly over the stage job's native output
        // pools (no managed round-trip — the stage job already wrote them native). PRE-SIZES the uniform grid on the
        // main thread (a Burst job cannot grow a NativeArray, and this sizing pass stays on frame N — R4, not this
        // stage), then SCHEDULES the job and returns without completing it. R3 (design §10.3): the Complete moves to
        // the START of the next Tick (HarvestCollision above), so the main thread never blocks on the single-threaded
        // greedy — it runs in the inter-frame gap instead. _sjCandidates is left sorted in placement order by the
        // job and _nSurvivors holds the per-sorted-position survivor flags; both are read by the NEXT Tick's harvest,
        // not this one — see _pendingCandidateCount's field comment for why the count must be pinned here.
        private void ScheduleCollision(int candidateCount, int boxCount)
        {
            if (candidateCount <= 0) return; // nothing pending ⇒ next Tick's harvest reads "no verdict"

            _nSurvivors.Resize(candidateCount, NativeArrayOptions.UninitializedMemory);
            NativeArray<LabelCandidate> nc = _sjCandidates.AsArray(); // stage job's candidates — sorted IN PLACE here
            NativeArray<LabelBox>       nb = _sjBoxes.AsArray();       // stage job's boxes (read-only; grid over [0,boxCount))

            // DEBUG (compiled out in release): the staged candidate stream must tile [0,boxCount) contiguously —
            // the invariant NodeUpperBoundByCandidates and the job's per-reference insert rely on. A violation is
            // the never-reproduced malformed-stream source behind the dense-scene node-pool overflow; catch it
            // loudly here with the offending candidate, rather than as a silent grid corruption downstream.
            AssertCandidateRangesTile(nc, candidateCount, boxCount);
            // R3: FadeId is now the DISPLAY key (HarvestCollision re-keys by it), so two live candidates sharing one
            // would both show when one wins — see LabelCandidate.FadeId's uniqueness contract and this assert's doc.
            AssertFadeIdsUnique(nc, candidateCount);

            // Pre-size the uniform grid: CellHead = W*H (filled -1); the node arrays bound the job's inserts.
            // The bound is counted PER CANDIDATE box-reference (NodeUpperBoundByCandidates), NOT per unique box, so
            // it mirrors the job's insert loop exactly and can't under-count regardless of the stream's shape (it
            // equals the per-unique bound when the ranges tile disjointly — the normal case the assert above checks
            // — and strictly exceeds it if a box were ever shared). Adopted after a dense-scene overflow whose exact
            // trigger was never reproduced; the assert catches a malformed stream if one ever occurs.
            LabelCollisionGridSizing.Dims dims = LabelCollisionGridSizing.ComputeDims(nb, boxCount);
            int cells   = dims.W * dims.H;
            int nodeCap = math.max(1, LabelCollisionGridSizing.NodeUpperBoundByCandidates(nc, candidateCount, nb, boxCount, in dims));
            _gridCellHead.Resize(cells,   NativeArrayOptions.UninitializedMemory);
            _gridNodeBox.Resize(nodeCap,  NativeArrayOptions.UninitializedMemory);
            _gridNodeNext.Resize(nodeCap, NativeArrayOptions.UninitializedMemory);
            NativeArray<int> cellHead = _gridCellHead.AsArray();
            for (int c = 0; c < cells; c++) cellHead[c] = -1;

            _collisionHandle = new LabelCollisionJob
            {
                Candidates     = nc, CandidateCount = candidateCount,
                Boxes          = nb, BoxCount = boxCount,
                Survivors      = _nSurvivors.AsArray(), OutSurvivorCount = _survivorCountOut,
                CellHead       = cellHead, NodeBox = _gridNodeBox.AsArray(), NodeNext = _gridNodeNext.AsArray(),
                GridMinX       = dims.MinX, GridMinY = dims.MinY, GridInvCell = dims.InvCell,
                GridW          = dims.W, GridH = dims.H,
            }.Schedule();
            // Without this the job may not be handed to a worker until the next sync point, which would defeat the
            // entire point of deferring — the wait would just move to wherever the next implicit sync lands.
            JobHandle.ScheduleBatchedJobs();

            _collisionPending = true;
            _pendingCandidateCount = candidateCount;
        }

        // Debug-only (UNITY_ASSERTIONS ⇒ Editor + development builds; compiled out of release). R3 promotes FadeId
        // from a fade/sort key to the DISPLAY key — the emit loop shows a candidate iff its FadeId is in last frame's
        // survivor set — so LabelCandidate.FadeId's "unique per live candidate" contract is now load-bearing for what
        // the user sees, not just for the opacity record. Two candidates sharing an id would both show when one wins.
        // Scope limit: this checks duplicates among the candidates of ONE frame; it cannot see sequential reuse of an
        // id across frames (that is a designed behaviour — a stable FadeId across frames is what lets a label keep
        // its opacity record instead of popping — so no cross-frame check is offered; see LabelDeferredCollisionTests
        // / the R3 design doc for the one known sequential-reuse fixture hazard this does NOT catch).
        //
        // Reuses the persistent _debugFadeIdSeen field rather than constructing a fresh NativeHashSet per call: a
        // per-call Allocator.Temp container's AtomicSafetyHandle/DisposeSentinel bookkeeping under
        // ENABLE_UNITY_COLLECTIONS_CHECKS (on in the Editor, where UNITY_ASSERTIONS is also on) allocates managed
        // memory occasionally — LabelPlacementAllocTests caught exactly this as a periodic steady-state GC alloc
        // once a NativeHashSet<long>(Allocator.Temp) was constructed here every Tick. Instance method (not static)
        // so it can reach the field; not itself a production cost (the whole body is compiled out of release).
        [System.Diagnostics.Conditional("UNITY_ASSERTIONS")]
        private void AssertFadeIdsUnique(NativeArray<LabelCandidate> candidates, int candidateCount)
        {
            // Lazy allocation (see the field's doc): a release build never reaches this line ([Conditional]
            // strips the call site), so _debugFadeIdSeen stays a default (uncreated) struct there — zero
            // allocation. 64 (not PlacedSetInitialCapacity's 16384) — Clear() keeps whatever capacity the set
            // grows to, so it warms itself after one real-scene frame; growth is a native realloc, which cannot
            // reintroduce the managed GC alloc the persistent-reuse pattern was adopted to fix.
            if (!_debugFadeIdSeen.IsCreated) _debugFadeIdSeen = new NativeHashSet<long>(64, Allocator.Persistent);
            _debugFadeIdSeen.Clear();
            for (int i = 0; i < candidateCount; i++)
            {
                LabelCandidate c = candidates[i];
                if (!_debugFadeIdSeen.Add(c.FadeId))
                {
                    UnityEngine.Debug.LogAssertion(
                        $"[LabelPlacementSystem] candidate[{i}] (LabelIndex={c.LabelIndex}, FeatureIndex={c.FeatureIndex}, " +
                        $"TileKey={c.TileKey}) shares FadeId={c.FadeId} with an earlier candidate this frame — " +
                        "LabelCandidate.FadeId must be unique per live candidate (R3 promotes it to the display key); " +
                        "the loser will incorrectly show alongside the winner.");
                    return;
                }
            }
        }

        // Debug-only (UNITY_ASSERTIONS ⇒ Editor + Development builds; compiled out of release, so it costs a live
        // build NOTHING — no field, no allocation, reads only what ScheduleCollision already holds). Asserts the staged
        // candidate stream is well-formed: box ranges tiling [0,boxCount) contiguously in staging order
        // (LabelCandidate.TryFindRangeTilingViolation's contract). A fire is the never-reproduced overlap/over-range
        // source behind the dense-scene collision overflow — release still never crashes (the sizing + Insert guard
        // hold), but here it surfaces LOUDLY with the exact offending candidate to capture instead of a silent
        // slightly-permissive placement.
        [System.Diagnostics.Conditional("UNITY_ASSERTIONS")]
        private static void AssertCandidateRangesTile(NativeArray<LabelCandidate> candidates, int candidateCount, int boxCount)
        {
            if (!LabelCandidate.TryFindRangeTilingViolation(candidates.AsSpan().Slice(0, candidateCount), boxCount,
                    out int i, out int expectedStart))
                return;

            if (i < candidateCount)
            {
                LabelCandidate c = candidates[i];
                UnityEngine.Debug.LogAssertion(
                    $"[LabelCollision] staged candidate box-ranges must tile [0,{boxCount}) contiguously, but " +
                    $"candidate[{i}] has BoxStart={c.BoxStart} BoxCount={c.BoxCount} (expected BoxStart={expectedStart}, " +
                    $"LabelIndex={c.LabelIndex}) — the never-reproduced overlap/over-range source; capture this frame.");
            }
            else
            {
                UnityEngine.Debug.LogAssertion(
                    $"[LabelCollision] staged candidate box-ranges cover only [0,{expectedStart}) of {boxCount} boxes " +
                    "— a gap/undercount in the staged stream; capture this frame.");
            }
        }

        // The label's text-color, sRGB→linear + folded text-opacity — the vertex color the shader emits
        // directly (mirrors StyledFill/LineTileBuilder's Color.linear; without it a dark #333 uploads as
        // linear ~0.2 and displays washed-out). Alpha is not gamma-encoded — carried straight.
        internal static float4 LinearColor(LabelInstance label)
        {
            float4 srgb   = label.Paint.TextColor;
            Color  linear = new Color(srgb.x, srgb.y, srgb.z, 1f).linear;
            return new float4(linear.r, linear.g, linear.b, srgb.w * label.Paint.Opacity);
        }

        // Demo path / out-of-range material index → default slot 0.
        internal static int ClampSlot(int slot, int slotCount) => (slot < 0 || slot >= slotCount) ? 0 : slot;

        // Refresh the native mirror from a managed batch (the DEMO / oracle path) — ONLY when the batch was rebuilt
        // (identity + BuildId change), never per frame. Now ALSO fills the Stage-2 record-level fields
        // (_mWorldStart/_mRepAnchor/_mWorldPoints/_mRecordDeparting/_mRecordCoverageFading) and the mirror COUNTS the
        // shared core reads — so the demo path, which now reads these from the mirror, matches the batch exactly.
        private void RefreshBatchMirror(SymbolLabelBatch batch)
        {
            // Null batch = an EMPTY frame (the demo overload's `batch?.Count ?? 0` documents null as valid input):
            // zero the mirror + reset the sentinel so TickCore's `_mNonDroppedCount > 0` gate no-ops (clearing any
            // labels a prior Tick left shown) instead of NRE-ing on batch.BuildId below.
            if (batch == null) { _mCount = 0; _mNonDroppedCount = 0; _mirrorSource = null; _mirrorVersion = long.MinValue; return; }
            // §7.10 1a: identity AND version — see _mirrorSource's header comment.
            if (ReferenceEquals(batch, _mirrorSource) && batch.BuildId == _mirrorVersion) return;
            MirrorRebuildCount++;
            _mirrorSource = batch;
            _mirrorVersion = batch.BuildId;
            int n = batch.Count;
            MirrorKinds(_mKinds, batch.Kinds, n);
            Mirror(_mDetail, batch.Detail, n); Mirror(_mWorldCount, batch.WorldCount, n);
            Mirror(_mPoints, batch.Points, batch.PointCount);
            Mirror(_mPointQuadStart, batch.PointQuadStart, batch.PointCount);
            Mirror(_mPointQuadCount, batch.PointQuadCount, batch.PointCount);
            Mirror(_mCurveds, batch.Curveds, batch.CurvedCount);
            Mirror(_mCurvedGlyphStart, batch.CurvedGlyphStart, batch.CurvedCount);
            Mirror(_mCurvedGlyphCount, batch.CurvedGlyphCount, batch.CurvedCount);
            Mirror(_mCurvedAnchorStart, batch.CurvedAnchorStart, batch.CurvedCount);
            Mirror(_mCurvedAnchorCount, batch.CurvedAnchorCount, batch.CurvedCount);
            Mirror(_mCurvedAnchorFadeStart, batch.CurvedAnchorFadeStart, batch.CurvedCount);
            Mirror(_mQuads, batch.Quads, batch.QuadCount);
            Mirror(_mGlyphs, batch.Glyphs, batch.GlyphCount);
            Mirror(_mAnchors, batch.Anchors, batch.AnchorCount);
            Mirror(_mFadeIds, batch.AnchorFadeIds, batch.AnchorFadeCount);
            // (E) the Stage-2 record-level fields the shared core's gather cull reads off the mirror.
            Mirror(_mWorldStart, batch.WorldStart, n);
            Mirror(_mRepAnchor, batch.RepAnchor, n);
            Mirror(_mWorldPoints, batch.WorldPoints, batch.WorldPointCount);
            MirrorBool(_mRecordDeparting, batch.RecordDeparting, n);
            MirrorBool(_mRecordCoverageFading, batch.RecordCoverageFading, n);
            // The demo/oracle batch never runs the tile-coverage cull (D1's ClassifyActive) — nothing is ever
            // Dropped on this path, so the mask is unconditionally clear.
            ClearBytes(_mRecordDropped, n);

            _mCount = n; _mNonDroppedCount = n; // demo/oracle path: never any Dropped records (see the comment above)
            _mPointCount = batch.PointCount; _mCurvedCount = batch.CurvedCount;
            _mQuadCount = batch.QuadCount; _mGlyphCount = batch.GlyphCount; _mAnchorCount = batch.AnchorCount;
            _mFadeCount = batch.AnchorFadeCount; _mWorldPointCount = batch.WorldPointCount;
            _mMaxBoxes = batch.MaxBoxes; _mMaxQuads = batch.MaxQuads; _mMaxCandidates = batch.MaxCandidates;
        }

        // Stage-2 (symbol-label native gather) — fill the native mirror directly from the per-frame winner
        // SymbolGatherPlan (the PRODUCTION path), compacting each winner's pre-baked block slice into the mirror
        // pools at running offsets and REMAPPING every Detail/*Start by the running pool offset. This replaces the
        // managed SoA Build + RefreshBatchMirror double-copy: the camera-independent per-label SoA is already baked
        // per tile (Stage 1), so per frame we only memcpy the winning slices into one contiguous buffer.
        //
        // Record order == plan order == the collected list Build would walk (D1: EVERY collected winner, Drops
        // included — the tile-coverage cull is a per-record MASK applied downstream, not a compaction here), and
        // each block slice == what Build would compute for that label (Stage-1 drift-guard), so the mirror is byte-identical to
        // Build over the same list (the parity teeth). departing/coverageFading are the per-frame overrides, applied
        // here from the plan (fenced OUT of the immutable block).
        //
        // R1 (memoized gather): everything below the entry early-out is a pure function of the WINNER SET
        // (plan.BlockId/LocalIndex/Blocks/WinnerCount), which changes only on a front-swap — see
        // SymbolLabelSubsystem's _frontSetVersion. On a same-source-and-version frame every heavy pool below is
        // already correct, so only the three per-frame masks are rewritten (WritePerFrameMasks).
        internal void GatherIntoMirror(SymbolGatherPlan plan)
        {
            // Same SOURCE and same SET version ⇒ every pool below is already correct (the set is the only thing they
            // depend on). The count term is the release-build backstop: if a future front-mutation site ever lands
            // without a version bump, fall through and rebuild rather than memcpy a mismatched length (a checked
            // throw in the Editor, a raw memmove out of bounds in a player).
            bool sameSourceAndVersion = plan != null && ReferenceEquals(plan, _mirrorSource)
                                         && plan.WinnerSetVersion == _mirrorVersion;
            if (sameSourceAndVersion)
            {
                AssertMemoPlanMatchesMirror(plan);        // debug-only — fires iff the key says "same set" but the
                                                           // count disagrees, i.e. exactly when the backstop below engages
                if (plan.WinnerCount == _mCount)
                {
                    WritePerFrameMasks(plan);             // the ONLY per-frame work on a held mirror
                    return;
                }
            }

            MirrorRebuildCount++;
            int winners = plan?.WinnerCount ?? 0;

            // The three per-frame masks are WritePerFrameMasks' inputs, never read by SymbolGatherJob — resized
            // here on the main thread exactly as the old inline resize did (:1075-1076 pre-Burst-gather).
            _mRecordDeparting.ResizeUninitialized(winners); _mRecordCoverageFading.ResizeUninitialized(winners);
            _mRecordDropped.ResizeUninitialized(winners);

            if (winners == 0)
            {
                // Degenerate case: the old two loops fell through to zero naturally over `for (int r = 0; r < 0; …)`.
                // An explicit branch keeps that behaviour without dispatching a job over an empty view table or
                // dereferencing a null plan.Blocks/plan.BlockCount.
                _mKinds.ResizeUninitialized(0); _mDetail.ResizeUninitialized(0);
                _mWorldCount.ResizeUninitialized(0); _mWorldStart.ResizeUninitialized(0);
                _mRepAnchor.ResizeUninitialized(0);
                _mPoints.ResizeUninitialized(0); _mPointQuadStart.ResizeUninitialized(0); _mPointQuadCount.ResizeUninitialized(0);
                _mCurveds.ResizeUninitialized(0);
                _mCurvedGlyphStart.ResizeUninitialized(0); _mCurvedGlyphCount.ResizeUninitialized(0);
                _mCurvedAnchorStart.ResizeUninitialized(0); _mCurvedAnchorCount.ResizeUninitialized(0);
                _mCurvedAnchorFadeStart.ResizeUninitialized(0);
                _mQuads.ResizeUninitialized(0); _mGlyphs.ResizeUninitialized(0);
                _mAnchors.ResizeUninitialized(0); _mFadeIds.ResizeUninitialized(0);
                _mWorldPoints.ResizeUninitialized(0);

                _mPointCount = 0; _mCurvedCount = 0;
                _mQuadCount = 0; _mGlyphCount = 0; _mAnchorCount = 0; _mFadeCount = 0; _mWorldPointCount = 0;
                _mMaxBoxes = 0; _mMaxQuads = 0; _mMaxCandidates = 0;
            }
            else
            {
                BuildBlockViews(plan); // main-thread, alloc-free (see its own doc) — the per-frame view table

                // Line-for-line Burst transliteration of the two managed loops this replaces — see
                // SymbolGatherJob's own doc for the byte-identity argument and the job-shape precedent.
                new SymbolGatherJob
                {
                    BlockViews = _gatherBlockViews.AsArray(),
                    BlockId = plan.BlockId.AsArray(),
                    LocalIndex = plan.LocalIndex.AsArray(),
                    WinnerCount = winners,
                    MKinds = _mKinds, MDetail = _mDetail, MWorldCount = _mWorldCount, MWorldStart = _mWorldStart,
                    MRepAnchor = _mRepAnchor,
                    MPoints = _mPoints, MPointQuadStart = _mPointQuadStart, MPointQuadCount = _mPointQuadCount,
                    MCurveds = _mCurveds,
                    MCurvedGlyphStart = _mCurvedGlyphStart, MCurvedGlyphCount = _mCurvedGlyphCount,
                    MCurvedAnchorStart = _mCurvedAnchorStart, MCurvedAnchorCount = _mCurvedAnchorCount,
                    MCurvedAnchorFadeStart = _mCurvedAnchorFadeStart,
                    MQuads = _mQuads, MGlyphs = _mGlyphs, MAnchors = _mAnchors, MFadeIds = _mFadeIds,
                    MWorldPoints = _mWorldPoints,
                    OutCounts = _gatherCounts,
                }.Run();

                _mPointCount = _gatherCounts[SymbolGatherJob.CountPoint];
                _mCurvedCount = _gatherCounts[SymbolGatherJob.CountCurved];
                _mQuadCount = _gatherCounts[SymbolGatherJob.CountQuad];
                _mGlyphCount = _gatherCounts[SymbolGatherJob.CountGlyph];
                _mAnchorCount = _gatherCounts[SymbolGatherJob.CountAnchor];
                _mFadeCount = _gatherCounts[SymbolGatherJob.CountFade];
                _mWorldPointCount = _gatherCounts[SymbolGatherJob.CountWorldPoint];
                _mMaxBoxes = _gatherCounts[SymbolGatherJob.CountMaxBoxes];
                _mMaxQuads = _gatherCounts[SymbolGatherJob.CountMaxQuads];
                _mMaxCandidates = _gatherCounts[SymbolGatherJob.CountMaxCandidates];
            }

            _mCount = winners; // set BEFORE WritePerFrameMasks — it bounds its copies on THIS frame's count, not
                                // the previous one (calling it before would write a partial/over-long mask range)

            // R1: the per-frame masks (Departing/CoverageFading/Dropped) + _mNonDroppedCount are the ONLY per-frame
            // inputs — one writer shared by the heavy path and the memo-hit early-out above, so mask byte-identity
            // between the two is structural, not tested-for.
            WritePerFrameMasks(plan);

            // Stamp the shared source key — a later Tick with the SAME plan+version memo-hits above; a demo
            // Tick(batch) or a different plan mismatches on identity and falls through to its own rebuild.
            _mirrorSource = plan; _mirrorVersion = plan?.WinnerSetVersion ?? long.MinValue;
        }

        // Burst-gather Stage 1 (design doc §2.4): rebuild the reusable view table from plan.Blocks[0,
        // plan.BlockCount) — one SymbolBlockView per block, each field a non-owning UnsafeList<T> pointer view
        // over that block's OWN Allocator.Persistent array. NativeArrayUnsafeUtility.GetUnsafeReadOnlyPtr performs
        // an AtomicSafetyHandle.CheckRead per call — a disposed block throws HERE, on the main thread, with a
        // real stack, rather than the job silently reading freed memory (§2.3's decisive argument for this
        // storage shape). Managed-allocation-free: indexing plan.Blocks (a plain array) and taking pointers off
        // already-live NativeArrays allocates nothing (SymbolTileLabelBlockBaker.Bake always allocates all 19
        // arrays, even at length 0, so no IsCreated guard is needed here). Called from GatherIntoMirror, above.
        private unsafe void BuildBlockViews(SymbolGatherPlan plan)
        {
            _gatherBlockViews.ResizeUninitialized(plan.BlockCount);
            for (int b = 0; b < plan.BlockCount; b++)
            {
                SymbolTileLabelBlock block = plan.Blocks[b];
                _gatherBlockViews[b] = new SymbolBlockView
                {
                    Kinds = new UnsafeList<byte>((byte*)NativeArrayUnsafeUtility.GetUnsafeReadOnlyPtr(block.Kinds), block.Kinds.Length),
                    Detail = new UnsafeList<int>((int*)NativeArrayUnsafeUtility.GetUnsafeReadOnlyPtr(block.Detail), block.Detail.Length),
                    WorldStart = new UnsafeList<int>((int*)NativeArrayUnsafeUtility.GetUnsafeReadOnlyPtr(block.WorldStart), block.WorldStart.Length),
                    WorldCount = new UnsafeList<int>((int*)NativeArrayUnsafeUtility.GetUnsafeReadOnlyPtr(block.WorldCount), block.WorldCount.Length),
                    RepAnchor = new UnsafeList<double3>((double3*)NativeArrayUnsafeUtility.GetUnsafeReadOnlyPtr(block.RepAnchor), block.RepAnchor.Length),

                    Points = new UnsafeList<PointStageInput>((PointStageInput*)NativeArrayUnsafeUtility.GetUnsafeReadOnlyPtr(block.Points), block.Points.Length),
                    PointQuadStart = new UnsafeList<int>((int*)NativeArrayUnsafeUtility.GetUnsafeReadOnlyPtr(block.PointQuadStart), block.PointQuadStart.Length),
                    PointQuadCount = new UnsafeList<int>((int*)NativeArrayUnsafeUtility.GetUnsafeReadOnlyPtr(block.PointQuadCount), block.PointQuadCount.Length),

                    Curveds = new UnsafeList<CurvedStageInput>((CurvedStageInput*)NativeArrayUnsafeUtility.GetUnsafeReadOnlyPtr(block.Curveds), block.Curveds.Length),
                    CurvedGlyphStart = new UnsafeList<int>((int*)NativeArrayUnsafeUtility.GetUnsafeReadOnlyPtr(block.CurvedGlyphStart), block.CurvedGlyphStart.Length),
                    CurvedGlyphCount = new UnsafeList<int>((int*)NativeArrayUnsafeUtility.GetUnsafeReadOnlyPtr(block.CurvedGlyphCount), block.CurvedGlyphCount.Length),
                    CurvedAnchorStart = new UnsafeList<int>((int*)NativeArrayUnsafeUtility.GetUnsafeReadOnlyPtr(block.CurvedAnchorStart), block.CurvedAnchorStart.Length),
                    CurvedAnchorCount = new UnsafeList<int>((int*)NativeArrayUnsafeUtility.GetUnsafeReadOnlyPtr(block.CurvedAnchorCount), block.CurvedAnchorCount.Length),
                    CurvedAnchorFadeStart = new UnsafeList<int>((int*)NativeArrayUnsafeUtility.GetUnsafeReadOnlyPtr(block.CurvedAnchorFadeStart), block.CurvedAnchorFadeStart.Length),

                    Quads = new UnsafeList<SymbolQuad>((SymbolQuad*)NativeArrayUnsafeUtility.GetUnsafeReadOnlyPtr(block.Quads), block.Quads.Length),
                    Glyphs = new UnsafeList<CurvedGlyph>((CurvedGlyph*)NativeArrayUnsafeUtility.GetUnsafeReadOnlyPtr(block.Glyphs), block.Glyphs.Length),
                    Anchors = new UnsafeList<LineAnchor>((LineAnchor*)NativeArrayUnsafeUtility.GetUnsafeReadOnlyPtr(block.Anchors), block.Anchors.Length),
                    WorldPoints = new UnsafeList<double3>((double3*)NativeArrayUnsafeUtility.GetUnsafeReadOnlyPtr(block.WorldPoints), block.WorldPoints.Length),
                    AnchorFadeIds = new UnsafeList<long>((long*)NativeArrayUnsafeUtility.GetUnsafeReadOnlyPtr(block.AnchorFadeIds), block.AnchorFadeIds.Length),
                };
            }
        }

        // R1: the ONLY per-frame inputs to the mirror — the three per-record byte masks (LabelTileCoverageFilter's
        // Keep/Fade/Drop classification + the store's departing flag) and the _mNonDroppedCount they derive.
        // Everything else the gather writes is a pure function of the winner SET, so a same-version frame runs only
        // this. Source and destination are already native and already the same length (SymbolGatherPlan's
        // NativeList<byte>s are WinnerCount long; the mirror masks were sized to `records` by the rebuild that
        // filled them), so this is a straight memcpy per mask — it never resizes, reallocates, or walks a record.
        // This is what makes the plan.WinnerCount == _mCount term in GatherIntoMirror's memo predicate non-optional
        // (see that predicate's comment for why a length mismatch here would be unsafe).
        private void WritePerFrameMasks(SymbolGatherPlan plan)
        {
            if (plan == null || _mCount == 0) { _mNonDroppedCount = 0; return; } // clean no-op (incl. the null-plan heavy path)
            NativeArray<byte>.Copy(plan.Departing.AsArray(), 0, _mRecordDeparting.AsArray(), 0, _mCount);
            NativeArray<byte>.Copy(plan.CoverageFading.AsArray(), 0, _mRecordCoverageFading.AsArray(), 0, _mCount);
            NativeArray<byte>.Copy(plan.Dropped.AsArray(), 0, _mRecordDropped.AsArray(), 0, _mCount);
            _mNonDroppedCount = _mCount - plan.DroppedCount; // Blocker 1: TickCore gates on THIS
        }

        // Debug-only (fires loudly in the Editor / development builds — where the RED-verify and gate run — and
        // costs a release player nothing): the memo key said "same set" (source + version match) but the record
        // count disagrees. That should never happen (every front-content change bumps the version), so a fire
        // means a future front-mutation site landed without bumping WinnerSetVersion — exactly the scenario the
        // GatherIntoMirror predicate's count term exists to survive (see its comment).
        [System.Diagnostics.Conditional("UNITY_ASSERTIONS")]
        private void AssertMemoPlanMatchesMirror(SymbolGatherPlan plan)
        {
            if (plan.WinnerCount == _mCount) return;
            UnityEngine.Debug.LogAssertion(
                $"[LabelPlacementSystem] gather memo key matched (same plan, WinnerSetVersion={plan.WinnerSetVersion}) " +
                $"but WinnerCount={plan.WinnerCount} != mirrored count={_mCount} — a front-content change did not " +
                "bump WinnerSetVersion; falling back to a full rebuild this frame.");
        }

        private static void Mirror<T>(NativeList<T> dst, T[] src, int count) where T : unmanaged
        {
            dst.ResizeUninitialized(count);
            for (int i = 0; i < count; i++) dst[i] = src[i];
        }

        private static void MirrorKinds(NativeList<byte> dst, SymbolLabelBatch.Kind[] src, int count)
        {
            dst.ResizeUninitialized(count);
            for (int i = 0; i < count; i++) dst[i] = (byte)src[i];
        }

        private static void MirrorBool(NativeList<byte> dst, bool[] src, int count)
        {
            dst.ResizeUninitialized(count);
            for (int i = 0; i < count; i++) dst[i] = (byte)(src[i] ? 1 : 0);
        }

        // D1: zero-fill a record-level byte mask with no managed source array (the demo batch path has no
        // Dropped concept — see RefreshBatchMirror).
        private static void ClearBytes(NativeList<byte> dst, int count)
        {
            dst.ResizeUninitialized(count);
            for (int i = 0; i < count; i++) dst[i] = 0;
        }

        /// <summary>Stage-2 parity-test seam (reached via <c>InternalsVisibleTo</c>): materialize the private
        /// native mirror — as last filled by <see cref="GatherIntoMirror"/> or <see cref="RefreshBatchMirror"/> —
        /// into <paramref name="dest"/>'s managed SoA, so a test can assert the gather is field-by-field identical
        /// to the <see cref="SymbolLabelBatchBuilder.Build"/> oracle over the same collected list (tooth #3). No
        /// production caller: the private native mirror is otherwise unreachable, so this is the sanctioned
        /// broaden-to-internal accessor.</summary>
        internal void CopyMirrorInto(SymbolLabelBatch dest)
        {
            dest.Kinds = new SymbolLabelBatch.Kind[_mCount];
            for (int i = 0; i < _mCount; i++) dest.Kinds[i] = (SymbolLabelBatch.Kind)_mKinds[i];
            dest.Detail = ToArray(_mDetail, _mCount);
            dest.WorldStart = ToArray(_mWorldStart, _mCount);
            dest.WorldCount = ToArray(_mWorldCount, _mCount);
            dest.RepAnchor = ToArray(_mRepAnchor, _mCount);
            dest.RecordDeparting = ToBoolArray(_mRecordDeparting, _mCount);
            dest.RecordCoverageFading = ToBoolArray(_mRecordCoverageFading, _mCount);
            dest.Count = _mCount;

            dest.Points = ToArray(_mPoints, _mPointCount);
            dest.PointQuadStart = ToArray(_mPointQuadStart, _mPointCount);
            dest.PointQuadCount = ToArray(_mPointQuadCount, _mPointCount);
            dest.PointCount = _mPointCount;

            dest.Curveds = ToArray(_mCurveds, _mCurvedCount);
            dest.CurvedGlyphStart = ToArray(_mCurvedGlyphStart, _mCurvedCount);
            dest.CurvedGlyphCount = ToArray(_mCurvedGlyphCount, _mCurvedCount);
            dest.CurvedAnchorStart = ToArray(_mCurvedAnchorStart, _mCurvedCount);
            dest.CurvedAnchorCount = ToArray(_mCurvedAnchorCount, _mCurvedCount);
            dest.CurvedAnchorFadeStart = ToArray(_mCurvedAnchorFadeStart, _mCurvedCount);
            dest.CurvedCount = _mCurvedCount;

            dest.Quads = ToArray(_mQuads, _mQuadCount); dest.QuadCount = _mQuadCount;
            dest.Glyphs = ToArray(_mGlyphs, _mGlyphCount); dest.GlyphCount = _mGlyphCount;
            dest.Anchors = ToArray(_mAnchors, _mAnchorCount); dest.AnchorCount = _mAnchorCount;
            dest.WorldPoints = ToArray(_mWorldPoints, _mWorldPointCount); dest.WorldPointCount = _mWorldPointCount;
            dest.AnchorFadeIds = ToArray(_mFadeIds, _mFadeCount); dest.AnchorFadeCount = _mFadeCount;

            dest.MaxBoxes = _mMaxBoxes; dest.MaxQuads = _mMaxQuads; dest.MaxCandidates = _mMaxCandidates;
        }

        private static T[] ToArray<T>(NativeList<T> src, int count) where T : unmanaged
        {
            var a = new T[count];
            for (int i = 0; i < count; i++) a[i] = src[i];
            return a;
        }

        private static bool[] ToBoolArray(NativeList<byte> src, int count)
        {
            var a = new bool[count];
            for (int i = 0; i < count; i++) a[i] = src[i] != 0;
            return a;
        }

        // Pre-size the job's output pools to the mirror worst case (Burst cannot grow) + the arc-walk scratch to at
        // least the longest path (the total gathered-point count is a safe upper bound for any single path).
        // R2: also sizes _sjAnchorWasPlaced — the A-5 anchor-incumbency resolve moved into LabelStageJob itself
        // (Burst), so this method only sizes the scratch the job fills; it no longer resolves any fade id.
        private void PreSizeStageOutputs()
        {
            _sjBoxes.ResizeUninitialized(math.max(1, _mMaxBoxes));
            _sjQuads.ResizeUninitialized(math.max(1, _mMaxQuads));
            _sjCandidates.ResizeUninitialized(math.max(1, _mMaxCandidates));
            _sjEmit.ResizeUninitialized(math.max(1, _mMaxCandidates));
            int scratch = math.max(1, _symbolPoints.Length);
            _sjPath.ResizeUninitialized(scratch);
            _sjCum.ResizeUninitialized(scratch);
            // No math.max(1, …) floor here, unlike the resizes above: a zero-length _mFadeCount is fine — the
            // job's fill loop and the curved arm's fade-id slice are both empty in that case, so there's no OOB
            // to guard against.
            _sjAnchorWasPlaced.ResizeUninitialized(_mFadeCount);
        }

        private void RunStageJob(float bearingRadians, double2 viewportLogicalPx)
        {
            new LabelStageJob
            {
                Kinds = _mKinds.AsArray(), Detail = _mDetail.AsArray(), WorldCount = _mWorldCount.AsArray(), Count = _mCount,
                Points = _mPoints.AsArray(), PointQuadStart = _mPointQuadStart.AsArray(), PointQuadCount = _mPointQuadCount.AsArray(),
                Curveds = _mCurveds.AsArray(),
                CurvedGlyphStart = _mCurvedGlyphStart.AsArray(), CurvedGlyphCount = _mCurvedGlyphCount.AsArray(),
                CurvedAnchorStart = _mCurvedAnchorStart.AsArray(), CurvedAnchorCount = _mCurvedAnchorCount.AsArray(),
                CurvedAnchorFadeStart = _mCurvedAnchorFadeStart.AsArray(),
                Quads = _mQuads.AsArray(), Glyphs = _mGlyphs.AsArray(), Anchors = _mAnchors.AsArray(), AnchorFadeIds = _mFadeIds.AsArray(),
                PointOffset = _sjPointOffset.AsArray(),
                Screen = _symbolScreen.AsArray(), Depth = _symbolDepth.AsArray(), Valid = _symbolValid.AsArray(),
                // Stage AC (curved-world): the SAME gathered world polyline Screen was projected FROM
                // (_symbolPoints persists across the synchronous .Run() call below — see its own field doc).
                WorldPointsRender = _symbolPoints.AsArray(),
                AnchorWasPlaced = _sjAnchorWasPlaced.AsArray(), Placed = _placedLastFrame.AsReadOnly(),
                Bearing = bearingRadians, Viewport = viewportLogicalPx,
                PathScratch = _sjPath.AsArray(), CumScratch = _sjCum.AsArray(),
                Boxes = _sjBoxes.AsArray(), StagedQuads = _sjQuads.AsArray(),
                Candidates = _sjCandidates.AsArray(), Emit = _sjEmit.AsArray(), OutCounts = _sjCounts,
            }.Run();
        }

        // ── A-4 fade helpers ──────────────────────────────────────────────────────────────────────────────
        // Move this identity's opacity one deltaTime step toward `target` (1 = placed, 0 = suppressed/gone). A
        // never-seen id starts at 0, so it fades IN. deltaTime == +inf (the Tick default) makes step == +inf, so
        // it SNAPS straight to the target — a single-Tick test then renders fully-placed labels (byte-parity).
        private float EaseFade(long fadeId, float target, float deltaTime)
        {
            bool had = _fadeOpacity.TryGetValue(fadeId, out float current); // absent ≡ 0 (`current` defaults to 0)
            float step = deltaTime / FadeDurationSeconds;
            float next = target > current ? math.min(current + step, target) : math.max(current - step, target);

            // Storing an invisible identity would leak it permanently: DecayUnseenFadeRecords only drops keys NOT
            // seen this frame, and a staged candidate is always seen — so every candidate that never places (most
            // of them, in a dense view) would park a 0 that nothing collects. Drop instead. This is the same floor
            // rule DecayUnseenFadeRecords already applies to its own decay; it just never reached the hot path.
            // Both readers outside this loop (MarkFadeOutIfAlive / TryForceFadeOut) test `TryGetValue && > epsilon`,
            // under which an ABSENT entry and ANY sub-epsilon stored value read alike — covering every value this
            // branch can discard, not just exact zeros — so dropping is behaviour-preserving there.
            // `target <= current` confines the drop to a fade-OUT (or an already-invisible identity): without it, a
            // fade-IN whose first step landed below epsilon would be dropped and restart from 0 every frame.
            if (next <= FadeEpsilon && target <= current)
            {
                if (had) _fadeOpacity.Remove(fadeId);
                return next; // <= epsilon ⇒ the caller skips it; nothing is emitted
            }

            _fadeOpacity[fadeId] = next;
            _seenFade.Add(fadeId); // see the field doc: seen ⟺ stored, so the decay sweep can trust it
            return next;
        }

        // Records whose label was NOT staged this frame (its tile left cover / it projected off-screen / it was
        // removed) decay toward 0 and are DROPPED once invisible — keeping the map bounded (never an
        // ever-growing dict). No-cache scope: an absent label is not re-drawn (v1); this only controls whether a
        // reappearing id resumes from a decayed value or fades in fresh.
        private void DecayUnseenFadeRecords(float deltaTime)
        {
            float step = deltaTime / FadeDurationSeconds;
            _fadeScratchKeys.Clear();
            foreach (long id in _fadeOpacity.Keys)
                if (!_seenFade.Contains(id)) _fadeScratchKeys.Add(id);
            for (int i = 0; i < _fadeScratchKeys.Count; i++)
            {
                long id = _fadeScratchKeys[i];
                float next = math.max(_fadeOpacity[id] - step, 0f);
                if (next <= FadeEpsilon) _fadeOpacity.Remove(id);
                else _fadeOpacity[id] = next;
            }
        }

        // Mark each just-staged candidate LabelCandidate.Suppressed BEFORE collision when its owning layer is OUT OF
        // the live camera zoom's minzoom/maxzoom (MapLibre layer visibility). A suppressed candidate is treated as
        // absent — never placed, never a blocker — while it still eases to 0 (fading out) in the emit loop. Evaluated
        // per-frame against the LIVE zoom — NOT the tile build zoom — so overzoomed tiles reveal/hide layers as the
        // camera crosses a layer boundary. Cheap main-thread pass; Slot comes from the emit record.
        private void ApplySuppression(int candidateCount, IReadOnlyList<SymbolRenderLayer> symbolLayers, double zoom)
        {
            if (symbolLayers == null || symbolLayers.Count == 0) return; // demo path → no per-layer zoom ranges
            for (int s = 0; s < candidateCount; s++)
            {
                LabelCandidate c = _sjCandidates[s];
                int slot = _sjEmit[c.LabelIndex].Slot;
                bool suppress = slot >= 0 && slot < symbolLayers.Count && symbolLayers[slot]?.StyleLayer != null
                    && !symbolLayers[slot].StyleLayer.IsVisibleAtZoom(zoom);
                if (c.Suppressed != suppress) { c.Suppressed = suppress; _sjCandidates[s] = c; }
            }
        }

        /// <summary>A-4 POINT fade identity: the A-3 cross-tile key on the shared canonical grid
        /// (<see cref="CrossTileLabelKey.CanonicalGridMeters"/>), hashed to a long. Stage 3b: this quantizes to
        /// the SAME fixed grid the store dedup does, so a point label's fade cell and its dedup cell are literally
        /// one canonical identity — the fade partition equals the dedup partition. Fixed (zoom-independent) grid ⇒
        /// a frame-STABLE id (a per-frame display-zoom grid would re-key every label as the camera zooms); reusing
        /// <see cref="CrossTileLabelKey"/> lets the same symbol from a swapped tile keep its opacity record (the
        /// seamless no-op). <c>internal</c> so an EditMode test can pin the stable-across-zoom + within-grid-collapse
        /// behaviour and the fade↔dedup agreement directly.</summary>
        /// <param name="iconImage">I6: the icon's resolved sprite name (null for text, <see cref="LabelInstance.IconImage"/>)
        /// — folded into the fade id via a guard-skip (see <see cref="Hash64"/>) so a text label's id is unchanged.</param>
        internal static long PointFadeId(in double3 anchorRender, int layerId, string text, string iconImage = null)
            => Hash64(CrossTileLabelKey.For(anchorRender, layerId, text, iconImage, CrossTileLabelKey.CanonicalGridMeters));

        private static long Hash64(in CrossTileLabelKey k)
        {
            unchecked
            {
                ulong h = 1469598103934665603UL; // FNV-1a 64
                h = (h ^ (ulong)k.GridX) * 1099511628211UL;
                h = (h ^ (ulong)k.GridZ) * 1099511628211UL;
                // Stage 3b: fold GridY too, so the fade tuple == the dedup tuple (GridX,GridZ,GridY, layer, text,
                // icon) — parity with CrossTileLabelKey.Equals, which already compares GridY. NOTE this is NOT a
                // raw no-op even when GridY == 0: (h ^ 0) * prime still multiplies the accumulator, so every
                // point label's RAW fade id shifts. It is still snapshot-safe because the fade id is only ever an
                // equality/PARTITION key (_fadeOpacity/_placedLastFrame/_seenFade lookups) and multiply-by-the-odd-
                // FNV-prime is a bijection on ulong ⇒ which labels share an id is unchanged; the one magnitude-
                // ordered use (LabelCollision.ComparePlacementOrder's FadeId tiebreak) is unreachable for points
                // (distinct point candidates already differ on FeatureIndex/TileKey). On Mercator render.y ≡ 0 ⇒
                // GridY = 0 (partition unchanged); on the globe GridY separates two equator-mirrored anchors that
                // share X/Z but differ in Y.
                h = (h ^ (ulong)k.GridY) * 1099511628211UL;
                h = (h ^ (ulong)(uint)k.LayerId) * 1099511628211UL;
                h = (h ^ (ulong)(uint)(k.Text?.GetHashCode() ?? 0)) * 1099511628211UL;
                // I6 guard-skip fold: only mix IconImage when non-null, so a text label's fade id (IconImage
                // always null) hashes IDENTICALLY to before this field existed — same #1 invariant as
                // CrossTileLabelKey.GetHashCode (an unconditional `?? 0` fold would still perturb every text id).
                if (k.IconImage != null)
                    h = (h ^ (ulong)(uint)k.IconImage.GetHashCode()) * 1099511628211UL;
                return (long)h;
            }
        }

        /// <summary>
        /// Manual column-by-column conversion (not an assumed <c>(float4x4)</c> cast operator — the same
        /// "don't rely on possibly-absent implicit numeric conversions" caution <c>FloatingOrigin</c>
        /// documents for <c>double3</c>→<c>float3</c>). Column-major, matching
        /// <see cref="LabelScreenProjection"/>'s <c>math.mul(float4x4, float4)</c> convention.
        /// <c>internal</c> so <c>LabelScreenProjectionUnityTests</c> reuses the SAME conversion instead of
        /// duplicating it (the test-code-bloat convention's allowed footprint: broaden private → internal).
        /// </summary>
        /// <summary>The camera's view-projection matrix (projection × world-to-camera) as a
        /// <see cref="float4x4"/> — the single definition shared by label placement (<see cref="Tick"/>) and the
        /// coverage pre-cull (<c>SymbolLabelSubsystem.CurrentBatch</c>), read live off the committed camera so
        /// both see the same frame's matrices. Column-major (<see cref="ToFloat4x4"/> convention).</summary>
        internal static float4x4 ViewProj(Camera camera)
            => math.mul(ToFloat4x4(camera.projectionMatrix), ToFloat4x4(camera.worldToCameraMatrix));

        internal static float4x4 ToFloat4x4(Matrix4x4 m)
        {
            Vector4 c0 = m.GetColumn(0);
            Vector4 c1 = m.GetColumn(1);
            Vector4 c2 = m.GetColumn(2);
            Vector4 c3 = m.GetColumn(3);
            return new float4x4(
                new float4(c0.x, c0.y, c0.z, c0.w),
                new float4(c1.x, c1.y, c1.z, c1.w),
                new float4(c2.x, c2.y, c2.z, c2.w),
                new float4(c3.x, c3.y, c3.z, c3.w));
        }

        /// <summary>Destroys the world renderer + materials (main-thread only — play → <c>Destroy</c>,
        /// edit → <c>DestroyImmediate</c>, mirroring <c>GlyphAtlasTexture</c>/<c>MaterialFactory</c>) and
        /// disposes the native scratch buffers. Idempotent.</summary>
        protected override void DoDispose()
        {
            // R3: a scheduled-but-never-completed collision holds _sjCandidates/_sjBoxes/_nSurvivors/_survivorCountOut
            // and the grid lists — disposing them under a live job is a use-after-free (a safety-system throw in the
            // Editor). This is the only teardown path (VerifiedDisposable guarantees DoDispose runs at most once).
            if (_collisionPending) { _collisionHandle.Complete(); _collisionPending = false; }

            // R1: _mirrorSource otherwise retains a strong reference to the plan/batch indefinitely (unlike the old
            // per-tick _lastBatch reset) — SymbolLabelSubsystem.Dispose disposes _gatherPlan while this system may
            // still point at it, and a later gather with that plan would memo-hit and memcpy from disposed
            // NativeLists. Not reachable through MapView teardown today; free to close.
            _mirrorSource = null; _mirrorVersion = long.MinValue;

            _worldRenderer.Dispose();

            _worldTextMaterial.DestroySafely();
            _worldTextMaterial = null;

            _worldIconMaterial.DestroySafely();
            _worldIconMaterial = null;

            _symbolPoints.Dispose(); // B-2 projection scratch
            _symbolScreen.Dispose();
            _symbolDepth.Dispose();
            _symbolValid.Dispose();

            _nSurvivors.Dispose(); // B-4a collision survivor flags
            _gridCellHead.Dispose();
            _gridNodeBox.Dispose();
            _gridNodeNext.Dispose();
            _survivorCountOut.Dispose();

            _gatherBlockViews.Dispose(); _gatherCounts.Dispose();                    // Burst-gather Stage 1 buffers
            _mKinds.Dispose(); _mDetail.Dispose(); _mWorldCount.Dispose();          // Lever C step 3b native buffers
            _mPointQuadStart.Dispose(); _mPointQuadCount.Dispose();
            _mCurvedGlyphStart.Dispose(); _mCurvedGlyphCount.Dispose();
            _mCurvedAnchorStart.Dispose(); _mCurvedAnchorCount.Dispose(); _mCurvedAnchorFadeStart.Dispose();
            _mPoints.Dispose(); _mCurveds.Dispose();
            _mQuads.Dispose(); _mGlyphs.Dispose(); _mAnchors.Dispose(); _mFadeIds.Dispose();
            _mWorldStart.Dispose(); _mRepAnchor.Dispose(); _mWorldPoints.Dispose();       // Stage-2 record-level fields
            _mRecordDeparting.Dispose(); _mRecordCoverageFading.Dispose(); _mRecordDropped.Dispose();
            _sjPointOffset.Dispose(); _sjAnchorWasPlaced.Dispose();
            _sjBoxes.Dispose(); _sjQuads.Dispose(); _sjCandidates.Dispose(); _sjEmit.Dispose();
            _sjCounts.Dispose(); _sjPath.Dispose(); _sjCum.Dispose();
            _placedLastFrame.Dispose(); // R2: native set, ctor-allocated alongside the other persistent containers
            // R3: AssertFadeIdsUnique's persistent scratch set (see its field doc) — lazily allocated, so a
            // release build (or an Editor instance that never staged a candidate) may never have created it.
            if (_debugFadeIdSeen.IsCreated) _debugFadeIdSeen.Dispose();
        }
    }
}