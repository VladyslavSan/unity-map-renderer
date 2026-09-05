using System;
using System.Collections.Generic;
using System.IO;
using NUnit.Framework;
using Unity.Mathematics;
using MapRenderer.Core.Geo;
using MapRenderer.Core.Geometry;

namespace MapRenderer.Tests.Meshing
{
    /// <summary>
    /// Globe-fill SUBDIVISION testbench (design §6.2). Unity EditMode only — it drives
    /// <see cref="SubdivisionCoverageValidator"/>, which is itself Unity-only (see that class's header).
    /// See <see cref="SubdivisionCoverageValidator"/> for the managed mirror + gap/coverage/quality
    /// analysis this exercises.
    /// </summary>
    public class GlobeSubdivisionTests
    {
        private static byte[] LoadFixture(string name)
        {
            string[] starts = { Directory.GetCurrentDirectory(), AppContext.BaseDirectory };
            foreach (string start in starts)
            {
                string dir = start;
                for (int i = 0; i < 16 && dir != null; i++)
                {
                    string p = Path.Combine(dir, "Assets", "Fixtures", name);
                    if (File.Exists(p)) return File.ReadAllBytes(p);
                    dir = Directory.GetParent(dir)?.FullName;
                }
            }
            throw new FileNotFoundException($"{name} not found walking up from {AppContext.BaseDirectory}");
        }

        private static readonly TileId Water63220 = new TileId { Z = 6, X = 32, Y = 20 };

        // ---- always-on guard — Mercator is a pass-through no-op (design §6.2: "Mercator moves zero pixels") ----

        [Test]
        public void Mercator_IsPassThrough_NoOp()
        {
            var mvt = LoadFixture("water-6-32-20.pbf.bytes");
            var rep = SubdivisionCoverageValidator.ValidateTileLayer(mvt, "water", Water63220, new WebMercatorProjection());

            Assert.AreEqual(0, rep.MaxDepthReached, "a constant-up projection must never subdivide: " + rep.Summary);
            Assert.IsFalse(rep.Subdivided, "Mercator subdivided — invariant broken: " + rep.Summary);
            Assert.AreEqual(rep.EarcutTriangles, rep.SubTriangles, "no-op ⇒ sub tris == earcut tris: " + rep.Summary);
            Assert.Less(rep.CoverageAreaRelError, 1e-9, "no-op ⇒ area identical: " + rep.Summary);
            Assert.Less(rep.MaxGapFracTile, 1e-9, "no-op ⇒ zero gap: " + rep.Summary);
            Assert.AreEqual(0, rep.TJunctions, "no-op ⇒ no midpoints inserted ⇒ no T-junctions: " + rep.Summary);
        }

        // ---- always-on guard — the validator itself is correct on clean synthetic globe input -----------

        [Test]
        public void CleanSyntheticGlobeSquare_IsConforming()
        {
            // A whole-tile square at the SAME z6/32/20 tile as the corpus, earcut into 2 triangles along its
            // diagonal. Being one convex, near-symmetric polygon (no coastline complexity), both triangles
            // subdivide to the SAME depth (verified: maxDepthReached=1, 0 T-junctions) — a case with NO depth
            // mismatch, unlike the real water corpus at this same tile/zoom (design §6.2's artefact).
            var outer = new List<double2>
            {
                new double2(0, 0), new double2(4096, 0), new double2(4096, 4096), new double2(0, 4096),
            };
            var poly = new Polygon(outer);

            var rep = SubdivisionCoverageValidator.Validate(new[] { poly }, Water63220, new SphericalProjection(), extent: 4096);

            Assert.AreEqual(0, rep.FlippedTris, "clean square must not fold: " + rep.Summary);
            Assert.AreEqual(0, rep.DegenerateTris, "clean square must not collapse: " + rep.Summary);
            Assert.IsTrue(rep.Passes(), "clean synthetic globe input must pass: " + rep.Summary);
        }

        // ---- acceptance tooth — the artefact (design §6.2) ------------------------------------------------
        // RED-verified against the un-fixed (per-triangle 1→4) job: MaxGapFracTile ≈ 0.00697 (maxGap≈3612m —
        // matches the design doc's measured 3612m; the PERCENTAGE differs from the design doc's 1.03% because
        // this validator normalizes by the tile's render-space DIAGONAL, not its top edge — see
        // MaxGapFracTile's doc comment), ~14x the 0.05% gate, while Subdivided==true and
        // CoverageAreaRelError~0 — i.e. it failed ONLY on the gap clause, proving it failed on the real
        // T-junction crack and not on a degenerate mirror. Flips GREEN under the edge-conforming fix
        // (per-edge marking + 3 templates): maxGap=0.0m, tJunctions=0.
        [Test]
        public void Corpus_Water_6_32_20_Globe_SubdivisionIsConforming()
        {
            var mvt = LoadFixture("water-6-32-20.pbf.bytes");
            var rep = SubdivisionCoverageValidator.ValidateTileLayer(mvt, "water", Water63220, new SphericalProjection());

            Assert.IsTrue(rep.Passes(), "globe fill subdivision has a visible T-junction crack: " + rep.Summary);
            Assert.IsFalse(rep.BudgetFired, "a conforming result must not have relied on the Budget cutoff: " + rep.Summary);
        }

        // ---- DEEP conformity tooth — depth>=2 (design §6.2) -------------------------------------------------
        // The corpus + clean-synthetic guards are all depth-1; a conformity bug that first appears in DEEP,
        // template-mixed recursion (depth>=2) would slip past them. A whole-tile quad at z2/0/0 (top of the
        // globe → strong curvature) subdivides to depth 5 — 2 earcut triangles, so it exercises BOTH
        // cross-parent (shared diagonal) AND intra-parent (deep recursion) T-junctions. It cracked under the
        // un-fixed non-conforming 1->4 job (maxGap≈5770m = 0.161% of tile, ~3x the 0.05% gate) while coverage
        // and curvature-fidelity were already satisfied — i.e. it failed ONLY on the gap clause. RED-verified;
        // flips GREEN alongside the corpus tooth under the conforming fix: maxGap=0.0m, tJunctions=0.
        [Test]
        public void Synthetic_Deep_Z2_Quad_Globe_SubdivisionIsConforming()
        {
            var outer = new List<double2>
            {
                new double2(0, 0), new double2(4096, 0), new double2(4096, 4096), new double2(0, 4096),
            };
            var rep = SubdivisionCoverageValidator.Validate(
                new[] { new Polygon(outer) }, new TileId { Z = 2, X = 0, Y = 0 }, new SphericalProjection(), extent: 4096);

            Assert.IsTrue(rep.Passes(), "deep (z2, depth>=2) globe subdivision has a T-junction crack: " + rep.Summary);
            Assert.IsFalse(rep.BudgetFired, "a conforming result must not have relied on the Budget cutoff: " + rep.Summary);
        }

        // ---- NON-UNIFORM-curvature deep tooth on a REAL z0 tile (design §6.2) ----------------------------
        // The corpus (depth-1) and z2-quad (uniform curvature → caps in lockstep) teeth both miss the regime
        // that bit us: a real z0 tile mixes well-formed fills, antimeridian earcut NEEDLE slivers, and
        // zero-area bridge slits, recursing to the depth cap NON-uniformly. The conforming fix keeps the
        // RENDERED geometry crack-free — worst real-geometry gap 26.7 m = 0.0002% of tile, sub-visible (the
        // ~0.08%-of-area earcut needles + phantom bridge slits are excluded from the gap/fidelity metrics:
        // they paint nothing and are an earcut concern, scope-fenced). Gated on gap MAGNITUDE (Passes), not a
        // zero T-junction COUNT — the "severity, not count" lesson.
        [Test]
        public void RealZ0Tile_NonUniformCurvature_IsConforming()
        {
            var rep = SubdivisionCoverageValidator.ValidateTileLayer(
                LoadFixture("sample-tile.bytes"), "countries", new TileId { Z = 0, X = 0, Y = 0 }, new SphericalProjection());

            Assert.IsTrue(rep.Passes(),
                "z0 non-uniform-curvature globe subdivision has a visible crack on rendered geometry: " + rep.Summary);
        }
    }
}
