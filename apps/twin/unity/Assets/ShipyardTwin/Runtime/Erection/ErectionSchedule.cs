using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace ShipyardTwin
{
    /// <summary>
    /// Erection timeline in days for one crane: blocks are lifted one at a time. Blocks sharing an erection day
    /// (actual, else forecast, else baseline) are spread evenly, in step and id order, over the time since the
    /// previous day's last landing, so the last of them lands on its day. A lift lasts <see cref="LiftDays"/> or
    /// its share of that window, whichever is shorter. Blocks without a day follow the last dated block, one lift
    /// apart. Day 0 is the start of the first lift.
    /// </summary>
    public sealed class ErectionSchedule
    {
        private readonly List<Entry> _entries;

        public ErectionSchedule(IEnumerable<BlockRecord> blocks, float liftDays)
        {
            if (liftDays <= 0f)
            {
                throw new ArgumentOutOfRangeException(nameof(liftDays), "Lift must take more than 0 days.");
            }

            LiftDays = liftDays;
            var dated = new List<(BlockRecord Block, DateTime? Day)>();
            foreach (var block in blocks ?? Enumerable.Empty<BlockRecord>())
            {
                dated.Add((block, ParseDay(block.Day)));
            }

            var known = dated.Where(d => d.Day.HasValue).Select(d => d.Day.Value).ToList();
            var first = known.Count > 0 ? known.Min() : DateTime.Today;
            Start = first.AddDays(-liftDays);

            _entries = new List<Entry>();
            var previous = 0f;
            var groups = dated.Where(d => d.Day.HasValue).GroupBy(d => d.Day.Value).OrderBy(g => g.Key);
            foreach (var group in groups)
            {
                var day = (float)(group.Key - Start).TotalDays;
                var members = group.OrderBy(d => d.Block.step <= 0 ? int.MaxValue : d.Block.step)
                    .ThenBy(d => d.Block.block, StringComparer.Ordinal).ToList();
                var slot = (day - previous) / members.Count;
                for (var i = 0; i < members.Count; i++)
                {
                    _entries.Add(new Entry(members[i].Block, previous + slot * (i + 1), Math.Min(liftDays, slot)));
                }

                previous = day;
            }

            foreach (var (block, _) in dated.Where(d => !d.Day.HasValue)
                         .OrderBy(d => d.Block.step <= 0 ? int.MaxValue : d.Block.step).ThenBy(d => d.Block.block, StringComparer.Ordinal))
            {
                previous += liftDays;
                _entries.Add(new Entry(block, previous, liftDays));
            }

            Duration = _entries.Count > 0 ? _entries.Max(e => e.FinishDay) : 0f;
            TotalWeight = _entries.Sum(e => e.Block.weight_t);
        }

        public float LiftDays { get; }

        /// <summary>Calendar day at cursor 0 (the first lift begins).</summary>
        public DateTime Start { get; }

        /// <summary>Cursor value when every block is erected.</summary>
        public float Duration { get; }

        public float TotalWeight { get; }

        public float Cursor { get; private set; }

        public DateTime CurrentDate => Start.AddDays(Cursor);

        public IReadOnlyList<Entry> Entries => _entries;

        public int ErectedCount { get; private set; }

        public float ErectedWeight { get; private set; }

        /// <summary>Highest step among blocks that are lifting or erected (0 before the first lift).</summary>
        public int CurrentStep { get; private set; }

        public bool Finished => Cursor >= Duration;

        /// <summary>0 = not started, between 0 and 1 = being lifted, 1 = erected.</summary>
        public float Progress(Entry entry) => ProgressAt(entry, Cursor);

        /// <summary><see cref="Progress"/> at any cursor, without moving this schedule's cursor (yard calendar).</summary>
        public static float ProgressAt(Entry entry, float cursor)
        {
            var begin = entry.FinishDay - entry.LiftDays;
            if (cursor <= begin)
            {
                return 0f;
            }

            return cursor >= entry.FinishDay ? 1f : (cursor - begin) / entry.LiftDays;
        }

        /// <summary>Moves the cursor (clamped to 0..Duration) and returns blocks erected by this move, in order.</summary>
        public IReadOnlyList<Entry> Seek(float cursor)
        {
            var previous = Cursor;
            Cursor = Math.Max(0f, Math.Min(Duration, cursor));
            var newly = new List<Entry>();
            ErectedCount = 0;
            ErectedWeight = 0f;
            CurrentStep = 0;
            foreach (var entry in _entries)
            {
                var progress = Progress(entry);
                if (progress >= 1f)
                {
                    ErectedCount++;
                    ErectedWeight += entry.Block.weight_t;
                    if (entry.FinishDay > previous)
                    {
                        newly.Add(entry);
                    }
                }

                if (progress > 0f)
                {
                    CurrentStep = Math.Max(CurrentStep, entry.Block.step);
                }
            }

            return newly;
        }

        public IReadOnlyList<Entry> Advance(float days) => Seek(Cursor + days);

        /// <summary>Blocks currently being lifted.</summary>
        public IEnumerable<Entry> Lifting()
        {
            foreach (var entry in _entries)
            {
                var progress = Progress(entry);
                if (progress > 0f && progress < 1f)
                {
                    yield return entry;
                }
            }
        }

        private static DateTime? ParseDay(string value) =>
            DateTime.TryParseExact(value, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var day)
                ? day
                : (DateTime?)null;

        public sealed class Entry
        {
            public Entry(BlockRecord block, float finishDay, float liftDays)
            {
                Block = block;
                FinishDay = finishDay;
                LiftDays = liftDays;
            }

            public BlockRecord Block { get; }

            /// <summary>Cursor value when the block is landed.</summary>
            public float FinishDay { get; }

            /// <summary>How long this lift takes (days).</summary>
            public float LiftDays { get; }
        }
    }

    /// <summary>
    /// Play / pause / speed state shared by the 3D view, crane and panel. With <see cref="Remote"/> set the shared ERP
    /// replay owns the clock: play, pause, speed, seek and restart are sent there and <see cref="SyncFrom"/> applies what it
    /// reports; <see cref="Tick"/> then does nothing.
    /// </summary>
    public sealed class ErectionPlayback
    {
        public static readonly float[] Speeds = { 1f, 2f, 4f, 8f };

        private bool _playing;

        public ErectionPlayback(ErectionSchedule schedule, float daysPerSecond, bool playing)
        {
            Schedule = schedule;
            DaysPerSecond = daysPerSecond;
            _playing = playing;
        }

        public ErectionSchedule Schedule { get; }

        /// <summary>Shared replay control: (action, value) with action play / pause / speed / seek / restart.</summary>
        public Action<string, string> Remote { get; set; }

        public bool Playing
        {
            get => _playing;
            set
            {
                if (Remote != null && value != _playing) Remote(value ? "play" : "pause", null);
                _playing = value;
            }
        }

        /// <summary>Simulation days per real second at speed ×1.</summary>
        public float DaysPerSecond { get; }

        public float SpeedMultiplier { get; private set; } = 1f;
        internal int PositionRevision { get; private set; }

        public event Action<ErectionSchedule.Entry> BlockErected;

        public void Tick(float seconds)
        {
            if (!_playing || Remote != null)
            {
                return;
            }

            Publish(Schedule.Advance(seconds * DaysPerSecond * SpeedMultiplier));
            if (Schedule.Finished)
            {
                _playing = false;
            }
        }

        public void Seek(float cursor)
        {
            if (Remote != null)
            {
                Remote("seek", Schedule.Start.AddDays(cursor).ToString("yyyy-MM-ddTHH:mm:ss") + "+09:00");
                return;
            }

            PositionRevision++;
            Publish(Schedule.Seek(cursor));
        }

        public void Restart()
        {
            if (Remote != null)
            {
                Remote("restart", null);
                return;
            }

            PositionRevision++;
            Schedule.Seek(0f);
            _playing = true;
        }

        public float NextSpeed()
        {
            var next = Speeds.FirstOrDefault(value => value > SpeedMultiplier);
            return SetSpeed(next > 0f ? next : Speeds[0]);
        }

        public float SetSpeed(float multiplier)
        {
            var next = Math.Max(0.05f, Math.Min(32f, multiplier));
            if (Math.Abs(SpeedMultiplier - next) < 0.00001f) return SpeedMultiplier;
            Remote?.Invoke("speed", next.ToString("R", CultureInfo.InvariantCulture));
            SpeedMultiplier = next;
            return SpeedMultiplier;
        }

        /// <summary>The shared replay's clock: cursor (days from <see cref="ErectionSchedule.Start"/>), play state, speed.</summary>
        public void SyncFrom(float cursor, bool playing, float speed)
        {
            _playing = playing;
            SpeedMultiplier = speed;
            Publish(Schedule.Seek(cursor));
        }

        private void Publish(IReadOnlyList<ErectionSchedule.Entry> erected)
        {
            foreach (var entry in erected)
            {
                BlockErected?.Invoke(entry);
            }
        }
    }
}

namespace ShipyardTwin
{
    /// <summary>
    /// Clock of the yard calendar playback (no hull selected): every hull's shops, stays, lifts, launch, berth and delivery
    /// at one calendar time that runs from <see cref="Start"/> to <see cref="End"/> and loops back to the start.
    /// </summary>
    public sealed class YardCalendar
    {
        public static readonly float[] Speeds = { 0.25f, 0.5f, 1f, 2f, 4f, 8f };

        public YardCalendar(DateTime start, DateTime end, DateTime now, float daysPerSecond)
        {
            Start = start;
            End = end > start ? end : start.AddDays(1);
            DaysPerSecond = daysPerSecond;
            Time = now < Start || now >= End ? Start : now;
        }

        public DateTime Start { get; }
        public DateTime End { get; }
        public DateTime Time { get; private set; }

        /// <summary>Calendar days per real second at ×1.</summary>
        public float DaysPerSecond { get; }

        public float SpeedMultiplier { get; set; } = 1f;
        public bool Playing { get; set; } = true;

        /// <summary>Completed loops over the range.</summary>
        public int Cycle { get; private set; }
        internal int PositionRevision { get; private set; }

        public float Fraction => (float)((Time - Start).TotalDays / (End - Start).TotalDays);

        public void Tick(float seconds)
        {
            if (Playing && seconds > 0f)
            {
                Advance(seconds * DaysPerSecond * SpeedMultiplier);
            }
        }

        public void Advance(double days)
        {
            var next = Time.AddDays(days);
            if (next >= End)
            {
                Cycle++;
                PositionRevision++;
                next = Start.AddDays((next - End).TotalDays % (End - Start).TotalDays);
            }

            Time = next;
        }

        public void Seek(DateTime time)
        {
            PositionRevision++;
            Time = time < Start ? Start : time >= End ? End.AddSeconds(-1) : time;
        }

        public void NextSpeed()
        {
            var i = Array.IndexOf(Speeds, SpeedMultiplier);
            SpeedMultiplier = Speeds[(i + 1) % Speeds.Length];
        }
    }
}
