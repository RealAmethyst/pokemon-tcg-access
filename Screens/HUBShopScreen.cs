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

// SHOP screen — driver: HUBShopScreenController (global namespace), a HUBScreenController, so the
// lifecycle hook matches HOME/Profile: postfix OnActivate(HUBGroupController)/OnDeactivate.
//
// Sections (visual top-to-bottom; section-stack model — Up/Down escapes between, Left/Right
// within, Tab cycles):
//   1. HUB top tab strip (HOME / DECKS / … / CARD DEX) + overflow "⋮" (HUBTopController_L).
//   2. Shop sub-tabs (BUNDLES / EXPANSIONS / REDEEM) — PageDragTab toggles (radio).
//   3. Shop content — the expansion selector + product buy buttons (ShopProductController) +
//      collection/booster tiles. Labels resolve via UiLabels.Describe (product → "name, price",
//      expansion → "Expansion, N of M cards collected") — which works here because, unlike an
//      FTUE-reparented focus, these elements are in their normal shop hierarchy.
//
// First cut: a Selectable sweep grouped in reading order, with per-section logging to refine.
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
    private Selectable? _lastFtueTarget;
    private bool _subscribedToFtue;
    private Coroutine? _ftueWaitCoroutine;

    private int _tabGroupIndex = -1;
    private int _contentGroupIndex = -1;

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
        else _groups[_activeGroup].RefocusCurrent(announce: false);
    }

    public void OnExit()
    {
        CancelFtueSettlementWait();
        _structureAtPause = StructureSignature();
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
        return $"Shop|{_activeGroup}|{idx}|{StructureSignature()}";
    }

    public void Tick()
    {
        if (_controller == null)
        {
            if (_active) { _active = false; _router.Deactivate(this); }
            return;
        }
        if (_groups.Count == 0) return;
        if (!Application.isFocused) return;

        if (UnityEngine.Input.GetKeyDown(KeyCode.Tab))
        {
            if (_groups.Count > 1)
            {
                var shift = UnityEngine.Input.GetKey(KeyCode.LeftShift) || UnityEngine.Input.GetKey(KeyCode.RightShift);
                _activeGroup = (_activeGroup + (shift ? -1 : 1) + _groups.Count) % _groups.Count;
                _groups[_activeGroup].FocusFirst(announce: true, interruptPrior: true);
            }
            return;
        }

        var nav = _groups[_activeGroup];
        if (UnityEngine.Input.GetKeyDown(KeyCode.UpArrow) && _activeGroup > 0 && nav.IsAtTopRow)
        {
            var col = nav.CurrentColumn;
            _activeGroup--;
            _groups[_activeGroup].FocusLastRow(preferredColumn: col, announce: true, interruptPrior: true);
            return;
        }
        if (UnityEngine.Input.GetKeyDown(KeyCode.DownArrow) && _activeGroup < _groups.Count - 1 && nav.IsAtBottomRow)
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
            _router.Speech.Say(Name, interrupt: true);
            _groups[_activeGroup].FocusFirst(announce: true, interruptPrior: false);
            _hasAnnouncedAtAll = true;
        }
        else
        {
            _groups[_activeGroup].RefocusCurrent(announce: true);
        }
    }

    private string StructureSignature() => string.Join(",", _groups.Select(g => g.Count));

    private void RebuildGroups()
    {
        _groups.Clear();
        _tabGroupIndex = -1;
        _contentGroupIndex = -1;
        if (_controller == null) return;

        var known = new HashSet<Selectable>();

        // Group 1: HUB top tab strip + overflow "⋮" (same discovery as HUBHomeScreen).
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

        // Group 3: shop content — products, expansion selector, info buttons, etc.
        var content = _controller.GetComponentsInChildren<Selectable>(includeInactive: false)
            .Where(s => !known.Contains(s))
            .Where(s => s is Button || s is Toggle)
            .Where(s => s.isActiveAndEnabled)
            .OrderBy(s => ReadingOrder(s.transform))
            .ToList();
        if (content.Count > 0)
        {
            AddGroup(content);
            _contentGroupIndex = _groups.Count - 1;
        }
    }

    private void RebuildForFtue()
    {
        CancelFtueSettlementWait();
        _groups.Clear();
        _activeGroup = -1;
        _tabGroupIndex = -1;
        _contentGroupIndex = -1;

        var target = FTUEHook.FocusedElement;
        _lastFtueTarget = target;
        if (target == null || !target.isActiveAndEnabled) return;

        AddGroup(new List<Selectable> { target });
        _activeGroup = 0;
        _groups[0].FocusFirst(announce: true, interruptPrior: true);
    }

    private int PickDefaultGroup()
    {
        if (_contentGroupIndex >= 0 && _contentGroupIndex < _groups.Count) return _contentGroupIndex;
        if (_tabGroupIndex >= 0 && _tabGroupIndex < _groups.Count) return _tabGroupIndex;
        return 0;
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
        Log.Info($"Shop group {_groups.Count}: {items.Count} items — {names}{extra}");
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

    // ---- FTUE coordination (ported from HUBHomeScreen) ----

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

    // Escape on a Shop section opens the overflow (⋮) menu via its real top-bar button.
    private static void OpenOverflowMenu() => HUBOverflowMenuScreen.TryOpen();

    private static float ReadingOrder(Transform t)
    {
        var pos = t.position;
        return -pos.y * 10000 + pos.x;
    }
}
