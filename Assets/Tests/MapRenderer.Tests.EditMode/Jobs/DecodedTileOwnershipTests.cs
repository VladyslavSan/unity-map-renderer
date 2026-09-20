// Unity EditMode only — NativeArray buffers over the committed .pbf corpus. NOT in core-tests.csproj.

using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using NUnit.Framework;
using UnityEngine;
using MapRenderer.Core.Geo;
using MapRenderer.Jobs.Geometry;
using MapRenderer.Jobs.Mvt;
using MapRenderer.Jobs.Tiles;
using MapRenderer.Unity.Rendering.Tile.Processing;

namespace MapRenderer.Tests.Jobs
{
    /// <summary>
    /// IR C1 P3, tooth <b>B</b> — the ownership relation: <b>a decoded tile cannot be paired with another
    /// tile's geometry</b>.
    ///
    /// <para>Two clauses, both required, because they close the hazard from opposite ends. <b>B1</b> is
    /// structural: no production method takes a <c>TileGeometryBuffers</c> and a <c>TileId</c> together —
    /// that pair of parameters IS the mispairing shape, and its absence is what makes the defect
    /// unexpressible. <b>B2</b> is behavioural at the decode seam: the address a buffer carries is the
    /// address its own decode was given, over real multi-layer fixtures at two tiles differing in z, x AND
    /// y.</para>
    ///
    /// <para><b>Why the shape mattered.</b> Until P3 the buffer's tile came from
    /// <c>TileGeometryStore.GetOrMaterialize(tileLayer, tile)</c> — a caller-supplied second copy, so a store
    /// could be handed one tile's layers and another tile's id with nothing in the type system able to tell.
    /// P3 moves the id into <c>ITileDecoder.Decode(TileId, byte[])</c>, where it enters the pipeline once.</para>
    /// </summary>
    [TestFixture]
    public class DecodedTileOwnershipTests
    {
        // ── B1 — by construction: the mispairing shape does not exist in any signature ────────────────

        [Test]
        public void NoProductionSignature_TakesBothATileGeometryBuffersAndATileId()
        {
            Assembly[] production =
            {
                typeof(TileGeometryBuffers).Assembly,   // MapRenderer.Jobs
                typeof(ITileFeatureSource).Assembly,    // MapRenderer.Unity
            };

            var offenders   = new List<string>();
            int bufferSites = 0;

            foreach (Assembly assembly in production)
                foreach (Type t in assembly.GetTypes())
                    foreach (MethodBase m in Methods(t))
                    {
                        bool hasBuffer = false, hasTileId = false;
                        foreach (ParameterInfo p in m.GetParameters())
                        {
                            Type pt = p.ParameterType.IsByRef ? p.ParameterType.GetElementType() : p.ParameterType;
                            if (pt == typeof(TileGeometryBuffers)) hasBuffer = true;
                            if (pt == typeof(TileId))              hasTileId = true;
                        }
                        if (hasBuffer) bufferSites++;
                        if (hasBuffer && hasTileId) offenders.Add($"{t.FullName}.{m.Name}");
                    }

            // Non-vacuity: the scan really sees TileGeometryBuffers in real signatures. Without this a
            // reflection load that returned nothing would report "no offenders" identically.
            Assert.GreaterOrEqual(bufferSites, 3,
                "precondition: TileGeometryBuffers must appear in at least 3 production method signatures, " +
                "or this scan is looking at nothing");

            Assert.IsEmpty(offenders,
                "no production method may take BOTH a TileGeometryBuffers and a TileId. That parameter pair " +
                "is the mispairing shape itself: it lets a caller hand one tile's geometry an unrelated " +
                "tile's address. Since IR C1 P3 the address is read off the buffer (`geometry.Tile`), which " +
                "the decoder stamped from the id the fetch already had. " +
                $"Offenders: {string.Join(", ", offenders)}");

            // …and the positive half: the layer is where geometry lives, so there is a legitimate place to
            // read it from. Without this clause the negative above is satisfiable by having no geometry at all.
            PropertyInfo geometry = typeof(ITileLayer).GetProperty(nameof(ITileLayer.Geometry));
            Assert.IsNotNull(geometry, "ITileLayer must expose Geometry");
            Assert.AreEqual(typeof(TileGeometryBuffers), geometry.PropertyType);
        }

        // ── B2 — behavioural, at the decode seam ──────────────────────────────────────────────────────

        /// <summary>
        /// Two DIFFERENT committed fixtures, decoded at two ids differing in <b>z, x and y</b>: every layer's
        /// buffer carries the address its own decode was given, and the two tiles' values differ.
        ///
        /// <para><b>Production configuration.</b> Real multi-layer OpenMapTiles fixtures, not a synthetic
        /// one-layer tile — the stamping happens per layer inside <c>DecodeLayer</c>, so a one-layer fixture
        /// could not see a decoder that stamped only the first. And all three of z/x/y differ, because a
        /// decoder that propagated only <c>z</c> would pass a z-only-different pair.</para>
        ///
        /// <para><b>Catches</b> a decoder stamping <c>default(TileId)</c>, a constant, or the previous
        /// decode's id — every shape the caller-supplied-id defect could take.</para>
        /// </summary>
        [Test]
        public void EveryLayersBuffer_CarriesTheAddressItsOwnDecodeWasGiven()
        {
            var a = new TileId { Z = 6, X = 32,  Y = 20  };
            var b = new TileId { Z = 9, X = 274, Y = 168 };
            Assert.AreNotEqual(a.Z, b.Z, "precondition: the two ids must differ in z");
            Assert.AreNotEqual(a.X, b.X, "precondition: …and in x");
            Assert.AreNotEqual(a.Y, b.Y, "precondition: …and in y — a z-only pair cannot see a z-only decoder");

            using MvtTile first  = MvtDecoder.Decode(a, Fixture("water-6-32-20.pbf.bytes"));
            using MvtTile second = MvtDecoder.Decode(b, Fixture("boundary-9-274-168.pbf.bytes"));

            int checkedLayers = AssertEveryLayerStamped(first, a, "water-6-32-20");
            checkedLayers    += AssertEveryLayerStamped(second, b, "boundary-9-274-168");

            // Non-vacuity: real multi-layer tiles with real geometry, or the loop above ran over nothing.
            Assert.Greater(first.Layers.Count, 1,
                "precondition: the first fixture must be MULTI-layer — a one-layer tile cannot see a decoder " +
                "that stamps only the first layer");
            Assert.Greater(checkedLayers, 2,
                "precondition: at least three layers across the two tiles must actually carry a buffer");

            // …and the two tiles' stamps really differ, so "equals its own id" is not satisfiable by a
            // constant that happens to be both.
            Assert.AreNotEqual(
                first.Layers[0].Geometry.Tile, second.Layers[0].Geometry.Tile,
                "the two decodes must produce DIFFERENT tile addresses — equal ones would make the " +
                "per-decode assertions above pass for a constant stamper");
        }

        private static int AssertEveryLayerStamped(MvtTile tile, TileId expected, string fixtureName)
        {
            int stamped = 0;
            foreach (MvtLayer layer in tile.Layers)
            {
                if (!layer.Geometry.IsCreated) continue; // a feature-less layer allocates nothing
                stamped++;
                Assert.AreEqual(expected, layer.Geometry.Tile,
                    $"{fixtureName}/{layer.Name}: the buffer's Tile must be the id THIS decode was given. " +
                    "It is the only copy of the address below the fetch, so a wrong one silently places the " +
                    "layer's whole geometry on another tile.");
                Assert.AreEqual((double)layer.Extent, layer.Geometry.Extent,
                    $"{fixtureName}/{layer.Name}: the buffer's Extent must be the LAYER's own extent — the " +
                    "quantization range its coordinates are expressed in travels with them or not at all.");
            }
            return stamped;
        }

        /// <summary>
        /// The same "one buffer per source-layer" claim as
        /// <c>DecodedLayerGeometryTests.LayerGeometry_IsOneBufferPerSourceLayer_ByReference</c>, but over a
        /// REAL decoded <see cref="MvtLayer"/>.
        ///
        /// <para><b>Why both exist — a blind tooth found by injection.</b> The sibling runs over
        /// <c>InMemoryTileLayer</c>, the test double. RED-verifying "a <c>Geometry</c> getter that
        /// re-materializes per read" (the P3 shape of the silent per-consumer mint) injected into
        /// <c>MvtLayer</c> left that sibling GREEN: it was pinning the double's behaviour, not production's.
        /// This clause is the production configuration — nothing else in the repo reads a real decoded
        /// layer's buffer twice and compares the allocation.</para>
        /// </summary>
        [Test]
        public void ARealDecodedLayer_HandsBackTheSameAllocationOnEveryRead()
        {
            var id = new TileId { Z = 6, X = 32, Y = 20 };
            using MvtTile tile = MvtDecoder.Decode(id, Fixture("water-6-32-20.pbf.bytes"));

            int compared = 0;
            foreach (MvtLayer layer in tile.Layers)
            {
                ITileLayer neutral = layer; // read through the INTERFACE — the surface every consumer uses
                TileGeometryBuffers first  = neutral.Geometry;
                TileGeometryBuffers second = neutral.Geometry;
                if (!first.IsCreated) continue;
                compared++;

                // NativeArray<T>.Equals is pointer + length, so this is an IDENTITY comparison: a getter
                // that re-materialized would return equal CONTENTS at a different address, and every
                // output test in the suite would stay green.
                Assert.IsTrue(first.Vertices.Equals(second.Vertices),
                    $"layer '{layer.Name}': two reads of ITileLayer.Geometry must return the SAME " +
                    "allocation. A getter that materializes afresh per read is the 108-materializations-" +
                    "per-tile shape wearing the layer's name — and it is output-neutral.");
                Assert.IsTrue(first.RingOffsets.Equals(second.RingOffsets), $"layer '{layer.Name}': RingOffsets");
                Assert.IsTrue(first.RingFeatureIdx.Equals(second.RingFeatureIdx), $"layer '{layer.Name}': RingFeatureIdx");
            }

            Assert.Greater(compared, 1,
                "precondition: at least two real layers must carry a buffer, or this compared nothing");
        }

        private static byte[] Fixture(string name)
        {
            string path = Path.Combine(Application.dataPath, "Fixtures", name);
            Assert.IsTrue(File.Exists(path), $"Fixture missing: {path}");
            return File.ReadAllBytes(path);
        }

        private static IEnumerable<MethodBase> Methods(Type t)
        {
            const BindingFlags All = BindingFlags.Public | BindingFlags.NonPublic |
                                     BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;
            foreach (MethodInfo m in t.GetMethods(All)) yield return m;
            foreach (ConstructorInfo c in t.GetConstructors(All)) yield return c;
        }
    }
}
