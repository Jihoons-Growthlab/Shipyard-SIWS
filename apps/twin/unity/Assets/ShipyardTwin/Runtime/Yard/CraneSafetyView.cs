using System;
using System.Collections.Generic;
using System.Globalization;
using GrowthLab.Core;
using GrowthLab.UI;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.UIElements;

namespace ShipyardTwin
{
    /// <summary>
    /// Click (or tap) a rail jib crane to see where it is dangerous and how much it may lift where. On the ground around
    /// the crane's slewing centre: the danger zone (largest hook radius + ERP danger margin, red), the largest and
    /// smallest hook radius, one ring per ERP load chart point coloured by its rating (green full rating → amber → red),
    /// and a bar out to the hook now, red when the load on the hook is over the chart rating at that radius. The panel
    /// (Dashboard.uxml "crane-panel") lists the same numbers and the chart. The rings follow the crane along its rail.
    /// Data: crane_kinematics (boom, danger_margin_m) and crane_load_charts from the ERP (<see cref="RailCraneView"/>).
    /// A click elsewhere, the close button or Esc clears the selection. Command line <c>-shipyard-crane ID</c> selects
    /// a crane at start (screenshots). Per frame: a few transforms; the panel text refreshes 5 times a second.
    /// </summary>
    [RequireComponent(typeof(UIDocument))]
    public sealed class CraneSafetyView : MonoBehaviour, ICoreConsumer
    {
        public const string CraneArgument = "-shipyard-crane";
        private const float ClickSeconds = 0.4f, ClickSlopPixels = 6f, Lift = 0.35f, PanelSeconds = 0.2f;
        private const int Segments = 160;

        private static readonly Color Danger = new Color(0.92f, 0.22f, 0.18f);
        private static readonly Color Reach = new Color(1f, 0.62f, 0.12f);
        private static readonly Color Near = new Color(0.62f, 0.66f, 0.72f);
        private static readonly Color Full = new Color(0.25f, 0.75f, 0.45f);
        private static readonly Color Half = new Color(0.95f, 0.75f, 0.2f);
        private static readonly Color Low = new Color(0.9f, 0.35f, 0.2f);

        private ShipPalette _palette;
        private RailCraneView _view;
        private RailCraneView.Crane _selected;
        private string _selectedId, _pending;
        private Transform _overlay, _bar, _barTip;
        private MeshRenderer _barRenderer, _tipRenderer;
        private readonly Dictionary<Color, Material> _materials = new Dictionary<Color, Material>();
        private readonly List<VisualElement> _tracked = new List<VisualElement>();
        private WorldLabels _labels;

        private VisualElement _root, _panel, _chart, _ratedTile, _loadTile;
        private Label _title, _sub, _danger, _radius, _rated, _load, _loadLabel, _state, _note;
        private float _nextPanel;
        private Vector2 _pressAt;
        private float _pressTime;
        private bool _pressValid;

        public RailCraneView.Crane Selected => _selected;

        public void Construct(CoreContext context)
        {
            context.Services.TryGet(out _palette);
        }

        private void Start()
        {
            _root = GetComponent<UIDocument>().rootVisualElement;
            _panel = _root?.Q("crane-panel");
            _labels = GetComponent<WorldLabels>();
            if (_panel != null)
            {
                _title = _panel.Q<Label>("crane-title");
                _sub = _panel.Q<Label>("crane-sub");
                _danger = _panel.Q<Label>("crane-danger");
                _radius = _panel.Q<Label>("crane-radius");
                _rated = _panel.Q<Label>("crane-rated");
                _load = _panel.Q<Label>("crane-load");
                _loadLabel = _panel.Q<Label>("crane-load-label");
                _state = _panel.Q<Label>("crane-state");
                _note = _panel.Q<Label>("crane-note");
                _chart = _panel.Q("crane-chart");
                _ratedTile = _panel.Q("crane-rated-tile");
                _loadTile = _panel.Q("crane-load-tile");
                var close = _panel.Q<Button>("crane-close");
                if (close != null) close.clicked += () => Select(null);
            }

            var args = Environment.GetCommandLineArgs();
            var index = Array.IndexOf(args, CraneArgument);
            _pending = index >= 0 && index + 1 < args.Length ? args[index + 1] : WebHost.Query("crane");
        }

        private void OnDestroy()
        {
            if (_view != null) _view.Rebuilt -= OnRebuilt;
            if (_overlay != null) Destroy(_overlay.gameObject);
            foreach (var material in _materials.Values) Destroy(material);
        }

        private void Update()
        {
            if (_view == null)
            {
                _view = FindFirstObjectByType<RailCraneView>();
                if (_view == null) return;
                _view.Rebuilt += OnRebuilt;
            }

            if (_pending != null && _view.Count > 0)
            {
                Select(FindCrane(_pending));
                _pending = null;
            }

            HandleInput();
        }

        private void LateUpdate()
        {
            if (_selected == null || _selected.Root == null)
            {
                return;
            }

            Follow();
            if (Time.unscaledTime >= _nextPanel)
            {
                _nextPanel = Time.unscaledTime + PanelSeconds;
                RefreshPanel();
            }
        }

        private void OnRebuilt()
        {
            // New ERP data rebuilt the cranes: keep the same crane selected (its rings rebuilt from the new rows).
            var id = _selectedId;
            Select(null);
            if (id != null) Select(FindCrane(id));
        }

        private RailCraneView.Crane FindCrane(string id)
        {
            if (_view == null || string.IsNullOrEmpty(id)) return null;
            foreach (var crane in _view.Cranes)
            {
                if (crane.Row.equipment == id) return crane;
            }

            return null;
        }

        /// <summary>A click is a short left press that hardly moves and does not start on the dashboard; a tap likewise.</summary>
        private void HandleInput()
        {
            if (Input.GetKeyDown(KeyCode.Escape) && _selected != null)
            {
                Select(null);
                return;
            }

            Vector2? click = null;
            if (Input.touchCount > 0)
            {
                var touch = Input.GetTouch(0);
                if (touch.phase == TouchPhase.Began) Press(touch.position, Input.touchCount == 1);
                if (Input.touchCount > 1 || (touch.position - _pressAt).magnitude > ClickSlopPixels) _pressValid = false;
                if (touch.phase == TouchPhase.Ended && Released()) click = touch.position;
            }
            else
            {
                var mouse = (Vector2)Input.mousePosition;
                if (Input.GetMouseButtonDown(0)) Press(mouse, true);
                if (Input.GetMouseButton(0) && (mouse - _pressAt).magnitude > ClickSlopPixels) _pressValid = false;
                if (Input.GetMouseButtonUp(0) && Released()) click = mouse;
            }

            if (click == null)
            {
                return;
            }

            var crane = _view.Find(HoverTooltipView.Pick(Camera.main, click.Value));
            if (crane != null || _selected != null) Select(crane == _selected ? _selected : crane);
        }

        private void Press(Vector2 at, bool valid)
        {
            _pressAt = at;
            _pressTime = Time.unscaledTime;
            _pressValid = valid && !DashboardShellView.PointerOverUi;
        }

        private bool Released()
        {
            var ok = _pressValid && Time.unscaledTime - _pressTime <= ClickSeconds;
            _pressValid = false;
            return ok;
        }

        public void Select(RailCraneView.Crane crane)
        {
            if (crane != null && !crane.Root.gameObject.activeInHierarchy) crane = null;
            if (crane == _selected && crane != null)
            {
                return;
            }

            ClearOverlay();
            _selected = crane;
            _selectedId = crane?.Row.equipment;
            _panel?.EnableInClassList("tw-crane--show", crane != null);
            if (crane == null)
            {
                return;
            }

            BuildOverlay(crane);
            Follow();
            BuildChart(crane);
            _nextPanel = 0f;
            RefreshPanel();
        }

        // ---------- ground rings ----------

        private void BuildOverlay(RailCraneView.Crane crane)
        {
            _overlay = new GameObject("CraneSafety " + crane.Row.equipment).transform;
            var k = crane.Boom;
            var max = RailCraneMotion.MaxReach(k);
            var min = RailCraneMotion.MinReach(k);
            var danger = RailCraneMotion.DangerRadius(k);
            Ring("Danger", danger, 1.8f, Danger);
            Ring("DangerInner", danger - 1.6f, 0.35f, Danger);
            Ring("MaxReach", max, 0.9f, Reach);
            Ring("MinReach", min, 0.5f, Near);
            Label(danger, "위험반경 " + Metres(danger), "tw-crane-label tw-crane-label--danger", 0.94f);

            var chart = crane.Chart;
            var labelled = float.NegativeInfinity;
            var top = 0f;
            foreach (var point in chart) top = Mathf.Max(top, point.capacity_t);
            for (var i = 0; i < chart.Length; i++)
            {
                var point = chart[i];
                if (point.radius_m >= max - 0.2f && i == chart.Length - 1)
                {
                    Label(point.radius_m, Metres(point.radius_m) + " · " + Tonnes(point.capacity_t), "tw-crane-label", 1f);
                    continue;   // drawn as the largest-radius ring already
                }

                Ring("Chart " + point.radius_m.ToString("0.#", CultureInfo.InvariantCulture), point.radius_m, 0.45f,
                    RatingColour(top > 0f ? point.capacity_t / top : 0f));
                // Label the edge of the full rating, every 10 m beyond it and the last point (not every ring).
                var falling = i + 1 < chart.Length && chart[i + 1].capacity_t < point.capacity_t;
                var round10 = Mathf.Abs(point.radius_m / 10f - Mathf.Round(point.radius_m / 10f)) < 0.01f;
                var wanted = (falling && Mathf.Approximately(point.capacity_t, top)) || (round10 && point.capacity_t < top) || i == chart.Length - 1;
                if (wanted && point.radius_m - labelled >= 8f)
                {
                    labelled = point.radius_m;
                    Label(point.radius_m, Metres(point.radius_m) + " · " + Tonnes(point.capacity_t), "tw-crane-label", 1f);
                }
            }

            _bar = Part("HookRadius", Vector3.one, Full).transform;
            _barRenderer = _bar.GetComponent<MeshRenderer>();
            _barTip = Ring("HookRing", 2.2f, 0.7f, Full).transform;
            _tipRenderer = _barTip.GetComponent<MeshRenderer>();
            _barTip.SetParent(_overlay, false);
        }

        private void ClearOverlay()
        {
            foreach (var element in _tracked) _labels?.Untrack(element);
            _tracked.Clear();
            if (_overlay != null) Destroy(_overlay.gameObject);
            _overlay = _bar = _barTip = null;
        }

        /// <summary>Overlay on the slewing centre at quay level, x along the jib direction; the hook bar along the boom.</summary>
        private void Follow()
        {
            var crane = _selected;
            var centre = crane.Root.position;
            _overlay.SetPositionAndRotation(new Vector3(centre.x, centre.y + Lift, centre.z),
                ShipCoordinates.HeadingToUnity(crane.Row.jib_heading_deg));
            var hook = crane.Hook.position;
            var flat = new Vector3(hook.x - centre.x, 0f, hook.z - centre.z);
            var radius = flat.magnitude;
            var colour = HookColour(crane, radius);
            _bar.gameObject.SetActive(radius > 0.5f);
            if (radius > 0.5f)
            {
                _bar.SetPositionAndRotation(new Vector3(centre.x, centre.y + Lift + 0.05f, centre.z) + flat / 2f,
                    Quaternion.LookRotation(flat.normalized, Vector3.up));
                _bar.localScale = new Vector3(0.9f, 0.12f, radius);
            }

            _barTip.position = new Vector3(hook.x, centre.y + Lift + 0.08f, hook.z);
            _barRenderer.sharedMaterial = Material(colour);
            _tipRenderer.sharedMaterial = Material(colour);
        }

        private static Color HookColour(RailCraneView.Crane crane, float radius)
        {
            if (radius > RailCraneMotion.MaxReach(crane.Boom) + 0.5f) return Danger;
            var rated = RailCraneMotion.RatedAt(crane.Chart, radius);
            if (crane.LoadT <= 0f || rated == null) return Full;
            if (crane.LoadT > rated.Value + 1e-3f) return Danger;
            return crane.LoadT > rated.Value * 0.9f ? Half : Full;
        }

        private static Color RatingColour(float share) =>
            share >= 0.999f ? Full : share >= 0.5f ? Color.Lerp(Half, Full, (share - 0.5f) / 0.5f) : Color.Lerp(Low, Half, share / 0.5f);

        private GameObject Ring(string name, float radius, float width, Color colour)
        {
            var go = new GameObject(name);
            go.transform.SetParent(_overlay, false);
            go.AddComponent<MeshFilter>().sharedMesh = RingMesh(Mathf.Max(0f, radius - width / 2f), radius + width / 2f);
            var renderer = go.AddComponent<MeshRenderer>();
            renderer.sharedMaterial = Material(colour);
            renderer.shadowCastingMode = ShadowCastingMode.Off;
            renderer.receiveShadows = false;
            return go;
        }

        private GameObject Part(string name, Vector3 size, Color colour)
        {
            var go = new GameObject(name);
            go.transform.SetParent(_overlay, false);
            go.AddComponent<MeshFilter>().sharedMesh = MetricBox.Get(size);
            var renderer = go.AddComponent<MeshRenderer>();
            renderer.sharedMaterial = Material(colour);
            renderer.shadowCastingMode = ShadowCastingMode.Off;
            return go;
        }

        /// <summary>Flat annulus in the x/z plane facing up (Unity frame), inner..outer radius in metres.</summary>
        public static Mesh RingMesh(float inner, float outer)
        {
            var vertices = new Vector3[(Segments + 1) * 2];
            var normals = new Vector3[vertices.Length];
            var uv = new Vector2[vertices.Length];
            var triangles = new int[Segments * 6];
            for (var i = 0; i <= Segments; i++)
            {
                var a = 2f * Mathf.PI * i / Segments;
                var d = new Vector3(Mathf.Cos(a), 0f, Mathf.Sin(a));
                vertices[2 * i] = d * inner;
                vertices[2 * i + 1] = d * outer;
                normals[2 * i] = normals[2 * i + 1] = Vector3.up;
                uv[2 * i] = new Vector2(i / (float)Segments, 0f);
                uv[2 * i + 1] = new Vector2(i / (float)Segments, 1f);
                if (i == Segments) break;
                var t = i * 6;
                // Wound clockwise seen from above so the ring faces up.
                triangles[t] = 2 * i; triangles[t + 1] = 2 * i + 2; triangles[t + 2] = 2 * i + 1;
                triangles[t + 3] = 2 * i + 1; triangles[t + 4] = 2 * i + 2; triangles[t + 5] = 2 * i + 3;
            }

            var mesh = new Mesh { name = "Ring", vertices = vertices, normals = normals, uv = uv, triangles = triangles };
            mesh.RecalculateBounds();
            return mesh;
        }

        private Material Material(Color colour)
        {
            if (_materials.TryGetValue(colour, out var material)) return material;
            material = _palette != null ? _palette.Create("crane safety") : new Material(Shader.Find("Universal Render Pipeline/Lit"));
            material.color = colour;
            material.SetColor("_BaseColor", colour);
            material.SetColor("_Color", colour);
            material.SetFloat("_Smoothness", 0.1f);
            material.SetFloat("_Glossiness", 0.1f);
            material.EnableKeyword("_EMISSION");
            material.SetColor("_EmissionColor", colour * 0.55f);   // readable in shade and at dusk
            _materials[colour] = material;
            return material;
        }

        private void Label(float radius, string text, string classes, float lift)
        {
            if (_labels == null) return;
            var anchor = new GameObject("Label").transform;
            anchor.SetParent(_overlay, false);
            anchor.localPosition = new Vector3(radius, lift, 0f);
            var label = new Label(text);
            foreach (var name in classes.Split(' ')) label.AddToClassList(name);
            _tracked.Add(_labels.Track(anchor, label, Vector3.zero));
        }

        // ---------- panel ----------

        private void BuildChart(RailCraneView.Crane crane)
        {
            if (_chart == null) return;
            _chart.Clear();
            var title = _panel.Q("crane-chart-title");
            if (title != null) title.style.display = crane.Chart.Length > 0 ? DisplayStyle.Flex : DisplayStyle.None;
            var top = 0f;
            foreach (var point in crane.Chart) top = Mathf.Max(top, point.capacity_t);
            foreach (var point in crane.Chart)
            {
                var row = new VisualElement();
                row.AddToClassList("tw-chart-row");
                row.userData = point;
                var radius = new Label(Metres(point.radius_m));
                radius.AddToClassList("tw-chart-radius");
                var track = new VisualElement();
                track.AddToClassList("tw-chart-track");
                var bar = new VisualElement();
                bar.AddToClassList("tw-chart-bar");
                var share = top > 0f ? point.capacity_t / top : 0f;
                bar.style.width = Length.Percent(100f * share);
                bar.style.backgroundColor = RatingColour(share);
                track.Add(bar);
                var value = new Label(Tonnes(point.capacity_t));
                value.AddToClassList("tw-chart-value");
                row.Add(radius);
                row.Add(track);
                row.Add(value);
                _chart.Add(row);
            }
        }

        private void RefreshPanel()
        {
            var crane = _selected;
            if (crane == null || _panel == null) return;
            var row = crane.Row;
            var k = crane.Boom;
            var radius = crane.Radius;
            var rated = RailCraneMotion.RatedAt(crane.Chart, radius);
            var max = RailCraneMotion.MaxReach(k);
            Set(_title, string.IsNullOrEmpty(row.name) ? row.equipment : row.name);
            Set(_sub, (row.equipment_type ?? "크레인") + (row.capacity_t > 0 ? " · 정격 " + Tonnes(row.capacity_t) : string.Empty) +
                      " · ERP " + row.equipment + (string.IsNullOrEmpty(row.station) ? string.Empty : " · " + row.station));
            Set(_danger, Metres(RailCraneMotion.DangerRadius(k)));
            Set(_radius, Metres(radius));
            Set(_rated, rated == null ? (row.capacity_t > 0 ? Tonnes(row.capacity_t) : "-") : rated.Value > 0f ? Tonnes(rated.Value) : "범위 밖");
            var loaded = crane.LoadT > 0f;
            Set(_load, loaded ? Tonnes(crane.LoadT) : "무부하");
            var limit = rated ?? (row.capacity_t > 0 ? row.capacity_t : (float?)null);
            var over = loaded && limit != null && crane.LoadT > limit.Value + 1e-3f;
            var near = loaded && !over && limit != null && crane.LoadT > limit.Value * 0.9f;
            Set(_loadLabel, loaded && limit > 0f ? "인양 중량 · 정격 대비 " + (100f * crane.LoadT / limit.Value).ToString("0") + "%" : "인양 중량");
            _loadTile?.EnableInClassList("tw-stat--over", over);
            _loadTile?.EnableInClassList("tw-stat--near", near);
            _ratedTile?.EnableInClassList("tw-stat--over", over);
            Set(_state, (over ? "⚠ 정격하중 초과 · " : near ? "정격의 90% 이상 · " : string.Empty) + (crane.Work ?? string.Empty));
            Set(_note, "위험반경 = 최대 작업반경 " + Metres(max) + " + 여유 " + Metres(k.danger_margin_m) + " (붐 " + Metres(k.boom_m) +
                       ", 최소 반경 " + Metres(RailCraneMotion.MinReach(k)) + "). " +
                       (crane.Chart.Length == 0
                           ? "ERP 정격하중표가 없어 장비 정격하중만 적용합니다(형상·배치 → 크레인 정격하중표)."
                           : "반경 사이는 더 먼 반경의 정격을 적용합니다."));
            if (_chart != null)
            {
                // Highlight the chart point that rates the present radius.
                CraneLoadChartRow now = null;
                foreach (var point in crane.Chart)
                {
                    if (radius <= point.radius_m + 1e-3f) { now = point; break; }
                }

                foreach (var child in _chart.Children())
                {
                    child.EnableInClassList("tw-chart-row--now", child.userData == now && now != null);
                }
            }
        }

        private static void Set(Label label, string text)
        {
            if (label != null && label.text != text) label.text = text;
        }

        private static string Metres(float m) => m.ToString("0.#", CultureInfo.InvariantCulture) + " m";

        private static string Tonnes(float t) => t.ToString("0.#", CultureInfo.InvariantCulture) + " t";
    }
}
