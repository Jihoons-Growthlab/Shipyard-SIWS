using System;
using System.Linq;
using GrowthLab.Core;
using UnityEngine;

namespace ShipyardTwin
{
    /// <summary>
    /// Orbit camera around the dock. Mouse: left- or middle-drag grabs the ground (the point under the cursor stays
    /// under it), right-drag (or Alt/Ctrl + left-drag, for trackpads) orbits around the pivot, the wheel zooms towards
    /// the point under the cursor, a double click centres on that point. Drags move the camera at once; presets, zoom
    /// and follow ease in. A drag that starts on the dashboard UI does nothing, and the wheel is ignored over it.
    /// Keys: 1 overview, 2 bow, 3 stern, 4 crane close-up, 5 top-down, 6 whole yard, F follow the lifted block,
    /// WASD / arrows pan, Q / E turn, + / - zoom.
    /// The view is framed in the part of the screen the dashboard leaves free (<see cref="DashboardShellView.FreeArea"/>):
    /// an off-centre projection puts the pivot in the middle of that area, and a narrow area (phone upright, open drawer)
    /// moves the camera back so the hull still fits across it. Both ease when panels open or close.
    /// Command line <c>-shipyard-view &lt;overview|bow|stern|crane|top|yard|follow&gt;</c> picks the start view;
    /// <c>-shipyard-camera x,y,yaw,pitch,distance</c> places it anywhere (yard metres, for checks and screenshots).
    /// Uses the classic Input Manager (Project Settings: Active Input Handling = Input Manager or Both).
    /// </summary>
    public sealed class OrbitCameraRig : MonoBehaviour, ICoreConsumer
    {
        public const string ViewArgument = "-shipyard-view";

        /// <summary>Command line <c>-shipyard-camera x,y,yaw,pitch,distance</c>: orbit pivot at yard (x, y) on the quay.</summary>
        public const string CameraArgument = "-shipyard-camera";

        [Tooltip("Orbit degrees per pixel of right-drag.")]
        [SerializeField] private float _orbitSpeed = 0.25f;
        [Tooltip("Zoom per wheel notch (share of the distance).")]
        [SerializeField] private float _zoomSpeed = 0.14f;
        [SerializeField] private float _damping = 9f;
        [Tooltip("Keyboard pan speed: share of the view distance per second.")]
        [SerializeField] private float _keyPanSpeed = 0.9f;

        private const float MinDistance = 20f, MaxDistance = 1800f, MinPitch = 4f, MaxPitch = 86f, DoubleClickSeconds = 0.3f;
        private const float WalkNearClip = 0.12f;
        private const float WalkTargetHeight = 1.15f, WalkCameraDefault = 4.5f;
        private const float WalkCameraMin = 2.8f, WalkCameraMax = 7f, WalkCameraRadius = 0.22f;

        private Camera _camera;
        private ShipView _ship;
        private Vector3 _dockCentre;
        private float _dockHeading;
        private string _dockId;
        private float _hullLength;
        private Vector3 _yardCentre;
        private float _yardSize;
        private Vector3 _pivot, _pivotGoal;
        private float _yaw, _pitch, _distance, _yawGoal, _pitchGoal, _distanceGoal;
        private bool _follow;
        private bool _fixedByArgument;
        private bool _dragFromUi;
        private Vector2 _lastMouse, _pressAt;
        private float _lastClick = -1f;
        private bool _pressMoved;
        private readonly YardWalking _walking = new YardWalking();
        private readonly WalkGait _gait = new WalkGait();
        private WalkWorkerView _worker;
        private readonly WalkFootsteps _footsteps = new WalkFootsteps();
        private WalkAvatar _avatar;
        private string _avatarToken;
        private float _avatarYaw, _walkCameraDistance, _walkCameraGoal = WalkCameraDefault;
        private Vector2 _walkInput;
        private int _lookTouchId = -1;
        private bool _mouseLooking;
        private Vector2 _lastLookMouse;
        private float _orbitNearClip;

        // Framing in the free screen area: projection centre in NDC and the distance factor for a narrow area.
        private Vector2 _shift, _shiftGoal;
        private float _fit = 1f, _fitGoal = 1f;

        /// <summary>Wide area the presets are written for (16:10); narrower areas move the camera back.</summary>
        private const float ReferenceAspect = 1.6f, MaxFit = 2.6f;

        public Vector3 PivotGoal => _pivotGoal;

        /// <summary>The camera has reached its goal and is not following a lift (nothing to animate).</summary>
        public bool Settled => IsWalking ? _walkInput.sqrMagnitude < 1e-6f && _gait.Weight <= 0f && _lookTouchId == -1 && !_mouseLooking :
                               !(_follow && _ship != null && _ship.CurrentLift != null) && (_pivot - _pivotGoal).sqrMagnitude < 0.0004f &&
                               Mathf.Abs(Mathf.DeltaAngle(_yaw, _yawGoal)) < 0.02f && Mathf.Abs(_pitch - _pitchGoal) < 0.02f &&
                               Mathf.Abs(_distance - _distanceGoal) < 0.02f && Mathf.Abs(_fit - _fitGoal) < 0.002f &&
                               (_shift - _shiftGoal).sqrMagnitude < 1e-6f;
        public float YawGoal => _yawGoal;
        public float PitchGoal => _pitchGoal;
        public float DistanceGoal => _distanceGoal;

        public string View { get; private set; } = "overview";
        public bool IsWalking { get; private set; }
        public bool CanChooseWalkAvatar { get; private set; }
        public WalkAvatar CurrentWalkAvatar => _avatar;
        public WalkPace Pace { get; private set; } = WalkPace.Normal;
        public float PaceMetersPerSecond => WalkGait.Speed(Pace);
        public WalkHazardMonitor WalkHazards { get; } = new WalkHazardMonitor();

        /// <summary>Chooses the base pace for keyboard and touch movement.</summary>
        public void SetWalkPace(WalkPace pace) =>
            Pace = pace is WalkPace.Slow or WalkPace.Fast ? pace : WalkPace.Normal;

        public bool SetWalkAvatar(WalkAvatar avatar)
        {
            if (!IsWalking || !CanChooseWalkAvatar || !Enum.IsDefined(typeof(WalkAvatar), avatar)) return false;
            UpdateWalkAvatar(avatar);
            return true;
        }

        /// <summary>Enters ground-level exploration, or restores the orbit pose saved on entry.</summary>
        public bool SetWalking(bool enabled)
        {
            if (_camera == null || _worker == null) return false;
            if (enabled == IsWalking) return true;
            if (enabled && !_walking.TryBegin(_pivot, _dockCentre, _yaw)) return false;

            IsWalking = enabled;
            ResetWalkInput();
            _dragFromUi = false;
            _lastClick = -1f;
            _lastMouse = Input.mousePosition;
            if (enabled)
            {
                _gait.Reset();
                _avatarYaw = _walking.Yaw;
                _walkCameraDistance = _walkCameraGoal;
                _orbitNearClip = _camera.nearClipPlane;
                _camera.nearClipPlane = WalkNearClip;
                _camera.ResetProjectionMatrix();
                PlaceWalkCamera(0f);
                RefreshWalkAvatar();
                _worker.Show(_avatar);
                _worker.Update(_walking.Feet, _avatarYaw, _gait.Phase, _gait.Weight);
                UpdateWalkHazards();
            }
            else
            {
                _worker.Hide();
                _footsteps.Stop();
                WalkHazards.Clear();
                _camera.nearClipPlane = _orbitNearClip;
                Place();
            }
            return true;
        }

        private void RefreshWalkAvatar()
        {
            var token = YardDataSource.Token;
            if (token == _avatarToken) return;
            _avatarToken = token;
            CanChooseWalkAvatar = false;
            _avatar = WalkAvatar.Worker;
            if (string.IsNullOrEmpty(token) || string.IsNullOrEmpty(YardDataSource.ErpUrl)) return;
            StartCoroutine(WalkAvatarRoles.Resolve(YardDataSource.ErpUrl, token, groupId =>
            {
                if (_avatarToken != token) return;
                if (string.IsNullOrEmpty(groupId))
                {
                    _avatarToken = null;
                    return;
                }
                ApplyWalkAccount(groupId);
            }));
        }

        internal void ApplyWalkAccount(string groupId)
        {
            CanChooseWalkAvatar = WalkAvatarRoles.CanChoose(groupId);
            UpdateWalkAvatar(WalkAvatarRoles.ForGroup(groupId));
        }

        private void UpdateWalkAvatar(WalkAvatar avatar)
        {
            if (_avatar == avatar) return;
            _avatar = avatar;
            if (!IsWalking) return;
            _worker.Show(avatar);
            _worker.Update(_walking.Feet, _avatarYaw, _gait.Phase, _gait.Weight);
        }

        /// <summary>Left/right and backward/forward from the touch movement control.</summary>
        public void SetWalkInput(Vector2 input) => _walkInput = IsWalking ? Vector2.ClampMagnitude(input, 1f) : Vector2.zero;

        private void OnApplicationFocus(bool focused)
        {
            if (focused) return;
            ResetWalkInput();
            _footsteps.Stop();
        }

        private void OnDestroy()
        {
            WalkHazards.Dispose();
            _worker?.Dispose();
            _footsteps.Dispose();
        }

        private void ResetWalkInput()
        {
            _walkInput = Vector2.zero;
            _lookTouchId = -1;
            _mouseLooking = false;
        }

        public void Construct(CoreContext context)
        {
            if (_camera != null || !context.Services.TryGet<TwinFile>(out var twin))
            {
                return;
            }

            _camera = Camera.main;
            _worker = new WalkWorkerView(context.Services.Get<ShipPalette>());
            _ship = FindFirstObjectByType<ShipView>();
            _dockCentre = YardLayout.DockCentre(twin) + Vector3.up * 14f;
            _dockHeading = YardLayout.PlacementDock(twin)?.heading_deg ?? 0f;
            _dockId = twin.placement.dock;
            _hullLength = twin.hull.length_m;
            var plan = twin.yard?.facilities?.Where(f => f.HasFootprint).ToList();
            if (plan != null && plan.Count > 0)
            {
                float x0 = plan.Min(f => f.x - f.size_x_m / 2f), x1 = plan.Max(f => f.x + f.size_x_m / 2f);
                float y0 = plan.Min(f => f.y - f.size_y_m / 2f), y1 = plan.Max(f => f.y + f.size_y_m / 2f);
                _yardCentre = ShipCoordinates.YardToUnity((x0 + x1) / 2f, (y0 + y1) / 2f, 12f);
                _yardSize = Mathf.Max(x1 - x0, y1 - y0);
            }
            else
            {
                _yardCentre = _dockCentre;
                _yardSize = _hullLength * 1.5f;
            }
            SetView(ArgumentValue(ViewArgument) ?? "overview", snap: true);
            var custom = ParseCamera(ArgumentValue(CameraArgument));
            if (custom != null)
            {
                _fixedByArgument = true;   // a placed camera (checks, screenshots) is not moved to the selected dock
                _follow = false;
                (_pivot, _yaw, _pitch, _distance) = (_pivotGoal, _yawGoal, _pitchGoal, _distanceGoal) = custom.Value;
                Place();
            }
        }

        /// <summary>"x,y,yaw,pitch,distance" (yard metres, degrees) → orbit preset; null when absent or malformed.</summary>
        public static (Vector3 Pivot, float Yaw, float Pitch, float Distance)? ParseCamera(string value)
        {
            var parts = value?.Split(',');
            if (parts == null || parts.Length != 5)
            {
                return null;
            }

            var v = new float[5];
            for (var i = 0; i < 5; i++)
            {
                if (!float.TryParse(parts[i], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out v[i]))
                {
                    return null;
                }
            }

            return (ShipCoordinates.YardToUnity(v[0], v[1], YardLayout.QuayLevel), v[2], Mathf.Clamp(v[3], 3f, 89f), Mathf.Max(5f, v[4]));
        }

        /// <summary>Applies a named preset (unknown names fall back to the overview).</summary>
        public void SetView(string name, bool snap = false)
        {
            if (IsWalking) SetWalking(false);
            _follow = name == "follow" || name == "crane";
            var half = _hullLength / 2f;
            (Vector3 pivot, float yaw, float pitch, float distance) preset = name switch
            {
                // yaw 0 looks north (+z, ship port side away from the camera); 90 looks forward along the hull.
                "bow" => (_dockCentre + new Vector3(half - 25f, -2f, 0), 305f, 12f, 125f),
                "stern" => (_dockCentre + new Vector3(-half + 28f, -6f, 0), 55f, 9f, 105f),
                "crane" => (_dockCentre, 22f, 16f, 175f),
                "follow" => (_dockCentre, 35f, 24f, 210f),
                "top" => (_dockCentre, 0f, 72f, 430f),
                "yard" => (_yardCentre, 40f, 38f, _yardSize * 1.25f),
                "pier" => (_dockCentre + new Vector3(505f, -4f, -320f), 120f, 22f, 520f),
                "coast" => (_dockCentre + new Vector3(66f, -4f, -303f), 331f, 15f, 170f),
                _ => (_dockCentre + new Vector3(-10f, 0, 0), 50f, 22f, 400f),
            };
            if (name != "yard")
            {
                // Dock presets are written for gates towards +x: turn them with the dock on another coast.
                preset.pivot = _dockCentre + ShipCoordinates.HeadingToUnity(_dockHeading) * (preset.pivot - _dockCentre);
                preset.yaw -= _dockHeading;
            }

            View = name;
            (_pivotGoal, _yawGoal, _pitchGoal, _distanceGoal) = preset;
            if (snap)
            {
                (_pivot, _yaw, _pitch, _distance) = preset;
                if (_follow)
                {
                    FollowTarget(1f);
                    _pivot = _pivotGoal;
                }

                Place();
            }
        }

        private void Update()
        {
            if (_camera == null)
            {
                return;
            }

            if (IsWalking)
            {
                UpdateWalking();
                return;
            }

            HandleInput();
            FollowSelectedDock();
            if (_follow)
            {
                FollowTarget(Time.deltaTime);
            }

            UpdateFraming();
            var t = 1f - Mathf.Exp(-_damping * Time.deltaTime);
            var f = 1f - Mathf.Exp(-8f * Time.unscaledDeltaTime);
            _shift = Vector2.Lerp(_shift, _shiftGoal, f);
            _fit = Mathf.Lerp(_fit, _fitGoal, f);
            _pivot = Vector3.Lerp(_pivot, _pivotGoal, t);
            _yaw = Mathf.LerpAngle(_yaw, _yawGoal, t);
            _pitch = Mathf.Lerp(_pitch, _pitchGoal, t);
            _distance = Mathf.Lerp(_distance, _distanceGoal, t);
            Place();
        }

        private void UpdateWalking()
        {
            ReadWalkLook();
            if (Input.touchCount == 0 && !DashboardShellView.PointerOverUi)
                _walkCameraGoal = Mathf.Clamp(_walkCameraGoal - Mathf.Clamp(Input.mouseScrollDelta.y, -3f, 3f) * 0.45f,
                    WalkCameraMin, WalkCameraMax);
            var keyboard = DashboardShellView.SearchControlsFocused ? Vector2.zero : ReadDirection();
            var movement = Vector2.ClampMagnitude(keyboard + _walkInput, 1f);
            var sprint = keyboard.sqrMagnitude > 0f && (Input.GetKey(KeyCode.LeftShift) || Input.GetKey(KeyCode.RightShift));
            var seconds = Mathf.Min(Time.unscaledDeltaTime, 0.05f);
            var before = _walking.Feet;
            var distance = _walking.Move(movement, sprint ? WalkGait.SprintSpeed : PaceMetersPerSecond, seconds);
            var travelled = _walking.Feet - before;
            if (distance > 1e-5f)
                _avatarYaw = Mathf.MoveTowardsAngle(_avatarYaw, Mathf.Atan2(travelled.x, travelled.z) * Mathf.Rad2Deg,
                    720f * seconds);
            var footContact = _gait.Advance(distance, seconds);
            PlaceWalkCamera(seconds);
            _worker.Update(_walking.Feet, _avatarYaw, _gait.Phase, _gait.Weight);
            if (footContact) _footsteps.Play(_camera.transform, _gait.Weight);
        }

        private void PlaceWalkCamera(float seconds)
        {
            var rotation = Quaternion.Euler(_walking.Pitch, _walking.Yaw, 0f);
            var pivot = _walking.Feet + Vector3.up * WalkTargetHeight;
            var back = rotation * Vector3.back;
            var distance = _walkCameraGoal;
            if (Physics.SphereCast(pivot, WalkCameraRadius, back, out var hit, distance,
                    Physics.DefaultRaycastLayers, QueryTriggerInteraction.Ignore))
                distance = Mathf.Max(0.15f, hit.distance - 0.08f);
            _walkCameraDistance = distance <= _walkCameraDistance ? distance :
                Mathf.Lerp(_walkCameraDistance, distance, 1f - Mathf.Exp(-9f * seconds));
            _camera.transform.SetPositionAndRotation(pivot + back * _walkCameraDistance, rotation);
        }

        private void LateUpdate()
        {
            if (!IsWalking || _camera == null) return;
            UpdateWalkHazards();
            WalkHazards.Render();
        }

        private void UpdateWalkHazards() => WalkHazards.Evaluate(_walking.Feet);

        private void ReadWalkLook()
        {
            if (Input.touchCount > 0)
            {
                _mouseLooking = false;
                var stillLooking = false;
                var sensitivity = 180f / Mathf.Max(400, Screen.height);
                for (var i = 0; i < Input.touchCount; i++)
                {
                    var touch = Input.GetTouch(i);
                    if (_lookTouchId == -1 && touch.phase == TouchPhase.Began && !DashboardShellView.IsScreenPointOverUi(touch.position))
                        _lookTouchId = touch.fingerId;
                    if (touch.fingerId != _lookTouchId || touch.phase is TouchPhase.Canceled or TouchPhase.Ended) continue;
                    stillLooking = true;
                    if (touch.phase == TouchPhase.Moved) _walking.Look(touch.deltaPosition, sensitivity);
                }
                if (!stillLooking) _lookTouchId = -1;
                return;
            }

            _lookTouchId = -1;
            var mouse = (Vector2)Input.mousePosition;
            if (Input.GetMouseButtonDown(1))
                _mouseLooking = !DashboardShellView.IsScreenPointOverUi(mouse);
            else if (_mouseLooking && Input.GetMouseButton(1))
                _walking.Look(mouse - _lastLookMouse, _orbitSpeed);
            else
                _mouseLooking = false;
            _lastLookMouse = mouse;
        }

        private void FollowTarget(float dt)
        {
            if (_ship != null && _ship.CurrentLift != null)
            {
                var target = _ship.CurrentLiftBounds.center;
                var along = ShipCoordinates.HeadingToUnity(_dockHeading) * Vector3.right;   // the dock's length
                var onAxis = _dockCentre + along * Vector3.Dot(target - _dockCentre, along);
                _pivotGoal = Vector3.Lerp(_pivotGoal, new Vector3(onAxis.x, Mathf.Max(target.y - 10f, 10f), onAxis.z), Mathf.Clamp01(dt * 3f));
            }
        }

        /// <summary>When a hull in another dock is selected, the dock views move to that dock (keeping the current view).</summary>
        private void FollowSelectedDock()
        {
            var hull = _ship != null ? _ship.Selected : null;
            if (hull?.Dock == null || hull.Dock.location == _dockId)
            {
                return;
            }

            _dockId = hull.Dock.location;
            if (_fixedByArgument)
            {
                return;
            }

            _dockCentre = ShipCoordinates.YardToUnity(hull.Dock.x, hull.Dock.y, 0f) + Vector3.up * 14f;
            _dockHeading = hull.Dock.heading_deg;
            _hullLength = hull.Info?.length_m > 0 ? hull.Info.length_m : _hullLength;
            if (View != "yard")
            {
                SetView(View);
            }
        }

        private void HandleInput()
        {
            var searchFocused = DashboardShellView.SearchControlsFocused;
            if (!searchFocused)
            {
                for (var key = 0; key < 6; key++)
                {
                    if (Input.GetKeyDown(KeyCode.Alpha1 + key))
                    {
                        SetView(new[] { "overview", "bow", "stern", "crane", "top", "yard" }[key]);
                    }
                }

                if (Input.GetKeyDown(KeyCode.F)) _follow = !_follow;
            }

            // Two fingers (phones, tablets): pinch zooms, moving both fingers orbits. One finger arrives as the
            // simulated left mouse button and pans like a left drag.
            if (Input.touchCount >= 2)
            {
                _lastMouse = Input.mousePosition;
                if (!_dragFromUi && !DashboardShellView.PointerOverUi)
                {
                    ApplyTouch(Input.GetTouch(0), Input.GetTouch(1));
                }

                return;
            }

            // A drag that starts on the dashboard (rail, drawers, buttons) never moves the camera.
            var pressed = Input.GetMouseButtonDown(0) || Input.GetMouseButtonDown(1) || Input.GetMouseButtonDown(2);
            var held = Input.GetMouseButton(0) || Input.GetMouseButton(1) || Input.GetMouseButton(2);
            _dragFromUi = DragBlocked(_dragFromUi, pressed, held, DashboardShellView.PointerOverUi);
            var mouse = (Vector2)Input.mousePosition;
            var pixels = pressed ? Vector2.zero : mouse - _lastMouse;
            _lastMouse = mouse;
            if (_dragFromUi)
            {
                return;
            }

            // Double click (left, without moving): centre on the point under the cursor.
            if (Input.GetMouseButtonDown(0))
            {
                _pressAt = mouse;
                _pressMoved = false;
            }

            if (Input.GetMouseButton(0) && (mouse - _pressAt).sqrMagnitude > 25f) _pressMoved = true;
            if (Input.GetMouseButtonUp(0) && !_pressMoved)
            {
                if (Time.unscaledTime - _lastClick < DoubleClickSeconds)
                {
                    FocusAt(mouse);
                    _lastClick = -1f;
                }
                else
                {
                    _lastClick = Time.unscaledTime;
                }
            }

            var modifier = Input.GetKey(KeyCode.LeftAlt) || Input.GetKey(KeyCode.RightAlt) || Input.GetKey(KeyCode.LeftControl) ||
                           Input.GetKey(KeyCode.RightControl) || Input.GetKey(KeyCode.LeftCommand);
            var left = Input.GetMouseButton(0);
            ApplyMouse(left && !modifier, Input.GetMouseButton(1) || left && modifier, Input.GetMouseButton(2), pixels, Vector2.zero,
                DashboardShellView.PointerOverUi ? 0f : Input.mouseScrollDelta.y, mouse);
            if (!searchFocused) ApplyKeys(Time.unscaledDeltaTime);
        }

        /// <summary>WASD / arrows pan over the ground, Q / E turn, + / - zoom (not while a dashboard field has the keys).</summary>
        private void ApplyKeys(float dt)
        {
            var move = ReadDirection();
            if (move.sqrMagnitude > 0f)
            {
                _follow = false;
                var flat = Quaternion.Euler(0f, _yawGoal, 0f);
                _pivotGoal += flat * new Vector3(move.x, 0f, move.y) * (_distanceGoal * _keyPanSpeed * dt);
                ClampPivot();
            }

            var turn = (Input.GetKey(KeyCode.E) ? 1f : 0f) - (Input.GetKey(KeyCode.Q) ? 1f : 0f);
            if (turn != 0f) _yawGoal += turn * 70f * dt;
            var zoom = (Input.GetKey(KeyCode.Equals) || Input.GetKey(KeyCode.KeypadPlus) ? 1f : 0f) -
                       (Input.GetKey(KeyCode.Minus) || Input.GetKey(KeyCode.KeypadMinus) ? 1f : 0f);
            if (zoom != 0f) _distanceGoal = Mathf.Clamp(_distanceGoal * Mathf.Exp(-zoom * 1.4f * dt), MinDistance, MaxDistance);
        }

        private static Vector2 ReadDirection() => new Vector2(
            (Input.GetKey(KeyCode.D) || Input.GetKey(KeyCode.RightArrow) ? 1f : 0f) - (Input.GetKey(KeyCode.A) || Input.GetKey(KeyCode.LeftArrow) ? 1f : 0f),
            (Input.GetKey(KeyCode.W) || Input.GetKey(KeyCode.UpArrow) ? 1f : 0f) - (Input.GetKey(KeyCode.S) || Input.GetKey(KeyCode.DownArrow) ? 1f : 0f));

        /// <summary>A drag is blocked from its press (over the UI) until every button is released.</summary>
        public static bool DragBlocked(bool wasBlocked, bool pressed, bool held, bool overUi) => pressed ? overUi : held && wasBlocked;

        /// <summary>
        /// One frame of mouse input. Left or middle: grab the ground and drag it (by the cursor's pixel motion). Right:
        /// orbit (pixels). Wheel: zoom towards the point under the cursor (screen centre when none is given). Drags apply
        /// at once (no easing), so the ground stays under the cursor. <paramref name="axis"/> is unused (kept for callers).
        /// </summary>
        public void ApplyMouse(bool left, bool right, bool middle, Vector2 pixels, Vector2 axis, float wheel, Vector2? cursor = null)
        {
            var at = cursor ?? new Vector2(Screen.width * 0.5f, Screen.height * 0.5f);
            if ((left || middle) && pixels.sqrMagnitude > 0f)
            {
                _follow = false;
                var before = GroundUnder(at - pixels);
                var after = GroundUnder(at);
                Vector3 delta;
                if (before.HasValue && after.HasValue)
                {
                    delta = before.Value - after.Value;
                }
                else
                {
                    // Cursor above the horizon: slide over the ground by the screen scale at the pivot.
                    var fov = _camera != null ? _camera.fieldOfView : 60f;
                    var metresPerPixel = 2f * Placed(_distanceGoal) * Mathf.Tan(fov * 0.5f * Mathf.Deg2Rad) / Mathf.Max(1, Screen.height);
                    var flat = Quaternion.Euler(0f, _yawGoal, 0f);
                    delta = -(flat * new Vector3(pixels.x, 0f, pixels.y)) * metresPerPixel;
                }

                // Near the horizon one pixel spans far: at most a third of the view distance per frame.
                delta.y = 0f;
                delta = Vector3.ClampMagnitude(delta, Placed(_distanceGoal) / 3f);
                _pivotGoal += delta;
                ClampPivot();
                _pivot = _pivotGoal;
            }

            if (right && pixels.sqrMagnitude > 0f)
            {
                _yawGoal += pixels.x * _orbitSpeed;
                _pitchGoal = Mathf.Clamp(_pitchGoal - pixels.y * _orbitSpeed * 0.8f, MinPitch, MaxPitch);
                _yaw = _yawGoal;
                _pitch = _pitchGoal;
            }

            if (Math.Abs(wheel) > 0.01f)
            {
                // Browsers report large, uneven deltas: a few notches at most per frame, eased per notch.
                var notches = Mathf.Clamp(wheel, -3f, 3f);
                var target = Mathf.Clamp(_distanceGoal * Mathf.Pow(1f - _zoomSpeed, notches), MinDistance, MaxDistance);
                var ground = GroundUnder(at);
                if (ground.HasValue && target < _distanceGoal)
                {
                    // Towards the cursor: the pivot moves to it by the share the distance shrinks.
                    var toward = ground.Value - _pivotGoal;
                    toward.y = 0f;
                    _pivotGoal += toward * (1f - target / _distanceGoal);
                    ClampPivot();
                }

                _distanceGoal = target;
            }
        }

        /// <summary>Double click: the pivot eases to the point under the cursor (object or ground), a little closer.</summary>
        private void FocusAt(Vector2 screen)
        {
            if (_camera == null) return;
            Vector3? point = Physics.Raycast(_camera.ScreenPointToRay(screen), out var hit, 6000f) ? hit.point : GroundUnder(screen);
            if (!point.HasValue) return;
            _follow = false;
            _pivotGoal = new Vector3(point.Value.x, Mathf.Clamp(point.Value.y, 0f, 120f), point.Value.z);
            ClampPivot();
            _distanceGoal = Mathf.Clamp(_distanceGoal * 0.6f, MinDistance, MaxDistance);
        }

        /// <summary>Sets orbit goals for a selected model, using the normal camera easing and free-area framing.</summary>
        public bool Focus(Bounds bounds)
        {
            if (_camera == null || bounds.size.sqrMagnitude <= 0f) return false;
            if (IsWalking) SetWalking(false);
            _follow = false;
            _pivotGoal = bounds.center;
            ClampPivot();
            var halfFov = _camera.fieldOfView * Mathf.Deg2Rad * 0.5f;
            _distanceGoal = Mathf.Clamp(bounds.extents.magnitude / Mathf.Sin(halfFov) * 1.15f, MinDistance, MaxDistance);
            return true;
        }

        /// <summary>
        /// Where a screen point meets the horizontal plane through the pivot, seen from the goal pose (where the camera is
        /// heading, so repeated drags stay consistent while it eases); null above the horizon or too far.
        /// </summary>
        private Vector3? GroundUnder(Vector2 screen)
        {
            if (_camera == null) return null;
            var rotation = Quaternion.Euler(_pitchGoal, _yawGoal, 0f);
            var origin = _pivotGoal - rotation * Vector3.forward * Placed(_distanceGoal);
            var tan = Mathf.Tan(_camera.fieldOfView * 0.5f * Mathf.Deg2Rad);
            var h = Mathf.Max(1, Screen.height);
            var x = (screen.x / Mathf.Max(1, Screen.width) * 2f - 1f - _shift.x) * tan * _camera.aspect;
            var y = (screen.y / h * 2f - 1f - _shift.y) * tan;
            var direction = rotation * new Vector3(x, y, 1f).normalized;
            if (direction.y > -0.01f) return null;
            var t = (_pivotGoal.y - origin.y) / direction.y;
            return t > 0f && t < Placed(_distanceGoal) * 8f ? origin + direction * t : (Vector3?)null;
        }

        /// <summary>Keeps the pivot over the yard and its surroundings, at ground level or a little above.</summary>
        private void ClampPivot()
        {
            var reach = Mathf.Max(_yardSize, _hullLength) * 1.5f + 800f;
            var flat = new Vector3(_pivotGoal.x - _yardCentre.x, 0f, _pivotGoal.z - _yardCentre.z);
            if (flat.magnitude > reach) flat = flat.normalized * reach;
            _pivotGoal = new Vector3(_yardCentre.x + flat.x, Mathf.Clamp(_pivotGoal.y, 0f, 400f), _yardCentre.z + flat.z);
        }

        private void ApplyTouch(Touch a, Touch b)
        {
            var height = Mathf.Max(1, Screen.height);
            var before = ((a.position - a.deltaPosition) - (b.position - b.deltaPosition)).magnitude;
            var after = (a.position - b.position).magnitude;
            if (before > 1f && after > 1f)
            {
                _distanceGoal = Mathf.Clamp(_distanceGoal * before / after, MinDistance, MaxDistance);
            }

            var move = (a.deltaPosition + b.deltaPosition) * 0.5f / height;
            if (move.sqrMagnitude > 0f)
            {
                _yawGoal += move.x * 180f;
                _pitchGoal = Mathf.Clamp(_pitchGoal - move.y * 120f, MinPitch, MaxPitch);
            }
        }

        /// <summary>Goals for the projection centre and distance factor from the free screen area.</summary>
        private void UpdateFraming()
        {
            var free = DashboardShellView.FreeArea;
            _shiftGoal = new Vector2(free.xMin + free.xMax - 1f, 1f - (free.yMin + free.yMax));
            _fitGoal = FitFor(free.width * Screen.width / Mathf.Max(1f, Screen.height), free.height);
        }

        /// <summary>
        /// Distance factor for a free area <paramref name="width"/> × <paramref name="height"/> (in screen heights): 1 when
        /// it is a full-height area at least 16:10 wide, larger when it is narrower or shorter (the vertical view angle is
        /// the whole screen's, so a short area sees less), at most <see cref="MaxFit"/>.
        /// </summary>
        public static float FitFor(float width, float height)
        {
            if (width <= 0f || height <= 0f || float.IsNaN(width) || float.IsNaN(height)) return 1f;
            return Mathf.Clamp(Mathf.Pow(Mathf.Max(1f / height, ReferenceAspect / width), 0.85f), 1f, MaxFit);
        }

        /// <summary>Distance the camera is placed at: the zoom distance times the framing factor.</summary>
        private float Placed(float distance) => distance * _fit;

        private void Place()
        {
            var rotation = Quaternion.Euler(_pitch, _yaw, 0);
            _camera.transform.position = _pivot - rotation * Vector3.forward * Placed(_distance);
            _camera.transform.rotation = rotation;
            // Off-centre projection: the optical axis (the pivot) at the centre of the free area.
            var projection = Matrix4x4.Perspective(_camera.fieldOfView, _camera.aspect, _camera.nearClipPlane, _camera.farClipPlane);
            projection[0, 2] = -_shift.x;
            projection[1, 2] = -_shift.y;
            _camera.projectionMatrix = projection;
        }

        private static string ArgumentValue(string name)
        {
            var args = Environment.GetCommandLineArgs();
            var index = Array.IndexOf(args, name);
            return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
        }
    }
}
