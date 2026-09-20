// The tooth under ProfilerCounterTelemetry's counter-construction choice: every counter is created with
// FlushOnEndOfFrame and WITHOUT ResetToZeroOnFlush, because these are LEVELS. docs/telemetry-design.md §3 states
// the consequence — a provider whose pass did not run leaves its counters holding their last real value rather
// than reading as a zero the map never had. That was a comment on the production type and nothing verified it.
//
// The reset-on-flush counter is the VALIDITY CONTROL, not a bonus assertion: if it does not go to zero, no flush
// happened in this environment and the hold result proves nothing. Without it a green run is indistinguishable
// from "EditMode never flushed", which is the exact way this test could lie while looking healthy.
//
// Scope: Editor/EditMode with ENABLE_PROFILER. A Development standalone build is unverified (design doc §8, the
// maintainer's step) and a release player strips the counters entirely.

using System.Collections;
using NUnit.Framework;
using Unity.Profiling;
using UnityEngine.TestTools;

namespace MapRenderer.Tests.Tiles
{
    [TestFixture]
    public class ProfilerCounterHoldTests
    {
        private static ProfilerCategory Category => new("MapRendererProbe");

        [Test]
        public void CounterValue_RoundTripsWithinASingleFrame()
        {
            var counter = new ProfilerCounterValue<int>(
                Category, "Probe.SameFrame", ProfilerMarkerDataUnit.Count, ProfilerCounterOptions.FlushOnEndOfFrame);

            counter.Value = 123;

            Assert.AreEqual(123, counter.Value,
                "a counter must at least hold what was just written to it — everything below assumes this.");
        }

        [UnityTest]
        public IEnumerator CounterValue_AcrossAFrameBoundary_HoldsWithoutResetOption_AndZeroesWithIt()
        {
            var held = new ProfilerCounterValue<int>(
                Category, "Probe.Held", ProfilerMarkerDataUnit.Count, ProfilerCounterOptions.FlushOnEndOfFrame);

            var reset = new ProfilerCounterValue<int>(
                Category, "Probe.Reset", ProfilerMarkerDataUnit.Count,
                ProfilerCounterOptions.FlushOnEndOfFrame | ProfilerCounterOptions.ResetToZeroOnFlush);

            held.Value = 456;
            reset.Value = 789;

            yield return null;

            // Read the control FIRST and report it even when the main assertion would pass: a held value that
            // survived because nothing flushed is not evidence of anything.
            var controlZeroed = reset.Value == 0;
            UnityEngine.Debug.Log($"COUNTER-HOLD: control(ResetToZeroOnFlush)={reset.Value} held(no reset)={held.Value} " +
                                  $"=> flush observed: {controlZeroed}");

            Assert.IsTrue(controlZeroed,
                "VALIDITY CONTROL failed: the reset-on-flush counter kept its value, so no end-of-frame flush " +
                "occurred in EditMode. The hold-across-frames claim is UNVERIFIED here — re-check under PlayMode " +
                "or a Development standalone build before relying on it.");

            Assert.AreEqual(456, held.Value,
                "a flush DID occur (the control zeroed) and the no-reset counter kept its value — which is what lets " +
                "ProfilerCounterTelemetry report levels that persist across a frame whose provider did not run.");
        }
    }
}
