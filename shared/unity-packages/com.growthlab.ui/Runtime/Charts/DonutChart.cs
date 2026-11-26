using System;
using System.Linq;
using UnityEngine;
using UnityEngine.UIElements;

namespace GrowthLab.UI.Charts
{
    /// <summary>
    /// Ring of slices on a track with centre text (e.g. "72%") and a caption; the slices sweep clockwise from the top
    /// while the chart animates. The legend lists the slices with their values. UXML: <c>&lt;gl:DonutChart /&gt;</c>.
    /// </summary>
    [UxmlElement]
    public partial class DonutChart : ChartElement
    {
        private const float Thickness = 11f;
        private DonutChartSpec _spec = new DonutChartSpec();

        public override ChartSpec Spec => _spec;

        public override void SetSpec(ChartSpec spec)
        {
            _spec = spec as DonutChartSpec ?? throw new ArgumentException("DonutChart needs a DonutChartSpec", nameof(spec));
            ApplyCommon(_spec, _spec.Slices.Select(s => (s.Label + " " + ChartScale.Short(s.Value), s.Color)));
        }

        private (Vector2 Centre, float Radius) Ring(Rect r)
        {
            var radius = Mathf.Max(4f, Mathf.Min(r.width, r.height) / 2f - Thickness / 2f - 2f);
            return (r.center, radius);
        }

        protected override void DrawPlot(Painter2D p, Rect rect)
        {
            if (_spec.IsEmpty) return;
            var (centre, radius) = Ring(rect);
            p.lineWidth = Thickness;
            p.lineCap = LineCap.Butt;
            p.strokeColor = TrackColor;
            p.BeginPath();
            p.Arc(centre, radius, Angle.Degrees(0f), Angle.Degrees(360f));
            p.Stroke();

            var total = _spec.RingTotal;
            var start = -90f;
            p.lineCap = _spec.Slices.Count == 1 ? LineCap.Round : LineCap.Butt;
            foreach (var slice in _spec.Slices)
            {
                var sweep = 360f * ChartScale.Fraction(slice.Value, total) * Reveal;
                if (sweep > 0.2f)
                {
                    p.strokeColor = slice.Color;
                    p.BeginPath();
                    p.Arc(centre, radius, Angle.Degrees(start), Angle.Degrees(start + sweep));
                    p.Stroke();
                }

                start += 360f * ChartScale.Fraction(slice.Value, total);
            }
        }

        protected override void PlaceLabels(Rect rect)
        {
            var (centre, _) = Ring(rect);
            var hasCaption = !string.IsNullOrEmpty(_spec.CenterCaption);
            if (!string.IsNullOrEmpty(_spec.CenterText))
            {
                PlaceLabel(_spec.CenterText, centre + new Vector2(0f, hasCaption ? -6f : 0f), new Vector2(0.5f, 0.5f), "center");
            }

            if (hasCaption)
            {
                PlaceLabel(_spec.CenterCaption, centre + new Vector2(0f, 12f), new Vector2(0.5f, 0.5f), "center-caption");
            }
        }

        protected override string HitTest(Vector2 local)
        {
            var (centre, radius) = Ring(PlotRect);
            var d = Vector2.Distance(local, centre);
            if (d < radius - Thickness || d > radius + Thickness) return null;
            // Angle clockwise from the top (y grows downwards).
            var angle = (Mathf.Atan2(local.y - centre.y, local.x - centre.x) * Mathf.Rad2Deg + 90f + 360f) % 360f;
            var total = _spec.RingTotal;
            var start = 0f;
            foreach (var slice in _spec.Slices)
            {
                var sweep = 360f * ChartScale.Fraction(slice.Value, total);
                if (angle >= start && angle < start + sweep)
                {
                    return slice.Label + " " + ChartScale.Short(slice.Value) + " (" + Mathf.RoundToInt(sweep / 3.6f) + "%)";
                }

                start += sweep;
            }

            return "남음 " + ChartScale.Short(Mathf.Max(0f, total - _spec.Slices.Sum(s => s.Value)));
        }
    }
}
