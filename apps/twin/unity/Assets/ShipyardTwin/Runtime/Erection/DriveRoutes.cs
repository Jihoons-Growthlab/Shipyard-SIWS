using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace ShipyardTwin
{
    /// <summary>
    /// Where a transporter drives between two yard points (yard metres). Where both ends are near the yard's roads
    /// (scenery.json), it leaves the start for the nearest road, follows the road network (shortest path over the
    /// road centre lines and their junctions) and turns off to the end. Elsewhere (a production line without roads)
    /// it drives an L along the coast's axes: first along the docks, then across them, so it keeps to the aprons
    /// behind the dock heads instead of crossing the docks diagonally. Paths are cached by their rounded ends.
    /// </summary>
    public sealed class DriveRoutes
    {
        /// <summary>Farthest a start or end may lie from a road for the road network to be used (m).</summary>
        public const float RoadReach = 70f;

        private readonly List<(Vector2 A, Vector2 B)> _roads;
        private readonly List<Vector2> _nodes = new List<Vector2>();
        private readonly List<List<(int To, float Length)>> _edges = new List<List<(int, float)>>();
        private readonly Func<Vector2, float> _axisAt;
        private readonly Dictionary<(int, int, int, int), Vector2[]> _cache = new Dictionary<(int, int, int, int), Vector2[]>();

        /// <param name="roads">Road centre lines (yard metres).</param>
        /// <param name="axisAt">Heading (deg) of the docks nearest a point: the axes of the L where there are no roads.</param>
        public DriveRoutes(IEnumerable<Road> roads, Func<Vector2, float> axisAt)
        {
            _roads = (roads ?? Enumerable.Empty<Road>()).Select(r => (new Vector2(r.x0, r.y0), new Vector2(r.x1, r.y1)))
                .Where(r => (r.Item2 - r.Item1).sqrMagnitude > 1f).ToList();
            _axisAt = axisAt ?? (_ => 0f);
            // Nodes: road ends and every crossing of two roads; each road is cut at its nodes into graph edges.
            var cuts = _roads.Select(r => new List<float> { 0f, 1f }).ToList();
            for (var i = 0; i < _roads.Count; i++)
            {
                for (var j = i + 1; j < _roads.Count; j++)
                {
                    if (Crossing(_roads[i], _roads[j], out var ti, out var tj))
                    {
                        cuts[i].Add(ti);
                        cuts[j].Add(tj);
                    }
                }
            }

            for (var i = 0; i < _roads.Count; i++)
            {
                var (a, b) = _roads[i];
                var ids = cuts[i].Distinct().OrderBy(t => t).Select(t => Node(Vector2.Lerp(a, b, t))).ToList();
                for (var k = 0; k + 1 < ids.Count; k++)
                {
                    Link(ids[k], ids[k + 1]);
                }
            }
        }

        public int NodeCount => _nodes.Count;

        /// <summary>The polyline from <paramref name="from"/> to <paramref name="to"/> (at least the two ends).</summary>
        public Vector2[] Path(Vector2 from, Vector2 to)
        {
            var key = (Mathf.RoundToInt(from.x), Mathf.RoundToInt(from.y), Mathf.RoundToInt(to.x), Mathf.RoundToInt(to.y));
            if (_cache.TryGetValue(key, out var cached))
            {
                return cached;
            }

            var path = ByRoad(from, to) ?? ByLanes(from, to);
            if (_cache.Count > 4096) _cache.Clear();
            _cache[key] = path;
            return path;
        }

        /// <summary>Point and heading (deg from yard +x toward +y) at a share 0..1 of a path's length.</summary>
        public static (Vector2 Point, float Heading) At(Vector2[] path, float share)
        {
            if (path.Length < 2)
            {
                return (path.Length == 1 ? path[0] : Vector2.zero, 0f);
            }

            return RouteMath.At(path, false, Mathf.Clamp01(share) * RouteMath.Length(path, false) * 0.9999f);
        }

        private Vector2[] ByRoad(Vector2 from, Vector2 to)
        {
            if (_nodes.Count < 2 || !Nearest(from, out var a, out var ra, out var da) || !Nearest(to, out var b, out var rb, out var db) ||
                da > RoadReach || db > RoadReach)
            {
                return null;
            }

            // Entry and exit points are joined to their road's two end nodes, then the shortest path between them.
            var entry = new List<(int Node, float Cost)> { (Node(_roads[ra].A), Vector2.Distance(a, _roads[ra].A)), (Node(_roads[ra].B), Vector2.Distance(a, _roads[ra].B)) };
            var exit = new List<(int Node, float Cost)> { (Node(_roads[rb].A), Vector2.Distance(b, _roads[rb].A)), (Node(_roads[rb].B), Vector2.Distance(b, _roads[rb].B)) };
            var points = new List<Vector2> { from, a };
            if (ra != rb)
            {
                var best = float.MaxValue;
                List<int> route = null;
                foreach (var (n0, c0) in entry)
                {
                    var (dist, prev) = Dijkstra(n0);
                    foreach (var (n1, c1) in exit)
                    {
                        var total = c0 + dist[n1] + c1;
                        if (total < best)
                        {
                            best = total;
                            route = Trace(prev, n0, n1);
                        }
                    }
                }

                if (route == null)
                {
                    return null;
                }

                points.AddRange(route.Select(n => _nodes[n]));
            }

            points.Add(b);
            points.Add(to);
            return Simplify(points);
        }

        private Vector2[] ByLanes(Vector2 from, Vector2 to)
        {
            var heading = _axisAt((from + to) / 2f) * Mathf.Deg2Rad;
            var along = new Vector2(Mathf.Cos(heading), Mathf.Sin(heading));
            var corner = from + along * Vector2.Dot(to - from, along);
            return Simplify(new List<Vector2> { from, corner, to });
        }

        private static Vector2[] Simplify(List<Vector2> points)
        {
            var kept = new List<Vector2>();
            foreach (var p in points)
            {
                if (kept.Count == 0 || (kept[kept.Count - 1] - p).sqrMagnitude > 0.25f) kept.Add(p);
            }

            if (kept.Count == 1) kept.Add(kept[0]);
            return kept.ToArray();
        }

        private bool Nearest(Vector2 p, out Vector2 point, out int road, out float distance)
        {
            point = p;
            road = -1;
            distance = float.MaxValue;
            for (var i = 0; i < _roads.Count; i++)
            {
                var (a, b) = _roads[i];
                var ab = b - a;
                var t = Mathf.Clamp01(Vector2.Dot(p - a, ab) / ab.sqrMagnitude);
                var q = a + ab * t;
                var d = Vector2.Distance(p, q);
                if (d < distance)
                {
                    distance = d;
                    point = q;
                    road = i;
                }
            }

            return road >= 0;
        }

        private (float[] Dist, int[] Prev) Dijkstra(int source)
        {
            var dist = Enumerable.Repeat(float.MaxValue, _nodes.Count).ToArray();
            var prev = Enumerable.Repeat(-1, _nodes.Count).ToArray();
            var done = new bool[_nodes.Count];
            dist[source] = 0f;
            for (var step = 0; step < _nodes.Count; step++)
            {
                var u = -1;
                for (var i = 0; i < _nodes.Count; i++)
                {
                    if (!done[i] && (u < 0 || dist[i] < dist[u])) u = i;
                }

                if (u < 0 || dist[u] == float.MaxValue) break;
                done[u] = true;
                foreach (var (v, length) in _edges[u])
                {
                    if (dist[u] + length < dist[v])
                    {
                        dist[v] = dist[u] + length;
                        prev[v] = u;
                    }
                }
            }

            return (dist, prev);
        }

        private static List<int> Trace(int[] prev, int source, int target)
        {
            var route = new List<int>();
            for (var n = target; n >= 0; n = prev[n])
            {
                route.Add(n);
                if (n == source) break;
            }

            route.Reverse();
            return route.Count > 0 && route[0] == source ? route : null;
        }

        private int Node(Vector2 p)
        {
            for (var i = 0; i < _nodes.Count; i++)
            {
                if ((_nodes[i] - p).sqrMagnitude < 1f) return i;
            }

            _nodes.Add(p);
            _edges.Add(new List<(int, float)>());
            return _nodes.Count - 1;
        }

        private void Link(int a, int b)
        {
            if (a == b) return;
            var length = Vector2.Distance(_nodes[a], _nodes[b]);
            _edges[a].Add((b, length));
            _edges[b].Add((a, length));
        }

        private static bool Crossing((Vector2 A, Vector2 B) p, (Vector2 A, Vector2 B) q, out float tp, out float tq)
        {
            tp = tq = 0f;
            var r = p.B - p.A;
            var s = q.B - q.A;
            var denominator = r.x * s.y - r.y * s.x;
            if (Mathf.Abs(denominator) < 1e-6f) return false;
            var d = q.A - p.A;
            tp = (d.x * s.y - d.y * s.x) / denominator;
            tq = (d.x * r.y - d.y * r.x) / denominator;
            const float slack = 0.02f;   // T-junctions where a road ends on another
            if (tp < -slack || tp > 1f + slack || tq < -slack || tq > 1f + slack) return false;
            tp = Mathf.Clamp01(tp);
            tq = Mathf.Clamp01(tq);
            return true;
        }
    }
}
