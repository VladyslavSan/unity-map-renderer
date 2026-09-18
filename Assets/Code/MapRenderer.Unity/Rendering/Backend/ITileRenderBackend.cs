using System;
using System.Collections.Generic;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.Rendering;
using MapRenderer.Core.Geo;

namespace MapRenderer.Unity.Rendering.Backend
{
    /// <summary>
    /// Common contract for the tile-draw backends —
    /// <see cref="BRG.TileRenderer"/> (raw BatchRendererGroup),
    /// <see cref="Entities.TileRenderer"/> (Entities Graphics), and
    /// <see cref="GameObjects.TileRenderer"/> (one GameObject per tile-layer, drawn by the SRP Batcher).
    /// Each registers one draw item per (tile, layer) mesh and is driven by a per-frame
    /// <see cref="Rebuild"/> that recomputes the floating-origin transforms. <c>TileManager</c> holds
    /// exactly one of these and treats them uniformly through this interface; only construction differs.
    ///
    /// <para>Construction carries the full-width, draw-slot-aligned per-layer lists — materials, style ids,
    /// and each layer's <c>Style.IRenderLayer.CastShadows</c> declaration. All three implementations must
    /// TRANSPORT the shadow list verbatim (indexed by <c>materialIndex</c>, absent or short slot ⇒
    /// <see cref="UnityEngine.Rendering.ShadowCastingMode.Off"/>) and must never re-derive it from the layer
    /// type or the material — re-deriving is how three backends drift apart.</para>
    /// </summary>
    internal interface ITileRenderBackend : IDisposable
    {
        /// <summary>
        /// Registers a tile-layer mesh as a draw item. <paramref name="tileOriginRender"/> is the tile's
        /// SW-corner projected render origin (S91-C, <c>double3</c>: Mercator <c>(mercX, 0, mercZ)</c>, globe
        /// ECEF) — the Level-2 <see cref="Rebuild"/> places the item relative to the frame's scene origin.
        /// <paramref name="materialIndex"/> is the layer's global SLOT (its
        /// <c>Style.IRenderLayer.DrawIndex</c>), indexing the full-width layer-material list; non-tile-mesh
        /// slots (symbol/background) are null and never receive this call. <paramref name="tileId"/> identifies
        /// the owning tile — the <see cref="Entities.TileRenderer"/>
        /// and <see cref="GameObjects.TileRenderer"/> use it to group a tile's layers under one named parent
        /// (entity / GameObject) for the per-tile debug affordance; <see cref="BRG.TileRenderer"/> has no
        /// per-item hierarchy and ignores it. Returns a handle for <see cref="RemoveItem"/>.
        /// </summary>
        int AddTileLayer(Mesh mesh, double3 tileOriginRender, int materialIndex, TileId tileId);

        /// <summary>Removes a previously registered draw item. Idempotent for unknown handles.</summary>
        void RemoveItem(int handle);

        /// <summary>
        /// Removes many draw items in ONE backend operation where the backend supports it. The
        /// <see cref="Entities.TileRenderer"/> destroys all the layer entities (plus any tile roots emptied by
        /// the batch) via a single <c>EntityManager.DestroyEntity(NativeArray&lt;Entity&gt;)</c> structural
        /// change instead of one per layer — the stall-#2 fix for the release storm. <see cref="BRG.TileRenderer"/>
        /// and <see cref="GameObjects.TileRenderer"/> fall back to a <see cref="RemoveItem"/> loop (their removal
        /// is already cheap — a dict remove / a GameObject destroy). Idempotent for unknown handles.
        /// </summary>
        void RemoveItems(ReadOnlySpan<int> handles);

        /// <summary>
        /// Per-frame: recompute every draw item's object-to-world from <paramref name="frame"/> (the
        /// camera-relative <see cref="SceneFrame"/> — scene origin + rebase) and refresh backend state for the
        /// upcoming render. For Mercator <paramref name="frame"/> is identity-rebase, so placement reduces to
        /// the pre-S91 translation.
        /// </summary>
        void Rebuild(in SceneFrame frame);

        /// <summary>
        /// Declares whether a layer SLOT draws at all. A gated-out slot submits no draw item — to the camera
        /// view or the light view — so a layer outside its zoom range costs no vertex and no draw call, not
        /// just no fragment. Pushed per frame from the layer's
        /// <c>Style.IFadeableRenderLayer.PaintsSomething</c>; an unchanged value must cost nothing,
        /// and a slot never declared draws.
        ///
        /// <para>Non-local invariant: the three implementations differ in MECHANISM (BRG drops the slot
        /// while it computes the emit order, GameObjects disables the renderer, Entities adds
        /// <c>DisableRendering</c>) but must agree on the OUTCOME, including for an item added while the
        /// slot is already gated — same rule <c>Style.IRenderLayer.CastShadows</c> carries.</para>
        /// </summary>
        /// <param name="slot">The layer's global SLOT; out-of-range indices are ignored.</param>
        /// <param name="visible">False to submit no draw for this slot.</param>
        void SetLayerVisible(int slot, bool visible);

        /// <summary>Replaces the full-width, SLOT-aligned per-layer material/shadow-mode lists —
        /// restyle-time counterpart of construction's. A slot unchanged BY REFERENCE keeps its registration
        /// and every live item; a slot going to null retires its own items its OWN way (the three backends
        /// deliberately differ, and the caller MUST set every survivor's draw order first) — `docs/tile-pipeline-design.md` §1.10.</summary>
        void SetLayerMaterials(IReadOnlyList<Material> layerMaterials, IReadOnlyList<ShadowCastingMode> layerShadowModes);

        /// <summary>
        /// XZ scene-space bounding box covering all live tile draw items (each draw item's translation,
        /// plus <paramref name="tileSizeWorld"/> for the tile's mesh extent beyond its origin). Used by
        /// tests to frame a camera that sees all rendered tiles. Returns <c>default</c> when empty.
        /// </summary>
        Bounds ComputeSceneBounds(float tileSizeWorld);
    }
}
