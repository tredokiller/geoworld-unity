using System;

// (namespace kept from when it was part of the editor's generator: worlds and packages made in a player use it too)
namespace GeoWorld.Editor
{
    // The export's local-enu-wgs84 projection: metres east (x) and north (z) on the tangent plane at the origin.
    // The same maths as extractor/geo.py EnuProjection, in double precision.
    public class GeoEnu
    {
        const double A = 6378137.0, F = 1.0 / 298.257223563, E2 = F * (2.0 - F);
        readonly double lat0, lon0, x0, y0, z0, sp, cp, sl, cl;

        public GeoEnu(double lat, double lon)
        {
            lat0 = lat; lon0 = lon;
            Ecef(lat, lon, out x0, out y0, out z0);
            double p = lat * Math.PI / 180, l = lon * Math.PI / 180;
            sp = Math.Sin(p); cp = Math.Cos(p); sl = Math.Sin(l); cl = Math.Cos(l);
        }

        static void Ecef(double lat, double lon, out double x, out double y, out double z)
        {
            double p = lat * Math.PI / 180, l = lon * Math.PI / 180, s = Math.Sin(p), c = Math.Cos(p);
            double n = A / Math.Sqrt(1.0 - E2 * s * s);
            x = n * c * Math.Cos(l); y = n * c * Math.Sin(l); z = n * (1.0 - E2) * s;
        }

        public void Forward(double lat, double lon, out double east, out double north)
        {
            Ecef(lat, lon, out double x, out double y, out double z);
            double dx = x - x0, dy = y - y0, dz = z - z0;
            east = -sl * dx + cl * dy;
            north = -sp * cl * dx - sp * sl * dy + cp * dz;
        }

        // Lat/lon of a local point, by fixed-point iteration on Forward().
        public void Inverse(double east, double north, out double lat, out double lon)
        {
            lat = lat0; lon = lon0;
            for (int i = 0; i < 6; i++)
            {
                Forward(lat, lon, out double fx, out double fy);
                double p = lat * Math.PI / 180, s2 = Math.Sin(p) * Math.Sin(p), w = Math.Sqrt(1.0 - E2 * s2);
                double mLat = Math.PI / 180 * A * (1.0 - E2) / (w * w * w), mLon = Math.PI / 180 * A / w * Math.Cos(p);
                lat += (north - fy) / mLat; lon += (east - fx) / mLon;
            }
        }
    }
}
