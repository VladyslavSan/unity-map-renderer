// Engine-free: no UnityEngine dependency. The tables come from UnicodeBidiData.g.cs.

using System;

namespace MapRenderer.Unity.Text.Bidi
{
    /// <summary>Lookups over the generated UAX #9 property tables.</summary>
    internal static partial class UnicodeBidiData
    {
        /// <summary>The Bidi_Class of <paramref name="codepoint"/>. A value outside Unicode gives <see cref="BidiClass.L"/>.</summary>
        internal static BidiClass ClassOf(int codepoint)
        {
            if (codepoint < 0 || codepoint > 0x10FFFF) return BidiClass.L;
            int index = Array.BinarySearch(RunStarts, codepoint);
            if (index < 0) index = ~index - 1;
            return (BidiClass)RunClasses[index];
        }

        /// <summary>The Bidi_Paired_Bracket and bracket type of <paramref name="codepoint"/>, or null when it is no bracket.</summary>
        internal static (int Paired, bool IsOpening)? BracketOf(int codepoint)
        {
            int index = Array.BinarySearch(BracketCodepoints, codepoint);
            if (index < 0) return null;
            return (BracketPaired[index], BracketIsOpening[index]);
        }

        /// <summary>The Bidi_Mirroring_Glyph of <paramref name="codepoint"/>, or <paramref name="codepoint"/> itself when it has none.</summary>
        internal static int MirrorOf(int codepoint)
        {
            int index = Array.BinarySearch(MirrorCodepoints, codepoint);
            return index >= 0 ? MirrorPartners[index] : codepoint;
        }
    }
}
