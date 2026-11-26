using System;
using System.Globalization;
using UnityEngine;
using UnityEngine.UIElements;

namespace ShipyardTwin
{
    /// <summary>
    /// Names the block, facility or ship under the cursor in a small tooltip beside it (Dashboard.uxml "hover-tip").
    /// One physics ray per frame against the colliders <see cref="HoverTarget"/> set up; the nearest hit counts, so
    /// ground, sea and cranes (occluders) hide what is behind them and blocks not yet erected have no collider.
    /// Hidden over the dashboard UI, while any mouse button is held (camera drag), and off screen. The tooltip
    /// ignores the pointer. Command line <c>-shipyard-hover x,y,z</c> (yard metres) fakes the cursor at that point
    /// for screenshots.
    /// Touch screens have no hover: there a tap (a short touch that hardly moves, not on the UI) names what is under
    /// the finger, shown above it so the finger does not cover it, until the next touch. Moving a real mouse switches
    /// back to hover.
    /// </summary>
    [RequireComponent(typeof(UIDocument))]
    public sealed class HoverTooltipView : MonoBehaviour
    {
        public const string HoverArgument = "-shipyard-hover";
        private const float OffsetX = 16f, OffsetY = 18f, Edge = 8f;
        private const float TapSeconds = 0.35f, TapSlopPixels = 12f;

        private VisualElement _root, _tip;
        private Label _kind, _title, _detail;
        private Vector3? _probe;
        private bool _touchMode;
        private int _lastTouchFrame = -10;
        private Vector2 _lastMouse, _touchStart;
        private float _touchStarted;
        private bool _touchMoved, _mouseSeen;
        private Vector2? _tap;

        /// <summary>The named target under a screen point (pixels, origin bottom left), or null.</summary>
        public static HoverTarget Pick(Camera camera, Vector2 screen)
        {
            if (camera == null)
            {
                return null;
            }

            Physics.SyncTransforms();   // blocks and cranes moved this frame
            if (!Physics.Raycast(camera.ScreenPointToRay(screen), out var hit, camera.farClipPlane))
            {
                return null;
            }

            var target = hit.collider.GetComponentInParent<HoverTarget>();
            return target == null || target.IsOccluder ? null : target;
        }

        /// <summary>The tooltip only shows for a free cursor: not over the UI, not dragging, on screen.</summary>
        public static bool Allowed(bool overUi, bool dragging, Vector2 screen, Vector2 size) =>
            !overUi && !dragging && screen.x >= 0f && screen.y >= 0f && screen.x <= size.x && screen.y <= size.y;

        public HoverTarget Current { get; private set; }

        private void Start()
        {
            _root = GetComponent<UIDocument>().rootVisualElement;
            _tip = _root?.Q("hover-tip");
            if (_tip == null)
            {
                return;
            }

            _kind = _tip.Q<Label>("hover-kind");
            _title = _tip.Q<Label>("hover-title");
            _detail = _tip.Q<Label>("hover-detail");
            _tip.pickingMode = PickingMode.Ignore;
            _tip.Query<VisualElement>().ForEach(e => e.pickingMode = PickingMode.Ignore);
            _probe = ParseProbe(ArgumentValue(HoverArgument));
            _touchMode = Input.touchSupported && !Input.mousePresent;
            _lastMouse = Input.mousePosition;
            Hide();
        }

        private void LateUpdate()
        {
            if (_tip == null)
            {
                return;
            }

            var camera = Camera.main;
            TrackTouch();
            if (_probe == null && _touchMode)
            {
                var tapTarget = _tap != null ? Pick(camera, _tap.Value) : null;
                if (tapTarget == null)
                {
                    Hide();
                    return;
                }

                Show(tapTarget, _tap.Value, above: true);
                return;
            }

            Vector2 screen = _probe != null && camera != null
                ? (Vector2)camera.WorldToScreenPoint(ShipCoordinates.YardToUnity(_probe.Value.x, _probe.Value.y, _probe.Value.z))
                : (Vector2)Input.mousePosition;
            if (_probe == null && !_mouseSeen)
            {
                Hide();   // the cursor position means nothing until a real mouse has moved (browsers start at 0,0)
                return;
            }

            var dragging = _probe == null && (Input.GetMouseButton(0) || Input.GetMouseButton(1) || Input.GetMouseButton(2));
            var overUi = _probe == null && DashboardShellView.PointerOverUi;
            var target = Allowed(overUi, dragging, screen, new Vector2(Screen.width, Screen.height)) ? Pick(camera, screen) : null;
            if (target == null)
            {
                Hide();
                return;
            }

            Show(target, screen);
        }

        /// <summary>Touch mode and taps. A tap is one finger, short, within a few pixels; any new touch clears it.</summary>
        private void TrackTouch()
        {
            if (Input.touchCount > 0)
            {
                _touchMode = true;
                _lastTouchFrame = Time.frameCount;
                var touch = Input.GetTouch(0);
                if (touch.phase == TouchPhase.Began)
                {
                    _tap = null;
                    _touchStart = touch.position;
                    _touchStarted = Time.unscaledTime;
                    _touchMoved = Input.touchCount > 1;
                }

                if (Input.touchCount > 1 || (touch.position - _touchStart).magnitude > TapSlopPixels)
                {
                    _touchMoved = true;
                }

                if (touch.phase == TouchPhase.Ended && Input.touchCount == 1 && !_touchMoved &&
                    Time.unscaledTime - _touchStarted <= TapSeconds && !DashboardShellView.PointerOverUi)
                {
                    _tap = touch.position;
                }

                _lastMouse = Input.mousePosition;
                return;
            }

            // A real mouse moving (not the mouse simulated from a touch that just ended) means hover again.
            var mouse = (Vector2)Input.mousePosition;
            if (Time.frameCount - _lastTouchFrame > 2 && (mouse - _lastMouse).sqrMagnitude > 4f)
            {
                _mouseSeen = true;
                _touchMode = false;
                _tap = null;
            }

            _lastMouse = mouse;
        }

        private void Hide()
        {
            Current = null;
            _tip.style.display = DisplayStyle.None;
        }

        private void Show(HoverTarget target, Vector2 screen, bool above = false)
        {
            if (Current != target)
            {
                Current = target;
                if (_kind != null)
                {
                    _kind.text = target.Kind ?? string.Empty;
                    _kind.style.display = string.IsNullOrEmpty(target.Kind) ? DisplayStyle.None : DisplayStyle.Flex;
                }

                if (_title != null) _title.text = target.Title;
                if (_detail != null)
                {
                    _detail.text = target.Detail ?? string.Empty;
                    _detail.style.display = string.IsNullOrEmpty(target.Detail) ? DisplayStyle.None : DisplayStyle.Flex;
                }
            }

            _tip.style.display = DisplayStyle.Flex;
            var panel = _root.panel;
            var p = panel != null ? RuntimePanelUtils.ScreenToPanel(panel, new Vector2(screen.x, Screen.height - screen.y)) : screen;
            float w = _tip.layout.width, h = _tip.layout.height, maxX = _root.layout.width, maxY = _root.layout.height;
            // Beside the cursor, flipped to the other side near the right or bottom edge, always inside the screen.
            var x = p.x + OffsetX;
            if (!float.IsNaN(w) && x + w > maxX - Edge) x = p.x - OffsetX - w;
            var y = p.y + OffsetY;
            if (!float.IsNaN(h) && (above || y + h > maxY - Edge)) y = p.y - OffsetY - h;
            if (!float.IsNaN(h) && above && y < Edge) y = p.y + OffsetY;
            _tip.style.left = Mathf.Max(Edge, x);
            _tip.style.top = Mathf.Max(Edge, y);
        }

        private static Vector3? ParseProbe(string value)
        {
            var parts = value?.Split(',');
            if (parts == null || parts.Length != 3)
            {
                return null;
            }

            var v = new float[3];
            for (var i = 0; i < 3; i++)
            {
                if (!float.TryParse(parts[i], NumberStyles.Float, CultureInfo.InvariantCulture, out v[i]))
                {
                    return null;
                }
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
