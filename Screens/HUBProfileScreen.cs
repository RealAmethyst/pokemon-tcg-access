using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using EnhancedUI.EnhancedScroller;
using HarmonyLib;
using PokemonTCGLiveAccessibility.Hooks;
using PokemonTCGLiveAccessibility.Navigation;
using PokemonTCGLiveAccessibility.Util;
using TMPro;
using TPCI.Avatar.Customizations;
using TPCI.Coroutines;
using TPCI.Localization;
using UnityEngine;
using UnityEngine.UI;

namespace PokemonTCGLiveAccessibility.Screens;

// HUB Profile screen — driver: HUBProfileScreenController (TPCI.RainierClient/HUBProfileScreenController.cs).
//
// Groups (in reading order):
//   1. HUB top tab strip — HOME / DECKS / etc. on HUBTopController_L.persistentNavButtons.
//      Each button's GameObject is *inactive* during FTUE startup and only becomes active
//      when the game re-enables the strip. So BuildGroups can't just snapshot once — we
//      rebuild on every OnResume (i.e. whenever a child handler pops back to us) and use a
//      structure signature to decide whether the state changed enough to re-announce.
//   2. FRIENDS button — friendsButton field on the controller.
//   3. Avatar customization grid — AvatarCustomizationGridItem cells (FACE / HAIR / etc.).
//   4. Catch-all for any remaining active Selectables on the controller (CUSTOMIZE / STATS /
//      avatar gender swap / etc.).
//
// Speech rule: speak when there's actually something new. The first OnEnter announces by
// default (we just arrived). Subsequent OnResume calls compare the structure signature
// (how many active items per group) to what it was at the matching OnExit — if it changed
// (e.g. the top tab strip just became navigable because FTUE released it), announce; if it
// didn't (we're between identical FTUE dialogue messages with no new items revealed), stay
// silent. That avoids the "Friends, 1 of 1" leak without using a timer or FTUE-state polling.
internal sealed class HUBProfileScreen : IScreenHandler
{
    private static HUBProfileScreen? _instance;

    private readonly ScreenRouter _router;
    private readonly List<KeyboardNavigator> _groups = new();
    private int _activeGroup = -1;
    private HUBProfileScreenController? _controller;
    private bool _active;

    private bool _hasAnnouncedAtAll;
    private string _structureAtPause = string.Empty;
    private Selectable? _lastFtueTarget;
    private bool _subscribedToFtue;
    private Coroutine? _ftueWaitCoroutine;
    private int _avatarGridGroupIndex = -1;

    public string Name => "Profile";

    public HUBProfileScreen(ScreenRouter router)
    {
        _router = router;
        _instance = this;
    }

    public static void Install(HarmonyLib.Harmony harmony, ScreenRouter router)
    {
        new HUBProfileScreen(router);

        var activate = AccessTools.Method(typeof(HUBProfileScreenController), "OnActivate");
        if (activate == null)
        {
            Log.Error("HUBProfileScreen: could not resolve HUBProfileScreenController.OnActivate");
            return;
        }
        harmony.Patch(activate, postfix: new HarmonyMethod(AccessTools.Method(typeof(HUBProfileScreen), nameof(Activate_Postfix))));

        var deactivate = AccessTools.Method(typeof(HUBProfileScreenController), "OnDeactivate");
        if (deactivate != null)
            harmony.Patch(deactivate, postfix: new HarmonyMethod(AccessTools.Method(typeof(HUBProfileScreen), nameof(Deactivate_Postfix))));

        Log.Info("HUBProfileScreen installed");
    }

    private static void Activate_Postfix(HUBProfileScreenController __instance)
    {
        if (_instance == null) return;
        _instance._controller = __instance;
        if (!_instance._active)
        {
            _instance._active = true;
            _instance._router.Activate(_instance);
        }
    }

    private static void Deactivate_Postfix(HUBProfileScreenController __instance)
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

        // FTUE-focused element wins over everything else — if the tutorial has highlighted
        // a specific button on us (from a previous indicator state), point our nav at it.
        if (FTUEHook.FtueIsRunning && FTUEHook.FocusedElement is { } target && target.isActiveAndEnabled)
        {
            RebuildForFtue();
            return;
        }

        RebuildGroups();
        if (_groups.Count == 0) return;
        _activeGroup = PickDefaultGroup();
        _groups[_activeGroup].FocusFirst(announce: false);

        if (FTUEHook.FtueIsRunning)
        {
            // Mirror the game's own pattern (DefaultFTUEController.WaitToEnable): start a
            // coroutine that yields per-frame until native FTUE state resolves the
            // ambiguity — either an FTUE state takes over (silent, the FTUE owns speech)
            // or the animator settles with nothing running (announce).
            StartFtueSettlementWait();
            return;
        }

        Announce();
    }

    public void OnResume()
    {
        if (FTUEHook.FtueIsRunning && FTUEHook.FocusedElement is { } target && target.isActiveAndEnabled)
        {
            RebuildForFtue();
            return;
        }

        // Rebuild because the underlying screen may have grown (e.g. HUB top strip became
        // active while we were paused).
        RebuildGroups();
        if (_groups.Count == 0) return;

        // Clamp active group / focus to the new bounds. If we never picked a group yet
        // (e.g. we suppressed announce at OnEnter during FTUE prep), prefer the avatar grid.
        if (_activeGroup < 0 || _activeGroup >= _groups.Count) _activeGroup = PickDefaultGroup();
        _groups[_activeGroup].FocusFirst(announce: false);

        if (FTUEHook.FtueIsRunning)
        {
            StartFtueSettlementWait();
            return;
        }

        var nowStructure = StructureSignature();
        if (nowStructure != _structureAtPause)
            Announce();
        else
            _groups[_activeGroup].RefocusCurrent(announce: false);
    }

    public void OnExit()
    {
        CancelFtueSettlementWait();
        _structureAtPause = StructureSignature();
    }

    // Native-state polling, modelled on DefaultFTUEController.WaitToEnable (which yields per
    // frame until navBusy / consentDone / HasBlocker / InTransition all resolve before
    // enabling the FTUE animator). Here we wait for the FTUE state machine to commit to one
    // of two outcomes after we entered/resumed the Profile screen:
    //
    //   • An FTUE state behavior enters _currentRunningStates  → FTUE owns speech, abort.
    //   • The FTUE animator is enabled and stable, with the running-state list empty
    //     → FTUE has nothing to say on this screen, announce normally.
    //
    // No frame count: we yield until a native condition resolves, the same way the game's
    // own coroutines coordinate FTUE startup.
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

        // Phase 1: wait for the FTUE animator to actually be enabled. DefaultFTUEController
        // enables it only after a chain of preconditions (navBusy, consent, blocker, scene
        // transition) clears — until that completes, the FTUE state machine isn't processing
        // and we can't tell what it intends to do.
        while (FTUEHook.FtueIsRunning && animator != null && !animator.enabled)
        {
            yield return null;
        }

        // Phase 2: once the animator is live, yield one frame so its first state transition
        // (entry → first behavior state, or animator's own update of any pending transition)
        // can run and call AddToCurrentRunningStates. The animator's update phase happens
        // after MonoBehaviour Update, so this single yield aligns us with post-animator state.
        yield return null;

        // Phase 3: if a *speech-claiming* FTUE state (FTUEDialogue / RainierScreenIndicator)
        // has entered itself, the FTUE owns speech — stay silent and let DialogueScreen /
        // FocusedElementChanged handle the announcement. Passive states (FTUETimedDelay,
        // FTUEWaitForScreenLeave etc.) don't claim speech, so we announce over them.
        if (FTUEHook.FtueSpeechClaimingStateActive)
        {
            Log.Info("Profile: FTUE speech-claim active, staying silent");
            _ftueWaitCoroutine = null;
            yield break;
        }

        // FTUE is alive but quiet on this screen — announce normally.
        Log.Info("Profile: FTUE settled with no speech-claim, announcing");
        _ftueWaitCoroutine = null;
        if (_active && _groups.Count > 0) Announce();
    }

    // FTUE has highlighted a specific button on this screen. Replace all groups with a
    // single-item nav containing just that Selectable — matches what the game's input
    // blocker allows the player to click.
    private void RebuildForFtue()
    {
        CancelFtueSettlementWait();
        _groups.Clear();
        _activeGroup = -1;

        var target = FTUEHook.FocusedElement;
        _lastFtueTarget = target;
        if (target == null || !target.isActiveAndEnabled) return;

        AddGroup(new List<Selectable> { target });
        _activeGroup = 0;
        _groups[0].FocusFirst(announce: true, interruptPrior: true);
    }

    private void SubscribeToFtue()
    {
        if (_subscribedToFtue) return;
        FTUEHook.FocusedElementChanged += OnFtueFocusChanged;
        _subscribedToFtue = true;
    }

    private void OnFtueFocusChanged()
    {
        // Only react when we're the active screen — if we're paused under a Dialogue handler,
        // the focus change will be picked up by our next OnResume.
        if (!_active) return;
        if (_controller == null) return;
        if (FTUEHook.FocusedElement == _lastFtueTarget) return;
        if (FTUEHook.FtueIsRunning)
            RebuildForFtue();
    }

    public void RespeakAll()
    {
        if (_groups.Count == 0) return;
        Announce();
    }

    public string StateSignature()
    {
        if (_controller == null) return "Profile|dead";
        var idx = _groups.Count > 0 && _activeGroup >= 0 ? _groups[_activeGroup].Index : -1;
        return $"Profile|{_activeGroup}|{idx}|{StructureSignature()}";
    }

    public void Tick()
    {
        if (_controller == null)
        {
            if (_active)
            {
                _active = false;
                _router.Deactivate(this);
            }
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

        // Section escape: groups are built in visual top-to-bottom order. Pressing Up at
        // the top row jumps to the section above and lands on its *bottom* row (closest
        // to where the user came from); Down at bottom row jumps below and lands on the
        // top row of the section below. The current column is carried over where the
        // target row has it — FACE (col 0 of grid) → SHOES (col 0 of grid's last row),
        // and into FRIENDS (single item, col 0 either way) → HOME (col 0 of tab strip).
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

    // Announce the focused item on the first ever announce and on subsequent re-announces.
    // Section name comes from the active group's GroupName (set in RebuildGroups for groups
    // that have a visual header above them — currently only the avatar grid uses
    // "Avatar Customization"). KeyboardNavigator.SetIndex(announceGroup: true) speaks the
    // GroupName + item — so we don't speak the header here separately, otherwise the user
    // hears "Avatar Customization" twice on entry (once from a separate header speak, once
    // from the group-name prefix).
    private void Announce()
    {
        if (_groups.Count == 0) return;
        if (!_hasAnnouncedAtAll)
        {
            _groups[_activeGroup].FocusFirst(announce: true, interruptPrior: true);
            _hasAnnouncedAtAll = true;
        }
        else
        {
            _groups[_activeGroup].RefocusCurrent(announce: true);
        }
    }

    // Stable string that captures "what's currently navigable" — number of items per group.
    // Used to detect when the screen's structure changes during a pause (FTUE released the
    // top strip, a new modal-opened section, etc.) so we know whether to re-announce.
    private string StructureSignature()
    {
        return string.Join(",", _groups.Select(g => g.Count));
    }

    private void RebuildGroups()
    {
        _groups.Clear();
        _avatarGridGroupIndex = -1;
        if (_controller == null) return;

        // Group 1: HUB top tab strip (persistent across HUB). The buttons live on each
        // PersistentNavButtonGroup's navButton field. We check the *button's* full native
        // click-route chain (isActiveAndEnabled + IsInteractable + raycast-reachable) rather
        // than the group's hierarchy, because the group's container may be animated to a
        // visually-hidden state while the buttons themselves remain active and clickable
        // (which is what FTUEWaitForScreenLeave relies on — OnlyAllowHome leaves HOME
        // interactable so the user can advance the tutorial by clicking it).
        var topController = Object.FindAnyObjectByType<HUBTopController_L>();
        if (topController != null)
        {
            var navGroups = ReadField<List<PersistentNavButtonGroup>>(topController, "persistentNavButtons");
            Log.Info($"Profile top strip: topController.allow={topController.allow} groups={navGroups?.Count ?? -1}");
            if (navGroups != null)
            {
                foreach (var pnb in navGroups)
                {
                    var btn = pnb != null ? ReadField<Button>(pnb, "navButton") : null;
                    var info = $"  pnb={(pnb != null ? pnb.gameObject.name : "<null>")} pnbActive={pnb?.gameObject.activeInHierarchy} btn={(btn != null ? btn.gameObject.name : "<null>")} btnActive={btn?.gameObject.activeInHierarchy} btnEnabled={btn?.isActiveAndEnabled} interactable={btn?.IsInteractable()}";
                    Log.Info(info);
                }
                var tabButtons = navGroups
                    .Where(pnb => pnb != null)
                    .Select(pnb => ReadField<Button>(pnb, "navButton"))
                    .Where(b => b != null && b!.isActiveAndEnabled && b.IsInteractable())
                    .OrderBy(b => ReadingOrder(b!.transform))
                    .Cast<Selectable>()
                    .ToList();
                if (tabButtons.Count > 0)
                    AddGroup(tabButtons, Util.UiLabels.TopNavLabel);
            }
        }

        var knownOnProfile = new HashSet<Selectable>();

        // Group 2: FRIENDS button.
        var friendsBtn = ReadField<Button>(_controller, "friendsButton");
        if (friendsBtn != null && friendsBtn.gameObject.activeInHierarchy)
        {
            knownOnProfile.Add(friendsBtn);
            AddGroup(new List<Selectable> { friendsBtn });
        }

        // Group 3: Avatar customization grid (FACE / HAIR / etc.).
        var gridItems = _controller.GetComponentsInChildren<AvatarCustomizationGridItem>(includeInactive: false);
        var gridSelectables = new List<Selectable>();
        foreach (var item in gridItems)
        {
            // Each AvatarCustomizationGridItem may have a Button on the same GameObject and
            // sometimes an additional Selectable (e.g. a Toggle wrapper). Collect ALL of the
            // item's Selectables so we don't have duplicates in the catch-all group.
            foreach (var sel in item.GetComponents<Selectable>())
            {
                if (sel != null && sel.gameObject.activeInHierarchy)
                {
                    knownOnProfile.Add(sel);
                    if (sel is Button) gridSelectables.Add(sel);
                }
            }
            foreach (var sel in item.GetComponentsInChildren<Selectable>(includeInactive: false))
            {
                if (sel != null) knownOnProfile.Add(sel);
            }
            // The grid item's interactive Button lives on a child "DynamicButton" GameObject,
            // but the visual wrapper parent (named "AvatarCustomizationCategoryFace" /
            // "...Hair" / etc.) also carries its own Selectable for hover/click effects. Walk
            // up the hierarchy from each grid item and add those parent Selectables to
            // knownOnProfile so the catch-all doesn't double-list every category.
            var t = item.transform.parent;
            while (t != null && t != _controller.transform)
            {
                foreach (var sel in t.GetComponents<Selectable>())
                {
                    if (sel != null) knownOnProfile.Add(sel);
                }
                t = t.parent;
            }
        }
        if (gridSelectables.Count > 0)
        {
            // Re-order in reading order across the grid (top-row left-to-right, then next row).
            var ordered = gridSelectables.OrderBy(s => ReadingOrder(s.transform)).ToList();
            AddGroup(ordered);
            _avatarGridGroupIndex = _groups.Count - 1;
            // Use the panel's visible header ("Avatar Customization") as this group's section
            // name, so section-escape into the grid (e.g. Down from FRIENDS) announces it.
            // For groups without a visual header above them (top tab strip, Friends, the
            // bottom Customize/Stats row) we leave GroupName null — the items themselves
            // are the labels.
            var header = LocalizationManager.Instance?.GetString("avatar_customization_title");
            if (!string.IsNullOrWhiteSpace(header) && !header!.Contains("(Missing)"))
                _groups[_avatarGridGroupIndex].GroupName = header;
        }

        // Group 4: catch-all for remaining active Selectables on the controller (CUSTOMIZE /
        // STATS / SwapAvatarBtn / etc.). NameInputField and Scrollbars are filtered out —
        // text input wants a dedicated handler later and scroll handles aren't menu items.
        //
        // Sub-controller exclusions use direct references to the game's own containers
        // (no heuristics, no alpha walking, no name matching):
        //
        //   • HUBProfileController._itemsMenu — the entire avatar customization items menu
        //     animator. Its GameObject parents the FACE/EYES tabs, the EnhancedScroller of
        //     item cells, the skin-tone strip's parent — everything in the inner panel.
        //     Excluding the whole subtree handles both the scroller cells (CustomizationItem
        //     cells that stay activeInHierarchy through the close animation) and the tabs /
        //     zoom icons that sit as siblings of the scroller.
        //   • HUBProfileController._avatarGroupObjectsController — owns the skin-tone /
        //     hair-color group strip that appears when a category opens. Belt-and-suspenders
        //     in case the prefab parents it outside _itemsMenu.
        //   • HUBProfileScreenController.friendsHubScreenController — the friends overlay
        //     contains its own Close / Btn_AddFriend / etc. that only become reachable when
        //     the user opens the friends sub-screen.
        //
        // The final native filter is Selectable.IsInteractable() — Unity's own "would this
        // selectable accept a click right now" check, including its m_Interactable flag and
        // the propagated CanvasGroup state. This is what filters out the RankFrame / Rank
        // visual chips on the nameplate — they're Selectables with interactable=false (the
        // game uses them for hover hints, not navigation) and Unity itself wouldn't route
        // clicks to them, so neither should we.
        var hubProfile = ReadField<HUBProfileController>(_controller, "_hubProfileController");
        var friendsHub = ReadField<FriendsHUBScreenController>(_controller, "friendsHubScreenController");

        Transform? itemsMenuT = null;
        Transform? avatarGroupObjectsT = null;
        if (hubProfile != null)
        {
            var itemsMenu = ReadField<Animator>(hubProfile, "_itemsMenu");
            if (itemsMenu != null) itemsMenuT = itemsMenu.transform;
            var groupObjects = ReadField<AvatarGroupObjectsController>(hubProfile, "_avatarGroupObjectsController");
            if (groupObjects != null) avatarGroupObjectsT = groupObjects.transform;
        }
        var friendsT = friendsHub != null ? friendsHub.transform : null;

        // Catch-all filter rationale:
        //
        //   • Type filter (`s is Button || s is Toggle`) — real interactive UI controls.
        //     Excludes pure `Selectable` instances used as decorative hover-targets on the
        //     nameplate (RankFrame, Rank) without falsely dropping greyed-out buttons.
        //
        //   • `isActiveAndEnabled` — Unity-native "the Selectable script is running on an
        //     active GameObject". Matches what FocusFirst checks, so any item that lands in
        //     the list is reachable.
        //
        //   • DOES NOT filter by `IsInteractable()`. A button with interactable=false (e.g.
        //     STATS when the avatar customization view is the active one) is still a real
        //     button the player can see. We include it; KeyboardNavigator.DefaultLabel
        //     appends ", unavailable" via `!s.interactable` so the user hears its state.
        var others = _controller.GetComponentsInChildren<Selectable>(includeInactive: false)
            .Where(s => !knownOnProfile.Contains(s))
            .Where(s => s is Button || s is Toggle)
            .Where(s => s.isActiveAndEnabled)
            .Where(s => itemsMenuT == null || !s.transform.IsChildOf(itemsMenuT))
            .Where(s => avatarGroupObjectsT == null || !s.transform.IsChildOf(avatarGroupObjectsT))
            .Where(s => friendsT == null || !s.transform.IsChildOf(friendsT))
            .OrderBy(s => ReadingOrder(s.transform))
            .ToList();
        if (others.Count > 0)
            AddGroup(others);
    }

    private void AddGroup(List<Selectable> items, KeyboardNavigator.LabelResolver? resolver = null)
    {
        var g = new KeyboardNavigator(_router.Speech) { AllowTab = false };
        g.SetItems(items, resolver);
        _groups.Add(g);
        var names = string.Join(", ", items.Take(20).Select(s => s != null ? s.gameObject.name : "<null>"));
        var extra = items.Count > 20 ? $" (+{items.Count - 20} more)" : string.Empty;
        Log.Info($"Profile group {_groups.Count}: {items.Count} items — {names}{extra}");
    }

    // The avatar customization grid is the conceptual "main content" of the Profile screen,
    // so default to focusing it. Falls back to whichever group exists if the grid wasn't
    // built this rebuild (e.g. Profile entered before AvatarCustomizationGridItem children
    // were spawned).
    private int PickDefaultGroup()
    {
        if (_avatarGridGroupIndex >= 0 && _avatarGridGroupIndex < _groups.Count)
            return _avatarGridGroupIndex;
        return 0;
    }

    private string? FindSectionHeader()
    {
        // Direct loc lookup is more reliable than scanning TMP_Text by font size — we already
        // saw "1"@46 (the level number on the player nameplate) outranking "Avatar
        // Customization" because they happened to use the same large font. The Profile
        // screen's header is unambiguously avatar_customization_title; only fall back to
        // scanning if the loc table doesn't have it.
        var loc = LocalizationManager.Instance?.GetString("avatar_customization_title");
        if (!string.IsNullOrWhiteSpace(loc) && !loc!.Contains("(Missing)"))
            return loc;

        if (_controller == null) return "Profile";
        var standalone = _controller.GetComponentsInChildren<TMP_Text>(includeInactive: false)
            .Where(t => !string.IsNullOrWhiteSpace(t.text))
            .Where(t => t.GetComponentInParent<Selectable>() == null)
            .OrderByDescending(t => t.fontSize)
            .ToList();
        return standalone.Count > 0 ? standalone.First().text.Trim() : "Profile";
    }

    // Walk the type hierarchy looking for the field — needed because subclasses can hide
    // a base-class field with a same-named one and Type.GetField only checks the type you
    // call it on. Includes both Public and NonPublic visibility: serialized Unity fields
    // are conventionally [SerializeField] private (NonPublic) but plenty of game code uses
    // plain public fields too (PersistentNavButtonGroup.navButton, etc.). Reading only one
    // visibility silently misses the others, which is what hid the entire HUB top tab
    // strip from us — every nav button reference came back null.
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

    private static float ReadingOrder(Transform t)
    {
        var pos = t.position;
        return -pos.y * 10000 + pos.x;
    }
}
