using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace ShipyardTwin
{
    /// <summary>
    /// The whole plan of one hull as rows of dated bars for the schedule bar: hull milestones (dock stay, berths, quay
    /// shifts, delivery), crane work, then one row per block (its stocking stays and its erection lift, in lift order).
    /// Times are calendar instants; a day-only row covers that whole day. Pure data, built from the ERP snapshot and the
    /// erection schedule the 3D view plays.
    /// </summary>
    public sealed class HullTimeline
    {
        public enum Kind
        {
            Dock,
            Berth,
            Shift,
            Delivery,
            Crane,
            Stock,
            Lift,
        }

        public sealed class Item
        {
            public Item(Kind kind, DateTime start, DateTime end, string title, string detail)
            {
                Kind = kind;
                Start = start;
                End = end > start ? end : start.AddDays(1);
                Title = title;
                Detail = detail;
            }

            public Kind Kind { get; }
            public DateTime Start { get; }

            /// <summary>Exclusive end (at least one day after a day-only start).</summary>
            public DateTime End { get; }

            public string Title { get; }
            public string Detail { get; }

            /// <summary>A one-day item without duration (shift, delivery): drawn as a mark.</summary>
            public bool Milestone => Kind == Kind.Shift || Kind == Kind.Delivery;

            public bool Covers(DateTime instant) => Start <= instant && instant < End;
        }

        public sealed class Row
        {
            public Row(string group, string label, string block)
            {
                Group = group;
                Label = label;
                Block = block;
            }

            public string Group { get; }
            public string Label { get; }

            /// <summary>Block ID for block rows, else null.</summary>
            public string Block { get; }

            public List<Item> Items { get; } = new List<Item>();
        }

        public const string HullGroup = "호선";
        public const string CraneGroup = "크레인";
        public const string BlockGroup = "블록";

        public List<Row> Rows { get; } = new List<Row>();

        /// <summary>Earliest start of any item (the schedule start when there is none).</summary>
        public DateTime From { get; private set; }

        /// <summary>Latest end of any item.</summary>
        public DateTime To { get; private set; }

        public IEnumerable<Item> Items => Rows.SelectMany(r => r.Items);

        /// <summary>Row of a block, or null.</summary>
        public Row BlockRow(string block) => Rows.FirstOrDefault(r => r.Block == block);

        /// <summary>
        /// What is going on at an instant, most specific first: lifts, crane work, hull events, then stays (block rows
        /// in a yard slot are summarised by the caller).
        /// </summary>
        public List<(Row Row, Item Item)> At(DateTime instant)
        {
            var order = new[] { Kind.Lift, Kind.Crane, Kind.Shift, Kind.Delivery, Kind.Dock, Kind.Berth, Kind.Stock };
            return Rows.SelectMany(r => r.Items.Where(i => i.Covers(instant)).Select(i => (Row: r, Item: i)))
                .OrderBy(x => Array.IndexOf(order, x.Item.Kind)).ToList();
        }

        public static HullTimeline Build(HullPlan hull, YardSnapshot snapshot, ErectionSchedule schedule)
        {
            var timeline = new HullTimeline();
            if (hull == null)
            {
                return timeline;
            }

            Row Add(string group, string label, string block = null)
            {
                var row = new Row(group, label, block);
                timeline.Rows.Add(row);
                return row;
            }

            var dock = Add(HullGroup, "도크");
            foreach (var (start, end) in hull.DockStays)
            {
                dock.Items.Add(new Item(Kind.Dock, start, end.AddDays(1),
                    "도크 " + (hull.Dock?.name ?? hull.Placement?.dock) + " 점유 계획",
                    Day(start) + " ~ " + Day(end) + " · 도크 예약 기간"));
            }

            var berths = Add(HullGroup, "선석");
            foreach (var row in (snapshot?.berth_assignments ?? new BerthAssignmentRow[0]).Where(r => r.hull == hull.Id))
            {
                if (Parse(row.start_day) is DateTime start)
                {
                    var end = Parse(row.end_day) ?? start;
                    berths.Items.Add(new Item(Kind.Berth, start, end.AddDays(1), "선석 " + PointName(snapshot, row.berth),
                        Day(start) + " ~ " + Day(end) + (string.IsNullOrEmpty(row.berth_purpose) ? string.Empty : " · " + row.berth_purpose)));
                }
            }

            var shifts = Add(HullGroup, "호선 이동");
            foreach (var row in (snapshot?.quay_shifts ?? new QuayShiftRow[0]).Where(r => r.hull == hull.Id))
            {
                var actual = Parse(row.actual_day);
                if ((actual ?? Parse(row.plan_day)) is DateTime day)
                {
                    shifts.Items.Add(new Item(Kind.Shift, day, day.AddDays(1), "호선 이동" + (string.IsNullOrEmpty(row.shift_purpose) ? string.Empty : " · " + row.shift_purpose),
                        PointName(snapshot, row.from_point) + " → " + PointName(snapshot, row.to_point) + " · 예정 " + row.plan_day +
                        (actual.HasValue ? " · 실적 " + row.actual_day : string.Empty)));
                }
            }

            var delivery = Add(HullGroup, "인도");
            if (Parse(hull.Info?.delivery) is DateTime delivered)
            {
                delivery.Items.Add(new Item(Kind.Delivery, delivered, delivered.AddDays(1), "인도", Day(delivered) + " 인도 예정"));
            }

            var crane = Add(CraneGroup, "크레인 작업");
            foreach (var row in (snapshot?.crane_assignments ?? new CraneAssignmentRow[0]).Where(r => r.hull == hull.Id))
            {
                if (Parse(row.work_day) is DateTime day)
                {
                    var start = day.AddHours(row.start_hour);
                    var end = row.hours > 0 ? start.AddHours(row.hours) : day.AddDays(1);
                    crane.Items.Add(new Item(Kind.Crane, start, end, row.equipment + " · " + row.crane_work + " " + row.block,
                        $"{row.work_day} {row.start_hour:00}:00 · {row.hours}시간 · {row.work_area} · {row.lift_t:0.#} t"));
                }
            }

            // One row per block in lift order (the schedule's), then any block it does not hold.
            var plans = hull.Blocks.ToDictionary(b => b.Id, StringComparer.Ordinal);
            var order = (schedule?.Entries.Select(e => e.Block.block) ?? Enumerable.Empty<string>())
                .Concat(hull.Blocks.Select(b => b.Id)).Distinct().ToList();
            foreach (var id in order)
            {
                plans.TryGetValue(id, out var plan);
                var entry = schedule?.Entries.FirstOrDefault(e => e.Block.block == id);
                var record = plan?.Record ?? entry?.Block;
                var row = Add(BlockGroup, ShortBlock(id) + (record != null && record.step > 0 ? " · " + record.step + "단계" : string.Empty), id);
                foreach (var stay in plan?.Stays ?? new List<StockStay>())
                {
                    // A stay ends at its last day, or at the lift; never after the block has been lifted.
                    var end = stay.Until?.AddDays(1) ?? (entry != null ? LiftStart(schedule, entry) : stay.From.AddDays(1));
                    if (entry != null && end > LiftStart(schedule, entry)) end = LiftStart(schedule, entry);
                    if (end <= stay.From) continue;
                    row.Items.Add(new Item(Kind.Stock, stay.From, end, "적치 " + stay.Location,
                        Day(stay.From) + " ~ " + (stay.Until.HasValue ? Day(stay.Until.Value) : "인양") + " · " + stay.Location));
                }

                if (entry != null)
                {
                    var start = LiftStart(schedule, entry);
                    var landed = schedule.Start.AddDays(entry.FinishDay);
                    row.Items.Add(new Item(Kind.Lift, start, landed, "탑재 " + id,
                        $"{start:MM-dd HH:mm} ~ {landed:MM-dd HH:mm} · {record?.weight_t:0.#} t" +
                        (record != null && record.predecessors.Length > 0 ? " · 선행 " + string.Join(", ", record.predecessors) : string.Empty)));
                }
            }

            timeline.Rows.RemoveAll(r => r.Block == null && r.Items.Count == 0);
            var all = timeline.Items.ToList();
            timeline.From = all.Count > 0 ? all.Min(i => i.Start) : schedule?.Start ?? DateTime.Today;
            timeline.To = all.Count > 0 ? all.Max(i => i.End) : timeline.From.AddDays(1);
            return timeline;
        }

        private static DateTime LiftStart(ErectionSchedule schedule, ErectionSchedule.Entry entry) =>
            schedule.Start.AddDays(entry.FinishDay - entry.LiftDays);

        private static string PointName(YardSnapshot snapshot, string id) =>
            snapshot?.shift_points?.FirstOrDefault(p => p.id == id)?.name ?? id;

        /// <summary>"SN2040-B12" → "B12" (the hull is the bar's context).</summary>
        public static string ShortBlock(string id)
        {
            var cut = id?.IndexOf('-') ?? -1;
            return cut > 0 && cut < id.Length - 1 ? id.Substring(cut + 1) : id;
        }

        private static string Day(DateTime day) => day.ToString("yyyy-MM-dd");

        private static DateTime? Parse(string value) =>
            !string.IsNullOrEmpty(value) && DateTime.TryParseExact(value.Length > 10 ? value.Substring(0, 10) : value, "yyyy-MM-dd",
                CultureInfo.InvariantCulture, DateTimeStyles.None, out var day)
                ? day
                : (DateTime?)null;
    }
}
