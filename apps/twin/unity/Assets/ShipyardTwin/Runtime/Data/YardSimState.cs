using System;
using System.Globalization;
using UnityEngine;

namespace ShipyardTwin
{
    // Shared ERP yard replay (GET/POST /api/yard_sim, apps/erp/yardsim.py). Field names match the JSON (snake_case for
    // JsonUtility). Positions are ERP yard metres; z is the centre-of-gravity height in the Unity frame (quay at 12 m).
    // Hidden or unplaced items carry no position (JSON null): JsonUtility leaves 0, so read Placed first.

    [Serializable]
    public sealed class YardSimState
    {
        public YardSimScenario scenario = new YardSimScenario();
        public YardSimClock clock = new YardSimClock();
        public YardSimBlock[] blocks = new YardSimBlock[0];
        public YardSimEquipment[] equipment = new YardSimEquipment[0];
        public string[] activity = new string[0];

        /// <summary>A scenario is open (the reply of GET with no session is {"scenario": null}).</summary>
        public bool HasScenario => scenario != null && !string.IsNullOrEmpty(scenario.id);

        public static YardSimState Parse(string json)
        {
            // Nulls become 0 in JsonUtility: keep which positions were really given.
            var state = JsonUtility.FromJson<YardSimState>(json) ?? new YardSimState();
            state.blocks ??= new YardSimBlock[0];
            state.equipment ??= new YardSimEquipment[0];
            state.activity ??= new string[0];
            state.clock ??= new YardSimClock();
            state.scenario ??= new YardSimScenario();
            return state;
        }

        /// <summary>Clock time as a local (KST) date and time; MinValue when absent.</summary>
        public DateTime Time => ParseTime(clock?.time);

        public static DateTime ParseTime(string iso) =>
            DateTimeOffset.TryParse(iso, CultureInfo.InvariantCulture, DateTimeStyles.None, out var time)
                ? time.ToOffset(TimeSpan.FromHours(9)).DateTime
                : DateTime.MinValue;
    }

    [Serializable]
    public sealed class YardSimScenario
    {
        public string id, hull, dock, start, end;
    }

    [Serializable]
    public sealed class YardSimClock
    {
        public string time, plan_day, timezone;
        public bool playing, work;
        public float speed = 1f, days_per_second, work_minutes_per_second;
        public int cycle, version;
    }

    [Serializable]
    public sealed class YardSimBlock
    {
        public string id, state, location, vehicle, crane, source, basis, plan_day;
        public float x, y, z, heading_deg;
        public int step;

        /// <summary>Drawn in the yard or on the hull (states without a position are hidden or listed as unplaced).</summary>
        public bool Placed => state != "생산 중" && state != "미배치" && !string.IsNullOrEmpty(state);

        public bool OnHook => state == "인양" || state == "착지";
    }

    [Serializable]
    public sealed class YardSimEquipment
    {
        public string id, name, kind, type, state, block, source, basis, job_basis, station, crane_type, work, kinematics;
        public float x, y, heading_deg, rail_pos, trolley_m;

        // Rail jib cranes (crane_type "jib"): slew = yard heading of the boom, luff = boom angle from horizontal,
        // radius = hook radius, tip_z = boom tip height (Unity frame), load_t = weight on the hook.
        public float slew_deg, luff_deg, radius_m, tip_z, boom_m, pivot_height_m, load_t;

        public bool IsJib => crane_type == "jib";
        public bool loaded;
        public YardSimHook hook = new YardSimHook();
        public YardSimRail rail = new YardSimRail();

        public bool Placed => state != "미배치" && !string.IsNullOrEmpty(state);
        public bool IsCrane => kind == "crane";
    }

    [Serializable]
    public sealed class YardSimHook
    {
        public float x, y, z;
    }

    [Serializable]
    public sealed class YardSimRail
    {
        public float x0, y0, x1, y1, gauge_m;
        public string source, basis;
    }
}
