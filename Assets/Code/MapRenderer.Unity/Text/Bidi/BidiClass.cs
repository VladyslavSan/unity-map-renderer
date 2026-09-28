// Engine-free: no UnityEngine dependency.

namespace MapRenderer.Unity.Text.Bidi
{
    /// <summary>
    /// The UAX #9 Bidi_Class values. The numeric order is the index the generated run table stores, so the
    /// order matches <c>CLASSES</c> in <c>Tools/generate-bidi-tables.py</c>.
    /// </summary>
    internal enum BidiClass : byte
    {
        /// <summary>Left-to-right strong.</summary>
        L,
        /// <summary>Right-to-left strong.</summary>
        R,
        /// <summary>Arabic letter, strong right-to-left.</summary>
        AL,
        /// <summary>European number.</summary>
        EN,
        /// <summary>European number separator.</summary>
        ES,
        /// <summary>European number terminator.</summary>
        ET,
        /// <summary>Arabic number.</summary>
        AN,
        /// <summary>Common number separator.</summary>
        CS,
        /// <summary>Nonspacing mark.</summary>
        NSM,
        /// <summary>Boundary neutral.</summary>
        BN,
        /// <summary>Paragraph separator.</summary>
        B,
        /// <summary>Segment separator.</summary>
        S,
        /// <summary>Whitespace.</summary>
        WS,
        /// <summary>Other neutral.</summary>
        ON,
        /// <summary>Left-to-right embedding.</summary>
        LRE,
        /// <summary>Left-to-right override.</summary>
        LRO,
        /// <summary>Right-to-left embedding.</summary>
        RLE,
        /// <summary>Right-to-left override.</summary>
        RLO,
        /// <summary>Pop directional format.</summary>
        PDF,
        /// <summary>Left-to-right isolate.</summary>
        LRI,
        /// <summary>Right-to-left isolate.</summary>
        RLI,
        /// <summary>First strong isolate.</summary>
        FSI,
        /// <summary>Pop directional isolate.</summary>
        PDI,
    }
}
