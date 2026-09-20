// Unity EditMode only — SymbolGatherPlan / Unity.Collections. NOT registered in core-tests.csproj.
//
// The symbol unit tests' concise "tick these symbols" seam, living in the TEST assembly instead of on
// SymbolPlacementSystem. It replaces the demo Tick(in SceneFrame, IReadOnlyList<…>, ...) overload (over the
// pre-migration per-symbol managed carrier), which existed on the production class for tests and demos only.
//
// It is NOT a reimplementation of that overload: it wraps a SymbolTileBuffer build buffer into a real
// SymbolGatherPlan and calls the PRODUCTION entry, so a unit test written against this seam exercises the
// path that ships. The convenience is the wrapping, not a second code path.
//
// The projection is an explicit parameter rather than read off the system, because reaching it would
// mean adding an accessor to SymbolPlacementSystem with no production caller — precisely the surface
// this step removes. Callers already have the MapCamera they built the system with.

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
        /// Tick <paramref name="buffer"/> through the production <see cref="SymbolGatherPlan"/> entry,
        /// building a throwaway plan for the call. Safe because <c>GatherIntoMirror</c> COPIES into the
        /// native mirror — neither the plan's lists nor the store's baked blocks are needed once
        /// <c>Tick</c> returns.
        ///
        /// <para>Allocates per call (the plan, the store, their native lists). Fine for a behavioural
        /// test; NOT fine inside an <c>Is.Not.AllocatingGCMemory</c> region, and it defeats the gather
        /// memo because each call presents a new plan identity. Both of those cases want the
        /// <see cref="TickSymbols(SymbolPlacementSystem, in SceneFrame, TestSymbolPlan, SymbolTileBuffer, GlyphAtlasTexture, float, IReadOnlyList{SymbolRenderLayer}, Texture2D)"/>
        /// overload with a fixture-owned plan.</para>
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

        /// <summary>W3 — the last <c>Tick</c>'s staged collision boxes, valid over
        /// <c>[0, SymbolPlacementSystem.LastBoxCount)</c>. Lives here, not on the system, for the same reason
        /// the world-slot forwards below do: a "Test surface" DATA accessor is the shape the conventions bar
        /// from a production class (<c>LastBoxCount</c> itself stays — it is a real N+1 of the counter
        /// telemetry family, which is a different shape).
        /// <para><b>Box ORDER is staging order, and collision does not disturb it</b> —
        /// <c>CollisionJob</c> sorts the CANDIDATES and its grid stores absolute box
        /// indices, so the box pool itself is never reordered. With a single curved symbol in the frame,
        /// <c>StageCurvedAnchor</c> appends box <c>g</c> and quad <c>g</c> in the same loop iteration, so
        /// <c>box[g]</c> pairs with that symbol's <c>vertices[4g … 4g+3]</c>. A caller relying on that pairing
        /// must ASSERT it (box count == vertex count / 4), not assume it.</para></summary>
        public static NativeArray<SymbolBox> LastStagedBoxes(this SymbolPlacementSystem system)
            => system._stageBoxes.AsArray();

        // ── World-slot inspection ────────────────────────────────────────────────────────────────────
        // These four were `internal` members on SymbolPlacementSystem, each a one-line forward to its
        // WorldSymbolRenderer and each documented "Test surface" — the shape the conventions call out as not
        // belonging on a production class. They forward to the same renderer from here instead; the field was
        // broadened private -> internal, which IS the sanctioned footprint. Names are unchanged, so every call
        // site reads exactly as before.

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

        // The bake needs a slot count covering every symbol's MaterialIndex; the retired demo overload
        // hardcoded 1 because it had no layer list. Deriving it keeps multi-layer callers working
        // without every call site having to state it.
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
