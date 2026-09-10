// Unity EditMode only — off-screen GPU render (SnapshotRenderer). NOT registered in core-tests.csproj.
//
// A LADDER, not a single assertion. Building shadows render into the shadow map (confirmed in the Frame
// Debugger) but nothing on screen darkens. Each rung below moves ONE step closer to the map's real
// configuration, so the first rung that fails names the cause:
//
//   Rung 1  stock URP Lit ground + stock URP Lit cube ........ harness/pipeline control
//   Rung 2  Map/Fill ground (forced opaque) + URP Lit cube .... does OUR shader code receive at all
//   Rung 3  Map/Fill ground (painter contract, transparent
//           queue, ZWrite off) + URP Lit cube ................ does the render state kill receiving
//   Rung 4  as rung 3, but the caster is the real
//           Map/FillExtrusion material ...................... does OUR caster feed the shadow map
//   Rung 5  rungs 4's meshes+materials drawn through
//           Entities Graphics instead of MeshRenderers ....... the demo's actual backend
//
// Only the RECEIVER changes between rungs 1→3 and only the CASTER between 3→4, so a rung that reddens
// while its predecessor is green isolates exactly one variable.
//
// The oracle, per rung. The ground is a flat plane under a directional light: N·L is identical at every
// point on it, so two ground regions can differ in luminance for exactly one reason — one of them is
// shadowed. Each rung therefore samples two equal, mirror-symmetric ground boxes (one inside the cube's
// projected shadow, one the same distance on the opposite side) and renders the scene TWICE — once with
// the light's shadows enabled, once disabled.
//
// That gives four numbers and makes the claim non-vacuous three ways over:
//   • the shadows-OFF frame must read the two boxes as EQUAL — proving the boxes really are
//     interchangeable, so any difference in the ON frame is shadowing and not a spatial gradient, a
//     mis-placed sample box, or one box straddling the cube;
//   • the ON frame must be non-blank, non-uniform and show real coverage — a "dark region exists"
//     assertion passes trivially on an empty frame;
//   • exactly ONE box must darken when shadows are switched on. Both darkening would mean the whole
//     ground dimmed (not a shadow); neither darkening is the bug under diagnosis.
// Because the verdict is "which box changed between two renders", it does not depend on the pixel
// buffer's vertical origin — a flipped readback would swap the two boxes without inverting the answer.

using System;
using System.Collections.Generic;
using NUnit.Framework;
using Unity.Entities;
using Unity.Mathematics;
using Unity.Rendering;
using Unity.Transforms;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;
using MapRenderer.Core.Geo;
using MapRenderer.Core.Json;
using MapRenderer.Core.Rendering;
using MapRenderer.Core.Tiles;
using MapRenderer.Unity.Rendering.Materials;
// Aliased, not imported: MapRenderer.Core.Expressions carries its own Color, which would make every
// UnityEngine.Color in this fixture ambiguous.
using Value = MapRenderer.Core.Expressions.Value;
using FillExtrusion = MapRenderer.Core.Style.FillExtrusion;
using ShaderProperties = MapRenderer.Unity.Rendering.ShaderProperties;

namespace MapRenderer.Tests.Visual
{
    /// <summary>
    /// Localises "buildings cast into the shadow map but nothing on screen darkens" by rendering the same
    /// shadow scene through five configurations, each one step closer to the map's real one — see the file
    /// header for the ladder, the oracle and why each rung is non-vacuous.
    ///
    /// <para>Each rung is its OWN <c>[Test]</c> rather than a stage of one method: the results XML then
    /// names the first failing rung directly, which is the whole point of a bisect. Every rung also logs
    /// its material state, the pipeline's shadow keyword state and the four measured luminances, so a red
    /// rung reports <i>what</i> was wrong and not merely <i>that</i> something is.</para>
    /// </summary>
    [TestFixture]
    public class ShadowReceiveBisectTests
    {
        private const int SnapW = 256;
        private const int SnapH = 256;

        /// <summary>Half-height of the desk-scale orthographic frustum, world units.</summary>
        private const float OrthoSize = 30f;

        /// <summary>Camera altitude — well above the cube's roof at y=20.</summary>
        private const float CameraHeight = 120f;

        /// <summary>Uniform scale on Unity's Plane primitive (10×10 units at scale 1) — a 50×50 ground,
        /// deliberately smaller than the 60×60 frustum so the frame carries visible background and the
        /// coverage precondition has a real signal to read.</summary>
        private const float GroundScale = 5f;

        /// <summary>Edge length of the occluder cube.</summary>
        private const float CubeSize = 10f;

        /// <summary>Cube centre height — the cube spans y ∈ [10, 20].</summary>
        private const float CubeCentreY = 15f;

        /// <summary>Directional light pitch. At 45° the light direction is (0, -√½, +√½), so a point at
        /// height y lands on the ground plane at z + y: the cube's floor (y=10, z ∈ [-5,5]) projects to
        /// z ∈ [5,15] and its roof (y=20) to z ∈ [15,25]. The shadow therefore occupies x ∈ [-5,5],
        /// z ∈ [5,25], which is what <see cref="SceneLayout.DeskScale"/>'s sample box sits inside.
        /// </summary>
        private const float LightPitchDegrees = 45f;

        private const float LightIntensity = 1.0f;

        /// <summary>Camera clear colour — the dark slate the other snapshot fixtures use, so the coverage
        /// analysis has a background that is not black.</summary>
        private static readonly Color BgColor = new Color(0.10f, 0.11f, 0.15f, 1f);

        private const byte BgR8 = 26;
        private const byte BgG8 = 28;
        private const byte BgB8 = 38;

        /// <summary>Flat ambient during the measurement. Low so the shadow has contrast to show, non-zero
        /// so a fully-shadowed region is still distinguishable from the background.</summary>
        private static readonly Color AmbientColor = new Color(0.08f, 0.08f, 0.10f, 1f);

        /// <summary>Ground albedo — mid-grey, plenty of headroom above and below.</summary>
        private static readonly Color GroundColor = new Color(0.62f, 0.62f, 0.62f, 1f);

        /// <summary>Cube albedo. Distinct from <see cref="GroundColor"/> so a sample box that wrongly
        /// straddles the cube shows up as region colour variance.</summary>
        private static readonly Color CubeColor = new Color(0.85f, 0.45f, 0.25f, 1f);

        /// <summary>
        /// The basemap's REAL ground albedo: <c>liberty.json</c>'s background layer paints
        /// <c>#f8f4f0</c>, converted to linear ≈ (0.939, 0.905, 0.871) — Rec.709 luminance 0.910, i.e.
        /// near-white. <see cref="GroundColor"/>'s 0.62 was chosen for "plenty of headroom above and
        /// below", which is precisely the headroom the app does not have, so every rung above it measures
        /// a scene brighter than the one under diagnosis cannot be.
        ///
        /// <para><c>Color.linear</c> rather than a baked triple, because that is the exact conversion
        /// production performs: <c>StyledFillTileBuilder</c> and <c>StyledFillExtrusionTileBuilder</c> bake
        /// <c>featureColor.linear</c> into the mesh COLOUR stream over a white <c>_BaseColor</c>, and this
        /// fixture's meshes carry white vertex colours over a coloured <c>_BaseColor</c> — the shader
        /// multiplies both, so a linear value here reproduces the app's albedo exactly.</para>
        /// </summary>
        private static readonly Color AppGroundColor =
            new Color(248f / 255f, 244f / 255f, 240f / 255f, 1f).linear;

        /// <summary>The buildings' REAL albedo: <c>liberty.json</c>'s <c>building-3d</c> layer paints
        /// <c>hsl(35,8%,85%)</c> = sRGB (0.862, 0.852, 0.838), linear ≈ (0.714, 0.696, 0.670) — also
        /// near-white, not <see cref="CubeColor"/>'s saturated orange.</summary>
        private static readonly Color AppBuildingColor =
            new Color(0.86200f, 0.85200f, 0.83800f, 1f).linear;

        /// <summary>The buildings' <c>fill-extrusion-opacity</c>. Carried for fidelity; a shadow caster's
        /// depth-only pass does not read it, so it changes the RECEIVER's picture, never the caster's
        /// contribution to the shadow map.</summary>
        private const float AppBuildingOpacity = 0.8f;

        /// <summary>The shadow distance the maintainer currently has installed in <c>RPAsset.asset</c>
        /// (up from the shipped 1200). The app-palette rung runs at it so its failure cannot be blamed on
        /// a distance the maintainer has already ruled out by raising.</summary>
        private const float InstalledShadowDistance = 6000f;

        /// <summary>Fraction of a sample box that must sit on the 8-bit ceiling before the region counts as
        /// CLIPPED. In an LDR readback, clipping IS 255 — a shadow subtracted from a value that was already
        /// above 1.0 lands back at 1.0 and is invisible, which is the mechanism the app-palette rung
        /// exists to confirm or kill.</summary>
        private const double ClippedFractionThreshold = 0.5;

        /// <summary>Fraction by which the shadowed box must darken once shadows are switched on. Well
        /// above readback noise (which the shadows-OFF symmetry control measures at &lt;1%) and well below
        /// the ~60% a fully-occluded region loses at these light/ambient levels.</summary>
        private const double MinShadowDarkening = 0.10;

        /// <summary>Fraction by which the UNSHADOWED box may drift once shadows are switched on. Anything
        /// beyond this means the whole ground dimmed rather than a shadow landing on one box.</summary>
        private const double MaxUnshadowedDrift = 0.03;

        /// <summary>Fractional difference tolerated between the two boxes in the shadows-OFF frame. They
        /// are mirror images under a uniform light on a flat plane, so this is a readback-noise budget.
        /// </summary>
        private const double MaxSymmetryError = 0.02;

        /// <summary>Colour variance tolerated inside a sample box in the shadows-OFF frame. A box that
        /// clipped the cube or ran off the ground's edge is not flat and fails here rather than producing
        /// a meaningless luminance.</summary>
        private const double MaxBoxVariance = 0.005;

        /// <summary>Minimum non-background coverage of the shadows-ON frame — the "something actually
        /// rendered" precondition.</summary>
        private const float MinFilledFraction = 0.20f;

        private const string MapFillMaterialPath =
            "Assets/Code/MapRenderer.Unity/Materials/Map/Fill/Lit/Fill.mat";

        private const string MapFillExtrusionMaterialPath =
            "Assets/Code/MapRenderer.Unity/Materials/Map/FillExtrusion/Lit/FillExtrusion.mat";

        // ── Rungs ────────────────────────────────────────────────────────────────────────────────

        /// <summary>
        /// Rung 1 — the control. A stock <c>Universal Render Pipeline/Lit</c> ground and cube. This rung
        /// contains no map code at all, so a failure here is a harness or pipeline-configuration problem
        /// (a quality level whose URP asset has main-light shadows off, a shadow distance shorter than the
        /// scene, a batch session without a shadow-capable GPU context) and every later rung is
        /// uninterpretable until it is green.
        /// </summary>
        [Test]
        public void StockUrpLit_GroundDarkensUnderACastShadow()
        {
            Material ground = null, cube = null;
            try
            {
                ground = BuildStockLitMaterial("BisectStockGround", GroundColor);
                cube   = BuildStockLitMaterial("BisectStockCube",   CubeColor);
                AssertRungReceivesShadows(
                    "rung1-stock-urp-lit",
                    Measure("rung1-stock-urp-lit", SceneLayout.DeskScale(), ground, cube, viaEntities: false),
                    "This rung uses no map code — a failure is the harness or the active render pipeline " +
                    "asset (main-light shadows disabled, shadow distance shorter than this 120 m scene, or " +
                    "a batch session whose GPU context cannot render a shadow map), NOT the map shaders. " +
                    "Fix this before reading any later rung.");
            }
            finally
            {
                DestroyMaterials(ground, cube);
            }
        }

        /// <summary>
        /// Rung 2 — the map's fill shader as the RECEIVER, in the most vanilla render state it can hold:
        /// opaque blend, depth write on, Geometry queue, no transparent surface keyword. The caster is
        /// still stock URP Lit, so the only change from rung 1 is which HLSL shades the ground. A failure
        /// here and a green rung 1 means <c>Map/Fill</c>'s own forward pass never applies shadow
        /// attenuation, independently of the queue and blend state the map puts it in.
        /// </summary>
        [Test]
        public void MapFillReceiver_OpaqueState_GroundDarkensUnderACastShadow()
        {
            Material ground = null, cube = null;
            try
            {
                ground = BuildMapFillMaterial("BisectMapFillOpaque");
                ForceOpaqueState(ground);
                cube = BuildStockLitMaterial("BisectStockCube", CubeColor);
                AssertRungReceivesShadows(
                    "rung2-map-fill-opaque",
                    Measure("rung2-map-fill-opaque", SceneLayout.DeskScale(), ground, cube, viaEntities: false),
                    "Rung 1 (stock URP Lit) is the only difference: the receiver is now Map/Fill in an " +
                    "opaque, Geometry-queue, depth-writing state. A red rung here with rung 1 green puts " +
                    "the defect inside Map/Fill's forward pass — shadow sampling, the shadowCoord " +
                    "interpolator, or a keyword the pass does not declare — and NOT in the render state " +
                    "or the queue, neither of which this rung uses.");
            }
            finally
            {
                DestroyMaterials(ground, cube);
            }
        }

        /// <summary>
        /// Rung 3 — the same <c>Map/Fill</c> receiver, now in the render state the map actually gives it:
        /// <see cref="FillTweaker.ApplyPainterContract"/> (alpha blend, depth write off, the
        /// <c>_SURFACE_TYPE_TRANSPARENT</c> keyword) at a transparent-band queue from
        /// <see cref="LayerDrawOrder.QueueFor"/>. The caster is still stock URP Lit. A failure here with a
        /// green rung 2 means the receiving is killed by the queue/blend/keyword state, not by the shader
        /// code.
        /// </summary>
        [Test]
        public void MapFillReceiver_PainterContractInTransparentQueue_GroundDarkensUnderACastShadow()
        {
            Material ground = null, cube = null;
            try
            {
                ground = BuildMapFillMaterial("BisectMapFillPainter");
                FillTweaker.ApplyPainterContract(ground);
                ground.SetColor(ShaderProperties.PropertyNames.BaseColor, GroundColor);
                ground.renderQueue = LayerDrawOrder.QueueFor(0);
                cube = BuildStockLitMaterial("BisectStockCube", CubeColor);
                AssertRungReceivesShadows(
                    "rung3-map-fill-painter",
                    Measure("rung3-map-fill-painter", SceneLayout.DeskScale(), ground, cube, viaEntities: false),
                    "Rung 2 is the only difference: the SAME Map/Fill receiver now carries the painter " +
                    $"contract (alpha blend, ZWrite off, _SURFACE_TYPE_TRANSPARENT) at queue " +
                    $"{LayerDrawOrder.QueueFor(0)}. A red rung here with rung 2 green means the map's " +
                    "transparent-band render state is what stops the shadow being applied — the shader " +
                    "code is fine.");
            }
            finally
            {
                DestroyMaterials(ground, cube);
            }
        }

        /// <summary>
        /// Rung 4 — the caster becomes the real <c>Map/FillExtrusion</c> material under its elevated
        /// contract, with the receiver unchanged from rung 3. A failure here with a green rung 3 means the
        /// map's building material does not feed the shadow map (a missing or mis-transformed ShadowCaster
        /// pass), which is the one half of the pipeline the Frame Debugger observation already suggests is
        /// working.
        /// </summary>
        [Test]
        public void MapFillExtrusionCaster_OnMapFillReceiver_GroundDarkensUnderACastShadow()
        {
            Material ground = null, cube = null;
            try
            {
                ground = BuildMapFillMaterial("BisectMapFillPainter");
                FillTweaker.ApplyPainterContract(ground);
                ground.SetColor(ShaderProperties.PropertyNames.BaseColor, GroundColor);
                ground.renderQueue = LayerDrawOrder.QueueFor(0);

                cube = BuildMapFillExtrusionMaterial("BisectMapFillExtrusion");
                FillExtrusionTweaker.ApplyElevatedContract(cube);
                cube.SetColor(ShaderProperties.PropertyNames.BaseColor, CubeColor);
                cube.renderQueue = LayerDrawOrder.QueueFor(1);

                AssertRungReceivesShadows(
                    "rung4-map-extrusion-caster",
                    Measure("rung4-map-extrusion-caster", SceneLayout.DeskScale(), ground, cube, viaEntities: false),
                    "Rung 3 is the only difference: the CASTER is now the real Map/FillExtrusion material " +
                    "under its elevated contract. A red rung here with rung 3 green puts the defect in " +
                    "that material's ShadowCaster pass — it did not write the occluder into the shadow " +
                    "map, or wrote it somewhere other than where its forward pass draws it.");
            }
            finally
            {
                DestroyMaterials(ground, cube);
            }
        }

        /// <summary>
        /// Rung 5 — rung 4's meshes and materials drawn through Entities Graphics instead of
        /// <see cref="MeshRenderer"/>s: the backend the demo actually runs
        /// (<c>RenderBackend.Entities</c>), whose draws reach the GPU as DOTS-instanced BRG batches. This
        /// is the rung that answers the keyword hypothesis — if the DOTS-instancing variants of
        /// <c>Map/Fill</c> lack shadow sampling, every earlier rung is green and this one is red.
        ///
        /// <para>Reachable headlessly only because the presentation system group is ticked by hand before
        /// each render; <c>EntitiesGraphicsSpikeTests</c> established that recipe.</para>
        /// </summary>
        [Test]
        public void EntitiesGraphicsBackend_GroundDarkensUnderACastShadow()
        {
            Material ground = null, cube = null;
            try
            {
                ground = BuildMapFillMaterial("BisectEntitiesGround");
                FillTweaker.ApplyPainterContract(ground);
                ground.SetColor(ShaderProperties.PropertyNames.BaseColor, GroundColor);
                ground.renderQueue = LayerDrawOrder.QueueFor(0);

                cube = BuildMapFillExtrusionMaterial("BisectEntitiesCube");
                FillExtrusionTweaker.ApplyElevatedContract(cube);
                cube.SetColor(ShaderProperties.PropertyNames.BaseColor, CubeColor);
                cube.renderQueue = LayerDrawOrder.QueueFor(1);

                AssertRungReceivesShadows(
                    "rung5-entities-graphics",
                    Measure("rung5-entities-graphics", SceneLayout.DeskScale(), ground, cube, viaEntities: true),
                    "Rung 4 is the only difference: the same meshes and materials are submitted by " +
                    "Entities Graphics (DOTS-instanced BRG draws) instead of MeshRenderers — the backend " +
                    "the demo runs. A red rung here with rung 4 green means the DOTS-instancing variants " +
                    "are the problem: the draws reach the shadow map but the forward variant used for " +
                    "them carries no shadow sampling.");
            }
            finally
            {
                DestroyMaterials(ground, cube);
            }
        }

        // ── Scene layout ─────────────────────────────────────────────────────────────────────────

        /// <summary>
        /// Everything that differs between a desk-scale rung and a map-scale one: the camera rig, the size
        /// and placement of the ground and the occluder, the sun, the ambient environment, and the two
        /// ground regions to sample. Rungs 1-5 all share <see cref="DeskScale"/>; the altitude sweep varies
        /// only <see cref="MapScale"/>'s single parameter, so distance from the camera is the one thing
        /// under test there.
        /// </summary>
        private readonly struct SceneLayout
        {
            /// <summary>Camera position, world space.</summary>
            public readonly Vector3 CameraPosition;

            /// <summary>Camera rotation, world space.</summary>
            public readonly Quaternion CameraRotation;

            /// <summary>True for an orthographic camera, false for a perspective one.</summary>
            public readonly bool Orthographic;

            /// <summary>Orthographic half-height when <see cref="Orthographic"/>, else vertical FOV in degrees.</summary>
            public readonly float OrthoSizeOrFieldOfView;

            /// <summary>Far clip plane. URP clamps its shadow distance to this, so a map-scale rung needs it
            /// well beyond the pipeline asset's shadow distance or the rung would measure the clamp instead.</summary>
            public readonly float FarClipPlane;

            /// <summary>Uniform XZ scale on Unity's Plane primitive; the ground is 10× this on a side.</summary>
            public readonly float GroundPlaneScale;

            /// <summary>Occluder centre, world space.</summary>
            public readonly Vector3 CubePosition;

            /// <summary>Occluder scale, world space.</summary>
            public readonly Vector3 CubeScale;

            /// <summary>Directional light rotation.</summary>
            public readonly Quaternion LightRotation;

            /// <summary>Ambient environment mode.</summary>
            public readonly AmbientMode Ambient;

            /// <summary>Flat ambient colour; ignored unless <see cref="Ambient"/> is
            /// <see cref="AmbientMode.Flat"/>.</summary>
            public readonly Color AmbientLight;

            /// <summary>Centre of the ground region that should fall inside the occluder's shadow.</summary>
            public readonly Vector3 ShadowBoxCentre;

            /// <summary>Centre of the mirrored ground region that should stay lit.</summary>
            public readonly Vector3 LitBoxCentre;

            /// <summary>Half-extents of both sample boxes on the ground plane (Y is unused).</summary>
            public readonly Vector3 BoxHalfExtents;

            /// <param name="cameraPosition">Camera position.</param>
            /// <param name="cameraRotation">Camera rotation.</param>
            /// <param name="orthographic">Orthographic or perspective.</param>
            /// <param name="orthoSizeOrFieldOfView">Ortho half-height, or vertical FOV in degrees.</param>
            /// <param name="farClipPlane">Far clip plane.</param>
            /// <param name="groundPlaneScale">Plane primitive scale.</param>
            /// <param name="cubePosition">Occluder centre.</param>
            /// <param name="cubeScale">Occluder scale.</param>
            /// <param name="lightRotation">Directional light rotation.</param>
            /// <param name="ambient">Ambient mode.</param>
            /// <param name="ambientLight">Flat ambient colour.</param>
            /// <param name="shadowBoxCentre">Centre of the shadowed sample region.</param>
            /// <param name="litBoxCentre">Centre of the lit sample region.</param>
            /// <param name="boxHalfExtents">Half-extents of both sample regions.</param>
            public SceneLayout(
                Vector3 cameraPosition, Quaternion cameraRotation, bool orthographic,
                float orthoSizeOrFieldOfView, float farClipPlane, float groundPlaneScale,
                Vector3 cubePosition, Vector3 cubeScale, Quaternion lightRotation,
                AmbientMode ambient, Color ambientLight,
                Vector3 shadowBoxCentre, Vector3 litBoxCentre, Vector3 boxHalfExtents)
            {
                CameraPosition         = cameraPosition;
                CameraRotation         = cameraRotation;
                Orthographic           = orthographic;
                OrthoSizeOrFieldOfView = orthoSizeOrFieldOfView;
                FarClipPlane           = farClipPlane;
                GroundPlaneScale       = groundPlaneScale;
                CubePosition           = cubePosition;
                CubeScale              = cubeScale;
                LightRotation          = lightRotation;
                Ambient                = ambient;
                AmbientLight           = ambientLight;
                ShadowBoxCentre        = shadowBoxCentre;
                LitBoxCentre           = litBoxCentre;
                BoxHalfExtents         = boxHalfExtents;
            }

            /// <summary>The desk-scale rig rungs 1-5 share: a top-down orthographic camera 120 m up over a
            /// 50 m ground, a 10 m cube floating at y=15 and a 45° sun, which puts the cube's shadow on
            /// z ∈ [5, 25] and its mirror on the opposite side.</summary>
            /// <returns>The desk-scale layout.</returns>
            public static SceneLayout DeskScale() => new SceneLayout(
                cameraPosition:         new Vector3(0f, CameraHeight, 0f),
                cameraRotation:         Quaternion.Euler(90f, 0f, 0f),
                orthographic:           true,
                orthoSizeOrFieldOfView: OrthoSize,
                farClipPlane:           200f,
                groundPlaneScale:       GroundScale,
                cubePosition:           new Vector3(0f, CubeCentreY, 0f),
                cubeScale:              new Vector3(CubeSize, CubeSize, CubeSize),
                lightRotation:          Quaternion.Euler(LightPitchDegrees, 0f, 0f),
                ambient:                AmbientMode.Flat,
                ambientLight:           AmbientColor,
                shadowBoxCentre:        new Vector3(0f, 0f,  16f),
                litBoxCentre:           new Vector3(0f, 0f, -16f),
                boxHalfExtents:         new Vector3(3f, 0f, 4f));

            /// <summary>
            /// The app's own rig at a chosen camera altitude: the tilted perspective camera, sun and ground
            /// plane the maintainer captured in the Frame Debugger — camera at (0, A, -0.518A) looking
            /// along (0, -0.888, 0.4598) with a 60° vertical FOV, sun at Euler(50, 30, 0) (which reproduces
            /// the captured <c>_MainLightPosition</c> of (-0.321, 0.766, -0.557) exactly).
            ///
            /// <para>Every length scales with the altitude, so the SCREEN picture is identical at each one
            /// and the only variable across the sweep is absolute distance from the camera — which is what
            /// URP's shadow fade and shadow distance are both functions of. The building is proportionally
            /// taller than a real one (0.25A rather than 30 m) purely so its shadow is more than a couple
            /// of pixels wide at 256²; neither fade nor shadow distance depends on the occluder's size.</para>
            /// </summary>
            /// <param name="altitude">Camera altitude above the ground plane, metres.</param>
            /// <param name="ambient">Ambient mode — the sweep uses Flat to stay comparable with rungs 1-5;
            /// the app's own Skybox environment is a separate variable.</param>
            /// <returns>The map-scale layout at that altitude.</returns>
            public static SceneLayout MapScale(float altitude, AmbientMode ambient) => new SceneLayout(
                cameraPosition:         new Vector3(0f, altitude, -0.5178f * altitude),
                cameraRotation:         Quaternion.LookRotation(new Vector3(0f, -0.888f, 0.4598f), Vector3.up),
                orthographic:           false,
                orthoSizeOrFieldOfView: 60f,
                // Far beyond the pipeline's 1200 m shadow distance, so this rung measures the pipeline's
                // own limit rather than a clamp this fixture introduced.
                farClipPlane:           5000f,
                groundPlaneScale:       0.12f * altitude,
                cubePosition:           new Vector3(0f, 0.125f * altitude, 0f),
                cubeScale:              new Vector3(0.05f, 0.25f, 0.05f) * altitude,
                lightRotation:          Quaternion.Euler(50f, 30f, 0f),
                ambient:                ambient,
                ambientLight:           AmbientColor,
                // The sun's ground-plane direction is (0.499, 0.866); the roof's shadow reaches 0.21A along
                // it, so 0.14A sits inside the shadow and clear of the building's own footprint.
                shadowBoxCentre:        new Vector3( 0.070f * altitude, 0f,  0.121f * altitude),
                litBoxCentre:           new Vector3(-0.070f * altitude, 0f, -0.121f * altitude),
                boxHalfExtents:         new Vector3(0.020f, 0f, 0.020f) * altitude);
        }

        /// <summary>
        /// Rung 6 — the app's OWN rig at four camera altitudes. Rungs 1-5 all pass, which rules out the
        /// shaders, the transparent queue and the Entities Graphics backend; the remaining difference
        /// between this fixture and the running app is scale, and scale is what URP's shadow distance and
        /// its distance-based shadow fade are both functions of.
        ///
        /// <para>The camera rig, sun and field of view are the maintainer's Frame Debugger capture; every
        /// length scales with the altitude, so the screen picture is identical at each one and camera
        /// distance is the sole variable. The altitude at which this stops darkening is the number the
        /// diagnosis turns on: the pipeline's 1200 m shadow distance and the captured
        /// <c>_MainLightShadowParams</c> fade band (no fade below ~1072 m from the camera, fully faded past
        /// ~1201 m) both predict a boundary near 950-1070 m of altitude, since the ground at screen centre
        /// sits ~1.126× the altitude away.</para>
        ///
        /// <para>Deliberately NOT varied here, so a failure has one cause: ambient (a separate test below)
        /// and the floating origin. The latter is left for last because <c>SceneFrame</c> rebases the
        /// camera and the geometry together and URP rebuilds its light matrices from the live transforms
        /// each frame, so it has no mechanism to desynchronise them — it is the weakest of the three.</para>
        /// </summary>
        /// <param name="altitude">Camera altitude in metres; the app sits at ~1014 m at z16 and ~279 m at z18.</param>
        [Test]
        public void MapScale_GroundDarkensUnderACastShadow(
            [Values(100f, 300f, 1000f, 1400f)] float altitude)
        {
            Material ground = null, cube = null;
            try
            {
                (ground, cube) = BuildMapLayerMaterials(
                    $"BisectMapScale{altitude:F0}", GroundColor, CubeColor, buildingOpacity: 1f);
                AssertRungReceivesShadows(
                    $"rung6-map-scale-{altitude:F0}m",
                    Measure($"rung6-map-scale-{altitude:F0}m", SceneLayout.MapScale(altitude, AmbientMode.Flat),
                            ground, cube, viaEntities: true),
                    $"Rung 5 is the only difference: the SAME materials and backend now render the app's " +
                    $"own tilted perspective rig with the camera {altitude:F0} m up, everything scaled so " +
                    "the screen picture is unchanged. A red rung here with rung 5 green means DISTANCE is " +
                    "the blocker — URP's shadow distance or its distance-based shadow fade — and the " +
                    "altitude sweep's first failing value is the boundary. Compare the passing altitudes' " +
                    "logged luminances: a gradual decline across them is the fade, a cliff is the shadow " +
                    "distance.");
            }
            finally
            {
                DestroyMaterials(ground, cube);
            }
        }

        /// <summary>
        /// Rung 7 — the app's rig at its real altitude under the app's real ambient environment
        /// (<see cref="AmbientMode.Skybox"/>, which the demo scene uses), rather than the near-black flat
        /// ambient every rung above runs. A shadow only removes the light's DIRECT contribution, so a
        /// bright indirect term can leave a mathematically-correct shadow visually invisible. This rung
        /// tells the two apart: a red result here with rung 6 green at the same altitude means the shadow
        /// IS being applied and is simply being washed out.
        /// </summary>
        [Test]
        public void MapScale_UnderTheAppsSkyboxAmbient_GroundDarkensUnderACastShadow()
        {
            Material ground = null, cube = null;
            try
            {
                (ground, cube) = BuildMapLayerMaterials(
                    "BisectSkyboxAmbient", GroundColor, CubeColor, buildingOpacity: 1f);
                AssertRungReceivesShadows(
                    "rung7-skybox-ambient",
                    Measure("rung7-skybox-ambient", SceneLayout.MapScale(1014f, AmbientMode.Skybox),
                            ground, cube, viaEntities: true),
                    "Rung 6 at 1000 m is the only difference: ambient is now the scene's own Skybox " +
                    "environment instead of a near-black flat term. A red rung here with rung 6 green at " +
                    "the same altitude means the shadow is computed correctly and washed out by indirect " +
                    "light — a look problem, not a broken shadow path.");
            }
            finally
            {
                DestroyMaterials(ground, cube);
            }
        }

        /// <summary>Camera altitude the sweep runs at — the app's own ~1014 m, rounded to the altitude
        /// rung 6 measured at 4.30% darkening so the two are directly comparable.</summary>
        private const float SweepAltitude = 1000f;

        /// <summary>
        /// Rung 9 — the app's REAL palette. Every rung above it runs a 0.62 mid-grey ground under a
        /// near-black flat ambient, chosen for "plenty of headroom above and below"; the app is a LIGHT
        /// basemap whose ground is <c>#f8f4f0</c> (linear luminance 0.910) under a skybox probe at full
        /// intensity, and has no headroom at all. This rung changes only the two albedos and nothing else
        /// — same rig, same backend, same materials, same 1000 m altitude, sun 1.0, skybox ambient 1.0, at
        /// the <see cref="InstalledShadowDistance"/> the maintainer already raised the pipeline to.
        ///
        /// <para><b>This rung is EXPECTED to fail</b>, and its value is entirely in HOW. The diagnostics it
        /// emits before the assertion — the shadow region's lit luminance in 8-bit terms and the fraction of
        /// it on the ceiling, via <see cref="ClippingVerdict"/> — separate the only two stories that produce
        /// the same near-zero darkening: a shadow that is applied to a surface already at 255 (invisible,
        /// but correct), and a shadow that is never applied at all. If instead this rung PASSES, the harness
        /// still does not reproduce the app and the difference lies somewhere nobody has named yet — a
        /// louder finding than either story, and one to report rather than tune away.</para>
        ///
        /// <para>Post-processing is deliberately NOT added to reach the app's tonemapped frame:
        /// <c>DefaultVolumeProfile.asset</c> sets Tonemapping <c>mode = None</c>, so UberPost performs no
        /// highlight compression and the LDR conversion hard-clamps at 1.0 — which this fixture's ARGB32
        /// target already does. A real tonemapper would have PRESERVED highlight contrast, not destroyed
        /// it, so adding one would make the harness kinder than the app rather than equal to it.</para>
        /// </summary>
        [Test]
        public void MapScale_AtTheAppsRealAlbedo_GroundDarkensUnderACastShadow()
        {
            float originalShadowDistance = SetPipelineShadowDistance(InstalledShadowDistance);
            Material ground = null, cube = null;
            try
            {
                (ground, cube) = BuildMapLayerMaterials(
                    "BisectAppAlbedo", AppGroundColor, AppBuildingColor, AppBuildingOpacity);
                ShadowReading reading = Measure(
                    "rung9-app-albedo", SceneLayout.MapScale(SweepAltitude, AmbientMode.Skybox),
                    ground, cube, viaEntities: true);

                AssertRungReceivesShadows(
                    "rung9-app-albedo", reading,
                    "Rung 7 is the only difference: the ground is now the basemap's real #f8f4f0 " +
                    "(linear 0.910) and the building its real hsl(35,8%,85%), instead of a 0.62 grey and a " +
                    "saturated orange. This rung is EXPECTED to be red; read the CLIPPING line logged " +
                    "above it, not this percentage. " + ClippingVerdict(reading) + " If that line says " +
                    "CLIPPED, the shadow is being computed and has nowhere to darken into, and the fix is " +
                    "exposure (ambient or sun), not the shadow path. If it says NOT clipped while the " +
                    "darkening is still ~0%, clipping is dead as an explanation and the residual fault is " +
                    "elsewhere. If this rung PASSED, the harness does not reproduce the app at all and no " +
                    "rung of this ladder is evidence about it.");
            }
            finally
            {
                DestroyMaterials(ground, cube);
                SetPipelineShadowDistance(originalShadowDistance);
            }
        }

        /// <summary>
        /// Rung 8 — the parameter sweep. Rung 6 shows the shadow IS applied at 1000 m but only removes
        /// 4.30% of the ground's luminance, and rung 7 shows the app's own skybox ambient cuts that to
        /// 0.26%. Three knobs could plausibly restore it, and this rung measures all three rather than
        /// arguing about them: URP's shadow distance (which sets the distance-fade band), the ambient
        /// probe's intensity (the indirect term a shadow cannot remove), and the sun's intensity DOWNWARD
        /// (if the lit ground is clipping at 1.0 there is no headroom for a shadow to darken into, and
        /// dimming the sun would restore contrast that brightening it destroys — the maintainer measured
        /// intensity 3 turning the app fully white).
        ///
        /// <para>The sweep walks one axis at a time from the maintainer's current configuration and then
        /// crosses the two most promising values, so the SHAPE of the surface is the finding: a row that
        /// moves names the dominant lever, and a table that stays flat says none of these three is the
        /// blocker. Every row also reports the absolute 8-bit luminance step, because "4.30%" and "a
        /// difference a human can see" are different questions.</para>
        ///
        /// <para>The sweep runs the app's real palette (<see cref="AppGroundColor"/>,
        /// <see cref="AppBuildingColor"/>) under the app's real <see cref="AmbientMode.Skybox"/>
        /// environment throughout, so rung 9 is its baseline row: a sweep against the 0.62 grey and
        /// near-black flat ambient of rungs 1-6 tunes numbers that do not transfer to a near-white basemap,
        /// which is exactly how an earlier pass concluded that shadow distance alone restored the shadow
        /// while the maintainer's app — already at 6000 — still showed none. The clipped column is what
        /// makes each row readable: while it stays high, the row is measuring exposure, not shadowing.</para>
        /// </summary>
        [Test]
        public void MapScale_ShadowKnobSweep_FindsAKnobThatRestoresTheShadow()
        {
            (float ShadowDistance, float Ambient, float Light)[] combinations =
            {
                // The baseline: the app's own configuration, which rung 9 asserts against.
                ( 6000f, 1.0f, 1.0f),
                // Ambient alone, at the shadow distance the maintainer currently has installed.
                ( 6000f, 0.7f, 1.0f),
                ( 6000f, 0.5f, 1.0f),
                ( 6000f, 0.3f, 1.0f),
                ( 6000f, 0.1f, 1.0f),
                ( 6000f, 0.0f, 1.0f),
                // Sun intensity downward — the headroom theory, not the brightness theory. Raising it is
                // what the maintainer already tried (intensity 3 turned the scene fully white).
                ( 6000f, 1.0f, 0.7f),
                ( 6000f, 1.0f, 0.5f),
                // The shipped shadow distance as the control, so the fade's share stays separable from
                // exposure's at both ends of the ambient axis.
                ( 1200f, 1.0f, 1.0f),
                ( 1200f, 0.0f, 1.0f),
                // The two exposure knobs together.
                ( 6000f, 0.1f, 0.5f),
            };

            float  originalShadowDistance = ReadPipelineShadowDistance();
            double bestDarkening          = double.NegativeInfinity;
            string bestRow                = "none";
            string table = "shadowDist ambient light |  darkening | shadowBox off → on |  Δ8bit | clipped | probe | symErr";

            try
            {
                foreach ((float ShadowDistance, float Ambient, float Light) knobs in combinations)
                {
                    SetPipelineShadowDistance(knobs.ShadowDistance);
                    string label =
                        $"sweep-sd{knobs.ShadowDistance:F0}-amb{knobs.Ambient:F2}-sun{knobs.Light:F2}";

                    Material ground = null, cube = null;
                    try
                    {
                        (ground, cube) = BuildMapLayerMaterials(
                            label, AppGroundColor, AppBuildingColor, AppBuildingOpacity);
                        ShadowReading reading = Measure(
                            label, SceneLayout.MapScale(SweepAltitude, AmbientMode.Skybox),
                            ground, cube, viaEntities: true,
                            lightIntensity: knobs.Light, ambientIntensity: knobs.Ambient);

                        if (reading.NoGpuContext)
                            Assert.Inconclusive(
                                "the scene render and a blank control are both all-black: no GPU context " +
                                "in this batch session, so no sweep row is readable.");

                        double darkening = 1.0 - reading.ShadowBoxOn / reading.ShadowBoxOff;
                        double levels    = (reading.ShadowBoxOff - reading.ShadowBoxOn) * 255.0;
                        double symmetry  = math.abs(reading.ShadowBoxOff - reading.LitBoxOff) /
                                           math.max(reading.ShadowBoxOff, reading.LitBoxOff);

                        string row =
                            $"{knobs.ShadowDistance,10:F0} {knobs.Ambient,7:F2} {knobs.Light,5:F2} | " +
                            $"{darkening,10:P2} | {reading.ShadowBoxOff,8:F4} → {reading.ShadowBoxOn,6:F4} | " +
                            $"{levels,6:F1} | {reading.ShadowBoxClippedFractionOff,7:P1} | " +
                            $"{reading.AmbientIrradiance,5:F3} | {symmetry:P2}";
                        table += "\n" + row;
                        Debug.Log($"[shadow-sweep] {row}");

                        if (darkening > bestDarkening)
                        {
                            bestDarkening = darkening;
                            bestRow       = row;
                        }
                    }
                    finally
                    {
                        DestroyMaterials(ground, cube);
                    }
                }
            }
            finally
            {
                SetPipelineShadowDistance(originalShadowDistance);
            }

            Debug.Log($"[shadow-sweep] TABLE at {SweepAltitude:F0} m under skybox ambient\n{table}");

            Assert.Greater(bestDarkening, MinShadowDarkening,
                $"NO combination of shadow distance, ambient intensity and sun intensity darkened the " +
                $"shadowed ground by more than {MinShadowDarkening:P0} at {SweepAltitude:F0} m. The best " +
                $"row reached {bestDarkening:P2}: [{bestRow}]. The remaining fault is therefore NOT in " +
                $"these three knobs.\n{table}");
        }

        /// <summary>Sun elevation above the horizon for <c>Euler(50, 30, 0)</c>: the light direction's
        /// vertical component is 0.766, so the elevation is asin(0.766) = 50°, and a vertical edge of
        /// height h lays a shadow h/tan(50°) = 0.839h long across level ground.</summary>
        private static readonly double SunElevationRadians = math.asin(0.766);

        /// <summary>The sun's direction across the ground, normalised — the axis the shadow runs along and
        /// the axis <see cref="MapScale_ShadowMapResolution_ShadowKeepsItsGeometricLength"/> scans.</summary>
        private static readonly Vector3 SunGroundDirection = new Vector3(0.499f, 0f, 0.866f);

        /// <summary>Fraction of a scan point's unshadowed luminance it must lose to count as inside the
        /// shadow. Far above the &lt;1% the shadows-off symmetry control measures as readback noise, and far
        /// below the ~50% a fully-occluded point loses, so the located edge is not a threshold artefact.</summary>
        private const double ScanShadowThreshold = 0.15;

        /// <summary>Fraction of its GEOMETRIC length the rendered shadow must retain. A shadow eroded past
        /// this is being destroyed by the shadow map rather than drawn by it.</summary>
        private const double MinShadowLengthFraction = 0.60;

        /// <summary>
        /// Rung 10 — SHADOW-MAP TEXEL DENSITY, the one app/harness difference every rung above is blind to.
        /// Rung 9 proved the shadow is neither clipped nor faded at the maintainer's own settings, and the
        /// sweep found no exposure knob that matters; what is left is that the harness occluder is 250 m
        /// tall while the basemap's buildings are <c>render_height</c> 10-50 m.
        ///
        /// <para>Bias and PCF erode a shadow by a number of shadow-map TEXELS that does not depend on the
        /// occluder's size, so the quantity that decides visibility is the shadow's length measured IN
        /// TEXELS. That makes resolution a faithful stand-in for building height: halving the map doubles
        /// the texel, and URP scales its depth and normal bias by texel size, so 2048→256 puts a 250 m
        /// occluder at exactly the texels-per-shadow ratio a 31 m building has at the shipped 2048.
        /// Substituting resolution for height keeps the geometry, the camera and the sample boxes identical
        /// to rung 9, so this rung still changes one variable.</para>
        ///
        /// <para>The measurement is the shadow's LENGTH, not a fixed box's luminance: erosion eats a shadow
        /// from its EDGE inward, and a box in the interior of a 210 m shadow has room to spare at any
        /// resolution — it would report green and mean nothing. So the rung scans the ground along the sun's
        /// track, finds the furthest point still darkened, and compares that against the length the
        /// geometry demands.</para>
        /// </summary>
        /// <param name="shadowMapResolution">Main-light shadow-map resolution; 2048 is what the project
        /// ships, and each halving stands in for halving the building's height.</param>
        [Test]
        public void MapScale_ShadowMapResolution_ShadowKeepsItsGeometricLength(
            [Values(2048, 1024, 512, 256)] int shadowMapResolution)
        {
            SceneLayout layout = SceneLayout.MapScale(SweepAltitude, AmbientMode.Skybox);

            // The occluder is axis-aligned, so its footprint reaches (halfX+halfZ) x the sun's ground
            // direction; the roof's shadow starts there and runs 0.839 x the roof height further.
            double footprintReach = 0.5 * layout.CubeScale.x * SunGroundDirection.x +
                                    0.5 * layout.CubeScale.z * SunGroundDirection.z;
            double roofHeight     = layout.CubePosition.y + 0.5 * layout.CubeScale.y;
            double predictedTip   = footprintReach + roofHeight / math.tan(SunElevationRadians);

            // Start clear of the occluder's own screen silhouette, and run past the predicted tip so an
            // UNeroded shadow is bracketed rather than merely reached.
            const int scanSteps = 32;
            double    scanFrom  = 0.20 * predictedTip;
            double    scanStep  = (1.30 * predictedTip - scanFrom) / (scanSteps - 1);
            var       radii     = new double[scanSteps];
            var       points    = new Vector3[scanSteps];
            for (int i = 0; i < scanSteps; i++)
            {
                radii[i]  = scanFrom + i * scanStep;
                points[i] = SunGroundDirection * (float)radii[i];
            }

            float originalShadowDistance   = SetPipelineShadowDistance(InstalledShadowDistance);
            int   originalShadowResolution = SetPipelineShadowResolution(shadowMapResolution);
            Material ground = null, cube = null;
            try
            {
                (ground, cube) = BuildMapLayerMaterials(
                    $"BisectShadowRes{shadowMapResolution}",
                    AppGroundColor, AppBuildingColor, AppBuildingOpacity);

                ShadowReading reading = Measure(
                    $"rung10-shadowmap-{shadowMapResolution}", layout, ground, cube, viaEntities: true,
                    scanPoints: points, scanHalfExtent: (float)(0.9 * scanStep));

                if (reading.NoGpuContext)
                {
                    Assert.Inconclusive("no GPU context in this batch session; the scan is unreadable.");
                    return;
                }

                double measuredTip = 0.0;
                for (int i = 0; i < scanSteps; i++)
                {
                    // A point that projected off-frame reads zero in BOTH frames; dividing by it would
                    // manufacture a NaN and, through the comparison below, a silently shorter shadow.
                    if (reading.ScanOff[i] <= 0.0) continue;
                    double darkening = 1.0 - reading.ScanOn[i] / reading.ScanOff[i];
                    if (darkening > ScanShadowThreshold) measuredTip = radii[i];
                    TestContext.WriteLine(
                        $"[shadow-scan {shadowMapResolution}] r={radii[i],7:F1} m  " +
                        $"off={reading.ScanOff[i]:F4} on={reading.ScanOn[i]:F4}  {darkening,8:P2}");
                }

                // 2048/N is the height divisor this rung stands in for; the equivalent real building is the
                // harness occluder shrunk by it.
                double equivalentHeight = roofHeight * shadowMapResolution / 2048.0;
                Debug.Log(
                    $"[shadow-texel] res={shadowMapResolution,5} | stands in for a " +
                    $"{equivalentHeight,6:F1} m building at 2048 | shadow reaches {measuredTip,6:F1} m of a " +
                    $"predicted {predictedTip,6:F1} m = {measuredTip / predictedTip,7:P1} | " +
                    $"eroded {predictedTip - measuredTip,6:F1} m");

                Assert.Greater(measuredTip / predictedTip, MinShadowLengthFraction,
                    $"[rung10-shadowmap-{shadowMapResolution}] the rendered shadow reaches only " +
                    $"{measuredTip:F1} m of the {predictedTip:F1} m the geometry demands " +
                    $"({measuredTip / predictedTip:P1}), i.e. {predictedTip - measuredTip:F1} m of it was " +
                    $"eroded away. At this resolution the occluder spans the same number of shadow-map " +
                    $"texels a {equivalentHeight:F1} m building spans at the shipped 2048, so a basemap " +
                    "building of that height loses the same fraction of its shadow. Erosion is measured in " +
                    "texels and does not depend on the occluder's size, which is why rung 9's 250 m tower " +
                    "keeps its shadow while the app's buildings do not.");
            }
            finally
            {
                DestroyMaterials(ground, cube);
                SetPipelineShadowResolution(originalShadowResolution);
                SetPipelineShadowDistance(originalShadowDistance);
            }
        }

        /// <summary>
        /// Writes the active pipeline asset's main-light shadow-map resolution for the duration of a rung,
        /// the same in-memory, restore-in-<c>finally</c> way <see cref="SetPipelineShadowDistance"/> does —
        /// nothing here saves the asset, so the maintainer's uncommitted <c>RPAsset.asset</c> edit survives.
        /// </summary>
        /// <param name="resolution">Resolution to install.</param>
        /// <returns>The value that was in place before the call, for the caller's restore.</returns>
        private static int SetPipelineShadowResolution(int resolution)
        {
            RenderPipelineAsset pipeline = GraphicsSettings.currentRenderPipeline;
            Assert.IsNotNull(pipeline, "no scriptable render pipeline is active — shadow-map resolution is " +
                                       "a URP asset field, so this rung has nothing to vary.");

            var serialized = new SerializedObject(pipeline);
            SerializedProperty property = serialized.FindProperty("m_MainLightShadowmapResolution");
            Assert.IsNotNull(property,
                "the active pipeline asset has no m_MainLightShadowmapResolution field.");

            int previous = property.intValue;
            property.intValue = resolution;
            serialized.ApplyModifiedPropertiesWithoutUndo();
            return previous;
        }


        // ── Rung 11 — where the caster's HEIGHT comes from ───────────────────────────────────────

        /// <summary>
        /// The three ways a <c>Map/FillExtrusion</c> caster can end up 250 m tall. Only the first two route
        /// through <c>MapVertexModify</c>, and therefore through the <c>TEXCOORD3</c>/<c>TEXCOORD4</c>
        /// extrusion streams a caster pass has to declare with the semantics the mesh actually writes.
        /// </summary>
        public enum ExtrusionHeightSource
        {
            /// <summary>Per-vertex <c>TEXCOORD4.y</c> bake — the data-driven path liberty.json takes with
            /// <c>"fill-extrusion-height": ["get","render_height"]</c>.</summary>
            BakedStream,

            /// <summary>The <c>_ExtrusionHeight</c> uniform — the constant/zoom path. Its DIRECTION still
            /// comes from <c>TEXCOORD3.xyz</c>, which is why it is not immune to a channel mismatch.</summary>
            UniformProperty,

            /// <summary>Mesh geometry, with the extrusion streams absent and <c>_ExtrusionHeight</c> at its
            /// material default of 0 — the primitive cube rungs 1-10 cast with. The control.</summary>
            MeshGeometry,
        }

        /// <summary>Tile the fixture footprint is decoded in — mid-latitude, so the baked extrude-up carries
        /// a non-unity sec φ and the test cannot pass by accident on a unit vector.</summary>
        private static readonly TileId CasterTile = new TileId { Z = 10, X = 300, Y = 380 };

        /// <summary>MVT extent for the fixture footprint.</summary>
        private const double CasterTileExtent = 4096.0;

        /// <summary>Feature property the data-driven variant reads its height from — the name liberty.json
        /// uses, so the fixture expression is the app's own.</summary>
        private const string CasterHeightProperty = "render_height";

        /// <summary>Material float carrying the constant/zoom height. Not on <c>ShaderProperties</c>: the
        /// production binder reaches it through the paint applier, and this fixture drives it by hand.</summary>
        private const string ExtrusionHeightProperty = "_ExtrusionHeight";

        /// <summary>
        /// Rung 11 — THE REGRESSION TOOTH for "buildings cast flat footprint shadows". A fill-extrusion mesh
        /// is a HEIGHT-AGNOSTIC footprint at elevation 0: every metre of a building's silhouette is produced
        /// by <c>MapVertexModify</c> from the <c>TEXCOORD3</c>/<c>TEXCOORD4</c> streams
        /// <c>StyledFillExtrusionTileBuilder</c> writes. A caster pass that declares those inputs on any
        /// other semantic reads Unity's zero-fill for an absent stream, computes zero elevation, and draws a
        /// caster lying flat ON the ground — and a ground-coplanar caster casts nothing onto the ground.
        ///
        /// <para>Every rung above this one is blind to that, because all of them cast with a PRIMITIVE CUBE
        /// whose height is mesh geometry: <c>_ExtrusionHeight</c> defaults to 0 in <c>FillExtrusion.mat</c>
        /// and the cube supplies no extrusion streams, so <c>MapVertexModify</c> contributes nothing to them
        /// either way. <see cref="ExtrusionHeightSource.MeshGeometry"/> reproduces exactly that caster here,
        /// as the control: it shares this rung's scene, scan, materials and shader, and differs only in
        /// where the 250 m comes from. A defect in the extrusion streams' semantics reddens the two
        /// stream-driven variants and leaves the control green; anything that reddens all three is a fault
        /// in shadows at large, not in this channel mapping.</para>
        ///
        /// <para>The measurement is the shadow's LENGTH along the sun's ground track, reusing rung 10's
        /// scan: a luminance box on the building itself cannot tell a footprint shadow from an extruded one
        /// (both darken the roof area), whereas only a RAISED caster throws its shadow 0.839 x its height
        /// clear of its own footprint.</para>
        /// </summary>
        /// <param name="heightSource">Where this variant's 250 m of caster height comes from.</param>
        [Test]
        public void FillExtrusionCaster_ShadowFollowsTheExtrudedSilhouette(
            [Values(ExtrusionHeightSource.BakedStream,
                    ExtrusionHeightSource.UniformProperty,
                    ExtrusionHeightSource.MeshGeometry)] ExtrusionHeightSource heightSource)
        {
            SceneLayout layout = SceneLayout.MapScale(SweepAltitude, AmbientMode.Skybox);

            // The same occluder rung 10 measures: a 50 m square footprint whose roof is 250 m up. The
            // extruded variants must land on those numbers too, or the three are not comparable.
            float footprintMetres = layout.CubeScale.x;
            float roofHeight      = layout.CubePosition.y + 0.5f * layout.CubeScale.y;

            double footprintReach = 0.5 * footprintMetres * (SunGroundDirection.x + SunGroundDirection.z);
            double predictedTip   = footprintReach + roofHeight / math.tan(SunElevationRadians);

            const int scanSteps = 32;
            double    scanFrom  = 0.20 * predictedTip;
            double    scanStep  = (1.30 * predictedTip - scanFrom) / (scanSteps - 1);
            var       radii     = new double[scanSteps];
            var       points    = new Vector3[scanSteps];
            for (int i = 0; i < scanSteps; i++)
            {
                radii[i]  = scanFrom + i * scanStep;
                points[i] = SunGroundDirection * (float)radii[i];
            }

            float    originalShadowDistance = SetPipelineShadowDistance(InstalledShadowDistance);
            Material ground = null, cube = null;
            Mesh     caster = null;
            try
            {
                (ground, cube) = BuildMapLayerMaterials(
                    $"BisectCaster{heightSource}", AppGroundColor, AppBuildingColor, buildingOpacity: 1f);

                if (heightSource != ExtrusionHeightSource.MeshGeometry)
                {
                    bool dataDriven = heightSource == ExtrusionHeightSource.BakedStream;
                    caster = BuildExtrusionCasterMesh(roofHeight, dataDriven);
                    if (!dataDriven) cube.SetFloat(ExtrusionHeightProperty, roofHeight);
                }

                Mesh       installed      = caster;
                float      installedWidth = footprintMetres;
                float      installedRoof  = roofHeight;
                Action<GameObject> configureCaster = installed == null
                    ? null
                    : go => InstallExtrusionCaster(go, installed, installedWidth, installedRoof);

                ShadowReading reading = Measure(
                    $"rung11-{heightSource}", layout, ground, cube, viaEntities: true,
                    scanPoints: points, scanHalfExtent: (float)(0.9 * scanStep),
                    configureCaster: configureCaster);

                if (reading.NoGpuContext)
                {
                    Assert.Inconclusive("no GPU context in this batch session; the scan is unreadable.");
                    return;
                }

                double measuredTip = 0.0;
                for (int i = 0; i < scanSteps; i++)
                {
                    // A point that projected off-frame reads zero in BOTH frames; dividing by it would
                    // manufacture a NaN and, through the comparison below, a silently shorter shadow.
                    if (reading.ScanOff[i] <= 0.0) continue;
                    double darkening = 1.0 - reading.ScanOn[i] / reading.ScanOff[i];
                    if (darkening > ScanShadowThreshold) measuredTip = radii[i];
                    TestContext.WriteLine(
                        $"[caster-scan {heightSource}] r={radii[i],7:F1} m  " +
                        $"off={reading.ScanOff[i]:F4} on={reading.ScanOn[i]:F4}  {darkening,8:P2}");
                }

                Debug.Log(
                    $"[caster-height] source={heightSource,-15} | roof {roofHeight:F0} m over a " +
                    $"{footprintMetres:F0} m footprint | shadow reaches {measuredTip,6:F1} m of a predicted " +
                    $"{predictedTip,6:F1} m = {measuredTip / predictedTip,7:P1}");

                Assert.Greater(measuredTip / predictedTip, MinShadowLengthFraction,
                    $"[rung11-{heightSource}] the cast shadow reaches only {measuredTip:F1} m of the " +
                    $"{predictedTip:F1} m the extruded silhouette demands ({measuredTip / predictedTip:P1}); " +
                    $"the footprint alone reaches {footprintReach:F1} m. A caster that shadows no further " +
                    "than its own footprint was drawn FLAT, which happens when a caster pass declares " +
                    "extrudeUpAndT/bakedBaseHeight on semantics StyledFillExtrusionTileBuilder does not " +
                    "write (it writes TexCoord3/TexCoord4) and so reads Unity's zero-fill. Compare the " +
                    $"{ExtrusionHeightSource.MeshGeometry} variant: if it is green, shadows themselves are " +
                    "fine and the fault is in the extrusion streams' semantics.");
            }
            finally
            {
                // Measure's teardown destroys whatever mesh the caster object ended up holding; this only
                // catches a mesh built for a Measure call that threw before installing it.
                if (caster != null) UnityEngine.Object.DestroyImmediate(caster);
                DestroyMaterials(ground, cube);
                SetPipelineShadowDistance(originalShadowDistance);
            }
        }

        /// <summary>
        /// Builds the caster the way the tile pipeline does — the REAL
        /// <c>StyledFillExtrusionTileBuilder</c> write, so the mesh carries the production vertex layout and
        /// this rung pins both halves of the mesh-writes/shader-reads contract rather than the shader half
        /// alone. Asserts the two fixture properties the rung's verdict rests on: the footprint is flat (so
        /// all of the silhouette comes from the shader) and the bake stream carries the height on the
        /// data-driven path and zero on the uniform one.
        /// </summary>
        /// <param name="roofHeight">Height in metres the caster must reach once extruded.</param>
        /// <param name="dataDriven">True to bake the height per-vertex via <c>["get", …]</c>; false for a
        /// constant, which leaves the bake stream zero and the uniform carrying the height.</param>
        /// <returns>The caster mesh; the caller (or <see cref="Measure"/>'s teardown) destroys it.</returns>
        private static Mesh BuildExtrusionCasterMesh(float roofHeight, bool dataDriven)
        {
            var properties = new Dictionary<string, Value>
            {
                [CasterHeightProperty] = Value.Number(roofHeight),
            };
            var feature = new DictionaryFeature(
                properties:   dataDriven ? properties : null,
                geometryType: TileGeometryType.Polygon,
                geometry:     SquareRing(1000, 1000, 500));

            string paintJson = dataDriven
                ? $"{{\"fill-extrusion-height\":[\"get\",\"{CasterHeightProperty}\"]}}"
                : $"{{\"fill-extrusion-height\":{roofHeight}}}";
            var paint = FillExtrusion.PaintProperties.Parse(JsonParser.Parse(paintJson));
            Assert.AreEqual(dataDriven, paint.Height.DependsOnFeature,
                "fixture sanity: this variant must classify as the height path it claims to exercise.");

            Mesh mesh = TestTileMeshBuilder.BuildFillExtrusion(
                new[] { feature }, paint, 0.0, CasterTileExtent, CasterTile);
            Assert.IsNotNull(mesh, "a single square footprint must produce fill-extrusion geometry.");

            // Without this the rung would be vacuous: a caster with geometric height casts correctly
            // whatever the passes read, which is exactly how rungs 1-10 stayed green through the defect.
            Assert.Less(mesh.bounds.size.y, 1e-3f,
                "the fill-extrusion mesh must be a FLAT footprint — every metre of the silhouette this rung " +
                "measures has to come from MapVertexModify, not from the vertex positions.");

            var bake = new List<Vector2>();
            mesh.GetUVs(4, bake);
            Assert.AreEqual(mesh.vertexCount, bake.Count, "TEXCOORD4 must be populated for every vertex.");
            foreach (Vector2 baked in bake)
                Assert.AreEqual(dataDriven ? roofHeight : 0f, baked.y, 1e-3f,
                    "fixture sanity: the bake stream must carry the height on the data-driven path and stay " +
                    "zero on the uniform one.");

            return mesh;
        }

        /// <summary>
        /// Swaps the primitive caster for a built fill-extrusion mesh, sized and centred to occupy the same
        /// ground footprint the primitive did. The Y scale stays 1 and no height is applied here:
        /// <c>TransformObjectToWorldDir</c> normalises the extrude-up direction, so the shader's world
        /// displacement is the elevation in metres regardless of the object's scale.
        /// </summary>
        /// <param name="casterObject">The occluder object <see cref="BuildCube"/> created.</param>
        /// <param name="casterMesh">The flat fill-extrusion footprint to install.</param>
        /// <param name="footprintMetres">Ground footprint the mesh must be scaled to span.</param>
        /// <param name="roofHeight">Height the shader will extrude to, for the bounds padding below.</param>
        private static void InstallExtrusionCaster(
            GameObject casterObject, Mesh casterMesh, float footprintMetres, float roofHeight)
        {
            var meshFilter = casterObject.GetComponent<MeshFilter>();
            if (meshFilter.sharedMesh != null)
                UnityEngine.Object.DestroyImmediate(meshFilter.sharedMesh);
            meshFilter.sharedMesh = casterMesh;

            Bounds footprint = casterMesh.bounds;
            float  scale     = footprintMetres / math.max(footprint.size.x, footprint.size.z);
            casterObject.transform.localScale = new Vector3(scale, 1f, scale);
            casterObject.transform.position   =
                new Vector3(-footprint.center.x * scale, 0f, -footprint.center.z * scale);

            // The imported bounds describe the FLAT footprint; the silhouette that has to survive culling
            // only exists after the vertex stage runs. Production never notices — a tile's footprint is
            // kilometres across and a building is tens of metres — but this caster inverts that ratio.
            casterMesh.bounds = new Bounds(
                footprint.center, footprint.size + new Vector3(0f, 4f * roofHeight, 0f));
        }

        /// <summary>ZigZag-encodes an MVT parameter integer.</summary>
        /// <param name="n">Value to encode.</param>
        /// <returns>The encoded parameter.</returns>
        private static uint ZigZag(int n) => (uint)((n << 1) ^ (n >> 31));

        /// <summary>An axis-aligned square exterior ring as an MVT command stream — MoveTo to the first
        /// corner, LineTo for the rest, implicitly closed.</summary>
        /// <param name="x0">First corner's tile-local X.</param>
        /// <param name="y0">First corner's tile-local Y.</param>
        /// <param name="size">Edge length in tile units.</param>
        /// <returns>The command stream.</returns>
        private static uint[] SquareRing(int x0, int y0, int size) => new uint[]
        {
            (1u << 3) | 1u, ZigZag(x0), ZigZag(y0),
            (3u << 3) | 2u,
            ZigZag(size),  ZigZag(0),
            ZigZag(0),     ZigZag(size),
            ZigZag(-size), ZigZag(0),
        };

        /// <summary>Builds the receiver/caster pair exactly as the map's own layer materials are built:
        /// the committed lit bases, their runtime contracts, and transparent-band draw-order queues.</summary>
        /// <param name="namePrefix">Prefix for both material names, so the diagnostics log names the rung.</param>
        /// <param name="groundColor">Ground albedo, LINEAR — the shader multiplies it by the mesh's white
        /// vertex colours, which is the same product production forms the other way round.</param>
        /// <param name="buildingColor">Building albedo, linear.</param>
        /// <param name="buildingOpacity">The building's <c>fill-extrusion-opacity</c>.</param>
        /// <returns>The ground (fill) and building (fill-extrusion) materials; the caller destroys both.</returns>
        private static (Material ground, Material cube) BuildMapLayerMaterials(
            string namePrefix, Color groundColor, Color buildingColor, float buildingOpacity)
        {
            Material ground = BuildMapFillMaterial($"{namePrefix}Ground");
            FillTweaker.ApplyPainterContract(ground);
            ground.SetColor(ShaderProperties.PropertyNames.BaseColor, groundColor);
            ground.renderQueue = LayerDrawOrder.QueueFor(0);

            Material cube = BuildMapFillExtrusionMaterial($"{namePrefix}Building");
            FillExtrusionTweaker.ApplyElevatedContract(cube);
            cube.SetColor(ShaderProperties.PropertyNames.BaseColor, buildingColor);
            if (cube.HasProperty(ShaderProperties.PropertyNames.Opacity))
                cube.SetFloat(ShaderProperties.PropertyNames.Opacity, buildingOpacity);
            cube.renderQueue = LayerDrawOrder.QueueFor(1);
            return (ground, cube);
        }

        // ── Measurement ──────────────────────────────────────────────────────────────────────────

        /// <summary>
        /// One rung's four luminance readings plus the preconditions needed to read them, gathered from
        /// two renders of the same scene that differ ONLY in whether the light casts shadows.
        /// </summary>
        private readonly struct ShadowReading
        {
            /// <summary>Mean luminance of the box inside the cube's projected shadow, shadows enabled.</summary>
            public readonly double ShadowBoxOn;

            /// <summary>The same box with the light's shadows disabled — the reference it must fall below.</summary>
            public readonly double ShadowBoxOff;

            /// <summary>Mean luminance of the mirrored box outside the shadow, shadows enabled.</summary>
            public readonly double LitBoxOn;

            /// <summary>The same mirrored box with shadows disabled.</summary>
            public readonly double LitBoxOff;

            /// <summary>Colour variance inside the shadow box in the shadows-OFF frame — near zero unless
            /// the box straddles the cube or the ground's edge.</summary>
            public readonly double ShadowBoxVarianceOff;

            /// <summary>Colour variance inside the mirrored box in the shadows-OFF frame.</summary>
            public readonly double LitBoxVarianceOff;

            /// <summary>Per-point mean luminance along the sun's ground track, shadows ENABLED. Empty unless
            /// the rung asked for a scan. Paired index-for-index with <see cref="ScanOff"/>.</summary>
            public readonly double[] ScanOn;

            /// <summary>The same track with shadows disabled — the reference each point darkens from.</summary>
            public readonly double[] ScanOff;

            /// <summary>Probe luminance for an up-facing normal, sampled while this rung's environment was
            /// installed. The sweep's ambient column is a MULTIPLIER; this is what it multiplied.</summary>
            public readonly double AmbientIrradiance;

            /// <summary>Fraction of the shadow box sitting on the 8-bit ceiling in the shadows-OFF frame —
            /// the LIT reading of the region a shadow is supposed to darken. This is the number that decides
            /// between "the shadow is computed but has no headroom to darken into" and every other story:
            /// a region already at 255 cannot get darker, however correct the shadow term is.</summary>
            public readonly double ShadowBoxClippedFractionOff;

            /// <summary>Coverage analysis of the shadows-ON frame — the "something rendered" precondition.</summary>
            public readonly SnapshotVerdict OnVerdict;

            /// <summary>Global shadow keyword state sampled immediately after the shadows-ENABLED render.
            /// Reading it any later reports leftover state from the shadows-disabled render instead, which
            /// is what the first version of this fixture did.</summary>
            public readonly string ShadowKeywords;

            /// <summary>True when both the scene render and a blank control came back black, i.e. this batch
            /// session has no usable GPU context and the rung is unreadable rather than failing.</summary>
            public readonly bool NoGpuContext;

            /// <param name="shadowBoxOn">Shadow box, shadows enabled.</param>
            /// <param name="shadowBoxOff">Shadow box, shadows disabled.</param>
            /// <param name="litBoxOn">Mirrored box, shadows enabled.</param>
            /// <param name="litBoxOff">Mirrored box, shadows disabled.</param>
            /// <param name="shadowBoxVarianceOff">Shadow box colour variance, shadows disabled.</param>
            /// <param name="litBoxVarianceOff">Mirrored box colour variance, shadows disabled.</param>
            /// <param name="shadowBoxClippedFractionOff">Shadow box's clipped fraction, shadows disabled.</param>
            /// <param name="ambientIrradiance">Probe luminance for an up normal under this rung's environment.</param>
            /// <param name="scanOn">Luminance along the sun's ground track, shadows enabled.</param>
            /// <param name="scanOff">The same track with shadows disabled.</param>
            /// <param name="onVerdict">Coverage analysis of the shadows-enabled frame.</param>
            /// <param name="shadowKeywords">Keyword state sampled during the shadows-enabled render.</param>
            /// <param name="noGpuContext">Whether the GPU-context guard fired.</param>
            public ShadowReading(
                double shadowBoxOn, double shadowBoxOff,
                double litBoxOn,    double litBoxOff,
                double shadowBoxVarianceOff, double litBoxVarianceOff,
                double shadowBoxClippedFractionOff, double ambientIrradiance,
                double[] scanOn, double[] scanOff,
                SnapshotVerdict onVerdict, string shadowKeywords, bool noGpuContext)
            {
                ScanOn                     = scanOn;
                ScanOff                    = scanOff;
                AmbientIrradiance          = ambientIrradiance;
                ShadowKeywords             = shadowKeywords;
                ShadowBoxOn                = shadowBoxOn;
                ShadowBoxOff               = shadowBoxOff;
                LitBoxOn                   = litBoxOn;
                LitBoxOff                  = litBoxOff;
                ShadowBoxVarianceOff       = shadowBoxVarianceOff;
                LitBoxVarianceOff          = litBoxVarianceOff;
                ShadowBoxClippedFractionOff = shadowBoxClippedFractionOff;
                OnVerdict                  = onVerdict;
                NoGpuContext               = noGpuContext;
            }
        }

        /// <summary>
        /// Builds the shadow scene with the given materials, renders it with the light's shadows enabled
        /// and then disabled, and samples both ground boxes in both frames. Writes both frames to
        /// <c>Logs/snapshots/</c> and logs the material, pipeline and luminance diagnostics — those are
        /// emitted even when the rung goes on to pass, because the cross-rung comparison is the bisect.
        /// </summary>
        /// <param name="label">Rung name; used for the PNG file names and every log line.</param>
        /// <param name="groundMaterial">Material for the ground plane (the shadow receiver).</param>
        /// <param name="cubeMaterial">Material for the occluder cube (the shadow caster).</param>
        /// <param name="viaEntities">When true the two meshes are drawn by Entities Graphics entities and
        /// the source <see cref="GameObject"/>s are deactivated, instead of by their own MeshRenderers.</param>
        /// <param name="lightIntensity">Directional light intensity; the sweep drives it DOWNWARD to test
        /// whether the lit surface is clipping and so has no headroom left for a shadow to darken into.</param>
        /// <param name="ambientIntensity">Multiplier on the ambient probe. Only <see cref="AmbientMode.Skybox"/>
        /// reads it, so it is inert for every flat-ambient rung.</param>
        /// <param name="scanPoints">Ground points to sample in both frames, or null for no scan. Used to
        /// locate the shadow's far edge, which a fixed sample box cannot see.</param>
        /// <param name="scanHalfExtent">Half-extent of each scan sample, world metres.</param>
        /// <param name="configureCaster">Runs on the occluder object after it is built and BEFORE the
        /// entity conversion reads its mesh and transform, or null to cast with the primitive cube as
        /// every rung above rung 11 does. Whatever mesh the object holds afterwards is destroyed with it.</param>
        /// <returns>The rung's readings, for <see cref="AssertRungReceivesShadows"/>.</returns>
        private static ShadowReading Measure(
            string label, in SceneLayout layout,
            Material groundMaterial, Material cubeMaterial, bool viaEntities,
            float lightIntensity = LightIntensity, float ambientIntensity = 1f,
            Vector3[] scanPoints = null, float scanHalfExtent = 0f,
            Action<GameObject> configureCaster = null)
        {
            var      previousAmbientMode      = RenderSettings.ambientMode;
            Color    previousAmbientLight     = RenderSettings.ambientLight;
            float    previousAmbientIntensity = RenderSettings.ambientIntensity;
            Material previousSkybox           = RenderSettings.skybox;
            Light    previousSun              = RenderSettings.sun;
            World    previousDefaultWorld     = World.DefaultGameObjectInjectionWorld;

            GameObject cameraGo = null, lightGo = null, groundGo = null, cubeGo = null;
            World      world    = null;
            var        snap     = new SnapshotRenderer(SnapW, SnapH);

            try
            {
                RenderSettings.ambientMode      = layout.Ambient;
                RenderSettings.ambientLight     = layout.AmbientLight;
                RenderSettings.ambientIntensity = ambientIntensity;

                Camera camera;
                (cameraGo, camera) = BuildCamera(layout);

                lightGo   = new GameObject($"ShadowBisectLight_{label}");
                var light = lightGo.AddComponent<Light>();
                light.type           = LightType.Directional;
                light.intensity      = lightIntensity;
                light.shadowStrength = 1f;
                lightGo.transform.rotation = layout.LightRotation;

                // The skybox probe is baked LAST, and only here: it needs both the skybox material and the
                // sun, and the sun is this light, which does not exist until now. Neither was ever set
                // before, so every "Skybox ambient" rung baked whatever the EditMode runner's own scene
                // carried — a probe measured at ~0.074 irradiance, roughly 5x darker than the demo scene's
                // Default-Skybox at intensity 1. That is the same class of error as the 0.62 albedo: it
                // gave the shadow contrast the app does not have.
                if (layout.Ambient == AmbientMode.Skybox)
                {
                    RenderSettings.skybox = AssetDatabase.GetBuiltinExtraResource<Material>(
                        "Default-Skybox.mat");
                    RenderSettings.sun = light;
                    DynamicGI.UpdateEnvironment();
                }

                groundGo = BuildGround(groundMaterial, layout);
                cubeGo   = BuildCube(cubeMaterial, layout);
                configureCaster?.Invoke(cubeGo);

                RectInt shadowRect = ProjectGroundBox(camera, layout.ShadowBoxCentre, layout.BoxHalfExtents);
                RectInt litRect    = ProjectGroundBox(camera, layout.LitBoxCentre,    layout.BoxHalfExtents);

                var scanExtent = new Vector3(scanHalfExtent, 0f, scanHalfExtent);
                var scanRects  = new RectInt[scanPoints == null ? 0 : scanPoints.Length];
                for (int i = 0; i < scanRects.Length; i++)
                    scanRects[i] = ProjectGroundBox(camera, scanPoints[i], scanExtent);

                if (viaEntities)
                {
                    world = BuildEntityScene(groundGo, cubeGo);
                }

                string keywordsDuringShadowsOn = null;

                byte[] Capture(LightShadows shadows, string suffix)
                {
                    light.shadows = shadows;
                    if (world != null) TickPresentation(world);
                    // The first render of a configuration absorbs shader-variant warm-up; the second is
                    // the steady state the measurement reads.
                    snap.Render(camera);
                    snap.Render(camera);
                    if (shadows != LightShadows.None) keywordsDuringShadowsOn = ReadShadowKeywords();
                    snap.WritePng($"shadow-bisect-{label}-{suffix}.png");
                    return (byte[])snap.RawPixels.Clone();
                }

                byte[] shadowsOn  = Capture(LightShadows.Soft, "shadows-on");
                byte[] shadowsOff = Capture(LightShadows.None, "shadows-off");

                bool noGpu = false;
                if (snap.IsAllBlack())
                {
                    // The blank control has to work for the entity rung too, where a MeshRenderer toggle
                    // reaches nothing — culling the camera empties the frame whichever path submits it.
                    int previousCullingMask = camera.cullingMask;
                    camera.cullingMask = 0;
                    snap.Render(camera);
                    noGpu = snap.IsAllBlack();
                    camera.cullingMask = previousCullingMask;
                }

                var reading = new ShadowReading(
                    shadowBoxOn:          BoxLuminance(shadowsOn,  shadowRect),
                    shadowBoxOff:         BoxLuminance(shadowsOff, shadowRect),
                    litBoxOn:             BoxLuminance(shadowsOn,  litRect),
                    litBoxOff:            BoxLuminance(shadowsOff, litRect),
                    shadowBoxVarianceOff: BoxVariance(shadowsOff,  shadowRect),
                    litBoxVarianceOff:    BoxVariance(shadowsOff,  litRect),
                    shadowBoxClippedFractionOff: BoxClippedFraction(shadowsOff, shadowRect),
                    ambientIrradiance:    AmbientIrradianceUp(),
                    scanOn:               ScanLuminances(shadowsOn,  scanRects),
                    scanOff:              ScanLuminances(shadowsOff, scanRects),
                    onVerdict:            SnapshotCoverage.Analyse(shadowsOn, SnapW, SnapH, BgR8, BgG8, BgB8),
                    shadowKeywords:       keywordsDuringShadowsOn,
                    noGpuContext:         noGpu);

                LogDiagnostics(label, groundMaterial, cubeMaterial, reading);
                return reading;
            }
            finally
            {
                snap.Dispose();
                // Restored unconditionally: BuildEntityScene installs the default world before it finishes
                // populating it, so a throw in between leaves `world` null with the global already moved.
                World.DefaultGameObjectInjectionWorld = previousDefaultWorld;
                if (world != null && world.IsCreated) world.Dispose();
                DestroyGameObjects(cubeGo, groundGo, lightGo, cameraGo);
                RenderSettings.sun              = previousSun;
                RenderSettings.skybox           = previousSkybox;
                RenderSettings.ambientIntensity = previousAmbientIntensity;
                RenderSettings.ambientLight     = previousAmbientLight;
                RenderSettings.ambientMode      = previousAmbientMode;
            }
        }

        /// <summary>
        /// Applies one rung's preconditions and then its claim, in that order — a claim asserted before its
        /// preconditions can pass for the wrong reason.
        /// </summary>
        /// <param name="label">Rung name, echoed into every failure message.</param>
        /// <param name="reading">The rung's readings from <see cref="Measure"/>.</param>
        /// <param name="diagnosis">What a failure of THIS rung means given the rungs below it — the
        /// sentence that turns a red test into a located defect.</param>
        private static void AssertRungReceivesShadows(
            string label, in ShadowReading reading, string diagnosis)
        {
            if (reading.NoGpuContext)
            {
                Assert.Inconclusive(
                    $"[{label}] the scene render and a blank control are both all-black: no GPU context " +
                    "in this batch session. Re-run as PlayMode: ./Tools/run-tests.sh PlayMode");
                return;
            }

            // ── Preconditions: something rendered, and the two boxes really are interchangeable ──
            Assert.IsFalse(reading.OnVerdict.IsBlank,
                $"[{label}] precondition: the shadows-on frame is blank. A 'a dark region exists' claim " +
                "over an empty frame is vacuous.");
            Assert.IsFalse(reading.OnVerdict.IsUniform,
                $"[{label}] precondition: the shadows-on frame is a single flat colour — neither the " +
                "ground, the cube nor a shadow is distinguishable in it.");
            Assert.Greater(reading.OnVerdict.FilledFraction, MinFilledFraction,
                $"[{label}] precondition: the shadows-on frame must show the ground plane covering more " +
                $"than {MinFilledFraction:P0} of the frame; measured {reading.OnVerdict.FilledFraction:F4}.");

            Assert.Less(reading.ShadowBoxVarianceOff, MaxBoxVariance,
                $"[{label}] precondition: the shadow sample box is not a flat piece of ground in the " +
                $"shadows-off frame (colour variance {reading.ShadowBoxVarianceOff:F6}). It is straddling " +
                "the cube or the ground's edge, so its luminance means nothing.");
            Assert.Less(reading.LitBoxVarianceOff, MaxBoxVariance,
                $"[{label}] precondition: the mirrored sample box is not a flat piece of ground in the " +
                $"shadows-off frame (colour variance {reading.LitBoxVarianceOff:F6}).");

            Assert.Greater(reading.ShadowBoxOff, 0.0,
                $"[{label}] precondition: the shadow sample box reads zero luminance with shadows off — " +
                "nothing was drawn there at all, so 'it got darker' cannot be measured.");
            Assert.Greater(reading.LitBoxOff, 0.0,
                $"[{label}] precondition: the mirrored sample box reads zero luminance with shadows off.");

            // ── The control: with shadows off the two boxes must be interchangeable ──
            double symmetryError =
                math.abs(reading.ShadowBoxOff - reading.LitBoxOff) /
                math.max(reading.ShadowBoxOff, reading.LitBoxOff);
            Assert.Less(symmetryError, MaxSymmetryError,
                $"[{label}] instrument failure, NOT a pass: with the light's shadows DISABLED the two " +
                $"ground boxes must read the same luminance (they are mirror images on a flat plane under " +
                $"a uniform light), but they differ by {symmetryError:P2} " +
                $"(shadow box {reading.ShadowBoxOff:F4}, mirrored box {reading.LitBoxOff:F4}). Something " +
                "other than shadowing distinguishes them, so the claim below could not attribute a " +
                "difference to a shadow.");

            // ── The claim ──
            double shadowBoxDarkening = 1.0 - reading.ShadowBoxOn / reading.ShadowBoxOff;
            double litBoxDrift        = math.abs(1.0 - reading.LitBoxOn / reading.LitBoxOff);

            Assert.Greater(shadowBoxDarkening, MinShadowDarkening,
                $"[{label}] THE SHADOW IS NOT BEING RECEIVED. Enabling the light's shadows must darken the " +
                $"ground box that lies inside the cube's projected shadow by more than " +
                $"{MinShadowDarkening:P0}; it changed by {shadowBoxDarkening:P2} " +
                $"({reading.ShadowBoxOff:F4} → {reading.ShadowBoxOn:F4}). " +
                $"The mirrored box outside the shadow moved {litBoxDrift:P2} " +
                $"({reading.LitBoxOff:F4} → {reading.LitBoxOn:F4}).\n{diagnosis}");

            Assert.Less(litBoxDrift, MaxUnshadowedDrift,
                $"[{label}] the ground box OUTSIDE the cube's shadow also changed by {litBoxDrift:P2} " +
                $"({reading.LitBoxOff:F4} → {reading.LitBoxOn:F4}) when shadows were enabled. The whole " +
                "ground dimmed rather than a shadow landing on one region, so the darkening measured on " +
                "the other box is not evidence of a received shadow.");
        }

        // ── Scene ────────────────────────────────────────────────────────────────────────────────

        /// <summary>Snapshot camera for a layout, disabled so only <c>Camera.Render</c> draws it. The
        /// aspect is pinned to 1 rather than inherited from the screen, because
        /// <see cref="ProjectGroundBox"/> reads the projection matrix before the square render target is
        /// attached.</summary>
        /// <param name="layout">The rig to build.</param>
        /// <returns>The camera's <see cref="GameObject"/> (the caller destroys it) and the camera.</returns>
        private static (GameObject go, Camera camera) BuildCamera(in SceneLayout layout)
        {
            var go     = new GameObject("ShadowBisectCamera");
            var camera = go.AddComponent<Camera>();
            camera.transform.position = layout.CameraPosition;
            camera.transform.rotation = layout.CameraRotation;
            camera.orthographic       = layout.Orthographic;
            if (layout.Orthographic) camera.orthographicSize = layout.OrthoSizeOrFieldOfView;
            else                     camera.fieldOfView      = layout.OrthoSizeOrFieldOfView;
            camera.aspect             = 1f;
            camera.nearClipPlane      = 0.3f;
            camera.farClipPlane       = layout.FarClipPlane;
            camera.clearFlags         = CameraClearFlags.SolidColor;
            camera.backgroundColor    = BgColor;
            camera.enabled            = false;
            return (go, camera);
        }

        /// <summary>Builds the ground plane — the shadow RECEIVER, which never casts.</summary>
        /// <param name="material">Material to draw it with.</param>
        /// <param name="layout">Supplies the plane's scale.</param>
        /// <returns>The plane's <see cref="GameObject"/>; the caller destroys it.</returns>
        private static GameObject BuildGround(Material material, in SceneLayout layout)
        {
            var go = BuildPrimitive(PrimitiveType.Plane, "ShadowBisectGround", material);
            go.transform.position   = Vector3.zero;
            go.transform.localScale = new Vector3(layout.GroundPlaneScale, 1f, layout.GroundPlaneScale);
            var meshRenderer = go.GetComponent<MeshRenderer>();
            meshRenderer.shadowCastingMode = ShadowCastingMode.Off;
            meshRenderer.receiveShadows    = true;
            return go;
        }

        /// <summary>Builds the occluder — the shadow CASTER, placed so its shadow lands clear of its own
        /// screen footprint under the layout's sun.</summary>
        /// <param name="material">Material to draw it with.</param>
        /// <param name="layout">Supplies the occluder's position and scale.</param>
        /// <returns>The occluder's <see cref="GameObject"/>; the caller destroys it.</returns>
        private static GameObject BuildCube(Material material, in SceneLayout layout)
        {
            var go = BuildPrimitive(PrimitiveType.Cube, "ShadowBisectCube", material);
            go.transform.position   = layout.CubePosition;
            go.transform.localScale = layout.CubeScale;
            var meshRenderer = go.GetComponent<MeshRenderer>();
            meshRenderer.shadowCastingMode = ShadowCastingMode.On;
            meshRenderer.receiveShadows    = true;
            return go;
        }

        /// <summary>
        /// Creates a primitive whose mesh is a private copy carrying an explicit WHITE vertex-colour
        /// stream. The copy is not cosmetic: <c>Map/Fill</c> and <c>Map/FillExtrusion</c> both multiply
        /// albedo by the mesh COLOR stream (their data-driven per-feature colour bake), and Unity's
        /// built-in primitive meshes have none — so the map materials would shade an undefined colour.
        /// </summary>
        /// <param name="type">Primitive to create.</param>
        /// <param name="name">Name for the created object and its mesh copy.</param>
        /// <param name="material">Material to assign.</param>
        /// <returns>The created <see cref="GameObject"/>; the caller destroys it.</returns>
        private static GameObject BuildPrimitive(PrimitiveType type, string name, Material material)
        {
            var go = GameObject.CreatePrimitive(type);
            go.name = name;

            var collider = go.GetComponent<Collider>();
            if (collider != null) UnityEngine.Object.DestroyImmediate(collider);

            var meshFilter = go.GetComponent<MeshFilter>();
            var mesh       = UnityEngine.Object.Instantiate(meshFilter.sharedMesh);
            mesh.name      = $"{name}Mesh";
            var colors     = new Color32[mesh.vertexCount];
            for (int i = 0; i < colors.Length; i++) colors[i] = new Color32(255, 255, 255, 255);
            mesh.colors32 = colors;
            meshFilter.sharedMesh = mesh;

            go.GetComponent<MeshRenderer>().sharedMaterial = material;
            return go;
        }

        // ── Entities Graphics rung ───────────────────────────────────────────────────────────────

        /// <summary>
        /// Re-creates the two source objects as Entities Graphics entities and deactivates the originals,
        /// so the frame is drawn entirely by DOTS-instanced BRG batches — the demo's real submission path.
        /// The shadow declarations mirror the map's own: the ground receives but never casts, the building
        /// does both.
        /// </summary>
        /// <param name="groundGo">The ground object, deactivated by this method.</param>
        /// <param name="cubeGo">The cube object, deactivated by this method.</param>
        /// <returns>The created <see cref="World"/>; the caller disposes it and restores the previous
        /// default world.</returns>
        private static World BuildEntityScene(GameObject groundGo, GameObject cubeGo)
        {
            var groundMesh     = groundGo.GetComponent<MeshFilter>().sharedMesh;
            var cubeMesh       = cubeGo.GetComponent<MeshFilter>().sharedMesh;
            var groundMaterial = groundGo.GetComponent<MeshRenderer>().sharedMaterial;
            var cubeMaterial   = cubeGo.GetComponent<MeshRenderer>().sharedMaterial;
            float4x4 groundToWorld = ToFloat4x4(groundGo.transform.localToWorldMatrix);
            float4x4 cubeToWorld   = ToFloat4x4(cubeGo.transform.localToWorldMatrix);

            groundGo.SetActive(false);
            cubeGo.SetActive(false);

            var world = DefaultWorldInitialization.Initialize("ShadowBisectWorld", editorWorld: false);
            World.DefaultGameObjectInjectionWorld = world;

            var meshArray = new RenderMeshArray(
                new[] { groundMaterial, cubeMaterial }, new[] { groundMesh, cubeMesh });

            CreateRenderEntity(world, meshArray, materialIndex: 0, meshIndex: 0,
                localToWorld: groundToWorld, cast: ShadowCastingMode.Off);
            CreateRenderEntity(world, meshArray, materialIndex: 1, meshIndex: 1,
                localToWorld: cubeToWorld,   cast: ShadowCastingMode.On);

            return world;
        }

        /// <summary>Creates one Entities Graphics render entity with generous bounds, so frustum culling
        /// never removes it and a blank rung means "did not submit", never "was culled".</summary>
        /// <param name="world">World to create the entity in.</param>
        /// <param name="meshArray">Shared mesh/material array both entities index into.</param>
        /// <param name="materialIndex">Index into <paramref name="meshArray"/>'s materials.</param>
        /// <param name="meshIndex">Index into <paramref name="meshArray"/>'s meshes.</param>
        /// <param name="localToWorld">The entity's world transform.</param>
        /// <param name="cast">Shadow-casting declaration; receiving is unconditional, as it is in the map.</param>
        private static void CreateRenderEntity(
            World world, RenderMeshArray meshArray,
            int materialIndex, int meshIndex, float4x4 localToWorld, ShadowCastingMode cast)
        {
            EntityManager entityManager = world.EntityManager;
            var description = new RenderMeshDescription(cast, receiveShadows: true);
            Entity entity   = entityManager.CreateEntity();

            RenderMeshUtility.AddComponents(
                entity, entityManager, description, meshArray,
                MaterialMeshInfo.FromRenderMeshArrayIndices(materialIndex, meshIndex));

            entityManager.SetComponentData(entity, new LocalToWorld { Value = localToWorld });
            if (entityManager.HasComponent<RenderBounds>(entity))
                entityManager.SetComponentData(entity, new RenderBounds
                {
                    Value = new AABB { Center = float3.zero, Extents = new float3(1e6f) }
                });
        }

        /// <summary>Converts a Unity transform matrix to the <see cref="float4x4"/> Entities Graphics
        /// stores in <see cref="LocalToWorld"/> — column by column, since no conversion is defined.</summary>
        /// <param name="matrix">The transform matrix to convert.</param>
        /// <returns>The same matrix as a <see cref="float4x4"/>.</returns>
        private static float4x4 ToFloat4x4(Matrix4x4 matrix)
            => new float4x4(
                matrix.GetColumn(0), matrix.GetColumn(1), matrix.GetColumn(2), matrix.GetColumn(3));

        /// <summary>
        /// Ticks the three top-level system groups twice. Entities Graphics uploads instance data and
        /// registers its BRG batches from <c>PresentationSystemGroup</c>, which the player loop drives —
        /// and the player loop does not run in headless EditMode, so without this an entity renders blank.
        /// </summary>
        /// <param name="world">The world to tick.</param>
        private static void TickPresentation(World world)
        {
            for (int pass = 0; pass < 2; pass++)
            {
                world.GetExistingSystemManaged<InitializationSystemGroup>()?.Update();
                world.GetExistingSystemManaged<SimulationSystemGroup>()?.Update();
                world.GetExistingSystemManaged<PresentationSystemGroup>()?.Update();
            }
        }

        // ── Materials ────────────────────────────────────────────────────────────────────────────

        /// <summary>Builds a stock <c>Universal Render Pipeline/Lit</c> material with a matte surface, so
        /// the control rung contains no map code whatsoever.</summary>
        /// <param name="name">Material name, for the diagnostics log.</param>
        /// <param name="color">Albedo.</param>
        /// <returns>The material; the caller destroys it.</returns>
        private static Material BuildStockLitMaterial(string name, Color color)
        {
            Shader shader = Shader.Find("Universal Render Pipeline/Lit");
            Assert.IsNotNull(shader,
                "Shader 'Universal Render Pipeline/Lit' not found — URP is not the active render pipeline " +
                "in this session, so no rung of this ladder can be read.");

            var material = new Material(shader) { name = name };
            material.SetColor(ShaderProperties.PropertyNames.BaseColor, color);
            material.SetFloat("_Smoothness", 0f);
            material.SetFloat("_Metallic",   0f);
            return material;
        }

        /// <summary>Instantiates the project's committed <c>Map/Fill</c> lit base material, so the rung
        /// exercises the same import-baked keyword and property state the map's own layer materials are
        /// cloned from rather than a hand-built approximation.</summary>
        /// <param name="name">Material name, for the diagnostics log.</param>
        /// <returns>The material; the caller destroys it.</returns>
        private static Material BuildMapFillMaterial(string name)
            => LoadBaseMaterial(MapFillMaterialPath, name);

        /// <summary>Instantiates the project's committed <c>Map/FillExtrusion</c> lit base material.</summary>
        /// <param name="name">Material name, for the diagnostics log.</param>
        /// <returns>The material; the caller destroys it.</returns>
        private static Material BuildMapFillExtrusionMaterial(string name)
            => LoadBaseMaterial(MapFillExtrusionMaterialPath, name);

        /// <summary>Loads a committed base <c>.mat</c> and returns a private copy of it.</summary>
        /// <param name="assetPath">Project-relative path to the base material.</param>
        /// <param name="name">Name for the copy.</param>
        /// <returns>The copy; the caller destroys it.</returns>
        private static Material LoadBaseMaterial(string assetPath, string name)
        {
            var baseMaterial = AssetDatabase.LoadAssetAtPath<Material>(assetPath);
            Assert.IsNotNull(baseMaterial,
                $"Base material '{assetPath}' not found — this rung cannot exercise the map's real " +
                "material state.");
            return new Material(baseMaterial) { name = name };
        }

        /// <summary>
        /// Puts a map material into the most vanilla render state its shader supports: opaque One/Zero
        /// blend, depth write on, the Geometry queue, no transparent surface keyword. This is what makes
        /// rung 2 differ from rung 3 in render state alone.
        /// </summary>
        /// <param name="material">The material to force opaque.</param>
        private static void ForceOpaqueState(Material material)
        {
            material.SetFloat("_ZWrite",   1f);
            material.SetFloat("_SrcBlend", (float)UnityEngine.Rendering.BlendMode.One);
            material.SetFloat("_DstBlend", (float)UnityEngine.Rendering.BlendMode.Zero);
            material.SetFloat("_ZTest",    (float)UnityEngine.Rendering.CompareFunction.LessEqual);
            material.DisableKeyword(FillTweaker.SurfaceTypeTransparentKeyword);
            material.SetColor(ShaderProperties.PropertyNames.BaseColor, GroundColor);
            material.SetFloat(ShaderProperties.PropertyNames.Opacity, 1f);
            material.renderQueue = (int)RenderQueue.Geometry;
        }

        // ── Sampling ─────────────────────────────────────────────────────────────────────────────

        /// <summary>
        /// Screen rectangle covered by an axis-aligned ground-plane box, from the camera's own view and
        /// projection matrices. Replaces the closed-form top-down mapping the first version used, which
        /// only held for an orthographic camera looking straight down.
        /// </summary>
        /// <param name="camera">The camera the frame was rendered through.</param>
        /// <param name="centre">Box centre on the ground plane.</param>
        /// <param name="halfExtents">Box half-extents; the Y component is ignored.</param>
        /// <returns>The covered pixel rectangle in the snapshot buffer.</returns>
        private static RectInt ProjectGroundBox(Camera camera, Vector3 centre, Vector3 halfExtents)
        {
            Matrix4x4 worldToClip = camera.projectionMatrix * camera.worldToCameraMatrix;
            int minX = int.MaxValue, minY = int.MaxValue, maxX = int.MinValue, maxY = int.MinValue;

            for (int corner = 0; corner < 4; corner++)
            {
                var world = new Vector3(
                    centre.x + ((corner & 1) == 0 ? -halfExtents.x : halfExtents.x),
                    centre.y,
                    centre.z + ((corner & 2) == 0 ? -halfExtents.z : halfExtents.z));

                Vector4 clip = worldToClip * new Vector4(world.x, world.y, world.z, 1f);
                Assert.Greater(clip.w, 0f,
                    $"sample box corner {world} is behind the camera — the layout puts the ground region " +
                    "outside the view, so nothing measured from it would mean anything.");

                // NDC y is up and the readback buffer's row 0 is its bottom scanline, so the row index
                // grows with NDC y and no flip is needed.
                int x = (int)math.round((clip.x / clip.w * 0.5f + 0.5f) * SnapW);
                int y = (int)math.round((clip.y / clip.w * 0.5f + 0.5f) * SnapH);
                minX = math.min(minX, x); maxX = math.max(maxX, x);
                minY = math.min(minY, y); maxY = math.max(maxY, y);
            }

            return new RectInt(minX, minY, maxX - minX, maxY - minY);
        }

        /// <summary>Rec.709 mean luminance inside a sample rectangle.</summary>
        /// <param name="pixels">RGBA32 frame from <see cref="SnapshotRenderer.RawPixels"/>.</param>
        /// <param name="rect">The rectangle to average.</param>
        /// <returns>Mean luminance in [0,1].</returns>
        private static double BoxLuminance(byte[] pixels, RectInt rect)
        {
            double[] mean = SnapshotCoverage.SampleRegionMeanColor(
                pixels, SnapW, SnapH, rect.xMin, rect.yMin, rect.xMax, rect.yMax);
            return 0.2126 * mean[0] + 0.7152 * mean[1] + 0.0722 * mean[2];
        }

        /// <summary>Mean luminance of each rectangle along a scan track.</summary>
        /// <param name="pixels">RGBA32 frame.</param>
        /// <param name="rects">The track's sample rectangles.</param>
        /// <returns>One luminance per rectangle, in track order.</returns>
        private static double[] ScanLuminances(byte[] pixels, RectInt[] rects)
        {
            var luminances = new double[rects.Length];
            for (int i = 0; i < rects.Length; i++) luminances[i] = BoxLuminance(pixels, rects[i]);
            return luminances;
        }

        /// <summary>Colour variance inside the same rectangle <see cref="BoxLuminance"/> reads — the check
        /// that the box lies wholly on flat ground.</summary>
        /// <param name="pixels">RGBA32 frame.</param>
        /// <param name="rect">The rectangle to measure.</param>
        /// <returns>Summed per-channel variance.</returns>
        private static double BoxVariance(byte[] pixels, RectInt rect)
            => SnapshotCoverage.RegionColorVariance(
                pixels, SnapW, SnapH, rect.xMin, rect.yMin, rect.xMax, rect.yMax);

        /// <summary>
        /// Fraction of pixels in a rectangle whose brightest channel has reached the 8-bit ceiling. Mean
        /// luminance cannot answer "is this region clipping" — a box that is half 255 and half 200 averages
        /// to a comfortable-looking 0.89 — so the clipping verdict counts saturated pixels instead.
        /// </summary>
        /// <param name="pixels">RGBA32 frame from <see cref="SnapshotRenderer.RawPixels"/>.</param>
        /// <param name="rect">The rectangle to measure.</param>
        /// <returns>Fraction in [0,1]; 0 when the rectangle is empty.</returns>
        private static double BoxClippedFraction(byte[] pixels, RectInt rect)
        {
            int x0 = math.clamp(rect.xMin, 0, SnapW), x1 = math.clamp(rect.xMax, 0, SnapW);
            int y0 = math.clamp(rect.yMin, 0, SnapH), y1 = math.clamp(rect.yMax, 0, SnapH);

            int clipped = 0, count = 0;
            for (int y = y0; y < y1; y++)
            {
                int row = y * SnapW;
                for (int x = x0; x < x1; x++)
                {
                    int b = (row + x) * 4;
                    if (pixels[b] == 255 || pixels[b + 1] == 255 || pixels[b + 2] == 255) clipped++;
                    count++;
                }
            }

            return count == 0 ? 0.0 : (double)clipped / count;
        }

        /// <summary>Global shadow keyword state, as a log line. Meaningful only while a shadow-casting
        /// render is the most recent one — see <see cref="ShadowReading.ShadowKeywords"/>.</summary>
        /// <returns>The keyword states, formatted for the diagnostics log.</returns>
        private static string ReadShadowKeywords()
            => $"_MAIN_LIGHT_SHADOWS={Shader.IsKeywordEnabled("_MAIN_LIGHT_SHADOWS")} " +
               $"_MAIN_LIGHT_SHADOWS_CASCADE={Shader.IsKeywordEnabled("_MAIN_LIGHT_SHADOWS_CASCADE")} " +
               $"_MAIN_LIGHT_SHADOWS_SCREEN={Shader.IsKeywordEnabled("_MAIN_LIGHT_SHADOWS_SCREEN")} " +
               $"_SHADOWS_SOFT={Shader.IsKeywordEnabled("_SHADOWS_SOFT")}";

        /// <summary>
        /// The active render pipeline asset's shadow distance, read through
        /// <see cref="SerializedObject"/> because this assembly does not reference URP's runtime assembly.
        /// Logged rather than overridden: a map-scale rung is only meaningful against the distance the app
        /// actually ships, and writing to a committed asset from a test risks leaving it modified.
        /// </summary>
        /// <returns>The configured shadow distance in metres, or -1 when it cannot be read.</returns>
        private static float ReadPipelineShadowDistance()
        {
            RenderPipelineAsset pipeline = GraphicsSettings.currentRenderPipeline;
            if (pipeline == null) return -1f;
            var property = new SerializedObject(pipeline).FindProperty("m_ShadowDistance");
            return property != null ? property.floatValue : -1f;
        }

        /// <summary>
        /// Writes the active pipeline asset's shadow distance for the duration of a sweep row. The write is
        /// in-memory only — nothing here saves the asset — and every caller restores the value it replaced,
        /// so the maintainer's own uncommitted <c>RPAsset.asset</c> edit survives the run.
        /// </summary>
        /// <param name="metres">Shadow distance to install.</param>
        /// <returns>The value that was in place before the call, for the caller's restore.</returns>
        private static float SetPipelineShadowDistance(float metres)
        {
            RenderPipelineAsset pipeline = GraphicsSettings.currentRenderPipeline;
            Assert.IsNotNull(pipeline, "no scriptable render pipeline is active — shadow distance is a URP " +
                                       "asset field, so the sweep has nothing to vary.");

            var serialized = new SerializedObject(pipeline);
            SerializedProperty property = serialized.FindProperty("m_ShadowDistance");
            Assert.IsNotNull(property,
                "the active pipeline asset has no m_ShadowDistance field — the sweep cannot vary it.");

            float previous = property.floatValue;
            property.floatValue = metres;
            serialized.ApplyModifiedPropertiesWithoutUndo();
            return previous;
        }

        // ── Diagnostics ──────────────────────────────────────────────────────────────────────────

        /// <summary>
        /// Logs the state a red rung needs to be actionable: the active pipeline, the shadow keywords the
        /// pipeline left enabled after rendering, both materials' queue/blend/keyword state, and all four
        /// luminances. Emitted for passing rungs too — comparing a green rung's numbers against the red one
        /// below it is how the ladder localises the defect.
        /// </summary>
        /// <param name="label">Rung name.</param>
        /// <param name="groundMaterial">The receiver material.</param>
        /// <param name="cubeMaterial">The caster material.</param>
        /// <param name="reading">The rung's measurements.</param>
        private static void LogDiagnostics(
            string label, Material groundMaterial, Material cubeMaterial, in ShadowReading reading)
        {
            RenderPipelineAsset pipeline = GraphicsSettings.currentRenderPipeline;
            TestContext.WriteLine(
                $"[shadow-bisect {label}] pipeline='{(pipeline != null ? pipeline.name : "<none, built-in>")}' " +
                $"qualityLevel={QualitySettings.GetQualityLevel()} " +
                $"({QualitySettings.names[QualitySettings.GetQualityLevel()]})");
            TestContext.WriteLine(
                $"[shadow-bisect {label}] shadowDistance={ReadPipelineShadowDistance():F1} " +
                $"keywords during the shadows-ON render: {reading.ShadowKeywords}");
            LogMaterial(label, "receiver", groundMaterial);
            LogMaterial(label, "caster",   cubeMaterial);
            TestContext.WriteLine(
                $"[shadow-bisect {label}] luminance shadowBox on/off={reading.ShadowBoxOn:F4}/" +
                $"{reading.ShadowBoxOff:F4} litBox on/off={reading.LitBoxOn:F4}/{reading.LitBoxOff:F4} " +
                $"variance(off) shadowBox={reading.ShadowBoxVarianceOff:F6} litBox={reading.LitBoxVarianceOff:F6} " +
                $"filled={reading.OnVerdict.FilledFraction:F4} blank={reading.OnVerdict.IsBlank} " +
                $"uniform={reading.OnVerdict.IsUniform}");
            TestContext.WriteLine($"[shadow-bisect {label}] {ClippingVerdict(reading)}");
            TestContext.WriteLine(
                $"[shadow-bisect {label}] ambientMode={RenderSettings.ambientMode} " +
                $"ambientIntensity={RenderSettings.ambientIntensity:F2} " +
                $"skybox='{(RenderSettings.skybox != null ? RenderSettings.skybox.name : "<none>")}' " +
                $"probeIrradiance(up)={AmbientIrradianceUp():F4}");
        }

        /// <summary>
        /// The headline number, as a sentence: the shadow region's LIT luminance in both [0,1] and 8-bit
        /// terms, how much of it is on the ceiling, and the verdict that follows. Reported for every rung,
        /// because "the shadow has no headroom to darken into" and "the shadow is not applied" produce the
        /// same near-zero darkening and are told apart only here.
        /// </summary>
        /// <param name="reading">The rung's measurements.</param>
        /// <returns>The verdict line.</returns>
        private static string ClippingVerdict(in ShadowReading reading)
            => $"CLIPPING: the shadow region reads {reading.ShadowBoxOff:F4} " +
               $"({reading.ShadowBoxOff * 255.0:F1}/255) with shadows OFF, and " +
               $"{reading.ShadowBoxClippedFractionOff:P1} of it is on the 8-bit ceiling — " +
               (reading.ShadowBoxClippedFractionOff >= ClippedFractionThreshold
                   ? "CLIPPED: it cannot darken, so a correctly-computed shadow would still be invisible."
                   : "NOT clipped: there is headroom, so any missing darkening is a shadow that was not " +
                     "applied, not one that had nowhere to go.");

        /// <summary>
        /// Rec.709 luminance the ambient probe delivers to a ground-facing (up) normal. The ambient axis of
        /// the sweep is an INTENSITY multiplier, which says nothing about what it multiplies — so without
        /// this number a near-black probe and the app's own sky are indistinguishable in the table, and an
        /// earlier pass reported ambient rows measured against a probe roughly 5x too dark.
        /// </summary>
        /// <returns>Probe luminance for an up-facing normal.</returns>
        private static double AmbientIrradianceUp()
        {
            var directions = new[] { Vector3.up };
            var results    = new Color[1];
            RenderSettings.ambientProbe.Evaluate(directions, results);
            return 0.2126 * results[0].r + 0.7152 * results[0].g + 0.0722 * results[0].b;
        }

        /// <summary>Logs one material's shading-relevant state.</summary>
        /// <param name="label">Rung name.</param>
        /// <param name="role">"receiver" or "caster".</param>
        /// <param name="material">The material to describe.</param>
        private static void LogMaterial(string label, string role, Material material)
        {
            TestContext.WriteLine(
                $"[shadow-bisect {label}] {role} '{material.name}' shader='{material.shader.name}' " +
                $"queue={material.renderQueue} " +
                $"zwrite={SafeFloat(material, "_ZWrite")} " +
                $"src={SafeFloat(material, "_SrcBlend")} dst={SafeFloat(material, "_DstBlend")} " +
                $"receiveShadowsProp={SafeFloat(material, ShaderProperties.PropertyNames.ReceiveShadows)} " +
                $"keywords=[{string.Join(", ", material.shaderKeywords)}]");
        }

        /// <summary>Reads a float property, or reports its absence — a material built on a different shader
        /// need not declare every property this log names.</summary>
        /// <param name="material">Material to read.</param>
        /// <param name="propertyName">Shader property name.</param>
        /// <returns>The value, or "n/a".</returns>
        private static string SafeFloat(Material material, string propertyName)
            => material.HasProperty(propertyName)
                ? material.GetFloat(propertyName).ToString("F1")
                : "n/a";

        // ── Teardown ─────────────────────────────────────────────────────────────────────────────

        /// <summary>Destroys the given materials, ignoring nulls.</summary>
        /// <param name="materials">Materials to destroy.</param>
        private static void DestroyMaterials(params Material[] materials)
        {
            foreach (Material material in materials)
                if (material != null) UnityEngine.Object.DestroyImmediate(material);
        }

        /// <summary>Destroys the given objects along with the private mesh copies
        /// <see cref="BuildPrimitive"/> gave them, ignoring nulls.</summary>
        /// <param name="gameObjects">Objects to destroy.</param>
        private static void DestroyGameObjects(params GameObject[] gameObjects)
        {
            foreach (GameObject go in gameObjects)
            {
                if (go == null) continue;
                var meshFilter = go.GetComponent<MeshFilter>();
                if (meshFilter != null && meshFilter.sharedMesh != null)
                    UnityEngine.Object.DestroyImmediate(meshFilter.sharedMesh);
                UnityEngine.Object.DestroyImmediate(go);
            }
        }
    }
}
