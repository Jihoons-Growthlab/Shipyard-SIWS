using System;
using System.Collections.Generic;
using System.Linq;
using GrowthLab.Core;
using GrowthLab.UI;
using UnityEngine;
using UnityEngine.UIElements;

namespace ShipyardTwin
{
    /// <summary>
    /// Yard calendar bar (bottom, while no hull is selected; layout in Dashboard.uxml "yard-clock"): the calendar time of
    /// <see cref="ShipView.Calendar"/>, what the yard does at that time (lifts, launches, blocks in shops and slots), play /
    /// pause, speed and a track to seek over the looped plan range. Above every shop (작업장) a label counts the blocks in
    /// it and names its busiest stage (ERP block_stages): the shops hold far more blocks than fit there at scale, so they
    /// are counted here and laid out in the shop's detail popup instead of being drawn.
    /// </summary>
    [RequireComponent(typeof(UIDocument))]
    public sealed class YardClockView : MonoBehaviour, ICoreConsumer
    {
        private const float RefreshInterval = 0.2f;
        private const float LabelHeight = 26f;

        private ShipView _ship;
        private WorldLabels _labels;
        private VisualElement _root, _bar, _track, _fill;
        private Label _date, _activity, _cycle;
        private Button _play, _speed, _toggle;
        private bool _expanded;
        private float _nextRefresh;
        private DateTime _shownDay = DateTime.MinValue;
        private YardModel _labelledModel;
        private readonly Dictionary<string, (Transform Anchor, Label Label)> _shops = new Dictionary<string, (Transform, Label)>();

        public void Construct(CoreContext context)
        {
            if (_ship != null)
            {
                return;
            }

            _ship = FindFirstObjectByType<ShipView>();
            _labels = GetComponent<WorldLabels>();
            _root = GetComponent<UIDocument>().rootVisualElement;
            if (_ship == null)
            {
                return;
            }

            _bar = _root.Q("yard-clock");
            if (_bar == null)
            {
                return;
            }

            _date = _bar.Q<Label>("yard-clock-date");
            _activity = _bar.Q<Label>("yard-clock-activity");
            _cycle = _bar.Q<Label>("yard-clock-cycle");
            _track = _bar.Q("yard-clock-track");
            _fill = _bar.Q("yard-clock-fill");
            _play = _bar.Q<Button>("yard-clock-play");
            _speed = _bar.Q<Button>("yard-clock-speed");
            _toggle = _root.Q<Button>("nav-yard-clock");
            if (_toggle != null) _toggle.clicked += ToggleBar;
            if (_play != null) _play.clicked += () =>
            {
                if (_ship.Calendar != null) _ship.Calendar.Playing = !_ship.Calendar.Playing;
                Refresh();
            };
            if (_speed != null) _speed.clicked += () =>
            {
                _ship.Calendar?.NextSpeed();
                Refresh();
            };
            _track?.RegisterCallback<PointerDownEvent>(evt =>
            {
                Seek(evt.localPosition.x);
                _track.CapturePointer(evt.pointerId);
            });
            _track?.RegisterCallback<PointerMoveEvent>(evt =>
            {
                if (_track.HasPointerCapture(evt.pointerId)) Seek(evt.localPosition.x);
            });
            _track?.RegisterCallback<PointerUpEvent>(evt => _track.ReleasePointer(evt.pointerId));
        }

        private void OnDestroy()
        {
            if (_toggle != null) _toggle.clicked -= ToggleBar;
            foreach (var (anchor, label) in _shops.Values)
            {
                _labels?.Untrack(label);
                if (anchor != null) Destroy(anchor.gameObject);
            }
        }

        private void Seek(float x)
        {
            var calendar = _ship.Calendar;
            var width = _track.layout.width;
            if (calendar == null || width <= 0f)
            {
                return;
            }

            calendar.Seek(calendar.Start.AddDays((calendar.End - calendar.Start).TotalDays * Mathf.Clamp01(x / width)));
            Refresh();
        }

        private bool CanShowBar => _ship.Selected == null && _ship.Calendar != null &&
            !(_root.ClassListContains("tw-compact") && _root.ClassListContains("tw-drawer-open"));

        private void ToggleBar()
        {
            if (!CanShowBar)
            {
                return;
            }

            _expanded = !_expanded;
            _toggle.tooltip = _expanded ? "야드 재생 바 숨기기" : "야드 재생 바 표시";
            _toggle.EnableInClassList("tw-top-button--on", _expanded);
            _root.EnableInClassList("tw-has-yard-clock", _expanded);
            if (_expanded) Refresh();
        }

        private void Update()
        {
            if (_bar == null)
            {
                return;
            }

            var available = CanShowBar;
            if (_toggle != null && _toggle.enabledSelf != available) _toggle.SetEnabled(available);
            _root.EnableInClassList("tw-has-yard-clock", available && _expanded);
            if (_ship.Model != _labelledModel)
            {
                BuildShopLabels(_ship.Model);
            }

            if (Time.unscaledTime >= _nextRefresh)
            {
                Refresh();
            }
        }

        private void Refresh()
        {
            _nextRefresh = Time.unscaledTime + RefreshInterval;
            var calendar = _ship.Calendar;
            if (calendar == null)
            {
                return;
            }

            var time = calendar.Time;
            if (time.Date != _shownDay)
            {
                _shownDay = time.Date;
                UpdateShopLabels(time.Date);
            }

            if (!_expanded || !CanShowBar)
            {
                return;
            }

            if (_date != null) _date.text = time.ToString("yyyy-MM-dd (ddd) HH시", System.Globalization.CultureInfo.GetCultureInfo("ko-KR"));
            if (_play != null) _play.text = calendar.Playing ? "일시정지" : "재생";
            if (_speed != null) _speed.text = "×" + calendar.SpeedMultiplier.ToString("0.##");
            if (_fill != null) _fill.style.width = Length.Percent(Mathf.Clamp01(calendar.Fraction) * 100f);
            if (_cycle != null)
            {
                _cycle.text = calendar.Start.ToString("yyyy-MM-dd") + " ~ " + calendar.End.ToString("yyyy-MM-dd") + " 반복 · " + (calendar.Cycle + 1) + "회차" +
                              (calendar.Playing && _ship.CalendarRate < 1f ? " · 인양 중 감속" : string.Empty);
            }

            if (_activity != null) _activity.text = Activity(time);
        }

        /// <summary>One line on what the yard does at a time: lifts, hulls launching or leaving, and where the blocks are.</summary>
        private string Activity(DateTime time)
        {
            var model = _ship.Model;
            if (model == null)
            {
                return string.Empty;
            }

            var parts = new List<string>();
            var lifts = _ship.YardLifts;
            if (lifts.Count > 0)
            {
                parts.Add("탑재 " + string.Join(", ", lifts.Take(2).Select(l => l.Block.block)) + (lifts.Count > 2 ? " 외 " + (lifts.Count - 2) : string.Empty));
            }

            var day = time.Date;
            foreach (var hull in model.Hulls)
            {
                var (where, _) = model.HullAt(hull, day);
                var (before, _) = model.HullAt(hull, day.AddDays(-1));
                if (where != before)
                {
                    parts.Add(hull.Id + " " + (where == HullWhere.Berth ? before == HullWhere.Dock ? "진수·안벽 이동" : "선석 이동"
                        : where == HullWhere.Away ? "출항(시운전·인도)" : where == HullWhere.Dock ? "입거" : "이동"));
                }
            }

            var counts = YardModel.Count(model.Hulls.SelectMany(h => h.Blocks), day);
            parts.Add($"공장 {counts.InProduction:N0} · 적치 {counts.Stocked:N0} · 탑재 완료 {counts.Erected:N0}");
            return string.Join("  ·  ", parts);
        }

        // ------------------------------------------------------------------ shop labels

        private void BuildShopLabels(YardModel model)
        {
            _labelledModel = model;
            foreach (var (anchor, label) in _shops.Values)
            {
                _labels?.Untrack(label);
                if (anchor != null) Destroy(anchor.gameObject);
            }

            _shops.Clear();
            _shownDay = DateTime.MinValue;
            if (model == null || _labels == null)
            {
                return;
            }

            var withStages = new HashSet<string>(model.Blocks.Values.SelectMany(b => b.ShopStages).Select(s => s.Location), StringComparer.Ordinal);
            foreach (var shop in model.Facilities.Where(f => f.kind == "작업장" && f.HasFootprint && withStages.Contains(f.location)))
            {
                var anchor = new GameObject("ShopLabel " + shop.location).transform;
                anchor.SetParent(transform, false);
                anchor.position = ShipCoordinates.YardToUnity(shop.x, shop.y, YardLayout.QuayLevel);
                var label = new Label { name = "shop-" + shop.location };
                label.AddToClassList("tw-shop-count");
                _labels.Track(anchor, label, new Vector3(0f, LabelHeight, 0f));
                _shops[shop.location] = (anchor, label);
            }
        }

        private void UpdateShopLabels(DateTime day)
        {
            var model = _ship.Model;
            if (model == null || _shops.Count == 0)
            {
                return;
            }

            var byShop = new Dictionary<string, List<ShopStage>>(StringComparer.Ordinal);
            foreach (var block in model.Blocks.Values)
            {
                var stage = block.ShopAt(day);
                if (stage != null && _shops.ContainsKey(stage.Location) && YardModel.StateAt(block, day) == BlockState.InProduction)
                {
                    if (!byShop.TryGetValue(stage.Location, out var list)) byShop[stage.Location] = list = new List<ShopStage>();
                    list.Add(stage);
                }
            }

            foreach (var pair in _shops)
            {
                var (_, label) = pair.Value;
                if (!byShop.TryGetValue(pair.Key, out var stages) || stages.Count == 0)
                {
                    label.text = "블록 0";
                    label.EnableInClassList("tw-shop-count--idle", true);
                    continue;
                }

                var top = stages.GroupBy(s => s.Stage).OrderByDescending(g => g.Count()).First();
                label.text = "블록 " + stages.Count + " · " + top.Key + " " + top.Count();
                label.EnableInClassList("tw-shop-count--idle", false);
            }
        }
    }
}
