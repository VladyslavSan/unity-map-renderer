// Engine-free: compiled verbatim by both the Unity EditMode runner and Tools/core-tests.
// Do NOT add any UnityEngine, MeshBuilder, NativeArray, or MonoBehaviour references.

using System;
using System.IO;
using NUnit.Framework;
using Unity.Mathematics;
using MapRenderer.Core.Style;
using MapRenderer.Core.Text;
using MapRenderer.Core.Text.Placement;
using SymbolStyle = MapRenderer.Core.Style.Symbol;

namespace MapRenderer.Tests
{
    /// <summary>
    /// Shared, engine-free fixture plumbing for the symbol-extraction test files: the walk-up loaders (which
    /// have to work under Unity batch mode AND <c>dotnet test</c>, whose working directories differ), the
    /// parsed-once Liberty style, and the <see cref="PointStageInput"/> adapter that turns a real extractor
    /// <see cref="SymbolStyle.SymbolFeature"/> into a staging input. One copy, so fixture paths and staging
    /// fields do not drift between files.
    /// </summary>
    internal static class SymbolTestFixtures
    {
        private static string[] PathParts(string root, string[] rest)
        {
            var parts = new string[rest.Length + 1];
            parts[0] = root;
            Array.Copy(rest, 0, parts, 1, rest.Length);
            return parts;
        }

        /// <summary>Reads a repo-relative text file, walking up from the cwd and from
        /// <see cref="AppContext.BaseDirectory"/> until the path resolves.</summary>
        public static string LoadUpText(params string[] relSegments)
        {
            string[] starts = { Directory.GetCurrentDirectory(), AppContext.BaseDirectory };
            foreach (string start in starts)
            {
                var dir = new DirectoryInfo(start);
                while (dir != null)
                {
                    string p = Path.Combine(PathParts(dir.FullName, relSegments));
                    if (File.Exists(p)) return File.ReadAllText(p);
                    dir = dir.Parent;
                }
            }
            throw new FileNotFoundException("Not found walking up from cwd/AppContext: " + string.Join("/", relSegments));
        }

        /// <summary>Byte-reading twin of <see cref="LoadUpText"/> (the committed <c>.pbf.bytes</c> tiles).</summary>
        public static byte[] LoadUpBytes(params string[] relSegments)
        {
            string[] starts = { Directory.GetCurrentDirectory(), AppContext.BaseDirectory };
            foreach (string start in starts)
            {
                var dir = new DirectoryInfo(start);
                while (dir != null)
                {
                    string p = Path.Combine(PathParts(dir.FullName, relSegments));
                    if (File.Exists(p)) return File.ReadAllBytes(p);
                    dir = dir.Parent;
                }
            }
            throw new FileNotFoundException("Not found walking up from cwd/AppContext: " + string.Join("/", relSegments));
        }

        public static string LoadLibertyJson() => LoadUpText("Assets", "StreamingAssets", "Fixtures", "liberty.json");
        public static string LoadLibertyNightJson() => LoadUpText("Assets", "StreamingAssets", "Fixtures", "liberty-night.json");

        private static StyleDocument _libertyDoc;
        private static StyleDocument _libertyNightDoc;

        /// <summary>The real shipped Liberty style, parsed once per test run.</summary>
        public static StyleDocument LibertyDoc() => _libertyDoc ??= StyleParser.Parse(LoadLibertyJson());

        /// <summary>The real shipped Liberty-night style (the day/night pair), parsed once
        /// per test run.</summary>
        public static StyleDocument LibertyNightDoc() => _libertyNightDoc ??= StyleParser.Parse(LoadLibertyNightJson());

        /// <summary>Liberty's symbol layer with this id, or null (a non-symbol layer also yields null).</summary>
        public static SymbolStyle.StyleLayer FindSymbolLayer(string id)
        {
            foreach (StyleLayer layer in LibertyDoc().Layers)
                if (layer.Id == id) return layer as SymbolStyle.StyleLayer;
            return null;
        }

        /// <summary>Hand-builds the <see cref="PointStageInput"/> for one half of a pair from the REAL
        /// extractor's own <see cref="SymbolStyle.SymbolFeature"/> — mirrors
        /// <c>SymbolTileBlockBaker.BuildPointInput</c>'s field math (the Unity-only bake step itself
        /// can't run headlessly — no Unity.Collections in Tools/core-tests — so this is the engine-free
        /// subset: Color/FadeId are placeholders the callers set or ignore).</summary>
        public static PointStageInput StageInputFor(SymbolStyle.SymbolFeature symbol, SymbolKind atlasKind,
            float2 boundsMin, float2 boundsMax, float2 screenPx, float textSizePx)
            => new PointStageInput
            {
                ScreenPx = screenPx, Depth = 0f, Projected = true,
                BoundsMin = boundsMin, BoundsMax = boundsMax,
                TextSizePx = textSizePx, PaddingPx = symbol.PaddingPx, SortKey = symbol.SortKey,
                FeatureIndex = symbol.FeatureIndex, TileKey = symbol.TileKey, Slot = 0,
                AllowOverlap = symbol.AllowOverlap, IgnorePlacement = symbol.IgnorePlacement,
                TranslatePx = symbol.TranslatePx, TranslateAnchor = symbol.TranslateAnchor,
                RotationAlignment = symbol.RotationAlignment, Color = new float4(1, 1, 1, 1),
                AtlasKind = atlasKind,
                PairOptional = symbol.PairOptional, // mirrors BuildPointInput's own carry
            };
    }
}
