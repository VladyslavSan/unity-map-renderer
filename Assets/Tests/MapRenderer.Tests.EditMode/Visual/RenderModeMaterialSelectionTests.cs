// Unity EditMode only — uses MonoBehaviour + per-layer material inspection (mirrors MapViewStyledFillTests).
//
// S4 (unlit epic) acceptance: render mode is a property of the MapMaterialSet the config references (the
// reshape of D1 = Option B — a Lit set puts the view in lit, an Unlit set in unlit; there is no separate
// config flag and no per-material mix). This end-to-end tooth proves the unlit shader twin actually flows
// through the material pipeline: the material MapView resolves for a fill layer carries the shader of the
// set's FillMaterial base — Map/Fill from a lit set, Map/FillUnlit from an unlit set.
//
// Drives the REAL production entry point, MapView.SetStyle (not the LoadTestStyle test helper). A
// tiles[]-only vector source needs no TileJSON fetch, so SetStyle never actually awaits network — the
// TileSourceFactoryOverride seam supplies the fixture bytes when SetSources lazily constructs the source.

using System.Threading;
using Cysharp.Threading.Tasks;
using NUnit.Framework;
using UnityEngine;
using MapRenderer.Core.Style;
using MapRenderer.Unity.Rendering.Materials;
using MapRenderer.Unity.Rendering.Tile;
using MapView = MapRenderer.Unity.Rendering.Map.MapViewComponent;
using RenderMode = MapRenderer.Unity.Rendering.Materials.RenderMode;

namespace MapRenderer.Tests.Visual
{
    /// <summary>
    /// S4 (unlit epic) — pins that the fill layer's material is cloned from whichever
    /// <see cref="MapMaterialSet"/> the config references, so an unlit set's <c>Map/FillUnlit</c> base
    /// reaches the rendered layer (the twin is wired end-to-end), and a lit set's <c>Map/Fill</c> base does
    /// under lit. Render mode itself lives on the set (<see cref="MapMaterialSet.RenderMode"/>) and drives
    /// the lighting bootstrap — see <c>DirectionalLightBootstrapTests</c>/<c>EnvironmentLightingTests</c>.
    /// </summary>
    [TestFixture]
    public class RenderModeMaterialSelectionTests
    {
        private static StyleDocument OneFillLayerStyle() => StyleParser.Parse(@"{
            ""version"": 8,
            ""name"": ""OneFill"",
            ""sources"": {
                ""maplibre"": { ""type"": ""vector"", ""tiles"": [""https://example.invalid/{z}/{x}/{y}.pbf""] }
            },
            ""layers"": [
                {
                    ""id"": ""fill-a"",
                    ""type"": ""fill"",
                    ""source"": ""maplibre"",
                    ""source-layer"": ""countries"",
                    ""paint"": { ""fill-color"": [""rgba"", 255, 0, 0, 1] }
                }
            ]
        }");

        [Test]
        public void FillLayer_FromLitMaterialSet_UsesLitShader()
            => AssertFillLayerShader(RenderMode.Lit, "Map/Fill");

        [Test]
        public void FillLayer_FromUnlitMaterialSet_UsesUnlitShader()
            => AssertFillLayerShader(RenderMode.Unlit, "Map/FillUnlit");

        private static void AssertFillLayerShader(RenderMode mode, string expectedShaderName)
        {
            var litSet = MapMaterialSetTestUtil.Load();

            // Under Unlit, build a throwaway set (never the shared production asset — same posture as
            // MapMaterialSetValidationTests.NewSet) whose RenderMode is Unlit and whose FillMaterial is the
            // real Map/FillUnlit twin (landed by S1). Line/Symbol bases reuse the lit ones — Validate()
            // (called inside SetStyle) requires all three regardless of mode, and symbols are already unlit
            // / out of this epic's scope. Under Lit, the shared production set already carries Map/Fill and
            // its default RenderMode.Lit.
            var isUnlit  = mode == RenderMode.Unlit;
            var unlitSet = isUnlit ? ScriptableObject.CreateInstance<MapMaterialSet>() : null;
            if (isUnlit)
            {
                unlitSet.RenderMode      = RenderMode.Unlit;
                unlitSet.FillMaterial    = new Material(Shader.Find("Map/FillUnlit"));
                unlitSet.LineMaterial    = litSet.LineMaterial;
                unlitSet.SymbolTextWorld = litSet.SymbolTextWorld;
            }
            MapMaterialSet set = isUnlit ? unlitSet : litSet;

            byte[] fixtureBytes = SampleTileFixture.Bytes();
            var go   = new GameObject("MapView");
            var view = go.AddComponent<MapView>();
            view.Config.MaterialSet = set;
            view.Config.TileSelection.MinZoom = 0;
            view.Config.TileSelection.MaxZoom = 0;
            view.WithTestCamera();
            // Never actually dials the network — see file header.
            view.View.TileSourceFactoryOverride = _ => TestDataSource.FromBytes(fixtureBytes);

            try
            {
                Await(view.View.SetStyle(OneFillLayerStyle(), "test-style", CancellationToken.None));

                Assert.AreEqual(1, view.FillLayerCount(), "The style declares exactly one fill layer.");
                Material mat = view.Layers[0].Material;
                Assert.IsNotNull(mat, "The fill layer must have resolved a material.");
                Assert.AreEqual(expectedShaderName, mat.shader.name,
                    $"A {mode} MapMaterialSet must resolve the fill layer's material onto shader " +
                    $"'{expectedShaderName}', not '{mat.shader.name}' — the fill layer is a clone of the " +
                    "referenced set's FillMaterial base, so the unlit twin must reach the rendered layer.");
            }
            finally
            {
                view.Teardown(); // dispose the backend world/BRG (OnDestroy does not fire on DestroyImmediate)
                UnityEngine.Object.DestroyImmediate(go);
                if (isUnlit)
                {
                    UnityEngine.Object.DestroyImmediate(unlitSet.FillMaterial);
                    UnityEngine.Object.DestroyImmediate(unlitSet);
                }
            }
        }

        /// <summary>Blocking, main-thread-only wait for a <see cref="UniTask"/> (mirrors
        /// <c>GeoJsonSourceTests.SpinToCompleted</c>) — parks the calling thread rather than yielding the
        /// continuation to whatever thread completes the task, so every line after this still runs on the
        /// main thread (DestroyImmediate requires it).</summary>
        private static void Await(UniTask task, int timeoutMs = 20000)
        {
            var t = task.Preserve();
            t.WaitOffPlayerLoop(timeoutMs);
            t.GetAwaiter().GetResult();
        }
    }
}
