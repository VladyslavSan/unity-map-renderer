namespace MapRenderer.Core.Text.Placement
{
    /// <summary>
    /// Why the Cull pass classified a record — the per-record verdict Compact consumes. None = kept (no trigger
    /// fired); Dropped = its tile was coverage-dropped (hard-skip, never on screen, no fade). Byte-backed so it
    /// stores in a NativeList and reads from Burst.
    /// <para>Values double as contiguous array indices: the Compact pass sizes a per-trigger tally by the highest
    /// value + 1 and increments <c>tally[(int)trigger]</c>, so keep them 0-based and contiguous with the highest
    /// value last (a new trigger inserted after the current max would silently undersize that buffer).</para>
    /// </summary>
    public enum GatherTrigger : byte
    {
        None = 0, Departing = 1, Coverage = 2, Zoom = 3, Horizon = 4, Distance = 5, Dropped = 6,
    }
}
