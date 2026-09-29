namespace MapRenderer.Core.Text.Placement
{
    /// <summary>
    /// Why the Cull pass classified a record, the verdict Compact consumes: None = kept. Byte-backed for
    /// NativeList and Burst. Non-local invariant: Compact sizes a tally by the highest value + 1, so keep values
    /// 0-based and contiguous with the highest one last.
    /// </summary>
    public enum GatherTrigger : byte
    {
        None = 0, Departing = 1, Zoom = 2, Horizon = 3, Distance = 4,
    }
}
