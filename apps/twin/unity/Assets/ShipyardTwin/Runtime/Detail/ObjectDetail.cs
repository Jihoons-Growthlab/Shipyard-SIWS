using System;
using System.Collections.Generic;
using GrowthLab.UI;
using GrowthLab.UI.Charts;

namespace ShipyardTwin
{
    /// <summary>
    /// What the detail popup shows for one clicked object: header, status, key facts and sections with charts (as
    /// <see cref="ChartSpec"/> data) and list rows. Built by <see cref="ObjectDetailBuilder"/>; rendered by
    /// <see cref="ObjectDetailView"/>. Pure data.
    /// </summary>
    public sealed class ObjectDetail
    {
        public string Kind;
        public string Title;
        public string Subtitle;
        public string Status;
        public StatusLevel StatusLevel = StatusLevel.Info;
        public readonly List<DetailFact> Facts = new List<DetailFact>();
        public readonly List<DetailSection> Sections = new List<DetailSection>();

        /// <summary>Where the figures come from (snapshot, ERP time, missing permissions).</summary>
        public string Source;

        public ObjectDetail Fact(string label, string value, StatusLevel? tone = null)
        {
            if (!string.IsNullOrEmpty(value)) Facts.Add(new DetailFact(label, value, tone));
            return this;
        }

        public DetailSection Section(string title, string note = null)
        {
            var section = new DetailSection { Title = title, Note = note };
            Sections.Add(section);
            return section;
        }
    }

    public sealed class DetailFact
    {
        public DetailFact(string label, string value, StatusLevel? tone = null)
        {
            Label = label;
            Value = value;
            Tone = tone;
        }

        public string Label { get; }
        public string Value { get; }

        /// <summary>Colour of the value (status words); null = plain text.</summary>
        public StatusLevel? Tone { get; }
    }

    public sealed class DetailSection
    {
        public string Title;
        public string Note;

        /// <summary>Charts in order; a donut next to the following chart shares its row.</summary>
        public readonly List<ChartSpec> Charts = new List<ChartSpec>();

        /// <summary>List rows under the charts (label, value, tone).</summary>
        public readonly List<DetailFact> Rows = new List<DetailFact>();

        /// <summary>Waiting for an ERP query: shown as a loading line until the detail is rebuilt.</summary>
        public bool Pending;

        /// <summary>Shop floor layout (blocks by stage) shown before the charts; null = none.</summary>
        public BayBoard Bays;

        public DetailSection Chart(ChartSpec spec)
        {
            if (spec != null) Charts.Add(spec);
            return this;
        }

        public DetailSection Row(string label, string value, StatusLevel? tone = null)
        {
            Rows.Add(new DetailFact(label, value, tone));
            return this;
        }
    }

    /// <summary>The identity of a clicked object, copied from its <see cref="HoverTarget"/> (testable without a scene).</summary>
    public readonly struct DetailTarget
    {
        public DetailTarget(TargetCategory category, string id, string hull, string place, string kind, string title, string detail)
        {
            Category = category;
            Id = id;
            Hull = hull;
            Place = place;
            Kind = kind;
            Title = title;
            Detail = detail;
        }

        public TargetCategory Category { get; }
        public string Id { get; }
        public string Hull { get; }
        public string Place { get; }
        public string Kind { get; }
        public string Title { get; }
        public string Detail { get; }

        /// <summary>Same object (category and key; title for unkeyed props).</summary>
        public bool SameAs(DetailTarget other) =>
            Category == other.Category && string.Equals(Id ?? Title, other.Id ?? other.Title, StringComparison.Ordinal);

        public static DetailTarget From(HoverTarget target) => target == null
            ? default
            : new DetailTarget(target.Category, target.Id, target.Hull, target.Place, target.Kind, target.Title, target.Detail);
    }
}

namespace ShipyardTwin
{
    /// <summary>A shop's blocks laid out by stage: one lane (bay) per stage, one chip per block.</summary>
    public sealed class BayBoard
    {
        public readonly System.Collections.Generic.List<Bay> Bays = new System.Collections.Generic.List<Bay>();

        /// <summary>Chips drawn per lane; the rest are summed in a "+N" chip.</summary>
        public int MaxChips = 12;

        public int Total => System.Linq.Enumerable.Sum(Bays, b => b.Chips.Count);

        /// <summary>Blocks of more than one hull: chips name their hull.</summary>
        public bool MultiHull => System.Linq.Enumerable.Count(System.Linq.Enumerable.Distinct(
            System.Linq.Enumerable.Select(System.Linq.Enumerable.SelectMany(Bays, b => b.Chips), c => c.Hull))) > 1;
    }

    public sealed class Bay
    {
        public string Title;
        public readonly System.Collections.Generic.List<BayChip> Chips = new System.Collections.Generic.List<BayChip>();
    }

    public sealed class BayChip
    {
        public string Block;
        public string Hull;

        /// <summary>Days left in the stage ("D-3"), "D-day", "2일 지연" or "대기 4일".</summary>
        public string Note;

        public GrowthLab.UI.StatusLevel Tone = GrowthLab.UI.StatusLevel.Normal;
    }
}
