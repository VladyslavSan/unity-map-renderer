// Namespace-collision guard (see GlyphAtlasTexture.cs's header comment for the full explanation): this
// file lives in MapRenderer.Unity.Text.Placement and uses Unity.Mathematics types (float2/float4/float4x4/
// double2/double3) — TOP-LEVEL `using Unity.Mathematics;` + unqualified types, NEVER an inline
// `Unity.Mathematics.X` (would bind to the nonexistent `MapRenderer.Unity.Text.Placement.Unity.Mathematics`,
// CS0234). `MapRenderer.Unity.Text` does not collide with any bare UnityEngine type.

using System;
using System.Collections.Generic;
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

        private static readonly ProfilerMarker PmCollide =
            new ProfilerMarker(ProfilerCategory.Scripts, "MapRenderer.Symbol.Collide");

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
        // (the acceleration lever is the spatial grid _collisionGrid, not Burst — the pass is inherently
        // serial: each placement depends on all prior survivors).
        //  * _candidates/_survivors/_emit are keyed by CANDIDATE. _candidates is reordered by the in-place
        //    placement sort; _survivors[i] is for SORTED candidate position i; _emit is keyed by the
        //    candidate's CREATION ordinal (carried opaquely in LabelCandidate.LabelIndex, so it survives the
        //    sort) and holds where that candidate's staged quads live + which material slot they draw in.
        //  * _boxes is the flat box pool (point + per-glyph boxes); _stagedQuads the flat placed-quad pool.
        //    Both are addressed by a candidate's [Start, Start+Count) range and grown on append (never sorted).
        private LabelCandidate[] _candidates  = Array.Empty<LabelCandidate>();
        private bool[]           _survivors   = Array.Empty<bool>();
        private CandidateEmit[]  _emit        = Array.Empty<CandidateEmit>();
        private LabelBox[]       _boxes       = Array.Empty<LabelBox>();
        private PlacedQuad[]     _stagedQuads = Array.Empty<PlacedQuad>();

        // Reused screen-space grid that turns the greedy collision from O(n²) to ~O(n·k) (k = local label
        // density) — the fix for the ~100 ms MapRenderer.Symbol.Collide at zoom-14-big-city label counts.
        // Owned here + reused every frame so there is zero per-frame GC (T4); its survivor set is
        // bit-identical to the brute-force reference (LabelCollisionTests differential).
        private readonly LabelCollisionGrid _collisionGrid = new LabelCollisionGrid();

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
        public void Tick(in SceneFrame frame, IReadOnlyList<LabelInstance> labels, GlyphAtlasTexture atlas,
            IReadOnlyList<Material>    materials = null)
        {
            TickCount++;
            LastInputLabelCount = labels?.Count ?? 0;
            LastCandidateCount = 0;
            LastSurvivorCount = 0;

            using (PmTick.Auto())
            {
                double2 viewportLogicalPx = _camera.ViewportPx / _camera.DevicePixelRatio;

                int slotCount = (materials != null && materials.Count > 0) ? materials.Count : 1;
                EnsureSlots(slotCount);
                for (int g = 0; g < slotCount; g++) _slotQuads[g].Clear();

                int totalQuads = 0;

                if (labels != null && labels.Count > 0 && atlas?.Texture != null && _material != null)
                {
                    EnsureCandidateCapacity(labels.Count);

                    float4x4 viewProj = math.mul(ToFloat4x4(_camera.Camera.projectionMatrix),
                        ToFloat4x4(_camera.Camera.worldToCameraMatrix));
                    double3 sceneOriginRender = frame.SceneOriginRender;

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
                        for (int i = 0; i < labels.Count; i++)
                        {
                            LabelInstance label = labels[i];
                            if (label == null) continue;

                            // Each Stage* returns HOW MANY candidates it staged: point → 0/1; curved → 0/1 for
                            // line-center, 0..N for symbol-placement:line (one per along-line repeat anchor, B4).
                            candidateCount += label.Placement == SymbolPlacement.Point
                                ? StagePointLabel(label, viewProj, sceneOriginRender, bearingRadians,
                                    viewportLogicalPx, slotCount, candidateCount, ref boxCount, ref stagedCount)
                                : StageCurvedLabel(label, viewProj, sceneOriginRender, bearingRadians,
                                    viewportLogicalPx, slotCount, candidateCount, ref boxCount, ref stagedCount);
                        }
                    }

                    // (2) Unified greedy, sort-key-driven, all-or-nothing collision — GLOBAL across all layers
                    //     and both placement kinds (sorts _candidates in place; _boxes keeps stable indices).
                    LastCandidateCount = candidateCount;
                    using (PmCollide.Auto())
                        LastSurvivorCount = LabelCollision.SelectSurvivors(
                            _candidates, candidateCount, _boxes, boxCount, _survivors, _collisionGrid);

                    // (3) Copy each surviving candidate's already-staged quads into its material slot's bucket.
                    for (int s = 0; s < candidateCount; s++)
                    {
                        if (!_survivors[s]) continue;
                        CandidateEmit          emit   = _emit[_candidates[s].LabelIndex];
                        NativeList<PlacedQuad> bucket = _slotQuads[emit.Slot];
                        for (int k = 0; k < emit.QuadCount; k++)
                        {
                            bucket.Add(_stagedQuads[emit.QuadStart + k]);
                            totalQuads++;
                        }
                    }
                }

                LastQuadCount = totalQuads;
                if (totalQuads == 0) return; // nothing visible this frame — no draw call submitted

                // Submit one draw per non-empty slot, each with its own per-layer material (or the default).
                for (int g = 0; g < slotCount; g++)
                {
                    if (_slotQuads[g].Length == 0) continue;
                    Material material = (materials != null && g < materials.Count && materials[g] != null)
                        ? materials[g]
                        : _material;
                    BuildAndSubmit(_slotQuads[g], _slotMeshes[g], material, viewportLogicalPx, atlas);
                }
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
            Array.Resize(ref _survivors,  cap);
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
            Array.Resize(ref _survivors,  cap);
            Array.Resize(ref _emit,       cap);
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

        /// <summary>
        /// Projects + stages one POINT label: one axis-aligned collision box (the whole-label AABB, Slice 2 —
        /// unrotated even under text-rotation-alignment:map, matching the point behaviour) + its glyph quads
        /// (at the shared anchor, rotated by the #4 bearing). Writes <c>_candidates[ordinal]</c> +
        /// <c>_emit[ordinal]</c> and returns <c>true</c>, or <c>false</c> if it has no quads or its anchor
        /// culls off-screen (no pool entries appended on a skip).
        /// </summary>
        private int StagePointLabel(LabelInstance label, in float4x4 viewProj, double3 sceneOriginRender,
            float bearingRadians, double2 viewportLogicalPx, int slotCount, int ordinal,
            ref int boxCount, ref int stagedCount)
        {
            IReadOnlyList<SymbolQuad> quads = label.Layout?.Quads;
            if (quads == null || quads.Count == 0) return 0;

            if (!LabelScreenProjection.TryProjectAnchor(label.AnchorRender, sceneOriginRender, viewProj,
                    viewportLogicalPx, out float2 screenPx, out float depth))
                return 0; // behind camera or far outside the viewport

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

            EnsureCandidateSlot(ordinal);
            _candidates[ordinal] = new LabelCandidate
            {
                BoxStart = boxStart, BoxCount = 1,
                SortKey = label.SortKey, FeatureIndex = label.FeatureIndex, TileKey = label.TileKey,
                AllowOverlap = label.AllowOverlap, IgnorePlacement = label.IgnorePlacement, LabelIndex = ordinal,
            };
            _emit[ordinal] = new CandidateEmit { QuadStart = quadStart, QuadCount = quads.Count, Slot = slot };
            return 1;
        }

        /// <summary>
        /// Projects + stages one CURVED along-line label (#5). Projects the render-space path (behind-camera
        /// cull only — a partly-off-screen line still labels), walks it, and stages one candidate per along-line
        /// ANCHOR: <c>line-center</c> → a single centred anchor; <c>line</c> → repeated anchors every
        /// <c>symbol-spacing</c> px (B4). Each anchor stages N per-glyph quads at their along-line screen points
        /// rotated to the local TANGENT, with a per-glyph rotated-corner collision box
        /// (<see cref="LabelBox.BuildRotatedGlyph"/>) — one all-or-nothing candidate per anchor that competes
        /// with point labels (B3). Returns the NUMBER of anchors staged (0 if the path culls, the projected line
        /// has zero length, or the label is longer than the whole line).
        ///
        /// <para><b>Tangent only, not the #4 bearing path:</b> the tangent comes from projected screen points,
        /// which already reflect the map bearing — routing through <c>LabelBearing.BillboardRotationRadians</c>
        /// too (line placement defaults rotation-alignment auto→map) would double-rotate.</para>
        /// </summary>
        private int StageCurvedLabel(LabelInstance label, in float4x4 viewProj, double3 sceneOriginRender,
            float bearingRadians, double2 viewportLogicalPx, int slotCount, int ordinal,
            ref int boxCount, ref int stagedCount)
        {
            double3[] path = label.PathRender;
            IReadOnlyList<CurvedGlyph> glyphs = label.CurvedGlyphs;
            if (path == null || path.Length < 2 || glyphs == null || glyphs.Count == 0) return 0;

            if (_pathScreenScratch.Length < path.Length)
            {
                int cap = _pathScreenScratch.Length == 0 ? 8 : _pathScreenScratch.Length;
                while (cap < path.Length) cap *= 2;
                _pathScreenScratch = new float2[cap];
            }

            // Project the path; any vertex behind the camera → skip this label (B2 — no near-plane clip). A
            // vertex projecting to a non-physical screen coord (near-plane blow-up) would explode the line
            // length and the anchor loop, so it also skips the whole label (bounds the work at the source).
            float pathDepth = 0f;
            for (int v = 0; v < path.Length; v++)
            {
                if (!LabelScreenProjection.TryProjectPoint(path[v], sceneOriginRender, viewProj,
                        viewportLogicalPx, out float2 sp, out float d))
                    return 0;
                if (!(math.abs(sp.x) < MaxProjectedPx && math.abs(sp.y) < MaxProjectedPx))
                    return 0; // near-plane / degenerate projection — skip (also rejects NaN via the negated test)
                _pathScreenScratch[v] = sp;
                if (v == path.Length / 2) pathDepth = d; // representative depth (labels are overlay anyway)
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

            int staged = 0;
            if (label.Placement == SymbolPlacement.Line)
            {
                // Fixed screen-space spacing from a half-spacing offset (MapLibre getAnchors); an anchor is
                // taken only where the whole label fits between the line ends (no glyph piled at a clamped end).
                // The anchor count is HARD-CAPPED (MaxAnchorsPerLine) — a real line uses a handful; the cap only
                // guards a degenerate projected length from spinning an unbounded loop (never trust length/spacing
                // to be small). Computed in float then clamped BEFORE the int cast so a huge length can't overflow.
                float spacing = math.max(1f, label.SpacingPx);
                int anchorCount = (int)math.min(total / spacing, (float)MaxAnchorsPerLine);
                for (int k = 0; k < anchorCount; k++)
                {
                    float centerArc = spacing * (k + 0.5f);
                    if (centerArc - halfSpan < 0f || centerArc + halfSpan > total) continue;
                    // Only advance the ordinal when the anchor is actually staged (text-max-angle may drop it).
                    if (StageCurvedAnchor(label, ordinal + staged, glyphs, centerArc, labelCenterBaked, scale,
                            color, slot, pathDepth, bearingRadians, ref boxCount, ref stagedCount))
                        staged++;
                }
                // No spacing anchor placed but the line IS long enough for one label → try a single centred one
                // (so a line between spacing/2 and spacing long still gets labelled, unless it's too curved).
                if (staged == 0 &&
                    StageCurvedAnchor(label, ordinal, glyphs, total * 0.5f, labelCenterBaked, scale,
                        color, slot, pathDepth, bearingRadians, ref boxCount, ref stagedCount))
                    staged = 1;
            }
            else // LineCenter — one anchor at the middle (fits: labelSpanPx <= total ⇒ half within each side).
            {
                if (StageCurvedAnchor(label, ordinal, glyphs, total * 0.5f, labelCenterBaked, scale,
                        color, slot, pathDepth, bearingRadians, ref boxCount, ref stagedCount))
                    staged = 1;
            }

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
        private bool StageCurvedAnchor(LabelInstance label, int ordinal, IReadOnlyList<CurvedGlyph> glyphs,
            float centerArc, float labelCenterBaked, float scale, float4 color, int slot, float pathDepth,
            float bearingRadians, ref int boxCount, ref int stagedCount)
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

            EnsureCandidateSlot(ordinal);
            _candidates[ordinal] = new LabelCandidate
            {
                BoxStart = boxStart, BoxCount = glyphs.Count,
                SortKey = label.SortKey, FeatureIndex = label.FeatureIndex, TileKey = label.TileKey,
                AllowOverlap = label.AllowOverlap, IgnorePlacement = label.IgnorePlacement, LabelIndex = ordinal,
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

                // Pin the draw to THIS map camera. A null camera submits for EVERY camera, which would draw this
                // camera's screen-space vertices into the SceneView/other cameras (at wrong positions, since the
                // verts are projected for this camera only). _camera.Camera confines it to the one we projected for.
                var rp = new RenderParams(material)
                {
                    camera            = _camera.Camera,
                    worldBounds       = HugeBounds,
                    receiveShadows    = false,
                    shadowCastingMode = ShadowCastingMode.Off,
                };
                Graphics.RenderMesh(in rp, mesh, 0, Matrix4x4.identity);
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
        }
    }
}