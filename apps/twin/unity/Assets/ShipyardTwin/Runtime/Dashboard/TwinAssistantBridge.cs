using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UIElements;

namespace ShipyardTwin
{
    /// <summary>
    /// Link between the browser page's SIWS assistant (/assistant/widget.js, twin mode) and the open scene.
    /// The page queues requests (ShipyardWeb.jslib); this component takes them, answers through the jslib, and never acts
    /// on its own:
    /// <list type="bullet">
    /// <item><c>find</c> {query}: candidates from <see cref="YardObjectSearch.Find"/> (the unified search's data and ranking).</item>
    /// <item><c>open</c> {pick}: one of the last candidates through <see cref="YardObjectSearch.Open"/> (the search's camera
    /// framing, highlight and detail popup), then <c>arrived</c> only once the camera has settled with the target in view.</item>
    /// <item><c>lift</c>: frames the block on a goliath hook now (the guide's own remark, pressed by the user).</item>
    /// <item><c>state</c>: this browser's own playback (date, playing, following the shared replay or not) and the block
    /// on a goliath hook now, for the guide character's remarks.</item>
    /// </list>
    /// Requests carry a name or ID typed by the user, or the index of a candidate the user pressed: no coordinates, no
    /// commands. The date and playback are never changed.
    /// </summary>
    public sealed class TwinAssistantBridge : MonoBehaviour
    {
        public const int Limit = 6;
        private const float PollSeconds = 0.1f, ArrivalTimeout = 6f, SettleSpeed = 0.6f, SettleShare = 0.15f;

        [Serializable]
        public sealed class Request
        {
            public int id;
            public string op;
            public string query;
            public int pick = -1;
        }

        [Serializable]
        public sealed class Candidate
        {
            public int pick;
            public string id;
            public string title;
            public string kind;
            public string subtitle;
            public string category;
            public bool available;
            public string reason;
        }

        [Serializable]
        public sealed class Reply
        {
            public int id;
            public string op;
            public string state;    // find: found|none|notready · open: moving|arrived|failed · state: ok|notready
            public string reason;
            public string title;
            public Candidate[] results;
            public string date;
            public bool playing;
            public bool synced;
            public string mode;     // yard (yard calendar) | hull (one hull's erection replay)
            public string hull;
            public string lift;     // block on (or waiting for) a goliath hook now, for the guide's remarks
            public bool liftHooked;
            public string liftDock;
            public float bottomShare;   // share of the screen height taken by the bottom bars (the guide stands above)
            public float rightShare;    // share of the screen width taken by right-hand cards (the guide stands left of them)
        }

        private readonly List<YardSearchResult> _last = new List<YardSearchResult>();
        private string _lastLiftBlock;   // the lift last reported to the page, framed if it ends before "보러 가기"
        private Bounds _lastLiftBounds;
        private float _lastLiftAt = float.NegativeInfinity;
        private const float LiftMemorySeconds = 90f;
        private ShipView _ship;
        private float _nextPoll;
        private Moving _moving;

        private sealed class Moving
        {
            public int Id;
            public string Title;
            public HoverTarget Target;   // null: a fixed point (the lift site)
            public Vector3 Point;
            public float Started;
            public Vector3 Position;
            public bool InView;
        }

#if UNITY_WEBGL && !UNITY_EDITOR
        [System.Runtime.InteropServices.DllImport("__Internal")]
        private static extern void ShipyardWeb_AssistantRegister();

        [System.Runtime.InteropServices.DllImport("__Internal")]
        private static extern string ShipyardWeb_AssistantTake();

        [System.Runtime.InteropServices.DllImport("__Internal")]
        private static extern void ShipyardWeb_AssistantReply(string json);
#else
        private static void ShipyardWeb_AssistantRegister() { }
        private static string ShipyardWeb_AssistantTake() => null;
        private static void ShipyardWeb_AssistantReply(string json) { }
#endif

        /// <summary>Raised with every reply (tests and other views); the page gets it through the jslib.</summary>
        public event Action<Reply> Replied;

        private void Start() => ShipyardWeb_AssistantRegister();

        private void Update()
        {
            if (Time.unscaledTime >= _nextPoll)
            {
                _nextPoll = Time.unscaledTime + PollSeconds;
                var json = ShipyardWeb_AssistantTake();
                if (!string.IsNullOrEmpty(json)) Handle(json);
            }

            Track();
        }

        /// <summary>One request as JSON (also the entry point for tests).</summary>
        public void Handle(string json)
        {
            Request request;
            try
            {
                request = JsonUtility.FromJson<Request>(json);
            }
            catch (Exception)
            {
                return;
            }

            if (request == null) return;
            switch (request.op)
            {
                case "find":
                    Find(request);
                    break;
                case "open":
                    Open(request);
                    break;
                case "state":
                    Send(State(request.id));
                    break;
                case "lift":
                    Lift(request);
                    break;
            }
        }

        private Camera _camera;

        private Camera SceneCamera => _camera != null && _camera.isActiveAndEnabled ? _camera
            : _camera = Camera.main != null ? Camera.main : FindFirstObjectByType<Camera>();

        private ShipView Ship => _ship != null ? _ship : _ship = FindFirstObjectByType<ShipView>();

        private void Find(Request request)
        {
            var reply = new Reply { id = request.id, op = "find" };
            var model = Ship != null ? Ship.Model : null;
            var query = Clean(request.query);
            if (model == null)
            {
                reply.state = "notready";
                reply.reason = "야드 장면을 불러오는 중입니다. 잠시 후 다시 요청해 주세요.";
                Send(reply);
                return;
            }

            _last.Clear();
            if (query.Length > 0) _last.AddRange(YardObjectSearch.Find(model, query, Limit));
            reply.results = new Candidate[_last.Count];
            for (var i = 0; i < _last.Count; i++)
            {
                var result = _last[i];
                reply.results[i] = new Candidate
                {
                    pick = i, id = result.Id, title = result.Title ?? result.Id, kind = result.Kind, subtitle = Clip(result.Subtitle, 90),
                    category = result.Category.ToString(), available = result.IsAvailable,
                    reason = result.IsAvailable ? null : OffScreenReason(result.Category)
                };
            }

            reply.state = _last.Count > 0 ? "found" : "none";
            Send(reply);
        }

        private void Open(Request request)
        {
            var reply = new Reply { id = request.id, op = "open" };
            if (request.pick < 0 || request.pick >= _last.Count)
            {
                reply.state = "failed";
                reply.reason = "선택한 후보가 더 이상 목록에 없습니다. 다시 요청해 주세요.";
                Send(reply);
                return;
            }

            var result = _last[request.pick];
            reply.title = result.Title ?? result.Id;
            if (!result.IsAvailable)
            {
                reply.state = "failed";
                reply.reason = OffScreenReason(result.Category);
                Send(reply);
                return;
            }

            bool opened;
            try
            {
                opened = YardObjectSearch.Open(result);
            }
            catch (Exception e)
            {
                Debug.LogWarning("[ShipyardTwin] assistant open failed: " + e.Message);
                opened = false;
            }

            if (!opened)
            {
                reply.state = "failed";
                reply.reason = "지금 화면에서 찾지 못했습니다. 재생이 진행되면서 사라졌거나 숨긴 표시 항목일 수 있습니다.";
                Send(reply);
                return;
            }

            if (_moving != null) Send(new Reply { id = _moving.Id, op = "open", state = "failed", title = _moving.Title, reason = "다른 이동 요청으로 바뀌었습니다." });
            var camera = SceneCamera;
            _moving = new Moving
            {
                Id = request.id, Title = reply.title, Target = result.Target, Started = Time.unscaledTime,
                Position = camera != null ? camera.transform.position : Vector3.zero
            };
            reply.state = "moving";
            Send(reply);
        }

        /// <summary>Arrival: the camera has (nearly) stopped and the target's centre is on screen; 6 s at most.</summary>
        private void Track()
        {
            if (_moving == null) return;
            var camera = SceneCamera;
            var target = _moving.Target;
            var elapsed = Time.unscaledTime - _moving.Started;
            var fixedPoint = target is null;
            if (camera == null || (!fixedPoint && (target == null || !target.isActiveAndEnabled)))
            {
                Finish(false, "이동 중에 대상이 화면에서 사라졌습니다.");
                return;
            }

            var position = camera.transform.position;
            var speed = (position - _moving.Position).magnitude / Mathf.Max(Time.unscaledDeltaTime, 1e-4f);
            _moving.Position = position;
            var centre = fixedPoint ? _moving.Point : Centre(target, position);
            var view = camera.WorldToViewportPoint(centre);
            _moving.InView = view.z > 0f && view.x > 0.02f && view.x < 0.98f && view.y > 0.02f && view.y < 0.98f;
            // Settled: moving less than a small share of the remaining distance per second (the easing's long tail).
            var settled = speed < Mathf.Max(SettleSpeed, (centre - position).magnitude * SettleShare);
            if (elapsed > 0.25f && _moving.InView && settled) Finish(true, null);
            else if (elapsed > ArrivalTimeout)
                Finish(_moving.InView, _moving.InView ? null : "카메라가 대상을 화면에 담지 못했습니다. 다른 조작으로 이동이 멈췄을 수 있습니다.");
        }

        private void Finish(bool arrived, string reason)
        {
            var moving = _moving;
            _moving = null;
            Send(new Reply { id = moving.Id, op = "open", state = arrived ? "arrived" : "failed", title = moving.Title, reason = reason });
        }

        private static Vector3 Centre(HoverTarget target, Vector3 fallback)
        {
            var renderers = target.GetComponentsInChildren<Renderer>(false);
            var found = false;
            var bounds = default(Bounds);
            foreach (var renderer in renderers)
            {
                if (!renderer.enabled) continue;
                if (found) bounds.Encapsulate(renderer.bounds);
                else bounds = renderer.bounds;
                found = true;
            }

            return found ? bounds.center : target.transform.position;
        }

        /// <summary>
        /// The guide's "보러 가기" on a lift it mentioned: frames the block now on (or waiting for) a goliath hook with the
        /// camera's normal focus. The lifted block is not a search target, so there is no detail popup.
        /// </summary>
        private void Lift(Request request)
        {
            var reply = new Reply { id = request.id, op = "open" };
            var ship = Ship;
            Bounds bounds = default;
            string block = null;
            if (ship != null && ship.Selected != null && ship.CurrentLiftBlock != null)
            {
                bounds = ship.CurrentLiftBounds;
                block = ship.CurrentLiftBlock.block;
            }
            else if (ship != null && ship.YardLifts.Count > 0)
            {
                bounds = ship.YardLifts[0].Bounds;
                block = ship.YardLifts[0].Block?.block;
            }

            var wanted = Clean(request.query);
            if ((block == null || (wanted.Length > 0 && wanted != block)) && _lastLiftBlock != null &&
                (wanted.Length == 0 || wanted == _lastLiftBlock) && Time.unscaledTime - _lastLiftAt < LiftMemorySeconds)
            {
                block = _lastLiftBlock;   // that lift has just ended: its site (the block now sits there)
                bounds = _lastLiftBounds;
            }

            reply.title = block;
            var rig = FindFirstObjectByType<OrbitCameraRig>();
            if (block == null || bounds.size.sqrMagnitude <= 0f || rig == null || !rig.Focus(bounds))
            {
                reply.state = "failed";
                reply.reason = block == null ? "지금은 인양 중인 블록이 없습니다. 재생이 그사이 넘어갔을 수 있습니다." : "카메라를 그 위치로 옮기지 못했습니다.";
                Send(reply);
                return;
            }

            if (_moving != null) Send(new Reply { id = _moving.Id, op = "open", state = "failed", title = _moving.Title, reason = "다른 이동 요청으로 바뀌었습니다." });
            var camera = SceneCamera;
            _moving = new Moving
            {
                Id = request.id, Title = block, Target = null, Point = bounds.center, Started = Time.unscaledTime,
                Position = camera != null ? camera.transform.position : Vector3.zero
            };
            reply.state = "moving";
            Send(reply);
        }

        private Reply State(int id)
        {
            var ship = Ship;
            if (ship == null || ship.Model == null) return new Reply { id = id, op = "state", state = "notready" };
            var hull = ship.Selected;
            string lift = null, liftDock = null;
            var hooked = false;
            var liftBounds = default(Bounds);
            if (hull != null)
            {
                lift = ship.CurrentLiftBlock?.block;
                hooked = ship.CurrentLiftHooked;
                liftDock = hull.Placement?.dock;
                liftBounds = ship.CurrentLiftBounds;
            }
            else if (ship.YardLifts.Count > 0)
            {
                var first = ship.YardLifts[0];
                lift = first.Block?.block;
                hooked = first.Hooked;
                liftDock = first.Dock?.name;
                liftBounds = first.Bounds;
            }

            if (lift != null && liftBounds.size.sqrMagnitude > 0f)
            {
                _lastLiftBlock = lift;
                _lastLiftBounds = liftBounds;
                _lastLiftAt = Time.unscaledTime;
            }

            return new Reply
            {
                lift = lift, liftHooked = hooked, liftDock = liftDock, bottomShare = BottomShare(), rightShare = RightShare(),
                id = id, op = "state", state = "ok",
                date = ship.CurrentDate.ToString("yyyy-MM-dd HH:mm"),
                playing = hull != null ? ship.Playback?.Playing ?? false : ship.Calendar?.Playing ?? false,
                synced = ship.Synced,
                mode = hull != null ? "hull" : "yard",
                hull = hull?.Id
            };
        }

        private UIDocument _document;

        /// <summary>How much of the screen bottom the dashboard's bottom bar (schedule/timeline) covers, 0..0.5.</summary>
        private float BottomShare()
        {
            if (_document == null) _document = FindFirstObjectByType<UIDocument>();
            var root = _document != null ? _document.rootVisualElement : null;
            var bar = root?.Q("timeline");
            if (bar == null || root.worldBound.height <= 0f || bar.resolvedStyle.display == DisplayStyle.None ||
                bar.resolvedStyle.visibility == Visibility.Hidden || bar.resolvedStyle.opacity <= 0.01f || bar.worldBound.height <= 0f) return 0f;
            return Mathf.Clamp((root.worldBound.yMax - bar.worldBound.yMin) / root.worldBound.height, 0f, 0.5f);
        }

        /// <summary>How much of the screen's right edge the object card or depth legend covers near the bottom, 0..0.6.</summary>
        private float RightShare()
        {
            if (_document == null) _document = FindFirstObjectByType<UIDocument>();
            var root = _document != null ? _document.rootVisualElement : null;
            if (root == null || root.worldBound.width <= 0f) return 0f;
            var edge = root.worldBound.xMax;
            var detail = root.Q("object-detail");
            // On compact screens the card is a bottom sheet across the width: the guide would stand above it, not beside.
            if (detail != null && detail.ClassListContains("tw-detail--open") && !root.ClassListContains("tw-compact") && Visible(detail))
                edge = Mathf.Min(edge, detail.worldBound.xMin);
            var legend = root.Q("depth-legend");
            if (legend != null && Visible(legend)) edge = Mathf.Min(edge, legend.worldBound.xMin);
            return Mathf.Clamp((root.worldBound.xMax - edge) / root.worldBound.width, 0f, 0.6f);
        }

        private static bool Visible(VisualElement e) =>
            e.resolvedStyle.display != DisplayStyle.None && e.resolvedStyle.visibility != Visibility.Hidden &&
            e.resolvedStyle.opacity > 0.01f && e.worldBound.width > 0f;

        private void Send(Reply reply)
        {
            Replied?.Invoke(reply);
            ShipyardWeb_AssistantReply(JsonUtility.ToJson(reply));
        }

        /// <summary>The name or ID to look for: trimmed, without control characters, at most 80 characters.</summary>
        public static string Clean(string query)
        {
            if (string.IsNullOrWhiteSpace(query)) return string.Empty;
            var chars = new List<char>(Math.Min(query.Length, 80));
            foreach (var c in query)
            {
                if (chars.Count >= 80) break;
                chars.Add(char.IsControl(c) ? ' ' : c);
            }

            return new string(chars.ToArray()).Trim();
        }

        /// <summary>Why a candidate cannot be shown now (the date and playback are left as they are).</summary>
        public static string OffScreenReason(TargetCategory category) => category switch
        {
            TargetCategory.Block => "지금 재생 시점에는 3D 화면에 없는 블록입니다(탑재 전·반출 등). 재생 일자는 바꾸지 않습니다.",
            TargetCategory.Hull => "지금 장면에 배치되지 않은 호선입니다. 재생 일자는 바꾸지 않습니다.",
            _ => "지금 화면에 없습니다(숨긴 표시 항목이거나 장면에 없는 대상). 재생 일자는 바꾸지 않습니다."
        };

        private static string Clip(string value, int max) =>
            string.IsNullOrEmpty(value) || value.Length <= max ? value : value.Substring(0, max - 1) + "…";
    }
}
