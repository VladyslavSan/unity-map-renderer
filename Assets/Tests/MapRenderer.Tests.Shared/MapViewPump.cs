using System;
using System.Collections;
using System.Threading;
using NUnit.Framework;
using MapViewComponent = MapRenderer.Unity.Rendering.Map.MapViewComponent;

namespace MapRenderer.Tests
{
    /// <summary>
    /// The frame loops a test runs to let a <see cref="MapViewComponent"/> load its cover. Call them bare, through
    /// <c>using static MapRenderer.Tests.MapViewPump;</c>. The synchronous loops end silently when their frame cap runs out.
    /// </summary>
    internal static class MapViewPump
    {
        /// <summary>Default frame cap of the synchronous loops.</summary>
        public const int DefaultMaxFrames = 2500;

        /// <summary>Updates and drains mesh builds until the view has tiles and all of them are settled.
        /// Non-obvious why: the <c>LoadedTileCount() &gt; 0</c> guard is load-bearing, because <c>AllTilesSettled()</c> is true on an empty cover.</summary>
        public static void PumpUntilSettled(MapViewComponent view, int maxFrames = DefaultMaxFrames)
        {
            for (int f = 0; f < maxFrames; f++)
            {
                view.LateUpdate();
                view.DrainMeshBuilds();
                if (view.LoadedTileCount() > 0 && view.AllTilesSettled()) return;
            }
        }

        /// <summary>As <see cref="PumpUntilSettled"/>, but waits for in-flight builds instead of draining them.
        /// Non-obvious why: the symbol pass rides the mesh kick, so draining never lands a label build, and the drain form silently yields no labels.</summary>
        public static void PumpUntilSettledByAwaiting(MapViewComponent view, int maxFrames = DefaultMaxFrames)
        {
            for (int f = 0; f < maxFrames; f++)
            {
                view.LateUpdate();
                if (view.LoadedTileCount() > 0 && view.AllTilesSettled()) return;
                view.AwaitInFlightMeshBuilds();
            }
        }

        /// <summary>Updates once per real frame until the view has tiles and all of them are settled, or the wall-clock bound ends.</summary>
        public static IEnumerator PumpUntilSettledAcrossFrames(MapViewComponent view)
            => PumpUntilAcrossFrames(view, () => view.LoadedTileCount() > 0 && view.AllTilesSettled());

        /// <summary>Updates once per real frame until <paramref name="done"/> holds, or the wall-clock bound ends.</summary>
        public static IEnumerator PumpUntilAcrossFrames(MapViewComponent view, Func<bool> done)
        {
            for (var settle = SettleTimeout.Start(); settle.Running; )
            {
                view.LateUpdate();
                if (done()) yield break;
                yield return null;
            }
        }

        /// <summary>
        /// Updates, runs <paramref name="afterTick"/>, and sleeps one millisecond, until <paramref name="done"/> holds.
        /// Fails the test when the wall-clock bound ends first.
        /// </summary>
        public static void PumpUntil(MapViewComponent view, Func<bool> done, string what, Action afterTick = null)
        {
            for (var settle = SettleTimeout.Start(); settle.Running;)
            {
                view.LateUpdate();
                Thread.Sleep(1); // builds run on worker threads; a pump never depends on which tick one finishes
                afterTick?.Invoke();
                if (done()) return;
            }

            Assert.Fail($"timed out waiting for: {what}");
        }
    }
}
