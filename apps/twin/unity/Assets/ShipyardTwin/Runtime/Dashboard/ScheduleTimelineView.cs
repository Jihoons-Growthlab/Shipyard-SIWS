using System;
using System.Collections.Generic;
using System.Linq;
using GrowthLab.Core;
using UnityEngine;
using UnityEngine.UIElements;

namespace ShipyardTwin
{
    /// <summary>
    /// Schedule bar along the bottom (Dashboard.uxml "timeline"): the selected hull's whole plan (<see cref="HullTimeline"/>)
    /// as a Gantt chart on a date axis, with the simulation date as a moving line, play / speed / restart, and above the
    /// yard a banner with the date and what is planned for it while the simulation plays.
    /// The view opens on the erection period; "전체 일정" switches to the whole plan. Chart: wheel zooms around the pointer (down to hours), Shift+wheel or wheel over the names scrolls rows, drag pans,
    /// a click moves the simulation to that date, hovering a bar shows its detail. While playing the view follows the
    /// date and the block being lifted, unless the chart was moved by hand in the last few seconds.
    /// The chart folds to its head row ("일정 ▲"); on a phone it starts folded.
    /// Starting playback hides the open drawer (<see cref="DashboardShellView.SetSimulating"/>); it returns on pause.
    /// </summary>
    [RequireComponent(typeof(UIDocument))]
    public sealed class ScheduleTimelineView : MonoBehaviour, ICoreConsumer
    {
        /// <summary>Command line: start playing as soon as a hull is selected (screenshots of the running simulation).</summary>
        public const string PlayArgument = "-shipyard-play";

        private const float RowHeight = 18f, AxisHeight = 22f, BarInset = 3f, MinPixelsPerTick = 64f;
        private const double MinDaysPerPixel = 1.0 / 24 / 40;   // fully zoomed in an hour is 40 px wide
        private const float RepaintInterval = 0.05f, ManualHoldSeconds = 4f;

        private static readonly Color Grid = new Color(1f, 1f, 1f, 0.06f), GridStrong = new Color(1f, 1f, 1f, 0.14f);
        private static readonly Color Stripe = new Color(1f, 1f, 1f, 0.025f), GroupLine = new Color(1f, 1f, 1f, 0.12f);
        private static readonly Color CursorColor = new Color(1f, 0.78f, 0.25f), OutOfRange = new Color(0f, 0f, 0f, 0.22f);
        private static readonly Color Focus = new Color(1f, 1f, 1f, 0.07f);

        private static readonly Dictionary<HullTimeline.Kind, Color> Colors = new Dictionary<HullTimeline.Kind, Color>
        {
            { HullTimeline.Kind.Dock, new Color(0.25f, 0.68f, 0.70f) },
            { HullTimeline.Kind.Berth, new Color(0.58f, 0.50f, 0.86f) },
            { HullTimeline.Kind.Shift, new Color(0.95f, 0.55f, 0.35f) },
            { HullTimeline.Kind.Delivery, new Color(0.40f, 0.85f, 0.50f) },
            { HullTimeline.Kind.Crane, new Color(0.96f, 0.68f, 0.24f) },
            { HullTimeline.Kind.Stock, new Color(0.50f, 0.57f, 0.65f) },
            { HullTimeline.Kind.Lift, new Color(0.33f, 0.63f, 1f) },
        };

        private ShipView _ship;
        private DashboardShellView _shell;
        private VisualElement _root, _bar, _canvas, _names, _ticks, _banner;
        private Label _date, _today, _hover, _bannerDate, _bannerText, _rangeLabel;
        private Button _play, _speed, _restart, _fit, _toggle;
        private bool? _collapsed;
        private readonly List<Label> _rowLabels = new List<Label>();
        private readonly List<Label> _tickLabels = new List<Label>();

        private ErectionPlayback _playback;
        private HullTimeline _timeline;
        private DateTime _origin;
        private double _viewStart, _daysPerPixel = 1, _pendingZoomRatio = 1;
        private float _applyZoomAt;
        private float _scrollY, _manualUntil = -1f, _nextRepaint;
        private float _shownCursor = float.NaN;
        private bool _wasPlaying, _dragging, _moved, _needsFit, _autoPlay, _showAll;
        private Vector2 _dragFrom;
        private double _dragView;
        private float _dragScroll;
        private string _focusBlock;

        public void Construct(CoreContext context)
        {
            if (_ship != null)
            {
                return;
            }

            _ship = FindFirstObjectByType<ShipView>();
            _shell = GetComponent<DashboardShellView>();
            _root = GetComponent<UIDocument>().rootVisualElement;
            _bar = _root.Q("timeline");
            if (_ship == null || _bar == null)
            {
                return;
            }

            _canvas = _bar.Q("timeline-canvas");
            _names = _bar.Q("timeline-names");
            _ticks = _bar.Q("timeline-ticks");
            _date = _bar.Q<Label>("timeline-date");
            _today = _bar.Q<Label>("timeline-today");
            _hover = _bar.Q<Label>("timeline-hover");
            _rangeLabel = _bar.Q<Label>("timeline-range");
            _play = _bar.Q<Button>("timeline-play");
            _speed = _bar.Q<Button>("timeline-speed");
            _restart = _bar.Q<Button>("timeline-restart");
            var exit = _bar.Q<Button>("timeline-exit");
            if (exit != null) exit.clicked += () => _ship.Select(null);   // ends the simulation (back to the plan day)
            _fit = _bar.Q<Button>("timeline-fit");
            _toggle = _bar.Q<Button>("timeline-toggle");
            if (_toggle != null) _toggle.clicked += () => SetCollapsed(!(_collapsed ?? false));
            _banner = _root.Q("schedule-banner");
            _bannerDate = _root.Q<Label>("banner-date");
            _bannerText = _root.Q<Label>("banner-text");

            if (_play != null) _play.clicked += () =>
            {
                if (_playback != null) _playback.Playing = !_playback.Playing && (_playback.Remote != null || !_playback.Schedule.Finished);
                _manualUntil = -1f;
                RefreshTexts();
            };
            if (_speed != null) _speed.clicked += () => { _playback?.NextSpeed(); RefreshTexts(); };
            if (_restart != null) _restart.clicked += () => { _playback?.Restart(); _manualUntil = -1f; RefreshTexts(); };
            if (_fit != null) _fit.clicked += () =>
            {
                _showAll = !_showAll;
                FitAll();
                UpdateView();
            };

            if (_canvas != null)
            {
                _canvas.generateVisualContent += Draw;
                _canvas.RegisterCallback<WheelEvent>(OnWheel);
                _canvas.RegisterCallback<PointerDownEvent>(OnPointerDown);
                _canvas.RegisterCallback<PointerMoveEvent>(OnPointerMove);
                _canvas.RegisterCallback<PointerUpEvent>(OnPointerUp);
                _canvas.RegisterCallback<PointerLeaveEvent>(_ => ShowHover(null, Vector2.zero));
                _canvas.RegisterCallback<GeometryChangedEvent>(_ =>
                {
                    if (_needsFit) FitAll();
                    UpdateView();
                });
            }

            _names?.RegisterCallback<WheelEvent>(evt =>
            {
                Scroll(Mathf.Clamp(evt.delta.y, -3f, 3f) * RowHeight * 2f);
                evt.StopPropagation();
            });

            _autoPlay = Array.IndexOf(Environment.GetCommandLineArgs(), PlayArgument) >= 0;
            _ship.Changed += Rebuild;
            Rebuild();
        }

        private void OnDestroy()
        {
            if (_ship != null) _ship.Changed -= Rebuild;
            if (_canvas != null) _canvas.generateVisualContent -= Draw;
        }

        /// <summary>Selection or plan changed: new timeline for the selected hull (bar hidden when none).</summary>
        private void Rebuild()
        {
            if (_playback != _ship.Playback) _pendingZoomRatio = 1;
            _playback = _ship.Playback;
            var hull = _ship.Selected;
            _root.EnableInClassList("tw-has-timeline", _playback != null && hull != null);
            if (_playback == null || hull == null)
            {
                _timeline = null;
                SetSimulating(false);
                return;
            }

            _timeline = HullTimeline.Build(hull, _ship.Model?.Source, _playback.Schedule);
            _origin = _timeline.From.Date;
            _names.Clear();
            _rowLabels.Clear();
            string group = null;
            foreach (var row in _timeline.Rows)
            {
                var label = new Label(row.Label) { pickingMode = PickingMode.Ignore };
                label.AddToClassList("tw-tl-name");
                label.AddToClassList(row.Group == HullTimeline.BlockGroup ? "tw-tl-name--block" : "tw-tl-name--hull");
                if (row.Group != group)
                {
                    label.AddToClassList("tw-tl-name--first");
                    group = row.Group;
                }

                _names.Add(label);
                _rowLabels.Add(label);
            }

            if (_rangeLabel != null)
            {
                _rangeLabel.text = hull.Id + " 전체 일정 " + _timeline.From.ToString("yyyy-MM-dd") + " ~ " + _timeline.To.AddDays(-1).ToString("yyyy-MM-dd") +
                                   " · 블록 " + _timeline.Rows.Count(r => r.Block != null) + "개";
            }

            // Phones start with the chart folded (head row only) so the yard has the screen; the choice then sticks.
            if (!_collapsed.HasValue) SetCollapsed(_root.ClassListContains("tw-compact"));
            _scrollY = 0f;
            _focusBlock = null;
            _showAll = false;
            _needsFit = true;
            FitAll();
            UpdateView();
            RefreshTexts();
            if (_autoPlay)
            {
                _autoPlay = false;
                _playback.Playing = true;
            }
        }

        private void Update()
        {
            if (_playback != null && _pendingZoomRatio != 1 && Time.unscaledTime >= _applyZoomAt)
            {
                _playback.SetSpeed((float)(_playback.SpeedMultiplier * _pendingZoomRatio));
                _pendingZoomRatio = 1;
                RefreshTexts();
            }

            if (_playback == null || _timeline == null)
            {
                return;
            }

            if (_playback.Playing != _wasPlaying)
            {
                SetSimulating(_playback.Playing);
            }

            var cursor = (float)(_ship.CurrentDate - _origin).TotalDays;
            if (Mathf.Approximately(cursor, _shownCursor) || Time.unscaledTime < _nextRepaint)
            {
                return;
            }

            _nextRepaint = Time.unscaledTime + RepaintInterval;
            _shownCursor = cursor;
            if (_playback.Playing && Time.unscaledTime > _manualUntil)
            {
                Follow();
            }

            RefreshTexts();
            UpdateView();
        }

        private void SetCollapsed(bool collapsed)
        {
            _collapsed = collapsed;
            _bar?.EnableInClassList("tw-timeline--collapsed", collapsed);
            if (_toggle != null) _toggle.text = collapsed ? "일정 ▲" : "접기";
            if (!collapsed)
            {
                _needsFit = true;
                _canvas?.schedule.Execute(() =>
                {
                    if (_needsFit) FitAll();
                    UpdateView();
                });
            }
        }

        private void SetSimulating(bool playing)
        {
            _wasPlaying = playing;
            _shell?.SetSimulating(playing);
            _root.EnableInClassList("tw-simulating", playing);
            RefreshTexts();
        }

        // ------------------------------------------------------------------ view (axis, names, follow)

        private float Width => _canvas == null || float.IsNaN(_canvas.contentRect.width) ? 0f : _canvas.contentRect.width;
        private float Height => _canvas == null || float.IsNaN(_canvas.contentRect.height) ? 0f : _canvas.contentRect.height;
        private double DaysOf(DateTime t) => (t - _origin).TotalDays;
        private float X(DateTime t) => (float)((DaysOf(t) - _viewStart) / _daysPerPixel);
        private DateTime At(float x) => _origin.AddDays(_viewStart + x * _daysPerPixel);
        private DateTime Current => _ship.CurrentDate;
        private float MaxScroll => Mathf.Max(0f, AxisHeight + _timeline.Rows.Count * RowHeight - Height);

        /// <summary>
        /// Fits the view: the erection period (what the 3D view plays) by default, the whole plan of the hull (dock stay
        /// to delivery) with the "전체 일정" button; a little margin either side.
        /// </summary>
        private void FitAll()
        {
            if (_fit != null) _fit.text = _showAll ? "탑재 구간" : "전체 일정";
            if (_timeline == null || Width <= 0f)
            {
                return;
            }

            _needsFit = false;
            var schedule = _playback.Schedule;
            var from = _showAll ? _timeline.From : schedule.Start;
            var to = _showAll ? _timeline.To : schedule.Start.AddDays(Math.Max(1f, schedule.Duration));
            var span = Math.Max(1.0, (to - from).TotalDays);
            _daysPerPixel = Math.Max(MinDaysPerPixel, span * 1.06 / Width);
            _viewStart = DaysOf(from) - span * 0.03;
        }

        private double MaxDaysPerPixel => Width > 0f && _timeline != null ? Math.Max(1.0, (_timeline.To - _timeline.From).TotalDays) * 1.5 / Width : 10;

        private void Zoom(float factor, float aroundX)
        {
            var previous = _daysPerPixel;
            var pivot = _viewStart + aroundX * previous;
            _daysPerPixel = Math.Max(MinDaysPerPixel, Math.Min(MaxDaysPerPixel, _daysPerPixel * factor));
            _viewStart = pivot - aroundX * _daysPerPixel;
            if (_daysPerPixel != previous)
            {
                _pendingZoomRatio *= _daysPerPixel / previous;
                _applyZoomAt = Time.unscaledTime + 0.12f;
            }
        }

        private void Scroll(float pixels)
        {
            if (_timeline == null) return;
            _scrollY = Mathf.Clamp(_scrollY + pixels, 0f, MaxScroll);
            _manualUntil = Time.unscaledTime + ManualHoldSeconds;
            UpdateView();
        }

        /// <summary>Keeps the simulation date in view and the row of the block being lifted on screen.</summary>
        private void Follow()
        {
            var x = X(Current);
            if (x < Width * 0.08f || x > Width * 0.85f)
            {
                _viewStart = DaysOf(Current) - Width * 0.25f * _daysPerPixel;
            }

            var lifting = _playback.Schedule.Lifting().FirstOrDefault()?.Block.block;
            _focusBlock = lifting;
            var index = lifting != null ? _timeline.Rows.FindIndex(r => r.Block == lifting) : -1;
            if (index >= 0)
            {
                var top = AxisHeight + index * RowHeight - _scrollY;
                if (top < AxisHeight || top + RowHeight > Height)
                {
                    _scrollY = Mathf.Clamp(AxisHeight + index * RowHeight - (Height + AxisHeight) * 0.5f, 0f, MaxScroll);
                }
            }
        }

        /// <summary>Moves row names and axis labels to the current view and repaints the chart.</summary>
        private void UpdateView()
        {
            if (_timeline == null || _canvas == null)
            {
                return;
            }

            _scrollY = Mathf.Clamp(_scrollY, 0f, MaxScroll);
            for (var i = 0; i < _rowLabels.Count; i++)
            {
                var top = AxisHeight + i * RowHeight - _scrollY;
                var label = _rowLabels[i];
                label.style.top = top;
                label.style.display = top < AxisHeight - RowHeight * 0.5f || top > Height ? DisplayStyle.None : DisplayStyle.Flex;
                label.EnableInClassList("tw-tl-name--focus", _timeline.Rows[i].Block != null && _timeline.Rows[i].Block == _focusBlock);
            }

            var ticks = Ticks().ToList();
            while (_tickLabels.Count < ticks.Count)
            {
                var label = new Label { pickingMode = PickingMode.Ignore };
                label.AddToClassList("tw-tl-tick");
                _ticks.Add(label);
                _tickLabels.Add(label);
            }

            for (var i = 0; i < _tickLabels.Count; i++)
            {
                var label = _tickLabels[i];
                if (i >= ticks.Count)
                {
                    label.style.display = DisplayStyle.None;
                    continue;
                }

                var x = X(ticks[i].At);
                label.style.display = x < 0f ? DisplayStyle.None : DisplayStyle.Flex;   // no half-cut label at the left edge
                label.text = ticks[i].Text;
                label.style.left = x + 3f;
                label.EnableInClassList("tw-tl-tick--major", ticks[i].Major);
            }

            _canvas.MarkDirtyRepaint();
        }

        /// <summary>Axis ticks for the zoom level: hours, days, weeks or months (major = a new day / month / year).</summary>
        private IEnumerable<(DateTime At, string Text, bool Major)> Ticks()
        {
            if (Width <= 0f)
            {
                yield break;
            }

            var from = At(0f);
            var to = At(Width);
            var minDays = MinPixelsPerTick * _daysPerPixel;
            var hours = new[] { 1, 2, 3, 6, 12 };
            var hourStep = hours.FirstOrDefault(h => h / 24.0 >= minDays);
            if (hourStep > 0)
            {
                for (var t = from.Date.AddHours(Math.Floor(from.TimeOfDay.TotalHours / hourStep) * hourStep); t <= to; t = t.AddHours(hourStep))
                {
                    var major = t.TimeOfDay == TimeSpan.Zero;
                    yield return (t, major ? t.ToString("MM-dd ddd") : t.ToString("HH:mm"), major);
                }

                yield break;
            }

            var days = new[] { 1, 2, 7, 14 };
            var dayStep = days.FirstOrDefault(d => d >= minDays);
            if (dayStep > 0)
            {
                var start = from.Date;
                if (dayStep == 7 || dayStep == 14) start = start.AddDays(-(((int)start.DayOfWeek + 6) % 7));   // Mondays
                for (var t = start; t <= to; t = t.AddDays(dayStep))
                {
                    var major = t.Day == 1;
                    yield return (t, major || t == start ? t.ToString("yyyy-MM-dd") : t.ToString(dayStep == 1 ? "dd ddd" : "MM-dd"), major);
                }

                yield break;
            }

            var months = new[] { 1, 2, 3, 6, 12 }.FirstOrDefault(m => m * 30.4 >= minDays);
            if (months == 0) months = 12;
            for (var t = new DateTime(from.Year, (from.Month - 1) / months * months + 1, 1); t <= to; t = t.AddMonths(months))
            {
                yield return (t, t.Month == 1 ? t.ToString("yyyy") : t.ToString("yyyy-MM"), t.Month == 1);
            }
        }

        // ------------------------------------------------------------------ chart

        private void Draw(MeshGenerationContext mgc)
        {
            if (_timeline == null || _playback == null)
            {
                return;
            }

            var p = mgc.painter2D;
            var w = Width;
            var h = Height;
            var now = Current;

            // Rows: stripes, group separators, the lifted block's row.
            for (var i = 0; i < _timeline.Rows.Count; i++)
            {
                var top = AxisHeight + i * RowHeight - _scrollY;
                if (top + RowHeight < AxisHeight || top > h) continue;
                var row = _timeline.Rows[i];
                if (row.Block != null && row.Block == _focusBlock) Rect(p, 0, top, w, RowHeight, Focus);
                else if (i % 2 == 1) Rect(p, 0, top, w, RowHeight, Stripe);
                if (i > 0 && _timeline.Rows[i - 1].Group != row.Group) Rect(p, 0, top, w, 1f, GroupLine);
            }

            foreach (var tick in Ticks())
            {
                Rect(p, X(tick.At), 0, 1f, h, tick.Major ? GridStrong : Grid);
            }

            // Outside the simulated span (the 3D view holds still there).
            var simFrom = X(_playback.Schedule.Start);
            var simTo = X(_playback.Schedule.Start.AddDays(_playback.Schedule.Duration));
            if (_ship.Synced && YardSimSync.Active?.State != null)
            {
                // Shared replay: its own range (first stocking stay to the last lift) is what plays.
                simFrom = X(YardSimState.ParseTime(YardSimSync.Active.State.scenario.start));
                simTo = X(YardSimState.ParseTime(YardSimSync.Active.State.scenario.end));
            }
            if (simFrom > 0f) Rect(p, 0, AxisHeight, Mathf.Min(simFrom, w), h - AxisHeight, OutOfRange);
            if (simTo < w) Rect(p, Mathf.Max(0f, simTo), AxisHeight, w - Mathf.Max(0f, simTo), h - AxisHeight, OutOfRange);

            for (var i = 0; i < _timeline.Rows.Count; i++)
            {
                var top = AxisHeight + i * RowHeight - _scrollY;
                if (top + RowHeight < AxisHeight || top > h) continue;
                foreach (var item in _timeline.Rows[i].Items)
                {
                    var x0 = X(item.Start);
                    var x1 = X(item.End);
                    if (x1 < 0f || x0 > w) continue;
                    var colour = Colors[item.Kind];
                    if (item.End <= now) colour = Color.Lerp(colour, new Color(0.35f, 0.38f, 0.42f), 0.55f);   // done
                    else if (!item.Covers(now)) colour.a = 0.78f;                                              // ahead
                    if (item.Milestone)
                    {
                        Diamond(p, Mathf.Max(x0, (x0 + x1) * 0.5f), top + RowHeight * 0.5f, 5.5f, colour);
                        continue;
                    }

                    var left = Mathf.Max(x0, -2f);
                    var right = Mathf.Min(Mathf.Max(x1, x0 + 2f), w + 2f);
                    var inset = item.Kind == HullTimeline.Kind.Stock ? BarInset + 2f : BarInset;
                    Rect(p, left, top + inset, right - left, RowHeight - inset * 2f, colour);
                    if (item.Covers(now) && item.Kind == HullTimeline.Kind.Lift)
                    {
                        // Lift progress: the done part brighter.
                        var done = Mathf.Clamp(X(now), left, right);
                        Rect(p, left, top + inset, done - left, RowHeight - inset * 2f, Color.Lerp(colour, Color.white, 0.35f));
                    }
                }
            }

            // Axis band over the rows, then the simulation date line.
            Rect(p, 0, 0, w, AxisHeight, new Color(0.08f, 0.10f, 0.13f, 0.96f));
            Rect(p, 0, AxisHeight - 1f, w, 1f, GridStrong);
            var cx = X(now);
            if (cx >= -2f && cx <= w + 2f)
            {
                Rect(p, cx - 1f, 0, 2f, h, CursorColor);
                p.fillColor = CursorColor;
                p.BeginPath();
                p.MoveTo(new Vector2(cx - 6f, 0f));
                p.LineTo(new Vector2(cx + 6f, 0f));
                p.LineTo(new Vector2(cx, 8f));
                p.ClosePath();
                p.Fill();
            }
        }

        private static void Rect(Painter2D p, float x, float y, float w, float h, Color colour)
        {
            if (w <= 0f || h <= 0f) return;
            p.fillColor = colour;
            p.BeginPath();
            p.MoveTo(new Vector2(x, y));
            p.LineTo(new Vector2(x + w, y));
            p.LineTo(new Vector2(x + w, y + h));
            p.LineTo(new Vector2(x, y + h));
            p.ClosePath();
            p.Fill();
        }

        private static void Diamond(Painter2D p, float x, float y, float r, Color colour)
        {
            p.fillColor = colour;
            p.BeginPath();
            p.MoveTo(new Vector2(x, y - r));
            p.LineTo(new Vector2(x + r, y));
            p.LineTo(new Vector2(x, y + r));
            p.LineTo(new Vector2(x - r, y));
            p.ClosePath();
            p.Fill();
        }

        // ------------------------------------------------------------------ input

        /// <summary>
        /// Wheel up/down zooms around the pointer; a sideways swipe (trackpad, tilt wheel) pans in time; Shift scrolls rows.
        /// Deltas are capped so a browser's large wheel steps zoom a few notches, not to the limit.
        /// </summary>
        private void OnWheel(WheelEvent evt)
        {
            if (_timeline == null) return;
            var dx = Mathf.Clamp(evt.delta.x, -6f, 6f);
            var dy = Mathf.Clamp(evt.delta.y, -3f, 3f);
            if (evt.shiftKey)
            {
                Scroll((Mathf.Abs(dy) > 0f ? dy : dx) * RowHeight);
            }
            else
            {
                if (Mathf.Abs(dx) > Mathf.Abs(dy))
                {
                    _viewStart += dx * 24f * _daysPerPixel;
                }
                else
                {
                    Zoom(Mathf.Pow(1.18f, dy), evt.localMousePosition.x);
                }

                _manualUntil = Time.unscaledTime + ManualHoldSeconds;
                UpdateView();
            }

            evt.StopPropagation();
        }

        private void OnPointerDown(PointerDownEvent evt)
        {
            if (_timeline == null || evt.button > 0) return;
            _dragging = true;
            _moved = false;
            _dragFrom = evt.localPosition;
            _dragView = _viewStart;
            _dragScroll = _scrollY;
            _canvas.CapturePointer(evt.pointerId);
            evt.StopPropagation();
        }

        private void OnPointerMove(PointerMoveEvent evt)
        {
            if (_timeline == null) return;
            var position = (Vector2)evt.localPosition;
            if (_dragging && _canvas.HasPointerCapture(evt.pointerId))
            {
                var delta = position - _dragFrom;
                if (delta.sqrMagnitude > 16f) _moved = true;
                if (!_moved) return;
                _viewStart = _dragView - delta.x * _daysPerPixel;
                _scrollY = Mathf.Clamp(_dragScroll - delta.y, 0f, MaxScroll);
                _manualUntil = Time.unscaledTime + ManualHoldSeconds;
                ShowHover(null, position);
                UpdateView();
                return;
            }

            ShowHover(Pick(position), position);
        }

        private void OnPointerUp(PointerUpEvent evt)
        {
            if (!_dragging) return;
            _dragging = false;
            if (_canvas.HasPointerCapture(evt.pointerId)) _canvas.ReleasePointer(evt.pointerId);
            if (!_moved && _playback != null)
            {
                // A click: the simulation goes to that date (clamped to the simulated span).
                _playback.Seek((float)(At(evt.localPosition.x) - _playback.Schedule.Start).TotalDays);
                _manualUntil = Time.unscaledTime + ManualHoldSeconds;
                RefreshTexts();
                UpdateView();
            }
        }

        private (HullTimeline.Row Row, HullTimeline.Item Item)? Pick(Vector2 position)
        {
            if (position.y < AxisHeight) return null;
            var index = Mathf.FloorToInt((position.y - AxisHeight + _scrollY) / RowHeight);
            if (index < 0 || index >= _timeline.Rows.Count) return null;
            var row = _timeline.Rows[index];
            var t = At(position.x);
            var slack = TimeSpan.FromDays(4 * _daysPerPixel);   // thin bars and marks are still easy to hit
            var item = row.Items.Where(i => i.Start - slack <= t && t < i.End + slack)
                .OrderBy(i => i.Covers(t) ? 0 : 1).ThenBy(i => i.Kind == HullTimeline.Kind.Stock ? 1 : 0).FirstOrDefault();
            return item != null ? ((HullTimeline.Row, HullTimeline.Item)?)(row, item) : null;
        }

        private void ShowHover((HullTimeline.Row Row, HullTimeline.Item Item)? hit, Vector2 position)
        {
            if (_hover == null) return;
            if (hit == null)
            {
                _hover.style.display = DisplayStyle.None;
                return;
            }

            var (row, item) = hit.Value;
            _hover.text = (row.Block != null ? row.Block + " · " : string.Empty) + item.Title + "\n" + item.Detail;
            _hover.style.display = DisplayStyle.Flex;
            var left = Mathf.Min(position.x + 14f, Mathf.Max(0f, Width - 280f));
            _hover.style.left = left + (_names?.layout.width ?? 0f);
            _hover.style.top = Mathf.Max(0f, position.y - 44f);
        }

        // ------------------------------------------------------------------ texts

        /// <summary>Date, what is planned for it, play state and speed (bar and banner).</summary>
        private void RefreshTexts()
        {
            if (_playback == null || _timeline == null)
            {
                return;
            }

            var schedule = _playback.Schedule;
            var now = _ship.CurrentDate;
            var dayText = now.ToString("yyyy-MM-dd (ddd)", System.Globalization.CultureInfo.GetCultureInfo("ko-KR"));
            var sync = YardSimSync.Active;
            var day = (sync != null && sync.Synced ? "공유 재생 · " : "D+" + Mathf.FloorToInt(schedule.Cursor) + " · ") +
                      "탑재 " + schedule.ErectedCount + "/" + schedule.Entries.Count;
            var today = sync != null && sync.Synced ? sync.Activity : Today(now);
            if (_date != null) _date.text = dayText;
            if (_today != null) _today.text = day + " · " + today;
            if (_bannerDate != null) _bannerDate.text = dayText + "  " + day;
            if (_bannerText != null) _bannerText.text = today + (sync != null && sync.Synced ? "\n" + sync.Status : string.Empty);
            if (_play != null) _play.text = _playback.Playing ? "일시정지" : schedule.Finished ? "완료" : "재생";
            if (_speed != null) _speed.text = "×" + _playback.SpeedMultiplier.ToString("0.##");
        }

        /// <summary>The planned work at an instant: lifts first, then crane work, hull events and stocked blocks.</summary>
        private string Today(DateTime now)
        {
            var at = _timeline.At(now);
            var parts = new List<string>();
            foreach (var (row, item) in at.Where(x => x.Item.Kind == HullTimeline.Kind.Lift))
            {
                var progress = (now - item.Start).TotalHours / Math.Max(0.01, (item.End - item.Start).TotalHours);
                parts.Add(HullTimeline.ShortBlock(row.Block) + " 탑재 중 " + Mathf.RoundToInt((float)progress * 100f) + "%");
            }

            parts.AddRange(at.Where(x => x.Item.Kind == HullTimeline.Kind.Crane).Select(x => x.Item.Title).Distinct().Take(2));
            parts.AddRange(at.Where(x => x.Item.Kind == HullTimeline.Kind.Shift || x.Item.Kind == HullTimeline.Kind.Delivery)
                .Select(x => x.Item.Title));
            var berth = at.FirstOrDefault(x => x.Item.Kind == HullTimeline.Kind.Berth || x.Item.Kind == HullTimeline.Kind.Dock);
            if (berth.Item != null) parts.Add(berth.Item.Title);
            var stocked = at.Count(x => x.Item.Kind == HullTimeline.Kind.Stock);
            if (stocked > 0) parts.Add("야드 적치 " + stocked + "개");
            if (parts.Count == 0) parts.Add(now < _playback.Schedule.Start ? "탑재 전 준비 기간" : "예정된 작업 없음");
            return string.Join(" · ", parts);
        }
    }
}
