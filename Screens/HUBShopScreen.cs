using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using PokemonTCGLiveAccessibility.Hooks;
using PokemonTCGLiveAccessibility.Navigation;
using PokemonTCGLiveAccessibility.Util;
using TMPro;
using TPCI.Coroutines;
using TPCI.Localization;
using UnityEngine;
using UnityEngine.UI;

namespace PokemonTCGLiveAccessibility.Screens;

// SHOP screen — driver: HUBShopScreenController (a HUBScreenController, so the lifecycle hook
// matches HOME/Profile: postfix OnActivate / OnDeactivate).
//
// The shop has three sub-tabs (BUNDLES / EXPANSIONS / REDEEM), each a ShopPageSubScreen built by
// ShopBuilder. We discover the *current* sub-screen via builder.GetPageDragManager().GetCurrentScreen()
// and present its content as a section stack (top-to-bottom; Up/Down escapes between sections,
// Left/Right within, Tab cycles — the same model as HOME):
//   1. HUB top tab strip (HOME / DECKS / … / CARD DEX) + overflow "⋮".
//   2. Shop sub-tabs (BUNDLES / EXPANSIONS / REDEEM) — PageDragTab toggles (radio).
//   3..N. One section PER product collection (RECOMMENDED / NEW ARRIVALS / decks / boosters …),
//         named by the collection's header title; each item is a product tile read as
//         "name, price, N available" (UiLabels.Describe → ShopLabels). Plus a trailing "extras"
//         section for screen-level controls (VIEW ALL, set picker, the redeem code field, …).
//
// The sub-tabs switch content asynchronously (LoadContent is a coroutine), so a content-signature
// poll rebuilds the sections when the active page or its product count changes — never losing a
// keypress in the load window. FTUE focus still wins (RebuildForFtue), as on every HUB screen.
internal sealed class HUBShopScreen : IScreenHandler
{
    private static HUBShopScreen? _instance;

    private readonly ScreenRouter _router;
    private readonly List<KeyboardNavigator> _groups = new();
    private int _activeGroup = -1;
    private HUBShopScreenController? _controller;
    private bool _active;

    private bool _hasAnnouncedAtAll;
    private string _structureAtPause = string.Empty;
    private int _indexAtPause = -1;
    private string _lastContentSig = string.Empty;
    private Selectable? _lastFtueTarget;
    private bool _subscribedToFtue;
    private Coroutine? _ftueWaitCoroutine;

    private int _tabGroupIndex = -1;
    private int _firstContentGroupIndex = -1;

    public string Name => "Shop";

    public HUBShopScreen(ScreenRouter router)
    {
        _router = router;
        _instance = this;
    }

    public static void Install(HarmonyLib.Harmony harmony, ScreenRouter router)
    {
        new HUBShopScreen(router);

        var activate = AccessTools.Method(typeof(HUBShopScreenController), "OnActivate");
        if (activate == null)
        {
            Log.Error("HUBShopScreen: could not resolve HUBShopScreenController.OnActivate");
            return;
        }
        harmony.Patch(activate, postfix: new HarmonyMethod(AccessTools.Method(typeof(HUBShopScreen), nameof(Activate_Postfix))));

        var deactivate = AccessTools.Method(typeof(HUBShopScreenController), "OnDeactivate");
        if (deactivate != null)
            harmony.Patch(deactivate, postfix: new HarmonyMethod(AccessTools.Method(typeof(HUBShopScreen), nameof(Deactivate_Postfix))));

        Log.Info("HUBShopScreen installed");
    }

    private static void Activate_Postfix(HUBShopScreenController __instance)
    {
        if (_instance == null) return;
        _instance._controller = __instance;
        if (!_instance._active)
        {
            _instance._active = true;
            _instance._router.Activate(_instance);
        }
    }

    private static void Deactivate_Postfix(HUBShopScreenController __instance)
    {
        if (_instance == null || _instance._controller != __instance) return;
        if (!_instance._active) return;
        _instance._active = false;
        _instance._router.Deactivate(_instance);
        _instance._controller = null;
    }

    public void OnEnter()
    {
        SubscribeToFtue();

        if (FTUEHook.FtueIsRunning && FTUEHook.FocusedElement is { } target && target.isActiveAndEnabled)
        {
            RebuildForFtue();
            return;
        }

        RebuildGroups();
        if (_groups.Count == 0) return;
        _activeGroup = PickDefaultGroup();
        _groups[_activeGroup].FocusFirst(announce: false);

        if (FTUEHook.FtueIsRunning) { StartFtueSettlementWait(); return; }
        Announce();
    }

    public void OnResume()
    {
        if (FTUEHook.FtueIsRunning && FTUEHook.FocusedElement is { } target && target.isActiveAndEnabled)
        {
            RebuildForFtue();
            return;
        }

        RebuildGroups();
        if (_groups.Count == 0) return;
        if (_activeGroup < 0 || _activeGroup >= _groups.Count) _activeGroup = PickDefaultGroup();
        _groups[_activeGroup].FocusFirst(announce: false);

        if (FTUEHook.FtueIsRunning) { StartFtueSettlementWait(); return; }

        if (StructureSignature() != _structureAtPause) Announce();
        else RestoreGroupFocus();   // structure unchanged (e.g. closed the ⋮ menu / a popup) → exact prior focus
    }

    public void OnExit()
    {
        CancelFtueSettlementWait();
        _structureAtPause = StructureSignature();
        _indexAtPause = (_groups.Count > 0 && _activeGroup >= 0 && _activeGroup < _groups.Count)
            ? _groups[_activeGroup].Index : -1;
    }

    private void RestoreGroupFocus()
    {
        var g = _groups[_activeGroup];
        if (_indexAtPause >= 0 && _indexAtPause < g.Count)
            g.FocusIndex(_indexAtPause, announce: false, interrupt: true);
        else
            g.RefocusCurrent(announce: false);
    }

    public void RespeakAll()
    {
        if (_groups.Count == 0) return;
        Announce();
    }

    public string StateSignature()
    {
        if (_controller == null) return "Shop|dead";
        var idx = _groups.Count > 0 && _activeGroup >= 0 ? _groups[_activeGroup].Index : -1;
        return $"Shop|{_activeGroup}|{idx}|{ContentSig()}";
    }

    public void Tick()
    {
        if (_controller == null)
        {
            if (_active) { _active = false; _router.Deactivate(this); }
            return;
        }
        if (!Application.isFocused) return;

        // React to a sub-tab switch / async content load: the section list changes, so rebuild.
        // Gated off during FTUE, where focus is tutorial-driven (RebuildForFtue).
        if (!FTUEHook.FtueIsRunning)
        {
            var sig = ContentSig();
            if (sig != _lastContentSig) { _lastContentSig = sig; OnContentChanged(); return; }
        }

        if (_groups.Count == 0) return;

        var nav = _groups[_activeGroup];
        // In a text field (redeem code box), arrows are caret movement and Tab exits the field —
        // so suppress the arrow-based section escape and turn Tab into a move WITHIN the section.
        bool editing = nav.CurrentIsTextField;

        if (UnityEngine.Input.GetKeyDown(KeyCode.Tab))
        {
            var shift = UnityEngine.Input.GetKey(KeyCode.LeftShift) || UnityEngine.Input.GetKey(KeyCode.RightShift);
            if (editing)
            {
                int before = nav.Index;
                if (shift) nav.MovePrev(); else nav.MoveNext();
                if (nav.Index != before) return;   // exited the field to the next control in the section
                // field was the section's edge → fall through to a normal section cycle
            }
            if (_groups.Count > 1)
            {
                _activeGroup = (_activeGroup + (shift ? -1 : 1) + _groups.Count) % _groups.Count;
                if (_activeGroup == _tabGroupIndex) FocusActiveTab(announce: true);
                else _groups[_activeGroup].FocusFirst(announce: true, interruptPrior: true);
            }
            return;
        }

        if (!editing && UnityEngine.Input.GetKeyDown(KeyCode.UpArrow) && _activeGroup > 0 && nav.IsAtTopRow)
        {
            var col = nav.CurrentColumn;
            _activeGroup--;
            if (_activeGroup == _tabGroupIndex) FocusActiveTab(announce: true);
            else _groups[_activeGroup].FocusLastRow(preferredColumn: col, announce: true, interruptPrior: true);
            return;
        }
        if (!editing && UnityEngine.Input.GetKeyDown(KeyCode.DownArrow) && _activeGroup < _groups.Count - 1 && nav.IsAtBottomRow)
        {
            var col = nav.CurrentColumn;
            _activeGroup++;
            _groups[_activeGroup].FocusFirstRow(preferredColumn: col, announce: true, interruptPrior: true);
            return;
        }

        nav.Tick();
    }

    private void Announce()
    {
        if (_groups.Count == 0) return;
        if (!_hasAnnouncedAtAll)
        {
            var tab = ActiveTabName();
            var header = string.IsNullOrWhiteSpace(tab) ? Name : $"{Name}, {tab}";
            _router.Speech.Say(header, interrupt: true);
            _groups[_activeGroup].FocusFirst(announce: true, interruptPrior: false);
            _hasAnnouncedAtAll = true;
        }
        else
        {
            _groups[_activeGroup].RefocusCurrent(announce: true);
        }
    }

    private string StructureSignature() => string.Join(",", _groups.Select(g => g.Count));

    // ---- group discovery -------------------------------------------------------------------

    private void RebuildGroups()
    {
        _groups.Clear();
        _tabGroupIndex = -1;
        _firstContentGroupIndex = -1;
        if (_controller == null) { _lastContentSig = ContentSig(); return; }

        var known = new HashSet<Selectable>();

        // Group 1: HUB top tab strip + overflow "⋮" (same discovery as HOME / Profile).
        var topItems = new List<Selectable>();
        var topController = Object.FindAnyObjectByType<HUBTopController_L>();
        if (topController != null)
        {
            var navGroups = ReadField<List<PersistentNavButtonGroup>>(topController, "persistentNavButtons");
            if (navGroups != null)
                foreach (var pnb in navGroups)
                {
                    var btn = pnb != null ? ReadField<Button>(pnb, "navButton") : null;
                    if (btn != null && btn.isActiveAndEnabled && btn.IsInteractable()) topItems.Add(btn);
                }
            var settingsT = ReadField<Transform>(topController, "settingsButton");
            var overflow = settingsT != null ? settingsT.GetComponent<Button>() : null;
            if (overflow != null && overflow.isActiveAndEnabled && overflow.IsInteractable()) topItems.Add(overflow);
        }
        if (topItems.Count > 0)
        {
            var ordered = topItems.OrderBy(s => ReadingOrder(s.transform)).ToList();
            foreach (var s in ordered) known.Add(s);
            AddGroup(ordered, resolver: Util.UiLabels.TopNavLabel);
        }

        // Group 2: shop sub-tabs (BUNDLES / EXPANSIONS / REDEEM) — PageDragTab toggles, radio.
        var tabs = _controller.GetComponentsInChildren<PageDragTab>(includeInactive: false)
            .Select(t => t.toggle)
            .Where(t => t != null && t.isActiveAndEnabled)
            .Cast<Selectable>()
            .OrderBy(s => ReadingOrder(s.transform))
            .ToList();
        foreach (var s in tabs) known.Add(s);
        if (tabs.Count > 0)
        {
            AddGroup(tabs, resolver: TabLabel, radioMode: true, groupName: "Shop section");
            _tabGroupIndex = _groups.Count - 1;
        }

        // Groups 3..N: the current sub-screen's content — one section per product collection plus a
        // trailing "extras" section, all ordered by reading order so side-by-side panels read left to
        // right and footer controls (VIEW ALL) come last.
        var sub = ActiveSubScreen();
        if (sub is ShopPageCodeCardScreen redeemSub)
        {
            // REDEEM is a code-entry screen, not a product list: present a clean, ordered set of
            // controls (the code field first so it's the landing spot and Tab reaches it directly),
            // dropping the desktop-irrelevant camera scanner and bare icon strays.
            var items = RedeemControls(redeemSub);
            if (items.Count > 0)
            {
                AddGroup(items, groupName: "Redeem a code");
                _firstContentGroupIndex = _groups.Count - 1;
            }
        }
        else if (sub != null)
        {
            var content = new List<(float key, List<Selectable> items, string? name)>();

            foreach (var col in ActiveCollections(sub))
            {
                var items = ProductButtons(col);
                if (items.Count == 0) continue;
                foreach (var s in items) known.Add(s);
                content.Add((ReadingOrder(col.transform), items, ShopLabels.CollectionTitle(col)));
            }

            var extras = ExtrasUnder(sub, known);
            if (extras.Count > 0)
                content.Add((ReadingOrder(extras[0].transform), extras, ExtrasGroupName(sub)));

            content.Sort((a, b) => a.key.CompareTo(b.key));
            foreach (var c in content)
            {
                AddGroup(c.items, groupName: c.name);
                if (_firstContentGroupIndex < 0) _firstContentGroupIndex = _groups.Count - 1;
            }
        }
        else
        {
            // Fallback (builder not ready / unknown page): sweep all content under the controller.
            var content = _controller.GetComponentsInChildren<Selectable>(includeInactive: false)
                .Where(s => !known.Contains(s))
                .Where(s => s is Button || s is Toggle || s is TMP_InputField || s is InputField)
                .Where(s => s.isActiveAndEnabled)
                .OrderBy(s => ReadingOrder(s.transform))
                .ToList();
            if (content.Count > 0) { AddGroup(content); _firstContentGroupIndex = _groups.Count - 1; }
        }

        _lastContentSig = ContentSig();
    }

    private void RebuildForFtue()
    {
        CancelFtueSettlementWait();
        _groups.Clear();
        _activeGroup = -1;
        _tabGroupIndex = -1;
        _firstContentGroupIndex = -1;
        _lastContentSig = ContentSig();

        var target = FTUEHook.FocusedElement;
        _lastFtueTarget = target;
        if (target == null || !target.isActiveAndEnabled) return;

        AddGroup(new List<Selectable> { target });
        _activeGroup = 0;
        _groups[0].FocusFirst(announce: true, interruptPrior: true);
    }

    private int PickDefaultGroup()
    {
        if (_firstContentGroupIndex >= 0 && _firstContentGroupIndex < _groups.Count) return _firstContentGroupIndex;
        if (_tabGroupIndex >= 0 && _tabGroupIndex < _groups.Count) return _tabGroupIndex;
        return 0;
    }

    // ---- content-change integration --------------------------------------------------------

    // Signature of the current content: active sub-screen type + page index + product count.
    // Changes only on a tab switch or async product (un)load — discrete events — so comparing it
    // each tick is a cheap way to know when to rebuild the section list.
    private string ContentSig()
    {
        var sub = ActiveSubScreen();
        if (sub == null) return "none";
        int products = sub.GetComponentsInChildren<ShopProductController>(includeInactive: false).Length;
        int page = -1;
        try
        {
            var pdm = _controller != null && _controller.builder != null ? _controller.builder.GetPageDragManager() : null;
            if (pdm != null) page = pdm.currentPage;
        }
        catch { }
        return $"{sub.GetType().Name}|{page}|{products}";
    }

    private void OnContentChanged()
    {
        int prev = _activeGroup;
        RebuildGroups();
        if (_groups.Count == 0) { _activeGroup = -1; return; }
        _activeGroup = Mathf.Clamp(prev < 0 ? PickDefaultGroup() : prev, 0, _groups.Count - 1);
        if (_activeGroup == _tabGroupIndex) FocusActiveTab(announce: false);
        else _groups[_activeGroup].RefocusCurrent(announce: false);
    }

    // Focus the selected sub-tab (the toggle that's on), so escaping up into the tab strip —
    // or rebuilding after a switch — lands on the active tab rather than the first.
    private void FocusActiveTab(bool announce)
    {
        if (_tabGroupIndex < 0 || _tabGroupIndex >= _groups.Count) return;
        var kn = _groups[_tabGroupIndex];
        for (int i = 0; i < kn.Items.Count; i++)
            if (kn.Items[i] is Toggle t && t.isOn) { kn.FocusIndex(i, announce: announce, interrupt: true); return; }
        kn.FocusFirst(announce);
    }

    // ---- sub-screen / collection helpers ---------------------------------------------------

    private ShopPageSubScreen? ActiveSubScreen()
    {
        if (_controller == null) return null;
        try
        {
            var builder = _controller.builder;
            var pdm = builder != null ? builder.GetPageDragManager() : null;
            var go = pdm != null ? pdm.GetCurrentScreen() : null;
            return go != null ? go.GetComponent<ShopPageSubScreen>() : null;
        }
        catch { return null; }
    }

    // The active product collections under the current sub-screen, in reading order. A collection
    // with no products is SetActive(false) by the game, so the active/has-products filter drops
    // empty ones (e.g. an absent "LEAVING SOON").
    private static List<ShopProductCollection> ActiveCollections(Component sub)
    {
        return sub.GetComponentsInChildren<ShopProductCollection>(includeInactive: false)
            .Where(c => c != null && c.gameObject.activeInHierarchy)
            .Where(c => c.GetComponentsInChildren<ShopProductController>(includeInactive: false).Length > 0)
            .OrderBy(c => ReadingOrder(c.transform))
            .ToList();
    }

    // The clickable button for each product tile (opens the product overlay), in reading order.
    private static List<Selectable> ProductButtons(ShopProductCollection col)
    {
        var result = new List<Selectable>();
        foreach (var p in col.GetComponentsInChildren<ShopProductController>(includeInactive: false))
        {
            var b = p.GetComponentInChildren<Button>(includeInactive: false);
            if (b != null && b.isActiveAndEnabled) result.Add(b);
        }
        return result.OrderBy(s => ReadingOrder(s.transform)).ToList();
    }

    // Screen-level controls on the sub-screen that aren't product tiles: VIEW ALL, the (i) info
    // buttons, the expansion set picker / drawer, the redeem code field + submit + collect-all.
    private static List<Selectable> ExtrasUnder(Component sub, HashSet<Selectable> known)
    {
        return sub.GetComponentsInChildren<Selectable>(includeInactive: false)
            .Where(s => s is Button || s is Toggle || s is TMP_InputField || s is InputField)
            .Where(s => s.isActiveAndEnabled)
            .Where(s => !known.Contains(s))
            .Where(s => s.GetComponentInParent<ShopProductController>() == null)
            .Where(s => !IsHiddenSeriesFilter(s))
            .OrderBy(s => ReadingOrder(s.transform))
            .ToList();
    }

    // The EXPANSIONS "filter by card series" menu starts collapsed: a CanvasGroup turns its toggles
    // non-interactable + invisible, but the toggle GameObjects stay active — so a plain sweep grabs
    // them as a long list of unlabeled "checkboxes" (the user's complaint). Skip them while the menu
    // is collapsed; Unity's IsInteractable() reflects the parent CanvasGroup state, so they reappear
    // (interactable) once the user opens the filter.
    private static bool IsHiddenSeriesFilter(Selectable s)
        => s.GetComponentInParent<ExpandableSeriesSelector>() != null && !s.IsInteractable();

    private static string ExtrasGroupName(Component sub)
    {
        if (sub is ShopPageExpansionsScreen) return "Choose set";
        return "More";
    }

    // The REDEEM screen's meaningful controls, ordered: the code field, then SUBMIT (matched by its
    // wired onClick), then the remaining worded buttons (CLEAR, COLLECT ALL, DROP RATES). The camera
    // scanner toggle and the bare scanner-frame icons have no worded label, so they're dropped.
    private static List<Selectable> RedeemControls(ShopPageCodeCardScreen redeem)
    {
        var items = new List<Selectable>();
        var field = ReadField<TMP_InputField>(redeem, "_scanResultInputField");
        if (field != null && field.isActiveAndEnabled) items.Add(field);

        var buttons = redeem.GetComponentsInChildren<Button>(includeInactive: false)
            .Where(b => b.isActiveAndEnabled).ToList();

        var submit = buttons.FirstOrDefault(b => HasHandler(b, "OnClickSubmitButton"));
        if (submit != null) items.Add(submit);

        foreach (var b in buttons.OrderBy(b => ReadingOrder(b.transform)))
        {
            if (items.Contains(b)) continue;
            if (HasHandler(b, "OnClickCameraToggle")) continue;        // desktop scanner toggle
            if (!HasLetterText(b) && !HasHandler(b, "OnClickClearInputField")) continue;  // icon-only stray
            items.Add(b);
        }
        return items;
    }

    private static bool HasHandler(Button b, string method)
    {
        if (b == null) return false;
        var n = b.onClick.GetPersistentEventCount();
        for (int i = 0; i < n; i++)
            if (b.onClick.GetPersistentMethodName(i) == method) return true;
        return false;
    }

    private static bool HasLetterText(Selectable s)
    {
        foreach (var t in s.GetComponentsInChildren<TMP_Text>(includeInactive: false))
        {
            var txt = t.text;
            if (string.IsNullOrEmpty(txt)) continue;
            foreach (var c in txt) if (char.IsLetter(c)) return true;
        }
        return false;
    }

    private string? ActiveTabName()
    {
        if (_controller == null) return null;
        foreach (var t in _controller.GetComponentsInChildren<PageDragTab>(includeInactive: false))
        {
            if (t.toggle == null || !t.toggle.isOn) continue;
            var id = t.GetLocTextID();
            if (!string.IsNullOrEmpty(id))
            {
                var loc = LocalizationManager.Instance?.GetString(id);
                if (!string.IsNullOrWhiteSpace(loc) && !loc!.Contains("(Missing)")) return loc;
            }
            var tmp = t.GetComponentInChildren<TMP_Text>(includeInactive: false);
            return tmp != null && !string.IsNullOrEmpty(tmp.text) ? tmp.text : null;
        }
        return null;
    }

    private void AddGroup(List<Selectable> items, KeyboardNavigator.LabelResolver? resolver = null,
                          bool radioMode = false, string? groupName = null)
    {
        var g = new KeyboardNavigator(_router.Speech) { AllowTab = false, RadioToggleMode = radioMode, GroupName = groupName };
        g.SetItems(items, resolver);
        // Escape on any Shop section opens the overflow (⋮) menu — same as the top-bar button.
        g.OnBack = OpenOverflowMenu;
        _groups.Add(g);
        var names = string.Join(", ", items.Take(20).Select(s => s != null ? s.gameObject.name : "<null>"));
        var extra = items.Count > 20 ? $" (+{items.Count - 20} more)" : string.Empty;
        Log.Info($"Shop group {_groups.Count} [{groupName ?? "-"}]: {items.Count} items — {names}{extra}");
    }

    private static string TabLabel(Selectable s)
    {
        string? label = null;
        var tab = s.GetComponent<PageDragTab>();
        if (tab != null)
        {
            var id = tab.GetLocTextID();
            if (!string.IsNullOrEmpty(id))
            {
                var loc = LocalizationManager.Instance?.GetString(id);
                if (!string.IsNullOrWhiteSpace(loc) && !loc!.Contains("(Missing)")) label = loc;
            }
        }
        if (string.IsNullOrEmpty(label))
        {
            var tmp = s.GetComponentInChildren<TMP_Text>(includeInactive: false);
            label = tmp != null && !string.IsNullOrEmpty(tmp.text) ? tmp.text : s.gameObject.name;
        }
        var state = (s is Toggle t && t.isOn) ? "selected" : "not selected";
        return s.interactable ? $"{label}, {state}" : $"{label}, {state}, unavailable";
    }

    // ---- FTUE coordination (ported from HUBHomeScreen) -------------------------------------

    private void SubscribeToFtue()
    {
        if (_subscribedToFtue) return;
        FTUEHook.FocusedElementChanged += OnFtueFocusChanged;
        _subscribedToFtue = true;
    }

    private void OnFtueFocusChanged()
    {
        if (!_active || _controller == null) return;
        if (FTUEHook.FocusedElement == _lastFtueTarget) return;
        if (FTUEHook.FtueIsRunning) RebuildForFtue();
    }

    private void StartFtueSettlementWait()
    {
        CancelFtueSettlementWait();
        _ftueWaitCoroutine = AnywhereCoroutine.Instance.unmanaged.StartCoroutine(WaitForFtueToSettle());
    }

    private void CancelFtueSettlementWait()
    {
        if (_ftueWaitCoroutine != null)
        {
            AnywhereCoroutine.Instance.unmanaged.StopCoroutine(_ftueWaitCoroutine);
            _ftueWaitCoroutine = null;
        }
    }

    private IEnumerator WaitForFtueToSettle()
    {
        var mgr = FTUEManager.instance;
        var animator = mgr != null ? mgr.GetComponent<Animator>() : null;
        while (FTUEHook.FtueIsRunning && animator != null && !animator.enabled) yield return null;
        yield return null;
        if (FTUEHook.FtueSpeechClaimingStateActive) { _ftueWaitCoroutine = null; yield break; }
        _ftueWaitCoroutine = null;
        if (_active && _groups.Count > 0) Announce();
    }

    private static T? ReadField<T>(object instance, string name) where T : class
    {
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;
        for (var t = instance.GetType(); t != null; t = t.BaseType)
        {
            var field = t.GetField(name, flags);
            if (field != null) return field.GetValue(instance) as T;
        }
        return null;
    }

    private static void OpenOverflowMenu() => HUBOverflowMenuScreen.TryOpen();

    private static float ReadingOrder(Transform t)
    {
        var pos = t.position;
        return -pos.y * 10000 + pos.x;
    }
}
