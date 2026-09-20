// Unity EditMode only — reads source files under Application.dataPath. NOT registered in core-tests.csproj.

using System.IO;
using System.Reflection;
using System.Text.RegularExpressions;
using NUnit.Framework;
using UnityEngine;
using MapRenderer.Unity.Rendering.Tile;

namespace MapRenderer.Tests.Structure
{
    /// <summary>
    /// UMR-95 fence F1: a speed bump, not a ban. Pins the recorded 3-field shape of
    /// <see cref="PreparedKey"/> — <c>Style</c> already partitions the cache, so a second discriminator
    /// would change no outcome at a data-plane cost. A deliberate fourth field
    /// (<c>park/umr-113-bake-revision</c>'s <c>Revision</c> is the known candidate) updates this tooth
    /// together with that decision, not around it. Asserts the SHAPE (arity), not a call-site count.
    /// </summary>
    [TestFixture]
    public class PreparedKeyShapeStructureTests
    {
        [Test]
        public void PreparedKey_HasExactlyThreeFields()
        {
            // Public|NonPublic — a non-public fourth field is a legal way to implement the rejected fork,
            // and Public-only would not see it.
            int count = typeof(PreparedKey)
                .GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance).Length;
            Assert.AreEqual(3, count,
                "PreparedKey must stay a 3-field key (Style, Tile, LayerId) — a recorded UMR-95 decision: " +
                "Style already partitions the cache, so a second discriminator would change no outcome at " +
                "a data-plane cost. Landing a fourth field on purpose (park/umr-113-bake-revision's " +
                "Revision is the known candidate)? Update this tooth together with that decision, not " +
                "instead of it.");
        }

        [Test]
        public void EveryPreparedKeyConstruction_TakesExactlyThreeArguments()
        {
            string codeDir = Path.Combine(Directory.GetParent(Application.dataPath).FullName, "Assets", "Code");
            int found = 0;
            foreach (string file in Directory.GetFiles(codeDir, "*.cs", SearchOption.AllDirectories))
            {
                string text = File.ReadAllText(file);
                // Naive comma counting — every call site today is a simple 3-arg form (no nested parens, no
                // object initializer). An initializer-form call would silently defeat this arity check.
                foreach (Match m in Regex.Matches(text, "new " + nameof(PreparedKey) + @"\(([^)]*)\)"))
                {
                    found++;
                    int argCount = m.Groups[1].Value.Split(',').Length;
                    Assert.AreEqual(3, argCount,
                        $"{file}: '{m.Value}' does not take 3 arguments — PreparedKey's arity changed. " +
                        "If this is a deliberate fourth field (see PreparedKey_HasExactlyThreeFields for " +
                        "the recorded decision it revises), update this tooth's expected arity together " +
                        "with it, not around it.");
                }
            }
            // A required-token check like the loop above fails OPEN on zero matches — a factory method or
            // an object-initializer construction would retire it silently. Guard the pattern still fires.
            Assert.Greater(found, 0, $"no 'new {nameof(PreparedKey)}(' call sites found under {codeDir} — " +
                "the arity check above never ran. PreparedKey construction moved to a form this regex " +
                "cannot see (a factory method, an object initializer) — update the tooth to match it.");
        }
    }
}
