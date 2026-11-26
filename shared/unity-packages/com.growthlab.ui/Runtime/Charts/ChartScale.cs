using System;
using System.Collections.Generic;
using UnityEngine;

namespace GrowthLab.UI.Charts
{
    /// <summary>Axis arithmetic shared by the charts: rounded maxima, tick values and date ticks. Pure, unit tested.</summary>
    public static class ChartScale
    {
        /// <summary>Smallest 1 / 2 / 2.5 / 5 × 10^n at or above <paramref name="value"/> (1 for zero or less).</summary>
        public static float NiceMax(float value)
        {
            if (float.IsNaN(value) || value <= 0f)
            {
                return 1f;
            }

            var magnitude = Mathf.Pow(10f, Mathf.Floor(Mathf.Log10(value)));
            foreach (var step in new[] { 1f, 2f, 2.5f, 5f, 10f })
            {
                if (step * magnitude >= value - magnitude * 1e-4f)
                {
                    return step * magnitude;
                }
            }

            return 10f * magnitude;
        }

        /// <summary><paramref name="count"/> + 1 evenly spaced values from 0 to <paramref name="max"/>.</summary>
        public static List<float> Ticks(float max, int count = 4)
        {
            count = Mathf.Max(1, count);
            var ticks = new List<float>(count + 1);
            for (var i = 0; i <= count; i++)
            {
                ticks.Add(max * i / count);
            }

            return ticks;
        }

        /// <summary>Fraction of a value on a 0..max axis, clamped.</summary>
        public static float Fraction(float value, float max) => max > 0f && !float.IsNaN(value) ? Mathf.Clamp01(value / max) : 0f;

        /// <summary>Fraction of an instant between two others, clamped.</summary>
        public static float Fraction(DateTime value, DateTime from, DateTime to)
        {
            var span = (to - from).TotalSeconds;
            return span > 0 ? Mathf.Clamp01((float)((value - from).TotalSeconds / span)) : 0f;
        }

        /// <summary>Short axis text: 1234 → "1.2k", 12.5 → "12.5", 3 → "3".</summary>
        public static string Short(float value)
        {
            var a = Mathf.Abs(value);
            if (a >= 10000f) return (value / 1000f).ToString("0") + "k";
            if (a >= 1000f) return (value / 1000f).ToString("0.#") + "k";
            return Mathf.Approximately(value, Mathf.Round(value)) ? Mathf.Round(value).ToString("0") : value.ToString("0.#");
        }

        /// <summary>
        /// Date ticks for a range: every day up to 10 days, Mondays up to ~10 weeks, first of the month up to ~1 year,
        /// else every third month. Returns the tick instants (inside the range) and the label format.
        /// </summary>
        public static (List<DateTime> Ticks, string Format) DateTicks(DateTime from, DateTime to)
        {
            var ticks = new List<DateTime>();
            var days = (to - from).TotalDays;
            if (days <= 0)
            {
                return (ticks, "MM-dd");
            }

            if (days <= 10)
            {
                for (var d = from.Date.AddDays(from.TimeOfDay > TimeSpan.Zero ? 1 : 0); d <= to; d = d.AddDays(1)) ticks.Add(d);
                return (ticks, "MM-dd");
            }

            if (days <= 75)
            {
                var d = from.Date;
                while (d.DayOfWeek != DayOfWeek.Monday || d < from) d = d.AddDays(1);
                for (; d <= to; d = d.AddDays(7)) ticks.Add(d);
                return (ticks, "MM-dd");
            }

            var monthStep = days <= 400 ? 1 : 3;
            var m = new DateTime(from.Year, from.Month, 1);
            if (m < from) m = m.AddMonths(1);
            for (; m <= to; m = m.AddMonths(monthStep)) ticks.Add(m);
            return (ticks, days <= 400 ? "M월" : "yy.MM");
        }

        /// <summary>Ease-out cubic: fast start, gentle stop (chart reveal).</summary>
        public static float EaseOut(float t)
        {
            t = Mathf.Clamp01(t);
            var u = 1f - t;
            return 1f - u * u * u;
        }
    }
}
