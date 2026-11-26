using System;
using System.Collections.Generic;
using System.Linq;
using GrowthLab.Core;
using UnityEngine;

namespace ShipyardTwin
{
    /// <summary>
    /// Yard dimensions shared by the environment, crane and camera (metres, Unity axes). Docks come from the ERP
    /// facility footprints (yard plan); without a plan the placement dock falls back to the hull size.
    /// </summary>
    public static class YardLayout
    {
        /// <summary>Keel-block height: the dock floor sits this far below the ship baseline (y = 0).</summary>
        public const float KeelBlockHeight = 1.8f;

        public const float DockFloor = -KeelBlockHeight;
        public const float QuayLevel = 12f;
        public const float WallThickness = 6f;
        public const float SeaLevel = 6f;

        /// <summary>The facility the hull sits in (placement.dock), if the yard plan has it with a footprint.</summary>
        public static Facility PlacementDock(TwinFile twin) =>
            twin.yard?.facilities?.FirstOrDefault(f => f.location == twin.placement.dock && f.HasFootprint);

        /// <summary>Half length of the dock pit (inside the walls).</summary>
        public static float PitHalfLength(TwinFile twin)
        {
            var dock = PlacementDock(twin);
            return dock != null ? dock.size_x_m / 2f - WallThickness : twin.hull.length_m / 2f + 32f;
        }

        /// <summary>Half width of the dock pit (inside the walls).</summary>
        public static float PitHalfWidth(TwinFile twin)
        {
            var dock = PlacementDock(twin);
            return dock != null ? dock.size_y_m / 2f - WallThickness : 44f;
        }

        /// <summary>Dock centre in Unity: the yard plan footprint, else dock_position, else the hull's midship.</summary>
        public static Vector3 DockCentre(TwinFile twin)
        {
            var facility = PlacementDock(twin);
            if (facility != null)
            {
                return ShipCoordinates.YardToUnity(facility.x, facility.y, 0f);
            }

            var dock = twin.placement.dock_position;
            return dock != null && !string.IsNullOrEmpty(dock.location)
                ? ShipCoordinates.YardToUnity(dock.x, dock.y, 0f)
                : ShipCoordinates.YardToUnity(twin.placement.origin_x + twin.hull.length_m / 2f, twin.placement.origin_y, 0f);
        }

        /// <summary>Docks with footprints; the placement dock is synthesised from the hull when the plan lacks it.</summary>
        public static List<Facility> Docks(TwinFile twin)
        {
            var docks = (twin.yard?.facilities ?? new Facility[0]).Where(f => f.kind == "도크" && f.HasFootprint).ToList();
            if (PlacementDock(twin) == null)
            {
                var centre = DockCentre(twin);
                docks.Add(new Facility
                {
                    location = twin.placement.dock, kind = "도크", name = twin.placement.dock, x = centre.x, y = centre.z,
                    size_x_m = (twin.hull.length_m / 2f + 32f + WallThickness) * 2f, size_y_m = (44f + WallThickness) * 2f,
                });
            }

            return docks;
        }

        /// <summary>
        /// Docks that face the same way and lie near one another (600 m), each with the facilities closest to it: one
        /// paved site and one shoreline (the line of their gates) per zone. The main yard (gates towards +x) is one
        /// zone; a line built on another stretch of coast is another.
        /// </summary>
        public static List<YardZone> Zones(TwinFile twin)
        {
            var zones = new List<YardZone>();
            foreach (var dock in Docks(twin))
            {
                var zone = zones.FirstOrDefault(z => Mathf.Abs(Mathf.DeltaAngle(z.Heading, dock.heading_deg)) < 1f &&
                                                     z.Docks.Any(d => Vector2.Distance(new Vector2(d.x, d.y), new Vector2(dock.x, dock.y)) < 600f));
                if (zone == null)
                {
                    zones.Add(zone = new YardZone(dock.heading_deg));
                }

                zone.Docks.Add(dock);
            }

            foreach (var f in (twin.yard?.facilities ?? new Facility[0]).Where(f => f.HasFootprint && f.kind != "도크"))
            {
                zones.OrderBy(z => z.Docks.Min(d => (new Vector2(d.x - f.x, d.y - f.y)).sqrMagnitude)).First().Others.Add(f);
            }

            var placement = zones.FindIndex(z => z.Docks.Any(d => d.location == twin.placement.dock));
            if (placement > 0)
            {
                zones.Insert(0, zones[placement]);
                zones.RemoveAt(placement + 1);
            }

            return zones;
        }

        /// <summary>Local x of the placement dock's shoreline: the seaward end (gate) of its zone's furthest dock.</summary>
        public static float Shoreline(TwinFile twin) => Zones(twin)[0].Shore;

        /// <summary>Margin of the paved site around the land footprints (m).</summary>
        public const float SiteMargin = 28f;

        /// <summary>Land footprints of the placement dock's zone, in its local axes (see <see cref="LandFootprints(YardZone)"/>).</summary>
        public static List<Facility> LandFootprints(TwinFile twin) => LandFootprints(Zones(twin)[0]);

        /// <summary>
        /// Footprints a zone's paved site is built from, in its local axes: docks, shops, yards, and a 1 m strip at the
        /// root of every quay (pier) that starts at the shoreline, so the paving runs out to the pier deck instead of
        /// stopping short of it with water in between. Mirrored in pipeline/generate_scenery.py (land_footprints).
        /// </summary>
        public static List<Facility> LandFootprints(YardZone zone)
        {
            var shore = zone.Shore;
            var others = zone.Others.Select(zone.Local).ToList();
            var land = others.Where(f => f.kind != "안벽").Concat(zone.Docks.Select(zone.Local)).ToList();
            foreach (var quay in others.Where(f => f.kind == "안벽"))
            {
                if (Mathf.Abs(Mathf.DeltaAngle(quay.heading_deg, 0f)) < 1f && Mathf.Abs(quay.x - quay.size_x_m / 2f - shore) <= SiteMargin)
                {
                    land.Add(new Facility
                    {
                        location = quay.location + "-ROOT", kind = "안벽", name = quay.name, x = shore - 0.5f, y = quay.y,
                        size_x_m = 1f, size_y_m = quay.size_y_m,
                    });
                }
            }

            return land;
        }

        /// <summary>Footprint corners (counter-clockwise), grown by a margin, honouring the facility heading.</summary>
        public static List<Vector2> Corners(Facility f, float margin = 0f) =>
            Polygon2D.Oriented(new Vector2(f.x, f.y), f.heading_deg, -f.size_x_m / 2f - margin, f.size_x_m / 2f + margin,
                -f.size_y_m / 2f - margin, f.size_y_m / 2f + margin);
    }

    /// <summary>
    /// Part of the yard on one stretch of coast (<see cref="YardLayout.Zones"/>). Local axes are the yard axes turned
    /// by the docks' heading, so +x is seaward and the gates lie on x = <see cref="Shore"/>; for the main yard
    /// (heading 0) local and yard coordinates are the same.
    /// </summary>
    public sealed class YardZone
    {
        public YardZone(float heading)
        {
            Heading = heading;
        }

        public float Heading { get; }

        /// <summary>Docks of the zone (yard coordinates).</summary>
        public List<Facility> Docks { get; } = new List<Facility>();

        /// <summary>Shops, yards and quays nearest to this zone's docks (yard coordinates).</summary>
        public List<Facility> Others { get; } = new List<Facility>();

        public float Shore => Docks.Select(Local).Max(d => d.x + d.size_x_m / 2f);

        public Vector2 ToLocal(Vector2 p) => Polygon2D.Rotate(p, -Heading);

        public Vector2 ToWorld(Vector2 p) => Polygon2D.Rotate(p, Heading);

        /// <summary>A copy of a facility in local axes.</summary>
        public Facility Local(Facility f)
        {
            var c = ToLocal(new Vector2(f.x, f.y));
            return new Facility
            {
                location = f.location, name = f.name, kind = f.kind, x = c.x, y = c.y, z = f.z,
                heading_deg = Mathf.Repeat(f.heading_deg - Heading, 360f),
                size_x_m = f.size_x_m, size_y_m = f.size_y_m, size_z_m = f.size_z_m, lat = f.lat, lon = f.lon,
            };
        }
    }

    /// <summary>
    /// Builds the yard from the exported ERP yard plan: every dock as a pit with walls, working altars and a gate;
    /// keel and side blocks under the docked hull; shops and warehouses as halls; yards as paved areas with staged
    /// blocks; fitting-out quays as jetties; quay ground around the pits and the sea beyond the shoreline.
    /// Also sets sunlight shadows, trilight ambient and distance fog.
    /// </summary>
    public sealed class YardEnvironment : MonoBehaviour, ICoreConsumer
    {
        [SerializeField] private Color _fogColor = new Color(0.74f, 0.80f, 0.86f);

        private ShipPalette _palette;
        private Transform _buildingDisplay, _hullDisplay, _blockDisplay;

        public void Construct(CoreContext context)
        {
            if (_palette != null || !context.Services.TryGet(out _palette) || !context.Services.TryGet<TwinFile>(out var twin))
            {
                return;
            }

            context.Services.TryGet<YardDisplayLayers>(out var layers);
            _buildingDisplay = layers?.ParentFor(transform, YardDisplayLayer.Buildings) ?? transform;
            _hullDisplay = layers?.ParentFor(transform, YardDisplayLayer.Hulls) ?? transform;
            _blockDisplay = layers?.ParentFor(transform, YardDisplayLayer.Blocks) ?? transform;
            Lighting();
            transform.position = Vector3.zero;
            var docks = YardLayout.Docks(twin);
            foreach (var dock in docks)
            {
                BuildDock(dock);
            }

            // Rail cranes (ERP crane_rails) keep the perimeter wall clear of their rails, extended by the overrun.
            if (context.Services.TryGet<YardDataSource>(out var rails) && rails.Model?.Source?.crane_rails != null)
            {
                foreach (var r in rails.Model.Source.crane_rails)
                {
                    Vector2 a = new Vector2(r.rail_x0, r.rail_y0), b = new Vector2(r.rail_x1, r.rail_y1);
                    var dir = (b - a).sqrMagnitude > 1e-4f ? (b - a).normalized : Vector2.zero;
                    _craneRails.Add((a - dir * 12f, b + dir * 12f, r.gauge_m / 2f + 3f));
                }
            }

            if (context.Services.TryGet<YardDataSource>(out var source) && source.Model.Hulls.Count > 0)
            {
                // Hulls that follow one another on the same berth share one set of keel and side blocks.
                foreach (var hull in source.Model.Hulls.GroupBy(h => (h.Placement.dock, Mathf.Round(h.Placement.origin_x), Mathf.Round(h.Placement.origin_y)))
                             .Select(g => g.OrderByDescending(h => h.Info.length_m).First()))
                {
                    BuildSupports(hull.Info, hull.Placement);
                }
            }
            else
            {
                BuildSupports(twin.hull, twin.placement);
            }

            BuildGround(twin, docks);
            foreach (var facility in twin.yard.facilities.Where(f => f.HasFootprint && f.kind != "도크"))
            {
                BuildFacility(facility);
            }
        }

        /// <summary>
        /// Sky, ambient, reflections, shadows and post-processing come from the URP setup baked into the scene
        /// (Editor/ShipyardTwinRendering). Here: distance fog matched to the sky horizon and a refreshed ambient probe.
        /// </summary>
        private void Lighting()
        {
            RenderSettings.fog = true;
            RenderSettings.fogMode = FogMode.Linear;
            RenderSettings.fogColor = _fogColor;
            RenderSettings.fogStartDistance = 1200f;
            RenderSettings.fogEndDistance = 7000f;
            var camera = Camera.main;
            if (camera != null)
            {
                // The orbit camera covers kilometres of yard while rendering thin surface markings.
                camera.nearClipPlane = 1f;
                camera.farClipPlane = 9000f;
                if (RenderSettings.skybox == null)
                {
                    camera.clearFlags = CameraClearFlags.SolidColor;
                    camera.backgroundColor = _fogColor;
                }
            }

            DynamicGI.UpdateEnvironment();
        }

        private void BuildDock(Facility dock)
        {
            var root = new GameObject(dock.location + " " + dock.name).transform;
            root.SetParent(transform, false);
            root.SetPositionAndRotation(ShipCoordinates.YardToUnity(dock.x, dock.y, 0f), ShipCoordinates.HeadingToUnity(dock.heading_deg));
            GameObject Box(string name, Vector3 position, Vector3 size, string paint) => this.Box(name, root, position, size, paint);   // dock axes: +x to the gate
            var c = Vector3.zero;
            var wall = YardLayout.WallThickness;
            var halfL = dock.size_x_m / 2f - wall;
            var halfW = dock.size_y_m / 2f - wall;
            float floor = YardLayout.DockFloor, quay = YardLayout.QuayLevel;
            Box("DockFloor", c + new Vector3(0, floor - 1f, 0), new Vector3(halfL * 2 + 2 * wall, 2f, halfW * 2 + 2 * wall), "dock_floor");
            foreach (var side in new[] { -1f, 1f })
            {
                YardWalkSurface.Mark(Box("DockWall", c + new Vector3(0, (floor + quay) / 2, side * (halfW + wall / 2)),
                    new Vector3(halfL * 2 + 2 * wall, quay - floor, wall), "concrete"));
                Box("DockAltar", c + new Vector3(0, floor + 3f, side * (halfW - 1.5f)), new Vector3(halfL * 2, 6f, 3f), "concrete");
                YardWalkSurface.Mark(Box("DockEdge", c + new Vector3(0, quay + 0.15f, side * (halfW + 0.3f)),
                    new Vector3(halfL * 2, 0.3f, 0.6f), "marking"));
            }

            YardWalkSurface.Mark(Box("DockHead", c + new Vector3(-halfL - wall / 2, (floor + quay) / 2, 0),
                new Vector3(wall, quay - floor, halfW * 2), "concrete"));
            Box("DockGate", c + new Vector3(halfL + 2f, (floor + quay) / 2, 0), new Vector3(4f, quay - floor + 1f, halfW * 2 + 2f), "crane_dark");
            Box("GateBand", c + new Vector3(halfL - 0.05f, quay - 2f, 0), new Vector3(0.2f, 1.2f, halfW * 2), "crane_red");
            HoverTarget.Attach(root.gameObject, "시설", HoverTarget.FacilityTitle(dock), FacilityDetail(dock)).Identify(TargetCategory.Facility, dock.location);
        }

        private static string FacilityDetail(Facility f) =>
            (string.IsNullOrEmpty(f.kind) ? string.Empty : f.kind + " · ") + f.location +
            (f.HasFootprint ? " · " + f.size_x_m.ToString("0") + " × " + f.size_y_m.ToString("0") + " m" : string.Empty);

        private void BuildSupports(HullInfo info, HullPlacement placement)
        {
            var pose = ShipCoordinates.HullPose(placement);
            var root = new GameObject("KeelBlocks " + info.hull).transform;
            root.SetParent(_hullDisplay, false);
            root.SetPositionAndRotation(pose.position, pose.rotation);
            var length = info.length_m;
            var h = YardLayout.KeelBlockHeight;
            // Keel blocks under the centre line; side (bilge) blocks under the flat bottom of the parallel body.
            for (var x = length * 0.06f; x < length * 0.96f; x += 2.4f)
            {
                Box("Keel", root, new Vector3(x, -h / 2, 0), new Vector3(1.4f, h, 2.2f), "keel_block");
            }

            var side = info.beam_m / 2f - 6f;
            for (var x = length * 0.10f; x < length * 0.86f; x += 6f)
            {
                foreach (var s in new[] { -1f, 1f })
                {
                    Box("Side", root, new Vector3(x, -h / 2, s * side), new Vector3(1.4f, h, 1.6f), "keel_block");
                }
            }

            // The supports remain clickable, but searches frame the assembled hull at its current position.
            ShipView.AttachShipTarget(root.gameObject, info).ExcludeFromSearch = true;
        }

        /// <summary>
        /// Quay ground per zone (<see cref="YardLayout.Zones"/>) as strips around the dock pits (pits run to the
        /// zone's shoreline), worked out in the zone's local axes, then the sea around everything. With satellite
        /// imagery and a geo-referenced site, the ground is the paved site only: the hull around the zone's land
        /// facilities (grown by 28 m, cut at its shoreline) with a revetment down to the sea, so the imagery shows the
        /// real surroundings.
        /// </summary>
        private void BuildGround(TwinFile twin, List<Facility> docks)
        {
            var satellite = twin.yard.site != null && !string.IsNullOrEmpty(twin.yard.site.id) &&
                            Resources.Load<TextAsset>(SatelliteGround.Folder + "satellite_meta") != null;
            foreach (var zone in YardLayout.Zones(twin))
            {
                var shore = zone.Shore;
                var land = YardLayout.LandFootprints(zone);
                var corners = land.SelectMany(f => YardLayout.Corners(f)).ToList();
                var apron = satellite ? SiteOutline(land, shore) : null;
                var x0 = (apron?.Min(v => v.x) ?? corners.Min(v => v.x) - 250f) - 1f;
                var y0 = (apron?.Min(v => v.y) ?? corners.Min(v => v.y) - 250f) - 1f;
                var y1 = (apron?.Max(v => v.y) ?? corners.Max(v => v.y) + 250f) + 1f;
                var cursor = y0;
                var local = zone.Docks.Select(zone.Local).OrderBy(d => d.y).ToList();
                foreach (var dock in local)
                {
                    float dy0 = dock.y - dock.size_y_m / 2f, dy1 = dock.y + dock.size_y_m / 2f;
                    float dx0 = dock.x - dock.size_x_m / 2f, dx1 = dock.x + dock.size_x_m / 2f;
                    Ground(zone, x0, shore, cursor, dy0, apron);
                    Ground(zone, x0, dx0, dy0, dy1, apron);
                    Ground(zone, dx1, shore, dy0, dy1, apron);
                    cursor = Mathf.Max(cursor, dy1);
                }

                Ground(zone, x0, shore, cursor, y1, apron);
                if (apron != null)
                {
                    Revetment(zone, apron, local, shore, TerrainModel.Load(), twin.yard.site, SatelliteGround.DredgeBand(zone, apron));
                    continue;
                }

                // No imagery: land beyond the plan fades into the fog as a wide apron behind the yard.
                const float depth = 26f;
                var centre = zone.ToWorld(new Vector2(x0 - 1500f, (y0 + y1) / 2f));
                var hinterland = Box("Hinterland", ShipCoordinates.YardToUnity(centre.x, centre.y, YardLayout.QuayLevel - depth / 2),
                    new Vector3(3000f, depth, (y1 - y0) + 4000f), "hinterland");
                hinterland.transform.rotation = ShipCoordinates.HeadingToUnity(zone.Heading);
                HoverTarget.Occluder(hinterland);
                YardWalkSurface.Mark(hinterland);
            }

            Sea(docks);
        }

        /// <summary>
        /// Sea surface everywhere around the site (bays inland of the shoreline included, seen through the satellite
        /// imagery's transparent water) except over the dock pits and 4 m beyond their gates, which are dry below sea level.
        /// </summary>
        private void Sea(List<Facility> docks)
        {
            const float reach = 6000f;
            float cx = docks.Average(d => d.x), cy = docks.Average(d => d.y);
            var pieces = new List<List<Vector2>> { Polygon2D.Rect(cx - reach, cx + reach, cy - reach, cy + reach) };
            foreach (var dock in docks)
            {
                var pit = Polygon2D.Oriented(new Vector2(dock.x, dock.y), dock.heading_deg, -dock.size_x_m / 2f, dock.size_x_m / 2f + 4f,
                    -dock.size_y_m / 2f, dock.size_y_m / 2f);
                pieces = pieces.SelectMany(piece => Polygon2D.Subtract(piece, pit)).ToList();
            }

            foreach (var piece in pieces)
            {
                var go = new GameObject("Sea");
                go.transform.SetParent(transform, false);
                go.AddComponent<MeshFilter>().sharedMesh = Polygon2D.Flat(piece, YardLayout.SeaLevel);
                var renderer = go.AddComponent<MeshRenderer>();
                renderer.sharedMaterial = _palette.Get("water");
                renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                HoverTarget.Occluder(go);
            }

            // Swell near the camera (the animated water shader only; the plain fallback material has no ring role).
            var water = _palette.Get("water");
            if (water.shader != null && water.shader.name == "ShipyardTwin/Water")
            {
                SeaSurface.Create(transform, water, docks, YardLayout.SeaLevel);
            }
        }

        /// <summary>Paved site outline of the yard plan (<see cref="YardLayout.LandFootprints"/>).</summary>
        public static List<Vector2> SiteOutline(TwinFile twin) => SiteOutline(YardLayout.Zones(twin)[0]);

        /// <summary>A zone's paved site outline in its local axes.</summary>
        public static List<Vector2> SiteOutline(YardZone zone) => SiteOutline(YardLayout.LandFootprints(zone), zone.Shore);

        /// <summary>Paved site outline: convex hull of land footprints grown by 28 m, cut at the shoreline.</summary>
        public static List<Vector2> SiteOutline(IEnumerable<Facility> land, float shore, float margin = YardLayout.SiteMargin)
        {
            var corners = land.SelectMany(f => YardLayout.Corners(f, margin));
            return Polygon2D.ClipConvex(Polygon2D.ConvexHull(corners), Polygon2D.Rect(-1e5f, shore, -1e5f, 1e5f));
        }

        /// <summary>Quay ground over a rectangle of a zone's local axes (clipped to its paved site when there is one).</summary>
        private void Ground(YardZone zone, float x0, float x1, float y0, float y1, List<Vector2> apron)
        {
            if (x1 - x0 < 0.5f || y1 - y0 < 0.5f)
            {
                return;
            }

            if (apron == null)
            {
                const float depth = 26f;
                var centre = zone.ToWorld(new Vector2((x0 + x1) / 2f, (y0 + y1) / 2f));
                var quay = Box("Quay", ShipCoordinates.YardToUnity(centre.x, centre.y, YardLayout.QuayLevel - depth / 2), new Vector3(x1 - x0, depth, y1 - y0), "concrete");
                quay.transform.rotation = ShipCoordinates.HeadingToUnity(zone.Heading);
                HoverTarget.Occluder(quay);
                YardWalkSurface.Mark(quay);
                return;
            }

            var piece = Polygon2D.ClipConvex(Polygon2D.Rect(x0, x1, y0, y1), apron);
            if (piece.Count < 3)
            {
                return;
            }

            var go = new GameObject("Quay");
            go.transform.SetParent(transform, false);
            go.AddComponent<MeshFilter>().sharedMesh = Polygon2D.Flat(piece.Select(zone.ToWorld).ToList(), YardLayout.QuayLevel);
            go.AddComponent<MeshRenderer>().sharedMaterial = _palette.Get("concrete");
            HoverTarget.Occluder(go);
            YardWalkSurface.Mark(go);
        }

        /// <summary>
        /// Quay wall from quay level to below the sea along the shoreline (berths), leaving the dock gates open; other
        /// sea-facing edges (outside is water in the terrain) get a sloped rubble mound; land-facing edges get graded terrain.
        /// </summary>
        private void Revetment(YardZone zone, List<Vector2> outline, List<Facility> docks, float shore, TerrainModel terrain, YardSite site, (float, float) band)
        {
            // outline, docks and shore are in the zone's local axes; meshes are built in yard coordinates.
            bool Seaward(Vector2 p, Vector2 q) => IsSeawardEdge(zone, p, q, terrain, site, band, shore);
            Vector3 At(Vector2 v, float h)
            {
                var w = zone.ToWorld(v);
                return ShipCoordinates.YardToUnity(w.x, w.y, h);
            }


            var vertices = new List<Vector3>();
            var triangles = new List<int>();
            // Both faces are drawn, each on its own vertices: front and back triangles sharing vertices would make
            // RecalculateNormals cancel to zero, and a zero normal turns into NaN in the WebGL shader (bloom then
            // smears it into large white blotches).
            void TwoSided(params Vector3[] polygon)
            {
                foreach (var face in new[] { polygon, polygon.Reverse().ToArray() })
                {
                    var start = vertices.Count;
                    vertices.AddRange(face);
                    for (var i = 1; i + 1 < face.Length; i++)
                    {
                        triangles.AddRange(new[] { start, start + i, start + i + 1 });
                    }
                }
            }

            void Wall(Vector2 p, Vector2 q) =>
                TwoSided(At(p, YardLayout.QuayLevel), At(q, YardLayout.QuayLevel), At(q, YardLayout.SeaLevel - 3f), At(p, YardLayout.SeaLevel - 3f));

            var mound = new List<(Vector2 P, Vector2 Q)>();
            var landward = new List<int>();   // outline edges (by start index) that meet graded ground
            for (var i = 0; i < outline.Count; i++)
            {
                Vector2 p = outline[i], q = outline[(i + 1) % outline.Count];
                if (Mathf.Abs(p.x - shore) > 0.01f || Mathf.Abs(q.x - shore) > 0.01f)
                {
                    if (Seaward(p, q))
                    {
                        mound.Add((p, q));
                    }
                    else
                    {
                        landward.Add(i);
                    }

                    continue;
                }

                // Shoreline edge: wall only between the dock gates.
                float lo = Mathf.Min(p.y, q.y), hi = Mathf.Max(p.y, q.y), y = lo;
                foreach (var dock in docks.OrderBy(d => d.y))
                {
                    float g0 = dock.y - dock.size_y_m / 2f, g1 = dock.y + dock.size_y_m / 2f;
                    if (g0 > y)
                    {
                        Wall(new Vector2(shore, y), new Vector2(shore, Mathf.Min(g0, hi)));
                    }

                    y = Mathf.Max(y, g1);
                }

                if (hi > y)
                {
                    Wall(new Vector2(shore, y), new Vector2(shore, hi));
                }
            }

            // Where the vertical quay wall meets a sloped mound at a shoreline corner, close the wedge between the
            // wall's end and the slope's side so no gap shows under the paving's corner.
            float toe = YardLayout.SeaLevel - 4f, run = (YardLayout.QuayLevel - toe) * 1.5f;
            foreach (var (p, q) in mound)
            {
                var outward = new Vector2(q.y - p.y, p.x - q.x).normalized * run;
                foreach (var corner in new[] { p, q }.Where(c => Mathf.Abs(c.x - shore) < 0.01f))
                {
                    var foot = corner + outward;                                   // the slope's toe at this corner
                    TwoSided(At(corner, YardLayout.QuayLevel), At(corner, YardLayout.SeaLevel - 3f), At(foot, toe));
                }
            }

            var mesh = new Mesh { name = "Revetment" };
            mesh.SetVertices(vertices);
            mesh.SetTriangles(triangles, 0);
            mesh.SetUVs(0, ObjMeshParser.BoxUvs(vertices, new List<List<int>> { triangles }));
            mesh.RecalculateNormals();
            mesh.RecalculateBounds();
            var go = new GameObject("Revetment");
            go.transform.SetParent(transform, false);
            go.AddComponent<MeshFilter>().sharedMesh = mesh;
            go.AddComponent<MeshRenderer>().sharedMaterial = _palette.Get("wall_base");
            HoverTarget.Occluder(go);
            RubbleMound(mound.Select(e => (zone.ToWorld(e.P), zone.ToWorld(e.Q))).ToList());
            Perimeter(zone, outline, landward, shore, terrain, site, band);
        }

        private readonly List<(Vector2 A, Vector2 B, float Clear)> _craneRails = new List<(Vector2 A, Vector2 B, float Clear)>();

        public const float WallInset = 3f;
        public const float WallHeight = 2.4f;
        private const float PostPitch = 4f;

        /// <summary>
        /// Where the paving meets the countryside: a precast concrete wall (posts every 4 m, panels, coping) just inside
        /// the land-facing edges, open where roads leave the site (the gate), and two staggered rows of trees on the
        /// grass verge outside (SatelliteGround paints the verge). Everything is merged into one wall mesh and one
        /// trunk and one foliage mesh per zone: three draw calls.
        /// </summary>
        private void Perimeter(YardZone zone, List<Vector2> outline, List<int> landward, float shore, TerrainModel terrain, YardSite site,
            (float, float) band)
        {
            if (landward.Count == 0)
            {
                return;
            }

            var count = outline.Count;
            Vector2 Inward(int i)
            {
                Vector2 a = outline[i], b = outline[(i + 1) % count];
                return new Vector2(-(b.y - a.y), b.x - a.x).normalized;   // left of a counter-clockwise edge
            }

            // Wall corners: the outline moved inward by WallInset (offset lines of neighbouring edges meet there).
            Vector2 Inset(int i)
            {
                Vector2 n0 = Inward((i - 1 + count) % count), n1 = Inward(i);
                var bisector = n0 + n1;
                var scale = 1f + Vector2.Dot(n0, n1);
                return outline[i] + (scale > 0.05f ? bisector / scale : n1) * WallInset;
            }

            // Road crossings (scenery roads in yard metres) become openings.
            var roads = new List<Road>();
            var asset = Resources.Load<TextAsset>(YardScenery.Resource);
            if (asset != null)
            {
                try { roads.AddRange(SceneryFile.Parse(asset.text).roads); }
                catch (FormatException) { }
            }

            var wall = new List<CombineInstance>();
            var posts = new List<CombineInstance>();
            void Piece(List<CombineInstance> into, Vector2 yard, float bottom, Vector3 size, float headingDeg)
            {
                into.Add(new CombineInstance
                {
                    mesh = MetricBox.Get(size),
                    transform = Matrix4x4.TRS(ShipCoordinates.YardToUnity(yard.x, yard.y, bottom + size.y / 2f), ShipCoordinates.HeadingToUnity(headingDeg), Vector3.one),
                });
            }

            var trunks = new List<CombineInstance>();
            var leaves = new List<CombineInstance>();
            var trunk = MetricBox.Get(new Vector3(0.35f, 1f, 0.35f));
            var crown = TreeCrown();
            float VergeHeight(Vector2 local, float distance)
            {
                var w = zone.ToWorld(local);
                var natural = SatelliteGround.Dredged(band, shore, local, terrain.AtYard(site, w.x, w.y));
                var t = Mathf.SmoothStep(0f, 1f, distance / 140f);   // SatelliteGround grading (default 140 m)
                return natural <= 0.5f ? float.NaN : Mathf.Lerp(YardLayout.QuayLevel - 0.3f, YardLayout.SeaLevel + natural, t);
            }

            foreach (var i in landward)
            {
                Vector2 a = Inset(i), b = Inset((i + 1) % count);
                Vector2 wa = zone.ToWorld(a), wb = zone.ToWorld(b);
                var length = (wb - wa).magnitude;
                if (length < 2f) continue;
                var along = (wb - wa) / length;
                var heading = Mathf.Atan2(along.y, along.x) * Mathf.Rad2Deg;
                var openings = roads.Select(r => Crossing(wa, wb, new Vector2(r.x0, r.y0), new Vector2(r.x1, r.y1), r.width))
                    .Where(o => o.HasValue).Select(o => o.Value).ToList();
                // Open where a road crosses or runs along the wall, and beside crane rails (portal legs, travel).
                bool Open(float s)
                {
                    var at = wa + along * s;
                    return openings.Any(o => Mathf.Abs(s - o.At) < o.Half) ||
                           roads.Any(r => SegmentDistance(at, new Vector2(r.x0, r.y0), new Vector2(r.x1, r.y1)) < r.width / 2f + 1.5f) ||
                           _craneRails.Any(c => SegmentDistance(at, c.A, c.B) < c.Clear);
                }
                for (var s = 0f; s < length; s += PostPitch)
                {
                    var end = Mathf.Min(s + PostPitch, length);
                    if (!Open(s)) Piece(posts, wa + along * s, YardLayout.QuayLevel, new Vector3(0.45f, WallHeight + 0.35f, 0.45f), heading);
                    var mid = (s + end) / 2f;
                    if (end - s > 0.6f && !Open(mid) && !Open(s) && !Open(end))
                    {
                        Piece(wall, wa + along * mid, YardLayout.QuayLevel, new Vector3(end - s, WallHeight, 0.2f), heading);
                        Piece(posts, wa + along * mid, YardLayout.QuayLevel + WallHeight, new Vector3(end - s, 0.12f, 0.32f), heading);   // coping
                    }
                }

                // Trees on the verge: two staggered rows 9 m and 19 m outside the outline, about every 12 m.
                Vector2 oa = outline[i], ob = outline[(i + 1) % count];
                var outward = -Inward(i);
                var edge = (ob - oa).magnitude;
                for (var row = 0; row < 2; row++)
                {
                    var distance = row == 0 ? 9f : 19f;
                    for (var s = 6f + row * 6f; s < edge - 6f; s += 12f)
                    {
                        var seed = Mathf.Abs(Mathf.Sin((s + 13f * i + 7f * row) * 12.9898f) * 43758.55f) % 1f;
                        var local = oa + (ob - oa) * (s / edge) + outward * (distance + (seed - 0.5f) * 3f);
                        var w = zone.ToWorld(local);
                        if (openings.Any(o => Mathf.Abs(Vector2.Dot(w - wa, along) - o.At) < o.Half + 6f)) continue;
                        var ground = VergeHeight(local, distance);
                        if (float.IsNaN(ground)) continue;
                        var scale = 0.8f + seed * 0.5f;
                        var trunkHeight = 2.2f * scale;
                        trunks.Add(new CombineInstance
                        {
                            mesh = trunk,
                            transform = Matrix4x4.TRS(ShipCoordinates.YardToUnity(w.x, w.y, ground + trunkHeight / 2f - 0.3f), Quaternion.identity,
                                new Vector3(scale, trunkHeight, scale)),
                        });
                        leaves.Add(new CombineInstance
                        {
                            mesh = crown,
                            transform = Matrix4x4.TRS(ShipCoordinates.YardToUnity(w.x, w.y, ground + trunkHeight - 0.5f),
                                Quaternion.Euler(0f, seed * 360f, 0f), Vector3.one * scale),
                        });
                    }
                }
            }

            Merged("Perimeter wall", wall, "fence");
            Merged("Perimeter posts", posts, "fence_post");
            Merged("Verge trees", trunks, "tree_trunk", occluder: false);
            Merged("Verge trees (crowns)", leaves, "tree_leaf", occluder: false);
        }

        private void Merged(string name, List<CombineInstance> parts, string paint, bool occluder = true)
        {
            if (parts.Count == 0) return;
            var mesh = new Mesh { name = name, indexFormat = UnityEngine.Rendering.IndexFormat.UInt32 };
            mesh.CombineMeshes(parts.ToArray(), true, true);
            var go = new GameObject(name);
            go.transform.SetParent(transform, false);
            go.AddComponent<MeshFilter>().sharedMesh = mesh;
            go.AddComponent<MeshRenderer>().sharedMaterial = _palette.Get(paint);
            if (occluder) HoverTarget.Occluder(go);
        }

        public static float SegmentDistance(Vector2 p, Vector2 a, Vector2 b)
        {
            var d = b - a;
            var t = d.sqrMagnitude > 1e-6f ? Mathf.Clamp01(Vector2.Dot(p - a, d) / d.sqrMagnitude) : 0f;
            return (p - (a + d * t)).magnitude;
        }

        /// <summary>Where a road (centre line, width) crosses the wall a→b: distance along the wall and half the opening.</summary>
        public static (float At, float Half)? Crossing(Vector2 a, Vector2 b, Vector2 r0, Vector2 r1, float width)
        {
            var d = b - a;
            var e = r1 - r0;
            var denom = d.x * e.y - d.y * e.x;
            if (Mathf.Abs(denom) < 1e-4f) return null;
            var t = ((r0.x - a.x) * e.y - (r0.y - a.y) * e.x) / denom;
            // The road surface runs half a width past its centre-line ends (YardScenery.Surface).
            var u = ((r0.x - a.x) * d.y - (r0.y - a.y) * d.x) / denom;
            var over = width / 2f / Mathf.Max(e.magnitude, 1e-3f);
            if (t < 0f || t > 1f || u < -over || u > 1f + over) return null;
            return (t * d.magnitude, width / 2f + 2f);
        }

        /// <summary>Broadleaf crown: two stacked octagonal cones (about 5 m across, 6 m tall), base at the origin.</summary>
        public static Mesh TreeCrown()
        {
            var vertices = new List<Vector3>();
            var triangles = new List<int>();
            void Cone(float y0, float y1, float radius)
            {
                const int sides = 8;
                var apex = vertices.Count;
                vertices.Add(new Vector3(0f, y1, 0f));
                var ring = vertices.Count;
                for (var k = 0; k < sides; k++)
                {
                    var a = k * Mathf.PI * 2f / sides;
                    vertices.Add(new Vector3(Mathf.Cos(a) * radius, y0, Mathf.Sin(a) * radius));
                }

                var centre = vertices.Count;
                vertices.Add(new Vector3(0f, y0, 0f));
                for (var k = 0; k < sides; k++)
                {
                    int p = ring + k, q = ring + (k + 1) % sides;
                    triangles.AddRange(new[] { apex, q, p });     // sides, outward
                    triangles.AddRange(new[] { centre, p, q });   // underside
                }
            }

            Cone(0f, 4.2f, 2.6f);
            Cone(2.2f, 6.2f, 1.9f);
            var mesh = new Mesh { name = "Tree crown" };
            mesh.SetVertices(vertices);
            mesh.SetTriangles(triangles, 0);
            mesh.RecalculateNormals();
            mesh.RecalculateBounds();
            return mesh;
        }

        /// <summary>
        /// True when a site outline edge (counter-clockwise) faces water: it gets a rubble mound instead of graded ground.
        /// Shared with SatelliteGround so the terrain under the paving matches the revetment.
        /// </summary>
        public static bool IsSeawardEdge(YardZone zone, Vector2 p, Vector2 q, TerrainModel terrain, YardSite site, (float, float) band, float shore)
        {
            // p, q and the band are in the zone's local axes.
            if (terrain == null)
            {
                return true;
            }

            var mid = (p + q) / 2f;
            var outward = new Vector2(q.y - p.y, p.x - q.x).normalized;     // right of a counter-clockwise edge
            // Water anywhere 20-60 m out (the terrain grid is ~16 m, so one sample can land on a beach cell).
            return new[] { 20f, 40f, 60f }.Select(d => mid + outward * d)
                .Any(at =>
                {
                    var w = zone.ToWorld(at);
                    return SatelliteGround.Dredged(band, shore, at, terrain.AtYard(site, w.x, w.y)) < 0.5f;
                });
        }

        /// <summary>
        /// Sloped rubble-mound revetment (about 1:1.5, the usual Korean reclamation edge) from quay level to below the
        /// sea on the given edges, with boulders on the face and concrete tetrapods along the waterline.
        /// </summary>
        private void RubbleMound(List<(Vector2 P, Vector2 Q)> edges)
        {
            if (edges.Count == 0)
            {
                return;
            }

            float top = YardLayout.QuayLevel, toe = YardLayout.SeaLevel - 4f, run = (top - toe) * 1.5f;
            var vertices = new List<Vector3>();
            void Tri(Vector3 a, Vector3 b, Vector3 c)
            {
                if (Vector3.Cross(b - a, c - a).y < 0f)
                {
                    (b, c) = (c, b);
                }

                vertices.Add(a);
                vertices.Add(b);
                vertices.Add(c);
            }

            Vector2 Outward(Vector2 p, Vector2 q) => new Vector2(q.y - p.y, p.x - q.x).normalized;
            Vector3 At(Vector2 v, float h) => ShipCoordinates.YardToUnity(v.x, v.y, h);
            var random = new System.Random(5);
            var rocks = new[] { "rock_a", "rock_b", "rock_c" }.ToDictionary(m => m, m => new List<Matrix4x4>());
            var tetrapods = new List<Matrix4x4>();
            float Next(float lo, float hi) => lo + (float)random.NextDouble() * (hi - lo);
            foreach (var (p, q) in edges)
            {
                var o = Outward(p, q) * run;
                Tri(At(p, top), At(q, top), At(q + o, toe));
                Tri(At(p, top), At(q + o, toe), At(p + o, toe));
                // Fill the wedge to the next sloped edge at a convex corner.
                foreach (var (r, s) in edges.Where(e => (e.P - q).sqrMagnitude < 0.01f))
                {
                    Tri(At(q, top), At(q + o, toe), At(q + Outward(r, s) * run, toe));
                }

                var length = Vector2.Distance(p, q);
                var along = (q - p) / length;
                var outward = o / run;
                for (var d = 2.5f; d < length - 2.5f; d += 3.4f)
                {
                    // Tetrapods (about 5 m, the 16-25 t class) in three staggered rows from just above the waterline
                    // to the toe; boulders scattered on the face above them.
                    var r = 0;
                    foreach (var row in new[] { 0.5f, 0.66f, 0.82f })
                    {
                        var at = p + along * (d + Next(-0.5f, 0.5f) + (r++ % 2) * 1.7f) + outward * (run * row + Next(-0.8f, 0.8f));
                        var h = Mathf.Lerp(top, toe, row) - 1.2f;
                        tetrapods.Add(Matrix4x4.TRS(At(at, h), Quaternion.Euler(Next(0, 360), Next(0, 360), Next(0, 360)), Vector3.one * Next(1.35f, 1.6f)));
                    }

                    for (var b = 0; b < 2; b++)
                    {
                        var t = Next(0.05f, 0.42f);
                        var at = p + along * (d + Next(-1.7f, 1.7f)) + outward * run * t;
                        var size = Next(1.2f, 2.8f);
                        rocks.ElementAt(random.Next(3)).Value.Add(Matrix4x4.TRS(At(at, Mathf.Lerp(top, toe, t) - 0.35f * size),
                            Quaternion.Euler(Next(-15, 15), Next(0, 360), Next(-15, 15)), Vector3.one * size));
                    }
                }
            }

            var mesh = new Mesh { name = "RubbleMound" };
            mesh.SetVertices(vertices);
            mesh.SetTriangles(Enumerable.Range(0, vertices.Count).ToArray(), 0);
            mesh.SetUVs(0, ObjMeshParser.BoxUvs(vertices, new List<List<int>> { Enumerable.Range(0, vertices.Count).ToList() }));
            mesh.RecalculateNormals();
            mesh.RecalculateBounds();
            var go = new GameObject("RubbleMound");
            go.transform.SetParent(transform, false);
            go.AddComponent<MeshFilter>().sharedMesh = mesh;
            go.AddComponent<MeshRenderer>().sharedMaterial = _palette.Get("riprap");
            HoverTarget.Occluder(go);
            PropLibrary.Combine("tetrapod", tetrapods, _palette, transform, "Armour");
            foreach (var pair in rocks)
            {
                PropLibrary.Combine(pair.Key, pair.Value, _palette, transform, "Armour");
            }
        }

        private void BuildFacility(Facility f)
        {
            var root = new GameObject(f.location + " " + f.name).transform;
            root.SetParent(f.kind == "작업장" || f.kind == "창고" ? _buildingDisplay : transform, false);
            root.SetPositionAndRotation(ShipCoordinates.YardToUnity(f.x, f.y, YardLayout.QuayLevel), ShipCoordinates.HeadingToUnity(f.heading_deg));
            float sx = f.size_x_m, sy = f.size_y_m, h = Mathf.Max(f.size_z_m, 0f);
            Transform blockDisplay = null;
            switch (f.kind)
            {
                case "작업장":
                case "창고":
                    if (PropLibrary.TryCreate(f.location, _palette, root, out _))
                    {
                        break; // generated building sized to this facility (pipeline/generate_scenery.py)
                    }

                    h = h > 0 ? h : 20f;
                    Box("Hall", root, new Vector3(0, h / 2, 0), new Vector3(sx, h, sy), "shop_wall");
                    Box("Roof", root, new Vector3(0, h + 1f, 0), new Vector3(sx + 2f, 2f, sy + 2f), "shop_roof");
                    Box("Band", root, new Vector3(sx / 2f + 0.15f, h * 0.78f, 0), new Vector3(0.3f, h * 0.1f, sy), "shop_roof");
                    Box("Door", root, new Vector3(sx / 2f + 0.2f, h * 0.32f, 0), new Vector3(0.4f, h * 0.64f, Mathf.Min(sy * 0.45f, 40f)), "crane_dark");
                    break;
                case "야드":
                    YardWalkSurface.Mark(Box("Pavement", root, new Vector3(0, 0.15f, 0), new Vector3(sx, 0.3f, sy), "asphalt"));
                    blockDisplay = new GameObject("Staged blocks").transform;
                    blockDisplay.SetParent(_blockDisplay, false);
                    blockDisplay.SetPositionAndRotation(root.position, root.rotation);
                    var rows = Mathf.Max(1, Mathf.FloorToInt(sy / 28f));
                    var cols = Mathf.Max(1, Mathf.FloorToInt(sx / 32f));
                    for (var r = 0; r < rows; r++)
                    {
                        for (var c = 0; c < cols; c++)
                        {
                            if ((r * 7 + c * 3) % 5 == 0)
                            {
                                continue; // leave gaps for transporters
                            }

                            var size = new Vector3(12f + (c % 3) * 3f, 4f + ((r + c) % 3) * 2.5f, 10f + (r % 2) * 4f);
                            var x = -sx / 2f + (c + 0.5f) * sx / cols;
                            var z = -sy / 2f + (r + 0.5f) * sy / rows;
                            Box("StagedBlock", blockDisplay, new Vector3(x, 0.3f + size.y / 2f, z), size, (r + c) % 4 == 0 ? "antifouling" : "primer");
                        }
                    }

                    break;
                case "안벽":
                    YardWalkSurface.Mark(Box("Jetty", root, new Vector3(0, -10f, 0), new Vector3(sx, 20f, sy), "concrete"));
                    Box("Fender", root, new Vector3(0, -2f, sy / 2f + 0.4f), new Vector3(sx, 2.5f, 0.8f), "crane_dark");
                    Box("Fender", root, new Vector3(0, -2f, -sy / 2f - 0.4f), new Vector3(sx, 2.5f, 0.8f), "crane_dark");
                    for (var x = -sx / 2f + 10f; x < sx / 2f; x += 25f)
                    {
                        foreach (var s in new[] { -1f, 1f })
                        {
                            Box("Bollard", root, new Vector3(x, 0.6f, s * (sy / 2f - 1.5f)), new Vector3(0.9f, 1.2f, 0.9f), "crane_dark");
                        }
                    }

                    break;
            }

            HoverTarget.Attach(root.gameObject, "시설", HoverTarget.FacilityTitle(f), FacilityDetail(f)).Identify(TargetCategory.Facility, f.location);
            if (blockDisplay != null)
            {
                HoverTarget.Attach(blockDisplay.gameObject, "시설", HoverTarget.FacilityTitle(f), FacilityDetail(f))
                    .Identify(TargetCategory.Facility, f.location);
            }
        }

        private GameObject Box(string name, Vector3 position, Vector3 size, string paint) => Box(name, transform, position, size, paint, true);

        private GameObject Box(string name, Transform parent, Vector3 localPosition, Vector3 size, string paint, bool world = false)
        {
            var box = MetricBox.Create(name, parent, size, _palette.Get(paint));
            if (world)
            {
                box.transform.position = localPosition;
            }
            else
            {
                box.transform.localPosition = localPosition;
                box.transform.localRotation = Quaternion.identity;
            }

            return box;
        }

        private void Update()
        {
            if (_palette != null)
            {
                // Slow drift of the ripple normal map.
                _palette.Get("water").SetTextureOffset("_BaseMap", new Vector2(Time.time * 0.004f, Time.time * 0.0025f));
            }
        }
    }
}
