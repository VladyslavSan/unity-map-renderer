// Engine-free, blittable (double-only fields, usable as NativeArray<T>/Burst job struct types). Construct
// via object initializer (`new GeoCoordinate { Latitude = …, Longitude = … }`), not a positional ctor.

using System;

namespace MapRenderer.Core.Geo
{
    /// <summary>
    /// A WGS-84 geodetic surface point (latitude, longitude in degrees).
    /// <para>Order: (Latitude, Longitude) — latitude first (ISO 6709 / human convention).</para>
    /// <para>Construct via object initializer: <c>new GeoCoordinate { Latitude = 52.52, Longitude = 13.40 }</c>.</para>
    /// <para>Blittable: only <c>double</c> backing fields; usable as <c>NativeArray&lt;GeoCoordinate&gt;</c>
    /// element type and as a Burst job struct field.</para>
    /// </summary>
    [Serializable]
    public readonly struct GeoCoordinate
    {
        /// <summary>Latitude, degrees [-90, 90]. WGS-84 geodetic.</summary>
        public double Latitude { get; init; }

        /// <summary>Longitude, degrees [-180, 180]. WGS-84 geodetic.</summary>
        public double Longitude { get; init; }

        public override string ToString() => $"GeoCoordinate({Latitude:F5}, {Longitude:F5})";
    }

    /// <summary>
    /// A WGS-84 geodetic point with altitude (latitude, longitude in degrees; altitude in metres above datum).
    /// Order: (Latitude, Longitude, Altitude) — latitude first.
    /// </summary>
    [Serializable]
    public readonly struct GeoCoordinate3D
    {
        /// <summary>Latitude, degrees [-90, 90]. WGS-84 geodetic.</summary>
        public double Latitude { get; init; }

        /// <summary>Longitude, degrees [-180, 180]. WGS-84 geodetic.</summary>
        public double Longitude { get; init; }

        /// <summary>Altitude in metres above the WGS-84 ellipsoid datum. Reserved for terrain; pass 0.</summary>
        public double Altitude { get; init; }

        /// <summary>Projects to the surface (drops altitude).</summary>
        public GeoCoordinate Surface => new GeoCoordinate { Latitude = Latitude, Longitude = Longitude };

        public override string ToString() => $"GeoCoordinate3D({Latitude:F5}, {Longitude:F5}, alt={Altitude:F1})";
    }
}