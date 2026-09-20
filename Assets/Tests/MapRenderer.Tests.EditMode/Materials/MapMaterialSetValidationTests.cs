// Epic A / A2 acceptance — plan §F tooth 9 (DECISION 2, replaces round-1 HIGH 1): MapMaterialSet.Validate()
// fails LOUD when any base material is unassigned, making a null-material background/fill/line/symbol slot
// (which would otherwise crash or leak at a backend's AddTileLayer — see BackendNullSlotTests' doc)
// unrepresentable. RED-verifiable: a stubbed-empty Validate() must fail every "missing base throws" case here.

using System;
using NUnit.Framework;
using UnityEngine;
using MapRenderer.Unity.Rendering.Materials;

namespace MapRenderer.Tests.Materials
{
    [TestFixture]
    public class MapMaterialSetValidationTests
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
            var set = NewSet(true, true, true);
            try { Assert.DoesNotThrow(() => set.Validate()); }
            finally { UnityEngine.Object.DestroyImmediate(set); }
        }

        // ── Epic A / A1 (Codex #2): SymbolTextWorld is REQUIRED; SymbolIconWorld stays optional-with-warn —
        //    pinning the required-vs-optional policy split. ──

        [Test]
        public void Validate_UnassignedSymbolTextWorld_ThrowsNamingIt()
        {
            var set = NewSet(true, true, symbolWorld: false);
            try
            {
                var ex = Assert.Throws<InvalidOperationException>(() => set.Validate());
                StringAssert.Contains("SymbolTextWorld", ex.Message);
            }
            finally { UnityEngine.Object.DestroyImmediate(set); }
        }

        [Test]
        public void Validate_UnassignedSymbolIconWorld_DoesNotThrow_OptionalPolicy()
        {
            // SymbolIconWorld is never assigned by NewSet, so all required bases assigned + SymbolIconWorld
            // left null must NOT throw.
            var set = NewSet(true, true, true);
            try { Assert.DoesNotThrow(() => set.Validate(), "SymbolIconWorld is optional-with-warn, not enforced."); }
            finally { UnityEngine.Object.DestroyImmediate(set); }
        }

        [Test]
        public void Validate_UnassignedFillMaterial_ThrowsNamingIt()
        {
            var set = NewSet(false, true, true);
            try
            {
                var ex = Assert.Throws<InvalidOperationException>(() => set.Validate());
                StringAssert.Contains("FillMaterial", ex.Message);
            }
            finally { UnityEngine.Object.DestroyImmediate(set); }
        }

        [Test]
        public void Validate_UnassignedLineMaterial_ThrowsNamingIt()
        {
            var set = NewSet(true, false, true);
            try
            {
                var ex = Assert.Throws<InvalidOperationException>(() => set.Validate());
                StringAssert.Contains("LineMaterial", ex.Message);
            }
            finally { UnityEngine.Object.DestroyImmediate(set); }
        }

        [Test]
        public void Validate_AllThreeUnassigned_Throws()
        {
            var set = NewSet(false, false, false);
            try { Assert.Throws<InvalidOperationException>(() => set.Validate()); }
            finally { UnityEngine.Object.DestroyImmediate(set); }
        }
    }
}
