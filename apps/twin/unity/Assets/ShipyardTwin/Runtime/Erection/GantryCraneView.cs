using System.Collections.Generic;
using System.Linq;
using GrowthLab.Core;
using UnityEngine;

namespace ShipyardTwin
{
    /// <summary>
    /// Shipyard goliath gantry crane spanning the building dock, built from primitives: a rigid A-frame leg on the
    /// north quay and a slender flexible leg on the south quay (the usual rigid/flexible pair that tolerates long
    /// rails), twin box girders with walkways, an upper trolley with two hooks running on the girders and a lower
    /// trolley with one hook under them, wheel bogies on quay rails, operator cab and aviation lights.
    /// The crane travels to the block being lifted; the upper trolley's two hooks carry it on four slings.
    /// Every other dock gets its own goliath (built by this one), parked over its PE yard at the dock head; the
    /// rails run from the PE yard to the gate. Illustrative only: it follows the playback, it does not plan lifts.
    /// Following the shared ERP replay it takes the replayed rail position, trolley and hook height of its dock's crane.
    /// A block on the hook drives the crane in the same frame (gantry, trolley and hooks over its path pivot); once
    /// released the hook rises plumb before the crane travels on (see <see cref="LateUpdate"/>).
    /// </summary>
    public sealed class GantryCraneView : MonoBehaviour, ICoreConsumer
    {
        [Tooltip("Girder underside height above the quay (m).")]
        [SerializeField] private float _height = 92f;

        [Tooltip("Rail clearance beyond each dock wall (m).")]
        [SerializeField] private float _railOffset = 8f;

        [Tooltip("Hook hoist speed while no block hangs from it (m/s at speed ×1).")]
        [SerializeField] private float _hoistSpeed = 10f;

        [Tooltip("Gantry travel speed along the rails while no block hangs from it (m/s at speed ×1).")]
        [SerializeField] private float _travelSpeed = 22f;

        [Tooltip("Upper trolley speed across the span while no block hangs from it (m/s at speed ×1).")]
        [SerializeField] private float _trolleySpeed = 10f;

        /// <summary>Gantry x along the rails, trolley z across the span, hook sling-point height (crane-local, m).</summary>
        private float _gantryX, _trolleyZ, _hookTop;

        /// <summary>Block on (or coming to) the hook last frame, kept so the yard calendar stays on the same lift.</summary>
        private string _liftId;
        private object _clock;
        private int _positionRevision;

        private ShipView _ship;
        private ShipPalette _palette;
        private Transform _gantry, _upperTrolley, _lowerTrolley;
        private readonly List<Transform> _hooks = new List<Transform>();
        private readonly List<Transform> _falls = new List<Transform>();
        private readonly List<Transform> _slings = new List<Transform>();
        private float _span, _girderY;
        private bool _placed;
        private Facility _dock;
        private bool _parked;

        /// <summary>ERP goliath this gantry stands for (its registered rail, else its dock's erection jobs).</summary>
        private string _craneId;

        /// <summary>0 = the dock's first goliath (draws the rails); 1 = a second goliath on the same rails.</summary>
        private int _craneIndex;
        private string _dockId;
        private Vector2 _dockCentre;
        private float _railStart, _railEnd, _parkX;
        private bool _settled;

        /// <summary>Rail extension inland of the dock (over the PE yard at the dock head), m.</summary>
        private const float HeadRail = 100f;

        /// <summary>Registered goliath rails already built this session (a rail spanning two docks is built once).</summary>
        private static readonly HashSet<string> Built = new HashSet<string>();

        private string _equipment;

        private void OnDestroy()
        {
            if (_equipment != null) Built.Remove(_equipment);
        }

        /// <summary>
        /// The ERP rail (crane_rails) of a goliath or gantry crane whose span covers a yard point (a dock centre), else null.
        /// </summary>
        public static CraneRailRow GoliathRail(IEnumerable<CraneRailRow> rails, Vector2 yard)
        {
            foreach (var row in rails ?? Enumerable.Empty<CraneRailRow>())
            {
                if (row.equipment_type != "골리앗 크레인" && row.equipment_type != "갠트리 크레인") continue;
                var a = new Vector2(row.rail_x0, row.rail_y0);
                var b = new Vector2(row.rail_x1, row.rail_y1);
                var length = (b - a).magnitude;
                if (length < 1f) continue;
                var along = (b - a) / length;
                var d = yard - a;
                var u = Vector2.Dot(d, along);
                var v = Mathf.Abs(along.x * d.y - along.y * d.x);
                if (u >= 0f && u <= length && v < row.gauge_m / 2f) return row;
            }

            return null;
        }

        /// <summary>This crane's span covers a yard point (in its rail axes: along the rails, between them).</summary>
        private bool Covers(Vector2 yard)
        {
            var local = transform.InverseTransformPoint(ShipCoordinates.YardToUnity(yard.x, yard.y, 0f));
            return Mathf.Abs(local.z) < _span / 2f && local.x >= _railStart - 5f && local.x <= _railEnd + 15f;
        }

        public void Construct(CoreContext context)
        {
            if (_gantry != null || !context.Services.TryGet<TwinFile>(out var twin) || !context.Services.TryGet(out _palette))
            {
                return;
            }

            _ship = FindFirstObjectByType<ShipView>();
            context.Services.TryGet<YardDataSource>(out var source);
            var rails = source?.Model?.Source?.crane_rails ?? new CraneRailRow[0];
            var dockFacility = _dock ?? YardLayout.PlacementDock(twin);
            var centre = _dock != null ? ShipCoordinates.YardToUnity(_dock.x, _dock.y, 0f) : YardLayout.DockCentre(twin);
            // A goliath registered with its own rails in the ERP (crane_rails) may span several docks: built once, on them.
            var registered = GoliathRail(rails, new Vector2(centre.x, centre.z));
            if (registered != null && !Built.Add(registered.equipment))
            {
                if (_parked) Destroy(gameObject);   // this dock is under another goliath's span
                return;
            }

            var heading = dockFacility?.heading_deg ?? 0f;   // rails run along the dock
            var pitHalfWidth = _dock != null ? _dock.size_y_m / 2f - YardLayout.WallThickness : YardLayout.PitHalfWidth(twin);
            var pit = _dock != null ? _dock.size_x_m / 2f - YardLayout.WallThickness : YardLayout.PitHalfLength(twin);
            _span = 2f * (pitHalfWidth + YardLayout.WallThickness + _railOffset);
            // Rails run from over the PE yard at the dock head to just short of the gate (not over the sea).
            float railStart = -pit - HeadRail, railEnd = pit - 10f;
            if (registered != null)
            {
                var a = new Vector2(registered.rail_x0, registered.rail_y0);
                var b = new Vector2(registered.rail_x1, registered.rail_y1);
                var mid = (a + b) / 2f;
                centre = ShipCoordinates.YardToUnity(mid.x, mid.y, 0f);
                heading = Mathf.Atan2(b.y - a.y, b.x - a.x) * Mathf.Rad2Deg;
                _span = registered.gauge_m;
                railStart = -(b - a).magnitude / 2f;
                railEnd = -railStart;
                pit = railEnd - 10f;
                _equipment = registered.equipment;
            }

            // ERP goliaths of this dock (erection jobs in it or in an alias of its footprint): a dock served by two gets a
            // second gantry on the same rails, waiting at the gate end; each follows the lifts of its own crane ID.
            var goliaths = new List<string>();
            if (registered != null)
            {
                _craneId = registered.equipment;
            }
            else if (dockFacility != null)
            {
                var ids = new HashSet<string>((source?.Model?.Source?.location_geo ?? new Facility[0])
                    .Where(f => Mathf.Abs(f.x - dockFacility.x) < 1f && Mathf.Abs(f.y - dockFacility.y) < 1f).Select(f => f.location)) { dockFacility.location };
                goliaths = (source?.Model?.Source?.crane_assignments ?? new CraneAssignmentRow[0])
                    .Where(j => j.crane_work == "블록 탑재" && j.equipment != null && ids.Contains(j.work_area))
                    .GroupBy(j => j.equipment).OrderByDescending(g => g.Count()).Select(g => g.Key).ToList();
                _craneId = _craneIndex < goliaths.Count ? goliaths[_craneIndex] : null;
            }

            _girderY = _height;
            transform.SetPositionAndRotation(new Vector3(centre.x, YardLayout.QuayLevel, centre.z), ShipCoordinates.HeadingToUnity(heading));

            var halfSpan = _span / 2f;
            _railStart = railStart;
            _railEnd = railEnd;
            _parkX = registered != null ? railStart + HeadRail / 2f : _craneIndex > 0 ? railEnd - 40f : -pit - HeadRail / 2f;
            _dockId = _dock != null ? _dock.location : twin.placement.dock;
            _dockCentre = new Vector2(centre.x, centre.z);
            foreach (var side in _craneIndex > 0 ? new float[0] : new[] { -1f, 1f })
            {
                Box("Rail", transform, new Vector3((railStart + railEnd) / 2f, 0.25f, side * halfSpan), new Vector3(railEnd - railStart, 0.5f, 1.2f), "rail");
            }

            _gantry = new GameObject("Gantry").transform;
            context.Services.TryGet<YardDisplayLayers>(out var layers);
            _gantry.SetParent(layers?.ParentFor(transform, YardDisplayLayer.Cranes) ?? transform, false);
            // Detailed Blender models (pipeline/build_hq_assets.py) when present, primitives otherwise.
            if (!PropLibrary.TryCreate("goliath_frame", _palette, _gantry, out _))
            {
                BuildLegs(halfSpan);
                BuildGirders(halfSpan);
            }

            BuildTrolleys(halfSpan);
            // Named (and clickable): a block or the ship behind the crane is still not picked through it.
            var craneName = registered != null
                ? (string.IsNullOrEmpty(registered.name) ? registered.equipment : registered.name)
                : _craneId ?? "골리앗 크레인 · " + (dockFacility != null ? HoverTarget.FacilityTitle(dockFacility) : _dockId);
            HoverTarget.Attach(_gantry.gameObject, "장비", craneName,
                    registered != null ? registered.equipment_type + " · 정격 " + registered.capacity_t.ToString("0") + " t" : "도크 골리앗 크레인 (ERP 레일 미등록)")
                .Identify(TargetCategory.Crane, registered?.equipment ?? _craneId, null, _dockId).CraneHazard =
                    WalkHazardShape.Gantry(halfSpan);
            _gantry.localPosition = new Vector3(_parkX, 0, 0);   // waiting over the PE yard until its dock's hull is selected
            _gantryX = _parkX;
            _hookTop = _girderY - 22f;
            if (_craneIndex == 0 && goliaths.Count > 1 && dockFacility != null)
            {
                var second = new GameObject("GoliathCrane 2 " + dockFacility.name).AddComponent<GantryCraneView>();
                second._dock = dockFacility;
                second._parked = true;
                second._craneIndex = 1;
                second._height = _height;
                second._railOffset = _railOffset;
                second.Construct(context);
            }

            if (_parked)
            {
                return;
            }

            // One goliath per other dock (or per registered rail spanning several), parked over its PE yard.
            foreach (var dock in YardLayout.Docks(twin).Where(d => d.location != twin.placement.dock))
            {
                var other = new GameObject("GoliathCrane " + dock.name).AddComponent<GantryCraneView>();
                other._dock = dock;
                other._parked = true;
                other._height = _height;
                other._railOffset = _railOffset;
                other.Construct(context);
            }
        }

        private void BuildLegs(float halfSpan)
        {
            var top = _girderY - 3.5f;
            // Rigid leg (north, +z): A-frame of two box columns, wide at the bogies, with ties.
            var rigid = halfSpan;
            foreach (var s in new[] { -1f, 1f })
            {
                Beam("RigidLeg", new Vector3(s * 13f, 3.5f, rigid), new Vector3(s * 4.5f, top, rigid), 3.6f, "crane_white");
            }

            Box("RigidTie", _gantry, new Vector3(0, _girderY * 0.42f, rigid), new Vector3(18f, 2.2f, 2.6f), "crane_white");
            Box("RigidTie", _gantry, new Vector3(0, _girderY * 0.75f, rigid), new Vector3(11f, 2.0f, 2.6f), "crane_white");
            Box("RigidHead", _gantry, new Vector3(0, top, rigid), new Vector3(14f, 4f, 5f), "crane_white");
            // Flexible leg (south, -z): two slender columns.
            var flexible = -halfSpan;
            foreach (var s in new[] { -1f, 1f })
            {
                Beam("FlexLeg", new Vector3(s * 5f, 3.5f, flexible), new Vector3(s * 4.5f, top, flexible), 2.4f, "crane_white");
            }

            Box("FlexHead", _gantry, new Vector3(0, top, flexible), new Vector3(12f, 3.2f, 4f), "crane_white");
            Box("FlexTie", _gantry, new Vector3(0, _girderY * 0.55f, flexible), new Vector3(10f, 1.4f, 1.8f), "crane_white");
            // Sill beams and wheel bogies on the rails.
            foreach (var (z, half) in new[] { (rigid, 17f), (flexible, 9f) })
            {
                Box("Sill", _gantry, new Vector3(0, 3f, z), new Vector3(half * 2f, 2.4f, 3f), "crane_red");
                foreach (var s in new[] { -1f, 1f })
                {
                    Box("Bogie", _gantry, new Vector3(s * (half - 2.5f), 1.1f, z), new Vector3(5f, 1.8f, 2.6f), "crane_dark");
                    foreach (var w in new[] { -1.4f, 1.4f })
                    {
                        Cylinder("Wheel", _gantry, new Vector3(s * (half - 2.5f) + w, 0.55f, z), Vector3.forward, 0.55f, 3f, "crane_dark");
                    }
                }
            }
        }

        private void BuildGirders(float halfSpan)
        {
            var length = _span + 14f;
            foreach (var s in new[] { -1f, 1f })
            {
                var x = s * 3.8f;
                Box("Girder", _gantry, new Vector3(x, _girderY + 3.5f, 0), new Vector3(3.2f, 7f, length), "crane_white");
                Box("GirderStripe", _gantry, new Vector3(x + s * 1.62f, _girderY + 5.4f, 0), new Vector3(0.06f, 1.5f, length), "crane_red");
                Box("TrolleyRail", _gantry, new Vector3(x, _girderY + 7.15f, 0), new Vector3(0.6f, 0.3f, length - 4f), "rail");
                Box("Walkway", _gantry, new Vector3(x + s * 2.6f, _girderY + 0.4f, 0), new Vector3(2f, 0.15f, length - 2f), "crane_dark");
                Box("Handrail", _gantry, new Vector3(x + s * 3.55f, _girderY + 1.5f, 0), new Vector3(0.08f, 0.08f, length - 2f), "crane_yellow");
                foreach (var z in new[] { -length / 2f + 1f, length / 2f - 1f })
                {
                    Box("AviationLight", _gantry, new Vector3(x, _girderY + 7.6f, z), new Vector3(0.7f, 0.7f, 0.7f), "crane_red");
                }
            }

            Box("EndBeam", _gantry, new Vector3(0, _girderY + 3.5f, halfSpan), new Vector3(11f, 6f, 3f), "crane_white");
            Box("EndBeam", _gantry, new Vector3(0, _girderY + 3.5f, -halfSpan), new Vector3(11f, 6f, 3f), "crane_white");
            Box("Cab", _gantry, new Vector3(-5.5f, _girderY - 2.2f, halfSpan - 14f), new Vector3(3.2f, 3f, 4f), "glass");
            Box("CabFrame", _gantry, new Vector3(-5.5f, _girderY - 0.55f, halfSpan - 14f), new Vector3(3.4f, 0.4f, 4.2f), "crane_white");
        }

        private void BuildTrolleys(float halfSpan)
        {
            // Upper trolley on the girders: frame, machinery house, two hoists over the gap between girders.
            _upperTrolley = new GameObject("UpperTrolley").transform;
            _upperTrolley.SetParent(_gantry, false);
            _upperTrolley.localPosition = new Vector3(0, _girderY + 7.3f, 0);
            var detailedTrolley = PropLibrary.TryCreate("goliath_upper_trolley", _palette, _upperTrolley, out _);
            if (!detailedTrolley)
            {
                Box("Frame", _upperTrolley, new Vector3(0, 1.4f, 0), new Vector3(12f, 2.8f, 11f), "crane_red");
                Box("House", _upperTrolley, new Vector3(0, 4.8f, 1.2f), new Vector3(10f, 4f, 7.5f), "crane_white");
                Box("HouseRoof", _upperTrolley, new Vector3(0, 6.95f, 1.2f), new Vector3(10.2f, 0.3f, 7.7f), "crane_red");
            }

            foreach (var hx in new[] { -2.6f, 2.6f })
            {
                if (!detailedTrolley)
                {
                    Cylinder("Drum", _upperTrolley, new Vector3(hx, 3.4f, -3.5f), Vector3.right, 0.9f, 2.4f, "crane_dark");
                }

                _hooks.Add(Hook("Hook", hx));
                for (var i = 0; i < 4; i++)
                {
                    _falls.Add(Box("Fall", _gantry, Vector3.zero, Vector3.one, "wire"));
                }
            }

            for (var i = 0; i < 4; i++)
            {
                var sling = Box("Sling", _gantry, Vector3.zero, Vector3.one, "wire");
                sling.gameObject.SetActive(false);
                _slings.Add(sling);
            }

            // Lower trolley under the girders, parked towards the rigid leg with its hook raised.
            _lowerTrolley = new GameObject("LowerTrolley").transform;
            _lowerTrolley.SetParent(_gantry, false);
            _lowerTrolley.localPosition = new Vector3(0, _girderY - 0.2f, halfSpan - 26f);
            if (!PropLibrary.TryCreate("goliath_lower_trolley", _palette, _lowerTrolley, out _))
            {
                Box("Body", _lowerTrolley, new Vector3(0, -2f, 0), new Vector3(9.5f, 3.6f, 6f), "crane_red");
                Box("Wire", _lowerTrolley, new Vector3(0, -7f, 0), new Vector3(0.25f, 6.5f, 0.25f), "wire");
                Box("Block", _lowerTrolley, new Vector3(0, -11f, 0), new Vector3(2f, 2.6f, 1.3f), "crane_yellow");
            }
        }

        private Transform Hook(string name, float x)
        {
            var hook = new GameObject(name).transform;
            hook.SetParent(_gantry, false);
            if (!PropLibrary.TryCreate("goliath_hook", _palette, hook, out _))
            {
                Box("Block", hook, new Vector3(0, 0.2f, 0), new Vector3(2.4f, 3f, 1.4f), "crane_yellow");
                Box("Stripe", hook, new Vector3(0, 0.2f, 0), new Vector3(2.45f, 0.5f, 1.45f), "crane_dark");
                Box("Shank", hook, new Vector3(0, -1.9f, 0), new Vector3(0.5f, 1.4f, 0.5f), "crane_dark");
                Box("Hook", hook, new Vector3(0.35f, -2.7f, 0), new Vector3(1.2f, 0.4f, 0.5f), "crane_dark");
            }
            hook.localPosition = new Vector3(x, _girderY - 20f, 0);
            return hook;
        }

        /// <summary>
        /// Moves the crane in the same frame as the block (ShipView updates the blocks in Update, this runs after).
        /// While a block hangs from the hook the gantry, upper trolley and hooks are locked to the block's path pivot
        /// (x along the rails, z across the span) and its top (sling height): no lag, at any speed or after a seek.
        /// With nothing on the hook the crane moves at its own speeds in a fixed order: hook straight up, then gantry
        /// and trolley travel (to the next pick-up or the park position), then the hook comes down over a block waiting
        /// for it. A seek or a loop (date jump) snaps to the new state so nothing of the previous lift carries over.
        /// </summary>
        private void LateUpdate()
        {
            if (_gantry == null || _ship == null)
            {
                return;
            }

            var playback = _ship.Playback;
            var calendar = _ship.Calendar;
            var clock = (object)playback ?? calendar;
            var positionRevision = playback?.PositionRevision ?? calendar?.PositionRevision ?? 0;
            var jumped = !_placed || !ReferenceEquals(_clock, clock) || _positionRevision != positionRevision;
            _clock = clock;
            _positionRevision = positionRevision;

            var multiplier = playback?.SpeedMultiplier ?? (calendar != null ? calendar.SpeedMultiplier * _ship.CalendarRate : 1f);
            // The selected hull's dock, also under an alias ID of the same building (same centre, e.g. DTW-DOCK / GY-DOCK2).
            var selectedDock = _ship.Selected?.Dock;
            var active = _ship.ActiveDock == _dockId || selectedDock != null &&
                         ((new Vector2(selectedDock.x, selectedDock.y) - _dockCentre).sqrMagnitude < 1f ||
                          _equipment != null && Covers(new Vector2(selectedDock.x, selectedDock.y)));
            var liftBlock = _ship.CurrentLiftBlock;
            if (_craneId != null && liftBlock != null)
            {
                // The ERP crane job decides first: a lift given to this crane is followed even when the hull's dock is
                // not one this crane's geometry matches (a registered rail shared by several work areas), and a lift
                // given to another goliath is left to it. An unassigned lift: the dock's first goliath.
                var crane = _ship.LiftCrane(liftBlock.block);
                active = crane != null ? crane == _craneId : active && _craneIndex == 0;
            }

            var carrying = active && _ship.CurrentLift != null;
            var lifting = carrying && _ship.CurrentLiftHooked;
            var bounds = _ship.CurrentLiftBounds;
            var pivot = carrying ? _ship.CurrentLift.position : bounds.center;
            var liftId = carrying ? liftBlock?.block : null;
            if (_ship.Selected == null)
            {
                // Yard calendar: a lift by this crane (its ERP crane job, else a dock it spans); the one it already
                // holds keeps priority, so two overlapping lifts do not make it jump between them.
                var found = false;
                foreach (var yardLift in _ship.YardLifts)
                {
                    var dock = yardLift.Dock;
                    if (_craneId != null && yardLift.Crane != null)
                    {
                        if (yardLift.Crane != _craneId) continue;   // another goliath's lift
                    }
                    else if (_craneIndex > 0 || dock == null || !(dock.location == _dockId || (new Vector2(dock.x, dock.y) - _dockCentre).sqrMagnitude < 1f ||
                                                                  _equipment != null && Covers(new Vector2(dock.x, dock.y))))
                    {
                        continue;
                    }

                    var id = yardLift.Block?.block;
                    if (found && id != _liftId) continue;
                    found = true;
                    carrying = true;
                    lifting = yardLift.Hooked;
                    bounds = yardLift.Bounds;
                    pivot = yardLift.Position;
                    liftId = id;
                    if (id == _liftId) break;
                }
            }

            // Shared ERP replay: the crane stands where the replay puts it (rail position, trolley, hook), at once.
            var synced = _ship.Synced ? _ship.SyncedCrane : null;
            if (synced != null && (_craneId != null && !string.IsNullOrEmpty(synced.id) ? synced.id != _craneId : !active))
                synced = null;
            if (synced == null && !carrying && _settled && !jumped)
            {
                return;   // parked or idle and already in place: nothing to move this frame
            }

            var parkTop = _girderY - 22f;
            var blockTop = bounds.max.y - transform.position.y + 9f;   // sling points just above the block
            var lift = transform.InverseTransformPoint(pivot);   // crane axes: x along the rails, z across the dock
            float targetX, targetZ, targetTop;
            if (lifting)
            {
                targetX = synced != null ? Mathf.Clamp(lift.x, _railStart, _railEnd) : ClampRail(lift.x);
                targetZ = ClampTrolley(lift.z);
                targetTop = blockTop;
            }
            else if (synced != null)
            {
                var centre = transform.InverseTransformPoint(ShipCoordinates.YardToUnity(synced.x, synced.y, 0f));
                var hookAt = transform.InverseTransformPoint(ShipCoordinates.YardToUnity(synced.hook.x, synced.hook.y, synced.hook.z));
                targetX = Mathf.Clamp(centre.x, _railStart, _railEnd);
                targetZ = ClampTrolley(hookAt.z);
                targetTop = Mathf.Min(parkTop, hookAt.y);
            }
            else
            {
                // Waiting for a block (driven to the rail head) or idle: over the block when it can be reached, else parked.
                targetX = carrying ? ClampRail(lift.x) : ClampRail(_parkX);
                targetZ = carrying ? ClampTrolley(lift.z) : 0f;
                var reachable = carrying && Mathf.Abs(lift.x - targetX) < 1f && Mathf.Abs(lift.z - targetZ) < 1f;
                targetTop = reachable ? Mathf.Min(parkTop, blockTop) : parkTop;
            }

            if (jumped || lifting || synced != null)
            {
                // Hooked: the block is the truth, the crane is where it hangs. Seek, loop or shared replay: the new state.
                _gantryX = targetX;
                _trolleyZ = targetZ;
                _hookTop = targetTop;
            }
            else
            {
                // Paused: the crane waits with the blocks. Small drifts (a transporter creeping in) do not lift the hook.
                var playing = playback != null ? playback.Playing || playback.Schedule.Finished : calendar?.Playing ?? true;
                var k = playing ? Mathf.Max(1f, multiplier) * Time.deltaTime : 0f;
                var far = Mathf.Abs(_gantryX - targetX) > 1f || Mathf.Abs(_trolleyZ - targetZ) > 1f;
                if (far && _hookTop < parkTop - 0.05f)
                {
                    _hookTop = Mathf.MoveTowards(_hookTop, parkTop, _hoistSpeed * k);   // released: hook straight up first
                }
                else
                {
                    _gantryX = Mathf.MoveTowards(_gantryX, targetX, _travelSpeed * k);
                    _trolleyZ = Mathf.MoveTowards(_trolleyZ, targetZ, _trolleySpeed * k);
                    var away = Mathf.Abs(_gantryX - targetX) > 0.05f || Mathf.Abs(_trolleyZ - targetZ) > 0.05f;
                    // Lower only once over the target (a raised hook may still go up to park height while travelling).
                    if (!away || targetTop >= _hookTop)
                    {
                        _hookTop = Mathf.MoveTowards(_hookTop, targetTop, _hoistSpeed * k);
                    }
                }
            }

            _placed = true;
            _liftId = carrying ? liftId : null;
            _gantry.localPosition = new Vector3(_gantryX, 0, 0);
            var trolley = _upperTrolley.localPosition;
            _upperTrolley.localPosition = new Vector3(0, trolley.y, _trolleyZ);
            var settled = !carrying && synced == null && Mathf.Abs(_gantryX - targetX) < 0.05f &&
                          Mathf.Abs(_trolleyZ - targetZ) < 0.05f && Mathf.Abs(_hookTop - targetTop) < 0.05f;

            // Hooks hang plumb under the trolley drums: falls from the drum straight down to the hook block.
            for (var h = 0; h < _hooks.Count; h++)
            {
                var hook = _hooks[h];
                var x = h == 0 ? -2.6f : 2.6f;
                hook.localPosition = new Vector3(x, _hookTop + 2.9f, _trolleyZ);
                var top = hook.localPosition + Vector3.up * 1.7f;
                var drum = new Vector3(x, trolley.y, _trolleyZ);
                for (var i = 0; i < 4; i++)
                {
                    var offset = new Vector3(i < 2 ? -0.5f : 0.5f, 0, i % 2 == 0 ? -0.35f : 0.35f);
                    Beam(_falls[h * 4 + i], drum + offset, top + offset, 0.14f);
                }
            }

            for (var i = 0; i < _slings.Count; i++)
            {
                _slings[i].gameObject.SetActive(lifting);
                if (!lifting)
                {
                    continue;
                }

                var hook = _hooks[i < 2 ? 0 : 1].localPosition + new Vector3(0.35f, -2.9f, 0);
                var world = new Vector3(i < 2 ? bounds.min.x + bounds.size.x * 0.2f : bounds.max.x - bounds.size.x * 0.2f, bounds.max.y,
                    i % 2 == 0 ? bounds.min.z + bounds.size.z * 0.2f : bounds.max.z - bounds.size.z * 0.2f);
                var corner = transform.InverseTransformPoint(world) - new Vector3(_gantryX, 0, 0);
                Beam(_slings[i], hook, corner, 0.12f);
            }

            _settled = settled;
        }

        /// <summary>Gantry x on its rails (kept 12 m from the head end for the legs and bogies).</summary>
        private float ClampRail(float x) =>
            Mathf.Clamp(x, _railStart + Mathf.Min(BlockMotion.RailHeadInset, (_railEnd - _railStart) / 2f), _railEnd);

        /// <summary>Upper trolley z between the legs (9 m inside each rail).</summary>
        private float ClampTrolley(float z) =>
            Mathf.Clamp(z, -_span / 2f + BlockMotion.TrolleyInset, _span / 2f - BlockMotion.TrolleyInset);

        private void Beam(string name, Vector3 from, Vector3 to, float thickness, string paint) =>
            Beam(Box(name, _gantry, Vector3.zero, Vector3.one, paint), from, to, thickness);

        /// <summary>Stretches a unit cube (child of the gantry) between two gantry-local points.</summary>
        private static void Beam(Transform beam, Vector3 from, Vector3 to, float thickness)
        {
            var direction = to - from;
            var length = direction.magnitude;
            beam.localPosition = (from + to) / 2f;
            beam.localRotation = length > 1e-4f ? Quaternion.LookRotation(direction, Mathf.Abs(direction.normalized.y) > 0.99f ? Vector3.forward : Vector3.up) : Quaternion.identity;
            beam.localScale = new Vector3(thickness, thickness, Mathf.Max(0.01f, length));
        }

        private Transform Box(string name, Transform parent, Vector3 localPosition, Vector3 size, string paint)
        {
            var part = GameObject.CreatePrimitive(PrimitiveType.Cube);
            Destroy(part.GetComponent<Collider>());
            part.name = name;
            part.transform.SetParent(parent, false);
            part.transform.localPosition = localPosition;
            part.transform.localScale = size;
            part.GetComponent<Renderer>().sharedMaterial = _palette.Get(paint);
            return part.transform;
        }

        private void Cylinder(string name, Transform parent, Vector3 localPosition, Vector3 axis, float radius, float length, string paint)
        {
            var part = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
            Destroy(part.GetComponent<Collider>());
            part.name = name;
            part.transform.SetParent(parent, false);
            part.transform.localPosition = localPosition;
            part.transform.localRotation = Quaternion.FromToRotation(Vector3.up, axis);
            part.transform.localScale = new Vector3(radius * 2f, length / 2f, radius * 2f);
            part.GetComponent<Renderer>().sharedMaterial = _palette.Get(paint);
        }
    }
}
