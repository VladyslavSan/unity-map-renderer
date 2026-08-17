namespace MapRenderer.Jobs.Mvt
{
    /// <summary>
    /// Selects which <c>IMvtPropertyStore</c> implementation <see cref="MvtDecoder"/> builds for each
    /// decoded feature's properties. <see cref="Dictionary"/> is the default — today's eager per-feature
    /// <c>Dictionary&lt;string,Value&gt;</c>, kept as the oracle and as production's byte-identical
    /// behaviour this stage. <see cref="Dense"/> keeps MVT's dense (keyIdx,valIdx) tag-pair representation
    /// instead of expanding it, eliminating that per-feature allocation; a later stage flips the default
    /// once it is proven in. Both implementations stay in the codebase (maintainer-requested A/B structure).
    /// </summary>
    public enum MvtPropertyStorage
    {
        Dictionary = 0,
        Dense = 1,
    }
}
