using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UIElements;

namespace GrowthLab.UI.Charts
{
    /// <summary>
    /// Base of the chart controls: a header (title + legend), a plot drawn with <see cref="Painter2D"/>, a text layer
    /// of absolutely placed labels over the plot (axis ticks, categories, values), a caption and an empty state.
    /// Subclasses draw from their <see cref="ChartSpec"/> and place labels for the current plot size. Drawing reads
    /// <see cref="Reveal"/> (0–1) so <see cref="Animate"/> grows the chart in; a tooltip label follows the pointer for
    /// subclasses that report a hit (<see cref="HitTest"/>). Colours of grid and axis come from the USS custom
    /// properties <c>--gl-chart-grid</c> and <c>--gl-chart-track</c>.
    /// </summary>
    public abstract class ChartElement : VisualElement
    {
        public const string UssClassName = "gl-chart";

        private static readonly CustomStyleProperty<Color> GridProperty = new CustomStyleProperty<Color>("--gl-chart-grid");
        private static readonly CustomStyleProperty<Color> TrackProperty = new CustomStyleProperty<Color>("--gl-chart-track");

        private readonly Label _title, _caption, _empty, _tooltip;
        private readonly VisualElement _legend, _labels;
        private IVisualElementScheduledItem _animation;
        private float _animationStart, _animationSeconds;
        private Rect _laidOut;

        protected ChartElement()
        {
            AddToClassList(UssClassName);
            var header = new VisualElement { name = "header", pickingMode = PickingMode.Ignore };
            header.AddToClassList(UssClassName + "__header");
            _title = new Label { name = "title", pickingMode = PickingMode.Ignore };
            _title.AddToClassList(UssClassName + "__title");
            _legend = new VisualElement { name = "legend", pickingMode = PickingMode.Ignore };
            _legend.AddToClassList(UssClassName + "__legend");
            header.Add(_title);
            header.Add(_legend);
            Add(header);

            Plot = new VisualElement { name = "plot" };
            Plot.AddToClassList(UssClassName + "__plot");
            Plot.generateVisualContent += ctx => DrawPlot(ctx.painter2D, PlotRect);
            Plot.RegisterCallback<GeometryChangedEvent>(_ => RelayoutLabels());
            Plot.RegisterCallback<PointerMoveEvent>(e => ShowTooltip(e.localPosition));
            Plot.RegisterCallback<PointerLeaveEvent>(_ => HideTooltip());
            _labels = new VisualElement { name = "labels", pickingMode = PickingMode.Ignore };
            _labels.AddToClassList(UssClassName + "__labels");
            Plot.Add(_labels);
            _tooltip = new Label { name = "tooltip", pickingMode = PickingMode.Ignore };
            _tooltip.AddToClassList(UssClassName + "__tooltip");
            _tooltip.style.display = DisplayStyle.None;
            Plot.Add(_tooltip);
            Add(Plot);

            _empty = new Label { name = "empty", pickingMode = PickingMode.Ignore };
            _empty.AddToClassList(UssClassName + "__empty");
            Add(_empty);
            _caption = new Label { name = "caption", pickingMode = PickingMode.Ignore };
            _caption.AddToClassList(UssClassName + "__caption");
            Add(_caption);

            RegisterCallback<CustomStyleResolvedEvent>(OnCustomStyle);
        }

        public VisualElement Plot { get; }

        /// <summary>Drawn fraction, 0 (nothing) to 1 (complete). Eased while <see cref="Animate"/> runs.</summary>
        public float Reveal { get; private set; } = 1f;

        public bool IsAnimating => _animation != null && _animation.isActive;

        protected Color GridColor { get; private set; } = new Color(1f, 1f, 1f, 0.08f);
        protected Color TrackColor { get; private set; } = new Color(1f, 1f, 1f, 0.10f);

        public abstract ChartSpec Spec { get; }

        /// <summary>Plot area inside its padding, in plot-local coordinates.</summary>
        protected Rect PlotRect => new Rect(0f, 0f, Plot.contentRect.width, Plot.contentRect.height);

        /// <summary>Replaces the data (must be this chart's spec type) and redraws without animating.</summary>
        public abstract void SetSpec(ChartSpec spec);

        /// <summary>Draws the chart in from nothing over <paramref name="seconds"/>, after <paramref name="delayMs"/>.</summary>
        public void Animate(float seconds = 0.8f, long delayMs = 0)
        {
            _animation?.Pause();
            Reveal = 0f;
            _animationSeconds = Mathf.Max(0.01f, seconds);
            Plot.MarkDirtyRepaint();
            _animation = schedule.Execute(() =>
            {
                if (_animationStart < 0f) _animationStart = Time.realtimeSinceStartup;
                var t = (Time.realtimeSinceStartup - _animationStart) / _animationSeconds;
                Reveal = ChartScale.EaseOut(t);
                Plot.MarkDirtyRepaint();
                if (t >= 1f)
                {
                    Reveal = 1f;
                    _animation.Pause();
                }
            }).Every(16).StartingIn(delayMs);
            _animationStart = -1f;
        }

        /// <summary>Stops any animation and shows the whole chart.</summary>
        public void Complete()
        {
            _animation?.Pause();
            Reveal = 1f;
            Plot.MarkDirtyRepaint();
        }

        /// <summary>Common part of <see cref="SetSpec"/>: header, caption, empty state, height, legend, redraw.</summary>
        protected void ApplyCommon(ChartSpec spec, IEnumerable<(string Name, Color Color)> legend)
        {
            _title.text = spec.Title ?? string.Empty;
            _title.style.display = string.IsNullOrEmpty(spec.Title) ? DisplayStyle.None : DisplayStyle.Flex;
            _caption.text = spec.Caption ?? string.Empty;
            _caption.style.display = string.IsNullOrEmpty(spec.Caption) ? DisplayStyle.None : DisplayStyle.Flex;
            _empty.text = spec.EmptyText ?? string.Empty;
            _empty.style.display = spec.IsEmpty ? DisplayStyle.Flex : DisplayStyle.None;
            Plot.style.display = spec.IsEmpty ? DisplayStyle.None : DisplayStyle.Flex;
            Plot.style.height = PlotHeight(spec);

            _legend.Clear();
            if (legend != null)
            {
                foreach (var (name, color) in legend)
                {
                    if (string.IsNullOrEmpty(name)) continue;
                    var item = new VisualElement { pickingMode = PickingMode.Ignore };
                    item.AddToClassList(UssClassName + "__legend-item");
                    var swatch = new VisualElement { pickingMode = PickingMode.Ignore };
                    swatch.AddToClassList(UssClassName + "__swatch");
                    swatch.style.backgroundColor = color;
                    item.Add(swatch);
                    var text = new Label(name) { pickingMode = PickingMode.Ignore };
                    text.AddToClassList(UssClassName + "__legend-text");
                    item.Add(text);
                    _legend.Add(item);
                }
            }

            _laidOut = default;
            RelayoutLabels();
            Plot.MarkDirtyRepaint();
        }

        protected virtual float PlotHeight(ChartSpec spec) => spec.Height;

        protected abstract void DrawPlot(Painter2D painter, Rect rect);

        /// <summary>Places text for the given plot size (labels were cleared just before).</summary>
        protected abstract void PlaceLabels(Rect rect);

        /// <summary>Tooltip text for a point in the plot, or null.</summary>
        protected virtual string HitTest(Vector2 local) => null;

        /// <summary>A text label at a plot point. <paramref name="anchor"/>: (0,0) = top left of the text at the point, (0.5,1) = centred above it.</summary>
        protected Label PlaceLabel(string text, Vector2 at, Vector2 anchor, string modifier = null)
        {
            var label = new Label(text) { pickingMode = PickingMode.Ignore };
            label.AddToClassList(UssClassName + "__label");
            if (!string.IsNullOrEmpty(modifier)) label.AddToClassList(UssClassName + "__label--" + modifier);
            label.style.position = Position.Absolute;
            label.style.left = at.x;
            label.style.top = at.y;
            label.style.translate = new Translate(Length.Percent(-anchor.x * 100f), Length.Percent(-anchor.y * 100f));
            _labels.Add(label);
            return label;
        }

        private void RelayoutLabels()
        {
            var rect = PlotRect;
            if (float.IsNaN(rect.width) || rect.width <= 0f || rect == _laidOut)
            {
                return;
            }

            _laidOut = rect;
            _labels.Clear();
            if (Spec != null && !Spec.IsEmpty) PlaceLabels(rect);
        }

        private void ShowTooltip(Vector2 local)
        {
            var text = Spec != null && !Spec.IsEmpty ? HitTest(local) : null;
            if (string.IsNullOrEmpty(text))
            {
                HideTooltip();
                return;
            }

            _tooltip.text = text;
            _tooltip.style.display = DisplayStyle.Flex;
            var rect = PlotRect;
            // Above the pointer, flipped left of it past the middle so it stays inside the plot.
            _tooltip.style.left = local.x;
            _tooltip.style.top = Mathf.Max(0f, local.y - 6f);
            _tooltip.style.translate = new Translate(Length.Percent(local.x > rect.width / 2f ? -100f : 0f), Length.Percent(-100f));
            _tooltip.BringToFront();
        }

        private void HideTooltip() => _tooltip.style.display = DisplayStyle.None;

        private void OnCustomStyle(CustomStyleResolvedEvent e)
        {
            if (e.customStyle.TryGetValue(GridProperty, out var grid)) GridColor = grid;
            if (e.customStyle.TryGetValue(TrackProperty, out var track)) TrackColor = track;
            Plot.MarkDirtyRepaint();
        }

        // ------------------------------------------------------------------ drawing helpers

        protected static void Line(Painter2D p, Vector2 a, Vector2 b, Color color, float width = 1f)
        {
            p.strokeColor = color;
            p.lineWidth = width;
            p.BeginPath();
            p.MoveTo(a);
            p.LineTo(b);
            p.Stroke();
        }

        protected static void DashedLine(Painter2D p, Vector2 a, Vector2 b, Color color, float dash = 4f, float gap = 3f, float width = 1f)
        {
            var length = Vector2.Distance(a, b);
            if (length <= 0f) return;
            var dir = (b - a) / length;
            p.strokeColor = color;
            p.lineWidth = width;
            p.BeginPath();
            for (var s = 0f; s < length; s += dash + gap)
            {
                p.MoveTo(a + dir * s);
                p.LineTo(a + dir * Mathf.Min(length, s + dash));
            }

            p.Stroke();
        }

        /// <summary>Filled rectangle with rounded corners (radius clamped to fit).</summary>
        protected static void RoundRect(Painter2D p, Rect r, float radius, Color fill)
        {
            if (r.width <= 0f || r.height <= 0f) return;
            radius = Mathf.Min(radius, r.width / 2f, r.height / 2f);
            p.fillColor = fill;
            p.BeginPath();
            TracePath(p, r, radius);
            p.Fill();
        }

        protected static void RoundRectOutline(Painter2D p, Rect r, float radius, Color stroke, float width = 1.2f)
        {
            if (r.width <= 0f || r.height <= 0f) return;
            radius = Mathf.Min(radius, r.width / 2f, r.height / 2f);
            p.strokeColor = stroke;
            p.lineWidth = width;
            p.BeginPath();
            TracePath(p, r, radius);
            p.Stroke();
        }

        private static void TracePath(Painter2D p, Rect r, float radius)
        {
            if (radius <= 0.01f)
            {
                p.MoveTo(new Vector2(r.xMin, r.yMin));
                p.LineTo(new Vector2(r.xMax, r.yMin));
                p.LineTo(new Vector2(r.xMax, r.yMax));
                p.LineTo(new Vector2(r.xMin, r.yMax));
                p.ClosePath();
                return;
            }

            p.MoveTo(new Vector2(r.xMin + radius, r.yMin));
            p.ArcTo(new Vector2(r.xMax, r.yMin), new Vector2(r.xMax, r.yMax), radius);
            p.ArcTo(new Vector2(r.xMax, r.yMax), new Vector2(r.xMin, r.yMax), radius);
            p.ArcTo(new Vector2(r.xMin, r.yMax), new Vector2(r.xMin, r.yMin), radius);
            p.ArcTo(new Vector2(r.xMin, r.yMin), new Vector2(r.xMax, r.yMin), radius);
            p.ClosePath();
        }

        protected static void Circle(Painter2D p, Vector2 centre, float radius, Color fill)
        {
            p.fillColor = fill;
            p.BeginPath();
            p.Arc(centre, radius, Angle.Degrees(0f), Angle.Degrees(360f));
            p.Fill();
        }

        protected static Color WithAlpha(Color c, float a) => new Color(c.r, c.g, c.b, c.a * a);
    }

    /// <summary>Factory: a spec in, the matching chart control out, already filled.</summary>
    public static class Chart
    {
        public static ChartElement Create(ChartSpec spec)
        {
            ChartElement chart;
            switch (spec)
            {
                case BarChartSpec _: chart = new BarChart(); break;
                case LineChartSpec _: chart = new LineChart(); break;
                case TimelineChartSpec _: chart = new TimelineChart(); break;
                case DonutChartSpec _: chart = new DonutChart(); break;
                default: throw new ArgumentException("Unknown chart spec: " + spec?.GetType().Name, nameof(spec));
            }

            chart.SetSpec(spec);
            return chart;
        }
    }
}
