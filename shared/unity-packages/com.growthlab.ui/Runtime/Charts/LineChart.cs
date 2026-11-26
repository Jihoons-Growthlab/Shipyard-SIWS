using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using UnityEngine.UIElements;

namespace GrowthLab.UI.Charts
{
    /// <summary>
    /// Lines over categories with dots, an optional soft area under each line and a dashed "now" marker. While the chart
    /// animates the lines are drawn from left to right. UXML: <c>&lt;gl:LineChart /&gt;</c>, data from code.
    /// </summary>
    [UxmlElement]
    public partial class LineChart : ChartElement
    {
        private const float AxisWidth = 30f, CategoryHeight = 16f, Top = 12f;
        private LineChartSpec _spec = new LineChartSpec();

        public override ChartSpec Spec => _spec;

        public float AxisMax => !float.IsNaN(_spec.Max) && _spec.Max > 0f
            ? _spec.Max
            : ChartScale.NiceMax(_spec.Series.SelectMany(s => s.Values).Where(v => !float.IsNaN(v)).DefaultIfEmpty(0f).Max());

        public override void SetSpec(ChartSpec spec)
        {
            _spec = spec as LineChartSpec ?? throw new ArgumentException("LineChart needs a LineChartSpec", nameof(spec));
            ApplyCommon(_spec, _spec.Series.Select(s => (s.Name, s.Color)));
        }

        private Rect Area(Rect r) => Rect.MinMaxRect(r.xMin + AxisWidth, r.yMin + Top, r.xMax - 6f, r.yMax - CategoryHeight);

        private Vector2 Point(Rect area, int index, float value)
        {
            var n = Mathf.Max(2, _spec.Categories.Count);
            return new Vector2(area.xMin + area.width * index / (n - 1), area.yMax - area.height * ChartScale.Fraction(value, AxisMax));
        }

        protected override void DrawPlot(Painter2D p, Rect rect)
        {
            if (_spec.IsEmpty) return;
            var area = Area(rect);
            foreach (var tick in ChartScale.Ticks(AxisMax))
            {
                var y = area.yMax - area.height * ChartScale.Fraction(tick, AxisMax);
                Line(p, new Vector2(area.xMin, y), new Vector2(area.xMax, y), GridColor);
            }

            var limitX = area.xMin + area.width * Reveal;
            foreach (var series in _spec.Series)
            {
                var points = new List<Vector2>();
                for (var i = 0; i < _spec.Categories.Count; i++)
                {
                    if (!float.IsNaN(series[i])) points.Add(Point(area, i, series[i]));
                }

                var shown = Clip(points, limitX);
                if (shown.Count < 2) continue;
                if (_spec.Area)
                {
                    p.fillColor = WithAlpha(series.Color, 0.16f);
                    p.BeginPath();
                    p.MoveTo(new Vector2(shown[0].x, area.yMax));
                    foreach (var point in shown) p.LineTo(point);
                    p.LineTo(new Vector2(shown[shown.Count - 1].x, area.yMax));
                    p.ClosePath();
                    p.Fill();
                }

                p.strokeColor = series.Color;
                p.lineWidth = 2f;
                p.lineJoin = LineJoin.Round;
                p.lineCap = LineCap.Round;
                p.BeginPath();
                p.MoveTo(shown[0]);
                for (var i = 1; i < shown.Count; i++) p.LineTo(shown[i]);
                p.Stroke();
                if (points.Count <= 24)
                {
                    foreach (var point in points.Where(pt => pt.x <= limitX + 0.5f)) Circle(p, point, 2.6f, series.Color);
                }
            }

            if (_spec.MarkerIndex >= 0 && _spec.MarkerIndex < _spec.Categories.Count)
            {
                var x = Point(area, _spec.MarkerIndex, 0f).x;
                DashedLine(p, new Vector2(x, area.yMin - 6f), new Vector2(x, area.yMax), ChartPalette.Alarm, 4f, 3f, 1.2f);
            }
        }

        /// <summary>The polyline up to x (the last segment cut at x).</summary>
        private static List<Vector2> Clip(List<Vector2> points, float limitX)
        {
            var result = new List<Vector2>();
            for (var i = 0; i < points.Count; i++)
            {
                if (points[i].x <= limitX)
                {
                    result.Add(points[i]);
                    continue;
                }

                if (i > 0)
                {
                    var a = points[i - 1];
                    var t = Mathf.InverseLerp(a.x, points[i].x, limitX);
                    result.Add(Vector2.Lerp(a, points[i], t));
                }

                break;
            }

            return result;
        }

        protected override void PlaceLabels(Rect rect)
        {
            var area = Area(rect);
            foreach (var tick in ChartScale.Ticks(AxisMax))
            {
                var y = area.yMax - area.height * ChartScale.Fraction(tick, AxisMax);
                PlaceLabel(ChartScale.Short(tick) + (Mathf.Approximately(tick, AxisMax) ? _spec.Unit : string.Empty),
                    new Vector2(area.xMin - 4f, y), new Vector2(1f, 0.5f), "axis");
            }

            var n = _spec.Categories.Count;
            var every = Mathf.Max(1, Mathf.CeilToInt(n * 44f / Mathf.Max(1f, area.width)));
            for (var i = 0; i < n; i += every)
            {
                PlaceLabel(_spec.Categories[i], new Vector2(Point(area, i, 0f).x, area.yMax + 2f), new Vector2(i == 0 ? 0f : i >= n - every ? 1f : 0.5f, 0f), "category");
            }

            if (_spec.MarkerIndex >= 0 && _spec.MarkerIndex < n && !string.IsNullOrEmpty(_spec.MarkerLabel))
            {
                PlaceLabel(_spec.MarkerLabel, new Vector2(Point(area, _spec.MarkerIndex, 0f).x, area.yMin - 7f), new Vector2(0.5f, 1f), "marker");
            }
        }

        protected override string HitTest(Vector2 local)
        {
            var area = Area(PlotRect);
            var n = _spec.Categories.Count;
            if (n < 2 || local.x < area.xMin - 8f || local.x > area.xMax + 8f) return null;
            var i = Mathf.Clamp(Mathf.RoundToInt((local.x - area.xMin) / area.width * (n - 1)), 0, n - 1);
            var lines = _spec.Series.Where(s => !float.IsNaN(s[i])).Select(s => s.Name + " " + ChartScale.Short(s[i]) + _spec.Unit);
            return _spec.Categories[i] + "\n" + string.Join("\n", lines);
        }
    }
}
