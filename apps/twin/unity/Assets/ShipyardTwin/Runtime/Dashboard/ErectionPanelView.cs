using System;
using System.Collections.Generic;
using System.Linq;
using GrowthLab.Core;
using GrowthLab.UI;
using UnityEngine;
using UnityEngine.UIElements;

namespace ShipyardTwin
{
    /// <summary>
    /// Erection drawer (Dashboard.uxml): ERP data status, the hull list (grouped by dock, scrolling on its own), yard and unplaced counts, and
    /// for the selected hull the simulation (date, step, progress gauges, play/speed/restart, block on the hook,
    /// erection log). Choosing a hull only prepares it; the play button starts it. A world label follows the lifted block.
    ///
    /// Updates are event driven: chips, counts and texts are rebuilt when the plan, the selection or the data status
    /// change; while playing the progress texts refresh at most <see cref="RefreshInterval"/> times a second, and
    /// nothing is rebuilt while paused.
    /// </summary>
    [RequireComponent(typeof(UIDocument))]
    public sealed class ErectionPanelView : MonoBehaviour, ICoreConsumer
    {
        private const float RefreshInterval = 0.1f;

        /// <summary>Command line: scroll the drawer to its end after start (screenshots of the folded sections).</summary>
        public const string PanelScrollArgument = "-shipyard-panel-scroll";

        [SerializeField] private float _labelHeight = 18f;

        private ShipView _ship;
        private YardDataSource _source;
        private ErectionPlayback _bound;
        private VisualElement _root, _hullList, _sim;
        private ScrollView _hullScroll;
        private Label _hullCount;
        private Label _data, _summary, _placeholder, _hull, _date, _step, _status, _liftDetail;
        private VisualElement _stats, _unplaced;
        private Foldout _unplacedFold;
        private readonly Dictionary<BlockState, Label> _statValues = new Dictionary<BlockState, Label>();
        private StatCard _liftCard;
        private Gauge _blocks, _weight;
        private MessageList _events;
        private Button _play, _speed, _restart, _exit;
        private WorldLabels _labels;
        private StatusBadge _hookLabel;
        private Transform _labelled;
        private float _shownCursor = -1f, _nextRefresh;
        private bool _shownPlaying;
        private float _shownSpeed;
        private BlockRecord _shownLift;

        public void Construct(CoreContext context)
        {
            if (_source != null || !context.Services.TryGet(out _source))
            {
                return;
            }

            _ship = FindFirstObjectByType<ShipView>();
            _labels = GetComponent<WorldLabels>();
            _root = GetComponent<UIDocument>().rootVisualElement;
            _data = _root.Q<Label>("data-status");
            _hullList = _root.Q("hull-list");
            _hullCount = _root.Q<Label>("hull-count");
            _hullScroll = _root.Q<ScrollView>("hull-scroll");
            if (_hullScroll != null)
            {
                // At either end of the hull list the wheel or swipe carries on scrolling the drawer.
                _hullScroll.nestedInteractionKind = ScrollView.NestedInteractionKind.ForwardScrolling;
            }
            _summary = _root.Q<Label>("yard-summary");
            _sim = _root.Q("sim");
            _placeholder = _root.Q<Label>("sim-placeholder");
            _hull = _root.Q<Label>("hull");
            _date = _root.Q<Label>("date");
            _step = _root.Q<Label>("step");
            _status = _root.Q<Label>("status");
            _blocks = _root.Q<Gauge>("blocks");
            _weight = _root.Q<Gauge>("weight");
            _liftCard = _root.Q<StatCard>("lift");
            _liftDetail = _root.Q<Label>("lift-detail");
            _unplaced = _root.Q("unplaced");
            _unplacedFold = _root.Q<Foldout>("unplaced-fold");
            _stats = _root.Q("yard-stats");
            BuildStats();
            _events = _root.Q<MessageList>("events");
            _play = _root.Q<Button>("play");
            _speed = _root.Q<Button>("speed");
            _restart = _root.Q<Button>("restart");
            _exit = _root.Q<Button>("sim-exit");

            if (_play != null) _play.clicked += () =>
            {
                var playback = _ship?.Playback;
                if (playback != null) playback.Playing = !playback.Playing && (playback.Remote != null || !playback.Schedule.Finished);
                RefreshProgress();
            };
            if (_speed != null) _speed.clicked += () => { _ship?.Playback?.NextSpeed(); RefreshProgress(); };
            if (_restart != null) _restart.clicked += () =>
            {
                _events?.Clear();
                _ship?.Playback?.Restart();
                RefreshProgress();
            };
            // Ends the simulation: deselecting stops the playback and returns every block to its plan-day state.
            if (_exit != null) _exit.clicked += () =>
            {
                _events?.Clear();
                _ship?.Select(null);
            };

            if (Array.IndexOf(Environment.GetCommandLineArgs(), PanelScrollArgument) >= 0)
            {
                // Screenshots of the lower drawer sections: scrolled to the end once laid out.
                var scroll = _root.Q<ScrollView>("erection-scroll");
                scroll?.schedule.Execute(() => scroll.scrollOffset = new Vector2(0f, float.MaxValue)).StartingIn(3000);
            }

            _source.Changed += RefreshData;
            if (_ship != null) _ship.Changed += Rebuild;
            RefreshData();
            Rebuild();
        }

        private void OnDestroy()
        {
            if (_source != null) _source.Changed -= RefreshData;
            if (_ship != null) _ship.Changed -= Rebuild;
            Bind(null);
        }

        private void RefreshData()
        {
            if (_data == null)
            {
                return;
            }

            _data.text = _source.Detail;
            _data.EnableInClassList("tw-data-status--erp", _source.Status == YardDataStatus.Erp);
            _data.EnableInClassList("tw-data-status--loading", _source.Status == YardDataStatus.Loading);
            _data.EnableInClassList("tw-data-status--failed", _source.Status == YardDataStatus.Failed);
        }

        /// <summary>Plan or selection changed: chips, counts, unplaced list, simulation header; rebinds the playback.</summary>
        private void Rebuild()
        {
            var model = _ship?.Model;
            var selected = _ship?.Selected;
            Bind(_ship?.Playback);
            if (_hullList != null && model != null)
            {
                BuildHullList(model, selected);
            }

            ShowYardPlan(model?.Source, selected);
            if (model != null) ShowUnplaced(model, selected);

            if (_sim != null) _sim.style.display = selected != null ? DisplayStyle.Flex : DisplayStyle.None;
            if (_placeholder != null) _placeholder.style.display = selected != null ? DisplayStyle.None : DisplayStyle.Flex;
            var history = _root.Q("history");
            if (history != null) history.style.display = selected != null ? DisplayStyle.Flex : DisplayStyle.None;
            if (selected != null && _hull != null)
            {
                var info = selected.Info;
                var site = model.Site;
                _hull.text = selected.Id + " · " + info.vessel_type + "\n" + info.length_m + " × " + info.beam_m + " × " + info.depth_m + " m · 블록 " +
                             selected.Blocks.Count + "개 · " + (selected.Dock?.name ?? selected.Placement.dock) +
                             (site != null && !string.IsNullOrEmpty(site.id) ? "\n" + site.name : string.Empty);
                var actual = model.Source?.hull_overview?.FirstOrDefault(row => row.hull == selected.Id);
                if (!string.IsNullOrEmpty(actual?.lifecycle_status))
                {
                    _hull.text += $"\nERP 실적 · {actual.lifecycle_status}\n탑재 검사 합격 {actual.erection_accepted_count}/{actual.erection_planned_count}" +
                                  $" · 진수 {actual.launch_actual ?? "미등록"}";
                }
            }

            if (_blocks != null && _ship?.Schedule != null) _blocks.Max = _ship.Schedule.Entries.Count;
            if (_weight != null && _ship?.Schedule != null) _weight.Max = Mathf.Max(1f, Mathf.Round(_ship.Schedule.TotalWeight));
            if (_play != null) _play.SetEnabled(selected != null);
            _events?.Clear();
            RefreshCounts();
            RefreshProgress();
        }

        /// <summary>
        /// Hull list (a scroll area of its own, "hull-scroll"): one row per hull, grouped by dock, the hulls in a dock in
        /// order of their dock stay. A row shows the hull, its vessel type, block count and dock status on the plan day.
        /// The selected row is kept in view.
        /// </summary>
        private void BuildHullList(YardModel model, HullPlan selected)
        {
            _hullList.Clear();
            if (model.Hulls.Count == 0)
            {
                _hullList.Add(new Label("표시할 호선이 없습니다. ERP 연결이나 스냅샷(export_yard.py)을 확인하세요.") { name = "no-hulls" });
                return;
            }

            if (_hullCount != null) _hullCount.text = model.Hulls.Count + "척";
            Button selectedRow = null;
            var groups = model.Hulls.GroupBy(h => h.Dock?.name ?? h.Placement?.dock ?? "도크 미배정")
                .OrderBy(g => g.Key, StringComparer.Ordinal);
            foreach (var group in groups)
            {
                var header = new Label(group.Key + " · " + group.Count() + "척");
                header.AddToClassList("tw-hull-group");
                _hullList.Add(header);
                foreach (var hull in group.OrderBy(h => h.DockStays.Count > 0 ? h.DockStays.Min(d => d.Start) : DateTime.MaxValue)
                             .ThenBy(h => h.Id, StringComparer.Ordinal))
                {
                    var row = new Button { name = "hull-" + hull.Id, tooltip = hull.Name };
                    row.AddToClassList("tw-hull-row");
                    row.EnableInClassList("tw-hull-row--active", hull == selected);
                    var text = new VisualElement { pickingMode = PickingMode.Ignore };
                    text.AddToClassList("tw-hull-text");
                    var title = new Label(hull.Id) { pickingMode = PickingMode.Ignore };
                    title.AddToClassList("tw-hull-id");
                    var sub = new Label(Short(hull.Info?.vessel_type) + " · 블록 " + hull.Blocks.Count) { pickingMode = PickingMode.Ignore };
                    sub.AddToClassList("tw-hull-sub");
                    text.Add(title);
                    text.Add(sub);
                    row.Add(text);
                    var (status, kind) = DockStatus(hull, model.AsOf);
                    var badge = new Label(status) { pickingMode = PickingMode.Ignore };
                    badge.AddToClassList("tw-hull-badge");
                    badge.AddToClassList("tw-hull-badge--" + kind);
                    row.Add(badge);
                    var id = hull.Id;
                    row.clicked += () => _ship.Select(_ship.Selected?.Id == id ? null : id);
                    _hullList.Add(row);
                    if (hull == selected) selectedRow = row;
                }
            }

            if (selectedRow != null && _hullScroll != null)
            {
                _hullScroll.schedule.Execute(() => _hullScroll.ScrollTo(selectedRow));
            }
        }

        /// <summary>Dock booking status on the yard's reference day.</summary>
        private static (string Text, string Kind) DockStatus(HullPlan hull, DateTime day)
        {
            if (hull.DockStays.Count == 0) return ("일정 미등록", "none");
            if (hull.DockStays.Any(d => d.Start <= day && day <= d.End)) return ("예약 기간 중", "active");
            var next = hull.DockStays.Where(d => d.Start > day).Select(d => d.Start).DefaultIfEmpty(DateTime.MaxValue).Min();
            return next != DateTime.MaxValue ? ("입거 예정 " + next.ToString("MM-dd"), "planned") : ("예약 종료", "done");
        }

        private void ShowYardPlan(YardSnapshot snapshot, HullPlan hull)
        {
            var panel = _root.Q<Foldout>("erp-yard-plan");
            if (panel == null) return;

            panel.Clear();
            panel.style.display = hull == null ? DisplayStyle.None : DisplayStyle.Flex;
            if (snapshot == null || hull == null) return;

            string Point(string id)
            {
                var point = snapshot.shift_points?.FirstOrDefault(row => row.id == id);
                if (point == null) return id + " (좌표 미확인)";
                if (!point.position_known || point.position == null) return point.name + " (좌표 미등록)";
                var p = point.position;
                return $"{point.name} ({p.x:0.#}, {p.y:0.#}, {p.z:0.#} m · {p.heading_deg:0.#}°)";
            }

            var total = 0;
            void Section(string title, IEnumerable<string> lines)
            {
                var rows = lines?.ToArray();
                var heading = new Label(title + (rows == null ? " · 조회 불가" : " · " + rows.Length + "건"));
                heading.AddToClassList("tw-plan-title");
                panel.Add(heading);
                if (rows == null || rows.Length == 0)
                {
                    var empty = new Label(rows == null ? "ERP에서 이 항목을 받지 못했습니다." : "등록된 일정 없음");
                    empty.AddToClassList("tw-plan-empty");
                    panel.Add(empty);
                    return;
                }

                total += rows.Length;
                const int MaxRows = 30;   // long lists (stock plans of every block) are in the schedule bar too
                foreach (var line in rows.Take(MaxRows))
                {
                    // First line: when / what; the rest is detail.
                    var item = new VisualElement();
                    item.AddToClassList("tw-plan-item");
                    var cut = line.IndexOf('\n');
                    var head = new Label(cut >= 0 ? line.Substring(0, cut) : line);
                    head.AddToClassList("tw-plan-head");
                    item.Add(head);
                    if (cut >= 0)
                    {
                        var detail = new Label(line.Substring(cut + 1));
                        detail.AddToClassList("tw-plan-detail");
                        item.Add(detail);
                    }

                    panel.Add(item);
                }

                if (rows.Length > MaxRows)
                {
                    var more = new Label("외 " + (rows.Length - MaxRows) + "건 · 아래 일정 바에서 블록별로 볼 수 있습니다.");
                    more.AddToClassList("tw-plan-empty");
                    panel.Add(more);
                }
            }

            Section("선석 배정 계획", snapshot.berth_assignments?.Where(row => row.hull == hull.Id)
                .Select(row => $"{row.start_day} ~ {row.end_day}\n{Point(row.berth)} · {row.berth_purpose}"));
            Section("크레인 배정", snapshot.crane_assignments?.Where(row => row.hull == hull.Id)
                .Select(row => $"{row.work_day} {row.start_hour:00}:00 · {row.hours}시간\n{row.equipment} · {row.crane_work} {row.block} · {row.lift_t:0.#} t · {row.work_area}"));
            Section("호선 이동", snapshot.quay_shifts?.Where(row => row.hull == hull.Id)
                .Select(row => $"예정 {row.plan_day} · 실적 {row.actual_day ?? "미입력"} · {row.shift_purpose}\n{Point(row.from_point)} → {Point(row.to_point)}"));

            var blocks = new HashSet<string>(hull.Blocks.Select(block => block.Id));
            Section("블록 위치 계획 (적치)", snapshot.block_stock_plans.Where(row => blocks.Contains(row.block))
                .Select(row => $"{row.stock_from} ~ {row.stock_until ?? "인양"} · {HullTimeline.ShortBlock(row.block)}\n{row.location} ({row.x:0.#}, {row.y:0.#} m)"));
            Section("블록 이동 기록", snapshot.moves?.Where(row => blocks.Contains(row.block))
                .Select(row => $"{row.block} · {row.origin ?? "미상"} → {row.destination} · {row.equipment}\n시작 {row.started_at} · 확정 {row.confirmed_at ?? "미확정"}"));
            panel.text = "ERP 야드 일정 · " + total + "건";
        }

        // Count tiles: state, label, colour class (the dots match the block colours of the 3D view legend).
        private static readonly (BlockState State, string Label, string Kind)[] StatTiles =
        {
            // The block being lifted has its own card in the simulation section.
            (BlockState.Stocked, "야드 적치", "stocked"), (BlockState.Erected, "탑재 완료", "erected"),
            (BlockState.InProduction, "생산 중", "production"), (BlockState.Unplaced, "미배치", "unplaced"),
        };

        private void BuildStats()
        {
            if (_stats == null) return;
            _stats.Clear();
            _statValues.Clear();
            foreach (var (state, label, kind) in StatTiles)
            {
                var tile = new VisualElement { name = "stat-" + kind };
                tile.AddToClassList("tw-stat");
                tile.AddToClassList("tw-stat--" + kind);
                var value = new Label("-");
                value.AddToClassList("tw-stat-value");
                var caption = new Label(label);
                caption.AddToClassList("tw-stat-label");
                tile.Add(value);
                tile.Add(caption);
                _stats.Add(tile);
                _statValues[state] = value;
            }

            // The unplaced tile opens the list of those blocks.
            _stats.Q("stat-unplaced")?.RegisterCallback<ClickEvent>(_ =>
            {
                if (_unplacedFold == null) return;
                _unplacedFold.value = true;
                _root.Q<ScrollView>("erection-scroll")?.schedule.Execute(() => _root.Q<ScrollView>("erection-scroll").ScrollTo(_unplacedFold));
            });
        }

        /// <summary>Count tiles: the yard on the plan day, or the selected hull at the simulation date.</summary>
        private void RefreshCounts()
        {
            var model = _ship?.Model;
            if (model == null)
            {
                return;
            }

            var selected = _ship.Selected;
            int Count(BlockState state)
            {
                if (selected != null) return _ship.CountIn(selected, state);
                var c = YardModel.Count(model.Hulls.SelectMany(h => h.Blocks), _ship.CurrentDate.Date);
                return state switch
                {
                    BlockState.Stocked => c.Stocked, BlockState.Erected => c.Erected, BlockState.InProduction => c.InProduction,
                    BlockState.Unplaced => c.Unplaced, _ => 0,
                };
            }

            foreach (var (state, _, _) in StatTiles)
            {
                if (_statValues.TryGetValue(state, out var value)) value.text = Count(state).ToString("N0");
            }

            if (_summary != null)
            {
                _summary.text = selected == null
                    ? "조선소 달력 " + _ship.CurrentDate.ToString("yyyy-MM-dd") + " 기준 · 호선 " + model.Hulls.Count + "척 · 블록 " + model.Blocks.Count.ToString("N0") + "개"
                    : selected.Id + " 시뮬레이션 날짜 기준 · 다른 호선은 달력 정지 시점 상태";
            }
        }

        /// <summary>
        /// Unplaced blocks (no stocking plan or slot, not yet erected on the plan day) as chips per hull; the selected
        /// hull first. The fold title carries the total.
        /// </summary>
        private void ShowUnplaced(YardModel model, HullPlan selected)
        {
            if (_unplaced == null) return;
            _unplaced.Clear();
            var hulls = model.Hulls
                .Select(h => (Hull: h, Blocks: h.Blocks.Where(b => b.Stays.Count == 0 && !(b.ErectionDay <= model.AsOf)).Select(b => b.Id).ToList()))
                .Where(x => x.Blocks.Count > 0)
                .OrderBy(x => x.Hull == selected ? 0 : 1).ThenBy(x => x.Hull.Id, StringComparer.Ordinal).ToList();
            var total = hulls.Sum(x => x.Blocks.Count);
            if (_unplacedFold != null) _unplacedFold.text = "미배치 블록 · " + (total > 0 ? total + "개" : "없음");
            if (total == 0)
            {
                _unplaced.Add(new Label("모든 블록에 적치 계획과 좌표가 있습니다.") { name = "no-unplaced" });
                return;
            }

            const int MaxChips = 24;
            foreach (var (hull, blocks) in hulls)
            {
                var header = new Label(hull.Id + " · " + blocks.Count + "개");
                header.AddToClassList("tw-unplaced-hull");
                header.EnableInClassList("tw-unplaced-hull--selected", hull == selected);
                _unplaced.Add(header);
                var chips = new VisualElement();
                chips.AddToClassList("tw-block-chips");
                foreach (var id in blocks.Take(MaxChips))
                {
                    var chip = new Label(HullTimeline.ShortBlock(id)) { tooltip = id };
                    chip.AddToClassList("tw-block-chip");
                    chips.Add(chip);
                }

                if (blocks.Count > MaxChips)
                {
                    var more = new Label("+" + (blocks.Count - MaxChips));
                    more.AddToClassList("tw-block-chip");
                    more.AddToClassList("tw-block-chip--more");
                    chips.Add(more);
                }

                _unplaced.Add(chips);
            }
        }

        private void Bind(ErectionPlayback playback)
        {
            if (_bound == playback)
            {
                return;
            }

            if (_bound != null) _bound.BlockErected -= OnErected;
            _bound = playback;
            if (_bound != null) _bound.BlockErected += OnErected;
            _shownCursor = -1f;
        }

        private void OnErected(ErectionSchedule.Entry entry) =>
            _events?.Add(StatusLevel.Normal, entry.Block.block + " 탑재 완료 · " + entry.Block.weight_t.ToString("F0") + "t");

        private void Update()
        {
            var playback = _ship?.Playback;
            if (playback == null)
            {
                UpdateHookLabel(null);
                if (Time.unscaledTime >= _nextRefresh && _ship?.Model != null)
                {
                    _nextRefresh = Time.unscaledTime + 1f;   // yard calendar: the yard counts follow its day
                    RefreshCounts();
                }

                return;
            }

            var changed = !Mathf.Approximately(playback.Schedule.Cursor, _shownCursor) || playback.Playing != _shownPlaying ||
                          !Mathf.Approximately(playback.SpeedMultiplier, _shownSpeed) || _ship.CurrentLiftBlock != _shownLift || _ship.Synced;
            if (changed && Time.unscaledTime >= _nextRefresh)
            {
                RefreshProgress();
            }
        }

        /// <summary>Simulation texts and gauges; only called when something they show changed.</summary>
        private void RefreshProgress()
        {
            _nextRefresh = Time.unscaledTime + RefreshInterval;
            var playback = _ship?.Playback;
            if (playback == null)
            {
                if (_play != null) _play.text = "재생";
                return;
            }

            var schedule = playback.Schedule;
            _shownCursor = schedule.Cursor;
            _shownPlaying = playback.Playing;
            _shownSpeed = playback.SpeedMultiplier;
            var sync = YardSimSync.Active;
            var synced = sync != null && sync.Synced;
            if (_date != null) _date.text = _ship.CurrentDate.ToString(synced ? "yyyy-MM-dd HH:mm" : "yyyy-MM-dd") +
                                            (synced ? "  · " + sync.Status : schedule.Finished ? "  · 탑재 재생 완료" : string.Empty);
            if (_step != null) _step.text = "탑재 단계 " + schedule.CurrentStep + " / 블록 " + schedule.ErectedCount + "개 탑재";
            if (_blocks != null) _blocks.Value = schedule.ErectedCount;
            if (_weight != null) _weight.Value = Mathf.Round(schedule.ErectedWeight);
            if (_play != null) _play.text = playback.Playing ? "일시정지" : schedule.Finished ? "완료" : "재생";
            if (_speed != null) _speed.text = "속도 ×" + playback.SpeedMultiplier.ToString("0.##");
            if (_status != null)
            {
                var state = schedule.Finished ? "탑재 재생 완료" : playback.Playing ? "재생 중" : schedule.Cursor > 0f ? "일시정지" : "대기";
                _status.text = state;
                _status.EnableInClassList("tw-status--playing", playback.Playing);
                _status.EnableInClassList("tw-status--paused", !playback.Playing && !schedule.Finished && schedule.Cursor > 0f);
            }

            var block = _ship.CurrentLiftBlock;
            _shownLift = block;
            if (_liftCard != null)
            {
                _liftCard.Title = block != null ? block.block : "대기";
                _liftCard.Level = block != null ? StatusLevel.Warning : StatusLevel.Offline;
            }

            if (_liftDetail != null)
            {
                _liftDetail.text = block != null
                    ? block.zone + "\n" + block.weight_t.ToString("F1") + "t · " + block.Size.x.ToString("F1") + " × " + block.Size.y.ToString("F1") +
                      " × " + block.Size.z.ToString("F1") + " m\n선행 " + (block.predecessors.Length > 0 ? string.Join(", ", block.predecessors) : "없음") +
                      (_ship.CurrentLiftHooked ? string.Empty : "\n트랜스포터로 PE장 이동 중")
                    : "인양 중인 블록 없음";
            }

            RefreshCounts();
            UpdateHookLabel(block);
        }

        private void UpdateHookLabel(BlockRecord block)
        {
            if (_labels == null)
            {
                return;
            }

            var target = block != null ? _ship.CurrentLift : null;
            if (target == _labelled)
            {
                return;
            }

            if (_hookLabel != null)
            {
                _labels.Untrack(_hookLabel);
                _hookLabel = null;
            }

            _labelled = target;
            if (target != null)
            {
                var id = block.block.Substring(block.block.LastIndexOf('-') + 1);
                _hookLabel = _labels.Track(target, new StatusBadge(StatusLevel.Warning, id + " 인양"), Vector3.up * _labelHeight);
                _hookLabel.AddToClassList("tw-lift-label");
            }
        }

        private static string Short(string vesselType)
        {
            if (string.IsNullOrEmpty(vesselType)) return "-";
            var cut = vesselType.IndexOf('(');
            return (cut > 0 ? vesselType.Substring(0, cut) : vesselType).Trim();
        }
    }
}
