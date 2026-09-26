# Tile decode surface

The format-neutral seam above the decoders: the `ITileDecoder` → `IDecodedTile` → `ITileLayer` interfaces
every decoded-tile consumer programs against, the style-layer-to-tile-layer resolution that sits in front of
them, and the feature-selection step (filter evaluation, native or managed) that sits behind them. `Mvt/`
and the GeoJSON decoder here are the two production implementations; nothing outside this folder should need
to know which one produced a given `IDecodedTile`.

## Why it exists

A style layer names a `source-layer` string and a `filter` expression; turning that into "these are the
features this layer draws from this tile" must work identically whether the tile came from an MVT fetch or a
sliced GeoJSON dataset. Splitting the format-specific decode (`Mvt/`, `GeoJsonTileDecoder` here) from the
format-neutral resolution/selection logic (`SourceLayerResolver`, `FeatureSelector`) is what makes that true:
a new tile format plugs in by implementing `ITileDecoder`/`ITileLayer`, not by teaching the selection code a
new case.

## Pipeline

```
  TileResponse.Encoding                          a geojson source builds its own GeoJsonTileDecoder
        │                                         directly, closed over its dataset — ForEncoding never
        │  Decoders.ForEncoding               returns one
        ▼
  ITileDecoder   (MvtTileDecoder, from ForEncoding; or a directly-constructed GeoJsonTileDecoder)
        │
        │  ITileDecoder.Decode(TileId, bytes)    bytes is null for a decoder with no wire format (GeoJSON)
        ▼
  IDecodedTile   (MvtTile / GeoJsonTile) — layers looked up by name
        │
        │  SourceLayerResolver.ResolveTileLayer   style layer's source-layer string → the matching ITileLayer
        ▼                                          (each tile format answers for its own empty-name semantics)
  ITileLayer   (features + the layer's borrowed TileGeometryBuffers, adopted once via LayerGeometryAdoption)
        │
        │  FeatureSelector.SelectFeatures         resolves the layer's filter, then evaluates it per feature —
        ▼                                          native (INativeFilterSource) when the layer supports it,
                                                    else the managed CompiledFilter path (KeyBindingBuffers
                                                    pools the per-call key-binding scratch)
  selected features — a plain feature list, or SelectedTileFeature (feature + its ordinal into
  ITileLayer.Features), depending on which overload the caller needs
```

**The native-filter capability seam, from the neutral side.** `INativeFilterSource.TryBindNativeFilter` is
what `FeatureSelector` probes an `ITileLayer` for (with a type test) before ever compiling a managed filter; a layer
that implements it hands back an `INativeFeatureMatcher` — owned by the caller, evaluated once per selection
via `MatchAll`. Neither interface names a format-specific type, which is what lets `FeatureSelector` stay
format-agnostic: today only `MvtLayer` implements the capability (forwarding to `Mvt/`'s
`NativeFilterSelection.TryBind`), and a future GeoJSON native path would opt in the same way, never through
an `is MvtLayer` check.

**One shared adopt-once guard.** `MvtLayer` and `GeoJsonTileLayer` each own a `TileGeometryBuffers` they take
from a materializer exactly once; `LayerGeometryAdoption.Validate` is the single place both guards
(already-adopted, feature-count lockstep) live, so the invariant three ordinal-indexed consumers rely on
cannot drift between the two implementations.

## Components

| Type | Role |
|---|---|
| `ITileLayer` | one decoded tile layer: name, extent, features, and its borrowed `TileGeometryBuffers` |
| `IDecodedTile` | a decoded tile: layers looked up by name; `IDisposable`, owned by a `SharedDisposable{IDecodedTile}` |
| `ITileDecoder` | the decode seam: `(TileId, bytes) → IDecodedTile`, selected by `TileEncoding` |
| `MvtTileDecoder` | the MVT `ITileDecoder`: the sole production call site of `MvtDecoder.Decode` |
| `Decoders` | resolves the `ITileDecoder` for a `TileEncoding` |
| `GeoJsonTileLayer` | the second production `ITileLayer`: one GeoJSON dataset's geometry as it falls inside one tile; presents the same shape `MvtLayer` does |
| `GeoJsonTile` | the GeoJSON `IDecodedTile`: zero or one `GeoJsonTileLayer`, returned regardless of the name asked for (a geojson source has no sub-layers to match) |
| `GeoJsonTileDecoder` | the GeoJSON `ITileDecoder`: a closure over a projected dataset that slices it per tile; `bytes` is always ignored |
| `SourceLayerResolver` | maps a style layer's `source-layer` string onto a decoded tile's `ITileLayer` (and its `source`/source-type onto a `SourceDefinition`) |
| `SelectedTileFeature` | one selected feature paired with its ordinal into the source layer's own `ITileLayer.Features` |
| `FeatureSelector` | the selection seam: resolves the source layer, then evaluates its filter per feature — several overloads cover a fresh `List`, a clear-and-fill `List`, and a grow-only-array form |
| `KeyBindingBuffers` | a `[ThreadStatic]` free-list of `int[]` scratch for `FeatureSelector`'s per-call key-binding step (the string→id key hoist) |
| `INativeFilterSource` | the native-filter capability seam a tile layer implements to evaluate a compiled program without going through the managed `CompiledFilter` |
| `INativeFeatureMatcher` | the per-selection product `INativeFilterSource.TryBindNativeFilter` hands back; owns its evaluation scratch, disposed by the caller |
| `LayerGeometryAdoption` | the shared adopt-once + feature-count-lockstep guard both `MvtLayer.AdoptGeometry` and `GeoJsonTileLayer.AdoptGeometry` call through |
