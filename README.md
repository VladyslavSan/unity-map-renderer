# unity-map-renderer

A **Unity-native** engine for rendering MapLibre-style *layered* vector maps from vector tiles, built
on **DOTS (ECS + Burst + Jobs)** for performance. Pluggable **bring-your-own data source** layer.

Not a Cesium-style photorealistic 3D-tiles renderer — this is closer to Mapbox/MapLibre: vector tiles
+ style-driven layered rendering, implemented clean-room from the open specs.

## Status
Past the pre-spike stage: MVT decode and the fill/line job graphs live in `MapRenderer.Jobs`
(Burst + Jobs, scheduled — see `ARCHITECTURE.md`'s module table), tiles render through three
interchangeable backends (Entities, BatchRendererGroup, GameObjects), and a per-frame symbol/text
placement system runs alongside the tile-mesh path. See [`ARCHITECTURE.md`](./ARCHITECTURE.md) for the
full design, module boundaries, and roadmap.

## Entry point
Three demo scenes: `Assets/Scenes/Demo/MapDemo/MapDemo.unity`,
`Assets/Scenes/Demo/MapDemo/OpenStreetMapLiberty.unity`, and
`Assets/Scenes/Test/TileLoadingStressTest/TileLoadingStressTest.unity`. Each wires up `MapHost`
(`Assets/Code/MapRenderer.App/MapHost.cs:21-40`), the composition root MonoBehaviour: `Start()` is the
runtime entry (reads inspector fields, builds the data source + style), `Wire(GameObject, Camera)` is
the static, testable entry that does the component-graph wiring alone.

## Architecture at a glance
See [`ARCHITECTURE.md`](./ARCHITECTURE.md) §2 for the current pipeline diagram and the module table
(which assembly owns what, and why `MapRenderer.Core` is legacy).

## Supported rendering surface
See `Assets/Code/MapRenderer.Unity/Rendering/Style/RenderLayerFactory.cs:19-23` for the current
supported / not-yet-supported layer kinds — that class doc is the single source of truth, kept in sync
with the render pipeline by construction.

## Setup & test commands
- `./Tools/run-tests.sh` — headless Unity tests, EditMode **and** PlayMode (the full gate).
- `dotnet test Tools/core-tests` — the fast `MapRenderer.Core` loop, no Editor lock.

See `AGENTS.md` for the full recipe (exit codes, licensing/lockfile caveats, single-platform runs).

## License
Proprietary — all rights reserved. Not open source.
