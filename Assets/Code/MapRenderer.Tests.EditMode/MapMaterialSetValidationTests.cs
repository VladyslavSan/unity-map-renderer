// Epic A / A2 acceptance — plan §F tooth 9 (DECISION 2, replaces round-1 HIGH 1): MapMaterialSet.Validate()
// fails LOUD when any base material is unassigned, making a null-material background/fill/line/symbol slot
// (which would otherwise crash or leak at a backend's AddTileLayer — see BackendNullSlotTests' doc)
// unrepresentable. RED-verifiable: a stubbed-empty Validate() must fail every "missing base throws" case here.

using System;
using NUnit.Framework;
using UnityEngine;
using MapRenderer.Unity.Rendering.Materials;

namespace MapRenderer.Tests
{
    [TestFixture]
    public class MapMaterialSetValidationTests
    {
        // A THROWAWAY MapMaterialSet per test — never the shared production asset (nulling a base here must
        // never mutate the committed asset other tests in the same batch also load via MapMaterialSetTestUtil).
        private static MapMaterialSet NewSet(bool fill, bool line, bool symbol)
        {
            var prod = MapMaterialSetTestUtil.Load();
            var set  = ScriptableObject.CreateInstance<MapMaterialSet>();
            set.FillMaterial = fill   ? prod.FillMaterial : null;
            set.LineMaterial = line   ? prod.LineMaterial : null;
            set.SymbolText   = symbol ? prod.SymbolText   : null;
            return set;
        }

        [Test]
        public void Validate_AllThreeBasesAssigned_DoesNotThrow()
        {
            var set = NewSet(true, true, true);
            try { Assert.DoesNotThrow(() => set.Validate()); }
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
        public void Validate_UnassignedSymbolText_ThrowsNamingIt()
        {
            var set = NewSet(true, true, false);
            try
            {
                var ex = Assert.Throws<InvalidOperationException>(() => set.Validate());
                StringAssert.Contains("SymbolText", ex.Message);
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
