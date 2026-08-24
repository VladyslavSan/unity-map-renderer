// Engine-free: shared verbatim between the Unity EditMode runner and Tools/core-tests (registered in
// core-tests.csproj). No UnityEngine — SymbolStringTable is a pure string→int table.

using NUnit.Framework;
using MapRenderer.Core.Text.Placement;

namespace MapRenderer.Tests.Text.Placement
{
    /// <summary>
    /// Stage 2 (symbols-async-reconcile): <see cref="SymbolStringTable"/> — the string→int table that lets the
    /// cross-tile dedup key partition by an integer id instead of a string hash. Its BIJECTION within a
    /// lifetime (different string ⇒ different id, ordinal) is the property the dedup partition-preservation
    /// rests on, so it gets its own falsifiable teeth here.
    /// </summary>
    [TestFixture]
    public class SymbolStringTableTests
    {
        // ── Idempotent: the same string always returns the same id (a re-stringTable is a lookup). ──
        [Test]
        public void Intern_SameString_ReturnsSameId()
        {
            var stringTable = new SymbolStringTable();
            int first = stringTable.Intern("Main St");
            Assert.AreEqual(first, stringTable.Intern("Main St"), "the same string interns to a stable id");
            Assert.AreEqual(1, stringTable.Count, "…and does not add a second mapping");
        }

        // ── The perfect-hash property: distinct strings get distinct ids (the bijection tooth). ──
        [Test]
        public void Intern_DistinctStrings_ReturnDistinctIds()
        {
            var stringTable = new SymbolStringTable();
            int a = stringTable.Intern("Paris");
            int b = stringTable.Intern("Lyon");
            int c = stringTable.Intern("Nice");
            Assert.AreNotEqual(a, b);
            Assert.AreNotEqual(b, c);
            Assert.AreNotEqual(a, c);
            Assert.AreEqual(3, stringTable.Count, "three distinct strings ⇒ three mappings");
        }

        // ── null → the 0 sentinel (inert: a null Text/IconImage never becomes a winner). ──
        [Test]
        public void Intern_Null_ReturnsZeroSentinel()
        {
            var stringTable = new SymbolStringTable();
            Assert.AreEqual(0, stringTable.Intern(null), "null maps to the 0 sentinel");
            Assert.AreNotEqual(0, stringTable.Intern("x"), "a real string never gets id 0 (reserved)");
        }

        // ── Ordinal, case-SENSITIVE — must match CrossTileSymbolKey.Equals's `Text == other.Text` semantics. An
        //    OrdinalIgnoreCase/culture comparer would fail this (and silently over-merge "Main St" vs "MAIN ST"). ──
        [Test]
        public void Intern_IsOrdinal_CaseSensitive()
        {
            var stringTable = new SymbolStringTable();
            Assert.AreNotEqual(stringTable.Intern("A"), stringTable.Intern("a"), "ordinal ⇒ 'A' and 'a' are distinct ids");
        }

        // ── Reset clears every mapping and restarts ids at 1; a string re-interns consistently thereafter. ──
        [Test]
        public void Reset_ClearsAndRestarts()
        {
            var stringTable = new SymbolStringTable();
            stringTable.Intern("first");
            stringTable.Intern("second");
            Assert.AreEqual(2, stringTable.Count);

            stringTable.Reset();
            Assert.AreEqual(0, stringTable.Count, "Reset drops every mapping");
            Assert.AreEqual(1, stringTable.Intern("fresh"), "…and ids restart at 1");
            Assert.AreEqual(1, stringTable.Intern("fresh"), "…still idempotent after a reset");
        }

        // ── Monotonic: interning N distinct strings yields N distinct ascending ids (1..N), never reused. ──
        [Test]
        public void Ids_Monotonic_NeverReusedWithinLifetime()
        {
            var stringTable = new SymbolStringTable();
            var seen = new System.Collections.Generic.HashSet<int>();
            for (int i = 0; i < 50; i++)
            {
                int id = stringTable.Intern("s" + i);
                Assert.AreEqual(i + 1, id, "ids ascend monotonically from 1");
                Assert.IsTrue(seen.Add(id), "…and are never reused within a lifetime");
            }
            Assert.AreEqual(50, stringTable.Count);
        }
    }
}
