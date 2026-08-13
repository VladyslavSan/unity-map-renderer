// T3 + T5 — the two pixel questions the tile-buffer clip stage exists to settle, measured on real pixels:
//   T3  does clipping remove the double-painted alpha BAND along a seam? (and does NOT-clipping still show it)
//   T5  does clipping at the tile boundary open a hairline CRACK in its place?
//
// Both render two adjacent tiles built through the REAL worker fan-out — TileLayerProcessorRunner.RunWorkerPass
// → TileMeshLayerProcessor → StyledFillTileBuilder — placed at their real TileRenderOrigin.Project origins, so
// the clip knob is exercised the whole way down the wiring, not injected at the builder.

using System.Collections.Generic;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.Rendering;
using Unity.Mathematics;
using MapRenderer.Core.Geo;
using MapRenderer.Core.Json;
using MapRenderer.Core.Lifetime;
using MapRenderer.Core.Rendering;
using MapRenderer.Core.Style;
using MapRenderer.Core.Tiles;
using MapRenderer.Jobs;
using MapRenderer.Unity.Rendering.Materials;
using MapRenderer.Unity.Rendering.Meshing;
using MapRenderer.Unity.Rendering.Style;
using MapRenderer.Unity.Rendering.Tile.Processing;
using MapRenderer.Jobs.Tiles;
using Fill = MapRenderer.Core.Style.Fill;
using IFeature = MapRenderer.Core.Expressions.IFeature; // aliased: a plain using would make
                                                        // 'Color' ambiguous with UnityEngine's

namespace MapRenderer.Tests.Visual
{
    [TestFixture]
    public class TileSeamSnapshotTests
    {
        private const int SnapW = 512;
        private const int SnapH = 512;

        private const double TileExtent = 4096.0;

        // The two neighbours: z1/x0/y0 and z1/x1/y0 abut along Mercator x = 0. z1 is the WORST case for the
        // RTC-baked float magnitudes T5 is about (a tile spans the half-world, ~2.0e7 m) — a crack that does
        // not open here does not open at z14 either.
        private static readonly TileId WestTile = new TileId { Z = 1, X = 0, Y = 0 };
        private static readonly TileId EastTile = new TileId { Z = 1, X = 1, Y = 0 };

        // The camera looks straight down at a point ON the shared seam, well inside both tiles vertically.
        private const float SeamWorldX = 0f;
        private const float SeamWorldZ = 1.0e7f;
        private const float CamY       = 1.0e6f;

        // The synthetic buffered feature: a closed ring (-64,-64) → (4160,-64) → (4160,4160) → (-64,4160) —
        // the exact 64-unit-buffered rectangle every real fixture produces. MoveTo×1 + LineTo×3 + ClosePath,
        // zigzag-encoded per the MVT spec, the same shape as TileBackgroundLayerProcessor's full-extent ring
        // (whose 8192/8191 are zigzag(±4096); these are zigzag(-64)=127 and zigzag(±4224)=8448/8447).
        private static readonly uint[] BufferedRingGeometry =
            { 9, 127, 127, 26, 8448, 0, 0, 8448, 8447, 0, 15 };

        // ── T3: the band ──────────────────────────────────────────────────────────────────────────

        // The seam strip and two interior reference strips, in pixels, at the T3 framing below. The strip is
        // narrower than the projected 128-px band so it never samples the band's edge; the references sit far
        // outside it. Rows are a central slab — every row is interior to BOTH tiles.
        private const int BandStripX0 = 236, BandStripX1 = 276;
        private const int LeftRefX0   =  40, LeftRefX1   = 120;
        private const int RightRefX0  = 392, RightRefX1  = 472;
        private const int SlabY0      = 200, SlabY1      = 312;

        // Half-width of the T3 view in world metres. The b=64 overlap strip is 2 × 64/4096 × tileSpan ≈
        // 626 km wide, which lands ~128 px across at this framing.
        private const float BandViewHalfWidth = 1.25e6f;

        [Test]
        public void TwoNeighbours_TranslucentFill_BandAtTheSeamIsPresentUnclipped_AndGoneWhenClipped()
        {
            // α = 0.5, never opaque: an OPAQUE fill composites identically in the double-painted strip, so this
            // tooth would be inert. 1 − (1−α)² = 0.75 against α = 0.5 ⇒ the strip reads 1.5× the interior.
            var (bufferedBand, bufferedLeft, bufferedRight) =
                MeasureSeamStrip(TileBufferClip.KeepTileUnits(64.0), alpha: 0.5f,
                                 BandViewHalfWidth, "tile-seam-band-b64.png");
            var (clippedBand, clippedLeft, clippedRight) =
                MeasureSeamStrip(TileBufferClip.KeepTileUnits(0.0), alpha: 0.5f,
                                 BandViewHalfWidth, "tile-seam-band-b0.png");

            double bufferedInterior = 0.5 * (bufferedLeft + bufferedRight);
            double clippedInterior  = 0.5 * (clippedLeft + clippedRight);

            Debug.Log($"[TileSeam T3] b=64: seam={bufferedBand:F4} interior={bufferedInterior:F4} " +
                      $"ratio={bufferedBand / bufferedInterior:F4} | " +
                      $"b=0: seam={clippedBand:F4} interior={clippedInterior:F4} " +
                      $"ratio={clippedBand / clippedInterior:F4}");

            // `||`, not `&&`: if only ONE arm fails to render, an `&&` guard does not fire and the ratio
            // below divides by ~0, asserting on a NaN with a message that blames the clip.
            if (bufferedInterior < 0.01 || clippedInterior < 0.01)
            {
                Assert.Inconclusive(
                    "Both interior references are ~black — the tiles did not render. Likely no GPU context " +
                    "in batch EditMode; re-run as PlayMode: ./Tools/run-tests.sh PlayMode");
                return;
            }

            // Arm 1 — the RED arm, asserted in the SAME test so the tooth cannot go vacuous: unclipped, the
            // two neighbours double-paint the buffer strip. In linear light the ratio is exactly 2 − α = 1.5:
            // one paint gives α·C, two give α·C + (1−α)·α·C.
            Assert.Greater(bufferedBand / bufferedInterior, 1.4,
                $"unclipped (b=64) the seam strip must read ~1.5× the interior in linear light (double-painted " +
                $"α=0.5 composites to 0.75, not 0.5). Measured seam={bufferedBand:F4}, " +
                $"interior={bufferedInterior:F4}. If this is ~1.0 the band is not being reproduced and arm 2 " +
                "proves nothing.");

            // Arm 2 — clipped at the tile boundary, the two tiles no longer overlap and the strip matches the
            // interior it sits between. Stated as a RATIO so it is scale-free: an absolute tolerance would
            // silently loosen as the fill colour darkened.
            Assert.LessOrEqual(math.abs(clippedBand / clippedInterior - 1.0), 0.02,
                $"clipped (b=0) the seam strip must read the same as the interior. Measured " +
                $"seam={clippedBand:F4}, interior={clippedInterior:F4} " +
                $"(ratio={clippedBand / clippedInterior:F4}).");
        }

        // ── T5: the crack ─────────────────────────────────────────────────────────────────────────

        // Columns scanned for a background-coloured gap, centred on the seam (world x = 0 projects to the
        // middle column at both framings below).
        private const int CrackScanX0 = 246, CrackScanX1 = 266;

        [Test]
        public void TwoNeighbours_OpaqueFill_ClippedAtTheTileBoundary_LeaveNoCrack()
        {
            // Two altitudes: most of the seam in view, and zoomed so one tile spans ~4× the viewport.
            AssertNoCrackAtAltitude(8.0e6f, "tile-seam-crack-wide.png");
            AssertNoCrackAtAltitude(2.5e6f, "tile-seam-crack-zoomed.png");
        }

        private static void AssertNoCrackAtAltitude(float viewHalfWidth, string pngName)
        {
            var scene = new SeamScene(TileBufferClip.KeepTileUnits(0.0), alpha: 1f,
                                      background: CrackBackground, viewHalfWidth: viewHalfWidth);
            try
            {
                byte[] px = scene.Render(pngName);
                if (px == null)
                {
                    Assert.Inconclusive("No GPU context in batch EditMode; re-run as PlayMode.");
                    return;
                }

                int backgroundPixels = 0;
                int longestRun       = 0;
                for (int y = SlabY0; y < SlabY1; y++)
                {
                    int run = 0;
                    for (int x = CrackScanX0; x < CrackScanX1; x++)
                    {
                        if (IsCrackBackground(px, x, y))
                        {
                            backgroundPixels++;
                            run++;
                            longestRun = math.max(longestRun, run);
                        }
                        else run = 0;
                    }
                }

                double scanLuminance = MeanLuminance(px, CrackScanX0, CrackScanX1);
                Debug.Log($"[TileSeam T5] viewHalfWidth={viewHalfWidth:F0} m " +
                          $"({viewHalfWidth * 2.0 / SnapW:F0} m/px): background pixels on the seam = " +
                          $"{backgroundPixels}, longest run = {longestRun} px, " +
                          $"scan mean luminance = {scanLuminance:F4}");

                // Non-vacuity: "no background-coloured pixel" is trivially true over an unrendered (black)
                // frame, which is neither fill nor magenta. The seam band must actually be covered by fill.
                Assert.Greater(scanLuminance, 0.05,
                    $"the scanned seam band is nearly black (mean luminance {scanLuminance:F4}) — the tiles " +
                    "did not render there, so a zero crack count would prove nothing.");

                Assert.AreEqual(0, backgroundPixels,
                    $"clipping at the tile boundary opened a crack: {backgroundPixels} background-coloured " +
                    $"pixels on the seam (longest run {longestRun} px) at viewHalfWidth={viewHalfWidth:F0} m. " +
                    "The default must then be the smallest margin that closes it — record the width.");
            }
            finally { scene.Dispose(); }
        }

        // ── Measurement ───────────────────────────────────────────────────────────────────────────

        private static readonly Color BandBackground  = new Color(0f, 0f, 0f, 1f);
        private static readonly Color CrackBackground = new Color(1f, 0f, 1f, 1f); // magenta: nothing else is

        /// <summary>Magenta-dominant: red and blue high, green far below them. Stated as a RATIO rather than
        /// absolute thresholds so a linear-vs-sRGB readback cannot flip it. The fill is neutral grey
        /// (R ≈ G ≈ B), so it can never satisfy <c>G × 3 &lt; R</c>.</summary>
        private static bool IsCrackBackground(byte[] px, int x, int y)
        {
            int b = (y * SnapW + x) * 4;
            return px[b] > 100 && px[b + 2] > 100 && px[b + 1] * 3 < px[b];
        }

        /// <summary>Mean luminance of the seam strip and of the two interior reference strips.</summary>
        private static (double band, double left, double right) MeasureSeamStrip(
            TileBufferClip clip, float alpha, float viewHalfWidth, string pngName)
        {
            var scene = new SeamScene(clip, alpha, BandBackground, viewHalfWidth);
            try
            {
                byte[] px = scene.Render(pngName);
                if (px == null) return (0.0, 0.0, 0.0);
                return (MeanLuminance(px, BandStripX0, BandStripX1),
                        MeanLuminance(px, LeftRefX0,   LeftRefX1),
                        MeanLuminance(px, RightRefX0,  RightRefX1));
            }
            finally { scene.Dispose(); }
        }

        /// <summary>
        /// Mean Rec.709 luminance in <b>LINEAR</b> light. The readback is sRGB-ENCODED, and the whole point of
        /// this tooth is an alpha-compositing ratio — which is a linear-light quantity. Measured on raw bytes a
        /// true 1.5× overdraw reads as only 1.5^(1/2.4) ≈ 1.18×, so an encoded-space threshold would be
        /// calibrating around the gamma curve instead of measuring the artefact.
        /// </summary>
        private static double MeanLuminance(byte[] px, int x0, int x1)
        {
            double sum = 0.0;
            int count  = 0;
            for (int y = SlabY0; y < SlabY1; y++)
            {
                for (int x = x0; x < x1; x++)
                {
                    int b = (y * SnapW + x) * 4;
                    sum += 0.2126 * SrgbToLinear(px[b])
                         + 0.7152 * SrgbToLinear(px[b + 1])
                         + 0.0722 * SrgbToLinear(px[b + 2]);
                    count++;
                }
            }
            return count > 0 ? sum / count : 0.0;
        }

        private static double SrgbToLinear(byte channel)
        {
            double c = channel / 255.0;
            return c <= 0.04045 ? c / 12.92 : math.pow((c + 0.055) / 1.055, 2.4);
        }

        // ── The scene ─────────────────────────────────────────────────────────────────────────────

        /// <summary>Two neighbouring tiles built through the real worker fan-out under one clip setting, a
        /// top-down ortho camera on their shared seam, and the off-screen render target.</summary>
        private sealed class SeamScene : System.IDisposable
        {
            private readonly GameObject       _sceneGo;
            private readonly GameObject       _cameraGo;
            private readonly Camera           _camera;
            private readonly SnapshotRenderer _snap;
            private readonly List<Object>     _disposables = new List<Object>();

            private readonly int          _prevQuality;
            private readonly AmbientMode  _prevAmbientMode;
            private readonly Color        _prevAmbientLight;

            public SeamScene(TileBufferClip clip, float alpha, Color background, float viewHalfWidth)
            {
                _prevQuality      = QualitySettings.GetQualityLevel();
                _prevAmbientMode  = RenderSettings.ambientMode;
                _prevAmbientLight = RenderSettings.ambientLight;
                QualitySettings.SetQualityLevel(0, false);
                RenderSettings.ambientMode  = AmbientMode.Flat;
                RenderSettings.ambientLight = new Color(0.9f, 0.9f, 0.9f, 1f);

                _sceneGo = new GameObject("TileSeamScene");

                var lightGo = new GameObject("DirLight");
                lightGo.transform.SetParent(_sceneGo.transform);
                lightGo.transform.rotation = Quaternion.Euler(90f, 0f, 0f); // straight down: both tiles shade alike
                var light = lightGo.AddComponent<Light>();
                light.type      = LightType.Directional;
                light.intensity = 1f;

                // A mid-grey base keeps the double-painted strip well clear of saturation, which would flatten
                // the 1.5× ratio T3 measures.
                var material = MaterialFactory.CreateFillMaterial(MapMaterialSetTestUtil.Load());
                material.SetColor("_BaseColor", new Color(0.35f, 0.35f, 0.35f, 1f));
                material.SetFloat("_Opacity", alpha);
                // Both tiles are ONE style layer, so they share a queue; the band comes from two draws of the
                // same layer overlapping, not from layer order.
                material.renderQueue = LayerDrawOrder.ComputeQueues(1)[0];
                _disposables.Add(material);

                var projection = new WebMercatorProjection();
                AddTile(WestTile, clip, projection, material);
                AddTile(EastTile, clip, projection, material);

                _cameraGo = new GameObject("TileSeamCamera");
                _camera   = _cameraGo.AddComponent<Camera>();
                _camera.transform.position = new Vector3(SeamWorldX, CamY, SeamWorldZ);
                _camera.transform.rotation = Quaternion.Euler(90f, 0f, 0f);
                _camera.orthographic       = true;
                _camera.orthographicSize   = viewHalfWidth; // square target ⇒ half-width == half-height
                _camera.nearClipPlane      = 1f;
                _camera.farClipPlane       = CamY * 4f;
                _camera.clearFlags         = CameraClearFlags.SolidColor;
                _camera.backgroundColor    = background;
                _camera.enabled            = false;

                _snap = new SnapshotRenderer(SnapW, SnapH);
            }

            /// <summary>Renders and returns the RGBA32 buffer, or null when there is no GPU context.</summary>
            public byte[] Render(string pngName)
            {
                _snap.Render(_camera);
                _snap.WritePng(pngName);
                return _snap.IsAllBlack() ? null : _snap.RawPixels;
            }

            private void AddTile(TileId id, TileBufferClip clip, IProjection projection, Material material)
            {
                double3 origin = TileRenderOrigin.Project(id, projection);
                Mesh mesh = BuildTileMesh(id, origin, clip, projection);
                Assert.IsNotNull(mesh, $"tile {id.Z}/{id.X}/{id.Y} produced no fill mesh.");
                _disposables.Add(mesh);

                var go = new GameObject($"Tile_{id.Z}_{id.X}_{id.Y}");
                go.transform.SetParent(_sceneGo.transform, worldPositionStays: false);
                // The RTC bake origin IS the tile placement — the same double3 the production tile transform
                // uses; cast to float only here, at the Unity boundary.
                go.transform.localPosition = new Vector3((float)origin.x, (float)origin.y, (float)origin.z);
                go.AddComponent<MeshFilter>().sharedMesh    = mesh;
                go.AddComponent<MeshRenderer>().sharedMaterial = material;
            }

            public void Dispose()
            {
                _snap?.Dispose();
                foreach (var d in _disposables) if (d != null) Object.DestroyImmediate(d);
                if (_sceneGo  != null) Object.DestroyImmediate(_sceneGo);
                if (_cameraGo != null) Object.DestroyImmediate(_cameraGo);
                QualitySettings.SetQualityLevel(_prevQuality, false);
                RenderSettings.ambientMode  = _prevAmbientMode;
                RenderSettings.ambientLight = _prevAmbientLight;
            }
        }

        /// <summary>Builds one tile's fill mesh through the production worker fan-out, so the clip travels the
        /// real <see cref="TileLayerProcessContext"/> → <see cref="TileMeshLayerProcessor"/> →
        /// <see cref="ITileMeshRenderLayer.WriteInto"/> path rather than being handed to the builder.</summary>
        private static Mesh BuildTileMesh(TileId id, double3 origin, TileBufferClip clip, IProjection projection)
        {
            var feature = new InMemoryTileFeature
            {
                GeometryType = TileGeometryType.Polygon,
                Geometry     = BufferedRingGeometry,
            };
            // IR C1 P3: the synthetic layer owns its buffer, materialized at construction like a decoded
            // one — and stamped with the SAME id the context builds at, which is now the only copy.
            using var seamTile = new InMemoryDecodedTile(
                new InMemoryTileLayer(SeamSourceLayerName, id, new IFeature[] { feature }, (uint)TileExtent));

            var styleLayer = new StyleLayer { Id = "seam-fill", SourceLayer = SeamSourceLayerName };
            var paint      = new Fill.PaintProperties(JsonParser.Parse("{\"fill-color\":\"#ffffff\"}"));
            var fillLayer  = new SeamFillRenderLayer(styleLayer, paint);

            var context = new TileLayerProcessContext
            {
                Tile             = id,
                Zoom             = id.Z,
                TileOriginRender = origin,
                Projection       = projection,
                BufferClip       = clip,
            };

            var processor = TileMeshLayerProcessor.AllocateForKick(fillLayer, materialIndex: 0);
            var decode    = new SharedDisposable<IDecodedTile>(new SeamTileDecoder(seamTile).Decode(id, SeamDecoderBytes));

            IRenderLayerPayload[] payloads;
            try
            {
                payloads = TileLayerProcessorRunner.RunWorkerPass(
                    decode, in context, new ITileMeshLayerProcessor[] { processor });
            }
            finally { decode.Release(); }

            Assert.AreEqual(1, payloads.Length);
            return payloads[0].Upload();
        }

        // ── Fakes (mirroring A6NonMvtDecoderTests' fan-out fixtures) ──────────────────────────────

        private const string SeamSourceLayerName = "seam-fixture-layer";

        // Never decoded: SeamTileDecoder ignores the bytes and returns the synthetic tile.
        private static readonly byte[] SeamDecoderBytes = { 0x1A, 0x64 };

        private sealed class SeamTileDecoder : ITileDecoder
        {
            private readonly IDecodedTile _tile;
            public SeamTileDecoder(IDecodedTile tile) => _tile = tile;
            public IDecodedTile Decode(TileId id, byte[] bytes) => _tile;
        }

        /// <summary>Mirrors <c>FillRenderLayer.WriteInto</c>'s forward without needing a real Material —
        /// this scene binds its own.</summary>
        private sealed class SeamFillRenderLayer : ITileMeshRenderLayer
        {
            private readonly Fill.PaintProperties _paint;
            public StyleLayer StyleLayer { get; }
            public RenderLayerBuild Build => RenderLayerBuild.TileMesh;
            public DrawPersistence Persistence => DrawPersistence.Persistent;
            public int DrawIndex => 0;
            public LayerSubSlot MaterialSubSlot => LayerSubSlot.Base;
            public Material Material => null;

            public SeamFillRenderLayer(StyleLayer styleLayer, Fill.PaintProperties paint)
            {
                StyleLayer = styleLayer;
                _paint     = paint;
            }

            public void ApplyZoom(double zoom, double devicePixelRatio) { }
            public void Dispose() { }

            public void WriteInto(
                Mesh.MeshData md, IReadOnlyList<SelectedTileFeature> selected, TileGeometryBuffers geometry,
                double zoom, double3 tileOriginRender, IProjection projection, TileBufferClip clip,
                out int vertexCount, out Bounds bounds)
                => StyledFillTileBuilder.WriteMeshData(
                    md, selected, geometry, _paint, zoom, tileOriginRender, out vertexCount, out bounds,
                    projection, layout: null, clip: clip);
        }
    }
}
