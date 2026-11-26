using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using GrowthLab.Core;
using UnityEngine;

namespace ShipyardTwin
{
    /// <summary>
    /// Every block of every hull in the yard plan, one GameObject each, kept for the whole session and found by block
    /// ID: in its yard slot, on the hook, or on the hull. Without a selected hull each block shows its state on the plan
    /// day (<see cref="YardModel.AsOf"/>). Selecting a hull resets its erection simulation to day 0; playback then moves
    /// only that hull's blocks: slot → (transporter) → crane → hull, in ERP erection order. Other hulls stay as planned.
    ///
    /// Cost: objects are created over several frames within <see cref="_frameBudgetMs"/>; OBJ text is parsed on worker
    /// threads and turned into meshes on the main thread within the same budget; a block gets a mesh only when it is
    /// shown or its hull is selected, and blocks hidden again after a switch release theirs. Per frame only the blocks
    /// whose state changes (sorted cursor events) and those actually moving are touched; nothing runs while paused.
    /// Each OBJ material (paint) is one submesh on shared palette materials; a lifted block is tinted, not repainted.
    /// </summary>
    public sealed class ShipView : MonoBehaviour, ICoreConsumer
    {
        public const string HullArgument = "-shipyard-hull";

        [SerializeField] private Color _liftTint = new Color(1f, 0.55f, 0.1f);
        [SerializeField, Range(0f, 1f)] private float _liftTintAmount = 0.4f;

        [Tooltip("Main-thread time per frame for creating blocks and meshes (ms).")]
        [SerializeField] private float _frameBudgetMs = 4f;

        [Tooltip("OBJ files parsed in parallel on worker threads.")]
        [SerializeField] private int _parallelParses = 6;

        private static readonly int ColorId = Shader.PropertyToID("_Color");
        private static readonly int BaseColorId = Shader.PropertyToID("_BaseColor");

        private readonly Dictionary<string, Actor> _actors = new Dictionary<string, Actor>(StringComparer.Ordinal);
        private readonly Dictionary<string, Transform> _hullRoots = new Dictionary<string, Transform>(StringComparer.Ordinal);
        private readonly LinkedList<Actor> _loadQueue = new LinkedList<Actor>();
        private readonly List<(Actor Actor, Task<ObjMeshParser.Result> Parse)> _parsing = new List<(Actor, Task<ObjMeshParser.Result>)>();
        private readonly HashSet<string> _markedHulls = new HashSet<string>();
        private readonly List<(float At, Actor Actor)> _events = new List<(float, Actor)>();
        private readonly HashSet<Actor> _moving = new HashSet<Actor>();
        private readonly Stopwatch _clock = new Stopwatch();
        private List<Actor> _selectedActors = new List<Actor>();
        private readonly HashSet<Actor> _yardMoving = new HashSet<Actor>();
        private readonly Dictionary<HullPlan, (ErectionSchedule Schedule, BlockMotion.Reach Reach)> _hullSchedules =
            new Dictionary<HullPlan, (ErectionSchedule, BlockMotion.Reach)>();
        private readonly Dictionary<HullPlan, (HullWhere Where, HullPlacement Pose)> _hullWhere = new Dictionary<HullPlan, (HullWhere, HullPlacement)>();
        private readonly List<YardLift> _yardLifts = new List<YardLift>();
        private double _calendarApplied = double.NaN;
        private DateTime _hullWhereAt = DateTime.MinValue;
        private readonly Dictionary<string, (DateTime Start, DateTime End, string Crane)> _liftWindows =
            new Dictionary<string, (DateTime, DateTime, string)>(StringComparer.Ordinal);
        private readonly Dictionary<string, BlockMotion.Reach> _registeredCraneReach =
            new Dictionary<string, BlockMotion.Reach>(StringComparer.Ordinal);
        private readonly List<(Transform Root, HoverTarget Target)> _transporters = new List<(Transform, HoverTarget)>();
        private readonly Dictionary<(string Block, DateTime Day), (DateTime Start, DateTime End)> _driveWindows =
            new Dictionary<(string, DateTime), (DateTime, DateTime)>();
        private readonly Dictionary<string, string> _hullTransporter = new Dictionary<string, string>(StringComparer.Ordinal);
        private DriveRoutes _routes;

        /// <summary>Transporter deck height above the quay (m): a driven block rides this high instead of on its dunnage.</summary>
        private const float TransporterDeck = 2f;
        private Dictionary<string, string> _facilityNames = new Dictionary<string, string>();

        private YardDataSource _source;
        private YardModel _model;
        private ShipPalette _palette;
        private Transform _hullDisplay, _blockDisplay, _vehicleDisplay;
        private ShipyardTwinSettings _settings;
        private MaterialPropertyBlock _properties;
        private Material _textMaterial;
        private int _next;
        private float _cursor = -1f;
        private bool _applying, _reapply;
        private string _pendingHull;
        private float? _pendingSeek;
        private string _pendingBlock;

        public YardModel Model => _model;
        public YardDataSource Source => _source;
        public HullPlan Selected { get; private set; }
        public ErectionPlayback Playback { get; private set; }
        public ErectionSchedule Schedule => Playback?.Schedule;
        public string ActiveDock => Selected?.Placement?.dock;

        /// <summary>True once the plan is placed and every block shown on the plan day has its mesh.</summary>
        public bool Ready { get; private set; }

        /// <summary>The block being lifted (earliest if several), or null.</summary>
        public Transform CurrentLift { get; private set; }
        public BlockRecord CurrentLiftBlock { get; private set; }
        public Bounds CurrentLiftBounds { get; private set; }

        /// <summary>False while the lifted block is still driven to the crane on the ground.</summary>
        public bool CurrentLiftHooked { get; private set; }

        public int ActorCount => _actors.Count;
        public int LoadedMeshes => _actors.Values.Count(a => a.Mesh != null);
        public int ShownCount => _actors.Values.Count(a => a.Shown);

        /// <summary>Meshes still being read or parsed.</summary>
        public bool Loading => _loadQueue.Count > 0 || _parsing.Count > 0 || _applying;

        /// <summary>
        /// The selected hull follows the shared ERP replay (<see cref="YardSimSync"/>): its blocks are placed from the
        /// reported state and the local schedule only mirrors the shared clock.
        /// </summary>
        public bool Synced { get; private set; }

        /// <summary>Shared replay time while <see cref="Synced"/>.</summary>
        public DateTime? SyncedTime { get; private set; }

        /// <summary>The crane lifting for the selected hull in the shared replay (null when none or not synced).</summary>
        public YardSimEquipment SyncedCrane { get; private set; }

        /// <summary>Date shown by the panels: the shared replay time, else the selected hull's schedule, else the yard calendar.</summary>
        public DateTime CurrentDate => Synced && SyncedTime.HasValue ? SyncedTime.Value : Schedule?.CurrentDate ?? Calendar?.Time ?? _model?.AsOf ?? DateTime.Today;

        /// <summary>
        /// Yard calendar playback while no hull is selected: every hull's blocks in shops, yard slots, on the goliath hook and
        /// on the hull, and every hull in its dock, at its berth or away, at one time that loops over the whole plan.
        /// Paused (kept) while a hull is selected.
        /// </summary>
        public YardCalendar Calendar { get; private set; }

        /// <summary>A block on a goliath hook in the yard calendar: its dock, where it is and whether it hangs from the hook.</summary>
        public readonly struct YardLift
        {
            public YardLift(BlockRecord block, Facility dock, Vector3 position, Bounds bounds, bool hooked, string crane)
            {
                Crane = crane;
                Block = block;
                Dock = dock;
                Position = position;
                Bounds = bounds;
                Hooked = hooked;
            }

            public BlockRecord Block { get; }
            public Facility Dock { get; }
            public Vector3 Position { get; }
            public Bounds Bounds { get; }
            public bool Hooked { get; }

            /// <summary>ERP goliath of the lift (crane_assignments), null when the lift has no crane job.</summary>
            public string Crane { get; }
        }

        /// <summary>ERP goliath assigned to a block's erection lift, else null.</summary>
        public string LiftCrane(string block) => block != null && _liftWindows.TryGetValue(block, out var window) ? window.Crane : null;

        /// <summary>ERP goliaths with erection jobs in a dock (crane_assignments work area), most jobs first.</summary>
        public List<string> DockGoliaths(IEnumerable<string> dockIds)
        {
            var docks = new HashSet<string>(dockIds.Where(d => d != null), StringComparer.Ordinal);
            return (_model?.Source?.crane_assignments ?? Array.Empty<CraneAssignmentRow>())
                .Where(j => j.crane_work == "블록 탑재" && j.equipment != null && docks.Contains(j.work_area))
                .GroupBy(j => j.equipment).OrderByDescending(g => g.Count()).Select(g => g.Key).ToList();
        }

        /// <summary>Blocks being lifted now in the yard calendar (empty while a hull is selected).</summary>
        public IReadOnlyList<YardLift> YardLifts => _yardLifts;

        /// <summary>Moves the simulation to a date (the shared replay when synced).</summary>
        public void SeekDate(DateTime when)
        {
            if (Playback != null)
            {
                Playback.Seek((float)(when - Schedule.Start).TotalDays);
            }
        }

        /// <summary>Starts or stops following the shared replay; stopping goes back to the local schedule.</summary>
        public void SetSynced(bool synced)
        {
            if (Synced == synced)
            {
                return;
            }

            Synced = synced;
            SyncedCrane = null;
            SyncedTime = null;
            if (!synced && Playback != null)
            {
                Playback.Remote = null;
                _cursor = -1f;
                ApplySimulation();
                UpdateCurrentLift();
            }
        }

        /// <summary>
        /// Shared replay state for the selected hull: each block at its reported pose (yard x, y, z = centre of gravity,
        /// heading) or hidden; the block on the hook becomes <see cref="CurrentLift"/> for the crane, camera and panel.
        /// </summary>
        public void ApplySynced(DateTime time, IEnumerable<YardSimBlock> blocks, YardSimEquipment crane)
        {
            if (!Synced || Selected == null)
            {
                return;
            }

            SyncedTime = time;
            SyncedCrane = crane;
            _moving.Clear();
            Actor lift = null;
            foreach (var block in blocks)
            {
                if (!_actors.TryGetValue(block.id, out var actor) || actor.Hull != Selected)
                {
                    continue;
                }

                if (!block.Placed)
                {
                    Hide(actor, block.state == "미배치" ? BlockState.Unplaced : BlockState.InProduction);
                    continue;
                }

                var state = block.state == "탑재 완료" ? BlockState.Erected : block.OnHook ? BlockState.Lifting : BlockState.Stocked;
                var pose = new Pose(ShipCoordinates.YardToUnity(block.x, block.y, block.z), ShipCoordinates.HeadingToUnity(block.heading_deg));
                RequestMesh(actor, true);
                Place(actor, pose, state, null);
                actor.Hooked = block.OnHook;
                if (block.OnHook || (block.state == "인양 대기" && crane != null && crane.block == block.id))
                {
                    lift = actor;
                }
            }

            CurrentLift = lift?.Transform;
            CurrentLiftBlock = lift?.Plan.Record;
            CurrentLiftHooked = lift != null && lift.Hooked;
            if (lift != null)
            {
                CurrentLiftBounds = LiftBounds(lift);
            }
        }

        /// <summary>Selection, playback object or plan changed (not raised per frame).</summary>
        public event Action Changed;

        public void Construct(CoreContext context)
        {
            if (_source != null || !context.Services.TryGet(out _source) || !context.Services.TryGet(out _palette))
            {
                return;
            }

            context.Services.TryGet<YardDisplayLayers>(out var layers);
            _hullDisplay = layers?.ParentFor(transform, YardDisplayLayer.Hulls) ?? transform;
            _blockDisplay = layers?.ParentFor(transform, YardDisplayLayer.Blocks) ?? transform;
            _vehicleDisplay = layers?.ParentFor(transform, YardDisplayLayer.Vehicles) ?? transform;
            _settings = context.Settings.Get<ShipyardTwinSettings>();
            _properties = new MaterialPropertyBlock();
            // Desktop: -shipyard-hull; browser: /twin/?hull=…&block=… from the Portal's production control screen.
            _pendingHull = Argument(HullArgument) ?? WebHost.Query("hull");
            _pendingBlock = WebHost.Query("block");
            var seek = Argument(ShipyardTwinBootstrap.SeekArgument);
            if (float.TryParse(seek, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var days))
            {
                _pendingSeek = days;
            }

            _source.Changed += OnSourceChanged;
            PerfProbe.StartPlayback = () =>
            {
                if (Selected == null && _model.Hulls.Count > 0) Select(_model.Hulls[0].Id);
                if (Playback != null) Playback.Playing = true;
            };
            PerfProbe.StopPlayback = () => { if (Playback != null) Playback.Playing = false; };
            PerfProbe.Busy = () => Loading;
            PerfProbe.SwitchHull = i => { if (_model.Hulls.Count > 0) Select(_model.Hulls[i % _model.Hulls.Count].Id); };
            PerfProbe.Extra = () => new Dictionary<string, double>
            {
                { "blocks", _actors.Count }, { "loaded_meshes", LoadedMeshes }, { "shown_blocks", ShownCount }, { "hulls", _model?.Hulls.Count ?? 0 },
            };
            ApplyModel(_source.Model);
        }

        private void OnDestroy()
        {
            if (_source != null)
            {
                _source.Changed -= OnSourceChanged;
            }

            foreach (var actor in _actors.Values)
            {
                ReleaseMesh(actor);
            }
        }

        private void OnSourceChanged()
        {
            if (_source.Model != _model)
            {
                ApplyModel(_source.Model);
            }
        }

        // ------------------------------------------------------------------ plan → actors (diff by block ID)

        private void ApplyModel(YardModel model)
        {
            if (model == null)
            {
                return;
            }

            if (_applying)
            {
                _reapply = true;   // the running pass picks up the newest model when it finishes
                return;
            }

            StartCoroutine(ApplyModelSliced(model));
        }

        private System.Collections.IEnumerator ApplyModelSliced(YardModel model)
        {
            _applying = true;
            var keepSelection = Selected?.Id ?? _pendingHull;
            if (Selected != null)
            {
                Select(null);
            }

            _model = model;
            _facilityNames = model.Facilities.Where(f => !string.IsNullOrEmpty(f.location)).GroupBy(f => f.location)
                .ToDictionary(g => g.Key, g => HoverTarget.FacilityTitle(g.First()));
            var keep = new HashSet<string>(model.Blocks.Keys, StringComparer.Ordinal);
            foreach (var gone in _actors.Keys.Where(id => !keep.Contains(id)).ToList())
            {
                Remove(_actors[gone]);
            }

            var hullIds = new HashSet<string>(model.Hulls.Select(h => h.Id), StringComparer.Ordinal);

            _clock.Restart();
            _hullSchedules.Clear();
            _hullWhere.Clear();
            _hullWhereAt = DateTime.MinValue;
            var (calendarStart, calendarEnd) = model.CalendarRange();
            var speed = Calendar?.SpeedMultiplier ?? 1f;
            var playing = Calendar?.Playing ?? true;
            Calendar = new YardCalendar(calendarStart, calendarEnd, Calendar?.Time ?? model.AsOf, YardDaysPerSecond) { SpeedMultiplier = speed, Playing = playing };
            _calendarApplied = double.NaN;
            PlanDrives(model);
            _routes = new DriveRoutes(LoadRoads(), p => model.Facilities.Where(f => f.kind == "도크")
                .OrderBy(f => (new Vector2(f.x, f.y) - p).sqrMagnitude).Select(f => f.heading_deg).DefaultIfEmpty(0f).First());
            // Goliath erection jobs (ERP crane_assignments): the lift happens in that hour window, so lifts sharing a
            // goliath (docks 4 and 5) follow one another as the ERP planned them.
            _liftWindows.Clear();
            _registeredCraneReach.Clear();
            var craneRails = model.Source?.crane_rails ?? Array.Empty<CraneRailRow>();
            foreach (var rail in craneRails)
            {
                if (!string.IsNullOrEmpty(rail.equipment) && BlockMotion.TryCraneReach(rail, out var reach))
                    _registeredCraneReach[rail.equipment] = reach;
            }

            foreach (var job in model.Source?.crane_assignments ?? Array.Empty<CraneAssignmentRow>())
            {
                if (job.crane_work == "블록 탑재" && !string.IsNullOrEmpty(job.block) && YardModel.ParseDay(job.work_day) is DateTime day && job.hours > 0)
                {
                    var start = day.AddHours(job.start_hour);
                    _liftWindows[job.block] = (start, start.AddHours(job.hours), job.equipment);
                }
            }

            foreach (var hull in model.Hulls)
            {
                if (!_hullRoots.TryGetValue(hull.Id, out var root))
                {
                    root = new GameObject("Hull " + hull.Id).transform;
                    root.SetParent(_hullDisplay, false);
                    _hullRoots.Add(hull.Id, root);
                }
                var hullTarget = root.GetComponent<HoverTarget>() ?? root.gameObject.AddComponent<HoverTarget>();
                IdentifyHull(hullTarget, hull.Info, hull.Name);
                var yardSchedule = new ErectionSchedule(hull.Blocks.Select(b => b.Record), _settings.LiftDays);
                var dock = hull.Dock;
                var dockRail = GantryCraneView.GoliathRail(craneRails, new Vector2(dock.x, dock.y));
                var dockReach = BlockMotion.TryCraneReach(dockRail, out var registeredReach)
                    ? registeredReach : BlockMotion.CraneReach(dock);
                _hullSchedules[hull] = (yardSchedule, dockReach);
                var yardEntries = yardSchedule.Entries.ToDictionary(e => e.Block.block, StringComparer.Ordinal);
                foreach (var plan in hull.Blocks)
                {
                    if (!_actors.TryGetValue(plan.Id, out var actor))
                    {
                        actor = Create(plan, hull);
                        _actors[plan.Id] = actor;
                    }
                    else
                    {
                        if (actor.Plan.Record.model_asset != plan.Record.model_asset)
                        {
                            ReleaseMesh(actor);
                        }

                        actor.Plan = plan;
                        actor.Hull = hull;
                        if (actor.State == BlockState.Erected && actor.Transform.parent != root)
                            actor.Transform.SetParent(root, true);
                        actor.ShopKnown = false;   // stays may have changed: find the shop again
                        actor.Target.Detail = ShipView.BlockDetail(plan.Record, hull.Info);
                    }

                    actor.Landed = BlockMotion.Landed(hull.Placement, plan.Record);
                    actor.YardEntry = yardEntries.TryGetValue(plan.Id, out var yardEntry) ? yardEntry : null;
                    ApplyPlanDay(actor);
                    if (_clock.Elapsed.TotalMilliseconds > _frameBudgetMs)
                    {
                        yield return null;
                        _clock.Restart();
                    }
                }

                if (_markedHulls.Add(hull.Id))
                {
                    AddMarkings(hull);
                }
            }

            foreach (var gone in _hullRoots.Keys.Where(id => !hullIds.Contains(id)).ToList())
            {
                Destroy(_hullRoots[gone].gameObject);
                _hullRoots.Remove(gone);
            }

            _applying = false;
            Changed?.Invoke();
            if (_reapply)
            {
                _reapply = false;
                ApplyModel(_source.Model);
                yield break;
            }

            if (keepSelection != null && model.Hull(keepSelection) != null)
            {
                _pendingHull = null;
                Select(keepSelection);
                if (_pendingSeek.HasValue)
                {
                    Playback.Seek(_pendingSeek.Value);
                    _pendingSeek = null;
                }
                else if (_pendingBlock != null && Schedule?.Entries.FirstOrDefault(e => e.Block.block == _pendingBlock) is ErectionSchedule.Entry entry)
                {
                    Playback.Seek(Mathf.Max(0f, entry.FinishDay - entry.LiftDays - 0.5f));   // just before its lift
                }

                // The ERP model replaces the snapshot a moment later and reselects the hull: seek again then.
                if (_source.Status != YardDataStatus.Loading)
                {
                    _pendingBlock = null;
                }
            }
        }

        private Actor Create(BlockPlan plan, HullPlan hull)
        {
            var go = new GameObject(plan.Id);
            go.transform.SetParent(_blockDisplay, false);
            var actor = new Actor
            {
                Plan = plan, Hull = hull, Go = go, Transform = go.transform, Filter = go.AddComponent<MeshFilter>(),
                Renderer = go.AddComponent<MeshRenderer>(), Collider = go.AddComponent<BoxCollider>(),
            };
            actor.Renderer.enabled = false;
            actor.Collider.enabled = false;
            // Hover picks a simple box (the block's bounding box): no mesh collider cooking per block.
            var bounds = ShipCoordinates.LocalBounds(plan.Record);
            actor.Collider.center = bounds.center;
            actor.Collider.size = bounds.size;
            actor.Target = go.AddComponent<HoverTarget>();
            actor.Target.Kind = "블록";
            actor.Target.Title = plan.Id;
            actor.Target.Detail = BlockDetail(plan.Record, hull.Info);
            actor.Target.Identify(TargetCategory.Block, plan.Id, hull.Id);
            return actor;
        }

        private void Remove(Actor actor)
        {
            _actors.Remove(actor.Plan.Id);
            _moving.Remove(actor);
            ReleaseMesh(actor);
            Destroy(actor.Go);
        }

        // ------------------------------------------------------------------ meshes (lazy, off-thread parse)

        private void RequestMesh(Actor actor, bool urgent)
        {
            if (actor.Mesh != null || actor.Queued)
            {
                return;
            }

            actor.Queued = true;
            if (urgent) _loadQueue.AddFirst(actor);
            else _loadQueue.AddLast(actor);
        }

        private void ReleaseMesh(Actor actor)
        {
            actor.Queued = false;
            if (actor.Mesh != null)
            {
                actor.Filter.sharedMesh = null;
                Destroy(actor.Mesh);
                actor.Mesh = null;
                actor.Renderer.enabled = false;
            }
        }

        private void PumpMeshes()
        {
            if (_loadQueue.Count == 0 && _parsing.Count == 0)
            {
                if (!Ready && !_applying && _model != null)
                {
                    Ready = true;
                    PerfProbe.MarkReady();
                }

                return;
            }

            _clock.Restart();
            var folder = _settings.ModelFolder;
            while (_loadQueue.Count > 0 && _parsing.Count < _parallelParses && _clock.Elapsed.TotalMilliseconds < _frameBudgetMs)
            {
                var actor = _loadQueue.First.Value;
                _loadQueue.RemoveFirst();
                if (!actor.Queued || actor.Go == null)
                {
                    continue;
                }

                var asset = actor.Plan.Record.model_asset;
                var text = string.IsNullOrEmpty(asset) ? null : Resources.Load<TextAsset>(folder + asset);
                if (text == null)
                {
                    actor.Queued = false;
                    Assign(actor, ObjMeshParser.Box(ShipCoordinates.LocalBounds(actor.Plan.Record), actor.Plan.Id), new List<string> { ShipPalette.Fallback });
                    continue;
                }

                var source = text.text;
                Resources.UnloadAsset(text);
                _parsing.Add((actor, WebHost.Offload(() => ObjMeshParser.Parse(source))));
            }

            for (var i = 0; i < _parsing.Count && _clock.Elapsed.TotalMilliseconds < _frameBudgetMs; i++)
            {
                var (actor, parse) = _parsing[i];
                if (!parse.IsCompleted)
                {
                    continue;
                }

                _parsing.RemoveAt(i--);
                if (!actor.Queued || actor.Go == null)
                {
                    continue;   // released or removed while parsing
                }

                actor.Queued = false;
                if (parse.IsFaulted)
                {
                    Assign(actor, ObjMeshParser.Box(ShipCoordinates.LocalBounds(actor.Plan.Record), actor.Plan.Id), new List<string> { ShipPalette.Fallback });
                    continue;
                }

                Assign(actor, ObjMeshParser.ToMesh(parse.Result, actor.Plan.Id), parse.Result.Materials);
            }

        }

        private void Assign(Actor actor, Mesh mesh, List<string> paints)
        {
            actor.Mesh = mesh;
            actor.Filter.sharedMesh = mesh;
            actor.Renderer.sharedMaterials = paints.Select(_palette.Get).ToArray();
            actor.Colors = paints.Select(p => ShipPalette.Lookup(p).Color).ToArray();
            WriteColors(actor);
            actor.Renderer.enabled = actor.Shown;
        }

        private void Update()
        {
            PumpMeshes();
            if (Playback == null)
            {
                UpdateCalendar();
                return;
            }

            if (Synced)
            {
                UpdateTransporters(Array.Empty<Actor>());   // the ERP replay draws its own vehicles
                return;   // synced: YardSimSync places the blocks
            }

            if (Playback.Playing)
            {
                Playback.Tick(Time.deltaTime);
            }

            var cursor = Schedule.Cursor;
            if (Mathf.Approximately(cursor, _cursor) && _moving.Count == 0)
            {
                return;
            }

            if (cursor < _cursor)
            {
                ApplySimulation();
            }
            else
            {
                while (_next < _events.Count && _events[_next].At < cursor)
                {
                    ApplySimulation(_events[_next].Actor);
                    _next++;
                }

                foreach (var actor in _moving.ToList())
                {
                    ApplySimulation(actor);
                }
            }

            _cursor = cursor;
            UpdateTransporters(_moving);
            UpdateCurrentLift();
        }

        // ------------------------------------------------------------------ selection and simulation

        /// <summary>Selects a hull (null clears): stops the previous playback and resets this hull to simulation day 0.</summary>
        public void Select(string hullId)
        {
            SetSynced(false);
            UnityEngine.Debug.Log("[ShipyardTwin] Hull selected: " + (hullId ?? "(none)"));
            if (Playback != null)
            {
                Playback.Playing = false;
            }

            var previous = _selectedActors;
            var previousHull = Selected;
            _selectedActors = new List<Actor>();
            _moving.Clear();
            _events.Clear();
            Selected = hullId != null ? _model?.Hull(hullId) : null;
            foreach (var actor in previous)
            {
                actor.Entry = null;
                ApplyPlanDay(actor);
                if (!actor.Shown)
                {
                    ReleaseMesh(actor);   // back in production: no mesh kept for it
                }
            }

            // Hulls sharing a berth with the old or new selection: hidden while it plays there, back to the plan day after.
            foreach (var actor in _actors.Values.Where(a => a.Entry == null && a.Hull != Selected &&
                                                            (a.Hull.SharesBerthWith(previousHull) || a.Hull.SharesBerthWith(Selected))))
            {
                ApplyPlanDay(actor);
            }

            CurrentLift = null;
            CurrentLiftBlock = null;
            _yardLifts.Clear();
            _yardMoving.Clear();
            if (Selected == null)
            {
                Playback = null;
                _cursor = -1f;
                _calendarApplied = double.NaN;   // back to the yard calendar where it was paused
                Changed?.Invoke();
                return;
            }

            var schedule = new ErectionSchedule(Selected.Blocks.Select(b => b.Record), _settings.LiftDays);
            Playback = new ErectionPlayback(schedule, _settings.DaysPerSecond, false);
            foreach (var entry in schedule.Entries)
            {
                if (!_actors.TryGetValue(entry.Block.block, out var actor))
                {
                    continue;
                }

                actor.Entry = entry;
                _selectedActors.Add(actor);
                RequestMesh(actor, true);
                var start = schedule.Start;
                float At(DateTime day) => (float)(day - start).TotalDays;
                _events.Add((entry.FinishDay - entry.LiftDays, actor));
                _events.Add((entry.FinishDay, actor));
                var stays = actor.Plan.Stays;
                for (var i = 0; i < stays.Count; i++)
                {
                    _events.Add((At(stays[i].From), actor));
                    if (stays[i].Until.HasValue) _events.Add((At(stays[i].Until.Value.AddDays(1)), actor));
                    _events.Add((At(stays[i].From.AddDays(-1)), actor));   // transporter: out of the shop, or to the next slot
                }
            }

            _events.Sort((a, b) => a.At.CompareTo(b.At));
            // Follow the yard calendar: a hull picked while it is erecting starts at the calendar's day.
            var calendarCursor = Calendar != null ? (float)(Calendar.Time - schedule.Start).TotalDays : 0f;
            schedule.Seek(calendarCursor > 0f && calendarCursor < schedule.Duration ? calendarCursor : 0f);
            _cursor = -1f;
            ApplySimulation();
            _cursor = schedule.Cursor;
            Changed?.Invoke();
        }

        /// <summary>
        /// Recomputes every block of the selected hull at the current cursor (selection, seek back, restart). Events are
        /// consumed only once the cursor has passed them: a block's state at exactly its event time is still the old one
        /// (e.g. progress 0 at the lift start), so it must be revisited when the cursor moves on.
        /// </summary>
        private void ApplySimulation()
        {
            _moving.Clear();
            foreach (var actor in _selectedActors)
            {
                ApplySimulation(actor);
            }

            var cursor = Schedule.Cursor;
            _next = 0;
            while (_next < _events.Count && _events[_next].At < cursor)
            {
                _next++;
            }
        }

        private void ApplySimulation(Actor actor)
        {
            if (actor.Entry == null)
            {
                return;
            }

            var schedule = Schedule;
            var progress = schedule.Progress(actor.Entry);
            var moving = false;
            actor.Driven = false;
            if (progress >= 1f)
            {
                Place(actor, actor.Landed, BlockState.Erected, null);
            }
            else if (progress > 0f)
            {
                var liftDay = schedule.Start.AddDays(actor.Entry.FinishDay - actor.Entry.LiftDays).Date;
                var stay = actor.Plan.LiftSource(liftDay);
                Place(actor, Lifted(actor, stay, progress), BlockState.Lifting, stay);
                moving = true;
            }
            else
            {
                moving = PlaceBeforeLift(actor, schedule.CurrentDate);
            }

            if (moving) _moving.Add(actor);
            else _moving.Remove(actor);
        }

        /// <summary>
        /// A block during its lift: from its yard slot (or, with none that day, from the door of its last shop) driven on a
        /// transporter to the crane's pick-up point when the slot is out of reach, then on the hook to the hull.
        /// </summary>
        private Pose Lifted(Actor actor, StockStay stay, float progress)
        {
            var crane = LiftCrane(actor.Plan.Id);
            var reach = crane != null && _registeredCraneReach.TryGetValue(crane, out var registered)
                ? registered : _hullSchedules[actor.Hull].Reach;
            var start = stay != null ? BlockMotion.InYard(actor.Plan.Record, stay) : ShopDoor(actor, reach);
            var (pose, hooked) = BlockMotion.Lift(start, actor.Landed, progress, reach);
            actor.Hooked = hooked;
            if (!hooked && start.HasValue)
            {
                var pickup = new Vector3(reach.Pickup.x, start.Value.position.y, reach.Pickup.y);
                pose = OnTransporter(actor, pose, start.Value.position, pickup, progress / BlockMotion.GroundShare);
            }

            return pose;
        }

        /// <summary>Road centre lines of the yard scenery (Resources/ShipyardTwin/scenery.json), empty without it.</summary>
        private static Road[] LoadRoads()
        {
            var asset = Resources.Load<TextAsset>(YardScenery.Resource);
            if (asset == null)
            {
                return Array.Empty<Road>();
            }

            try
            {
                return SceneryFile.Parse(asset.text).roads;
            }
            catch (FormatException)
            {
                return Array.Empty<Road>();
            }
            finally
            {
                Resources.UnloadAsset(asset);
            }
        }

        /// <summary>Door of the block's last shop (else the shop nearest the crane) facing the crane's pick-up point, on the ground.</summary>
        private Pose? ShopDoor(Actor actor, BlockMotion.Reach reach)
        {
            var location = actor.Plan.ShopStages.Count > 0 ? actor.Plan.ShopStages[actor.Plan.ShopStages.Count - 1].Location : null;
            var shop = _model.Facilities.FirstOrDefault(f => f.location == location && f.HasFootprint) ??
                       _model.Facilities.Where(f => f.kind == "작업장" && f.HasFootprint)
                           .OrderBy(f => (new Vector2(f.x, f.y) - reach.Pickup).sqrMagnitude).FirstOrDefault();
            if (shop == null)
            {
                return null;
            }

            var centre = new Vector2(shop.x, shop.y);
            var toward = reach.Pickup - centre;
            var edge = Mathf.Min(shop.size_x_m, shop.size_y_m) / 2f + 4f;
            var door = toward.magnitude > edge ? centre + toward.normalized * edge : centre;
            return BlockMotion.InYard(actor.Plan.Record, door.x, door.y, reach.HeadingDeg);
        }

        /// <summary>
        /// A driven block rides on a transporter: raised from its dunnage to the deck (jacked up at the start, down at the
        /// end) and marked so <see cref="UpdateTransporters"/> puts a transporter under it, facing the way it drives.
        /// </summary>
        private Pose OnTransporter(Actor actor, Pose pose, Vector3 from, Vector3 to, float t)
        {
            t = Mathf.Clamp01(t);
            var jack = Mathf.Clamp01(Mathf.Min(t, 1f - t) * 8f);
            pose.position += Vector3.up * ((TransporterDeck - BlockMotion.Dunnage) * jack);
            var way = to - from;
            var heading = way.x * way.x + way.z * way.z > 1e-4f ? Mathf.Atan2(way.z, way.x) * Mathf.Rad2Deg : 0f;
            if (_routes != null)
            {
                // Along the roads (or the lanes behind the docks) instead of straight across the yard.
                var path = _routes.Path(new Vector2(from.x, from.z), new Vector2(to.x, to.z));
                var (point, along) = DriveRoutes.At(path, Mathf.SmoothStep(0f, 1f, t));
                pose.position = new Vector3(point.x, pose.position.y, point.y);
                heading = along;
            }

            actor.Drive = new Pose(new Vector3(pose.position.x, YardLayout.QuayLevel, pose.position.z), ShipCoordinates.HeadingToUnity(heading));
            actor.Driven = true;
            return pose;
        }

        /// <summary>One transporter (pooled prop) under every block being driven this frame; the rest are hidden.</summary>
        private void UpdateTransporters(IEnumerable<Actor> moving)
        {
            var used = 0;
            foreach (var actor in moving)
            {
                if (!actor.Driven || !actor.Shown)
                {
                    continue;
                }

                if (used == _transporters.Count)
                {
                    var root = new GameObject("Transporter").transform;
                    root.SetParent(_vehicleDisplay, false);
                    if (!PropLibrary.TryCreate("transporter", _palette, root, out _))
                    {
                        var body = GameObject.CreatePrimitive(PrimitiveType.Cube);
                        Destroy(body.GetComponent<Collider>());
                        body.transform.SetParent(root, false);
                        body.transform.localPosition = new Vector3(0f, 1f, 0f);
                        body.transform.localScale = new Vector3(18f, 2f, 6f);
                    }

                    var transporterTarget = HoverTarget.Attach(root.gameObject, "중장비", "블록 트랜스포터", "블록 운반 중");
                    transporterTarget.IsHeavyEquipment = true;
                    _transporters.Add((root, transporterTarget));
                }

                var (transporter, target) = _transporters[used++];
                transporter.gameObject.SetActive(true);
                transporter.SetPositionAndRotation(actor.Drive.position, actor.Drive.rotation);
                var vehicle = _hullTransporter.TryGetValue(actor.Hull.Id, out var id) ? id : "트랜스포터";
                if (target.Title != vehicle)
                {
                    target.Title = vehicle;
                    target.Identify(TargetCategory.Vehicle, vehicle, actor.Hull.Id);
                }

                target.Detail = actor.Plan.Id + " 운반 중";
            }

            for (var i = used; i < _transporters.Count; i++)
            {
                if (_transporters[i].Root.gameObject.activeSelf) _transporters[i].Root.gameObject.SetActive(false);
            }
        }

        /// <summary>
        /// A block before its lift on a date: in a yard slot (driven over the day before its next slot), brought out of its
        /// shop the day before its first slot, else hidden (in the shops, or unplaced). True while it is driven.
        /// </summary>
        private bool PlaceBeforeLift(Actor actor, DateTime date)
        {
            var stay = actor.Plan.StayAt(date.Date);
            var first = actor.Plan.Stays.Count > 0 ? actor.Plan.Stays[0] : null;
            if (stay == null && first != null && date.Date == first.From.AddDays(-1).Date && ShopExit(actor, first) is Pose shop)
            {
                // The day before its first stay the line's transporter brings it out of the nearest shop to the slot,
                // in its own window of that day (one block at a time per transporter).
                var share = DriveShare(actor, first.From.AddDays(-1), date);
                var target = BlockMotion.InYard(actor.Plan.Record, first);
                if (share >= 1f)
                {
                    Place(actor, target, BlockState.Stocked, first);
                    return false;
                }

                if (share > 0f)
                {
                    Place(actor, OnTransporter(actor, Blend(shop, target, share), shop.position, target.position, share), BlockState.InProduction, null);
                }
                else
                {
                    Hide(actor, BlockState.InProduction);
                }

                return true;   // followed every frame until it is in the slot
            }

            if (stay == null)
            {
                var state = YardModel.StateAt(actor.Plan, date.Date);
                Hide(actor, state == BlockState.InProduction || state == BlockState.Erected ? BlockState.InProduction : BlockState.Unplaced);
                return false;
            }

            var pose = BlockMotion.InYard(actor.Plan.Record, stay);
            var index = actor.Plan.Stays.IndexOf(stay);
            if (index + 1 < actor.Plan.Stays.Count)
            {
                // The day before the next stay the transporter drives it over, in its window of that day.
                var next = actor.Plan.Stays[index + 1];
                if (date.Date == next.From.AddDays(-1).Date)
                {
                    var share = DriveShare(actor, next.From.AddDays(-1), date);
                    var target = BlockMotion.InYard(actor.Plan.Record, next);
                    if (share >= 1f)
                    {
                        Place(actor, target, BlockState.Stocked, next);
                        return false;
                    }

                    if (share > 0f) pose = OnTransporter(actor, Blend(pose, target, share), pose.position, target.position, share);
                    Place(actor, pose, BlockState.Stocked, stay);
                    return true;
                }
            }

            Place(actor, pose, BlockState.Stocked, stay);
            return false;
        }

        private static Pose Blend(Pose a, Pose b, float share)
        {
            var s = Mathf.SmoothStep(0f, 1f, share);
            return new Pose(Vector3.Lerp(a.position, b.position, s), Quaternion.Slerp(a.rotation, b.rotation, s));
        }

        /// <summary>
        /// How far (0..1) a block's drive on a day has got: within the window the drive plan gave it on its line's
        /// transporter, else over that whole day.
        /// </summary>
        private float DriveShare(Actor actor, DateTime day, DateTime date)
        {
            if (_driveWindows.TryGetValue((actor.Plan.Id, day.Date), out var window))
            {
                return Mathf.Clamp((float)((date - window.Start).TotalHours / (window.End - window.Start).TotalHours), -1f, 2f);
            }

            return (float)(date - day.Date).TotalDays;
        }

        /// <summary>
        /// Drive windows: every block driven on a day (out of its shop the day before its first stay, or to its next
        /// slot the day before that one) goes on its hull's transporter (the ERP vehicle of its moves, else the one its
        /// dock's other hulls use), one after another from 07:00 in lift order, at most two hours each over 14 hours.
        /// </summary>
        private void PlanDrives(YardModel model)
        {
            _driveWindows.Clear();
            _hullTransporter.Clear();
            var source = model.Source;
            var registered = source?.equipment_specs?.Where(s => s.equipment_type == "트랜스포터").ToArray();
            var transporters = new HashSet<string>(StringComparer.Ordinal);
            var vehicleOf = new Dictionary<string, Dictionary<string, int>>(StringComparer.Ordinal);
            foreach (var move in source?.moves ?? Array.Empty<BlockMoveRow>())
            {
                if (string.IsNullOrEmpty(move.equipment) || !(registered != null ? registered.Any(s => s.equipment == move.equipment) :
                        move.equipment.EndsWith("-TP", StringComparison.Ordinal)) ||
                    !model.Blocks.TryGetValue(move.block ?? string.Empty, out var block))
                {
                    continue;
                }

                transporters.Add(move.equipment);
                if (!vehicleOf.TryGetValue(block.Hull, out var counts)) vehicleOf[block.Hull] = counts = new Dictionary<string, int>();
                counts[move.equipment] = counts.TryGetValue(move.equipment, out var n) ? n + 1 : 1;
            }

            foreach (var hull in model.Hulls)
            {
                if (vehicleOf.TryGetValue(hull.Id, out var counts))
                {
                    _hullTransporter[hull.Id] = counts.OrderByDescending(c => c.Value).First().Key;
                }
            }

            foreach (var hull in model.Hulls.Where(h => !_hullTransporter.ContainsKey(h.Id)))
            {
                var stations = new HashSet<string>(hull.Blocks.SelectMany(b => b.Stays).Select(stay => stay.Location));
                var stationed = registered?.FirstOrDefault(s => stations.Contains(s.station));
                if (stationed != null)
                {
                    _hullTransporter[hull.Id] = stationed.equipment;
                    continue;
                }

                var mate = model.Hulls.FirstOrDefault(h => h.Dock?.location == hull.Dock?.location && _hullTransporter.ContainsKey(h.Id));
                _hullTransporter[hull.Id] = mate != null ? _hullTransporter[mate.Id] : transporters.OrderBy(t => t, StringComparer.Ordinal).FirstOrDefault() ?? "TP";
            }

            var drives = new List<(string Vehicle, DateTime Day, DateTime Lift, string Block)>();
            foreach (var hull in model.Hulls)
            {
                foreach (var block in hull.Blocks)
                {
                    var lift = block.ErectionDay ?? DateTime.MaxValue;
                    foreach (var stay in block.Stays)
                    {
                        drives.Add((_hullTransporter[hull.Id], stay.From.AddDays(-1).Date, lift, block.Id));
                    }
                }
            }

            foreach (var group in drives.GroupBy(d => (d.Vehicle, d.Day)))
            {
                var list = group.OrderBy(d => d.Lift).ThenBy(d => d.Block, StringComparer.Ordinal).ToList();
                var slot = Math.Min(2.0, 14.0 / list.Count);
                for (var k = 0; k < list.Count; k++)
                {
                    var start = group.Key.Day.AddHours(7 + k * slot);
                    _driveWindows[(list[k].Block, group.Key.Day)] = (start, start.AddHours(slot));
                }
            }
        }

        // ------------------------------------------------------------------ yard calendar (no hull selected)

        /// <summary>Calendar days per real second at ×1 (slower than a single hull's erection playback: the whole yard runs).</summary>
        private const float YardDaysPerSecond = 1f;

        /// <summary>Calendar rate while a block is on a goliath hook (a lift then takes about a second at ×1).</summary>
        private const float LiftSlowdown = 0.1f;

        /// <summary>Current calendar rate factor: <see cref="LiftSlowdown"/> during lifts, else 1.</summary>
        public float CalendarRate { get; private set; } = 1f;

        private void UpdateCalendar()
        {
            if (Calendar == null || _model == null || _applying)
            {
                return;
            }

            // While a goliath lifts, the calendar slows down so the lift can be followed (a lift is about 1/7 day).
            CalendarRate = _yardLifts.Count > 0 ? LiftSlowdown : 1f;
            Calendar.Tick(Time.deltaTime * CalendarRate);
            var time = Calendar.Time;
            var stamp = Math.Floor((time - Calendar.Start).TotalHours / 2.0);   // a full pass every two calendar hours
            if (stamp != _calendarApplied)
            {
                _calendarApplied = stamp;
                _yardMoving.Clear();
                foreach (var actor in _actors.Values)
                {
                    if (actor.Entry == null) ApplyCalendar(actor, time);
                }
            }
            else
            {
                foreach (var actor in _yardMoving.ToList())
                {
                    ApplyCalendar(actor, time);
                }
            }

            UpdateTransporters(_yardMoving);
            _yardLifts.Clear();
            foreach (var actor in _yardMoving)
            {
                if (actor.State == BlockState.Lifting)
                {
                    _yardLifts.Add(new YardLift(actor.Plan.Record, actor.Hull.Dock, actor.Transform.position,
                        LiftBounds(actor), actor.Hooked, LiftCrane(actor.Plan.Id)));
                }
            }
        }

        private (HullWhere Where, HullPlacement Pose) HullWhereAt(HullPlan hull, DateTime time)
        {
            if (_hullWhereAt != time.Date)
            {
                _hullWhereAt = time.Date;
                _hullWhere.Clear();
            }

            if (!_hullWhere.TryGetValue(hull, out var where))
            {
                _hullWhere[hull] = where = _model.HullAt(hull, time);
            }

            return where;
        }

        /// <summary>
        /// A block at a calendar time: on the hull (in the dock, or at the berth after launch; hidden at sea and after
        /// delivery), on the goliath hook during its lift, in its yard slot or on a transporter, else in the shops.
        /// </summary>
        private void ApplyCalendar(Actor actor, DateTime time)
        {
            var moving = false;
            var progress = 0f;
            actor.Driven = false;
            _hullSchedules.TryGetValue(actor.Hull, out var hull);
            var liftDay = DateTime.MinValue;
            if (_liftWindows.TryGetValue(actor.Plan.Id, out var window))
            {
                progress = time <= window.Start ? 0f : time >= window.End ? 1f : (float)((time - window.Start).TotalHours / (window.End - window.Start).TotalHours);
                liftDay = window.Start.Date;
            }
            else if (actor.YardEntry != null && hull.Schedule != null)
            {
                progress = ErectionSchedule.ProgressAt(actor.YardEntry, (float)(time - hull.Schedule.Start).TotalDays);
                liftDay = hull.Schedule.Start.AddDays(actor.YardEntry.FinishDay - actor.YardEntry.LiftDays).Date;
            }
            else if (actor.Plan.ErectionDay.HasValue && actor.Plan.ErectionDay.Value <= time.Date)
            {
                progress = 1f;
            }

            if (progress >= 1f)
            {
                var (where, pose) = HullWhereAt(actor.Hull, time);
                if (where == HullWhere.Berth && pose != null)
                {
                    Place(actor, BlockMotion.Landed(pose, actor.Plan.Record), BlockState.Erected, null);
                }
                else if ((where == HullWhere.Dock || where == HullWhere.Building) && !actor.Hull.SharesBerthWith(Selected))
                {
                    Place(actor, actor.Landed, BlockState.Erected, null);
                }
                else
                {
                    Hide(actor, BlockState.Erected);
                }
            }
            else if (progress > 0f)
            {
                var stay = actor.Plan.LiftSource(liftDay);
                Place(actor, Lifted(actor, stay, progress), BlockState.Lifting, stay);
                moving = true;
            }
            else
            {
                moving = PlaceBeforeLift(actor, time);
            }

            if (moving) _yardMoving.Add(actor);
            else _yardMoving.Remove(actor);
        }

        /// <summary>
        /// Where a block leaves production for its first stay: the edge of the nearest shop (작업장) facing the slot,
        /// on the ground, turned as in the slot. Null when the yard has no shops.
        /// </summary>
        private Pose? ShopExit(Actor actor, StockStay stay)
        {
            if (!actor.ShopKnown)
            {
                actor.ShopKnown = true;
                var slot = new Vector2(stay.Row.x, stay.Row.y);
                var shop = _model.Facilities.Where(f => f.kind == "작업장" && f.HasFootprint)
                    .OrderBy(f => (new Vector2(f.x, f.y) - slot).sqrMagnitude).FirstOrDefault();
                if (shop != null)
                {
                    // Door: the shop centre moved toward the slot until the footprint edge (half its smaller side).
                    var centre = new Vector2(shop.x, shop.y);
                    var toward = slot - centre;
                    var reach = Mathf.Min(shop.size_x_m, shop.size_y_m) / 2f;
                    var door = toward.magnitude > reach ? centre + toward.normalized * reach : centre;
                    actor.Shop = BlockMotion.InYard(actor.Plan.Record, door.x, door.y, stay.Row.heading_deg);
                }
            }

            return actor.Shop;
        }

        /// <summary>State of a block outside the selected hull's playback: the yard calendar's time (kept while a hull is selected).</summary>
        private void ApplyPlanDay(Actor actor)
        {
            if (actor.Entry != null)
            {
                ApplySimulation(actor);
                return;
            }

            ApplyCalendar(actor, Calendar?.Time ?? _model.AsOf);
        }

        private void Place(Actor actor, Pose pose, BlockState state, StockStay stay)
        {
            actor.Transform.SetPositionAndRotation(pose.position, pose.rotation);
            SetState(actor, state, stay);
            SetShown(actor, true);
            Tint(actor, state == BlockState.Lifting);
        }

        private void Hide(Actor actor, BlockState state)
        {
            SetState(actor, state, null);
            SetShown(actor, false);
        }

        private void SetState(Actor actor, BlockState state, StockStay stay)
        {
            if (actor.State == state && actor.Stay == stay)
            {
                return;
            }

            // An erected block is part of the ship. While staged, transported or lifted it is a separate block.
            if ((actor.State == BlockState.Erected) != (state == BlockState.Erected))
            {
                actor.Transform.SetParent(state == BlockState.Erected ? _hullRoots[actor.Hull.Id] : _blockDisplay, true);
            }

            actor.State = state;
            actor.Stay = stay;
            var where = state == BlockState.Stocked && stay != null
                ? "적치 " + (_facilityNames.TryGetValue(stay.Location, out var name) ? name : stay.Location)
                : state == BlockState.Lifting ? "인양 중" : state == BlockState.Erected ? "탑재 완료" : null;
            actor.Target.Detail = BlockDetail(actor.Plan.Record, actor.Hull.Info) + (where != null ? " · " + where : string.Empty);
        }

        private void SetShown(Actor actor, bool shown)
        {
            if (shown)
            {
                RequestMesh(actor, actor.Entry != null);
            }

            if (actor.Shown == shown)
            {
                return;
            }

            actor.Shown = shown;
            actor.Renderer.enabled = shown && actor.Mesh != null;
            actor.Collider.enabled = shown;
            foreach (var marker in actor.Markers)
            {
                marker.SetActive(shown);
            }
        }

        private void Tint(Actor actor, bool lifting)
        {
            if (actor.Tinted != lifting)
            {
                actor.Tinted = lifting;
                WriteColors(actor);
            }
        }

        private void WriteColors(Actor actor)
        {
            if (actor.Mesh == null)
            {
                return;   // written when the mesh arrives
            }

            if (!actor.Tinted)
            {
                // Untinted blocks use the palette materials as they are: no property block, so the SRP Batcher keeps
                // batching them (a property block on every block forced one draw per submesh per shadow cascade).
                actor.Renderer.SetPropertyBlock(null);
                for (var i = 0; i < actor.Colors.Length; i++)
                {
                    actor.Renderer.SetPropertyBlock(null, i);
                }

                return;
            }

            for (var i = 0; i < actor.Colors.Length; i++)
            {
                actor.Renderer.GetPropertyBlock(_properties, i);
                var color = actor.Tinted ? Color.Lerp(actor.Colors[i], _liftTint, _liftTintAmount) : actor.Colors[i];
                _properties.SetColor(ColorId, color);
                _properties.SetColor(BaseColorId, color);
                actor.Renderer.SetPropertyBlock(_properties, i);
            }
        }

        private void UpdateCurrentLift()
        {
            Actor lift = null;
            foreach (var actor in _moving)
            {
                if (actor.State == BlockState.Lifting && (lift == null || actor.Entry.FinishDay < lift.Entry.FinishDay))
                {
                    lift = actor;
                }
            }

            CurrentLift = lift?.Transform;
            CurrentLiftBlock = lift?.Plan.Record;
            CurrentLiftHooked = lift != null && lift.Hooked;
            if (lift != null)
            {
                CurrentLiftBounds = LiftBounds(lift);
            }
        }

        private static Bounds LiftBounds(Actor actor)
        {
            if (actor.Mesh == null)
            {
                return new Bounds(actor.Transform.position, ShipCoordinates.LocalBounds(actor.Plan.Record).size);
            }

            if (actor.Go.activeInHierarchy)
            {
                return actor.Renderer.bounds;
            }

            // A hidden block still moves under the crane, so compute its bounds at the current pose.
            var local = actor.Mesh.bounds;
            var matrix = actor.Transform.localToWorldMatrix;
            var x = matrix.MultiplyVector(Vector3.right * local.extents.x);
            var y = matrix.MultiplyVector(Vector3.up * local.extents.y);
            var z = matrix.MultiplyVector(Vector3.forward * local.extents.z);
            var extents = new Vector3(
                Mathf.Abs(x.x) + Mathf.Abs(y.x) + Mathf.Abs(z.x),
                Mathf.Abs(x.y) + Mathf.Abs(y.y) + Mathf.Abs(z.y),
                Mathf.Abs(x.z) + Mathf.Abs(y.z) + Mathf.Abs(z.z));
            return new Bounds(matrix.MultiplyPoint3x4(local.center), 2f * extents);
        }

        // ------------------------------------------------------------------ queries for the panel

        public IEnumerable<BlockPlan> BlocksIn(HullPlan hull, BlockState state) =>
            hull == null ? Enumerable.Empty<BlockPlan>() : hull.Blocks.Where(b => _actors.TryGetValue(b.Id, out var a) && a.State == state);

        public int CountIn(HullPlan hull, BlockState state) => BlocksIn(hull, state).Count();

        /// <summary>Hover text of a block: hull number, zone and weight (the title is the block ID).</summary>
        public static string BlockDetail(BlockRecord block, HullInfo hull) =>
            "호선 " + (hull?.hull ?? block.hull ?? "-") + (string.IsNullOrEmpty(block.zone) ? string.Empty : " · " + block.zone) +
            " · " + block.weight_t.ToString("0") + " t";

        /// <summary>Ship hover target (keel blocks and other hull fittings): vessel name, else the hull number.</summary>
        public static HoverTarget AttachShipTarget(GameObject root, HullInfo hull)
        {
            var name = string.IsNullOrWhiteSpace(hull.vessel_name) ? hull.hull : hull.vessel_name;
            return IdentifyHull(HoverTarget.Attach(root, "선박", name), hull);
        }

        private static HoverTarget IdentifyHull(HoverTarget target, HullInfo hull, string planName = null)
        {
            target.Kind = "선박";
            target.Title = string.IsNullOrWhiteSpace(hull.vessel_name)
                ? (string.IsNullOrWhiteSpace(planName) ? hull.hull : planName)
                : hull.vessel_name;
            target.Detail = "호선 " + hull.hull + (string.IsNullOrEmpty(hull.vessel_type) ? string.Empty : " · " + hull.vessel_type);
            return target.Identify(TargetCategory.Hull, hull.hull, hull.hull);
        }

        // ------------------------------------------------------------------ hull markings

        /// <summary>
        /// Painted hull markings as world-space text on the blocks that carry them (they follow the block, shown with
        /// it): ship's name on both bow sides, name and port of registry on the transom, draft marks every 2 m.
        /// </summary>
        private void AddMarkings(HullPlan plan)
        {
            var hull = plan.Info;
            var actors = plan.Blocks.Select(b => _actors[b.Id]).ToList();
            Actor Find(Func<BlockRecord, bool> predicate) => actors.FirstOrDefault(a => predicate(a.Plan.Record));
            var name = (hull.vessel_name ?? hull.hull ?? string.Empty).Split('(')[0].Trim().ToUpperInvariant();
            var halfBeam = hull.beam_m / 2f;
            foreach (var side in new[] { 1f, -1f })
            {
                var bow = Find(b => b.y_max > 0 == side > 0 && b.z_min >= hull.depth_m / 2f - 0.1f && b.x_min > hull.length_m * 0.85f &&
                                    b.x_min < hull.length_m * 0.9f);
                if (bow != null)
                {
                    var r = bow.Plan.Record;
                    Marker(bow, name, new Vector3(r.x_min + 7f, side * (Mathf.Abs(side > 0 ? r.y_max : r.y_min) + 0.06f), hull.depth_m - 3.2f), side, 1.8f);
                }

                foreach (var x in new[] { hull.length_m * 0.86f, hull.length_m * 0.5f, hull.length_m * 0.22f })
                {
                    for (var z = 4; z <= Mathf.FloorToInt(hull.draft_m) + 1; z += 2)
                    {
                        var shell = Find(b => b.y_max > 0 == side > 0 && Mathf.Abs(side > 0 ? b.y_max : b.y_min) > halfBeam - 0.2f &&
                                              b.x_min <= x && x <= b.x_max && b.z_min <= z && z <= b.z_max);
                        if (shell != null)
                        {
                            Marker(shell, z + "M", new Vector3(x, side * (halfBeam + 0.06f), z), side, 0.7f);
                        }
                    }
                }
            }

            var transom = Find(b => b.y_max > 0 && b.x_min <= 0.01f && b.z_min >= hull.depth_m / 2f - 0.1f);
            if (transom != null)
            {
                Marker(transom, name + "\nGEOJE", new Vector3(-0.08f, 0f, hull.depth_m - 4.5f), 0f, 1.5f);
            }
        }

        /// <summary>Depth-tested text material (Resources Shaders/Text3D); the font's own material draws through the hull.</summary>
        private Material TextMaterial(Font font)
        {
            if (_textMaterial == null)
            {
                var shader = Shader.Find("ShipyardTwin/Text3D");
                _textMaterial = shader != null ? new Material(shader) { mainTexture = font.material.mainTexture } : font.material;
            }

            return _textMaterial;
        }

        /// <summary>White painted text at a ship-coordinate point on a block; side +1 port, -1 starboard, 0 transom.</summary>
        private void Marker(Actor block, string text, Vector3 ship, float side, float height)
        {
            var go = new GameObject("Marking " + text.Replace('\n', ' '));
            go.transform.SetParent(block.Transform, false);
            // Ship point relative to the block's centre of gravity, in the block's (ship) axes.
            go.transform.localPosition = ShipCoordinates.ShipToUnity(ship.x, ship.y, ship.z) - ShipCoordinates.CentreOfGravity(block.Plan.Record);
            // TextMesh reads from -z: starboard as is, port turned around, transom turned to face aft (-x).
            go.transform.localRotation = side < 0 ? Quaternion.identity : side > 0 ? Quaternion.Euler(0, 180f, 0) : Quaternion.Euler(0, 90f, 0);
            var mesh = go.AddComponent<TextMesh>();
            mesh.text = text;
            mesh.font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
            mesh.fontSize = 96;
            mesh.characterSize = height / 9.6f;
            mesh.anchor = TextAnchor.MiddleCenter;
            mesh.alignment = TextAlignment.Center;
            mesh.color = new Color(0.96f, 0.96f, 0.94f);
            go.GetComponent<MeshRenderer>().sharedMaterial = TextMaterial(mesh.font);
            go.SetActive(block.Shown);
            block.Markers.Add(go);
        }

        private static string Argument(string name)
        {
            var args = Environment.GetCommandLineArgs();
            var index = Array.IndexOf(args, name);
            return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
        }

        private sealed class Actor
        {
            public BlockPlan Plan;
            public HullPlan Hull;
            public GameObject Go;
            public Transform Transform;
            public MeshFilter Filter;
            public MeshRenderer Renderer;
            public BoxCollider Collider;
            public HoverTarget Target;
            public Mesh Mesh;
            public bool Queued;
            public Color[] Colors = new Color[0];
            public bool Tinted;
            public bool Shown;
            public bool Hooked;
            public BlockState State = (BlockState)(-1);
            public StockStay Stay;
            public Pose Landed;
            public bool ShopKnown;
            public Pose? Shop;
            public ErectionSchedule.Entry Entry;
            public ErectionSchedule.Entry YardEntry;
            public bool Driven;
            public Pose Drive;
            public readonly List<GameObject> Markers = new List<GameObject>();
        }
    }
}
