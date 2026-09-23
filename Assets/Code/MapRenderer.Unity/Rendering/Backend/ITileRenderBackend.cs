using System;
using System.Collections.Generic;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.Rendering;
using MapRenderer.Core.Geo;

namespace MapRenderer.Unity.Rendering.Backend
{
    /// <summary>
    /// Common contract for the tile-draw backends — <see cref="BRG.TileRenderer"/>,
    /// <see cref="Entities.TileRenderer"/>, and <see cref="GameObjects.TileRenderer"/>. Each registers one
    /// draw item per (tile, layer) mesh and is driven by a per-frame <see cref="Rebuild"/>;
    /// <c>TileManager</c> holds exactly one, uniformly, through this interface. Non-local invariant: every
    /// implementation must TRANSPORT construction's per-slot shadow list (each layer's
    /// <c>Style.IRenderLayer.CastShadows</c>, indexed by <c>materialIndex</c>; absent or short slot ⇒
    /// <see cref="UnityEngine.Rendering.ShadowCastingMode.Off"/>) verbatim and never re-derive it from the
    /// layer type or material, or the backends drift apart.
    /// </summary>
    internal interface ITileRenderBackend : IDisposable
    {
        /// <summary>
        /// Registers a tile-layer mesh as a draw item. <paramref name="tileOriginRender"/> is the tile's
        /// SW-corner projected render origin (<c>double3</c>); <see cref="Rebuild"/> places it relative to
        /// the frame's scene origin. <paramref name="materialIndex"/> is the layer's global SLOT into the
        /// full-width material list; non-tile-mesh slots are null and never receive this call.
        /// <paramref name="tileId"/> groups a tile's layers under one named parent for debugging
        /// (<see cref="BRG.TileRenderer"/> ignores it). Returns a handle for <see cref="RemoveItem"/>.
        /// </summary>
        int AddTileLayer(Mesh mesh, double3 tileOriginRender, int materialIndex, TileId tileId);

        /// <summary>Removes a previously registered draw item. Idempotent for unknown handles.</summary>
        void RemoveItem(int handle);

        /// <summary>
        /// Removes many draw items in ONE backend operation where the backend supports it.
        /// <see cref="Entities.TileRenderer"/> destroys all affected entities (layers plus any emptied tile
        /// roots) in a single structural change instead of one per layer, so a release storm costs one
        /// structural change. <see cref="BRG.TileRenderer"/> and <see cref="GameObjects.TileRenderer"/> fall back to a
        /// <see cref="RemoveItem"/> loop. Idempotent for unknown handles.
        /// </summary>
        void RemoveItems(ReadOnlySpan<int> handles);

        /// <summary>
        /// Per-frame: recompute every draw item's object-to-world from <paramref name="frame"/> (the
        /// camera-relative <see cref="SceneFrame"/> — scene origin + rebase) and refresh backend state for the
        /// upcoming render. For Mercator <paramref name="frame"/> is identity-rebase, so placement reduces to
        /// a translation.
        /// </summary>
        void Rebuild(in SceneFrame frame);

        /// <summary>
        /// Declares whether a layer SLOT draws at all. A gated-out slot submits no draw item — to the
        /// camera view or the light view — so a layer outside its zoom range costs no vertex and no draw
        /// call. Pushed per frame from <c>Style.IFadeableRenderLayer.PaintsSomething</c>; an unchanged
        /// value must cost nothing, and a slot never declared draws. Non-local invariant: the three
        /// implementations differ in mechanism but must agree on the outcome, including for an item added
        /// while the slot is already gated.
        /// </summary>
        /// <param name="slot">The layer's global SLOT; out-of-range indices are ignored.</param>
        /// <param name="visible">False to submit no draw for this slot.</param>
        void SetLayerVisible(int slot, bool visible);

        /// <summary>Replaces the full-width, SLOT-aligned per-layer material/shadow-mode lists —
        /// restyle-time counterpart of construction's. A slot unchanged BY REFERENCE keeps its registration
        /// and every live item; a slot going to null retires its own items its OWN way (the three backends
        /// differ by design, and the caller MUST set every survivor's draw order first) — `docs/tile-pipeline-design.md`.</summary>
        void SetLayerMaterials(IReadOnlyList<Material> layerMaterials, IReadOnlyList<ShadowCastingMode> layerShadowModes);

        /// <summary>
        /// XZ scene-space bounding box covering all live tile draw items (each draw item's translation,
        /// plus <paramref name="tileSizeWorld"/> for the tile's mesh extent beyond its origin). Used by
        /// tests to frame a camera that sees all rendered tiles. Returns <c>default</c> when empty.
        /// </summary>
        Bounds ComputeSceneBounds(float tileSizeWorld);
    }
}
