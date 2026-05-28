# TODO — outstanding issues & plan forward

Snapshot as of 2026-05-28. Things that work and are wired in are in `CLAUDE.md`'s status section — this file is for what's broken or unfinished, with enough context that a fresh session can pick up where this one left off without re-deriving anything.

## What's working end-to-end

- Language Select (first launch), confirmation modal, loading screen, consent flow (Terms / Analytics / Migration sub-steps), SimpleMessage popups, ModalElementContainer popups (SKIP MIGRATION etc.), Professor dialogue chain (FTUE welcome bubbles).
- `KeyboardNavigator` auto-detects axes from item bounding box, supports group names, Space-toggles-checkboxes, Esc/Backspace = back per-handler, no-wrap arrows, raycast-blocked filter, focus regain on window-focus return only when state changed.
- Junction-based dev path (`C:\PokemonTCGLive` → `…\Pokémon Trading Card Game Live\`) so MelonLoader sees an ASCII path while PTC OAuth keeps working against the registered install.

## Issue 1 — "Friends, 1 of 1" leaks during FTUE dialogue transitions

**Symptom:** while the FTUE dialogue chain is playing, the underlying `HUBProfileScreen` briefly becomes the top of the stack between dialogue messages (~50ms gap from `HideDialogue` → next `StartDialogue`). During that gap it speaks the focused item label, then the dialogue interrupts. The user hears half a word of the underlying screen between every dialogue message.

**What we tried that didn't work:**
- 200ms `Time.unscaledTime` deferral on the OnResume announce — **workaround, violates the no-timers rule**. Reverted.
- `FTUEManager.runFTUE` suppression — works for the gap, but `runFTUE` stays true through the post-dialogue "waiting for user to click HOME" state. Side effect: HUBProfile stays silent after FTUE dialogues end and the user is stuck without speech.
- Structure-signature compare on `OnResume` (current code) — meant to be "if nothing changed during pause, stay silent". In practice the signature changes between dialogue messages too (some sub-element activates/deactivates mid-flow), so the leak is back.

**What we need to actually dig into:**

1. `DialogueManager.StartDialogue` / `HideDialogue` paths — confirm exactly what changes on the underlying `HUBProfileScreenController` between adjacent dialogues. The structure signature is being too sensitive to *something* — find what.
2. `OverlayManager.BlockInput(state)` — line 310 of `OverlayManager.cs`. Called with `true` in `StartDialogue` (line 247) and `false` in `NextSpeaker` (line 427). That's exactly the "is the underlying screen meant to be deaf right now" signal — but the `hardBlock` field is private. Options:
   - Reflection on `hardBlock` (acceptable — it's a query of the game's own state)
   - Reflection on `inputBlocker.raycastTarget` (the Image is what `BlockInput` toggles — `inputBlocker.raycastTarget == true` = blocked)
   - Hook `BlockInput` itself with a Harmony prefix to track the latest value in a mod-side bool
3. Once we have a clean "is overlay input currently blocked" signal, suppress underlying-screen speech on that signal *only* (not on `runFTUE`). When `BlockInput(false)` fires at `HideDialogue`, the underlying screen is genuinely active and we should speak. When `BlockInput(true)` fires at the next `StartDialogue` (~50ms later), we suppress; the speech we may have just started can be `Speech.Stop()`-ed.
4. Even better: **stop in-flight speech when the dialogue handler activates**. Our `SpeechEngine.Stop()` already exists (used on focus-loss). When `DialogueScreen` pushes onto the stack, immediately call `_router.Speech.Stop()` to cut any underlying announcement mid-word. Combined with the BlockInput suppression, the gap leak goes away.

## Issue 2 — Profile screen never announces after FTUE finishes

**Symptom:** after the FTUE dialogues complete ("Click the Home button when you're done"), the player is dropped onto an interactive Profile screen but the mod stays silent. No header, no focus announcement, no nav.

**Root cause:** my BuildGroups runs once at `OnActivate` (227ms before the first FTUE dialogue), when the HUB top tab buttons are still `activeInHierarchy = false` (the game disables them during FTUE setup). I caches that "no tabs" state and don't rebuild correctly when the game re-enables them. The structure-signature gating in `OnResume` is supposed to detect this, but the user reports it doesn't fire.

**What we know from the diagnostic log:**
```
HUBTopController_L found=True
persistentNavButtons=6
nav 'HomeTab': pnbActive=True btnActive=False     ← parent active, button inactive
nav 'DecksTab': pnbActive=True btnActive=False
... (all six tabs the same)
gridButtons=9
others=19  (catch-all included NameInputField, CategoryFace/Hair duplicates — fixed in current code)
total groups: 3
```

**What we need to dig into:**

1. **When exactly do the HUB top buttons become active in hierarchy?** Search `HUBTopController_L.cs` and `MainMenuController.cs` for `.SetActive(true)` calls on `persistentNavButtons[*].navButton.gameObject`. Find the method or animator transition that enables them. Hook *that* — when those buttons activate, that's the signal to rebuild our nav groups and announce.
2. **Does the game expose an `OnHubReady` / `OnNavReady` event?** Look at `HUBTopController_L.SetupPersistentNavIfNotready` (line 196 per the earlier research). Trace what calls it and when.
3. The `ftueManager.runFTUE` flag stays true past the end of dialogues. Investigate `FTUEManager.EndFTUE` (line 683) — what triggers it? If it's "user clicks HOME", then by the time it fires the user has already navigated away. We need a signal *before* that, when the post-dialogue interactive state begins.
4. As a fallback: poll `HUBTopController_L.persistentNavButtons[0].navButton.gameObject.activeInHierarchy` every Tick — when it flips true, rebuild + announce. This is property-polling on a game-maintained state, which is acceptable (not a timer; it's a state-change detector).

## Issue 3 — Avatar customization detail picker not yet handled

**Symptom:** clicking FACE / HAIR / GLASSES / HATS / TOPS / BOTTOMS / SHOES / CATCHPHRASES / POSES via mouse opens the detail picker visually, but our mod has no handler for it. Once the user backs out of the detail picker, the underlying 3×3 grid is reportedly not interactive anymore.

**Research already done** (full report in transient agent output — key points captured below for next session):

- **Driver:** `HUBProfileController` (TPCI.RainierClient/HUBProfileController.cs, 1315 lines).
- **Entry method:** `SelectCategory(AvatarCustomizationType category)` line 611. Sets `_categoryTitle` (LocText with `avatar_face` / `avatar_hair` / `avatar_glasses` / `avatar_hats` / `avatar_tops` / `avatar_bottoms` / `avatar_shoes` / `avatar_poses` / `avatar_catchphrase` loc IDs depending on category), populates `_currSortedItemData` (List<AvatarCustomizationData>).
- **State change:** `SetState(category)` line 1108 → `_itemsMenu.SetInteger("State", (int)CurrentAvatarUIState.FullOpen)` — animator transitions Closed → HalfOpen → FullOpen.
- **Item list — grid categories** (FACE / HAIR / GLASSES / HATS / TOPS / BOTTOMS / SHOES / POSES): backed by `_avatarEnhancedScroller` (Echo17 EnhancedScroller). Visible rows come from `_avatarEnhancedScroller.ActiveCellViews` cast to `AvatarCustomizationRowView`; each row exposes `Items` (List<CustomizationItem>). `_itemsPerRow` calculated at init.
- **Item list — phrase categories** (CatchphraseIntro / CatchphraseVictory / AnimIntro / AnimVictory): uses `_phraseScrollRect` + `_spawnedPhraseCustomizations` (List<CustomizationItem>) instead of the EnhancedScroller. `IsPhraseCategory(category)` is the discriminator (line 202).
- **Cross-mode helper:** `GetItemFromIndex(int idx)` (line 1015) returns the `CustomizationItem` for an index regardless of grid vs phrase mode.
- **Activate item:** `SelectItemByIndex(int idx, bool forceFocusedSelection = true)` line 769 — the game's own commit path. Handles owned-vs-purchasable (fires the purchase modal for unowned items via `ShowConfirmPurchasePopup`).
- **Back out to 3×3 grid:** `AvatarGroupObjectsController.CloseList()` → `_hubProfileController.SetState(AvatarCustomizationType.None)` → animator returns to Closed.
- **Scroll-to-focused:** for phrases, `_phraseScrollRect.SmoothSnapToTarget(rectTransform, autoScrollCurve, autoScrollDuration)` is the game's own method (line 1255). For the EnhancedScroller grid, need to investigate its `JumpToDataIndex` / scroll API or compute target row index and set `ScrollPosition`.

**Handler plan:**

1. New `Screens/HUBProfileDetailScreen.cs` — patches `HUBProfileController.SelectCategory` (postfix) to activate, and `HUBProfileController.SetState(AvatarCustomizationType.None)` (or `AvatarGroupObjectsController.CloseList`) to deactivate.
2. On enter: read `_categoryTitle`'s rendered TMP for the localised category name. Speak it as the screen header (game-native, no hardcoded English).
3. Build the item list as a flat single group. For grid mode: iterate `_currSortedItemData`, render labels from each `AvatarCustomizationData.ID` via `LocalizationManager.GetString`. For phrase mode: same iteration. Don't depend on EnhancedScroller's visible cells — those are just the rendered subset of the data.
4. Internal focus index drives:
   - Speech: `_currSortedItemData[i].ID` → loc string → speak with `i+1 of count` suffix
   - Selection: call `_hubProfileController.SelectItemByIndex(i)` on Enter
   - Scroll-to-focused: call `_avatarEnhancedScroller`'s scroll-to-row method (need to identify exact API) or `SmoothSnapToTarget` for phrases
5. Esc/Backspace: call `_hubProfileController.SetState(AvatarCustomizationType.None)` to return to the 3×3 grid.
6. After deactivation, `HUBProfileScreen` is back on top of the stack and its `OnResume` fires — should re-detect that the grid items are interactive again and announce.

## Issue 4 — Grid items not interactive after returning from detail picker

**Symptom:** user reports that after opening (e.g.) HAIR detail picker and returning to the Profile screen, the 3×3 grid is no longer interactive via keyboard.

**Suspected cause:** when the detail picker opens, no handler activates (we don't have `HUBProfileDetailScreen` yet). `HUBProfileScreen` stays on top of the stack. The grid items become inactive in hierarchy when the picker opens, our `IsActiveInHierarchy` filter starts skipping them, but our existing `_groups` still hold references to the now-inactive Selectables and we never rebuild. When the picker closes the items reactivate but our focus state hasn't recovered.

**Fix path (depends on Issue 3):**

- Once `HUBProfileDetailScreen` exists, opening the detail picker pushes it on the stack — `HUBProfileScreen` pauses via `OnExit`, structure signature captured at that moment.
- Closing the picker pops the detail handler. `HUBProfileScreen.OnResume` rebuilds groups (current code already does). Grid items are reactivated, so the rebuild picks them up.
- Verify the user can navigate the 3×3 grid after the round-trip.

## Plan forward (priority order)

1. **Dig into `OverlayManager.BlockInput` to fix the dialogue-gap leak.** Either reflect on `inputBlocker.raycastTarget` or Harmony-prefix-patch `BlockInput` to track the value in a mod-side bool. Suppress underlying-screen speech when blocked. Bonus: also call `_router.Speech.Stop()` when `DialogueScreen` pushes onto the stack, so any in-flight announcement is cut cleanly.
2. **Find the signal for "HUB top tab buttons just activated".** Likely path: hook a method on `HUBTopController_L` or `MainMenuController`, or detect `persistentNavButtons[0].navButton.gameObject.activeInHierarchy` flipping true. Trigger a rebuild + announce on that edge.
3. **Build `HUBProfileDetailScreen` handler** per Issue 3's plan. This fixes both the detail picker accessibility *and* Issue 4 (grid re-interactivity).
4. **Promote the structure-signature pattern (or whatever replaces it) to a memory rule** once it's stable — every screen that can be re-entered will need similar logic.
5. **End-user-setup.md still needs an AMM integration pass** — `research/end-user-setup.md` §7 has the checklist.

## Reference — where everything lives

- Handlers: `Screens/{Language,SimpleMessage,Loading,Consent,ModalContainer,ProfessorTooltip,Dialogue,HUBProfile}Screen.cs`
- Router + interface: `Screens/{ScreenRouter,IScreenHandler}.cs`
- Universal kb nav: `Navigation/KeyboardNavigator.cs`
- Hooks: `Hooks/LocalizationHook.cs`
- Speech: `Speech/{SpeechEngine,Prism,TextProcessor,Announce}.cs`
- Util: `Util/{Log,WindowFix}.cs`
- Per-screen design questions: `QUESTIONS-002-ui-navigation.md`
- Memory (cross-session): `C:\Users\Amethyst\.claude\projects\C--Users-Amethyst-projects-Pokemon-TCG-live-mod\memory\MEMORY.md` and per-topic files alongside it.

## Decompiled cheatsheet — files to grep first

- `decompiled/TPCI.RainierClient/OverlayManager.cs` — `BlockInput(bool)` line 310, `hardBlock` private field line 86, `inputBlocker` field
- `decompiled/TPCI.RainierClient/DialogueManager.cs` — `StartDialogue` line 247 (calls BlockInput(true)), `NextSpeaker` line 427 (calls BlockInput(false))
- `decompiled/TPCI.RainierClient/HUBTopController_L.cs` — `persistentNavButtons` line 34, `SetupPersistentNavIfNotready` line 196
- `decompiled/TPCI.RainierClient/HUBProfileScreenController.cs` — `OnActivate` line 139, `friendsButton` line 60
- `decompiled/TPCI.RainierClient/HUBProfileController.cs` — `SelectCategory` line 611, `SelectItemByIndex` line 769, `SetState` line 1108, `_categoryTitle` line 65, `_currSortedItemData` line 88, `_avatarEnhancedScroller` line 41
- `decompiled/TPCI.RainierClient/FTUEManager.cs` — `runFTUE` line 252, `HasCurrentlyRunningState` line 854, `DisableFtue` line 707
