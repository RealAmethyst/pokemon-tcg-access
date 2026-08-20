using MelonLoader;
using PokemonTCGLiveAccessibility;
using PokemonTCGLiveAccessibility.Hooks;
using PokemonTCGLiveAccessibility.Screens;
using PokemonTCGLiveAccessibility.Speech;
using PokemonTCGLiveAccessibility.Util;

[assembly: MelonInfo(typeof(Mod), BuildInfo.Name, BuildInfo.Version, BuildInfo.Author)]
[assembly: MelonGame("pokemon", "Pokemon TCG Live")]

namespace PokemonTCGLiveAccessibility;

public static class BuildInfo
{
    public const string Name = "PokemonTCGLiveAccessibility";
    public const string Author = "RealAmethyst";
    public const string Version = "0.1.0";
}

public class Mod : MelonMod
{
    private SpeechEngine? _speech;
    private WindowFix? _windowFix;
    private ScreenRouter? _router;
    private bool _wasFocused = true;
    private string? _signatureAtFocusLoss;

    public override void OnInitializeMelon()
    {
        Log.Init(LoggerInstance);
        Log.Info($"{BuildInfo.Name} v{BuildInfo.Version} starting");

        _speech = new SpeechEngine();
        _speech.Start();

        _windowFix = new WindowFix();
        _router = new ScreenRouter(_speech);

        CurrencyReader.Install(_speech);
        PlayerNameReader.Install(_speech, _router);
        HubTabShortcuts.Install(_speech);

        LocalizationHook.Install(HarmonyInstance, _speech);
        MatchReadyHook.Install(HarmonyInstance);
        BattleLogHook.Install(HarmonyInstance, _speech);
        MatchIntroHook.Install(HarmonyInstance, _speech);
        RankUpHook.Install(HarmonyInstance, _speech);
        FTUEHook.Install(HarmonyInstance);
        FtueFocusFallback.Install(_router);

        LanguageSelectScreen.Install(HarmonyInstance, _router);
        SimpleMessageScreen.Install(HarmonyInstance, _router);
        LoadingScreen.Install(HarmonyInstance, _router);
        ConsentScreen.Install(HarmonyInstance, _router);
        ModalContainerScreen.Install(HarmonyInstance, _router);
        ProfessorTooltipScreen.Install(HarmonyInstance, _router);
        DialogueScreen.Install(HarmonyInstance, _router);
        HUBProfileScreen.Install(HarmonyInstance, _router);
        FriendsPanelScreen.Install(HarmonyInstance, _router);
        AddFriendOverlayScreen.Install(HarmonyInstance, _router);
        FriendOptionsOverlayScreen.Install(HarmonyInstance, _router);
        SendJoinMatchOverlayScreen.Install(HarmonyInstance, _router);
        MatchmakingOverlayScreen.Install(HarmonyInstance, _router);
        ConfirmOverlayScreen.Install(HarmonyInstance, _router);
        HUBHomeScreen.Install(HarmonyInstance, _router);
        HUBShopScreen.Install(HarmonyInstance, _router);
        HUBExpansionChallengeScreen.Install(HarmonyInstance, _router);
        HUBCardDexScreen.Install(HarmonyInstance, _router);
        CardDexCardOverlayScreen.Install(HarmonyInstance, _router);
        LeagueTowerScreen.Install(HarmonyInstance, _router);
        InfoPopupScreen.Install(HarmonyInstance, _router);
        BnbPopupScreen.Install(HarmonyInstance, _router);
        BnbLobbyScreen.Install(_router);
        ShopProductOverlayScreen.Install(HarmonyInstance, _router);
        BundlesOverlayScreen.Install(HarmonyInstance, _router);
        HUBDeckSelectScreen.Install(HarmonyInstance, _router);
        HUBEditDeckScreen.Install(HarmonyInstance, _router);
        LandscapeDeckOptionsScreen.Install(HarmonyInstance, _router);
        DeckNamePopupScreen.Install(HarmonyInstance, _router);
        ItemGrantScreen.Install(HarmonyInstance, _router);
        ErrorOverlayScreen.Install(HarmonyInstance, _router);
        RewardOverlayScreen.Install(HarmonyInstance, _router);
        SeasonResetOverlayScreen.Install(HarmonyInstance, _router);
        GiftOverlayScreen.Install(HarmonyInstance, _router);
        NewsOverlayScreen.Install(HarmonyInstance, _router);
        HUBOverflowMenuScreen.Install(HarmonyInstance, _router);
        HUBSettingsScreen.Install(HarmonyInstance, _router);
        HUBOverflowSubOverlayScreen.Install(HarmonyInstance, _router);
        LearningLabSection.Install(HarmonyInstance);
        AvatarCategoryScreen.Install(HarmonyInstance, _router);
        FTUEBattleIntroScreen.Install(HarmonyInstance, _router);
        BattleScreen.Install(HarmonyInstance, _router);
        BattlePromptScreen.Install(HarmonyInstance, _router);
        MulliganRevealScreen.Install(HarmonyInstance, _router);
        MatchResultScreen.Install(HarmonyInstance, _router);
        BattleSettingsScreen.Install(_router);
    }

    public override void OnUpdate()
    {
        _windowFix?.Tick();
        HandleFocusTransition();
        // Handlers that activate via async / coroutine-driven game state poll here each frame.
        ConsentScreen.Poll();
        // Friends slide-out has no clean open/close callback in landscape — detect it by geometry.
        FriendsPanelScreen.Poll();
        // Add-friend search isn't opened via OverlayManager (no OnActivate) — driven by its events.
        AddFriendOverlayScreen.Poll();
        // B&B run lobby is a standalone-scene MonoBehaviour (no OnActivate hook) — detect by polling.
        BnbLobbyScreen.Poll();
        _router?.Tick();
        // Global "read my balances" key (P) — acts only on screens showing the HUB currency bar.
        CurrencyReader.Tick();
        // "Read + copy my screen name" key (N) — scoped to the Profile + friends screens.
        PlayerNameReader.Tick();
        // Number-row (1-6) jumps straight to a HUB top-strip tab (HOME/DECKS/…) — only where the
        // strip is actually clickable (no-ops in battle, under a popup, or in a text field).
        HubTabShortcuts.Tick();
        // Global FTUE-focus fallback: surfaces the tutorial's highlighted element on screens we
        // don't handle yet. No-ops whenever a screen handler is active (checks _router.Active).
        FtueFocusFallback.Tick();
    }

    // Bridge: a screen may have activated / changed state while the user was on a browser
    // window during PTC login etc. On focus regain ask the top handler to re-announce its
    // full state — but only if the state actually changed while we were away. If the user
    // just alt-tabbed to Discord and back without anything happening in the game, we stay quiet.
    private void HandleFocusTransition()
    {
        var focused = UnityEngine.Application.isFocused;
        if (focused == _wasFocused) return;
        _wasFocused = focused;
        if (focused)
        {
            var now = _router?.Active?.StateSignature();
            if (now != null && now != _signatureAtFocusLoss)
                _router!.Active!.RespeakAll();
        }
        else
        {
            _speech?.Stop();
            _signatureAtFocusLoss = _router?.Active?.StateSignature();
        }
    }

    public override void OnApplicationQuit()
    {
        _speech?.Shutdown();
    }
}
