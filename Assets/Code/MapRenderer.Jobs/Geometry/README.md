# Tile geometry — the shared buffers every mesher reads

One decoded tile's rings, as blittable native columns, plus the stages that produce and narrow them. Fill,
line and extrusion all read the **same** buffer for a given source layer; none of them owns it.

The types here carry their contracts in their own XML docs, and those docs are the authority — this file is
the map, not a restatement. Read `TileGeometryBuffers`' type doc before changing anything in this directory.

## The one rule that spans every file

**Exactly one owner; everyone else borrows.** A materializer *mints* a buffer and *transfers* it to the
decoded tile layer, which owns it for the life of the decoded tile. Every mesher **borrows** it off
`ITileLayer.Geometry`: never disposes it, never retains it past the decode scope, never mutates it.

This is why a per-layer mesher can be cheap — no copy per consumer — and it is also the sharpest edge in the
directory. A borrower that disposes what it read corrupts every other consumer of the same tile, and the
symptom appears in an unrelated layer.

## Two things that are deliberately NOT part of the contract

- **Coordinate space is fixed, not tagged.** `Vertices` is tile-local `double2` in `[0, Extent]`, Y-down,
  always — never geodetic, never projected. The type is monomorphic on purpose, so "feed the geodetic
  version to earcut" is a compile-time impossibility rather than a rule to remember. Geodetic and projected
  coordinates live in their own buffers, produced by the projection stage.
- **Winding is not constrained.** A producer may emit rings of either orientation. Ring assembly derives the
  exterior sign per feature from the data and treats the opposite sign as holes, so nothing downstream
  requires a fixed input winding. The "canonical CCW" rule governs *tessellator output*, not decoded input
  rings — do not apply it here. What a producer owes is only that one feature's own rings are mutually
  consistent.

## Two backing modes, one owner

A buffer is either **array-backed** (minted whole) or **list-backed** (derived by a stage that grows its
output). The public fields read the same either way, but a list-backed buffer's fields are *views* over the
backing lists — and disposing a view is invalid. That is the entire reason the modes are distinguished, and
`Dispose` frees the right thing in each case. If you add a mode, that discriminator is what you must keep.

## Components

| Type | Folder | Role |
|---|---|---|
| `TileGeometryBuffers` | `Geometry/` | the buffer itself — columns, provenance, both backing modes, disposal |
| `ITileGeometryMaterializer` | `Geometry/` | the mint-and-transfer interface |
| `MvtGeometryMaterializer` | `Mvt/` | materializes from a decoded MVT tile |
| `PathGeometryMaterializer` | `Geometry/` | materializes synthetic geometry (the background quad's full-extent ring) |
| `RingSelectJob` | `Geometry/` | narrows a tile's rings to one layer's visit order |
| `RingClipJob` | `Geometry/` | the same, clipped to the tile's buffer window — production's default path |

`FillGraphOutput` — the mesh-side columns a build produces — lives in `Fill/`, not here, with the rest of
the fill mesher's namespace.

## A trap worth knowing

`RingClipJob` and `RingSelectJob` are alternatives, chosen by whether clipping is enabled — and clipping is
**enabled by default in production**. A test that builds a layer input without setting the clip runs the
*other* job. Several tests in this repo did exactly that, and each one silently exercised an arm production
never takes. If you are writing a test here, set the clip explicitly.
