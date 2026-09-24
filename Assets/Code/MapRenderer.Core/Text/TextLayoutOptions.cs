// Engine-free: no UnityEngine dependency.
// Construction convention: object initializer with named members.

using Unity.Mathematics;

namespace MapRenderer.Core.Text
{
    /// <summary>
    /// Text layout parameters. Every length is in EMS (<c>TextQuadLayout.OneEm</c> = 24 baked px) and carries
    /// no text-size; placement applies the <c>text-size/24</c> screen scale. Build from <see cref="Default"/>:
    /// <c>new TextLayoutOptions()</c> zero-fills <see cref="MaxWidthEm"/>/<see cref="LineHeightEm"/>, and C# 9
    /// cannot override a struct's parameterless constructor. <c>TextQuadLayout</c> falls back to the defaults
    /// for non-positive values of those two, so a zeroed value still wraps and stacks lines.
    /// </summary>
    public readonly struct TextLayoutOptions
    {
        public TextAnchor Anchor { get; init; }

        /// <summary>Constant translation in ems (raw y-up; MapLibre's y-down <c>text-offset</c> convention
        /// is reconciled upstream by <c>TextLayoutOptionsBuilder</c>).</summary>
        public float2 Offset { get; init; }

        /// <summary>Radial translation in ems, resolved to an x/y from the anchor; overrides <see cref="Offset"/> when non-zero.</summary>
        public float RadialOffset { get; init; }

        public TextJustify Justify { get; init; }

        /// <summary>Greedy word-wrap width in ems.</summary>
        public float MaxWidthEm { get; init; }

        /// <summary>Line-to-line baseline spacing in ems.</summary>
        public float LineHeightEm { get; init; }

        /// <summary>Additional pen advance between every consecutive glyph, in ems.</summary>
        public float LetterSpacingEm { get; init; }

        /// <summary>
        /// The MapLibre style-spec defaults: anchor center, zero offset, auto justify, 10em max-width,
        /// 1.2em line-height, zero letter-spacing. Prefer this over <c>new TextLayoutOptions()</c> (see
        /// the class doc's construction note) — override individual members via an object initializer
        /// copy, e.g. <c>new TextLayoutOptions { Anchor = TextAnchor.Left, Offset = Default.Offset, ... }</c>.
        /// </summary>
        public static readonly TextLayoutOptions Default = new TextLayoutOptions
        {
            Anchor = TextAnchor.Center,
            Offset = float2.zero,
            RadialOffset = 0f,
            Justify = TextJustify.Auto,
            MaxWidthEm = 10f,
            LineHeightEm = 1.2f,
            LetterSpacingEm = 0f,
        };
    }
}
