// Engine-free: no UnityEngine dependency.
// Construction convention: object initializer with named members.

using Unity.Mathematics;

namespace MapRenderer.Core.Text
{
    /// <summary>
    /// S19 layout parameters. Every length-valued property is in EMS (<c>TextQuadLayout.OneEm</c> = 24
    /// baked px) — deliberately carries NO text-size: layout is size-independent, S20 applies the
    /// zoom-dependent <c>text-size/24</c> screen scale.
    ///
    /// <para>
    /// <b>Construction — read before writing <c>new TextLayoutOptions()</c>:</b> a struct's
    /// compiler-synthesized parameterless constructor zero-fills every field. For
    /// <see cref="MaxWidthEm"/>/<see cref="LineHeightEm"/> that is a BROKEN layout (a 0-px wrap width;
    /// 0-px line stacking that collapses every line onto the same baseline), not merely an unstyled
    /// one — so a bare <c>new TextLayoutOptions()</c> or <c>default(TextLayoutOptions)</c> is a
    /// footgun. This project's C# LangVersion is 9 (see <c>MapRenderer.Core.csproj</c>), which cannot
    /// override a struct's parameterless constructor (that needs C# 10) the way the S19 plan's
    /// original sketch assumed, so the mitigation is two-fold: prefer <see cref="Default"/> for
    /// construction, and <c>TextQuadLayout</c> itself falls back to the documented defaults for a
    /// non-positive <see cref="MaxWidthEm"/>/<see cref="LineHeightEm"/> so a zero-valued options value
    /// degrades gracefully instead of breaking wrap/line-stacking.
    /// </para>
    /// </summary>
    public readonly struct TextLayoutOptions
    {
        public TextAnchor Anchor { get; init; }

        /// <summary>Constant translation in ems (raw y-up; MapLibre's y-down <c>text-offset</c> convention is reconciled at S20).</summary>
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
