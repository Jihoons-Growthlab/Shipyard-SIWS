using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using GrowthLab.Network;
using UnityEngine;

namespace ShipyardTwin
{
    /// <summary>Rows of extra ERP routes for the detail popup, by route name; null while not loaded.</summary>
    public interface IDetailRows
    {
        T[] Get<T>(string route);

        /// <summary>"" when loaded (or not needed), else why it is missing: "조회 중", "권한 없음", "ERP 미연결" …</summary>
        string Missing(string route);
    }

    /// <summary>
    /// On-demand ERP queries for the detail popup: routes the yard snapshot does not carry (outfitting work and
    /// progress, block assembly stages, crane load, hull milestones). Each route is fetched once with the session's
    /// token (<see cref="YardDataSource.Token"/>; never logged) when a popup first needs it and reused for a minute,
    /// so clicking around the yard does not repeat queries. Forbidden or unknown routes are remembered as such.
    /// </summary>
    public sealed class ErpDetailCache : IDetailRows
    {
        private static readonly TimeSpan Fresh = TimeSpan.FromSeconds(60);

        private readonly Dictionary<string, Entry> _entries = new Dictionary<string, Entry>();
        private RestClient _client;

        private sealed class Entry
        {
            public DateTime At;
            public string Body;
            public string Missing = "조회 중";
            public Task Loading;
            public readonly Dictionary<Type, Array> Parsed = new Dictionary<Type, Array>();
        }

        public static bool Configured => !string.IsNullOrEmpty(YardDataSource.ErpUrl);

        public T[] Get<T>(string route)
        {
            if (!_entries.TryGetValue(route, out var entry) || entry.Body == null)
            {
                return null;
            }

            if (!entry.Parsed.TryGetValue(typeof(T), out var rows))
            {
                rows = Parse<T>(entry.Body);
                entry.Parsed[typeof(T)] = rows;
            }

            return (T[])rows;
        }

        public string Missing(string route)
        {
            if (!Configured) return "ERP 미연결";
            return _entries.TryGetValue(route, out var entry) ? entry.Body != null ? string.Empty : entry.Missing : "조회 중";
        }

        /// <summary>True when every route is loaded and fresh (no query needed).</summary>
        public bool Has(IEnumerable<string> routes)
        {
            foreach (var route in routes)
            {
                if (!_entries.TryGetValue(route, out var entry) || !Answered(entry))
                {
                    return false;
                }
            }

            return true;
        }

        /// <summary>Fetches the stale or missing routes (in parallel); completes when all have answered or failed.</summary>
        public async Task Load(IEnumerable<string> routes, CancellationToken cancel = default)
        {
            if (!Configured)
            {
                return;
            }

            var waits = new List<Task>();
            foreach (var route in routes)
            {
                if (!_entries.TryGetValue(route, out var entry))
                {
                    entry = new Entry();
                    _entries[route] = entry;
                }

                if (entry.Loading != null && !entry.Loading.IsCompleted)
                {
                    waits.Add(entry.Loading);
                    continue;
                }

                if (Answered(entry))
                {
                    continue;   // answered (rows or a refusal) within the last minute
                }

                entry.Loading = Fetch(route, entry, cancel);
                waits.Add(entry.Loading);
            }

            await Task.WhenAll(waits);
        }

        private static bool Answered(Entry entry) =>
            (entry.Loading == null || entry.Loading.IsCompleted) && entry.At != default && DateTime.UtcNow - entry.At <= Fresh;

        private async Task Fetch(string route, Entry entry, CancellationToken cancel)
        {
            _client ??= new RestClient(new UnityWebRequestTransport(), new RestClientOptions { Timeout = TimeSpan.FromSeconds(12), MaxRetries = 1 });
            var request = new HttpRequest(HttpMethod.Get, YardDataSource.ErpUrl + "/api/" + route);
            var token = YardDataSource.Token;
            if (!string.IsNullOrEmpty(token))
            {
                request.Headers["Authorization"] = "Bearer " + token;
            }

            try
            {
                var body = (await _client.SendAsync(request, cancel)).Body;
                entry.Body = string.IsNullOrWhiteSpace(body) || body.TrimStart()[0] != '[' ? "[]" : body;
                entry.Missing = string.Empty;
            }
            catch (NetworkException error)
            {
                entry.Body = null;
                entry.Missing = error.StatusCode == 401 ? "로그인 필요" : error.StatusCode == 403 ? "권한 없음" :
                    error.StatusCode == 404 ? "ERP에 없는 조회" : "조회 실패";
            }
            catch (Exception)
            {
                entry.Body = null;
                entry.Missing = "조회 실패";
            }

            entry.Parsed.Clear();
            entry.At = DateTime.UtcNow;
        }

        public static T[] Parse<T>(string body) =>
            JsonUtility.FromJson<RowsOf<T>>("{\"rows\":" + (string.IsNullOrWhiteSpace(body) ? "[]" : body) + "}").rows ?? new T[0];

        [Serializable]
        private sealed class RowsOf<T>
        {
            public T[] rows;
        }
    }

    /// <summary>Rows supplied directly (tests, offline snapshot): every route present, nothing missing.</summary>
    public sealed class StaticDetailRows : IDetailRows
    {
        private readonly Dictionary<string, object> _rows = new Dictionary<string, object>();

        public StaticDetailRows Add<T>(string route, params T[] rows)
        {
            _rows[route] = rows;
            return this;
        }

        public T[] Get<T>(string route) => _rows.TryGetValue(route, out var rows) ? rows as T[] : null;

        public string Missing(string route) => _rows.ContainsKey(route) ? string.Empty : "ERP 미연결";
    }

    // ---------------------------------------------------------------------- ERP rows read only by the detail popup

    [Serializable]
    public sealed class OutfitWorkRow
    {
        public string id, hull, block, outfit_zone, outfit_trade, work_phase, work_area, start_day, end_day, work_status, schedule_check;
        public float planned_mh, progress_pct, earned_mh;
        public int crew;
    }

    [Serializable]
    public sealed class OutfitProgressRow
    {
        public string id, hull, outfit_zone, start_day, end_day, progress_status;
        public int works, works_done, works_held;
        public float planned_mh, planned_to_date_mh, earned_mh, progress_pct, spi;
    }

    [Serializable]
    public sealed class BlockStageRow
    {
        public string id, block, assembly_stage, center, baseline, forecast, actual;
        public float planned_mh, actual_mh, progress_pct;
    }

    [Serializable]
    public sealed class BlockProgressRow
    {
        public string block, hull, current_stage;
        public int stages, completed_stages;
        public float planned_mh, actual_mh, earned_mh, progress_pct, weight_t, earned_t, productivity;
    }

    [Serializable]
    public sealed class CraneLoadRow
    {
        public string id, equipment, equipment_type, station, week, hulls_served, load_status;
        public int jobs, work_days;
        public float booked_hours, available_hours, utilization_pct, peak_lift_t, capacity_t;
    }

    [Serializable]
    public sealed class HullMilestoneRow
    {
        public string id, hull, milestone, baseline, forecast, actual, reason;
    }
}
