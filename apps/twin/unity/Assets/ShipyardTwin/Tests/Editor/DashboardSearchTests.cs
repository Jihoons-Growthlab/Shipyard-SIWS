using System.IO;
using System.Linq;
using NUnit.Framework;
using UnityEditor;
using UnityEngine.UIElements;

namespace ShipyardTwin.Tests
{
    /// <summary>Object search drawer UI (<see cref="DashboardObjectSearch"/>, Dashboard.uxml "search-panel"), without a scene.</summary>
    public sealed class DashboardSearchTests
    {
        private const string Uxml = "Assets/ShipyardTwin/Authoring/Dashboard.uxml";
        private const string BuildJson = "Assets/ShipyardTwin/Authoring/shipyard.build.json";

        [Test]
        public void Highlight_MarksTheMatchCaseInsensitiveAndEscapesMarkup()
        {
            Assert.AreEqual("SN2040-<color=#7FB8FF><b>241C</b></color>1", DashboardObjectSearch.Highlight("SN2040-241C1", " 241c "));
            Assert.AreEqual("<color=#7FB8FF><b>1도크</b></color>", DashboardObjectSearch.Highlight("1도크", "1도크"));
            Assert.AreEqual("A<noparse><</noparse>B", DashboardObjectSearch.Highlight("A<B", "zz"), "No match: escaped as typed.");
            Assert.AreEqual("a<noparse><</noparse><color=#7FB8FF><b>b</b></color>", DashboardObjectSearch.Highlight("a<b", "B"));
            Assert.AreEqual("블록", DashboardObjectSearch.Highlight("블록", ""));
            Assert.AreEqual(string.Empty, DashboardObjectSearch.Highlight(null, "x"));
        }

        [Test]
        public void CardText_IdKindAndCountLine()
        {
            Assert.AreEqual("<color=#7FB8FF><b>JC</b></color>-DK-1  ·  지브 크레인",
                DashboardObjectSearch.MetaText("JC-DK-1", "1도크 지브 크레인 1호", "지브 크레인", "jc"));
            Assert.AreEqual("블록", DashboardObjectSearch.MetaText("B1", "b1", "블록", "x"), "ID equal to the title is not repeated.");
            Assert.AreEqual("결과 3건", DashboardObjectSearch.StatusText(3, 0, 40));
            Assert.AreEqual("짧은 설명", DashboardObjectSearch.Clip("짧은 설명", 72));
            var clipped = DashboardObjectSearch.Clip(string.Join(" · ", Enumerable.Repeat("지브 크레인 50 t", 12)), 72);
            Assert.LessOrEqual(clipped.Length, 72);
            StringAssert.EndsWith("…", clipped);
            Assert.AreEqual("결과 40건 (상위 40건) · 지금 화면에 없는 대상 2건", DashboardObjectSearch.StatusText(40, 2, 40));
        }

        [Test]
        public void Texts_UnifiedSearchByNameAndHullNumber()
        {
            StringAssert.Contains("SN", DashboardObjectSearch.EmptyGuide);
            StringAssert.Contains("명칭", DashboardObjectSearch.EmptyGuide);
            StringAssert.Contains("‘SN99’", DashboardObjectSearch.NoMatchText("SN99"));
            Assert.AreEqual("화면에 없음", DashboardObjectSearch.OffScreenText, "Off-screen status stays; there is no 보기 badge.");
            var uss = File.ReadAllText("Assets/ShipyardTwin/Authoring/Dashboard.uss");
            StringAssert.Contains(".tw-search-chip { display: none;", uss, "Chip hidden on cards that can be shown.");
            StringAssert.Contains(".tw-search-card--off .tw-search-chip { display: flex;", uss);
        }

        [Test]
        public void Glyphs_EveryCategoryHasAnIcon()
        {
            foreach (TargetCategory category in System.Enum.GetValues(typeof(TargetCategory)))
            {
                CollectionAssert.Contains(DashboardIcon.Glyphs, DashboardIcon.GlyphFor(category), category.ToString());
            }

            Assert.AreEqual("block", DashboardObjectSearch.GlyphFor(TargetCategory.Block, "블록"));
            Assert.AreEqual("vehicle", DashboardObjectSearch.GlyphFor(TargetCategory.Prop, "지게차"), "Scenery machines look like vehicles.");
            Assert.AreEqual("building", DashboardObjectSearch.GlyphFor(TargetCategory.Prop, "창고"));
            Assert.AreEqual("tw-search-card--facility", DashboardObjectSearch.CategoryClass(TargetCategory.Facility));
        }

        [Test]
        public void Layout_SearchDrawerIsWiredInTheRailAndPanel()
        {
            var tree = AssetDatabase.LoadAssetAtPath<VisualTreeAsset>(Uxml);
            Assert.IsNotNull(tree, Uxml);
            var root = tree.Instantiate();
            var rail = root.Q("rail");
            var search = rail.Q<Button>("nav-search");
            Assert.IsNotNull(search, "Magnifier in the left rail.");
            Assert.Less(rail.IndexOf(search), rail.IndexOf(rail.Q<Button>("nav-erection")), "Search is the first menu.");
            var panel = root.Q("search-panel");
            Assert.IsTrue(panel.ClassListContains("tw-drawer"), "Same drawer slot and motion as erection and chat.");
            Assert.IsNotNull(panel.Q<TextField>("search-field"));
            Assert.IsNotNull(panel.Q<Button>("search-clear"));
            Assert.IsNotNull(panel.Q<Button>("search-close"));
            Assert.IsNotNull(panel.Q<ScrollView>("search-scroll").Q("search-results"));
            Assert.IsNotNull(panel.Q("search-empty"));
            Assert.IsNotNull(panel.Q<Label>("search-status"));
            Assert.IsNull(root.Q("select-mark"), "Selection brackets stay removed.");
            Assert.AreEqual("통합검색", panel.Q<Label>("search-title").text, "Named 통합검색, not object search.");
            StringAssert.Contains("통합검색", search.tooltip);
            StringAssert.Contains("SN", panel.Q<Label>("search-placeholder").text, "Hint names the hull number.");
            Assert.AreEqual(DashboardObjectSearch.EmptyTitle, panel.Q<Label>("search-empty-title").text);
            Assert.AreEqual(DashboardObjectSearch.EmptyGuide, panel.Q<Label>("search-empty-text").text);
            Assert.IsFalse(panel.Query<Button>().ToList().Any(b => b.text == "보기"), "No 보기 button: the card itself opens.");
            StringAssert.Contains("\"DashboardObjectSearch\"", File.ReadAllText(BuildJson), "Component on the dashboard panel.");
        }

        [Test]
        public void SearchFocusFlags_FalseWithoutADashboard()
        {
            Assert.IsFalse(DashboardShellView.SearchFieldFocused);
            Assert.IsFalse(DashboardShellView.SearchControlsFocused);
            Assert.AreEqual(DashboardIcon.Glyphs.Length, DashboardIcon.Glyphs.Distinct().Count());
        }
    }
}
