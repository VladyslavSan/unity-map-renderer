// Epic A / A2 acceptance — plan §F tooth 9 (DECISION 2, replaces round-1 HIGH 1): MapMaterialSet.Validate()
// fails LOUD when any base material is unassigned, making a null-material background/fill/line/symbol slot
// (which would otherwise crash or leak at a backend's AddTileLayer — see BackendNullSlotTests' doc)
// unrepresentable. RED-verifiable: a stubbed-empty Validate() must fail every "missing base throws" case here.
//
// Stays its own file (UMR-176): its `using System;` (for InvalidOperationException) would collide with
// MaterialsTests.cs's bare `Object.DestroyImmediate` calls (System.Object vs UnityEngine.Object, CS0104) —
// see docs/conventions-short.md's "Plain-import collisions" note.

using System;
using NUnit.Framework;
using UnityEngine;
using MapRenderer.Unity.Rendering.Materials;

namespace MapRenderer.Tests.Materials
{
    [TestFixture]
    public class MapMaterialSetValidationTests : BaseTestFixture
    {
        // A THROWAWAY MapMaterialSet per test — never the shared production asset (nulling a base here must
        // never mutate the committed asset other tests in the same batch also load via MapMaterialSetTestUtil).
        // symbolWorld defaults true (assigned) so the existing fill/line cases below keep testing exactly
        // what they tested before — SymbolTextWorld is the only symbol base left (commit 2 retired the
        // screen SymbolText field), pinned by its own dedicated case.
        private static MapMaterialSet NewSet(bool fill, bool line, bool symbolWorld = true)
        {
            var prod = MapMaterialSetTestUtil.Load();
            var set  = ScriptableObject.CreateInstance<MapMaterialSet>();
            set.FillMaterial     = fill       ? prod.FillMaterial     : null;
            set.LineMaterial     = line       ? prod.LineMaterial     : null;
            set.SymbolTextWorld  = symbolWorld ? prod.SymbolTextWorld : null;
            return set;
        }

        [Test]
        public void Validate_AllThreeBasesAssigned_DoesNotThrow()
        {
            var set = Track(NewSet(true, true, true));
            Assert.DoesNotThrow(() => set.Validate());
        }

        // ── Epic A / A1 (Codex #2): SymbolTextWorld is REQUIRED; SymbolIconWorld stays optional-with-warn —
        //    pinning the required-vs-optional policy split. ──

        [Test]
        public void Validate_UnassignedSymbolTextWorld_ThrowsNamingIt()
        {
            var set = Track(NewSet(true, true, symbolWorld: false));
            var ex = Assert.Throws<InvalidOperationException>(() => set.Validate());
            StringAssert.Contains("SymbolTextWorld", ex.Message);
        }

        [Test]
        public void Validate_UnassignedSymbolIconWorld_DoesNotThrow_OptionalPolicy()
        {
            // SymbolIconWorld is never assigned by NewSet, so all required bases assigned + SymbolIconWorld
            // left null must NOT throw.
            var set = Track(NewSet(true, true, true));
            Assert.DoesNotThrow(() => set.Validate(), "SymbolIconWorld is optional-with-warn, not enforced.");
        }

        [Test]
        public void Validate_UnassignedFillMaterial_ThrowsNamingIt()
        {
            var set = Track(NewSet(false, true, true));
            var ex = Assert.Throws<InvalidOperationException>(() => set.Validate());
            StringAssert.Contains("FillMaterial", ex.Message);
        }

        [Test]
        public void Validate_UnassignedLineMaterial_ThrowsNamingIt()
        {
            var set = Track(NewSet(true, false, true));
            var ex = Assert.Throws<InvalidOperationException>(() => set.Validate());
            StringAssert.Contains("LineMaterial", ex.Message);
        }

        [Test]
        public void Validate_AllThreeUnassigned_Throws()
        {
            var set = Track(NewSet(false, false, false));
            Assert.Throws<InvalidOperationException>(() => set.Validate());
        }
    }
}
