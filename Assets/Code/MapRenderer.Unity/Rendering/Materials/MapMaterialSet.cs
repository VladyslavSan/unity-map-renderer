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
        [Tooltip("Whether this set's base materials are lit or unlit. A map view draws in " +
                 "whichever mode the MapMaterialSet it references declares — reference a Lit set for lit, an " +
                 "Unlit set (Map/FillUnlit / Map/FillExtrusionUnlit / Map/LineUnlit bases) for unlit. Under " +
                 "Unlit the host skips the ambient-probe setup but keeps the directional light, whose " +
                 "direction the unlit fill-extrusion reads. Lit is value 0, so a set that does not set it stays lit.")]
        [SerializeField] public RenderMode RenderMode = RenderMode.Lit;

        [Tooltip("Base material for all fill (polygon) layers. Cloned per style layer.")]
        [SerializeField] public Material FillMaterial;

        [Tooltip("Base material for all line layers. Cloned per style layer.")]
        [SerializeField] public Material LineMaterial;

        [Tooltip("Map/FillExtrusion: base material for the 3D-building roof+wall draw path. " +
                 "Cloned per style layer (MaterialFactory.CreateFillExtrusionMaterial). Optional — unassigned " +
                 "means fill-extrusion layers will not render (a warning is logged); NOT enforced by " +
                 "Validate() (mirrors SymbolIconWorld — enforcing it would red every pre-existing scene/test " +
                 "asset that predates this field).")]
        [SerializeField] public Material FillExtrusionMaterial;

        [Tooltip("Map/Symbol/TextWorld: base material for the world-anchored point-text draw " +
                 "path. Cloned per style layer (SymbolRenderLayer.WorldTextMaterial). REQUIRED — enforced by " +
                 "Validate(), because A1 retired the screen-space point-text path: an unassigned base means " +
                 "points never render.")]
        [SerializeField] public Material SymbolTextWorld;

        [Tooltip("Map/Symbol/IconWorld: base material for the world-anchored icon draw path. " +
                 "Cloned per style layer (SymbolRenderLayer.WorldIconMaterial). Optional — unassigned means " +
                 "world icons will not render (text still does); NOT enforced by Validate().")]
        [SerializeField] public Material SymbolIconWorld;

        /// <summary>
        /// Fails LOUD when any base material is unassigned, rather than letting a null base reach the
        /// pipeline (a null-material fill/line/background/symbol slot crashes a backend's
        /// <c>AddTileLayer</c> — see <c>BackendNullSlotTests</c>' doc). A partial material set is a developer
        /// CONFIGURATION error (an unassigned <see cref="ScriptableObject"/> field), not a runtime/data
        /// condition.
        ///
        /// <para>The fields above are live-mutable, so call this on a CAPTURED reference at COMMIT time
        /// (immediately before the synchronous <c>SetStyle</c> commit, after the one
        /// <c>await BuildSourceSpecs</c>) — never at construction/entry alone, which a concurrent mutation
        /// during the await could invalidate (TOCTOU). See <c>MapView.SetStyle</c>.</para>
        /// </summary>
        public void Validate()
        {
            if (FillMaterial == null)
                throw new System.InvalidOperationException(
                    "MapMaterialSet.FillMaterial is unassigned — every map base material (FillMaterial, " +
                    "LineMaterial, SymbolTextWorld) must be set.");
            if (LineMaterial == null)
                throw new System.InvalidOperationException(
                    "MapMaterialSet.LineMaterial is unassigned — every map base material (FillMaterial, " +
                    "LineMaterial, SymbolTextWorld) must be set.");
            // SymbolTextWorld is REQUIRED — it is the ONLY point-text draw path, so an unassigned base
            // means points never render. SymbolIconWorld stays optional-with-warn and is NOT checked here.
            if (SymbolTextWorld == null)
                throw new System.InvalidOperationException(
                    "MapMaterialSet.SymbolTextWorld is unassigned — every map base material (FillMaterial, " +
                    "LineMaterial, SymbolTextWorld) must be set.");
        }
    }

    /// <summary>
    /// The shading family a <see cref="MapMaterialSet"/>'s base materials belong to — a property of the SET
    /// (the mode is chosen by which set a view references, not a separate view flag). <see cref="Lit"/> is
    /// value 0, so an older serialized set deserializes to the unchanged default.
    /// </summary>
    public enum RenderMode
    {
        /// <summary>Full URP-lit base materials — the default. The host runs its directional-light +
        /// ambient-probe setup.</summary>
        Lit = 0,

        /// <summary>Unlit base materials (Map/FillUnlit, Map/FillExtrusionUnlit, Map/LineUnlit) — no lighting
        /// math; the host skips the directional-light + ambient-probe setup the unlit
        /// twins never read.</summary>
        Unlit = 1,
    }
}
