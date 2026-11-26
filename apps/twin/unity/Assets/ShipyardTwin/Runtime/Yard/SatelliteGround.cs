using System;
using System.Collections.Generic;
using System.Linq;
using GrowthLab.Core;
using UnityEngine;

namespace ShipyardTwin
{
    /// <summary>Satellite image metadata from pipeline/fetch_satellite.py (Web Mercator tile origin, size, terrain grid).</summary>
    [Serializable]
    public sealed class SatelliteMeta
    {
        public string format;
        public int zoom;
        public int tile_x0;
        public int tile_y0;
        public int size_px;
        public int grid;
        public string attribution;
        public int inner_zoom;
        public int inner_tile_x0;
        public int inner_tile_y0;
        public int inner_size_px;

        public bool HasInner => inner_size_px > 0 && inner_zoom > zoom;

        /// <summary>Outer image pixel → inner (detail) image UV; outside 0..1 when not covered.</summary>
        public Vector2 InnerUv(double px, double py)
        {
            var k = Math.Pow(2, inner_zoom - zoom);
            return new Vector2((float)(((tile_x0 * 256.0 + px) * k - inner_tile_x0 * 256.0) / inner_size_px),
                (float)(1.0 - ((tile_y0 * 256.0 + py) * k - inner_tile_y0 * 256.0) / inner_size_px));
        }
    }

    /// <summary>
    /// VWorld satellite imagery draped over real terrain (Mapzen Terrain Tiles) around the yard, georeferenced
    /// through the ERP yard site (lat/lon + bearing): every vertex is a Web Mercator pixel converted to lat/lon,
    /// then to yard metres, at sea level + terrain height. Sea areas lie below the sea plane, so the coast slopes
    /// into the water. Around the yard a finer mesh carries the detail imagery (about 0.5 m/px). Land near the site is
    /// graded smoothly to quay level (earthworks); under the site the terrain lies just below the paving; dock pits
    /// are cut out and the water in front is kept submerged. Along the natural coastline: a soft surf band and
    /// scattered boulders. Purely visual.
    /// </summary>
    public sealed class SatelliteGround : MonoBehaviour, ICoreConsumer
    {
        public const string Folder = "ShipyardTwin/Satellite/";

        /// <summary>Paving within this distance of the shoreline wall has no terrain right under it (m, > one grid cell).</summary>
        private const float ShoreWallSetback = 24f;

        /// <summary>Grass verge outside the site outline (m); YardEnvironment plants its trees inside it.</summary>
        public const float VergeWidth = 32f;

        [Tooltip("Distance over which land is graded from quay level back to the natural terrain (m).")]
        [SerializeField] private float _grading = 140f;

        [Tooltip("Detail mesh subdivisions per terrain cell (about 16 m).")]
        [SerializeField] private int _innerSubdivisions = 4;

        [Tooltip("Surf band width seaward of the waterline (m).")]
        [SerializeField] private float _surfWidth = 7f;

        private Material _surf;

        public void Construct(CoreContext context)
        {
            if (GetComponent<MeshFilter>() != null || !context.Services.TryGet<TwinFile>(out var twin) ||
                !context.Services.TryGet<ShipPalette>(out var palette) || twin.yard?.site == null || string.IsNullOrEmpty(twin.yard.site.id))
            {
                return;
            }

            var terrain = TerrainModel.Load();
            var image = Resources.Load<Texture2D>(Folder + "satellite");
            if (terrain == null || image == null)
            {
                context.Log.Warning("ShipyardTwin", "No satellite imagery/terrain; run pipeline/fetch_satellite.py.");
                return;
            }

            var docks = YardLayout.Docks(twin);
            // Per zone, in its local axes: the same paving as YardEnvironment (pier root included), its dredged band
            // and the outline edges that meet graded ground.
            var zones = YardLayout.Zones(twin).Select(zone =>
            {
                var outline = YardEnvironment.SiteOutline(zone);
                var band = DredgeBand(zone, outline);
                return (Zone: zone, Site: outline, Shore: zone.Shore, Band: band,
                    Graded: GradedEdges(zone, outline, zone.Shore, terrain, twin.yard.site, band));
            }).ToList();
            float Dredge(Vector2 p, float natural) =>
                zones.Aggregate(natural, (h, z) => Dredged(z.Band, z.Shore, z.Zone.ToLocal(p), h));
            bool OpenWater(Vector2 p) => Dredge(p, 100f) < 0f;      // even a hill would be dredged here
            float SiteDistance(Vector2 p) => zones.Min(z => Polygon2D.Distance(z.Site, z.Zone.ToLocal(p)));
            // Cut only the dock pits (dry, below sea level). Under the rest of the site the terrain lies just below
            // the paving (no gap along the outline); the open water in front of the yard is pushed under the sea.
            var pits = docks.Select(d => Polygon2D.Oriented(new Vector2(d.x, d.y), d.heading_deg, -d.size_x_m / 2f - 8f, d.size_x_m / 2f + 8f,
                -d.size_y_m / 2f - 8f, d.size_y_m / 2f + 8f)).ToList();
            bool Excluded(Vector2 p) => pits.Any(r => Polygon2D.Contains(r, p));
            float Height(Vector2 p, float natural)
            {
                foreach (var z in zones)
                {
                    var local = z.Zone.ToLocal(p);
                    if (Polygon2D.Contains(z.Site, local))
                    {
                        // Behind the vertical quay wall the ground drops below the wall's foot: a terrain cell spanning
                        // the shoreline would otherwise slope from the paving out past the wall and show above the water.
                        // Only behind the wall (and mounds): near graded edges the ground still meets the paving.
                        return local.x > z.Shore - ShoreWallSetback && DistanceToGradedEdges(z.Graded, local) > ShoreWallSetback
                            ? YardLayout.SeaLevel - 4f
                            : YardLayout.QuayLevel - 0.3f;
                    }
                }

                natural = Dredge(p, natural);

                if (natural <= 0f)
                {
                    return YardLayout.SeaLevel + natural;                        // seabed stays below the sea plane
                }

                var t = Mathf.SmoothStep(0f, 1f, SiteDistance(p) / _grading);
                return Mathf.Lerp(YardLayout.QuayLevel - 0.3f, YardLayout.SeaLevel + natural, t);
            }

            var inner = palette.Create("SatelliteDetail");
            var detail = terrain.Meta.HasInner ? Resources.Load<Texture2D>(Folder + "satellite_inner") : null;
            // Ground cut down by more than 1.5 m: drawn as an earth bank instead of stretched imagery.
            bool Bank(Vector2 p, float natural) => natural - Dredge(p, natural) > 1.5f && natural > 0f && SiteDistance(p) > 0f;
            // Landscaped verge around the paving: the near band and wherever grading moved the ground by more than a
            // metre (imagery stretched over earthworks looks like grey smears). Land only; the satellite takes over beyond.
            bool Grass(Vector2 p, float natural)
            {
                var dredged = Dredge(p, natural);
                if (dredged <= 0.5f) return false;
                var distance = SiteDistance(p);
                return distance > 0f && (distance < VergeWidth ||
                                         distance < 90f && Mathf.Abs(Height(p, natural) - (YardLayout.SeaLevel + dredged)) > 2.5f);
            }

            var mesh = BuildMesh(terrain, twin.yard.site, Height, Excluded, detail != null ? _innerSubdivisions : 0, Bank, Grass);
            gameObject.AddComponent<MeshFilter>().sharedMesh = mesh;
            var renderer = gameObject.AddComponent<MeshRenderer>();
            var materials = new List<Material> { Imagery(palette.Create("Satellite"), image) };
            if (detail != null)
            {
                materials.Add(Imagery(inner, detail));
            }

            materials.Add(palette.Get("bank"));
            materials.Add(palette.Get("embankment"));
            renderer.sharedMaterials = materials.Take(mesh.subMeshCount).ToArray();

            // Natural coastline (not the site's own edge nor the cut-out water in front): surf and boulders.
            var coast = Coastline(mesh, terrain.Vertices, twin.yard.site)
                .Where(s => SiteDistance((s.A + s.B) / 2f) > 45f && !OpenWater((s.A + s.B) / 2f)).ToList();
            BuildSurf(coast);
            BuildBoulders(coast, palette, terrain);
            HoverTarget.Occluder(gameObject);   // hills and shore hide what lies behind them
            YardWalkSurface.Mark(gameObject);
            context.Log.Info("ShipyardTwin", "Satellite terrain: zoom " + terrain.Meta.zoom + ", " + terrain.Meta.size_px + " px, grid " +
                                             terrain.Meta.grid + (detail != null ? ", detail zoom " + terrain.Meta.inner_zoom : string.Empty) +
                                             ", coast " + coast.Count + " segments (" + terrain.Meta.attribution + ")");
        }

        /// <summary>Site outline edges that meet graded ground (neither the shoreline wall nor a rubble mound).</summary>
        public static List<(Vector2 A, Vector2 B)> GradedEdges(YardZone zone, List<Vector2> site, float shore, TerrainModel terrain, YardSite yardSite, (float, float) band)
        {
            var edges = new List<(Vector2, Vector2)>();
            for (var i = 0; i < site.Count; i++)
            {
                Vector2 a = site[i], b = site[(i + 1) % site.Count];
                var wall = Mathf.Abs(a.x - shore) < 0.01f && Mathf.Abs(b.x - shore) < 0.01f;
                if (!wall && !YardEnvironment.IsSeawardEdge(zone, a, b, terrain, yardSite, band, shore))
                {
                    edges.Add((a, b));
                }
            }

            return edges;
        }

        public static float DistanceToGradedEdges(List<(Vector2 A, Vector2 B)> edges, Vector2 p)
        {
            var best = float.MaxValue;
            foreach (var (a, b) in edges)
            {
                var ab = b - a;
                var t = ab.sqrMagnitude > 1e-6f ? Mathf.Clamp01(Vector2.Dot(p - a, ab) / ab.sqrMagnitude) : 0f;
                best = Mathf.Min(best, Vector2.Distance(p, a + ab * t));
            }

            return best;
        }

        /// <summary>
        /// Along-shore extent (local y of a zone) of the dredged water: from below the lowest berth (pier berths are a
        /// beam and fenders wide) to 10 m past the highest dock gate or berth; the natural coast beyond stays as it is.
        /// </summary>
        public static (float Start, float End) DredgeBand(YardZone zone, List<Vector2> site)
        {
            var docks = zone.Docks.Select(zone.Local).ToList();
            var piers = zone.Others.Where(f => f.kind == "안벽").Select(zone.Local).ToList();
            const float berth = 60f;
            var ends = docks.Select(f => (f.y - f.size_y_m / 2f, f.y + f.size_y_m / 2f))
                .Concat(piers.Select(f => (f.y - f.size_y_m / 2f - berth, f.y + f.size_y_m / 2f + berth))).ToList();
            return ends.Count > 0 ? (ends.Min(e => e.Item1) - 10f, ends.Max(e => e.Item2) + 10f) : (site.Min(v => v.y), site.Max(v => v.y));
        }

        /// <summary>
        /// Natural height after dredging the water in front of the yard. In the core (along the band, from 100 m inland
        /// of the quay line seaward) everything goes to at least 2 m below sea level. Fading out over 150 m beyond the
        /// band's start, 60 m beyond its end (the beach past the 1도크 gate) and 120 m inland, only low ground
        /// (beaches, flats under about 10 m) is lowered, so hills and islands nearby keep their shape.
        /// </summary>
        public static float Dredged((float Start, float End) band, float shore, Vector2 p, float natural)
        {
            var along = 1f - Mathf.Clamp01(Mathf.Max((band.Start - p.y) / 150f, (p.y - band.End) / 60f, 0f));
            var across = Mathf.Clamp01((p.x - (shore - 220f)) / 120f);
            var d = Mathf.SmoothStep(0f, 1f, along) * Mathf.SmoothStep(0f, 1f, across);
            if (d <= 0f)
            {
                return natural;
            }

            var low = 1f - Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(2f, 10f, natural));
            var core = Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(0.7f, 1f, along)) * Mathf.SmoothStep(0f, 1f, across);
            return Mathf.Lerp(natural, Mathf.Min(natural, -2f), d * Mathf.Lerp(low, 1f, core));
        }

        private void Update()
        {
            if (_surf != null)
            {
                // Breathing surf: the band brightens and slides slightly as waves come in.
                var t = Time.time;
                _surf.SetColor("_BaseColor", new Color(1f, 1f, 1f, 0.55f + 0.25f * Mathf.Sin(t * 0.9f)));
                _surf.SetTextureOffset("_BaseMap", new Vector2(t * 0.01f, 0.08f * Mathf.Sin(t * 0.9f)));
            }
        }

        private static Material Imagery(Material material, Texture2D texture)
        {
            material.SetTexture("_BaseMap", texture);
            material.SetTexture("_MainTex", texture);
            material.SetColor("_BaseColor", Color.white);
            material.SetColor("_Color", Color.white);
            material.SetFloat("_Smoothness", 0.05f);
            material.SetFloat("_Metallic", 0f);
            return material;
        }

        /// <summary>
        /// Terrain grid mesh; vertex (i, j) = image pixel (i, j) * size / grid, row 0 = north. With subdivisions &gt; 0
        /// and detail imagery in the metadata, the cells it covers move to a second submesh, each split into
        /// subdivisions² quads that use the detail image; its outer edges lie on the coarse cell edges (no cracks).
        /// </summary>
        public static Mesh BuildMesh(TerrainModel terrain, YardSite site, Func<Vector2, float, float> height, Func<Vector2, bool> exclude = null,
            int subdivisions = 0, Func<Vector2, float, bool> bank = null, Func<Vector2, float, bool> grass = null)
        {
            var meta = terrain.Meta;
            var n = terrain.Vertices;
            var grid = meta.grid;
            var cell = (double)meta.size_px / grid;
            var vertices = new List<Vector3>(n * n);
            var uvs = new List<Vector2>(n * n);
            var yardPoints = new List<Vector2>(n * n);
            var banked = new List<bool>(n * n);
            var grassed = new List<bool>(n * n);
            void Vertex(double px, double py, float natural, Vector2 uv)
            {
                var (lat, lon) = ShipCoordinates.MercatorPixelToGeo(meta.zoom, meta.tile_x0 * 256.0 + px, meta.tile_y0 * 256.0 + py);
                var yard = ShipCoordinates.GeoToYard(site, lat, lon);
                yardPoints.Add(yard);
                var isBank = bank != null && bank(yard, natural);
                banked.Add(isBank);
                grassed.Add(!isBank && grass != null && grass(yard, natural));
                vertices.Add(ShipCoordinates.YardToUnity(yard.x, yard.y, height(yard, natural)));
                uvs.Add(uv);
            }

            for (var j = 0; j < n; j++)
            {
                for (var i = 0; i < n; i++)
                {
                    Vertex(i * cell, j * cell, terrain.At(i, j), new Vector2((float)i / grid, 1f - (float)j / grid));
                }
            }

            // Coarse cells fully inside the detail image (one cell margin).
            int ci0 = grid, ci1 = -1, cj0 = grid, cj1 = -1;
            if (subdivisions > 0 && meta.HasInner)
            {
                for (var c = 0; c < grid; c++)
                {
                    var lo = meta.InnerUv(c * cell, c * cell);
                    var hi = meta.InnerUv((c + 1) * cell, (c + 1) * cell);
                    if (lo.x >= 0.002f && hi.x <= 0.998f)
                    {
                        ci0 = Math.Min(ci0, c);
                        ci1 = Math.Max(ci1, c);
                    }

                    if (lo.y <= 0.998f && hi.y >= 0.002f)
                    {
                        cj0 = Math.Min(cj0, c);
                        cj1 = Math.Max(cj1, c);
                    }
                }
            }

            bool Detailed(int i, int j) => i >= ci0 && i <= ci1 && j >= cj0 && j <= cj1;
            var coarse = new List<int>(grid * grid * 6);
            var bankQuads = new List<int>();
            var grassQuads = new List<int>();
            void Quad(List<int> target, int a, int b, int c, int d)
            {
                if (banked[a] || banked[b] || banked[c] || banked[d])
                {
                    bankQuads.AddRange(new[] { a, b, c, d });
                    return;
                }

                if (grassed[a] && grassed[b] && grassed[c] && grassed[d])
                {
                    grassQuads.AddRange(new[] { a, b, c, d });
                    return;
                }

                target.AddRange(new[] { a, b, c, a, c, d });
            }

            for (var j = 0; j < grid; j++)
            {
                for (var i = 0; i < grid; i++)
                {
                    // North-up: a top-left, b top-right, c bottom-right, d bottom-left; clockwise from above = facing up.
                    int a = j * n + i, b = a + 1, d = a + n, c = d + 1;
                    if (Detailed(i, j) || exclude != null && exclude((yardPoints[a] + yardPoints[c]) / 2f))
                    {
                        continue;
                    }

                    Quad(coarse, a, b, c, d);
                }
            }

            var fine = new List<int>();
            if (ci1 >= ci0 && cj1 >= cj0)
            {
                var s = subdivisions;
                int cols = (ci1 - ci0 + 1) * s + 1, rows = (cj1 - cj0 + 1) * s + 1, start = vertices.Count;
                for (var r = 0; r < rows; r++)
                {
                    for (var q = 0; q < cols; q++)
                    {
                        double gx = ci0 + (double)q / s, gy = cj0 + (double)r / s;
                        Vertex(gx * cell, gy * cell, terrain.AtPixel(gx * cell, gy * cell), meta.InnerUv(gx * cell, gy * cell));
                    }
                }

                for (var r = 0; r < rows - 1; r++)
                {
                    for (var q = 0; q < cols - 1; q++)
                    {
                        int a = start + r * cols + q, b = a + 1, d = a + cols, c = d + 1;
                        if (exclude != null && exclude((yardPoints[a] + yardPoints[c]) / 2f))
                        {
                            continue;
                        }

                        Quad(fine, a, b, c, d);
                    }
                }
            }

            // Bank and grass quads get their own vertices with metric UVs (texture tiles in metres come from the paint).
            List<int> Metric(List<int> quads)
            {
                var triangles = new List<int>();
                for (var k = 0; k < quads.Count; k += 4)
                {
                    var start = vertices.Count;
                    for (var m = 0; m < 4; m++)
                    {
                        var src = quads[k + m];
                        vertices.Add(vertices[src]);
                        uvs.Add(yardPoints[src]);
                    }

                    triangles.AddRange(new[] { start, start + 1, start + 2, start, start + 2, start + 3 });
                }

                return triangles;
            }

            var bankTriangles = Metric(bankQuads);
            var grassTriangles = Metric(grassQuads);

            var mesh = new Mesh { name = "SatelliteTerrain", indexFormat = UnityEngine.Rendering.IndexFormat.UInt32 };
            mesh.SetVertices(vertices);
            mesh.SetUVs(0, uvs);
            var parts = new List<List<int>> { coarse };
            if (fine.Count > 0)
            {
                parts.Add(fine);
            }

            // Always present (possibly empty) so the material slots stay: imagery, [detail], bank, grass.
            parts.Add(bankTriangles);
            parts.Add(grassTriangles);

            mesh.subMeshCount = parts.Count;
            for (var k = 0; k < parts.Count; k++)
            {
                mesh.SetTriangles(parts[k], k);
            }

            mesh.RecalculateNormals();
            mesh.RecalculateBounds();
            return mesh;
        }

        /// <summary>A waterline segment in yard metres; Sea is the unit direction towards the water.</summary>
        public readonly struct CoastSegment
        {
            public CoastSegment(Vector2 a, Vector2 b, Vector2 sea)
            {
                A = a;
                B = b;
                Sea = sea;
            }

            public Vector2 A { get; }
            public Vector2 B { get; }
            public Vector2 Sea { get; }
        }

        /// <summary>Waterline (terrain height = sea level) of the coarse grid by marching squares.</summary>
        public static List<CoastSegment> Coastline(Mesh mesh, int n, YardSite site)
        {
            var v = new List<Vector3>();
            mesh.GetVertices(v);
            var level = YardLayout.SeaLevel;
            var segments = new List<CoastSegment>();
            Vector2 Yard(int k) => new Vector2(v[k].x, v[k].z);                  // Unity (x, z) = yard (x, y)
            for (var j = 0; j < n - 1; j++)
            {
                for (var i = 0; i < n - 1; i++)
                {
                    int[] corners = { j * n + i, j * n + i + 1, (j + 1) * n + i + 1, (j + 1) * n + i };
                    var points = new List<Vector2>(4);
                    for (var e = 0; e < 4; e++)
                    {
                        int p = corners[e], q = corners[(e + 1) % 4];
                        float hp = v[p].y - level, hq = v[q].y - level;
                        if (hp > 0f != hq > 0f)
                        {
                            points.Add(Vector2.Lerp(Yard(p), Yard(q), hp / (hp - hq)));
                        }
                    }

                    if (points.Count != 2)
                    {
                        continue;                                                // flat, or a saddle (rare): skip
                    }

                    // Towards the water: from the cell's land corners to its sea corners.
                    Vector2 landSum = Vector2.zero, seaSum = Vector2.zero;
                    int landCount = 0, seaCount = 0;
                    foreach (var k in corners)
                    {
                        if (v[k].y > level)
                        {
                            landSum += Yard(k);
                            landCount++;
                        }
                        else
                        {
                            seaSum += Yard(k);
                            seaCount++;
                        }
                    }

                    var along = (points[1] - points[0]).normalized;
                    var normal = new Vector2(-along.y, along.x);
                    if (Vector2.Dot(normal, seaSum / seaCount - landSum / landCount) < 0f)
                    {
                        normal = -normal;
                    }

                    segments.Add(new CoastSegment(points[0], points[1], normal));
                }
            }

            return segments;
        }

        /// <summary>Soft white surf band on the water along the coastline (transparent, animated in Update).</summary>
        private void BuildSurf(List<CoastSegment> coast)
        {
            var template = Resources.Load<Material>("ShipyardTwin/Materials/Foam");
            if (template == null || coast.Count == 0)
            {
                return;
            }

            _surf = new Material(template) { name = "Surf" };
            _surf.SetTexture("_BaseMap", SurfTexture());
            var vertices = new List<Vector3>();
            var uvs = new List<Vector2>();
            var triangles = new List<int>();
            var y = YardLayout.SeaLevel + 0.08f;
            foreach (var s in coast)
            {
                var along = (s.B - s.A).normalized * 1.5f;                       // overlap neighbours slightly
                Vector2 a = s.A - along, b = s.B + along, inland = -s.Sea * 1.5f, outward = s.Sea * _surfWidth;
                var start = vertices.Count;
                var length = Vector2.Distance(a, b);
                var u = (a.x + a.y) / 40f;
                foreach (var (p, uv) in new[]
                         {
                             (a + inland, new Vector2(u, 1f)), (b + inland, new Vector2(u + length / 40f, 1f)),
                             (b + outward, new Vector2(u + length / 40f, 0f)), (a + outward, new Vector2(u, 0f)),
                         })
                {
                    vertices.Add(ShipCoordinates.YardToUnity(p.x, p.y, y));
                    uvs.Add(uv);
                }

                triangles.AddRange(new[] { start, start + 1, start + 2, start, start + 2, start + 3, start, start + 2, start + 1, start, start + 3, start + 2 });
            }

            var mesh = new Mesh { name = "Surf", indexFormat = UnityEngine.Rendering.IndexFormat.UInt32 };
            mesh.SetVertices(vertices);
            mesh.SetUVs(0, uvs);
            mesh.SetTriangles(triangles, 0);
            mesh.RecalculateBounds();
            var go = new GameObject("Surf");
            go.transform.SetParent(transform, false);
            go.AddComponent<MeshFilter>().sharedMesh = mesh;
            var renderer = go.AddComponent<MeshRenderer>();
            renderer.sharedMaterial = _surf;
            renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
        }

        /// <summary>Foam alpha: strongest at the waterline (v = 1), fading seaward, broken up along the shore.</summary>
        private static Texture2D SurfTexture()
        {
            const int w = 128, h = 32;
            var texture = new Texture2D(w, h, TextureFormat.RGBA32, true) { wrapModeU = TextureWrapMode.Repeat, wrapModeV = TextureWrapMode.Clamp, name = "Surf" };
            var pixels = new Color32[w * h];
            for (var y = 0; y < h; y++)
            {
                var v = (float)y / (h - 1);
                for (var x = 0; x < w; x++)
                {
                    var u = (float)x / w * Mathf.PI * 2f;
                    var broken = 0.82f + 0.12f * Mathf.Sin(u * 3f) + 0.06f * Mathf.Sin(u * 7f + 1.3f);
                    // Edge line near the shore plus a fainter second wave line further out.
                    var line = Mathf.Exp(-Mathf.Pow((v - 0.8f) / 0.1f, 2f)) + 0.5f * Mathf.Exp(-Mathf.Pow((v - 0.4f) / 0.08f, 2f)) * broken;
                    var fade = Mathf.SmoothStep(0f, 1f, Mathf.Min(v / 0.25f, (1f - v) / 0.12f));
                    pixels[y * w + x] = new Color(1f, 1f, 1f, Mathf.Clamp01(0.85f * line * fade * broken));
                }
            }

            texture.SetPixels32(pixels);
            texture.Apply(true);
            return texture;
        }

        /// <summary>Boulders along rocky stretches of the natural shore, near the yard (detail area), one combined mesh.</summary>
        private void BuildBoulders(List<CoastSegment> coast, ShipPalette palette, TerrainModel terrain)
        {
            var random = new System.Random(17);
            var models = new[] { "rock_a", "rock_b", "rock_c" };
            var placements = models.ToDictionary(m => m, m => new List<Matrix4x4>());
            foreach (var s in coast.Where(s => s.A.magnitude < 1600f))
            {
                var length = Vector2.Distance(s.A, s.B);
                for (var k = 0; k < Mathf.CeilToInt(length / 6f); k++)
                {
                    if (random.NextDouble() < 0.45)
                    {
                        continue;
                    }

                    var p = Vector2.Lerp(s.A, s.B, (float)random.NextDouble()) + s.Sea * (float)(random.NextDouble() * 5.0 - 2.5);
                    var size = (float)(0.8 + random.NextDouble() * random.NextDouble() * 3.2);
                    var rotation = Quaternion.Euler((float)random.NextDouble() * 20f - 10f, (float)random.NextDouble() * 360f, (float)random.NextDouble() * 20f - 10f);
                    var position = ShipCoordinates.YardToUnity(p.x, p.y, YardLayout.SeaLevel - 0.4f * size);
                    placements[models[random.Next(models.Length)]].Add(Matrix4x4.TRS(position, rotation, Vector3.one * size));
                }
            }

            foreach (var pair in placements.Where(x => x.Value.Count > 0))
            {
                PropLibrary.Combine(pair.Key, pair.Value, palette, transform, "Boulders");
            }
        }
    }
}
