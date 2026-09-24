using UnityEngine;

namespace MapRenderer.Unity.Rendering.Materials
{
    /// <summary>
    /// The base <see cref="Material"/> per map rendering technique (fill, line, …). Per-style-layer materials
    /// are clones of these bases (<see cref="MaterialFactory"/>), so one base asset is the editable,
    /// Play-mode-tunable source of default styling. A base references its shader by GUID, so a shader rename
    /// never breaks material creation. Create one via <c>Assets ▸ Create ▸ MapRenderer ▸ Material Set</c>
    /// and reference it on the map component.
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
                 "asset that leaves this field empty).")]
        [SerializeField] public Material FillExtrusionMaterial;

        [Tooltip("Map/Symbol/TextWorld: base material for the world-anchored point-text draw " +
                 "path. Cloned per style layer (SymbolRenderLayer.WorldTextMaterial). REQUIRED — enforced by " +
                 "Validate(), because this is the only point-text draw path: an unassigned base means " +
                 "points never render.")]
        [SerializeField] public Material SymbolTextWorld;

        [Tooltip("Map/Symbol/IconWorld: base material for the world-anchored icon draw path. " +
                 "Cloned per style layer (SymbolRenderLayer.WorldIconMaterial). Optional — unassigned means " +
                 "world icons will not render (text still does); NOT enforced by Validate().")]
        [SerializeField] public Material SymbolIconWorld;

        /// <summary>
        /// Throws when a required base material is unassigned, a developer configuration error; a null slot
        /// would crash a backend's <c>AddTileLayer</c>. Non-local invariant: the fields are live-mutable, so
        /// <c>MapView.SetStyle</c> calls this on a captured reference at commit time, after its one
        /// <c>await</c>; a check at entry alone would miss a mutation during the await (TOCTOU).
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
