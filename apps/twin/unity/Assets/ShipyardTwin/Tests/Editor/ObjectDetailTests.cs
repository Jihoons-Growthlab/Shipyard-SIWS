using System;
using System.Linq;
using GrowthLab.UI;
using GrowthLab.UI.Charts;
using NUnit.Framework;

namespace ShipyardTwin.Tests
{
    /// <summary>Detail popup content per clicked object (<see cref="ObjectDetailBuilder"/>), without a scene.</summary>
    public sealed class ObjectDetailTests
    {
        private static readonly DateTime AsOf = new DateTime(2026, 11, 20);

        private static YardSnapshot Snapshot() => new YardSnapshot
        {
            source = "http://erp",
            vessel_details = new[] { new HullInfo { hull = "H1", vessel_name = "가람호", vessel_type = "LNG선", length_m = 200, beam_m = 32, depth_m = 18, delivery = "2027-03-01" } },
            erection_timeline = new[]
            {
                new BlockRecord { block = "H1-B01", hull = "H1", zone = "선미", weight_t = 150, step = 1, baseline = "2026-11-10", forecast = "2026-11-12", erected_day = "2026-11-12" },
                new BlockRecord { block = "H1-B02", hull = "H1", zone = "선수", weight_t = 120, step = 2, baseline = "2026-11-15", forecast = "2026-11-18", predecessors = new[] { "H1-B01" } },
            },
            location_geo = new[]
            {
                new Facility { location = "DK1", name = "1도크", kind = "도크", size_x_m = 300, size_y_m = 60 },
                new Facility { location = "QW1", name = "의장 안벽", kind = "안벽", size_x_m = 400, size_y_m = 30 },
                new Facility { location = "ST1", name = "적치장", kind = "야드" },
            },
            quay_shifts = new[]
            {
                new QuayShiftRow { id = "S1", hull = "H1", from_point = "DK1", to_point = "QW1", plan_day = "2026-11-01", actual_day = "2026-11-02", shift_purpose = "진수 후 이동" },
                new QuayShiftRow { id = "S2", hull = "H1", from_point = "QW1", to_point = "SEA", plan_day = "2026-11-18", shift_purpose = "시운전 출항" },
                new QuayShiftRow { id = "S3", hull = "H1", from_point = "SEA", to_point = "QW1", plan_day = "2026-12-05", shift_purpose = "시운전 귀항" },
            },
            shift_points = new[] { new ShiftPointRow { id = "SEA", name = "시운전 해역" } },
            berth_assignments = new[] { new BerthAssignmentRow { id = "BA1", hull = "H1", berth = "QW1", start_day = "2026-11-02", end_day = "2027-02-28", berth_purpose = "안벽 의장" } },
            dock_bookings = new[] { new DockBookingRow { id = "DB1", dock = "DK1", hull = "H1", start_day = "2026-09-01", end_day = "2026-11-01" },
                                    new DockBookingRow { id = "DB2", dock = "DK1", hull = "H2", start_day = "2026-11-10", end_day = "2027-01-31" } },
            crane_rails = new[] { new CraneRailRow { equipment = "JC1", name = "1번 지브", equipment_type = "지브 크레인", station = "QW1", capacity_t = 40, rail_x1 = 100, gauge_m = 10 } },
            crane_assignments = new[]
            {
                new CraneAssignmentRow { id = "C1", equipment = "JC1", hull = "H1", block = "H1-B02", crane_work = "의장품 인양", work_area = "QW1", work_day = "2026-11-20", start_hour = 8, hours = 4, lift_t = 12 },
                new CraneAssignmentRow { id = "C2", equipment = "JC1", hull = "H1", crane_work = "의장품 인양", work_area = "QW1", work_day = "2026-11-24", start_hour = 13, hours = 2, lift_t = 30 },
            },
            moves = new[] { new BlockMoveRow { id = "M1", block = "H1-B02", equipment = "TP1", origin = "ST1", destination = "DK1", started_at = "2026-11-19T01:00:00+00:00" } },
            block_stock_plans = new[] { new StockPlanRow { id = "SP1", block = "H1-B02", hull = "H1", location = "ST1", stock_from = "2026-11-15", stock_until = "2026-11-25" } },
            hull_overview = new[] { new HullOverviewRow { hull = "H1", lifecycle_status = "안벽 의장", erection_planned_count = 2, erection_accepted_count = 1 } },
        }.Normalised();

        private static StaticDetailRows ErpRows() => new StaticDetailRows()
            .Add(ObjectDetailBuilder.OutfitWorks,
                new OutfitWorkRow { id = "W1", hull = "H1", outfit_zone = "기관실", outfit_trade = "배관", work_phase = "안벽 의장", start_day = "2026-11-10", end_day = "2026-11-30", planned_mh = 100, earned_mh = 50, progress_pct = 50, work_status = "진행", schedule_check = "정상" },
                new OutfitWorkRow { id = "W2", hull = "H1", outfit_zone = "거주구", outfit_trade = "전장", work_phase = "안벽 의장", start_day = "2026-11-01", end_day = "2026-11-10", planned_mh = 100, earned_mh = 100, progress_pct = 100, work_status = "완료", schedule_check = "정상" },
                new OutfitWorkRow { id = "W3", hull = "H2", outfit_zone = "기관실", start_day = "2026-11-01", end_day = "2026-11-10", planned_mh = 999 })
            .Add(ObjectDetailBuilder.OutfitProgress,
                new OutfitProgressRow { hull = "H1", outfit_zone = "기관실", planned_to_date_mh = 60, earned_mh = 50, spi = 0.83f },
                new OutfitProgressRow { hull = "H1", outfit_zone = "거주구", planned_to_date_mh = 100, earned_mh = 100, spi = 1f })
            .Add(ObjectDetailBuilder.Milestones, new HullMilestoneRow { hull = "H1", milestone = "진수", baseline = "2026-11-01", forecast = "2026-11-01", actual = "2026-11-01" })
            .Add(ObjectDetailBuilder.BlockStages,
                new BlockStageRow { block = "H1-B02", assembly_stage = "소조립", baseline = "2026-10-01", forecast = "2026-10-01", actual = "2026-10-01", planned_mh = 100, actual_mh = 90, progress_pct = 100 },
                new BlockStageRow { block = "H1-B02", assembly_stage = "대조립", baseline = "2026-10-20", forecast = "2026-10-25", planned_mh = 200, actual_mh = 120, progress_pct = 60 })
            .Add(ObjectDetailBuilder.BlockProgress, new BlockProgressRow { block = "H1-B02", progress_pct = 73, stages = 2, completed_stages = 1, current_stage = "대조립", productivity = 0.8f })
            .Add(ObjectDetailBuilder.CraneLoad, new CraneLoadRow { equipment = "JC1", week = "2026-11-16", utilization_pct = 55 }, new CraneLoadRow { equipment = "JC1", week = "2026-11-23", utilization_pct = 92 });

        private static DetailContext Context(IDetailRows rows = null) => new DetailContext(Snapshot(), null, AsOf, rows ?? ErpRows());

        private static DetailTarget Target(TargetCategory category, string id, string hull = null, string title = null, string kind = null) =>
            new DetailTarget(category, id, hull, null, kind, title ?? id, null);

        private sealed class LoadingRows : IDetailRows
        {
            public T[] Get<T>(string route) => null;
            public string Missing(string route) => "조회 중";
        }

        [Test]
        public void Hull_ShowsQuayShiftPlan_OutfitProgress_AndErection()
        {
            var detail = ObjectDetailBuilder.Build(Target(TargetCategory.Hull, "H1", "H1"), Context());

            Assert.AreEqual("가람호", detail.Title);
            Assert.AreEqual("안벽 의장", detail.Status);
            Assert.AreEqual("의장 안벽", detail.Facts.Single(f => f.Label == "현재 위치").Value);
            StringAssert.Contains("시운전 해역 → 의장 안벽", detail.Facts.Single(f => f.Label == "다음 안벽 이동").Value);
            Assert.AreEqual(StatusLevel.Warning, detail.Facts.Single(f => f.Label == "이동 지연").Tone);   // S2 planned 11-18, no actual

            var master = detail.Sections.Single(s => s.Title == "안벽 이동 · 대일정");
            var schedule = (TimelineChartSpec)master.Charts.Single();
            Assert.AreEqual(3, schedule.Items.Count(i => i.Row == "안벽 이동"));
            Assert.AreEqual(1, schedule.Items.Count(i => i.Row == "선석"));
            Assert.AreEqual(1, schedule.Items.Count(i => i.Row == "대일정"));
            Assert.AreEqual(3, master.Rows.Count);

            var outfit = detail.Sections.Single(s => s.Title == "의장 진척");
            Assert.IsFalse(outfit.Pending);
            var donut = (DonutChartSpec)outfit.Charts[0];
            Assert.AreEqual("75%", donut.CenterText);   // 150 earned of 200 planned (H2's work left out)
            var bars = (BarChartSpec)outfit.Charts[1];
            CollectionAssert.AreEqual(new[] { "기관실", "거주구" }, bars.Categories);
            StringAssert.Contains("기관실", outfit.Rows.Single(r => r.Label == "가장 늦은 구역").Value);
            Assert.AreEqual(2, ((TimelineChartSpec)detail.Sections.Single(s => s.Title == "의장 작업 (진행·예정)").Charts.Single()).Items.Count);
        }

        [Test]
        public void Hull_MarksOutfitPending_WhileTheErpAnswers()
        {
            var detail = ObjectDetailBuilder.Build(Target(TargetCategory.Hull, "H1", "H1"), Context(new LoadingRows()));
            var outfit = detail.Sections.Single(s => s.Title == "의장 진척");
            Assert.IsTrue(outfit.Pending);
            Assert.IsEmpty(outfit.Charts);
            Assert.IsFalse(detail.Sections.Any(s => s.Title == "의장 작업 (진행·예정)"));
        }

        [Test]
        public void Block_ShowsStagesManHoursAndSchedule()
        {
            var detail = ObjectDetailBuilder.Build(Target(TargetCategory.Block, "H1-B02", "H1"), Context());
            Assert.AreEqual("탑재 지연", detail.Status);   // forecast 11-18 passed, not erected
            Assert.AreEqual(StatusLevel.Warning, detail.StatusLevel);
            Assert.AreEqual("B01", detail.Facts.Single(f => f.Label == "선행 블록").Value);

            var assembly = detail.Sections.Single(s => s.Title == "조립 단계 진척");
            Assert.AreEqual("73%", ((DonutChartSpec)assembly.Charts[0]).CenterText);
            CollectionAssert.AreEqual(new[] { 100f, 60f }, ((BarChartSpec)assembly.Charts[1]).Series[0].Values);
            Assert.AreEqual(2, ((BarChartSpec)detail.Sections.Single(s => s.Title == "공수").Charts.Single()).Series.Count);
            var schedule = (TimelineChartSpec)detail.Sections.Single(s => s.Title == "일정").Charts.Single();
            Assert.IsTrue(schedule.Items.Any(i => i.Row == "탑재" && i.Milestone && i.Hollow));
        }

        [Test]
        public void Crane_ShowsWeeklyLoadAndAssignments()
        {
            var detail = ObjectDetailBuilder.Build(Target(TargetCategory.Crane, "JC1", title: "1번 지브"), Context());
            Assert.AreEqual("오늘 작업 1건", detail.Status);
            Assert.AreEqual("40 t", detail.Facts.Single(f => f.Label == "정격하중").Value);
            var load = (BarChartSpec)detail.Sections.Single(s => s.Title == "가동 현황").Charts.Single();
            CollectionAssert.AreEqual(new[] { 55f, 92f }, load.Series[0].Values);
            Assert.AreEqual(85f, load.Threshold);
            var jobs = detail.Sections.Single(s => s.Title == "작업 계획");
            Assert.AreEqual(2, ((TimelineChartSpec)jobs.Charts[0]).Items.Count);
            Assert.AreEqual(40f, ((BarChartSpec)jobs.Charts[1]).Threshold);
        }

        [Test]
        public void VehicleDockQuayYardAndProp_HaveTheirOwnViews()
        {
            var vehicle = ObjectDetailBuilder.Build(Target(TargetCategory.Vehicle, "TP1", title: "TP1"), Context());
            Assert.AreEqual("이동 중", vehicle.Status);

            var dock = ObjectDetailBuilder.Build(Target(TargetCategory.Facility, "DK1", title: "1도크"), Context());
            Assert.AreEqual("건조 중 · H2", dock.Status);
            Assert.IsInstanceOf<DonutChartSpec>(dock.Sections.Single(s => s.Title == "도크 점유").Charts[0]);

            var quay = ObjectDetailBuilder.Build(Target(TargetCategory.Facility, "QW1", title: "의장 안벽"), Context());
            Assert.AreEqual("접안 H1", quay.Status);

            var yard = ObjectDetailBuilder.Build(Target(TargetCategory.Facility, "ST1", title: "적치장"), Context());
            Assert.AreEqual("블록 1개 적치", yard.Status);

            var prop = ObjectDetailBuilder.Build(Target(TargetCategory.Prop, "forklift", title: "지게차", kind: "중장비"), Context());
            Assert.AreEqual(StatusLevel.Offline, prop.StatusLevel);
            Assert.IsTrue(prop.Sections.All(s => s.Charts.Count == 0));
        }

        [Test]
        public void Routes_AreOnlyTheOnesTheSnapshotLacks()
        {
            CollectionAssert.AreEquivalent(new[] { "outfit_works", "outfit_progress", "hull_milestones" }, ObjectDetailBuilder.Routes(Target(TargetCategory.Hull, "H1")));
            CollectionAssert.AreEquivalent(new[] { "block_stages", "block_progress" }, ObjectDetailBuilder.Routes(Target(TargetCategory.Block, "B")));
            CollectionAssert.IsEmpty(ObjectDetailBuilder.Routes(Target(TargetCategory.Facility, "DK1")));
        }

        [Test]
        public void ShopBoard_LaysBlocksOutByStage_LateFirst()
        {
            BlockPlan Block(string id) => new BlockPlan(new BlockRecord { block = id, hull = "H1" }, new System.Collections.Generic.List<StockStay>());
            var day = new DateTime(2026, 11, 20);
            var board = ObjectDetailBuilder.ShopBoard(new[]
            {
                (Block("H1-W1"), new ShopStage("FAB", "반출 대기", day.AddDays(-10), day.AddDays(5), waiting: true)),
                (Block("H1-A1"), new ShopStage("FAB", "중조립", day.AddDays(-2), day.AddDays(3))),
                (Block("H1-A2"), new ShopStage("FAB", "중조립", day.AddDays(-6), day.AddDays(-2))),
                (Block("H1-C1"), new ShopStage("FAB", "절단·가공", day, day)),
            }, day);

            CollectionAssert.AreEqual(new[] { "절단·가공", "중조립", "반출 대기" }, board.Bays.Select(b => b.Title));
            Assert.AreEqual(4, board.Total);
            Assert.AreEqual("D-day", board.Bays[0].Chips[0].Note);
            Assert.IsFalse(board.MultiHull);
            var assembly = board.Bays[1].Chips;
            Assert.AreEqual("A2", assembly[0].Block, "The late block leads its lane.");
            Assert.AreEqual("2일 지연", assembly[0].Note);
            Assert.AreEqual(StatusLevel.Alarm, assembly[0].Tone);
            Assert.AreEqual("D-3", assembly[1].Note);
            Assert.AreEqual("대기 10일", board.Bays[2].Chips[0].Note);
            Assert.AreEqual(StatusLevel.Alarm, board.Bays[2].Chips[0].Tone, "Waiting more than a week.");
        }

        [Test]
        public void Block_UsesSnapshotStages_WithoutTheErp()
        {
            var snapshot = Snapshot();
            snapshot.block_stages = new[]
            {
                new StagePlanRow { block = "H1-B02", assembly_stage = "소조립", baseline = "2026-10-01", forecast = "2026-10-01", actual = "2026-10-01", progress_pct = 100 },
                new StagePlanRow { block = "H1-B02", assembly_stage = "대조립", baseline = "2026-10-20", forecast = "2026-10-25", progress_pct = 40 },
            };
            var detail = ObjectDetailBuilder.Build(Target(TargetCategory.Block, "H1-B02", "H1"), new DetailContext(snapshot, null, AsOf, new LoadingRows()));
            var assembly = detail.Sections.Single(s => s.Title == "조립 단계 진척");
            Assert.IsFalse(assembly.Pending);
            StringAssert.Contains("스냅샷", assembly.Note);
            Assert.AreEqual("70%", ((DonutChartSpec)assembly.Charts[0]).CenterText);
            Assert.IsFalse(detail.Sections.Any(s => s.Title == "공수"), "No man-hours in the snapshot.");
        }

        [Test]
        public void SCurve_IsCumulativePerWeek_ActualStopsAtThePlanDay()
        {
            var blocks = Snapshot().erection_timeline.Select(r => new BlockPlan(r, new System.Collections.Generic.List<StockStay>())).ToList();
            var curve = ObjectDetailBuilder.SCurve(blocks, AsOf);
            Assert.AreEqual(2f, curve.Series[0].Values.Last());
            Assert.AreEqual(1f, curve.Series[1].Values[curve.MarkerIndex]);
            Assert.GreaterOrEqual(curve.MarkerIndex, 0);
        }
    }
}
