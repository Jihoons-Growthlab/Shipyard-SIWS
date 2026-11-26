using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace ShipyardTwin
{
    /// <summary>Small 2D polygon helpers for the paved yard outline (yard metres, counter-clockwise).</summary>
    public static class Polygon2D
    {
        /// <summary>Convex hull (Andrew's monotone chain), counter-clockwise, no repeated last point.</summary>
        public static List<Vector2> ConvexHull(IEnumerable<Vector2> points)
        {
            var p = points.Distinct().OrderBy(v => v.x).ThenBy(v => v.y).ToList();
            if (p.Count < 3)
            {
                return p;
            }

            float Cross(Vector2 o, Vector2 a, Vector2 b) => (a.x - o.x) * (b.y - o.y) - (a.y - o.y) * (b.x - o.x);
            var hull = new List<Vector2>();
            foreach (var pass in new[] { p, Enumerable.Reverse(p).ToList() })
            {
                var start = hull.Count;
                foreach (var q in pass)
                {
                    while (hull.Count >= start + 2 && Cross(hull[hull.Count - 2], hull[hull.Count - 1], q) <= 0)
                    {
                        hull.RemoveAt(hull.Count - 1);
                    }

                    hull.Add(q);
                }

                hull.RemoveAt(hull.Count - 1);
            }

            return hull;
        }

        /// <summary>Sutherland–Hodgman: clips a polygon by a convex counter-clockwise polygon.</summary>
        public static List<Vector2> ClipConvex(List<Vector2> subject, List<Vector2> clip)
        {
            var output = subject;
            for (var i = 0; i < clip.Count && output.Count > 0; i++)
            {
                output = KeepLeft(output, clip[i], clip[(i + 1) % clip.Count]);
            }

            return output;
        }

        /// <summary>Keeps the part of a polygon left of the directed line a→b (the inside of a counter-clockwise edge).</summary>
        public static List<Vector2> KeepLeft(List<Vector2> input, Vector2 a, Vector2 b)
        {
            float Side(Vector2 p) => (b.x - a.x) * (p.y - a.y) - (b.y - a.y) * (p.x - a.x);
            var output = new List<Vector2>();
            for (var j = 0; j < input.Count; j++)
            {
                Vector2 p = input[j], q = input[(j + 1) % input.Count];
                float dp = Side(p), dq = Side(q);
                if (dq >= 0)
                {
                    if (dp < 0) output.Add(p + (q - p) * (dp / (dp - dq)));
                    output.Add(q);
                }
                else if (dp >= 0)
                {
                    output.Add(p + (q - p) * (dp / (dp - dq)));
                }
            }

            return output;
        }

        /// <summary>Point inside a convex counter-clockwise polygon (edges count as inside).</summary>
        public static bool Contains(List<Vector2> polygon, Vector2 p)
        {
            for (var i = 0; i < polygon.Count; i++)
            {
                Vector2 a = polygon[i], b = polygon[(i + 1) % polygon.Count];
                if ((b.x - a.x) * (p.y - a.y) - (b.y - a.y) * (p.x - a.x) < 0)
                {
                    return false;
                }
            }

            return polygon.Count >= 3;
        }

        /// <summary>Distance from a point to a convex polygon (0 inside).</summary>
        public static float Distance(List<Vector2> polygon, Vector2 p)
        {
            if (Contains(polygon, p))
            {
                return 0f;
            }

            var best = float.MaxValue;
            for (var i = 0; i < polygon.Count; i++)
            {
                Vector2 a = polygon[i], b = polygon[(i + 1) % polygon.Count], ab = b - a;
                var t = Mathf.Clamp01(Vector2.Dot(p - a, ab) / Mathf.Max(ab.sqrMagnitude, 1e-6f));
                best = Mathf.Min(best, Vector2.Distance(p, a + ab * t));
            }

            return best;
        }

        /// <summary>Convex pieces covering a convex polygon minus a convex counter-clockwise hole (empty pieces dropped).</summary>
        public static List<List<Vector2>> Subtract(List<Vector2> subject, List<Vector2> hole)
        {
            var pieces = new List<List<Vector2>>();
            var rest = subject;
            for (var i = 0; i < hole.Count && rest.Count >= 3; i++)
            {
                Vector2 a = hole[i], b = hole[(i + 1) % hole.Count];
                var outside = Clean(KeepLeft(rest, b, a));
                if (outside.Count >= 3 && Area(outside) > 0.01f)
                {
                    pieces.Add(outside);
                }

                rest = KeepLeft(rest, a, b);
            }

            return pieces;
        }

        /// <summary>
        /// Drops repeated points (within 1 cm) and points on a straight run, so a fan over the polygon has no
        /// zero-area triangles (those leave zero normals, which shade as NaN in WebGL).
        /// </summary>
        public static List<Vector2> Clean(List<Vector2> polygon)
        {
            var points = new List<Vector2>(polygon);
            for (var changed = true; changed && points.Count >= 3;)
            {
                changed = false;
                for (var i = 0; i < points.Count && points.Count >= 3; i++)
                {
                    Vector2 a = points[(i + points.Count - 1) % points.Count], b = points[i], c = points[(i + 1) % points.Count];
                    var cross = (b.x - a.x) * (c.y - a.y) - (b.y - a.y) * (c.x - a.x);
                    if ((b - a).sqrMagnitude < 1e-4f || Mathf.Abs(cross) < 1e-3f * Mathf.Max(1f, (c - a).magnitude))
                    {
                        points.RemoveAt(i);
                        changed = true;
                        break;
                    }
                }
            }

            return points;
        }

        /// <summary>Signed area (positive when counter-clockwise).</summary>
        public static float Area(List<Vector2> polygon)
        {
            var area = 0f;
            for (var i = 0; i < polygon.Count; i++)
            {
                Vector2 a = polygon[i], b = polygon[(i + 1) % polygon.Count];
                area += a.x * b.y - b.x * a.y;
            }

            return area / 2f;
        }

        /// <summary>Rotates a point about the origin (degrees, from +x toward +y).</summary>
        public static Vector2 Rotate(Vector2 p, float degrees)
        {
            var a = degrees * Mathf.Deg2Rad;
            float c = Mathf.Cos(a), s = Mathf.Sin(a);
            return new Vector2(p.x * c - p.y * s, p.x * s + p.y * c);
        }

        /// <summary>Rectangle given in a frame centred on centre and turned by heading (x0..x1 along it), counter-clockwise.</summary>
        public static List<Vector2> Oriented(Vector2 centre, float headingDeg, float x0, float x1, float y0, float y1) =>
            Rect(x0, x1, y0, y1).Select(v => centre + Rotate(v, headingDeg)).ToList();

        public static List<Vector2> Rect(float x0, float x1, float y0, float y1) =>
            new List<Vector2> { new Vector2(x0, y0), new Vector2(x1, y0), new Vector2(x1, y1), new Vector2(x0, y1) };

        /// <summary>Flat fan mesh for a convex yard polygon at a height, UVs in metres, facing up.</summary>
        public static Mesh Flat(List<Vector2> polygon, float height)
        {
            var vertices = polygon.Select(v => ShipCoordinates.YardToUnity(v.x, v.y, height)).ToList();
            var triangles = new List<int>();
            for (var i = 1; i + 1 < polygon.Count; i++)
            {
                // Counter-clockwise seen from above stays so in Unity; Unity front faces are clockwise: reverse.
                triangles.AddRange(new[] { 0, i + 1, i });
            }

            var mesh = new Mesh { name = "Ground" };
            mesh.SetVertices(vertices);
            mesh.SetTriangles(triangles, 0);
            mesh.SetUVs(0, vertices.Select(v => new Vector2(v.x, v.z)).ToList());
            mesh.RecalculateNormals();
            mesh.RecalculateTangents();
            mesh.RecalculateBounds();
            return mesh;
        }
    }
}
