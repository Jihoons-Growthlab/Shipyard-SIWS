using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using GrowthLab.Core;
using GrowthLab.Network;
using UnityEngine;

namespace ShipyardTwin
{
    public enum YardDataStatus
    {
        /// <summary>Showing the snapshot while the ERP query runs.</summary>
        Loading,

        /// <summary>Live ERP data.</summary>
        Erp,

        /// <summary>No ERP configured: the exported snapshot.</summary>
        Snapshot,

        /// <summary>The ERP query failed: the snapshot (if any) is shown.</summary>
        Failed,
    }

    /// <summary>
    /// Yard plan provider. Startup is never blocked on the network: the exported snapshot
    /// (Resources/ShipyardTwin/yard.snapshot.json, pipeline/export_yard.py) is parsed in Configure and the scene is
    /// built from it; then, when SHIPYARD_API_URL is set, the same ERP routes are fetched once (login with
    /// SHIPYARD_API_TOKEN or SHIPYARD_API_USER/PASSWORD; secrets come from the environment only and are never logged),
    /// parsed off the main thread and handed over as a new <see cref="YardModel"/> through <see cref="Changed"/> on the
    /// main thread. <see cref="Refresh"/> repeats the query. Nothing here runs per frame.
    /// </summary>
    public sealed class YardDataSource
    {
        public static readonly string[] Routes =
        {
            "hulls", "vessel_details", "hull_geometry", "hull_placements", "erection_timeline", "block_stock_plans",
            "location_geo", "yard_sites",
        };

        private static readonly string[] PlanningRoutes =
        {
            "shift_points", "berth_assignments", "crane_assignments", "quay_shifts", "moves", "dock_bookings", "crane_rails", "crane_kinematics",
            "crane_load_charts", "hull_overview", "block_stages", "center_locations", "equipment_specs",
        };

        private readonly ILog _log;
        private readonly DateTime _asOf;
        private int _generation;

        public YardDataSource(YardModel snapshot, DateTime asOf, ILog log)
        {
            Model = snapshot;
            _asOf = asOf;
            _log = log;
            HasSnapshot = snapshot != null && snapshot.Hulls.Count > 0;
            Status = YardDataStatus.Snapshot;
            Detail = HasSnapshot ? "내보낸 스냅샷 " + Stamp(snapshot.Source?.exported_at) : "스냅샷 없음";
        }

        public YardModel Model { get; private set; }

        /// <summary>Plan day the yard is shown at.</summary>
        public DateTime AsOf => _asOf;

        public bool HasSnapshot { get; }

        public YardDataStatus Status { get; private set; }

        /// <summary>One line for the panel: source, time or failure reason.</summary>
        public string Detail { get; private set; }

        /// <summary>Raised on the main thread when the model or the status changes.</summary>
        public event Action Changed;

        public static string ErpUrl => WebHost.ErpUrl;

        /// <summary>
        /// Bearer token for ERP calls: the configured one (environment, browser tab), else the one this app obtained by
        /// logging in with SHIPYARD_API_USER/PASSWORD. Kept in memory only, never logged.
        /// </summary>
        public static string Token => !string.IsNullOrEmpty(WebHost.Token) ? WebHost.Token : _loginToken;

        private static string _loginToken;

        public async void Refresh()
        {
            var url = ErpUrl;
            if (string.IsNullOrEmpty(url))
            {
                SetStatus(YardDataStatus.Snapshot, "ERP 미설정(SHIPYARD_API_URL) · " + (HasSnapshot ? "스냅샷 " + Stamp(Model?.Source?.exported_at) : "스냅샷 없음"));
                return;
            }

            var generation = ++_generation;
            SetStatus(YardDataStatus.Loading, "ERP 조회 중 · " + Host(url));
            var started = Time.realtimeSinceStartup;
            try
            {
                var snapshot = await Fetch(url, CancellationToken.None);
                var model = await WebHost.Offload(() => YardModel.Build(snapshot, _asOf));
                if (generation != _generation)
                {
                    return;
                }

                if (model.Hulls.Count == 0)
                {
                    throw new InvalidOperationException("이 야드의 도크에 배치된 호선이 ERP에 없습니다");
                }

                Model = model;
                _log.Info("ShipyardTwin", "ERP yard plan: " + model.Hulls.Count + " hulls, " + model.Blocks.Count + " blocks in " +
                                          (Time.realtimeSinceStartup - started).ToString("0.00") + " s");
                SetStatus(YardDataStatus.Erp, "ERP " + Host(url) + " · " + DateTime.Now.ToString("HH:mm") + " 조회");
            }
            catch (Exception exception)
            {
                if (generation != _generation)
                {
                    return;
                }

                // Never echo request details: the URL is fine, credentials are not part of the message.
                _log.Warning("ShipyardTwin", "ERP query failed: " + exception.GetType().Name + ": " + exception.Message);
                SetStatus(YardDataStatus.Failed, "ERP 조회 실패(" + Reason(exception) + ") · " +
                                                 (HasSnapshot ? "스냅샷 " + Stamp(Model?.Source?.exported_at) + " 표시" : "표시할 데이터 없음"));
            }
        }

        private void SetStatus(YardDataStatus status, string detail)
        {
            Status = status;
            Detail = detail;
            Changed?.Invoke();
        }

        /// <summary>Logs in when needed and reads every route (unpaged: whole arrays) into one snapshot.</summary>
        public static async Task<YardSnapshot> Fetch(string url, CancellationToken cancel)
        {
            var client = new RestClient(new UnityWebRequestTransport(), new RestClientOptions { Timeout = TimeSpan.FromSeconds(15), MaxRetries = 1 });
            var token = WebHost.Token;
            var user = Environment.GetEnvironmentVariable("SHIPYARD_API_USER");
            var password = Environment.GetEnvironmentVariable("SHIPYARD_API_PASSWORD");
            if (string.IsNullOrEmpty(token) && !string.IsNullOrEmpty(user) && !string.IsNullOrEmpty(password))
            {
                var login = new HttpRequest(HttpMethod.Post, url + "/api/login")
                {
                    Body = JsonUtility.ToJson(new LoginBody { id = user, password = password }),
                };
                var response = await client.SendAsync(login, cancel);
                token = JsonUtility.FromJson<LoginReply>(response.Body)?.token;
                _loginToken = token;
            }

            var bodies = new Dictionary<string, string>();
            foreach (var route in System.Linq.Enumerable.Concat(Routes, PlanningRoutes))
            {
                var request = new HttpRequest(HttpMethod.Get, url + "/api/" + route);
                if (!string.IsNullOrEmpty(token))
                {
                    request.Headers["Authorization"] = "Bearer " + token;
                }

                try
                {
                    bodies[route] = (await client.SendAsync(request, cancel)).Body;
                }
                catch (NetworkException error) when (Array.IndexOf(PlanningRoutes, route) >= 0 &&
                    (error.StatusCode == 403 || error.StatusCode == 404))
                {
                    // Existing ERP roles and older servers can still load the yard without these tables.
                }
            }

            return await WebHost.Offload(() => Assemble(bodies, url));
        }

        /// <summary>ERP route bodies (JSON arrays) to a snapshot; safe off the main thread (JsonUtility, no Unity objects).</summary>
        public static YardSnapshot Assemble(IReadOnlyDictionary<string, string> bodies, string source)
        {
            T[] Rows<T>(string route) => JsonUtility.FromJson<RowsOf<T>>("{\"rows\":" + bodies[route] + "}").rows ?? new T[0];
            T[] Optional<T>(string route) => bodies.ContainsKey(route) ? Rows<T>(route) : null;
            return new YardSnapshot
            {
                format = YardSnapshot.Format, exported_at = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ"), source = source,
                hulls = Rows<HullRow>("hulls"), vessel_details = Rows<HullInfo>("vessel_details"),
                hull_geometry = Rows<HullGeometryRow>("hull_geometry"), hull_placements = Rows<HullPlacement>("hull_placements"),
                erection_timeline = Rows<BlockRecord>("erection_timeline"), block_stock_plans = Rows<StockPlanRow>("block_stock_plans"),
                location_geo = Rows<Facility>("location_geo"), yard_sites = Rows<YardSite>("yard_sites"),
                shift_points = Optional<ShiftPointRow>("shift_points"), berth_assignments = Optional<BerthAssignmentRow>("berth_assignments"),
                crane_assignments = Optional<CraneAssignmentRow>("crane_assignments"), quay_shifts = Optional<QuayShiftRow>("quay_shifts"),
                moves = Optional<BlockMoveRow>("moves"), dock_bookings = Optional<DockBookingRow>("dock_bookings"),
                crane_rails = Optional<CraneRailRow>("crane_rails"),
                crane_kinematics = Optional<CraneKinematicsRow>("crane_kinematics"),
                crane_load_charts = Optional<CraneLoadChartRow>("crane_load_charts"),
                hull_overview = Optional<HullOverviewRow>("hull_overview"),
                block_stages = Optional<StagePlanRow>("block_stages"),
                center_locations = Optional<CenterLocationRow>("center_locations"),
                equipment_specs = Optional<YardEquipmentSpec>("equipment_specs"),
            }.Normalised();
        }

        private static string Reason(Exception exception)
        {
            switch (exception)
            {
                case NetworkException network:
                    return network.StatusCode == 401 ? "로그인 필요" : network.StatusCode == 403 ? "권한 없음" :
                        network.StatusCode > 0 ? "HTTP " + network.StatusCode : "연결 오류";
                case InvalidOperationException invalid:
                    return invalid.Message;
                case TaskCanceledException _:
                case TimeoutException _:
                    return "시간 초과";
                default:
                    return exception.GetType().Name;
            }
        }

        private static string Host(string url) => url.Contains("//") ? url.Substring(url.IndexOf("//", StringComparison.Ordinal) + 2) : url;

        private static string Stamp(string iso) =>
            DateTime.TryParse(iso, null, System.Globalization.DateTimeStyles.RoundtripKind, out var time)
                ? time.ToLocalTime().ToString("yyyy-MM-dd HH:mm")
                : "(시각 미상)";

        [Serializable]
        private sealed class RowsOf<T>
        {
            public T[] rows;
        }

        [Serializable]
        private sealed class LoginBody
        {
            public string id;
            public string password;
        }

        [Serializable]
        private sealed class LoginReply
        {
            public string token;
        }
    }
}
