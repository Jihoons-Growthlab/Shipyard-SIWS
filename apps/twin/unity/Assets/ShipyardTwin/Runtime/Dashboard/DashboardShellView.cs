using System;
using System.Collections.Generic;
using GrowthLab.Core;
using UnityEngine;
using UnityEngine.UIElements;

namespace ShipyardTwin
{
    /// <summary>
    /// Dashboard shell around the erection panel (layout in Dashboard.uxml / Dashboard.uss): the left icon rail,
    /// drawers that slide out from it (object search, erection; one at a time), the depth-map toggle with its legend,
    /// and the exit button with a confirmation. Everything starts closed. Escape closes the dialog or the open drawer
    /// (in the search field it first clears the query). While the search field has the keyboard
    /// <see cref="SearchFieldFocused"/> is true so camera shortcuts stay off.
    /// While the pointer is over these controls <see cref="PointerOverUi"/> is true so the camera ignores it.
    /// While the simulation plays the open drawer is put away (<see cref="SetSimulating"/>, from the schedule bar).
    /// Command line <c>-shipyard-panel</c> opens the erection drawer and <c>-shipyard-depth</c> turns the depth map on
    /// at start (screenshots).
    /// Layout adapts to the panel size: <c>tw-compact</c> below 720 x 520 panel units (phones, small windows) and
    /// <c>tw-portrait</c> for a compact panel taller than wide; <c>tw-touch</c> (last input was a touch) swaps the control help. In the browser
    /// the panel is scaled by the canvas pixel ratio so one panel unit is one CSS pixel on every device, and the
    /// bundled Korean font replaces OS fonts (a browser has none).
    /// Third-person walk: the top-bar "걸어보기" button calls <see cref="OrbitCameraRig.SetWalking"/>; the button, the
    /// root class <c>tw-walking</c>, the entry guide and the thumb joystick (touch or compact) follow
    /// <see cref="OrbitCameraRig.IsWalking"/> every frame, so a switch made elsewhere (search focus, Escape) shows too.
    /// The joystick feeds <see cref="OrbitCameraRig.SetWalkInput"/> and blocks the camera like the other controls.
    /// While walking inside a simulated crane or equipment zone (<see cref="OrbitCameraRig.WalkHazards"/>) a small
    /// "위험 구역 진입" card shows above the bottom bars; it hides by itself on leaving and never takes the pointer.
    /// Walk pace (느리게 / 보통 / 빠르게, <see cref="OrbitCameraRig.SetWalkPace"/>) is a segmented bar under the top bar,
    /// shown only while walking; it follows <see cref="OrbitCameraRig.Pace"/> and blocks camera drags like the other controls.
    /// Character (일꾼 / 중인 / 무림고수, <see cref="OrbitCameraRig.SetWalkAvatar"/>): only for a signed-in admin while
    /// walking (<see cref="OrbitCameraRig.CanChooseWalkAvatar"/>), a toggle at the end of the pace bar opens a small menu
    /// below it; it folds after a choice, on Escape, when a utility panel opens and when the walk ends.
    /// </summary>
    [RequireComponent(typeof(UIDocument))]
    public sealed class DashboardShellView : MonoBehaviour, ICoreConsumer
    {
        public const string PanelArgument = "-shipyard-panel";
        public const string DepthArgument = "-shipyard-depth";
        private const float RailWidth = 72f, DrawerGap = 14f, DrawerWidth = 360f;
        private const float CompactRailWidth = 60f, CompactGap = 8f, CompactWidth = 720f, CompactHeight = 520f;
        private const string FontResource = "ShipyardTwin/Fonts/NotoSansKR-Regular";
        private const int SlideMilliseconds = 320;
        private const int WalkHintMilliseconds = 7000;
        private const float PadDeadZone = 0.12f;

        private readonly List<VisualElement> _interactive = new List<VisualElement>();
        private VisualElement _root, _erectionPanel, _searchPanel, _legend, _confirm, _rail, _timeline, _banner;
        private Button _navErection, _navDepth, _navSearch;
        private TextField _searchField;
        private VisualElement _yardClock, _layersPanel, _helpPanel, _utilityPanel;
        private Button _navLayers, _navHelp;
        private YardDisplayLayers _displayLayers;
        private VisualElement _openDrawer, _suspendedDrawer;
        private int _lastTouchFrame = -10;
        private Vector2 _lastMouse;
        private OrbitCameraRig _rig;
        private Button _navWalk;
        private Label _walkLabel;
        private DashboardIcon _walkIcon;
        private VisualElement _walkHint, _walkPad, _walkKnob, _walkWarning;
        private Label _warningName, _warningRange, _warningSim;
        private bool _warningShown;
        private readonly Button[] _paceButtons = new Button[3];
        private WalkPace? _paceShown;
        private Button _avatarToggle;
        private VisualElement _avatarMenu;
        private readonly Button[] _avatarButtons = new Button[3];
        private bool _avatarAllowed, _avatarMenuOpen;
        private WalkAvatar? _avatarShown;

        private static readonly (string Button, WalkAvatar Avatar, string Label)[] AvatarOptions =
        {
            ("walk-avatar-worker", WalkAvatar.Worker, "일꾼"),
            ("walk-avatar-steward", WalkAvatar.Steward, "중인"),
            ("walk-avatar-master", WalkAvatar.Master, "무림고수"),
        };

        private static readonly (string Button, WalkPace Pace)[] PaceOptions =
        {
            ("walk-pace-slow", WalkPace.Slow),
            ("walk-pace-normal", WalkPace.Normal),
            ("walk-pace-fast", WalkPace.Fast),
        };
        private (string Name, string Range, int Count, bool Estimated) _warningKey;
        private IVisualElementScheduledItem _walkHintFade;
        private bool _walkingShown;
        private int _padPointer = -1;
        private float _barsTop = float.NaN, _freeRight = float.NaN;

        private static DashboardShellView _active;

        /// <summary>
        /// True while the mouse or the first touch is over a dashboard control (camera input should be ignored).
        /// Evaluated on read, so a touch that begins this frame is seen whatever the script order.
        /// </summary>
        public static bool PointerOverUi => _active != null && _active.IsPointerOverControls();

        /// <summary>
        /// True when a screen point (Unity screen pixels, origin bottom left, e.g. <c>Input.GetTouch(i).position</c>) is
        /// over a dashboard control, the walk joystick included. Lets the camera judge every finger of a multi-touch
        /// (one on the joystick, another looking around) with the same test as <see cref="PointerOverUi"/>.
        /// </summary>
        public static bool IsScreenPointOverUi(Vector2 screenPoint) => _active != null && _active.IsOverControls(screenPoint);

        /// <summary>The camera is in first-person walk mode (false without a camera rig).</summary>
        public bool IsWalking => Rig != null && Rig.IsWalking;

        /// <summary>
        /// Part of the screen not covered by the dashboard (rail, open drawer, schedule bar, banner), as fractions of the
        /// screen with y downwards. The camera centres the yard in it (<see cref="OrbitCameraRig"/>), so the ship is not
        /// hidden behind a panel, on a phone in particular. Whole screen when there is no dashboard.
        /// </summary>
        public static Rect FreeArea => _active != null ? _active._free : new Rect(0f, 0f, 1f, 1f);

        /// <summary>
        /// True only while the object search field in the open search drawer has keyboard focus (typing): camera and
        /// other keyboard shortcuts must ignore keys then (<see cref="OrbitCameraRig"/>). Evaluated on read.
        /// </summary>
        public static bool SearchFieldFocused => _active != null && _active.IsSearchFieldFocused();

        /// <summary>
        /// True while keyboard focus is anywhere in the open search drawer (the field, a result card, its buttons): the
        /// camera ignores arrow and other shortcut keys then (<see cref="OrbitCameraRig"/>). <see cref="SearchFieldFocused"/>
        /// (the field only) decides what Escape does. Evaluated on read.
        /// </summary>
        public static bool SearchControlsFocused => _active != null && _active.IsSearchPanelFocused();

        /// <summary>The search drawer is open.</summary>
        public bool IsSearchOpen => _searchPanel != null && _openDrawer == _searchPanel;

        /// <summary>Raised when the search drawer opens (<see cref="DashboardObjectSearch"/> refreshes and focuses).</summary>
        public event Action SearchOpened;

        private Rect _free = new Rect(0f, 0f, 1f, 1f);

        /// <summary>Depth map toggle state; raised when it changes.</summary>
        public bool DepthVisible { get; private set; }

        public event Action<bool> DepthVisibleChanged;

        public void Construct(CoreContext context)
        {
            context.Services.TryGet(out _displayLayers);
        }

        private void Start()
        {
            _root = GetComponent<UIDocument>().rootVisualElement;
            if (_root == null || _root.Q("rail") == null)
            {
                return;
            }

            _active = this;
#if UNITY_WEBGL && !UNITY_EDITOR
            // The browser page's assistant (twin mode) finds objects and moves the camera through the unified search.
            if (GetComponent<TwinAssistantBridge>() == null) gameObject.AddComponent<TwinAssistantBridge>();
#endif
            var font = Resources.Load<Font>(FontResource);
            if (font != null) _root.style.unityFontDefinition = FontDefinition.FromFont(font);

            _erectionPanel = _root.Q("erection-panel");
            _searchPanel = _root.Q("search-panel");
            _searchField = _searchPanel?.Q<TextField>("search-field");
            _legend = _root.Q("depth-legend");
            _confirm = _root.Q("exit-confirm");
            _rail = _root.Q("rail");
            _timeline = _root.Q("timeline");
            _banner = _root.Q("schedule-banner");
            _yardClock = _root.Q("yard-clock");
            _layersPanel = _root.Q("layers-panel");
            _helpPanel = _root.Q("help-panel");
            _navLayers = _root.Q<Button>("nav-layers");
            _navHelp = _root.Q<Button>("nav-help");
            _navErection = _root.Q<Button>("nav-erection");
            _navDepth = _root.Q<Button>("nav-depth");
            _navSearch = _root.Q<Button>("nav-search");
            _navWalk = _root.Q<Button>("nav-walk");
            _walkLabel = _root.Q<Label>("nav-walk-label");
            _walkIcon = _root.Q<DashboardIcon>("nav-walk-icon");
            _walkHint = _root.Q("walk-hint");
            _walkPad = _root.Q("walk-pad");
            _walkKnob = _root.Q("walk-knob");
            _walkWarning = _root.Q("walk-warning");
            _warningName = _root.Q<Label>("walk-warning-name");
            _warningRange = _root.Q<Label>("walk-warning-range");
            _warningSim = _root.Q<Label>("walk-warning-sim");

            _interactive.Clear();
            foreach (var name in new[] { "rail", "erection-panel", "search-panel", "depth-legend", "crane-panel", "top-nav",
                         "layers-panel", "help-panel", "exit-confirm", "timeline", "object-detail", "yard-clock", "walk-pad", "walk-pace", "walk-avatar-menu" })
            {
                var element = _root.Q(name);
                if (element != null) _interactive.Add(element);
            }

            Click("nav-erection", () => ToggleDrawer(_erectionPanel));
            Click("nav-search", () => ToggleDrawer(_searchPanel));
            Click("search-close", () => Open(null));
            Click("erection-close", () => Open(null));
            Click("nav-depth", () => SetDepth(!DepthVisible));
            Click("nav-walk", ToggleWalk);
            for (var i = 0; i < PaceOptions.Length; i++)
            {
                var (name, pace) = PaceOptions[i];
                _paceButtons[i] = _root.Q<Button>(name);
                if (_paceButtons[i] != null) _paceButtons[i].clicked += () => SetPace(pace);
            }
            _avatarToggle = _root.Q<Button>("walk-avatar-toggle");
            _avatarMenu = _root.Q("walk-avatar-menu");
            if (_avatarToggle != null) _avatarToggle.clicked += () => SetAvatarMenu(!_avatarMenuOpen);
            for (var i = 0; i < AvatarOptions.Length; i++)
            {
                var avatar = AvatarOptions[i].Avatar;
                _avatarButtons[i] = _root.Q<Button>(AvatarOptions[i].Button);
                if (_avatarButtons[i] != null) _avatarButtons[i].clicked += () => ChooseAvatar(avatar);
            }
            BindWalkPad();
            Click("nav-layers", () => SetUtilityPanel(_utilityPanel == _layersPanel ? null : _layersPanel));
            Click("nav-help", () => SetUtilityPanel(_utilityPanel == _helpPanel ? null : _helpPanel));
            Click("layers-close", () => SetUtilityPanel(null));
            Click("help-close", () => SetUtilityPanel(null));
            BindLayer("layer-cranes", YardDisplayLayer.Cranes, "tw-hide-cranes");
            BindLayer("layer-vehicles", YardDisplayLayer.Vehicles, "tw-hide-vehicles");
            BindLayer("layer-buildings", YardDisplayLayer.Buildings, "tw-hide-buildings");
            BindLayer("layer-hulls", YardDisplayLayer.Hulls, "tw-hide-hulls");
            BindLayer("layer-blocks", YardDisplayLayer.Blocks, "tw-hide-blocks");
            Click("exit", () =>
            {
                SetUtilityPanel(null);
                SetConfirm(true);
            });
            Click("exit-cancel", () => SetConfirm(false));
            Click("exit-confirm-button", DashboardExit.Quit);
#if UNITY_EDITOR
            var detail = _root.Q<Label>("exit-detail");
            if (detail != null) detail.text = "Play Mode를 종료합니다.";
#endif
            _root.RegisterCallback<GeometryChangedEvent>(_ => FitDrawers());
            FitDrawers();

            if (Array.IndexOf(Environment.GetCommandLineArgs(), PanelArgument) >= 0 || WebHost.Query("hull") != null)
            {
                Open(_erectionPanel, instant: true);
            }

            if (Array.IndexOf(Environment.GetCommandLineArgs(), DepthArgument) >= 0)
            {
                SetDepth(true);
            }
        }

        /// <summary>Fills the depth legend: status badge, detail text and colour bands (null bands: no scale).</summary>
        public void SetDepthLegend(string status, string detail, IReadOnlyList<(Color Color, string Label)> bands)
        {
            var root = GetComponent<UIDocument>().rootVisualElement;
            var statusLabel = root.Q<Label>("depth-status");
            if (statusLabel != null)
            {
                statusLabel.text = status;
                statusLabel.EnableInClassList("tw-status--muted", bands == null);
            }

            var detailLabel = root.Q<Label>("depth-detail");
            if (detailLabel != null) detailLabel.text = detail;
            var scale = root.Q("depth-scale");
            if (scale == null)
            {
                return;
            }

            scale.Clear();
            scale.style.display = bands == null ? DisplayStyle.None : DisplayStyle.Flex;
            if (bands == null)
            {
                return;
            }

            foreach (var (color, label) in bands)
            {
                var band = new VisualElement();
                band.AddToClassList("tw-scale-band");
                var swatch = new VisualElement();
                swatch.AddToClassList("tw-scale-swatch");
                swatch.style.backgroundColor = color;
                band.Add(swatch);
                var text = new Label(label);
                text.AddToClassList("tw-scale-label");
                band.Add(text);
                scale.Add(band);
            }

            var unit = new Label("m");
            unit.AddToClassList("tw-scale-unit");
            scale.Add(unit);
        }

        private void OnDisable()
        {
            ReleasePad();
            if (_active == this) _active = null;
        }

        private void Update()
        {
            TrackInputKind();
            if (Input.GetKeyDown(KeyCode.Escape))
            {
                if (SearchFieldFocused)
                {
                    // Typing: Escape clears the query first, then closes the search drawer (not the popup behind it).
                    var search = GetComponent<DashboardObjectSearch>();
                    if (search == null || !search.ClearOnEscape()) Open(null);
                }
                else if (_avatarMenuOpen)
                {
                    SetAvatarMenu(false);   // the character menu folds before leaving the walk
                }
                else if (IsWalking)
                {
                    Rig.SetWalking(false);   // walking: Escape goes back to the map view first (the camera does not handle it)
                }
                else
                {
                    CloseTopmost();
                }
            }

            SyncWalk();
            SyncPace();
            SyncAvatar();
            UpdateFreeArea();
            PlaceWalkPad();
            UpdateWalkWarning();
        }

        /// <summary>
        /// Free screen area from the laid-out panels: right of the rail (and of a side drawer), above the schedule bar
        /// or a bottom-sheet drawer, below the simulation banner. Never less than a third of either side.
        /// </summary>
        private void UpdateFreeArea()
        {
            var width = _root?.layout.width ?? 0f;
            var height = _root?.layout.height ?? 0f;
            if (float.IsNaN(width) || width <= 0f || height <= 0f)
            {
                return;
            }

            bool Shown(VisualElement e) => e != null && e.resolvedStyle.display == DisplayStyle.Flex && e.resolvedStyle.visibility == Visibility.Visible &&
                                           !float.IsNaN(e.layout.width) && e.layout.width > 0f;
            var portrait = _root.ClassListContains("tw-portrait");
            float left = Shown(_rail) ? _rail.layout.xMax : 0f, right = width, top = 0f, bottom = height;
            if (_openDrawer != null && Shown(_openDrawer))
            {
                if (portrait) bottom = Mathf.Min(bottom, _openDrawer.layout.yMin);
                else left = Mathf.Max(left, _openDrawer.layout.xMax);
            }

            if (Shown(_timeline)) bottom = Mathf.Min(bottom, _timeline.layout.yMin);
            if (Shown(_yardClock)) bottom = Mathf.Min(bottom, _yardClock.layout.yMin);
            var detail = _root.Q("object-detail");
            if (detail != null && detail.ClassListContains("tw-detail--open") && Shown(detail))
            {
                // The object popup (ObjectDetailView): a right-hand card, or a bottom sheet on compact screens.
                if (_root.ClassListContains("tw-compact")) bottom = Mathf.Min(bottom, detail.layout.yMin);
                else right = Mathf.Max(left + width * 0.34f, Mathf.Min(right, detail.layout.xMin));
            }
            if (Shown(_banner)) top = Mathf.Max(top, _banner.layout.yMax);
            _barsTop = bottom;   // the walk joystick sits above the bottom bars, sheets and popups
            _freeRight = right;   // and the hazard card left of the object popup
            left = Mathf.Min(left, width * 0.66f);
            bottom = Mathf.Max(bottom, top + height * 0.34f);
            _free = Rect.MinMaxRect(left / width, top / height, right / width, Mathf.Min(1f, bottom / height));
        }

        /// <summary>
        /// tw-touch (touch help text) follows the input actually used: browsers report touch support on desktops too.
        /// A touch sets it; a real mouse moving (not one simulated from a touch that just ended) clears it.
        /// </summary>
        private void TrackInputKind()
        {
            var mouse = (Vector2)Input.mousePosition;
            if (Input.touchCount > 0)
            {
                _lastTouchFrame = Time.frameCount;
                _root?.EnableInClassList("tw-touch", true);
            }
            else if (Time.frameCount - _lastTouchFrame > 2 && (mouse - _lastMouse).sqrMagnitude > 4f)
            {
                _root?.EnableInClassList("tw-touch", false);
            }

            _lastMouse = mouse;
        }

        private void Click(string name, Action action)
        {
            var button = _root.Q<Button>(name);
            if (button != null) button.clicked += action;
        }

        private void BindLayer(string name, YardDisplayLayer layer, string hiddenClass)
        {
            var toggle = _root.Q<Toggle>(name);
            if (toggle == null) return;

            toggle.EnableInClassList("tw-layer-toggle--on", toggle.value);
            toggle.RegisterValueChangedCallback(evt =>
            {
                toggle.EnableInClassList("tw-layer-toggle--on", evt.newValue);
                _displayLayers?.SetVisible(layer, evt.newValue);
                _root.EnableInClassList(hiddenClass, !evt.newValue);
                if (!evt.newValue)
                {
                    ObjectDetailView.Active?.CloseIfHidden();
                    if (layer == YardDisplayLayer.Cranes) GetComponent<CraneSafetyView>()?.Select(null);
                }
            });
        }

        private void SetUtilityPanel(VisualElement panel)
        {
            _utilityPanel?.RemoveFromClassList("tw-utility-panel--open");
            _utilityPanel = panel;
            _utilityPanel?.AddToClassList("tw-utility-panel--open");
            _root?.EnableInClassList("tw-utility-open", panel != null);   // hides the walk guide in the same corner
            if (panel != null) SetAvatarMenu(false);
            _navLayers?.EnableInClassList("tw-top-button--on", panel != null && panel == _layersPanel);
            _navHelp?.EnableInClassList("tw-top-button--on", panel != null && panel == _helpPanel);
        }

        private void ToggleDrawer(VisualElement drawer)
        {
            _suspendedDrawer = null;   // opened or closed by hand: not brought back after the simulation
            Open(_openDrawer == drawer ? null : drawer);
        }

        /// <summary>
        /// Simulation playing: the open drawer slides away so the yard and the schedule bar have the screen; when it
        /// stops (pause, end) the same drawer comes back unless one was opened or closed meanwhile.
        /// </summary>
        public void SetSimulating(bool playing)
        {
            if (playing)
            {
                if (_openDrawer != null)
                {
                    _suspendedDrawer = _openDrawer;
                    Open(null);
                }

                return;
            }

            if (_suspendedDrawer != null && _openDrawer == null)
            {
                Open(_suspendedDrawer);
            }

            _suspendedDrawer = null;
        }

        /// <summary>Opens the search drawer (command line <c>-shipyard-search</c>); the query is set by <see cref="DashboardObjectSearch"/>.</summary>
        public void OpenSearch()
        {
            _suspendedDrawer = null;
            if (!IsSearchOpen) Open(_searchPanel);
        }

        /// <summary>
        /// A search result was opened: the search drawer folds away so the scene and the detail popup are visible (the
        /// query and results stay for the next time it opens). Not brought back after a simulation.
        /// </summary>
        public void CollapseSearch()
        {
            if (!IsSearchOpen)
            {
                return;
            }

            _suspendedDrawer = null;
            Open(null);
        }

        /// <summary>Opens one drawer (closing the others) or closes all with null.</summary>
        private void Open(VisualElement drawer, bool instant = false)
        {
            var wasSearch = IsSearchOpen;
#if UNITY_WEBGL && !UNITY_EDITOR
            if (drawer != null) ShipyardWeb_CloseAssistant();   // the browser assistant shares the drawer slot
#endif
            _openDrawer = drawer;
            _root?.EnableInClassList("tw-drawer-open", drawer != null);
            foreach (var d in new[] { _erectionPanel, _searchPanel })
            {
                if (d != null) SetDrawer(d, d == drawer, instant);
            }

            _navErection?.EnableInClassList("tw-nav--active", drawer != null && drawer == _erectionPanel);
            _navSearch?.EnableInClassList("tw-nav--active", drawer != null && drawer == _searchPanel);
            if (wasSearch && !IsSearchOpen) _searchField?.Blur();   // a hidden field must not keep the keyboard
            if (IsSearchOpen && !wasSearch) SearchOpened?.Invoke();
        }

        private void SetDrawer(VisualElement drawer, bool open, bool instant)
        {
            drawer.EnableInClassList("tw-drawer--instant", instant);
            if (open)
            {
                drawer.style.visibility = Visibility.Visible;
                if (instant)
                {
                    drawer.AddToClassList("tw-drawer--open");
                }
                else
                {
                    // Next frame, so the closed state is laid out first and the slide transition runs.
                    drawer.schedule.Execute(() =>
                    {
                        if (_openDrawer == drawer) drawer.AddToClassList("tw-drawer--open");
                    });
                }

                return;
            }

            if (!drawer.ClassListContains("tw-drawer--open") && drawer.style.visibility != Visibility.Visible)
            {
                return;   // already closed
            }

            drawer.RemoveFromClassList("tw-drawer--open");
            drawer.Blur();
            if (instant)
            {
                drawer.style.visibility = Visibility.Hidden;
                return;
            }

            // Hide after the slide-out so a closed drawer cannot be clicked or focused.
            drawer.schedule.Execute(() =>
            {
                if (_openDrawer != drawer) drawer.style.visibility = Visibility.Hidden;
            }).ExecuteLater(SlideMilliseconds);
        }

        private void SetDepth(bool visible)
        {
            DepthVisible = visible;
            _navDepth?.EnableInClassList("tw-nav--active", visible);
            _legend?.EnableInClassList("tw-legend--show", visible);
            DepthVisibleChanged?.Invoke(visible);
        }

        private void SetConfirm(bool show)
        {
            _confirm?.EnableInClassList("tw-backdrop--show", show);
            if (show) _root.Q<Button>("exit-cancel")?.Focus();
        }

        private void CloseTopmost()
        {
            if (_confirm != null && _confirm.ClassListContains("tw-backdrop--show"))
            {
                SetConfirm(false);
            }
            else if (_utilityPanel != null)
            {
                SetUtilityPanel(null);
            }
            else if (ObjectDetailView.Active != null && ObjectDetailView.Active.IsOpen)
            {
                ObjectDetailView.Active.Close();   // the object popup is the topmost layer after the dialog
            }
            else if (_openDrawer != null)
            {
                Open(null);
            }
        }

        private void FitDrawers()
        {
            ApplyBrowserScale();
            var panelWidth = _root.layout.width;
            var panelHeight = _root.layout.height;
            if (float.IsNaN(panelWidth) || panelWidth <= 0 || panelHeight <= 0)
            {
                return;
            }

            var compact = panelWidth < CompactWidth || panelHeight < CompactHeight;
            var portrait = compact && panelWidth < panelHeight;
            _root.EnableInClassList("tw-compact", compact);
            _root.EnableInClassList("tw-portrait", portrait);

            // Portrait: the drawer is a sheet stretched between the rail and the right edge (USS left/right).
            var free = panelWidth - (compact ? CompactRailWidth + CompactGap * 2f : RailWidth + DrawerGap * 2f);
            var width = portrait ? new StyleLength(StyleKeyword.Null) : new StyleLength(Mathf.Max(0f, Mathf.Min(DrawerWidth, free)));
            if (_erectionPanel != null) _erectionPanel.style.width = width;
            if (_searchPanel != null) _searchPanel.style.width = width;
        }

        /// <summary>Browser only: one panel unit per CSS pixel, whatever the device pixel ratio or page zoom.</summary>
        private void ApplyBrowserScale()
        {
            if (!WebHost.IsBrowser)
            {
                return;
            }

            var settings = GetComponent<UIDocument>().panelSettings;
            if (settings == null)
            {
                return;
            }

            var ratio = WebHost.PixelRatio;
            if (settings.scaleMode != PanelScaleMode.ConstantPixelSize) settings.scaleMode = PanelScaleMode.ConstantPixelSize;
            if (Mathf.Abs(settings.scale - ratio) > 0.01f) settings.scale = ratio;
        }

        /// <summary>Camera rig (found once, again later if the scene had none yet).</summary>
        private OrbitCameraRig Rig
        {
            get
            {
                if (_rig == null && Time.frameCount % 30 == 0) _rig = FindFirstObjectByType<OrbitCameraRig>();
                return _rig;
            }
        }

#if UNITY_WEBGL && !UNITY_EDITOR
        [System.Runtime.InteropServices.DllImport("__Internal")]
        private static extern void ShipyardWeb_CloseAssistant();
#endif

        private void ToggleWalk()
        {
            if (_rig == null) _rig = FindFirstObjectByType<OrbitCameraRig>();
            if (_rig == null)
            {
                return;
            }

            var walk = !_rig.IsWalking;
            if (walk) SetUtilityPanel(null);   // the guide takes that corner
            _rig.SetWalking(walk);
            SyncWalk();
        }

        private void SetPace(WalkPace pace)
        {
            if (Rig == null && (_rig = FindFirstObjectByType<OrbitCameraRig>()) == null)
            {
                return;
            }

            _rig.SetWalkPace(pace);
            SyncPace();
        }

        /// <summary>The selected pace button follows the rig's pace (also when changed elsewhere), while walking.</summary>
        private void SyncPace()
        {
            if (!_walkingShown || _rig == null)
            {
                return;
            }

            var pace = _rig.Pace;
            if (pace == _paceShown)
            {
                return;
            }

            _paceShown = pace;
            for (var i = 0; i < PaceOptions.Length; i++)
            {
                _paceButtons[i]?.EnableInClassList("tw-pace-button--on", PaceOptions[i].Pace == pace);
            }
        }

        /// <summary>
        /// The character toggle shows only while an admin walks (<see cref="OrbitCameraRig.CanChooseWalkAvatar"/>, which
        /// may turn true late when the account answers); its text and the menu's selected option follow
        /// <see cref="OrbitCameraRig.CurrentWalkAvatar"/>.
        /// </summary>
        private void SyncAvatar()
        {
            if (_root == null)
            {
                return;
            }

            var rig = _walkingShown ? Rig : null;
            var allowed = rig != null && rig.CanChooseWalkAvatar;
            if (allowed != _avatarAllowed)
            {
                _avatarAllowed = allowed;
                _root.EnableInClassList("tw-avatar-allowed", allowed);
                if (!allowed) SetAvatarMenu(false);
                _avatarShown = null;
            }

            if (!allowed || rig.CurrentWalkAvatar == _avatarShown)
            {
                return;
            }

            var current = rig.CurrentWalkAvatar;
            _avatarShown = current;
            for (var i = 0; i < AvatarOptions.Length; i++)
            {
                var on = AvatarOptions[i].Avatar == current;
                _avatarButtons[i]?.EnableInClassList("tw-avatar-option--on", on);
                if (on && _avatarToggle != null) _avatarToggle.text = AvatarOptions[i].Label;
            }
        }

        private void SetAvatarMenu(bool open)
        {
            open &= _avatarAllowed;
            if (open) SetUtilityPanel(null);   // same corner: the panels fold (and the walk guide hides, below)
            _avatarMenuOpen = open;
            _avatarMenu?.EnableInClassList("tw-avatar-menu--open", open);
            _avatarToggle?.EnableInClassList("tw-avatar-toggle--open", open);
            _root?.EnableInClassList("tw-avatar-open", open);
        }

        private void ChooseAvatar(WalkAvatar avatar)
        {
            var rig = Rig;
            if (rig != null && rig.CanChooseWalkAvatar) rig.SetWalkAvatar(avatar);
            _avatarShown = null;
            SyncAvatar();
            SetAvatarMenu(false);
        }

        /// <summary>
        /// Button, guide and joystick follow the camera's mode, whoever changed it (this button, Escape, a search
        /// result focusing the camera, the camera itself).
        /// </summary>
        private void SyncWalk()
        {
            var walking = IsWalking;
            if (walking == _walkingShown || _root == null)
            {
                return;
            }

            _walkingShown = walking;
            _root.EnableInClassList("tw-walking", walking);
            _navWalk?.EnableInClassList("tw-walk-button--on", walking);
            if (_navWalk != null) _navWalk.tooltip = walking ? "지도 시점으로 돌아가기 (Esc)" : "3인칭으로 야드 걸어보기";
            if (_walkLabel != null) _walkLabel.text = walking ? "지도 시점" : "걸어보기";
            if (_walkIcon != null) _walkIcon.Glyph = walking ? "map" : "walk";

            _walkHintFade?.Pause();
            _walkHint?.EnableInClassList("tw-walk-hint--show", walking);
            if (walking && _walkHint != null)
            {
                // The guide shows on entry and fades out; the help panel keeps the same keys.
                _walkHintFade = _walkHint.schedule.Execute(() => _walkHint.RemoveFromClassList("tw-walk-hint--show"));
                _walkHintFade.ExecuteLater(WalkHintMilliseconds);
            }

            if (!walking)
            {
                ReleasePad();
                SetAvatarMenu(false);
            }
        }

        private void BindWalkPad()
        {
            if (_walkPad == null)
            {
                return;
            }

            _walkPad.focusable = false;
            _walkPad.RegisterCallback<PointerDownEvent>(e =>
            {
                if (_padPointer >= 0 || !IsWalking)
                {
                    return;
                }

                _padPointer = e.pointerId;
                _walkPad.CapturePointer(e.pointerId);
                _walkPad.AddToClassList("tw-walk-pad--held");
                MovePad(e.localPosition);
                e.StopPropagation();
            });
            _walkPad.RegisterCallback<PointerMoveEvent>(e =>
            {
                if (e.pointerId != _padPointer) return;
                MovePad(e.localPosition);
                e.StopPropagation();
            });
            _walkPad.RegisterCallback<PointerUpEvent>(e =>
            {
                if (e.pointerId == _padPointer) ReleasePad();
            });
            _walkPad.RegisterCallback<PointerCancelEvent>(e =>
            {
                if (e.pointerId == _padPointer) ReleasePad();
            });
            _walkPad.RegisterCallback<PointerCaptureOutEvent>(_ => ReleasePad());
        }

        /// <summary>Joystick at a pad-local point: knob follows (within the ring), input x = right, y = forward.</summary>
        private void MovePad(Vector2 local)
        {
            var size = _walkPad.layout.size;
            var radius = Mathf.Max(1f, Mathf.Min(size.x, size.y) / 2f - 14f);
            var offset = Vector2.ClampMagnitude(local - size / 2f, radius);
            if (_walkKnob != null) _walkKnob.style.translate = new Translate(offset.x, offset.y);
            var input = offset / radius;
            input = input.magnitude < PadDeadZone ? Vector2.zero : new Vector2(input.x, -input.y);   // UI y is down
            if (_rig != null) _rig.SetWalkInput(input);
        }

        /// <summary>Finger lifted, cancelled or walk mode left: knob back to centre and no movement.</summary>
        private void ReleasePad()
        {
            var pointer = _padPointer;
            _padPointer = -1;
            if (pointer >= 0 && _walkPad != null && _walkPad.HasPointerCapture(pointer)) _walkPad.ReleasePointer(pointer);
            _walkPad?.RemoveFromClassList("tw-walk-pad--held");
            if (_walkKnob != null) _walkKnob.style.translate = new Translate(0f, 0f);
            if ((pointer >= 0 || _walkingShown) && _rig != null) _rig.SetWalkInput(Vector2.zero);
        }

        /// <summary>Joystick above whatever covers the bottom of the screen (schedule bars, sheets, the compact popup).</summary>
        private void PlaceWalkPad()
        {
            if (_walkPad == null || !_walkingShown || float.IsNaN(_barsTop))
            {
                return;
            }

            var height = _root.layout.height;
            var bottom = Mathf.Max(_root.ClassListContains("tw-compact") ? 12f : 24f, height - _barsTop + 12f);
            if (Mathf.Abs(_walkPad.resolvedStyle.bottom - bottom) > 0.5f) _walkPad.style.bottom = bottom;
        }

        /// <summary>
        /// Hazard card while walking inside a simulated zone: shown and filled the same frame the monitor reports it,
        /// hidden the frame it is left (or walking ends). Text is rebuilt only when it changes.
        /// </summary>
        private void UpdateWalkWarning()
        {
            if (_walkWarning == null)
            {
                return;
            }

            var hazards = IsWalking ? Rig.WalkHazards : null;
            var show = hazards != null && hazards.Count > 0;
            if (show)
            {
                var count = hazards.Count;
                var name = string.IsNullOrEmpty(hazards.Name) ? "장비" : hazards.Name;
                var range = hazards.RangeText ?? string.Empty;
                var estimated = hazards.IsEstimated;
                var key = (name, range, count, estimated);
                if (key != _warningKey)
                {
                    _warningKey = key;
                    if (_warningName != null) _warningName.text = WarningName(name, count);
                    if (_warningRange != null)
                    {
                        _warningRange.text = range;
                        _warningRange.style.display = range.Length > 0 ? DisplayStyle.Flex : DisplayStyle.None;
                    }

                    if (_warningSim != null) _warningSim.text = estimated ? "시뮬레이션 · 추정" : "시뮬레이션";
                }

                PlaceWalkWarning();
            }

            if (show != _warningShown)
            {
                _warningShown = show;
                _walkWarning.EnableInClassList("tw-walk-warning--show", show);
                if (!show) _warningKey = default;
            }
        }

        /// <summary>Equipment line: the nearest zone's equipment and how many other zones overlap here.</summary>
        public static string WarningName(string name, int count) => count > 1 ? $"{name} 외 {count - 1}건" : name;

        /// <summary>
        /// Above the bottom bars (and sheets, compact popup), right of the rail and the joystick, left of the desktop
        /// popup; on a narrow screen it stacks over the joystick across the free width. Clear of the top bar, the guide
        /// and the help panel in the top-right corner.
        /// </summary>
        private void PlaceWalkWarning()
        {
            var width = _root.layout.width;
            var height = _root.layout.height;
            if (float.IsNaN(width) || width <= 0f || float.IsNaN(_barsTop))
            {
                return;
            }

            var compact = _root.ClassListContains("tw-compact");
            var margin = compact ? 8f : 16f;
            var bottom = Mathf.Max(compact ? 12f : 24f, height - _barsTop + 12f);
            var left = (_rail != null && !float.IsNaN(_rail.layout.xMax) ? _rail.layout.xMax : 0f) + margin;
            var right = (float.IsNaN(_freeRight) ? width : _freeRight) - margin;
            var padShown = _walkPad != null && _walkPad.resolvedStyle.display == DisplayStyle.Flex && !float.IsNaN(_walkPad.layout.width);
            if (padShown)
            {
                if (right - (_walkPad.layout.xMax + 12f) >= 240f)
                {
                    left = _walkPad.layout.xMax + 12f;   // same row, beside the joystick
                }
                else
                {
                    bottom += _walkPad.layout.height + 10f;   // narrow: on top of the joystick
                }
            }

            var cardWidth = Mathf.Max(160f, Mathf.Min(compact ? 300f : 340f, right - left));
            if (!padShown) left = Mathf.Max(left, (left + right - cardWidth) / 2f);   // centred in the free row
            if (Moved(_walkWarning.style.left, left)) _walkWarning.style.left = left;
            if (Moved(_walkWarning.style.bottom, bottom)) _walkWarning.style.bottom = bottom;
            if (Moved(_walkWarning.style.width, cardWidth)) _walkWarning.style.width = cardWidth;
        }

        private static bool Moved(StyleLength current, float value) =>
            current.keyword != StyleKeyword.Undefined || Mathf.Abs(current.value.value - value) > 0.5f;

        private bool IsSearchPanelFocused()
        {
            if (_searchPanel == null || !IsSearchOpen || _searchPanel.panel == null)
            {
                return false;
            }

            var focused = _searchPanel.panel.focusController?.focusedElement as VisualElement;
            return focused != null && (focused == _searchPanel || _searchPanel.Contains(focused));
        }

        private bool IsSearchFieldFocused()
        {
            if (_searchField == null || !IsSearchOpen || _searchField.panel == null)
            {
                return false;
            }

            // The text input inside the TextField holds focus while typing; a focused result card does not count.
            var focused = _searchField.panel.focusController?.focusedElement as VisualElement;
            return focused != null && (focused == _searchField || _searchField.Contains(focused));
        }

        private bool IsPointerOverControls() =>
            IsOverControls(Input.touchCount > 0 ? Input.GetTouch(0).position : (Vector2)Input.mousePosition);

        /// <summary>Unity screen point (origin bottom left) over one of the dashboard controls in <see cref="_interactive"/>.</summary>
        private bool IsOverControls(Vector2 screen)
        {
            var panel = _root?.panel;
            if (panel == null)
            {
                return false;
            }

            var position = RuntimePanelUtils.ScreenToPanel(panel, new Vector2(screen.x, Screen.height - screen.y));
            for (var picked = panel.Pick(position); picked != null; picked = picked.parent)
            {
                if (_interactive.Contains(picked))
                {
                    return true;
                }
            }

            return false;
        }
    }
}
