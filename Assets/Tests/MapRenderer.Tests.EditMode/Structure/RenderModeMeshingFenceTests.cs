// Unity EditMode only — reads source files under Application.dataPath. NOT registered in core-tests.csproj.

using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using NUnit.Framework;
using UnityEngine;
// Disambiguate from UnityEngine.RenderMode (Canvas) — the map's render mode is the material-set one.
using RenderMode = MapRenderer.Unity.Rendering.Materials.RenderMode;

namespace MapRenderer.Tests.Structure
{
    /// <summary>
    /// The unlit epic's HARD CONSTRAINT, made falsifiable: <b>mesh preparation never branches on render
    /// mode.</b> One builder produces one per-layer vertex/mesh layout; the Lit and Unlit shader twins both
    /// consume it. That is what makes unlit a material-set setting rather than a parallel pipeline — and it
    /// holds today only <i>by construction</i>, since nothing in the meshing path names
    /// <see cref="RenderMode"/> at all. This fixture is what notices if that stops being true.
    ///
    /// <para><b>The discriminator (fenced by LOCATION, and stated so a reader can tell an intentional
    /// exception from a regression).</b> The fence covers every source file that participates in turning
    /// decoded tile bytes into a mesh:</para>
    /// <list type="bullet">
    /// <item><c>MapRenderer.Jobs</c> — the whole assembly. It is the blittable data plane: decode, geometry,
    /// triangulation, the Burst job graph. No file in it has any business knowing a shading family.</item>
    /// <item><c>MapRenderer.Unity/Rendering/Meshing</c> — the styled tile builders and layer mesh builds,
    /// the types that own the vertex descriptors the constraint is about.</item>
    /// <item><c>MapRenderer.Unity/Rendering/Tile</c> — the tile pipeline that drives them (build graph,
    /// layer processors, prepared-tile cache).</item>
    /// </list>
    /// <para><c>MapRenderer.Core</c> is deliberately NOT listed: it is engine-free and its asmdef does not
    /// reference <c>MapRenderer.Unity</c>, so it cannot name the enum even if someone tried —
    /// <c>CoreAssemblyBoundaryTests</c> already fences that direction. Everything OUTSIDE the fence
    /// (<c>Rendering/Materials</c>, <c>Rendering/Map</c>, <c>MapRenderer.App</c>) may name the mode freely;
    /// choosing materials and bootstrapping lighting is exactly its job, and two of those folders are this
    /// fixture's positive control.</para>
    ///
    /// <para><b>Why a source scan rather than reflection over member signatures.</b> The hazard is a
    /// <i>branch</i> — <c>if (mode == RenderMode.Unlit) …</c> inside a build method — which changes no
    /// signature and so is invisible to reflection. A comment-stripped text scan sees method bodies.
    /// The repo's recorded lesson is that a text-matching tooth fails OPEN when a rename defeats its
    /// pattern, so the token is not a literal here: it is <c>typeof(RenderMode).Name</c>, read off the real
    /// type at run time. Rename the enum and the fence re-aims itself. Every way to reach the enum from a
    /// fenced file — a using alias, <c>using static</c>, a fully-qualified name, a <c>.RenderMode</c> read
    /// off a <c>MapMaterialSet</c> — writes that token into the file.</para>
    ///
    /// <para><b>Known limitation, and the direction it fails in.</b> Comments are stripped but string
    /// literals are not, so a fenced file that merely quoted the token in a message would trip the fence.
    /// That is a false RED — the safe direction, and cheaper to resolve than the false GREEN the
    /// alternative buys. The fence also cannot see a mode smuggled through an untyped proxy (a
    /// <c>bool isUnlit</c> parameter); nothing structural can. The byte-identity claim is the meshing
    /// constraint's other half, and <c>MapFillUnlitMaterialTests</c>' vertex-layout tooth covers it from
    /// the shader side.</para>
    /// </summary>
    [TestFixture]
    public class RenderModeMeshingFenceTests
    {
        /// <summary>The mesh-preparation source roots, repo-relative to <c>Assets/</c>, each with the
        /// minimum file count that proves the scan actually reached a corpus. A folder rename or a moved
        /// tree reds this fixture instead of silently scanning nothing — the fence fails closed.</summary>
        private static readonly (string Path, int MinFiles)[] FencedRoots =
        {
            (Path.Combine("Code", "MapRenderer.Jobs"),                          40),
            (Path.Combine("Code", "MapRenderer.Unity", "Rendering", "Meshing"),  10),
            (Path.Combine("Code", "MapRenderer.Unity", "Rendering", "Tile"),     15),
        };

        /// <summary>The positive control: the roots that legitimately DO name the render mode — material
        /// selection and the host's lighting bootstrap. If the same scan finds nothing here it is blind, and
        /// the zero it reports over the fenced roots means nothing.</summary>
        private static readonly string[] ControlRoots =
        {
            Path.Combine("Code", "MapRenderer.Unity", "Rendering", "Materials"),
            Path.Combine("Code", "MapRenderer.App"),
        };

        [Test]
        public void NoMeshPreparationSource_NamesTheRenderModeEnum()
        {
            // Derived, never a literal: a rename of the enum re-aims the scan instead of defeating it.
            string token = typeof(RenderMode).Name;

            var offenders = new List<string>();
            int scanned   = 0;

            foreach ((string relativeRoot, int minFiles) in FencedRoots)
            {
                string root = Path.Combine(Application.dataPath, relativeRoot);
                Assert.IsTrue(Directory.Exists(root),
                    $"precondition: the fenced mesh-preparation root '{relativeRoot}' must exist. If the " +
                    "tree moved, re-aim this fence — do not delete the entry.");

                string[] files = Directory.GetFiles(root, "*.cs", SearchOption.AllDirectories);
                Assert.GreaterOrEqual(files.Length, minFiles,
                    $"precondition: '{relativeRoot}' must contain at least {minFiles} .cs files; a scan " +
                    "that visited a near-empty tree would report 'no offenders' just as loudly");

                foreach (string file in files)
                {
                    scanned++;
                    if (StripComments(File.ReadAllText(file)).Contains(token, StringComparison.Ordinal))
                        offenders.Add(file.Substring(Application.dataPath.Length).TrimStart('/', '\\'));
                }
            }

            // Positive control — the SAME comment-stripped matcher, over the folders whose job IS the mode.
            var controlHits = new List<string>();
            foreach (string relativeRoot in ControlRoots)
            {
                string root = Path.Combine(Application.dataPath, relativeRoot);
                Assert.IsTrue(Directory.Exists(root),
                    $"precondition: the control root '{relativeRoot}' must exist — it is what proves the " +
                    "matcher can see the token it reports as absent above");

                foreach (string file in Directory.GetFiles(root, "*.cs", SearchOption.AllDirectories))
                    if (StripComments(File.ReadAllText(file)).Contains(token, StringComparison.Ordinal))
                        controlHits.Add(file.Substring(Application.dataPath.Length).TrimStart('/', '\\'));
            }

            Assert.IsNotEmpty(controlHits,
                $"positive control: the comment-stripped scan MUST find '{token}' in code under " +
                $"{string.Join(" or ", ControlRoots)} — material selection and the lighting bootstrap are " +
                "where the mode belongs. Finding none means the matcher is blind and the fenced roots' " +
                "clean result below proves nothing.");

            TestContext.WriteLine(
                $"[RenderModeMeshingFence] token='{token}' fencedFilesScanned={scanned} " +
                $"controlHits={controlHits.Count} ({string.Join(", ", controlHits)})");

            Assert.IsEmpty(offenders,
                $"no mesh-preparation source may name '{token}'. The unlit epic's hard constraint is that " +
                "the per-layer vertex/mesh layout is byte-identical between the Lit and Unlit twins, so " +
                "mesh preparation NEVER branches on render mode — each builder produces one layout and both " +
                "shaders consume it. A meshing/geometry/tile-pipeline file that can see the mode is one " +
                "edit away from forking the layout, which turns unlit from a material-set setting into a " +
                "parallel pipeline. Choose materials by mode outside the fence (Rendering/Materials, " +
                $"Rendering/Map, MapRenderer.App). Offenders: {string.Join(", ", offenders)}");
        }

        /// <summary>Strips block comments and then everything from <c>//</c> (which covers <c>///</c>) to end
        /// of line, so a doc comment explaining the constraint cannot trip the fence it documents. A grep
        /// guard, not a C# parser — same shape as <c>CoreAssemblyBoundaryTests</c>.</summary>
        /// <param name="text">The full source text of one <c>.cs</c> file.</param>
        /// <returns>The same text with comment spans removed.</returns>
        private static string StripComments(string text)
        {
            string noBlocks = Regex.Replace(text, @"/\*.*?\*/", "", RegexOptions.Singleline);
            string[] lines = noBlocks.Split('\n');
            for (int i = 0; i < lines.Length; i++)
            {
                int idx = lines[i].IndexOf("//", StringComparison.Ordinal);
                if (idx >= 0) lines[i] = lines[i].Substring(0, idx);
            }
            return string.Join('\n', lines);
        }
    }
}
