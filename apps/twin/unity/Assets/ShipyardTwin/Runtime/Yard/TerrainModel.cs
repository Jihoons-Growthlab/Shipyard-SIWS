using System;
using UnityEngine;

namespace ShipyardTwin
{
    /// <summary>
    /// Real terrain heights around the yard (metres above sea level; negative = seabed) on a grid aligned with the
    /// satellite image (pipeline/fetch_satellite.py: terrain.bytes, float32, (grid+1)², row 0 = north).
    /// The coastline follows the imagery: water ≤ -3 m, land ≥ 0.5 m. Unity height = <see cref="YardLayout.SeaLevel"/> + h.
    /// </summary>
    public sealed class TerrainModel
    {
        private static TerrainModel _cached;
        private static bool _loaded;
        private readonly float[] _heights;

        public TerrainModel(SatelliteMeta meta, float[] heights)
        {
            Meta = meta;
            _heights = heights;
            Vertices = meta.grid + 1;
            if (heights.Length != Vertices * Vertices)
            {
                throw new ArgumentException("Terrain grid does not match satellite.json (grid " + meta.grid + ").");
            }
        }

        public SatelliteMeta Meta { get; }

        /// <summary>Vertices per side (grid + 1).</summary>
        public int Vertices { get; }

        /// <summary>Loads Resources/ShipyardTwin/Satellite (cached); null when there is no imagery or terrain.</summary>
        public static TerrainModel Load()
        {
            if (_loaded)
            {
                return _cached;
            }

            _loaded = true;
            var json = Resources.Load<TextAsset>(SatelliteGround.Folder + "satellite_meta");
            var bytes = Resources.Load<TextAsset>(SatelliteGround.Folder + "terrain");
            if (json == null || bytes == null)
            {
                return null;
            }

            var meta = JsonUtility.FromJson<SatelliteMeta>(json.text);
            var raw = bytes.bytes;
            var heights = new float[raw.Length / 4];
            Buffer.BlockCopy(raw, 0, heights, 0, heights.Length * 4);   // little-endian float32, as written
            _cached = meta.grid > 0 ? new TerrainModel(meta, heights) : null;
            return _cached;
        }

        /// <summary>Height at grid vertex (i, j).</summary>
        public float At(int i, int j) => _heights[Mathf.Clamp(j, 0, Vertices - 1) * Vertices + Mathf.Clamp(i, 0, Vertices - 1)];

        /// <summary>Bilinear height at an image pixel (0..size_px); outside the image clamps to the edge.</summary>
        public float AtPixel(double px, double py)
        {
            var gx = px / Meta.size_px * Meta.grid;
            var gy = py / Meta.size_px * Meta.grid;
            var i = (int)Math.Floor(gx);
            var j = (int)Math.Floor(gy);
            var fx = (float)(gx - i);
            var fy = (float)(gy - j);
            return Mathf.Lerp(Mathf.Lerp(At(i, j), At(i + 1, j), fx), Mathf.Lerp(At(i, j + 1), At(i + 1, j + 1), fx), fy);
        }

        public float AtGeo(double lat, double lon)
        {
            var (x, y) = ShipCoordinates.GeoToMercatorPixel(Meta.zoom, lat, lon);
            return AtPixel(x - Meta.tile_x0 * 256.0, y - Meta.tile_y0 * 256.0);
        }

        public float AtYard(YardSite site, float x, float y)
        {
            var (lat, lon) = ShipCoordinates.YardToGeo(site, x, y);
            return AtGeo(lat, lon);
        }
    }
}
