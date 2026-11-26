using UnityEngine;

namespace ShipyardTwin
{
    /// <summary>
    /// Block poses (world space, pivot = centre of gravity, block mesh in ship axes): in a yard slot, landed on the
    /// hull, and along the erection lift from the slot to the hull. The goliath of the hull's dock only reaches over
    /// its rails (the dock and the PE yard at its head); a block stocked elsewhere is first driven on the ground to
    /// the pick-up point at the rail head, then hoisted, carried over and lowered.
    /// </summary>
    public static class BlockMotion
    {
        /// <summary>Supports under a stocked block (m).</summary>
        public const float Dunnage = 0.3f;

        /// <summary>Clearance above the higher of start and landing while carried (m).</summary>
        public const float CarryClearance = 30f;

        /// <summary>Lift progress spent driving to the rail head when the slot is out of the crane's reach.</summary>
        public const float GroundShare = 0.3f;

        private const float PickupInset = 50f;

        /// <summary>Distance from each rail to the upper trolley's travel limit (m).</summary>
        public const float TrolleyInset = 9f;

        internal const float RailHeadInset = 12f;

        public readonly struct Reach
        {
            public Reach(Rect rails, Vector2 pickup) : this(rails, pickup, Vector2.zero, 0f)
            {
            }

            /// <param name="rails">Reach in the dock's axes (x along the dock heading, y across), relative to <paramref name="origin"/>.</param>
            /// <param name="pickup">Pick-up point in yard metres.</param>
            public Reach(Rect rails, Vector2 pickup, Vector2 origin, float headingDeg)
            {
                Rails = rails;
                Pickup = pickup;
                Origin = origin;
                HeadingDeg = headingDeg;
            }

            /// <summary>Rectangle the hook can reach, in the dock's axes (x along the dock, y across) around <see cref="Origin"/>.</summary>
            public Rect Rails { get; }

            /// <summary>Where a transporter sets a block down for the crane (yard metres): the PE yard at the rail head.</summary>
            public Vector2 Pickup { get; }

            /// <summary>Yard-metre origin of <see cref="Rails"/> (the dock centre; zero for a yard-axis rectangle).</summary>
            public Vector2 Origin { get; }

            /// <summary>Dock heading (deg from yard +x toward +y) that <see cref="Rails"/> is turned by.</summary>
            public float HeadingDeg { get; }

            /// <summary>The yard point in the dock's axes.</summary>
            public Vector2 Local(Vector2 yard)
            {
                var a = HeadingDeg * Mathf.Deg2Rad;
                var d = yard - Origin;
                return new Vector2(d.x * Mathf.Cos(a) + d.y * Mathf.Sin(a), -d.x * Mathf.Sin(a) + d.y * Mathf.Cos(a));
            }

            public bool Contains(Vector2 yard)
            {
                var position = Local(yard);
                const float tolerance = 0.001f;
                return position.x >= Rails.xMin - tolerance && position.x <= Rails.xMax + tolerance &&
                       position.y >= Rails.yMin - tolerance && position.y <= Rails.yMax + tolerance;
            }
        }

        /// <summary>
        /// Crane reach of a dock (GantryCraneView rails: 100 m past the head, to 10 m short of the gate, 8 m beyond the
        /// walls), in the dock's own axes so docks turned to their coast (e.g. 292°) reach along their own length.
        /// </summary>
        public static Reach CraneReach(Facility dock)
        {
            var pit = dock.size_x_m / 2f - YardLayout.WallThickness;
            var half = dock.size_y_m / 2f + 8f - TrolleyInset;
            var rails = Rect.MinMaxRect(-pit - 100f + RailHeadInset, -half, pit - 10f, half);
            var a = dock.heading_deg * Mathf.Deg2Rad;
            var along = new Vector2(Mathf.Cos(a), Mathf.Sin(a));
            var centre = new Vector2(dock.x, dock.y);
            return new Reach(rails, centre - along * (pit + PickupInset), centre, dock.heading_deg);
        }

        /// <summary>The pickup and travel limits of a goliath with a registered ERP rail.</summary>
        public static bool TryCraneReach(CraneRailRow rail, out Reach reach)
        {
            reach = default;
            if (rail == null || rail.equipment_type is not ("골리앗 크레인" or "갠트리 크레인")) return false;

            var start = new Vector2(rail.rail_x0, rail.rail_y0);
            var end = new Vector2(rail.rail_x1, rail.rail_y1);
            var length = Vector2.Distance(start, end);
            var half = rail.gauge_m / 2f - TrolleyInset;
            if (!(length >= 1f && half > 0f)) return false;

            var delta = end - start;
            var heading = Mathf.Atan2(delta.y, delta.x) * Mathf.Rad2Deg;
            var pickup = start + delta * (Mathf.Min(PickupInset, length / 2f) / length);
            reach = new Reach(Rect.MinMaxRect(Mathf.Min(RailHeadInset, length / 2f), -half, length, half), pickup, start, heading);
            return true;
        }

        public static Pose Landed(HullPlacement placement, BlockRecord block)
        {
            var hull = ShipCoordinates.HullPose(placement);
            return new Pose(hull.position + hull.rotation * ShipCoordinates.CentreOfGravity(block), hull.rotation);
        }

        /// <summary>Resting on its bottom in the slot, ship x along the slot heading.</summary>
        public static Pose InYard(BlockRecord block, float x, float y, float headingDeg) =>
            new Pose(ShipCoordinates.YardToUnity(x, y, YardLayout.QuayLevel + Dunnage + (block.cog_z - block.z_min)),
                ShipCoordinates.HeadingToUnity(headingDeg));

        public static Pose InYard(BlockRecord block, StockStay stay) => InYard(block, stay.Row.x, stay.Row.y, stay.Row.heading_deg);

        public static Vector2 YardOf(Vector3 world) => new Vector2(world.x, world.z);

        /// <summary>
        /// Pose at lift progress p (0..1) and whether the crane holds it (false while driven on the ground). With no
        /// slot the block appears already suspended above the crane's pickup point.
        /// </summary>
        public static (Pose Pose, bool Hooked) Lift(Pose? slot, Pose landed, float p, Reach reach)
        {
            p = Mathf.Clamp01(p);
            if (slot == null)
            {
                var height = Mathf.Max(YardLayout.QuayLevel, landed.position.y) + CarryClearance;
                var pickup = new Vector3(reach.Pickup.x, height, reach.Pickup.y);
                var overLanding = new Vector3(landed.position.x, height, landed.position.z);
                var position = p < 0.75f
                    ? Vector3.Lerp(pickup, overLanding, Mathf.SmoothStep(0f, 1f, p / 0.75f))
                    : Vector3.Lerp(overLanding, landed.position, Mathf.SmoothStep(0f, 1f, (p - 0.75f) / 0.25f));
                return (new Pose(position, landed.rotation), true);
            }

            var start = slot.Value;
            var ground = reach.Contains(YardOf(start.position)) ? 0f : GroundShare;
            if (ground > 0f)
            {
                var pickup = new Vector3(reach.Pickup.x, start.position.y, reach.Pickup.y);
                if (p < ground)
                {
                    var t = Mathf.SmoothStep(0f, 1f, p / ground);
                    return (new Pose(Vector3.Lerp(start.position, pickup, t), start.rotation), false);
                }

                start = new Pose(pickup, start.rotation);
            }

            var q = (p - ground) / (1f - ground);
            var top = Mathf.Max(start.position.y, landed.position.y) + CarryClearance;
            var above = new Vector3(start.position.x, top, start.position.z);
            var over = new Vector3(landed.position.x, top, landed.position.z);
            if (q < 0.2f)
            {
                return (new Pose(Vector3.Lerp(start.position, above, Mathf.SmoothStep(0f, 1f, q / 0.2f)), start.rotation), true);
            }

            if (q < 0.75f)
            {
                var t = Mathf.SmoothStep(0f, 1f, (q - 0.2f) / 0.55f);
                return (new Pose(Vector3.Lerp(above, over, t), Quaternion.Slerp(start.rotation, landed.rotation, t)), true);
            }

            return (new Pose(Vector3.Lerp(over, landed.position, Mathf.SmoothStep(0f, 1f, (q - 0.75f) / 0.25f)), landed.rotation), true);
        }
    }
}
