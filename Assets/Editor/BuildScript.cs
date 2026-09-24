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
    /// Headless player-build entry points, one release and one development method per platform, driven by
    /// <c>Tools/build.sh</c> through the <c>unity build</c> CLI. Non-local invariant: the method name alone picks
    /// platform, variant and output path under <c>Builds/</c>, so the shell and the menu items build the same thing.
    /// The enabled Build Settings scenes ship: a public build must enable <c>OpenStreetMapLiberty</c> (it carries
    /// the attribution) and disable <c>MapDemo</c>. Every path exits 0/1 with a <c>BUILD OK</c>/<c>BUILD FAIL</c> line.
    /// </summary>
    public static class BuildScript
    {
        private const string OutputRoot  = "Builds";
        private const string ProductBase = "UnityMapRenderer"; // file-name base (productName without spaces)

        // ── Entry points ─────────────────────────────────────────────────────────────────────────────
        // `Tools/build.sh <target> --dev` works on every target; macOS is the only development variant with a menu item.

        [MenuItem("Tools/Build/Android (APK)")]
        public static void BuildAndroid()            => RunAndroid(development: false);
        public static void BuildAndroidDevelopment() => RunAndroid(development: true);

        [MenuItem("Tools/Build/macOS (.app)")]
        public static void BuildMacOS()            => RunMacOS(development: false);

        /// <summary>
        /// The DEVELOPMENT macOS player — the build that makes the map's telemetry readable outside the Editor
        /// (<c>docs/telemetry-design.md</c> § "Why", item 2, the reason the counter consumer exists). See
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
            // APK, not an AAB. An empty keystore makes Unity debug-sign it: installable by sideload, not
            // publishable to the Play Store.
            EditorUserBuildSettings.buildAppBundle = false;
            RunBuild(BuildTarget.Android, BuildTargetGroup.Android,
                     "Android", ProductBase + ".apk", development);
        }

        private static void RunMacOS(bool development)
        {
            // Architecture follows Player Settings (Universal runs on other Macs). Gatekeeper blocks this
            // unsigned .app elsewhere: recipients run `xattr -cr <app>`, or it needs signing + notarization.
            RunBuild(BuildTarget.StandaloneOSX, BuildTargetGroup.Standalone,
                     "macOS", ProductBase + ".app", development);
        }

        private static void RunLinux(bool development)
        {
            RunBuild(BuildTarget.StandaloneLinux64, BuildTargetGroup.Standalone,
                     "Linux", ProductBase + ".x86_64", development);
        }

        /// <summary>
        /// The web player. It forces Burst AOT (unless <c>UMR_WEB_BURST=off</c>) and threads support, because
        /// Burst is the only route to a worker thread on web. Threads need a cross-origin-isolated host
        /// (COOP/COEP, which <c>Tools/serve-web.sh</c> sends). Do not turn threads off without turning Burst off.
        /// See docs/web-target.md § "The three settings that decide whether a web player starts".
        /// </summary>
        /// <param name="development">Whether to build the development variant.</param>
        private static void RunWeb(bool development)
        {
            PlayerSettings.WebGL.threadsSupport = true;
            SetWebBurstAot(WebBurstRequested());
            RunBuild(BuildTarget.WebGL, BuildTargetGroup.WebGL, "Web", ProductBase, development);
        }

        /// <summary>Whether this web build should Burst-compile: on unless <c>UMR_WEB_BURST</c> says
        /// <c>off</c>/<c>0</c>/<c>false</c>, for the reasons the summary on <see cref="RunWeb"/> gives.</summary>
        /// <returns>True to Burst-compile the web player.</returns>
        private static bool WebBurstRequested()
        {
            string requested = Environment.GetEnvironmentVariable("UMR_WEB_BURST")?.Trim().ToLowerInvariant();
            if (string.IsNullOrEmpty(requested)) return true;

            bool enabled = requested != "off" && requested != "0" && requested != "false";
            Console.WriteLine($"[build] web: UMR_WEB_BURST='{requested}' => Burst AOT {(enabled ? "on" : "off")}");
            return enabled;
        }

        /// <summary>The development-only switches a build was asked for, read from the environment once and
        /// carried together so no two consumers can disagree about them.</summary>
        private readonly struct DevelopmentFlags
        {
            /// <summary>Private so the three same-typed switches can only be ordered wrongly in one place —
            /// <see cref="FromEnvironment"/>, where each argument sits beside the name it reads.</summary>
            /// <param name="connectProfiler">Auto-connect to the Profiler at startup.</param>
            /// <param name="deepProfile">Compile in deep profiling (every managed call sampled).</param>
            /// <param name="allowDebugging">Let a managed debugger attach.</param>
            private DevelopmentFlags(bool connectProfiler, bool deepProfile, bool allowDebugging)
            {
                ConnectProfiler = connectProfiler;
                DeepProfile     = deepProfile;
                AllowDebugging  = allowDebugging;
            }

            /// <summary>Whether the player auto-connects to the Profiler at startup.</summary>
            public bool ConnectProfiler { get; }
            /// <summary>Whether deep profiling (every managed call sampled) is compiled in.</summary>
            public bool DeepProfile { get; }
            /// <summary>Whether a managed debugger may attach.</summary>
            public bool AllowDebugging { get; }

            /// <summary>Reads all three switches from the environment, once.</summary>
            /// <returns>The flags this build was asked for.</returns>
            public static DevelopmentFlags FromEnvironment() => new DevelopmentFlags(
                connectProfiler: ConnectProfilerRequested(),
                deepProfile:     EnvSwitch("UMR_DEEP_PROFILE"),
                allowDebugging:  EnvSwitch("UMR_ALLOW_DEBUGGING"));
        }

        /// <summary>Reads an on/off environment switch. <paramref name="name"/> is treated as OFF when unset
        /// or empty, so the flags all default to the cheaper player.</summary>
        /// <param name="name">Environment variable to read.</param>
        /// <returns>True when the variable says on/1/true.</returns>
        private static bool EnvSwitch(string name)
        {
            string requested = Environment.GetEnvironmentVariable(name)?.Trim().ToLowerInvariant();
            if (string.IsNullOrEmpty(requested)) return false;

            bool enabled = requested == "on" || requested == "1" || requested == "true";
            Console.WriteLine($"[build] {name}='{requested}' => {(enabled ? "on" : "off")}");
            return enabled;
        }

        /// <summary>Whether a development player should auto-connect to the Profiler: off unless
        /// <c>UMR_CONNECT_PROFILER</c> says <c>on</c>/<c>1</c>/<c>true</c>.</summary>
        /// <returns>True to build with <see cref="BuildOptions.ConnectWithProfiler"/>.</returns>
        /// <remarks>Opt-in rather than implied by <c>--dev</c>, because the two are separate things and only
        /// one of them is usually wanted: <c>ENABLE_PROFILER</c> is what compiles the <c>MapRenderer.*</c>
        /// counters into the player, while auto-connect makes that player hunt for an Editor at startup —
        /// paid on every launch, including the runs where nothing is attached. <c>Tools/build.sh --profiler</c>
        /// sets this.</remarks>
        private static bool ConnectProfilerRequested() => EnvSwitch("UMR_CONNECT_PROFILER");

        /// <summary>Whether the graphics jobs Player Setting is being overridden for this build, and to what.
        /// <c>UMR_GRAPHICS_JOBS</c> unset means LEAVE IT ALONE.</summary>
        /// <returns>Null to keep the project's committed value; otherwise the value to build with.</returns>
        /// <remarks>Tri-state, because graphics jobs is a committed rendering setting a build must not silently
        /// redefine. It also affects release players, so <see cref="RunBuild"/> applies it around both variants
        /// and restores it, since writing it modifies the committed <c>ProjectSettings.asset</c>.</remarks>
        private static bool? GraphicsJobsRequested()
        {
            string requested = Environment.GetEnvironmentVariable("UMR_GRAPHICS_JOBS")?.Trim().ToLowerInvariant();
            if (string.IsNullOrEmpty(requested)) return null;

            bool enabled = requested == "on" || requested == "1" || requested == "true";
            Console.WriteLine($"[build] UMR_GRAPHICS_JOBS='{requested}' => graphics jobs {(enabled ? "on" : "off")}");
            return enabled;
        }

        /// <summary>Applies the graphics jobs override, if one was asked for.</summary>
        /// <returns>An action restoring the committed value, or null when nothing was overridden.</returns>
        /// <remarks><c>PlayerSettings.graphicsJobs</c> is project-wide rather than per-target, so this
        /// captures and restores a single value.</remarks>
        private static Action ApplyGraphicsJobsOverride()
        {
            bool? requested = GraphicsJobsRequested();
            if (requested is null) return null;

            bool previous = PlayerSettings.graphicsJobs;
            PlayerSettings.graphicsJobs = requested.Value;
            Console.WriteLine($"[build] GraphicsJobs={(requested.Value ? "True" : "False")} (was {previous})");
            return () => PlayerSettings.graphicsJobs = previous;
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
            // Search every loaded assembly: Burst 2.0 keeps this type in the built-in UnityEditor.BurstModule,
            // and a lookup in a named assembly that lacks it would build a Burst-less player silently.
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
        /// <c>-Development</c>, so it never overwrites the release one. Only <c>--clean</c> deletes output.</param>
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

                // Read ONCE and pass down: each of these lands in two places (the player options below and
                // EditorUserBuildSettings), which must not be able to disagree about it.
                DevelopmentFlags flags = development ? DevelopmentFlags.FromEnvironment() : default;

                Action restoreSettings = null;
                if (development) restoreSettings = ApplyDevelopmentSettings(group, flags);
                else             ApplyReleaseSettings(group);

                // Applied around BOTH variants: graphics jobs is a rendering setting a release player has
                // too, not development machinery. Null when nothing was overridden.
                Action restoreGraphicsJobs = ApplyGraphicsJobsOverride();

                // Everything after the settings change runs in this try, so the finally restores them on any exit.
                // An outer finally would not run: every exit path calls EditorApplication.Exit, which never returns.
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
                        // Development defines ENABLE_PROFILER, so the counters exist either way.
                        // ConnectWithProfiler (opt-in) makes the player look for an Editor at startup.
                        options          = development
                            ? BuildOptions.Development
                              | (flags.ConnectProfiler ? BuildOptions.ConnectWithProfiler        : BuildOptions.None)
                              | (flags.DeepProfile     ? BuildOptions.EnableDeepProfilingSupport : BuildOptions.None)
                              | (flags.AllowDebugging  ? BuildOptions.AllowDebugging             : BuildOptions.None)
                            : BuildOptions.None,
                    };

                    summary = BuildPipeline.BuildPlayer(options).summary;
                }
                finally { restoreGraphicsJobs?.Invoke(); restoreSettings?.Invoke(); }

                if (summary.result == BuildResult.Succeeded)
                {
                    // Report the shippable size of the whole output dir minus Unity's non-ship symbol dirs,
                    // which summary.totalSize counts. The dir, because a Linux .x86_64 is only a launcher.
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

            // High stripping: style JSON is parsed by hand, not by reflection. Limitation: a stripping break shows
            // only at run time (a web player can hang at 100%), so smoke-test before publishing, and fix a break
            // with Assets/link.xml or [Preserve], not a lower level.
            PlayerSettings.SetManagedStrippingLevel(nbt, ManagedStrippingLevel.High);

            // Drop unused engine modules.
            PlayerSettings.stripEngineCode = true;

            Console.WriteLine($"[build] release config: IL2CPP/Release, managed stripping=" +
                              $"{PlayerSettings.GetManagedStrippingLevel(nbt)}, engine-code-strip=on, " +
                              $"development=off ({nbt})");
        }

        /// <summary>
        /// Development counterpart to <see cref="ApplyReleaseSettings"/>: a profileable player with the
        /// <c>ENABLE_PROFILER</c> counters compiled in. Non-obvious why: it keeps IL2CPP, because a Mono profile
        /// does not describe what ships, and IL2CPP proves the counters' generic instantiations are reachable
        /// (<c>docs/telemetry-design.md</c> § "Risks"); Minimal stripping keeps "reachable" apart from "stripped".
        /// The caller restores these settings, because they live in the committed <c>ProjectSettings.asset</c>.
        /// </summary>
        /// <param name="group">Build target group whose Player Settings are being staged.</param>
        /// <param name="flags">The development switches this build was asked for. Each is independent of
        /// <c>ENABLE_PROFILER</c>, which development defines regardless.</param>
        /// <returns>An action that restores every Player Setting this method wrote.</returns>
        private static Action ApplyDevelopmentSettings(BuildTargetGroup group, DevelopmentFlags flags)
        {
            EditorUserBuildSettings.development                  = true;
            EditorUserBuildSettings.connectProfiler              = flags.ConnectProfiler;
            EditorUserBuildSettings.buildWithDeepProfilingSupport = flags.DeepProfile;
            EditorUserBuildSettings.allowDebugging               = flags.AllowDebugging;

            NamedBuildTarget nbt = NamedBuildTarget.FromBuildTargetGroup(group);

            // Capture EVERY setting the block below writes — one omission leaks into the committed
            // ProjectSettings.asset (il2cppCompilerConfiguration did exactly that before it was captured here).
            ScriptingImplementation      backend     = PlayerSettings.GetScriptingBackend(nbt);
            Il2CppCompilerConfiguration  compilerCfg = PlayerSettings.GetIl2CppCompilerConfiguration(nbt);
            ManagedStrippingLevel        stripping   = PlayerSettings.GetManagedStrippingLevel(nbt);
            bool                         stripEngine = PlayerSettings.stripEngineCode;

            // IL2CPP in the Release compiler configuration, as release ships. Debug would de-optimise the
            // generated C++ of the managed main-thread code this build exists to measure.
            PlayerSettings.SetScriptingBackend(nbt, ScriptingImplementation.IL2CPP);
            PlayerSettings.SetIl2CppCompilerConfiguration(nbt, Il2CppCompilerConfiguration.Release);
            PlayerSettings.SetManagedStrippingLevel(nbt, ManagedStrippingLevel.Minimal);
            PlayerSettings.stripEngineCode = false;

            Console.WriteLine($"[build] DEVELOPMENT config: IL2CPP/Release, managed stripping=Minimal, " +
                              $"engine-code-strip=off, development=on, " +
                              $"ConnectProfiler={(flags.ConnectProfiler ? "True" : "False")}, " +
                              $"DeepProfile={(flags.DeepProfile ? "True" : "False")}, " +
                              $"AllowDebugging={(flags.AllowDebugging ? "True" : "False")} ({nbt})");
            Console.WriteLine("[build] ENABLE_PROFILER is defined => ProfilerCounterTelemetry is compiled in; " +
                              "look for MapRenderer.Tiles.* / .Cache.* / .Symbols.* counters in the Profiler.");

            // Returned, not hooked onto an editor event: batch mode exits right after the build, so a deferred
            // restore would never run. The caller invokes this in a finally around BuildPlayer.
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
