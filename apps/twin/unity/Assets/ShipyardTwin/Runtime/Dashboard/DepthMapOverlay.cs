using System;
using System.Collections.Generic;
using System.Linq;
using GrowthLab.Core;
using UnityEngine;

namespace ShipyardTwin
{
    /// <summary>
    /// Surveyed depth map (KHOA 150 m grid, pipeline/fetch_bathymetry.py → Resources/ShipyardTwin/Satellite/
    /// bathymetry_grid.bytes + bathymetry_meta.json)
    /// drawn as a translucent sheet just above the sea: depth bands every <see cref="BandMetres"/> m with contour lines.
    /// Land and the yard are higher, so they hide it; the dock pits are cut out. Toggled by the dashboard's depth button
    /// (<see cref="DashboardShellView.DepthVisibleChanged"/>), built on first use.
    /// </summary>
    public sealed class DepthMapOverlay : MonoBehaviour, ICoreConsumer
    {
        public const float BandMetres = 2f;
        private const int TextureSize = 1536;
        private static readonly int DepthMapVisibleId = Shader.PropertyToID("_ShipyardDepthMapVisible");
        private const float FillAlpha = 0.62f;

        /// <summary>Band colours, shallow to deep; index 0 is ground exposed at the datum (tidal flat, rock).</summary>
        public static readonly Color32[] BandColors =
        {
            new Color32(196, 172, 122, 255),
            new Color32(207, 239, 245, 255),
            new Color32(158, 216, 234, 255),
            new Color32(99, 181, 219, 255),
            new Color32(58, 140, 196, 255),
            new Color32(40, 102, 163, 255),
            new Color32(27, 68, 121, 255),
        };

        private TwinFile _twin;
        private TerrainModel _terrain;
        private float[] _depths;
        private BathymetryMeta _meta;
        private GameObject _sheet;
        private DashboardShellView _shell;

        [Serializable]
        public sealed class BathymetryMeta
        {
            public string format;
            public int grid;
            public string attribution;
            public string datum;
            public int points;
            public float min_m;
            public float max_m;
        }

        public void Construct(CoreContext context)
        {
            if (_shell != null || !context.Services.TryGet(out _twin) || _twin.yard?.site == null)
            {
                return;
            }

            _shell = FindFirstObjectByType<DashboardShellView>();
            if (_shell == null)
            {
                return;
            }

            _terrain = TerrainModel.Load();
            _meta = LoadMeta();
            _depths = LoadDepths(Resources.Load<TextAsset>(SatelliteGround.Folder + "bathymetry_grid"));
            if (_terrain == null || _meta == null || _depths == null || _depths.Length != _terrain.Vertices * _terrain.Vertices)
            {
                _shell.SetDepthLegend("데이터 없음", "측량 수심 자료가 없습니다. pipeline/fetch_bathymetry.py를 실행하세요.", null);
                context.Log.Warning("ShipyardTwin", "No bathymetry grid; run pipeline/fetch_bathymetry.py.");
                return;
            }

            _shell.SetDepthLegend("실측 " + _meta.points + "점",
                "범위 " + _meta.min_m.ToString("0.0") + " ~ " + _meta.max_m.ToString("0.0") + " m, 등심선 " + BandMetres.ToString("0") +
                " m 간격\n" + _meta.datum + "\n출처: " + _meta.attribution, Legend());
            _shell.DepthVisibleChanged += SetVisible;
            SetVisible(_shell.DepthVisible);   // already on (-shipyard-depth)
            context.Log.Info("ShipyardTwin", "Bathymetry: " + _meta.points + " points, " + _meta.min_m + " ~ " + _meta.max_m + " m");
        }

        private void OnDestroy()
        {
            if (_shell != null) _shell.DepthVisibleChanged -= SetVisible;
            Shader.SetGlobalFloat(DepthMapVisibleId, 0f);
        }

        private static BathymetryMeta LoadMeta()
        {
            var asset = Resources.Load<TextAsset>(SatelliteGround.Folder + "bathymetry_meta");
            return asset != null ? JsonUtility.FromJson<BathymetryMeta>(asset.text) : null;
        }

        private static float[] LoadDepths(TextAsset asset)
        {
            if (asset == null)
            {
                return null;
            }

            var raw = asset.bytes;
            var values = new float[raw.Length / 4];
            Buffer.BlockCopy(raw, 0, values, 0, values.Length * 4);   // little-endian float32, NaN = no data
            return values;
        }

        /// <summary>Band index for a depth (0 = exposed ground, then one per <see cref="BandMetres"/>, last band open).</summary>
        public static int Band(float depth) =>
            depth < 0f ? 0 : Mathf.Min(1 + Mathf.FloorToInt(depth / BandMetres), BandColors.Length - 1);

        private static List<(Color Color, string Label)> Legend()
        {
            var bands = new List<(Color, string)> { (BandColors[0], "노출") };
            for (var i = 1; i < BandColors.Length; i++)
            {
                var from = (i - 1) * BandMetres;
                bands.Add((BandColors[i], i == BandColors.Length - 1 ? from.ToString("0") + "+" : from.ToString("0")));
            }

            return bands;
        }

        private void SetVisible(bool visible)
        {
            if (visible && _sheet == null)
            {
                _sheet = BuildSheet();
            }

            var active = visible && _sheet != null;
            if (_sheet != null) _sheet.SetActive(active);
            Shader.SetGlobalFloat(DepthMapVisibleId, active ? 1f : 0f);
        }

        private GameObject BuildSheet()
        {
            var template = Resources.Load<Material>("ShipyardTwin/Materials/Foam");
            if (template == null)
            {
                return null;
            }

            var material = new Material(template) { name = "DepthMap", renderQueue = template.renderQueue + 1 };
            material.SetTexture("_BaseMap", BuildTexture());
            material.SetTextureScale("_BaseMap", Vector2.one);
            material.SetTextureOffset("_BaseMap", Vector2.zero);
            if (material.HasProperty("_BaseColor")) material.SetColor("_BaseColor", Color.white);

            var go = new GameObject("DepthMap");
            go.transform.SetParent(transform, false);
            go.AddComponent<MeshFilter>().sharedMesh = BuildMesh();
            var renderer = go.AddComponent<MeshRenderer>();
            renderer.sharedMaterial = material;
            renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            renderer.receiveShadows = false;
            return go;
        }

        private float Depth(int i, int j)
        {
            var n = _terrain.Vertices;
            return _depths[Mathf.Clamp(j, 0, n - 1) * n + Mathf.Clamp(i, 0, n - 1)];
        }

        /// <summary>Bilinear depth on the grid (gx, gy in cells, row 0 = north), skipping vertices without data.</summary>
        private float Sample(float gx, float gy)
        {
            var i = Mathf.FloorToInt(gx);
            var j = Mathf.FloorToInt(gy);
            float fx = gx - i, fy = gy - j, sum = 0f, weight = 0f;
            void Add(float d, float w)
            {
                if (!float.IsNaN(d) && w > 0f)
                {
                    sum += d * w;
                    weight += w;
                }
            }

            Add(Depth(i, j), (1 - fx) * (1 - fy));
            Add(Depth(i + 1, j), fx * (1 - fy));
            Add(Depth(i, j + 1), (1 - fx) * fy);
            Add(Depth(i + 1, j + 1), fx * fy);
            return weight > 0.25f ? sum / weight : float.NaN;
        }

        /// <summary>Fill colour per depth band, white contour where the band changes. Texture row 0 = south.</summary>
        private Texture2D BuildTexture()
        {
            const int size = TextureSize;
            var grid = _terrain.Meta.grid;
            var bands = new int[size * size];
            for (var y = 0; y < size; y++)
            {
                var gy = (1f - y / (size - 1f)) * grid;
                for (var x = 0; x < size; x++)
                {
                    var d = Sample(x / (size - 1f) * grid, gy);
                    bands[y * size + x] = float.IsNaN(d) ? -1 : Band(d);
                }
            }

            var pixels = new Color32[size * size];
            var line = new Color32(255, 255, 255, 220);
            for (var y = 0; y < size; y++)
            {
                for (var x = 0; x < size; x++)
                {
                    var b = bands[y * size + x];
                    if (b < 0)
                    {
                        continue;   // transparent
                    }

                    var right = x + 1 < size ? bands[y * size + x + 1] : b;
                    var up = y + 1 < size ? bands[(y + 1) * size + x] : b;
                    if ((right >= 0 && right != b) || (up >= 0 && up != b))
                    {
                        pixels[y * size + x] = line;
                        continue;
                    }

                    var c = BandColors[b];
                    c.a = (byte)(FillAlpha * 255f);
                    pixels[y * size + x] = c;
                }
            }

            var texture = new Texture2D(size, size, TextureFormat.RGBA32, true)
            {
                name = "DepthMap", wrapMode = TextureWrapMode.Clamp, filterMode = FilterMode.Trilinear, anisoLevel = 4,
            };
            texture.SetPixels32(pixels);
            texture.Apply(true, true);
            return texture;
        }

        /// <summary>The satellite grid at sea level (+0.12 m), without cells that have no data or lie in a dock pit.</summary>
        private Mesh BuildMesh()
        {
            var meta = _terrain.Meta;
            var n = _terrain.Vertices;
            var grid = meta.grid;
            var cell = (double)meta.size_px / grid;
            var site = _twin.yard.site;
            var pits = YardLayout.Docks(_twin).Select(d => Polygon2D.Oriented(new Vector2(d.x, d.y), d.heading_deg,
                -d.size_x_m / 2f - 8f, d.size_x_m / 2f + 8f, -d.size_y_m / 2f - 8f, d.size_y_m / 2f + 8f)).ToList();
            var vertices = new Vector3[n * n];
            var uvs = new Vector2[n * n];
            var yard = new Vector2[n * n];
            for (var j = 0; j < n; j++)
            {
                for (var i = 0; i < n; i++)
                {
                    var (lat, lon) = ShipCoordinates.MercatorPixelToGeo(meta.zoom, meta.tile_x0 * 256.0 + i * cell, meta.tile_y0 * 256.0 + j * cell);
                    var p = ShipCoordinates.GeoToYard(site, lat, lon);
                    yard[j * n + i] = p;
                    vertices[j * n + i] = ShipCoordinates.YardToUnity(p.x, p.y, YardLayout.SeaLevel + 0.12f);
                    uvs[j * n + i] = new Vector2(i / (float)grid, 1f - j / (float)grid);
                }
            }

            var triangles = new List<int>();
            for (var j = 0; j < grid; j++)
            {
                for (var i = 0; i < grid; i++)
                {
                    int a = j * n + i, b = a + 1, c = a + n, d = c + 1;
                    if (float.IsNaN(_depths[a]) && float.IsNaN(_depths[b]) && float.IsNaN(_depths[c]) && float.IsNaN(_depths[d]))
                    {
                        continue;
                    }

                    var centre = (yard[a] + yard[d]) / 2f;
                    if (pits.Any(r => Polygon2D.Contains(r, centre)))
                    {
                        continue;
                    }

                    // Face up in Unity (yard x/y map to Unity x/z; image rows run north to south).
                    triangles.AddRange(new[] { a, b, d, a, d, c });
                }
            }

            var mesh = new Mesh { name = "DepthMap", indexFormat = UnityEngine.Rendering.IndexFormat.UInt32 };
            mesh.SetVertices(vertices);
            mesh.SetUVs(0, uvs);
            // Unity front faces have normal Cross(b - a, c - a); flip the winding if the mapping made it face down.
            if (Vector3.Cross(vertices[1] - vertices[0], vertices[n + 1] - vertices[0]).y < 0f)
            {
                for (var t = 0; t < triangles.Count; t += 3)
                {
                    (triangles[t + 1], triangles[t + 2]) = (triangles[t + 2], triangles[t + 1]);
                }
            }

            mesh.SetTriangles(triangles, 0);
            mesh.RecalculateNormals();

            mesh.RecalculateBounds();
            return mesh;
        }
    }
}
