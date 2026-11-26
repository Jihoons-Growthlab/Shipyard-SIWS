using System;
using System.Collections.Generic;
using System.Text;
using UnityEngine;
using UnityEngine.UIElements;

namespace ShipyardTwin
{
    /// <summary>
    /// 통합검색 in the left drawer ("search-panel", rail button "nav-search"; Dashboard.uxml). Typing a name, a hull number
    /// (SN…) or an ID lists the related objects as icon cards, live as you type (short debounce): category glyph, name with
    /// the match marked, ID and kind, and a "화면에 없음" chip only for objects not on screen. The whole card is the control
    /// (click, tap, Enter or Space; no separate button): it asks <see cref="YardObjectSearch.Open"/> to frame it with the camera and open the existing detail
    /// popup, then folds the drawer away so the scene and the popup are visible. Data and camera are
    /// <see cref="YardObjectSearch"/>'s (model: <see cref="ShipView.Model"/>); this view never changes the date or playback.
    /// An empty query shows a short guide instead of cards; no match shows "검색 결과 없음". While results are open they
    /// are re-queried every <see cref="RefreshSeconds"/> (objects come and go with playback) and updated in place, so
    /// keyboard focus, scroll position and a touch in progress are kept. Keys: Enter opens the first card that can be
    /// shown, ↓ moves into the list, Escape clears the query (then closes; <see cref="DashboardShellView"/>).
    /// Command line <c>-shipyard-search &lt;query&gt;</c> opens the drawer with that query once the yard is built (screenshots).
    /// </summary>
    [RequireComponent(typeof(UIDocument))]
    public sealed class DashboardObjectSearch : MonoBehaviour
    {
        public const string SearchArgument = "-shipyard-search";
        public const int Limit = 40;
        public const float RefreshSeconds = 2.5f;

        /// <summary>Card subtitles are cut to about two lines (the full text is in the detail popup).</summary>
        public const int SubtitleChars = 60;
        private const long DebounceMilliseconds = 90;

        /// <summary>Chip of a card whose object is not on screen now; cards that can be shown have no chip.</summary>
        public const string OffScreenText = "화면에 없음";
        private const float TapSlopPixels = 10f;

        private VisualElement _root, _panel, _results, _empty, _clear;
        private ScrollView _scroll;
        private TextField _field;
        private Label _placeholder, _status, _emptyTitle, _emptyText;
        private DashboardIcon _emptyIcon;
        private DashboardShellView _shell;
        private ShipView _ship;
        private readonly List<Card> _cards = new List<Card>();
        private IVisualElementScheduledItem _pendingSearch;
        private string _query = string.Empty;
        private float _nextRefresh;
        private bool _pressing;
        private string _notice;
        private string _startQuery;

        private sealed class Card
        {
            public VisualElement Root, Tile, Chip;
            public DashboardIcon Icon;
            public Label Title, Meta, Subtitle, ChipText;
            public YardSearchResult Result;
            public bool HasResult;
            public Vector2 PressAt;
            public int PointerId = -1;
            public string CategoryClass;
        }

        private void Start()
        {
            _root = GetComponent<UIDocument>().rootVisualElement;
            _panel = _root?.Q("search-panel");
            if (_panel == null)
            {
                return;
            }

            _shell = GetComponent<DashboardShellView>();
            _field = _panel.Q<TextField>("search-field");
            _placeholder = _panel.Q<Label>("search-placeholder");
            _clear = _panel.Q<Button>("search-clear");
            _status = _panel.Q<Label>("search-status");
            _scroll = _panel.Q<ScrollView>("search-scroll");
            _results = _panel.Q("search-results");
            _empty = _panel.Q("search-empty");
            _emptyIcon = _panel.Q<DashboardIcon>("search-empty-icon");
            _emptyTitle = _panel.Q<Label>("search-empty-title");
            _emptyText = _panel.Q<Label>("search-empty-text");
            if (_field == null || _results == null)
            {
                return;
            }

            _field.isDelayed = false;
            _field.RegisterValueChangedCallback(evt => OnQueryTyped(evt.newValue));
            _field.RegisterCallback<KeyDownEvent>(OnFieldKey, TrickleDown.TrickleDown);
            var box = _panel.Q("search-box");
            _field.RegisterCallback<FocusInEvent>(_ => box?.AddToClassList("tw-search-box--focus"));
            _field.RegisterCallback<FocusOutEvent>(_ => box?.RemoveFromClassList("tw-search-box--focus"));
            if (_clear is Button clear) clear.clicked += ClearQuery;
            _scroll?.RegisterCallback<PointerDownEvent>(_ => _pressing = true, TrickleDown.TrickleDown);
            _scroll?.RegisterCallback<PointerUpEvent>(_ => _pressing = false, TrickleDown.TrickleDown);
            _scroll?.RegisterCallback<PointerCancelEvent>(_ => _pressing = false, TrickleDown.TrickleDown);
            _scroll?.RegisterCallback<PointerLeaveEvent>(_ => _pressing = false);
            if (_shell != null) _shell.SearchOpened += OnOpened;
            Render(Array.Empty<YardSearchResult>());
            var args = Environment.GetCommandLineArgs();
            var at = Array.IndexOf(args, SearchArgument);
            if (at >= 0 && at + 1 < args.Length) _startQuery = args[at + 1];
        }

        private void OnDestroy()
        {
            if (_shell != null) _shell.SearchOpened -= OnOpened;
        }

        private void Update()
        {
            if (_startQuery != null && _shell != null && _field != null && Time.frameCount > 90)
            {
                var query = _startQuery;
                _startQuery = null;
                _field.SetValueWithoutNotify(query);
                UpdateFieldChrome(query);
                _shell.OpenSearch();
                Search(keepNotice: false);
            }

            if (_panel == null || _shell == null || !_shell.IsSearchOpen || _query.Length == 0)
            {
                return;
            }

            // Low-frequency refresh of open results (blocks erected, vehicles moving); never during a press or drag.
            if (Time.unscaledTime >= _nextRefresh && !_pressing)
            {
                Search(keepNotice: true);
            }
        }

        // ------------------------------------------------------------------ query

        private void OnOpened()
        {
            Search(keepNotice: false);
            if (!_root.ClassListContains("tw-touch"))
            {
                // Desktop: type at once. Touch (tw-touch, DashboardShellView): no keyboard until the field is tapped.
                _field.schedule.Execute(() => _field.Focus());
            }
        }

        private void OnQueryTyped(string value)
        {
            UpdateFieldChrome(value);
            _pendingSearch?.Pause();
            _pendingSearch = _field.schedule.Execute(() => Search(keepNotice: false)).StartingIn(DebounceMilliseconds);
        }

        private void UpdateFieldChrome(string value)
        {
            var empty = string.IsNullOrEmpty(value);
            if (_placeholder != null) _placeholder.style.display = empty ? DisplayStyle.Flex : DisplayStyle.None;
            if (_clear != null) _clear.style.display = empty ? DisplayStyle.None : DisplayStyle.Flex;
        }

        /// <summary>
        /// Enter or ↓ within the debounce after typing: the scheduled search is dropped and the current text searched now,
        /// so the key acts on the results of what is in the field, not of the query before it.
        /// </summary>
        private void FlushPendingSearch()
        {
            if (string.Equals((_field.value ?? string.Empty).Trim(), _query, StringComparison.Ordinal))
            {
                return;
            }

            _pendingSearch?.Pause();
            _pendingSearch = null;
            Search(keepNotice: false);
        }

        public void ClearQuery()
        {
            _field.SetValueWithoutNotify(string.Empty);
            UpdateFieldChrome(string.Empty);
            _pendingSearch?.Pause();
            Search(keepNotice: false);
            _field.Focus();
        }

        /// <summary>Escape in the field: clears a query; returns false when it was already empty (the drawer closes).</summary>
        public bool ClearOnEscape()
        {
            if (_field == null || string.IsNullOrEmpty(_field.value))
            {
                return false;
            }

            ClearQuery();
            return true;
        }

        private void Search(bool keepNotice)
        {
            _nextRefresh = Time.unscaledTime + RefreshSeconds;
            if (!keepNotice) _notice = null;
            _query = (_field?.value ?? string.Empty).Trim();
            if (_query.Length == 0)
            {
                Render(Array.Empty<YardSearchResult>());
                return;
            }

            if (_ship == null) _ship = FindFirstObjectByType<ShipView>();
            var model = _ship != null ? _ship.Model : null;
            IReadOnlyList<YardSearchResult> found;
            try
            {
                found = model != null ? YardObjectSearch.Find(model, _query, Limit) : null;
            }
            catch (Exception e)
            {
                Debug.LogWarning("[ShipyardTwin] search failed: " + e.Message);
                found = null;
            }

            if (model == null)
            {
                ShowEmpty("search", "야드 데이터를 불러오는 중", "야드가 준비되면 같은 검색어로 다시 찾습니다.");
                SetStatus(null);
                ClearCards();
                return;
            }

            Render(found ?? Array.Empty<YardSearchResult>());
        }

        private void OnFieldKey(KeyDownEvent evt)
        {
            if (evt.keyCode is KeyCode.Return or KeyCode.KeypadEnter or KeyCode.DownArrow) FlushPendingSearch();
            switch (evt.keyCode)
            {
                case KeyCode.Return:
                case KeyCode.KeypadEnter:
                    var first = _cards.Find(c => c.HasResult && c.Result.IsAvailable);
                    if (first != null) Activate(first);
                    evt.StopPropagation();
                    break;
                case KeyCode.DownArrow:
                    if (_cards.Count > 0)
                    {
                        _cards[0].Root.Focus();
                        evt.StopPropagation();
                    }

                    break;
            }
        }

        // ------------------------------------------------------------------ rendering

        private void Render(IReadOnlyList<YardSearchResult> results)
        {
            if (_query.Length == 0)
            {
                ClearCards();
                ShowEmpty("search", EmptyTitle, EmptyGuide);
                SetStatus(null);
                return;
            }

            if (results.Count == 0)
            {
                ClearCards();
                ShowEmpty("search", "검색 결과 없음", NoMatchText(_query));
                SetStatus(null);
                return;
            }

            if (_empty != null) _empty.style.display = DisplayStyle.None;
            var hidden = 0;
            for (var i = 0; i < results.Count; i++)
            {
                if (i >= _cards.Count) _cards.Add(CreateCard());
                Fill(_cards[i], results[i]);
                if (!results[i].IsAvailable) hidden++;
            }

            // Drop only the extra cards: the ones kept stay the same elements (focus, hover, scroll are kept).
            for (var i = _cards.Count - 1; i >= results.Count; i--)
            {
                _cards[i].Root.RemoveFromHierarchy();
                _cards.RemoveAt(i);
            }

            SetStatus(_notice ?? StatusText(results.Count, hidden, Limit));
        }

        /// <summary>Count line above the cards: total, a cap note, and how many are not on screen now.</summary>
        public static string StatusText(int count, int hidden, int limit)
        {
            var text = new StringBuilder();
            text.Append("결과 ").Append(count).Append("건");
            if (count >= limit) text.Append(" (상위 ").Append(limit).Append("건)");
            if (hidden > 0) text.Append(" · 지금 화면에 없는 대상 ").Append(hidden).Append("건");
            return text.ToString();
        }

        private void ShowEmpty(string glyph, string title, string text)
        {
            if (_empty == null) return;
            _empty.style.display = DisplayStyle.Flex;
            if (_emptyIcon != null) _emptyIcon.Glyph = glyph;
            if (_emptyTitle != null) _emptyTitle.text = title;
            if (_emptyText != null) _emptyText.text = text;
        }

        private void SetStatus(string text)
        {
            if (_status == null) return;
            _status.text = text ?? string.Empty;
            _status.style.display = string.IsNullOrEmpty(text) ? DisplayStyle.None : DisplayStyle.Flex;
            _status.EnableInClassList("tw-search-status--notice", _notice != null && text == _notice);
        }

        private void ClearCards()
        {
            foreach (var card in _cards) card.Root.RemoveFromHierarchy();
            _cards.Clear();
        }

        private Card CreateCard()
        {
            var card = new Card { Root = new VisualElement { focusable = true, tabIndex = 0 } };
            card.Root.AddToClassList("tw-search-card");
            card.Tile = new VisualElement { pickingMode = PickingMode.Ignore };
            card.Tile.AddToClassList("tw-search-tile");
            card.Icon = new DashboardIcon();
            card.Icon.AddToClassList("tw-search-tile-icon");
            card.Tile.Add(card.Icon);
            card.Root.Add(card.Tile);

            var body = new VisualElement { pickingMode = PickingMode.Ignore };
            body.AddToClassList("tw-search-body");
            card.Title = NewLabel("tw-search-title");
            card.Meta = NewLabel("tw-search-meta");
            card.Subtitle = NewLabel("tw-search-sub");
            body.Add(card.Title);
            body.Add(card.Meta);
            body.Add(card.Subtitle);
            card.Root.Add(body);

            card.Chip = new VisualElement { pickingMode = PickingMode.Ignore };
            card.Chip.AddToClassList("tw-search-chip");
            card.ChipText = NewLabel("tw-search-chip-text");
            card.Chip.Add(card.ChipText);
            card.Root.Add(card.Chip);

            // A tap is a press and release that hardly moved: a drag scrolls the list instead.
            card.Root.RegisterCallback<PointerDownEvent>(evt =>
            {
                card.PressAt = evt.position;
                card.PointerId = evt.pointerId;
                card.Root.AddToClassList("tw-search-card--pressed");
            });
            card.Root.RegisterCallback<PointerMoveEvent>(evt =>
            {
                if (card.PointerId == evt.pointerId && ((Vector2)evt.position - card.PressAt).magnitude > TapSlopPixels) CancelPress(card);
            });
            card.Root.RegisterCallback<PointerUpEvent>(evt =>
            {
                var tap = card.PointerId == evt.pointerId && ((Vector2)evt.position - card.PressAt).magnitude <= TapSlopPixels;
                CancelPress(card);
                if (tap) Activate(card);
            });
            card.Root.RegisterCallback<PointerLeaveEvent>(_ => CancelPress(card));
            card.Root.RegisterCallback<PointerCancelEvent>(_ => CancelPress(card));
            card.Root.RegisterCallback<NavigationSubmitEvent>(_ => Activate(card));
            card.Root.RegisterCallback<KeyDownEvent>(evt =>
            {
                if (evt.keyCode == KeyCode.UpArrow && _cards.Count > 0 && _cards[0] == card)
                {
                    _field.Focus();   // ↑ from the first card returns to the query
                    evt.StopPropagation();
                }
            });
            _results.Add(card.Root);
            return card;
        }

        private static Label NewLabel(string className)
        {
            var label = new Label { pickingMode = PickingMode.Ignore };
            label.AddToClassList(className);
            return label;
        }

        private static void CancelPress(Card card)
        {
            card.PointerId = -1;
            card.Root.RemoveFromClassList("tw-search-card--pressed");
        }

        private void Fill(Card card, YardSearchResult result)
        {
            card.Result = result;
            card.HasResult = true;
            var category = CategoryClass(result.Category);
            if (card.CategoryClass != category)
            {
                if (card.CategoryClass != null) card.Root.RemoveFromClassList(card.CategoryClass);
                card.Root.AddToClassList(category);
                card.CategoryClass = category;
            }

            card.Icon.Glyph = GlyphFor(result.Category, result.Kind);
            card.Title.text = Highlight(result.Title ?? result.Id ?? "-", _query);
            card.Meta.text = MetaText(result.Id, result.Title, result.Kind, _query);
            card.Subtitle.text = Escape(Clip(result.Subtitle, SubtitleChars));
            card.Subtitle.style.display = string.IsNullOrEmpty(result.Subtitle) ? DisplayStyle.None : DisplayStyle.Flex;
            card.Root.EnableInClassList("tw-search-card--off", !result.IsAvailable);
            card.ChipText.text = OffScreenText;   // shown by USS only on tw-search-card--off
            card.Root.tooltip = result.IsAvailable
                ? "누르면 그 위치로 이동하고 상세 정보를 엽니다"
                : "현재 날짜·표시 상태에서는 3D 화면에 없습니다 (재생 시점은 바꾸지 않습니다)";
        }

        private void Activate(Card card)
        {
            if (!card.HasResult)
            {
                return;
            }

            var result = card.Result;

            if (!result.IsAvailable)
            {
                Notice(card, $"‘{result.Title ?? result.Id}’은(는) 지금 화면에 없습니다. 날짜와 재생 상태는 그대로 둡니다.");
                return;
            }

            bool opened;
            try
            {
                opened = YardObjectSearch.Open(result);
            }
            catch (Exception e)
            {
                Debug.LogWarning("[ShipyardTwin] search open failed: " + e.Message);
                opened = false;
            }

            if (!opened)
            {
                // Gone since the list was built (erected, hidden layer): show it as such and re-check the list soon.
                card.Root.AddToClassList("tw-search-card--off");
                card.ChipText.text = OffScreenText;
                Notice(card, $"‘{result.Title ?? result.Id}’을(를) 지금 화면에서 찾지 못했습니다.");
                _nextRefresh = Time.unscaledTime + 0.4f;
                return;
            }

            _notice = null;
            _field.Blur();
            card.Root.Blur();
            _shell?.CollapseSearch();
        }

        private void Notice(Card card, string text)
        {
            _notice = text;
            SetStatus(text);
            card.Root.RemoveFromClassList("tw-search-card--nudge");
            card.Root.schedule.Execute(() => card.Root.AddToClassList("tw-search-card--nudge"));
            card.Root.schedule.Execute(() => card.Root.RemoveFromClassList("tw-search-card--nudge")).StartingIn(380);
        }

        // ------------------------------------------------------------------ pure helpers (tests)

        public const string EmptyTitle = "통합검색";

        public const string EmptyGuide =
            "명칭이나 호선번호(예: SN2040), ID 일부를 입력하면 관련 호선·블록·크레인·시설·장비가 바로 나옵니다. 카드를 누르면 그 위치로 이동하고 상세 정보가 열립니다.";

        /// <summary>Empty-result text: suggests a shorter name or hull number.</summary>
        public static string NoMatchText(string query) =>
            $"‘{query}’ 관련 결과가 없습니다. 명칭이나 호선번호(SN) 일부로 다시 검색해 보세요.";

        /// <summary>Card glyph: the category's, but scenery machines (forklift, car) look like vehicles, not buildings.</summary>
        public static string GlyphFor(TargetCategory category, string kind) =>
            category == TargetCategory.Prop && LooksLikeVehicle(kind) ? "vehicle" : DashboardIcon.GlyphFor(category);

        public static bool LooksLikeVehicle(string kind) =>
            !string.IsNullOrEmpty(kind) && (kind.Contains("차") || kind.Contains("장비") || kind.Contains("트랜스포터"));

        /// <summary>USS modifier per category (tile colour).</summary>
        public static string CategoryClass(TargetCategory category) => "tw-search-card--" + category.ToString().ToLowerInvariant();

        /// <summary>Second line: the ID (match marked) unless it repeats the title, then the kind.</summary>
        public static string MetaText(string id, string title, string kind, string query)
        {
            var parts = new List<string>();
            if (!string.IsNullOrEmpty(id) && !string.Equals(id, title, StringComparison.OrdinalIgnoreCase))
            {
                parts.Add(Highlight(id, query));
            }

            if (!string.IsNullOrEmpty(kind)) parts.Add(Escape(kind));
            return string.Join("  ·  ", parts);
        }

        /// <summary>
        /// Rich text with the first case-insensitive match of the query marked (accent colour, bold); the rest escaped so a
        /// name with &lt; is shown as typed.
        /// </summary>
        public static string Highlight(string text, string query)
        {
            if (string.IsNullOrEmpty(text)) return string.Empty;
            var q = query?.Trim();
            var at = string.IsNullOrEmpty(q) ? -1 : text.IndexOf(q, StringComparison.OrdinalIgnoreCase);
            if (at < 0) return Escape(text);
            return Escape(text.Substring(0, at)) + "<color=#7FB8FF><b>" + Escape(text.Substring(at, q.Length)) + "</b></color>" +
                   Escape(text.Substring(at + q.Length));
        }

        /// <summary>Text cut at a word boundary near <paramref name="max"/> characters with an ellipsis.</summary>
        public static string Clip(string text, int max)
        {
            if (string.IsNullOrEmpty(text) || text.Length <= max) return text ?? string.Empty;
            var cut = text.LastIndexOfAny(new[] { ' ', '·' }, max - 1);
            if (cut < max / 2) cut = max - 1;
            return text.Substring(0, cut).TrimEnd(' ', '·') + "…";
        }

        private static string Escape(string text) => string.IsNullOrEmpty(text) ? string.Empty : text.Replace("<", "<noparse><</noparse>");
    }
}
