// Engine-free: no UnityEngine dependency.

namespace MapRenderer.Core.Text
{
    /// <summary>
    /// The resolved paragraph/run direction for a shaped run. Only a single strong direction per run is
    /// distinguished — mixed-direction (full UAX #9) bidi is a
    /// deferred follow-up; see <see cref="CodepointTextShaper"/>.
    /// </summary>
    public enum TextDirection
    {
        LeftToRight,
        RightToLeft,
    }
}
