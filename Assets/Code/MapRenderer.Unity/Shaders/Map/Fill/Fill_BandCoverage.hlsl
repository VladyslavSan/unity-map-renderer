#ifndef MAP_FILL_BAND_COVERAGE_INCLUDED
#define MAP_FILL_BAND_COVERAGE_INCLUDED

// ============================================================================
// Fill_BandCoverage.hlsl — the fill boundary band's fragment coverage, in one place.
//
// The band grows OUTWARD from the polygon boundary: `side` is 0 everywhere the hard fill already was and
// 1 at the band's outer edge, so coverage ramps 1 -> 0 across a strip that lies entirely OUTSIDE the
// boundary and the filled region itself is never displaced. That placement is the mechanism, not a
// detail: two abutting fills still leave zero background weight, which is what a ramp placed even
// partly inside the boundary would destroy at every shared edge and tile seam.
//
// The expression is the line path's shipped outer-edge ramp (Map/Line/Line_VertexExtrude.hlsl), reused
// rather than re-derived, and pinned character-for-character against that file by
// FillBandAttributeTests. Two properties of it are load-bearing and neither is visible from the code:
//   * the gradient is EUCLIDEAN, not fwidth. fwidth is |ddx| + |ddy|, which over-reads by up to sqrt(2) on
//     a diagonal silhouette. The failure is not "softer": coverage is (1 - side) DIVIDED by the gradient, so
//     an over-read gradient makes coverage fall FASTER — the ramp narrows to ~1/sqrt(2) px and the coverage
//     at the boundary itself drops from 1 to ~0.71, an under-inked seam. Measured 0.80 px against 1.00 px
//     on a 45-degree silhouette, while an axis-aligned one stays bit-identical (there one derivative is 0
//     and the two gradients agree exactly), which is why only a diagonal fixture can see it.
//   * the 1e-6 clamp is what makes an INTERIOR fragment read 1. Across an interior triangle `side` is
//     constant, so its screen derivative is exactly 0 and the whole reading rests on that floor.
//
// Shared by the Lit and Unlit forward passes; the depth-writing passes clip the band out instead of
// shading it, so they never call this.
// ============================================================================

/// Coverage of one fragment of the outward boundary band.
/// side — 0 on the boundary (and throughout the interior), 1 at the band's outer edge.
float MapFillBandCoverage(float side)
{
    float sideGrad = max(length(float2(ddx(side), ddy(side))), 1e-6);
    return saturate((1.0 - side) / sideGrad);
}

#endif // MAP_FILL_BAND_COVERAGE_INCLUDED
