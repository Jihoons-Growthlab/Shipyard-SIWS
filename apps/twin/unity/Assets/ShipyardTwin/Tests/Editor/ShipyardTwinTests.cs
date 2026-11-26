using System;
using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.UIElements;

namespace ShipyardTwin.Tests
{
    public sealed class ShipyardTwinTests
    {
        private const string Json = @"{
 ""format"": ""shipyard-twin/1"",
 ""hull"": {""hull"": ""T1"", ""length_m"": 100, ""beam_m"": 20, ""depth_m"": 10},
 ""placement"": {""dock"": ""D"", ""origin_x"": 50, ""origin_y"": 10, ""origin_z"": 0, ""heading_deg"": 90,
   ""dock_position"": {""location"": ""D"", ""x"": 100, ""y"": 10, ""z"": 0, ""heading_deg"": 0}},
 ""blocks"": [
  {""block"": ""T1-221C1"", ""zone"": ""z"", ""weight_t"": 100, ""step"": 2, ""predecessors"": [""T1-241C1""], ""forecast"": ""2027-03-04"",
   ""erected_day"": null, ""model_asset"": null, ""x_min"": 10, ""x_max"": 20, ""y_min"": -5, ""y_max"": 5, ""z_min"": 0, ""z_max"": 3,
   ""cog_x"": 15, ""cog_y"": 0, ""cog_z"": 1.5},
  {""block"": ""T1-241C1"", ""zone"": ""z"", ""weight_t"": 50, ""step"": 1, ""predecessors"": [], ""forecast"": ""2027-03-01"",
   ""erected_day"": null, ""model_asset"": null, ""x_min"": 0, ""x_max"": 10, ""y_min"": 0, ""y_max"": 8, ""z_min"": 0, ""z_max"": 2,
   ""cog_x"": 5, ""cog_y"": 4, ""cog_z"": 1},
  {""block"": ""T1-511C1"", ""zone"": ""z"", ""weight_t"": 10, ""step"": 3, ""predecessors"": [], ""forecast"": null,
   ""erected_day"": null, ""model_asset"": null, ""x_min"": 0, ""x_max"": 4, ""y_min"": -2, ""y_max"": 2, ""z_min"": 10, ""z_max"": 14,
   ""cog_x"": 2, ""cog_y"": 0, ""cog_z"": 12}
 ]}";

        [TestCase("PROD", WalkAvatar.Worker)]
        [TestCase("DEMO_VIEW", WalkAvatar.Worker)]
        [TestCase(null, WalkAvatar.Worker)]
        [TestCase("ADMIN", WalkAvatar.Steward)]
        [TestCase("EXEC", WalkAvatar.Master)]
        [TestCase("임원", WalkAvatar.Master)]
        public void WalkAvatar_UsesTheAuthenticatedGroupForAppearance(string group, WalkAvatar expected)
        {
            Assert.AreEqual(expected, WalkAvatarRoles.ForGroup(group));
        }

        [Test]
        public void CraneLoadChart_RatesByRadiusAndDangerZoneAddsMargin()
        {
            var chart = new[]
            {
                new CraneLoadChartRow { radius_m = 20f, capacity_t = 50f },
                new CraneLoadChartRow { radius_m = 40f, capacity_t = 25f },
            };
            Assert.AreEqual(50f, RailCraneMotion.RatedAt(chart, 12f));
            Assert.AreEqual(25f, RailCraneMotion.RatedAt(chart, 20.5f), "Between points the farther radius rates.");
            Assert.AreEqual(0f, RailCraneMotion.RatedAt(chart, 41f), "Beyond the chart nothing may be lifted.");
            Assert.IsNull(RailCraneMotion.RatedAt(new CraneLoadChartRow[0], 10f));
            var k = JsonUtility.FromJson<CraneKinematicsRow>("{\"boom_m\":60,\"luff_min_deg\":15,\"luff_max_deg\":82}");
            Assert.AreEqual(5f, k.danger_margin_m, "Older ERP rows without the margin: 5 m.");
            Assert.AreEqual(60f * Mathf.Cos(15f * Mathf.Deg2Rad) + 5f, RailCraneMotion.DangerRadius(k), 1e-3f);
            var snapshot = YardDataSource.Assemble(new Dictionary<string, string>
            {
                ["hulls"] = "[]", ["vessel_details"] = "[]", ["hull_geometry"] = "[]", ["hull_placements"] = "[]",
                ["erection_timeline"] = "[]", ["block_stock_plans"] = "[]", ["location_geo"] = "[]", ["yard_sites"] = "[]",
                ["crane_load_charts"] = "[{\"equipment\":\"JC\",\"radius_m\":20,\"capacity_t\":50}]",
            }, "test");
            Assert.AreEqual(50f, snapshot.crane_load_charts[0].capacity_t);
            var ring = CraneSafetyView.RingMesh(9f, 11f);
            Assert.AreEqual(11f, ring.bounds.extents.x, 1e-3f);
            Assert.AreEqual(Vector3.up, ring.normals[0]);
        }

        [Test]
        public void TwinFile_ParsesExportAndRejectsOtherFormats()
        {
            var twin = TwinFile.Parse(Json);
            Assert.AreEqual("T1", twin.hull.hull);
            Assert.AreEqual(3, twin.blocks.Length);
            CollectionAssert.AreEqual(new[] { "T1-241C1" }, twin.blocks[0].predecessors);
            Assert.AreEqual("2027-03-04", twin.blocks[0].Day);
            Assert.AreEqual(100f, twin.placement.dock_position.x);
            Assert.Throws<FormatException>(() => TwinFile.Parse("{\"format\":\"other\"}"));
        }

        [Test]
        public void Coordinates_MapShipAndYardAxesWithoutMirroring()
        {
            Assert.AreEqual(new Vector3(1, 3, 2), ShipCoordinates.ShipToUnity(1, 2, 3));
            Assert.AreEqual(new Vector3(1, 2, -3), ShipCoordinates.ObjToUnity(1, 2, 3));
            // OBJ (ship x, ship z, -ship y) of ship point (1, 2, 3) lands where ShipToUnity puts it.
            Assert.AreEqual(ShipCoordinates.ShipToUnity(1, 2, 3), ShipCoordinates.ObjToUnity(1, 3, -2));
            // Heading 90° turns ship forward (yard +x) to yard +y, i.e. Unity +z.
            var forward = ShipCoordinates.HeadingToUnity(90f) * Vector3.right;
            Assert.That(Vector3.Distance(forward, Vector3.forward), Is.LessThan(1e-5f));
            var pose = ShipCoordinates.HullPose(TwinFile.Parse(Json).placement);
            Assert.AreEqual(new Vector3(50, 0, 10), pose.position);
            var bounds = ShipCoordinates.LocalBounds(TwinFile.Parse(Json).blocks[1]);
            Assert.AreEqual(new Vector3(-5, -1, -4), bounds.min);
            Assert.AreEqual(new Vector3(5, 1, 4), bounds.max);
        }

        [Test]
        public void ObjParser_ConvertsAxesFansPolygonsAndSplitsBackFaces()
        {
            const string obj = "# quad, both windings\nv 0 0 0\nv 1 0 0\nv 1 0 -1\nv 0 0 -1\nf 1 2 3 4\nf 1/1 4/4 3/3 2/2\n";
            var result = ObjMeshParser.Parse(obj);
            Assert.AreEqual(new Vector3(1, 0, 1), result.Vertices[2]);
            Assert.AreEqual(12, result.Triangles.Count);
            // Front face (reversed for Unity): 1-3-2 fan → indices 0,2,1 and 0,3,2.
            CollectionAssert.AreEqual(new[] { 0, 2, 1, 0, 3, 2 }, result.Triangles.Take(6));
            // Back face reuses no vertex of the front triangles it mirrors.
            Assert.AreEqual(4 + 6, result.Vertices.Count);
            var mesh = ObjMeshParser.ToMesh(result, "quad");
            var normals = mesh.normals;
            Assert.That(normals.All(n => n.sqrMagnitude > 0.9f), "No normal cancelled out.");
            UnityEngine.Object.DestroyImmediate(mesh);
            Assert.Throws<FormatException>(() => ObjMeshParser.Parse("v 0 0 0\nf 1 2 3\n"));
            Assert.AreEqual(3, ObjMeshParser.Parse("v 0 0 0\nv 1 0 0\nv 0 1 0\nf -3 -2 -1\n").Triangles.Count);
        }

        [Test]
        public void ObjParser_SplitsSubmeshesByMaterialInFirstUseOrder()
        {
            const string obj = "v 0 0 0\nv 1 0 0\nv 0 1 0\nv 0 0 1\nf 1 2 3\nusemtl deck\nf 1 2 4\nusemtl antifouling\nf 1 3 4\nusemtl deck\nf 2 3 4\n";
            var result = ObjMeshParser.Parse(obj);
            CollectionAssert.AreEqual(new[] { "", "deck", "antifouling" }, result.Materials);
            CollectionAssert.AreEqual(new[] { 3, 6, 3 }, result.Submeshes.Select(s => s.Count));
            var mesh = ObjMeshParser.ToMesh(result, "m");
            Assert.AreEqual(3, mesh.subMeshCount);
            UnityEngine.Object.DestroyImmediate(mesh);
        }

        [Test]
        public void Palette_KnowsGeneratorPaintsAndFallsBackToPrimer()
        {
            foreach (var name in new[] { "antifouling", "boottop", "topside", "deck", "primer", "glass", "bronze", "crane_white", "concrete" })
            {
                Assert.IsTrue(ShipPalette.Paints.ContainsKey(name), name);
            }

            Assert.AreEqual(ShipPalette.Paints["primer"].Color, ShipPalette.Lookup("no-such-paint").Color);
            var palette = new ShipPalette(null);
            var deck = palette.Get("deck");
            Assert.AreSame(deck, palette.Get("deck"), "One shared material per paint.");
            Assert.AreEqual(ShipPalette.Paints["deck"].Color, deck.color);
            Assert.AreSame(palette.Get("primer"), palette.Get("unknown"));
            UnityEngine.Object.DestroyImmediate(deck);
        }

        [Test]
        public void ObjParser_KeepsNormalsAndUvsFromBlenderStyleFiles()
        {
            // A cube corner: two faces share position 1 but have different normals and UVs.
            const string obj = "v 0 0 0\nv 1 0 0\nv 1 1 0\nv 0 1 0\nv 0 0 -1\nv 0 1 -1\n" +
                               "vt 0 0\nvt 1 0\nvt 1 1\nvt 0 1\nvn 0 0 1\nvn -1 0 0\n" +
                               "usemtl wall_panel\nf 1/1/1 2/2/1 3/3/1 4/4/1\nf 1/2/2 4/3/2 6/4/2 5/1/2\n";
            var result = ObjMeshParser.Parse(obj);
            Assert.AreEqual(8, result.Vertices.Count, "Position 1 and 4 split by normal.");
            Assert.AreEqual(12, result.Triangles.Count);
            Assert.AreEqual(new Vector3(0, 0, -1), result.Normals[0], "OBJ +z normal becomes Unity -z.");
            Assert.AreEqual(new Vector2(0, 0), result.Uvs[0]);
            var mesh = ObjMeshParser.ToMesh(result, "corner");
            Assert.AreEqual(8, mesh.uv.Length);
            UnityEngine.Object.DestroyImmediate(mesh);

            var box = ObjMeshParser.BoxUvs(new System.Collections.Generic.List<Vector3> { new Vector3(0, 0, 0), new Vector3(4, 0, 0), new Vector3(0, 0, 3) },
                new System.Collections.Generic.List<System.Collections.Generic.List<int>> { new System.Collections.Generic.List<int> { 0, 1, 2 } });
            Assert.AreEqual(new Vector2(4, 0), box[1], "Floor triangle: UV = (x, z) in metres.");
            Assert.AreEqual(new Vector2(0, 3), box[2]);
        }

        [Test]
        public void Schedule_OrdersByDayLiftsThenLands()
        {
            var schedule = new ErectionSchedule(TwinFile.Parse(Json).blocks, liftDays: 2f);
            CollectionAssert.AreEqual(new[] { "T1-241C1", "T1-221C1", "T1-511C1" }, schedule.Entries.Select(e => e.Block.block));
            Assert.AreEqual(new DateTime(2027, 2, 27), schedule.Start);
            // 241C1 lands day 2 (03-01), 221C1 day 5 (03-04), undated 511C1 one lift after the last: day 7.
            CollectionAssert.AreEqual(new[] { 2f, 5f, 7f }, schedule.Entries.Select(e => e.FinishDay));
            Assert.AreEqual(7f, schedule.Duration);

            var first = schedule.Entries[0];
            Assert.AreEqual(0f, schedule.Progress(first));
            var landed = schedule.Seek(1f);
            Assert.AreEqual(0.5f, schedule.Progress(first), 1e-5f);
            Assert.AreEqual(0, landed.Count);
            Assert.AreEqual(1, schedule.CurrentStep);
            Assert.AreEqual("T1-241C1", schedule.Lifting().Single().Block.block);

            landed = schedule.Advance(4.5f);
            CollectionAssert.AreEqual(new[] { "T1-241C1", "T1-221C1" }, landed.Select(e => e.Block.block));
            Assert.AreEqual(150f, schedule.ErectedWeight);
            Assert.AreEqual(3, schedule.CurrentStep, "511C1 has started lifting.");
            Assert.AreEqual(new DateTime(2027, 3, 4, 12, 0, 0), schedule.CurrentDate);

            Assert.AreEqual(0, schedule.Seek(0f).Count, "Seeking backwards erects nothing.");
            Assert.AreEqual(0, schedule.ErectedCount);
            schedule.Seek(100f);
            Assert.IsTrue(schedule.Finished);
            Assert.AreEqual(7f, schedule.Cursor);
        }

        [Test]
        public void Schedule_LiftsSameDayBlocksOneAtATimeBeforeTheirDay()
        {
            BlockRecord Block(string id, int step, string day) => new BlockRecord { block = id, step = step, forecast = day };
            var schedule = new ErectionSchedule(new[]
            {
                Block("K", 1, "2027-03-01"), Block("C", 2, "2027-03-04"), Block("A", 2, "2027-03-04"), Block("B", 3, "2027-03-04"),
            }, liftDays: 2f);

            // Key block day 2; the three 03-04 blocks share days 2..5 in step/id order: A, C (step 2), then B.
            CollectionAssert.AreEqual(new[] { "K", "A", "C", "B" }, schedule.Entries.Select(e => e.Block.block));
            CollectionAssert.AreEqual(new[] { 2f, 3f, 4f, 5f }, schedule.Entries.Select(e => e.FinishDay));
            CollectionAssert.AreEqual(new[] { 2f, 1f, 1f, 1f }, schedule.Entries.Select(e => e.LiftDays));
            for (var cursor = 0f; cursor <= 5f; cursor += 0.25f)
            {
                schedule.Seek(cursor);
                Assert.LessOrEqual(schedule.Lifting().Count(), 1, "One crane lifts one block at a time (day " + cursor + ").");
            }
        }

        [Test]
        public void Playback_TicksAtSpeedPublishesLandingsAndStopsAtEnd()
        {
            var playback = new ErectionPlayback(new ErectionSchedule(TwinFile.Parse(Json).blocks, 2f), daysPerSecond: 1f, playing: true);
            var landed = new List<string>();
            playback.BlockErected += e => landed.Add(e.Block.block);

            playback.Tick(2f);
            CollectionAssert.AreEqual(new[] { "T1-241C1" }, landed);
            Assert.AreEqual(2f, playback.NextSpeed());
            playback.Tick(2.5f);
            Assert.AreEqual(3, landed.Count);
            Assert.IsFalse(playback.Playing, "Stops when finished.");
            playback.Playing = false;
            playback.Tick(10f);
            Assert.AreEqual(3, landed.Count);

            playback.Restart();
            Assert.IsTrue(playback.Playing);
            Assert.AreEqual(0f, playback.Schedule.Cursor);
            Assert.Throws<ArgumentOutOfRangeException>(() => new ErectionSchedule(new BlockRecord[0], 0f));
        }

        [Test]
        public void YardSim_ParsesSharedStateWithMissingPositions()
        {
            const string json = @"{""scenario"": {""id"": ""SN2050:2026-10-23:2027-01-13"", ""hull"": ""SN2050"", ""dock"": ""DTW-DOCK"",
              ""start"": ""2026-10-23T00:00:00+09:00"", ""end"": ""2027-01-14T00:00:00+09:00""},
             ""clock"": {""time"": ""2026-12-29T07:30:00+09:00"", ""plan_day"": ""2026-12-29"", ""playing"": true, ""speed"": 2, ""cycle"": 1,
              ""version"": 3, ""work"": true, ""days_per_second"": 4.0, ""work_minutes_per_second"": 20.0},
             ""blocks"": [
              {""id"": ""SN2050-114P1"", ""state"": ""인양"", ""location"": null, ""x"": 158.7, ""y"": -32.5, ""z"": 23.08, ""heading_deg"": 90.0,
               ""crane"": ""DTW-GL"", ""vehicle"": null, ""source"": ""plan_sim"", ""basis"": ""ERP 크레인 배정 CRA-1"", ""step"": 1, ""plan_day"": ""2026-12-29""},
              {""id"": ""SN2050-121C1"", ""state"": ""생산 중"", ""location"": null, ""x"": null, ""y"": null, ""z"": null, ""heading_deg"": null,
               ""crane"": null, ""vehicle"": null, ""source"": ""plan"", ""basis"": null, ""step"": 2, ""plan_day"": null}],
             ""equipment"": [
              {""id"": ""DTW-GL"", ""kind"": ""crane"", ""state"": ""인양"", ""x"": 158.7, ""y"": 10.0, ""heading_deg"": 0.0, ""rail_pos"": 0.017,
               ""trolley_m"": -42.5, ""hook"": {""x"": 158.7, ""y"": -32.5, ""z"": 34.6}, ""block"": ""SN2050-114P1"", ""source"": ""plan_sim"",
               ""rail"": {""x0"": 151.0, ""y0"": 10.0, ""x1"": 599.0, ""y1"": 10.0, ""gauge_m"": 116.0, ""source"": ""estimated"", ""basis"": ""레일 미등록""}},
              {""id"": ""DTW-TP"", ""kind"": ""vehicle"", ""state"": ""미배치"", ""x"": null, ""y"": null, ""heading_deg"": null, ""loaded"": false, ""source"": ""plan""}],
             ""activity"": [""SN2050-114P1 인양 · DTW-GL""]}";
            var state = YardSimState.Parse(json);
            Assert.IsTrue(state.HasScenario);
            Assert.AreEqual(new DateTime(2026, 12, 29, 7, 30, 0), state.Time, "Clock in KST.");
            Assert.AreEqual((1, 3, 2f), (state.clock.cycle, state.clock.version, state.clock.speed));
            var lifted = state.blocks[0];
            Assert.IsTrue(lifted.Placed && lifted.OnHook);
            Assert.AreEqual(23.08f, lifted.z, 1e-4f);
            Assert.IsFalse(state.blocks[1].Placed, "In production: no position (JSON null).");
            Assert.IsTrue(state.equipment[0].IsCrane);
            Assert.AreEqual(-32.5f, state.equipment[0].hook.y, 1e-4f);
            Assert.AreEqual("estimated", state.equipment[0].rail.source);
            Assert.IsFalse(state.equipment[1].Placed, "Vehicle without coordinates: unplaced.");
            Assert.IsFalse(YardSimState.Parse("{\"scenario\": null}").HasScenario);
        }

        [Test]
        public void Playback_WithRemoteSendsControlsAndFollowsTheSharedClock()
        {
            var playback = new ErectionPlayback(new ErectionSchedule(TwinFile.Parse(Json).blocks, 2f), daysPerSecond: 1f, playing: false);
            var sent = new List<string>();
            playback.Remote = (action, value) => sent.Add(action + (value == null ? string.Empty : " " + value));
            playback.Playing = true;
            playback.NextSpeed();
            playback.Seek(2.5f);
            playback.Restart();
            CollectionAssert.AreEqual(new[] { "play", "speed 2", "seek 2027-03-01T12:00:00+09:00", "restart" }, sent);
            playback.Tick(100f);
            Assert.AreEqual(0f, playback.Schedule.Cursor, "The shared clock drives it, not Tick.");
            var landed = new List<string>();
            playback.BlockErected += e => landed.Add(e.Block.block);
            playback.SyncFrom(5.5f, playing: false, speed: 4f);
            Assert.AreEqual(5.5f, playback.Schedule.Cursor);
            Assert.IsFalse(playback.Playing);
            Assert.AreEqual(4f, playback.SpeedMultiplier);
            CollectionAssert.AreEqual(new[] { "T1-241C1", "T1-221C1" }, landed);
        }

        [Test]
        public void Timeline_ListsTheWholeHullPlanAndOneRowPerBlockInLiftOrder()
        {
            var file = TwinFile.Parse(Json);
            var schedule = new ErectionSchedule(file.blocks, 2f);
            var stock = new StockPlanRow { block = "T1-221C1", location = "PE1", stock_from = "2027-02-20" };
            var hull = new HullPlan
            {
                Id = "T1", Info = new HullInfo { hull = "T1", delivery = "2027-06-30" }, Placement = file.placement,
                Blocks = file.blocks.Select(b => new BlockPlan(b, b.block == "T1-221C1"
                    ? new List<StockStay> { new StockStay(stock, new DateTime(2027, 2, 20), null) }
                    : new List<StockStay>())).ToList(),
            };
            hull.DockStays.Add((new DateTime(2027, 1, 10), new DateTime(2027, 5, 1)));
            var snapshot = new YardSnapshot
            {
                crane_assignments = new[]
                {
                    new CraneAssignmentRow { hull = "T1", equipment = "GC-01", block = "T1-241C1", work_day = "2027-03-01", start_hour = 8, hours = 4, crane_work = "탑재" },
                    new CraneAssignmentRow { hull = "T2", equipment = "GC-02", block = "T2-1", work_day = "2027-03-01", hours = 2 },
                },
                quay_shifts = new[] { new QuayShiftRow { hull = "T1", plan_day = "2027-05-02", shift_purpose = "안벽 이동", from_point = "D", to_point = "Q" } },
            };

            var timeline = HullTimeline.Build(hull, snapshot, schedule);

            // Empty hull rows (no berth assignment) are left out; blocks follow the lift order with their step.
            CollectionAssert.AreEqual(new[] { "도크", "호선 이동", "인도", "크레인 작업", "241C1 · 1단계", "221C1 · 2단계", "511C1 · 3단계" },
                timeline.Rows.Select(r => r.Label));
            Assert.AreEqual(new DateTime(2027, 1, 10), timeline.From);
            Assert.AreEqual(new DateTime(2027, 7, 1), timeline.To, "Delivery day is the last item (end exclusive).");
            var dockBooking = timeline.Rows.Single(r => r.Label == "도크").Items.Single();
            StringAssert.Contains("도크 예약 기간", dockBooking.Detail);
            StringAssert.DoesNotContain("진수", dockBooking.Detail);
            Assert.AreEqual(1, timeline.Rows.Single(r => r.Label == "크레인 작업").Items.Count, "Only this hull's crane work.");

            var stay = timeline.BlockRow("T1-221C1").Items.Single(i => i.Kind == HullTimeline.Kind.Stock);
            Assert.AreEqual(new DateTime(2027, 3, 2), stay.End, "An open stay lasts until its lift begins (day 3).");
            var lift = timeline.BlockRow("T1-221C1").Items.Single(i => i.Kind == HullTimeline.Kind.Lift);
            Assert.AreEqual(schedule.Start.AddDays(3), lift.Start);
            Assert.AreEqual(schedule.Start.AddDays(5), lift.End);

            var lifting = timeline.At(new DateTime(2027, 2, 28, 12, 0, 0));
            Assert.AreEqual(HullTimeline.Kind.Lift, lifting[0].Item.Kind, "Lifts come first.");
            Assert.AreEqual("T1-241C1", lifting[0].Row.Block);
            Assert.IsTrue(lifting.Any(x => x.Item.Kind == HullTimeline.Kind.Dock));
            var craneWork = timeline.At(new DateTime(2027, 3, 1, 9, 0, 0));
            Assert.IsTrue(craneWork.Any(x => x.Item.Kind == HullTimeline.Kind.Crane));
            Assert.IsFalse(timeline.At(new DateTime(2027, 3, 1, 13, 0, 0)).Any(x => x.Item.Kind == HullTimeline.Kind.Crane), "Crane work lasts its hours.");
            Assert.IsTrue(timeline.Rows.Single(r => r.Label == "인도").Items.Single().Milestone);
        }

        [Test]
        public void YardLayout_UsesErpDockFootprintAndFallsBackToHull()
        {
            var twin = TwinFile.Parse(Json);
            Assert.AreEqual(new Vector3(100, 0, 10), YardLayout.DockCentre(twin), "No plan: dock_position.");
            Assert.AreEqual(82f, YardLayout.PitHalfLength(twin), "No plan: hull length / 2 + 32.");
            Assert.AreEqual(1, YardLayout.Docks(twin).Count, "Placement dock synthesised.");

            twin.yard = new YardPlan
            {
                site = new YardSite { id = "S", name = "가상", lat = 34.83, lon = 128.55, x_axis_bearing_deg = 180 },
                facilities = new[]
                {
                    new Facility { location = "D", kind = "도크", x = 120, y = 20, size_x_m = 200, size_y_m = 60 },
                    new Facility { location = "D2", kind = "도크", x = 100, y = 120, size_x_m = 300, size_y_m = 60 },
                    new Facility { location = "P", kind = "작업장", x = -50, y = 0, size_x_m = 40, size_y_m = 20, size_z_m = 15 },
                    new Facility { location = "Q", kind = "안벽" },
                },
            };
            Assert.AreEqual(new Vector3(120, 0, 20), YardLayout.DockCentre(twin));
            Assert.AreEqual(94f, YardLayout.PitHalfLength(twin));
            Assert.AreEqual(24f, YardLayout.PitHalfWidth(twin));
            Assert.AreEqual(2, YardLayout.Docks(twin).Count);
            Assert.AreEqual(250f, YardLayout.Shoreline(twin), "Seaward end of the longest-reaching dock.");
            Assert.IsFalse(twin.yard.facilities[3].HasFootprint);
        }

        [Test]
        public void YardModel_MergesAliasFootprintsAndListsEveryHullWithItsDockDays()
        {
            Facility Dock(string id, string name, float y) =>
                new Facility { location = id, name = name, kind = "도크", x = 485, y = y, size_x_m = 180, size_y_m = 45 };
            HullPlacement Place(string hull, string dock) => new HullPlacement { hull = hull, dock = dock, origin_x = 405, origin_y = 90 };
            BlockRecord Block(string hull) => new BlockRecord { block = hull + "-B01", hull = hull, forecast = "2026-11-01", x_max = 10, y_max = 5, z_max = 4 };
            DockBookingRow Booking(string hull, string dock, string from, string until) =>
                new DockBookingRow { hull = hull, dock = dock, start_day = from, end_day = until };
            var snapshot = new YardSnapshot
            {
                format = YardSnapshot.Format,
                location_geo = new[] { Dock("GY-DK", "2도크", 90), Dock("TW-DK", "트윈 도크", 90), Dock("L-DK", "건조 도크", 300) },
                hulls = new[] { "A", "B", "C", "D", "E" }.Select(id => new HullRow { id = id }).ToArray(),
                hull_placements = new[] { Place("A", "L-DK"), Place("B", "L-DK"), Place("C", "L-DK"), Place("D", "GY-DK"), Place("E", "TW-DK") },
                erection_timeline = new[] { "A", "B", "C", "D", "E" }.Select(Block).ToArray(),
                dock_bookings = new[]
                {
                    Booking("A", "L-DK", "2026-09-01", "2026-10-01"), Booking("B", "L-DK", "2026-11-01", "2026-11-30"),
                    Booking("C", "L-DK", "2026-12-15", "2027-01-15"), Booking("E", "TW-DK", "2026-11-10", "2026-11-20"),
                },
            }.Normalised();

            var model = YardModel.Build(snapshot, new DateTime(2026, 11, 18));
            CollectionAssert.AreEquivalent(new[] { "GY-DK", "L-DK" }, model.Facilities.Select(f => f.location));
            CollectionAssert.AreEquivalent(new[] { "A", "B", "C", "D", "E" }, model.Hulls.Select(h => h.Id), "every placed hull can be simulated");
            Assert.AreEqual("GY-DK", model.Hull("E").Placement.dock, "the alias dock resolves to the kept facility");
            CollectionAssert.AreEquivalent(new[] { "B" }, model.InDocks(model.AsOf)["L-DK"].Select(h => h.Id), "B's booking covers the plan day");
            Assert.IsFalse(model.Hull("A").InDock(model.AsOf), "A was launched in October");
            Assert.IsTrue(model.Hull("C").InDock(new DateTime(2027, 1, 15)), "booking end day inclusive");
            Assert.IsFalse(model.Hull("C").InDock(new DateTime(2027, 3, 1)));
            Assert.IsTrue(model.Hull("D").InDock(model.AsOf), "no bookings: always in its dock");
            Assert.IsTrue(model.Hull("A").SharesBerthWith(model.Hull("B")), "same berth in the same dock");
            Assert.IsFalse(model.Hull("D").SharesBerthWith(model.Hull("B")), "another dock");
            snapshot.dock_bookings = null;
            Assert.AreEqual(5, YardModel.Build(snapshot, new DateTime(2026, 11, 18)).Hulls.Count, "without bookings every placed hull stays");
        }

        [Test]
        public void YardModel_UsesRegisteredBerthPoseWithinTheAssignmentAndHidesOffshore()
        {
            var snapshot = new YardSnapshot
            {
                shift_points = new[]
                {
                    new ShiftPointRow { id = "Q1", kind = "선석", position_known = true,
                        position = new ShiftPointPosition { x = 625, y = -196, z = -5.5f, heading_deg = 180 } },
                    new ShiftPointRow { id = "SEA-TRIAL", kind = "해상" },
                },
                berth_assignments = new[]
                {
                    new BerthAssignmentRow { hull = "H1", berth = "Q1", start_day = "2027-01-14", end_day = "2027-01-24" },
                },
                quay_shifts = new[]
                {
                    new QuayShiftRow { id = "1", hull = "H1", to_point = "SEA-TRIAL",
                        plan_day = "2027-01-19", actual_day = "2027-01-20" },
                    new QuayShiftRow { id = "2", hull = "H1", to_point = "Q1", plan_day = "2027-01-22" },
                },
            }.Normalised();

            HullPlacement At(int day) => YardModel.Build(snapshot, new DateTime(2027, 1, day)).BerthPlacement("H1");
            Assert.IsNull(At(13));
            Assert.AreEqual(new Vector3(625, -5.5f, -196), ShipCoordinates.HullPose(At(14)).position);
            Assert.AreEqual(180f, At(19).heading_deg, "The recorded departure takes precedence over its planned day.");
            Assert.IsNull(At(20));
            Assert.IsNotNull(At(22));
            Assert.IsNotNull(At(24));
            Assert.IsNull(At(25));
            snapshot.shift_points[0].position_known = false;
            Assert.IsNull(At(14));
        }

        [Test]
        public void Scenery_ParsesFlatRoutesAndRouteMathLoopsOrPingPongs()
        {
            var scenery = SceneryFile.Parse(@"{""format"":""shipyard-scenery/1"",""roads"":[{""x0"":0,""y0"":0,""x1"":10,""y1"":0,""width"":8}],
                ""props"":[{""model"":""car_red"",""x"":1,""y"":2,""heading"":90}],
                ""routes"":[{""model"":""truck"",""speed"":2,""loop"":true,""path"":[0,0,10,0,10,10,0,10]}]}");
            Assert.AreEqual(8f, scenery.roads[0].width);
            Assert.AreEqual(90f, scenery.props[0].heading);
            var square = scenery.routes[0].Points();
            CollectionAssert.AreEqual(new[] { new Vector2(0, 0), new Vector2(10, 0), new Vector2(10, 10), new Vector2(0, 10) }, square);
            Assert.AreEqual(40f, RouteMath.Length(square, loop: true));
            var (p, heading) = RouteMath.At(square, true, 15f);
            Assert.AreEqual(new Vector2(10, 5), p);
            Assert.AreEqual(90f, heading, 1e-4f, "Driving +y on the second side.");
            Assert.AreEqual(new Vector2(5, 0), RouteMath.At(square, true, 45f).Position, "Loops wrap.");

            var line = new[] { new Vector2(0, 0), new Vector2(10, 0) };
            var (back, backHeading) = RouteMath.At(line, false, 13f);
            Assert.AreEqual(new Vector2(7, 0), back, "Open paths drive back.");
            Assert.AreEqual(180f, backHeading, 1e-4f);
            Assert.Throws<FormatException>(() => SceneryFile.Parse("{}"));
        }

        [Test]
        public void Geo_RoundTripsYardAndWebMercator()
        {
            var site = new YardSite { id = "S", lat = 34.833114, lon = 128.59749, x_axis_bearing_deg = 235 };
            var (lat, lon) = ShipCoordinates.YardToGeo(site, 430f, 170f);
            var back = ShipCoordinates.GeoToYard(site, lat, lon);
            Assert.That(Vector2.Distance(back, new Vector2(430f, 170f)), Is.LessThan(0.01f));
            // Bearing 235: yard +x points south-west, so +x lowers latitude and longitude.
            var (lat2, lon2) = ShipCoordinates.YardToGeo(site, 100f, 0f);
            Assert.Less(lat2, site.lat);
            Assert.Less(lon2, site.lon);
            // Web Mercator: tile (0,0) corner at zoom 0 is (85.05 N, 180 W); the centre is (0, 0).
            var (cLat, cLon) = ShipCoordinates.MercatorPixelToGeo(0, 128, 128);
            Assert.AreEqual(0.0, cLat, 1e-9);
            Assert.AreEqual(0.0, cLon, 1e-9);
            Assert.AreEqual(85.0511, ShipCoordinates.MercatorPixelToGeo(0, 0, 0).Lat, 1e-3);
        }

        [Test]
        public void Polygon_HullClipAndSiteOutline()
        {
            var hull = Polygon2D.ConvexHull(new[] { new Vector2(0, 0), new Vector2(10, 0), new Vector2(10, 10), new Vector2(0, 10), new Vector2(5, 5) });
            Assert.AreEqual(4, hull.Count, "Interior point dropped.");
            var clipped = Polygon2D.ClipConvex(Polygon2D.Rect(5, 20, -5, 5), hull);
            Assert.AreEqual(4, clipped.Count);
            Assert.AreEqual(25f, Area(clipped), 1e-3f);
            var outline = YardEnvironment.SiteOutline(new[] { new Facility { x = 0, y = 0, size_x_m = 100, size_y_m = 50 } }, shore: 40f, margin: 10f);
            Assert.AreEqual(40f, outline.Max(v => v.x), 1e-4f, "Cut at the shoreline.");
            Assert.AreEqual(-60f, outline.Min(v => v.x), 1e-4f);
            var flat = Polygon2D.Flat(outline, 12f);
            Assert.Greater(flat.normals[0].y, 0.99f, "Ground faces up.");
            Assert.IsTrue(Polygon2D.Contains(outline, new Vector2(0, 0)));
            Assert.IsFalse(Polygon2D.Contains(outline, new Vector2(45, 0)), "Seaward of the shoreline.");
            Assert.AreEqual(0f, Polygon2D.Distance(outline, new Vector2(0, 0)));
            Assert.AreEqual(5f, Polygon2D.Distance(outline, new Vector2(45, 0)), 1e-4f);
            UnityEngine.Object.DestroyImmediate(flat);
        }

        [Test]
        public void Zones_SplitDocksByCoastAndWorkInLocalAxes()
        {
            var twin = TwinFile.Parse(Json);
            twin.placement.dock = "D1";
            twin.yard = new YardPlan
            {
                site = new YardSite { id = "S", lat = 34.83, lon = 128.55, x_axis_bearing_deg = 0 },
                facilities = new[]
                {
                    new Facility { location = "D1", kind = "도크", x = 430, y = 10, size_x_m = 370, size_y_m = 100 },
                    new Facility { location = "W1", kind = "작업장", x = 40, y = 10, size_x_m = 100, size_y_m = 60 },
                    new Facility { location = "D2", kind = "도크", x = 979.56f, y = 598.59f, heading_deg = 292, size_x_m = 180, size_y_m = 45 },
                    new Facility { location = "D3", kind = "도크", x = 1039.83f, y = 622.94f, heading_deg = 292, size_x_m = 180, size_y_m = 45 },
                    new Facility { location = "Q2", kind = "안벽", x = 1224, y = 514, heading_deg = 292, size_x_m = 180, size_y_m = 45 },
                },
            };
            var zones = YardLayout.Zones(twin);
            Assert.AreEqual(2, zones.Count, "Main yard and the coast facing the bay.");
            Assert.AreEqual(new[] { "D1" }, zones[0].Docks.Select(d => d.location).ToArray(), "The placement dock's zone comes first.");
            Assert.AreEqual(new[] { "W1" }, zones[0].Others.Select(d => d.location).ToArray());
            Assert.AreEqual(new[] { "Q2" }, zones[1].Others.Select(d => d.location).ToArray());
            Assert.AreEqual(615f, YardLayout.Shoreline(twin), 1e-3f);

            var bay = zones[1];
            var gate = new Vector2(979.56f, 598.59f) + Polygon2D.Rotate(new Vector2(90, 0), 292);
            Assert.AreEqual(bay.ToLocal(gate).x, bay.Shore, 0.05f, "Gates lie on the zone's shoreline.");
            var back = bay.ToWorld(bay.ToLocal(new Vector2(12, 34)));
            Assert.AreEqual(12f, back.x, 1e-3f);
            Assert.AreEqual(34f, back.y, 1e-3f);
            var site = YardEnvironment.SiteOutline(bay);
            Assert.IsTrue(Polygon2D.Contains(site, bay.ToLocal(new Vector2(979.56f, 598.59f))), "Dock inside its zone's paving.");
            Assert.LessOrEqual(site.Max(v => v.x), bay.Shore + 1e-3f);
            Assert.IsTrue(YardLayout.LandFootprints(bay).Any(f => f.location == "Q2-ROOT"), "The pier starting at the gates gets a root.");

            var sea = Polygon2D.Rect(-100, 100, -100, 100);
            var pit = Polygon2D.Oriented(Vector2.zero, 30f, -20f, 20f, -10f, 10f);
            var pieces = Polygon2D.Subtract(sea, pit);
            Assert.AreEqual(200f * 200f - 40f * 20f, pieces.Sum(Polygon2D.Area), 0.5f, "Sea minus a turned pit, without overlaps.");
            Assert.IsFalse(pieces.Any(piece => Polygon2D.Contains(piece, Polygon2D.Rotate(new Vector2(15, 5), 30f))));
            Assert.IsTrue(pieces.All(piece => piece.Count >= 3), "No degenerate sea pieces.");
            foreach (var piece in pieces)
            {
                var mesh = Polygon2D.Flat(piece, 0f);
                Assert.IsTrue(mesh.normals.All(n => n.sqrMagnitude > 0.5f), "Every sea vertex has a normal (a zero one shades as NaN in WebGL).");
                UnityEngine.Object.DestroyImmediate(mesh);
            }

            var straight = Polygon2D.Clean(new List<Vector2> { new Vector2(0, 0), new Vector2(5, 0), new Vector2(10, 0), new Vector2(10, 10), new Vector2(10, 10.001f), new Vector2(0, 10) });
            Assert.AreEqual(4, straight.Count, "Points on a straight run and repeats are dropped.");
        }

        [Test]
        public void SiteOutline_ReachesThePierRootAndJunctionsAreDetected()
        {
            var twin = TwinFile.Parse(Json);
            twin.yard = new YardPlan
            {
                site = new YardSite { id = "S", lat = 34.83, lon = 128.55, x_axis_bearing_deg = 180 },
                facilities = new[]
                {
                    new Facility { location = "D", kind = "도크", x = 430, y = 10, size_x_m = 370, size_y_m = 100 },
                    new Facility { location = "W", kind = "작업장", x = 470, y = -150, size_x_m = 120, size_y_m = 60 },
                    new Facility { location = "P", kind = "안벽", x = 935, y = -150, size_x_m = 640, size_y_m = 40 },
                },
            };
            var shore = YardLayout.Shoreline(twin);
            Assert.AreEqual(615f, shore);
            var withRoot = YardEnvironment.SiteOutline(twin);
            var withoutRoot = YardEnvironment.SiteOutline(twin.yard.facilities.Where(f => f.kind != "안벽"), shore);
            foreach (var y in new[] { -170f, -150f, -130f })
            {
                Assert.IsTrue(Polygon2D.Contains(withRoot, new Vector2(shore - 0.5f, y)), "Paving meets the pier deck at y " + y);
            }

            Assert.IsFalse(Polygon2D.Contains(withoutRoot, new Vector2(shore - 0.5f, -150f)), "Without the root the paving stops short.");
            Assert.LessOrEqual(withRoot.Max(v => v.x), shore + 1e-3f, "Never past the shoreline (berths and dock gate stay open).");
            Assert.AreEqual(3, YardLayout.LandFootprints(twin).Count, "Dock, shop and the pier root strip.");

            var main = new Road { x0 = 0, y0 = 0, x1 = 100, y1 = 0, width = 14 };
            var side = new Road { x0 = 50, y0 = 0, x1 = 50, y1 = 60, width = 8 };
            var roads = new[] { main, side };
            Assert.IsTrue(YardScenery.OnOtherRoad(new Vector2(50, 6.6f), main, roads), "Main road's edge at the side road's mouth.");
            Assert.IsFalse(YardScenery.OnOtherRoad(new Vector2(20, 6.6f), main, roads));
            Assert.IsTrue(YardScenery.OnOtherRoad(new Vector2(50, 3f), side, roads), "Side road's end inside the main road.");
            Assert.AreEqual(57f, YardScenery.Surface(main).HalfLength, 1e-4f, "Surface runs half a width past each end.");
        }

        private static YardSnapshot YardFixture()
        {
            var box = new Func<string, string, string, BlockRecord>((id, hull, day) => new BlockRecord
            {
                block = id, hull = hull, forecast = day, weight_t = 100, step = 1, x_min = 0, x_max = 12, y_min = -5, y_max = 5, z_min = 2,
                z_max = 6, cog_x = 6, cog_y = 0, cog_z = 4,
            });
            StockPlanRow Stay(string block, string location, float x, float y, string from, string until) =>
                new StockPlanRow { block = block, location = location, x = x, y = y, heading_deg = 90, stock_from = from, stock_until = until };
            return new YardSnapshot
            {
                format = YardSnapshot.Format,
                hulls = new[] { new HullRow { id = "H1", name = "H1" }, new HullRow { id = "H2", name = "H2" } },
                vessel_details = new[] { new HullInfo { hull = "H1", vessel_type = "LNG", length_m = 100, beam_m = 20 } },
                hull_geometry = new[] { new HullGeometryRow { hull = "H1", depth_m = 10, draft_m = 6, frame_spacing_m = 0.8f } },
                hull_placements = new[]
                {
                    new HullPlacement { hull = "H1", dock = "D", origin_x = 380, origin_y = 170 },
                    new HullPlacement { hull = "H2", dock = "ELSEWHERE" },
                },
                erection_timeline = new[]
                {
                    box("B1", "H1", "2027-01-10"), box("B2", "H1", "2027-01-20"), box("B3", "H1", "2027-01-25"), box("X1", "H2", "2027-01-01"),
                },
                block_stock_plans = new[]
                {
                    Stay("B1", "PE", 200, 170, "2027-01-01", "2027-01-10"),
                    Stay("B3", "STOCK", -100, -60, "2027-01-05", "2027-01-14"), Stay("B3", "PE", 210, 160, "2027-01-15", null),
                },
                location_geo = new[]
                {
                    new Facility { location = "D", name = "1도크", kind = "도크", x = 430, y = 170, size_x_m = 370, size_y_m = 100 },
                    new Facility { location = "PE", name = "1 PE장", kind = "야드", x = 197.5f, y = 170, size_x_m = 95, size_y_m = 100 },
                },
            }.Normalised();
        }

        [Test]
        public void YardModel_PlacesBlocksByPlanDay()
        {
            var model = YardModel.Build(YardFixture(), new DateTime(2027, 1, 5));
            Assert.AreEqual(new[] { "H1" }, model.Hulls.Select(h => h.Id).ToArray(), "H2 sits on a dock outside this yard.");
            Assert.AreEqual(3, model.Blocks.Count);
            Assert.AreEqual(10f, model.Hulls[0].Info.depth_m, "Geometry merged into the hull particulars.");
            Assert.AreEqual(430f, model.Hulls[0].Placement.dock_position.x, "Dock position from the facility.");
            BlockState At(string id, int month, int day) => YardModel.StateAt(model.Blocks[id], new DateTime(2027, month, day));
            Assert.AreEqual(BlockState.InProduction, YardModel.StateAt(model.Blocks["B1"], new DateTime(2026, 12, 31)));
            Assert.AreEqual(BlockState.Stocked, At("B1", 1, 5));
            Assert.AreEqual(BlockState.Erected, At("B1", 1, 10), "On its erection day it is on the hull.");
            Assert.AreEqual(BlockState.Unplaced, At("B2", 1, 5), "No stocking plan: 미배치.");
            Assert.AreEqual(BlockState.Erected, At("B2", 1, 20));
            Assert.AreEqual(BlockState.InProduction, At("B3", 1, 4));
            Assert.AreEqual("STOCK", model.Blocks["B3"].StayAt(new DateTime(2027, 1, 14)).Location);
            Assert.AreEqual("PE", model.Blocks["B3"].StayAt(new DateTime(2027, 1, 24)).Location, "Open-ended stay lasts until erection.");
            Assert.AreEqual("PE", model.Blocks["B3"].LiftSource(new DateTime(2027, 1, 23)).Location);
            Assert.IsNull(model.Blocks["B3"].LiftSource(new DateTime(2027, 1, 4)), "A future stay cannot supply the lift.");
            Assert.IsNull(model.Blocks["B1"].LiftSource(new DateTime(2027, 1, 11)), "An expired stay cannot supply the lift.");
            var counts = YardModel.Count(model.Hulls[0].Blocks, model.AsOf);
            Assert.AreEqual((2, 0, 0, 1), (counts.Stocked, counts.Erected, counts.InProduction, counts.Unplaced), "B1 and B3 (from Jan 5) stocked, B2 unplaced.");
            var twin = model.ToTwinFile(model.Hulls[0]);
            Assert.AreEqual("D", twin.placement.dock);
            Assert.AreEqual(2, twin.yard.facilities.Length);
        }

        [Test]
        public void YardModel_PutsBlocksInShopsByTheirErpStages()
        {
            var snapshot = YardFixture();
            snapshot.location_geo = snapshot.location_geo.Concat(new[]
            {
                new Facility { location = "FAB", name = "조립장", kind = "작업장", x = 0, y = 0, size_x_m = 60, size_y_m = 40 },
                new Facility { location = "OUT", name = "의장장", kind = "작업장", x = 0, y = 80, size_x_m = 60, size_y_m = 40 },
            }).ToArray();
            snapshot.center_locations = new[]
            {
                new CenterLocationRow { center = "C-FAB", location = "FAB" }, new CenterLocationRow { center = "C-OUT", location = "OUT" },
                new CenterLocationRow { center = "C-PE", location = "PE" },
            };
            snapshot.block_stages = new[]
            {
                new StagePlanRow { block = "B2", assembly_stage = "중조립", center = "C-FAB", forecast = "2027-01-08" },
                new StagePlanRow { block = "B2", assembly_stage = "대조립", center = "C-OUT", forecast = "2027-01-14", actual = "2027-01-13" },
                new StagePlanRow { block = "B2", assembly_stage = "선행탑재(PE)", center = "C-PE", forecast = "2027-01-18" },
            };
            var model = YardModel.Build(snapshot, new DateTime(2027, 1, 5));
            var b2 = model.Blocks["B2"];
            Assert.AreEqual(2, b2.ShopStages.Count, "The PE stage is a yard stay, not a shop.");
            Assert.IsNull(b2.ShopAt(new DateTime(2027, 1, 1)), "Before its first stage (a week before it ends).");
            Assert.AreEqual(("FAB", "중조립"), (b2.ShopAt(new DateTime(2027, 1, 2)).Location, b2.ShopAt(new DateTime(2027, 1, 2)).Stage));
            Assert.AreEqual("대조립", b2.ShopAt(new DateTime(2027, 1, 9)).Stage, "The next stage starts the day after the last one ended.");
            var waiting = b2.ShopAt(new DateTime(2027, 1, 16));
            Assert.IsTrue(waiting.Waiting, "Done on the actual day: waits at the last shop until its lift (no yard stay).");
            Assert.AreEqual("OUT", waiting.Location);
            Assert.IsNull(b2.ShopAt(new DateTime(2027, 1, 20)), "Lifted on its erection day.");
            Assert.AreEqual(BlockState.InProduction, YardModel.StateAt(b2, new DateTime(2027, 1, 16)), "In a shop, not unplaced.");
            Assert.AreEqual(new[] { "B2" }, model.ShopBlocks("OUT", new DateTime(2027, 1, 10)).Select(b => b.Block.Id).ToArray());
            Assert.IsEmpty(model.ShopBlocks("FAB", new DateTime(2027, 1, 10)));
        }

        [Test]
        public void YardModel_FollowsTheHullFromDockToBerthAndAway()
        {
            var snapshot = YardFixture();
            snapshot.dock_bookings = new[] { new DockBookingRow { dock = "D", hull = "H1", start_day = "2027-01-05", end_day = "2027-01-26" } };
            snapshot.shift_points = new[]
            {
                new ShiftPointRow { id = "Q1", kind = "선석", position_known = true, position = new ShiftPointPosition { x = 625, y = -196, heading_deg = 180 } },
                new ShiftPointRow { id = "SEA-TRIAL", kind = "해상" }, new ShiftPointRow { id = "DEPARTED", kind = "해상" },
            };
            snapshot.quay_shifts = new[]
            {
                new QuayShiftRow { id = "1", hull = "H1", to_point = "Q1", plan_day = "2027-01-26" },
                new QuayShiftRow { id = "2", hull = "H1", to_point = "SEA-TRIAL", plan_day = "2027-02-05" },
                new QuayShiftRow { id = "3", hull = "H1", to_point = "Q1", plan_day = "2027-02-07" },
                new QuayShiftRow { id = "4", hull = "H1", to_point = "DEPARTED", plan_day = "2027-02-12" },
            };
            var model = YardModel.Build(snapshot, new DateTime(2027, 1, 5));
            var hull = model.Hulls[0];
            HullWhere At(int month, int day) => model.HullAt(hull, new DateTime(2027, month, day)).Where;
            Assert.AreEqual(HullWhere.Building, At(1, 1));
            Assert.AreEqual(HullWhere.Dock, At(1, 26), "Launched at the end of the booking day.");
            Assert.AreEqual(HullWhere.Berth, At(1, 27));
            Assert.AreEqual(625f, model.HullAt(hull, new DateTime(2027, 1, 27)).Pose.origin_x);
            Assert.AreEqual(HullWhere.Away, At(2, 6), "Sea trial.");
            Assert.AreEqual(HullWhere.Berth, At(2, 8));
            Assert.AreEqual(HullWhere.Away, At(2, 12), "Delivered.");
            var (start, end) = model.CalendarRange();
            Assert.AreEqual(new DateTime(2027, 1, 1), start, "First yard stay.");
            Assert.AreEqual(new DateTime(2027, 2, 15), end, "A few days after delivery.");
        }

        [Test]
        public void DriveRoutes_FollowRoadsNearbyAndLanesElsewhere()
        {
            var roads = new[]
            {
                new Road { x0 = 0, y0 = 0, x1 = 200, y1 = 0, width = 10 },     // east-west
                new Road { x0 = 100, y0 = -100, x1 = 100, y1 = 100, width = 10 },   // crossing north-south
            };
            var routes = new DriveRoutes(roads, _ => 90f);
            Assert.AreEqual(5, routes.NodeCount, "Four road ends and their crossing.");
            var path = routes.Path(new Vector2(20, 10), new Vector2(110, 90));
            CollectionAssert.Contains(path, new Vector2(100, 0), "Turns at the junction.");
            Assert.AreEqual(new Vector2(20, 10), path[0]);
            Assert.AreEqual(new Vector2(110, 90), path[path.Length - 1]);
            var lanes = routes.Path(new Vector2(1000, 1000), new Vector2(1040, 1100));
            Assert.AreEqual(new[] { new Vector2(1000, 1000), new Vector2(1000, 1100), new Vector2(1040, 1100) }, lanes,
                "Far from roads: along the dock axis (90°) first, then across.");
            var (mid, heading) = DriveRoutes.At(lanes, 0.5f);
            Assert.AreEqual(90f, heading, 0.01f);
            Assert.AreEqual(1000f, mid.x, 0.01f);
        }

        [Test]
        public void YardCalendar_PlaysAtSpeedAndLoopsOverTheRange()
        {
            var calendar = new YardCalendar(new DateTime(2027, 1, 1), new DateTime(2027, 1, 11), new DateTime(2026, 1, 1), 1f);
            Assert.AreEqual(new DateTime(2027, 1, 1), calendar.Time, "A start outside the range begins at the range start.");
            calendar.NextSpeed();
            Assert.AreEqual(2f, calendar.SpeedMultiplier, "×1 → ×2.");
            calendar.Tick(4f);
            Assert.AreEqual(new DateTime(2027, 1, 9), calendar.Time);
            calendar.Tick(1.5f);
            Assert.AreEqual(new DateTime(2027, 1, 2), calendar.Time, "Past the end it loops to the start.");
            Assert.AreEqual(1, calendar.Cycle);
            calendar.Playing = false;
            calendar.Tick(1f);
            Assert.AreEqual(new DateTime(2027, 1, 2), calendar.Time);
            var entry = new ErectionSchedule.Entry(new BlockRecord { block = "B" }, 3f, 0.5f);
            Assert.AreEqual((0f, 0.5f, 1f), (ErectionSchedule.ProgressAt(entry, 2.5f), ErectionSchedule.ProgressAt(entry, 2.75f), ErectionSchedule.ProgressAt(entry, 3f)));
        }

        [Test]
        public void YardDataSource_AssemblesErpRoutesLikeTheSnapshot()
        {
            var fixture = YardFixture();
            string Array<T>(T[] rows) => "[" + string.Join(",", rows.Select(r => JsonUtility.ToJson(r))) + "]";
            var bodies = new Dictionary<string, string>
            {
                { "hulls", Array(fixture.hulls) }, { "vessel_details", Array(fixture.vessel_details) },
                { "hull_geometry", Array(fixture.hull_geometry) }, { "hull_placements", Array(fixture.hull_placements) },
                { "erection_timeline", Array(fixture.erection_timeline) }, { "block_stock_plans", Array(fixture.block_stock_plans) },
                { "location_geo", Array(fixture.location_geo) }, { "yard_sites", "[]" },
            };
            CollectionAssert.AreEquivalent(YardDataSource.Routes, bodies.Keys);
            var snapshot = YardDataSource.Assemble(bodies, "test");
            Assert.AreEqual(YardSnapshot.Format, snapshot.format);
            Assert.AreEqual(3, snapshot.block_stock_plans.Length);
            Assert.IsNull(snapshot.shift_points);
            bodies["shift_points"] = "[{\"id\":\"A\",\"position_known\":true,\"position\":{\"x\":0,\"heading_deg\":90.5}},{\"id\":\"B\",\"position_known\":false}]";
            bodies["quay_shifts"] = "[]";
            var planned = YardDataSource.Assemble(bodies, "test");
            Assert.IsTrue(planned.shift_points[0].position_known);
            Assert.AreEqual(90.5f, planned.shift_points[0].position.heading_deg);
            Assert.IsFalse(planned.shift_points[1].position_known);
            Assert.IsEmpty(planned.quay_shifts);
            var model = YardModel.Build(snapshot, new DateTime(2027, 1, 5));
            Assert.AreEqual(3, model.Blocks.Count);
            Assert.AreEqual(BlockState.Stocked, YardModel.StateAt(model.Blocks["B1"], model.AsOf));
            Assert.Throws<FormatException>(() => YardSnapshot.Parse("{\"format\":\"other\"}"));
        }

        [Test]
        public void RailCranes_FollowTheErpCranePlan()
        {
            // GET /api/crane_activity (apps/erp/yardsim.py crane_activity): one crane lifting outfitting material for its
            // ERP assignment, one without work waiting parked with nothing on the hook.
            const string json = @"{""time"": ""2026-11-22T10:31:00+09:00"", ""cranes"": [
              {""id"": ""JC-GY-DOCK2-2"", ""crane_type"": ""jib"", ""state"": ""인양"", ""work"": ""의장품 인양"", ""rail_pos"": 0.692,
               ""slew_deg"": 271.5, ""luff_deg"": 47.46, ""radius_m"": 48.6, ""hook"": {""x"": 1, ""y"": 2, ""z"": 22.0}, ""load_t"": 12.0,
               ""loaded"": true, ""source"": ""estimated""},
              {""id"": ""JC-GY-DOCK2-1"", ""crane_type"": ""jib"", ""state"": ""대기"", ""work"": null, ""rail_pos"": 0.38,
               ""slew_deg"": 90.0, ""luff_deg"": 74.0, ""hook"": {""x"": 0, ""y"": 0, ""z"": 40.0}, ""load_t"": 0.0, ""loaded"": false, ""source"": ""plan""}]}";
            var activity = CraneActivity.Parse(json);
            Assert.AreEqual(2, activity.cranes.Length);
            var working = activity.cranes[0];
            Assert.IsTrue(working.IsJib && working.load_t > 0f);
            Assert.AreEqual((271.5f, 47.46f, 22f), (working.slew_deg, working.luff_deg, working.hook.z));
            Assert.AreEqual("의장품 인양", working.work);
            var idle = activity.cranes[1];
            Assert.AreEqual(("대기", 0f), (idle.state, idle.load_t), "No ERP job: nothing on the hook.");
            Assert.IsEmpty(CraneActivity.Parse("{}").cranes);

            // Parked rail position shared with the ERP (middle of the travel zone) and boom angles from radii.
            Assert.AreEqual(0.5f, RailCraneMotion.Home("JC-GY-DOCK2-1"), 1e-6f);
            var boom = new CraneKinematicsRow { boom_m = 72f, pivot_height_m = 32f, luff_min_deg = 15f, luff_max_deg = 82f, rest_luff_deg = 74f };
            Assert.AreEqual(56.25f, RailCraneMotion.LuffFor(boom, 40f), 0.01f, "acos(40/72).");
            Assert.AreEqual(82f, RailCraneMotion.LuffFor(boom, 1f), 0.01f, "Clamped to the steepest boom angle.");
            Assert.AreEqual(28f, RailCraneMotion.TopHook(boom), 1e-4f);
        }

        [Test]
        public void SeaSurface_RingIsDenseNearTheCentre()
        {
            var mesh = SeaSurface.BuildRing();
            Assert.AreEqual(1 + SeaSurface.Rings * SeaSurface.Segments, mesh.vertexCount);
            Assert.Less(mesh.vertexCount, 6000, "cheap enough for WebGL and phones");
            var radii = mesh.vertices.Select(v => new Vector2(v.x, v.z).magnitude).Distinct().OrderBy(r => r).ToList();
            Assert.Less(radii[2] - radii[1], radii[radii.Count - 1] - radii[radii.Count - 2], "inner rings closer together");
            Assert.AreEqual(1f, radii.Last(), 1e-4f);
        }

        [Test]
        public void Perimeter_OpensWhereRoadsCrossAndTreesStandUp()
        {
            // Wall along x from 0 to 100; a 12 m road crossing at x = 40, one ending 5 m short of the wall, one parallel.
            var crossing = YardEnvironment.Crossing(Vector2.zero, new Vector2(100f, 0f), new Vector2(40f, -30f), new Vector2(40f, 30f), 12f);
            Assert.IsTrue(crossing.HasValue);
            Assert.AreEqual(40f, crossing.Value.At, 1e-3f);
            Assert.AreEqual(8f, crossing.Value.Half, 1e-3f, "half the road plus 2 m");
            Assert.IsTrue(YardEnvironment.Crossing(Vector2.zero, new Vector2(100f, 0f), new Vector2(60f, -30f), new Vector2(60f, -4f), 12f).HasValue,
                "the road surface runs half a width past its end");
            Assert.IsFalse(YardEnvironment.Crossing(Vector2.zero, new Vector2(100f, 0f), new Vector2(0f, 5f), new Vector2(100f, 5f), 8f).HasValue);
            Assert.AreEqual(5f, YardEnvironment.SegmentDistance(new Vector2(50f, 5f), Vector2.zero, new Vector2(100f, 0f)), 1e-4f);
            var crown = YardEnvironment.TreeCrown();
            Assert.Less(crown.vertexCount, 40, "cheap enough to merge hundreds");
            Assert.Greater(crown.normals.Where((n, i) => crown.vertices[i].y > 0.1f).Average(n => n.y), 0f, "crown faces out and up");
        }

        [Test]
        public void CraneReach_FollowsATurnedDock()
        {
            // South-line dock: 180 x 45 m turned to 292° (gate toward the bay).
            var dock = new Facility { location = "S-DK", kind = "도크", x = 1000f, y = 600f, size_x_m = 180f, size_y_m = 45f, heading_deg = 292f };
            var reach = BlockMotion.CraneReach(dock);
            var a = 292f * Mathf.Deg2Rad;
            var along = new Vector2(Mathf.Cos(a), Mathf.Sin(a));
            var centre = new Vector2(dock.x, dock.y);
            Assert.IsTrue(reach.Contains(centre + along * 60f), "Inside the pit toward the gate.");
            Assert.IsTrue(reach.Contains(centre - along * 140f), "The PE yard behind the dock head is under the rails.");
            Assert.IsFalse(reach.Contains(centre + new Vector2(0f, 80f)), "Yard-axis offset past the walls is out of reach.");
            Assert.IsFalse(reach.Contains(centre + new Vector2(140f, 0f)), "A yard-axis rectangle would wrongly reach here.");
            Assert.IsTrue(reach.Contains(reach.Pickup), "The pick-up point is under the crane.");
            Assert.Less(Vector2.Distance(reach.Pickup, centre - along * (90f - YardLayout.WallThickness + 50f)), 1e-3f);
        }

        [Test]
        public void BlockMotion_LiftsFromTheSlotOrDrivesToTheCraneFirst()
        {
            var model = YardModel.Build(YardFixture(), new DateTime(2027, 1, 5));
            var hull = model.Hulls[0];
            var b1 = model.Blocks["B1"];
            var reach = BlockMotion.CraneReach(hull.Dock);
            Assert.IsTrue(reach.Contains(new Vector2(197.5f, 170f)), "The PE yard at the dock head is under the rails.");
            Assert.IsFalse(reach.Contains(new Vector2(-100f, -60f)), "The block stockyard is not.");
            var slot = BlockMotion.InYard(b1.Record, b1.Stays[0]);
            Assert.AreEqual(YardLayout.QuayLevel + BlockMotion.Dunnage + 2f, slot.position.y, 1e-4f, "Resting on its bottom (cog 4, z_min 2).");
            Assert.AreEqual(90f, slot.rotation.eulerAngles.y > 180f ? 360f - slot.rotation.eulerAngles.y : slot.rotation.eulerAngles.y, 1e-3f);
            var landed = BlockMotion.Landed(hull.Placement, b1.Record);
            Assert.AreEqual(new Vector3(386f, 4f, 170f), landed.position);
            Assert.AreEqual(slot.position, BlockMotion.Lift(slot, landed, 0f, reach).Pose.position);
            Assert.AreEqual(landed.position, BlockMotion.Lift(slot, landed, 1f, reach).Pose.position);
            var carried = BlockMotion.Lift(slot, landed, 0.5f, reach);
            Assert.IsTrue(carried.Hooked);
            Assert.Greater(carried.Pose.position.y, landed.position.y + 20f, "Carried high over the dock wall.");

            var far = BlockMotion.InYard(model.Blocks["B3"].Record, model.Blocks["B3"].Stays[0]);   // block stockyard, out of reach
            var driving = BlockMotion.Lift(far, landed, BlockMotion.GroundShare * 0.5f, reach);
            Assert.IsFalse(driving.Hooked, "Driven to the crane on the ground first.");
            Assert.AreEqual(far.position.y, driving.Pose.position.y, 1e-4f);
            var atCrane = BlockMotion.Lift(far, landed, BlockMotion.GroundShare, reach);
            Assert.AreEqual(reach.Pickup, BlockMotion.YardOf(atCrane.Pose.position));
            Assert.IsTrue(atCrane.Hooked);
            var noSlot = BlockMotion.Lift(null, landed, 0f, reach);
            Assert.AreEqual(reach.Pickup, BlockMotion.YardOf(noSlot.Pose.position));
            Assert.Greater(noSlot.Pose.position.y, landed.position.y, "Unplaced blocks start suspended above the pickup point.");
        }

        [Test]
        public void BlockMotion_RegisteredCraneCarriesWithinItsRailAndLowersVertically()
        {
            Assert.IsTrue(BlockMotion.TryCraneReach(new CraneRailRow
            {
                equipment_type = "골리앗 크레인", rail_x0 = 200f, rail_y0 = 300f,
                rail_x1 = 280f, rail_y1 = 360f, gauge_m = 120f,
            }, out var reach));
            var landing = new Pose(new Vector3(249f, 4f, 355f), Quaternion.identity);
            Assert.IsTrue(reach.Contains(BlockMotion.YardOf(landing.position)));
            Assert.IsFalse(reach.Contains(new Vector2(200f, 365f)));

            var start = new Pose(new Vector3(200f, 15f, 365f), Quaternion.identity);
            Assert.IsFalse(BlockMotion.Lift(start, landing, 0.1f, reach).Hooked);
            foreach (var source in new Pose?[] { start, null })
            {
                var pickup = BlockMotion.Lift(source, landing, BlockMotion.GroundShare, reach);
                Assert.IsTrue(pickup.Hooked);
                Assert.IsTrue(reach.Contains(BlockMotion.YardOf(pickup.Pose.position)));
                var high = BlockMotion.Lift(source, landing, 0.85f, reach);
                var low = BlockMotion.Lift(source, landing, 0.95f, reach);
                Assert.AreEqual(BlockMotion.YardOf(landing.position), BlockMotion.YardOf(high.Pose.position));
                Assert.AreEqual(BlockMotion.YardOf(landing.position), BlockMotion.YardOf(low.Pose.position));
                Assert.Greater(high.Pose.position.y, low.Pose.position.y);
            }
        }

        private static float Area(System.Collections.Generic.List<Vector2> p)
        {
            var a = 0f;
            for (var i = 0; i < p.Count; i++)
            {
                a += p[i].x * p[(i + 1) % p.Count].y - p[(i + 1) % p.Count].x * p[i].y;
            }

            return Mathf.Abs(a) / 2f;
        }

        [Test]
        public void Terrain_InterpolatesGridAndMapsMercator()
        {
            var meta = new SatelliteMeta { zoom = 16, tile_x0 = 0, tile_y0 = 0, size_px = 512, grid = 2 };
            var terrain = new TerrainModel(meta, new float[] { 0, 10, 20, 0, 10, 20, 0, 10, 20 });
            Assert.AreEqual(10f, terrain.AtPixel(256, 100), 1e-4f, "Middle column.");
            Assert.AreEqual(15f, terrain.AtPixel(384, 0), 1e-4f, "Halfway between columns 1 and 2.");
            Assert.AreEqual(20f, terrain.AtPixel(9999, 0), 1e-4f, "Clamped at the edge.");
            Assert.Throws<ArgumentException>(() => new TerrainModel(meta, new float[3]));
            var (x, y) = ShipCoordinates.GeoToMercatorPixel(16, 34.8331, 128.5975);
            var (lat, lon) = ShipCoordinates.MercatorPixelToGeo(16, x, y);
            Assert.AreEqual(34.8331, lat, 1e-9);
            Assert.AreEqual(128.5975, lon, 1e-9);
        }

        [Test]
        public void Dashboard_StartsClosedAndPausedWithRailControls()
        {

            var asset = UnityEditor.AssetDatabase.LoadAssetAtPath<UnityEngine.UIElements.VisualTreeAsset>(
                "Assets/ShipyardTwin/Authoring/Dashboard.uxml");
            Assert.IsNotNull(asset);
            var root = asset.CloneTree();
            var rail = root.Q("rail");
            foreach (var name in new[] { "nav-erection", "nav-depth" })
            {
                Assert.IsNotNull(rail.Q<UnityEngine.UIElements.Button>(name), name + " sits in the rail.");
            }

            // The assistant is the web page's yard guide character, not a rail button or drawer.
            Assert.IsNull(root.Q("nav-chat"), "No chat button in the rail.");
            Assert.IsNull(root.Q("chat-panel"), "No chat drawer.");

            foreach (var name in new[] { "exit", "erection-panel", "depth-legend", "exit-confirm", "play", "speed", "restart", "events" })
            {
                Assert.IsNotNull(root.Q(name), name);
            }

            var top = root.Q("top-nav");
            Assert.IsNotNull(top);
            foreach (var name in new[] { "nav-yard-clock", "nav-layers", "nav-help", "exit" })
            {
                Assert.IsNotNull(top.Q<Button>(name), name);
                Assert.IsNull(rail.Q(name), name);
            }

            var layers = root.Q("layers-panel");
            Assert.IsFalse(layers.ClassListContains("tw-utility-panel--open"));
            Assert.IsFalse(root.Q("help-panel").ClassListContains("tw-utility-panel--open"));
            foreach (var name in new[] { "layer-cranes", "layer-vehicles", "layer-buildings", "layer-hulls", "layer-blocks" })
            {
                var toggle = layers.Q<Toggle>(name);
                Assert.IsTrue(toggle.value, name + " starts visible.");
                Assert.IsNotNull(toggle.Q(className: "unity-toggle__input").Q(className: "unity-toggle__checkmark"),
                    name + " has the elements used by the switch styling.");
            }

            Assert.IsFalse(root.Q("erection-panel").ClassListContains("tw-drawer--open"), "Erection panel starts closed.");
        }

        [Test]
        public void YardDisplayLayers_PreserveOwnerAndChildStatesIncludingLateArrivals()
        {
            var owner = new GameObject("Yard");
            try
            {
                var layers = new YardDisplayLayers();
                var vehicles = layers.ParentFor(owner.transform, YardDisplayLayer.Vehicles);
                var inUse = new GameObject("In use");
                inUse.transform.SetParent(vehicles, false);
                var parked = new GameObject("Inactive pooled vehicle");
                parked.transform.SetParent(vehicles, false);
                parked.SetActive(false);

                layers.SetVisible(YardDisplayLayer.Vehicles, false);
                var lateOwner = new GameObject("ERP replay");
                lateOwner.transform.SetParent(owner.transform, false);
                var lateVehicles = layers.ParentFor(lateOwner.transform, YardDisplayLayer.Vehicles);
                var buildings = layers.ParentFor(owner.transform, YardDisplayLayer.Buildings);

                Assert.IsTrue(owner.activeInHierarchy && lateOwner.activeInHierarchy);
                Assert.IsTrue(buildings.gameObject.activeInHierarchy);
                Assert.IsFalse(inUse.activeInHierarchy || lateVehicles.gameObject.activeInHierarchy);
                Assert.AreSame(vehicles, layers.ParentFor(owner.transform, YardDisplayLayer.Vehicles));

                layers.SetVisible(YardDisplayLayer.Vehicles, true);
                Assert.IsTrue(inUse.activeInHierarchy && lateVehicles.gameObject.activeInHierarchy);
                Assert.IsFalse(parked.activeInHierarchy);
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(owner);
            }
        }

        [Test]
        public void YardDisplayLayers_HullsAndLooseBlocksCanBeShownIndependently()
        {
            var owner = new GameObject("Yard");
            try
            {
                var layers = new YardDisplayLayers();
                var hulls = layers.ParentFor(owner.transform, YardDisplayLayer.Hulls);
                var blocks = layers.ParentFor(owner.transform, YardDisplayLayer.Blocks);
                layers.SetVisible(YardDisplayLayer.Blocks, false);
                Assert.IsTrue(hulls.gameObject.activeInHierarchy);
                Assert.IsFalse(blocks.gameObject.activeInHierarchy);

                layers.SetVisible(YardDisplayLayer.Hulls, false);
                layers.SetVisible(YardDisplayLayer.Blocks, true);
                Assert.IsTrue(blocks.gameObject.activeInHierarchy);
                Assert.IsFalse(hulls.gameObject.activeInHierarchy);
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(owner);
            }
        }
    }
}
