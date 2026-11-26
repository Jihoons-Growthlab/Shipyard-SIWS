using System;
using System.Collections.Generic;
using System.Linq;
using System.Collections;
using GrowthLab.Core;
using UnityEngine;
using UnityEngine.Networking;

namespace ShipyardTwin
{
    /// <summary>
    /// Rail jib crane helpers shared with the ERP (apps/erp/yardsim.py): parked rail position, boom angle for a radius,
    /// parked hook height. The cranes move only for their ERP crane assignments (GET /api/crane_activity, or the shared
    /// replay); otherwise they wait parked with the boom up and nothing on the hook.
    /// </summary>
    public static class RailCraneMotion
    {
        public const float RestHook = 28f;

        /// <summary>Parked rail position 0..1: the middle of the crane's travel zone (cranes sharing a rail wait apart).</summary>
        public static float Home(string id) => 0.5f;

        public static float LuffFor(CraneKinematicsRow k, float radius)
        {
            var low = Mathf.Cos(k.luff_max_deg * Mathf.Deg2Rad);
            var high = Mathf.Cos(k.luff_min_deg * Mathf.Deg2Rad);
            return Mathf.Acos(Mathf.Clamp(radius / k.boom_m, low, high)) * Mathf.Rad2Deg;
        }

        /// <summary>Smallest hook radius (m): boom · cos(largest boom angle).</summary>
        public static float MinReach(CraneKinematicsRow k) => k.boom_m * Mathf.Cos(k.luff_max_deg * Mathf.Deg2Rad);

        /// <summary>Largest hook radius (m): boom · cos(smallest boom angle).</summary>
        public static float MaxReach(CraneKinematicsRow k) => k.boom_m * Mathf.Cos(k.luff_min_deg * Mathf.Deg2Rad);

        /// <summary>Danger zone radius around the slewing centre: largest hook radius + ERP danger margin (yardsim danger_radius_m).</summary>
        public static float DangerRadius(CraneKinematicsRow k) => MaxReach(k) + Mathf.Max(0f, k.danger_margin_m);

        /// <summary>
        /// Rated load (t) at a hook radius from load chart points sorted by radius: the first point at or beyond the
        /// radius (conservative between points), 0 beyond the last, null without a chart (apps/erp/geometry.py chart_capacity).
        /// </summary>
        public static float? RatedAt(IReadOnlyList<CraneLoadChartRow> chart, float radius)
        {
            if (chart == null || chart.Count == 0) return null;
            foreach (var point in chart)
            {
                if (radius <= point.radius_m + 1e-3f) return point.capacity_t;
            }

            return 0f;
        }

        /// <summary>Hook height above the quay while parked: 28 m, at most 4 m under the parked boom tip.</summary>
        public static float TopHook(CraneKinematicsRow k) =>
            Mathf.Min(RestHook, k.pivot_height_m + k.boom_m * Mathf.Sin(k.rest_luff_deg * Mathf.Deg2Rad) - 4f);
    }

    /// <summary>GET /api/crane_activity: every ERP rail jib crane at the ERP's clock (same fields as the replay's equipment).</summary>
    [Serializable]
    public sealed class CraneActivity
    {
        public string time;
        public YardSimEquipment[] cranes = new YardSimEquipment[0];

        public static CraneActivity Parse(string json)
        {
            var activity = JsonUtility.FromJson<CraneActivity>(json) ?? new CraneActivity();
            activity.cranes ??= new YardSimEquipment[0];
            return activity;
        }
    }

    /// <summary>
    /// The ERP's rail-mounted cranes (crane_rails + crane_kinematics), built as a working crane: a portal frame on its two
    /// rails, a slewing superstructure (machinery house, cab, counterweight, A-frame) that turns, a boom that luffs from
    /// its foot pin, the hoist wire and hook block that go up and down, and a load on the hook while lifting. The pose
    /// comes from the shared ERP replay when it reports the crane (<see cref="YardSimSync"/>), else from the ERP crane plan
    /// now (GET /api/crane_activity every <see cref="PollSeconds"/>, blended between replies); without ERP data the cranes
    /// wait parked. A crane only lifts during its ERP crane assignments, and what it lifts is outfitting material.
    /// Replaces the fixed quay cranes of the scenery when the ERP has rails. Per frame: a few transforms per crane.
    /// </summary>
    public sealed class RailCraneView : MonoBehaviour, ICoreConsumer
    {
        public const string Model = "luffing_crane";
        private const float RailOverrun = 6f;

        /// <summary>One built crane and what it is doing now (read by <see cref="CraneSafetyView"/>).</summary>
        public sealed class Crane
        {
            public CraneRailRow Row;
            public CraneKinematicsRow Boom;
            public CraneLoadChartRow[] Chart = new CraneLoadChartRow[0];
            public Transform Root, Slew, BoomPivot, Hook, Wire, Load;
            public HoverTarget Target;
            public string State;

            /// <summary>Weight on the hook now (t) and the work state shown in the tooltip.</summary>
            public float LoadT;
            public string Work;

            /// <summary>Hook radius now: horizontal distance from the slewing centre to the hook (m).</summary>
            public float Radius
            {
                get
                {
                    var d = Hook.position - Root.position;
                    return new Vector2(d.x, d.z).magnitude;
                }
            }
        }

        private readonly List<Crane> _cranes = new List<Crane>();
        public const float PollSeconds = 1f;
        private readonly Dictionary<string, YardSimEquipment> _live = new Dictionary<string, YardSimEquipment>();
        private readonly Dictionary<string, YardSimEquipment> _livePrevious = new Dictionary<string, YardSimEquipment>();
        private float _liveAt, _nextPoll;
        private bool _polling;
        private YardDataSource _source;
        private ShipPalette _palette;
        private Transform _craneDisplay;
        private YardDisplayLayers _displayLayers;
        private CraneRailRow[] _built;
        private float _nextLabel;

        public int Count => _cranes.Count;

        public IReadOnlyList<Crane> Cranes => _cranes;

        /// <summary>The crane a hover target belongs to, else null.</summary>
        public Crane Find(HoverTarget target) => target == null ? null : _cranes.FirstOrDefault(c => c.Target == target);

        /// <summary>Raised after the cranes were rebuilt from new ERP data (a selection should look its crane up again).</summary>
        public event Action Rebuilt;

        public void Construct(CoreContext context)
        {
            if (_source != null || !context.Services.TryGet(out _source) || !context.Services.TryGet(out _palette))
            {
                return;
            }

            context.Services.TryGet(out _displayLayers);
            _craneDisplay = _displayLayers?.ParentFor(transform, YardDisplayLayer.Cranes);
            _source.Changed += Rebuild;
            Rebuild();
        }

        private void OnDestroy()
        {
            if (_source != null)
            {
                _source.Changed -= Rebuild;
            }
        }

        /// <summary>True when the yard data has rail cranes: the scenery leaves out its fixed quay cranes then.</summary>
        public static bool HasRails(YardDataSource source) => source?.Model?.Source?.crane_rails?.Length > 0;

        private void Rebuild()
        {
            var rows = _source.Model?.Source?.crane_rails;
            if (rows == null || rows == _built)
            {
                return;
            }

            _built = rows;
            foreach (Transform child in transform)
            {
                if (child != _craneDisplay) Destroy(child.gameObject);
            }
            if (_craneDisplay != null)
            {
                foreach (Transform child in _craneDisplay) Destroy(child.gameObject);
            }

            _cranes.Clear();
            var booms = (_source.Model?.Source?.crane_kinematics ?? new CraneKinematicsRow[0])
                .Where(k => !string.IsNullOrEmpty(k.equipment)).GroupBy(k => k.equipment).ToDictionary(g => g.Key, g => g.First());
            var charts = (_source.Model?.Source?.crane_load_charts ?? new CraneLoadChartRow[0])
                .Where(c => !string.IsNullOrEmpty(c.equipment)).GroupBy(c => c.equipment)
                .ToDictionary(g => g.Key, g => g.OrderBy(c => c.radius_m).ToArray());
            // Goliath and gantry rails belong to GantryCraneView; this view builds the slewing jib cranes.
            foreach (var row in rows.Where(r => !string.IsNullOrEmpty(r.equipment) && r.equipment_type != "골리앗 크레인" &&
                                                r.equipment_type != "갠트리 크레인"))
            {
                var a = new Vector2(row.rail_x0, row.rail_y0);
                var b = new Vector2(row.rail_x1, row.rail_y1);
                if ((b - a).sqrMagnitude < 1f)
                {
                    continue;
                }

                BuildRails(row, a, b);
                var registered = booms.TryGetValue(row.equipment, out var k);
                var boom = registered ? k : CraneKinematicsRow.Default;
                var crane = Build(row, boom);
                crane.Target.CraneHazard = WalkHazardShape.Circle(RailCraneMotion.DangerRadius(boom), !registered);
                crane.Chart = charts.TryGetValue(row.equipment, out var chart) ? chart : new CraneLoadChartRow[0];
                _cranes.Add(crane);
            }

            Update();
            Rebuilt?.Invoke();
        }

        private void BuildRails(CraneRailRow row, Vector2 a, Vector2 b)
        {
            var along = (b - a).normalized;
            var across = new Vector2(-along.y, along.x);
            var length = (b - a).magnitude + 2f * RailOverrun;
            var centre = (a + b) / 2f;
            var heading = Mathf.Atan2(along.y, along.x) * Mathf.Rad2Deg;
            foreach (var side in new[] { -0.5f, 0.5f })
            {
                var c = centre + across * (side * row.gauge_m);
                var rail = MetricBox.Create("Rail " + row.equipment, transform, new Vector3(length, 0.18f, 0.32f), _palette.Get("rail"));
                rail.transform.SetPositionAndRotation(ShipCoordinates.YardToUnity(c.x, c.y, YardLayout.QuayLevel + 0.09f),
                    ShipCoordinates.HeadingToUnity(heading));
            }
        }

        /// <summary>
        /// Crane parts (Unity axes; root on the rail centre line at quay level, x along the rail): portal legs to the deck
        /// under the slewing ring, the superstructure on the ring (x toward the boom), the boom from its foot pin (x along
        /// the boom), and hook, wire and load placed in world space every frame.
        /// </summary>
        private Crane Build(CraneRailRow row, CraneKinematicsRow k)
        {
            var root = new GameObject(row.equipment).transform;
            root.SetParent(_craneDisplay != null ? _craneDisplay : transform, false);
            var g = Mathf.Max(4f, row.gauge_m) / 2f;
            var deck = Mathf.Max(6f, k.pivot_height_m - 7f);
            var yellow = "crane_yellow";
            var dark = "crane_dark";

            // Portal: four legs on the rails, cross girders at the top, bogies at the feet.
            foreach (var sx in new[] { -1f, 1f })
            {
                foreach (var sz in new[] { -1f, 1f })
                {
                    Box("Leg", root, new Vector3(sx * (g - 0.6f), deck / 2f, sz * (g - 0.6f)), new Vector3(1.1f, deck, 1.1f), yellow);
                    Box("Bogie", root, new Vector3(sx * (g - 0.6f), 0.6f, sz * g), new Vector3(3.2f, 1.2f, 1.4f), dark);
                }

                Box("Girder", root, new Vector3(sx * (g - 0.6f), deck - 0.8f, 0f), new Vector3(1.2f, 1.6f, 2f * g), yellow);
                Box("Brace", root, new Vector3(0f, deck - 0.8f, sx * (g - 0.6f)), new Vector3(2f * g, 1.6f, 1.2f), yellow);
            }

            Box("Deck", root, new Vector3(0f, deck + 0.3f, 0f), new Vector3(2f * g, 0.6f, 2f * g), dark);
            Box("Tower", root, new Vector3(0f, deck + 3f, 0f), new Vector3(4f, 5f, 4f), yellow);

            // Slewing superstructure on the ring; its +x points along the boom.
            var slew = new GameObject("Slew").transform;
            slew.SetParent(root, false);
            slew.localPosition = new Vector3(0f, k.pivot_height_m - 1.5f, 0f);
            Box("Ring", slew, Vector3.zero, new Vector3(6f, 0.8f, 6f), dark);
            Box("House", slew, new Vector3(-3.5f, 2.4f, 0f), new Vector3(9f, 4f, 5.5f), yellow);
            Box("Counterweight", slew, new Vector3(-8.5f, 1.8f, 0f), new Vector3(3f, 3.4f, 5.2f), "concrete");
            Box("Cab", slew, new Vector3(2.4f, 2.2f, 2.6f), new Vector3(2.6f, 2.6f, 2.2f), "glass");
            Box("A-frame", slew, new Vector3(-1.5f, 7f, 0f), new Vector3(1f, 9f, 4.6f), yellow);

            // Boom: foot pin at the front of the superstructure; local +x runs out along the boom.
            var pivot = new GameObject("BoomPivot").transform;
            pivot.SetParent(slew, false);
            pivot.localPosition = new Vector3(1.5f, 1.5f, 0f);
            Box("Boom", pivot, new Vector3(k.boom_m / 2f, 0f, 0f), new Vector3(k.boom_m, 1.4f, 1.8f), yellow);
            Box("Chord", pivot, new Vector3(k.boom_m / 2f, 0.9f, 0f), new Vector3(k.boom_m * 0.96f, 0.25f, 0.25f), dark);
            Box("Sheave", pivot, new Vector3(k.boom_m, 0f, 0f), new Vector3(1.6f, 1.6f, 2.2f), dark);

            var hook = Box("Hook", root, Vector3.zero, new Vector3(1.6f, 2.2f, 1.2f), yellow);
            var wire = Box("Wire", root, Vector3.zero, Vector3.one, "wire");
            // Outfitting material on the hook: a pallet with a bundle of pipes (small cranes do not lift hull blocks).
            var load = new GameObject("Load").transform;
            load.SetParent(root, false);
            Box("Pallet", load, new Vector3(0f, -1.6f, 0f), new Vector3(6f, 0.4f, 2.4f), "steel");
            for (var i = 0; i < 6; i++)
            {
                Box("Pipe", load, new Vector3(0f, -1.1f + (i / 3) * 0.6f, -0.8f + (i % 3) * 0.8f), new Vector3(5.6f, 0.55f, 0.55f), "pipe");
            }

            Box("Sling", load, new Vector3(0f, 0.2f, 0f), new Vector3(0.1f, 2.4f, 0.1f), "wire");
            load.gameObject.SetActive(false);

            var box = root.gameObject.AddComponent<BoxCollider>();   // a box for hover picks: no mesh collider cooking
            box.center = new Vector3(0f, k.pivot_height_m / 2f, 0f);
            box.size = new Vector3(2f * g, k.pivot_height_m, 2f * g);
            var target = HoverTarget.Attach(root.gameObject, "장비", string.IsNullOrEmpty(row.name) ? row.equipment : row.name, Detail(row, k, null))
                .Identify(TargetCategory.Crane, row.equipment, null, row.station);
            return new Crane { Row = row, Boom = k, Root = root, Slew = slew, BoomPivot = pivot, Hook = hook, Wire = wire, Load = load, Target = target };
        }

        private Transform Box(string name, Transform parent, Vector3 localPosition, Vector3 size, string paint)
        {
            var part = MetricBox.Create(name, parent, size, _palette.Get(paint));
            part.transform.localPosition = localPosition;
            return part.transform;
        }

        private static string Detail(CraneRailRow row, CraneKinematicsRow k, string state)
        {
            var rating = row.capacity_t > 0 ? " " + row.capacity_t.ToString("0") + " t" : string.Empty;
            return (row.equipment_type ?? "크레인") + rating + " · ERP " + row.equipment + (string.IsNullOrEmpty(row.station) ? string.Empty : " · " + row.station) +
                   " · 붐 " + k.boom_m.ToString("0") + " m" + (state != null ? " · " + state : string.Empty) +
                   " · 클릭하면 위험반경·반경별 정격하중";
        }

        private void Update()
        {
            if (_cranes.Count == 0)
            {
                return;
            }

            PollLive();
            var label = Time.unscaledTime >= _nextLabel;
            var showCargo = _displayLayers?.IsVisible(YardDisplayLayer.Blocks) ?? true;
            var sync = YardSimSync.Active;
            var blend = Mathf.Clamp01((Time.unscaledTime - _liveAt) / PollSeconds);
            foreach (var crane in _cranes)
            {
                var row = crane.Row;
                float position, slew, luff, hookZ;
                bool loaded;
                string state;
                YardSimEquipment plan = null;
                if (sync != null && sync.TryGetJib(row.equipment, out var replay))
                {
                    plan = replay;   // shared ERP replay
                }
                else if (_live.TryGetValue(row.equipment, out var now))
                {
                    plan = _livePrevious.TryGetValue(row.equipment, out var before) ? Blend(before, now, blend) : now;
                }

                if (plan != null)
                {
                    position = plan.rail_pos;
                    slew = plan.slew_deg;
                    luff = plan.luff_deg;
                    hookZ = plan.hook.z - YardLayout.QuayLevel;
                    loaded = plan.load_t > 0f;
                    state = plan.state + (string.IsNullOrEmpty(plan.work) ? string.Empty : " · " + plan.work) +
                            (plan.source == "estimated" && !string.IsNullOrEmpty(plan.work) ? " · 인양 지점 추정" :
                             plan.source == "plan_sim" ? " · 계획 기반 모의" : string.Empty);
                }
                else
                {
                    // No ERP plan to follow: parked, boom up, nothing on the hook.
                    position = RailCraneMotion.Home(row.equipment);
                    slew = row.jib_heading_deg;
                    luff = crane.Boom.rest_luff_deg;
                    hookZ = RailCraneMotion.TopHook(crane.Boom);
                    loaded = false;
                    state = "대기 · ERP 작업 계획 미연결";
                }

                Apply(crane, position, slew, luff, hookZ, loaded && showCargo);
                crane.LoadT = loaded && plan != null ? plan.load_t : 0f;
                crane.Work = state;
                if (label && crane.State != state)
                {
                    crane.State = state;
                    crane.Target.Detail = Detail(row, crane.Boom, state + " · 선회 " + slew.ToString("0") + "° · 붐 " + luff.ToString("0") + "°");
                }
            }

            if (label)
            {
                _nextLabel = Time.unscaledTime + 1f;
            }
        }

        /// <summary>Reads the ERP crane plan now about once a second while live ERP data is loaded.</summary>
        private void PollLive()
        {
            if (_polling || Time.unscaledTime < _nextPoll || _source == null || _source.Status != YardDataStatus.Erp ||
                string.IsNullOrEmpty(YardDataSource.ErpUrl))
            {
                return;
            }

            _nextPoll = Time.unscaledTime + PollSeconds;
            StartCoroutine(Poll());
        }

        private IEnumerator Poll()
        {
            _polling = true;
            using (var request = UnityWebRequest.Get(YardDataSource.ErpUrl + "/api/crane_activity"))
            {
                var token = YardDataSource.Token;
                if (!string.IsNullOrEmpty(token)) request.SetRequestHeader("Authorization", "Bearer " + token);
                request.timeout = 6;
                yield return request.SendWebRequest();
                _polling = false;
                if (request.result != UnityWebRequest.Result.Success)
                {
                    if (request.responseCode == 403 || request.responseCode == 404) _nextPoll = Time.unscaledTime + 60f;   // no plan: parked
                    yield break;
                }

                var activity = CraneActivity.Parse(request.downloadHandler.text);
                _livePrevious.Clear();
                foreach (var pair in _live) _livePrevious[pair.Key] = pair.Value;
                _live.Clear();
                foreach (var crane in activity.cranes.Where(c => !string.IsNullOrEmpty(c.id))) _live[crane.id] = crane;
                _liveAt = Time.unscaledTime;
            }
        }

        private static YardSimEquipment Blend(YardSimEquipment a, YardSimEquipment b, float t)
        {
            if (t >= 1f || a.state == null) return b;
            return new YardSimEquipment
            {
                id = b.id, state = b.state, work = b.work, source = b.source, load_t = b.load_t, rail_pos = Mathf.Lerp(a.rail_pos, b.rail_pos, t),
                slew_deg = Mathf.LerpAngle(a.slew_deg, b.slew_deg, t), luff_deg = Mathf.Lerp(a.luff_deg, b.luff_deg, t),
                hook = new YardSimHook { x = b.hook.x, y = b.hook.y, z = Mathf.Lerp(a.hook.z, b.hook.z, t) },
            };
        }

        /// <summary>Places a crane: on the rail, superstructure turned to the slew, boom at its angle, hook below the tip.</summary>
        private static void Apply(Crane crane, float position, float slew, float luff, float hookAboveQuay, bool loaded)
        {
            var row = crane.Row;
            var x = Mathf.Lerp(row.rail_x0, row.rail_x1, position);
            var y = Mathf.Lerp(row.rail_y0, row.rail_y1, position);
            var railHeading = Mathf.Atan2(row.rail_y1 - row.rail_y0, row.rail_x1 - row.rail_x0) * Mathf.Rad2Deg;
            crane.Root.SetPositionAndRotation(ShipCoordinates.YardToUnity(x, y, YardLayout.QuayLevel), ShipCoordinates.HeadingToUnity(railHeading));
            crane.Slew.rotation = ShipCoordinates.HeadingToUnity(slew);
            crane.BoomPivot.localRotation = Quaternion.Euler(0f, 0f, luff);

            var tip = crane.BoomPivot.TransformPoint(new Vector3(crane.Boom.boom_m, -0.9f, 0f));
            var hookY = Mathf.Min(YardLayout.QuayLevel + hookAboveQuay + 1.1f, tip.y - 3f);
            var hook = new Vector3(tip.x, hookY, tip.z);
            crane.Hook.SetPositionAndRotation(hook, crane.Slew.rotation);
            var length = Mathf.Max(0.01f, tip.y - hookY);
            crane.Wire.SetPositionAndRotation(new Vector3(tip.x, (tip.y + hookY) / 2f, tip.z), Quaternion.identity);
            crane.Wire.localScale = new Vector3(0.12f, length, 0.12f);
            crane.Load.gameObject.SetActive(loaded);
            if (loaded)
            {
                crane.Load.SetPositionAndRotation(hook + Vector3.down * 4f, crane.Slew.rotation);
            }
        }
    }
}
