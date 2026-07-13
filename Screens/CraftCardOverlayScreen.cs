using System.Reflection;
using System.Text;
using HarmonyLib;
using PokemonTCGLiveAccessibility.Util;
using TPCI.Localization;
using UnityEngine;

namespace PokemonTCGLiveAccessibility.Screens;

// Per-card EXCHANGE popup — driver: CardPurchasePopupParts, the quantity/cost popup the deck editor's
// card-detail "EXCHANGE" button opens (the game's code calls it Craft/CraftOpen; the UI says Exchange).
// (AddCardOverlayManager.PurchaseCardButtonActions ->
// cardPurchasePopup.ShowPurchasePopup). The same popup component is reused by the Card Dex purchase
// overlay, so one handler covers both. It is a quantity stepper ([-] N cards [+]) plus the Credit
// cost, your owned count, and a Confirm button — fully silent without a handler.
//
// CardPurchasePopupParts is a plain MonoBehaviour (NOT an OverlayScreenGroup), shown/hidden via its
// own ShowPurchasePopup/HidePurchasePopup, so we patch those directly — there's no shared-base cast
// hazard like [[feedback-overlay-hook-hazard]]. We drive the game's own public action methods —
// IncreaseCardCountActions / DecreaseCardCountActions / ConfirmPurchaseButtonActions — exactly what
// the popup's own +/- and confirm buttons call (see [[feedback-drive-game-ui-not-ops]]).
//
// Quantity moves with Left/Right OR Up/Down OR +/- (it's a single stepper, so every axis adjusts it);
// Enter confirms, Escape cancels. Confirmation feedback: on a successful craft (owned count rose) we
// set JustCrafted so the card-detail handler underneath can say "Crafted" before re-reading the now-
// owned card — gated to the deck-editor context so a Card Dex craft can't strand the flag.
internal sealed class CraftCardOverlayScreen : IScreenHandler
{
    // Set true when a craft just SUCCEEDED with the deck-editor card detail open beneath us. Read +
    // cleared by AddCardOverlayScreen.OnResume.
    public static bool JustCrafted;

    private static CraftCardOverlayScreen? _instance;

    private readonly ScreenRouter _router;
    private CardPurchasePopupParts? _popup;
    private bool _active;
    private bool _confirming;       // a purchase is in flight — ignore input until it resolves
    private int _ownedAtConfirm = -1;

    // Exact popup state read straight off its private ints — more reliable than parsing the formatted,
    // sprite-bearing LocText. cardCount = copies to craft, numOwned = owned, calculatedCurrencyAmount =
    // total cost in Trade Currency for cardCount copies.
    private static readonly FieldInfo? CountField = AccessTools.Field(typeof(CardPurchasePopupParts), "cardCount");
    private static readonly FieldInfo? OwnedField = AccessTools.Field(typeof(CardPurchasePopupParts), "numOwned");
    private static readonly FieldInfo? CostField = AccessTools.Field(typeof(CardPurchasePopupParts), "calculatedCurrencyAmount");

    public string Name => "Exchange";

    public CraftCardOverlayScreen(ScreenRouter router)
    {
        _router = router;
        _instance = this;
    }

    public static void Install(HarmonyLib.Harmony harmony, ScreenRouter router)
    {
        new CraftCardOverlayScreen(router);

        var show = AccessTools.Method(typeof(CardPurchasePopupParts), "ShowPurchasePopup");
        var hide = AccessTools.Method(typeof(CardPurchasePopupParts), "HidePurchasePopup");
        if (show == null || hide == null)
        {
            Log.Error("CraftCardOverlayScreen: could not resolve CardPurchasePopupParts Show/HidePurchasePopup");
            return;
        }
        harmony.Patch(show, postfix: new HarmonyMethod(AccessTools.Method(typeof(CraftCardOverlayScreen), nameof(Show_Postfix))));
        harmony.Patch(hide, postfix: new HarmonyMethod(AccessTools.Method(typeof(CraftCardOverlayScreen), nameof(Hide_Postfix))));
        Log.Info("CraftCardOverlayScreen installed");
    }

    private static void Show_Postfix(CardPurchasePopupParts __instance)
    {
        if (_instance == null) return;
        _instance._popup = __instance;
        _instance._confirming = false;
        _instance._ownedAtConfirm = -1;
        if (!_instance._active) { _instance._active = true; _instance._router.Activate(_instance); }
        else _instance.Announce(interrupt: true);
    }

    private static void Hide_Postfix(CardPurchasePopupParts __instance)
    {
        if (_instance == null || _instance._popup != __instance || !_instance._active) return;
        // A purchase we issued that bumped the owned count = a successful craft. (Independent of
        // _confirming, which an intervening error/pending overlay's resume may already have cleared.)
        if (_instance._ownedAtConfirm >= 0 && _instance.Owned() > _instance._ownedAtConfirm
            && (AddCardOverlayScreen.IsOpen || CardDexCardOverlayScreen.IsOpen))
            JustCrafted = true;
        _instance._active = false;
        _instance._confirming = false;
        _instance._ownedAtConfirm = -1;
        _instance._router.Deactivate(_instance);
        _instance._popup = null;
    }

    public void OnEnter() => Announce(interrupt: true);
    public void OnResume() { _confirming = false; Announce(interrupt: true); }   // back from an error overlay
    public void OnExit() { }
    public void RespeakAll() => Announce(interrupt: false);

    public string StateSignature() => _popup == null ? "Craft|dead" : $"Craft|{Count()}|{Cost()}|{_confirming}";

    public void Tick()
    {
        if (_popup == null || !IsOpen())
        {
            _active = false; _router.Deactivate(this); _popup = null;
            return;
        }
        if (!Application.isFocused) return;

        if (UnityEngine.Input.GetKeyDown(KeyCode.Escape) || UnityEngine.Input.GetKeyDown(KeyCode.Backspace)) { Close(); return; }
        if (_confirming) return;   // mid-purchase: ignore further input until the popup resolves

        if (UnityEngine.Input.GetKeyDown(KeyCode.RightArrow) || UnityEngine.Input.GetKeyDown(KeyCode.UpArrow)
            || UnityEngine.Input.GetKeyDown(KeyCode.Equals) || UnityEngine.Input.GetKeyDown(KeyCode.KeypadPlus)
            || UnityEngine.Input.GetKeyDown(KeyCode.Plus)) { Step(+1); return; }
        if (UnityEngine.Input.GetKeyDown(KeyCode.LeftArrow) || UnityEngine.Input.GetKeyDown(KeyCode.DownArrow)
            || UnityEngine.Input.GetKeyDown(KeyCode.Minus) || UnityEngine.Input.GetKeyDown(KeyCode.KeypadMinus)) { Step(-1); return; }
        if (UnityEngine.Input.GetKeyDown(KeyCode.Return) || UnityEngine.Input.GetKeyDown(KeyCode.KeypadEnter)) { Confirm(); return; }
    }

    // currentlyVisible flips synchronously in Show/Hide (the GameObject only goes inactive after the
    // hide animation), so it's the reliable "is the popup up" signal.
    private bool IsOpen()
    {
        if (_popup == null) return false;
        try { var a = _popup.AnimatedSetActive; if (a != null) return a.currentlyVisible; } catch { }
        try { return _popup.gameObject.activeInHierarchy; } catch { return false; }
    }

    private void Close() { try { _popup?.HidePurchasePopup(); } catch { } }   // Hide_Postfix tears us down

    private void Step(int dir)
    {
        if (_popup == null) return;
        int before = Count();
        try { if (dir > 0) _popup.IncreaseCardCountActions(); else _popup.DecreaseCardCountActions(); } catch { }
        int after = Count();
        if (after == before) { _router.Speech.Say(dir > 0 ? "Maximum" : "Minimum", interrupt: true); return; }
        _router.Speech.Say(CountAndCost(), interrupt: true);
    }

    private void Confirm()
    {
        if (_popup == null) return;
        _confirming = true;
        _ownedAtConfirm = Owned();
        _router.Speech.Say("Exchanging", interrupt: true);
        try { _popup.ConfirmPurchaseButtonActions(); } catch { _confirming = false; }
    }

    private void Announce(bool interrupt)
    {
        if (_popup == null) { _router.Speech.Say(Name, interrupt); return; }
        var sb = new StringBuilder(Name).Append(". ").Append(CountAndCost());
        int owned = Owned();
        if (owned >= 0) sb.Append(", ").Append(owned).Append(" owned");
        _router.Speech.Say(sb.ToString(), interrupt);
    }

    // "3 cards, cost 225 Credits" (singular "1 card"). Cost is the popup's own computed total.
    private string CountAndCost()
    {
        int n = Count(), cost = Cost();
        var s = n == 1 ? "1 card" : $"{n} cards";
        if (cost > 0) s += $", cost {cost} {CurrencyName()}";
        return s;
    }

    private int Count() => ReadInt(CountField, 1);
    private int Owned() => ReadInt(OwnedField, -1);
    private int Cost() => ReadInt(CostField, -1);

    private int ReadInt(FieldInfo? f, int dflt)
    {
        try { return _popup != null && f != null && f.GetValue(_popup) is int v ? v : dflt; }
        catch { return dflt; }
    }

    // The localized currency name (loc currency_tc); "Credits" is the English scaffold fallback,
    // consistent with the mod's other pending-localization connective words.
    private static string CurrencyName()
    {
        try
        {
            var s = LocalizationManager.Instance?.GetString("currency_tc");
            if (!string.IsNullOrWhiteSpace(s) && !s!.Contains("(Missing)")) return s.Trim();
        }
        catch { }
        return "Credits";
    }
}
