# What you ship to users (2026-08-20)

You're right — **the map file never ships.** Here's the whole list.

## Ship these

- **`PokemonTCGLiveAccessibility.dll`** — the protected one, from `mod\bin\Obfuscated\`. This is the
  mod itself. Goes in the player's `<game>\Mods\` folder.
- **`prism.dll`** — the speech library. Goes in `<game>\` next to `Pokemon TCG Live.exe`, *not* in
  `Mods\`. Without it the mod loads but says nothing.
- Your setup instructions and the plain-language changelog.

That's it. Two files plus the docs. Players also need MelonLoader installed, but that's their own
installer, not something you hand out.

## Never ship these

- **`Mapping.txt`** — this is the big one, and your instinct was exactly right. It's the key that
  translates the scrambled names back into the real ones. Shipping it alongside the protected DLL
  would undo the entire point of obfuscating: anyone with both files gets the readable version back
  in seconds. Keep it, never distribute it. I save a copy of each one privately under
  `research\protected-builds\` so we can still decode a crash report you're sent.
- **The clean build** from `bin\Release\`, and `bin\clean-known-good.dll` — those are the fully
  readable versions. They're for your testing and debugging only.
- **Debug symbol files (`.pdb`)** — you were already right not to ship these. The protected build
  doesn't even produce one now, so there's nothing to leak by accident.
- Anything from the source folder: `build\`, `obfuscar.xml`, the `research\` folder, log files.

## The one-line rule

If it came out of `bin\Obfuscated\`, only the **`.dll`** travels. Everything else in that folder
stays home.

## Still unprotected

The Updater app's mod (`PokemonTCGLiveUpdaterAccessibility.dll`) hasn't been obfuscated — it's a
separate project and needs its own setup pass. If you distribute that one too, tell me and I'll wire
it up the same way.

---

# You are now on the PROTECTED build (2026-08-20)

Answering your question first: the build you'd been testing was the normal clean one. **It is now
the protected build** — I've swapped it in, and from here on that's what gets deployed.

## What to watch for

Everything should behave exactly as before. Obfuscation doesn't change any logic, but a break, if
there is one, tends to show up on **one screen only** rather than everywhere — so please use it
normally for a while and cover the screens you visit most. Deck editor, battle, shop, settings.

If any screen suddenly goes silent or a button stops responding, that's the signal. Tell me and
I'll switch you straight back — I keep a copy of the last known-good clean build ready, so rolling
back takes seconds.

One thing worth knowing: it also hides the text inside the file, not just the names. Your logs still
read perfectly normally while the game runs — the text is only unreadable to someone opening the DLL.
That's a bonus rather than a cost. The one real cost is that if the mod ever crashes, the crash
report will name scrambled functions; I save the key that decodes them with every build I deploy, so
we can still read it.

---

# The Alt+F4 reopening: found and fixed (2026-08-20)

**Short version: the game wasn't reopening — it was crashing on exit, and Windows was restarting it.
It was our bug, it has been there a long time, and it's now fixed. Both mods rebuilt and deployed.**

## What actually happened

You pressed Alt+F4, the game began shutting down normally, and then it crashed about one second
later. Windows restarted the crashed game, which crashed on exit again — that loop is what you
experienced as "it keeps reopening itself". Your Windows event log recorded ten crashes in a row.

The cause is ours. To make the game visible to your screen reader at all, the mod takes over the
game window's message handler. We installed that, but never took it back out again on the way down.
So when the game shut down and the mod's code was unloaded, Windows kept sending the window its
final messages — to code that no longer existed. That's an instant crash, every single exit.

The reason this hid for so long is that a crash *during exit* is invisible: the window disappears
either way, so it looks like a normal close. What made it show up this time was Windows deciding to
restart it. So this is almost certainly not new — it was very likely happening on every Alt+F4 for
months, silently.

Nothing you did caused it, and it can't have damaged anything — a crash at exit happens after the
game has already saved (you can see "Preferences Saved" in the log just before each crash).

## What I changed

The mod now puts the game's own window handler back before it shuts down, so there's nothing of ours
left for Windows to call into. The Updater app had exactly the same bug, so it got the same fix.

## Please test

- Play as normal, then close with Alt+F4 like you always do. It should close and stay closed.
- Worth doing two or three times, and once straight after a match, since it's a shutdown-timing bug.
- If anything reopens, tell me and I'll look at the event log again.

## The review found more, and it's fixed too

The independent review caught something important in my first version of the fix: if putting the
window handler back ever *failed*, my code would still have thrown away the safety net — recreating
the very crash it was meant to fix. It now checks that the handler is still ours, checks whether the
restore actually succeeded, and if anything goes wrong it keeps everything in place and tries again
on the way out rather than giving up silently. It also now cleans up on a second shutdown path that
can skip the first one. Every outcome writes a clear line to the log, so if it ever does fail we'll
see exactly why instead of guessing.

## Note on the earlier answer

When you asked "if this is the obfuscated build" — it wasn't. You were testing the normal clean
build; the protected/obfuscated one has never been run in-game yet. So the rename result is real,
but obfuscation is still untested. Worth knowing before we ship anything protected.

---

# Deck rename now works, plus a file on hiding the mod's code (2026-08-20)

Two things from your message are ready, and a third (the update-diff tool) is still being
built as I write this. Details below.

## 1. Deck rename — fixed and deployed, please test

You were right that this never worked. The reason: the rename box that opens when you press
the pencil next to a deck's name had no handler in the mod at all. So the box opened
silently, the deck editor underneath kept the keyboard, the text field was never switched on
for typing, and there was no way to actually rename anything — exactly the "does not work"
you hit.

It now has a proper handler. Here's what to try, in the deck editor (and it works the same
way from the DECKS screen's deck panel):

- Find the **Rename deck** control. In the editor it's up in the top row of options; it now
  announces itself as "Rename deck" rather than a cryptic name. Press **Enter** on it.
- The rename box opens and speaks "Rename deck" (or whatever the game's own header says),
  then reads the field with the current deck name already in it. You're placed in the field,
  ready to type.
- Type the new name. Each character echoes as you type, the same as the search and redeem
  fields.
- Press **Tab** to move from the field to the buttons, then arrow to **DONE** or **CANCEL**
  and press Enter — or just press **Enter** while still in the field to confirm.
- **Escape** cancels and restores the old name, always.
- On a real rename you'll hear "Deck renamed to <the new name>" so you know it took. Cancelling
  or leaving it unchanged says nothing extra.

Please check: that you can reach it, type into it, hear your characters, confirm, and that the
new name sticks (back out to the deck list and the deck should read by its new name). Also try
Cancel and Escape and confirm they leave the name alone.

## 2. Hiding the mod's code from others — a file to read and decide on

You asked how to make the mod much harder for others to decompile and read. I've written you a
full recommendations file at:

**`research\obfuscation-recommendations.md`**

The honest headline: we can make it a lot harder to read, but not impossible, and the "compile
it native/static" idea specifically doesn't work for us (our loader needs the readable form —
the file explains why). The real tool is called obfuscation, and there's a catch that's special
to our mod because it leans so heavily on reflection.

I'm not going to do anything to the build yet — this is your call. The file ends with four
questions; answer whichever you have a feel for (right there in the file is fine) and I'll take
it from there. The most important one is what you're actually trying to stop, because that sets
how far it's worth going.

## 3. The "what changed this patch?" tool — being built now

Your third question — do we have something that diffs the client each update and tells us where
the changes are — we didn't, and it's a genuinely good idea given how often this game patches
and how the breakages hide (a screen that navigates but says nothing). Codex is building it as
I write this: a tool that snapshots each client build, diffs two builds, and — the useful part —
tells us exactly which of the mod's ~500 game hookups a patch has broken or moved, instead of
us finding out one silent screen at a time.

I'll come back to you when it's done and tested, with how to run it. Nothing for you to do on
this one yet.

## Review done — fixes are already in the build you're testing

The independent code review ran and found five things (none critical). I validated each against the
real game code and fixed all five; the build you're testing has them. Nothing here needs anything
from you — it's just so you know what changed since the first deploy:

- Fixed a real one: if you opened the rename box, alt-tabbed away and came back (or reopened it),
  each character you typed could start echoing twice. The field's typing-echo hook wasn't being let
  go when the box rebuilt. Now it is.
- The rename pencil on the DECKS screen (not just inside the editor) now also announces itself as
  "Rename deck". It's wired differently there, so it needed naming a second way.
- "Rename deck" and "Deck options" now come from the game's own words in your language, not baked-in
  English — so they'll follow your client language like the rest of the game text.
- Two smaller safety fixes so a rename can never be announced when it didn't actually happen, and
  Enter can never submit twice.

One thing I did NOT touch, on purpose, because you didn't ask for it: the review's tool found an
old, unrelated dead spot — the popup-message screen reads a game field that no longer exists. It's
harmless (there's already a fallback) but it's dead code. Want me to clean it up? One-line removal.

## Code protection is wired in (nothing to test in-game)

You chose the free renaming option, and it's set up. It changes nothing about how you build and test
day to day — your normal `dotnet build -c Release` and everything you deploy stays exactly as clean
and readable as before. The protection only happens when you deliberately ask for it, for a build
you're going to distribute:

- Normal (what you deploy and test): `dotnet build -c Release`
- Protected, for shipping: `dotnet build -c Release -p:Obfuscate=true` — the scrambled DLL lands in
  `bin\Obfuscated\`, separate from your normal build so it can't be picked up by accident.

There's a safety check baked in so a protected build that would break the mod simply refuses to
build. I tested it both directions and it does the right thing. Full explanation and how-to is at the
bottom of `research\obfuscation-recommendations.md`.

I ran the independent code review on this build change too; if it turns anything up I'll fix it and
tell you. If you ever want it stronger than renaming, or want the Updater protected the same way,
just say so.
