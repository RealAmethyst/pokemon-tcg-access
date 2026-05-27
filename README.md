# Pokémon TCG Live Accessibility

Screen-reader accessibility mod for **Pokémon TCG Live** (Unity 6.3.5 Mono).

Private project. Goal: parity with sighted players, including online matches.

## Stack
- **Mod loader:** [MelonLoader 0.7+](https://github.com/LavaGang/MelonLoader)
- **Speech:** [Prism](https://github.com/ethindp/prism) (C, MPL-2.0) via direct P/Invoke
- **Target framework:** `net472` (the game's Mono runtime)
- **Patching:** Harmony (via MelonLoader)

## Repo layout

- `mod/` — this repo. Mod source.
- `../research/` — decompiled game DLLs, architecture notes, archived design questions. Not in git.

## Build

Requires:
1. .NET SDK (any modern; the project targets `net472`)
2. The game installed at an **ASCII-only path** (MelonLoader's `plthook_open` fails on non-ASCII paths — see MelonLoader issue #978). Default expected:
   `C:\Users\Amethyst\The Pokemon Company International\Pokemon Trading Card Game Live`
   (override with `msbuild /p:GameDir="..."` if installed elsewhere)
3. MelonLoader 0.7+ installed into the game folder (run the installer once)

Then:
```
dotnet build -c Release
```

Output: `bin\Release\PokemonTCGLiveAccessibility.dll`

## Install

1. Drop `PokemonTCGLiveAccessibility.dll` into `<game>\Mods\`
2. Drop `prism.dll` (built from the Prism repo via CMake) into `<game>\` (next to `Pokemon TCG Live.exe`)
3. Launch the game. Check `<game>\MelonLoader\Latest.log` for startup.

## Build `prism.dll`

```
git clone https://github.com/ethindp/prism
cd prism
cmake -B build -A x64
cmake --build build --config Release
```
Resulting `prism.dll` goes next to the game executable.

## Architecture

See `../research/architecture-notes.md` for the full game architecture survey.

Key hookpoints used by the mod:
- `LocalizationManager.GetString` — universal text capture
- `MainMenuNavigation.GoToScreen` — screen transitions
- `RainierManager.Awake` — bootstrap timing
- `CardDatabaseRepository.GetCardDataRow` — card data (later phase)

## Roadmap

- **Phase 0** — Speech engine (Prism), focus tracker, keyboard nav primitives (this scaffold)
- **Phase 1** — Title, main menu, settings, login
- **Phase 2** — Deck builder, card collection
- **Phase 3** — Battle screen
- **Phase 4** — Shop, quests, profile, tournaments, online plumbing
