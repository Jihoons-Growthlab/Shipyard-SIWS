using System;
using System.Linq;
using UnityEngine;
using UnityEngine.UIElements;

namespace GrowthLab.UI.Charts
{
    /// <summary>
    /// Grouped vertical bars with a 0-based value axis, category labels, an optional dashed threshold and value text.
    /// Bars grow up from the axis while the chart animates. UXML: <c>&lt;gl:BarChart /&gt;</c>, data from code.
    /// </summary>
    [UxmlElement]
    public partial class BarChart : ChartElement
    {
        private const float AxisWidth = 30f, CategoryHeight = 16f, Top = 12f;
        private BarChartSpec _spec = new BarChartSpec();

        public override ChartSpec Spec => _spec;

        public float AxisMax => !float.IsNaN(_spec.Max) && _spec.Max > 0f
            ? _spec.Max
            : ChartScale.NiceMax(Math.Max(_spec.Series.SelectMany(s => s.Values).Where(v => !float.IsNaN(v)).DefaultIfEmpty(0f).Max(),
                float.IsNaN(_spec.Threshold) ? 0f : _spec.Threshold));

        public override void SetSpec(ChartSpec spec)
        {
            _spec = spec as BarChartSpec ?? throw new ArgumentException("BarChart needs a BarChartSpec", nameof(spec));
            ApplyCommon(_spec, _spec.Series.Count > 1 || !string.IsNullOrEmpty(_spec.ThresholdLabel)
                ? _spec.Series.Select(s => (s.Name, s.Color)).Concat(string.IsNullOrEmpty(_spec.ThresholdLabel)
                    ? Enumerable.Empty<(string, Color)>()
                    : new[] { (_spec.ThresholdLabel, ChartPalette.Alarm) })
                : null);
        }

        private Rect Area(Rect r) => Rect.MinMaxRect(r.xMin + AxisWidth, r.yMin + Top, r.xMax - 2f, r.yMax - CategoryHeight);

        private Rect BarRect(Rect area, int category, int series, float value, float reveal)
        {
            var n = Mathf.Max(1, _spec.Categories.Count);
            var band = area.width / n;
            var groupWidth = band * 0.72f;
            var count = Mathf.Max(1, _spec.Series.Count);
            var width = Mathf.Min(28f, groupWidth / count);
            var x = area.xMin + band * category + (band - width * count) / 2f + width * series;
            var h = area.height * ChartScale.Fraction(value, AxisMax) * reveal;
            return new Rect(x + 1f, area.yMax - h, Mathf.Max(1f, width - 2f), h);
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

            for (var c = 0; c < _spec.Categories.Count; c++)
            {
                for (var s = 0; s < _spec.Series.Count; s++)
                {
                    var value = _spec.Series[s][c];
                    if (float.IsNaN(value) || value <= 0f) continue;
                    var bar = BarRect(area, c, s, value, Reveal);
                    RoundRect(p, bar, 3f, _spec.Series[s].Color);
                    // Square the bottom corners: a plain strip over the rounded base.
                    RoundRect(p, new Rect(bar.x, bar.yMax - Mathf.Min(3f, bar.height), bar.width, Mathf.Min(3f, bar.height)), 0f, _spec.Series[s].Color);
                }
            }

            if (!float.IsNaN(_spec.Threshold))
            {
                var y = area.yMax - area.height * ChartScale.Fraction(_spec.Threshold, AxisMax);
                DashedLine(p, new Vector2(area.xMin, y), new Vector2(area.xMin + area.width * Reveal, y), ChartPalette.Alarm, 5f, 3f, 1.4f);
            }
        }

        protected override void PlaceLabels(Rect rect)
        {
            var area = Area(rect);
            foreach (var tick in ChartScale.Ticks(AxisMax))
            {
                var y = area.yMax - area.height * ChartScale.Fraction(tick, AxisMax);
                PlaceLabel(ChartScale.Short(tick), new Vector2(area.xMin - 4f, y), new Vector2(1f, 0.5f), "axis");
            }

            var band = area.width / Mathf.Max(1, _spec.Categories.Count);
            var every = Mathf.Max(1, Mathf.CeilToInt(_spec.Categories.Count * 46f / Mathf.Max(1f, area.width)));
            for (var c = 0; c < _spec.Categories.Count; c += every)
            {
                var label = PlaceLabel(_spec.Categories[c], new Vector2(area.xMin + band * (c + 0.5f), area.yMax + 2f), new Vector2(0.5f, 0f), "category");
                label.style.maxWidth = band * every;
            }

            if (_spec.ShowValues && _spec.Series.Count == 1 && _spec.Categories.Count <= 10)
            {
                for (var c = 0; c < _spec.Categories.Count; c++)
                {
                    var value = _spec.Series[0][c];
                    if (float.IsNaN(value)) continue;
                    var bar = BarRect(area, c, 0, value, 1f);
                    PlaceLabel(ChartScale.Short(value) + _spec.Unit, new Vector2(bar.center.x, bar.yMin - 1f), new Vector2(0.5f, 1f), "value");
                }
            }
        }

        protected override string HitTest(Vector2 local)
        {
            var area = Area(PlotRect);
            if (!area.Contains(local) || _spec.Categories.Count == 0) return null;
            var c = Mathf.Clamp((int)((local.x - area.xMin) / (area.width / _spec.Categories.Count)), 0, _spec.Categories.Count - 1);
            var lines = _spec.Series.Where(s => !float.IsNaN(s[c]))
                .Select(s => (_spec.Series.Count > 1 ? s.Name + " " : string.Empty) + ChartScale.Short(s[c]) + _spec.Unit);
            return _spec.Categories[c] + "\n" + string.Join("\n", lines);
        }
    }
}
