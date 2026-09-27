namespace MapRenderer.Unity.Interpolation
{
    /// <summary>A value an <see cref="IInterpolation{T}"/> can interpolate: itself mixed toward
    /// <paramref name="to"/> at <paramref name="weight"/> (0 = this value, 1 = <paramref name="to"/>).</summary>
    internal interface IInterpolatable<T> where T : struct
    {
        T Interpolate(in T to, float weight);
    }
}
