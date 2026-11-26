using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using UnityEngine.UIElements;

namespace GrowthLab.UI.Charts
{
    /// <summary>
    /// Gantt rows: a date axis on top, one row per <see cref="TimelineItem.Row"/>, bars (solid = confirmed, outline =
    /// planned) with an optional done fraction, diamonds for one-day milestones and a dashed "now" line. Bars extend
    /// from their start while the chart animates. The pointer shows the item under it. UXML: <c>&lt;gl:TimelineChart /&gt;</c>.
    /// </summary>
    [UxmlElement]
    public partial class TimelineChart : ChartElement
    {
        private const float NameWidth = 78f, AxisHeight = 16f;
        private TimelineChartSpec _spec = new TimelineChartSpec();
        private List<string> _rows = new List<string>();

        public override ChartSpec Spec => _spec;

        public DateTime From { get; private set; }
        public DateTime To { get; private set; }

        public override void SetSpec(ChartSpec spec)
        {
            _spec = spec as TimelineChartSpec ?? throw new ArgumentException("TimelineChart needs a TimelineChartSpec", nameof(spec));
            _rows = _spec.Rows.ToList();
            (From, To) = Range(_spec);
            ApplyCommon(_spec, null);
        }

        /// <summary>The spec's range, else the items' (and now's) range padded by 4 % each side, at least two days.</summary>
        public static (DateTime From, DateTime To) Range(TimelineChartSpec spec)
        {
            if (spec.From.HasValue && spec.To.HasValue && spec.To > spec.From)
            {
                return (spec.From.Value, spec.To.Value);
            }

            if (spec.Items.Count == 0)
            {
                var day = (spec.Now ?? DateTime.Today).Date;
                return (day.AddDays(-1), day.AddDays(1));
            }

            var from = spec.Items.Min(i => i.Start);
            var to = spec.Items.Max(i => i.Milestone ? i.Start.AddDays(1) : i.End > i.Start ? i.End : i.Start.AddDays(1));
            if (spec.Now.HasValue)
            {
                if (spec.Now.Value < from) from = spec.Now.Value;
                if (spec.Now.Value > to) to = spec.Now.Value;
            }

            if ((to - from).TotalDays < 2) to = from.AddDays(2);
            var pad = TimeSpan.FromTicks((to - from).Ticks / 25);
            return (spec.From ?? from - pad, spec.To ?? to + pad);
        }

        protected override float PlotHeight(ChartSpec spec) => AxisHeight + Mathf.Max(1, _rows.Count) * _spec.RowHeight + 4f;

        private Rect Area(Rect r) => Rect.MinMaxRect(r.xMin + NameWidth, r.yMin + AxisHeight, r.xMax - 4f, r.yMax - 2f);

        private float X(Rect area, DateTime t) => area.xMin + area.width * ChartScale.Fraction(t, From, To);

        private Rect ItemRect(Rect area, TimelineItem item, float reveal)
        {
            var row = Mathf.Max(0, _rows.IndexOf(item.Row ?? string.Empty));
            var top = area.yMin + row * _spec.RowHeight;
            var h = _spec.RowHeight * 0.56f;
            var y = top + (_spec.RowHeight - h) / 2f;
            if (item.Milestone)
            {
                var x = X(area, item.Start);
                return new Rect(x - h / 2f, y, h, h);
            }

            var x0 = X(area, item.Start);
            var x1 = X(area, item.End > item.Start ? item.End : item.Start.AddDays(1));
            return new Rect(x0, y, Mathf.Max(2f, (x1 - x0) * reveal), h);
        }

        protected override void DrawPlot(Painter2D p, Rect rect)
        {
            if (_spec.IsEmpty) return;
            var area = Area(rect);
            for (var r = 0; r < _rows.Count; r++)
            {
                if (r % 2 == 0) RoundRect(p, new Rect(rect.xMin, area.yMin + r * _spec.RowHeight, rect.width, _spec.RowHeight), 0f, WithAlpha(GridColor, 0.5f));
            }

            foreach (var tick in ChartScale.DateTicks(From, To).Ticks)
            {
                var x = X(area, tick);
                Line(p, new Vector2(x, area.yMin - 3f), new Vector2(x, area.yMax), GridColor);
            }

            foreach (var item in _spec.Items)
            {
                var box = ItemRect(area, item, Reveal);
                if (item.Milestone)
                {
                    var s = box.width / 2f * Mathf.Clamp01(Reveal * 1.6f);
                    var c = box.center;
                    p.fillColor = item.Color;
                    p.strokeColor = item.Color;
                    p.lineWidth = 1.5f;
                    p.BeginPath();
                    p.MoveTo(c + new Vector2(0, -s));
                    p.LineTo(c + new Vector2(s, 0));
                    p.LineTo(c + new Vector2(0, s));
                    p.LineTo(c + new Vector2(-s, 0));
                    p.ClosePath();
                    if (item.Hollow) p.Stroke();
                    else p.Fill();
                    continue;
                }

                if (item.Hollow)
                {
                    RoundRect(p, box, 3f, WithAlpha(item.Color, 0.14f));
                    RoundRectOutline(p, box, 3f, item.Color, 1.2f);
                }
                else
                {
                    RoundRect(p, box, 3f, WithAlpha(item.Color, float.IsNaN(item.Progress) ? 0.9f : 0.35f));
                }

                if (!float.IsNaN(item.Progress) && item.Progress > 0f)
                {
                    var full = ItemRect(area, item, 1f);
                    var done = new Rect(full.x, full.y, Mathf.Min(box.width, full.width * Mathf.Clamp01(item.Progress)), full.height);
                    RoundRect(p, done, 3f, item.Color);
                }
            }

            if (_spec.Now.HasValue && _spec.Now.Value >= From && _spec.Now.Value <= To)
            {
                var x = X(area, _spec.Now.Value);
                DashedLine(p, new Vector2(x, area.yMin - 4f), new Vector2(x, area.yMin + (area.height) * Reveal), ChartPalette.Alarm, 4f, 3f, 1.4f);
            }
        }

        protected override void PlaceLabels(Rect rect)
        {
            var area = Area(rect);
            for (var r = 0; r < _rows.Count; r++)
            {
                var label = PlaceLabel(_rows[r], new Vector2(rect.xMin + 4f, area.yMin + (r + 0.5f) * _spec.RowHeight), new Vector2(0f, 0.5f), "row");
                label.style.maxWidth = NameWidth - 8f;
            }

            var (ticks, format) = ChartScale.DateTicks(From, To);
            var every = Mathf.Max(1, Mathf.CeilToInt(ticks.Count * 40f / Mathf.Max(1f, area.width)));
            for (var i = 0; i < ticks.Count; i += every)
            {
                PlaceLabel(ticks[i].ToString(format), new Vector2(X(area, ticks[i]), rect.yMin), new Vector2(0.5f, 0f), "axis");
            }

            if (_spec.Now.HasValue && _spec.Now.Value >= From && _spec.Now.Value <= To && !string.IsNullOrEmpty(_spec.NowLabel))
            {
                PlaceLabel(_spec.NowLabel, new Vector2(X(area, _spec.Now.Value) + 3f, area.yMax - 1f), new Vector2(0f, 1f), "marker");
            }
        }

        protected override string HitTest(Vector2 local)
        {
            var area = Area(PlotRect);
            // Nearest item on the pointer's row that the pointer touches (milestones get a few pixels of slack).
            var row = Mathf.FloorToInt((local.y - area.yMin) / _spec.RowHeight);
            if (row < 0 || row >= _rows.Count) return null;
            var hit = _spec.Items.Where(i => (i.Row ?? string.Empty) == _rows[row])
                .Select(i => (Item: i, Box: ItemRect(area, i, 1f)))
                .Where(x => local.x >= x.Box.xMin - 4f && local.x <= x.Box.xMax + 4f)
                .OrderBy(x => Mathf.Abs(x.Box.center.x - local.x)).Select(x => x.Item).FirstOrDefault();
            if (hit == null) return null;
            var when = hit.Milestone || (hit.End - hit.Start).TotalDays <= 1.01
                ? hit.Start.ToString("yyyy-MM-dd")
                : hit.Start.ToString("yyyy-MM-dd") + " ~ " + hit.End.AddDays(-1).ToString("yyyy-MM-dd");
            return (hit.Label ?? hit.Row) + "\n" + when + (string.IsNullOrEmpty(hit.Detail) ? string.Empty : "\n" + hit.Detail) +
                   (float.IsNaN(hit.Progress) ? string.Empty : "\n진척 " + Mathf.RoundToInt(hit.Progress * 100f) + "%");
        }
    }
}
