// Engine-free: no UnityEngine, no UnityEditor. Compiled verbatim by both the Unity EditMode runner
// and Tools/core-tests.

using System;
using System.IO;
using NUnit.Framework;

namespace MapRenderer.Tests
{
    /// <summary>
    /// Engine-free shader path resolution for tests that must compile under <c>Tools/core-tests</c>,
    /// where <c>ShaderPropertyParser</c>'s <c>UnityEditor.AssetDatabase</c>-based
    /// <c>MapShaderPath</c> is unavailable. Resolves a shader under <c>Shaders/Map/</c> by filename,
    /// recursively. Limitation: unlike the GUID lookup, it breaks if <c>MapRenderer.Unity</c> moves.
    /// </summary>
    internal static class EngineFreeShaderPaths
    {
        /// <summary>
        /// Resolves <paramref name="fileName"/> to its absolute path under <c>Shaders/Map/</c>, walking
        /// up from both the working directory and the assembly base directory (the two differ between
        /// the Unity EditMode runner and <c>Tools/core-tests</c>, and neither is the repo root). Fails
        /// the test loudly if zero or more than one match is found.
        /// </summary>
        public static string ResolveMapShaderPath(string fileName)
        {
            string[] starts = { Directory.GetCurrentDirectory(), AppDomain.CurrentDomain.BaseDirectory };
            foreach (string start in starts)
            {
                string dir = start;
                for (int i = 0; i < 16 && dir != null; i++)
                {
                    string mapDir = Path.Combine(dir, "Assets", "Code", "MapRenderer.Unity", "Shaders", "Map");
                    if (Directory.Exists(mapDir))
                    {
                        string[] matches = Directory.GetFiles(mapDir, fileName, SearchOption.AllDirectories);
                        Assert.That(matches.Length, Is.EqualTo(1),
                            $"Expected exactly one '{fileName}' under Shaders/Map/; found {matches.Length}" +
                            (matches.Length > 1 ? " [" + string.Join(", ", matches) + "]" : "") + ".");
                        return matches[0];
                    }
                    dir = Directory.GetParent(dir)?.FullName;
                }
            }

            Assert.Fail($"Shaders/Map directory not found walking up from cwd=" +
                $"{Directory.GetCurrentDirectory()} or {AppDomain.CurrentDomain.BaseDirectory}.");
            return null;
        }
    }
}
