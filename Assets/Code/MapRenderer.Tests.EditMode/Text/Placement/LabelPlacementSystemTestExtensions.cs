// Unity EditMode only — SymbolGatherPlan / Unity.Collections. NOT registered in core-tests.csproj.
//
// The label unit tests' concise "tick these labels" seam, living in the TEST assembly instead of on
// LabelPlacementSystem. It replaces the demo Tick(in SceneFrame, IReadOnlyList<LabelInstance>, ...)
// overload, which existed on the production class for tests and demos only.
//
// It is NOT a reimplementation of that overload: it wraps the labels into a real SymbolGatherPlan and
// calls the PRODUCTION entry, so a unit test written against this seam exercises the path that ships.
// The convenience is the wrapping, not a second code path.
//
// The projection is an explicit parameter rather than read off the system, because reaching it would
// mean adding an accessor to LabelPlacementSystem with no production caller — precisely the surface
// this step removes. Callers already have the MapCamera they built the system with.

using System.Collections.Generic;
using UnityEngine;
using MapRenderer.Core.Geo;
using MapRenderer.Core.Text;
using MapRenderer.Core.Text.Placement;
using MapRenderer.Unity.Rendering.Backend;
using MapRenderer.Unity.Rendering.Style;
using MapRenderer.Unity.Text;
using MapRenderer.Unity.Text.Placement;

namespace MapRenderer.Tests.Text.Placement
{
    internal static class LabelPlacementSystemTestExtensions
    {
        /// <summary>
        /// Tick <paramref name="labels"/> through the production <see cref="SymbolGatherPlan"/> entry,
        /// building a throwaway plan for the call. Safe because <c>GatherIntoMirror</c> COPIES into the
        /// native mirror — neither the plan's lists nor the store's baked blocks are needed once
        /// <c>Tick</c> returns.
        ///
        /// <para>Allocates per call (the plan, the store, their native lists). Fine for a behavioural
        /// test; NOT fine inside an <c>Is.Not.AllocatingGCMemory</c> region, and it defeats the gather
        /// memo because each call presents a new plan identity. Both of those cases want the
        /// <see cref="TickLabels(LabelPlacementSystem, in SceneFrame, TestSymbolPlan, IReadOnlyList{LabelInstance}, GlyphAtlasTexture, float, IReadOnlyList{SymbolRenderLayer}, Texture2D)"/>
        /// overload with a fixture-owned plan.</para>
        /// </summary>
        public static void TickLabels(this LabelPlacementSystem system, in SceneFrame frame,
            IReadOnlyList<LabelInstance> labels, GlyphAtlasTexture atlas, IProjection projection,
            float deltaTime = float.PositiveInfinity,
            IReadOnlyList<SymbolRenderLayer> symbolLayers = null, Texture2D spriteTexture = null)
        {
            using var plan = new TestSymbolPlan(projection);
            system.Tick(in frame, plan.Build(labels, SlotCountFor(labels)), atlas, deltaTime,
                symbolLayers, spriteTexture);
        }

        /// <summary>
        /// Same, against a caller-owned <see cref="TestSymbolPlan"/> — no per-call allocation, and a
        /// stable plan identity so the gather memo behaves as it does in production.
        /// </summary>
        public static void TickLabels(this LabelPlacementSystem system, in SceneFrame frame,
            TestSymbolPlan plan, IReadOnlyList<LabelInstance> labels, GlyphAtlasTexture atlas,
            float deltaTime = float.PositiveInfinity,
            IReadOnlyList<SymbolRenderLayer> symbolLayers = null, Texture2D spriteTexture = null)
            => system.Tick(in frame, plan.Build(labels, SlotCountFor(labels)), atlas, deltaTime,
                symbolLayers, spriteTexture);

        // ── World-slot inspection ────────────────────────────────────────────────────────────────────
        // These four were `internal` members on LabelPlacementSystem, each a one-line forward to its
        // WorldLabelRenderer and each documented "Test surface" — the shape the conventions call out as not
        // belonging on a production class. They forward to the same renderer from here instead; the field was
        // broadened private -> internal, which IS the sanctioned footprint. Names are unchanged, so every call
        // site reads exactly as before.

        /// <summary>The WORLD mesh bound to <c>(tileKey, slot, kind)</c>'s slot, or null if nothing has been
        /// emitted to it. Returns whatever the slot last built, regardless of current visibility — see
        /// <see cref="IsWorldSlotVisible"/> for the presenter's show/hide state.</summary>
        public static bool TryGetWorldSlotMesh(this LabelPlacementSystem system, long tileKey, int slot,
            LabelKind kind, out Mesh mesh)
            => system.WorldRenderer.TryGetSlotMesh(tileKey, slot, kind, out mesh);

        /// <summary>Whether the WORLD presenter for <c>(tileKey, slot, kind)</c> is currently drawing — the
        /// "exactly one presenter draws" check.</summary>
        public static bool IsWorldSlotVisible(this LabelPlacementSystem system, long tileKey, int slot, LabelKind kind)
            => system.WorldRenderer.IsSlotVisible(tileKey, slot, kind);

        /// <summary>Number of LIVE tile containers in the label tree. Not <c>WorldLabelTreeRoot().childCount</c>:
        /// the root also carries the inactive pool nodes that recycled leaves/layer-nodes/containers park
        /// under, so a raw child count answers "live tiles + pools", which is not a quantity anyone means.</summary>
        public static int WorldLabelTileCount(this LabelPlacementSystem system)
            => system.WorldRenderer._tree.NodeCount;

        /// <summary>The label tree's root transform ("Map Labels") — the grouping tooth's Hierarchy entry point.</summary>
        public static Transform WorldLabelTreeRoot(this LabelPlacementSystem system)
            => system.WorldRenderer.TreeRoot;

        /// <summary>The WORLD text/icon child transform for <c>(tileKey, slot, kind)</c>, or null if never
        /// presented — the grouping tooth asserts root -> tile container -> symbol-layer node -> this child.</summary>
        public static Transform WorldSlotTransform(this LabelPlacementSystem system, long tileKey, int slot, LabelKind kind)
            => system.WorldRenderer.GetSlotTransform(tileKey, slot, kind);

        // The bake needs a slot count covering every label's MaterialIndex; the retired demo overload
        // hardcoded 1 because it had no layer list. Deriving it keeps multi-layer callers working
        // without every call site having to state it.
        private static int SlotCountFor(IReadOnlyList<LabelInstance> labels)
        {
            int max = 0;
            for (int i = 0; i < (labels?.Count ?? 0); i++)
                if (labels[i] != null && labels[i].MaterialIndex > max) max = labels[i].MaterialIndex;
            return max + 1;
        }
    }
}
