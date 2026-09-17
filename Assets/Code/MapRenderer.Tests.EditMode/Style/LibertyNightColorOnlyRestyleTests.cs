using System;
using System.Collections.Generic;
using System.IO;
using NUnit.Framework;
using MapRenderer.Core.Json;

namespace MapRenderer.Tests.Style
{
    /// <summary>
    /// UMR-143: pins <c>liberty-night.json</c> as a COLOUR-ONLY restyle of <c>liberty.json</c> — same
    /// sources, same layer id sequence, same per-layer type/source/source-layer/filter/minzoom/maxzoom/
    /// layout, only <c>*-color</c> paint values differ. Without this, an edit to the night style can
    /// silently drift into a structural restyle, and any conclusion drawn from clicking between the two
    /// (e.g. "geometry stayed, so the tile cache was reused") stops being true with nothing going red.
    /// </summary>
    [TestFixture]
    public class LibertyNightColorOnlyRestyleTests
    {
        private const int MinDifferingColorValues = 100;

        // Non-paint layer fields that must be byte-identical between the two styles.
        private static readonly string[] StructuralFields =
            { "type", "source", "source-layer", "filter", "minzoom", "maxzoom", "layout" };

        // ---- fixture loader (walk-up; same pattern as TileJsonTests/SymbolTestFixtures) -----------
        private static string LoadStreamingFixtureText(string fileName)
        {
            string[] starts = { Directory.GetCurrentDirectory(), AppContext.BaseDirectory };
            foreach (string start in starts)
            {
                string dir = start;
                for (int i = 0; i < 16 && dir != null; i++)
                {
                    string candidate = Path.Combine(dir, "Assets", "StreamingAssets", "Fixtures", fileName);
                    if (File.Exists(candidate))
                        return File.ReadAllText(candidate);
                    dir = Directory.GetParent(dir)?.FullName;
                }
            }
            throw new FileNotFoundException(
                $"{fileName} not found under Assets/StreamingAssets/Fixtures walking up from " +
                $"{Directory.GetCurrentDirectory()} or {AppContext.BaseDirectory}");
        }

        private static JsonValue Day => JsonParser.Parse(LoadStreamingFixtureText("liberty.json"));
        private static JsonValue Night => JsonParser.Parse(LoadStreamingFixtureText("liberty-night.json"));

        [Test]
        public void Sources_AreIdentical()
        {
            Assert.AreEqual(JsonCanonical.Write(Day.Get("sources")), JsonCanonical.Write(Night.Get("sources")),
                "liberty-night.json's 'sources' must be byte-identical to liberty.json's — a colour-only " +
                "restyle changes no source, so cached tile geometry stays reusable across a switch.");
        }

        [Test]
        public void LayerIdSequence_And_StructuralFields_AreIdentical()
        {
            IReadOnlyList<JsonValue> dayLayers = Day.Get("layers").Items;
            IReadOnlyList<JsonValue> nightLayers = Night.Get("layers").Items;

            Assert.AreEqual(dayLayers.Count, nightLayers.Count,
                "liberty.json and liberty-night.json must declare the same number of layers.");

            for (int i = 0; i < dayLayers.Count; i++)
            {
                string dayId = dayLayers[i].GetString("id");
                string nightId = nightLayers[i].GetString("id");
                Assert.AreEqual(dayId, nightId,
                    $"layer id sequence diverges at index {i}: liberty has '{dayId}', liberty-night has " +
                    $"'{nightId}' — the layer ORDER must match, not just the set of ids.");

                foreach (string field in StructuralFields)
                {
                    string dayField = JsonCanonical.Write(dayLayers[i].Get(field));
                    string nightField = JsonCanonical.Write(nightLayers[i].Get(field));
                    Assert.AreEqual(dayField, nightField,
                        $"layer '{dayId}' diverges in non-colour field '{field}': " +
                        $"liberty has {dayField}, liberty-night has {nightField} — liberty-night.json must " +
                        "be a COLOUR-ONLY restyle (regenerate it with Tools/generate-liberty-night.py).");
                }
            }
        }

        [Test]
        public void AtLeastMostColorValues_Differ()
        {
            IReadOnlyList<JsonValue> dayLayers = Day.Get("layers").Items;
            IReadOnlyList<JsonValue> nightLayers = Night.Get("layers").Items;

            int differing = 0;
            for (int i = 0; i < dayLayers.Count; i++)
            {
                JsonValue dayPaint = dayLayers[i].Get("paint");
                JsonValue nightPaint = nightLayers[i].Get("paint");
                if (dayPaint == null) continue;

                foreach (string key in dayPaint.Members.Keys)
                {
                    if (!key.EndsWith("-color", StringComparison.Ordinal)) continue;
                    string dayValue = JsonCanonical.Write(dayPaint.Get(key));
                    string nightValue = JsonCanonical.Write(nightPaint?.Get(key));
                    if (dayValue != nightValue) differing++;
                }
            }

            Assert.GreaterOrEqual(differing, MinDifferingColorValues,
                $"only {differing} '*-color' paint values differ between liberty.json and liberty-night.json " +
                $"— expected at least {MinDifferingColorValues}. The night style should be a genuine, " +
                "visually-distinct recolour, not a token edit.");
        }
    }
}
