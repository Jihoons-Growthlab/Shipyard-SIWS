using System;
using System.Collections.Generic;
using System.Linq;
using GrowthLab.Core;
using GrowthLab.UI;
using GrowthLab.UI.Charts;
using UnityEngine;
using UnityEngine.UIElements;

namespace ShipyardTwin
{
    /// <summary>
    /// Click (or tap) a block, ship, crane, vehicle, dock, quay, building or scenery machine to open one shared detail
    /// popup (Dashboard.uxml "object-detail"): it slides and fades in from the right (a bottom sheet on compact screens),
    /// its sections follow one after another and their charts draw themselves in. Content comes from
    /// <see cref="ObjectDetailBuilder"/>: the yard snapshot at once, then ERP routes the snapshot lacks
    /// (<see cref="ErpDetailCache"/>) fill the pending sections when they arrive. The selected model itself is outlined
    /// (<see cref="SelectionHighlight"/>); a selected heavy machine also gets a simulated danger zone on the ground
    /// (<see cref="EquipmentZone"/>, labelled as an estimate in the popup, "detail-zone"). A click picks what the hover tooltip names (<see cref="HoverTooltipView.Pick"/>);
    /// clicks that drag the camera, start on the dashboard or hit nothing named do not change the popup. Close with
    /// the X, Escape (<see cref="DashboardShellView"/>) or by clicking the same object again.
    /// Command line <c>-shipyard-select x,y,z</c> (yard metres) or <c>-shipyard-select ID</c> (hull, block, equipment or
    /// facility ID, or a title) opens the popup for that object once the yard is built (screenshots).
    /// </summary>
    [RequireComponent(typeof(UIDocument))]
    public sealed class ObjectDetailView : MonoBehaviour, ICoreConsumer
    {
        public const string SelectArgument = "-shipyard-select";
        private const float ClickSlopPixels = 6f, TapSeconds = 0.35f, Staggers = 0.07f;
        private const int OpenMilliseconds = 420;

        private VisualElement _root, _panel, _facts, _sections, _status;
        private Label _kind, _title, _subtitle, _source, _zoneNote;
        private WorldLabels _labels;
        private readonly SelectionHighlight _highlight = new SelectionHighlight();
        private readonly EquipmentZone _zone = new EquipmentZone();
        private ScrollView _scroll;
        private YardDataSource _data;
        private ShipView _ship;
        private DateTime _builtDay;
        private float _nextDayCheck;
        private readonly ErpDetailCache _cache = new ErpDetailCache();
        private HoverTarget _target;
        private DetailTarget _identity;
        private readonly HashSet<string> _pending = new HashSet<string>();
        private Vector2 _pressAt;
        private float _pressTime;
        private bool _pressValid;
        private int _generation;
        private Vector3? _probe;
        private string _selectId;

        public static ObjectDetailView Active { get; private set; }

        public bool IsOpen { get; private set; }

        /// <summary>Raised after the popup opens for a target, or with null when it closes.</summary>
        public event Action<HoverTarget> SelectionChanged;

        public void Construct(CoreContext context)
        {
            if (_data == null && context.Services.TryGet(out _data))
            {
                _data.Changed += () =>
                {
                    if (IsOpen && _data.Status != YardDataStatus.Loading) Rebuild(animate: false);
                };
            }
        }

        private void OnEnable() => Active = this;

        private void OnDisable()
        {
            if (Active == this) Active = null;
            ClearSelectionVisuals();
        }

        private void OnDestroy()
        {
            _highlight.Dispose();
            _zone.Dispose();
        }

        private void Start()
        {
            _root = GetComponent<UIDocument>().rootVisualElement;
            _panel = _root?.Q("object-detail");
            if (_panel == null)
            {
                return;
            }

            _kind = _panel.Q<Label>("detail-kind");
            _title = _panel.Q<Label>("detail-title");
            _subtitle = _panel.Q<Label>("detail-subtitle");
            _status = _panel.Q("detail-status");
            _facts = _panel.Q("detail-facts");
            _sections = _panel.Q("detail-sections");
            _scroll = _panel.Q<ScrollView>("detail-scroll");
            _source = _panel.Q<Label>("detail-source");
            _zoneNote = _panel.Q<Label>("detail-zone");
            if (_zoneNote != null) _zoneNote.style.display = DisplayStyle.None;
            _labels = GetComponent<WorldLabels>();

            var close = _panel.Q<Button>("detail-close");
            if (close != null) close.clicked += Close;
            _panel.style.visibility = Visibility.Hidden;
            var select = ArgumentValue(SelectArgument);
            _probe = ParseProbe(select);
            if (_probe == null && !string.IsNullOrEmpty(select)) _selectId = select;
        }

        private void Update()
        {
            if (_panel == null)
            {
                return;
            }

            if (_probe != null && Camera.main != null && Time.frameCount > 5)
            {
                var p = _probe.Value;
                _probe = null;
                var screen = (Vector2)Camera.main.WorldToScreenPoint(ShipCoordinates.YardToUnity(p.x, p.y, p.z));
                Select(HoverTooltipView.Pick(Camera.main, screen));
            }

            if (_selectId != null && Time.frameCount > 90 && Time.frameCount % 15 == 0)
            {
                // The yard may still be building: retry until the object exists (at most 20 s).
                var id = _selectId;
                var targets = FindObjectsByType<HoverTarget>(FindObjectsSortMode.None).Where(t => !t.IsOccluder && t.gameObject.activeInHierarchy).ToList();
                var found = targets.FirstOrDefault(t => t.Id == id) ?? targets.FirstOrDefault(t => t.Title == id);
                if (found != null || Time.realtimeSinceStartup > 20f)
                {
                    _selectId = null;
                    Debug.Log("[ShipyardTwin] select " + id + ": " + (found != null ? found.Category + " " + found.Title : "not found"));
                    Select(found);
                }
            }

            TrackClick();
        }

        private void LateUpdate()
        {
            // Playback can move a selected block into a hidden hull when it is erected.
            if (_target != null && _target.Category == TargetCategory.Block) CloseIfHidden();
            if (_target == null && IsOpen && _identity.Category != TargetCategory.None && (_highlight.IsShowing || _zone.IsShowing))
            {
                ClearSelectionVisuals();   // the object went away (hull switch): keep the popup, drop its highlight
            }

            if (IsOpen)
            {
                _highlight.Render();
                _zone.Render(Camera.main);
            }
            AvoidCranePanel();
            FollowPlayback();
        }

        /// <summary>
        /// The crane safety panel (<see cref="CraneSafetyView"/>, "crane-panel") uses the same top-right corner: while it
        /// is shown the popup sits to its left instead of under it (wide screens; the compact sheet is at the bottom).
        /// </summary>
        private void AvoidCranePanel()
        {
            _cranePanel ??= _root?.Q("crane-panel");
            if (_panel == null || _cranePanel == null)
            {
                return;
            }

            var shown = _cranePanel.ClassListContains("tw-crane--show") && !_root.ClassListContains("tw-compact") &&
                        !float.IsNaN(_cranePanel.layout.width) && _cranePanel.layout.width > 0f;
            var right = shown ? _root.layout.width - _cranePanel.layout.xMin + 12f : float.NaN;
            if (shown != _besideCrane || shown && Mathf.Abs(right - _besideRight) > 0.5f)
            {
                _besideCrane = shown;
                _besideRight = right;
                _panel.style.right = shown ? new StyleLength(right) : new StyleLength(StyleKeyword.Null);
            }
        }

        private VisualElement _cranePanel;
        private bool _besideCrane;
        private float _besideRight;

        /// <summary>A click is a short press and release that hardly moved, not started over the dashboard.</summary>
        private void TrackClick()
        {
            if (Input.touchCount > 0)
            {
                var touch = Input.GetTouch(0);
                if (touch.phase == TouchPhase.Began)
                {
                    _pressAt = touch.position;
                    _pressTime = Time.unscaledTime;
                    _pressValid = Input.touchCount == 1 && !DashboardShellView.PointerOverUi;
                }

                if (Input.touchCount > 1 || (touch.position - _pressAt).magnitude > ClickSlopPixels * 2f) _pressValid = false;
                if (touch.phase == TouchPhase.Ended && _pressValid && Time.unscaledTime - _pressTime <= TapSeconds)
                {
                    _pressValid = false;
                    Select(HoverTooltipView.Pick(Camera.main, touch.position), fromClick: true);
                }

                return;
            }

            var mouse = (Vector2)Input.mousePosition;
            if (Input.GetMouseButtonDown(0))
            {
                var modifier = Input.GetKey(KeyCode.LeftAlt) || Input.GetKey(KeyCode.RightAlt) || Input.GetKey(KeyCode.LeftControl) ||
                               Input.GetKey(KeyCode.RightControl) || Input.GetKey(KeyCode.LeftCommand);
                _pressAt = mouse;
                _pressValid = !modifier && !DashboardShellView.PointerOverUi;
            }

            if (Input.GetMouseButton(0) && (mouse - _pressAt).magnitude > ClickSlopPixels) _pressValid = false;
            if (Input.GetMouseButtonUp(0) && _pressValid)
            {
                _pressValid = false;
                Select(HoverTooltipView.Pick(Camera.main, mouse), fromClick: true);
            }
        }

        /// <summary>Opens the popup for a target (null is ignored). Clicking the open object again closes it.</summary>
        public void Select(HoverTarget target, bool fromClick = false)
        {
            if (target == null || _panel == null)
            {
                return;
            }

            var identity = DetailTarget.From(target);
            if (IsOpen && identity.SameAs(_identity) && (fromClick || _target == target))
            {
                // Second click of a double click (camera focus) keeps it; a later click on the same object closes it.
                if (fromClick && Time.unscaledTime - _lastSelect > 0.4f) Close();
                return;
            }

            _lastSelect = Time.unscaledTime;
            _target = target;
            _identity = identity;
            Rebuild(animate: true);
            ShowPanel();
            ShowSelectionVisuals(target);
            SelectionChanged?.Invoke(target);
            LoadErp(++_generation);
        }

        /// <summary>The day the popup describes: the playback (or yard calendar) date of the ship view, else the plan day.</summary>
        private DateTime Day
        {
            get
            {
                if (_ship == null) _ship = FindFirstObjectByType<ShipView>();
                return (_ship != null ? _ship.CurrentDate : _data?.AsOf ?? DateTime.Today).Date;
            }
        }

        /// <summary>While the date plays on, an open popup is rebuilt (without animation) when the day changes.</summary>
        private void FollowPlayback()
        {
            if (!IsOpen || Time.unscaledTime < _nextDayCheck)
            {
                return;
            }

            _nextDayCheck = Time.unscaledTime + 0.25f;
            if (Day != _builtDay) Rebuild(animate: false);
        }

        private float _lastSelect = -10f;
        private string _categoryClass;

        public void CloseIfHidden()
        {
            if (_target != null && !_target.gameObject.activeInHierarchy) Close();
        }

        public void Close()
        {
            if (!IsOpen)
            {
                return;
            }

            IsOpen = false;
            _generation++;
            _target = null;
            _identity = default;
            _panel.RemoveFromClassList("tw-detail--open");
            _root.EnableInClassList("tw-detail-open", false);
            _panel.schedule.Execute(() =>
            {
                if (!IsOpen) _panel.style.visibility = Visibility.Hidden;
            }).ExecuteLater(OpenMilliseconds);
            ClearSelectionVisuals();
            SelectionChanged?.Invoke(null);
        }

        private void ShowPanel()
        {
            if (IsOpen)
            {
                // Already open on another object: a short nudge instead of a full slide.
                _panel.RemoveFromClassList("tw-detail--swap");
                _panel.schedule.Execute(() => _panel.AddToClassList("tw-detail--swap")).ExecuteLater(10);
                _panel.schedule.Execute(() => _panel.RemoveFromClassList("tw-detail--swap")).ExecuteLater(170);
                return;
            }

            IsOpen = true;
            _panel.style.visibility = Visibility.Visible;
            _root.EnableInClassList("tw-detail-open", true);
            // Next frame, so the closed state is laid out first and the transition runs.
            _panel.schedule.Execute(() =>
            {
                if (IsOpen) _panel.AddToClassList("tw-detail--open");
            });
        }

        private async void LoadErp(int generation)
        {
            var routes = ObjectDetailBuilder.Routes(_identity);
            if (routes.Length == 0 || !ErpDetailCache.Configured || _cache.Has(routes))
            {
                return;
            }

            try
            {
                await _cache.Load(routes);
            }
            catch (Exception exception)
            {
                Debug.LogWarning("[ShipyardTwin] detail query failed: " + exception.GetType().Name);
            }

            if (generation == _generation && IsOpen)
            {
                Rebuild(animate: false);
            }
        }

        /// <summary>Builds the detail and renders it; <paramref name="animate"/> staggers every section in, else only the ones that were pending.</summary>
        private void Rebuild(bool animate)
        {
            if (_identity.Category == TargetCategory.None && string.IsNullOrEmpty(_identity.Title))
            {
                return;
            }

            var model = _data?.Model;
            var context = new DetailContext(model?.Source, model, Day, _cache);
            _builtDay = context.AsOf;
            ObjectDetail detail;
            try
            {
                detail = ObjectDetailBuilder.Build(_identity, context);
            }
            catch (Exception exception)
            {
                Debug.LogException(exception);
                detail = new ObjectDetail { Kind = _identity.Kind, Title = _identity.Title, Subtitle = _identity.Detail, Status = "정보 오류", StatusLevel = StatusLevel.Alarm };
                detail.Section("상세 정보", "이 오브젝트의 정보를 만들지 못했습니다.");
            }

            Render(detail, animate);
        }

        private void Render(ObjectDetail detail, bool animate)
        {
            var wasPending = new HashSet<string>(_pending);
            _pending.Clear();
            // Accent colour per kind of object (tw-detail--hull, --block, --crane …).
            if (_categoryClass != null) _panel.RemoveFromClassList(_categoryClass);
            _categoryClass = "tw-detail--" + CategoryClass(_identity.Category);
            _panel.AddToClassList(_categoryClass);

            _kind.text = detail.Kind ?? string.Empty;
            _title.text = detail.Title ?? string.Empty;
            _subtitle.text = detail.Subtitle ?? string.Empty;
            _subtitle.style.display = string.IsNullOrEmpty(detail.Subtitle) ? DisplayStyle.None : DisplayStyle.Flex;
            _status.Clear();
            if (!string.IsNullOrEmpty(detail.Status)) _status.Add(new StatusBadge(detail.StatusLevel, detail.Status));

            _facts.Clear();
            foreach (var fact in detail.Facts)
            {
                var item = new VisualElement();
                item.AddToClassList("tw-fact");
                var label = new Label(fact.Label);
                label.AddToClassList("tw-fact-label");
                var value = new Label(fact.Value);
                value.AddToClassList("tw-fact-value");
                if (fact.Tone.HasValue) value.AddToClassList("tw-tone--" + fact.Tone.Value.ToString().ToLowerInvariant());
                item.Add(label);
                item.Add(value);
                _facts.Add(item);
            }

            _sections.Clear();
            var index = 0;
            foreach (var section in detail.Sections)
            {
                var animateThis = animate || wasPending.Contains(section.Title);
                var box = RenderSection(section, animateThis ? index : -1);
                _sections.Add(box);
                if (section.Pending) _pending.Add(section.Title);
                if (animateThis)
                {
                    var delay = (long)((animate ? 120 : 0) + index * Staggers * 1000f);
                    box.schedule.Execute(() => box.AddToClassList("tw-detail-section--in")).ExecuteLater(Math.Max(16, delay));
                }
                else
                {
                    box.AddToClassList("tw-detail-section--in");
                }

                index++;
            }

            _source.text = detail.Source ?? string.Empty;
            if (animate) _scroll?.schedule.Execute(() => _scroll.scrollOffset = Vector2.zero);
        }

        /// <summary>One section card: title, note, charts (a donut shares a row with the chart after it), rows.</summary>
        private static VisualElement RenderSection(DetailSection section, int stagger)
        {
            var box = new VisualElement();
            box.AddToClassList("tw-detail-section");
            var title = new Label(section.Title);
            title.AddToClassList("tw-section-title");
            box.Add(title);
            if (!string.IsNullOrEmpty(section.Note))
            {
                var note = new Label(section.Note);
                note.AddToClassList("tw-section-note");
                if (section.Pending) note.AddToClassList("tw-section-note--pending");
                box.Add(note);
            }

            if (section.Pending)
            {
                var skeleton = new VisualElement();
                skeleton.AddToClassList("tw-skeleton");
                for (var i = 0; i < 3; i++)
                {
                    var bar = new VisualElement();
                    bar.AddToClassList("tw-skeleton-bar");
                    bar.style.width = Length.Percent(90 - i * 22);
                    skeleton.Add(bar);
                }

                box.Add(skeleton);
                // Gentle pulse while the ERP answers (no keyframes in USS: a class flipped on a timer with a transition).
                skeleton.schedule.Execute(() => skeleton.ToggleInClassList("tw-skeleton--dim")).Every(600);
            }

            if (section.Bays != null) box.Add(RenderBays(section.Bays, stagger));

            var charts = section.Charts;
            for (var i = 0; i < charts.Count; i++)
            {
                var chart = Chart.Create(charts[i]);
                if (charts[i] is DonutChartSpec && i + 1 < charts.Count && !(charts[i + 1] is TimelineChartSpec))
                {
                    var row = new VisualElement();
                    row.AddToClassList("tw-chart-row");
                    // Explicit height: header + plot + caption of the taller chart (a row of fixed-height plots can measure short).
                    row.style.minHeight = Mathf.Max(charts[i].Height, charts[i + 1].Height) + 44f;
                    chart.AddToClassList("tw-chart-donut");
                    row.Add(chart);
                    var next = Chart.Create(charts[i + 1]);
                    row.Add(next);
                    box.Add(row);
                    Reveal(chart, stagger, i);
                    Reveal(next, stagger, i + 1);
                    i++;
                    continue;
                }

                if (charts[i] is DonutChartSpec) chart.AddToClassList("tw-chart-donut--solo");
                box.Add(chart);
                Reveal(chart, stagger, i);
            }

            if (section.Rows.Count > 0)
            {
                var list = new VisualElement();
                list.AddToClassList("tw-detail-rows");
                foreach (var row in section.Rows)
                {
                    var line = new VisualElement();
                    line.AddToClassList("tw-detail-row");
                    var label = new Label(row.Label);
                    label.AddToClassList("tw-row-label");
                    var value = new Label(row.Value);
                    value.AddToClassList("tw-row-value");
                    if (row.Tone.HasValue) value.AddToClassList("tw-tone--" + row.Tone.Value.ToString().ToLowerInvariant());
                    line.Add(label);
                    line.Add(value);
                    list.Add(line);
                }

                box.Add(list);
            }

            return box;
        }

        /// <summary>
        /// Shop floor: one lane per stage (name and count on the left, block chips on the right, a "+N" chip past
        /// <see cref="BayBoard.MaxChips"/>). Lanes slide in one after another with the section; chips are coloured by tone.
        /// </summary>
        private static VisualElement RenderBays(BayBoard board, int stagger)
        {
            var floor = new VisualElement();
            floor.AddToClassList("tw-bays");
            var most = Math.Max(1, board.Bays.Max(b => b.Chips.Count));
            var multiHull = board.MultiHull;
            for (var i = 0; i < board.Bays.Count; i++)
            {
                var bay = board.Bays[i];
                var lane = new VisualElement();
                lane.AddToClassList("tw-bay");
                if (bay.Title == "반출 대기") lane.AddToClassList("tw-bay--waiting");
                var head = new VisualElement();
                head.AddToClassList("tw-bay-head");
                var name = new Label(bay.Title);
                name.AddToClassList("tw-bay-name");
                var count = new Label(bay.Chips.Count.ToString());
                count.AddToClassList("tw-bay-count");
                var meter = new VisualElement();
                meter.AddToClassList("tw-bay-meter");
                var fill = new VisualElement();
                fill.AddToClassList("tw-bay-meter-fill");
                fill.style.width = Length.Percent(100f * bay.Chips.Count / most);
                meter.Add(fill);
                head.Add(name);
                head.Add(count);
                head.Add(meter);
                lane.Add(head);

                var chips = new VisualElement();
                chips.AddToClassList("tw-bay-chips");
                foreach (var chip in bay.Chips.Take(board.MaxChips))
                {
                    var item = new VisualElement { tooltip = chip.Hull + " " + chip.Block + " · " + chip.Note };
                    item.AddToClassList("tw-chip-block");
                    item.AddToClassList("tw-chip-block--" + chip.Tone.ToString().ToLowerInvariant());
                    var id = new Label(chip.Block);
                    id.AddToClassList("tw-chip-block-id");
                    var note = new Label(multiHull ? chip.Hull + " · " + chip.Note : chip.Note);
                    note.AddToClassList("tw-chip-block-note");
                    item.Add(id);
                    item.Add(note);
                    chips.Add(item);
                }

                if (bay.Chips.Count > board.MaxChips)
                {
                    var more = new Label("+" + (bay.Chips.Count - board.MaxChips));
                    more.AddToClassList("tw-chip-more");
                    chips.Add(more);
                }

                lane.Add(chips);
                floor.Add(lane);
                if (stagger < 0)
                {
                    lane.AddToClassList("tw-bay--in");
                }
                else
                {
                    var delay = 180L + (long)(stagger * Staggers * 1000f) + i * 70L;
                    lane.schedule.Execute(() => lane.AddToClassList("tw-bay--in")).ExecuteLater(delay);
                }
            }

            return floor;
        }

        /// <summary>Charts draw in after their section has started sliding in; -1 = show at once.</summary>
        private static void Reveal(ChartElement chart, int stagger, int position)
        {
            if (stagger < 0)
            {
                return;
            }

            chart.Animate(0.9f, 220 + (long)(stagger * Staggers * 1000f) + position * 90);
        }

        private static string CategoryClass(TargetCategory category) => category.ToString().ToLowerInvariant();

        // ------------------------------------------------------------------ selection highlight and heavy equipment zone

        /// <summary>Outline on the new target (replacing the last one) and, for heavy equipment, its simulated danger zone.</summary>
        private void ShowSelectionVisuals(HoverTarget target)
        {
            _highlight.Show(target);
            var zone = _zone.Show(target, _labels);
            if (_zoneNote == null)
            {
                return;
            }

            _zoneNote.text = zone ? "모의 위험반경 ≈ " + EquipmentZone.Metres(_zone.Radius) + " · " + EquipmentZone.Notice : string.Empty;
            _zoneNote.style.display = zone ? DisplayStyle.Flex : DisplayStyle.None;
        }

        private void ClearSelectionVisuals()
        {
            _highlight.Clear();
            _zone.Clear();
            if (_zoneNote != null) _zoneNote.style.display = DisplayStyle.None;
        }

        private static Vector3? ParseProbe(string value)
        {
            var parts = value?.Split(',');
            if (parts == null || parts.Length != 3) return null;
            var v = new float[3];
            for (var i = 0; i < 3; i++)
            {
                if (!float.TryParse(parts[i], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out v[i])) return null;
            }

            return new Vector3(v[0], v[1], v[2]);
        }

        private static string ArgumentValue(string name)
        {
            var args = Environment.GetCommandLineArgs();
            var index = Array.IndexOf(args, name);
            return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
        }
    }
}
