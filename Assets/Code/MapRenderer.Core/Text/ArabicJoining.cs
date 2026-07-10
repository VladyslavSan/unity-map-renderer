// Engine-free: no UnityEngine dependency.
//
// Clean-room from PUBLIC Unicode data files, not MapLibre source:
//   - Joining_Type values (R/L/D/C/T/U below) — Unicode Character Database "ArabicShaping.txt".
//   - Presentation-form codepoints (U+FE70-FEFF "Arabic Presentation Forms-B", plus the U+FEF5-FEFC
//     lam-alef ligatures) — Unicode Character Database "UnicodeData.txt" (each presentation-form
//     codepoint's <isolated>/<initial>/<medial>/<final> compatibility-decomposition tag maps it back
//     to exactly one base codepoint + form; this table is the forward direction of that mapping).
// Both are permissive (Unicode, Inc. data files under the Unicode License), not MapLibre/Mapbox source.

using System.Collections.Generic;

namespace MapRenderer.Core.Text
{
    /// <summary>
    /// A character's joining behavior (Unicode "Joining_Type" property). Right/Left describe which
    /// side the character connects on; Dual connects both sides; Join_Causing (e.g. tatweel) has no
    /// shape variants of its own but propagates a join through it as if dual-joining; Transparent
    /// (combining marks) is invisible to the algorithm — skipped when scanning for a neighbor.
    /// </summary>
    public enum ArabicJoiningType
    {
        NonJoining,
        Transparent,
        RightJoining,
        LeftJoining,
        DualJoining,
        JoinCausing,
    }

    /// <summary>The 4 classical Arabic contextual glyph shapes.</summary>
    public enum ArabicGlyphForm
    {
        Isolated,
        Initial,
        Medial,
        Final,
    }

    /// <summary>
    /// One shaped output unit: the atlas (presentation-form) codepoint plus the source char-offset
    /// cluster it was produced from (a lam-alef ligature consumes 2 source chars but reports the LAM's
    /// cluster — the first of the pair).
    /// </summary>
    public readonly struct ArabicShapedUnit
    {
        public uint AtlasCodepoint { get; init; }
        public int Cluster { get; init; }
    }

    /// <summary>
    /// The standard Arabic cursive-joining algorithm (Unicode Standard, "Arabic Cursive Joining"; the
    /// same table-driven transform every Arabic-aware shaper implements from the same public data) —
    /// implemented here as a bounded, codepoint-level pass per S18 decision 7 (Option Y): map each
    /// Arabic base letter (U+0600-06FF) to its contextual presentation-form codepoint, entirely from a
    /// hand-rolled joining-type table. No font, no glyph indices — output stays codepoint-keyed so it
    /// can look up the codepoint-keyed glyph-PBF atlas directly (S18 decision 6, Model A).
    /// </summary>
    public static class ArabicJoining
    {
        public const uint Lam = 0x0644;

        // ── Joining_Type table (ArabicShaping.txt) — standard Arabic block U+0621-064A + combining
        //    marks + tatweel + alef wasla. Codepoints outside this table default to NonJoining (a safe
        //    fallback: NonJoining never joins, so an unlisted codepoint just renders isolated/unchanged
        //    rather than corrupting a neighbor's shape).
        private static readonly Dictionary<uint, ArabicJoiningType> JoiningTypes = new Dictionary<uint, ArabicJoiningType>
        {
            [0x0621] = ArabicJoiningType.NonJoining,   // HAMZA (isolated only — no connecting forms)
            [0x0622] = ArabicJoiningType.RightJoining, // ALEF WITH MADDA ABOVE
            [0x0623] = ArabicJoiningType.RightJoining, // ALEF WITH HAMZA ABOVE
            [0x0624] = ArabicJoiningType.RightJoining, // WAW WITH HAMZA ABOVE
            [0x0625] = ArabicJoiningType.RightJoining, // ALEF WITH HAMZA BELOW
            [0x0626] = ArabicJoiningType.DualJoining,  // YEH WITH HAMZA ABOVE
            [0x0627] = ArabicJoiningType.RightJoining, // ALEF
            [0x0628] = ArabicJoiningType.DualJoining,  // BEH
            [0x0629] = ArabicJoiningType.RightJoining, // TEH MARBUTA (only occurs word-finally)
            [0x062A] = ArabicJoiningType.DualJoining,  // TEH
            [0x062B] = ArabicJoiningType.DualJoining,  // THEH
            [0x062C] = ArabicJoiningType.DualJoining,  // JEEM
            [0x062D] = ArabicJoiningType.DualJoining,  // HAH
            [0x062E] = ArabicJoiningType.DualJoining,  // KHAH
            [0x062F] = ArabicJoiningType.RightJoining, // DAL
            [0x0630] = ArabicJoiningType.RightJoining, // THAL
            [0x0631] = ArabicJoiningType.RightJoining, // REH
            [0x0632] = ArabicJoiningType.RightJoining, // ZAIN
            [0x0633] = ArabicJoiningType.DualJoining,  // SEEN
            [0x0634] = ArabicJoiningType.DualJoining,  // SHEEN
            [0x0635] = ArabicJoiningType.DualJoining,  // SAD
            [0x0636] = ArabicJoiningType.DualJoining,  // DAD
            [0x0637] = ArabicJoiningType.DualJoining,  // TAH
            [0x0638] = ArabicJoiningType.DualJoining,  // ZAH
            [0x0639] = ArabicJoiningType.DualJoining,  // AIN
            [0x063A] = ArabicJoiningType.DualJoining,  // GHAIN
            [0x0640] = ArabicJoiningType.JoinCausing,  // TATWEEL (kashida filler — no shape of its own)
            [0x0641] = ArabicJoiningType.DualJoining,  // FEH
            [0x0642] = ArabicJoiningType.DualJoining,  // QAF
            [0x0643] = ArabicJoiningType.DualJoining,  // KAF
            [0x0644] = ArabicJoiningType.DualJoining,  // LAM
            [0x0645] = ArabicJoiningType.DualJoining,  // MEEM
            [0x0646] = ArabicJoiningType.DualJoining,  // NOON
            [0x0647] = ArabicJoiningType.DualJoining,  // HEH
            [0x0648] = ArabicJoiningType.RightJoining, // WAW
            [0x0649] = ArabicJoiningType.RightJoining, // ALEF MAKSURA
            [0x064A] = ArabicJoiningType.DualJoining,  // YEH
            [0x064B] = ArabicJoiningType.Transparent,  // FATHATAN (combining mark)
            [0x064C] = ArabicJoiningType.Transparent,  // DAMMATAN
            [0x064D] = ArabicJoiningType.Transparent,  // KASRATAN
            [0x064E] = ArabicJoiningType.Transparent,  // FATHA
            [0x064F] = ArabicJoiningType.Transparent,  // DAMMA
            [0x0650] = ArabicJoiningType.Transparent,  // KASRA
            [0x0651] = ArabicJoiningType.Transparent,  // SHADDA
            [0x0652] = ArabicJoiningType.Transparent,  // SUKUN
            [0x0670] = ArabicJoiningType.Transparent,  // SUPERSCRIPT ALEF (mark)
            [0x0671] = ArabicJoiningType.RightJoining, // ALEF WASLA
        };

        // ── base codepoint -> (isolated, final, initial, medial) presentation-form codepoints.
        //    Right-joining-only letters (see JoiningTypes above) only ever have isolated/final forms —
        //    their initial/medial slots are 0 (unused; ToPresentationForm falls back to isolated).
        private static readonly Dictionary<uint, (uint isolated, uint final, uint initial, uint medial)> PresentationForms =
            new Dictionary<uint, (uint, uint, uint, uint)>
        {
            [0x0621] = (0xFE80, 0, 0, 0),           // HAMZA
            [0x0622] = (0xFE81, 0xFE82, 0, 0),      // ALEF WITH MADDA ABOVE
            [0x0623] = (0xFE83, 0xFE84, 0, 0),      // ALEF WITH HAMZA ABOVE
            [0x0624] = (0xFE85, 0xFE86, 0, 0),      // WAW WITH HAMZA ABOVE
            [0x0625] = (0xFE87, 0xFE88, 0, 0),      // ALEF WITH HAMZA BELOW
            [0x0626] = (0xFE89, 0xFE8A, 0xFE8B, 0xFE8C), // YEH WITH HAMZA ABOVE
            [0x0627] = (0xFE8D, 0xFE8E, 0, 0),      // ALEF
            [0x0628] = (0xFE8F, 0xFE90, 0xFE91, 0xFE92), // BEH
            [0x0629] = (0xFE93, 0xFE94, 0, 0),      // TEH MARBUTA
            [0x062A] = (0xFE95, 0xFE96, 0xFE97, 0xFE98), // TEH
            [0x062B] = (0xFE99, 0xFE9A, 0xFE9B, 0xFE9C), // THEH
            [0x062C] = (0xFE9D, 0xFE9E, 0xFE9F, 0xFEA0), // JEEM
            [0x062D] = (0xFEA1, 0xFEA2, 0xFEA3, 0xFEA4), // HAH
            [0x062E] = (0xFEA5, 0xFEA6, 0xFEA7, 0xFEA8), // KHAH
            [0x062F] = (0xFEA9, 0xFEAA, 0, 0),      // DAL
            [0x0630] = (0xFEAB, 0xFEAC, 0, 0),      // THAL
            [0x0631] = (0xFEAD, 0xFEAE, 0, 0),      // REH
            [0x0632] = (0xFEAF, 0xFEB0, 0, 0),      // ZAIN
            [0x0633] = (0xFEB1, 0xFEB2, 0xFEB3, 0xFEB4), // SEEN
            [0x0634] = (0xFEB5, 0xFEB6, 0xFEB7, 0xFEB8), // SHEEN
            [0x0635] = (0xFEB9, 0xFEBA, 0xFEBB, 0xFEBC), // SAD
            [0x0636] = (0xFEBD, 0xFEBE, 0xFEBF, 0xFEC0), // DAD
            [0x0637] = (0xFEC1, 0xFEC2, 0xFEC3, 0xFEC4), // TAH
            [0x0638] = (0xFEC5, 0xFEC6, 0xFEC7, 0xFEC8), // ZAH
            [0x0639] = (0xFEC9, 0xFECA, 0xFECB, 0xFECC), // AIN
            [0x063A] = (0xFECD, 0xFECE, 0xFECF, 0xFED0), // GHAIN
            [0x0641] = (0xFED1, 0xFED2, 0xFED3, 0xFED4), // FEH
            [0x0642] = (0xFED5, 0xFED6, 0xFED7, 0xFED8), // QAF
            [0x0643] = (0xFED9, 0xFEDA, 0xFEDB, 0xFEDC), // KAF
            [0x0644] = (0xFEDD, 0xFEDE, 0xFEDF, 0xFEE0), // LAM
            [0x0645] = (0xFEE1, 0xFEE2, 0xFEE3, 0xFEE4), // MEEM
            [0x0646] = (0xFEE5, 0xFEE6, 0xFEE7, 0xFEE8), // NOON
            [0x0647] = (0xFEE9, 0xFEEA, 0xFEEB, 0xFEEC), // HEH
            [0x0648] = (0xFEED, 0xFEEE, 0, 0),      // WAW
            [0x0649] = (0xFEEF, 0xFEF0, 0, 0),      // ALEF MAKSURA
            [0x064A] = (0xFEF1, 0xFEF2, 0xFEF3, 0xFEF4), // YEH
        };

        // ── Mandatory lam-alef ligature: LAM immediately followed by one of the 4 alef-family letters
        //    collapses to a SINGLE glyph (isolated/final only — the ligature behaves like alef itself
        //    with respect to a following character, since alef never joins forward).
        private static readonly Dictionary<uint, (uint isolated, uint final)> LamAlefLigatures =
            new Dictionary<uint, (uint, uint)>
        {
            [0x0622] = (0xFEF5, 0xFEF6), // LAM + ALEF WITH MADDA ABOVE
            [0x0623] = (0xFEF7, 0xFEF8), // LAM + ALEF WITH HAMZA ABOVE
            [0x0625] = (0xFEF9, 0xFEFA), // LAM + ALEF WITH HAMZA BELOW
            [0x0627] = (0xFEFB, 0xFEFC), // LAM + ALEF
        };

        public static ArabicJoiningType GetJoiningType(uint codepoint)
            => JoiningTypes.TryGetValue(codepoint, out ArabicJoiningType type) ? type : ArabicJoiningType.NonJoining;

        /// <summary>
        /// Maps a base codepoint + resolved <see cref="ArabicGlyphForm"/> to its presentation-form
        /// codepoint. Falls back to the isolated form (or the base codepoint itself, if even that is
        /// unmapped) when the requested form doesn't exist for this letter — defensive only; the join
        /// algorithm below never requests a form a right/left-joining letter doesn't have.
        /// </summary>
        public static uint ToPresentationForm(uint baseCodepoint, ArabicGlyphForm form)
        {
            if (!PresentationForms.TryGetValue(baseCodepoint, out var forms))
            {
                return baseCodepoint;
            }

            uint mapped = form switch
            {
                ArabicGlyphForm.Isolated => forms.isolated,
                ArabicGlyphForm.Final => forms.final,
                ArabicGlyphForm.Initial => forms.initial,
                ArabicGlyphForm.Medial => forms.medial,
                _ => 0,
            };
            if (mapped != 0) return mapped;
            return forms.isolated != 0 ? forms.isolated : baseCodepoint;
        }

        /// <summary>
        /// Runs the full Arabic joining pass over a codepoint sequence in LOGICAL (reading) order,
        /// producing presentation-form-mapped <see cref="ArabicShapedUnit"/>s still in logical order
        /// (bidi reversal to visual order is a separate step — see <see cref="BidiReorder"/>).
        /// </summary>
        public static IReadOnlyList<ArabicShapedUnit> Shape(IReadOnlyList<uint> codepoints, IReadOnlyList<int> clusters)
        {
            int n = codepoints.Count;
            var types = new ArabicJoiningType[n];
            for (int i = 0; i < n; i++)
            {
                types[i] = GetJoiningType(codepoints[i]);
            }

            var joinsPrev = new bool[n];
            var joinsNext = new bool[n];
            for (int i = 0; i < n; i++)
            {
                if (types[i] == ArabicJoiningType.Transparent) continue;

                ArabicJoiningType prevType = FindPrevNonTransparent(types, i);
                ArabicJoiningType nextType = FindNextNonTransparent(types, i);
                joinsPrev[i] = CanReceiveFromPrev(types[i]) && CanSendToNext(prevType);
                joinsNext[i] = CanSendToNext(types[i]) && CanReceiveFromPrev(nextType);
            }

            var result = new List<ArabicShapedUnit>(n);
            for (int i = 0; i < n; i++)
            {
                if (types[i] == ArabicJoiningType.Transparent)
                {
                    // Combining marks aren't presentation-form-mapped in S18 Slice 3 scope; pass through.
                    result.Add(new ArabicShapedUnit { AtlasCodepoint = codepoints[i], Cluster = clusters[i] });
                    continue;
                }

                if (codepoints[i] == Lam && i + 1 < n
                    && LamAlefLigatures.TryGetValue(codepoints[i + 1], out var ligature))
                {
                    uint ligatureCodepoint = joinsPrev[i] ? ligature.final : ligature.isolated;
                    result.Add(new ArabicShapedUnit { AtlasCodepoint = ligatureCodepoint, Cluster = clusters[i] });
                    i++; // consume the alef too — the pair collapses into one glyph.
                    continue;
                }

                ArabicGlyphForm form = ResolveForm(joinsPrev[i], joinsNext[i]);
                result.Add(new ArabicShapedUnit
                {
                    AtlasCodepoint = ToPresentationForm(codepoints[i], form),
                    Cluster = clusters[i],
                });
            }
            return result;
        }

        private static ArabicGlyphForm ResolveForm(bool joinsPrev, bool joinsNext)
        {
            if (joinsPrev && joinsNext) return ArabicGlyphForm.Medial;
            if (joinsNext) return ArabicGlyphForm.Initial;
            if (joinsPrev) return ArabicGlyphForm.Final;
            return ArabicGlyphForm.Isolated;
        }

        // A character can RECEIVE a join from its preceding neighbor only if it is itself right-joining,
        // dual-joining, or join-causing (join-causing propagates as if dual for this purpose).
        private static bool CanReceiveFromPrev(ArabicJoiningType type)
            => type == ArabicJoiningType.RightJoining || type == ArabicJoiningType.DualJoining || type == ArabicJoiningType.JoinCausing;

        // A character can SEND a join to its following neighbor only if it is itself left-joining,
        // dual-joining, or join-causing.
        private static bool CanSendToNext(ArabicJoiningType type)
            => type == ArabicJoiningType.LeftJoining || type == ArabicJoiningType.DualJoining || type == ArabicJoiningType.JoinCausing;

        private static ArabicJoiningType FindPrevNonTransparent(ArabicJoiningType[] types, int index)
        {
            for (int j = index - 1; j >= 0; j--)
            {
                if (types[j] != ArabicJoiningType.Transparent) return types[j];
            }
            return ArabicJoiningType.NonJoining; // start of run: no neighbor to join with.
        }

        private static ArabicJoiningType FindNextNonTransparent(ArabicJoiningType[] types, int index)
        {
            for (int j = index + 1; j < types.Length; j++)
            {
                if (types[j] != ArabicJoiningType.Transparent) return types[j];
            }
            return ArabicJoiningType.NonJoining; // end of run: no neighbor to join with.
        }
    }
}
