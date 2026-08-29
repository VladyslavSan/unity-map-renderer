using Unity.Burst;
using Unity.Jobs.LowLevel.Unsafe;
using UnityEngine;

namespace MapRenderer.App
{
    /// <summary>
    /// One-shot log of the perf-relevant runtime settings — the ones that silently make the build slow and
    /// are invisible in a player (no editor menus). Call <see cref="Log"/> once at startup. Works in a WebGL
    /// build too, which is where <see cref="BurstActive"/> earns its keep: the editor's Burst-compilation
    /// toggle is editor-only and cannot be read from a player, so the truth of whether Burst is running has
    /// to be probed by executing a Burst function and seeing whether the managed fallback fired.
    /// </summary>
    [BurstCompile]
    public static class RuntimeDiagnostics
    {
        /// <summary>Emit the settings line to the Unity console. Cheap; the one cost is the first
        /// <see cref="BurstActive"/> call JIT-compiling its probe (editor only) — hence startup, not per-frame.</summary>
        public static void Log()
        {
            bool burst = BurstActive();
            string line =
                $"[Diag] platform={Application.platform} gfx={SystemInfo.graphicsDeviceType} " +
                $"devBuild={Debug.isDebugBuild} cpus={SystemInfo.processorCount} " +
                $"jobWorkers={JobsUtility.JobWorkerCount} " +
                $"burstActive={burst}";

            // Burst off ⇒ every .Run() job executes as managed IL (~10-15× slower); this is invisible without
            // a probe (the editor toggle is per-user and unreadable in a player), so shout about it.
            if (burst) Debug.Log(line);
            else Debug.LogWarning(line + "  ← BURST IS OFF: jobs run as managed IL, expect a major slowdown.");
        }

        /// <summary>Whether Burst is actually compiling and running, not what a menu claims. Runs a tiny
        /// Burst function pointer whose only side effect is via <see cref="Flag"/>, which carries
        /// <c>[BurstDiscard]</c> — so it is skipped in Burst-compiled code and runs only on the managed
        /// fallback. Result 0 ⇒ Burst active, 1 ⇒ managed.</summary>
        public static bool BurstActive()
        {
            var fp = BurstCompiler.CompileFunctionPointer<Probe>(RunProbe);
            return fp.Invoke() == 0;
        }

        /// <summary>Signature for the Burst-compiled probe (see <see cref="BurstActive"/>).</summary>
        private delegate int Probe();

        /// <summary>The probed function: returns 1 only if <see cref="Flag"/> (managed-only) ran.
        /// <c>CompileSynchronously</c> so <see cref="BurstActive"/>'s first call gets the real Burst version,
        /// not the async-JIT managed fallback the editor hands back while it compiles in the background
        /// (which would make an enabled-Burst editor read as inactive).</summary>
        [BurstCompile(CompileSynchronously = true)]
        private static int RunProbe()
        {
            int managed = 0;
            Flag(ref managed);
            return managed;
        }

        /// <summary>Managed-only marker — <c>[BurstDiscard]</c> means Burst-compiled code never calls it,
        /// so a surviving write proves the caller ran as managed IL.</summary>
        [BurstDiscard]
        private static void Flag(ref int managed) => managed = 1;
    }
}
