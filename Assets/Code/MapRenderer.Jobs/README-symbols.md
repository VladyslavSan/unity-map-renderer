# Symbol jobs

The Burst half of symbol placement: which labels and icons survive, and where each one lands on screen
this frame. Every job here is a port of a managed pass that still exists as the parity oracle — none of
them invented an algorithm, and that is deliberate (see *Parity, not reimplementation* below).

`docs/symbol-symbol-perf-design.md` is the design SSOT and explains the *why*; this file explains the
*shape* and the order.

## Two cadences, and the split matters

These jobs do not all run at the same rate, and confusing the two is the easiest mistake to make here.

```
  PER TILE-LOAD (or per style/cover change) — "which symbols exist at all"
        │
        │  SymbolGatherJob      Stage 1: gather candidate symbols from a tile's placed blocks
        ▼
    candidates
        │
        │  SymbolCullJob        per record: dropped → departing → coverage → zoom → horizon
        ▼                       (the SAME predicate order as the managed pass, deliberately)
    survivors (sparse, with per-record keep flags)
        │
        │  SymbolCompactJob     squeezes the sparse keep-flags into a dense run
        ▼
    dense candidate set
        │
        │  SymbolCollisionJob   grid-accelerated: drops symbols whose boxes overlap a higher-priority one
        ▼
    the placed set


  PER FRAME — "where do the survivors land right now"
        │
        │  SymbolProjectionJob  parallel: render-space world points → screen, for every visible symbol
        ▼
    screen positions + depths
        │
        │  SymbolStageJob       the staging loop: per-symbol quads into the batch's SoA mirrors
        ▼
    vertex/index streams the renderer draws
```

`SymbolBlockView` is not a stage — it is a **blittable, non-owning view** over one placed tile block's
arrays, so a Burst job can index them without the block itself being job-compatible. It owns nothing and
must not outlive the block it views.

## Parity, not reimplementation

Every job here has a managed counterpart that is still the oracle for its tests, and several are described
in their own docs as line-for-line transliterations. That is the point: symbol placement is full of
order-dependent decisions (which of two overlapping labels wins, which predicate drops a symbol first), and
a "cleaner" reordering silently changes which labels appear on the map. When changing one of these jobs,
change the managed pass in the same commit or the parity tooth will tell you — that is the tooth working,
not an obstacle.

## Files

| file | role |
|---|---|
| `SymbolGatherJob.cs` | Stage 1 — gathers candidates from placed tile blocks |
| `SymbolCullJob.cs` | the Cull pass: the fixed predicate order, per record |
| `SymbolCompactJob.cs` | the Compact pass: sparse keep-flags → a dense run |
| `SymbolCollisionJob.cs` | grid-accelerated collision; decides the placed set |
| `SymbolProjectionJob.cs` | per-frame, parallel: world → screen for visible symbols |
| `SymbolStageJob.cs` | per-frame staging: symbols → the batch's vertex/index SoA |
| `SymbolBlockView.cs` | a blittable non-owning view over one block's arrays |
