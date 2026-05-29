# Pokémon TCG Live — Accessibility Mod

Private screen-reader accessibility mod for **Pokémon TCG Live** (Unity 6.3.5f2, Mono).

## Project Goal
Give blind / visually-impaired players the same gameplay experience as sighted players, including online matches. No cheating — just speech and keyboard access for everything the game already offers visually.

## Status (initialised 2026-05-27)
- **Phase 0 (foundation):** scaffold complete, builds clean, loads in game, speech works, window accessibility solved (NVDA can focus and OCR the game window).
- **Phase 1 (front-of-house — title, language select, main menu, settings, login):** not started.
- **Phase 2 (deck builder):** not started.
- **Phase 3 (battle):** not started.
- **Phase 4 (shop, quests, profile, tournaments, online plumbing):** not started.

See `../research/QUESTIONS-001-initial-setup.md` for the locked-in design decisions.

---

## Repos & Paths

### This Repo (mod source)
- **Local:** `C:\Users\Amethyst\projects\Pokemon TCG live\mod`
- **GitHub:** https://github.com/RealAmethyst/pokemon-tcg-access (private)
- Single C# project: `PokemonTCGLiveAccessibility.csproj` (net472, SDK-style)

### Research & Data (NOT in git, local only)
- **Local:** `C:\Users\Amethyst\projects\Pokemon TCG live\research`
- `research/decompiled/` — 60 game-specific DLLs decompiled with `ilspycmd` (~26 MB, 5,534 .cs files)
- `research/architecture-notes.md` — first-pass architecture survey
- `research/launching-the-game.md` — launch flags, window-focus workarounds, troubleshooting
- `research/QUESTIONS-001-initial-setup.md` — locked-in design Q&A (carry over to future sessions for context)
- `research/prism-source/` — clone of `https://github.com/ethindp/prism` for building `prism.dll`
- `research/build-prism.bat` — rebuilds `prism.dll` via CMake under vcvars64
- `research/decompile.sh` / `decompile.log` — bash script to redecompile all DLLs
- `research/prism.h` — saved Prism C header for API reference

### Game Install (via NTFS junction — see Gotchas #1)
- Dev paths reference `C:\PokemonTCGLive` (junction → real install). The real install on disk is
  at `C:\Users\Amethyst\The Pokemon Company International\Pokémon Trading Card Game Live\`.
- Mods go in `<game>\Mods\`
- `prism.dll` lives directly in `<game>\` next to `Pokemon TCG Live.exe`
- MelonLoader's own files in `<game>\MelonLoader\`, log at `<game>\MelonLoader\Latest.log`
- Loader config at `<game>\UserData\Loader.cfg` (we've set `hide_console = true`)

---

## Stack

| Layer | Choice |
|---|---|
| Mod loader | MelonLoader 0.7.3 (Open-Beta, Mono support) |
| Mod target framework | `net472` (Unity's Mono runtime) |
| Patching | Harmony (`HarmonyLib.Harmony`, bundled by MelonLoader's `LavaGang.MelonLoader` NuGet) |
| Speech | Prism (C, MPL-2.0) — built locally as `prism.dll`, P/Invoked directly |
| Build | `dotnet build -c Release` (no extra tooling needed) |

Why direct Prism P/Invoke and not the .NET Prismatoid binding? Prismatoid targets `net10.0` which won't load in Unity Mono. Direct P/Invoke avoids the cross-runtime barrier. The full Prism C API is ~30 functions; we bind only what we use in `Speech/Prism.cs`.

---

## Architecture / Hookpoints

| Purpose | Hook target | File reference (in `research/decompiled`) |
|---|---|---|
| Universal text capture | `LocalizationManager.GetString` (postfix) | `TPCILocalization/TPCI/Localization/LocalizationManager.cs` |
| Screen transitions | `MainMenuNavigation.GoToScreen` (postfix) | `TPCI.RainierClient/...` |
| Bootstrap timing | `RainierManager.Awake` (postfix) | `TPCI.RainierClient/RainierManager.cs` |
| Card data lookup | `CardDatabaseRepository.GetCardDataRow` (postfix) | `CardDatabase.DataAccess/.../CardDatabaseRepository.cs` |
| Focus tracking | `EventSystem.current.currentSelectedGameObject` polling; also Unity's `Selectable` chain | uGUI standard |
| Match lifecycle | `NetworkMatchController` static methods | `TPCI.RainierClient/NetworkMatchController.cs` |

UI framework: pure uGUI (Canvas + RectTransform + TextMeshProUGUI). **No UI Toolkit.** Input: legacy `UnityEngine.Input` only — no new InputSystem package. Game is **mouse-primary**; no controller / keyboard navigation system exists, so we synthesize our own (see `Navigation/`).

Full architecture survey: `../research/architecture-notes.md`.

---

## Source layout

```
mod/
├── PokemonTCGLiveAccessibility.csproj   # net472, SDK-style
├── PokemonTCGLiveAccessibility.sln
├── Directory.Build.props                # $(GameDir) — override on cmd line if needed
├── Main.cs                              # MelonMod entry; wires SpeechEngine, ScreenRouter, hooks
├── Speech/
│   ├── Prism.cs                         # P/Invoke bindings for prism.dll
│   ├── SpeechEngine.cs                  # init / shutdown / Say
│   └── Announce.cs                      # "item, description, X of Y" formatter
├── Hooks/
│   └── LocalizationHook.cs              # Harmony postfix on LocalizationManager.GetString; loc cache
├── Navigation/
│   └── KeyboardNavigator.cs             # reusable kb nav: arrows/tab/enter → Selectable.Select + Button.onClick.Invoke
├── Screens/
│   ├── IScreenHandler.cs                # per-menu handler interface
│   ├── ScreenRouter.cs                  # owns the active handler, ticks it
│   └── LanguageSelectScreen.cs          # first concrete handler (LanguageSelect.Initialize patch)
├── Util/
│   ├── Log.cs                           # MelonLogger wrapper
│   └── WindowFix.cs                     # WS_POPUP→overlapped + WndProc subclass for WM_GETOBJECT
├── vendor/
│   └── prism/prism.dll                  # gitignored; built copy of prism.dll for reference
└── CLAUDE.md                            # you are here
```

Per-menu handlers live in `Screens/` and each one installs its own Harmony patch on whichever class actually drives that screen in the decompiled source. They share `KeyboardNavigator` for the universal "read arrow keys → Selectable.Select → Button.onClick" loop, since the game itself has zero keyboard input plumbing.

---

## Build & Deploy

### Build
```powershell
dotnet build -c Release
```
Output: `bin\Release\PokemonTCGLiveAccessibility.dll`.

### Deploy (one-shot)
```powershell
Copy-Item bin\Release\PokemonTCGLiveAccessibility.dll `
  "C:\PokemonTCGLive\Mods\PokemonTCGLiveAccessibility.dll" -Force
```
Game must be closed (Windows holds a file lock while loaded).

### First-time install (already done)
1. MelonLoader 0.7+ installed into the game folder via official installer.
2. `prism.dll` placed in `<game>\` next to `Pokemon TCG Live.exe`. To rebuild: run `..\research\build-prism.bat` (uses vcvars64 + CMake).

### Rebuild prism.dll
```powershell
..\research\build-prism.bat
Copy-Item ..\research\prism-source\build\Release\prism.dll `
  "C:\PokemonTCGLive\prism.dll" -Force
```

---

## Critical Gotchas (don't trip on these again)

1. **Game install path tug-of-war: PTC OAuth requires the registered install path, MelonLoader has Mono-runtime issues with non-ASCII paths.** The PTC launcher's true default install path is `C:\Users\<user>\The Pokémon Company International\Pokémon Trading Card Game Live\` — **both** folder segments contain `é`. On this dev machine the outer folder happens to be ASCII (`The Pokemon Company International`) because it was manually renamed before a game repair; that's why MelonLoader currently loads here. Most players will have the double-`é` default. Original failure (2026-05-27): MelonLoader's `plthook_open` failed on the double-`é` path (see [MelonLoader issue #978](https://github.com/LavaGang/MelonLoader/issues/978)). We then moved the install to a fully-ASCII path; that broke PTC OAuth because the URL scheme handler is registered against whatever path the PTC launcher installed to — moving the install means the OAuth callback fires a stale path. Resolution: keep the install at the PTC default path, install MelonLoader through an NTFS junction at an ASCII alias (`C:\PokemonTCGLive` → real install). Junction is the same physical directory, so OAuth keeps working. Full end-user procedure: `../research/end-user-setup.md`.

2. **`[MelonGame]` attribute must match `<game>\Pokemon TCG Live_Data\app.info` exactly.** That file says `pokemon\nPokemon TCG Live` (lowercase dev, no accents). The marketing names ("The Pokémon Company International" / "Pokémon Trading Card Game Live") would make MelonLoader reject the mod as incompatible.

3. **Prism init quirks.** Use `prism_registry_create_best`, not `acquire_best`. Treat `PRISM_ERROR_ALREADY_INITIALIZED` as a success result, not an error. Some backends (NVDA, JAWS via their RPC bridges) come pre-initialised from the registry. See `Speech/SpeechEngine.cs`.

4. **Unity 6 suppresses `WM_GETOBJECT`.** Unity 6's WndProc actively returns nothing for accessibility queries, so screen readers see Unity windows as opaque. Fix is implemented in `Util/WindowFix.cs`: subclass Unity's WndProc via `SetWindowLongPtr`, route `WM_GETOBJECT` to `DefWindowProc` instead of the original Unity proc. Older Unity (Master Duel etc.) does not have this regression.

5. **Console hidden.** `<game>\UserData\Loader.cfg` has `hide_console = true` — without this, the MelonLoader console steals alt-tab focus on launch.

6. **`prism.dll` location matters.** Must be in `<game>\` (next to the exe), not in `<game>\Mods\`. P/Invoke resolves it relative to the executable's directory.

7. **Don't use file-scoped namespace + `using HarmonyLib` together.** There's a top-level `Harmony` namespace that collides with `HarmonyLib.Harmony` when the using is in scope. Fully-qualify as `HarmonyLib.Harmony` in parameter types — see `Hooks/*Hook.cs`.

---

## How to Find Things in Decompiled Code

```powershell
# Search for a class / method name:
rg "class LocalizationManager" ..\research\decompiled\TPCILocalization
# Find files containing a string the game might display:
rg "Select Language" ..\research\decompiled
# Find Harmony patch targets by namespace:
ls ..\research\decompiled\TPCI.RainierClient\TPCI\Rainier
```
All decompilation done with `ilspycmd` (in `C:\Users\Amethyst\.dotnet\tools\ilspycmd.exe`). Per-DLL folders are compilable projects (we won't compile them — they're for reference).

---

## Workflow Conventions (durable preferences)

- **No workarounds. Use the game's own systems.** The decompiled code in `../research/decompiled/` is the definitive source of truth — every screen, button, focus model, and input path is already in there. Before writing any nav/focus/input logic, find what the game uses natively (uGUI `Selectable.navigation`, `EventSystem.SetSelectedGameObject`, `StandaloneInputModule`, `FocusedElement`, button `OnClick` UnityEvents, etc.) and hook *those*. Don't synthesize parallel systems just because the first guess didn't work — re-read the decompiled code until you find the right hook. Synthetic fallbacks (HoverableScanner-style) are only a last resort for screens where the game truly has nothing.
- **Designer / coder split.** Amethyst designs, Claude codes. Open design questions go in `mod/QUESTIONS.md` (then move to `../research/` once answered).
- **Mod folder is the git repo.** `mod/` only contains code. `research/` is local-only (not in git), holds decompiled source, notes, archived questions, the Prism clone, build scripts.
- **Speech format default:** `"<item name>[, <description>], <index> of <total>"` — single speech call per item. Shops/options screens are exceptions because they involve prices/values. Cards are their own domain (deferred to Phase 3).
- **Keyboard focus discovery heuristic:** "if it's hover/click-interactive for a sighted player, it's focusable for us." Scan for `Selectable`, `IPointerClickHandler`, etc. — see `Navigation/HoverableScanner.cs`.
- **No open-sourcing.** GitHub repo is private; used as a backup / rollback mechanism, not for distribution.
- **Distribution:** Amethyst has a separate tool that packages mods as `<name>-vX.Y.Z-amm.zip` for the Amethyst Mod Manager. We don't worry about packaging here.

---

## What to do next

**Phase 1 — front-of-house. Avatar customization complete (2026-05-29). Next: remaining HUB screens.**

The outer Profile screen and every inner avatar customization category panel are now functional. Both use the section-stack nav model (see `memory/feedback_section_escape_nav.md`):
- Outer Profile: top tab strip ↕ Friends ↕ avatar grid ↕ catch-all (Customize/Stats/SwapAvatar)
- Category panels: Back ↕ Tabs ↕ Colors ↕ Slider ↕ Items, with each middle section present only when the panel has it (Intro/Victory tabs for Catchphrase + Anim; Face/Eyes tabs for Face; Frames/Lenses tabs for Glasses; skin/eye/hair color swatches for Face & Hair; hair hue slider for Hair; frames+lens sliders for Glasses).

**Completed (this session, 2026-05-29):**

1. **Face / Eyes sub-tabs** in `AvatarCategoryScreen` — **DONE**. Implementation differs from the
   originally-sketched design because the game's Face/Eyes pair is structurally distinct from
   Intro/Victory: it lives inside `AvatarGroupObjectsController._faceSelectorGameObject` (not
   as siblings of `_avatarEnhancedScroller`), uses plain `Toggle` components, and activating
   "Eyes" toggles the color group's visibility without calling `SelectCategory(Eyes)`. The
   handler discovers the pair via the `_faceSelectorGameObject` anchor and re-announces
   explicitly after Face/Eyes activation since `SelectCategory_Postfix` won't fire. See
   [[project-avatar-panel-components]] for full details.

2. **Skin-tone / eye-color strip nav for Face** — **DONE**. Added `Section.Colors` between
   Tabs and Items in `AvatarCategoryScreen`. Discovery reads the currently-visible group
   from `AvatarGroupObjectsController._colorGroupObjectDict` (whichever
   `ColorGroupObjects._localGameObject.activeInHierarchy == true`) at section-entry time, so
   swapping the Face/Eyes tab automatically picks up the new group's swatches. Activation
   sets `swatch.isOn = true`, routing through the game's existing color-toggle handlers. See
   [[feedback-color-section-nav]].

   **Hair color** (task #4) and **Glasses frames/lenses + sliders** (task #5) are still pending —
   each has an extra widget (Hair has a hue slider; Glasses has frames/lenses sub-tabs plus
   colour & alpha sliders) that needs a dedicated nav model.

3. **Back button as topmost section** — **DONE**. Section.Back wired into the existing escape
   chain (Items → Colors → Tabs → Back). Activation invokes
   `AvatarGroupObjectsController._closeListButton.onClick`, which routes through `CloseList`
   for the full save-and-tear-down flow. Important gotcha logged in
   [[feedback-inactive-click-endpoints]]: that Button reference is normally inactive — it's a
   click-handler endpoint, not the visible back arrow — so `BackAvailable` gates on non-null
   only, not `isActiveAndEnabled`.

4. **Hair color + hue slider** — **DONE**. Section.Slider added between Colors and Items in
   the stack. Discovery reads `AvatarGroupObjectsController._colorHairSlider`; Left/Right
   adjusts by 5% steps; the game's own onValueChanged (OnProcessHairColorSlider) applies
   the color, so Enter is a no-op. See [[feedback-slider-section-nav]]. Section escape
   refactored to a single ordered-stack helper (`s_orderedSections` + `EscapeUp`/`EscapeDown`)
   so future categories just need their `HasX`/`Discover` pair.

5. **Glasses (frames/lenses tabs + 3 sliders)** — **DONE**. Adds Frames/Lenses sub-tab
   pair (same shape as Face/Eyes) via a generic `DiscoverPlainTogglePair` helper.
   Section.Slider extended to a list of sliders + index — Up/Down moves between stacked
   sliders within the section before escaping. Frames tab surfaces 1 slider (Frames
   colour); Lenses tab surfaces 2 (Lens colour + Lens transparency). DiscoverSlider runs
   on section entry, so flipping the tab repoints the slider list automatically.

**Pending (next pick-up):**

6. **FTUE tutorial battle intro overlay** — **DONE**. Hooks `FTUEManager.ChangeBattleText`
   (activate / refresh) + `FTUEManager.CloseBattleCanvas` (deactivate). Reads header
   ("BATTLE X OUT OF Y") + description from `FTUEManagerParts.battleCountText.CurrentText`
   / `battleText.CurrentText`. Three buttons (PLAY / SKIP THIS BATTLE / SKIP ALL BATTLES)
   navigated by Up/Down arrows in reading order — explicitly vertical-only, Tab disabled
   per [[feedback-arrows-over-tab]]. Enter routes through each button's onClick
   (PlayBattle / SkipThisBattle / SkipAllBattles).

7. **Battle — major focus for next session.** Phase 3 begins. The actual in-battle UI is
   the largest unhandled surface in the game: board zones (active, bench, prizes, hand,
   deck, discard), card detail / inspection, action selection (attack, retreat, play
   item, evolve), turn flow, opponent status. Battle is mouse-primary; board cards and
   hand cards aren't standard Selectables — needs new abstractions on top of the
   existing screen-handler model. Will likely involve patching `NetworkMatchController`
   or whatever drives match state, plus a board-zone nav model distinct from menus.
   Start by surveying `TPCI.RainierClient` for the match controller + card prefab
   structure before writing any handler.

8. **Remaining HUB screens** — HOME, DECKS, SHOP, BATTLE PASS, CARD DEX. Same handler
   pattern as Profile. Lower priority than battle but still needed for full HUB coverage:
   - `HUBHomeController` (HOME)
   - `HUBDeckSelectController` (DECKS)
   - `HUBShopController` (SHOP)
   - `HUBExpansionChallengeScreenController` (BATTLE PASS)
   - `HUBCardDexController` (CARD DEX)

9. **Verify WndProc subclass alone is sufficient** — strip the `WS_POPUP → WS_OVERLAPPEDWINDOW` style change from `WindowFix.cs` and confirm screen reader still works without the visible titlebar. Still pending from Phase 0.

**Phase 1.5 — login / consent / language picker flow:**
- `LanguageSelectScreen`, `ConsentScreen`, `LoadingScreen`, `DialogueScreen`, `SimpleMessageScreen`, `ModalContainerScreen`, `ProfessorTooltipScreen` are already implemented and working through the FTUE chain. Verify they still behave correctly after the recent group-name and filter changes.

**Phase 2 onward** (later sessions):
- Deck builder
- Match / battle UI (the big one — battle is mouse-primary with no keyboard nav, board cards/hand cards aren't standard Selectables)
- Shop, quests, online plumbing

Refer to `../research/architecture-notes.md` for hook target line numbers and rationale.

---

## Memory & persistent notes

Long-term durable knowledge for future Claude sessions lives at:
`C:\Users\Amethyst\.claude\projects\C--Users-Amethyst-projects-Pokemon-TCG-live-mod\memory\MEMORY.md`

That index points to per-topic memory files covering user preferences, project identity, design decisions, and gotchas. Keep it updated when something non-obvious happens.
