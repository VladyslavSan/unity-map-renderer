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
        [SerializeField] public Material FillMaterial;

        [Tooltip("Base material for all line layers. Cloned per style layer.")]
        [SerializeField] public Material LineMaterial;
        
        [Tooltip("Base material for symbol text render items. Cloned per style layer.")]
        [SerializeField] public Material SymbolText;

        [Tooltip("Base material for symbol icon (sprite) render items. Cloned per style layer. Optional — " +
                 "unassigned means icons will not render (labels still do); NOT enforced by Validate().")]
        [SerializeField] public Material SymbolIcon;

        /// <summary>
        /// Epic A / A2 (DECISION 2): fail LOUD when any base material is unassigned, rather than letting a
        /// null base silently reach the pipeline (a null-material fill/line/background/symbol slot would
        /// otherwise crash a backend's <c>AddTileLayer</c> — see <c>BackendNullSlotTests</c>' doc). A partial
        /// material set is a developer CONFIGURATION error (an unassigned <see cref="ScriptableObject"/>
        /// field), not a runtime/data condition.
        ///
        /// <para>Round-4: the fields above are live-mutable, so call this on a CAPTURED reference at COMMIT
        /// time (immediately before the synchronous <c>SetStyle</c> commit, after the one
        /// <c>await BuildSourceSpecs</c>) — never at construction/entry alone, which a concurrent mutation
        /// during the await could invalidate (TOCTOU). See <c>MapView.SetStyle</c>.</para>
        /// </summary>
        public void Validate()
        {
            if (FillMaterial == null)
                throw new System.InvalidOperationException(
                    "MapMaterialSet.FillMaterial is unassigned — every map base material (FillMaterial, " +
                    "LineMaterial, SymbolText) must be set.");
            if (LineMaterial == null)
                throw new System.InvalidOperationException(
                    "MapMaterialSet.LineMaterial is unassigned — every map base material (FillMaterial, " +
                    "LineMaterial, SymbolText) must be set.");
            if (SymbolText == null)
                throw new System.InvalidOperationException(
                    "MapMaterialSet.SymbolText is unassigned — every map base material (FillMaterial, " +
                    "LineMaterial, SymbolText) must be set.");
        }
    }
}
