// MapMaterialSet.Validate() throws when any base material is unassigned, so a null-material slot never
// reaches a backend's AddTileLayer. Own file: its `using System;` collides with MaterialsTests' bare Object.

using System;
using NUnit.Framework;
using UnityEngine;
using MapRenderer.Unity.Rendering.Materials;

namespace MapRenderer.Tests.Materials
{
    [TestFixture]
    public class MapMaterialSetValidationTests : BaseTestFixture
    {
        // A THROWAWAY set per test, so nulling a base never mutates the committed asset other tests load.
        // symbolWorld defaults to assigned; SymbolTextWorld has its own case.
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

        // ── SymbolTextWorld is REQUIRED; SymbolIconWorld stays optional-with-warn —
        //    pinning the required-vs-optional policy split. ──

        /// <summary>Each REQUIRED base, left unassigned alone, throws an exception naming it.</summary>
        [Test]
        public void Validate_UnassignedRequiredBase_ThrowsNamingIt()
        {
            var missingFill = Track(NewSet(fill: false, line: true, symbolWorld: true));
            var fillEx = Assert.Throws<InvalidOperationException>(() => missingFill.Validate());
            StringAssert.Contains("FillMaterial", fillEx.Message);

            var missingLine = Track(NewSet(fill: true, line: false, symbolWorld: true));
            var lineEx = Assert.Throws<InvalidOperationException>(() => missingLine.Validate());
            StringAssert.Contains("LineMaterial", lineEx.Message);

            var missingSymbolTextWorld = Track(NewSet(fill: true, line: true, symbolWorld: false));
            var symbolEx = Assert.Throws<InvalidOperationException>(() => missingSymbolTextWorld.Validate());
            StringAssert.Contains("SymbolTextWorld", symbolEx.Message);
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
        public void Validate_AllThreeUnassigned_Throws()
        {
            var set = Track(NewSet(false, false, false));
            Assert.Throws<InvalidOperationException>(() => set.Validate());
        }
    }
}
