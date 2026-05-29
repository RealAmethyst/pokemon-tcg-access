# Open Questions — Phase 3: Battle

This is the design brief for the in-battle handler. It has four parts:

- **Part A** — how a Pokémon TCG battle screen is laid out (orientation, tied to your screenshot).
- **Part B** — what I learned from the decompiled code that changes how we should design this.
- **Part C** — the interaction model I'm proposing as a starting point (HS-informed).
- **Part D** — the open decisions I need from you. Answer inline after each **What I need:**.

Read Parts A–C first; they'll make the questions in Part D make sense. Once answered, this file graduates to `../research/QUESTIONS-002-battle.md` and I write the locked design doc + start scaffolding.

---

# Part A — How a Pokémon TCG battle screen is laid out

Your side is the **bottom half**, the opponent's is the **top half**, and the two are mirror images. The center stone arena is where the two **Active** Pokémon face off. Here's every zone, what it means in play, and where it sits in your screenshot (tutorial vs. "Professor"):

### Your side (bottom)
- **Active Pokémon** — the one Pokémon that's "in the ring" and can attack / be attacked. *Screenshot: Charmander, 80 HP, glowing cyan (it's selected). The green ▲ arrow on it means it can evolve right now (you have its evolution available). The little mouse-cursor icon is just the game showing the click target.*
- **Bench** — up to **5** Pokémon waiting behind the Active. They can be switched in, take effects, and you attach/evolve onto them. *Screenshot: Charmeleon, 110 HP, sitting on your bench.*
- **Hand** — your cards. Public to you, hidden from the opponent. *Screenshot: a stack labeled "Basic Fire Energy" with a **3** — you're holding 3 cards.*
- **Deck** — your face-down draw pile. You and the opponent only ever know the **count**, never the order. *Screenshot: a blue face-down stack with a count.*
- **Discard pile** — KO'd / used cards, **face-up and public** to both players. *Screenshot: the other blue stack with a count.*
- **Prize cards** — face-down cards set aside at the start. Each time you knock out an opponent's Pokémon you take one. **Taking your last prize wins the game.** You don't know what your own prizes are until you take them. *Screenshot: tracked by the blue **3** on the right sidebar — 3 prizes left to take. (Normal games start at 6; this tutorial uses a reduced count.)*
- **Lost Zone** — a one-way "removed from play" pile that a few cards use. Face-up/public. (Empty here.)

### Opponent's side (top) — the mirror
Same six zones, flipped. *Screenshot: Riolu (80 HP) is their Active with an "Accelerating Stab" attack; their bench (the wide red bar) is empty; red face-down stacks with counts (3 and 13) are their deck/discard; their hand is a row of face-down red card-backs (you only get the count).*

### Shared / center
- **Stadium** — a single shared slot in the middle for Stadium cards; whoever played it "owns" it but it affects both. (Empty here.)

### The HUD (heads-up display)
- **Timers** — each player has a chess-clock-style timer. *Screenshot: 39:00 each.*
- **Prize counters** — prizes left to take, per player. *Screenshot: red **3** (opponent) and blue **3** (you) on the right rail.*
- **End Turn button** — *Screenshot: the white "END TURN" hexagon on the right.*
- **Settings gear** (top-right) — pause / concede / options live behind it.
- **Opponent name** — *Screenshot: "Professor" top-left.*

> A couple of the tiny center stacks/counters are visually ambiguous in a still screenshot; I'll confirm exact on-screen anchor positions when I run it. The **zones themselves** above are what the design is built on, and those are confirmed in code.

### A Pokémon in play carries a lot of state
Each Pokémon on the board isn't just a name — it has: **current HP vs. max HP** (damage is tracked in counters of 10), **attached Energy** (which powers attacks), **attached Tools** (equipment), **status conditions** (Asleep / Poisoned / Burned / Paralyzed / Confused), its **evolution stage** and what's stacked under it, its **attacks** (each with an energy cost, damage number, and effect text), any **Ability**, and its **Weakness / Resistance / Retreat cost**. All of that is information a sighted player can see, so all of it is fair to speak.

---

# Part B — What the code told us (things you should know)

Five things from the research that directly shape the design:

### 1. We literally *cannot* cheat — and that makes safety simple
The game is server-authoritative: the server sends your client only what you're allowed to see. Hidden cards (the opponent's hand, both decks, face-down prizes, secret picks) arrive as blank placeholders tagged `"PRIVATE"` with **no identity attached** — your client doesn't even know what they are. So the rule is airtight and trivial to enforce:

> **Speak a card's name only when the game itself is showing it face-up. Otherwise it's "face-down card" + which zone + the count.**

This is the *exact same check* the game uses to decide whether to draw a card face-up, so what we announce matches what a sighted player sees, perfectly. Counts, in-play HP/damage/status/energy, both discard piles, the lost zone, and the stadium are all public and safe. When a card effect legitimately reveals hidden info to you ("look at the top 3 cards of your deck"), the game makes those public first, so we *can and should* read them.

### 2. One handler covers both tutorial and normal battles
A tutorial battle is just an offline match against a scripted bot running on the **same** battle engine as a real online match. The tutorial layer only *adds* hand-holding on top (it spotlights the one card you're meant to play and blocks the rest). So we build one battle handler against the normal system, and add a thin "tutorial-aware" layer that follows the spotlighted card and reads the professor's coaching. **No split needed.**

### 3. Board cards aren't normal buttons — we build our own navigation
Everywhere else in the mod we ride the game's keyboard/Selectable system. In battle there isn't one: cards are 3-D objects you click with the mouse, with no keyboard focus at all. So, exactly as we expected, we **enumerate the zones ourselves and synthesize the navigation**, then perform actions through the game's own action methods (play / attach / evolve / attack / retreat / end-turn). This is still "use the game's systems" — we drive the real action plumbing, we just supply the missing keyboard layer on top.

### 4. The decision prompts are the moments that matter most
Throughout a match the game stops and asks you to choose: "choose a Pokémon for your Active spot," "discard 2 cards," "choose which prize to take," "pick a target for this attack," coin flips, mulligans, yes/no. **Every one of these funnels through a single system**, so we can reliably catch each prompt, read out the question and the options, and let you answer with the keyboard. These are where a blind player is most lost without help, and they behave identically in tutorial and normal play.

### 5. We can always read "what's legal right now"
The game pre-computes, for each of your cards, exactly which actions are legal this instant — which attacks you can afford, where a card can be played, what it can attach to, whether you can retreat or end your turn. That means we can offer **only legal moves** and never send you down a dead end. It's also what powers a "cycle through my available plays" feature.

---

# Part C — Interaction model (rebuilt to match Hearthstone Access)

You picked the HS-style review-key model, so I've rebuilt the keymap to follow Hearthstone Access closely and deviate only where Pokémon's board genuinely differs from Hearthstone's. The core loop is pure HS: **a letter key jumps your focus into a zone, ← / → walk through the cards in that zone, and ↓ / ↑ read the focused card line by line.**

## The core loop (HS list-navigation, identical keys)

| Key | What it does |
|---|---|
| **← / →** | Previous / next card in the focused zone (across your bench, across your hand, …) |
| **↓ / ↑** | Read the focused card **line by line** — next / previous detail line |
| **Shift+↑** | Repeat the current line |
| **Shift+↓** | Read from the current line to the end of the card |
| **Home / End** | First / last card in the zone |
| **1–6 (and 0)** | Jump straight to card N in the current zone |
| **Enter** | Act on the focused card → opens its legal-action menu (Attack / Ability / Retreat / Attach / Evolve / Play / Inspect, as applicable); also confirms a target or a menu choice |
| **Backspace** | Cancel / back out of a menu |

Landing on a card speaks **just its name** (plus its position, e.g. "Charmander, 2 of 3") — then you arrow **down** for the detail, exactly like HS. The detail lines, in order, would be roughly:

1. *(name — spoken automatically on focus)*
2. HP — remaining of max, plus any status condition (Asleep / Poisoned / Burned / Paralyzed / Confused)
3. Type · Weakness · Resistance · Retreat cost
4. Attached Energy, attached Tool(s)
5. Attack 1 — name, energy cost, damage
6. Attack 1 — effect text
7. *(further attacks…)*
8. Ability — name + text
9. Stage / evolves-from
10. Flavor text + card number (last)

## Zone keys (HS "look at" keys — jump focus into a zone)

HS uses C = your hand, B = your board, Shift+C = opponent hand count, D = deck count, etc. Mapped onto Pokémon's zones:

| Key | Zone | Notes |
|---|---|---|
| **C** | Your **hand** | HS-identical. ← / → across your cards. |
| **Shift+C** | Opponent's **hand** | Count only ("opponent has 4 cards") — identities hidden, same as HS. |
| **B** | Your **bench** | ← / → across bench slots. |
| **Shift+B** | Opponent's **bench** | (HS uses **G** for the opponent's board; Shift+B is more consistent — your call, see open items.) |
| **A** | Your **Active** Pokémon | Pokémon has no mana, so HS's A/Shift+A (mana) is free — "A for Active" fits. |
| **Shift+A** | Opponent's **Active** Pokémon | |
| **D** | Your **deck** | Count only — deck is always hidden/unordered (HS-identical: D = count your deck). |
| **Shift+D** | Opponent's **deck** | Count only. |
| **P** | Your **prize cards** | Count only (face-down) until taken or revealed. |
| **Shift+P** | Opponent's **prizes** | Count only. |
| **S** | The **Stadium** card | Shared center slot, if one's in play. |
| **(discard — letter TBD)** | Your **discard pile** | Face-up & browsable (← / →). Needs a letter — see open items. |
| **Shift+(same)** | Opponent's discard | Browsable. |

## Reading the game without moving focus

| Key | What it does |
|---|---|
| **T** | Match-status overview — whose turn, turn number, your clock, prizes left for each player, both Actives' HP at a glance (the "where do things stand?" key) |
| **Y** | Open the **battle log / history** (the game keeps one) — read past actions in order |
| **I** | Read the focused card's full rules text (ability + attack effects) — HS-style "keywords" key |
| **Tab / Shift+Tab** | Cycle through **only the plays that are legal right now** (next / previous valid play) |
| **E / Shift+E** | End turn (with / without a "you still have moves" confirmation) |
| **Escape** | Open the battle menu (pause / concede / settings) — only when no card menu or prompt is open |

**Decision prompts** ("choose your Active", "discard 2 cards", "pick a target", coin flips, yes/no) read out the question automatically when they pop, then behave like a list: ← / → (or ↑ / ↓) to move through the options, Enter to choose, Shift+↑ to re-hear the question. Same keys you already know — no new mode to learn.

**Reading never changes the game.** Moving focus and arrowing through cards only reads. You act only via Enter (which opens a menu of *legal* choices) or Tab-cycle + Enter. Nothing during exploration can accidentally play a card.

**Energy notation:** costs are stored as letter codes (R=Fire, G=Grass, W=Water, L=Lightning, P=Psychic, F=Fighting, D=Darkness, M=Metal, Y=Fairy, N=Dragon, C=Colorless); we speak them as words — "two Fire and one Colorless."

### A few key-letter choices still open (your call)
- **Opponent's bench:** Shift+B (my pick, mnemonic) vs. HS's literal **G**?
- **Discard pile letter:** if opp-bench = Shift+B, then **G** is free for your discard (Shift+G opponent's); if opp-bench = G, discard could be **X** (X = discard/trash). Pick the pair.
- **Status overview on T** and **battle log on Y** — confirm or rename.
- Anything to add? e.g. a "re-read the last thing spoken" key, or a direct "read my Active's attacks" shortcut instead of arrowing to them. I agree, X for discard I think, Shift B works fine and B. everything else is good.

---

# Part D — Open design questions

### 1. Board navigation model
How do you want to move around the board?

- **(A) Hybrid** — arrow keys for spatial zone/card nav *plus* single-key jumps to read any zone instantly *plus* Tab to cycle legal plays. Most powerful; most keys to learn. *(My recommendation — it matches your section-stack model and adds HS's quick-reads.)*
- **(B) Spatial only** — arrows across zones and cards, no dedicated quick-read keys. Fewest keys; querying a specific zone means arrowing to it.
- **(C) HS-style review keys** — a dedicated key per element + Tab to act, minimal spatial nav. Closest to Hearthstone Access; leans on memorized hotkeys over exploration. This, see my section above.

**What I need:** A, B, or C (and any tweaks)?

> _Answer: C for hand, B for bench maybe? Look at how HS reads the card lines with up and down arrow, enter and al that to navigate and back out is perfectly fine. Please suggest more in terms of other places to go or speak._

---

### 2. How you act on a focused card (what Enter does)

- **(A) Contextual menu** — Enter opens a list of *only the legal actions* for that card (Attack / Ability / Retreat / Attach / Evolve / Play / Inspect, as applicable); pick with arrows. Mirrors the game's own action overlay; impossible to choose an illegal move. *(My recommendation.)*
- **(B) Dedicated mode keys** — separate keys to attack / play / attach / evolve, each entering a targeting sub-mode. Fewer menus, faster for experts, more keys to memorize.

**What I need:** A or B?

> _Answer: A_

---

### 3. How much should the mod announce on its own?
Especially during the opponent's turn.

- **(A) Key events** — auto-announce turn changes, prompts aimed at you, damage/KO to your Pokémon, prizes taken, and the opponent's plays; board inspection stays on-demand. Balanced — you follow the game without being flooded. *(My recommendation.)*
- **(B) Minimal** — only turn changes and prompts you must answer; everything else on-demand. Quietest; risks missing what the opponent did.
- **(C) Full play-by-play** — narrate every action both players take, like a live battle log. Most info; can be chatty on long opponent turns.

**What I need:** A, B, or C? (We could also make this a setting you toggle in-match.)

> _Answer: What is the difference between a and C? B is out of the question, answer that in this file and let me decide after that._

**A vs. C — the difference (Claude):**

- **A (key events) = filtered summary.** The mod speaks only the consequential moments and stays quiet on routine bookkeeping. You'd hear: turn changes, any prompt you must answer, damage / KO / status applied to *your* Pokémon, prizes taken, and a one-line summary of the opponent's *significant* plays (e.g. "Professor's Riolu used Accelerating Stab — your Charmander took 30, now 50 of 80"). You would **not** hear every minor step (drawing a card, shuffling, each individual coin in a multi-flip, the opponent merely looking at a card). You dig into anything else yourself with the review keys.

- **C (full play-by-play) = read the whole log live.** The mod narrates **every** game action in sequence, for both players, the way the on-screen battle log records it — "Professor drew a card. Professor attached Fire Energy to Riolu. Professor's Riolu used Accelerating Stab. Coin flip: heads. 30 damage to Charmander. Professor ended turn." Nothing is skipped. Most complete, but on a long opponent turn it's a continuous stream of speech to sit through (interruptible, but still chatty).

**The practical point:** the game already keeps a battle log, and I'm proposing a key (**Y**) to open it on demand. So even under **A**, the *full* sequence is always one keypress away when you want it — A just keeps the *live* speech focused on what's decision-relevant, with the complete history available whenever you ask. That's why I lean A: C's main benefit (never missing a step) is already covered by the on-demand log, without the constant chatter. Your call.

> _Final answer (A or C): If it's shown on screen when something happens then it's gotta be C, those speech calls should all use say queue or yeah the queue flag, not interupt, this is in case multiple stuff happens quickly that we can hear lal of it._

---

### 4. First milestone (battle is the biggest surface in the game)

- **(A) Read-only first** — make the whole board *perceivable* (navigation, full card detail, all the auto-announcements) and verify it in a real tutorial battle. Wire up *performing* actions in the next pass. Lowest risk; gets you "following along" fastest. *(My recommendation.)*
- **(B) Full vertical slice** — navigation + inspection + performing the core actions (play / attach / evolve / attack / retreat / end turn) + answering prompts, all in one build. Playable end-to-end sooner, but a much bigger first step to debug.
- **(C) Tutorial-driven** — build exactly what the FTUE tutorial needs first (it's scripted and constrained, so smaller), then generalize. Pragmatic for clearing the tutorial; some work won't transfer.

**What I need:** A, B, or C?

> _Answer: Either A or B_

**Note (Claude):** A and B aren't really rivals — B *contains* A. You can't perform actions without first being able to perceive the board, so the read-only foundation (A) is the first thing built either way; B just keeps going and wires up the action menus on top of it in the same push. So my plan is **A first as the first verifiable milestone** (navigate + inspect + announcements, tested in a tutorial battle), then roll straight into actions (the rest of B) without stopping — unless you'd rather I hold at A and check in before touching anything that sends moves. Tell me which.

> _Hold at read-only, or roll straight into actions?: right into actions, however research properly first._

---

### 5. Smaller calls (answer any you have an opinion on; I'll default the rest)

- **a. How a Pokémon is summarized** when you land on it. Proposed default: *"name, HP remaining of max, [status], [N energy attached], [tool], bench/active position."* e.g. "Charmander, 80 of 80 HP, no energy, your active." Too much? Too little?
  > _Answer: Just name, see above for how I want that to work_

- **b. Card detail depth.** One key reads everything, or a layered read (press again / arrow down for the next layer: stats → attacks → ability → weakness/resistance/retreat → flavor), HS-style?
  > _Answer: oo yes, this, exactly this arrow down to go line by line, name reads automatically when focused_

- **c. Numbered jumps.** Do you want number keys (1–6) to jump straight to a bench slot or hand card, like HS?
  > _Answer: Yes, although that's not super important, but nice to have I guess_

- **d. Targeting read-out.** When an attack/effect needs a target, I'll announce the valid targets and you arrow through them. Want the predicted damage/effect read too (the game computes it)?
  > _Answer: not unless it's actually shown on screen, if it's just under lying code then no._

- **e. Opponent actions.** When the opponent acts, how detailed? e.g. "Professor attacked with Accelerating Stab, 30 damage to your Charmander" vs. just "your Charmander took 30, now 50 of 80."
  > _Answer: If the game has text for this that is shown we use that, meaning localization works by default, if not then opponent pokemon, and the action and then damage or whatever result they did._

- **f. Concede / pause.** Reachable via the settings gear → a menu we already handle. Want a dedicated panic-key to read match status (turn, both actives' HP, prizes left) at any time?
  > _Answer: Escape in the battle UI should open that, to be fair not whenever there is a menu open, like actions for card or whatever._

---

### 6. Follow-ups from your answers (Claude)

Two small confirmations — both have a sensible default, so they **won't block** the research I'm starting now. Answer whenever.

**a. Announce model (your Q3 = C) — confirming my read.** "C, driven by what's shown on screen, queued" tells me: we **voice the game's own on-screen action text** — each battle-log entry / on-screen banner as it appears — rather than composing our own narration from the underlying state. All such announcements use **queued (non-interrupting) speech**, so a fast burst of events is heard in full, in order. We do *not* invent speech for things the game doesn't surface visually (consistent with your (d)/(e)). A big research target right now is therefore pinning down exactly where the game emits that on-screen action text (its battle-log/banner system). Correct?
  > _Confirm / correct:_

**b. Lock the key letters.** My working defaults (trivial to change later):
  - Opponent's bench = **Shift+B** (vs. HS's G).
  - Discard pile = **G** (yours) / **Shift+G** (opponent's).
  - **T** = match-status overview · **Y** = battle log · **I** = full rules text.
  - Suggested extra: a "repeat the last thing spoken" key (e.g. **`** backtick) — handy on a busy board.
  Override any, or just say "defaults are fine."
  > _Your letters:_

Defaulting the rest unless you say otherwise: the card-detail line order shown in Part C; Enter's menu lists only *actions* (inspection stays on the arrows + I key); reading order within a zone follows the on-board order.

---

## Appendix — technical anchors (for the record, not for you to action)

For future sessions / my own reference, the confirmed hook points:
- **Controller / turn state:** `MatchManager` (Match scene `MonoSingleton`) — `MatchState { None, Setup, LocalTurn, OpponentTurn }`, `MatchStateChanged` event, `IsMyTurn`, card registry `entityID → Card3D`.
- **Ground-truth state:** `NetworkMatchController.currentMatchInfo` → `MatchInfo` → `MatchBoard` (flat `p1*/p2*` zone lists); `CardInfo.Get(entityID)` for live per-card data + legal actions (`activeUsableActions`, `placeableBoardPos`, `attachableEntities`).
- **No-cheat gate:** `Card3D.IsPublic` (== `cardInfo != null`); `MatchManager.TryGetCardNameFromEntityID/TryGetCardIDFromEntityID` already return false for `"PRIVATE"`/unknown cards.
- **Actions (invoke):** `MatchUserOperations.PlaceCard/AttachCard/EvolveCard/UseCard/Retreat/EndTurn/Concede/SelectInput`; `Card3D.TryUseAction(MatchAction)`; `AttackOverlay` for the action menu; `BIO_EndTurnButton.OnClick()`.
- **Prompts/decisions:** `SelectionOverlayOpener.RunSelectionRequest` → `PlayerSelectionOverlay.OpenWithSelectionRequest` (read question via `GetSelectionDescription` + options via `TextSelectionOverlay.GetOptions`), `SubmitSelectionAndClose` (answered). `BattleMenuNavigation.OnMenuHistoryChange` for any-menu open/close. `CoinFlipOverlay.OnCoinLanded`.
- **Tutorial layer:** `FTUEManager.instance.runFTUE` (is-tutorial flag); `FTUEMatchManager.Instance.focusedCard` / pointers / board highlights (spotlighted card); reuses all the normal prompt overlays.
- **Reveals to announce:** `RevealOperation` / `MatchEventType.EntityRevealed`.
- **Win condition:** `PrizeController.PrizeCountChanged` (taking your last prize = win); end-of-match via `MatchManager.LoadEndBattleScreen`.
