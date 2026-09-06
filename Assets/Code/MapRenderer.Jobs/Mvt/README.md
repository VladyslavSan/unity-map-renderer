# MVT decode

Turns one tile's Mapbox Vector Tile protobuf bytes into the decoded layer/feature/geometry surface the rest
of the pipeline reads — clean-room, built from the open MVT spec. Also home to the MVT-specific half of the
native filter VM (`Expressions/README.md` documents the format-agnostic half); see *Why the native filter
files live here* below for why that split runs through this folder.

## Why it exists

A tile's features, properties and geometry all live in one protobuf message, and a naive decode would parse
it straight into managed objects — a `Dictionary` per feature, a `uint[]` command array per feature, boxed
property values — on a hot, per-tile, often off-main-thread path. `MvtDecoder` decodes the whole message in
one pass but never mints a per-feature managed array to get there: every feature's geometry commands and
every feature's (key,value) tag pairs are flattened directly into one shared `Allocator.Persistent`
`NativeArray` apiece, and a feature reads its own slice out of that shared buffer on demand
(`DensePropertyStore`) rather than eagerly expanding it. The geometry commands are then decoded by a Burst
job (`MvtDecodeJob`), not managed code, so the coordinate math itself never leaves the native side either.

## Pipeline

```
  MVT protobuf bytes + TileId
        │
        │  MvtDecoder.Decode                 loops over Layer submessages; a malformed tile frees every
        ▼                                     buffer already minted by earlier layers, then rethrows
  MvtDecoder.DecodeLayer (per layer)
        │
        ├─ CountLayerElements (read-only counting pass)   pre-sizes the Features/Keys lists
        ├─ decode loop → Name/Extent/Version/Keys, DecodeValue → the value table, DecodeFeature → per-feature
        │                geometry-kind + id + BYTE BOUNDS only (tag words and geometry commands are not
        │                parsed here — just where their bytes start and end)
        │
        │  FlattenFeatureColumn (once for tags, once for geometry)   two-pass count-then-fill into ONE
        ▼                                                            shared NativeArray<uint> apiece
  tagWords[] / commands[]  (feature-indexed by a shared (offset, length) column pair)
        │
        ├── tags ──► MvtLayerPropertyResolver ──► DensePropertyStore   one view per feature; resolves its
        │                                                              (offset,count) slice on demand, not eagerly
        │
        └── geometry ──► MvtGeometryMaterializer.Materialize
                              │
                              │  MvtDecodeJob   [BurstCompile] IJob — the MVT command-stream decoder itself
                              ▼                 (MoveTo/LineTo/ClosePath, zigzag-delta cursor), run via `.Run()`
                          TileGeometryBuffers (rings) ── adopted by MvtLayer.Geometry
        │
        │  layer.AdoptGeometry / AdoptFeatureTagWords / AdoptFeatureTagColumns / AdoptValues (each callable
        ▼  exactly once; the geometry adopt also lockstep-checks the feature count)
  MvtLayer (complete) ── MvtFeature × N, each built once its Store exists ── MvtTile.Layers
```

Everything a `MvtLayer` owns — `Geometry`, `FeatureTagWords`, `FeatureTagOffsets`/`FeatureTagLengths`,
`Values` — is **borrowed** by every store and resolver that reads it, and freed once, by `MvtLayer.Dispose`,
which `MvtTile.Dispose` drives. Reading through a store after that point is a use-after-free, not a race.

**The native filter chain, bound per tile-layer.** `NativeFilterSelection.TryBind` (called from
`MvtLayer.TryBindNativeFilter`) is the seam a format-agnostic `FeatureSelector` calls through
`INativeFilterSource`; everything below it is MVT-specific:

```
  NativeFilterProgram (compiled once in Expressions/) + this MvtLayer
        │
        │  NativeFilterSelection.TryBind   null (⇒ managed fallback) if the layer has no DenseKeyResolver yet;
        │                                  else → NativeFilterRebind.Rebind, which resolves the program's key
        ▼                                  names and literal strings against THIS layer's key/value tables
                                            (also null if a literal matches ≥2 distinct strings there)
  binding (slot→id) ── owned by a new MvtNativeFeatureMatcher
        │
        │  MvtNativeFeatureMatcher.MatchAll → NativeFilterEvaluationJob.Execute (Burst, run via `RunByRef`)
        ▼                                     reads MvtLayerPropertyResolver's TagWords/Values/TagOffsets/TagLengths
  per-feature matched (byte) + NativeFilterError, both by layer ordinal
```

**Why the native filter VM's job, matcher and rebind live here, not in `Expressions/`.** Each names an
MVT-specific type in a member signature — `NativeFilterEvaluationJob`'s `Values` column is
`NativeArray<MvtValueNative>`, `MvtNativeFeatureMatcher` takes an `MvtLayer`, `NativeFilterRebind.Rebind`
takes an `MvtLayerPropertyResolver` — and a production type outside a decoder folder may not name a
format-specific type in a member signature (structurally enforced; see `NeutralGeometryPathTests`).
`MvtNativeFeatureMatcher` is doubly pinned: naming `MvtLayer` also makes it format-named by location, not
just by signature. All three are therefore MVT decoder-folder residents even though the opcode machine,
program and value types they operate over (`NativeFilterCompiler`, `NativeFilterProgram`, `NativeValue`) are
format-agnostic and stay in `Expressions/`. `Expressions/README.md` states the same split from that side.

## Components

| Type | Role |
|---|---|
| `MvtDecoder` | the decode entry point — the sole production parser of MVT bytes; frees every buffer it has minted so far if a malformed tile throws mid-decode |
| `MvtDecodeJob` | `[BurstCompile] IJob` — decodes one layer's flattened geometry command stream into ring vertices + per-ring offsets |
| `MvtGeometryMaterializer` | the `ITileGeometryMaterializer` (`Geometry/`) implementation for MVT: pre-counts exact ring/vertex capacity, then runs `MvtDecodeJob` |
| `MvtValueNative` | the decoded Value sub-message as a blittable 16-byte struct (Number / Boolean / String-as-id / Null); `ToValue`/`ToNativeValue` are its two read boundaries, into the managed `Value` and the VM's `NativeValue` respectively |
| `MvtLayerPropertyResolver` | the per-layer shared Keys/Values/tag-word/key-index tables every feature's store resolves its slice through; also implements `INativeFilterColumns` for the VM |
| `IMvtPropertyStore` | the property-bag interface a decoded feature's `Store` implements |
| `DensePropertyStore` | the only data-bearing `IMvtPropertyStore`: a `(offset, count)` view into the owning layer's shared tag words, resolved lazily — `TryGetByKeyIndex` (hot, alloc-free) and `AsDictionary` (cold) are two independent walks kept in agreement by a differential test |
| `EmptyPropertyStore` | the `IMvtPropertyStore` Null Object — the default `MvtFeature.Store`, so that field is never null |
| `INativeFilterColumns` | the native-column capability a Burst filter evaluator reads a feature's inputs through, instead of depending on a concrete property-store type |
| `MvtFeature` | one decoded feature: geometry type, id, and its property store — construct-once, no geometry of its own (geometry belongs to the layer) |
| `MvtLayer` | one decoded layer: name, extent, features, and the adopted geometry/tag/value buffers; implements `ITileLayer`, `IIndexedFeatureSource` and `INativeFilterSource` |
| `MvtTile` | the decoded tile: its layers, looked up by name; disposing it disposes every layer |
| `NativeFilterEvaluationJob` | `[BurstCompile] IJob` — the opcode VM's batched `Execute`, run over one layer's whole feature range in one dispatch; `NativeFilterError` (same file) is its threaded-error-code taxonomy |
| `MvtNativeFeatureMatcher` | the `INativeFeatureMatcher` (`Tiles/`) implementation for MVT: owns the rebound binding plus the per-feature result/error/kind columns, and runs the batched job |
| `NativeFilterRebind` | `NativeFilterProgram.Rebind` — the extension method that resolves a compiled program's key names and literal strings against one tile-layer's tables |
| `NativeFilterSelection` | `MvtLayer.TryBindNativeFilter`'s implementation, kept off `MvtLayer` so the layer stays a thin forwarder |
