# Accessibility settings can be changed now, and values read first (2026-08-20)

Both in and deployed.

## Why you couldn't change them

Not a game problem — the mod's own rule. Its navigation code deliberately treats a checkbox the way
a web form does: **Space** ticks it and **Enter** does nothing. That's a reasonable convention for a
form, and it's wrong for a settings row, where Enter is what you'd reach for. There was also no way
for you to discover the difference, since the mod doesn't speak control instructions.

So on this screen the settings toggles now take:

- **Enter** — flips the setting.
- **Right** — turns it on. **Left** — turns it off.

Left and Right are deliberately fixed rather than flipping, so the same key always gives the same
result, exactly like the sliders. Space still works as it always did.

The tabs are untouched by this: they're toggles too, but they keep their tab behaviour, so nothing
there changes.

Turning a setting on or off goes through the game's own change event, which is what applies and
saves it — so it sticks, and it's the same path a mouse click takes.

## Value first, and re-spoken on change

All four accessibility rows now read **name, value, then description**, and re-read themselves with
the new value the moment you change one. That matches the sliders and the resolution dropdown, which
you'd already found working.

The General tab's rows were already name-then-value, so those were consistent to begin with.

## One inconsistency I left alone

The General tab says "checked" and "unchecked" for its switches, while the accessibility rows say
"on" and "off" — because that's the game's own wording for those particular controls, and I'd rather
use the game's words than the mod's where they exist.

Having both in one screen is a bit uneven. I didn't change it because you asked about ordering rather
than wording, and it affects rows you'd said were working. Say the word and I'll move the General
switches onto the game's on/off wording too, so the whole screen matches.

## Please test

- On each of the four accessibility rows: press Enter, then Left, then Right. Each should change the
  setting and immediately read back the new value.
- Confirm the change sticks — leave settings, come back, and check it still reports what you set.
- Confirm the card foil row still moves with Left and Right.
- Confirm the tabs still behave as tabs and haven't started flipping.

## Still open

Whether to keep the mod repairing the card-foil arrows (broken in the game itself, for sighted
players too) or just report that setting as unavailable.
