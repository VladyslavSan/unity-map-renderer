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

        // Slice-2 collision scratch — plain managed arrays reused across Ticks (grown geometrically, so
        // steady-state Ticks with a stable label count never reallocate — T4). The greedy pass is managed
        // (not a Burst job): it is order-dependent (each placement depends on all prior survivors), so it is
        // inherently serial — the acceleration lever is the spatial grid (_collisionGrid), not Burst. It
        // stays zero-GC over these reused buffers. _boxes/_survivors are indexed by CANDIDATE position
        // (0..candidateCount, reordered by the in-place sort); _render is keyed by SOURCE LABEL index
        // (survives the sort — read back via LabelBox.LabelIndex).
        private LabelBox[]    _boxes     = Array.Empty<LabelBox>();
        private bool[]        _survivors = Array.Empty<bool>();
        private LabelRender[] _render    = Array.Empty<LabelRender>();

        // Reused screen-space grid that turns the greedy collision from O(n²) to ~O(n·k) (k = local label
        // density) — the fix for the ~100 ms MapRenderer.Symbol.Collide at zoom-14-big-city label counts.
        // Owned here + reused every frame so there is zero per-frame GC (T4); its survivor set is
        // bit-identical to the brute-force reference (LabelCollisionTests differential).
        private readonly LabelCollisionGrid _collisionGrid = new LabelCollisionGrid();

        // Per-candidate render data the collision box does NOT carry (kept off the blittable LabelBox):
        // the projected anchor + depth + baked vertex color, keyed by source label index so it is stable
        // across the in-place box sort.
        private struct LabelRender
        {
            public float2 AnchorScreenPx;
            public float  Depth;
            public float4 Color;
        }

        /// <summary>Number of <see cref="Tick"/> calls so far — T5 structural guard (the vertex buffer is
        /// rebuilt every Tick, not once at tile consume). Test surface.</summary>
        internal int TickCount { get; private set; }

        /// <summary>The quad count submitted on the LAST <see cref="Tick"/> (0 if nothing was visible). Test surface.</summary>
        internal int LastQuadCount { get; private set; }

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

                    // (1) Project every label's anchor, cull off-screen ones, and build the collision box for
                    //     each survivor of projection. _render[i] (keyed by SOURCE label index) caches the
                    //     projected anchor/depth/color so the sort in step (2) can reorder _boxes freely.
                    int candidateCount = 0;
                    using (PmProject.Auto())
                    {
                        for (int i = 0; i < labels.Count; i++)
                        {
                            LabelInstance             label = labels[i];
                            IReadOnlyList<SymbolQuad> quads = label?.Layout?.Quads;
                            if (quads == null || quads.Count == 0) continue;

                            if (!LabelScreenProjection.TryProjectAnchor(
                                    label.AnchorRender, sceneOriginRender, viewProj, viewportLogicalPx,
                                    out float2 screenPx, out float depth))
                            {
                                continue; // behind camera or far outside the viewport
                            }

                            // sRGB→linear: the symbol paint's text-color is sRGB (parsed from the style's hex/rgb),
                            // but the project renders in Linear color space and the shader emits this vertex color
                            // directly — so bake LINEAR here, exactly as StyledFill/LineTileBuilder do via
                            // Color.linear. Without it a dark #333 label uploads as linear 0.2 and displays as ~0.48
                            // mid-gray (washed out / hard to read). Alpha is not gamma-encoded — carry it straight,
                            // then fold in text-opacity.
                            float4 srgb   = label.Paint.TextColor;
                            Color  linear = new Color(srgb.x, srgb.y, srgb.z, 1f).linear;
                            float4 color  = new float4(linear.r, linear.g, linear.b, srgb.w * label.Paint.Opacity);
                            _render[i] = new LabelRender { AnchorScreenPx = screenPx, Depth = depth, Color = color };

                            _boxes[candidateCount++] = LabelBox.Build(
                                screenPx, label.Layout.BoundsMin, label.Layout.BoundsMax,
                                label.TextSizePx, label.PaddingPx,
                                label.SortKey, label.FeatureIndex, label.TileKey, i,
                                label.AllowOverlap, label.IgnorePlacement);
                        }
                    }

                    // (2) Greedy, sort-key-driven collision — GLOBAL across all layers (sorts _boxes in place).
                    using (PmCollide.Auto())
                        LabelCollision.SelectSurvivors(_boxes, candidateCount, _survivors, _collisionGrid);

                    // (3) Expand ONLY survivors' quads into their MATERIAL SLOT's buffer, in placement order.
                    for (int s = 0; s < candidateCount; s++)
                    {
                        if (!_survivors[s]) continue;

                        int                       li     = _boxes[s].LabelIndex;
                        LabelRender               render = _render[li];
                        LabelInstance             label  = labels[li];
                        IReadOnlyList<SymbolQuad> quads  = label.Layout.Quads;

                        int slot                                = label.MaterialIndex;
                        if (slot < 0 || slot >= slotCount) slot = 0; // demo path / out-of-range → default slot
                        NativeList<PlacedQuad> bucket           = _slotQuads[slot];

                        for (int q = 0; q < quads.Count; q++)
                        {
                            bucket.Add(new PlacedQuad
                            {
                                Quad           = quads[q],
                                AnchorScreenPx = render.AnchorScreenPx,
                                TextSizePx     = label.TextSizePx,
                                Depth          = render.Depth,
                                Color          = render.Color,
                            });
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

        // Grows the collision scratch to hold at least `count` candidates. Geometric doubling so a stable
        // (or shrinking) label count never reallocates after warm-up — the T4 no-per-frame-GC guarantee.
        private void EnsureCandidateCapacity(int count)
        {
            if (_boxes.Length >= count) return;
            int cap                 = _boxes.Length == 0 ? 16 : _boxes.Length;
            while (cap < count) cap *= 2;
            Array.Resize(ref _boxes,     cap);
            Array.Resize(ref _survivors, cap);
            Array.Resize(ref _render,    cap);
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