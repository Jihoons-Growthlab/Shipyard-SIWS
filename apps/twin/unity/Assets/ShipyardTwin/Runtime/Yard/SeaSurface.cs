using System.Collections.Generic;
using UnityEngine;

namespace ShipyardTwin
{
    /// <summary>
    /// Wave-displaced sea around the camera: one ring mesh (rings packed toward the centre, ~4.6k vertices) that
    /// follows the camera on the sea plane and scales with its height, drawn with the water shader in its "near ring"
    /// role. The flat yard sea is cut out inside the ring (global _SeaNear) and the dock pits are cut out of the ring
    /// (global _SeaHole*), so the two meet without overlap. Per frame: one transform and one global vector.
    /// </summary>
    public sealed class SeaSurface : MonoBehaviour
    {
        public const int Rings = 48;
        public const int Segments = 96;
        public const int MaxHoles = 8;
        private const float Snap = 8f;   // metres: the ring moves in steps so the vertex pattern does not crawl

        private static readonly int NearId = Shader.PropertyToID("_SeaNear");
        private static readonly int HoleAId = Shader.PropertyToID("_SeaHoleA");
        private static readonly int HoleBId = Shader.PropertyToID("_SeaHoleB");
        private static readonly int HoleCountId = Shader.PropertyToID("_SeaHoleCount");

        private Camera _camera;
        private float _level;

        /// <summary>Unit ring (radius 1) on the xz plane, rings spaced quadratically: dense near the centre.</summary>
        public static Mesh BuildRing()
        {
            var vertices = new List<Vector3> { Vector3.zero };
            for (var r = 1; r <= Rings; r++)
            {
                var t = (float)r / Rings;
                var radius = t * t;
                for (var s = 0; s < Segments; s++)
                {
                    var a = s * Mathf.PI * 2f / Segments;
                    vertices.Add(new Vector3(Mathf.Cos(a) * radius, 0f, Mathf.Sin(a) * radius));
                }
            }

            var triangles = new List<int>();
            for (var s = 0; s < Segments; s++)
            {
                triangles.AddRange(new[] { 0, 1 + (s + 1) % Segments, 1 + s });   // clockwise seen from above
            }

            for (var r = 1; r < Rings; r++)
            {
                int inner = 1 + (r - 1) * Segments, outer = 1 + r * Segments;
                for (var s = 0; s < Segments; s++)
                {
                    int s1 = (s + 1) % Segments;
                    triangles.AddRange(new[] { inner + s, inner + s1, outer + s });
                    triangles.AddRange(new[] { inner + s1, outer + s1, outer + s });
                }
            }

            var mesh = new Mesh { name = "Sea ring" };
            mesh.SetVertices(vertices);
            mesh.SetTriangles(triangles, 0);
            mesh.RecalculateNormals();
            // Displacement happens in the shader: keep it out of frustum culling by a generous box.
            mesh.bounds = new Bounds(Vector3.zero, new Vector3(2.2f, 6f, 2.2f));
            return mesh;
        }

        /// <summary>Creates the ring under parent with its own water material (role 1) and registers the dock pits.</summary>
        public static SeaSurface Create(Transform parent, Material water, IReadOnlyList<Facility> docks, float level)
        {
            var go = new GameObject("Sea (waves)");
            go.transform.SetParent(parent, false);
            go.AddComponent<MeshFilter>().sharedMesh = BuildRing();
            var material = new Material(water) { name = "water (near)" };
            material.SetFloat("_SeaRole", 1f);
            var renderer = go.AddComponent<MeshRenderer>();
            renderer.sharedMaterial = material;
            renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            var sea = go.AddComponent<SeaSurface>();
            sea._level = level;
            SetHoles(docks);
            return sea;
        }

        /// <summary>Dock pits (and 4 m beyond their gates, as the flat sea) as rectangles in Unity xz.</summary>
        public static void SetHoles(IReadOnlyList<Facility> docks)
        {
            var a = new Vector4[MaxHoles];
            var b = new Vector4[MaxHoles];
            var n = 0;
            foreach (var dock in docks)
            {
                if (n >= MaxHoles) break;
                var h = dock.heading_deg * Mathf.Deg2Rad;
                var along = new Vector2(Mathf.Cos(h), Mathf.Sin(h));
                var centre = new Vector2(dock.x, dock.y) + along * 2f;   // pit plus 4 m beyond the gate
                a[n] = new Vector4(centre.x, centre.y, dock.size_x_m / 2f + 2f, dock.size_y_m / 2f);
                b[n] = new Vector4(along.x, along.y, 0f, 0f);
                n++;
            }

            Shader.SetGlobalVectorArray(HoleAId, a);
            Shader.SetGlobalVectorArray(HoleBId, b);
            Shader.SetGlobalFloat(HoleCountId, n);
        }

        private void OnDisable() => Shader.SetGlobalVector(NearId, Vector4.zero);

        private void LateUpdate()
        {
            if (_camera == null)
            {
                _camera = Camera.main;
                if (_camera == null) return;
            }

            var eye = _camera.transform.position;
            // Big enough to fill the view's near water, small enough to keep vertices dense: about 3x the height.
            var radius = Mathf.Clamp((eye.y - _level) * 3f, 350f, 2200f);
            var x = Mathf.Round(eye.x / Snap) * Snap;
            var z = Mathf.Round(eye.z / Snap) * Snap;
            transform.SetPositionAndRotation(new Vector3(x, _level, z), Quaternion.identity);
            transform.localScale = new Vector3(radius, 1f, radius);
            Shader.SetGlobalVector(NearId, new Vector4(x, z, radius, 1f));
        }
    }
}
