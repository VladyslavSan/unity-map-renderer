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
    /// Headless player-build entry points for unity-map-renderer: a release and a <c>…Development</c> method per
    /// platform (Android / macOS / Linux), the release three plus the macOS development player also mirrored as
    /// <c>Tools ▸ Build ▸ …</c> menu items for in-Editor use.
    ///
    /// <para>The method name is the ONLY thing that decides what gets built — platform, variant, and output
    /// path. That is what lets <c>Tools/build.sh &lt;target&gt; [--dev]</c> be a pure name lookup with no
    /// side-channel arguments, and what makes the menu items behave identically to the shell.</para>
    ///
    /// <para><c>Tools/build.sh</c> drives them through the <c>unity</c> CLI's <c>build</c> command, which
    /// <i>requires</i> <c>--execute-method</c>: Unity has no built-in command-line build, so these entry points
    /// are not replaceable by the CLI. The output path is composed HERE, not passed in — the CLI's
    /// <c>--output-path</c> is deliberately unused, because the menu items have no shell to get a path from and
    /// one source of truth beats two. <c>build.sh</c>'s header lists the other CLI flags that are inert for the
    /// same reason.</para>
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

        // ── Entry points ─────────────────────────────────────────────────────────────────────────────
        // Each platform has a release and a DEVELOPMENT entry point, so `Tools/build.sh <target> --dev`
        // means the same thing on every target rather than being macOS-only. Menu items exist for the
        // release three plus the macOS development player (the one used for profiling); the other two
        // development entry points are reached by name from the shell.

        [MenuItem("Tools/Build/Android (APK)")]
        public static void BuildAndroid()            => RunAndroid(development: false);
        public static void BuildAndroidDevelopment() => RunAndroid(development: true);

        [MenuItem("Tools/Build/macOS (.app)")]
        public static void BuildMacOS()            => RunMacOS(development: false);

        /// <summary>
        /// The DEVELOPMENT macOS player — the build that makes the map's telemetry readable outside the Editor
        /// (<c>docs/telemetry-design.md</c> §1.2, the reason the counter consumer exists). See
        /// <see cref="ApplyDevelopmentSettings"/> for what "development" costs and why the backend is not lowered.
        /// </summary>
        [MenuItem("Tools/Build/macOS DEVELOPMENT (.app, profileable)")]
        public static void BuildMacOSDevelopment() => RunMacOS(development: true);

        [MenuItem("Tools/Build/Linux (x86_64)")]
        public static void BuildLinux()            => RunLinux(development: false);
        public static void BuildLinuxDevelopment() => RunLinux(development: true);

        [MenuItem("Tools/Build/Web (WebGL/WebGPU)")]
        public static void BuildWeb()            => RunWeb(development: false);
        public static void BuildWebDevelopment() => RunWeb(development: true);

        private static void RunAndroid(bool development)
        {
            // APK (sideload/preview), not an AAB. An empty keystore => Unity debug-signs it: installable
            // by sideload, NOT Play-Store-publishable. First Android build is slow (target switch =
            // full reimport + IL2CPP/NDK compile).
            EditorUserBuildSettings.buildAppBundle = false;
            RunBuild(BuildTarget.Android, BuildTargetGroup.Android,
                     "Android", ProductBase + ".apk", development);
        }

        private static void RunMacOS(bool development)
        {
            // Architecture follows Player Settings (Edit ▸ Project Settings ▸ Player ▸ macOS ▸ Architecture).
            // Set it to "Intel 64-bit + Apple silicon" (Universal) if the .app must run on other Macs.
            // NOTE: an unsigned .app is Gatekeeper-blocked elsewhere ("damaged"): recipients right-click ▸
            // Open or `xattr -cr <app>`; a proper fix needs an Apple Developer cert + notarization.
            RunBuild(BuildTarget.StandaloneOSX, BuildTargetGroup.Standalone,
                     "macOS", ProductBase + ".app", development);
        }

        private static void RunLinux(bool development)
        {
            RunBuild(BuildTarget.StandaloneLinux64, BuildTargetGroup.Standalone,
                     "Linux", ProductBase + ".x86_64", development);
        }

        /// <summary>
        /// The web player. Two settings are forced here rather than left to the project, because on this
        /// target they are not preferences — a build with either one wrong does not start at all:
        ///
        /// <para><b>Burst AOT is disabled for Web by default.</b> `com.unity.entities` + Burst AOT traps
        /// during static init on this Unity version, before any managed entry point runs (WebGPU hangs
        /// instead of trapping). Reproduced from a stock project with no ECS code, so it is not ours to fix.
        /// The cost is real and is the reason the web player is slow: Burst is the ONLY way a job reaches a
        /// worker thread on the web, so with it off every job runs inline on the main thread. Set
        /// <c>UMR_WEB_BURST=on</c> to build the other way and retest that trap after a Burst or Entities
        /// upgrade; flip the default here once a Burst-on player is observed rendering — see
        /// <c>docs/web-target.md</c>.</para>
        ///
        /// <para><b>Threads support is left ON.</b> It buys nothing while Burst is off — nothing can reach a
        /// worker — and it costs a serving constraint: the player then requires a cross-origin-isolated host
        /// (COOP/COEP on every response, which <c>Tools/serve-web.sh</c> sends and most static hosts do not).
        /// It stays on because this is the configuration that has actually been observed rendering a map;
        /// turning it off is an untested variation, and an unverified build recipe is worse than a
        /// constrained one. Revisit together with Burst.</para>
        /// </summary>
        /// <param name="development">Whether to build the development variant.</param>
        private static void RunWeb(bool development)
        {
            PlayerSettings.WebGL.threadsSupport = true;
            SetWebBurstAot(WebBurstRequested());
            RunBuild(BuildTarget.WebGL, BuildTargetGroup.WebGL, "Web", ProductBase, development);
        }

        /// <summary>Whether this web build should Burst-compile: the <c>UMR_WEB_BURST</c> environment
        /// variable (<c>on</c>/<c>1</c>/<c>true</c> to enable), defaulting to the disabled state the summary
        /// on <see cref="RunWeb"/> explains.</summary>
        /// <returns>True to Burst-compile the web player.</returns>
        private static bool WebBurstRequested()
        {
            string requested = Environment.GetEnvironmentVariable("UMR_WEB_BURST")?.Trim().ToLowerInvariant();
            bool enabled = requested == "on" || requested == "1" || requested == "true";
            if (requested != null)
                Console.WriteLine($"[build] web: UMR_WEB_BURST='{requested}' => Burst AOT {(enabled ? "on" : "off")}");
            return enabled;
        }

        /// <summary>Sets Burst's per-platform AOT toggle for Web through Burst's own settings object, so the
        /// value lands wherever this Burst version keeps it instead of in a hand-written JSON path.</summary>
        /// <param name="enabled">Whether Burst AOT compiles for the web player.</param>
        /// <remarks>Verify the result rather than trusting it: Unity has been observed skipping Burst's build
        /// callback entirely, producing a Burst-less player that reports Burst as enabled. The check that
        /// cannot be fooled is whether <c>lib_burst_generated.wasm</c> appears under <c>Library/Bee</c>.
        /// <c>Tools/build.sh web</c> performs it.</remarks>
        private static void SetWebBurstAot(bool enabled)
        {
            // Look the type up by name across every loaded assembly rather than in a named one. Burst 2.0
            // (Unity 6.6) turned com.unity.burst into a shim package and moved the editor code into the
            // built-in UnityEditor.BurstModule, so the old `Unity.Burst.Editor` assembly no longer exists
            // — and the miss was silent, producing Burst-less players from Burst-on requests.
            const string SettingsType = "Unity.Burst.Editor.BurstPlatformAotSettings";
            Type t = AppDomain.CurrentDomain.GetAssemblies()
                .Select(a => a.GetType(SettingsType, throwOnError: false))
                .FirstOrDefault(found => found != null);
            if (t == null)
                throw new InvalidOperationException(
                    $"No loaded assembly defines {SettingsType} — the web Burst AOT toggle needs porting to " +
                    $"this Burst version. Refusing to build a player whose Burst setting is unknown.");

            const System.Reflection.BindingFlags Any =
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public |
                System.Reflection.BindingFlags.Static    | System.Reflection.BindingFlags.Instance;
            System.Reflection.MethodInfo getOrCreate =
                Member(t.GetMethod("GetOrCreateSettings", Any), "GetOrCreateSettings");
            System.Reflection.MethodInfo resolveTarget =
                Member(t.GetMethod("ResolveTarget", Any), "ResolveTarget");

            object settings = getOrCreate.Invoke(null, WebArgsFor(getOrCreate));
            Member(t.GetField("EnableBurstCompilation", Any), "EnableBurstCompilation").SetValue(settings, enabled);
            // Save against the RESOLVED target, not the requested one: Burst groups some targets onto one
            // settings file, and the resolved handle is the one that names the file the build reads.
            object target = resolveTarget.Invoke(null, WebArgsFor(resolveTarget));
            Member(t.GetMethod("Save", Any), "Save").Invoke(settings, new[] { target });
            Console.WriteLine($"[build] web: Burst AOT EnableBurstCompilation={enabled}");
        }

        /// <summary>Builds the argument list for one of Burst's settings methods: the web build target for
        /// every target-shaped parameter, and each remaining parameter's own default.</summary>
        /// <param name="method">The Burst settings method about to be invoked.</param>
        /// <returns>Arguments positionally matching <paramref name="method"/>.</returns>
        /// <remarks>Reading the parameters instead of hard-coding them is what survives a Burst upgrade:
        /// <c>GetOrCreateSettings</c> grew a second parameter in Burst 2.0, and a fixed argument array turns
        /// that into a build-time <c>TargetParameterCountException</c>.</remarks>
        private static object[] WebArgsFor(System.Reflection.MethodInfo method) =>
            method.GetParameters().Select(p =>
                p.ParameterType == typeof(BuildTarget) || p.ParameterType == typeof(BuildTarget?)
                    ? (object)BuildTarget.WebGL
                    : p.HasDefaultValue
                        ? p.DefaultValue
                        : throw new InvalidOperationException(
                            $"Burst's {method.Name} takes a '{p.Name}' ({p.ParameterType.Name}) with no " +
                            $"default and no meaning this build script knows — the web Burst AOT toggle " +
                            $"needs porting to this Burst version."))
            .ToArray();

        /// <summary>Asserts that a reflected member was found, naming it when it was not.</summary>
        /// <param name="member">The reflection lookup result.</param>
        /// <param name="name">The member's name, for the failure message.</param>
        /// <typeparam name="T">The reflected member kind.</typeparam>
        /// <returns><paramref name="member"/>, never null.</returns>
        /// <remarks>Reflection into Burst's editor internals is version-fragile, and the failure that matters
        /// is the quiet one: a build that reports the Burst setting it never applied. Naming the member turns
        /// "Burst produced nothing" into "Burst moved this."</remarks>
        private static T Member<T>(T member, string name) where T : class =>
            member ?? throw new InvalidOperationException(
                $"Burst's BurstPlatformAotSettings has no '{name}' — the web Burst AOT toggle needs porting " +
                $"to this Burst version.");

        /// <param name="platformDir">Output subfolder under <c>Builds/</c>. A development build appends
        /// <c>-Development</c> to it, so it can never overwrite the release artifact of the same platform —
        /// the two differ in stripping and profiler content and are not interchangeable.</param>
        private static void RunBuild(BuildTarget target, BuildTargetGroup group,
                                     string platformDir, string fileName, bool development)
        {
            string locationPathName = Path.Combine(
                OutputRoot, development ? platformDir + "-Development" : platformDir, fileName);

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

                Action restoreSettings = null;
                if (development) restoreSettings = ApplyDevelopmentSettings(group);
                else             ApplyReleaseSettings(group);

                // EVERYTHING after the settings were applied runs inside this try, so the finally puts them back
                // however we leave — including a throw from the path setup, which would otherwise reach the outer
                // catch, Exit the process, and strand ProjectSettings.asset modified.
                //
                // The restore CANNOT move to an outer finally: every exit path here calls
                // EditorApplication.Exit, which never returns, so an enclosing finally would simply not run.
                BuildSummary summary;
                try
                {
                    string fullOut = Path.GetFullPath(locationPathName);
                    Directory.CreateDirectory(Path.GetDirectoryName(fullOut));

                    var options = new BuildPlayerOptions
                    {
                        scenes           = scenes,
                        locationPathName = locationPathName,
                        target           = target,
                        targetGroup      = group,
                        options          = development
                            // Development => ENABLE_PROFILER + the player auto-connects, so the Profiler picks it
                            // up without hunting for it in the target dropdown.
                            ? BuildOptions.Development | BuildOptions.ConnectWithProfiler
                            : BuildOptions.None,
                    };

                    summary = BuildPipeline.BuildPlayer(options).summary;
                }
                finally { restoreSettings?.Invoke(); }

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

            // WEB EXCEPTION (measured 2026-09-01): High produces a player that builds clean and then never
            // finishes initialising — engine, graphics device, physics and input all come up, then the loader
            // sticks at 100% with no error. Minimal renders, from the same tree with the same Burst setting
            // and one build between them, so this is exactly the runtime hazard the paragraph above warns
            // about rather than a code defect. Suspected cause is Entities' reflection-driven type/system
            // registration; not confirmed, because Minimal was enough to unblock. The cost is binary size
            // (18.6 MB vs 14.0 MB shippable). Revisit with an Assets/link.xml preserving what it needs.
            if (nbt == NamedBuildTarget.WebGL)
                PlayerSettings.SetManagedStrippingLevel(nbt, ManagedStrippingLevel.Minimal);

            // Drop unused engine modules.
            PlayerSettings.stripEngineCode = true;

            Console.WriteLine($"[build] release config: IL2CPP/Release, managed stripping=" +
                              $"{PlayerSettings.GetManagedStrippingLevel(nbt)}, engine-code-strip=on, " +
                              $"development=off ({nbt})");
        }

        /// <summary>
        /// Development counterpart to <see cref="ApplyReleaseSettings"/>: a profileable player, then the committed
        /// Player Settings put back. Development defines <c>ENABLE_PROFILER</c>, so
        /// <c>ProfilerCounterTelemetry</c> is compiled in and the <c>MapRenderer.Tiles.*</c> / <c>.Cache.*</c> /
        /// <c>.Symbols.*</c> counters appear in the Profiler once it attaches.
        ///
        /// <para><b>Same scripting backend as release (IL2CPP), deliberately.</b> A Mono development build would
        /// compile far faster, but this project's perf work is about MANAGED main-thread cost (the label Stage
        /// loop, the batch build), and Mono and IL2CPP do not generate comparable code for it — a Mono profile
        /// would produce numbers that do not describe what ships. Building IL2CPP also exercises the reachability
        /// risk in <c>docs/telemetry-design.md</c> §6: the generic instantiations over
        /// <c>ProfilerCounterValue&lt;int/long/double&gt;</c> have to be statically reachable, and a build that
        /// produces working counters is the proof.</para>
        ///
        /// <para><b>Stripping is lowered to Minimal for development builds only.</b> Release uses High; that is
        /// safe there precisely because the counters do not exist in a release player at all (the whole file is
        /// behind <c>ENABLE_PROFILER</c>), so aggressive stripping cannot remove them wrongly. Here they DO exist
        /// and are reached only through generic instantiation, so Minimal keeps the question "are the counters
        /// reachable" separate from "did the stripper eat them".</para>
        ///
        /// <para><b>Why the restore matters.</b> <c>EditorUserBuildSettings</c> is per-user state under
        /// <c>Library/</c>, but the scripting backend and stripping level live in
        /// <c>ProjectSettings/ProjectSettings.asset</c>, which IS committed — so a development build that simply
        /// left them lowered would show up as a stray repo diff and, worse, silently weaken the NEXT release build
        /// if someone ran it from the Editor rather than through <see cref="ApplyReleaseSettings"/>.</para>
        /// </summary>
        private static Action ApplyDevelopmentSettings(BuildTargetGroup group)
        {
            EditorUserBuildSettings.development     = true;
            EditorUserBuildSettings.connectProfiler = true;
            EditorUserBuildSettings.allowDebugging  = false;   // managed debugger not needed to read counters

            NamedBuildTarget nbt = NamedBuildTarget.FromBuildTargetGroup(group);

            // Capture EVERY setting the block below writes — one omission leaks into the committed
            // ProjectSettings.asset (il2cppCompilerConfiguration did exactly that before it was captured here).
            ScriptingImplementation      backend     = PlayerSettings.GetScriptingBackend(nbt);
            Il2CppCompilerConfiguration  compilerCfg = PlayerSettings.GetIl2CppCompilerConfiguration(nbt);
            ManagedStrippingLevel        stripping   = PlayerSettings.GetManagedStrippingLevel(nbt);
            bool                         stripEngine = PlayerSettings.stripEngineCode;

            // IL2CPP in the RELEASE compiler configuration — the same one release ships (see
            // BuildMacOSDevelopment's rationale).
            //
            // Do NOT lower this to Debug to save build time. Under IL2CPP the managed code IS the generated C++,
            // so Debug (C++ optimisations off) de-optimises exactly the managed main-thread work this build
            // exists to measure — the label Stage loop and batch build — while Burst jobs, which compile
            // natively on their own path, are untouched. The result is a player several times slower than the
            // Editor (Mono JIT, optimised) in precisely the code under study: timings that describe nothing that
            // ships. Development-build overhead is unavoidable; an unoptimised backend is not.
            PlayerSettings.SetScriptingBackend(nbt, ScriptingImplementation.IL2CPP);
            PlayerSettings.SetIl2CppCompilerConfiguration(nbt, Il2CppCompilerConfiguration.Release);
            PlayerSettings.SetManagedStrippingLevel(nbt, ManagedStrippingLevel.Minimal);
            PlayerSettings.stripEngineCode = false;

            Console.WriteLine($"[build] DEVELOPMENT config: IL2CPP/Release, managed stripping=Minimal, " +
                              $"engine-code-strip=off, development=on, profiler auto-connect=on ({nbt})");
            Console.WriteLine("[build] ENABLE_PROFILER is defined => ProfilerCounterTelemetry is compiled in; " +
                              "look for MapRenderer.Tiles.* / .Cache.* / .Symbols.* counters in the Profiler.");

            // Returned rather than hooked onto an editor event: batch mode calls EditorApplication.Exit as soon
            // as the build finishes, so anything deferred to afterAssemblyReload would never run and would leave
            // ProjectSettings.asset modified. The caller invokes this in a finally around BuildPlayer.
            return () =>
            {
                PlayerSettings.SetScriptingBackend(nbt, backend);
                PlayerSettings.SetIl2CppCompilerConfiguration(nbt, compilerCfg);
                PlayerSettings.SetManagedStrippingLevel(nbt, stripping);
                PlayerSettings.stripEngineCode = stripEngine;
                Console.WriteLine("[build] restored committed Player Settings (backend/compiler-config/stripping) " +
                                  "after the development build.");
            };
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
