# Today's work — please test (2026-07-13)

Fixed and deployed. The DLL is already copied into the game, so just launch and test.

## Stadium ability wouldn't activate — Surfing Beach — fixed, please test

**What you reported.** In the tutorial, the Surfing Beach stadium wouldn't let you use its ability. The action menu opened and listed the ability, but it read as "unavailable" — wrong, because the tutorial was pointing right at it and wanting you to use it. You noted some other stadium abilities work, but this one never did.

**What was going on (found in the game's own code, not guessing).** Two different tutorial "spotlight" mechanisms exist. Most attack/ability steps arm one specific action a moment after you open the menu — and to stop you firing an attack in the split second before that arming finishes (which freezes the match), the mod holds the action until it's armed. That guard was written to assume every spotlight arms an action.

But the kind of spotlight used for Surfing Beach — the one for an ability that then opens a follow-up choice — never arms a specific action at all. It just points at the card and waits for you to use it. So the mod's guard sat there forever thinking "not armed yet" and greyed the ability out permanently. That's why this stadium never worked while simpler ones did: the simpler ones use the arming kind of spotlight.

**The fix.** The guard now only applies during the real "about to arm an attack/ability" window. For every other spotlight, the mod trusts the game's own on/off state for the button — the exact thing a sighted player's mouse obeys. So Surfing Beach's ability is usable the moment the game says it is. The attack-before-ready freeze protection is unchanged.

Checked by the independent reviewer — no issues found, and it confirmed the freeze protection is still intact.

**Please test:**
1. Get to the Surfing Beach stadium step in the tutorial (or any match where you've played a stadium with an activatable ability). Arrow to the stadium with S, press Enter to open its menu, and the ability should now read as usable — Enter on it activates it.
2. If the ability opens a follow-up choice (pick a card, move something), see whether that choice then reads and works. If it gets stuck at that second step, that's a separate thing to chase — send me `C:\PokemonTCGLive\Mods\PokemonTCGLiveAccessibility.log` and I'll pick it up from there.
3. A quick sanity check that nothing regressed: a normal tutorial attack step should still work (you can't fire the attack until the tutorial has asked for it), and a plain non-tutorial stadium ability should activate normally.

## One open question for you

You mentioned you think this also happens in normal (non-tutorial) play. From the code, the tutorial-only guard can't be the cause outside the tutorial — so if a stadium ability still won't activate in a normal match, it's something else (for instance the ability genuinely isn't usable at that moment, or its follow-up choice needs handling). If you hit it in a real match, grab the log at the moment you try and I'll dig in. No need to go looking for it specially.
