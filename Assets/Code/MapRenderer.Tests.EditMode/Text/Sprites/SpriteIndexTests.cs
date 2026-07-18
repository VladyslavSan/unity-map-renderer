// Engine-free: this file is compiled verbatim by both the Unity EditMode runner
// (Assets/Code/MapRenderer.Tests.EditMode/) and the fast dotnet test project (Tools/core-tests/).
// Do NOT add any UnityEngine, MeshBuilder, NativeArray, or MonoBehaviour references.

using System;
using System.IO;
using NUnit.Framework;
using MapRenderer.Core.Text.Sprites;

namespace MapRenderer.Tests
{
    /// <summary>
    /// I1 acceptance: a MapLibre sprite JSON index parses into name-keyed <see cref="SpriteEntry"/>
    /// values; malformed/wrong-shaped input never throws (forward-compat posture, mirrors
    /// StyleParserTests).
    /// </summary>
    [TestFixture]
    public class SpriteIndexTests
    {
        // ---- fixture loader (walk-up; works under Unity batch mode AND dotnet test) ---------------
        private static string LoadFixtureText(string fileName)
        {
            string[] starts = { Directory.GetCurrentDirectory(), AppContext.BaseDirectory };
            foreach (string start in starts)
            {
                string dir = start;
                for (int i = 0; i < 16 && dir != null; i++)
                {
                    string candidate = Path.Combine(dir, "Assets", "Fixtures", fileName);
                    if (File.Exists(candidate))
                        return File.ReadAllText(candidate);
                    dir = Directory.GetParent(dir)?.FullName;
                }
            }
            throw new FileNotFoundException(
                $"{fileName} not found (cwd={Directory.GetCurrentDirectory()}," +
                $" base={AppContext.BaseDirectory})");
        }

        [Test]
        public void KnownNames_ParseExactRectAndPixelRatio()
        {
            var index = SpriteIndex.Parse(LoadFixtureText("sprites/sample-sprite.json"));

            Assert.IsTrue(index.TryGetSprite("star", out var star));
            Assert.AreEqual(16, star.X);
            Assert.AreEqual(0, star.Y);
            Assert.AreEqual(24, star.Width);
            Assert.AreEqual(24, star.Height);
            Assert.AreEqual(2f, star.PixelRatio);
            Assert.IsFalse(star.Sdf);

            Assert.IsTrue(index.TryGetSprite("marker", out var marker));
            Assert.AreEqual(0, marker.X);
            Assert.AreEqual(0, marker.Y);
            Assert.AreEqual(16, marker.Width);
            Assert.AreEqual(16, marker.Height);
            Assert.AreEqual(1f, marker.PixelRatio);

            Assert.IsTrue(index.TryGetSprite("dot", out var dot));
            Assert.AreEqual(0, dot.X);
            Assert.AreEqual(32, dot.Y);
            Assert.AreEqual(8, dot.Width);
            Assert.AreEqual(8, dot.Height);
        }

        [Test]
        public void MemberNotObject_Skipped()
        {
            var index = SpriteIndex.Parse("{\"foo\":42}");

            Assert.IsFalse(index.TryGetSprite("foo", out _));
            Assert.AreEqual(0, index.Count);
        }

        [Test]
        public void UnknownName_NotFound()
        {
            var index = SpriteIndex.Parse(LoadFixtureText("sprites/sample-sprite.json"));

            Assert.IsFalse(index.TryGetSprite("nope", out _));
        }

        [Test]
        public void Malformed_DoesNotThrow_EmptyIndex()
        {
            SpriteIndex index = null;
            Assert.DoesNotThrow(() => index = SpriteIndex.Parse("{ not json"));
            Assert.AreEqual(0, index.Count);
        }

        [Test]
        public void RootNotObject_EmptyIndex()
        {
            Assert.AreEqual(0, SpriteIndex.Parse("[]").Count);
            Assert.AreEqual(0, SpriteIndex.Parse("42").Count);
        }

        [Test]
        public void Sdf_And_DefaultPixelRatio_ReadThrough()
        {
            var index = SpriteIndex.Parse("{\"a\":{\"x\":1,\"y\":2,\"width\":3,\"height\":4,\"sdf\":true}}");

            Assert.IsTrue(index.TryGetSprite("a", out var a));
            Assert.IsTrue(a.Sdf);
            Assert.AreEqual(1f, a.PixelRatio);
        }
    }
}
