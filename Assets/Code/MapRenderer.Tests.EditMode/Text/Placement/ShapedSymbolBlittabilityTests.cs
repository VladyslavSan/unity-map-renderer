// Unity EditMode only — Unity.Collections' NativeArray. NOT registered in core-tests.csproj (that project has
// no Unity.Collections shim).

using NUnit.Framework;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using MapRenderer.Core.Text.Placement;

namespace MapRenderer.Tests.Text.Placement
{
    /// <summary>
    /// UMR-87: <see cref="ShapedSymbol"/> must live in a <see cref="NativeArray{T}"/> — the whole point of
    /// interning its <c>Text</c>/<c>IconImage</c> strings into <c>TextId</c>/<c>IconImageId</c> ints. Before
    /// that change this struct held two managed <c>string</c> fields, so <c>NativeArray&lt;ShapedSymbol&gt;</c>
    /// construction threw at runtime (Collections' safety checks reject a non-blittable T) — the failure this
    /// tooth pins as fixed.
    ///
    /// <para><b>NOT <c>UnsafeUtility.IsBlittable&lt;T&gt;()</c>.</b> That API answers a STRICTER, CLR-marshaling
    /// question — it returns <c>false</c> for any struct containing a <c>bool</c> field, which
    /// <see cref="ShapedSymbol"/> has four of (<c>AllowOverlap</c>/<c>IgnorePlacement</c>/<c>KeepUpright</c>/
    /// <c>PairOptional</c>) and always did, even before UMR-87. <see cref="IsBlittable_IsTheWrongPredicate_PointStageInputAlsoReadsFalse"/>
    /// below RUNS that API against <c>PointStageInput</c> — already a <c>NativeArray</c> element in production,
    /// also with <c>bool</c> fields — to prove it reads the SAME false there, so it is the wrong predicate for
    /// "can this live in a NativeArray", not a regression this tooth should chase.</para>
    /// </summary>
    [TestFixture]
    public class ShapedSymbolBlittabilityTests
    {
        [Test]
        public void ShapedSymbol_LivesInANativeArray()
        {
            // Not `using var` — CS1654 forbids an indexed WRITE through a using-variable (see the repo's
            // using-var-native-index-write-cs1654 lesson); Dispose explicitly instead.
            var array = new NativeArray<ShapedSymbol>(1, Allocator.Temp);
            try
            {
                array[0] = new ShapedSymbol { TextId = 7, IconImageId = 3, FeatureIndex = 1 };
                Assert.AreEqual(7, array[0].TextId);
                Assert.AreEqual(3, array[0].IconImageId);
            }
            finally { array.Dispose(); }
        }

        /// <summary>The control for the type doc's claim: <c>PointStageInput</c> is an EXISTING, already-shipped
        /// <c>NativeArray</c> element (<c>SymbolTileBlock.Points</c>) with <c>bool</c> fields of its own, so if
        /// <c>IsBlittable</c> also reads false for it, the API — not <see cref="ShapedSymbol"/> — is what
        /// disagrees with reality.</summary>
        [Test]
        public void IsBlittable_IsTheWrongPredicate_PointStageInputAlsoReadsFalse()
        {
            Assert.IsFalse(UnsafeUtility.IsBlittable<PointStageInput>(),
                "control: a bool-bearing struct ALREADY living in NativeArray<PointStageInput> in production " +
                "still reads false here — confirms IsBlittable is not the right predicate for ShapedSymbol either");
        }
    }
}
