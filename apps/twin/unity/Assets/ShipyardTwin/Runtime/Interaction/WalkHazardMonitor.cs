using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;
using Object = UnityEngine.Object;

namespace ShipyardTwin
{
    internal readonly struct WalkHazardShape
    {
        public readonly Vector2 HalfSize;
        public readonly bool IsBox, IsEstimated;

        private WalkHazardShape(Vector2 halfSize, bool box, bool estimated)
        {
            HalfSize = halfSize;
            IsBox = box;
            IsEstimated = estimated;
        }

        public static WalkHazardShape Circle(float radius, bool estimated) =>
            new WalkHazardShape(Vector2.one * radius, false, estimated);

        // Half length of the main bogie frame (17 m) plus the same 5 m visual clearance on each side.
        public static WalkHazardShape Gantry(float halfSpan) =>
            new WalkHazardShape(new Vector2(22f, halfSpan + 5f), true, true);

        public bool Contains(Vector3 local, float padding)
        {
            if (IsBox) return Mathf.Abs(local.x) <= HalfSize.x + padding && Mathf.Abs(local.z) <= HalfSize.y + padding;
            var radius = HalfSize.x + padding;
            return local.x * local.x + local.z * local.z <= radius * radius;
        }
    }

    /// <summary>Tests the walking camera's feet against live yard equipment positions, independent of object selection.</summary>
    public sealed class WalkHazardMonitor : IDisposable
    {
        private const float ExitPadding = 0.35f, HeightTolerance = 2.5f, GroundOffset = 0.35f;
        private static readonly int ColorId = Shader.PropertyToID("_Color");
        private static readonly int RadiusId = Shader.PropertyToID("_Radius");
        private static readonly int BoxId = Shader.PropertyToID("_BoxHalfSize");
        private static readonly int AlertId = Shader.PropertyToID("_Alert");
        private static readonly Color AlertColor = new Color(1f, 0.29f, 0.14f);

        private readonly HashSet<HoverTarget> _inside = new HashSet<HoverTarget>();
        private readonly List<Area> _visible = new List<Area>();
        private readonly List<Renderer> _renderers = new List<Renderer>();
        private readonly Dictionary<HoverTarget, (Vector3 Centre, WalkHazardShape Shape)> _footprints =
            new Dictionary<HoverTarget, (Vector3, WalkHazardShape)>();
        private MaterialPropertyBlock _properties;
        private Material _material;
        private Mesh _square;
        private float _refreshAt;
        private WalkHazardShape _primary;
        private HoverTarget _target;

        public int Count => _visible.Count;
        public string Name => _target != null ? _target.Title : string.Empty;
        public bool IsEstimated => _primary.IsEstimated;
        public string RangeText { get; private set; } = string.Empty;

        internal void Evaluate(Vector3 feet)
        {
            _visible.Clear();
            _target = null;
            var previous = _primary;
            _primary = default;
            var nearest = float.PositiveInfinity;
            if (Time.unscaledTime >= _refreshAt)
            {
                _footprints.Clear();
                _refreshAt = Time.unscaledTime + 2f;
            }

            foreach (var target in HoverTarget.ActiveHazards)
            {
                if (!TryArea(target, out var area) || Mathf.Abs(feet.y - area.Centre.y) > HeightTolerance) continue;
                var local = Quaternion.Inverse(area.Yaw) * (feet - area.Centre);
                if (!area.Shape.Contains(local, _inside.Contains(target) ? ExitPadding : 0f)) continue;
                _visible.Add(area);
                var distance = local.x * local.x + local.z * local.z;
                if (distance >= nearest) continue;
                nearest = distance;
                _target = target;
                _primary = area.Shape;
            }

            _inside.Clear();
            foreach (var area in _visible) _inside.Add(area.Target);
            if (previous.HalfSize != _primary.HalfSize || previous.IsBox != _primary.IsBox)
                RangeText = Count == 0 ? string.Empty : _primary.IsBox
                    ? "레일 작업 구역 · 폭 " + EquipmentZone.Metres(_primary.HalfSize.y * 2f)
                    : "위험반경 " + EquipmentZone.Metres(_primary.HalfSize.x);
        }

        private bool TryArea(HoverTarget target, out Area area)
        {
            area = default;
            if (target == null || !target.isActiveAndEnabled || target.IsOccluder) return false;
            var root = target.transform;
            var yaw = EquipmentZone.Yaw(root);
            if (target.CraneHazard.HalfSize.x > 0f)
            {
                area = new Area(target, root.position, yaw, target.CraneHazard);
                return true;
            }

            if (!EquipmentZone.Applies(target)) return false;
            if (!_footprints.TryGetValue(target, out var footprint))
            {
                _renderers.Clear();
                target.GetComponentsInChildren(true, _renderers);
                if (!EquipmentZone.Footprint(root, _renderers, out var centre, out var half) ||
                    !EquipmentZone.GroundY(_renderers, out var ground)) return false;
                centre.y = ground - root.position.y;
                footprint = (centre, WalkHazardShape.Circle(EquipmentZone.EstimateRadius(half.x, half.y), estimated: true));
                _footprints[target] = footprint;
            }
            area = new Area(target, root.position + yaw * footprint.Centre, yaw, footprint.Shape);
            return true;
        }

        internal void Render()
        {
            if (Count == 0 || !EnsureMaterial()) return;
            foreach (var area in _visible)
            {
                var shape = area.Shape;
                var size = shape.HalfSize;
                _properties.SetFloat(RadiusId, size.x);
                _properties.SetVector(BoxId, shape.IsBox ? new Vector4(size.x, size.y, 0f, 0f) : Vector4.zero);
                var rp = new RenderParams(_material)
                {
                    layer = area.Target.gameObject.layer,
                    shadowCastingMode = ShadowCastingMode.Off,
                    receiveShadows = false,
                    motionVectorMode = MotionVectorGenerationMode.ForceNoMotion,
                    lightProbeUsage = LightProbeUsage.Off,
                    reflectionProbeUsage = ReflectionProbeUsage.Off,
                    matProps = _properties,
                };
                var matrix = Matrix4x4.TRS(area.Centre + Vector3.up * GroundOffset, area.Yaw, new Vector3(size.x, 1f, size.y));
                Graphics.RenderMesh(rp, shape.IsBox ? Square : EquipmentZone.Disk, 0, matrix);
            }
        }

        internal void Clear()
        {
            _visible.Clear();
            _inside.Clear();
            _renderers.Clear();
            _footprints.Clear();
            _target = null;
            _primary = default;
            RangeText = string.Empty;
        }

        public void Dispose()
        {
            Clear();
            if (_material != null) Object.Destroy(_material);
            if (_square != null) Object.Destroy(_square);
        }

        private bool EnsureMaterial()
        {
            if (_material != null) return true;
            var shader = Shader.Find("ShipyardTwin/SelectionZone");
            if (shader == null || !shader.isSupported) return false;
            _material = new Material(shader) { name = "First-person hazard", hideFlags = HideFlags.DontSave };
            _material.SetColor(ColorId, AlertColor);
            _material.SetFloat(AlertId, 1f);
            _properties ??= new MaterialPropertyBlock();
            return true;
        }

        private Mesh Square
        {
            get
            {
                if (_square != null) return _square;
                _square = new Mesh
                {
                    name = "Gantry hazard footprint", hideFlags = HideFlags.DontSave,
                    vertices = new[] { new Vector3(-1, 0, -1), new Vector3(-1, 0, 1), new Vector3(1, 0, 1), new Vector3(1, 0, -1) },
                    uv = new[] { Vector2.zero, Vector2.up, Vector2.one, Vector2.right },
                    triangles = new[] { 0, 1, 2, 0, 2, 3 },
                };
                return _square;
            }
        }

        private readonly struct Area
        {
            public readonly HoverTarget Target;
            public readonly Vector3 Centre;
            public readonly Quaternion Yaw;
            public readonly WalkHazardShape Shape;

            public Area(HoverTarget target, Vector3 centre, Quaternion yaw, WalkHazardShape shape)
            {
                Target = target;
                Centre = centre;
                Yaw = yaw;
                Shape = shape;
            }
        }
    }
}
