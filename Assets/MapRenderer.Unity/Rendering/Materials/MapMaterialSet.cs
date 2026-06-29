using UnityEngine;

namespace MapRenderer.Unity.Rendering.Materials
{
    /// <summary>
    /// The base <see cref="Material"/> per map rendering technique (fill, line, …).
    ///
    /// <para>Per-style-layer materials are <b>clones</b> of these bases (see <see cref="MaterialFactory"/> +
    /// <see cref="MaterialExtensions.CloneWithParent"/>), so a single base asset is the editable source of
    /// default styling for all fills / all lines — and, in the Editor, live-tunable during Play.</para>
    ///
    /// <para>This replaces hardcoded <c>Shader.Find("...")</c> name lookups in code: the base material
    /// references its shader by GUID, so renaming a shader never breaks material creation.</para>
    ///
    /// Create one via <c>Assets ▸ Create ▸ MapRenderer ▸ Material Set</c>, assign the base fill/line
    /// materials, then reference it on the map component (e.g. <c>MapView</c>).
    /// </summary>
    [CreateAssetMenu(fileName = "MapMaterialSet", menuName = "MapRenderer/Material Set", order = 0)]
    public sealed class MapMaterialSet : ScriptableObject
    {
        [Tooltip("Base material for all fill (polygon) layers. Cloned per style layer.")]
        [SerializeField] private Material _fillMaterial;

        [Tooltip("Base material for all line layers. Cloned per style layer.")]
        [SerializeField] private Material _lineMaterial;

        /// <summary>Base material for fill (polygon) layers. May be null if unassigned.</summary>
        public Material FillMaterial => _fillMaterial;

        /// <summary>Base material for line layers. May be null if unassigned.</summary>
        public Material LineMaterial => _lineMaterial;
    }
}
