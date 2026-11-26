using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace GrowthLab.UI.Charts
{
    /// <summary>
    /// Declarative description of a chart: pure data, no UI objects, so domain code can build it (and test it) without
    /// a panel. <see cref="Chart.Create"/> turns a spec into the matching <see cref="ChartElement"/>; the same element
    /// takes a new spec of its kind through <see cref="ChartElement.SetSpec"/>.
    /// </summary>
    public abstract class ChartSpec
    {
        public string Title;

        /// <summary>One line under the plot (source, basis, unit note).</summary>
        public string Caption;

        /// <summary>Shown instead of the plot when <see cref="IsEmpty"/>.</summary>
        public string EmptyText = "표시할 데이터가 없습니다";

        /// <summary>Plot height in panel units (timelines grow with their rows instead).</summary>
        public float Height = 140f;

        public abstract bool IsEmpty { get; }
    }

    /// <summary>One named series of values, one per category (NaN = no value).</summary>
    public sealed class ChartSeries
    {
        public ChartSeries(string name, Color color, IEnumerable<float> values = null)
        {
            Name = name;
            Color = color;
            if (values != null) Values.AddRange(values);
        }

        public string Name { get; }
        public Color Color { get; }
        public List<float> Values { get; } = new List<float>();

        public float this[int index] => index >= 0 && index < Values.Count ? Values[index] : float.NaN;
    }

    /// <summary>Grouped bars: one group per category, one bar per series.</summary>
    public sealed class BarChartSpec : ChartSpec
    {
        public readonly List<string> Categories = new List<string>();
        public readonly List<ChartSeries> Series = new List<ChartSeries>();
        public string Unit = string.Empty;

        /// <summary>Axis top; NaN = a rounded value above the largest bar.</summary>
        public float Max = float.NaN;

        /// <summary>Dashed reference line (capacity, 100 %); NaN = none.</summary>
        public float Threshold = float.NaN;

        public string ThresholdLabel;

        /// <summary>Value text above each bar (single-series charts with few categories).</summary>
        public bool ShowValues = true;

        public override bool IsEmpty => Categories.Count == 0 || Series.All(s => s.Values.All(float.IsNaN));
    }

    /// <summary>Lines over categories (e.g. a cumulative plan/actual S-curve); optional filled area and a "now" mark.</summary>
    public sealed class LineChartSpec : ChartSpec
    {
        public readonly List<string> Categories = new List<string>();
        public readonly List<ChartSeries> Series = new List<ChartSeries>();
        public string Unit = string.Empty;
        public float Max = float.NaN;
        public bool Area = true;

        /// <summary>Category index of a vertical marker (today); -1 = none.</summary>
        public int MarkerIndex = -1;

        public string MarkerLabel = "오늘";

        public override bool IsEmpty => Categories.Count < 2 || Series.All(s => s.Values.All(float.IsNaN));
    }

    /// <summary>A dated bar (or a one-day milestone) on a named row of a <see cref="TimelineChartSpec"/>.</summary>
    public sealed class TimelineItem
    {
        public string Row;
        public string Label;
        public DateTime Start;

        /// <summary>Exclusive end; a milestone ignores it.</summary>
        public DateTime End;

        public Color Color = ChartPalette.Accent;

        /// <summary>Done fraction 0–1 drawn as a brighter fill inside the bar; NaN = none.</summary>
        public float Progress = float.NaN;

        public bool Milestone;

        /// <summary>Planned, not confirmed: drawn as an outline.</summary>
        public bool Hollow;

        /// <summary>Extra line shown with the label when the pointer rests on the item.</summary>
        public string Detail;
    }

    /// <summary>Gantt-style rows of dated items with a time axis and an optional "now" line.</summary>
    public sealed class TimelineChartSpec : ChartSpec
    {
        public readonly List<TimelineItem> Items = new List<TimelineItem>();

        /// <summary>Visible range; null = the items' range with a small margin.</summary>
        public DateTime? From, To;

        public DateTime? Now;
        public string NowLabel = "오늘";
        public float RowHeight = 20f;

        /// <summary>Rows in first-appearance order.</summary>
        public IReadOnlyList<string> Rows => Items.Select(i => i.Row ?? string.Empty).Distinct().ToList();

        public override bool IsEmpty => Items.Count == 0;
    }

    public sealed class DonutSlice
    {
        public DonutSlice(string label, float value, Color color)
        {
            Label = label;
            Value = value;
            Color = color;
        }

        public string Label { get; }
        public float Value { get; }
        public Color Color { get; }
    }

    /// <summary>Ring of slices against <see cref="Total"/> (the rest is the empty track), with big centre text.</summary>
    public sealed class DonutChartSpec : ChartSpec
    {
        public readonly List<DonutSlice> Slices = new List<DonutSlice>();

        /// <summary>Full ring; NaN = the sum of the slices.</summary>
        public float Total = float.NaN;

        public string CenterText;
        public string CenterCaption;

        public DonutChartSpec()
        {
            Height = 120f;
        }

        public float RingTotal => float.IsNaN(Total) || Total <= 0f ? Slices.Sum(s => Mathf.Max(0f, s.Value)) : Total;

        public override bool IsEmpty => Slices.Count == 0 || RingTotal <= 0f;

        /// <summary>A single progress ring: <paramref name="done"/> of <paramref name="total"/>, percentage in the centre.</summary>
        public static DonutChartSpec Progress(string title, float done, float total, string doneLabel, Color color, string caption = null)
        {
            var spec = new DonutChartSpec { Title = title, Total = total, CenterCaption = caption };
            spec.Slices.Add(new DonutSlice(doneLabel, done, color));
            spec.CenterText = total > 0f ? Mathf.RoundToInt(Mathf.Clamp01(done / total) * 100f) + "%" : "-";
            return spec;
        }
    }

    /// <summary>Default colours (match GrowthLabUI.uss variables) and a categorical series palette.</summary>
    public static class ChartPalette
    {
        public static readonly Color Accent = new Color32(84, 160, 255, 255);
        public static readonly Color Normal = new Color32(64, 190, 120, 255);
        public static readonly Color Warning = new Color32(240, 190, 60, 255);
        public static readonly Color Alarm = new Color32(235, 80, 70, 255);
        public static readonly Color Muted = new Color32(120, 128, 140, 255);
        public static readonly Color Teal = new Color32(115, 222, 194, 255);
        public static readonly Color Violet = new Color32(170, 140, 255, 255);

        private static readonly Color[] Series = { Accent, Teal, Warning, Violet, new Color32(255, 140, 110, 255), Muted };

        public static Color At(int index) => Series[((index % Series.Length) + Series.Length) % Series.Length];
    }
}
