using System.Collections.Generic;
using System.Text;
using System.Threading;
using System;
using Cysharp.Threading.Tasks;
using UnityEngine;
using MapRenderer.Core.Data;
using MapRenderer.Core.Tiles;
using MapRenderer.Unity.Concurrency;
using MapRenderer.Unity.Rendering.Source;
using MapRenderer.Unity.Style;
using GeoJson = MapRenderer.Core.GeoJson;

namespace MapRenderer.Unity.Rendering.Map
{
    /// <summary>Resolves the sources of a style into the specs <see cref="Tile.TileManager"/> wires.</summary>
    internal static class MapViewSourceSpecs
    {
        /// <summary>
        /// Resolves each rendered source-id of <paramref name="style"/> into a <see cref="Tile.TileManager.SourceSpec"/>.
        /// Inline <c>tiles[]</c> short-circuits; a <c>url</c>-only source, or a geojson source with a URL <c>data</c>,
        /// fetches once. A failed fetch skips that source only. It runs before <see cref="Rendering.Layers.RenderLayerSet.Build"/>,
        /// so it walks the raw style layers through <see cref="Rendering.Layers.RenderLayerFactory.TryGetFetchSource"/>.
        /// </summary>
        internal static async UniTask<List<Tile.TileManager.SourceSpec>> Build(
            StyleDocument style,
            Func<string, CancellationToken, UniTask<string>> documentLoaderOverride,
            Func<TileUrlTemplate, IDataSource> tileSourceFactoryOverride,
            CancellationToken ct)
        {
            var loader    = documentLoaderOverride    ?? StyleDocumentLoader.LoadTextAsync;
            // Vector tiles decode as MVT; the encoding is the source's, never the transport's.
            var factory   = tileSourceFactoryOverride ?? (address => new TemplatedTileSource(address, TileEncoding.Mvt));
            IWorkScheduler scheduler = WorkSchedulerFactory.ForCurrentPlatform();

            // Distinct rendered source-ids in declared order.
            var seen    = new HashSet<string>();
            var ordered = new List<string>();
            foreach (var sl in style.Layers)
            {
                if (!Rendering.Layers.RenderLayerFactory.TryGetFetchSource(sl, out string sid)) continue;
                if (seen.Add(sid)) ordered.Add(sid);
            }

            var specs = new List<Tile.TileManager.SourceSpec>(ordered.Count);
            foreach (string sid in ordered)
            {
                SourceDefinition def = style.GetSource(sid);
                if (def == null)
                {
                    Debug.LogWarning($"[MapView.SetStyle] layer references undefined source '{sid}' — skipped.");
                    continue;
                }

                // A geojson source is sliced locally, so it branches before the TileJSON fetch and the no-tiles skip; only a
                // URL `data` still fetches, through the same loader. A bad source is skipped, never a thrown SetStyle.
                if (def.Type == SourceType.GeoJson)
                {
                    // An inline object was already parsed at style load (SourcePayload.Parse); only a URL
                    // `data` still has work to do here, since a fetch cannot happen synchronously at parse time.
                    GeoJson.GeoJsonDataset parsed = def.Data?.Dataset;
                    string error = def.Data?.Error;
                    try
                    {
                        if (def.Data?.Url != null)
                        {
                            // A URL `data`: one document fetch through the same loader TileJSON uses, before
                            // anything is mutated — BuildSourceSpecs is already SetStyle's one pre-mutation await.
                            string text = await loader(def.Data.Url, ct);
                            parsed = GeoJson.GeoJsonParser.Parse(text);
                        }
                    }
                    catch (System.OperationCanceledException)
                    {
                        throw;
                    }
                    catch (System.Exception ex)
                    {
                        // Any parser or loader throw, not only GeoJsonFormatException, would fault SetStyle over
                        // one bad source. Cancellation is rethrown above.
                        error = ex.Message;
                    }

                    if (error != null)
                    {
                        Debug.LogWarning($"[MapView.SetStyle] geojson source '{sid}' failed to load or parse: " +
                                         $"{error}. Source skipped.");
                        continue;
                    }
                    if (parsed == null)
                    {
                        Debug.LogWarning($"[MapView.SetStyle] geojson source '{sid}' needs an inline object " +
                                         "or URL string `data` — skipped.");
                        continue;
                    }

                    // Slice options are supplied here, at the wiring site. An authored `buffer` (Style Spec [0, 512]; 512 is
                    // 4096 units, hence x8) overrides the margin; absent, `GeoJsonSliceOptions.Default`'s margin stays.
                    var geoJsonOptions = def.Buffer is double buffer
                        ? new GeoJson.GeoJsonSliceOptions
                        {
                            Extent                  = GeoJson.GeoJsonSliceOptions.DefaultExtent,
                            BufferAtReferenceExtent = buffer * (TileBufferClip.ReferenceExtent / 512.0),
                            SimplifyTolerance       = 0.0,
                        }
                        : GeoJson.GeoJsonSliceOptions.Default;
                    specs.Add(new Tile.TileManager.SourceSpec(
                        sid, Tile.TileManager.SourceKey.From(def), def.MinZoom, def.MaxZoom,
                        () => new Tile.Processing.GeoJsonTileFeatureSource(parsed, geoJsonOptions, scheduler)));
                    continue;
                }

                // Fetch + resolve the TileJSON ONCE when the source is url-only (inline tiles[] short-circuits).
                TileJson resolvedTileJson = null;
                if (SourceResolver.NeedsTileJson(def))
                {
                    try
                    {
                        string tjText = await loader(def.Url, ct);
                        resolvedTileJson = TileJsonParser.Parse(tjText);
                        SourceResolver.Resolve(def, resolvedTileJson);
                    }
                    catch (System.OperationCanceledException)
                    {
                        throw;
                    }
                    catch (System.Exception ex)
                    {
                        Debug.LogWarning($"[MapView.SetStyle] TileJSON load failed for source '{sid}' " +
                                         $"({def.Url}): {ex.Message}. Source skipped (no tiles).");
                        continue; // failure isolation — other sources still wire
                    }
                }

                if (def.Tiles == null || def.Tiles.Length == 0)
                {
                    Debug.LogWarning($"[MapView.SetStyle] source '{sid}' resolved to no tiles — skipped.");
                    continue;
                }

                var address = new TileUrlTemplate { Template = def.Tiles[0], Tms = def.Scheme == "tms" };
                // def.Bounds/BoundsMalformed reflect whichever JSON supplied `bounds` (TileJSON or the
                // source's own — SourceResolver.Resolve carries both), validated below before the key.
                Tile.GeoBounds bounds = ValidateBounds(def.Bounds, def.BoundsMalformed, sid);
                var            key    = Tile.TileManager.SourceKey.From(def, bounds);

                // The one production site that wraps the byte fetcher into the ITileFeatureSource seam. A `"tms"`
                // scheme flips only the fetch address (inside TileUrlTemplate); downstream keeps XYZ addressing.
                specs.Add(new Tile.TileManager.SourceSpec(
                    sid, key, def.MinZoom, def.MaxZoom,
                    () => new Tile.Processing.MvtTileFeatureSource(factory(address), scheduler),
                    bounds));
            }

            return specs;
        }

        /// <summary>Converts a resolved, already-validated <c>bounds</c> array to <see cref="Tile.GeoBounds"/>.
        /// <paramref name="malformed"/> (<see cref="Style.SourceDefinition.BoundsMalformed"/>) is the
        /// parser's verdict, read here rather than re-inspecting raw JSON. Malformed warns once and returns
        /// <c>default</c> (no gate); otherwise <paramref name="parsedBounds"/> converts unchanged.</summary>
        private static Tile.GeoBounds ValidateBounds(double[] parsedBounds, bool malformed, string sourceId)
        {
            if (malformed)
            {
                Debug.LogWarning($"[MapView.SetStyle] source '{sourceId}' has a malformed bounds (need " +
                                  "[west, south, east, north] as 4 numbers, south <= north, longitudes in " +
                                  "[-180, 180]) — ignored, no bounds gate.");
                return default;
            }

            return new Tile.GeoBounds
            {
                West = parsedBounds[0], South = parsedBounds[1],
                East = parsedBounds[2], North = parsedBounds[3],
                HasBounds = true,
            };
        }

        /// <summary>Joins every spec's <c>(SourceId, resolved SourceKey)</c> in order — the style-token
        /// input <see cref="SetStyle(StyleDocument,string,CancellationToken)"/>'s full-rebuild arm folds in,
        /// so two loads that resolve a source differently never share a prepared-cache key.</summary>
        internal static string ResolvedSourceIdentity(List<Tile.TileManager.SourceSpec> specs)
        {
            var sb = new StringBuilder();
            for (int i = 0; i < specs.Count; i++)
                sb.Append(specs[i].SourceId).Append('=').Append(specs[i].Key).Append(';');
            return sb.ToString();
        }
    }
}
