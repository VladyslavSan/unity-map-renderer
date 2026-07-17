using System;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;
using UnityEngine;

namespace MapRenderer.Build
{
    /// <summary>
    /// Headless player-build entry points for unity-map-renderer, invoked from <c>Tools/build.sh</c> via
    /// <c>-executeMethod MapRenderer.Build.BuildScript.BuildAndroid|BuildMacOS|BuildLinux</c> (and mirrored
    /// as <c>Tools ▸ Build ▸ …</c> menu items for in-Editor use).
    ///
    /// <para><b>Which scene ships is Build Settings' job, not this script's.</b> The scene list comes from
    /// <see cref="EditorBuildSettings.scenes"/> (the enabled entries). For a PUBLIC build, enable the
    /// <c>OpenStreetMapLiberty</c> scene (which carries the attribution overlay) and DISABLE the internal
    /// <c>MapDemo</c> scene first — otherwise the build ships the dev scene with no on-screen credit. The
    /// script logs the exact scene list it is about to build and hard-fails if none are enabled.</para>
    ///
    /// <para>Output goes under <c>Builds/&lt;platform&gt;/</c> (git-ignored). Every path calls
    /// <see cref="EditorApplication.Exit"/> explicitly (0 = ok, 1 = fail) so the shell wrapper gets a
    /// trustworthy exit code, and prints a grep-able <c>BUILD OK &lt;path&gt;</c> / <c>BUILD FAIL …</c>
    /// sentinel that <c>build.sh</c> echoes back.</para>
    /// </summary>
    public static class BuildScript
    {
        private const string OutputRoot  = "Builds";
        private const string ProductBase = "UnityMapRenderer"; // file-name base (productName without spaces)

        [MenuItem("Tools/Build/Android (APK)")]
        public static void BuildAndroid()
        {
            // APK (sideload/preview), not an AAB. An empty keystore => Unity debug-signs it: installable
            // by sideload, NOT Play-Store-publishable. First Android build is slow (target switch =
            // full reimport + IL2CPP/NDK compile).
            EditorUserBuildSettings.buildAppBundle = false;
            RunBuild(BuildTarget.Android, BuildTargetGroup.Android,
                     Path.Combine(OutputRoot, "Android", ProductBase + ".apk"));
        }

        [MenuItem("Tools/Build/macOS (.app)")]
        public static void BuildMacOS()
        {
            // Architecture follows Player Settings (Edit ▸ Project Settings ▸ Player ▸ macOS ▸ Architecture).
            // Set it to "Intel 64-bit + Apple silicon" (Universal) if the .app must run on other Macs.
            // NOTE: an unsigned .app is Gatekeeper-blocked elsewhere ("damaged"): recipients right-click ▸
            // Open or `xattr -cr <app>`; a proper fix needs an Apple Developer cert + notarization.
            RunBuild(BuildTarget.StandaloneOSX, BuildTargetGroup.Standalone,
                     Path.Combine(OutputRoot, "macOS", ProductBase + ".app"));
        }

        [MenuItem("Tools/Build/Linux (x86_64)")]
        public static void BuildLinux()
        {
            RunBuild(BuildTarget.StandaloneLinux64, BuildTargetGroup.Standalone,
                     Path.Combine(OutputRoot, "Linux", ProductBase + ".x86_64"));
        }

        private static void RunBuild(BuildTarget target, BuildTargetGroup group, string locationPathName)
        {
            try
            {
                string[] scenes = EditorBuildSettings.scenes
                    .Where(s => s.enabled)
                    .Select(s => s.path)
                    .ToArray();

                if (scenes.Length == 0)
                {
                    Fail("no scenes enabled in Build Settings — enable the scene to ship " +
                         "(File ▸ Build Settings), e.g. the public OpenStreetMapLiberty scene.");
                    return;
                }

                Console.WriteLine($"[build] target={target}  product={Application.productName}");
                Console.WriteLine("[build] scenes to include:\n  " + string.Join("\n  ", scenes));

                ApplyReleaseSettings(group);

                string fullOut = Path.GetFullPath(locationPathName);
                Directory.CreateDirectory(Path.GetDirectoryName(fullOut));

                var options = new BuildPlayerOptions
                {
                    scenes           = scenes,
                    locationPathName = locationPathName,
                    target           = target,
                    targetGroup      = group,
                    options          = BuildOptions.None,
                };

                BuildSummary summary = BuildPipeline.BuildPlayer(options).summary;
                if (summary.result == BuildResult.Succeeded)
                {
                    // Report the SHIPPABLE payload size — the platform output dir minus the sibling
                    // _BackUpThisFolder / _DoNotShip symbol dirs Unity drops next to it (which
                    // summary.totalSize wrongly counts, reading as multi-GB). Measuring the dir (not just
                    // locationPathName) is also what makes Linux correct: its .x86_64 is a tiny launcher
                    // and the real payload is the sibling _Data folder + UnityPlayer.so.
                    double mb = ShippableSizeBytes(locationPathName) / (1024.0 * 1024.0);
                    Console.WriteLine($"BUILD OK {locationPathName} ({mb:F1} MB shippable, built in {summary.totalTime})");
                    EditorApplication.Exit(0);
                }
                else
                {
                    Fail($"{summary.result} — {summary.totalErrors} error(s); see the log above.");
                }
            }
            catch (Exception e)
            {
                // An uncaught -executeMethod exception can still exit 0 on some Unity versions — never
                // let that happen for a build.
                Fail(e.ToString());
            }
        }

        /// <summary>
        /// Pin an explicit RELEASE configuration instead of inheriting whatever Player Settings happen to
        /// hold. Unity has no per-build override for scripting backend / stripping, so these persist into
        /// ProjectSettings — but they only re-assert the intended release shape (matching the current
        /// values), so a release is reproducible regardless of settings drift.
        /// </summary>
        private static void ApplyReleaseSettings(BuildTargetGroup group)
        {
            // Non-development player: no managed debugger, profiler auto-connect, or dev watermark.
            EditorUserBuildSettings.development     = false;
            EditorUserBuildSettings.allowDebugging  = false;
            EditorUserBuildSettings.connectProfiler = false;

            NamedBuildTarget nbt = NamedBuildTarget.FromBuildTargetGroup(group);

            // IL2CPP (AOT → native; no decompilable managed assemblies ship) in the Release compiler config.
            PlayerSettings.SetScriptingBackend(nbt, ScriptingImplementation.IL2CPP);
            PlayerSettings.SetIl2CppCompilerConfiguration(nbt, Il2CppCompilerConfiguration.Release);

            // Managed code stripping = HIGH (maximum). The usual High hazard is reflection-only types
            // getting stripped, but this codebase has none in shipped code: style/TileJSON parsing is a
            // hand-written recursive-descent parser (JsonParser -> JsonValue -> manual field reads), not
            // JsonUtility/Newtonsoft, and the only System.Reflection usage lives in EditMode tests (never
            // built). So nothing app-side depends on metadata High would remove.
            //   CAVEAT: High is a RUNTIME concern — a stripped build can compile clean yet break only when
            //   run (missing type/method surfaces as a NullRef/empty result, not a build error). If a
            //   package's internal reflection ever trips, preserve the needed types with an
            //   Assets/link.xml (or a [Preserve] attribute) rather than dropping the level. Smoke-test a
            //   High-stripped build (map loads, tiles render, input works) before publishing.
            PlayerSettings.SetManagedStrippingLevel(nbt, ManagedStrippingLevel.High);

            // Drop unused engine modules.
            PlayerSettings.stripEngineCode = true;

            Console.WriteLine($"[build] release config: IL2CPP/Release, managed stripping=High, " +
                              $"engine-code-strip=on, development=off ({nbt})");
        }

        // Scratch dirs Unity drops in the output folder that are NOT part of the shippable player.
        private static readonly string[] NonShipMarkers =
        {
            "_BackUpThisFolder_ButDontShipItWithYourGame",
            "_BurstDebugInformation_DoNotShip",
        };

        /// <summary>Total bytes of the shippable payload: every file in the platform output directory
        /// (the whole .app bundle, or the Linux exe + _Data + UnityPlayer.so, or the .apk) EXCEPT the
        /// non-ship symbol/backup scratch dirs. Each platform builds into its own subfolder, so the
        /// directory's non-scratch contents are exactly what gets distributed.</summary>
        private static long ShippableSizeBytes(string artifactPath)
        {
            string dir = Path.GetDirectoryName(Path.GetFullPath(artifactPath));
            if (!Directory.Exists(dir)) return 0;
            return Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories)
                            .Where(f => !NonShipMarkers.Any(f.Contains))
                            .Sum(f => new FileInfo(f).Length);
        }

        private static void Fail(string message)
        {
            Console.WriteLine("BUILD FAIL " + message);
            EditorApplication.Exit(1);
        }
    }
}
