using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using GrowthLab.UI;
using GrowthLab.UI.Charts;
using UnityEngine;

namespace ShipyardTwin
{
    /// <summary>Everything a detail is built from: the yard snapshot and model, the plan day and on-demand ERP rows.</summary>
    public sealed class DetailContext
    {
        public DetailContext(YardSnapshot snapshot, YardModel model, DateTime asOf, IDetailRows rows)
        {
            Snapshot = snapshot ?? new YardSnapshot().Normalised();
            Model = model;
            AsOf = asOf.Date;
            Rows = rows ?? new StaticDetailRows();
        }

        public YardSnapshot Snapshot { get; }
        public YardModel Model { get; }
        public DateTime AsOf { get; }
        public IDetailRows Rows { get; }
    }

    /// <summary>
    /// Builds the popup content of a clicked object from the plan data, one builder per <see cref="TargetCategory"/>:
    /// hull (quay shifts, berths, milestones, outfitting progress and work, erection S-curve), block (assembly stages,
    /// man-hours, stays and erection), crane (weekly load, assignments, lift weights), vehicle (block moves), facility
    /// (dock bookings, berth use, blocks stocked) and background props. Charts are <see cref="ChartSpec"/> data drawn by
    /// GrowthLab.UI. <see cref="Routes"/> names the extra ERP routes a target needs; until they arrive the sections
    /// that use them are marked pending. Pure: no scene access.
    /// </summary>
    public static class ObjectDetailBuilder
    {
        public const string OutfitWorks = "outfit_works", OutfitProgress = "outfit_progress", Milestones = "hull_milestones";
        public const string BlockStages = "block_stages", BlockProgress = "block_progress", CraneLoad = "crane_load";

        private static readonly Color Plan = ChartPalette.Muted, Done = ChartPalette.Normal, Live = ChartPalette.Accent;

        /// <summary>ERP routes beyond the snapshot that the detail of this target uses.</summary>
        public static string[] Routes(DetailTarget target)
        {
            switch (target.Category)
            {
                case TargetCategory.Hull: return new[] { OutfitWorks, OutfitProgress, Milestones };
                case TargetCategory.Block: return new[] { BlockStages, BlockProgress };
                case TargetCategory.Crane: return new[] { CraneLoad };
                default: return new string[0];
            }
        }

        public static ObjectDetail Build(DetailTarget target, DetailContext ctx)
        {
            ObjectDetail detail;
            switch (target.Category)
            {
                case TargetCategory.Hull: detail = Hull(target, ctx); break;
                case TargetCategory.Block: detail = Block(target, ctx); break;
                case TargetCategory.Crane: detail = Crane(target, ctx); break;
                case TargetCategory.Vehicle: detail = Vehicle(target, ctx); break;
                case TargetCategory.Facility: detail = Facility(target, ctx); break;
                default: detail = Prop(target); break;
            }

            var missing = Routes(target).Select(ctx.Rows.Missing).Where(m => !string.IsNullOrEmpty(m) && m != "조회 중").Distinct().ToList();
            detail.Source = (ctx.Snapshot.source?.StartsWith("http", StringComparison.Ordinal) == true ? "ERP 야드 계획" : "내보낸 스냅샷") +
                            " · 기준일 " + ctx.AsOf.ToString("yyyy-MM-dd") +
                            (missing.Count > 0 ? " · 추가 정보 " + string.Join(", ", missing) : string.Empty);
            return detail;
        }

        // ------------------------------------------------------------------ hull

        private static ObjectDetail Hull(DetailTarget t, DetailContext ctx)
        {
            var id = t.Hull ?? t.Id;
            var plan = ctx.Model?.Hull(id);
            var info = plan?.Info ?? ctx.Snapshot.vessel_details.FirstOrDefault(h => h.hull == id);
            var overview = ctx.Snapshot.hull_overview?.FirstOrDefault(r => r.hull == id);
            var shifts = (ctx.Snapshot.quay_shifts ?? new QuayShiftRow[0]).Where(r => r.hull == id)
                .OrderBy(r => Day(r.actual_day) ?? Day(r.plan_day) ?? DateTime.MaxValue).ToList();
            var berths = (ctx.Snapshot.berth_assignments ?? new BerthAssignmentRow[0]).Where(r => r.hull == id).ToList();

            var d = new ObjectDetail
            {
                Kind = "호선",
                Title = string.IsNullOrWhiteSpace(info?.vessel_name) ? id : info.vessel_name,
                Subtitle = Join(" · ", id, info?.vessel_type, info?.customer),
                Status = overview?.lifecycle_status ?? (plan != null && plan.InDock(ctx.AsOf) ? "도크 건조 중" : "계획"),
            };
            d.StatusLevel = Level(d.Status);
            var lastShift = shifts.LastOrDefault(s => Day(s.actual_day) <= ctx.AsOf);   // only moves that happened
            var nextShift = shifts.FirstOrDefault(s => string.IsNullOrEmpty(s.actual_day) && (Day(s.plan_day) ?? DateTime.MaxValue) >= ctx.AsOf);
            var lateShift = shifts.FirstOrDefault(s => string.IsNullOrEmpty(s.actual_day) && Day(s.plan_day) < ctx.AsOf);
            d.Fact("호선 번호", id)
                .Fact("주요 치수", info != null && info.length_m > 0 ? $"L {info.length_m:0} × B {info.beam_m:0} × D {info.depth_m:0.#} m" : null)
                .Fact("현재 위치", lastShift != null ? Point(ctx, lastShift.to_point) : plan?.Dock != null && plan.InDock(ctx.AsOf) ? HoverTarget.FacilityTitle(plan.Dock) : null)
                .Fact("다음 안벽 이동", nextShift != null ? $"{Short(nextShift.plan_day)} {Point(ctx, nextShift.from_point)} → {Point(ctx, nextShift.to_point)}" : "예정 없음",
                    nextShift != null ? StatusLevel.Info : (StatusLevel?)null)
                .Fact("이동 지연", lateShift != null ? $"{Short(lateShift.plan_day)} 예정 이동 실적 없음" : null, StatusLevel.Warning)
                .Fact("인도 예정", Short(info?.delivery))
                .Fact("블록 탑재", overview != null && overview.erection_planned_count > 0
                    ? $"{overview.erection_accepted_count} / {overview.erection_planned_count} 합격" : null);

            // Master schedule: dock, berths, quay shifts, milestones.
            var schedule = new TimelineChartSpec { Now = ctx.AsOf, Caption = "실선: 확정·실적 · 테두리: 계획 · ◆ 이동·마일스톤" };
            foreach (var (start, end) in plan?.DockStays ?? new List<(DateTime, DateTime)>())
            {
                schedule.Items.Add(new TimelineItem { Row = "도크", Label = "도크 " + (plan.Dock != null ? HoverTarget.FacilityTitle(plan.Dock) : string.Empty), Start = start, End = end.AddDays(1),
                    Color = ChartPalette.Violet, Hollow = start > ctx.AsOf });
            }

            foreach (var b in berths)
            {
                if (Day(b.start_day) is DateTime start)
                {
                    schedule.Items.Add(new TimelineItem { Row = "선석", Label = "선석 " + Point(ctx, b.berth), Start = start, End = (Day(b.end_day) ?? start).AddDays(1),
                        Color = ChartPalette.Teal, Hollow = start > ctx.AsOf, Detail = b.berth_purpose });
                }
            }

            foreach (var s in shifts)
            {
                var actual = Day(s.actual_day);
                if ((actual ?? Day(s.plan_day)) is DateTime day)
                {
                    var late = actual == null && day < ctx.AsOf;
                    schedule.Items.Add(new TimelineItem { Row = "안벽 이동", Milestone = true, Start = day, End = day.AddDays(1), Hollow = actual == null,
                        Color = actual != null ? Done : late ? ChartPalette.Alarm : Live,
                        Label = Join(" · ", s.shift_purpose, Point(ctx, s.from_point) + " → " + Point(ctx, s.to_point)),
                        Detail = "예정 " + Short(s.plan_day) + (actual != null ? " · 실적 " + Short(s.actual_day) : late ? " · 실적 없음(지연)" : string.Empty) });
                }
            }

            var milestones = ctx.Rows.Get<HullMilestoneRow>(Milestones)?.Where(m => m.hull == id).ToList();
            foreach (var m in milestones ?? new List<HullMilestoneRow>())
            {
                var actual = Day(m.actual);
                if ((actual ?? Day(m.forecast) ?? Day(m.baseline)) is DateTime day)
                {
                    schedule.Items.Add(new TimelineItem { Row = "대일정", Milestone = true, Start = day, End = day.AddDays(1), Hollow = actual == null,
                        Color = actual != null ? Done : Day(m.forecast) > Day(m.baseline) ? ChartPalette.Warning : Live, Label = m.milestone,
                        Detail = "기준 " + Short(m.baseline) + " · 예정 " + Short(m.forecast) + (actual != null ? " · 실적 " + Short(m.actual) : string.Empty) });
                }
            }

            var master = d.Section("안벽 이동 · 대일정", shifts.Count == 0 && berths.Count == 0 ? "ERP에 등록된 선석 배정·안벽 이동 계획이 없습니다." : null).Chart(schedule);
            foreach (var s in shifts)
            {
                var late = string.IsNullOrEmpty(s.actual_day) && Day(s.plan_day) < ctx.AsOf;
                master.Row(Short(s.plan_day) + " " + (s.shift_purpose ?? "이동"), Point(ctx, s.from_point) + " → " + Point(ctx, s.to_point) + " · " +
                    (string.IsNullOrEmpty(s.actual_day) ? late ? "지연" : "예정" : "완료 " + Short(s.actual_day)),
                    string.IsNullOrEmpty(s.actual_day) ? late ? StatusLevel.Warning : StatusLevel.Info : StatusLevel.Normal);
            }

            // Outfitting: progress by zone and the work packages around the plan day.
            var works = ctx.Rows.Get<OutfitWorkRow>(OutfitWorks)?.Where(w => w.hull == id).ToList();
            var zones = ctx.Rows.Get<OutfitProgressRow>(OutfitProgress)?.Where(z => z.hull == id).ToList();
            var outfit = d.Section("의장 진척");
            if (works == null)
            {
                outfit.Pending = ctx.Rows.Missing(OutfitWorks) == "조회 중";
                outfit.Note = outfit.Pending ? "ERP에서 의장 작업을 불러오는 중입니다." : "의장 작업을 볼 수 없습니다(" + ctx.Rows.Missing(OutfitWorks) + ").";
            }
            else if (works.Count == 0)
            {
                outfit.Note = "이 호선에 등록된 의장 작업이 없습니다.";
            }
            else
            {
                var planned = works.Sum(w => w.planned_mh);
                var earned = works.Sum(w => w.earned_mh > 0 ? w.earned_mh : w.planned_mh * w.progress_pct / 100f);
                outfit.Chart(DonutChartSpec.Progress("전체 의장 진척", earned, planned, "기성 공수", ChartPalette.Teal, "기성 / 계획 M/H"));
                var byZone = zones != null && zones.Count > 0
                    ? zones.Select(z => (Zone: z.outfit_zone, ToDate: z.planned_to_date_mh, Earned: z.earned_mh)).ToList()
                    : works.GroupBy(w => w.outfit_zone ?? "-").Select(g => (Zone: g.Key, ToDate: g.Sum(w => w.planned_mh), Earned: g.Sum(w => w.earned_mh))).ToList();
                var bars = new BarChartSpec { Title = "구역별 공수 (M/H)", Unit = "h", Caption = zones != null && zones.Count > 0 ? "현재까지 계획 대비 기성" : "계획 대비 기성" };
                bars.Categories.AddRange(byZone.Select(z => z.Zone));
                bars.Series.Add(new ChartSeries("계획", Plan, byZone.Select(z => z.ToDate)));
                bars.Series.Add(new ChartSeries("기성", ChartPalette.Teal, byZone.Select(z => z.Earned)));
                outfit.Chart(bars);
                int done = works.Count(w => w.work_status == "완료"), active = works.Count(w => w.work_status == "진행" || w.work_status == "진행 중");
                int flagged = works.Count(w => !string.IsNullOrEmpty(w.schedule_check) && w.schedule_check != "정상");
                outfit.Row("작업 패키지", $"완료 {done} · 진행 {active} · 대기 {works.Count - done - active}")
                    .Row("일정 점검", flagged > 0 ? flagged + "건 확인 필요" : "이상 없음", flagged > 0 ? StatusLevel.Warning : StatusLevel.Normal);
                var weakest = zones?.Where(z => z.spi > 0f && z.spi < 0.95f).OrderBy(z => z.spi).FirstOrDefault();
                if (weakest != null) outfit.Row("가장 늦은 구역", $"{weakest.outfit_zone} · SPI {weakest.spi:0.00}", StatusLevel.Warning);

                // Work packages nearest the plan day (running ones first), at most 10 rows.
                var shown = works.Select(w => (Work: w, Start: Day(w.start_day), End: Day(w.end_day)))
                    .Where(w => w.Start.HasValue)
                    .OrderBy(w => w.Start <= ctx.AsOf && (w.End ?? w.Start) >= ctx.AsOf ? 0 : 1)
                    .ThenBy(w => Math.Abs((w.Start.Value - ctx.AsOf).TotalDays)).Take(10).OrderBy(w => w.Start).ToList();
                var plan2 = new TimelineChartSpec { Title = "의장 작업 일정", Now = ctx.AsOf, RowHeight = 18f,
                    Caption = shown.Count < works.Count ? $"기준일 전후 {shown.Count}건 / 전체 {works.Count}건 · 막대 안 밝은 부분이 진척" : "막대 안 밝은 부분이 진척" };
                foreach (var (w, start, end) in shown)
                {
                    var late = !string.IsNullOrEmpty(w.schedule_check) && w.schedule_check != "정상";
                    plan2.Items.Add(new TimelineItem
                    {
                        Row = Join(" ", w.outfit_zone, w.outfit_trade), Label = Join(" · ", w.id, w.work_phase), Start = start.Value, End = (end ?? start.Value).AddDays(1),
                        Progress = w.progress_pct / 100f, Color = w.work_status == "완료" ? Done : late ? ChartPalette.Alarm : Live,
                        Detail = Join(" · ", w.work_status, late ? w.schedule_check : null, w.planned_mh > 0 ? w.planned_mh.ToString("0") + " M/H" : null),
                    });
                }

                d.Section("의장 작업 (진행·예정)").Chart(plan2);
            }

            // Erection: accepted blocks and the cumulative S-curve.
            var blocks = plan?.Blocks ?? new List<BlockPlan>();
            if (blocks.Count > 0)
            {
                var erect = d.Section("블록 탑재 진척");
                var accepted = overview?.erection_accepted_count ?? blocks.Count(b => !string.IsNullOrEmpty(b.Record.erected_day));
                var total = overview != null && overview.erection_planned_count > 0 ? overview.erection_planned_count : blocks.Count;
                erect.Chart(DonutChartSpec.Progress("탑재 합격", accepted, total, "합격 블록", Done, accepted + " / " + total + " 블록"));
                erect.Chart(SCurve(blocks, ctx.AsOf));
            }

            return d;
        }

        /// <summary>Cumulative planned (forecast, else baseline) and actual erections per week, actual only up to the plan day.</summary>
        public static LineChartSpec SCurve(IReadOnlyList<BlockPlan> blocks, DateTime asOf)
        {
            var planned = blocks.Select(b => Day(b.Record.forecast) ?? Day(b.Record.baseline)).Where(x => x.HasValue).Select(x => x.Value).ToList();
            var actual = blocks.Select(b => Day(b.Record.erected_day)).Where(x => x.HasValue).Select(x => x.Value).ToList();
            var spec = new LineChartSpec { Title = "누적 탑재 (계획 대비 실적)", Unit = "개", Caption = "주 단위 누적 블록 수" };
            if (planned.Count == 0 && actual.Count == 0)
            {
                return spec;
            }

            var first = Monday(planned.Concat(actual).Min());
            var last = Monday(planned.Concat(actual).Concat(new[] { asOf }).Max());
            var weeks = new List<DateTime>();
            for (var w = first; w <= last && weeks.Count < 40; w = w.AddDays(7)) weeks.Add(w);
            if (weeks.Count == 1) weeks.Add(first.AddDays(7));
            spec.Categories.AddRange(weeks.Select(w => w.ToString("MM-dd")));
            spec.Series.Add(new ChartSeries("계획", Plan, weeks.Select(w => (float)planned.Count(x => x < w.AddDays(7)))));
            spec.Series.Add(new ChartSeries("실적", Done, weeks.Select(w => w > asOf ? float.NaN : actual.Count(x => x < w.AddDays(7)))));
            spec.MarkerIndex = weeks.FindLastIndex(w => w <= asOf);
            return spec;
        }

        // ------------------------------------------------------------------ block

        private static ObjectDetail Block(DetailTarget t, DetailContext ctx)
        {
            var id = t.Id;
            BlockPlan plan = null;
            ctx.Model?.Blocks.TryGetValue(id ?? string.Empty, out plan);
            var record = plan?.Record ?? ctx.Snapshot.erection_timeline.FirstOrDefault(b => b.block == id);
            var stay = plan?.StayAt(ctx.AsOf);
            var shop = plan?.ShopAt(ctx.AsOf);
            var erected = Day(record?.erected_day);
            var due = Day(record?.Day);
            var d = new ObjectDetail
            {
                Kind = "블록",
                Title = id,
                Subtitle = Join(" · ", "호선 " + (record?.hull ?? t.Hull), record?.zone),
                Status = erected.HasValue ? "탑재 완료" : due.HasValue && due < ctx.AsOf ? "탑재 지연" : stay != null ? "적치 중" :
                    shop != null ? shop.Waiting ? "반출 대기" : "생산 중 · " + shop.Stage : "생산 중",
            };
            d.StatusLevel = erected.HasValue ? StatusLevel.Normal : due.HasValue && due < ctx.AsOf ? StatusLevel.Warning : StatusLevel.Info;
            d.Fact("호선", record?.hull ?? t.Hull)
                .Fact("중량", record != null ? record.weight_t.ToString("0.#") + " t" : null)
                .Fact("탑재 순서", record != null && record.step > 0 ? record.step + "단계" : null)
                .Fact("현재 위치", erected.HasValue ? "선체 탑재" : stay?.Location ?? (shop != null ? Facility(ctx, shop.Location) : null))
                .Fact("현재 작업장", shop != null && stay == null
                    ? Facility(ctx, shop.Location) + " · " + shop.Stage + (shop.Waiting ? " " + (ctx.AsOf - shop.From).Days + "일째" : " ~ " + shop.Until.ToString("MM-dd"))
                    : null, shop != null && !shop.Waiting && shop.Until < ctx.AsOf ? StatusLevel.Warning : StatusLevel.Info)
                .Fact("탑재 계획", Short(record?.baseline))
                .Fact("탑재 예정", Short(record?.forecast), Day(record?.forecast) > Day(record?.baseline) ? StatusLevel.Warning : (StatusLevel?)null)
                .Fact("탑재 실적", Short(record?.erected_day), StatusLevel.Normal)
                .Fact("선행 블록", record != null && record.predecessors != null && record.predecessors.Length > 0 ? string.Join(", ", record.predecessors.Select(HullTimeline.ShortBlock)) : null);

            var stages = ctx.Rows.Get<BlockStageRow>(BlockStages)?.Where(s => s.block == id).ToList();
            var fromSnapshot = false;
            if (stages == null && ctx.Snapshot.block_stages != null)
            {
                // Offline (or before the ERP answers): the stage plan the yard snapshot carries, without man-hours.
                stages = ctx.Snapshot.block_stages.Where(s => s.block == id).Select(s => new BlockStageRow
                {
                    block = s.block, assembly_stage = s.assembly_stage, center = s.center, baseline = s.baseline, forecast = s.forecast,
                    actual = s.actual, progress_pct = s.progress_pct,
                }).ToList();
                fromSnapshot = true;
            }

            var progress = ctx.Rows.Get<BlockProgressRow>(BlockProgress)?.FirstOrDefault(p => p.block == id);
            var assembly = d.Section("조립 단계 진척");
            if (stages == null)
            {
                assembly.Pending = ctx.Rows.Missing(BlockStages) == "조회 중";
                assembly.Note = assembly.Pending ? "ERP에서 조립 단계를 불러오는 중입니다." : "조립 단계를 볼 수 없습니다(" + ctx.Rows.Missing(BlockStages) + ").";
            }
            else if (stages.Count == 0)
            {
                assembly.Note = "이 블록에 등록된 조립 단계가 없습니다.";
            }
            else
            {
                if (fromSnapshot) assembly.Note = "야드 스냅샷 기준 · 공수는 ERP 연결 시 표시됩니다.";
                var pct = progress?.progress_pct ?? stages.Average(s => s.progress_pct);
                assembly.Chart(DonutChartSpec.Progress("블록 진척", pct, 100f, "진척", Live,
                    progress != null ? $"{progress.completed_stages}/{progress.stages} 단계 완료" : null));
                var bars = new BarChartSpec { Title = "단계별 진척률", Unit = "%", Max = 100f };
                bars.Categories.AddRange(stages.Select(s => s.assembly_stage));
                bars.Series.Add(new ChartSeries("진척률", Live, stages.Select(s => s.progress_pct)));
                assembly.Chart(bars);
                if (!string.IsNullOrEmpty(progress?.current_stage)) assembly.Row("현재 단계", progress.current_stage, StatusLevel.Info);
                if (progress != null && progress.productivity > 0f)
                {
                    assembly.Row("생산성 (기성/투입)", progress.productivity.ToString("0.00"), progress.productivity < 0.9f ? StatusLevel.Warning : StatusLevel.Normal);
                }

                if (stages.Any(s => s.planned_mh > 0f || s.actual_mh > 0f))
                {
                    var mh = new BarChartSpec { Title = "공수 계획 대비 실적 (M/H)", Unit = "h", ShowValues = false };
                    mh.Categories.AddRange(stages.Select(s => s.assembly_stage));
                    mh.Series.Add(new ChartSeries("계획", Plan, stages.Select(s => s.planned_mh)));
                    mh.Series.Add(new ChartSeries("실적", ChartPalette.Warning, stages.Select(s => s.actual_mh)));
                    d.Section("공수").Chart(mh);
                }
            }

            var timeline = new TimelineChartSpec { Now = ctx.AsOf, Caption = "◆ 단계 완료(실선=실적, 테두리=예정) · 막대 적치 기간" };
            foreach (var s in stages ?? new List<BlockStageRow>())
            {
                var actual = Day(s.actual);
                if ((actual ?? Day(s.forecast) ?? Day(s.baseline)) is DateTime day)
                {
                    timeline.Items.Add(new TimelineItem { Row = s.assembly_stage, Milestone = true, Start = day, End = day.AddDays(1), Hollow = actual == null,
                        Color = actual != null ? Done : Day(s.forecast) > Day(s.baseline) ? ChartPalette.Warning : Live, Label = s.assembly_stage,
                        Detail = "기준 " + Short(s.baseline) + " · 예정 " + Short(s.forecast) + (actual != null ? " · 실적 " + Short(s.actual) : string.Empty) });
                }
            }

            foreach (var s in plan?.Stays ?? new List<StockStay>())
            {
                var end = (s.Until ?? due ?? s.From).AddDays(1);
                timeline.Items.Add(new TimelineItem { Row = "적치", Label = "적치 " + s.Location, Start = s.From, End = end > s.From ? end : s.From.AddDays(1),
                    Color = ChartPalette.Teal, Hollow = s.From > ctx.AsOf });
            }

            if (due.HasValue)
            {
                timeline.Items.Add(new TimelineItem { Row = "탑재", Milestone = true, Start = due.Value, End = due.Value.AddDays(1), Hollow = !erected.HasValue,
                    Color = erected.HasValue ? Done : due < ctx.AsOf ? ChartPalette.Alarm : ChartPalette.Violet, Label = "탑재 " + id,
                    Detail = "계획 " + Short(record?.baseline) + " · 예정 " + Short(record?.forecast) + (erected.HasValue ? " · 실적 " + Short(record?.erected_day) : string.Empty) });
            }

            if (timeline.Items.Count > 0) d.Section("일정").Chart(timeline);
            return d;
        }

        // ------------------------------------------------------------------ crane

        private static ObjectDetail Crane(DetailTarget t, DetailContext ctx)
        {
            var rail = ctx.Snapshot.crane_rails?.FirstOrDefault(r => r.equipment == t.Id);
            var boom = ctx.Snapshot.crane_kinematics?.FirstOrDefault(k => k.equipment == t.Id);
            var jobs = (ctx.Snapshot.crane_assignments ?? new CraneAssignmentRow[0])
                .Where(a => t.Id != null ? a.equipment == t.Id : !string.IsNullOrEmpty(t.Place) && a.work_area == t.Place)
                .Select(a => (Row: a, Start: Day(a.work_day)?.AddHours(a.start_hour)))
                .Where(a => a.Start.HasValue).OrderBy(a => a.Start).ToList();
            var today = jobs.Where(j => j.Start.Value.Date == ctx.AsOf).ToList();
            var capacity = rail?.capacity_t ?? 0f;
            var d = new ObjectDetail
            {
                Kind = "크레인",
                Title = t.Title,
                Subtitle = Join(" · ", t.Id, rail?.equipment_type, Facility(ctx, rail?.station ?? t.Place)),
                Status = today.Count > 0 ? "오늘 작업 " + today.Count + "건" : "대기",
                StatusLevel = today.Count > 0 ? StatusLevel.Info : StatusLevel.Offline,
            };
            var next = jobs.FirstOrDefault(j => j.Start.Value.Date >= ctx.AsOf);
            d.Fact("장비 ID", t.Id ?? "ERP 미등록")
                .Fact("정격하중", capacity > 0 ? capacity.ToString("0") + " t" : null)
                .Fact("레일", rail != null ? $"길이 {Vector2.Distance(new Vector2(rail.rail_x0, rail.rail_y0), new Vector2(rail.rail_x1, rail.rail_y1)):0} m · 폭 {rail.gauge_m:0} m" : null)
                .Fact("붐", boom != null ? $"{boom.boom_m:0} m · 지지점 {boom.pivot_height_m:0} m" : null)
                .Fact("다음 작업", next.Row != null ? $"{next.Start:MM-dd HH:mm} {next.Row.crane_work} {HullTimeline.ShortBlock(next.Row.block)}" : "예정 없음");

            var load = ctx.Rows.Get<CraneLoadRow>(CraneLoad)?.Where(r => r.equipment == t.Id).OrderBy(r => r.week).ToList();
            var weekly = new BarChartSpec { Title = "주간 가동률", Unit = "%", Max = 100f, Threshold = 85f, ThresholdLabel = "과부하 기준 85%" };
            if (load != null && load.Count > 0)
            {
                var shown = load.Skip(Math.Max(0, load.Count - 10)).ToList();
                weekly.Categories.AddRange(shown.Select(r => Short(r.week)));
                weekly.Series.Add(new ChartSeries("가동률", Live, shown.Select(r => r.utilization_pct)));
                weekly.Caption = "ERP 주간 크레인 부하 (2교대 16시간 × 5일 기준)";
            }
            else if (jobs.Count > 0)
            {
                // Without the ERP load view: booked hours per week of the assignments against 80 h.
                var weeks = jobs.GroupBy(j => Monday(j.Start.Value)).OrderBy(g => g.Key).ToList();
                var shown = weeks.Skip(Math.Max(0, weeks.Count - 10)).ToList();
                weekly.Categories.AddRange(shown.Select(g => g.Key.ToString("MM-dd")));
                weekly.Series.Add(new ChartSeries("가동률", Live, shown.Select(g => Mathf.Min(100f, g.Sum(j => j.Row.hours) / 80f * 100f))));
                weekly.Caption = "작업 배정 시간 ÷ 주 80시간" + (ErpDetailCache.Configured ? string.Empty : " (ERP 미연결)");
            }

            var section = d.Section("가동 현황").Chart(weekly);
            section.Pending = load == null && ctx.Rows.Missing(CraneLoad) == "조회 중" && jobs.Count == 0;

            var window = jobs.Where(j => j.Start.Value >= ctx.AsOf.AddDays(-14) && j.Start.Value <= ctx.AsOf.AddDays(28)).ToList();
            if (window.Count == 0 && jobs.Count > 0) window = jobs.Skip(Math.Max(0, jobs.Count - 8)).ToList();
            var schedule = new TimelineChartSpec { Title = "작업 배정 (기준일 −2주 ~ +4주)", Now = ctx.AsOf, RowHeight = 18f };
            foreach (var (row, start) in window)
            {
                schedule.Items.Add(new TimelineItem { Row = row.hull ?? "-", Start = start.Value, End = start.Value.AddHours(Math.Max(1, row.hours)),
                    Label = Join(" ", row.crane_work, HullTimeline.ShortBlock(row.block)), Hollow = start.Value.Date > ctx.AsOf,
                    Color = row.crane_work == "블록 탑재" ? ChartPalette.Violet : row.crane_work == "크레인 정비" ? Plan : ChartPalette.Teal,
                    Detail = $"{start:MM-dd HH:mm} · {row.hours}시간 · {row.lift_t:0.#} t · {Facility(ctx, row.work_area)}" });
            }

            if (schedule.Items.Count > 0)
            {
                var jobsSection = d.Section("작업 계획").Chart(schedule);
                var lifts = window.Where(j => j.Row.lift_t > 0f).Take(10).ToList();
                if (lifts.Count > 0)
                {
                    var weights = new BarChartSpec { Title = "인양 중량", Unit = "t", Threshold = capacity > 0 ? capacity : float.NaN,
                        ThresholdLabel = capacity > 0 ? "정격하중 " + capacity.ToString("0") + " t" : null, ShowValues = lifts.Count <= 6 };
                    weights.Categories.AddRange(lifts.Select(j => j.Start.Value.ToString("MM-dd")));
                    weights.Series.Add(new ChartSeries("인양", ChartPalette.Warning, lifts.Select(j => j.Row.lift_t)));
                    jobsSection.Chart(weights);
                }

                foreach (var (row, start) in jobs.Where(j => j.Start.Value >= ctx.AsOf).Take(4))
                {
                    jobsSection.Row($"{start:MM-dd HH:mm}", Join(" · ", row.crane_work, row.hull, HullTimeline.ShortBlock(row.block), row.lift_t > 0 ? row.lift_t.ToString("0.#") + " t" : null));
                }
            }
            else
            {
                d.Section("작업 계획", "ERP 크레인 작업 배정이 없습니다.");
            }

            return d;
        }

        // ------------------------------------------------------------------ vehicle

        private static ObjectDetail Vehicle(DetailTarget t, DetailContext ctx)
        {
            var moves = (ctx.Snapshot.moves ?? new BlockMoveRow[0]).Where(m => m.equipment == t.Id)
                .Select(m => (Row: m, Start: Instant(m.started_at), End: Instant(m.confirmed_at)))
                .Where(m => m.Start.HasValue).OrderBy(m => m.Start).ToList();
            var open = moves.Where(m => !m.End.HasValue).ToList();
            var d = new ObjectDetail
            {
                Kind = "운반 장비", Title = t.Title, Subtitle = t.Detail,
                Status = open.Count > 0 ? "이동 중" : "대기", StatusLevel = open.Count > 0 ? StatusLevel.Warning : StatusLevel.Offline,
            };
            d.Fact("장비 ID", t.Id).Fact("이동 실적", moves.Count + "건").Fact("미확정 이동", open.Count > 0 ? open.Count + "건" : null, StatusLevel.Warning)
                .Fact("마지막 이동", moves.Count > 0 ? $"{moves[moves.Count - 1].Start:MM-dd HH:mm} {HullTimeline.ShortBlock(moves[moves.Count - 1].Row.block)}" : null);
            if (moves.Count == 0)
            {
                d.Section("블록 이동", "ERP에 기록된 이 장비의 블록 이동이 없습니다.");
                return d;
            }

            var recent = moves.Skip(Math.Max(0, moves.Count - 10)).ToList();
            var chart = new TimelineChartSpec { Title = "최근 블록 이동", Now = ctx.AsOf, RowHeight = 18f, Caption = "테두리: 도착 미확정" };
            foreach (var (row, start, end) in recent)
            {
                chart.Items.Add(new TimelineItem { Row = HullTimeline.ShortBlock(row.block), Start = start.Value,
                    End = end ?? (start.Value.AddHours(6) > ctx.AsOf ? start.Value.AddHours(6) : ctx.AsOf), Hollow = !end.HasValue,
                    Color = end.HasValue ? Done : ChartPalette.Warning, Label = row.origin + " → " + row.destination });
            }

            var perWeek = moves.GroupBy(m => Monday(m.Start.Value)).OrderBy(g => g.Key).ToList();
            var bars = new BarChartSpec { Title = "주간 이동 건수", Unit = "건" };
            bars.Categories.AddRange(perWeek.Skip(Math.Max(0, perWeek.Count - 8)).Select(g => g.Key.ToString("MM-dd")));
            bars.Series.Add(new ChartSeries("이동", Live, perWeek.Skip(Math.Max(0, perWeek.Count - 8)).Select(g => (float)g.Count())));
            d.Section("블록 이동").Chart(chart).Chart(bars);
            return d;
        }

        // ------------------------------------------------------------------ facility

        private static ObjectDetail Facility(DetailTarget t, DetailContext ctx)
        {
            var f = ctx.Model?.Facilities.FirstOrDefault(x => x.location == t.Id) ?? ctx.Snapshot.location_geo.FirstOrDefault(x => x.location == t.Id);
            var kind = f?.kind ?? "시설";
            var d = new ObjectDetail { Kind = kind, Title = t.Title, Subtitle = Join(" · ", t.Id, kind), Status = "운영", StatusLevel = StatusLevel.Normal };
            d.Fact("시설 ID", t.Id).Fact("크기", f != null && f.HasFootprint ? $"{f.size_x_m:0} × {f.size_y_m:0} m" : null);

            var crane = (ctx.Snapshot.crane_assignments ?? new CraneAssignmentRow[0]).Where(a => a.work_area == t.Id).ToList();
            if (kind == "도크")
            {
                var bookings = (ctx.Snapshot.dock_bookings ?? new DockBookingRow[0]).Where(b => b.dock == t.Id)
                    .Select(b => (Row: b, Start: Day(b.start_day), End: Day(b.end_day))).Where(b => b.Start.HasValue).OrderBy(b => b.Start).ToList();
                var current = bookings.FirstOrDefault(b => b.Start <= ctx.AsOf && (b.End ?? b.Start) >= ctx.AsOf);
                d.Status = current.Row != null ? "건조 중 · " + current.Row.hull : "비어 있음";
                d.StatusLevel = current.Row != null ? StatusLevel.Info : StatusLevel.Offline;
                var next = bookings.FirstOrDefault(b => b.Start > ctx.AsOf);
                d.Fact("현재 호선", current.Row?.hull ?? "없음")
                    .Fact("점유 기간", current.Row != null ? Short(current.Row.start_day) + " ~ " + Short(current.Row.end_day) + " (진수)" : null)
                    .Fact("다음 입거", next.Row != null ? $"{next.Row.hull} · {Short(next.Row.start_day)}" : "예정 없음");
                AddOccupancy(d, "도크 점유", bookings.Select(b => (b.Row.hull, b.Start.Value, b.End ?? b.Start.Value)), ctx.AsOf, "점유 예약이 없습니다.");
            }
            else if (kind == "안벽")
            {
                // Berth assignments and quay shifts name a 선석 (berths.id), not the quay facility it lies along.
                var points = BerthsAlong(ctx, f, t.Id);
                var berths = (ctx.Snapshot.berth_assignments ?? new BerthAssignmentRow[0]).Where(b => points.Contains(b.berth))
                    .Select(b => (Row: b, Start: Day(b.start_day), End: Day(b.end_day))).Where(b => b.Start.HasValue).OrderBy(b => b.Start).ToList();
                var current = berths.Where(b => b.Start <= ctx.AsOf && (b.End ?? b.Start) >= ctx.AsOf).Select(b => b.Row.hull).ToList();
                d.Status = current.Count > 0 ? "접안 " + string.Join(", ", current) : "비어 있음";
                d.StatusLevel = current.Count > 0 ? StatusLevel.Info : StatusLevel.Offline;
                AddOccupancy(d, "선석 배정", berths.Select(b => (b.Row.hull, b.Start.Value, b.End ?? b.Start.Value)), ctx.AsOf, "이 안벽에 등록된 선석 배정이 없습니다.");
                var shifts = (ctx.Snapshot.quay_shifts ?? new QuayShiftRow[0]).Where(s => points.Contains(s.to_point) || points.Contains(s.from_point)).ToList();
                if (shifts.Count > 0)
                {
                    var section = d.Sections.Last();
                    foreach (var s in shifts.OrderBy(s => s.plan_day).Where(s => Day(s.plan_day) >= ctx.AsOf.AddDays(-30)).Take(5))
                    {
                        section.Row(Short(s.plan_day) + " " + s.hull, (points.Contains(s.to_point) ? "입항 " : "출항 ") + (s.shift_purpose ?? string.Empty) +
                                                                       (string.IsNullOrEmpty(s.actual_day) ? " · 예정" : " · 완료"),
                            string.IsNullOrEmpty(s.actual_day) ? StatusLevel.Info : StatusLevel.Normal);
                    }
                }
            }
            else
            {
                if (kind == "작업장" && ctx.Model != null) AddShopFloor(d, t.Id, ctx);

                // Work shops, stores and yards: blocks stocked here.
                var stays = ctx.Snapshot.block_stock_plans.Where(s => s.location == t.Id)
                    .Select(s => (Row: s, From: Day(s.stock_from), Until: Day(s.stock_until))).Where(s => s.From.HasValue).ToList();
                var now = stays.Count(s => s.From <= ctx.AsOf && (s.Until ?? DateTime.MaxValue) >= ctx.AsOf);
                if (kind != "작업장" || ctx.Model == null)
                {
                    d.Status = now > 0 ? "블록 " + now + "개 적치" : "적치 없음";
                    d.StatusLevel = now > 0 ? StatusLevel.Info : StatusLevel.Offline;
                }

                if (now > 0 || kind != "작업장") d.Fact("현재 적치 블록", now + "개");
                if (stays.Count > 0)
                {
                    var weeks = Enumerable.Range(-5, 9).Select(i => Monday(ctx.AsOf).AddDays(7 * i)).ToList();
                    var bars = new BarChartSpec { Title = "주별 적치 블록 수", Unit = "개" };
                    bars.Categories.AddRange(weeks.Select(w => w.ToString("MM-dd")));
                    bars.Series.Add(new ChartSeries("적치", ChartPalette.Teal,
                        weeks.Select(w => (float)stays.Count(s => s.From < w.AddDays(7) && (s.Until ?? DateTime.MaxValue) >= w))));
                    var near = stays.OrderBy(s => s.From <= ctx.AsOf && (s.Until ?? DateTime.MaxValue) >= ctx.AsOf ? 0 : 1)
                        .ThenBy(s => Math.Abs((s.From.Value - ctx.AsOf).TotalDays)).Take(10).OrderBy(s => s.From).ToList();
                    var timeline = new TimelineChartSpec { Title = "적치 일정", Now = ctx.AsOf, RowHeight = 17f,
                        Caption = near.Count < stays.Count ? $"기준일 전후 {near.Count}건 / 전체 {stays.Count}건" : null };
                    foreach (var (row, from, until) in near)
                    {
                        timeline.Items.Add(new TimelineItem { Row = HullTimeline.ShortBlock(row.block), Start = from.Value, End = (until ?? from.Value.AddDays(14)).AddDays(1),
                            Hollow = !until.HasValue || from > ctx.AsOf, Color = ChartPalette.Teal, Label = row.block, Detail = until.HasValue ? null : "종료일 미정(탑재까지)" });
                    }

                    d.Section("블록 적치").Chart(bars).Chart(timeline);
                }
                else if (kind != "작업장")
                {
                    d.Section("블록 적치", "이 시설에 등록된 블록 적치 계획이 없습니다.");
                }
            }

            if (crane.Count > 0)
            {
                var week = crane.Where(a => Day(a.work_day) is DateTime day && day >= Monday(ctx.AsOf) && day < Monday(ctx.AsOf).AddDays(7)).ToList();
                d.Fact("이번 주 크레인 작업", week.Count + "건 · " + week.Sum(a => a.hours) + "시간");
            }

            return d;
        }

        /// <summary>Order of the shop stages on the board; others follow in order of appearance.</summary>
        public static readonly string[] StageOrder = { "절단·가공", "소조립", "중조립", "대조립", "반출 대기" };

        /// <summary>Shop: blocks by stage on the day (bays) and the number of blocks in the shop a week back to four weeks ahead.</summary>
        private static void AddShopFloor(ObjectDetail d, string location, DetailContext ctx)
        {
            var blocks = ctx.Model.ShopBlocks(location, ctx.AsOf);
            var board = ShopBoard(blocks, ctx.AsOf);
            var late = board.Bays.Sum(b => b.Chips.Count(c => c.Tone == StatusLevel.Alarm));
            var waiting = blocks.Count(b => b.Stage.Waiting);
            d.Status = blocks.Count > 0 ? "블록 " + blocks.Count + "개 작업 중" : "작업 블록 없음";
            d.StatusLevel = late > 0 ? StatusLevel.Warning : blocks.Count > 0 ? StatusLevel.Info : StatusLevel.Offline;
            d.Fact("작업 중 블록", blocks.Count + "개" + (blocks.Select(b => b.Block.Hull).Distinct().Count() > 1
                    ? " · " + blocks.Select(b => b.Block.Hull).Distinct().Count() + "개 호선" : string.Empty))
                .Fact("단계 지연", late > 0 ? late + "개 블록" : null, StatusLevel.Alarm)
                .Fact("반출 대기", waiting > 0 ? waiting + "개 블록" : null, StatusLevel.Warning);

            var section = d.Section("현재 블록 배치",
                blocks.Count == 0 ? (ctx.Snapshot.block_stages == null ? "조립 단계 계획이 없어 작업장 배치를 알 수 없습니다." : "이 날 작업 중인 블록이 없습니다.") : null);
            if (blocks.Count == 0)
            {
                return;
            }

            section.Bays = board;
            var days = Enumerable.Range(-7, 36).Select(i => ctx.AsOf.AddDays(i)).ToList();
            var load = new LineChartSpec { Title = "작업장 블록 수 (−1주 ~ +4주)", Unit = "개", MarkerIndex = 7, Caption = "조립 단계 계획 기준 · 날마다 작업장에 있는 블록 수" };
            load.Categories.AddRange(days.Select(x => x.ToString("MM-dd")));
            load.Series.Add(new ChartSeries("블록", ChartPalette.Teal, days.Select(x => (float)ctx.Model.ShopBlocks(location, x).Count)));
            section.Chart(load);
        }

        /// <summary>Blocks in a shop on a day as lanes by stage (canonical order), late chips first, then by stage end.</summary>
        public static BayBoard ShopBoard(IEnumerable<(BlockPlan Block, ShopStage Stage)> blocks, DateTime day)
        {
            var board = new BayBoard();
            var list = blocks.ToList();
            var stages = StageOrder.Where(s => list.Any(b => b.Stage.Stage == s))
                .Concat(list.Select(b => b.Stage.Stage).Where(s => Array.IndexOf(StageOrder, s) < 0).Distinct());
            foreach (var stage in stages)
            {
                var bay = new Bay { Title = stage };
                foreach (var (block, shop) in list.Where(b => b.Stage.Stage == stage))
                {
                    var left = (shop.Until.Date - day.Date).Days;
                    var chip = new BayChip { Block = HullTimeline.ShortBlock(block.Id), Hull = block.Hull };
                    if (shop.Waiting)
                    {
                        var waited = (day.Date - shop.From.Date).Days;
                        chip.Note = "대기 " + waited + "일";
                        chip.Tone = waited > 7 ? StatusLevel.Alarm : StatusLevel.Warning;
                    }
                    else
                    {
                        chip.Note = left > 0 ? "D-" + left : left == 0 ? "D-day" : -left + "일 지연";
                        chip.Tone = left < 0 ? StatusLevel.Alarm : left <= 1 ? StatusLevel.Info : StatusLevel.Normal;
                    }

                    bay.Chips.Add(chip);
                }

                bay.Chips.Sort((a, b) => Rank(a).CompareTo(Rank(b)));
                board.Bays.Add(bay);
            }

            return board;
        }

        private static int Rank(BayChip chip) => chip.Tone == StatusLevel.Alarm ? 0 : chip.Tone == StatusLevel.Warning ? 1 : chip.Tone == StatusLevel.Info ? 2 : 3;

        /// <summary>Occupancy section: a ring of booked days in the next 90 days and the bookings as a timeline.</summary>
        private static void AddOccupancy(ObjectDetail d, string title, IEnumerable<(string Hull, DateTime Start, DateTime End)> stays, DateTime asOf, string empty)
        {
            var list = stays.ToList();
            if (list.Count == 0)
            {
                d.Section(title, empty);
                return;
            }

            var booked = Enumerable.Range(0, 90).Count(i => list.Any(s => s.Start <= asOf.AddDays(i) && asOf.AddDays(i) <= s.End));
            var timeline = new TimelineChartSpec { Title = title + " 일정", Now = asOf };
            foreach (var (hull, start, end) in list)
            {
                timeline.Items.Add(new TimelineItem { Row = hull ?? "-", Label = hull, Start = start, End = end.AddDays(1), Hollow = start > asOf,
                    Color = end < asOf ? Plan : ChartPalette.Teal });
            }

            d.Section(title)
                .Chart(DonutChartSpec.Progress("향후 90일 점유", booked, 90f, "점유일", ChartPalette.Teal, booked + " / 90일"))
                .Chart(timeline);
        }

        // ------------------------------------------------------------------ props

        private static ObjectDetail Prop(DetailTarget t)
        {
            var d = new ObjectDetail { Kind = t.Kind ?? "배경", Title = t.Title, Subtitle = t.Detail, Status = "배경 요소", StatusLevel = StatusLevel.Offline };
            d.Fact("구분", t.Kind).Fact("데이터", "ERP에 등록되지 않은 배경 " + (t.Kind == "시설" ? "건물" : "장비") + "입니다");
            d.Section("운영 정보", "야드 분위기를 위한 배경 모델이라 계획·실적이 없습니다. 실제 장비는 ERP 장비 정보로 등록하면 크레인·운반 장비처럼 일정과 그래프가 표시됩니다.");
            return d;
        }

        // ------------------------------------------------------------------ helpers

        public static DateTime? Day(string value) =>
            !string.IsNullOrEmpty(value) && value.Length >= 10 &&
            DateTime.TryParseExact(value.Substring(0, 10), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var day)
                ? day
                : (DateTime?)null;

        private static DateTime? Instant(string value) =>
            !string.IsNullOrEmpty(value) && DateTime.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var t)
                ? t.AddHours(9)   // ERP stores UTC; the yard works in KST
                : (DateTime?)null;

        private static DateTime Monday(DateTime day) => day.Date.AddDays(-(((int)day.DayOfWeek + 6) % 7));

        private static string Short(string day) => Day(day)?.ToString("yyyy-MM-dd") ?? (string.IsNullOrEmpty(day) ? null : day);

        /// <summary>
        /// The quay itself and the 선석 shift points along it: the ERP <c>quay</c> of each berth, or for a snapshot without
        /// it the berth whose registered position falls on this quay's footprint (widened 30 m toward the water).
        /// </summary>
        private static HashSet<string> BerthsAlong(DetailContext ctx, ShipyardTwin.Facility quay, string quayId)
        {
            var set = new HashSet<string>(StringComparer.Ordinal) { quayId };
            foreach (var p in ctx.Snapshot.shift_points ?? new ShiftPointRow[0])
            {
                if (p?.id == null || p.kind != "선석") continue;
                if (!string.IsNullOrEmpty(p.quay))
                {
                    if (p.quay == quayId) set.Add(p.id);
                }
                else if (quay != null && quay.HasFootprint && p.position_known && p.position != null)
                {
                    var rad = quay.heading_deg * Mathf.Deg2Rad;
                    float dx = p.position.x - quay.x, dy = p.position.y - quay.y;
                    float along = dx * Mathf.Cos(rad) + dy * Mathf.Sin(rad), across = -dx * Mathf.Sin(rad) + dy * Mathf.Cos(rad);
                    if (Mathf.Abs(along) <= quay.size_x_m / 2f && Mathf.Abs(across) <= quay.size_y_m / 2f + 30f) set.Add(p.id);
                }
            }
            return set;
        }

        private static string Point(DetailContext ctx, string id) =>
            ctx.Snapshot.shift_points?.FirstOrDefault(p => p.id == id)?.name ?? Facility(ctx, id) ?? id;

        private static string Facility(DetailContext ctx, string id)
        {
            if (string.IsNullOrEmpty(id)) return null;
            var f = ctx.Model?.Facilities.FirstOrDefault(x => x.location == id) ?? ctx.Snapshot.location_geo.FirstOrDefault(x => x.location == id);
            return f != null ? HoverTarget.FacilityTitle(f) : id;
        }

        private static string Join(string separator, params string[] parts) =>
            string.Join(separator, parts.Where(p => !string.IsNullOrWhiteSpace(p)));

        /// <summary>Status words to a badge colour.</summary>
        public static StatusLevel Level(string status)
        {
            if (string.IsNullOrEmpty(status)) return StatusLevel.Info;
            if (status.Contains("지연") || status.Contains("경과") || status.Contains("위험")) return StatusLevel.Warning;
            if (status.Contains("완료")) return StatusLevel.Normal;
            return StatusLevel.Info;
        }
    }
}
