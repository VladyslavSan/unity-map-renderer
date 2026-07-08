// Engine-free: no UnityEngine dependency.

namespace MapRenderer.Core.Text
{
    /// <summary>
    /// The resolved paragraph/run direction for a shaped run. S18 Slice 3 (Option Y, decision 8) only
    /// distinguishes a single strong direction per run — mixed-direction (full UAX #9) bidi is a
    /// deferred follow-up; see <see cref="CodepointTextShaper"/>.
    /// </summary>
    public enum TextDirection
    {
        LeftToRight,
        RightToLeft,
    }
}
