using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using UnityEngine;

namespace ShipyardTwin
{
    /// <summary>Where a block is on a given day.</summary>
    public enum BlockState
    {
        /// <summary>Its first stocking stay is still ahead: in the shops, not shown.</summary>
        InProduction,

        /// <summary>In a yard slot (ERP block_stock_plans).</summary>
        Stocked,

        /// <summary>On the hook (simulation only).</summary>
        Lifting,

        /// <summary>On the hull in its dock.</summary>
        Erected,

        /// <summary>Not erected and no stocking plan or slot for that day (미배치): listed, not drawn.</summary>
        Unplaced,
    }

    /// <summary>One stay of a block at a yard slot, from a block_stock_plans row.</summary>
    public sealed class StockStay
    {
        public StockStay(StockPlanRow row, DateTime from, DateTime? until)
        {
            Row = row;
            From = from;
            Until = until;
        }

        public StockPlanRow Row { get; }
        public string Location => Row.location;
        public DateTime From { get; }

        /// <summary>Last day in the slot; null = until the erection lift.</summary>
        public DateTime? Until { get; }

        public bool Covers(DateTime day) => From <= day && (!Until.HasValue || day <= Until.Value);
    }

    /// <summary>
    /// A block in a shop (작업장): one ERP assembly stage (block_stages, its centre's facility from center_locations) from the
    /// day after the previous stage ended (a week before the first one ends) to its own end, both inclusive; or, with
    /// <see cref="Waiting"/>, done and waiting at the last shop until its first yard stay or its lift.
    /// </summary>
    public sealed class ShopStage
    {
        public ShopStage(string location, string stage, DateTime from, DateTime until, bool waiting = false)
        {
            Location = location;
            Stage = stage;
            From = from;
            Until = until;
            Waiting = waiting;
        }

        public string Location { get; }
        public string Stage { get; }
        public DateTime From { get; }
        public DateTime Until { get; }
        public bool Waiting { get; }

        public bool Covers(DateTime day) => From <= day.Date && day.Date <= Until;
    }

    /// <summary>A hull's whereabouts on a day (<see cref="YardModel.HullAt"/>).</summary>
    public enum HullWhere
    {
        /// <summary>Not in its dock yet (blocks in production or stocked).</summary>
        Building,
        Dock,
        Berth,

        /// <summary>Sea trial or delivered: not in the yard.</summary>
        Away,
    }

    public sealed class BlockPlan
    {
        public BlockPlan(BlockRecord record, List<StockStay> stays)
        {
            Record = record;
            Stays = stays;
            ErectionDay = YardModel.ParseDay(record.Day);
        }

        public BlockRecord Record { get; }
        public string Id => Record.block;
        public string Hull => Record.hull;

        /// <summary>Stays in start order.</summary>
        public List<StockStay> Stays { get; }

        /// <summary>Actual, else forecast, else baseline erection day.</summary>
        public DateTime? ErectionDay { get; }

        public StockStay StayAt(DateTime day)
        {
            for (var i = Stays.Count - 1; i >= 0; i--)
            {
                if (Stays[i].Covers(day))
                {
                    return Stays[i];
                }
            }

            return null;
        }

        /// <summary>The documented stocking slot on the lift day, or null when the source is unknown.</summary>
        public StockStay LiftSource(DateTime liftDay) => StayAt(liftDay);

        /// <summary>Its stages in shops (작업장), in order; empty without ERP block_stages.</summary>
        public List<ShopStage> ShopStages { get; } = new List<ShopStage>();

        private ShopStage _waiting;

        /// <summary>
        /// The shop stage the block is in on a day; after its last shop stage, the wait at that shop until its first yard
        /// stay (or its lift when it has none, brought straight from the shop). Null before production, in the yard or erected.
        /// </summary>
        public ShopStage ShopAt(DateTime day)
        {
            day = day.Date;
            foreach (var stage in ShopStages)
            {
                if (stage.Covers(day))
                {
                    return stage;
                }
            }

            if (ShopStages.Count == 0)
            {
                return null;
            }

            var last = ShopStages[ShopStages.Count - 1];
            var leaves = Stays.Count > 0 ? Stays[0].From : ErectionDay;
            if (day <= last.Until || !leaves.HasValue || day >= leaves.Value)
            {
                return null;
            }

            return _waiting ??= new ShopStage(last.Location, "반출 대기", last.Until.AddDays(1), leaves.Value.AddDays(-1), true);
        }
    }

    public sealed class HullPlan
    {
        public string Id;
        public string Name;
        public HullInfo Info;
        public HullPlacement Placement;
        public Facility Dock;
        public List<BlockPlan> Blocks = new List<BlockPlan>();

        /// <summary>ERP dock bookings of this hull (start, end inclusive); empty = no booking, in the dock on every day.</summary>
        public List<(DateTime Start, DateTime End)> DockStays = new List<(DateTime Start, DateTime End)>();

        /// <summary>On the building berth that day: a booking covers it, or the hull has no bookings at all.</summary>
        public bool InDock(DateTime day) => DockStays.Count == 0 || DockStays.Any(s => s.Start <= day.Date && day.Date <= s.End);

        /// <summary>Same dock and overlapping ship length (aft end to bow along the dock): they cannot be there together.</summary>
        public bool SharesBerthWith(HullPlan other)
        {
            if (other == null || other == this || Dock?.location != other.Dock?.location || Placement == null || other.Placement == null)
            {
                return false;
            }

            float Along(HullPlacement p) => p.origin_x * Mathf.Cos(p.heading_deg * Mathf.Deg2Rad) + p.origin_y * Mathf.Sin(p.heading_deg * Mathf.Deg2Rad);
            float a0 = Along(Placement), b0 = Along(other.Placement);
            return a0 < b0 + Mathf.Max(other.Info.length_m, 1f) && b0 < a0 + Mathf.Max(Info.length_m, 1f);   // unknown length: same origin only
        }

        public string Label => Id + (string.IsNullOrEmpty(Info?.vessel_type) ? string.Empty : " · " + Info.vessel_type);
    }

    public struct YardCounts
    {
        public int Stocked, InProduction, Erected, Unplaced;
    }

    /// <summary>
    /// The yard plan from the ERP (or its snapshot): hulls placed on this yard's docks, their blocks with erection
    /// days and stocking stays, and the facilities. Pure data: the scene reads states from it, by block ID.
    /// </summary>
    public sealed class YardModel
    {
        private readonly Dictionary<string, BlockPlan> _blocks = new Dictionary<string, BlockPlan>(StringComparer.Ordinal);

        private YardModel()
        {
        }

        private readonly Dictionary<string, HullPlacement> _berthPlacements = new Dictionary<string, HullPlacement>();

        public YardSnapshot Source { get; private set; }

        /// <summary>Plan reference day: blocks are placed as the plan says they are on this day.</summary>
        public DateTime AsOf { get; private set; }

        public List<HullPlan> Hulls { get; } = new List<HullPlan>();

        public IReadOnlyDictionary<string, BlockPlan> Blocks => _blocks;

        public Facility[] Facilities { get; private set; } = new Facility[0];

        public YardSite Site { get; private set; }

        public HullPlan Hull(string id) => Hulls.FirstOrDefault(h => h.Id == id);

        /// <summary>Registered berth pose on the yard overview day; offshore points are not positions.</summary>
        public HullPlacement BerthPlacement(string hull) =>
            _berthPlacements.TryGetValue(hull, out var placement) ? placement : null;

        public static DateTime? ParseDay(string value) =>
            DateTime.TryParseExact(value, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var day)
                ? day
                : (DateTime?)null;

        /// <summary>
        /// Hulls whose placement dock is one of the yard facilities and that have blocks; their blocks and stays. Every
        /// such hull is listed (and can be simulated), also when several share a dock: their ERP dock bookings
        /// (<see cref="HullPlan.DockStays"/>) say on which days each one stands there.
        /// Stay rows with unreadable days are dropped (the block may end up unplaced).
        /// </summary>
        public static YardModel Build(YardSnapshot snapshot, DateTime asOf)
        {
            var facilities = DistinctFootprints(snapshot.location_geo, out var alias);
            string Canonical(string id) => id != null && alias.TryGetValue(id, out var kept) ? kept : id;
            var model = new YardModel { Source = snapshot, AsOf = asOf.Date, Facilities = facilities };
            model.Site = snapshot.yard_sites.FirstOrDefault();
            model.ReadBerthPlacements(snapshot);
            var docks = facilities.Where(f => f.kind == "도크").ToDictionary(f => f.location);
            var stays = new Dictionary<string, List<StockStay>>(StringComparer.Ordinal);
            foreach (var row in snapshot.block_stock_plans)
            {
                var from = ParseDay(row.stock_from);
                if (from == null || string.IsNullOrEmpty(row.block))
                {
                    continue;
                }

                if (!stays.TryGetValue(row.block, out var list))
                {
                    stays[row.block] = list = new List<StockStay>();
                }

                list.Add(new StockStay(row, from.Value, ParseDay(row.stock_until)));
            }

            var blocksByHull = snapshot.erection_timeline.Where(b => !string.IsNullOrEmpty(b.block) && !string.IsNullOrEmpty(b.hull))
                .GroupBy(b => b.hull).ToDictionary(g => g.Key, g => g.ToList());
            var placed = new List<(HullRow Row, HullPlacement Placement, Facility Dock, List<BlockRecord> Records)>();
            foreach (var row in snapshot.hulls.OrderBy(h => h.id, StringComparer.Ordinal))
            {
                var placement = snapshot.hull_placements.FirstOrDefault(p => p.hull == row.id);
                if (placement == null || !docks.TryGetValue(Canonical(placement.dock), out var dock) || !blocksByHull.TryGetValue(row.id, out var records))
                {
                    continue;
                }

                placement.dock = dock.location;
                placed.Add((row, placement, dock, records));
            }

            var bookings = (snapshot.dock_bookings ?? new DockBookingRow[0])
                .Select(b => (b.hull, Start: ParseDay(b.start_day), End: ParseDay(b.end_day)))
                .Where(b => b.hull != null && b.Start.HasValue && b.End.HasValue && b.Start <= b.End).ToList();
            var shopStages = ShopStagesByBlock(snapshot, facilities, Canonical);
            foreach (var (row, placement, dock, records) in placed)
            {
                var vessel = snapshot.vessel_details.FirstOrDefault(v => v.hull == row.id) ?? new HullInfo { hull = row.id };
                var geometry = snapshot.hull_geometry.FirstOrDefault(g => g.hull == row.id);
                var info = new HullInfo
                {
                    hull = row.id, vessel_type = vessel.vessel_type, vessel_name = vessel.vessel_name, customer = vessel.customer,
                    length_m = vessel.length_m, beam_m = vessel.beam_m, delivery = vessel.delivery,
                    depth_m = geometry?.depth_m ?? vessel.depth_m, draft_m = geometry?.draft_m ?? vessel.draft_m,
                    frame_spacing_m = geometry?.frame_spacing_m ?? vessel.frame_spacing_m,
                };
                placement.dock_position = new YardPoint { location = dock.location, x = dock.x, y = dock.y, z = dock.z, heading_deg = dock.heading_deg };
                var hull = new HullPlan { Id = row.id, Name = row.name, Info = info, Placement = placement, Dock = dock };
                hull.DockStays.AddRange(bookings.Where(b => b.hull == row.id).OrderBy(b => b.Start).Select(b => (b.Start.Value, b.End.Value)));
                foreach (var record in records)
                {
                    record.predecessors ??= new string[0];
                    var list = stays.TryGetValue(record.block, out var s) ? s.OrderBy(x => x.From).ToList() : new List<StockStay>();
                    var plan = new BlockPlan(record, list);
                    if (shopStages.TryGetValue(record.block, out var inShops)) plan.ShopStages.AddRange(inShops);
                    hull.Blocks.Add(plan);
                    model._blocks[plan.Id] = plan;
                }

                model.Hulls.Add(hull);
            }

            model.ReadHullMoves(snapshot);
            return model;
        }

        /// <summary>
        /// Shop stages of every block from the ERP block_stages: a stage ends on its actual (else forecast, else baseline)
        /// day and starts the day after the previous stage of the block ended (the first one a week before its end). Only
        /// stages at a work centre whose facility is a shop (작업장) of the yard are kept: the PE stage is the yard stay and
        /// the erection is the lift.
        /// </summary>
        private static Dictionary<string, List<ShopStage>> ShopStagesByBlock(YardSnapshot snapshot, Facility[] facilities, Func<string, string> canonical)
        {
            var result = new Dictionary<string, List<ShopStage>>(StringComparer.Ordinal);
            if (snapshot.block_stages == null || snapshot.center_locations == null)
            {
                return result;
            }

            var shops = new HashSet<string>(facilities.Where(f => f.kind == "작업장" && f.HasFootprint).Select(f => f.location), StringComparer.Ordinal);
            var where = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var row in snapshot.center_locations)
            {
                if (row.center != null && row.location != null) where[row.center] = canonical(row.location);
            }

            foreach (var group in snapshot.block_stages.Where(r => r.block != null).GroupBy(r => r.block))
            {
                var dated = group.Select(r => (Row: r, End: ParseDay(r.actual) ?? ParseDay(r.forecast) ?? ParseDay(r.baseline)))
                    .Where(r => r.End.HasValue).OrderBy(r => r.End.Value).ToList();
                DateTime? previous = null;
                var list = new List<ShopStage>();
                foreach (var (row, end) in dated)
                {
                    var from = previous.HasValue ? previous.Value.AddDays(1) : end.Value.AddDays(-6);
                    if (from > end.Value) from = end.Value;
                    previous = end;
                    if (row.center != null && where.TryGetValue(row.center, out var location) && shops.Contains(location))
                    {
                        list.Add(new ShopStage(location, row.assembly_stage, from, end.Value));
                    }
                }

                if (list.Count > 0) result[group.Key] = list;
            }

            return result;
        }

        /// <summary>Blocks in a shop on a day, with the stage each is in (<see cref="BlockPlan.ShopAt"/>), by stage start.</summary>
        public List<(BlockPlan Block, ShopStage Stage)> ShopBlocks(string location, DateTime day)
        {
            var list = new List<(BlockPlan, ShopStage)>();
            foreach (var block in _blocks.Values)
            {
                var stage = block.ShopAt(day);
                if (stage != null && stage.Location == location && YardModel.StateAt(block, day) == BlockState.InProduction)
                {
                    list.Add((block, stage));
                }
            }

            return list.OrderBy(b => b.Item2.From).ThenBy(b => b.Item1.Id, StringComparer.Ordinal).ToList();
        }

        private readonly Dictionary<string, List<(DateTime Day, ShiftPointRow Point)>> _hullMoves =
            new Dictionary<string, List<(DateTime, ShiftPointRow)>>(StringComparer.Ordinal);

        /// <summary>Quay shifts of every hull in date order (actual, else planned day) with their destination point.</summary>
        private void ReadHullMoves(YardSnapshot snapshot)
        {
            var points = (snapshot.shift_points ?? Array.Empty<ShiftPointRow>()).Where(p => p.id != null)
                .GroupBy(p => p.id).ToDictionary(g => g.Key, g => g.First());
            foreach (var group in (snapshot.quay_shifts ?? Array.Empty<QuayShiftRow>()).Where(s => s.hull != null).GroupBy(s => s.hull))
            {
                _hullMoves[group.Key] = group
                    .Select(s => (Day: ParseDay(s.actual_day) ?? ParseDay(s.plan_day), Row: s))
                    .Where(s => s.Day.HasValue && s.Row.to_point != null && points.ContainsKey(s.Row.to_point))
                    .OrderBy(s => s.Day.Value).ThenBy(s => s.Row.id, StringComparer.Ordinal)
                    .Select(s => (s.Day.Value, points[s.Row.to_point])).ToList();
            }
        }

        /// <summary>
        /// Where a hull is on a day: in its dock while booked there, then where its last quay shift took it (a berth with a
        /// known position, the dock again, or away at sea / delivered). After the dock stay without any shift it has left.
        /// </summary>
        public (HullWhere Where, HullPlacement Pose) HullAt(HullPlan hull, DateTime day)
        {
            day = day.Date;
            if (hull.InDock(day))
            {
                return (HullWhere.Dock, hull.Placement);
            }

            if (hull.DockStays.Count > 0 && day < hull.DockStays[0].Start)
            {
                return (HullWhere.Building, null);
            }

            if (_hullMoves.TryGetValue(hull.Id, out var moves))
            {
                for (var i = moves.Count - 1; i >= 0; i--)
                {
                    if (moves[i].Day > day)
                    {
                        continue;
                    }

                    var point = moves[i].Point;
                    if (point.kind == "도크")
                    {
                        return (HullWhere.Dock, hull.Placement);
                    }

                    if (point.kind == "해상" || !point.position_known || point.position == null)
                    {
                        return (HullWhere.Away, null);
                    }

                    var pose = point.position;
                    return (HullWhere.Berth, new HullPlacement
                    {
                        hull = hull.Id, origin_x = pose.x, origin_y = pose.y, origin_z = pose.z, heading_deg = pose.heading_deg,
                    });
                }
            }

            return (HullWhere.Away, null);
        }

        /// <summary>
        /// Days the yard calendar loops over: from the first shop stage, stay or dock stay of any hull to a few days after
        /// its last erection, launch or quay shift (delivery).
        /// </summary>
        public (DateTime Start, DateTime End) CalendarRange()
        {
            var days = new List<DateTime>();
            foreach (var hull in Hulls)
            {
                days.AddRange(hull.DockStays.SelectMany(s => new[] { s.Start, s.End }));
                foreach (var block in hull.Blocks)
                {
                    if (block.ErectionDay.HasValue) days.Add(block.ErectionDay.Value);
                    if (block.Stays.Count > 0) days.Add(block.Stays[0].From);
                    if (block.ShopStages.Count > 0) days.Add(block.ShopStages[0].From);
                }
            }

            days.AddRange(_hullMoves.Where(m => Hull(m.Key) != null).SelectMany(m => m.Value.Select(v => v.Day)));
            return days.Count == 0 ? (AsOf, AsOf.AddDays(1)) : (days.Min(), days.Max().AddDays(3));
        }

        private void ReadBerthPlacements(YardSnapshot snapshot)
        {
            var points = (snapshot.shift_points ?? Array.Empty<ShiftPointRow>()).ToDictionary(p => p.id);
            var latestShift = (snapshot.quay_shifts ?? Array.Empty<QuayShiftRow>())
                .Select(s => (Row: s, Day: ParseDay(s.actual_day) ?? ParseDay(s.plan_day)))
                .Where(s => s.Day.HasValue && s.Day.Value <= AsOf)
                .OrderByDescending(s => s.Day).ThenByDescending(s => s.Row.id, StringComparer.Ordinal)
                .ToLookup(s => s.Row.hull, s => s.Row);
            foreach (var assignment in snapshot.berth_assignments ?? Array.Empty<BerthAssignmentRow>())
            {
                var start = ParseDay(assignment.start_day);
                var end = ParseDay(assignment.end_day);
                if (!start.HasValue || !end.HasValue || start.Value > AsOf || end.Value < AsOf ||
                    !points.TryGetValue(assignment.berth, out var point) || !point.position_known ||
                    point.position == null ||
                    (latestShift[assignment.hull].FirstOrDefault() is QuayShiftRow shift &&
                     points.TryGetValue(shift.to_point, out var destination) && destination.kind == "해상"))
                {
                    continue;
                }

                var pose = point.position;
                _berthPlacements[assignment.hull] = new HullPlacement
                {
                    hull = assignment.hull, origin_x = pose.x, origin_y = pose.y, origin_z = pose.z, heading_deg = pose.heading_deg,
                };
            }
        }

        /// <summary>
        /// Facilities with footprints, one per distinct footprint: ERP aliases of the same building (same kind, centre,
        /// size and heading) collapse to one, preferring a name without "트윈", then the lowest ID. alias maps every
        /// dropped ID to the kept one. Point locations are kept as they are.
        /// </summary>
        public static Facility[] DistinctFootprints(IEnumerable<Facility> facilities, out Dictionary<string, string> alias)
        {
            alias = new Dictionary<string, string>(StringComparer.Ordinal);
            var kept = new List<Facility>();
            foreach (var group in facilities.GroupBy(f => f.HasFootprint
                         ? string.Join("|", f.kind, Mathf.Round(f.x * 10), Mathf.Round(f.y * 10), Mathf.Round(f.size_x_m * 10),
                             Mathf.Round(f.size_y_m * 10), Mathf.Round(f.heading_deg * 10))
                         : "point|" + f.location))
            {
                var ordered = group.OrderBy(f => (f.name ?? string.Empty).Contains("트윈") ? 1 : 0)
                    .ThenBy(f => f.location, StringComparer.Ordinal).ToList();
                kept.Add(ordered[0]);
                foreach (var other in ordered.Skip(1))
                {
                    alias[other.location] = ordered[0].location;
                }
            }

            return kept.ToArray();
        }

        /// <summary>
        /// Hull in each dock on a day from the ERP dock bookings: the booking covering the day, else the next one to
        /// start, else the last one to end. Docks without bookings are absent (every placed hull is shown there).
        /// </summary>
        public static Dictionary<string, string> DockOccupants(IEnumerable<DockBookingRow> bookings, Func<string, string> canonical, DateTime day)
        {
            var result = new Dictionary<string, string>(StringComparer.Ordinal);
            var valid = (bookings ?? Enumerable.Empty<DockBookingRow>())
                .Select(b => (Dock: canonical(b.dock), b.hull, Start: ParseDay(b.start_day), End: ParseDay(b.end_day)))
                .Where(b => b.Dock != null && b.hull != null && b.Start.HasValue && b.End.HasValue);
            foreach (var dock in valid.GroupBy(b => b.Dock))
            {
                var current = dock.FirstOrDefault(b => b.Start <= day && day <= b.End);
                var next = dock.Where(b => b.Start > day).OrderBy(b => b.Start).FirstOrDefault();
                var last = dock.OrderByDescending(b => b.End).First();
                result[dock.Key] = current.hull ?? next.hull ?? last.hull;
            }

            return result;
        }

        /// <summary>The hulls standing in each dock on a day (bookings covering it, or hulls without bookings).</summary>
        public Dictionary<string, List<HullPlan>> InDocks(DateTime day) =>
            Hulls.Where(h => h.InDock(day)).GroupBy(h => h.Dock.location).ToDictionary(g => g.Key, g => g.ToList());

        /// <summary>
        /// State on a day: erected once its erection day has come; otherwise in the slot of a stay covering the day;
        /// in production before its first stay; unplaced with no stays (or none left before erection).
        /// </summary>
        public static BlockState StateAt(BlockPlan block, DateTime day)
        {
            if (block.ErectionDay.HasValue && block.ErectionDay.Value <= day)
            {
                return BlockState.Erected;
            }

            if (block.StayAt(day) != null)
            {
                return BlockState.Stocked;
            }

            if (block.ShopAt(day) != null || block.Stays.Count > 0 && day < block.Stays[0].From ||
                block.ShopStages.Count > 0 && day < block.ShopStages[0].From)
            {
                return BlockState.InProduction;
            }

            return BlockState.Unplaced;
        }

        public static YardCounts Count(IEnumerable<BlockPlan> blocks, DateTime day)
        {
            var counts = new YardCounts();
            foreach (var block in blocks)
            {
                switch (StateAt(block, day))
                {
                    case BlockState.Stocked: counts.Stocked++; break;
                    case BlockState.InProduction: counts.InProduction++; break;
                    case BlockState.Erected: counts.Erected++; break;
                    default: counts.Unplaced++; break;
                }
            }

            return counts;
        }

        /// <summary>The single-hull view the environment, camera and crane were built around (facilities, site, placement).</summary>
        public TwinFile ToTwinFile(HullPlan hull)
        {
            return new TwinFile
            {
                format = "shipyard-twin/1", exported_at = Source?.exported_at, hull = hull?.Info ?? new HullInfo(),
                placement = hull?.Placement ?? new HullPlacement(),
                blocks = hull?.Blocks.Select(b => b.Record).ToArray() ?? new BlockRecord[0],
                yard = new YardPlan { site = Site, facilities = Facilities },
            };
        }
    }
}
