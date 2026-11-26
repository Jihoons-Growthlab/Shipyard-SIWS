using System;
using System.Collections.Generic;
using System.Globalization;
using GrowthLab.UI;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.UIElements;

namespace ShipyardTwin
{
    /// <summary>
    /// Simulated danger zone of a selected heavy machine (forklift, transporter: <see cref="HoverTarget.IsHeavyEquipment"/>):
    /// a ground ring that follows the model and a "모의 위험반경" label. The ERP holds no verified radius for these machines,
    /// so the radius is a visual estimate from the model's own ground footprint (<see cref="EstimateRadius"/>), always
    /// labelled as such — never shown as a fixed measured value. Rail and goliath cranes keep their ERP-based danger
    /// radius (<see cref="CraneSafetyView"/>) and are skipped here, as are ordinary cars and trucks (not heavy equipment).
    /// Drawn with <see cref="Graphics.RenderMesh"/> ("ShipyardTwin/SelectionZone" on a unit
    /// <see cref="CraneSafetyView.RingMesh"/> disk): no collider, no shadow, nothing added under the model.
    /// Owned by <see cref="ObjectDetailView"/> next to <see cref="SelectionHighlight"/>.
    /// </summary>
    public sealed class EquipmentZone : IDisposable
    {
        public static readonly Color ZoneColour = new Color(1f, 0.62f, 0.22f, 1f);

        /// <summary>Clearance added to the footprint's half diagonal: 35 % of it, at least 1.5 m, at most 5 m.</summary>
        public const float MarginShare = 0.35f, MinMargin = 1.5f, MaxMargin = 5f;

        public const float GroundLift = 0.08f;

        public const string Notice = "ERP에 검증된 위험반경이 없어 모델 바닥 점유 크기로 추정한 시각용 표시입니다. 실제 작업 반경·안전거리로 쓰지 마세요.";

        private static readonly int ColorId = Shader.PropertyToID("_Color");
        private static readonly int RadiusId = Shader.PropertyToID("_Radius");
        private static readonly int FadeId = Shader.PropertyToID("_Fade");
        private static Mesh _disk;

        private readonly List<Renderer> _renderers = new List<Renderer>();
        private Material _material;
        private MaterialPropertyBlock _block;
        private Transform _anchor;
        private Label _label;
        private WorldLabels _labels;
        private Vector3 _centre;   // footprint centre in the target's yaw frame (metres)
        private float _shownAt;

        public HoverTarget Target { get; private set; }

        public bool IsShowing => Target != null;

        /// <summary>Estimated radius in metres of the shown machine (0 when nothing is shown).</summary>
        public float Radius { get; private set; }

        /// <summary>Whether the last <see cref="Render"/> drew the ring (tests, diagnostics).</summary>
        public bool Drawn { get; private set; }

        /// <summary>Heavy equipment gets a simulated zone; cranes have their own and cars or trucks none.</summary>
        public static bool Applies(HoverTarget target) =>
            target != null && target.IsHeavyEquipment && target.Category != TargetCategory.Crane;

        /// <summary>
        /// Visual radius (m) from a footprint's half extents (m): half diagonal plus a margin
        /// (<see cref="MarginShare"/> of it, clamped to <see cref="MinMargin"/>..<see cref="MaxMargin"/>), rounded up to 0.5 m.
        /// </summary>
        public static float EstimateRadius(float halfLength, float halfWidth)
        {
            var diagonal = Mathf.Sqrt(halfLength * halfLength + halfWidth * halfWidth);
            var margin = Mathf.Clamp(diagonal * MarginShare, MinMargin, MaxMargin);
            return Mathf.Ceil((diagonal + margin) * 2f) / 2f;
        }

        public static string Metres(float radius) => radius.ToString("0.#", CultureInfo.InvariantCulture) + " m";

        /// <summary>Label on the ring: the word "모의" and "추정" make clear it is not a measured value.</summary>
        public static string LabelText(float radius) => "모의 위험반경 ≈ " + Metres(radius) + "\n장비 크기 기반 추정 · ERP 미검증";

        /// <summary>Starts the zone for a heavy machine (anything else, or null, clears). Returns whether it is shown.</summary>
        public bool Show(HoverTarget target, WorldLabels labels)
        {
            Clear();
            if (!Applies(target))
            {
                return false;
            }

            target.GetComponentsInChildren(true, _renderers);
            if (!Footprint(target.transform, _renderers, out _centre, out var half))
            {
                _renderers.Clear();
                return false;
            }

            Target = target;
            Radius = EstimateRadius(half.x, half.y);
            _shownAt = Time.unscaledTime;
            _labels = labels;
            if (_labels != null)
            {
                _anchor = new GameObject("EquipmentZone label") { hideFlags = HideFlags.DontSave }.transform;
                _label = new Label(LabelText(Radius)) { pickingMode = PickingMode.Ignore };
                _label.AddToClassList("tw-zone-label");
                _labels.Track(_anchor, _label, Vector3.zero);
            }

            return true;
        }

        public void Clear()
        {
            Target = null;
            Radius = 0f;
            Drawn = false;
            _renderers.Clear();
            if (_label != null) _labels?.Untrack(_label);
            _label = null;
            if (_anchor != null) DestroyObject(_anchor.gameObject);
            _anchor = null;
        }

        /// <summary>Draws the ring under the machine this frame; clears when it was destroyed, hides while it is hidden.</summary>
        public void Render(Camera camera)
        {
            Drawn = false;
            if (Target == null)
            {
                if (_anchor != null || _label != null) Clear();
                return;
            }

            var ground = 0f;
            var visible = Target.gameObject.activeInHierarchy && GroundY(_renderers, out ground);
            // visibility, not display: WorldLabels.LateUpdate owns display (on screen or not) and would show it again.
            if (_label != null) _label.style.visibility = visible ? Visibility.Visible : Visibility.Hidden;
            if (!visible || !EnsureMaterial())
            {
                return;
            }

            var t = Mathf.Clamp01((Time.unscaledTime - _shownAt) / SelectionHighlight.FadeSeconds);
            var ease = 1f - (1f - t) * (1f - t) * (1f - t);
            var root = Target.transform;
            var centre = root.position + Yaw(root) * _centre;
            centre.y = ground + GroundLift;
            var radius = Radius * Mathf.Lerp(0.82f, 1f, ease);
            _block.SetFloat(FadeId, ease);
            _block.SetFloat(RadiusId, radius);
            var rp = new RenderParams(_material)
            {
                layer = Target.gameObject.layer,
                shadowCastingMode = ShadowCastingMode.Off,
                receiveShadows = false,
                motionVectorMode = MotionVectorGenerationMode.ForceNoMotion,
                lightProbeUsage = LightProbeUsage.Off,
                reflectionProbeUsage = ReflectionProbeUsage.Off,
                matProps = _block,
            };
            Graphics.RenderMesh(rp, Disk, 0, Matrix4x4.TRS(centre, Quaternion.identity, new Vector3(radius, 1f, radius)));
            Drawn = true;

            if (_anchor != null)
            {
                // On the ring edge to the camera's right, a little above the ground, so it never sits on the machine.
                var side = camera != null ? Vector3.ProjectOnPlane(camera.transform.right, Vector3.up) : Vector3.right;
                if (side.sqrMagnitude < 1e-4f) side = Vector3.right;
                _anchor.position = centre + side.normalized * radius + Vector3.up * 0.6f;
            }
        }

        public void Dispose()
        {
            Clear();
            if (_material != null) DestroyObject(_material);
            _material = null;
        }

        /// <summary>
        /// Ground footprint of a model in its root's yaw frame (heading only, metres): centre and half extents
        /// (x = across, along the root's right; y = along its forward). False without a mesh.
        /// </summary>
        public static bool Footprint(Transform root, IReadOnlyList<Renderer> renderers, out Vector3 centre, out Vector2 half)
        {
            centre = Vector3.zero;
            half = Vector2.zero;
            var toFrame = Quaternion.Inverse(Yaw(root));
            float minX = float.MaxValue, maxX = float.MinValue, minZ = float.MaxValue, maxZ = float.MinValue;
            var any = false;
            foreach (var renderer in renderers)
            {
                var mesh = SelectionHighlight.MeshOf(renderer);
                if (mesh == null)
                {
                    continue;
                }

                var b = mesh.bounds;
                var matrix = renderer.localToWorldMatrix;
                for (var i = 0; i < 8; i++)
                {
                    var corner = b.center + Vector3.Scale(b.extents, new Vector3((i & 1) == 0 ? -1 : 1, (i & 2) == 0 ? -1 : 1, (i & 4) == 0 ? -1 : 1));
                    var p = toFrame * (matrix.MultiplyPoint3x4(corner) - root.position);
                    minX = Mathf.Min(minX, p.x);
                    maxX = Mathf.Max(maxX, p.x);
                    minZ = Mathf.Min(minZ, p.z);
                    maxZ = Mathf.Max(maxZ, p.z);
                    any = true;
                }
            }

            if (!any)
            {
                return false;
            }

            centre = new Vector3((minX + maxX) / 2f, 0f, (minZ + maxZ) / 2f);
            half = new Vector2((maxX - minX) / 2f, (maxZ - minZ) / 2f);
            return true;
        }

        /// <summary>The root's heading only (a machine tilting on a slope keeps a flat ring).</summary>
        public static Quaternion Yaw(Transform root)
        {
            var forward = Vector3.ProjectOnPlane(root.forward, Vector3.up);
            return forward.sqrMagnitude > 1e-6f ? Quaternion.LookRotation(forward, Vector3.up) : Quaternion.identity;
        }

        internal static bool GroundY(List<Renderer> renderers, out float ground)
        {
            ground = float.MaxValue;
            foreach (var r in renderers)
            {
                if (r == null || !r.enabled || !r.gameObject.activeInHierarchy || SelectionHighlight.MeshOf(r) == null) continue;
                ground = Mathf.Min(ground, r.bounds.min.y);
            }

            return ground < float.MaxValue;
        }

        internal static Mesh Disk
        {
            get
            {
                if (_disk == null)
                {
                    _disk = CraneSafetyView.RingMesh(0f, 1f);
                    _disk.name = "EquipmentZone disk";
                    _disk.hideFlags = HideFlags.DontSave;
                }

                return _disk;
            }
        }

        private bool EnsureMaterial()
        {
            if (_material != null)
            {
                return true;
            }

            var shader = Shader.Find("ShipyardTwin/SelectionZone");
            if (shader == null || !shader.isSupported)
            {
                return false;
            }

            _material = new Material(shader) { name = "EquipmentZone", hideFlags = HideFlags.DontSave };
            _material.SetColor(ColorId, ZoneColour);
            _block ??= new MaterialPropertyBlock();
            return true;
        }

        private static void DestroyObject(UnityEngine.Object o)
        {
            if (Application.isPlaying) UnityEngine.Object.Destroy(o);
            else UnityEngine.Object.DestroyImmediate(o);
        }
    }
}
