using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace ShipyardTwin
{
    public readonly struct YardSearchResult
    {
        public string Title { get; }
        public string Id { get; }
        public string Kind { get; }
        public string Subtitle { get; }
        public TargetCategory Category { get; }
        public bool IsAvailable { get; }
        internal HoverTarget Target { get; }

        internal YardSearchResult(TargetCategory category, string id, string title, string kind, string subtitle,
            HoverTarget target, bool available = false)
        {
            Category = category;
            Id = id;
            Title = title;
            Kind = kind;
            Subtitle = subtitle;
            Target = target;
            IsAvailable = available;
        }
    }

    /// <summary>Searches the registered yard objects and plan records without scanning the Unity scene.</summary>
    public static class YardObjectSearch
    {
        private const StringComparison Comparison = StringComparison.OrdinalIgnoreCase;

        public static IReadOnlyList<YardSearchResult> Find(YardModel model, string query, int limit = 40)
        {
            var words = (query ?? string.Empty).Split((char[])null, StringSplitOptions.RemoveEmptyEntries);
            if (words.Length == 0 || limit <= 0) return Array.Empty<YardSearchResult>();

            var items = new List<YardSearchResult>();
            var keys = new HashSet<(TargetCategory, string)>();
            foreach (var target in HoverTarget.ActiveTargets)
            {
                if (target == null || target.IsOccluder || target.ExcludeFromSearch) continue;
                if (target.Category != TargetCategory.Prop && !string.IsNullOrEmpty(target.Id) &&
                    !keys.Add((target.Category, target.Id))) continue;
                items.Add(new YardSearchResult(target.Category, target.Id, target.Title, target.Kind, target.Detail, target));
            }
            if (model != null) AddPlan(model, keys, items);

            var term = string.Join(" ", words);
            var compactTerm = CompactId(term);
            var hulls = model?.Hulls.ToDictionary(hull => hull.Id);
            var renderers = new List<MeshRenderer>();
            return items.Select(item => (Item: item, Rank: Rank(item, OwnerOf(item), words, term, compactTerm)))
                .Where(match => match.Rank >= 0)
                .OrderBy(match => match.Rank)
                .ThenBy(match => match.Item.Category == TargetCategory.Hull ? 0 : 1)
                .ThenBy(match => match.Item.Title, StringComparer.OrdinalIgnoreCase)
                .ThenBy(match => match.Item.Id, StringComparer.OrdinalIgnoreCase)
                .Take(limit)
                .Select(match =>
                {
                    var item = match.Item;
                    return new YardSearchResult(item.Category, item.Id, item.Title, item.Kind, item.Subtitle,
                        item.Target, TryBounds(item.Target, renderers, out _));
                })
                .ToArray();

            HullPlan OwnerOf(YardSearchResult item)
            {
                if (hulls == null) return null;
                if (item.Category == TargetCategory.Hull)
                    return item.Id != null && hulls.TryGetValue(item.Id, out var hull) ? hull : null;
                if (item.Category != TargetCategory.Block) return null;

                var id = item.Target != null ? item.Target.Hull : null;
                if (string.IsNullOrEmpty(id) && item.Id != null && model.Blocks.TryGetValue(item.Id, out var block))
                    id = block.Hull;
                return id != null && hulls.TryGetValue(id, out var owner) ? owner : null;
            }
        }

        /// <summary>Rechecks visibility and the ID before moving the camera, since replay continues as the list is open.</summary>
        public static bool Open(YardSearchResult result)
        {
            var target = result.Target;
            if (target == null || target.Id != result.Id || target.Category != result.Category ||
                !TryBounds(target, new List<MeshRenderer>(), out var bounds)) return false;

            var detail = ObjectDetailView.Active;
            var camera = UnityEngine.Object.FindFirstObjectByType<OrbitCameraRig>();
            if (detail == null || camera == null || !camera.Focus(bounds)) return false;

            var rail = UnityEngine.Object.FindFirstObjectByType<RailCraneView>();
            UnityEngine.Object.FindFirstObjectByType<CraneSafetyView>()?.Select(rail?.Find(target));
            detail.Select(target);
            return true;
        }

        private static bool Matches(YardSearchResult row, string term) =>
            Contains(row.Id, term) || Contains(row.Title, term) || Contains(row.Kind, term) || Contains(row.Subtitle, term);

        private static bool Contains(string value, string term) => value != null && value.IndexOf(term, Comparison) >= 0;

        private static string CompactId(string value) => value != null &&
            (value.IndexOf('-') >= 0 || value.Any(char.IsWhiteSpace))
                ? string.Concat(value.Where(c => c != '-' && !char.IsWhiteSpace(c)))
                : value;

        private static int Rank(YardSearchResult result, HullPlan owner, string[] words, string term, string compactTerm)
        {
            var id = CompactId(result.Id);
            var name = result.Category == TargetCategory.Hull ? owner?.Name : null;
            if ((compactTerm.Length != 0 && string.Equals(id, compactTerm, Comparison)) ||
                string.Equals(result.Title, term, Comparison) || string.Equals(name, term, Comparison)) return 0;
            if ((compactTerm.Length != 0 && id?.StartsWith(compactTerm, Comparison) == true) ||
                result.Title?.StartsWith(term, Comparison) == true || name?.StartsWith(term, Comparison) == true) return 1;

            var ownedBlock = result.Category == TargetCategory.Block && owner != null;
            var usedRelation = false;
            foreach (var word in words)
            {
                var compactWord = CompactId(word);
                if (Matches(result, word) || Contains(name, word) ||
                    (compactWord.Length != 0 && Contains(id, compactWord))) continue;
                if (!ownedBlock || (!Contains(owner.Name, word) && !Contains(owner.Info?.vessel_name, word) &&
                    !Contains(owner.Id, word) && (compactWord.Length == 0 || !Contains(CompactId(owner.Id), compactWord))))
                    return -1;
                usedRelation = true;
            }
            return usedRelation ? 3 : 2;
        }

        private static void AddPlan(YardModel model, HashSet<(TargetCategory, string)> keys, List<YardSearchResult> items)
        {
            void Add(TargetCategory category, string id, string title, string kind, string subtitle)
            {
                if (!string.IsNullOrEmpty(id) && keys.Add((category, id)))
                    items.Add(new YardSearchResult(category, id, string.IsNullOrWhiteSpace(title) ? id : title, kind, subtitle, null));
            }

            foreach (var hull in model.Hulls)
                Add(TargetCategory.Hull, hull.Id,
                    string.IsNullOrWhiteSpace(hull.Info?.vessel_name) ? hull.Name : hull.Info.vessel_name, "호선", hull.Info?.vessel_type);
            foreach (var block in model.Blocks.Values)
                Add(TargetCategory.Block, block.Id, block.Id, "블록", ShipView.BlockDetail(block.Record, null));
            foreach (var facility in model.Facilities)
                Add(TargetCategory.Facility, facility.location, facility.name, facility.kind, null);
            foreach (var rail in model.Source?.crane_rails ?? Array.Empty<CraneRailRow>())
                Add(TargetCategory.Crane, rail.equipment, rail.name, rail.equipment_type, rail.station);
            foreach (var spec in model.Source?.equipment_specs ?? Array.Empty<YardEquipmentSpec>())
            {
                var category = spec.equipment_type is "지게차" or "트랜스포터" ? TargetCategory.Vehicle : TargetCategory.Crane;
                Add(category, spec.equipment, spec.equipment, spec.equipment_type, spec.station);
            }
        }

        private static bool TryBounds(HoverTarget target, List<MeshRenderer> renderers, out Bounds bounds)
        {
            bounds = default;
            if (target == null || !target.isActiveAndEnabled) return false;
            renderers.Clear();
            target.GetComponentsInChildren(false, renderers);
            var found = false;
            foreach (var renderer in renderers)
            {
                if (!renderer.enabled || renderer.forceRenderingOff || SelectionHighlight.MeshOf(renderer) == null) continue;
                if (found) bounds.Encapsulate(renderer.bounds);
                else bounds = renderer.bounds;
                found = true;
            }
            renderers.Clear();
            return found && bounds.size.sqrMagnitude > 0f;
        }
    }
}
