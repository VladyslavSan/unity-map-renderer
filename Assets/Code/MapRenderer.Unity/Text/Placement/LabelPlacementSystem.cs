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

        // #5 (B3): the UNIFIED collision scratch — plain managed arrays reused across Ticks (grown
        // geometrically, so steady-state Ticks with a stable label count never reallocate — T4). Every label,
        // point (1 box) or curved along-line (N glyph boxes), becomes ONE LabelCandidate spanning a
        // contiguous range of the flat _boxes pool, so a road name and a city name compete in ONE greedy pass
        // (B-4a: that pass runs as the Burst LabelCollisionJob over native mirrors of these pools; the grid keeps
        // it ~O(n·k), and the greedy is inherently serial — each placement depends on all prior survivors).
        //  * _candidates/_emit are keyed by CANDIDATE. _candidates is copied into _nCandidates and reordered
        //    there by the in-place placement sort (inside the job); _emit is keyed by the candidate's CREATION
        //    ordinal (carried opaquely in LabelCandidate.LabelIndex, so it survives the sort) and holds where that
        //    candidate's staged quads live + which material slot they draw in.
        //  * _boxes is the flat box pool (point + per-glyph boxes); _stagedQuads the flat placed-quad pool.
        //    Both are addressed by a candidate's [Start, Start+Count) range and grown on append (never sorted).
        private LabelCandidate[] _candidates  = Array.Empty<LabelCandidate>();
        private CandidateEmit[]  _emit        = Array.Empty<CandidateEmit>();
        private LabelBox[]       _boxes       = Array.Empty<LabelBox>();
        private PlacedQuad[]     _stagedQuads = Array.Empty<PlacedQuad>();

        // ── B-4a: collision runs as a Burst IJob (LabelCollisionJob) over NATIVE mirrors of the staging pools ──
        // The greedy pass is serial, so it is ONE job (not a fan-out); the grid keeps it ~O(n·k). Staging still
        // writes the managed _candidates/_boxes (Stage* untouched); a per-frame bulk copy into these native
        // mirrors feeds the job, which sorts _nCandidates in place and writes _nSurvivors — the emit loop then
        // reads the sorted native candidates + survivor flags. The uniform grid is PRE-SIZED on the main thread
        // (LabelCollisionGridSizing) each frame because a Burst job cannot grow a NativeArray. All reused + grown
        // geometrically → zero per-frame GC (T4). Bit-identical to the managed LabelCollision reference (the
        // differential test locks it over adversarial inputs).
        private NativeList<LabelCandidate> _nCandidates;
        private NativeList<LabelBox>       _nBoxes;
        private NativeList<byte>           _nSurvivors;
        private NativeList<int>            _gridCellHead;
        private NativeList<int>            _gridNodeBox;
        private NativeList<int>            _gridNodeNext;
        private NativeArray<int>           _survivorCountOut;

        // #5: reused scratch for curved along-line placement — the projected screen polyline (grown
        // geometrically, never shrinks) + a reused arc walker over it, so the per-frame walk is zero-GC (T4).
        private float2[] _pathScreenScratch = System.Array.Empty<float2>();
        private readonly PolylineArcWalker _arcWalker = new PolylineArcWalker();

        // HARD CEILING on the along-line repeat loop (#5 B4). A real line has a handful of labels; the ceiling
        // exists only so a DEGENERATE projection can never spin an unbounded loop: a line vertex at/just in
        // front of the near plane projects to a near-infinite screen coord, blowing the projected line length
        // (and thus the fixed-spacing anchor count `length / spacing`) up to millions. NEVER iterate a
        // data-derived count without a finite bound — a laughably-high one is fine, an unbounded one is a hang.
        private const int MaxAnchorsPerLine = 256;
        // A projected line vertex beyond this many logical px is non-physical (near-plane blow-up / NaN); such a
        // line is skipped rather than fed to the arc walker, keeping the projected length (and anchor loop) sane.
        private const float MaxProjectedPx = 1e5f;

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
        // points go in _symbolPoints; _pointOffset[i] is label i's start in it (-1 = null / B-3-culled → skipped);
        // the fill writes the parallel _symbolScreen/_symbolDepth/_symbolValid. The job is dispatched with .Run()
        // (Burst-compiled, executed inline on the caller — no Schedule/Complete round-trip, no worker hand-off, no
        // count threshold), so the projection is always Burst SIMD with zero managed fallback and zero per-frame GC.
        // All buffers are reused + grown geometrically.
        private NativeList<double3> _symbolPoints;
        private NativeList<float2>  _symbolScreen;
        private NativeList<float>   _symbolDepth;
        private NativeList<byte>    _symbolValid;
        private int[] _pointOffset = Array.Empty<int>();

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

        // Where a surviving candidate's already-built quads live in _stagedQuads + which material slot they
        // draw in. Keyed by the candidate's creation ordinal (LabelCandidate.LabelIndex) so it is stable
        // across the in-place candidate sort — emission just copies the [QuadStart, QuadStart+QuadCount) range.
        private struct CandidateEmit
        {
            public int QuadStart;
            public int QuadCount;
            public int Slot;
        }

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

            _nCandidates      = new NativeList<LabelCandidate>(Allocator.Persistent); // B-4a native collision mirrors
            _nBoxes           = new NativeList<LabelBox>(Allocator.Persistent);
            _nSurvivors       = new NativeList<byte>(Allocator.Persistent);
            _gridCellHead     = new NativeList<int>(Allocator.Persistent);
            _gridNodeBox      = new NativeList<int>(Allocator.Persistent);
            _gridNodeNext     = new NativeList<int>(Allocator.Persistent);
            _survivorCountOut = new NativeArray<int>(1, Allocator.Persistent);

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
            TickCount++;
            LastInputLabelCount = labels?.Count ?? 0;

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

                if (labels != null && labels.Count > 0 && atlas?.Texture != null && _material != null)
                {
                    didBuild = true;
                    EnsureCandidateCapacity(labels.Count);

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

                    // (1) Project + STAGE every label into the unified pools: one LabelCandidate per label
                    //     (point = 1 AABB box; curved = N rotated-glyph boxes), its collision boxes in _boxes,
                    //     its drawn quads in _stagedQuads. Point and curved share the collision pass from here,
                    //     so a road name and a city name compete for space (#5 B3).
                    int candidateCount = 0, boxCount = 0, stagedCount = 0;
                    using (PmProject.Auto())
                    {
                        // B-2: gather every un-culled symbol's world points (anchor / line path) — cheap, no matrix
                        // mul, and it is where the B-3 distance cull now runs — then project them all in one pass
                        // (parallel Burst job above the threshold, else serial). Staging reads the precomputed
                        // screen positions below rather than projecting each anchor/path inline.
                        using (PmProjectFill.Auto())
                        {
                            GatherSymbolPoints(labels, sceneOriginRender, cullRadius);
                            ProjectSymbols(sceneOriginRender, viewProj, viewportLogicalPx);
                        }

                        using (PmStage.Auto())
                        {
                            for (int i = 0; i < labels.Count; i++)
                            {
                                int off = _pointOffset[i];
                                if (off < 0) continue; // null label or B-3-distance-culled (not gathered/projected)

                                // Each Stage* returns HOW MANY candidates it staged: point → 0/1; curved → 0/1 for
                                // line-center, 0..N for symbol-placement:line (one per along-line repeat anchor, B4).
                                LabelInstance label = labels[i];
                                candidateCount += label.Placement == SymbolPlacement.Point
                                    ? StagePointLabel(label, _symbolScreen[off], _symbolDepth[off], _symbolValid[off] != 0,
                                        bearingRadians, viewportLogicalPx, slotCount, candidateCount, ref boxCount, ref stagedCount)
                                    : StageCurvedLabel(label, off, bearingRadians,
                                        viewportLogicalPx, slotCount, candidateCount, ref boxCount, ref stagedCount);
                            }
                        }
                    }

                    // (2) Unified greedy, sort-key-driven, all-or-nothing collision — GLOBAL across all layers and
                    //     both placement kinds — run as the Burst LabelCollisionJob over native mirrors of the
                    //     staged pools (B-4a). Copy the managed staged data in, size the grid, schedule + Complete
                    //     (synchronous for B-4a; B-4b defers the Complete a frame). _nCandidates is sorted in place
                    //     by the job; the emit loop below reads the sorted native candidates + survivor flags.
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
                            LabelCandidate cand = _nCandidates[s]; // sorted in place by the collision job
                            bool survived = _nSurvivors[s] != 0;
                            _seenFade.Add(cand.FadeId);
                            // A-5: incumbency tracks COLLISION survival (not opacity) — a just-born survivor still at
                            // ~0 alpha is placed, so it must stay sticky. Keyed by FadeId (line labels: per-anchor).
                            if (survived) _placedLastFrame.Add(cand.FadeId);
                            float opacity = EaseFade(cand.FadeId, survived ? 1f : 0f, deltaTime);
                            if (opacity <= FadeEpsilon) continue;

                            CandidateEmit          emit   = _emit[cand.LabelIndex];
                            NativeList<PlacedQuad> bucket = _slotQuads[emit.Slot];
                            for (int k = 0; k < emit.QuadCount; k++)
                            {
                                PlacedQuad q = _stagedQuads[emit.QuadStart + k];
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

        // Reserves the per-CANDIDATE scratch for `count` labels as a warm-up estimate (one candidate each).
        // symbol-placement:line can stage MANY candidates per label (B4), so EnsureCandidateSlot grows it
        // further on demand; geometric doubling keeps steady-state Ticks realloc-free (T4).
        private void EnsureCandidateCapacity(int count)
        {
            if (_candidates.Length >= count) return;
            int cap = _candidates.Length == 0 ? 16 : _candidates.Length;
            while (cap < count) cap *= 2;
            Array.Resize(ref _candidates, cap);
            Array.Resize(ref _emit,       cap);
        }

        // Grows the per-candidate scratch to admit ordinal `index` (a line label repeated N times overruns the
        // one-per-label reserve). Geometric — no realloc once the frame's peak candidate count is seen (T4).
        private void EnsureCandidateSlot(int index)
        {
            if (index < _candidates.Length) return;
            int cap = _candidates.Length == 0 ? 16 : _candidates.Length;
            while (cap <= index) cap *= 2;
            Array.Resize(ref _candidates, cap);
            Array.Resize(ref _emit,       cap);
        }

        // ── B-2: gather + project every symbol's screen geometry ──────────────────────────────────────────────
        // Flatten every un-culled symbol's world points into _symbolPoints — a point symbol's anchor, a line
        // symbol's path vertices — recording each label's start in _pointOffset (-1 = null / B-3-culled → skipped
        // by the staging loop). Cheap: field reads + the B-3 distance cull, no matrix mul. Generic over symbol
        // kind; the projection (matrix mul) happens once, in ProjectSymbols.
        private void GatherSymbolPoints(IReadOnlyList<LabelInstance> labels, double3 sceneOriginRender, double cullRadius)
        {
            EnsurePointOffset(labels.Count);
            _symbolPoints.Clear();
            for (int i = 0; i < labels.Count; i++)
            {
                LabelInstance label = labels[i];
                if (label == null) { _pointOffset[i] = -1; continue; }

                // B-3: skip the far horizon pile-up BEFORE projection/collision — culled symbols are not gathered.
                if (LabelViewDistance.IsCulled(RepresentativeAnchor(label), sceneOriginRender, cullRadius))
                {
                    LastDistanceCulledCount++;
                    _pointOffset[i] = -1;
                    continue;
                }

                _pointOffset[i] = _symbolPoints.Length;
                if (label.Placement == SymbolPlacement.Point)
                {
                    _symbolPoints.Add(label.AnchorRender);
                }
                else
                {
                    double3[] path = label.PathRender;
                    int n = path?.Length ?? 0;
                    for (int v = 0; v < n; v++) _symbolPoints.Add(path[v]);
                }
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

        // Grows the per-label offset map geometrically (T4). Indexed by label-list position.
        private void EnsurePointOffset(int count)
        {
            if (_pointOffset.Length >= count) return;
            int cap = _pointOffset.Length == 0 ? 16 : _pointOffset.Length;
            while (cap < count) cap *= 2;
            Array.Resize(ref _pointOffset, cap);
        }

        // B-4a: run the greedy collision as the Burst LabelCollisionJob over native mirrors of the staged pools.
        // Copies the managed staged candidates/boxes in, PRE-SIZES the uniform grid on the main thread (a Burst job
        // cannot grow a NativeArray), then schedules + Completes the job (synchronous — B-4b defers the Complete a
        // frame). Returns the survivor count; _nCandidates is left sorted in placement order and _nSurvivors holds
        // the per-sorted-position survivor flags the emit loop reads.
        private int RunCollision(int candidateCount, int boxCount)
        {
            if (candidateCount <= 0) return 0;

            _nCandidates.Resize(candidateCount, NativeArrayOptions.UninitializedMemory);
            _nBoxes.Resize(boxCount,            NativeArrayOptions.UninitializedMemory);
            _nSurvivors.Resize(candidateCount,  NativeArrayOptions.UninitializedMemory);
            NativeArray<LabelCandidate> nc = _nCandidates.AsArray();
            NativeArray<LabelBox>       nb = _nBoxes.AsArray();
            for (int i = 0; i < candidateCount; i++) nc[i] = _candidates[i];
            for (int i = 0; i < boxCount; i++)       nb[i] = _boxes[i];

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

        // Append one box/quad to the flat pool, growing it geometrically (never shrinks — T4). Returns the
        // new cursor. Array.Resize preserves prior entries, so a mid-pass grow keeps earlier BoxStart ranges
        // valid.
        private int AppendBox(int count, in LabelBox box)
        {
            if (_boxes.Length <= count) Array.Resize(ref _boxes, _boxes.Length == 0 ? 32 : _boxes.Length * 2);
            _boxes[count] = box;
            return count + 1;
        }

        private int AppendQuad(int count, in PlacedQuad quad)
        {
            if (_stagedQuads.Length <= count) Array.Resize(ref _stagedQuads, _stagedQuads.Length == 0 ? 64 : _stagedQuads.Length * 2);
            _stagedQuads[count] = quad;
            return count + 1;
        }

        // The label's text-color, sRGB→linear + folded text-opacity — the vertex color the shader emits
        // directly (mirrors StyledFill/LineTileBuilder's Color.linear; without it a dark #333 uploads as
        // linear ~0.2 and displays washed-out). Alpha is not gamma-encoded — carried straight.
        private static float4 LinearColor(LabelInstance label)
        {
            float4 srgb   = label.Paint.TextColor;
            Color  linear = new Color(srgb.x, srgb.y, srgb.z, 1f).linear;
            return new float4(linear.r, linear.g, linear.b, srgb.w * label.Paint.Opacity);
        }

        // Demo path / out-of-range material index → default slot 0.
        private static int ClampSlot(int slot, int slotCount) => (slot < 0 || slot >= slotCount) ? 0 : slot;

        // B-3: the render-space point used for the pre-projection distance cull — a point label's anchor, or a
        // line label's MIDPOINT vertex (a long line spanning near→far is culled only when its middle is past the
        // horizon radius, so a partly-near line is never wrongly dropped). Falls back to AnchorRender if a line
        // somehow carries no path (it then reads default(double3) — harmless: at worst it isn't culled).
        private static double3 RepresentativeAnchor(LabelInstance label)
        {
            double3[] path = label.PathRender;
            if (label.Placement != SymbolPlacement.Point && path != null && path.Length > 0)
                return path[path.Length / 2];
            return label.AnchorRender;
        }

        /// <summary>
        /// Projects + stages one POINT label: one axis-aligned collision box (the whole-label AABB, Slice 2 —
        /// unrotated even under text-rotation-alignment:map, matching the point behaviour) + its glyph quads
        /// (at the shared anchor, rotated by the #4 bearing). Writes <c>_candidates[ordinal]</c> +
        /// <c>_emit[ordinal]</c> and returns <c>true</c>, or <c>false</c> if it has no quads or its anchor
        /// culls off-screen (no pool entries appended on a skip).
        /// </summary>
        private int StagePointLabel(LabelInstance label, float2 screenPx, float depth, bool projected,
            float bearingRadians, double2 viewportLogicalPx, int slotCount, int ordinal,
            ref int boxCount, ref int stagedCount)
        {
            IReadOnlyList<SymbolQuad> quads = label.Layout?.Quads;
            if (quads == null || quads.Count == 0) return 0;

            // B-2: the anchor was projected up front (SymbolProjectionJob / serial fill). Apply the point cull here
            // — behind-camera (projected == false) OR outside the viewport margin (the cheap screen-bounds test the
            // job omits). Together these equal the old inline TryProjectAnchor (= TryProjectPoint + margin).
            if (!projected || !LabelScreenProjection.IsWithinViewportMargin(screenPx, viewportLogicalPx))
                return 0;

            // text-translate (Slice C): shift the placed anchor so the whole label — box AND quads, both built
            // from screenPx — moves together; text-translate-anchor:map rotates the offset by the bearing (#4).
            screenPx = LabelTranslate.ApplyTranslate(screenPx, label.TranslatePx, label.TranslateAnchor, bearingRadians);
            float4 color = LinearColor(label);

            // text-rotation-alignment:map (#4) — one angle for the whole label (rotation is about the anchor).
            float rotationRadians = LabelBearing.BillboardRotationRadians(label.RotationAlignment, bearingRadians);

            int boxStart = boxCount;
            boxCount = AppendBox(boxCount, LabelBox.Build(
                screenPx, label.Layout.BoundsMin, label.Layout.BoundsMax, label.TextSizePx, label.PaddingPx,
                label.SortKey, label.FeatureIndex, label.TileKey, ordinal, label.AllowOverlap, label.IgnorePlacement));

            int slot      = ClampSlot(label.MaterialIndex, slotCount);
            int quadStart = stagedCount;
            for (int q = 0; q < quads.Count; q++)
                stagedCount = AppendQuad(stagedCount, new PlacedQuad
                {
                    Quad = quads[q], AnchorScreenPx = screenPx, TextSizePx = label.TextSizePx,
                    Depth = depth, Color = color, RotationRadians = rotationRadians,
                });

            long fadeId = PointFadeId(label.AnchorRender, label.MaterialIndex, label.Text); // A-4 cross-frame identity
            EnsureCandidateSlot(ordinal);
            _candidates[ordinal] = new LabelCandidate
            {
                BoxStart = boxStart, BoxCount = 1,
                SortKey = label.SortKey, FeatureIndex = label.FeatureIndex, TileKey = label.TileKey,
                AllowOverlap = label.AllowOverlap, IgnorePlacement = label.IgnorePlacement, LabelIndex = ordinal,
                FadeId = fadeId,
                WasPlacedLastFrame = _placedLastFrame.Contains(fadeId), // A-5 sticky-placement incumbency
            };
            _emit[ordinal] = new CandidateEmit { QuadStart = quadStart, QuadCount = quads.Count, Slot = slot };
            return 1;
        }

        /// <summary>
        /// Projects + stages one CURVED along-line label (#5). Projects the render-space path (behind-camera
        /// cull only — a partly-off-screen line still labels), walks it, and stages one candidate per
        /// <see cref="LabelInstance.LineAnchors">build-time anchor</see> (A-2: stable tile-space topology, so the
        /// anchors don't slide/pop on zoom). Each anchor stages N per-glyph quads at their along-line screen
        /// points rotated to the local TANGENT, with a per-glyph rotated-corner collision box
        /// (<see cref="LabelBox.BuildRotatedGlyph"/>) — one all-or-nothing candidate per anchor that competes
        /// with point labels (B3). Returns the NUMBER of anchors staged (0 if the path culls, the projected line
        /// has zero length, or the label is longer than the whole line).
        ///
        /// <para><b>Tangent only, not the #4 bearing path:</b> the tangent comes from projected screen points,
        /// which already reflect the map bearing — routing through <c>LabelBearing.BillboardRotationRadians</c>
        /// too (line placement defaults rotation-alignment auto→map) would double-rotate.</para>
        /// </summary>
        private int StageCurvedLabel(LabelInstance label, int pathOffset,
            float bearingRadians, double2 viewportLogicalPx, int slotCount, int ordinal,
            ref int boxCount, ref int stagedCount)
        {
            double3[] path = label.PathRender;
            IReadOnlyList<CurvedGlyph> glyphs = label.CurvedGlyphs;
            LineAnchor[] anchors = label.LineAnchors;
            if (path == null || path.Length < 2 || glyphs == null || glyphs.Count == 0
                || anchors == null || anchors.Length == 0) return 0;

            if (_pathScreenScratch.Length < path.Length)
            {
                int cap = _pathScreenScratch.Length == 0 ? 8 : _pathScreenScratch.Length;
                while (cap < path.Length) cap *= 2;
                _pathScreenScratch = new float2[cap];
            }

            // B-2: the path vertices were projected up front (SymbolProjectionJob / serial fill) at pathOffset —
            // read the precomputed screen positions instead of projecting inline. Same cull semantics as before:
            // any vertex behind the camera (_symbolValid == 0) skips the label; a non-physical screen coord
            // (near-plane blow-up) would explode the line length and the anchor loop, so it also skips the label.
            float pathDepth = 0f;
            for (int v = 0; v < path.Length; v++)
            {
                int k = pathOffset + v;
                if (_symbolValid[k] == 0) return 0; // behind the camera
                float2 sp = _symbolScreen[k];
                if (!(math.abs(sp.x) < MaxProjectedPx && math.abs(sp.y) < MaxProjectedPx))
                    return 0; // near-plane / degenerate projection — skip (also rejects NaN via the negated test)
                _pathScreenScratch[v] = sp;
                if (v == path.Length / 2) pathDepth = _symbolDepth[k]; // representative depth (labels are overlay anyway)
            }

            _arcWalker.Init(_pathScreenScratch, path.Length);
            float total = _arcWalker.TotalLength;
            if (!(total > 0f)) return 0; // zero-length or non-finite (negated test also rejects NaN)

            float scale            = label.TextSizePx / TextQuadLayout.OneEm;
            float labelCenterBaked = (glyphs[0].ArcCenter + glyphs[glyphs.Count - 1].ArcCenter) * 0.5f;
            float labelSpanPx      = (glyphs[glyphs.Count - 1].ArcCenter - glyphs[0].ArcCenter) * scale;
            float halfSpan         = labelSpanPx * 0.5f;
            if (labelSpanPx > total) return 0; // label longer than the whole line — never fits

            float4 color = LinearColor(label);
            int    slot  = ClampSlot(label.MaterialIndex, slotCount);

            // A-2: iterate the BUILD-TIME anchors — stable (segment, t) topology pinned to fixed world points,
            // so a label's repeats no longer slide or change count as the camera zooms (the old fixed
            // screen-px-from-start walk did both). Each anchor's per-frame screen arc distance comes from the
            // projected polyline via ArcDistanceAt; the fit-gate (whole label within the projected line) is the
            // only per-frame position decision. anchors.Length is already bounded at build; the MaxAnchorsPerLine
            // clamp is defence-in-depth (never iterate a data-derived count without a finite ceiling).
            int staged = 0;
            int anchorCount = math.min(anchors.Length, MaxAnchorsPerLine);
            for (int a = 0; a < anchorCount; a++)
            {
                float centerArc = _arcWalker.ArcDistanceAt(anchors[a].Segment, anchors[a].T);
                if (centerArc - halfSpan < 0f || centerArc + halfSpan > total) continue; // label spills the ends
                // Only advance the ordinal when the anchor is actually staged (text-max-angle may drop it). The
                // fade id keys on the stable anchor INDEX `a` (not the volatile ordinal), so it survives frames.
                if (StageCurvedAnchor(label, ordinal + staged, a, glyphs, centerArc, labelCenterBaked, scale,
                        color, slot, pathDepth, bearingRadians, ref boxCount, ref stagedCount))
                    staged++;
            }
            // The line fits one label but no build-time anchor's projected position passed the fit-gate this
            // frame (a mid-length line at a zoom where only the centre fits) → try a single centred label. The
            // centre is the projected arc-midpoint — itself a stable world point, so this keeps A-2's no-slide.
            if (staged == 0 &&
                StageCurvedAnchor(label, ordinal, -1, glyphs, total * 0.5f, labelCenterBaked, scale,
                    color, slot, pathDepth, bearingRadians, ref boxCount, ref stagedCount)) // -1: the centred fallback's own id
                staged = 1;

            return staged;
        }

        /// <summary>
        /// Stages ONE curved-label instance centred at arc distance <paramref name="centerArc"/> along the
        /// current <see cref="_arcWalker"/>: one all-or-nothing candidate over its N per-glyph boxes + quads.
        /// Returns <c>false</c> (rolling the box/quad pools back, staging nothing) when the label's along-line
        /// curvature exceeds <c>text-max-angle</c> at any adjacent glyph pair — the label is dropped at this
        /// anchor rather than bent illegibly around a corner (#6).
        ///
        /// <para>keep-upright (#6, default true) is evaluated at THIS anchor's local tangent (a long line can
        /// curve back on itself, so each repeat decides its own left-to-right flip independently); with
        /// <c>text-keep-upright:false</c> the glyphs follow the raw line direction.</para>
        /// </summary>
        private bool StageCurvedAnchor(LabelInstance label, int ordinal, int anchorIndex,
            IReadOnlyList<CurvedGlyph> glyphs, float centerArc, float labelCenterBaked, float scale, float4 color,
            int slot, float pathDepth, float bearingRadians, ref int boxCount, ref int stagedCount)
        {
            // keep-upright: a label whose center tangent points leftward would render right-to-left; walk the
            // arc reversed and flip each glyph +pi so it still reads left-to-right. Disabled by keep-upright:false.
            _arcWalker.At(centerArc, out _, out float centerTangent);
            bool  reversed = label.KeepUpright && math.cos(centerTangent) < 0f;
            float dir      = reversed ? -1f : 1f;
            float flip     = reversed ? math.PI : 0f;

            // text-max-angle: the label is dropped if the LINE tangent turns by more than this between any two
            // adjacent characters (the flip is a constant across glyphs, so it cancels in adjacent deltas).
            float maxAngleRad = math.radians(label.MaxAngleDeg);

            int boxStart  = boxCount;
            int quadStart = stagedCount;
            float prevTangent = 0f;
            for (int g = 0; g < glyphs.Count; g++)
            {
                CurvedGlyph cg = glyphs[g];
                float arc = centerArc + dir * (cg.ArcCenter - labelCenterBaked) * scale;
                _arcWalker.At(arc, out float2 pt, out float tangent);

                if (g > 0 && math.abs(AngleDelta(tangent, prevTangent)) > maxAngleRad)
                {
                    boxCount = boxStart; stagedCount = quadStart; // roll back this anchor's partial appends
                    return false;                                  // too sharp a bend → drop the label here (#6)
                }
                prevTangent = tangent;

                pt = LabelTranslate.ApplyTranslate(pt, label.TranslatePx, label.TranslateAnchor, bearingRadians);
                float rotation = tangent + flip; // tangent ONLY — not LabelBearing (would double-rotate)

                boxCount = AppendBox(boxCount,
                    LabelBox.BuildRotatedGlyph(pt, cg.Cell, label.TextSizePx, rotation, label.PaddingPx));
                stagedCount = AppendQuad(stagedCount, new PlacedQuad
                {
                    Quad = cg.Cell, AnchorScreenPx = pt, TextSizePx = label.TextSizePx,
                    Depth = pathDepth, Color = color, RotationRadians = rotation,
                });
            }

            long fadeId = LineFadeId(label.TileKey, label.FeatureIndex, anchorIndex); // A-4 within-tile identity
            EnsureCandidateSlot(ordinal);
            _candidates[ordinal] = new LabelCandidate
            {
                BoxStart = boxStart, BoxCount = glyphs.Count,
                SortKey = label.SortKey, FeatureIndex = label.FeatureIndex, TileKey = label.TileKey,
                AllowOverlap = label.AllowOverlap, IgnorePlacement = label.IgnorePlacement, LabelIndex = ordinal,
                FadeId = fadeId,
                WasPlacedLastFrame = _placedLastFrame.Contains(fadeId), // A-5 sticky-placement incumbency
            };
            _emit[ordinal] = new CandidateEmit { QuadStart = quadStart, QuadCount = glyphs.Count, Slot = slot };
            return true;
        }

        // Smallest signed angle a - b, wrapped to (-pi, pi] (via atan2 so it's bounded, no while-loop drift).
        private static float AngleDelta(float a, float b)
        {
            float d = a - b;
            return math.atan2(math.sin(d), math.cos(d));
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

        // A-4 LINE fade identity: within-tile (tile, feature, anchor-index) — stable across frames while the tile
        // is loaded (line labels are excluded from A-3 cross-tile identity in v1, so this does NOT persist a tile
        // swap; a line label crossfades on a swap rather than a true no-op).
        private static long LineFadeId(long tileKey, int featureIndex, int anchorIndex)
        {
            unchecked
            {
                ulong h = 1469598103934665603UL; // FNV-1a 64
                h = (h ^ (ulong)tileKey) * 1099511628211UL;
                h = (h ^ (ulong)(uint)featureIndex) * 1099511628211UL;
                h = (h ^ (ulong)(uint)anchorIndex) * 1099511628211UL;
                return (long)h;
            }
        }

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

            _nCandidates.Dispose(); // B-4a native collision mirrors
            _nBoxes.Dispose();
            _nSurvivors.Dispose();
            _gridCellHead.Dispose();
            _gridNodeBox.Dispose();
            _gridNodeNext.Dispose();
            _survivorCountOut.Dispose();
        }
    }
}