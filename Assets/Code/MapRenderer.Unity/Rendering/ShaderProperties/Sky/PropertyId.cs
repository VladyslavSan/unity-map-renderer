using UnityEngine;

namespace MapRenderer.Unity.Rendering.ShaderProperties.Sky
{
    /// <summary>
    /// Cached <c>Shader.PropertyToID</c> integer ids for every property in <see cref="PropertyNames"/>.
    /// Use these for all <c>Material.Set*/Get*</c> calls that target <c>Map/Sky</c>.
    /// </summary>
    public static class PropertyId
    {
        public static readonly int SkyColor         = Shader.PropertyToID(PropertyNames.SkyColor);
        public static readonly int HorizonColor     = Shader.PropertyToID(PropertyNames.HorizonColor);
        public static readonly int SkyHorizonBlend  = Shader.PropertyToID(PropertyNames.SkyHorizonBlend);
        public static readonly int MapEdgeElevation = Shader.PropertyToID(PropertyNames.MapEdgeElevation);
        public static readonly int SkyTopElevation  = Shader.PropertyToID(PropertyNames.SkyTopElevation);
    }
}
