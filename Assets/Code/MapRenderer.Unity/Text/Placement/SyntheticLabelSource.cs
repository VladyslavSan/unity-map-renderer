// Namespace-collision guard (see GlyphAtlasTexture.cs's header comment): this file lives in
// MapRenderer.Unity.Text.Placement and uses Unity.Mathematics.double3 — TOP-LEVEL `using Unity.Mathematics;`
// + unqualified double3, NEVER an inline `Unity.Mathematics.double3`.

using System.Collections.Generic;
using Unity.Mathematics;
using UnityEngine;
using MapRenderer.Core.Geo;
using MapRenderer.Core.Text;
using MapRenderer.Core.Text.Placement;
using MapRenderer.Unity.Rendering.Map;

namespace MapRenderer.Unity.Text.Placement
{
    /// <summary>
    /// S20 Slice 1 EYEBALL DEMO — hand-builds a handful of <see cref="LabelInstance"/>s (real strings,
    /// shaped via <see cref="CodepointTextShaper"/> → <see cref="TextQuadLayout"/>) anchored near the
    /// map's current look-at, using a REAL SDF atlas (<see cref="GlyphManager"/> over the committed
    /// <see cref="FixtureGlyphSource"/> fixture — no network, no style parse). Feeds them into
    /// <see cref="MapView.LabelInstances"/>/<see cref="MapView.LabelAtlas"/>, the demo-only seam S20
    /// Slice 1 reserved on <see cref="MapView"/> pending S105's real feature→label data flow.
    ///
    /// <para><b>Not production</b> — this is scaffolding for the maintainer's visual acceptance (stage
    /// doc §4 Bucket B). Drop this component on any <see cref="GameObject"/> in a wired map scene (e.g.
    /// the existing <c>MapDemo</c> scene's <c>MapRoot</c>) and press Play; it self-wires to the first
    /// <see cref="MapViewComponent"/> it finds (mirrors <c>TileLoadStressDriver</c>'s self-wiring
    /// convention). <see cref="DefaultExecutionOrderAttribute"/> pushes this component's <c>Start</c>
    /// after the default order (0), so it reliably runs after <c>Bootstrapper.Start</c> has wired the
    /// camera regardless of GameObject/script ordering in the scene.</para>
    /// </summary>
    [DefaultExecutionOrder(1000)]
    public sealed class SyntheticLabelSource : MonoBehaviour
    {
        [Tooltip("The MapViewComponent to attach labels to. Leave empty -- found automatically on Start " +
                 "(FindAnyObjectByType), so this component works on any GameObject in a wired scene.")]
        public MapViewComponent Map;

        [Tooltip("Label strings. Each is anchored near the map's current look-at, offset by the matching " +
                 "index in NorthOffsetDegrees/EastOffsetDegrees (missing offsets default to 0).")]
        public string[] Texts = { "Berlin", "Hello Map", "MapRenderer" };

        [Tooltip("Per-label north offset (decimal degrees latitude) from the look-at, matched to Texts by index.")]
        public double[] NorthOffsetDegrees = { 0.01, -0.01, 0.0 };

        [Tooltip("Per-label east offset (decimal degrees longitude) from the look-at, matched to Texts by index.")]
        public double[] EastOffsetDegrees = { -0.01, 0.01, 0.02 };

        [Tooltip("text-size in logical pixels (TextQuadLayout.OneEm = 24 baked px is the neutral scale).")]
        public float TextSizePx = 32f;

        [Tooltip("text-padding in logical pixels — grows each label's Slice-2 collision box on every edge.")]
        public float PaddingPx = 2f;

        [Tooltip("Slice-2 collision demo: strings stacked at the SAME look-at anchor with ASCENDING " +
                 "sort keys (index 0 = lowest = wins). Collision should leave ONLY the first one visible. " +
                 "Empty = no cluster.")]
        public string[] CollisionClusterTexts = { "WINNER", "loser-a", "loser-b" };

        [Tooltip("The bundled fixture font-stack directory name under Assets/Fixtures/glyphs/ " +
                 "(FixtureGlyphSource reads <name>/<rangeStart>-<rangeStart+255>.pbf.bytes from it).")]
        public string FontStackName = "NotoSansRegular";

        private GlyphManager _glyphManager;
        private GlyphAtlasTexture _atlasTexture;

        private async void Start()
        {
            if (Map == null) Map = FindAnyObjectByType<MapViewComponent>();
            if (Map == null || Map.Camera == null)
            {
                Debug.LogWarning("[SyntheticLabelSource] No wired MapViewComponent found (is Bootstrapper " +
                                 "on this GameObject, and has it run yet?) -- no labels will be built.");
                return;
            }

            _glyphManager = new GlyphManager(new FixtureGlyphSource());
            _atlasTexture = new GlyphAtlasTexture();

            var fontStack = new FontStack { Names = new[] { FontStackName } };
            var resolver = _glyphManager.CreateResolver(fontStack);
            var shaper = new CodepointTextShaper();

            var labels = new List<LabelInstance>();
            int count = Texts?.Length ?? 0;
            for (int i = 0; i < count; i++)
            {
                string text = Texts[i];
                if (string.IsNullOrEmpty(text)) continue;

                // Ensure every codepoint's 256-range is fetched/decoded/appended to the shared atlas
                // BEFORE shaping/layout reads it (both need atlas entries to resolve advances/metrics).
                foreach (char c in text)
                {
                    await _glyphManager.EnsureFontStackRangeAsync(fontStack, c);
                }

                ShapedRun run = shaper.Shape(new ShapingRequest { Text = text, FontStack = fontStack, Metrics = resolver });
                TextLayoutResult layout = TextQuadLayout.Layout(run, _glyphManager.Atlas, TextLayoutOptions.Default);

                double north = i < NorthOffsetDegrees.Length ? NorthOffsetDegrees[i] : 0.0;
                double east = i < EastOffsetDegrees.Length ? EastOffsetDegrees[i] : 0.0;

                GeoCoordinate3D lookAt = Map.Camera.CurrentProperties.LookAt;
                var anchorGeo = new GeoCoordinate { Latitude = lookAt.Latitude + north, Longitude = lookAt.Longitude + east };
                double3 anchorRender = Map.Camera.Projection.Project(anchorGeo);

                labels.Add(new LabelInstance
                {
                    AnchorRender = anchorRender,
                    Layout = layout,
                    Paint = LabelPaint.Default,
                    TextSizePx = TextSizePx,
                    PaddingPx = PaddingPx,
                    SortKey = 0f,
                    FeatureIndex = i,
                    TileKey = 0L,
                });
            }

            // Slice-2 collision demo: several strings stacked at the SAME anchor (the look-at) with
            // ASCENDING sort keys. Greedy placement keeps only the lowest-key one ("WINNER"); the rest
            // collide with it and are culled -- the maintainer's Bucket-B "no overlaps" eyeball check.
            int clusterCount = CollisionClusterTexts?.Length ?? 0;
            for (int c = 0; c < clusterCount; c++)
            {
                string text = CollisionClusterTexts[c];
                if (string.IsNullOrEmpty(text)) continue;

                foreach (char ch in text)
                {
                    await _glyphManager.EnsureFontStackRangeAsync(fontStack, ch);
                }

                ShapedRun run = shaper.Shape(new ShapingRequest { Text = text, FontStack = fontStack, Metrics = resolver });
                TextLayoutResult layout = TextQuadLayout.Layout(run, _glyphManager.Atlas, TextLayoutOptions.Default);

                GeoCoordinate3D lookAt = Map.Camera.CurrentProperties.LookAt;
                var anchorGeo = new GeoCoordinate { Latitude = lookAt.Latitude, Longitude = lookAt.Longitude };
                double3 anchorRender = Map.Camera.Projection.Project(anchorGeo);

                labels.Add(new LabelInstance
                {
                    AnchorRender = anchorRender,
                    Layout = layout,
                    Paint = LabelPaint.Default,
                    TextSizePx = TextSizePx,
                    PaddingPx = PaddingPx,
                    SortKey = c, // ascending: index 0 wins
                    FeatureIndex = 1000 + c,
                    TileKey = 0L,
                });
            }

            // Upload once after every label's glyphs have been appended -- one texture upload, not one per label.
            _atlasTexture.Upload(_glyphManager.Atlas);

            Map.View.LabelInstances = labels;
            Map.View.LabelAtlas = _atlasTexture;

            Debug.Log($"[SyntheticLabelSource] Built {labels.Count} synthetic label(s) over a real " +
                      $"{_glyphManager.Atlas.Size.x}x{_glyphManager.Atlas.Size.y}px SDF atlas.");
        }

        private void OnDestroy()
        {
            _atlasTexture?.Dispose();
            _glyphManager?.Dispose();
        }
    }
}
