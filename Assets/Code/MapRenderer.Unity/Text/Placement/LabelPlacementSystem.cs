// Namespace-collision guard (see GlyphAtlasTexture.cs's header comment for the full explanation): this
// file lives in MapRenderer.Unity.Text.Placement and uses Unity.Mathematics types (float2/float4/float4x4/
// double2/double3) — TOP-LEVEL `using Unity.Mathematics;` + unqualified types, NEVER an inline
// `Unity.Mathematics.X` (would bind to the nonexistent `MapRenderer.Unity.Text.Placement.Unity.Mathematics`,
// CS0234). `MapRenderer.Unity.Text` does not collide with any bare UnityEngine type.

using System;
using System.Collections.Generic;
using MapRenderer.Core.Geo;
using MapRenderer.Core.Lifetime;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;
using Unity.Profiling;
using UnityEngine;
using UnityEngine.Rendering;
using MapRenderer.Unity.Common;
using MapRenderer.Core.Text;
using MapRenderer.Core.Text.Placement;
using MapRenderer.Jobs;
using MapRenderer.Unity.Rendering.Backend;
using MapRenderer.Unity.Rendering.Map;
using MapRenderer.Unity.Rendering.Materials;
// Alias (not a namespace import): `CameraProperties` is ambiguous between our Core camera state and
// UnityEngine.Rendering.CameraProperties (pulled in above for RenderParams/ShadowCastingMode). B-1 keys its
// static-frame skip on the committed Core CameraProperties, so bind the bare name to that one.
using CameraProperties = MapRenderer.Core.View.Camera.CameraProperties;

namespace MapRenderer.Unity.Text.Placement
{
    /// <summary>
    /// S20 Slice 1: the dedicated PER-FRAME label renderer (F1, stage doc §6) — a plain class (NOT a
    /// MonoBehaviour), owned by <see cref="MapView"/>, ticked as the last step of <c>MapView.LateUpdate</c>
    /// AFTER <see cref="MapCamera.SyncToCamera"/> (the committed-camera seam). Every <see cref="Tick"/>
    /// re-projects every label's anchor, rebuilds the ENTIRE screen-space billboard vertex buffer from
    /// scratch, and submits ONE <see cref="Graphics.RenderMesh(in RenderParams, Mesh, int, Matrix4x4)"/>
    /// draw call over a single persistent <see cref="Mesh"/> — this is the "placed every frame" path
    /// (<c>ARCHITECTURE.md</c> §"Two geometry classes"), structurally distinct from the static
    /// per-<c>(tile,layer)</c> <see cref="Backend.ITileRenderBackend"/> meshes (T5: this type never calls
    /// <c>AddTileLayer</c> — grep-checked by <c>LabelPlacementStructureTests</c>).
    ///
    /// <para><b>Collision (Slice 2).</b> Every on-screen label's screen-space AABB (<see cref="LabelBox"/>,
    /// <c>text-padding</c> applied) is run through <see cref="LabelCollision.SelectSurvivors"/> — greedy,
    /// sort-key-driven, permutation-invariant survivor selection (stage doc §4 T1) — BEFORE the billboard
    /// build, so only survivors emit quads. The projection/collision/build pass runs over reused arrays
    /// (no per-frame managed allocation — T4).</para>
    ///
    /// <para><b>Screen-space submission (S20 plan Risk #1).</b> Billboard vertices are LOGICAL SCREEN
    /// PIXELS (not object/world positions) — <c>Map/Symbol</c>'s vertex shader converts px→clip directly
    /// via <c>_ScreenParamsLogical</c>, bypassing the normal transform. <see cref="Graphics.RenderMesh"/>'s
    /// CPU-side frustum cull is defeated with an enormous <see cref="RenderParams.worldBounds"/> (that
    /// cull is evaluated as if the vertex data were real object-space positions, which it is not — see
    /// <c>Shaders/Map/Symbol/Text/README.md</c>).</para>
    /// <para><c>internal</c> (not <c>public</c>): mirrors <c>Tile.TileManager</c>'s own visibility — this
    /// is an implementation detail <see cref="MapView"/> owns, not a public API surface. Its members stay
    /// declared <c>public</c> regardless (matching <c>TileManager</c>'s convention), which also keeps
    /// <see cref="Tick"/>'s effective accessibility domain aligned with its <c>internal</c>
    /// <see cref="Backend.SceneFrame"/> parameter (CS0051 would fire if this class were <c>public</c>).</para>
    /// </summary>
    internal sealed class LabelPlacementSystem : VerifiedDisposable
    {
        // Single interleaved stream matching BillboardVertex's field order exactly: ScreenPx.xy + Depth
        // (Position, Float32x3), Color (Float32x4), Uv (TexCoord0, Float32x2). 36 bytes/vertex.
        // ORDER IS LOAD-BEARING: Unity's canonical ascending VertexAttribute enum order is Position=0,
        // Color=3, TexCoord0=4 (mirrors StyledLineTileBuilder's identical rule) — declaring TexCoord0
        // before Color silently triggers a "non-standard order" layout re-adjustment that reads the
        // wrong bytes for the wrong attribute (nothing renders; see BillboardVertex's header comment).
        private static readonly VertexAttributeDescriptor[] VertexDescriptors =
        {
            new VertexAttributeDescriptor(VertexAttribute.Position,  VertexAttributeFormat.Float32, 3),
            new VertexAttributeDescriptor(VertexAttribute.Color,     VertexAttributeFormat.Float32, 4),
            new VertexAttributeDescriptor(VertexAttribute.TexCoord0, VertexAttributeFormat.Float32, 2),
        };

        // Per-frame profiler markers for the label path (Profiler window → search "MapRenderer.Symbol").
        // PmTick is the whole per-frame submit; the three sub-markers break it into project→collide→build.
        private static readonly ProfilerMarker PmTick =
            new ProfilerMarker(ProfilerCategory.Scripts, "MapRenderer.Symbol.LabelTick");

        private static readonly ProfilerMarker PmProject =
            new ProfilerMarker(ProfilerCategory.Scripts, "MapRenderer.Symbol.Project");

        // PmProject breakdown: ProjectFill = gather + projection (the SymbolProjectionJob wait above the threshold,
        // else the serial main-thread fill); Stage = the managed staging loop that reads the projected screen
        // positions and builds the collision candidates/boxes/quads. Splitting them answers the architecture
        // question the timeline can't at a glance: is the Project cost a JOB WAIT (ProjectFill) or MANAGED main-
        // thread work (Stage)? The two nest inside PmProject so the umbrella total is preserved.
        private static readonly ProfilerMarker PmProjectFill =
            new ProfilerMarker(ProfilerCategory.Scripts, "MapRenderer.Symbol.ProjectFill");

        private static readonly ProfilerMarker PmStage =
            new ProfilerMarker(ProfilerCategory.Scripts, "MapRenderer.Symbol.Stage");

        private static readonly ProfilerMarker PmCollide =
            new ProfilerMarker(ProfilerCategory.Scripts, "MapRenderer.Symbol.Collide");

        // Emit = the A-4 fade + per-slot quad-bucket assembly (managed, main thread) that runs AFTER collision and
        // BEFORE the mesh upload. Separated from BuildSubmit (the Mesh write/upload) so the managed emit cost is
        // not hidden inside the LabelTick umbrella — it is a candidate main-thread hot spot in its own right.
        private static readonly ProfilerMarker PmEmit =
            new ProfilerMarker(ProfilerCategory.Scripts, "MapRenderer.Symbol.Emit");

        private static readonly ProfilerMarker PmBuildSubmit =
            new ProfilerMarker(ProfilerCategory.Scripts, "MapRenderer.Symbol.BuildSubmit");

        // Skip main-thread index validation + redundant bounds recompute (mirrors StyledLineTileBuilder's
        // NoValidate) — this class sets Mesh.bounds explicitly (HugeBounds) every Tick anyway.
        private const MeshUpdateFlags NoValidate =
            MeshUpdateFlags.DontValidateIndices | MeshUpdateFlags.DontRecalculateBounds;

        // Graphics.RenderMesh's CPU cull is evaluated against this AS IF it were a real object-space
        // bound — our vertex data is screen pixels, so "never cull" is the only correct choice (Risk #1).
        private static readonly Bounds HugeBounds = new Bounds(Vector3.zero, new Vector3(1e9f, 1e9f, 1e9f));

        private static readonly int AtlasPropId               = Shader.PropertyToID("_MainTex");
        private static readonly int ScreenParamsLogicalPropId = Shader.PropertyToID("_ScreenParamsLogical");

        // S105 F1: one (mesh, quad-buffer) per MATERIAL SLOT. Collision is GLOBAL across all labels; only
        // the billboard build + draw is partitioned by LabelInstance.MaterialIndex so each symbol layer's
        // survivors draw with their own material (per-layer text-halo-*). Graphics.RenderMesh is deferred,
        // so each slot needs its OWN persistent mesh (one shared mesh would be overwritten before the
        // deferred draw reads it). The demo / single-material path is just slot 0.
        private readonly List<Mesh>                   _slotMeshes = new List<Mesh>();
        private readonly List<NativeList<PlacedQuad>> _slotQuads  = new List<NativeList<PlacedQuad>>();

        // The default material — a clone of MapMaterialSet.SymbolText — used for the demo path (no per-layer
        // materials passed) and as the fallback for any slot without a supplied material.
        private Material _material;

        private NativeList<BillboardVertex> _vertexScratch;
        private NativeList<int>             _indexScratch;
        private NativeArray<int>            _vertexCountOut;
        private NativeArray<int>            _indexCountOut;

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
        private const float FadeDurationSeconds = 0.3f;
        // FIXED (zoom-INDEPENDENT) anchor-quantization for the POINT fade id. Must NOT be the A-3 display-zoom
        // dedup grid: that grid's cell size changes with zoom, so a zooming camera would step the id to a new
        // cell every fraction of a zoom level → every label's record misses → continuous re-fade (worse than the
        // pop). A fixed few-metre grid keeps the id frame-stable (AnchorRender is already zoom-invariant). Trade:
        // the cross-tile no-op is a true no-op only at HIGH zoom (parent/child MVT diff < a cell) and softens to
        // a same-place crossfade at low zoom — the accepted A-3/A-4 "identical text at the same spot" bar.
        private const double FadeGridMeters = 4.0;
        private const float FadeEpsilon = 1e-3f; // below this, a record is invisible → not emitted / dropped
        private readonly Dictionary<long, float> _fadeOpacity = new Dictionary<long, float>();
        private readonly HashSet<long> _seenFade = new HashSet<long>();
        private readonly List<long> _fadeScratchKeys = new List<long>(); // reused decay-sweep buffer (no per-frame GC)

        // ── A-5: sticky-placement hysteresis ────────────────────────────────────────────────────────────────
        // FadeIds that SURVIVED last frame's collision. Staging looks each candidate up here to set
        // LabelCandidate.WasPlacedLastFrame, which biases the greedy sort so an incumbent keeps its slot over a
        // near-tied newcomer (killing the tile-churn/reprojection tiebreak flip that reads as flicker). Rebuilt
        // from the survivors AFTER each real collision — NOT on the B-1 skip path (which returns before collision),
        // so a skipped static frame leaves the kept-set frozen (correct: its survivor set is unchanged). Reused
        // across frames → zero per-frame GC (T4).
        private readonly HashSet<long> _placedLastFrame = new HashSet<long>();

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
        private long _mirrorBuildId = long.MinValue;
        private NativeList<byte> _mKinds;
        private NativeList<int>  _mDetail, _mWorldCount, _mPointQuadStart, _mPointQuadCount;
        private NativeList<int>  _mCurvedGlyphStart, _mCurvedGlyphCount, _mCurvedAnchorStart, _mCurvedAnchorCount, _mCurvedAnchorFadeStart;
        private NativeList<PointStageInput>  _mPoints;
        private NativeList<CurvedStageInput> _mCurveds;
        private NativeList<SymbolQuad>  _mQuads;
        private NativeList<CurvedGlyph> _mGlyphs;
        private NativeList<LineAnchor>  _mAnchors;
        private NativeList<long>        _mFadeIds;
        private NativeList<int>  _sjPointOffset;                 // gather output (-1 = culled)
        private NativeList<byte> _sjPointWasPlaced, _sjAnchorWasPlaced; // per-frame A-5 incumbency
        private NativeList<LabelBox>       _sjBoxes;             // job outputs (pre-sized to batch worst case)
        private NativeList<PlacedQuad>     _sjQuads;
        private NativeList<LabelCandidate> _sjCandidates;
        private NativeList<CandidateEmit>  _sjEmit;
        private NativeArray<int>           _sjCounts;            // [candidateCount, boxCount, quadCount]
        private NativeList<float2> _sjPath;                      // arc-walk scratch (>= max path length)
        private NativeList<float>  _sjCum;

        // ── B-1: static-frame skip ─────────────────────────────────────────────────────────────────────────
        // When the COLLECTED label set (labelSetVersion — SymbolTileLabelStore.Version), the committed CAMERA,
        // and the FADE state are ALL unchanged since the last real build, a re-projection would produce a
        // byte-identical mesh. So Tick RE-SUBMITS the cached per-slot meshes (Graphics.RenderMesh is immediate-
        // mode — issued every frame regardless) and skips project/collide/build/upload. This removes the idle
        // Project/Collide cost AND the per-frame rebuild that is the root of the idle blink.
        //   * Camera-unchanged is keyed on the SAME committed CameraProperties fields TileManager keys its cover
        //     on (lon/lat/alt/zoom/heading/tilt/fov + viewport) — NOT the derived viewProj float4x4, which carries
        //     per-frame FP jitter on an idle camera so it would never compare equal (the skip would be silent dead
        //     code). CameraProperties is the SOURCE the matrices, the scene origin (origin ≡ look-at, S52) and the
        //     viewport all derive from, so equal properties ⇒ byte-identical build. This assumes MapView passes a
        //     SceneFrame consistent with the committed camera (it does: the frame's origin is the look-at).
        //   * GOVERNING RULE: OVER-invalidate. A missed skip costs a few ms; a wrong skip freezes labels. So the
        //     skip is illegal until the first real build (_haveCachedFrame), any frame that does NOT build (no
        //     atlas yet / no labels) drops the cache, and the demo/test path opts out via the sentinel version.
        //   * SCOPE: inside Tick only. CollectInto/reconcile upstream still run every frame (cheap vs projection);
        //     folding the skip up into MapView to skip collection too is a follow-up.
        private const long NeverSkipVersion = long.MinValue; // demo/test callers pass no version → never skip
        private bool   _haveCachedFrame;                     // a real build has completed at least once
        private long   _cachedVersion = NeverSkipVersion;
        private bool   _cachedCameraValid;
        private CameraProperties _cachedCamera;
        private double2 _cachedViewportLogicalPx;
        // The SceneFrame is the one build input NOT derived from _camera inside Tick — MapView passes it in. It is
        // a deterministic function of the committed look-at today (origin = Project(lookAt), rebase =
        // TangentBasisAt(lookAt)), so the camera key already covers it transitively; keying on it DIRECTLY makes
        // the skip robust to a future BuildSceneFrame that accumulates rebased state (over-invalidate) — and it is
        // what the projection actually consumes, so it is the honest guard.
        private double3  _cachedSceneOrigin;
        private float3x3 _cachedSceneRebase;
        // Per-slot record of the LAST real build so a skip re-submits exactly the slots that drew (with the same
        // material). The persistent _slotMeshes still hold that frame's buffers — a skip never rewrites them.
        private struct SlotDraw { public bool NonEmpty; public Material Material; }
        private readonly List<SlotDraw> _cachedSlotDraws = new List<SlotDraw>();

        // Where a surviving candidate's already-built quads live in _sjQuads + which material slot they draw
        // in (Core.Text.Placement.CandidateEmit) — keyed by the candidate's creation ordinal
        // (LabelCandidate.LabelIndex) so it is stable across the in-place candidate sort; emission just copies the
        // [QuadStart, QuadStart+QuadCount) range. Filled by LabelStagingMath alongside the candidates.

        /// <summary>Number of <see cref="Tick"/> calls so far — T5 structural guard (the vertex buffer is
        /// rebuilt every Tick, not once at tile consume). Test surface.</summary>
        internal int TickCount { get; private set; }

        /// <summary>The quad count submitted on the LAST <see cref="Tick"/> (0 if nothing was visible). Test surface.</summary>
        internal int LastQuadCount { get; private set; }

        /// <summary>Labels fed into the LAST <see cref="Tick"/> (before any projection cull) — telemetry.</summary>
        internal int LastInputLabelCount { get; private set; }

        /// <summary>Collision CANDIDATES on the last Tick (labels that survived projection and entered the
        /// greedy pass — a point label is 1, a curved/repeated line label is 1 per anchor). Telemetry.</summary>
        internal int LastCandidateCount { get; private set; }

        /// <summary>Collision SURVIVORS on the last Tick (candidates actually placed). Telemetry.</summary>
        internal int LastSurvivorCount { get; private set; }

        /// <summary>B-3: labels skipped by the pre-projection horizon/distance cull on the last Tick (never
        /// projected or collided). Telemetry — a proxy for how much the tilted-view horizon pile-up was trimmed.</summary>
        internal int LastDistanceCulledCount { get; private set; }

        /// <summary>B-1: <c>true</c> iff the last <see cref="Tick"/> took the static-frame skip (re-submitted the
        /// cached meshes without re-projecting). The skip is invisible in output (byte-identical to a rebuild), so
        /// a test MUST assert on this to prove the skip actually fired — an un-fired skip is silent dead code.</summary>
        internal bool LastTickSkipped { get; private set; }

        /// <summary>The persistent billboard mesh <see cref="Tick"/> rebuilds every call. Test surface: headless
        /// EditMode has no player loop, so a <see cref="Graphics.RenderMesh"/> submission never appears under a
        /// manually-invoked <see cref="Camera.Render()"/> (see <c>docs/lessons-learned.md</c>) — a snapshot test
        /// verifies the real mesh/material this class built by attaching them to a temporary MeshFilter/MeshRenderer
        /// instead (the proven-headless path every other snapshot test already uses). The actual
        /// <see cref="Graphics.RenderMesh"/> call is a Play-mode-only eyeball item.</summary>
        internal Mesh Mesh => _slotMeshes.Count > 0 ? _slotMeshes[0] : null;

        /// <summary>The default material <see cref="Tick"/> refreshes every call (atlas texture + screen params). Test surface — see <see cref="Mesh"/>.</summary>
        internal Material Material => _material;

        // The map view this system renders labels for — injected at construction (S20: one
        // LabelPlacementSystem per MapView). Read AFTER MapCamera.SyncToCamera has committed the frame's
        // transform: MapView.LateUpdate does SyncToCamera → tile rebase → places labels, in that order.
        private readonly MapCamera _camera;

        /// <summary>
        /// The default material is a <see cref="MaterialExtensions.CloneWithParent"/> clone of the
        /// <see cref="MapMaterialSet.SymbolText"/> base (S58 pattern: materials reference the shader by GUID,
        /// no <c>Shader.Find</c>) — used for the demo / single-material path and as the fallback for any slot
        /// without a supplied per-layer material. Cloned (not the base asset itself) because <see cref="Tick"/>
        /// mutates it every frame (atlas texture + screen params). Per-symbol-layer materials (per-layer
        /// <c>text-halo-*</c>) are supplied to <see cref="Tick"/> by the symbol subsystem.
        /// </summary>
        /// <param name="baseMaterial">The <c>MapMaterialSet.SymbolText</c> base; null → labels won't render (logged once).</param>
        public LabelPlacementSystem(MapCamera camera, Material baseMaterial)
        {
            _camera = camera ?? throw new ArgumentNullException(nameof(camera));

            _vertexScratch  = new NativeList<BillboardVertex>(Allocator.Persistent);
            _indexScratch   = new NativeList<int>(Allocator.Persistent);
            _vertexCountOut = new NativeArray<int>(1, Allocator.Persistent);
            _indexCountOut  = new NativeArray<int>(1, Allocator.Persistent);

            _symbolPoints = new NativeList<double3>(Allocator.Persistent); // B-2 projection scratch
            _symbolScreen = new NativeList<float2>(Allocator.Persistent);
            _symbolDepth  = new NativeList<float>(Allocator.Persistent);
            _symbolValid  = new NativeList<byte>(Allocator.Persistent);

            _nSurvivors       = new NativeList<byte>(Allocator.Persistent); // B-4a collision survivor flags
            _gridCellHead     = new NativeList<int>(Allocator.Persistent);
            _gridNodeBox      = new NativeList<int>(Allocator.Persistent);
            _gridNodeNext     = new NativeList<int>(Allocator.Persistent);
            _survivorCountOut = new NativeArray<int>(1, Allocator.Persistent);

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
            _sjPointOffset = new NativeList<int>(Allocator.Persistent);
            _sjPointWasPlaced = new NativeList<byte>(Allocator.Persistent);
            _sjAnchorWasPlaced = new NativeList<byte>(Allocator.Persistent);
            _sjBoxes = new NativeList<LabelBox>(Allocator.Persistent);
            _sjQuads = new NativeList<PlacedQuad>(Allocator.Persistent);
            _sjCandidates = new NativeList<LabelCandidate>(Allocator.Persistent);
            _sjEmit = new NativeList<CandidateEmit>(Allocator.Persistent);
            _sjCounts = new NativeArray<int>(3, Allocator.Persistent);
            _sjPath = new NativeList<float2>(Allocator.Persistent);
            _sjCum = new NativeList<float>(Allocator.Persistent);

            if (baseMaterial == null)
            {
                Debug.LogWarning("[LabelPlacementSystem] no base symbol-text material (MapMaterialSet.SymbolText " +
                                 "unassigned) — labels will not render.");
                _material = null;
            }
            else
            {
                _material      = baseMaterial.CloneWithParent();
                _material.name = "LabelPlacementSystem_Material";
            }
        }

        /// <summary>
        /// One frame of the per-label placement loop: project every label's anchor (culling behind-camera
        /// / far-outside-viewport anchors), rebuild the billboard vertex/index buffers from every
        /// surviving label's quads, and submit ONE <see cref="Graphics.RenderMesh"/> draw call. A no-op
        /// submission (no draw, but <see cref="TickCount"/> still advances) when there is nothing to draw
        /// (empty <paramref name="labels"/>, no atlas texture yet, or every anchor culled).
        /// </summary>
        /// <param name="frame">This frame's floating-origin scene frame (<see cref="SceneFrame.SceneOriginRender"/> — the T2 rebase).</param>
        /// <param name="labels">Every candidate label this frame (collision selects the survivors).</param>
        /// <param name="atlas">The uploaded R8 SDF glyph atlas texture backing every label's <see cref="SymbolQuad"/> UVs.</param>
        /// <param name="materials">Per-symbol-layer materials indexed by <see cref="LabelInstance.MaterialIndex"/>
        /// (production, per-layer <c>text-halo-*</c>). Null / empty → the demo path: every label draws with the
        /// single default material. Collision is GLOBAL regardless; only the draw is partitioned by material.</param>
        /// <param name="deltaTime">Seconds since the last <see cref="Tick"/> — drives the A-4 fade ease. Default
        /// <see cref="float.PositiveInfinity"/> SNAPS every fade to its target (no animation), so a single-Tick
        /// test renders fully-placed labels exactly as before A-4 (byte-parity); production passes
        /// <c>Time.deltaTime</c>.</param>
        /// <param name="labelSetVersion">B-1: the collected label set's version (<c>SymbolTileLabelStore.Version</c>).
        /// When it — together with the committed camera and the fade state — is unchanged since the last real
        /// build, <see cref="Tick"/> re-submits the cached meshes and skips project/collide/build. The default
        /// sentinel (<see cref="NeverSkipVersion"/>) opts OUT: the demo / single-material / test path always
        /// rebuilds (byte-parity), so only a caller that threads a real version can be skipped.</param>
        public void Tick(in SceneFrame frame, IReadOnlyList<LabelInstance> labels, GlyphAtlasTexture atlas,
            float deltaTime = float.PositiveInfinity, IReadOnlyList<Material> materials = null,
            long labelSetVersion = NeverSkipVersion)
        {
            // Demo / test seam: convert the managed carriers into the blittable batch (the SAME conversion the
            // production subsystem does once per collected-set change), then tick it. Rebuilt every call here (the
            // sentinel path isn't perf-critical); production threads a pre-built, version-cached batch instead.
            int slotCount = (materials != null && materials.Count > 0) ? materials.Count : 1;
            SymbolLabelBatchBuilder.Build(_demoBatch, labels, slotCount);
            Tick(frame, _demoBatch, atlas, deltaTime, materials, labelSetVersion, labels?.Count ?? 0);
        }

        /// <summary>Production entry: tick a pre-built, version-cached <see cref="SymbolLabelBatch"/> (built off the
        /// per-frame path by <see cref="SymbolLabelBatchBuilder"/> at the aggregation seam). Same placement as the
        /// managed-list overload; no per-frame LabelInstance iteration or conversion.</summary>
        public void Tick(in SceneFrame frame, SymbolLabelBatch batch, GlyphAtlasTexture atlas,
            float deltaTime = float.PositiveInfinity, IReadOnlyList<Material> materials = null,
            long labelSetVersion = NeverSkipVersion)
            => Tick(frame, batch, atlas, deltaTime, materials, labelSetVersion, batch?.Count ?? 0);

        private void Tick(in SceneFrame frame, SymbolLabelBatch batch, GlyphAtlasTexture atlas,
            float deltaTime, IReadOnlyList<Material> materials, long labelSetVersion, int inputLabelCount)
        {
            TickCount++;
            LastInputLabelCount = inputLabelCount;

            using (PmTick.Auto())
            {
                double2 viewportLogicalPx = _camera.ViewportPx / _camera.DevicePixelRatio;
                CameraProperties cam = _camera.CurrentProperties;

                // B-1: static-frame skip — re-submit the cached meshes when nothing that affects the built
                // geometry changed since the last real build (label set + camera + scene frame + fades all stable).
                if (CanSkipFrame(labelSetVersion, cam, viewportLogicalPx, frame))
                {
                    LastTickSkipped = true;
                    ResubmitCachedFrame();
                    return;
                }
                LastTickSkipped = false;

                LastCandidateCount = 0;
                LastSurvivorCount = 0;
                LastDistanceCulledCount = 0;

                int slotCount = (materials != null && materials.Count > 0) ? materials.Count : 1;
                EnsureSlots(slotCount);
                for (int g = 0; g < slotCount; g++) _slotQuads[g].Clear();

                int  totalQuads = 0;
                bool didBuild   = false;

                if (batch != null && batch.Count > 0 && atlas?.Texture != null && _material != null)
                {
                    didBuild = true;
                    float4x4 viewProj = math.mul(ToFloat4x4(_camera.Camera.projectionMatrix),
                        ToFloat4x4(_camera.Camera.worldToCameraMatrix));
                    double3 sceneOriginRender = frame.SceneOriginRender;

                    // B-3: the pre-projection horizon/distance cull radius (render metres around the look-at) —
                    // one logical pixel of ground = GroundResolution(zoom), so LabelViewportSpans screen-widths
                    // of ground. Labels beyond it are skipped BEFORE projection/collision (the horizon pile-up).
                    double cullRadius = LabelViewDistance.CullRadiusMeters(
                        viewportLogicalPx, WebMercator.GroundResolution(_camera.CurrentProperties.Zoom), LabelViewportSpans);

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
                            GatherSymbolPoints(batch, sceneOriginRender, cullRadius);
                            ProjectSymbols(sceneOriginRender, viewProj, viewportLogicalPx);
                        }

                        using (PmStage.Auto())
                        {
                            // Stage the whole batch in ONE Burst job (LabelStageJob) — same LabelStagingMath as the
                            // managed reference, SIMD-compiled. Refresh the native batch mirror only if it was rebuilt;
                            // resolve this frame's incumbency + pre-size outputs; run; read counts; copy the native
                            // outputs into the managed pools the unchanged collision/emit passes read.
                            RefreshBatchMirror(batch);
                            ResolveIncumbency(batch);
                            PreSizeStageOutputs(batch);
                            RunStageJob(batch, bearingRadians, viewportLogicalPx);
                            candidateCount = _sjCounts[0]; boxCount = _sjCounts[1];
                        }
                    }

                    // (2) Unified greedy, sort-key-driven, all-or-nothing collision — GLOBAL across all layers and
                    //     both placement kinds — run as the Burst LabelCollisionJob directly over the stage job's
                    //     native pools (B-4a). Size the grid, schedule + Complete (synchronous for B-4a; B-4b defers
                    //     the Complete a frame). _sjCandidates is sorted in place by the job; the emit loop below
                    //     reads the sorted native candidates + survivor flags.
                    LastCandidateCount = candidateCount;
                    using (PmCollide.Auto())
                        LastSurvivorCount = RunCollision(candidateCount, boxCount);

                    // (3) A-4 FADE (strictly downstream of collision — never fed back into SelectSurvivors):
                    //     ease each candidate's persistent opacity toward 1 (survivor) or 0 (suppressed), then
                    //     emit its staged quads scaled by that opacity. A suppressed label is still staged this
                    //     frame, so it fades OUT in place from its live placement (no cross-frame quad cache); a
                    //     new label fades IN from 0. Records not seen this frame decay and are dropped (bounded).
                    using (PmEmit.Auto())
                    {
                        _seenFade.Clear();
                        _placedLastFrame.Clear(); // A-5: rebuild this frame's incumbents for next frame's staging
                        for (int s = 0; s < candidateCount; s++)
                        {
                            LabelCandidate cand = _sjCandidates[s]; // sorted in place by the collision job
                            bool survived = _nSurvivors[s] != 0;
                            _seenFade.Add(cand.FadeId);
                            // A-5: incumbency tracks COLLISION survival (not opacity) — a just-born survivor still at
                            // ~0 alpha is placed, so it must stay sticky. Keyed by FadeId (line labels: per-anchor).
                            if (survived) _placedLastFrame.Add(cand.FadeId);
                            float opacity = EaseFade(cand.FadeId, survived ? 1f : 0f, deltaTime);
                            if (opacity <= FadeEpsilon) continue;

                            CandidateEmit          emit   = _sjEmit[cand.LabelIndex];
                            NativeList<PlacedQuad> bucket = _slotQuads[emit.Slot];
                            for (int k = 0; k < emit.QuadCount; k++)
                            {
                                PlacedQuad q = _sjQuads[emit.QuadStart + k];
                                q.Color.w *= opacity; // per-vertex alpha the shader emits — the fade, no shader change
                                bucket.Add(q);
                                totalQuads++;
                            }
                        }
                        DecayUnseenFadeRecords(deltaTime);
                    }
                }

                // B-1: capture (or invalidate) the skip cache. Only a real build produces a cacheable static
                // frame; any frame that could NOT build (no atlas / no labels / no material) drops the cache so
                // the next frame rebuilds once its inputs are ready — never skip into a stale or never-drawn state.
                if (didBuild)
                    RecordFrameCache(labelSetVersion, cam, viewportLogicalPx, frame, slotCount, materials);
                else
                {
                    _haveCachedFrame = false;
                    _placedLastFrame.Clear(); // A-5: nothing placed this frame → no incumbents to carry forward
                }

                LastQuadCount = totalQuads;
                if (totalQuads == 0) return; // nothing visible this frame — no draw call submitted

                // Submit one draw per non-empty slot, each with its own per-layer material (or the default).
                for (int g = 0; g < slotCount; g++)
                {
                    if (_slotQuads[g].Length == 0) continue;
                    BuildAndSubmit(_slotQuads[g], _slotMeshes[g], ResolveSlotMaterial(g, materials), viewportLogicalPx, atlas);
                }
            }
        }

        // ── B-1 helpers ─────────────────────────────────────────────────────────────────────────────────────
        // The per-layer material a slot draws with: the supplied per-symbol-layer material, else the default.
        private Material ResolveSlotMaterial(int slot, IReadOnlyList<Material> materials)
            => (materials != null && slot < materials.Count && materials[slot] != null) ? materials[slot] : _material;

        // True iff a re-projection would produce a byte-identical build (so Tick can re-submit the cached meshes).
        // OVER-invalidate: any doubt returns false. The demo/test sentinel version forces a rebuild every frame.
        private bool CanSkipFrame(long labelSetVersion, in CameraProperties cam, double2 viewportLogicalPx,
            in SceneFrame frame)
        {
            if (!_haveCachedFrame) return false;                 // no real build yet — nothing to re-submit
            if (labelSetVersion == NeverSkipVersion) return false; // demo/test path opts out of the skip
            if (labelSetVersion != _cachedVersion) return false; // the collected label set changed
            if (!_cachedCameraValid || !CameraUnchanged(cam, viewportLogicalPx)) return false; // camera moved
            if (!SceneFrameUnchanged(frame)) return false;       // the floating-origin frame shifted
            if (AnyFadeInProgress()) return false;               // a fade is still animating — mesh not static yet
            return true;
        }

        // Compare the committed camera field-by-field against the cache (the CameraProperties + viewport that fully
        // determine the build). Exact equality, not an epsilon: identical committed properties re-derive identical
        // matrices/origin, so the skipped rebuild would be byte-identical (that is B-1's whole invariant).
        private bool CameraUnchanged(in CameraProperties cam, double2 viewportLogicalPx)
            => cam.LookAt.Longitude == _cachedCamera.LookAt.Longitude
            && cam.LookAt.Latitude  == _cachedCamera.LookAt.Latitude
            && cam.LookAt.Altitude  == _cachedCamera.LookAt.Altitude
            && cam.Zoom             == _cachedCamera.Zoom
            && cam.Heading.Degrees  == _cachedCamera.Heading.Degrees
            && cam.Tilt.Degrees     == _cachedCamera.Tilt.Degrees
            && cam.VerticalFovDeg   == _cachedCamera.VerticalFovDeg
            && viewportLogicalPx.x  == _cachedViewportLogicalPx.x
            && viewportLogicalPx.y  == _cachedViewportLogicalPx.y;

        // The floating-origin frame every anchor projects through must be bit-identical too (origin + rebase basis).
        private bool SceneFrameUnchanged(in SceneFrame frame)
            => frame.SceneOriginRender.Equals(_cachedSceneOrigin)
            && frame.Rebase.Equals(_cachedSceneRebase);

        // A fade record strictly between invisible and fully-placed means the mesh alpha is still changing frame
        // to frame → the geometry is NOT static, so the skip stays illegal until every fade settles (then re-skips).
        // Settled survivors sit at exactly 1f (EaseFade clamps to target); fade-outs are dropped once <= eps.
        private bool AnyFadeInProgress()
        {
            foreach (float v in _fadeOpacity.Values)
                if (v > FadeEpsilon && v < 1f) return true;
            return false;
        }

        // Snapshot the just-built frame so a following static frame can be skipped. Even the sentinel version is
        // recorded (harmless — CanSkipFrame rejects the sentinel outright), so the demo/test path stays consistent.
        private void RecordFrameCache(long version, in CameraProperties cam, double2 viewportLogicalPx,
            in SceneFrame frame, int slotCount, IReadOnlyList<Material> materials)
        {
            _cachedVersion           = version;
            _cachedCamera            = cam;
            _cachedCameraValid       = true;
            _cachedViewportLogicalPx = viewportLogicalPx;
            _cachedSceneOrigin       = frame.SceneOriginRender;
            _cachedSceneRebase       = frame.Rebase;
            _cachedSlotDraws.Clear();
            for (int g = 0; g < slotCount; g++)
                _cachedSlotDraws.Add(new SlotDraw
                {
                    NonEmpty = _slotQuads[g].Length > 0,
                    Material = ResolveSlotMaterial(g, materials),
                });
            _haveCachedFrame = true;
        }

        // Re-issue the cached per-slot draws over the persistent meshes (which still hold the last build's buffers).
        private void ResubmitCachedFrame()
        {
            for (int g = 0; g < _cachedSlotDraws.Count; g++)
            {
                SlotDraw d = _cachedSlotDraws[g];
                if (!d.NonEmpty || d.Material == null || g >= _slotMeshes.Count) continue;
                SubmitDraw(_slotMeshes[g], d.Material);
            }
        }

        // Ensures slotCount (mesh, quad-buffer) slots exist. Grows only when a style adds symbol layers —
        // amortized, warm-up only; steady-state Ticks reuse the slots (T4 no-per-frame-GC).
        private void EnsureSlots(int slotCount)
        {
            while (_slotMeshes.Count < slotCount)
            {
                var mesh = new Mesh { name = $"LabelPlacementSystem_Mesh{_slotMeshes.Count}" };
                mesh.MarkDynamic();
                _slotMeshes.Add(mesh);
                _slotQuads.Add(new NativeList<PlacedQuad>(Allocator.Persistent));
            }
        }

        // ── B-2: gather + project every symbol's screen geometry ──────────────────────────────────────────────
        // Flatten every un-culled record's world points (from the batch SoA) into _symbolPoints — a point's anchor,
        // a line's path vertices — recording each record's start in the native _sjPointOffset (-1 = B-3-culled →
        // skipped by the stage job). Cheap: array reads + the B-3 distance cull, no matrix mul; the projection
        // (matrix mul) happens once, in ProjectSymbols.
        private void GatherSymbolPoints(SymbolLabelBatch batch, double3 sceneOriginRender, double cullRadius)
        {
            _sjPointOffset.ResizeUninitialized(batch.Count);
            _symbolPoints.Clear();
            for (int r = 0; r < batch.Count; r++)
            {
                // B-3: skip the far horizon pile-up BEFORE projection/collision — culled records are not gathered.
                if (LabelViewDistance.IsCulled(batch.RepAnchor[r], sceneOriginRender, cullRadius))
                {
                    LastDistanceCulledCount++;
                    _sjPointOffset[r] = -1;
                    continue;
                }

                _sjPointOffset[r] = _symbolPoints.Length;
                int ws = batch.WorldStart[r], wc = batch.WorldCount[r];
                for (int v = 0; v < wc; v++) _symbolPoints.Add(batch.WorldPoints[ws + v]);
            }
        }

        // Project the gathered _symbolPoints to screen/depth/valid. Parallel Burst job at/above the threshold, else
        // a serial loop over the SAME LabelScreenProjection.TryProjectPoint (so the two fills are bit-identical —
        // the job Schedule+Complete overhead only pays off at high counts, and below it a job could cost MORE than
        // the serial matrix-muls it replaces, so B-2 can only help, never regress the common case).
        private void ProjectSymbols(double3 sceneOriginRender, in float4x4 viewProj, double2 viewportLogicalPx)
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
                ViewProj          = viewProj,
                ViewportLogicalPx = viewportLogicalPx,
                OutScreen         = _symbolScreen.AsArray(),
                OutDepth          = _symbolDepth.AsArray(),
                OutValid          = _symbolValid.AsArray(),
            }.Run(total);
        }

        // B-4a: run the greedy collision as the Burst LabelCollisionJob directly over the stage job's native output
        // pools (no managed round-trip — the stage job already wrote them native). PRE-SIZES the uniform grid on the
        // main thread (a Burst job cannot grow a NativeArray), then schedules + Completes the job (synchronous —
        // B-4b defers the Complete a frame). Returns the survivor count; _sjCandidates is left sorted in placement
        // order and _nSurvivors holds the per-sorted-position survivor flags the emit loop reads.
        private int RunCollision(int candidateCount, int boxCount)
        {
            if (candidateCount <= 0) return 0;

            _nSurvivors.Resize(candidateCount, NativeArrayOptions.UninitializedMemory);
            NativeArray<LabelCandidate> nc = _sjCandidates.AsArray(); // stage job's candidates — sorted IN PLACE here
            NativeArray<LabelBox>       nb = _sjBoxes.AsArray();       // stage job's boxes (read-only; grid over [0,boxCount))

            // Pre-size the uniform grid: CellHead = W*H (filled -1); the node arrays = the exact upper bound (sum
            // of cells each box covers — mirrors the job's cell mapping so no insert is ever dropped).
            LabelCollisionGridSizing.Dims dims = LabelCollisionGridSizing.ComputeDims(nb, boxCount);
            int cells   = dims.W * dims.H;
            int nodeCap = math.max(1, LabelCollisionGridSizing.NodeUpperBound(nb, boxCount, in dims));
            _gridCellHead.Resize(cells,   NativeArrayOptions.UninitializedMemory);
            _gridNodeBox.Resize(nodeCap,  NativeArrayOptions.UninitializedMemory);
            _gridNodeNext.Resize(nodeCap, NativeArrayOptions.UninitializedMemory);
            NativeArray<int> cellHead = _gridCellHead.AsArray();
            for (int c = 0; c < cells; c++) cellHead[c] = -1;

            new LabelCollisionJob
            {
                Candidates     = nc, CandidateCount = candidateCount,
                Boxes          = nb, BoxCount = boxCount,
                Survivors      = _nSurvivors.AsArray(), OutSurvivorCount = _survivorCountOut,
                CellHead       = cellHead, NodeBox = _gridNodeBox.AsArray(), NodeNext = _gridNodeNext.AsArray(),
                GridMinX       = dims.MinX, GridMinY = dims.MinY, GridInvCell = dims.InvCell,
                GridW          = dims.W, GridH = dims.H,
            }.Schedule().Complete();

            return _survivorCountOut[0];
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

        // Refresh the native mirror of the batch's STAGE data — ONLY when the batch was rebuilt (BuildId change),
        // never per frame. The Burst LabelStageJob reads these NativeArrays instead of the managed batch arrays.
        private void RefreshBatchMirror(SymbolLabelBatch batch)
        {
            if (batch.BuildId == _mirrorBuildId) return;
            _mirrorBuildId = batch.BuildId;
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

        // A-5 per-frame: resolve each point/anchor fade id against _placedLastFrame into native arrays the job reads
        // (the one managed, main-thread piece that stays outside the Burst job — it needs the placed-set HashSet).
        private void ResolveIncumbency(SymbolLabelBatch batch)
        {
            _sjPointWasPlaced.ResizeUninitialized(batch.PointCount);
            for (int i = 0; i < batch.PointCount; i++)
                _sjPointWasPlaced[i] = (byte)(_placedLastFrame.Contains(batch.Points[i].FadeId) ? 1 : 0);
            _sjAnchorWasPlaced.ResizeUninitialized(batch.AnchorFadeCount);
            for (int i = 0; i < batch.AnchorFadeCount; i++)
                _sjAnchorWasPlaced[i] = (byte)(_placedLastFrame.Contains(batch.AnchorFadeIds[i]) ? 1 : 0);
        }

        // Pre-size the job's output pools to the batch worst case (Burst cannot grow) + the arc-walk scratch to at
        // least the longest path (the total gathered-point count is a safe upper bound for any single path).
        private void PreSizeStageOutputs(SymbolLabelBatch batch)
        {
            _sjBoxes.ResizeUninitialized(math.max(1, batch.MaxBoxes));
            _sjQuads.ResizeUninitialized(math.max(1, batch.MaxQuads));
            _sjCandidates.ResizeUninitialized(math.max(1, batch.MaxCandidates));
            _sjEmit.ResizeUninitialized(math.max(1, batch.MaxCandidates));
            int scratch = math.max(1, _symbolPoints.Length);
            _sjPath.ResizeUninitialized(scratch);
            _sjCum.ResizeUninitialized(scratch);
        }

        private void RunStageJob(SymbolLabelBatch batch, float bearingRadians, double2 viewportLogicalPx)
        {
            new LabelStageJob
            {
                Kinds = _mKinds.AsArray(), Detail = _mDetail.AsArray(), WorldCount = _mWorldCount.AsArray(), Count = batch.Count,
                Points = _mPoints.AsArray(), PointQuadStart = _mPointQuadStart.AsArray(), PointQuadCount = _mPointQuadCount.AsArray(),
                Curveds = _mCurveds.AsArray(),
                CurvedGlyphStart = _mCurvedGlyphStart.AsArray(), CurvedGlyphCount = _mCurvedGlyphCount.AsArray(),
                CurvedAnchorStart = _mCurvedAnchorStart.AsArray(), CurvedAnchorCount = _mCurvedAnchorCount.AsArray(),
                CurvedAnchorFadeStart = _mCurvedAnchorFadeStart.AsArray(),
                Quads = _mQuads.AsArray(), Glyphs = _mGlyphs.AsArray(), Anchors = _mAnchors.AsArray(), AnchorFadeIds = _mFadeIds.AsArray(),
                PointOffset = _sjPointOffset.AsArray(),
                Screen = _symbolScreen.AsArray(), Depth = _symbolDepth.AsArray(), Valid = _symbolValid.AsArray(),
                PointWasPlaced = _sjPointWasPlaced.AsArray(), AnchorWasPlaced = _sjAnchorWasPlaced.AsArray(),
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
            float current = _fadeOpacity.TryGetValue(fadeId, out float v) ? v : 0f;
            float step = deltaTime / FadeDurationSeconds;
            float next = target > current ? math.min(current + step, target) : math.max(current - step, target);
            _fadeOpacity[fadeId] = next;
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

        /// <summary>A-4 POINT fade identity: the A-3 cross-tile key on a FIXED (zoom-independent) grid, hashed to
        /// a long. Fixed grid ⇒ a frame-STABLE id (a per-frame display-zoom grid would re-key every label as the
        /// camera zooms); reusing <see cref="CrossTileLabelKey"/> lets the same symbol from a swapped tile keep
        /// its opacity record (the seamless no-op) at high zoom. <c>internal</c> so an EditMode test can pin the
        /// stable-across-zoom + within-grid-collapse behaviour directly.</summary>
        internal static long PointFadeId(in double3 anchorRender, int layerId, string text)
            => Hash64(CrossTileLabelKey.For(anchorRender, layerId, text, FadeGridMeters));

        private static long Hash64(in CrossTileLabelKey k)
        {
            unchecked
            {
                ulong h = 1469598103934665603UL; // FNV-1a 64
                h = (h ^ (ulong)k.GridX) * 1099511628211UL;
                h = (h ^ (ulong)k.GridZ) * 1099511628211UL;
                h = (h ^ (ulong)(uint)k.LayerId) * 1099511628211UL;
                h = (h ^ (ulong)(uint)(k.Text?.GetHashCode() ?? 0)) * 1099511628211UL;
                return (long)h;
            }
        }

        private void BuildAndSubmit(NativeList<PlacedQuad> quads,             Mesh              mesh, Material material,
            double2                                        viewportLogicalPx, GlyphAtlasTexture atlas)
        {
            using (PmBuildSubmit.Auto())
            {
                int quadCount = quads.Length;
                int vCount    = SymbolBillboardJob.MaxVertexCount(quadCount);
                int iCount    = SymbolBillboardJob.MaxIndexCount(quadCount);

                _vertexScratch.Resize(vCount, NativeArrayOptions.UninitializedMemory);
                _indexScratch.Resize(iCount, NativeArrayOptions.UninitializedMemory);

                new SymbolBillboardJob
                {
                    Quads          = quads.AsArray(),
                    QuadCount      = quadCount,
                    OutVertices    = _vertexScratch.AsArray(),
                    OutIndices     = _indexScratch.AsArray(),
                    OutVertexCount = _vertexCountOut,
                    OutIndexCount  = _indexCountOut,
                }.Run();

                int writtenVerts   = _vertexCountOut[0];
                int writtenIndices = _indexCountOut[0];
                if (writtenVerts == 0 || writtenIndices == 0) return;

                // Rebuilt every Tick, never once — T5's behavioral half (the structural half is the grep for
                // AddTileLayer in LabelPlacementStructureTests). One mesh per material slot (the CPU vertex/index
                // scratch is shared across slots — safe, since each slot's data is uploaded into its OWN mesh
                // before the next slot reuses the scratch; only the persistent Mesh must be per-slot).
                mesh.SetVertexBufferParams(writtenVerts, VertexDescriptors);
                mesh.SetVertexBufferData(_vertexScratch.AsArray(), 0, 0, writtenVerts, 0, NoValidate);
                mesh.SetIndexBufferParams(writtenIndices, IndexFormat.UInt32);
                mesh.SetIndexBufferData(_indexScratch.AsArray(), 0, 0, writtenIndices, NoValidate);
                mesh.subMeshCount = 1;
                mesh.SetSubMesh(0, new SubMeshDescriptor(0, writtenIndices, MeshTopology.Triangles), NoValidate);
                mesh.bounds = HugeBounds;

                material.SetTexture(AtlasPropId, atlas.Texture);
                material.SetVector(ScreenParamsLogicalPropId,
                    new Vector4((float)viewportLogicalPx.x, (float)viewportLogicalPx.y, 0f, 0f));

                SubmitDraw(mesh, material);
            }
        }

        // The single Graphics.RenderMesh submit — shared by a fresh build (BuildAndSubmit) and the B-1 cached
        // re-submit (ResubmitCachedFrame). Pin the draw to THIS map camera: a null camera submits for EVERY
        // camera, which would draw this camera's screen-space vertices into the SceneView/other cameras (at wrong
        // positions, since the verts are projected for this camera only). _camera.Camera confines it to the one
        // we projected for. The material's atlas texture + screen params persist from its last build (unchanged on
        // a skip — the viewport is part of the skip key), so the cached re-submit needs no per-frame material set.
        private void SubmitDraw(Mesh mesh, Material material)
        {
            var rp = new RenderParams(material)
            {
                camera            = _camera.Camera,
                worldBounds       = HugeBounds,
                receiveShadows    = false,
                shadowCastingMode = ShadowCastingMode.Off,
            };
            Graphics.RenderMesh(in rp, mesh, 0, Matrix4x4.identity);
        }

        /// <summary>
        /// Manual column-by-column conversion (not an assumed <c>(float4x4)</c> cast operator — the same
        /// "don't rely on possibly-absent implicit numeric conversions" caution <c>FloatingOrigin</c>
        /// documents for <c>double3</c>→<c>float3</c>). Column-major, matching
        /// <see cref="LabelScreenProjection"/>'s <c>math.mul(float4x4, float4)</c> convention.
        /// <c>internal</c> so <c>LabelScreenProjectionUnityTests</c> reuses the SAME conversion instead of
        /// duplicating it (the test-code-bloat convention's allowed footprint: broaden private → internal).
        /// </summary>
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

        /// <summary>Destroys the mesh/material (main-thread only — play → <c>Destroy</c>, edit →
        /// <c>DestroyImmediate</c>, mirroring <c>GlyphAtlasTexture</c>/<c>MaterialFactory</c>) and disposes
        /// the native scratch buffers. Idempotent.</summary>
        protected override void DoDispose()
        {
            for (int g = 0; g < _slotMeshes.Count; g++)
            {
                _slotMeshes[g].DestroySafely();
                _slotQuads[g].Dispose();
            }

            _slotMeshes.Clear();
            _slotQuads.Clear();

            _material.DestroySafely();
            _material = null;

            _vertexScratch.Dispose();
            _indexScratch.Dispose();
            _vertexCountOut.Dispose();
            _indexCountOut.Dispose();

            _symbolPoints.Dispose(); // B-2 projection scratch
            _symbolScreen.Dispose();
            _symbolDepth.Dispose();
            _symbolValid.Dispose();

            _nSurvivors.Dispose(); // B-4a collision survivor flags
            _gridCellHead.Dispose();
            _gridNodeBox.Dispose();
            _gridNodeNext.Dispose();
            _survivorCountOut.Dispose();

            _mKinds.Dispose(); _mDetail.Dispose(); _mWorldCount.Dispose();          // Lever C step 3b native buffers
            _mPointQuadStart.Dispose(); _mPointQuadCount.Dispose();
            _mCurvedGlyphStart.Dispose(); _mCurvedGlyphCount.Dispose();
            _mCurvedAnchorStart.Dispose(); _mCurvedAnchorCount.Dispose(); _mCurvedAnchorFadeStart.Dispose();
            _mPoints.Dispose(); _mCurveds.Dispose();
            _mQuads.Dispose(); _mGlyphs.Dispose(); _mAnchors.Dispose(); _mFadeIds.Dispose();
            _sjPointOffset.Dispose(); _sjPointWasPlaced.Dispose(); _sjAnchorWasPlaced.Dispose();
            _sjBoxes.Dispose(); _sjQuads.Dispose(); _sjCandidates.Dispose(); _sjEmit.Dispose();
            _sjCounts.Dispose(); _sjPath.Dispose(); _sjCum.Dispose();
        }
    }
}