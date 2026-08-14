// Engine-free: compiled verbatim by both the Unity EditMode runner and Tools/core-tests.

using System;
using System.Collections.Generic;
using System.IO;
using NUnit.Framework;

namespace MapRenderer.Tests.GeoJsons
{
    /// <summary>
    /// T12 — a structural guard on where the GeoJSON stack is allowed to live.
    ///
    /// <para><b>The claim has been restated, because its original premise is spent.</b> S1 wrote this to keep
    /// the carrier fork open: the stage shipped only what was identical under both arms (transcode into an
    /// opcode stream vs. hand the pipeline a neutral tile-local buffer), so naming either carrier would have
    /// silently picked a side. That fork is now CLOSED — IR-first — and S2 deliberately names
    /// <c>TileGeometryBuffers</c>, in <c>MapRenderer.Jobs/Tiles</c>, which is a sanctioned decoder location.
    /// The fence did not become pointless when the fork closed; it became a different and still-live
    /// invariant, which is what it now pins:</para>
    ///
    /// <para><b>Core's GeoJSON stack — parse, project, slice — stays engine-free and therefore stays in the
    /// 0.1 s <c>dotnet test</c> loop.</b> That is the whole reason it lives in <c>MapRenderer.Core</c>, and it
    /// is exactly what the same token list enforces: a <c>NativeArray</c>, a Burst attribute or a
    /// <c>TileGeometryBuffers</c> in any of these files would drag the parse/slice algorithms out of the fast
    /// loop and into the multi-minute Unity gate. The carrier work belongs one layer out, in the decoder that
    /// consumes <c>TileSlice</c>; the scanned set is unchanged and so is
    /// <see cref="ForbiddenTokens"/>.</para>
    /// </summary>
    [TestFixture]
    public class GeoJsonForkNeutralityTests
    {
        private static readonly string[] ForbiddenTokens =
        {
            "uint[]",              // the MVT opcode-stream carrier
            "TileGeometryBuffers", // the geometry-IR carrier
            "ITileLayer",          // the decoded-tile source interfaces — S2's, not S1's.
            "IDecodedTile",        // (IR C1 deleted ITileFeature, which used to stand here;
                                   //  these two are the surface that inherited its role.)
            "NativeArray",
            "Unity.Burst",
            "Unity.Collections"
        };

        [Test]
        public void T12_TheGeoJsonStageNamesNeitherGeometryCarrier()
        {
            List<string> files = StageSourceFiles();

            // Non-vacuity: a glob that matched nothing would pass this test without reading a byte.
            Assert.That(files.Count, Is.GreaterThanOrEqualTo(5),
                "expected the GeoJson/ sources plus both window clippers — a scan of nothing proves nothing");

            foreach (string file in files)
            {
                string text = File.ReadAllText(file);
                foreach (string token in ForbiddenTokens)
                    Assert.That(text, Does.Not.Contain(token),
                        $"{Path.GetFileName(file)} names \"{token}\": S1 is scoped to what is identical " +
                        "under both arms of the carrier decision, and must not pre-empt it (nor leave the " +
                        "engine-free fast-test loop).");
            }
        }

        private static List<string> StageSourceFiles()
        {
            string core = ResolveUp(Path.Combine("Assets", "Code", "MapRenderer.Core"));
            var files = new List<string>();

            files.AddRange(Directory.GetFiles(Path.Combine(core, "GeoJson"), "*.cs"));
            files.Add(Path.Combine(core, "Geometry", "RingWindowClipper.cs"));
            files.Add(Path.Combine(core, "Geometry", "PolylineWindowClipper.cs"));
            files.Add(Path.Combine(core, "Coordinates", "WebMercatorTiling.cs"));

            foreach (string file in files)
                Assert.That(File.Exists(file), Is.True, $"expected source file not found: {file}");

            return files;
        }

        /// <summary>Walks up from the working directory and from <see cref="AppContext.BaseDirectory"/> —
        /// Unity batch mode and <c>dotnet test</c> start in different places.</summary>
        private static string ResolveUp(string relative)
        {
            string[] starts = { Directory.GetCurrentDirectory(), AppContext.BaseDirectory };
            foreach (string start in starts)
            {
                var dir = new DirectoryInfo(start);
                while (dir != null)
                {
                    string candidate = Path.Combine(dir.FullName, relative);
                    if (Directory.Exists(candidate)) return candidate;
                    dir = dir.Parent;
                }
            }
            throw new DirectoryNotFoundException(
                $"'{relative}' not found walking up from cwd={Directory.GetCurrentDirectory()} " +
                $"or {AppContext.BaseDirectory}");
        }
    }
}
