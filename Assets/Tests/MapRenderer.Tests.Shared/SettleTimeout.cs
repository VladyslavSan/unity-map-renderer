using System.Diagnostics;

namespace MapRenderer.Tests
{
    /// <summary>
    /// The wall-clock bound for a test loop that pumps frames until asynchronous work signals completion.
    /// An EditMode frame rate is not tied to wall-clock time, so a frame count cannot bound such a wait.
    /// The loop exits on its completion signal; this bound only stops a genuine hang.
    /// Use: <c>for (var settle = SettleTimeout.Start(); settle.Running &amp;&amp; !done; ) { …; yield return null; }</c>
    /// </summary>
    public readonly struct SettleTimeout
    {
        /// <summary>Seconds a loop may wait for its completion signal before it gives up.</summary>
        private const double Seconds = 30.0;

        /// <summary>The <c>Stopwatch</c> timestamp at which the bound started.</summary>
        private readonly long _startTimestamp;

        /// <summary>Creates a bound that started at <paramref name="startTimestamp"/>.</summary>
        private SettleTimeout(long startTimestamp) => _startTimestamp = startTimestamp;

        /// <summary>Starts the bound now.</summary>
        public static SettleTimeout Start() => new SettleTimeout(Stopwatch.GetTimestamp());

        /// <summary>True until the bound has elapsed.</summary>
        public bool Running => (Stopwatch.GetTimestamp() - _startTimestamp) / (double)Stopwatch.Frequency < Seconds;
    }
}
