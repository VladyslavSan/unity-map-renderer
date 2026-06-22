using System;
using System.Collections.Generic;
using UnityEngine;
using MapRenderer.Core.Style;
using MapRenderer.Core.Rendering;
using MapRenderer.Core.View.Camera;

namespace MapRenderer.Unity
{
    /// <summary>
    /// The set of per-style-layer render bundles (fills + lines), built once from a
    /// <see cref="StyleDocument"/> and kept current per frame. One bundle per fill/line layer holds the
    /// parsed paint, the source <see cref="StyleLayer"/> (for feature selection), a GPU <see cref="Material"/>
    /// whose <c>renderQueue</c> encodes the layer's place in the draw order, and a <see cref="ZoomStyleApplier"/>
    /// that pushes zoom-dependent uniforms each frame.
    ///
    /// <para>Step 2 of the MapView decomposition: MapView owns the tile loop; this owns
    /// "style → GPU layers, and keep them current". Records are built ONCE (not per tile); each tile produces
    /// a mesh per layer and draws it with the matching bundle's material. In the eventual DOTS shape this is
    /// the "bake + update styled layers" system.</para>
    /// </summary>
    internal sealed class StyledLayerSet : IDisposable
    {
        /// <summary>One render bundle per fill style layer, in declared order.</summary>
        public struct FillLayerRecord
        {
            public FillPaint        Paint;       // parsed fill paint
            public StyleLayer       StyleLayer;  // source-layer + filter, for FeatureSelector
            public Material         Material;    // renderQueue = TransparentQueue + styleIndex
            public ZoomStyleApplier Applier;     // pushes zoom-dependent uniforms
        }

        /// <summary>One render bundle per line style layer, in declared order.</summary>
        public struct LineLayerRecord
        {
            public LinePaint        Paint;
            public StyleLayer       StyleLayer;
            public Material         Material;
            public ZoomStyleApplier Applier;
        }

        private readonly List<FillLayerRecord> _fills = new List<FillLayerRecord>(16);
        private readonly List<LineLayerRecord> _lines = new List<LineLayerRecord>(16);

        public int FillCount => _fills.Count;
        public int LineCount => _lines.Count;

        public IReadOnlyList<FillLayerRecord> Fills => _fills;
        public IReadOnlyList<LineLayerRecord> Lines => _lines;

        /// <summary>Snapshot copies for the background tessellation task (so the lists can't mutate mid-flight).</summary>
        public FillLayerRecord[] SnapshotFills() => _fills.ToArray();
        public LineLayerRecord[] SnapshotLines() => _lines.ToArray();

        /// <summary>
        /// Builds the fill + line bundles from <paramref name="style"/>. Draw order IS the style's declared
        /// layer order (MapLibre painter's algorithm): walk <c>style.Layers</c> ONCE and assign a single
        /// monotonic render queue across fills AND lines by their index in that list, so an interleaved
        /// fill-over-line or line-over-fill composites exactly as declared. Fills and lines share one
        /// transparent band (ZWrite off), so the renderQueue offset alone decides order — see
        /// <see cref="LayerDrawOrder"/>. Disposes any previously-built bundles first.
        /// </summary>
        public void Build(StyleDocument style, double initialZoom, MapMaterialSet settings = null)
        {
            Dispose();
            if (style == null) return;

            int drawIndex = 0;
            foreach (var sl in style.Layers)
            {
                if (sl.LayerType == StyleLayerType.Fill)
                {
                    FillPaint paint = new FillPaint(sl);
                    Material mat = MaterialFactory.CreateFillMaterial(settings);
                    if (mat == null) continue;   // unconfigured material set — warned by the factory; skip the layer
                    mat.renderQueue = LayerDrawOrder.TransparentQueue + drawIndex;

                    var applier = new ZoomStyleApplier(mat);
                    MaterialFactory.BindFillPaintToApplier(paint, applier, mat);
                    applier.ApplyZoom(initialZoom);

                    _fills.Add(new FillLayerRecord
                    {
                        Paint = paint, StyleLayer = sl, Material = mat, Applier = applier,
                    });
                    drawIndex++;
                }
                else if (sl.LayerType == StyleLayerType.Line)
                {
                    LinePaint paint = new LinePaint(sl);
                    Material mat = MaterialFactory.CreateLineMaterial(settings);
                    if (mat == null) continue;   // unconfigured material set — warned by the factory; skip the layer
                    mat.renderQueue = LayerDrawOrder.TransparentQueue + drawIndex;

                    var applier = new ZoomStyleApplier(mat);
                    MaterialFactory.BindLinePaintToApplier(paint, applier, mat);
                    applier.ApplyZoom(initialZoom);

                    _lines.Add(new LineLayerRecord
                    {
                        Paint = paint, StyleLayer = sl, Material = mat, Applier = applier,
                    });
                    drawIndex++;
                }
                // background/symbol/raster/fill-extrusion are not rendered here; they don't take a slot.
            }
        }

        /// <summary>
        /// Pushes per-frame zoom-dependent uniforms to every bundle's material. Fill/line zoom paint via the
        /// <see cref="ZoomStyleApplier"/>; line width additionally needs the live ground resolution
        /// (<c>_MetersPerPixel</c>) and zoom-step dasharrays re-evaluated each frame.
        /// </summary>
        public void ApplyZoom(double zoom)
        {
            for (int i = 0; i < _fills.Count; i++)
                _fills[i].Applier.ApplyZoom(zoom);

            // Pixel-mode line width needs the live ground resolution: the shader computes
            //   widthM = _Width(px) * _MetersPerPixel
            // and the world is in Web-Mercator metres, so _MetersPerPixel must track the current zoom.
            float metersPerPixel = (float)CameraPoseMath.MetersPerPixel(zoom);
            for (int i = 0; i < _lines.Count; i++)
            {
                _lines[i].Applier.ApplyZoom(zoom);
                _lines[i].Material.SetFloat("_MetersPerPixel", metersPerPixel);
                MaterialFactory.ApplyLineDashArray(_lines[i].Paint, _lines[i].Material, zoom);
            }
        }

        /// <summary>Destroy all fill and line layer Material instances and clear the bundles.</summary>
        public void Dispose()
        {
            for (int i = 0; i < _fills.Count; i++) DestroyMaterial(_fills[i].Material);
            _fills.Clear();
            for (int i = 0; i < _lines.Count; i++) DestroyMaterial(_lines[i].Material);
            _lines.Clear();
        }

        private static void DestroyMaterial(Material mat)
        {
            if (mat == null) return;
            if (Application.isPlaying) UnityEngine.Object.Destroy(mat);
            else                       UnityEngine.Object.DestroyImmediate(mat);
        }
    }
}
