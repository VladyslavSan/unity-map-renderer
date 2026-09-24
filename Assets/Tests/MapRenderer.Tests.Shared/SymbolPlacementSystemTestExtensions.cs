// Unity EditMode only — SymbolGatherPlan / Unity.Collections. NOT registered in core-tests.csproj.
//
// Non-obvious why: this is the symbol unit tests' "tick these symbols" seam, living in the TEST assembly
// instead of on SymbolPlacementSystem. It wraps a SymbolTileBuffer into a real SymbolGatherPlan and calls
// the PRODUCTION entry, so a unit test written against it exercises the path that ships — the wrapping is
// the only convenience, not a second code path. The projection is an explicit parameter rather than read
// off the system, because that would mean adding an accessor to SymbolPlacementSystem with no production
// caller; callers already have the MapCamera they built the system with.

using System.Collections.Generic;
using Unity.Collections;
using UnityEngine;
using MapRenderer.Core.Geo;
using MapRenderer.Core.Text;
using MapRenderer.Core.Text.Placement;
using MapRenderer.Unity.Rendering.Backend;
using MapRenderer.Unity.Rendering.Style;
using MapRenderer.Unity.Text;
using MapRenderer.Unity.Text.Placement;

namespace MapRenderer.Tests
{
    internal static class SymbolPlacementSystemTestExtensions
    {
        /// <summary>
        /// Ticks <paramref name="buffer"/> through the production <see cref="SymbolGatherPlan"/> entry with a
        /// throwaway plan, which is safe because <c>GatherIntoMirror</c> copies before <c>Tick</c> returns. It
        /// allocates per call; use the fixture-owned-plan overload inside <c>Is.Not.AllocatingGCMemory</c> or
        /// where the gather memo needs a stable plan identity:
        /// <see cref="TickSymbols(SymbolPlacementSystem, in SceneFrame, TestSymbolPlan, SymbolTileBuffer, GlyphAtlasTexture, float, IReadOnlyList{SymbolRenderLayer}, Texture2D)"/>.
        /// </summary>
        public static void TickSymbols(this SymbolPlacementSystem system, in SceneFrame frame,
            SymbolTileBuffer buffer, GlyphAtlasTexture atlas, IProjection projection,
            float deltaTime = float.PositiveInfinity,
            IReadOnlyList<SymbolRenderLayer> symbolLayers = null, Texture2D spriteTexture = null)
        {
            using var plan = new TestSymbolPlan(projection);
            system.Tick(in frame, plan.Build(buffer, SlotCountFor(buffer)), atlas, deltaTime,
                symbolLayers, spriteTexture);
        }

        /// <summary>
        /// Same, against a caller-owned <see cref="TestSymbolPlan"/> — no per-call allocation, and a
        /// stable plan identity so the gather memo behaves as it does in production.
        /// </summary>
        public static void TickSymbols(this SymbolPlacementSystem system, in SceneFrame frame,
            TestSymbolPlan plan, SymbolTileBuffer buffer, GlyphAtlasTexture atlas,
            float deltaTime = float.PositiveInfinity,
            IReadOnlyList<SymbolRenderLayer> symbolLayers = null, Texture2D spriteTexture = null)
            => system.Tick(in frame, plan.Build(buffer, SlotCountFor(buffer)), atlas, deltaTime,
                symbolLayers, spriteTexture);

        // ── Staged collision boxes ───────────────────────────────────────────────────────────────────

        /// <summary>Non-local invariant: the last <c>Tick</c>'s staged collision boxes, valid over
        /// <c>[0, SymbolPlacementSystem.LastBoxCount)</c> — lives here, not on the system, for the same
        /// reason the world-slot forwards below do (a "Test surface" DATA accessor is the shape the
        /// conventions bar from a production class; <c>LastBoxCount</c> stays there because it's a
        /// counter-telemetry member, a different shape). Box ORDER is staging order and collision does not
        /// disturb it — <c>CollisionJob</c> sorts the CANDIDATES and its grid stores absolute box indices, so
        /// the pool itself is never reordered. With a single curved symbol, <c>StageCurvedAnchor</c> appends
        /// box <c>g</c> and quad <c>g</c> in the same loop iteration, so <c>box[g]</c> pairs with
        /// <c>vertices[4g … 4g+3]</c> — a caller relying on that pairing must ASSERT it (box count == vertex
        /// count / 4), not assume it.</summary>
        public static NativeArray<SymbolBox> LastStagedBoxes(this SymbolPlacementSystem system)
            => system._stageBoxes.AsArray();

        // ── World-slot inspection ────────────────────────────────────────────────────────────────────
        // Non-obvious why: a "Test surface" DATA accessor does not belong on the production class, so these
        // are one-line forwards to WorldSymbolRenderer, broadened private -> internal — the sanctioned footprint.

        /// <summary>The WORLD mesh bound to <c>(tileKey, slot, kind)</c>'s slot, or null if nothing has been
        /// emitted to it. Returns whatever the slot last built, regardless of current visibility — see
        /// <see cref="IsWorldSlotVisible"/> for the presenter's show/hide state.</summary>
        public static bool TryGetWorldSlotMesh(this SymbolPlacementSystem system, long tileKey, int slot,
            SymbolKind kind, out Mesh mesh)
            => system.WorldRenderer.TryGetSlotMesh(tileKey, slot, kind, out mesh);

        /// <summary>Whether the WORLD presenter for <c>(tileKey, slot, kind)</c> is currently drawing — the
        /// "exactly one presenter draws" check.</summary>
        public static bool IsWorldSlotVisible(this SymbolPlacementSystem system, long tileKey, int slot, SymbolKind kind)
            => system.WorldRenderer.IsSlotVisible(tileKey, slot, kind);

        /// <summary>Number of LIVE tile containers in the symbol tree. Not <c>WorldSymbolTreeRoot().childCount</c>:
        /// the root also carries the inactive pool nodes that recycled leaves/layer-nodes/containers park
        /// under, so a raw child count answers "live tiles + pools", which is not a quantity anyone means.</summary>
        public static int WorldSymbolTileCount(this SymbolPlacementSystem system)
            => system.WorldRenderer._tree.NodeCount;

        /// <summary>The symbol tree's root transform ("Map Symbols") — the grouping tooth's Hierarchy entry point.</summary>
        public static Transform WorldSymbolTreeRoot(this SymbolPlacementSystem system)
            => system.WorldRenderer.TreeRoot;

        /// <summary>The WORLD text/icon child transform for <c>(tileKey, slot, kind)</c>, or null if never
        /// presented — the grouping tooth asserts root -> tile container -> symbol-layer node -> this child.</summary>
        public static Transform WorldSlotTransform(this SymbolPlacementSystem system, long tileKey, int slot, SymbolKind kind)
            => system.WorldRenderer.GetSlotTransform(tileKey, slot, kind);

        // The bake needs a slot count covering every symbol's MaterialIndex. Deriving it keeps multi-layer
        // callers working without every call site having to state it.
        private static int SlotCountFor(SymbolTileBuffer buffer)
        {
            int max = 0;
            int count = buffer?.Symbols.Count ?? 0;
            for (int i = 0; i < count; i++)
            {
                ShapedSymbol symbol = buffer.Symbols[i];
                if (symbol.MaterialIndex > max) max = symbol.MaterialIndex;
            }
            return max + 1;
        }
    }
}
