// RightHandedSphereProjectionWindingTests — THE decisive S100 tooth.
//
// SphericalProjection is curved AND left-handed (its ECEF→render (X,Z,Y) axis-swap is a reflection, det −1).
// This throwaway projection is curved AND RIGHT-handed: the *un-swapped* ECEF (World = (x, y, z), no axis
// swap), so its render-space tangent basis has det +1 — the opposite handedness. Built through the SAME
// one-path StyledLineTileBuilder, its line ribbon must STILL wind the same way as flat Mercator relative to the
// surface normal.
//
// Why it has teeth: winding is derived by construction (across = cross(along, up), one frame). Anything that
// instead reads curvature or handedness (a "curved ⇒ flip", or a det/handedness lookup) to decide winding
// inverts THIS case — a right-handed curved projection — while leaving SphericalProjection right. Only the
// genuine cross(along, up) derivation makes BOTH curved projections, of OPPOSITE handedness, match Mercator.
// And because the centerline is projected through the managed (boxed) IProjection, this never-registered type
// runs end-to-end; a builder still using the closed Burst ProjectionDispatch would throw instead.

using System.IO;
using System.Collections.Generic;
using NUnit.Framework;
using UnityEngine;
using Unity.Mathematics;
using MapRenderer.Core.Geo;
using MapRenderer.Core.Style;
using MapRenderer.Core.View.Camera;
using LineStyleLayer = MapRenderer.Core.Style.Line.StyleLayer;
using MapRenderer.Jobs.Tiles;
using MapRenderer.Jobs.Mvt;
using MapRenderer.Core.Expressions;

namespace MapRenderer.Tests.Globe
{
    /// <summary>A curved, RIGHT-handed projection: un-swapped ECEF (det(TangentBasis) = +1), the mirror of
    /// <see cref="SphericalProjection"/>'s left-handed swap. Only the geometry surface is real (the line builder
    /// uses <see cref="ProjectPoint"/> + <see cref="MaxRefineAngleRad"/>); the camera-interaction members throw.</summary>
    public readonly struct RightHandedSphereProjection : IProjection
    {
        public const double Radius = EarthConstants.A;

        public ProjectedPoint ProjectPoint(in GeoCoordinate geo)
        {
            double lambda = geo.Longitude * math.PI_DBL / 180.0;
            double phi    = geo.Latitude  * math.PI_DBL / 180.0;
            double cosPhi = math.cos(phi), sinPhi = math.sin(phi);
            double cosLam = math.cos(lambda), sinLam = math.sin(lambda);

            // Radial normal (unit), and the surface point at Radius — NO axis swap (raw ECEF is render space).
            double upX = cosPhi * cosLam, upY = cosPhi * sinLam, upZ = sinPhi;
            return new ProjectedPoint
            {
                World = new double3(upX * Radius, upY * Radius, upZ * Radius),
                Up    = new double3(upX,          upY,          upZ),
            };
        }

        public double3 Project(in GeoCoordinate geo) => ProjectPoint(geo).World;
        public double3 UpAt(in GeoCoordinate geo)    => ProjectPoint(geo).Up;

        public float3x3 TangentBasisAt(in GeoCoordinate geo)
        {
            double lambda = geo.Longitude * math.PI_DBL / 180.0;
            double phi    = geo.Latitude  * math.PI_DBL / 180.0;
            double cosPhi = math.cos(phi), sinPhi = math.sin(phi);
            double cosLam = math.cos(lambda), sinLam = math.sin(lambda);
            double3 up   = new double3(cosPhi * cosLam, cosPhi * sinLam, sinPhi);
            double3 east = new double3(-sinLam, cosLam, 0.0);
            double3 north = math.cross(up, east);
            // No axis swap → right-handed ENU (det +1). c0=east, c1=up, c2=north.
            return new float3x3((float3)east, (float3)up, (float3)north);
        }

        public double MetersPerUnit    => 1.0;
        public double MaxRefineAngleRad => SphericalProjection.MaxCurveSegmentRad; // curved — same tolerance as the sphere

        public bool TryGetHorizonOccluder(out double3 renderCentre, out double radius)
        {
            renderCentre = default; radius = 0.0; return false; // unused by the line builder
        }

        public GeoCoordinate3D ScreenToGround(double2 screenPx, double2 viewportPx, in CameraProperties camera)
            => throw new System.NotSupportedException("RightHandedSphereProjection is a geometry-only test double.");
        public double2 GroundToScreen(in GeoCoordinate3D ground, double2 viewportPx, in CameraProperties camera)
            => throw new System.NotSupportedException("RightHandedSphereProjection is a geometry-only test double.");
        public double ClampValidLatitude(double latitudeDegrees) => math.clamp(latitudeDegrees, -90.0, 90.0);
        public bool IsFinitePlanarWorld => false;
        public GeoCoordinate3D ClampLookAtToWorld(double2 viewportPx, in CameraProperties camera)
            => throw new System.NotSupportedException("RightHandedSphereProjection is a geometry-only test double.");
    }

    public class RightHandedSphereProjectionWindingTests
    {
        [TestCase("boundary-6-34-21.pbf.bytes",   6,  34,  21)]
        [TestCase("boundary-9-274-168.pbf.bytes", 9, 274, 168)]
        public void RightHandedCurvedLine_WindsSameAsMercator_RelativeToSurfaceNormal(string fixture, int z, int x, int y)
        {
            var id = new TileId { Z = z, X = x, Y = y };

            StyleDocument style = StyleParser.Parse(File.ReadAllText(
                Path.Combine(Application.dataPath, "StreamingAssets", "Fixtures", "liberty.json")));
            LineStyleLayer layer = null;
            foreach (var l in style.Layers)
                if (l.Id == "boundary_3") { layer = l as LineStyleLayer; break; }
            Assert.IsNotNull(layer, "boundary_3 must be a Line.StyleLayer");

            using MvtTile tile = MvtDecoder.Decode(
                id, File.ReadAllBytes(Path.Combine(Application.dataPath, "Fixtures", fixture)));
            ITileLayer mvtLayer = SourceLayerResolver.ResolveTileLayer(layer, tile);
            Assert.IsNotNull(mvtLayer, "boundary_3's source-layer must resolve in this fixture");
            var selected = TestTileMeshBuilder.Select(layer, mvtLayer, z);
            Assert.Greater(selected.Count, 0, "expected boundary_3 line features in this tile");

            Mesh flat  = TestTileMeshBuilder.BuildLineFromLayer(mvtLayer, selected, layer.Paint, layer.Layout, z, id, new WebMercatorProjection());
            Mesh rh    = TestTileMeshBuilder.BuildLineFromLayer(mvtLayer, selected, layer.Paint, layer.Layout, z, id, new RightHandedSphereProjection());
            Assert.IsNotNull(flat, "Mercator line must produce geometry");
            Assert.IsNotNull(rh,   "right-handed curved line must produce geometry (managed ProjectPoint path)");

            var (mSign, mUnif, _) = GlobeLineWindingTests.RibbonWindingSign(flat);
            var (rSign, rUnif, _) = GlobeLineWindingTests.RibbonWindingSign(rh);

            Assert.Greater(mUnif, 0.99, "Mercator ribbon winding is not uniform");
            Assert.Greater(rUnif, 0.99, "right-handed curved ribbon winding is not uniform");
            Assert.AreEqual(mSign, rSign,
                $"{fixture}: a RIGHT-handed curved projection winds OPPOSITE to Mercator — winding is being decided " +
                "by curvature/handedness, not derived from cross(along, up). This is the S100 shortcut the tooth forbids.");

            Object.DestroyImmediate(flat);
            Object.DestroyImmediate(rh);
        }
    }
}
