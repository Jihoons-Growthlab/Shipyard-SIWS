using System;
using GrowthLab.UI.Charts;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.UIElements;

namespace GrowthLab.UI.Tests
{
    public sealed class ChartTests
    {
        [TestCase(0f, 1f)]
        [TestCase(7f, 10f)]
        [TestCase(23f, 25f)]
        [TestCase(40f, 50f)]
        [TestCase(101f, 200f)]
        [TestCase(0.3f, 0.5f)]
        public void NiceMax_RoundsUpTo125Steps(float value, float expected)
        {
            Assert.AreEqual(expected, ChartScale.NiceMax(value), expected * 1e-4f);
        }

        [Test]
        public void Ticks_AreEven_AndFractionClamps()
        {
            CollectionAssert.AreEqual(new[] { 0f, 25f, 50f, 75f, 100f }, ChartScale.Ticks(100f, 4));
            Assert.AreEqual(1f, ChartScale.Fraction(150f, 100f));
            Assert.AreEqual(0f, ChartScale.Fraction(float.NaN, 100f));
            Assert.AreEqual(0.5f, ChartScale.Fraction(new DateTime(2026, 1, 2), new DateTime(2026, 1, 1), new DateTime(2026, 1, 3)), 1e-5f);
            Assert.AreEqual("1.2k", ChartScale.Short(1234f));
            Assert.AreEqual("12.5", ChartScale.Short(12.5f));
        }

        [Test]
        public void DateTicks_PickDaysWeeksOrMonths()
        {
            var from = new DateTime(2026, 11, 2);   // a Monday
            var (days, dayFormat) = ChartScale.DateTicks(from, from.AddDays(6));
            Assert.AreEqual(7, days.Count);
            Assert.AreEqual("MM-dd", dayFormat);

            var (weeks, _) = ChartScale.DateTicks(from, from.AddDays(35));
            Assert.AreEqual(6, weeks.Count);
            Assert.IsTrue(weeks.TrueForAll(d => d.DayOfWeek == DayOfWeek.Monday));

            var (months, monthFormat) = ChartScale.DateTicks(from, from.AddDays(200));
            Assert.AreEqual("M월", monthFormat);
            Assert.AreEqual(new DateTime(2026, 12, 1), months[0]);
        }

        [Test]
        public void Factory_MakesTheMatchingChart_AndShowsEmptyState()
        {
            var bars = new BarChartSpec { Title = "부하" };
            var chart = Chart.Create(bars);
            Assert.IsInstanceOf<BarChart>(chart);
            Assert.IsTrue(bars.IsEmpty);
            Assert.AreEqual(DisplayStyle.None, chart.Plot.style.display.value);
            Assert.AreEqual(DisplayStyle.Flex, chart.Q<Label>("empty").style.display.value);

            bars.Categories.Add("1주");
            bars.Series.Add(new ChartSeries("가동률", ChartPalette.Accent, new[] { 40f }));
            chart.SetSpec(bars);
            Assert.IsFalse(bars.IsEmpty);
            Assert.AreEqual(DisplayStyle.Flex, chart.Plot.style.display.value);
            Assert.AreEqual(50f, ((BarChart)chart).AxisMax);

            Assert.IsInstanceOf<LineChart>(Chart.Create(new LineChartSpec()));
            Assert.IsInstanceOf<TimelineChart>(Chart.Create(new TimelineChartSpec()));
            Assert.IsInstanceOf<DonutChart>(Chart.Create(new DonutChartSpec()));
            Assert.Throws<ArgumentException>(() => chart.SetSpec(new LineChartSpec()));
        }

        [Test]
        public void Donut_Progress_ShowsPercent_AndThresholdWidensBarAxis()
        {
            var donut = DonutChartSpec.Progress("진척", 3f, 4f, "완료", Color.green);
            Assert.AreEqual("75%", donut.CenterText);
            Assert.AreEqual(4f, donut.RingTotal);
            Assert.IsFalse(donut.IsEmpty);

            var bars = new BarChartSpec { Threshold = 600f };
            bars.Categories.Add("A");
            bars.Series.Add(new ChartSeries("인양", Color.white, new[] { 150f }));
            Assert.AreEqual(1000f, ((BarChart)Chart.Create(bars)).AxisMax);
        }

        [Test]
        public void Timeline_Range_CoversItemsAndNow_WithMargin()
        {
            var spec = new TimelineChartSpec { Now = new DateTime(2026, 12, 20) };
            spec.Items.Add(new TimelineItem { Row = "도크", Start = new DateTime(2026, 12, 1), End = new DateTime(2026, 12, 11) });
            spec.Items.Add(new TimelineItem { Row = "이동", Start = new DateTime(2026, 12, 5), Milestone = true });
            CollectionAssert.AreEqual(new[] { "도크", "이동" }, spec.Rows);
            var (from, to) = TimelineChart.Range(spec);
            Assert.Less(from, new DateTime(2026, 12, 1));
            Assert.Greater(to, new DateTime(2026, 12, 20));
        }

        [Test]
        public void Animate_StartsFromZero_CompleteShowsAll()
        {
            var chart = Chart.Create(DonutChartSpec.Progress("진척", 1f, 2f, "완료", Color.green));
            chart.Animate(1f);
            Assert.AreEqual(0f, chart.Reveal);
            chart.Complete();
            Assert.AreEqual(1f, chart.Reveal);
        }
    }
}
