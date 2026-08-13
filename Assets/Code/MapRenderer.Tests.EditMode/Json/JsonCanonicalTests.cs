// Engine-free: compiled verbatim by both the Unity EditMode runner and Tools/core-tests.
// Do NOT add any UnityEngine, MeshBuilder, native-container, or MonoBehaviour references.

using System.Collections.Generic;
using System.Globalization;
using System.Threading;
using NUnit.Framework;
using MapRenderer.Core.Json;

namespace MapRenderer.Tests
{
    /// <summary>
    /// <c>JsonCanonical.Write</c> — the serialization <c>TileManager.SourceKey</c> keys an inline
    /// <c>data</c> document on. Its whole contract is comparability in BOTH directions, so every arm here
    /// pins one of the two failures: <b>two authorings of one document must produce the same text</b> (or
    /// every restyle rebuilds every geojson pipeline), and <b>two different documents must produce different
    /// text</b> (or a restyle keeps the wrong dataset's pipeline and renders geometry the style no longer
    /// declares).
    ///
    /// <para><b>The collision arms are the load-bearing half.</b> A writer that merely "looks like JSON"
    /// passes the agreement arms trivially — <see cref="JsonValue.ToString"/> does, and it renders every
    /// two-member object as <c>"{2 members}"</c>. That is why the arms below name the specific DOM pairs a
    /// sloppy writer merges: a number against its string spelling, a null node against the string
    /// <c>"null"</c>, a string carrying the delimiters against the structure it would forge, and a literal
    /// backslash against the control character it would otherwise be spelled as.</para>
    /// </summary>
    [TestFixture]
    public class JsonCanonicalTests
    {
        private static JsonValue Obj(params (string Key, JsonValue Value)[] members)
        {
            var map = new Dictionary<string, JsonValue>();
            foreach ((string key, JsonValue value) in members) map[key] = value;
            return JsonValue.OfObject(map);
        }

        private static JsonValue Arr(params JsonValue[] items) => JsonValue.OfArray(new List<JsonValue>(items));

        private static JsonValue Num(double v) => JsonValue.OfNumber(v);
        private static JsonValue Str(string v) => JsonValue.OfString(v);

        // ── Agreement: two authorings of one document are one key ─────────────────────────────────────

        /// <summary>JSON object member order is not semantic (RFC 8259 §4: an object is an unordered
        /// collection), so two authorings of the same object must canonicalise identically — otherwise a
        /// re-parse that happened to hash its members differently would look like a different source.
        /// </summary>
        [Test]
        public void ObjectMembers_AreOrderInsensitive()
        {
            Assert.AreEqual(
                JsonCanonical.Write(Obj(("a", Num(1)), ("b", Num(2)))),
                JsonCanonical.Write(Obj(("b", Num(2)), ("a", Num(1)))),
                "the same object authored in two member orders must produce ONE canonical text; if it does " +
                "not, every restyle rebuilds every inline source's pipeline");
        }

        /// <summary>Array order IS semantic — a polygon ring is not its reversal — so the writer must not
        /// sort items. This is the arm that stops "sort everything" from satisfying its sibling above.
        /// </summary>
        [Test]
        public void ArrayItems_AreOrderSensitive()
        {
            Assert.AreNotEqual(
                JsonCanonical.Write(Arr(Num(1), Num(2))),
                JsonCanonical.Write(Arr(Num(2), Num(1))),
                "array order is semantic: [1,2] and [2,1] are different documents. A writer that sorted " +
                "items would make a ring and its reversal one source key.");
        }

        /// <summary>The member sort applies at EVERY depth, not just the root — an inline GeoJSON document
        /// nests its objects several levels down, so a root-only sort would be indistinguishable from no
        /// sort at all for the documents this key actually sees.</summary>
        [Test]
        public void Nesting_IsCanonicalisedRecursively()
        {
            JsonValue first = Obj(
                ("outer", Arr(Obj(("x", Num(1)), ("y", Num(2))))),
                ("also",  Obj(("p", Str("q")), ("m", Str("n")))));
            JsonValue second = Obj(
                ("also",  Obj(("m", Str("n")), ("p", Str("q")))),
                ("outer", Arr(Obj(("y", Num(2)), ("x", Num(1))))));

            Assert.AreEqual(JsonCanonical.Write(first), JsonCanonical.Write(second),
                "the ordinal member sort must reach objects nested inside arrays inside objects. A root-only " +
                "sort passes the flat arm and still rebuilds the pipeline of every real GeoJSON document.");
        }

        /// <summary>Under a comma-decimal ambient culture the text must still use <c>.</c>. Two machines in
        /// two locales must compute the same key for the same document, or a shared style renders one way in
        /// one place and rebuilds continuously in another.</summary>
        [Test]
        public void Numbers_AreInvariantCulture_NotTheAmbientLocale()
        {
            CultureInfo previous = Thread.CurrentThread.CurrentCulture;
            try
            {
                Thread.CurrentThread.CurrentCulture = new CultureInfo("de-DE"); // ',' decimal separator

                Assert.AreEqual("1.5", JsonCanonical.Write(Num(1.5)),
                    "a comma-decimal CurrentCulture must not reach the canonical text — '1,5' would also " +
                    "forge an array separator inside an object, so this is a structure defect, not a " +
                    "cosmetic one");
                Assert.AreEqual("[1.5,2.25]", JsonCanonical.Write(Arr(Num(1.5), Num(2.25))),
                    "…including inside a container, where the ambient separator collides with the real one");
            }
            finally
            {
                Thread.CurrentThread.CurrentCulture = previous;
            }
        }

        // ── Collision: two different documents are two keys ───────────────────────────────────────────

        /// <summary>A number and its string spelling are different DOM values and must not canonicalise
        /// alike. This is what the quotes around strings are FOR — an unquoted writer merges them.</summary>
        [Test]
        public void ANumber_AndItsStringSpelling_AreDistinct()
        {
            Assert.AreNotEqual(JsonCanonical.Write(Num(1)), JsonCanonical.Write(Str("1")),
                "1 and \"1\" are different values; a writer that drops the quotes makes them one key");
            Assert.AreEqual("1", JsonCanonical.Write(Num(1)), "…and the number is the unquoted one");
            Assert.AreEqual("\"1\"", JsonCanonical.Write(Str("1")), "…and the string is the quoted one");
        }

        /// <summary>A null node writes <c>null</c>; the STRING <c>"null"</c> does not write the same thing.
        /// Both a null reference and a <see cref="JsonKind.Null"/> node are the same "absent" for this
        /// writer, which is stated in its doc and pinned here so it cannot drift silently.</summary>
        [Test]
        public void Null_IsWritten_AndIsNotTheStringNull()
        {
            Assert.AreEqual("null", JsonCanonical.Write(JsonValue.Null), "a null node writes the literal");
            Assert.AreEqual("null", JsonCanonical.Write(null),
                "…and so does a null reference — SourceKey.From distinguishes ABSENT before it asks, so " +
                "these two are deliberately one text here");
            Assert.AreNotEqual(JsonCanonical.Write(JsonValue.Null), JsonCanonical.Write(Str("null")),
                "the string \"null\" is a different document from a null node, and an unquoted writer " +
                "merges them");
        }

        /// <summary>
        /// A string carrying the delimiters must not be able to forge structure, on <b>three of the eight
        /// independent escape arms</b> the writer has — quote, backslash, and the <c>&lt; 0x20</c> default.
        ///
        /// <para><b>Why these three, and why the other five need no arm.</b> <c>JsonCanonical.WriteString</c>
        /// has eight cases: <c>"</c>, <c>\</c>, <c>\b</c>, <c>\f</c>, <c>\n</c>, <c>\r</c>, <c>\t</c> and
        /// the control-character default. Only the first two can forge a COLLISION — the quote closes a
        /// string and the backslash re-spells one — and the default is the only arm reached by a character
        /// with no case of its own. The five named control escapes are convenience spellings: emitted raw,
        /// a real newline is still a different text from the two characters <c>\</c> and <c>n</c> (which the
        /// backslash arm doubles), so no two distinct DOM values can be made to canonicalise alike through
        /// them. Coverage here is deliberate, not partial.</para>
        ///
        /// <para><b>Why three and not one.</b> The arms are separate <c>switch</c> cases, so an injection
        /// into any one of them leaves the others intact: a fixture exercising only the quote is BLIND to a
        /// backslash writer that stopped doubling, under which <c>"\\n"</c> (backslash, 'n') and a real
        /// newline canonicalise to the same text — two distinct DOM values, one source key, which is exactly
        /// the failure this writer exists to prevent. Measured, not reasoned: that injection left a
        /// quote-only arm green.</para>
        /// </summary>
        [Test]
        public void StringsCarryingDelimiters_AreEscaped_SoTheyCannotForgeStructure()
        {
            // ── quote ──  one member whose VALUE spells out two members, against the two members themselves.
            JsonValue forged  = Obj(("a", Str("b\",\"c\":\"d")));
            JsonValue genuine = Obj(("a", Str("b")), ("c", Str("d")));

            Assert.AreNotEqual(JsonCanonical.Write(genuine), JsonCanonical.Write(forged),
                "a string must not be able to forge object structure. Unescaped, the one-member object's " +
                "text IS the two-member object's text, so a style could alias one inline source onto " +
                "another by authoring a quote.");

            // ── backslash ──  a literal backslash-then-'n' against the control character it spells.
            JsonValue literalBackslashN = Str("\\n");
            JsonValue realNewline       = Str("\n");

            Assert.AreNotEqual(JsonCanonical.Write(realNewline), JsonCanonical.Write(literalBackslashN),
                "a literal backslash followed by 'n' and a real newline are different strings and must " +
                "stay different texts. A writer that emits the backslash undoubled merges them — the " +
                "escape sequence it writes is re-read as the character it escapes.");
            Assert.AreEqual("\"\\\\n\"", JsonCanonical.Write(literalBackslashN),
                "…and the backslash is spelled as the two-character escape, exactly. Asserting only that " +
                "the two differ would accept any spelling that happens to differ today.");

            // ── control character ──  the \uXXXX arm, which neither of the two above reaches.
            Assert.AreEqual("\"\\u0001\"", JsonCanonical.Write(Str("\u0001")),
                "a control character below 0x20 must be escaped as \\uXXXX — emitted raw it is a byte the " +
                "delimiter set does not account for, and the text stops being unambiguous");
        }

        /// <summary>
        /// The reason this type exists rather than <see cref="JsonValue.ToString"/>: that method renders any
        /// two-member object as <c>"{2 members}"</c>, so two entirely different inline datasets stringify
        /// identically.
        ///
        /// <para>The <b>precondition</b> is asserted, not assumed. Without it, a future <c>ToString</c> that
        /// started emitting real JSON would turn this test into a tautology that still passed while proving
        /// nothing.</para>
        /// </summary>
        [Test]
        public void ToString_CollidesOnDifferentObjects_WhereCanonicalDoesNot()
        {
            JsonValue first  = Obj(("type", Str("FeatureCollection")), ("features", Arr(Num(1))));
            JsonValue second = Obj(("type", Str("FeatureCollection")), ("features", Arr(Num(2))));

            Assert.AreEqual(first.ToString(), second.ToString(),
                "precondition: ToString really does collide on these two documents. If it stopped doing so " +
                "this test would pass for the wrong reason, proving nothing about the canonical writer.");

            Assert.AreNotEqual(JsonCanonical.Write(first), JsonCanonical.Write(second),
                "…and the canonical text does NOT collide. Keying SourceKey on ToString is the defect this " +
                "type replaces: two different inline datasets would be one source.");
        }
    }
}
