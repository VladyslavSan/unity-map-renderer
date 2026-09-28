// Engine-free: no UnityEngine dependency.

using System;

namespace MapRenderer.Unity.Text.Bidi
{
    /// <summary>The paragraph embedding direction: fixed, or taken from the first strong character (P2, P3).</summary>
    internal enum ParagraphDirection
    {
        /// <summary>The first strong character sets the direction (P2, P3); production passes this.</summary>
        Auto,

        /// <summary>Paragraph level 0, whatever the text holds.</summary>
        LeftToRight,

        /// <summary>Paragraph level 1, whatever the text holds.</summary>
        RightToLeft,
    }

    /// <summary>
    /// The Unicode Bidirectional Algorithm (UAX #9) for one text run: P1-P3, X9, W1-W7, N0-N2, I1-I2 and L1.
    /// <c>BidiReorder</c> in Core applies L2. Explicit embeddings, overrides and isolates (X1-X8) are not resolved: the
    /// embedding and override characters are removed as in X9, and the isolate characters act as neutrals.
    /// The methods allocate nothing on the managed heap.
    /// </summary>
    internal static class BidiResolver
    {
        /// <summary>The longest run <see cref="Resolve"/> accepts. It bounds the stack scratch and the loops.</summary>
        internal const int MaxCodepoints = 1024;

        /// <summary>The bracket stack depth of BD16. Deeper nesting cancels all bracket pairing in the paragraph.</summary>
        private const int MaxBracketDepth = 63;

        /// <summary>
        /// Writes the resolved embedding level of every code point to <paramref name="levels"/> and returns the
        /// level of the first paragraph. A B character ends a paragraph (P1), and each paragraph resolves alone.
        /// Levels are after L1, with the whole run as one line. Production passes <see cref="ParagraphDirection.Auto"/>;
        /// a fixed direction is the paragraph-level override the Unicode conformance data drives.
        /// </summary>
        /// <exception cref="ArgumentException">The run needs resolving and is longer than <see cref="MaxCodepoints"/>, or <paramref name="levels"/> is too short.</exception>
        internal static int Resolve(ReadOnlySpan<int> codepoints, Span<byte> levels,
            ParagraphDirection direction = ParagraphDirection.Auto)
        {
            int count = codepoints.Length;
            if (levels.Length < count)
                throw new ArgumentException("The levels span is shorter than the code point span.");

            bool withinCap = count <= MaxCodepoints;
            Span<byte> original = withinCap ? stackalloc byte[count] : default;
            bool needsResolution = direction == ParagraphDirection.RightToLeft;
            for (int i = 0; i < count; i++)
            {
                BidiClass bidiClass = UnicodeBidiData.ClassOf(codepoints[i]);
                if (withinCap) original[i] = (byte)bidiClass;
                needsResolution |= bidiClass == BidiClass.R || bidiClass == BidiClass.AL || bidiClass == BidiClass.AN;
            }

            // The all-LTR fast path comes before the cap: a long label with no R, AL or AN never needs the scratch.
            if (!needsResolution)
            {
                levels.Slice(0, count).Clear();
                return 0;
            }
            if (!withinCap)
                throw new ArgumentException($"A bidi run holds at most {MaxCodepoints} code points, got {count}.");

            Span<byte> work = stackalloc byte[count];
            Span<int> map = stackalloc int[count];
            Span<int> pairOpen = stackalloc int[count / 2 + 1];
            Span<int> pairClose = stackalloc int[count / 2 + 1];
            Span<int> stackCodepoints = stackalloc int[MaxBracketDepth];
            Span<int> stackPositions = stackalloc int[MaxBracketDepth];

            int firstLevel = -1;
            int start = 0;
            for (int i = 0; i < count; i++)
            {
                bool endsParagraph = original[i] == (byte)BidiClass.B || i == count - 1;
                if (!endsParagraph) continue;
                int length = i + 1 - start;
                int level = ResolveParagraph(
                    codepoints.Slice(start, length), original.Slice(start, length), levels.Slice(start, length),
                    direction, work, map, pairOpen, pairClose, stackCodepoints, stackPositions);
                if (firstLevel < 0) firstLevel = level;
                start = i + 1;
            }
            return firstLevel < 0 ? (direction == ParagraphDirection.RightToLeft ? 1 : 0) : firstLevel;
        }

        /// <summary>The characters X9 removes: the embedding and override controls and boundary neutrals.</summary>
        private static bool IsRemoved(byte bidiClass)
            => bidiClass == (byte)BidiClass.BN
               || (bidiClass >= (byte)BidiClass.LRE && bidiClass <= (byte)BidiClass.PDF);

        /// <summary>The neutral and isolate classes that N1 and N2 resolve (NI).</summary>
        private static bool IsNeutral(byte bidiClass)
            => bidiClass == (byte)BidiClass.B || bidiClass == (byte)BidiClass.S || bidiClass == (byte)BidiClass.WS
               || bidiClass == (byte)BidiClass.ON || bidiClass >= (byte)BidiClass.LRI;

        /// <summary>The direction a resolved class counts as for N0-N2: L, or R with EN and AN as R. Otherwise none.</summary>
        private static int StrongDirection(byte bidiClass)
        {
            if (bidiClass == (byte)BidiClass.L) return (int)BidiClass.L;
            bool isRight = bidiClass == (byte)BidiClass.R || bidiClass == (byte)BidiClass.EN
                           || bidiClass == (byte)BidiClass.AN;
            return isRight ? (int)BidiClass.R : -1;
        }

        /// <summary>Maps the two angle brackets with canonical equivalents to one code point (BD16).</summary>
        private static int CanonicalBracket(int codepoint)
            => codepoint == 0x2329 ? 0x3008 : codepoint == 0x232A ? 0x3009 : codepoint;

        /// <summary>Resolves one paragraph (P2-P3 to L1), writes its levels, and returns the paragraph level.</summary>
        private static int ResolveParagraph(
            ReadOnlySpan<int> codepoints, ReadOnlySpan<byte> original, Span<byte> levels,
            ParagraphDirection direction, Span<byte> work, Span<int> map, Span<int> pairOpen, Span<int> pairClose,
            Span<int> stackCodepoints, Span<int> stackPositions)
        {
            int length = codepoints.Length;
            int paragraphLevel = direction == ParagraphDirection.RightToLeft ? 1 : 0;
            if (direction == ParagraphDirection.Auto)
            {
                for (int i = 0; i < length; i++)
                {
                    byte bidiClass = original[i];
                    if (bidiClass == (byte)BidiClass.L) break;
                    if (bidiClass == (byte)BidiClass.R || bidiClass == (byte)BidiClass.AL) { paragraphLevel = 1; break; }
                }
            }
            byte embedding = paragraphLevel == 0 ? (byte)BidiClass.L : (byte)BidiClass.R;

            int count = 0;
            for (int i = 0; i < length; i++)
            {
                if (IsRemoved(original[i])) continue;
                map[count] = i;
                work[count] = original[i];
                count++;
            }

            ResolveWeakTypes(work.Slice(0, count), embedding);
            ResolveBracketPairs(codepoints, original, work.Slice(0, count), map, embedding, pairOpen, pairClose,
                stackCodepoints, stackPositions);
            ResolveNeutralTypes(work.Slice(0, count), embedding);

            for (int i = 0; i < count; i++)
            {
                byte bidiClass = work[i];
                int level = paragraphLevel;
                if ((paragraphLevel & 1) == 0)
                {
                    if (bidiClass == (byte)BidiClass.R) level += 1;
                    else if (bidiClass == (byte)BidiClass.AN || bidiClass == (byte)BidiClass.EN) level += 2;
                }
                else if (bidiClass == (byte)BidiClass.L || bidiClass == (byte)BidiClass.EN || bidiClass == (byte)BidiClass.AN)
                {
                    level += 1;
                }
                levels[map[i]] = (byte)level;
            }

            byte carried = (byte)paragraphLevel;
            for (int i = 0; i < length; i++)
            {
                if (IsRemoved(original[i])) levels[i] = carried;
                else carried = levels[i];
            }

            bool trailing = true;
            for (int i = length - 1; i >= 0; i--)
            {
                byte bidiClass = original[i];
                if (bidiClass == (byte)BidiClass.S || bidiClass == (byte)BidiClass.B)
                {
                    levels[i] = (byte)paragraphLevel;
                    trailing = true;
                }
                else if (bidiClass == (byte)BidiClass.WS || bidiClass >= (byte)BidiClass.LRI || IsRemoved(bidiClass))
                {
                    if (trailing) levels[i] = (byte)paragraphLevel;
                }
                else
                {
                    trailing = false;
                }
            }
            return paragraphLevel;
        }

        /// <summary>Applies W1-W7 in place. <paramref name="startOfSequence"/> is sos, and it doubles as eos.</summary>
        private static void ResolveWeakTypes(Span<byte> types, byte startOfSequence)
        {
            int count = types.Length;
            for (int i = 0; i < count; i++)
            {
                if (types[i] != (byte)BidiClass.NSM) continue;
                byte previous = i == 0 ? startOfSequence : types[i - 1];
                types[i] = previous >= (byte)BidiClass.LRI ? (byte)BidiClass.ON : previous;
            }

            byte lastStrong = startOfSequence;
            for (int i = 0; i < count; i++)
            {
                byte bidiClass = types[i];
                if (bidiClass == (byte)BidiClass.L || bidiClass == (byte)BidiClass.R || bidiClass == (byte)BidiClass.AL)
                    lastStrong = bidiClass;
                else if (bidiClass == (byte)BidiClass.EN && lastStrong == (byte)BidiClass.AL)
                    types[i] = (byte)BidiClass.AN;
            }

            for (int i = 0; i < count; i++)
                if (types[i] == (byte)BidiClass.AL) types[i] = (byte)BidiClass.R;

            for (int i = 1; i < count - 1; i++)
            {
                byte before = types[i - 1];
                bool numberBefore = before == (byte)BidiClass.EN || before == (byte)BidiClass.AN;
                if (!numberBefore || types[i + 1] != before) continue;
                if (types[i] == (byte)BidiClass.CS
                    || (types[i] == (byte)BidiClass.ES && before == (byte)BidiClass.EN))
                    types[i] = before;
            }

            for (int i = 0; i < count; i++)
            {
                if (types[i] != (byte)BidiClass.ET) continue;
                int end = i;
                while (end < count && types[end] == (byte)BidiClass.ET) end++;
                bool nextToNumber = (i > 0 && types[i - 1] == (byte)BidiClass.EN)
                                    || (end < count && types[end] == (byte)BidiClass.EN);
                if (nextToNumber)
                    for (int k = i; k < end; k++) types[k] = (byte)BidiClass.EN;
                i = end - 1;
            }

            for (int i = 0; i < count; i++)
            {
                byte bidiClass = types[i];
                if (bidiClass == (byte)BidiClass.ES || bidiClass == (byte)BidiClass.ET || bidiClass == (byte)BidiClass.CS)
                    types[i] = (byte)BidiClass.ON;
            }

            lastStrong = startOfSequence;
            for (int i = 0; i < count; i++)
            {
                byte bidiClass = types[i];
                if (bidiClass == (byte)BidiClass.L || bidiClass == (byte)BidiClass.R) lastStrong = bidiClass;
                else if (bidiClass == (byte)BidiClass.EN && lastStrong == (byte)BidiClass.L) types[i] = (byte)BidiClass.L;
            }
        }

        /// <summary>Applies N0: finds the bracket pairs of BD16, then sets each pair to a direction by its content.</summary>
        private static void ResolveBracketPairs(
            ReadOnlySpan<int> codepoints, ReadOnlySpan<byte> original, Span<byte> types, ReadOnlySpan<int> map,
            byte embedding, Span<int> pairOpen, Span<int> pairClose, Span<int> stackCodepoints, Span<int> stackPositions)
        {
            int count = types.Length;
            int depth = 0;
            int pairCount = 0;
            for (int i = 0; i < count; i++)
            {
                if (types[i] != (byte)BidiClass.ON) continue;
                int codepoint = codepoints[map[i]];
                (int Paired, bool IsOpening)? bracket = UnicodeBidiData.BracketOf(codepoint);
                if (bracket == null) continue;
                if (bracket.Value.IsOpening)
                {
                    if (depth == MaxBracketDepth) { pairCount = 0; break; }
                    stackCodepoints[depth] = CanonicalBracket(bracket.Value.Paired);
                    stackPositions[depth] = i;
                    depth++;
                    continue;
                }
                int closing = CanonicalBracket(codepoint);
                for (int k = depth - 1; k >= 0; k--)
                {
                    if (stackCodepoints[k] != closing) continue;
                    pairOpen[pairCount] = stackPositions[k];
                    pairClose[pairCount] = i;
                    pairCount++;
                    depth = k;
                    break;
                }
            }

            for (int i = 1; i < pairCount; i++)
            {
                int open = pairOpen[i];
                int close = pairClose[i];
                int k = i - 1;
                while (k >= 0 && pairOpen[k] > open)
                {
                    pairOpen[k + 1] = pairOpen[k];
                    pairClose[k + 1] = pairClose[k];
                    k--;
                }
                pairOpen[k + 1] = open;
                pairClose[k + 1] = close;
            }

            int opposite = embedding == (byte)BidiClass.L ? (int)BidiClass.R : (int)BidiClass.L;
            for (int p = 0; p < pairCount; p++)
            {
                bool foundEmbedding = false;
                bool foundOpposite = false;
                for (int k = pairOpen[p] + 1; k < pairClose[p]; k++)
                {
                    int strong = StrongDirection(types[k]);
                    foundEmbedding |= strong == embedding;
                    foundOpposite |= strong == opposite;
                }

                int chosen;
                if (foundEmbedding)
                {
                    chosen = embedding;
                }
                else if (foundOpposite)
                {
                    int context = embedding;
                    for (int k = pairOpen[p] - 1; k >= 0; k--)
                    {
                        int strong = StrongDirection(types[k]);
                        if (strong < 0) continue;
                        context = strong;
                        break;
                    }
                    chosen = context == opposite ? opposite : embedding;
                }
                else
                {
                    continue;
                }

                SetBracket(types, original, map, pairOpen[p], (byte)chosen);
                SetBracket(types, original, map, pairClose[p], (byte)chosen);
            }
        }

        /// <summary>Sets a bracket and the characters that were NSM before W1 and follow it to <paramref name="direction"/>.</summary>
        private static void SetBracket(Span<byte> types, ReadOnlySpan<byte> original, ReadOnlySpan<int> map, int position, byte direction)
        {
            types[position] = direction;
            for (int k = position + 1; k < types.Length && original[map[k]] == (byte)BidiClass.NSM; k++)
                types[k] = direction;
        }

        /// <summary>Applies N1 and N2 in place: a neutral run takes the direction its two sides share, else the embedding.</summary>
        private static void ResolveNeutralTypes(Span<byte> types, byte embedding)
        {
            int count = types.Length;
            for (int i = 0; i < count; i++)
            {
                if (!IsNeutral(types[i])) continue;
                int end = i;
                while (end < count && IsNeutral(types[end])) end++;
                int before = i == 0 ? embedding : StrongDirection(types[i - 1]);
                int after = end == count ? embedding : StrongDirection(types[end]);
                byte chosen = before == after ? (byte)before : embedding;
                for (int k = i; k < end; k++) types[k] = chosen;
                i = end - 1;
            }
        }
    }
}
