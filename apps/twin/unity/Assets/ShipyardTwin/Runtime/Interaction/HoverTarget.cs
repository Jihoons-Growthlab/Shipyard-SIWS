using System.Collections.Generic;
using UnityEngine;

namespace ShipyardTwin
{
    /// <summary>What a named object is, for the detail popup (<see cref="ObjectDetailView"/>).</summary>
    public enum TargetCategory
    {
        None,
        Hull,
        Block,
        Crane,
        Vehicle,
        Facility,

        /// <summary>Scenery (cars, forklifts, buildings) that the ERP does not know.</summary>
        Prop,
    }

    /// <summary>
    /// Name shown when the cursor rests on an object (<see cref="HoverTooltipView"/>) and identity for the detail popup
    /// a click opens (<see cref="ObjectDetailView"/>). Sits on the object's root; the colliders on its child meshes find
    /// it with GetComponentInParent, so the nearest target wins (a block before the ship it belongs to). A target with an
    /// empty <see cref="Title"/> is an occluder: ground, sea — it stops the pick so nothing behind it is named.
    /// </summary>
    public sealed class HoverTarget : MonoBehaviour
    {
        private static readonly HashSet<HoverTarget> Registered = new HashSet<HoverTarget>();
        private static readonly HashSet<HoverTarget> Hazards = new HashSet<HoverTarget>();

        internal static IEnumerable<HoverTarget> ActiveTargets => Registered;
        internal static IEnumerable<HoverTarget> ActiveHazards => Hazards;
        internal bool ExcludeFromSearch { get; set; }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void ResetTargets()
        {
            Registered.Clear();
            Hazards.Clear();
        }

        private void OnEnable()
        {
            Registered.Add(this);
            UpdateHazardRegistration();
        }

        private void OnDisable()
        {
            Registered.Remove(this);
            Hazards.Remove(this);
        }

        public string Kind;
        public string Title;
        public string Detail;

        public TargetCategory Category;

        /// <summary>ERP key: hull, block, equipment or facility ID; scenery model name for props; may be null.</summary>
        public string Id;

        /// <summary>Hull the object belongs to (blocks, ships), else null.</summary>
        public string Hull;

        /// <summary>Facility the object works at (a goliath's dock), else null.</summary>
        public string Place;

        /// <summary>Mobile yard machinery eligible for the Twin's simulated danger-radius overlay.</summary>
        private bool _isHeavyEquipment;
        private WalkHazardShape _craneHazard;

        public bool IsHeavyEquipment
        {
            get => _isHeavyEquipment;
            set
            {
                _isHeavyEquipment = value;
                UpdateHazardRegistration();
            }
        }

        internal WalkHazardShape CraneHazard
        {
            get => _craneHazard;
            set
            {
                _craneHazard = value;
                UpdateHazardRegistration();
            }
        }

        private void UpdateHazardRegistration()
        {
            if (isActiveAndEnabled && (_isHeavyEquipment || _craneHazard.HalfSize.x > 0f)) Hazards.Add(this);
            else Hazards.Remove(this);
        }

        public bool IsOccluder => string.IsNullOrEmpty(Title);

        /// <summary>Sets the identity the detail popup looks up; returns this for chaining after <see cref="Attach"/>.</summary>
        public HoverTarget Identify(TargetCategory category, string id, string hull = null, string place = null)
        {
            Category = category;
            Id = id;
            Hull = hull;
            Place = place;
            return this;
        }

        /// <summary>Adds (or updates) the target on root and a mesh collider on every child mesh without one.</summary>
        public static HoverTarget Attach(GameObject root, string kind, string title, string detail = null)
        {
            var target = root.GetComponent<HoverTarget>();
            if (target == null)
            {
                target = root.AddComponent<HoverTarget>();
            }

            target.Kind = kind;
            target.Title = title;
            target.Detail = detail;
            foreach (var filter in root.GetComponentsInChildren<MeshFilter>(true))
            {
                if (filter.sharedMesh != null && filter.GetComponent<Collider>() == null)
                {
                    filter.gameObject.AddComponent<MeshCollider>().sharedMesh = filter.sharedMesh;
                }
            }

            return target;
        }

        /// <summary>Ground, sea and machinery that hide what is behind them but have no name of their own.</summary>
        public static HoverTarget Occluder(GameObject root) => Attach(root, null, null);

        /// <summary>Facility label: the ERP name, else its ID.</summary>
        public static string FacilityTitle(Facility f) => string.IsNullOrWhiteSpace(f.name) ? f.location : f.name;
    }
}
