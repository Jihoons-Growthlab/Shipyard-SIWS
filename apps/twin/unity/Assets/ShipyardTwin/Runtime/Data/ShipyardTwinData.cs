using System;
using UnityEngine;

namespace ShipyardTwin
{
    // Field names match the JSON written by DataMigration/export_twin.py (ShipYard-ERP API rows), so they stay
    // snake_case for JsonUtility. Format id: "shipyard-twin/1".

    /// <summary>One exported hull: particulars, dock placement and one row per block.</summary>
    [Serializable]
    public sealed class TwinFile
    {
        public string format;
        public string exported_at;
        public HullInfo hull = new HullInfo();
        public HullPlacement placement = new HullPlacement();
        public BlockRecord[] blocks = new BlockRecord[0];
        public YardPlan yard = new YardPlan();

        public static TwinFile Parse(string json)
        {
            var file = JsonUtility.FromJson<TwinFile>(json);
            if (file == null || file.format != "shipyard-twin/1")
            {
                throw new FormatException("Not a shipyard-twin/1 file (export it with DataMigration/export_twin.py).");
            }

            file.blocks ??= new BlockRecord[0];
            file.yard ??= new YardPlan();
            file.yard.facilities ??= new Facility[0];
            return file;
        }
    }

    [Serializable]
    public sealed class HullInfo
    {
        public string hull;
        public string vessel_type;
        public string vessel_name;
        public string customer;
        public float length_m;
        public float beam_m;
        public float depth_m;
        public float draft_m;
        public float frame_spacing_m;
        public string delivery;
    }

    /// <summary>Ship origin (aft end, centre line, baseline) in yard coordinates on a dock.</summary>
    [Serializable]
    public sealed class HullPlacement
    {
        public string hull;
        public string dock;
        public float origin_x;
        public float origin_y;
        public float origin_z;
        public float heading_deg;
        public YardPoint dock_position = new YardPoint();
    }

    [Serializable]
    public sealed class YardPoint
    {
        public string location;
        public float x;
        public float y;
        public float z;
        public float heading_deg;
    }

    /// <summary>Geo-referenced yard: site origin (lat/lon, bearing of yard +x) and facility footprints from the ERP.</summary>
    [Serializable]
    public sealed class YardPlan
    {
        public YardSite site;
        public Facility[] facilities = new Facility[0];
    }

    [Serializable]
    public sealed class YardSite
    {
        public string id;
        public string name;
        public double lat;
        public double lon;
        public float x_axis_bearing_deg;
    }

    /// <summary>ERP location with yard footprint (rectangle centred on x/y; size 0 = point) and WGS84 centre.</summary>
    [Serializable]
    public sealed class Facility
    {
        public string location;
        public string name;
        public string kind;
        public float x, y, z, heading_deg;
        public float size_x_m, size_y_m, size_z_m;
        public double lat, lon;

        public bool HasFootprint => size_x_m > 0f && size_y_m > 0f;
    }

    /// <summary>One block: ship-coordinate box and centre of gravity, erection step and days, model key.</summary>
    [Serializable]
    public sealed class BlockRecord
    {
        public string block;
        public string hull;
        public string zone;
        public float weight_t;
        public int step;
        public string[] predecessors = new string[0];
        public string destination;
        public string baseline;
        public string forecast;
        public string erected_day;
        public string model_asset;
        public float x_min, x_max, y_min, y_max, z_min, z_max;
        public float cog_x, cog_y, cog_z;

        /// <summary>Actual erection day when recorded, otherwise the forecast, otherwise the baseline.</summary>
        public string Day => !string.IsNullOrEmpty(erected_day) ? erected_day : !string.IsNullOrEmpty(forecast) ? forecast : baseline;

        public Vector3 Size => new Vector3(x_max - x_min, y_max - y_min, z_max - z_min);
    }

    /// <summary>
    /// Yard-wide ERP rows, one array per route (format "shipyard-yard/1"): the startup query of
    /// <see cref="YardDataSource"/> and the snapshot pipeline/export_yard.py writes for offline use have this shape.
    /// </summary>
    [Serializable]
    public sealed class YardSnapshot
    {
        public string format;
        public string exported_at;
        public string source;
        public HullRow[] hulls = new HullRow[0];
        public HullInfo[] vessel_details = new HullInfo[0];
        public HullGeometryRow[] hull_geometry = new HullGeometryRow[0];
        public HullPlacement[] hull_placements = new HullPlacement[0];
        public BlockRecord[] erection_timeline = new BlockRecord[0];
        public StockPlanRow[] block_stock_plans = new StockPlanRow[0];
        public Facility[] location_geo = new Facility[0];
        public YardSite[] yard_sites = new YardSite[0];

        // Null means this route was unavailable or is absent from the older snapshot.
        public ShiftPointRow[] shift_points;
        public BerthAssignmentRow[] berth_assignments;
        public CraneAssignmentRow[] crane_assignments;
        public QuayShiftRow[] quay_shifts;
        public BlockMoveRow[] moves;
        public DockBookingRow[] dock_bookings;
        public CraneRailRow[] crane_rails;
        public CraneKinematicsRow[] crane_kinematics;
        public CraneLoadChartRow[] crane_load_charts;
        public HullOverviewRow[] hull_overview;
        public StagePlanRow[] block_stages;
        public CenterLocationRow[] center_locations;
        public YardEquipmentSpec[] equipment_specs;

        public const string Format = "shipyard-yard/1";

        public static YardSnapshot Parse(string json)
        {
            var file = JsonUtility.FromJson<YardSnapshot>(json);
            if (file == null || file.format != Format)
            {
                throw new FormatException("Not a " + Format + " file (export it with pipeline/export_yard.py).");
            }

            return file.Normalised();
        }

        public YardSnapshot Normalised()
        {
            hulls ??= new HullRow[0];
            vessel_details ??= new HullInfo[0];
            hull_geometry ??= new HullGeometryRow[0];
            hull_placements ??= new HullPlacement[0];
            erection_timeline ??= new BlockRecord[0];
            block_stock_plans ??= new StockPlanRow[0];
            location_geo ??= new Facility[0];
            yard_sites ??= new YardSite[0];
            return this;
        }
    }

    /// <summary>ERP block_stages row: one assembly stage of a block at a work centre, ending on its forecast (actual) day.</summary>
    [Serializable]
    public sealed class StagePlanRow
    {
        public string block, assembly_stage, center, baseline, forecast, actual;
        public float progress_pct;
    }

    /// <summary>ERP center_locations row: the facility a work centre works in.</summary>
    [Serializable]
    public sealed class CenterLocationRow
    {
        public string center, location;
    }

    [Serializable]
    public sealed class HullOverviewRow
    {
        public string hull, lifecycle_status, launch_actual, trial_actual, delivery_actual, actual_shift_day, actual_shift_point;
        public int erection_planned_count, erection_accepted_count;
    }

    [Serializable]
    public sealed class YardEquipmentSpec
    {
        public string equipment, equipment_type, station;
        public float capacity_t;
    }

    [Serializable]
    public sealed class ShiftPointRow
    {
        public string id, name, kind;
        public bool position_known;
        public ShiftPointPosition position;
    }

    [Serializable]
    public sealed class ShiftPointPosition
    {
        public float x, y, z, heading_deg;
    }

    [Serializable]
    public sealed class BerthAssignmentRow
    {
        public string id, hull, berth, start_day, end_day, berth_purpose;
    }

    [Serializable]
    public sealed class CraneAssignmentRow
    {
        public string id, hull, equipment, block, work_area, work_day, crane_work;
        public int start_hour, hours;
        public float lift_t;
    }

    /// <summary>ERP dock_bookings row: the hull occupies the dock from start_day to end_day (launch), both inclusive.</summary>
    [Serializable]
    public sealed class DockBookingRow
    {
        public string id, dock, hull, start_day, end_day;
    }

    /// <summary>ERP crane_rails row: a rail-mounted crane's travel zone (yard m), rail gauge and jib direction.</summary>
    [Serializable]
    public sealed class CraneRailRow
    {
        public string equipment, name, equipment_type, station;
        public float rail_x0, rail_y0, rail_x1, rail_y1, gauge_m, jib_heading_deg, capacity_t;
    }

    /// <summary>
    /// ERP crane_kinematics row: a rail crane's slewing jib. Boom length from the slewing pivot (pivot_height_m above the
    /// quay), boom angle range from horizontal (hook radius = boom · cos(angle)), parked angle, load staging distance and
    /// the danger margin beyond the largest hook radius (danger zone = largest radius + margin).
    /// </summary>
    [Serializable]
    public sealed class CraneKinematicsRow
    {
        public string equipment, name;
        public float boom_m = 50f, pivot_height_m = 30f, luff_min_deg = 15f, luff_max_deg = 80f, rest_luff_deg = 70f, staging_offset_m = 15f;
        public float danger_margin_m = 5f;

        /// <summary>Defaults when the ERP has no row (same as apps/erp/yardsim.py DEFAULT_KINEMATICS).</summary>
        public static readonly CraneKinematicsRow Default = new CraneKinematicsRow();
    }

    /// <summary>
    /// ERP crane_load_charts row: the rated load (t) of a rail jib crane at a hook radius (m from the slewing centre).
    /// Between points the next larger radius applies; beyond the last point nothing may be lifted (apps/erp/geometry.py).
    /// </summary>
    [Serializable]
    public sealed class CraneLoadChartRow
    {
        public string equipment, name;
        public float radius_m, capacity_t;
    }

    [Serializable]
    public sealed class QuayShiftRow
    {
        public string id, hull, from_point, to_point, plan_day, actual_day, shift_purpose;
    }

    [Serializable]
    public sealed class BlockMoveRow
    {
        public string id, block, equipment, origin, destination, started_at, confirmed_at;
    }

    [Serializable]
    public sealed class HullRow
    {
        public string id;
        public string name;
    }

    [Serializable]
    public sealed class HullGeometryRow
    {
        public string hull;
        public float depth_m;
        public float draft_m;
        public float frame_spacing_m;
    }

    /// <summary>ERP block_stock_plans row: one stay of a block at a yard slot (yard metres, heading from yard +x).</summary>
    [Serializable]
    public sealed class StockPlanRow
    {
        public string id;
        public string block;
        public string hull;
        public string location;
        public float x;
        public float y;
        public float heading_deg;
        public string stock_from;
        public string stock_until;
        public string note;
    }
}
