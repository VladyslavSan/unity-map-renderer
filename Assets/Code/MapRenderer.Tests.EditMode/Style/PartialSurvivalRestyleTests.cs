// Unity EditMode only — real Materials via MaterialFactory / MapMaterialSet, real RenderLayerSet; NOT in
// Tools/core-tests. UMR-151 partial-survival teeth, at RenderLayerSet level: reorder and removal only.

using System.Collections.Generic;
using NUnit.Framework;
using UnityEngine;
using MapRenderer.Core.Style;
using MapRenderer.Core.Rendering;
using MapRenderer.Unity.Rendering.Style;

namespace MapRenderer.Tests.Style
{
    [TestFixture]
    public class PartialSurvivalRestyleTests
    {
        // Three fill layers, one source. "a" changes fill-color (still gate-eligible: paint-only); "c" is
        // byte-identical; "b" is removed in the new document.
        private const string ThreeFillOld = @"{
    ""version"": 8, ""name"": ""T"",
    ""sources"": { ""s"": { ""type"": ""vector"", ""tiles"": [""https://x/{z}/{x}/{y}.pbf""] } },
    ""layers"": [
        { ""id"": ""a"", ""type"": ""fill"", ""source"": ""s"", ""source-layer"": ""a"", ""paint"": { ""fill-color"": [""rgba"",255,0,0,1] } },
        { ""id"": ""b"", ""type"": ""fill"", ""source"": ""s"", ""source-layer"": ""b"", ""paint"": { ""fill-color"": [""rgba"",0,255,0,1] } },
        { ""id"": ""c"", ""type"": ""fill"", ""source"": ""s"", ""source-layer"": ""c"", ""paint"": { ""fill-color"": [""rgba"",0,0,255,1] } }
    ]
}";

        private const string ThreeFillRemovedB = @"{
    ""version"": 8, ""name"": ""T"",
    ""sources"": { ""s"": { ""type"": ""vector"", ""tiles"": [""https://x/{z}/{x}/{y}.pbf""] } },
    ""layers"": [
        { ""id"": ""a"", ""type"": ""fill"", ""source"": ""s"", ""source-layer"": ""a"", ""paint"": { ""fill-color"": [""rgba"",255,128,0,1] } },
        { ""id"": ""c"", ""type"": ""fill"", ""source"": ""s"", ""source-layer"": ""c"", ""paint"": { ""fill-color"": [""rgba"",0,0,255,1] } }
    ]
}";

        private static RenderLayerSet Build(string json)
        {
            var set = new RenderLayerSet();
            set.Build(StyleParser.Parse(json), 0.0, MapMaterialSetTestUtil.Load());
            return set;
        }

        /// <summary>The discriminating tooth. Three fill layers A, B, C; restyle to (A, C) with A's
        /// fill-color changed. A and C must survive with their instance/Material/slot unchanged; B's slot
        /// must become a tombstone with its Material destroyed exactly once. A REPLACED Material is out of
        /// this stage's scope, so "destroyed and vacated" against "preserved and easing" is the strongest
        /// statement available here.</summary>
        [Test]
        public void RemovalRestyle_KeepsSurvivorsAndRetiresOnlyTheRemovedLayer()
        {
            var oldStyle = StyleParser.Parse(ThreeFillOld);
            var newStyle = StyleParser.Parse(ThreeFillRemovedB);
            var set = Build(ThreeFillOld);

            IRenderLayer layerA = set[0];
            IRenderLayer layerB = set[1];
            IRenderLayer layerC = set[2];
            Material materialA = layerA.Material;
            Material materialB = layerB.Material;
            Material materialC = layerC.Material;

            Assert.IsTrue(set.TryRestyleInPlace(oldStyle, newStyle, StyleTransition.Default, nowSeconds: 0.0),
                "A paint-only change on A plus a removal of B must take the in-place path.");

            Assert.AreEqual(3, set.Count, "the slot WIDTH never shrinks — B's slot becomes a tombstone, not a gap.");
            Assert.AreSame(layerA, set[0], "A's render-layer INSTANCE must be reference-equal — not rebuilt.");
            Assert.AreSame(layerC, set[2], "C's slot must be UNCHANGED — not compacted from 2 to 1.");
            Assert.AreSame(materialA, set[0].Material, "A's Material OBJECT must survive (identity, not just value).");
            Assert.AreSame(materialC, set[2].Material, "C's Material OBJECT must survive untouched.");

            Assert.IsInstanceOf<TombstoneRenderLayer>(set[1], "B's slot must hold a tombstone, not be compacted away.");
            Assert.IsTrue(materialB == null, // Unity fake-null: true only once Destroy actually ran.
                "B's Material must be destroyed — the tombstone swap disposes it exactly once.");

            Assert.Greater(layerA.TransitioningCount, 0, "A's fill-color changed — its uniform must be easing.");
            Assert.AreEqual(0, layerC.TransitioningCount, "C is byte-identical — nothing to ease.");
        }

        // Fill, symbol, fill restyled to (C, S, A): every slot survives, only declared order changes. The
        // symbol layer's ICON sits at LayerSubSlot.Base of its band, its TEXT at LayerSubSlot.Above.
        private const string FillSymbolFillOld = @"{
    ""version"": 8, ""name"": ""T"",
    ""sources"": { ""s"": { ""type"": ""vector"", ""tiles"": [""https://x/{z}/{x}/{y}.pbf""] } },
    ""layers"": [
        { ""id"": ""a"", ""type"": ""fill"", ""source"": ""s"", ""source-layer"": ""a"", ""paint"": { ""fill-color"": [""rgba"",255,0,0,1] } },
        { ""id"": ""s"", ""type"": ""symbol"", ""source"": ""s"", ""source-layer"": ""s"", ""layout"": { ""text-field"": ""{NAME}"" } },
        { ""id"": ""c"", ""type"": ""fill"", ""source"": ""s"", ""source-layer"": ""c"", ""paint"": { ""fill-color"": [""rgba"",0,0,255,1] } }
    ]
}";
        private const string FillSymbolFillReordered = @"{
    ""version"": 8, ""name"": ""T"",
    ""sources"": { ""s"": { ""type"": ""vector"", ""tiles"": [""https://x/{z}/{x}/{y}.pbf""] } },
    ""layers"": [
        { ""id"": ""c"", ""type"": ""fill"", ""source"": ""s"", ""source-layer"": ""c"", ""paint"": { ""fill-color"": [""rgba"",0,0,255,1] } },
        { ""id"": ""s"", ""type"": ""symbol"", ""source"": ""s"", ""source-layer"": ""s"", ""layout"": { ""text-field"": ""{NAME}"" } },
        { ""id"": ""a"", ""type"": ""fill"", ""source"": ""s"", ""source-layer"": ""a"", ""paint"": { ""fill-color"": [""rgba"",255,0,0,1] } }
    ]
}";

        /// <summary>T2. A reorder with NO paint change: every slot/Mesh/Material identity stays, but every
        /// declared-order-derived <c>Material.renderQueue</c> moves to the NEW order, including a symbol
        /// layer's icon band.</summary>
        [Test]
        public void ReorderRestyle_KeepsEverySlotAndRewritesOnlyTheQueue()
        {
            var oldStyle = StyleParser.Parse(FillSymbolFillOld);
            var newStyle = StyleParser.Parse(FillSymbolFillReordered);
            var set = Build(FillSymbolFillOld);

            var instances = new IRenderLayer[] { set[0], set[1], set[2] };
            var materials = new Material[] { set[0].Material, set[1].Material, set[2].Material };
            var iconMaterial = ((SymbolRenderLayer)set[1]).WorldIconMaterial;

            Assert.IsTrue(set.TryRestyleInPlace(oldStyle, newStyle, StyleTransition.Default, nowSeconds: 0.0),
                "a pure reorder with no paint change must take the in-place path.");

            for (int i = 0; i < 3; i++)
            {
                Assert.AreSame(instances[i], set[i], $"slot {i}'s instance must be unchanged.");
                Assert.AreSame(materials[i], set[i].Material, $"slot {i}'s Material identity must be unchanged.");
            }
            Assert.AreSame(iconMaterial, ((SymbolRenderLayer)set[1]).WorldIconMaterial, "the icon Material must survive too.");

            // Slots never move on a reorder (Build assigned a=0, s=1, c=2); queue is a function of the NEW
            // declared order (c, s, a), read off each layer's slot — so c is lowest now and a is highest.
            int queueA = set[0].Material.renderQueue;     // slot 0 = fill "a", now declared LAST
            int queueSIcon = iconMaterial.renderQueue;    // slot 1 = symbol "s" icon (Base), declared SECOND
            int queueSText = set[1].Material.renderQueue; // slot 1 = symbol "s" text (Above), declared SECOND
            int queueC = set[2].Material.renderQueue;     // slot 2 = fill "c", now declared FIRST

            Assert.Less(queueC, queueSIcon, "c (declared first) must draw below s's icon.");
            Assert.Less(queueSIcon, queueSText, "the icon (Base) must draw below its OWN layer's text (Above).");
            Assert.Less(queueSText, queueA, "s's text (declared second) must draw below a (declared last).");
        }

        // fill a(0), fill b(1), symbol s(2), fill c(3) -> (a, c): removes BOTH b and s. b is removed at a
        // LOWER slot than s on purpose — a too-late fence disposes b before reaching s, which clause 2 sees.
        private const string FillFillSymbolFillOld = @"{
    ""version"": 8, ""name"": ""T"",
    ""sources"": { ""s"": { ""type"": ""vector"", ""tiles"": [""https://x/{z}/{x}/{y}.pbf""] } },
    ""layers"": [
        { ""id"": ""a"", ""type"": ""fill"", ""source"": ""s"", ""source-layer"": ""a"", ""paint"": { ""fill-color"": [""rgba"",255,0,0,1] } },
        { ""id"": ""b"", ""type"": ""fill"", ""source"": ""s"", ""source-layer"": ""b"", ""paint"": { ""fill-color"": [""rgba"",0,255,0,1] } },
        { ""id"": ""s"", ""type"": ""symbol"", ""source"": ""s"", ""source-layer"": ""s"", ""layout"": { ""text-field"": ""{NAME}"" } },
        { ""id"": ""c"", ""type"": ""fill"", ""source"": ""s"", ""source-layer"": ""c"", ""paint"": { ""fill-color"": [""rgba"",0,0,255,1] } }
    ]
}";

        private const string FillFillSymbolRemoved = @"{
    ""version"": 8, ""name"": ""T"",
    ""sources"": { ""s"": { ""type"": ""vector"", ""tiles"": [""https://x/{z}/{x}/{y}.pbf""] } },
    ""layers"": [
        { ""id"": ""a"", ""type"": ""fill"", ""source"": ""s"", ""source-layer"": ""a"", ""paint"": { ""fill-color"": [""rgba"",255,0,0,1] } },
        { ""id"": ""c"", ""type"": ""fill"", ""source"": ""s"", ""source-layer"": ""c"", ""paint"": { ""fill-color"": [""rgba"",0,0,255,1] } }
    ]
}";

        /// <summary>Removing a SYMBOL layer must REFUSE the in-place path (UMR-152 would let it survive).
        /// That arm skips the <c>_symbolRenderLayers</c> rebuild, so tombstoning a symbol slot leaves the
        /// list handing <c>SymbolPlacementSystem.Tick</c> materials <c>SymbolRenderLayer.Dispose</c> has
        /// destroyed. Clause 2 pins WHERE the fence sits: a fence below the mutation pass still returns
        /// false, but leaves the lower-slotted fill b disposed and tombstoned.</summary>
        [Test]
        public void SymbolRemovalRestyle_RefusesAndLeavesEveryLayerUntouched()
        {
            var oldStyle = StyleParser.Parse(FillFillSymbolFillOld);
            var newStyle = StyleParser.Parse(FillFillSymbolRemoved);
            var set = Build(FillFillSymbolFillOld);
            try
            {
                Assert.AreEqual(4, set.Count, "drive precondition: all four layers must have taken a slot.");
                var instances = new IRenderLayer[] { set[0], set[1], set[2], set[3] };
                var materials = new Material[] { set[0].Material, set[1].Material, set[2].Material, set[3].Material };
                var iconMaterial = ((SymbolRenderLayer)set[2]).WorldIconMaterial;

                Assert.IsFalse(set.TryRestyleInPlace(oldStyle, newStyle, StyleTransition.Default, nowSeconds: 0.0),
                    "a restyle that REMOVES a symbol layer must refuse and fall through to the full rebuild.");

                for (int i = 0; i < 4; i++)
                {
                    Assert.AreSame(instances[i], set[i],
                        $"slot {i}'s instance must be UNCHANGED on refusal — a fence below the mutation pass " +
                        "leaves an earlier removed slot already tombstoned.");
                    Assert.IsFalse(materials[i] == null, $"slot {i}'s Material must NOT have been destroyed on refusal.");
                }
                Assert.IsFalse(iconMaterial == null, "the symbol layer's icon Material must NOT have been destroyed on refusal.");
            }
            finally
            {
                set.Dispose();
            }
        }

        /// <summary>Not plan §7.1's T7 (that needs a mid-call observation hook <c>TryRestyleInPlace</c>
        /// exposes none of — no <c>CommitProbe</c>-equivalent exists inside a single synchronous call).
        /// This is the two-pass shape's OTHER half instead: a REFUSED restyle must leave every original
        /// Material and instance completely untouched — never disposed, never replaced.</summary>
        [Test]
        public void RefusedRestyle_LeavesEveryLayerAndMaterialInstanceUntouched()
        {
            // "b" changes filter — a MESH-AFFECTING change — so the whole restyle must refuse.
            const string MeshAffectingChange = @"{
    ""version"": 8, ""name"": ""T"",
    ""sources"": { ""s"": { ""type"": ""vector"", ""tiles"": [""https://x/{z}/{x}/{y}.pbf""] } },
    ""layers"": [
        { ""id"": ""a"", ""type"": ""fill"", ""source"": ""s"", ""source-layer"": ""a"", ""paint"": { ""fill-color"": [""rgba"",255,128,0,1] } },
        { ""id"": ""c"", ""type"": ""fill"", ""source"": ""s"", ""source-layer"": ""c"", ""filter"": [""=="",""class"",""x""], ""paint"": { ""fill-color"": [""rgba"",0,0,255,1] } }
    ]
}";
            var oldStyle = StyleParser.Parse(ThreeFillOld);
            var newStyle = StyleParser.Parse(MeshAffectingChange);
            var set = Build(ThreeFillOld);
            var instances = new IRenderLayer[] { set[0], set[1], set[2] };
            var materials = new Material[] { set[0].Material, set[1].Material, set[2].Material };

            Assert.IsFalse(set.TryRestyleInPlace(oldStyle, newStyle, StyleTransition.Default, nowSeconds: 0.0),
                "an added-relative-to-removed pairing plus a filter change on the survivor must refuse.");

            for (int i = 0; i < 3; i++)
            {
                Assert.AreSame(instances[i], set[i], $"slot {i}'s instance must be UNCHANGED on refusal.");
                Assert.IsFalse(materials[i] == null, $"slot {i}'s Material must NOT have been destroyed on refusal.");
                Assert.AreSame(materials[i], set[i].Material, $"slot {i}'s Material identity must be UNCHANGED on refusal.");
            }
        }
    }
}
