using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using PokemonTCGLiveAccessibility.Navigation;
using PokemonTCGLiveAccessibility.Util;
using TMPro;
using TPCI.Avatar.Customizations;
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
        RebuildGroups();
        if (_groups.Count == 0) return;
        _activeGroup = 0;
        _groups[_activeGroup].FocusFirst(announce: false);
        Announce();
    }

    public void OnResume()
    {
        // Rebuild because the underlying screen may have grown (e.g. HUB top strip became
        // active while we were paused).
        var prevStructure = StructureSignature();
        RebuildGroups();
        if (_groups.Count == 0) return;

        // Clamp active group / focus to the new bounds.
        if (_activeGroup < 0 || _activeGroup >= _groups.Count) _activeGroup = 0;

        var nowStructure = StructureSignature();
        if (nowStructure != _structureAtPause)
        {
            // Something materially changed during the pause — announce.
            _groups[_activeGroup].FocusFirst(announce: false);
            Announce();
        }
        else
        {
            // Same shape as before pause — just refocus visually, no speech.
            _groups[_activeGroup].RefocusCurrent(announce: false);
        }
    }

    public void OnExit()
    {
        _structureAtPause = StructureSignature();
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

        _groups[_activeGroup].Tick();
    }

    // Announce screen header + focused item on the first ever announce. After that, just the
    // focused item. _hasAnnouncedAtAll persists across pause/resume so the header rule plays
    // out the same as elsewhere in the mod.
    private void Announce()
    {
        if (_groups.Count == 0) return;
        if (!_hasAnnouncedAtAll)
        {
            var header = FindSectionHeader();
            if (!string.IsNullOrWhiteSpace(header))
                _router.Speech.Say(header!, interrupt: true);
            _groups[_activeGroup].FocusFirst(announce: true, interruptPrior: false);
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
        if (_controller == null) return;

        // Group 1: HUB top tab strip (persistent across HUB).
        var topController = Object.FindAnyObjectByType<HUBTopController_L>();
        if (topController != null)
        {
            var navGroups = ReadField<List<PersistentNavButtonGroup>>(topController, "persistentNavButtons");
            if (navGroups != null)
            {
                var tabButtons = navGroups
                    .Where(pnb => pnb != null && pnb.gameObject.activeInHierarchy)
                    .Select(pnb => ReadField<Button>(pnb, "navButton"))
                    .Where(b => b != null && b!.gameObject.activeInHierarchy)
                    .OrderBy(b => ReadingOrder(b!.transform))
                    .Cast<Selectable>()
                    .ToList();
                if (tabButtons.Count > 0)
                    AddGroup(tabButtons);
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
        }
        if (gridSelectables.Count > 0)
        {
            // Re-order in reading order across the grid (top-row left-to-right, then next row).
            var ordered = gridSelectables.OrderBy(s => ReadingOrder(s.transform)).ToList();
            AddGroup(ordered);
        }

        // Group 4: catch-all for remaining active Selectables on the controller (CUSTOMIZE /
        // STATS / SwapAvatarBtn / etc.). NameInputField and Scrollbars are filtered out —
        // text input wants a dedicated handler later and scroll handles aren't menu items.
        var others = _controller.GetComponentsInChildren<Selectable>(includeInactive: false)
            .Where(s => s.gameObject.activeInHierarchy)
            .Where(s => !knownOnProfile.Contains(s))
            .Where(s => !(s is Scrollbar))
            .Where(s => !(s is InputField))
            .Where(s => s.GetComponent<TMP_InputField>() == null)
            .OrderBy(s => ReadingOrder(s.transform))
            .ToList();
        if (others.Count > 0)
            AddGroup(others);
    }

    private void AddGroup(List<Selectable> items)
    {
        var g = new KeyboardNavigator(_router.Speech) { AllowTab = false };
        g.SetItems(items);
        _groups.Add(g);
    }

    private string? FindSectionHeader()
    {
        if (_controller == null) return null;
        var standalone = _controller.GetComponentsInChildren<TMP_Text>(includeInactive: false)
            .Where(t => !string.IsNullOrWhiteSpace(t.text))
            .Where(t => t.GetComponentInParent<Selectable>() == null)
            .OrderByDescending(t => t.fontSize)
            .ToList();
        if (standalone.Count == 0) return null;
        return standalone.First().text.Trim();
    }

    private static T? ReadField<T>(object instance, string name) where T : class
    {
        var field = instance.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic);
        return field?.GetValue(instance) as T;
    }

    private static float ReadingOrder(Transform t)
    {
        var pos = t.position;
        return -pos.y * 10000 + pos.x;
    }
}
