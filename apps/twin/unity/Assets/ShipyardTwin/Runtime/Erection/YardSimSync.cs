using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using GrowthLab.Core;
using GrowthLab.UI;
using UnityEngine;
using UnityEngine.Networking;

namespace ShipyardTwin
{
    /// <summary>
    /// Follows the shared ERP yard replay (GET/POST {ERP}/api/yard_sim, apps/erp/yardsim.py) that the Portal equipment map
    /// reads too: the ERP holds the clock and computes every block, crane and vehicle position; this view only shows it.
    /// <list type="bullet">
    /// <item>Polls the state every <see cref="PollSeconds"/> while live ERP data is loaded; between two replies positions are
    /// interpolated (a seek, restart or loop snaps).</item>
    /// <item>Selecting a hull opens its scenario (keeping the clock when it is already open); when another screen opens a
    /// different hull, this view selects it.</item>
    /// <item>The selected hull's blocks are placed by <see cref="ShipView.ApplySynced"/>, the dock crane takes the replayed
    /// rail position and hook, ERP vehicles are drawn as transporters with their ID and state, and the decorative scripted
    /// traffic is hidden. Play, pause, speed, seek and restart of the panels go to the shared session.</item>
    /// </list>
    /// Without ERP (snapshot, failure) the local playback stays as before.
    /// </summary>
    public sealed class YardSimSync : MonoBehaviour, ICoreConsumer
    {
        public const float PollSeconds = 0.2f;
        private const float OpenGraceSeconds = 6f;

        public static YardSimSync Active { get; private set; }

        private ShipView _ship;
        private YardDataSource _source;
        private ShipPalette _palette;
        private ShipyardTwinSettings _settings;
        private WorldLabels _labels;
        private YardScenery _scenery;
        private Transform _vehicleDisplay;
        private YardDisplayLayers _displayLayers;

        private YardSimState _previous, _current;
        private float _receivedAt, _nextPoll, _openedAt = -100f;
        private bool _inFlight, _speedInFlight;
        private string _queuedSpeed;
        private int _failures;
        private string _openedHull, _seenScenario;
        private bool _scenarioSeen;
        private readonly Dictionary<string, Vehicle> _vehicles = new Dictionary<string, Vehicle>();
        private readonly Dictionary<string, YardSimEquipment> _jibs = new Dictionary<string, YardSimEquipment>();

        /// <summary>
        /// The replayed pose of a rail jib crane this frame (blended), when the shared replay reports it: the rail crane
        /// view then shows it instead of the 120 s rule.
        /// </summary>
        public bool TryGetJib(string id, out YardSimEquipment crane)
        {
            crane = null;
            return Synced && _jibs.TryGetValue(id, out crane);
        }
        private Transform _craneTag;
        private StatusBadge _craneBadge;

        /// <summary>The shared replay is being shown for the selected hull.</summary>
        public bool Synced => _ship != null && _ship.Synced;

        /// <summary>One line for the panels: connection and data sources.</summary>
        public string Status { get; private set; } = "공유 재생 대기";

        /// <summary>What is going on now in the shared replay (blocks being moved or lifted, with their equipment).</summary>
        public string Activity => _current == null ? string.Empty
            : _current.activity.Length == 0 ? "진행 중인 운반·인양 없음" : string.Join(" · ", _current.activity.Take(3)) +
                                                                       (_current.activity.Length > 3 ? " 외 " + (_current.activity.Length - 3) : string.Empty);

        public YardSimState State => _current;

        public void Construct(CoreContext context)
        {
            if (_ship != null || !context.Services.TryGet(out _source) || !context.Services.TryGet(out _palette))
            {
                return;
            }

            _settings = context.Settings.Get<ShipyardTwinSettings>();
            _ship = FindFirstObjectByType<ShipView>();
            _labels = FindFirstObjectByType<WorldLabels>();
            _scenery = FindFirstObjectByType<YardScenery>();
            context.Services.TryGet(out _displayLayers);
            _vehicleDisplay = _displayLayers?.ParentFor(transform, YardDisplayLayer.Vehicles) ?? transform;
            Active = this;
            if (_ship != null) _ship.Changed += OnShipChanged;
        }

        private void OnDestroy()
        {
            if (_ship != null) _ship.Changed -= OnShipChanged;
            if (Active == this) Active = null;
        }

        private bool Live => _source != null && _source.Status == YardDataStatus.Erp && !string.IsNullOrEmpty(YardDataSource.ErpUrl);

        private void Update()
        {
            if (_ship == null)
            {
                return;
            }

            if (!Live)
            {
                Detach(_source == null || _source.Status != YardDataStatus.Erp ? "로컬 재생 · ERP 미연결" : Status);
                return;
            }

            if (!_inFlight && Time.unscaledTime >= _nextPoll)
            {
                StartCoroutine(Send("GET", null));
            }

            ShowFrame();
        }

        // ------------------------------------------------------------------ selection and session

        private void OnShipChanged()
        {
            if (!Live || _ship.Selected == null)
            {
                return;
            }

            var hull = _ship.Selected.Id;
            if (_current != null && _current.HasScenario && _current.scenario.hull == hull)
            {
                Attach();
            }
            else if (_openedHull != hull || Time.unscaledTime - _openedAt > OpenGraceSeconds)
            {
                Open(hull);
            }
        }

        private void Open(string hull)
        {
            _openedHull = hull;
            _openedAt = Time.unscaledTime;
            var days = (_settings?.DaysPerSecond ?? 4f).ToString(System.Globalization.CultureInfo.InvariantCulture);
            StartCoroutine(Send("POST", "{\"action\":\"open\",\"hull\":\"" + hull + "\",\"days_per_second\":" + days + "}"));
        }

        private void Control(string action, string value)
        {
            if (action == "speed")
            {
                _queuedSpeed = value;
                if (!_speedInFlight) StartCoroutine(SendSpeed());
                return;
            }

            var body = value == null
                ? "{\"action\":\"" + action + "\"}"
                : "{\"action\":\"" + action + "\",\"value\":\"" + value + "\"}";
            StartCoroutine(Send("POST", body));
        }

        private IEnumerator SendSpeed()
        {
            _speedInFlight = true;
            var hull = _ship.Selected?.Id;
            while (_queuedSpeed != null && hull == _ship.Selected?.Id)
            {
                var value = _queuedSpeed;
                _queuedSpeed = null;
                yield return Send("POST", "{\"action\":\"speed\",\"value\":" + value + "}");
            }

            _queuedSpeed = null;
            _speedInFlight = false;
        }

        private IEnumerator Send(string method, string body)
        {
            var polling = method == "GET";
            if (polling)
            {
                _inFlight = true;
                _nextPoll = Time.unscaledTime + PollSeconds;
            }

            using (var request = new UnityWebRequest(YardDataSource.ErpUrl + "/api/yard_sim", method))
            {
                request.downloadHandler = new DownloadHandlerBuffer();
                if (body != null)
                {
                    request.uploadHandler = new UploadHandlerRaw(Encoding.UTF8.GetBytes(body));
                    request.SetRequestHeader("Content-Type", "application/json");
                }

                var token = YardDataSource.Token;
                if (!string.IsNullOrEmpty(token)) request.SetRequestHeader("Authorization", "Bearer " + token);
                request.timeout = 8;
                yield return request.SendWebRequest();
                if (polling) _inFlight = false;
                if (request.result == UnityWebRequest.Result.Success)
                {
                    _failures = 0;
                    Receive(YardSimState.Parse(request.downloadHandler.text));
                }
                else
                {
                    // Never echo request details beyond the status (the token is in the headers).
                    var code = request.responseCode;
                    if (polling && ++_failures >= 5)
                    {
                        Detach(code == 403 ? "공유 재생 권한 없음 · 로컬 재생" : code == 404 ? "ERP에 공유 재생 없음 · 로컬 재생" : "공유 재생 연결 끊김 · 로컬 재생");
                    }
                    else if (!polling)
                    {
                        Status = "공유 재생 요청 실패(HTTP " + code + ")";
                        if (method == "POST" && body != null && body.Contains("\"open\"")) _openedHull = null;
                    }
                }
            }
        }

        private void Receive(YardSimState state)
        {
            var reset = _current == null || !state.HasScenario || _current.scenario.id != state.scenario.id ||
                        _current.clock.version != state.clock.version || _current.clock.cycle != state.clock.cycle;
            _previous = reset ? null : _current;
            _current = state;
            _receivedAt = Time.unscaledTime;
            if (!state.HasScenario)
            {
                Status = "공유 재생 시나리오 없음";
                _scenarioSeen = true;   // a scenario opened from now on is another screen's choice
                if (_ship.Selected != null && (_openedHull != _ship.Selected.Id || Time.unscaledTime - _openedAt > OpenGraceSeconds)) Open(_ship.Selected.Id);
                return;
            }

            var hull = state.scenario.hull;
            // A scenario another screen opens later is followed; one already open when this view starts is not (the yard
            // calendar stays until a hull is picked here).
            var changed = _scenarioSeen && _seenScenario != state.scenario.id;
            _seenScenario = state.scenario.id;
            _scenarioSeen = true;
            var waitingForOwnOpen = _openedHull != null && _openedHull != hull && Time.unscaledTime - _openedAt < OpenGraceSeconds;
            if (_ship.Selected?.Id != hull && changed && !waitingForOwnOpen && _ship.Model?.Hull(hull) != null)
            {
                _openedHull = hull;          // opened by another screen: follow it
                _openedAt = Time.unscaledTime;
                _ship.Select(hull);
            }

            if (_ship.Selected?.Id == hull) Attach();
            Status = "공유 재생 " + state.scenario.id + (state.clock.work ? " · 작업 구간(분 단위)" : string.Empty) +
                     " · 반복 " + (state.clock.cycle + 1) + "회차";
        }

        private void Attach()
        {
            if (_ship.Playback == null)
            {
                return;
            }

            _ship.SetSynced(true);
            _ship.Playback.Remote = Control;
            _scenery?.ShowTraffic(false);
        }

        private void Detach(string status)
        {
            Status = status;
            if (_ship.Synced) _ship.SetSynced(false);
            if (_vehicles.Count > 0 || _craneTag != null)
            {
                foreach (var vehicle in _vehicles.Values) vehicle.Root.gameObject.SetActive(false);
                if (_craneTag != null) _craneTag.gameObject.SetActive(false);
                _scenery?.ShowTraffic(true);
            }
        }

        // ------------------------------------------------------------------ frame

        /// <summary>Blends the last two replies (one poll behind) and hands the result to the scene.</summary>
        private void ShowFrame()
        {
            if (!_ship.Synced || _current == null || !_current.HasScenario || _ship.Selected?.Id != _current.scenario.hull)
            {
                return;
            }

            var t = _previous == null ? 1f : Mathf.Clamp01((Time.unscaledTime - _receivedAt) / PollSeconds);
            var time = _current.Time;
            if (_previous != null && _previous.Time != DateTime.MinValue)
            {
                time = _previous.Time + TimeSpan.FromTicks((long)((_current.Time - _previous.Time).Ticks * (double)t));
            }

            var before = _previous?.blocks.ToDictionary(b => b.id) ?? new Dictionary<string, YardSimBlock>();
            var blocks = _current.blocks.Select(b => before.TryGetValue(b.id, out var p) ? Blend(p, b, t) : b).ToList();
            var equipmentBefore = _previous?.equipment.ToDictionary(e => e.id) ?? new Dictionary<string, YardSimEquipment>();
            var equipment = _current.equipment.Select(e => equipmentBefore.TryGetValue(e.id, out var p) ? Blend(p, e, t) : e).ToList();
            var crane = equipment.Where(e => e.IsCrane && !e.IsJib && e.Placed).OrderBy(e => string.IsNullOrEmpty(e.block) ? 1 : 0).FirstOrDefault();
            _jibs.Clear();
            foreach (var jib in equipment.Where(e => e.IsJib && e.Placed)) _jibs[jib.id] = jib;
            _ship.ApplySynced(time, blocks, crane);
            var schedule = _ship.Schedule;
            if (schedule != null)
            {
                _ship.Playback.SyncFrom((float)(time - schedule.Start).TotalDays, _current.clock.playing, _speedInFlight ? _ship.Playback.SpeedMultiplier : Mathf.Clamp(_current.clock.speed, 0.05f, 32f));
            }

            foreach (var item in equipment.Where(e => !e.IsCrane))
            {
                ShowVehicle(item);
            }

            foreach (var gone in _vehicles.Keys.Except(equipment.Where(e => !e.IsCrane).Select(e => e.id)).ToList())
            {
                _vehicles[gone].Root.gameObject.SetActive(false);
            }

            ShowCraneTag(crane);
        }

        private static YardSimBlock Blend(YardSimBlock a, YardSimBlock b, float t)
        {
            if (!a.Placed || !b.Placed || t >= 1f)
            {
                return b;
            }

            return new YardSimBlock
            {
                id = b.id, state = b.state, location = b.location, vehicle = b.vehicle, crane = b.crane, source = b.source, basis = b.basis,
                plan_day = b.plan_day, step = b.step, x = Mathf.Lerp(a.x, b.x, t), y = Mathf.Lerp(a.y, b.y, t), z = Mathf.Lerp(a.z, b.z, t),
                heading_deg = Mathf.LerpAngle(a.heading_deg, b.heading_deg, t),
            };
        }

        private static YardSimEquipment Blend(YardSimEquipment a, YardSimEquipment b, float t)
        {
            if (!a.Placed || !b.Placed || t >= 1f)
            {
                return b;
            }

            return new YardSimEquipment
            {
                id = b.id, name = b.name, kind = b.kind, type = b.type, state = b.state, block = b.block, source = b.source, basis = b.basis,
                job_basis = b.job_basis, station = b.station, loaded = b.loaded, rail = b.rail,
                x = Mathf.Lerp(a.x, b.x, t), y = Mathf.Lerp(a.y, b.y, t), heading_deg = Mathf.LerpAngle(a.heading_deg, b.heading_deg, t),
                rail_pos = Mathf.Lerp(a.rail_pos, b.rail_pos, t), trolley_m = Mathf.Lerp(a.trolley_m, b.trolley_m, t),
                crane_type = b.crane_type, work = b.work, kinematics = b.kinematics, boom_m = b.boom_m, pivot_height_m = b.pivot_height_m,
                load_t = b.load_t, slew_deg = Mathf.LerpAngle(a.slew_deg, b.slew_deg, t), luff_deg = Mathf.Lerp(a.luff_deg, b.luff_deg, t),
                radius_m = Mathf.Lerp(a.radius_m, b.radius_m, t), tip_z = Mathf.Lerp(a.tip_z, b.tip_z, t),
                hook = new YardSimHook { x = Mathf.Lerp(a.hook.x, b.hook.x, t), y = Mathf.Lerp(a.hook.y, b.hook.y, t), z = Mathf.Lerp(a.hook.z, b.hook.z, t) },
            };
        }

        private sealed class Vehicle
        {
            public Transform Root;
            public GameObject Empty, Loaded;
            public StatusBadge Badge;
        }

        /// <summary>ERP vehicle (transporter model, empty or loaded) at the replayed pose, labelled with its ID and state.</summary>
        private void ShowVehicle(YardSimEquipment item)
        {
            if (!_vehicles.TryGetValue(item.id, out var vehicle))
            {
                var root = new GameObject("ERP vehicle " + item.id).transform;
                root.SetParent(_vehicleDisplay, false);
                PropLibrary.TryCreate("transporter", _palette, root, out var empty);
                PropLibrary.TryCreate("transporter_loaded", _palette, root, out var loaded);
                if (empty == null)
                {
                    empty = GameObject.CreatePrimitive(PrimitiveType.Cube);
                    Destroy(empty.GetComponent<Collider>());
                    empty.transform.SetParent(root, false);
                    empty.transform.localScale = new Vector3(18f, 2f, 6f);
                }

                var target = HoverTarget.Attach(root.gameObject, "장비", item.id, item.name)
                    .Identify(TargetCategory.Vehicle, item.id, place: item.station);
                target.IsHeavyEquipment = item.type is "트랜스포터" or "지게차";
                vehicle = new Vehicle { Root = root, Empty = empty, Loaded = loaded };
                if (_labels != null)
                {
                    vehicle.Badge = _labels.Track(root, new StatusBadge(StatusLevel.Normal, item.id), Vector3.up * 9f);
                    vehicle.Badge.AddToClassList("tw-vehicle-label");
                }
                _vehicles[item.id] = vehicle;
            }

            var placed = item.Placed;
            vehicle.Root.gameObject.SetActive(placed);
            if (!placed)
            {
                return;
            }

            vehicle.Root.SetPositionAndRotation(ShipCoordinates.YardToUnity(item.x, item.y, YardLayout.QuayLevel),
                ShipCoordinates.HeadingToUnity(item.heading_deg));
            var carrying = item.loaded && vehicle.Loaded != null;
            var showCargo = carrying && (_displayLayers?.IsVisible(YardDisplayLayer.Blocks) ?? true);
            vehicle.Empty.SetActive(!showCargo);
            if (vehicle.Loaded != null) vehicle.Loaded.SetActive(showCargo);
            if (vehicle.Badge != null)
            {
                vehicle.Badge.text = item.id + " · " + item.state + (item.source == "estimated" ? " · 추정 경로" : string.Empty);
                vehicle.Badge.Level = item.state == "대기" ? StatusLevel.Offline : item.loaded ? StatusLevel.Warning : StatusLevel.Normal;
            }
        }

        /// <summary>Label over the hook: crane ID, its state and the source of the motion.</summary>
        private void ShowCraneTag(YardSimEquipment crane)
        {
            if (crane == null || _labels == null)
            {
                if (_craneTag != null) _craneTag.gameObject.SetActive(false);
                return;
            }

            if (_craneTag == null)
            {
                _craneTag = new GameObject("ERP crane tag").transform;
                _craneTag.SetParent(transform, false);
                _craneBadge = _labels.Track(_craneTag, new StatusBadge(StatusLevel.Normal, crane.id), Vector3.zero);
                _craneBadge.AddToClassList("tw-crane-replay-label");
            }

            _craneTag.gameObject.SetActive(true);
            _craneTag.position = ShipCoordinates.YardToUnity(crane.hook.x, crane.hook.y, crane.hook.z + 10f);
            _craneBadge.text = crane.id + " · " + crane.state + (crane.state == "대기" ? string.Empty :
                crane.source == "plan_sim" ? " · 계획 기반 모의" : crane.source == "estimated" ? " · 추정" : string.Empty);
            _craneBadge.Level = crane.state == "대기" ? StatusLevel.Offline : StatusLevel.Warning;
        }
    }
}
