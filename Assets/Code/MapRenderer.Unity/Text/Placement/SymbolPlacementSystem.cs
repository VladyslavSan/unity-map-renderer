// Namespace-collision guard: TOP-LEVEL `using Unity.Mathematics;` with unqualified types, never an inline
// `Unity.Mathematics.X` (it binds to the nonexistent `...Placement.Unity.Mathematics`, CS0234).

using System;
using System.Collections.Generic;
using MapRenderer.Core.Geo;
using MapRenderer.Core.Lifetime;
using MapRenderer.Unity.View;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Jobs;
using Unity.Mathematics;
using Unity.Profiling;
using UnityEngine;
using MapRenderer.Unity.Common;
using MapRenderer.Core.Text;
using MapRenderer.Core.Text.Placement;
using MapRenderer.Unity.Text;
using MapRenderer.Jobs.Symbols;
using MapRenderer.Unity.Rendering.Backend;
using MapRenderer.Unity.Rendering.Map;
using MapRenderer.Unity.Rendering.Materials;
using MapRenderer.Unity.Rendering.Style;

namespace MapRenderer.Unity.Text.Placement
{
    /// <summary>Per-frame symbol renderer, owned by <see cref="MapView"/>, ticked last after
    /// <see cref="MapCamera.SyncToCamera"/>. Collision runs across every symbol layer; only the draw step
    /// splits by material slot. The class is <c>internal</c>, but members stay <c>public</c> so
    /// <see cref="Tick"/>'s <c>internal</c> <see cref="Backend.SceneFrame"/> param avoids CS0051.</summary>
    // `partial`: the opt-in breakdown diagnostic lives in SymbolPlacementSystem.Diagnostics.cs.
    internal sealed partial class SymbolPlacementSystem : VerifiedDisposable
    {
        /// <summary>Profiler marker name constants (SSOT) for the symbol path — <c>ProfilerMarkerTests</c>
        /// asserts this exact string set. Hierarchical names so the Profiler flat search reads as a tree.</summary>
        internal static class ProfilerMarkerNames
        {
            /// <summary>Marker for Stage-2 mirror compaction — a same-version frame mostly measures a memo hit.</summary>
            internal const string Gather      = "MapRenderer.Symbol.Gather";
            internal const string Tick        = "MapRenderer.Symbol.SymbolTick";
            internal const string Project     = "MapRenderer.Symbol.Project";
            /// <summary>Marker for the per-symbol cull scan, split out so its cost isn't lumped into ProjectPositions.</summary>
            internal const string GatherPoints = "MapRenderer.Symbol.GatherPoints";
            /// <summary>GatherPoints' two passes: Cull is the per-symbol verdict, Compact is the fade-probe/append/tally.</summary>
            internal const string GatherCull    = "MapRenderer.Symbol.Gather.Cull";
            internal const string GatherCompact = "MapRenderer.Symbol.Gather.Compact";
            internal const string ProjectPositions = "MapRenderer.Symbol.ProjectPositions";
            internal const string Stage       = "MapRenderer.Symbol.Stage";
            // Grid sizing + Schedule only — the job's own wait lives in CollideHarvest.
            internal const string Collide     = "MapRenderer.Symbol.Collide";
            /// <summary>Marker for harvesting the previous Tick's scheduled collision — should read ≈0.</summary>
            internal const string CollideHarvest = "MapRenderer.Symbol.CollideHarvest";
            internal const string Emit        = "MapRenderer.Symbol.Emit";
            /// <summary>Emit splits into EmitLoop (per-candidate) and EmitDecay (per-live-fade-identity) —
            /// the two cost shapes differ enough to profile separately.</summary>
            internal const string EmitLoop    = "MapRenderer.Symbol.EmitLoop";
            internal const string EmitDecay   = "MapRenderer.Symbol.EmitDecay";
        }

        // Per-frame profiler markers (Profiler window → search "MapRenderer.Symbol"); PmTick covers the whole submit.
        private static readonly ProfilerMarker PmGather =
            new(ProfilerCategory.Scripts, ProfilerMarkerNames.Gather);

        private static readonly ProfilerMarker PmTick =
            new(ProfilerCategory.Scripts, ProfilerMarkerNames.Tick);

        private static readonly ProfilerMarker PmProject =
            new(ProfilerCategory.Scripts, ProfilerMarkerNames.Project);

        // GatherPoints, ProjectPositions and Stage nest under PmProject, split so the cull scan's cost isn't lumped in.
        private static readonly ProfilerMarker PmGatherPoints =
            new(ProfilerCategory.Scripts, ProfilerMarkerNames.GatherPoints);

        // GatherPoints' two passes nest under PmGatherPoints — see ProfilerMarkerNames.GatherCull/GatherCompact.
        private static readonly ProfilerMarker PmGatherCull =
            new(ProfilerCategory.Scripts, ProfilerMarkerNames.GatherCull);
        private static readonly ProfilerMarker PmGatherCompact =
            new(ProfilerCategory.Scripts, ProfilerMarkerNames.GatherCompact);

        private static readonly ProfilerMarker PmProjectPositions =
            new(ProfilerCategory.Scripts, ProfilerMarkerNames.ProjectPositions);

        private static readonly ProfilerMarker PmStage =
            new(ProfilerCategory.Scripts, ProfilerMarkerNames.Stage);

        private static readonly ProfilerMarker PmCollide =
            new(ProfilerCategory.Scripts, ProfilerMarkerNames.Collide);

        // Should read ≈0 — see ProfilerMarkerNames.CollideHarvest's comment.
        private static readonly ProfilerMarker PmCollideHarvest =
            new(ProfilerCategory.Scripts, ProfilerMarkerNames.CollideHarvest);

        // Emit is the fade + world-renderer hand-off (managed, main thread), run after collision.
        private static readonly ProfilerMarker PmEmit =
            new(ProfilerCategory.Scripts, ProfilerMarkerNames.Emit);

        // The two halves of PmEmit — see the ProfilerMarkerNames comment for why they are split.
        private static readonly ProfilerMarker PmEmitLoop =
            new(ProfilerCategory.Scripts, ProfilerMarkerNames.EmitLoop);

        private static readonly ProfilerMarker PmEmitDecay =
            new(ProfilerCategory.Scripts, ProfilerMarkerNames.EmitDecay);

        /// <summary>World-anchored fallback materials, used for any slot with no supplied per-layer material.
        /// Null means that draw path stays inert.</summary>
        private Material _worldTextMaterial;
        private Material _worldIconMaterial;

        /// <summary>The world-anchored point/icon renderer, ticked BeginFrame/Emit/EndFrame every <see cref="Tick"/>.
        /// <c>internal</c> so test-only accessors reach it as extension methods instead of living here.</summary>
        internal WorldSymbolRenderer WorldRenderer { get; } = new WorldSymbolRenderer();

        /// <summary>Collision is one Burst job over the stage job's output — each symbol is one
        /// <c>SymbolCandidate</c> spanning a box range, so every symbol competes in one greedy pass.</summary>
        private NativeList<byte>           _nSurvivors;
        private NativeList<int>            _gridCellHead;
        private NativeList<int>            _gridNodeBox;
        private NativeList<int>            _gridNodeNext;
        private NativeArray<int>           _survivorCountOut;

        /// <summary>Scheduled at the end of a Tick, harvested at the start of the next. Null means nothing
        /// scheduled; <c>_pendingCandidateCount</c> pins the count since the next stage job overwrites it.</summary>
        private JobHandle? _collisionHandle;
        private int        _pendingCandidateCount;

        /// <summary><c>AssertFadeIdsUnique</c>'s duplicate-check scratch, allocated lazily inside that
        /// method — a release build strips its call sites, so this field stays zero-allocation.</summary>
        private NativeHashSet<long> _debugFadeIdSeen;

        /// <summary>The pre-projection distance cull, as a fraction of the camera's far-clip distance — past it a
        /// symbol skips projection/collision. Runtime-tunable via the Diagnostics slider.</summary>
        internal double SymbolMaxDistanceFraction { get; set; } = 1.0;

        // ── Fade state machine ──
        /// <summary>Seconds for a symbol's opacity to ease fully toward 1 (placed) or 0 (gone). <c>internal</c>
        /// because <c>SymbolSubsystem</c> derives its departing-tile grace window from this constant.</summary>
        internal const float FadeDurationSeconds = 0.3f;
        private const float FadeEpsilon = 1e-3f; // below this, a symbol is invisible: not emitted, dropped
        // Holds identities visible or fading in — nothing is parked at 0 once a fade-out completes (EaseFade).
        /// <summary>Sized by visible symbols, not staged candidates — the two differ by an order of magnitude
        /// in a dense view. Native so the gather scan's fade-alive probe can move into Burst.</summary>
        private const int FadeMapInitialCapacity = 16384;
        /// <summary>Not readonly — allocated in the ctor.</summary>
        private NativeHashMap<long, float> _fadeOpacity;
        /// <summary>The identities <c>EaseFade</c> stored this frame. The decay sweep needs
        /// <c>stored ⇒ seen</c>: a key it finds unseen was untouched.</summary>
        private NativeHashSet<long> _seenFade;
        /// <summary>Reused decay-sweep buffer; not readonly — allocated in the ctor.</summary>
        private NativeList<long> _fadeSweepKeys;

        /// <summary>FadeIds the gather cull hit this frame whose fade is still alive — gather keeps staging
        /// them instead of a hard skip, and the emit loop eases them toward 0 in place.</summary>
        private NativeHashSet<long> _forceFadeOut; // not readonly — allocated in the ctor

        /// <summary>Per-frame slot → visible-at-live-zoom lookup, filled before <c>GatherSymbolPoints</c>'
        /// symbol loop. Index == material slot == <see cref="ShapedSymbol.MaterialIndex"/>.</summary>
        private NativeList<bool> _slotVisibleThisFrame; // not readonly — allocated in the ctor

        /// <summary>Per-symbol cull verdict from <c>GatherSymbolPoints</c>' Cull pass, consumed by its Compact pass.</summary>
        private NativeList<GatherTrigger> _gatherTrigger; // not readonly — allocated in the ctor

        /// <summary>Per-trigger culled tally the Compact pass writes — a job can't write the managed
        /// <c>Last*CulledCount</c> properties, so this is the bridge.</summary>
        private NativeArray<int> _gatherCulledCounts;

        // ── Sticky-placement hysteresis ──
        /// <summary>FadeIds that survived last frame's collision — staging biases the greedy sort so an
        /// incumbent keeps its slot over a near-tied newcomer, killing the tile-churn flicker.</summary>
        private const int PlacedSetInitialCapacity = 16384;
        /// <summary>Not readonly — allocated in the ctor.</summary>
        private NativeHashSet<long> _placedLastFrame;

        // ── Per-half collision verdict for optional pairs (icon-optional / text-optional) ──
        /// <summary>FadeId → <c>SymbolCandidate.DroppedBoxMask</c> for survivors with a non-zero mask —
        /// carries last frame's verdict across the one-frame collision-defer gap.</summary>
        private const int DroppedHalvesInitialCapacity = 64;
        /// <summary>Not readonly — allocated in the ctor.</summary>
        private NativeHashMap<long, byte> _droppedHalvesLastFrame;

        /// <summary>Every visible symbol's screen geometry this frame, projected by the Burst
        /// <c>SymbolProjectionJob</c>. <c>_stagePointOffset[r]</c> is symbol r's start (-1 = culled).</summary>
        private NativeList<double3> _symbolPoints;
        // Index-parallel to _symbolPoints — the unit surface normal at each point; not yet consumed downstream.
        private NativeList<float3>  _symbolUps;
        private NativeList<float2>  _symbolScreen;
        private NativeList<float>   _symbolDepth;
        private NativeList<byte>    _symbolValid;

        /// <summary>Native mirror of the plan's stage data, refreshed only when <c>WinnerSetVersion</c> changes.
        /// The memo key is (instance, version) — identity matters if a second plan instance appears.</summary>
        private SymbolGatherPlan _mirrorPlan;
        /// <summary>The plan's WinnerSetVersion when it was mirrored.</summary>
        private long _mirrorVersion = long.MinValue;
        /// <summary>Per-frame reused table of non-owning views over the plan's blocks, built just before the
        /// gather job runs. Never held across a frame boundary — <see cref="GatherIntoMirror"/>'s <c>.Run()</c> is synchronous.</summary>
        private NativeList<BlockView> _gatherBlockViews;
        /// <summary><c>SymbolGatherJob</c>'s OutCounts — see its Count* consts for the layout.</summary>
        private NativeArray<int> _gatherCounts;
        // ── mirror: per-symbol fields (one entry per gathered winner, mirror-local index) ──
        private NativeList<SymbolPlacementKind> _mirrorKinds;
        private NativeList<int>  _mirrorDetail;
        private NativeList<int>  _mirrorWorldCount;

        // ── mirror: point-symbol slices (into _mirrorQuads) ──
        private NativeList<int> _mirrorPointQuadStart;
        private NativeList<int> _mirrorPointQuadCount;

        // ── mirror: curved-symbol slices (into _mirrorGlyphs / _mirrorAnchors / _mirrorFadeIds) ──
        private NativeList<int> _mirrorCurvedGlyphStart;
        private NativeList<int> _mirrorCurvedGlyphCount;
        private NativeList<int> _mirrorCurvedAnchorStart;
        private NativeList<int> _mirrorCurvedAnchorCount;
        private NativeList<int> _mirrorCurvedAnchorFadeStart;

        // ── mirror: per-kind detail symbols (indexed by _mirrorDetail, selected by _mirrorKinds) ──
        private NativeList<PointStageInput>  _mirrorPoints;
        private NativeList<CurvedStageInput> _mirrorCurveds;

        // ── mirror: flat pools the slices above index into ──
        private NativeList<SymbolQuad>  _mirrorQuads;
        private NativeList<CurvedGlyph> _mirrorGlyphs;
        private NativeList<LineAnchor>  _mirrorAnchors;
        private NativeList<long>        _mirrorFadeIds;
        /// <summary>Record-level fields the gather cull reads, native so the path never touches a managed
        /// SoA. <c>_mirrorRepAnchor</c> is the distance-cull point.</summary>
        private NativeList<int>     _mirrorWorldStart;
        private NativeList<double3> _mirrorRepAnchor;
        private NativeList<double3> _mirrorWorldPoints;
        // Index-parallel to _mirrorWorldPoints — the unit surface normal at each point; not yet consumed.
        private NativeList<float3>  _mirrorWorldUps;
        private NativeList<byte>    _mirrorSymbolDeparting;
        private NativeList<byte>    _mirrorSymbolCoverageFading;
        /// <summary>The tile-coverage cull's Drop decision as a per-symbol mask. A Dropped winner stays
        /// resident in the mirror; <c>GatherSymbolPoints</c> hard-skips it first, unconditionally.</summary>
        private NativeList<byte>    _mirrorSymbolDropped;
        // Mirror-side counts — the shared core reads these instead of any managed source's counts.
        private int _mirrorCount;
        private int _mirrorPointCount;
        private int _mirrorCurvedCount;
        private int _mirrorQuadCount;
        private int _mirrorGlyphCount;
        private int _mirrorAnchorCount;
        private int _mirrorFadeCount;
        private int _mirrorWorldPointCount;
        /// <summary><c>_mirrorCount</c> includes Dropped symbols, so it does not mean "any placement
        /// work this frame". <c>TickCore</c>'s gate reads this field instead, so an all-Dropped frame keeps fades frozen.</summary>
        private int _mirrorNonDroppedCount;

        // ── mirror: staging-output upper bounds (camera-independent — summed from the gathered blocks) ──
        private int _mirrorMaxBoxes;
        private int _mirrorMaxQuads;
        private int _mirrorMaxCandidates;

        // ── stage-job buffers (pre-sized to the mirror's worst case, refilled every Tick) ──
        /// <summary>Gather output; -1 = culled.</summary>
        private NativeList<int>            _stagePointOffset;
        /// <summary>Per-frame anchor incumbency — filled by StageJob, sized here.</summary>
        private NativeList<byte>           _stageAnchorWasPlaced;
        /// <summary><c>internal</c>: read by <c>SymbolPlacementSystemTestExtensions.LastStagedBoxes()</c>.</summary>
        internal NativeList<SymbolBox>      _stageBoxes;
        private NativeList<PlacedQuad>     _stageQuads;
        private NativeList<SymbolCandidate> _stageCandidates;
        private NativeList<CandidateEmit>  _stageEmit;
        /// <summary>[candidateCount, boxCount, quadCount, emitCount].</summary>
        private NativeArray<int>           _stageCounts;
        /// <summary>Arc-walk scratch (&gt;= max path length).</summary>
        private NativeList<float2>         _stagePath;
        private NativeList<float>          _stageCumulativeLength;

        // A surviving candidate's built quads live in _stageQuads as a range (EmitStart/EmitCount) the sort can't disturb.

        /// <summary>Number of <see cref="Tick"/> calls so far — the vertex buffer rebuilds every Tick, not
        /// once at tile consume. Test surface.</summary>
        internal int TickCount { get; private set; }

        /// <summary>Heavy mirror fills so far — bumped once per real <see cref="GatherIntoMirror"/> rebuild,
        /// never on a memo hit. Drives the telemetry panel's rebuilds/second readout.</summary>
        internal int MirrorRebuildCount { get; private set; }

        private SymbolPlacementTelemetrySnapshot _telemetry;

        /// <summary>This provider's levels, handed out by reference (see <c>docs/telemetry-design.md</c>).
        /// Refreshed at the end of <see cref="Tick"/>, so the numbers are this pass's, not last frame's.</summary>
        internal ref readonly SymbolPlacementTelemetrySnapshot Telemetry => ref _telemetry;

        private void RefreshTelemetry() =>
            _telemetry = new SymbolPlacementTelemetrySnapshot
            {
                InputSymbolCount         = LastInputSymbolCount,
                DistanceCulledSymbols    = LastDistanceCulledCount,
                HorizonCulledSymbols     = LastHorizonCulledCount,
                CoverageFadingSymbols    = LastCoverageFadingCulledCount,
                ZoomCulledSymbols        = LastZoomCulledCount,
                CollisionCandidateCount = LastCandidateCount,
                CollisionSurvivorCount  = LastSurvivorCount,
                PlacedQuadCount         = LastQuadCount,
                MirrorRebuildCount      = MirrorRebuildCount,
                LiveFadeSymbolCount     = LiveFadeSymbolCount,
            };

        /// <summary>The quad count submitted on the LAST <see cref="Tick"/> (0 if nothing was visible). Test surface.</summary>
        internal int LastQuadCount { get; private set; }

        /// <summary>Symbols fed into the LAST <see cref="Tick"/> (before any projection cull) — telemetry.</summary>
        internal int LastInputSymbolCount { get; private set; }

        /// <summary>Collision boxes staged on the last <see cref="Tick"/> — a point symbol is 1, a curved symbol is 1 per glyph.</summary>
        internal int LastBoxCount { get; private set; }

        /// <summary>Collision candidates on the last Tick — a point symbol is 1, a curved/repeated line symbol is 1 per anchor.</summary>
        internal int LastCandidateCount { get; private set; }

        /// <summary>Collision survivors — one Tick behind <see cref="LastCandidateCount"/>, since collision is
        /// deferred: this is the raw, unfiltered survivor count from the previous Tick's candidates.</summary>
        internal int LastSurvivorCount { get; private set; }

        /// <summary>Symbols skipped by the pre-projection far-distance cull on the last Tick (past
        /// <see cref="SymbolMaxDistanceFraction"/> × far plane).</summary>
        internal int LastDistanceCulledCount { get; private set; }

        /// <summary>Symbols skipped on the last Tick because their tile is leaving cover and has finished
        /// fading out — before that they stay staged, fading, so there's no pop.</summary>
        internal int LastDepartingCulledCount { get; private set; }

        /// <summary>Symbols skipped on the last Tick because their anchor is hidden behind the globe's bulk
        /// (<see cref="HorizonCull"/>). Always 0 under a planar projection.</summary>
        internal int LastHorizonCulledCount { get; private set; }

        /// <summary>Symbols skipped by the pre-projection zoom gate on the last Tick — out of the live camera
        /// zoom range and not fading. A still-fading symbol stays staged instead; <see cref="ApplySuppression"/> hides it same-frame.</summary>
        internal int LastZoomCulledCount { get; private set; }

        /// <summary>Live fade-identity count — the size of the map <see cref="DecayUnseenFadeSymbols"/> walks
        /// each Tick, the cost being measured. Keep it the raw size: filtering it would silently disarm
        /// <c>SymbolFadeTests.Tick_StableCollisionLoser_LeavesNoFadeRecordBehind</c>, which pins this exact count.</summary>
        internal int LiveFadeSymbolCount => _fadeOpacity.Count;

        /// <summary>Symbols skipped on the last Tick because their tile's coverage dropped below threshold and
        /// have now fully faded out. Mirrors <see cref="LastDepartingCulledCount"/> for the coverage trigger.</summary>
        internal int LastCoverageFadingCulledCount { get; private set; }

        /// <summary>The map view this system renders symbols for. Read after <see cref="MapCamera.SyncToCamera"/>
        /// commits the frame transform — <c>MapView.LateUpdate</c> runs sync → rebase → place, in that order.</summary>
        private readonly MapCamera _camera;

        /// <summary>Default world materials — the fallback for slots with no per-layer material (owned instead
        /// by <c>Style.SymbolRenderLayer</c>), cloned since <see cref="Tick"/> mutates them every frame.</summary>
        /// <param name="worldTextBase">The only world-anchored point-text draw path; no <c>Shader.Find</c> fallback, the caller must supply it.</param>
        /// <param name="worldIconBase">The world-anchored icon draw path; null leaves world icons inert.</param>
        public SymbolPlacementSystem(MapCamera camera, Material worldTextBase, Material worldIconBase = null)
        {
            _camera = camera ?? throw new ArgumentNullException(nameof(camera));

            _symbolPoints = new NativeList<double3>(Allocator.Persistent); // projection scratch
            _symbolUps = new NativeList<float3>(Allocator.Persistent);
            _symbolScreen = new NativeList<float2>(Allocator.Persistent);
            _symbolDepth  = new NativeList<float>(Allocator.Persistent);
            _symbolValid  = new NativeList<byte>(Allocator.Persistent);

            _nSurvivors       = new NativeList<byte>(Allocator.Persistent); // collision survivor flags
            _gridCellHead     = new NativeList<int>(Allocator.Persistent);
            _gridNodeBox      = new NativeList<int>(Allocator.Persistent);
            _gridNodeNext     = new NativeList<int>(Allocator.Persistent);
            _survivorCountOut = new NativeArray<int>(1, Allocator.Persistent);

            // Burst-gather: the reusable block-view table + SymbolGatherJob's output counts.
            _gatherBlockViews = new NativeList<BlockView>(Allocator.Persistent);
            _gatherCounts = new NativeArray<int>(SymbolGatherJob.CountLength, Allocator.Persistent);

            // The Burst stage job's native buffers.
            _mirrorKinds = new NativeList<SymbolPlacementKind>(Allocator.Persistent);
            _mirrorDetail = new NativeList<int>(Allocator.Persistent);
            _mirrorWorldCount = new NativeList<int>(Allocator.Persistent);
            _mirrorPointQuadStart = new NativeList<int>(Allocator.Persistent);
            _mirrorPointQuadCount = new NativeList<int>(Allocator.Persistent);
            _mirrorCurvedGlyphStart = new NativeList<int>(Allocator.Persistent);
            _mirrorCurvedGlyphCount = new NativeList<int>(Allocator.Persistent);
            _mirrorCurvedAnchorStart = new NativeList<int>(Allocator.Persistent);
            _mirrorCurvedAnchorCount = new NativeList<int>(Allocator.Persistent);
            _mirrorCurvedAnchorFadeStart = new NativeList<int>(Allocator.Persistent);
            _mirrorPoints = new NativeList<PointStageInput>(Allocator.Persistent);
            _mirrorCurveds = new NativeList<CurvedStageInput>(Allocator.Persistent);
            _mirrorQuads = new NativeList<SymbolQuad>(Allocator.Persistent);
            _mirrorGlyphs = new NativeList<CurvedGlyph>(Allocator.Persistent);
            _mirrorAnchors = new NativeList<LineAnchor>(Allocator.Persistent);
            _mirrorFadeIds = new NativeList<long>(Allocator.Persistent);
            _mirrorWorldStart = new NativeList<int>(Allocator.Persistent);            // Stage-2 symbol-level fields
            _mirrorRepAnchor = new NativeList<double3>(Allocator.Persistent);
            _mirrorWorldPoints = new NativeList<double3>(Allocator.Persistent);
            _mirrorWorldUps = new NativeList<float3>(Allocator.Persistent);
            _mirrorSymbolDeparting = new NativeList<byte>(Allocator.Persistent);
            _mirrorSymbolCoverageFading = new NativeList<byte>(Allocator.Persistent);
            _mirrorSymbolDropped = new NativeList<byte>(Allocator.Persistent);
            _stagePointOffset = new NativeList<int>(Allocator.Persistent);
            _stageAnchorWasPlaced = new NativeList<byte>(Allocator.Persistent);
            _placedLastFrame = new NativeHashSet<long>(PlacedSetInitialCapacity, Allocator.Persistent);
            _droppedHalvesLastFrame = new NativeHashMap<long, byte>(DroppedHalvesInitialCapacity, Allocator.Persistent);
            _fadeOpacity = new NativeHashMap<long, float>(FadeMapInitialCapacity, Allocator.Persistent);
            _seenFade    = new NativeHashSet<long>(FadeMapInitialCapacity, Allocator.Persistent);
            _forceFadeOut = new NativeHashSet<long>(FadeMapInitialCapacity, Allocator.Persistent);
            _fadeSweepKeys = new NativeList<long>(FadeMapInitialCapacity, Allocator.Persistent);
            _gatherTrigger = new NativeList<GatherTrigger>(Allocator.Persistent);
            _slotVisibleThisFrame = new NativeList<bool>(Allocator.Persistent);
            _gatherCulledCounts = new NativeArray<int>((int)GatherTrigger.Dropped + 1, Allocator.Persistent);
            // _debugFadeIdSeen isn't allocated here — see its field doc; AssertFadeIdsUnique allocates it lazily.
            _stageBoxes = new NativeList<SymbolBox>(Allocator.Persistent);
            _stageQuads = new NativeList<PlacedQuad>(Allocator.Persistent);
            _stageCandidates = new NativeList<SymbolCandidate>(Allocator.Persistent);
            _stageEmit = new NativeList<CandidateEmit>(Allocator.Persistent);
            _stageCounts = new NativeArray<int>(4, Allocator.Persistent); // [3]=emitCount
            _stagePath = new NativeList<float2>(Allocator.Persistent);
            _stageCumulativeLength = new NativeList<float>(Allocator.Persistent);

            if (worldTextBase == null)
            {
                Debug.LogWarning("[SymbolPlacementSystem] no base symbol-text material (MapMaterialSet.SymbolTextWorld " +
                                 "unassigned) — labels will not render.");
            }
            else
            {
                _worldTextMaterial      = worldTextBase.CloneWithParent();
                _worldTextMaterial.name = "SymbolPlacementSystem_WorldTextMaterial";
            }

            // The world icon material follows the same null-tolerant pattern as worldTextBase above.
            if (worldIconBase != null)
            {
                _worldIconMaterial      = worldIconBase.CloneWithParent();
                _worldIconMaterial.name = "SymbolPlacementSystem_WorldIconMaterial";
            }
        }

        /// <summary>Production entry: ticks a per-frame <see cref="SymbolGatherPlan"/> — <see cref="GatherIntoMirror"/>
        /// compacts each winner into the native mirror, then project→stage→collide→emit runs off it.</summary>
        /// <param name="symbolLayers">Per-symbol-layer render layers, index == <see cref="ShapedSymbol.MaterialIndex"/>; null/empty draws through the fallback.</param>
        /// <param name="spriteTexture">The sprite sheet backing every icon's UVs; null means icons never build.</param>
        public void Tick(in SceneFrame frame, SymbolGatherPlan plan, GlyphAtlasTexture atlas,
            float deltaTime = float.PositiveInfinity, IReadOnlyList<SymbolRenderLayer> symbolLayers = null,
            Texture2D spriteTexture = null)
        {
            using (PmGather.Auto())
                GatherIntoMirror(plan); // sets _mirrorNonDroppedCount — read below, not plan.WinnerCount, which includes Dropped symbols
            TickCore(frame, atlas, deltaTime, symbolLayers, _mirrorNonDroppedCount, spriteTexture);
            RefreshTelemetry();   // after the pass, so the levels are this Tick's
        }


        // The per-frame core reads only the native mirror, never a managed source — split out from Tick.
        private void TickCore(in SceneFrame frame, GlyphAtlasTexture atlas,
            float deltaTime, IReadOnlyList<SymbolRenderLayer> symbolLayers, int inputSymbolCount,
            Texture2D spriteTexture = null)
        {
            TickCount++;
            LastInputSymbolCount = inputSymbolCount;

            using (PmTick.Auto())
            {
                // Completes the previous Tick's scheduled collision, re-keying survivors into _placedLastFrame.
                using (PmCollideHarvest.Auto())
                    HarvestCollision();

                double2 viewportLogicalPx = _camera.ViewportLogicalPx;
                // The world ruler for map-pitched curved symbols — converts device pixels to logical pixels once, here.
                float metresPerLogicalPixel = (float)(_camera.MetresPerDevicePixel * _camera.DevicePixelRatio);
                // text-halo-width/-blur are LOGICAL px on the emit; the SDF shader measures in DEVICE px.
                // Read once per Tick against the LIVE ratio, so a dpr change needs no re-bake anywhere.
                float haloDevicePixelRatio =
                    (float)DeviceScaling.LogicalToDevicePx(1.0, PixelSpace.Device, _camera.DevicePixelRatio);

                LastCandidateCount = 0;
                LastDistanceCulledCount = 0;
                LastDepartingCulledCount = 0;
                LastHorizonCulledCount = 0;
                LastCoverageFadingCulledCount = 0;
                LastZoomCulledCount = 0;

                WorldRenderer.BeginFrame(); // clear every live world slot's accumulators

                int totalQuads = 0;

                // Gate on the EFFECTIVE non-Dropped count (see _mirrorNonDroppedCount's field doc).
                if (_mirrorNonDroppedCount > 0 && atlas?.Texture != null && _worldTextMaterial != null)
                {
                    float4x4 viewProj = ViewProj(_camera.Camera);
                    double3 sceneOriginRender = frame.SceneOriginRender;
                    float3x3 rebase = frame.Rebase;

                    // Globe horizon-cull params, built once per Tick — on a planar projection occ is false, so HorizonCull is a no-op.
                    bool occ = _camera.Projection.TryGetHorizonOccluder(out double3 occCentre, out double occRadius);
                    double3 cameraRelative = frame.CameraRelativePosition;
                    double  globeRadiusSq  = occ ? occRadius * occRadius : -1.0;

                    // Pre-projection distance-cull threshold, from CurrentFarMetres so it works before SyncToCamera runs this frame.
                    double symbolCullDistance = SymbolMaxDistanceFraction * _camera.CurrentFarMetres;

                    // Map bearing drives text-translate-anchor:map/text-rotation-alignment:map; zero for a north-up map.
                    float bearingRadians = (float)_camera.CurrentProperties.Heading.Radians;

                    // (1) Project and stage every symbol into the unified native pools (the Burst StageJob).
                    int candidateCount = 0, boxCount = 0;
                    using (PmProject.Auto())
                    {
                        // Gathers every un-culled symbol's world point — O(input), since it culls even when everything is culled.
                        using (PmGatherPoints.Auto())
                        {
                            // A Dropped tile's symbols stay resident and get hard-skipped below; a Fading tile's still gather.
                            GatherSymbolPoints(sceneOriginRender, symbolCullDistance, rebase, cameraRelative, occCentre, globeRadiusSq,
                                               symbolLayers, _camera.CurrentProperties.Zoom);
                        }

                        // Projects only the symbols the scan kept, via the parallel Burst SymbolProjectionJob — free when fully culled.
                        using (PmProjectPositions.Auto())
                            ProjectSymbols(sceneOriginRender, viewProj, viewportLogicalPx, rebase);

                        using (PmStage.Auto())
                        {
                            // Stages the whole mirror in one Burst job (StageJob); its output feeds collision and emit directly.
                            PreSizeStageOutputs();
                            // The collision box is the screen AABB of the glyph's four projected world corners.
                            var view = new SymbolViewTransform
                            {
                                SceneOriginRender = sceneOriginRender,
                                Rebase            = rebase,
                                ViewProj          = viewProj,
                                ViewportLogicalPx = viewportLogicalPx,
                            };
                            RunStageJob(bearingRadians, viewportLogicalPx, metresPerLogicalPixel, in view);
                            candidateCount = _stageCounts[0]; boxCount = _stageCounts[1];
                            LastBoxCount = boxCount;
                        }
                    }

                    // Diagnostic (opt-in): on an armed capture, dumps this frame's input symbols by style layer and screen band.
                    if (_breakdownRequested)
                        CaptureSymbolBreakdown(symbolLayers, viewportLogicalPx);

                    // Marks out-of-zoom candidates Suppressed before collision, so they neither win nor block the true winner.
                    ApplySuppression(candidateCount, symbolLayers, _camera.CurrentProperties.Zoom);

                    LastCandidateCount = candidateCount;

                    // (2) Fade using last tick's verdict, then emit — staging order, since the collision sort below runs after.
                    using (PmEmit.Auto())
                    {
                        using (PmEmitLoop.Auto())
                        {
                            _seenFade.Clear();
                            for (int s = 0; s < candidateCount; s++)
                            {
                                SymbolCandidate cand = _stageCandidates[s]; // staging order — the collision job hasn't sorted yet
                                long fadeId = cand.FadeId;

                                // WasPlacedLastFrame comes pre-resolved from StageJob — safe since HarvestCollision, the sole writer, ran first.
                                bool placed = cand.WasPlacedLastFrame;

                                // Skipping here matches what EaseFade would do anyway: ease 0 toward 0 and return 0.
                                if (!placed && !_fadeOpacity.ContainsKey(fadeId)) continue;

                                // Suppressed is evaluated live this frame, so the display gate stays same-frame.
                                bool show = placed && !cand.Suppressed && !_forceFadeOut.Contains(fadeId);
                                float opacity = EaseFade(fadeId, show ? 1f : 0f, deltaTime);
                                if (opacity <= FadeEpsilon) continue;

                                // A pair's icon+text share EmitCount == 2 and fade as one unit; this skips only the half last tick dropped.
                                byte droppedHalves = cand.DroppedBoxMask;
                                for (int e = cand.EmitStart, eEnd = cand.EmitStart + cand.EmitCount; e < eEnd; e++)
                                {
                                    if (droppedHalves != 0 && (droppedHalves & (1 << (e - cand.EmitStart))) != 0) continue;
                                    CandidateEmit emit = _stageEmit[e];
                                    totalQuads += WorldRenderer.Emit(in emit, _stageQuads.AsArray(), opacity,
                                        haloDevicePixelRatio);
                                }
                            }
                        }

                        using (PmEmitDecay.Auto())
                            DecayUnseenFadeSymbols(deltaTime);
                    }

                    // (3) Schedule collision last — the job sorts _stageCandidates in place, so nothing after this may read it.
                    using (PmCollide.Auto())
                        ScheduleCollision(candidateCount, boxCount);
                }

                LastQuadCount = totalQuads;

                // Builds and places every non-empty world slot, hiding the rest, since a prior Tick may have left slots visible.
                WorldRenderer.EndFrame(in frame, symbolLayers, _worldTextMaterial, _worldIconMaterial,
                    atlas?.Texture, spriteTexture, viewportLogicalPx);
            }
        }

        // Flattens every un-culled symbol's world point into _symbolPoints; _stagePointOffset holds each start (-1 = culled).
        private void GatherSymbolPoints(double3 sceneOriginRender, double symbolCullDistance,
            in float3x3 rebase, double3 cameraRelative, double3 globeCentreRelative, double globeRadiusSq,
            IReadOnlyList<SymbolRenderLayer> symbolLayers, double zoom)
        {
            _stagePointOffset.ResizeUninitialized(_mirrorCount);
            _symbolPoints.Clear();
            _symbolUps.Clear();
            _forceFadeOut.Clear();

            // Resolves the per-frame zoom gate once per slot — no layer list means not gated.
            int slotCount = symbolLayers?.Count ?? 0;
            if (_slotVisibleThisFrame.Length < slotCount)
                _slotVisibleThisFrame.Resize(slotCount, NativeArrayOptions.UninitializedMemory);
            for (int s = 0; s < slotCount; s++)
            {
                var sl = symbolLayers[s]?.StyleLayer;
                _slotVisibleThisFrame[s] = sl == null || sl.IsVisibleAtZoom(zoom);
            }

            _gatherTrigger.ResizeUninitialized(_mirrorCount);

            // Pass 1 — Cull: priority dropped → departing → coverage → zoom → horizon → distance.
            using (PmGatherCull.Auto())
                new CullJob
                {
                    SymbolDropped = _mirrorSymbolDropped.AsArray(), SymbolDeparting = _mirrorSymbolDeparting.AsArray(),
                    SymbolCoverageFading = _mirrorSymbolCoverageFading.AsArray(),
                    RepAnchor = _mirrorRepAnchor.AsArray(), Kinds = _mirrorKinds.AsArray(), Detail = _mirrorDetail.AsArray(),
                    Points = _mirrorPoints.AsArray(), Curveds = _mirrorCurveds.AsArray(),
                    SlotVisible = _slotVisibleThisFrame.AsArray(),
                    SceneOriginRender = sceneOriginRender, Rebase = rebase, CameraRelative = cameraRelative,
                    GlobeCentreRelative = globeCentreRelative, GlobeRadiusSq = globeRadiusSq,
                    SymbolCullDistance = symbolCullDistance, SlotCount = slotCount,
                    OutTrigger = _gatherTrigger.AsArray(),
                }.Run(_mirrorCount);

            // Pass 2 — Compact: a fade-alive triggered symbol keeps staging via _forceFadeOut. The running offset makes this IJob, not parallel.
            using (PmGatherCompact.Auto())
            {
                // _gatherCulledCounts is persistent scratch, the bridge since a job can't write managed properties; zeroed each call.
                for (int i = 0; i < _gatherCulledCounts.Length; i++) _gatherCulledCounts[i] = 0;
                new CompactJob
                {
                    Trigger = _gatherTrigger.AsArray(),
                    Kinds = _mirrorKinds.AsArray(), Detail = _mirrorDetail.AsArray(),
                    PointDetails = _mirrorPoints.AsArray(),
                    CurvedAnchorFadeStart = _mirrorCurvedAnchorFadeStart.AsArray(),
                    CurvedAnchorCount = _mirrorCurvedAnchorCount.AsArray(),
                    FadeIds = _mirrorFadeIds.AsArray(),
                    WorldStart = _mirrorWorldStart.AsArray(), WorldCount = _mirrorWorldCount.AsArray(),
                    WorldPoints = _mirrorWorldPoints.AsArray(), WorldUps = _mirrorWorldUps.AsArray(),
                    FadeOpacity = _fadeOpacity, FadeEpsilon = FadeEpsilon, Count = _mirrorCount,
                    StageOffset = _stagePointOffset.AsArray(),
                    OutPoints = _symbolPoints, OutUps = _symbolUps, ForceFadeOut = _forceFadeOut,
                    Counts = _gatherCulledCounts,
                }.Run();

                LastDepartingCulledCount      += _gatherCulledCounts[(int)GatherTrigger.Departing];
                LastCoverageFadingCulledCount += _gatherCulledCounts[(int)GatherTrigger.Coverage];
                LastZoomCulledCount            += _gatherCulledCounts[(int)GatherTrigger.Zoom];
                LastHorizonCulledCount         += _gatherCulledCounts[(int)GatherTrigger.Horizon];
                LastDistanceCulledCount        += _gatherCulledCounts[(int)GatherTrigger.Distance];
            }
        }

        // Projects the gathered _symbolPoints to screen/depth/valid via the Burst SymbolProjectionJob, run inline.
        private void ProjectSymbols(double3 sceneOriginRender, in float4x4 viewProj, double2 viewportLogicalPx, in float3x3 rebase)
        {
            int total = _symbolPoints.Length;
            _symbolScreen.Resize(total, NativeArrayOptions.UninitializedMemory);
            _symbolDepth.Resize(total,  NativeArrayOptions.UninitializedMemory);
            _symbolValid.Resize(total,  NativeArrayOptions.UninitializedMemory);
            if (total == 0) return;

            // .Run() executes inline — the caller blocks here regardless, since staging reads the output immediately.
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

        // Completes last tick's collision, re-keying survivors by FadeId into _placedLastFrame. This filter
        // must match the emit loop's exact expression from that frame.
        private void HarvestCollision()
        {
            if (_collisionHandle is not { } scheduled)
            {
                // No collision in flight means last Tick produced no verdict — same state as an empty-build clear.
                _placedLastFrame.Clear();
                _droppedHalvesLastFrame.Clear();
                LastSurvivorCount = 0;
                return;
            }

            scheduled.Complete();
            _collisionHandle = null;

            _placedLastFrame.Clear();
            _droppedHalvesLastFrame.Clear();
            for (int s = 0; s < _pendingCandidateCount; s++)
            {
                if (_nSurvivors[s] == 0) continue;
                SymbolCandidate candidate = _stageCandidates[s];
                long fadeId = candidate.FadeId;
                if (_forceFadeOut.Contains(fadeId)) continue;
                _placedLastFrame.Add(fadeId);
                // A survivor missing an optional half is labeled here, read back by next Tick's staging for the emit gate.
                if (candidate.DroppedBoxMask != 0) _droppedHalvesLastFrame[fadeId] = candidate.DroppedBoxMask;
            }
            LastSurvivorCount = _survivorCountOut[0];
        }

        // Runs collision as a Burst job over the stage output, scheduled but not completed here — Complete moves
        // to next Tick's harvest, so the main thread never blocks. Outputs are read by that next Tick, not this one.
        private void ScheduleCollision(int candidateCount, int boxCount)
        {
            if (candidateCount <= 0) return; // nothing pending ⇒ next Tick's harvest reads "no verdict"

            _nSurvivors.Resize(candidateCount, NativeArrayOptions.UninitializedMemory);
            NativeArray<SymbolCandidate> nc = _stageCandidates.AsArray(); // stage job's candidates — sorted IN PLACE here
            NativeArray<SymbolBox>       nb = _stageBoxes.AsArray();       // stage job's boxes (read-only; grid over [0,boxCount))

            // Debug-only: catches a malformed staged stream here, with the offending candidate.
            AssertCandidateRangesTile(nc, candidateCount, boxCount);
            // FadeId is the display key (HarvestCollision re-keys by it) — see SymbolCandidate.FadeId's uniqueness contract.
            AssertFadeIdsUnique(nc, candidateCount);

            // Pre-sizes the uniform grid; the node bound is counted per candidate box-reference so it can't under-count.
            CollisionGridSizing.Dims dims = CollisionGridSizing.ComputeDims(nb, boxCount);
            int cells   = dims.W * dims.H;
            int nodeCap = math.max(1, CollisionGridSizing.NodeUpperBoundByCandidates(nc, candidateCount, nb, boxCount, in dims));
            _gridCellHead.Resize(cells,   NativeArrayOptions.UninitializedMemory);
            _gridNodeBox.Resize(nodeCap,  NativeArrayOptions.UninitializedMemory);
            _gridNodeNext.Resize(nodeCap, NativeArrayOptions.UninitializedMemory);
            NativeArray<int> cellHead = _gridCellHead.AsArray();
            for (int c = 0; c < cells; c++) cellHead[c] = -1;

            _collisionHandle = new CollisionJob
            {
                Candidates     = nc, CandidateCount = candidateCount,
                Boxes          = nb, BoxCount = boxCount,
                Survivors      = _nSurvivors.AsArray(), OutSurvivorCount = _survivorCountOut,
                CellHead       = cellHead, NodeBox = _gridNodeBox.AsArray(), NodeNext = _gridNodeNext.AsArray(),
                GridMinX       = dims.MinX, GridMinY = dims.MinY, GridInvCell = dims.InvCell,
                GridW          = dims.W, GridH = dims.H,
            }.Schedule();
            // Without this the job may not reach a worker until the next sync point, defeating the point of deferring.
            JobHandle.ScheduleBatchedJobs();

            _pendingCandidateCount = candidateCount;
        }

        // Debug-only. FadeId is the display key: two candidates sharing an id would both show when one wins.
        // Limitation: it checks one frame only, not reuse across frames (see SymbolDeferredCollisionTests).
        [System.Diagnostics.Conditional("UNITY_ASSERTIONS")]
        private void AssertFadeIdsUnique(NativeArray<SymbolCandidate> candidates, int candidateCount)
        {
            // 64, not PlacedSetInitialCapacity's 16384: Clear() keeps grown capacity, so this warms itself after one frame.
            if (!_debugFadeIdSeen.IsCreated) _debugFadeIdSeen = new NativeHashSet<long>(64, Allocator.Persistent);
            _debugFadeIdSeen.Clear();
            for (int i = 0; i < candidateCount; i++)
            {
                SymbolCandidate c = candidates[i];
                if (!_debugFadeIdSeen.Add(c.FadeId))
                {
                    UnityEngine.Debug.LogAssertion(
                        $"[SymbolPlacementSystem] candidate[{i}] (SymbolIndex={c.SymbolIndex}, FeatureIndex={c.FeatureIndex}, " +
                        $"TileKey={c.TileKey}) shares FadeId={c.FadeId} with an earlier candidate this frame — " +
                        "SymbolCandidate.FadeId must be unique per live candidate (R3 promotes it to the display key); " +
                        "the loser will incorrectly show alongside the winner.");
                    return;
                }
            }
        }

        // Debug-only. Asserts the staged stream tiles [0,boxCount) contiguously, reported with the offending candidate.
        [System.Diagnostics.Conditional("UNITY_ASSERTIONS")]
        private static void AssertCandidateRangesTile(NativeArray<SymbolCandidate> candidates, int candidateCount, int boxCount)
        {
            if (!SymbolCandidate.TryFindRangeTilingViolation(candidates.AsSpan().Slice(0, candidateCount), boxCount,
                    out int i, out int expectedStart))
                return;

            if (i < candidateCount)
            {
                SymbolCandidate c = candidates[i];
                UnityEngine.Debug.LogAssertion(
                    $"[SymbolCollision] staged candidate box-ranges must tile [0,{boxCount}) contiguously, but " +
                    $"candidate[{i}] has BoxStart={c.BoxStart} BoxCount={c.BoxCount} (expected BoxStart={expectedStart}, " +
                    $"SymbolIndex={c.SymbolIndex}) — the never-reproduced overlap/over-range source; capture this frame.");
            }
            else
            {
                UnityEngine.Debug.LogAssertion(
                    $"[SymbolCollision] staged candidate box-ranges cover only [0,{expectedStart}) of {boxCount} boxes " +
                    "— a gap/undercount in the staged stream; capture this frame.");
            }
        }

        // Debug-only. Every mirror Owner must be immediately followed by its Rider, and every Rider by its Owner.
        [System.Diagnostics.Conditional("UNITY_ASSERTIONS")]
        private void AssertPairAdjacency()
        {
            NativeArray<PointStageInput> points = _mirrorPoints.AsArray();
            for (int d = 0; d < _mirrorPointCount; d++)
            {
                SymbolPairRole role = points[d].PairRole;
                if (role == SymbolPairRole.Owner)
                {
                    bool riderFollows = d + 1 < _mirrorPointCount && points[d + 1].PairRole == SymbolPairRole.Rider;
                    if (!riderFollows)
                        UnityEngine.Debug.LogAssertion(
                            $"[SymbolPlacementSystem] mirror point[{d}] is a pair Owner with no Rider immediately " +
                            "after it — StageJob degrades it to a lone badge (safe), but the reconciler's " +
                            "owner->rider adjacency contract was broken upstream; capture this frame.");
                }
                else if (role == SymbolPairRole.Rider)
                {
                    bool ownerPrecedes = d > 0 && points[d - 1].PairRole == SymbolPairRole.Owner;
                    if (!ownerPrecedes)
                        UnityEngine.Debug.LogAssertion(
                            $"[SymbolPlacementSystem] mirror point[{d}] is a pair Rider with no Owner immediately " +
                            "before it — an orphan rider; StageJob skips staging it (safe), but the reconciler's " +
                            "adjacency contract was broken upstream; capture this frame.");
                }
            }
        }

        // sRGB→linear + folded opacity, the vertex color the shader emits directly; alpha isn't gamma-encoded.
        internal static float4 LinearColor(in SymbolPaint paint)
        {
            float4 srgb   = paint.TextColor;
            Color  linear = new Color(srgb.x, srgb.y, srgb.z, 1f).linear;
            return new float4(linear.r, linear.g, linear.b, srgb.w * paint.Opacity);
        }

        // The halo sibling of LinearColor. text-opacity is NOT folded in: it already rides the
        // quad's own colour, which the emit multiplies this alpha onto, so folding it here applies it twice.
        internal static float4 LinearHaloColor(in SymbolPaint paint)
        {
            float4 srgb   = paint.HaloColor;
            Color  linear = new Color(srgb.x, srgb.y, srgb.z, 1f).linear;
            return new float4(linear.r, linear.g, linear.b, srgb.w);
        }

        // Demo path / out-of-range material index → default slot 0.
        internal static int ClampSlot(int slot, int slotCount) => (slot < 0 || slot >= slotCount) ? 0 : slot;


        // Fills the native mirror from the per-frame winner plan; record order matches plan order, so the mirror is byte-identical to Build.
        internal void GatherIntoMirror(SymbolGatherPlan plan)
        {
            // Same source and version means every pool below is already correct; the count check is a release-build backstop.
            bool sameSourceAndVersion = plan != null && ReferenceEquals(plan, _mirrorPlan)
                                         && plan.WinnerSetVersion == _mirrorVersion;
            if (sameSourceAndVersion)
            {
                AssertMemoPlanMatchesMirror(plan); // debug-only — fires when the key says same-set but the count disagrees
                if (plan.WinnerCount == _mirrorCount)
                {
                    WritePerFrameMasks(plan);             // the ONLY per-frame work on a held mirror
                    return;
                }
            }

            MirrorRebuildCount++;
            int winners = plan?.WinnerCount ?? 0;

            // The three per-frame masks are WritePerFrameMasks' inputs, resized here on the main thread.
            _mirrorSymbolDeparting.ResizeUninitialized(winners); _mirrorSymbolCoverageFading.ResizeUninitialized(winners);
            _mirrorSymbolDropped.ResizeUninitialized(winners);

            if (winners == 0)
            {
                // Explicit branch avoids dispatching a job over an empty view table or a null plan.Blocks/BlockCount.
                _mirrorKinds.ResizeUninitialized(0); _mirrorDetail.ResizeUninitialized(0);
                _mirrorWorldCount.ResizeUninitialized(0); _mirrorWorldStart.ResizeUninitialized(0);
                _mirrorRepAnchor.ResizeUninitialized(0);
                _mirrorPoints.ResizeUninitialized(0); _mirrorPointQuadStart.ResizeUninitialized(0); _mirrorPointQuadCount.ResizeUninitialized(0);
                _mirrorCurveds.ResizeUninitialized(0);
                _mirrorCurvedGlyphStart.ResizeUninitialized(0); _mirrorCurvedGlyphCount.ResizeUninitialized(0);
                _mirrorCurvedAnchorStart.ResizeUninitialized(0); _mirrorCurvedAnchorCount.ResizeUninitialized(0);
                _mirrorCurvedAnchorFadeStart.ResizeUninitialized(0);
                _mirrorQuads.ResizeUninitialized(0); _mirrorGlyphs.ResizeUninitialized(0);
                _mirrorAnchors.ResizeUninitialized(0); _mirrorFadeIds.ResizeUninitialized(0);
                _mirrorWorldPoints.ResizeUninitialized(0); _mirrorWorldUps.ResizeUninitialized(0);

                _mirrorPointCount = 0; _mirrorCurvedCount = 0;
                _mirrorQuadCount = 0; _mirrorGlyphCount = 0; _mirrorAnchorCount = 0; _mirrorFadeCount = 0; _mirrorWorldPointCount = 0;
                _mirrorMaxBoxes = 0; _mirrorMaxQuads = 0; _mirrorMaxCandidates = 0;
            }
            else
            {
                BuildBlockViews(plan); // main-thread, alloc-free (see its own doc) — the per-frame view table

                // Line-for-line Burst transliteration of the two managed loops this replaces — see SymbolGatherJob's own doc.
                new SymbolGatherJob
                {
                    BlockViews = _gatherBlockViews.AsArray(),
                    BlockId = plan.BlockId.AsArray(),
                    LocalIndex = plan.LocalIndex.AsArray(),
                    WinnerCount = winners,
                    MKinds = _mirrorKinds, MDetail = _mirrorDetail, MWorldCount = _mirrorWorldCount, MWorldStart = _mirrorWorldStart,
                    MRepAnchor = _mirrorRepAnchor,
                    MPoints = _mirrorPoints, MPointQuadStart = _mirrorPointQuadStart, MPointQuadCount = _mirrorPointQuadCount,
                    MCurveds = _mirrorCurveds,
                    MCurvedGlyphStart = _mirrorCurvedGlyphStart, MCurvedGlyphCount = _mirrorCurvedGlyphCount,
                    MCurvedAnchorStart = _mirrorCurvedAnchorStart, MCurvedAnchorCount = _mirrorCurvedAnchorCount,
                    MCurvedAnchorFadeStart = _mirrorCurvedAnchorFadeStart,
                    MQuads = _mirrorQuads, MGlyphs = _mirrorGlyphs, MAnchors = _mirrorAnchors, MFadeIds = _mirrorFadeIds,
                    MWorldPoints = _mirrorWorldPoints, MWorldUps = _mirrorWorldUps,
                    OutCounts = _gatherCounts,
                }.Run();

                _mirrorPointCount = _gatherCounts[SymbolGatherJob.CountPoint];
                _mirrorCurvedCount = _gatherCounts[SymbolGatherJob.CountCurved];
                _mirrorQuadCount = _gatherCounts[SymbolGatherJob.CountQuad];
                _mirrorGlyphCount = _gatherCounts[SymbolGatherJob.CountGlyph];
                _mirrorAnchorCount = _gatherCounts[SymbolGatherJob.CountAnchor];
                _mirrorFadeCount = _gatherCounts[SymbolGatherJob.CountFade];
                _mirrorWorldPointCount = _gatherCounts[SymbolGatherJob.CountWorldPoint];
                _mirrorMaxBoxes = _gatherCounts[SymbolGatherJob.CountMaxBoxes];
                _mirrorMaxQuads = _gatherCounts[SymbolGatherJob.CountMaxQuads];
                _mirrorMaxCandidates = _gatherCounts[SymbolGatherJob.CountMaxCandidates];

                // Debug-only, rebuild path only — a miss here means something between bake and gather broke adjacency.
                AssertPairAdjacency();
            }

            _mirrorCount = winners; // set before WritePerFrameMasks, which bounds copies on this frame's count

            // The per-frame masks + _mirrorNonDroppedCount are the only per-frame inputs, written by one shared writer.
            WritePerFrameMasks(plan);

            // Stamps the shared source key so a later same-plan-and-version Tick memo-hits above; anything else rebuilds.
            _mirrorPlan = plan; _mirrorVersion = plan?.WinnerSetVersion ?? long.MinValue;
        }

        // Rebuilds the reusable view table from plan.Blocks — non-owning pointer views over each block's own array.
        // GetUnsafeReadOnlyPtr checks safety per call, so a disposed block throws here, not silently in the job.
        private unsafe void BuildBlockViews(SymbolGatherPlan plan)
        {
            _gatherBlockViews.ResizeUninitialized(plan.BlockCount);
            for (int b = 0; b < plan.BlockCount; b++)
            {
                SymbolTileBlock block = plan.Blocks[b];
                _gatherBlockViews[b] = new BlockView
                {
                    Kinds = new UnsafeList<SymbolPlacementKind>((SymbolPlacementKind*)NativeArrayUnsafeUtility.GetUnsafeReadOnlyPtr(block.Kinds), block.Kinds.Length),
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
                    WorldUps = new UnsafeList<float3>((float3*)NativeArrayUnsafeUtility.GetUnsafeReadOnlyPtr(block.WorldUps), block.WorldUps.Length),
                    AnchorFadeIds = new UnsafeList<long>((long*)NativeArrayUnsafeUtility.GetUnsafeReadOnlyPtr(block.AnchorFadeIds), block.AnchorFadeIds.Length),
                };
            }
        }

        // The only per-frame inputs to the mirror — everything else the gather writes is a pure function of
        // the winner set. A length mismatch here is unsafe, which is why GatherIntoMirror's count check is non-optional.
        private void WritePerFrameMasks(SymbolGatherPlan plan)
        {
            if (plan == null || _mirrorCount == 0) { _mirrorNonDroppedCount = 0; return; } // clean no-op (incl. the null-plan heavy path)
            NativeArray<byte>.Copy(plan.Departing.AsArray(), 0, _mirrorSymbolDeparting.AsArray(), 0, _mirrorCount);
            NativeArray<byte>.Copy(plan.CoverageFading.AsArray(), 0, _mirrorSymbolCoverageFading.AsArray(), 0, _mirrorCount);
            NativeArray<byte>.Copy(plan.Dropped.AsArray(), 0, _mirrorSymbolDropped.AsArray(), 0, _mirrorCount);
            _mirrorNonDroppedCount = _mirrorCount - plan.DroppedCount; // TickCore gates on this, not _mirrorCount
        }

        // Debug-only. A fire means the memo key said "same set" but the count disagrees — a future site landed without bumping WinnerSetVersion.
        [System.Diagnostics.Conditional("UNITY_ASSERTIONS")]
        private void AssertMemoPlanMatchesMirror(SymbolGatherPlan plan)
        {
            if (plan.WinnerCount == _mirrorCount) return;
            UnityEngine.Debug.LogAssertion(
                $"[SymbolPlacementSystem] gather memo key matched (same plan, WinnerSetVersion={plan.WinnerSetVersion}) " +
                $"but WinnerCount={plan.WinnerCount} != mirrored count={_mirrorCount} — a front-content change did not " +
                "bump WinnerSetVersion; falling back to a full rebuild this frame.");
        }

        private static void Mirror<T>(NativeList<T> dst, T[] src, int count) where T : unmanaged
        {
            dst.ResizeUninitialized(count);
            for (int i = 0; i < count; i++) dst[i] = src[i];
        }


        private static void MirrorBool(NativeList<byte> dst, bool[] src, int count)
        {
            dst.ResizeUninitialized(count);
            for (int i = 0; i < count; i++) dst[i] = (byte)(src[i] ? 1 : 0);
        }

        // Zero-fill a symbol-level byte mask that has no per-symbol source to copy from.
        private static void ClearBytes(NativeList<byte> dst, int count)
        {
            dst.ResizeUninitialized(count);
            for (int i = 0; i < count; i++) dst[i] = 0;
        }

        /// <summary>Test seam (via <c>InternalsVisibleTo</c>): materializes the private native mirror into
        /// <paramref name="dest"/>'s managed SoA, so a test can assert gather matches an independent oracle.</summary>
        internal void CopyMirrorInto(SymbolBatch dest)
        {
            dest.Kinds = new SymbolPlacementKind[_mirrorCount];
            for (int i = 0; i < _mirrorCount; i++) dest.Kinds[i] = _mirrorKinds[i];
            dest.Detail = ToArray(_mirrorDetail, _mirrorCount);
            dest.WorldStart = ToArray(_mirrorWorldStart, _mirrorCount);
            dest.WorldCount = ToArray(_mirrorWorldCount, _mirrorCount);
            dest.RepAnchor = ToArray(_mirrorRepAnchor, _mirrorCount);
            dest.SymbolDeparting = ToBoolArray(_mirrorSymbolDeparting, _mirrorCount);
            dest.SymbolCoverageFading = ToBoolArray(_mirrorSymbolCoverageFading, _mirrorCount);
            dest.Count = _mirrorCount;

            dest.Points = ToArray(_mirrorPoints, _mirrorPointCount);
            dest.PointQuadStart = ToArray(_mirrorPointQuadStart, _mirrorPointCount);
            dest.PointQuadCount = ToArray(_mirrorPointQuadCount, _mirrorPointCount);
            dest.PointCount = _mirrorPointCount;

            dest.Curveds = ToArray(_mirrorCurveds, _mirrorCurvedCount);
            dest.CurvedGlyphStart = ToArray(_mirrorCurvedGlyphStart, _mirrorCurvedCount);
            dest.CurvedGlyphCount = ToArray(_mirrorCurvedGlyphCount, _mirrorCurvedCount);
            dest.CurvedAnchorStart = ToArray(_mirrorCurvedAnchorStart, _mirrorCurvedCount);
            dest.CurvedAnchorCount = ToArray(_mirrorCurvedAnchorCount, _mirrorCurvedCount);
            dest.CurvedAnchorFadeStart = ToArray(_mirrorCurvedAnchorFadeStart, _mirrorCurvedCount);
            dest.CurvedCount = _mirrorCurvedCount;

            dest.Quads = ToArray(_mirrorQuads, _mirrorQuadCount); dest.QuadCount = _mirrorQuadCount;
            dest.Glyphs = ToArray(_mirrorGlyphs, _mirrorGlyphCount); dest.GlyphCount = _mirrorGlyphCount;
            dest.Anchors = ToArray(_mirrorAnchors, _mirrorAnchorCount); dest.AnchorCount = _mirrorAnchorCount;
            dest.WorldPoints = ToArray(_mirrorWorldPoints, _mirrorWorldPointCount); dest.WorldPointCount = _mirrorWorldPointCount;
            dest.WorldUps = ToArray(_mirrorWorldUps, _mirrorWorldPointCount); dest.WorldUpCount = _mirrorWorldPointCount;
            dest.AnchorFadeIds = ToArray(_mirrorFadeIds, _mirrorFadeCount); dest.AnchorFadeCount = _mirrorFadeCount;

            dest.MaxBoxes = _mirrorMaxBoxes; dest.MaxQuads = _mirrorMaxQuads; dest.MaxCandidates = _mirrorMaxCandidates;
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

        // Pre-sizes the job's output pools to the mirror's worst case, since Burst can't grow them.
        private void PreSizeStageOutputs()
        {
            _stageBoxes.ResizeUninitialized(math.max(1, _mirrorMaxBoxes));
            _stageQuads.ResizeUninitialized(math.max(1, _mirrorMaxQuads));
            _stageCandidates.ResizeUninitialized(math.max(1, _mirrorMaxCandidates));
            _stageEmit.ResizeUninitialized(math.max(1, _mirrorMaxCandidates));
            int pathCapacity = math.max(1, _symbolPoints.Length);
            _stagePath.ResizeUninitialized(pathCapacity);
            _stageCumulativeLength.ResizeUninitialized(pathCapacity);
            // No math.max(1, …) floor here: a zero-length _mirrorFadeCount is fine, since the fill loop and fade-id slice are both empty then.
            _stageAnchorWasPlaced.ResizeUninitialized(_mirrorFadeCount);
        }

        private void RunStageJob(float bearingRadians, double2 viewportLogicalPx, float metresPerLogicalPixel,
            in SymbolViewTransform view)
        {
            new StageJob
            {
                Kinds = _mirrorKinds.AsArray(), Detail = _mirrorDetail.AsArray(), WorldCount = _mirrorWorldCount.AsArray(), Count = _mirrorCount,
                Points = _mirrorPoints.AsArray(), PointQuadStart = _mirrorPointQuadStart.AsArray(), PointQuadCount = _mirrorPointQuadCount.AsArray(),
                Curveds = _mirrorCurveds.AsArray(),
                CurvedGlyphStart = _mirrorCurvedGlyphStart.AsArray(), CurvedGlyphCount = _mirrorCurvedGlyphCount.AsArray(),
                CurvedAnchorStart = _mirrorCurvedAnchorStart.AsArray(), CurvedAnchorCount = _mirrorCurvedAnchorCount.AsArray(),
                CurvedAnchorFadeStart = _mirrorCurvedAnchorFadeStart.AsArray(),
                Quads = _mirrorQuads.AsArray(), Glyphs = _mirrorGlyphs.AsArray(), Anchors = _mirrorAnchors.AsArray(), AnchorFadeIds = _mirrorFadeIds.AsArray(),
                PointOffset = _stagePointOffset.AsArray(),
                Screen = _symbolScreen.AsArray(), Depth = _symbolDepth.AsArray(), Valid = _symbolValid.AsArray(),
                WorldPointsRender = _symbolPoints.AsArray(), WorldUpsRender = _symbolUps.AsArray(), // the same polyline Screen was projected from
                AnchorWasPlaced = _stageAnchorWasPlaced.AsArray(), Placed = _placedLastFrame.AsReadOnly(),
                DroppedHalves = _droppedHalvesLastFrame.AsReadOnly(),
                Bearing = bearingRadians, Viewport = viewportLogicalPx,
                MetresPerLogicalPixel = metresPerLogicalPixel, // per-frame ruler, patched per curved symbol
                View = view,                                  // per-frame view transform (curved arm only)
                PathPoints = _stagePath.AsArray(), CumulativeLengths = _stageCumulativeLength.AsArray(),
                Boxes = _stageBoxes.AsArray(), StagedQuads = _stageQuads.AsArray(),
                Candidates = _stageCandidates.AsArray(), Emit = _stageEmit.AsArray(), OutCounts = _stageCounts,
            }.Run();
        }

        // ── Fade helpers ──
        // Moves this identity's opacity one deltaTime step toward target; deltaTime == +inf snaps straight to it.
        private float EaseFade(long fadeId, float target, float deltaTime)
        {
            bool had = _fadeOpacity.TryGetValue(fadeId, out float current); // absent ≡ 0 (`current` defaults to 0)
            float step = deltaTime / FadeDurationSeconds;
            float next = target > current ? math.min(current + step, target) : math.max(current - step, target);

            // Dropping here only fires on a fade-out — without that guard, a fade-in below epsilon would restart from 0 every frame.
            if (next <= FadeEpsilon && target <= current)
            {
                if (had) _fadeOpacity.Remove(fadeId);
                return next; // <= epsilon ⇒ the caller skips it; nothing is emitted
            }

            _fadeOpacity[fadeId] = next;
            _seenFade.Add(fadeId); // see the field doc: seen ⟺ stored, so the decay sweep can trust it
            return next;
        }

        // Symbols not staged this frame decay toward 0 and are dropped once invisible, keeping the map bounded.
        private void DecayUnseenFadeSymbols(float deltaTime)
        {
            float step = deltaTime / FadeDurationSeconds;
            _fadeSweepKeys.Clear();
            // NativeHashMap has no .Keys collection; enumeration here is read-only, with removals deferred into _fadeSweepKeys.
            foreach (var kv in _fadeOpacity)
                if (!_seenFade.Contains(kv.Key)) _fadeSweepKeys.Add(kv.Key);
            for (int i = 0; i < _fadeSweepKeys.Length; i++)
            {
                long id = _fadeSweepKeys[i];
                float next = math.max(_fadeOpacity[id] - step, 0f);
                if (next <= FadeEpsilon) _fadeOpacity.Remove(id);
                else _fadeOpacity[id] = next;
            }
        }

        // Marks each candidate Suppressed before collision when its layer is out of the live camera zoom.
        private void ApplySuppression(int candidateCount, IReadOnlyList<SymbolRenderLayer> symbolLayers, double zoom)
        {
            if (symbolLayers == null || symbolLayers.Count == 0) return; // no layer list → no per-layer zoom ranges
            for (int s = 0; s < candidateCount; s++)
            {
                SymbolCandidate c = _stageCandidates[s];
                // EmitStart, not SymbolIndex — a pair's icon/text share one layer/slot, so either half's label answers this.
                int slot = _stageEmit[c.EmitStart].Slot;
                bool suppress = slot >= 0 && slot < symbolLayers.Count && symbolLayers[slot]?.StyleLayer != null
                    && !symbolLayers[slot].StyleLayer.IsVisibleAtZoom(zoom);
                if (c.Suppressed != suppress) { c.Suppressed = suppress; _stageCandidates[s] = c; }
            }
        }

        /// <summary>Point fade identity — hashed from the cross-tile key on the shared canonical grid, the same
        /// grid the store dedup uses, so a symbol's fade cell and dedup cell are one identity. Fixed and
        /// zoom-independent, so the same symbol from a swapped tile keeps a stable id and doesn't pop.</summary>
        /// <param name="textId">Interned <c>ShapedSymbol.TextId</c> (0 for an icon-only symbol).</param>
        /// <param name="iconImageId">Interned <c>ShapedSymbol.IconImageId</c>; folded in via a
        /// guard-skip so a text symbol's id is unchanged.</param>
        internal static long PointFadeId(in double3 anchorRender, int layerId, int textId, int iconImageId = 0)
            => Hash64(DedupKey.For(anchorRender, layerId, textId, iconImageId, CrossTileSymbolKey.CanonicalGridMeters));

        // Folds DedupKey's ints directly. ShapedSymbol carries only interned ids, so this reuses the
        // all-integer identity the reconciler's DedupKey already establishes.
        private static long Hash64(in DedupKey k)
        {
            unchecked
            {
                ulong h = 1469598103934665603UL; // FNV-1a 64
                h = (h ^ (ulong)k.GridX) * 1099511628211UL;
                h = (h ^ (ulong)k.GridZ) * 1099511628211UL;
                // Folding GridY is not a no-op even at 0 — (h ^ 0) * prime still shifts every id. This stays
                // snapshot-safe since the fade id is only an equality key, and multiply-by-the-odd-prime is a bijection.
                h = (h ^ (ulong)k.GridY) * 1099511628211UL;
                h = (h ^ (ulong)(uint)k.LayerId) * 1099511628211UL;
                h = (h ^ (ulong)(uint)k.TextId) * 1099511628211UL;
                // Guard-skip fold: mixes IconImageId only when non-zero (0 == no icon), so a text symbol's fade
                // id is unchanged from before this field existed.
                if (k.IconImageId != 0)
                    h = (h ^ (ulong)(uint)k.IconImageId) * 1099511628211UL;
                return (long)h;
            }
        }

        /// <summary>The camera's view-projection matrix — the single definition shared by <see cref="Tick"/>
        /// and the coverage pre-cull, so both see the same frame. Column-major, matching <c>SymbolScreenProjection</c>'s convention.</summary>
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

        /// <summary>Destroys the world renderer, materials, and native scratch buffers (main-thread only). Idempotent.</summary>
        protected override void DoDispose()
        {
            // A scheduled-but-never-completed collision holds several native buffers — disposing under a live
            // job is a use-after-free. This is the only teardown path (DoDispose runs at most once).
            if (_collisionHandle is { } scheduled) { scheduled.Complete(); _collisionHandle = null; }

            // _mirrorPlan otherwise retains the plan indefinitely — SymbolSubsystem.Dispose may dispose it while
            // this system still points at it, so a later gather would memo-hit and memcpy from disposed NativeLists.
            _mirrorPlan = null; _mirrorVersion = long.MinValue;

            WorldRenderer.Dispose();

            _worldTextMaterial.DestroySafely();
            _worldTextMaterial = null;

            _worldIconMaterial.DestroySafely();
            _worldIconMaterial = null;

            _symbolPoints.Dispose(); // projection scratch
            _symbolUps.Dispose();
            _symbolScreen.Dispose();
            _symbolDepth.Dispose();
            _symbolValid.Dispose();

            _nSurvivors.Dispose(); // collision survivor flags
            _gridCellHead.Dispose();
            _gridNodeBox.Dispose();
            _gridNodeNext.Dispose();
            _survivorCountOut.Dispose();

            _gatherBlockViews.Dispose(); _gatherCounts.Dispose();                    // Burst-gather buffers
            _mirrorKinds.Dispose(); _mirrorDetail.Dispose(); _mirrorWorldCount.Dispose();          // Burst stage job's native buffers
            _mirrorPointQuadStart.Dispose(); _mirrorPointQuadCount.Dispose();
            _mirrorCurvedGlyphStart.Dispose(); _mirrorCurvedGlyphCount.Dispose();
            _mirrorCurvedAnchorStart.Dispose(); _mirrorCurvedAnchorCount.Dispose(); _mirrorCurvedAnchorFadeStart.Dispose();
            _mirrorPoints.Dispose(); _mirrorCurveds.Dispose();
            _mirrorQuads.Dispose(); _mirrorGlyphs.Dispose(); _mirrorAnchors.Dispose(); _mirrorFadeIds.Dispose();
            _mirrorWorldStart.Dispose(); _mirrorRepAnchor.Dispose(); _mirrorWorldPoints.Dispose(); _mirrorWorldUps.Dispose(); // Stage-2 symbol-level fields
            _mirrorSymbolDeparting.Dispose(); _mirrorSymbolCoverageFading.Dispose(); _mirrorSymbolDropped.Dispose();
            _stagePointOffset.Dispose(); _stageAnchorWasPlaced.Dispose();
            _stageBoxes.Dispose(); _stageQuads.Dispose(); _stageCandidates.Dispose(); _stageEmit.Dispose();
            _stageCounts.Dispose(); _stagePath.Dispose(); _stageCumulativeLength.Dispose();
            _placedLastFrame.Dispose(); // native set, ctor-allocated alongside the other persistent containers
            _droppedHalvesLastFrame.Dispose(); // Same lifetime as _placedLastFrame
            _fadeOpacity.Dispose(); _seenFade.Dispose(); _forceFadeOut.Dispose(); _fadeSweepKeys.Dispose(); // fade collections, same lifetime
            _gatherTrigger.Dispose(); // gather Cull→Compact per-symbol verdict scratch
            _slotVisibleThisFrame.Dispose(); // per-slot zoom-visibility lookup, CullJob input
            _gatherCulledCounts.Dispose(); // per-trigger culled tally, CompactJob output bridge
            // AssertFadeIdsUnique's scratch set may be a default (never-created) value here; Dispose() no-ops on that.
            _debugFadeIdSeen.Dispose();
        }
    }
}