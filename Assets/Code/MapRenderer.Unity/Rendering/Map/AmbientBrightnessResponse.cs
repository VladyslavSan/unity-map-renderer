using Unity.Mathematics;
using UnityEngine;

namespace MapRenderer.Unity.Rendering.Map
{
    /// <summary>
    /// An approximation of a diffuse lit surface at N·L = 1: <see cref="Factor"/> is 1 at
    /// <see cref="SunLight.DefaultIntensity"/> and <see cref="AmbientFraction"/> at zero light.
    /// </summary>
    internal static class AmbientBrightnessResponse
    {
        /// <summary>Darkest channel value of the fallback ambient probe. A flat 0.4 renders correctly on
        /// web (<c>MapHost.ResolveAmbientProbe</c>).</summary>
        public const float MinFallbackAmbient = 0.4f;

        /// <summary><see cref="MinFallbackAmbient"/> as a fraction of the total brightness at
        /// <see cref="SunLight.DefaultIntensity"/> (ambient plus the default direct term) — <see cref="Factor"/>'s
        /// value at zero light.</summary>
        private const float AmbientFraction = MinFallbackAmbient / (MinFallbackAmbient + SunLight.DefaultIntensity);

        /// <summary>Linear brightness multiplier for <paramref name="unityLightIntensity"/> (a Unity
        /// <c>Light.intensity</c> value, e.g. <see cref="SunLight.Intensity"/>).</summary>
        public static float Factor(float unityLightIntensity)
            => AmbientFraction + (1f - AmbientFraction) * unityLightIntensity;

        /// <summary><paramref name="baseColor"/> scaled by <see cref="Factor"/> of
        /// <paramref name="unityLightIntensity"/> in LINEAR space (matching how a lit surface is shaded),
        /// clamped to [0,1] per channel, alpha untouched. Exactly <paramref name="baseColor"/> at the
        /// default intensity.</summary>
        public static Color Scale(Color baseColor, float unityLightIntensity)
        {
            float factor = Factor(unityLightIntensity);
            if (factor == 1f) return baseColor;

            Color linear = baseColor.linear;
            Color scaledLinear = new Color(math.saturate(linear.r * factor), math.saturate(linear.g * factor),
                math.saturate(linear.b * factor), linear.a);
            Color gamma = scaledLinear.gamma;
            return new Color(gamma.r, gamma.g, gamma.b, baseColor.a);
        }
    }
}
