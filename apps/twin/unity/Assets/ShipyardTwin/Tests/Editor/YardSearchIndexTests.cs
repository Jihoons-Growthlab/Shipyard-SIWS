using System;
using System.Linq;
using NUnit.Framework;

namespace ShipyardTwin.Tests
{
    public sealed class YardSearchIndexTests
    {
        [Test]
        public void Search_RanksExactIdAndFindsPlanObjectsThatAreNotDisplayed()
        {
            var model = YardModel.Build(new YardSnapshot
            {
                location_geo = new[]
                {
                    new Facility { location = "TEST-D", name = "남측 도크", kind = "도크", size_x_m = 250, size_y_m = 50 },
                },
                hulls = new[] { new HullRow { id = "H101", name = "시연 유조선" } },
                hull_placements = new[] { new HullPlacement { hull = "H101", dock = "TEST-D" } },
                erection_timeline = new[] { new BlockRecord { block = "H101-A1", hull = "H101", zone = "선미" } },
                equipment_specs = new[] { new YardEquipmentSpec { equipment = "FL-77", equipment_type = "지게차", station = "TEST-D" } },
            }, new DateTime(2026, 11, 25));

            Assert.IsEmpty(YardObjectSearch.Find(model, "  "));
            Assert.IsEmpty(YardObjectSearch.Find(model, "없는 장비"));
            var hull = YardObjectSearch.Find(model, " h101 ", limit: 1)[0];
            Assert.AreEqual("H101", hull.Id);
            Assert.AreEqual("시연 유조선", hull.Title);
            Assert.AreEqual(TargetCategory.Hull, hull.Category);
            Assert.IsFalse(hull.IsAvailable);
            Assert.IsFalse(YardObjectSearch.Open(hull));

            Assert.AreEqual("H101-A1", YardObjectSearch.Find(model, "h101 선미")[0].Id);
            Assert.AreEqual("TEST-D", YardObjectSearch.Find(model, "남측")[0].Id);
            Assert.AreEqual(TargetCategory.Vehicle, YardObjectSearch.Find(model, "지게차")[0].Category);
        }

        [Test]
        public void Search_FindsHullAndItsBlocksByVesselNamePlanNameOrSn()
        {
            var model = YardModel.Build(new YardSnapshot
            {
                location_geo = new[]
                {
                    new Facility { location = "TEST-D", name = "남측 도크", kind = "도크", size_x_m = 250, size_y_m = 50 },
                },
                hulls = new[]
                {
                    new HullRow { id = "SN2040", name = "대형 유조선" },
                    new HullRow { id = "SN2041", name = "다른 호선" },
                },
                vessel_details = new[] { new HullInfo { hull = "SN2040", vessel_name = "블루 오션" } },
                hull_placements = new[]
                {
                    new HullPlacement { hull = "SN2040", dock = "TEST-D" },
                    new HullPlacement { hull = "SN2041", dock = "TEST-D" },
                },
                erection_timeline = new[]
                {
                    new BlockRecord { block = "A1", hull = "SN2040", zone = "선미" },
                    new BlockRecord { block = "SN2041-B1", hull = "SN2041", zone = "선미" },
                },
            }, new DateTime(2026, 11, 25));

            foreach (var query in new[] { "블루 오", "대형 유조선", "sn2040", "SN-2040", "SN 2040" })
                CollectionAssert.AreEqual(new[] { "SN2040", "A1" },
                    YardObjectSearch.Find(model, query).Select(result => result.Id).ToArray(), query);

            Assert.AreEqual("A1", YardObjectSearch.Find(model, "블루 선미").Single().Id);
            CollectionAssert.AreEquivalent(new[] { "SN2040", "SN2041" },
                YardObjectSearch.Find(model, "sn", limit: 2).Select(result => result.Id).ToArray());
        }
    }
}
