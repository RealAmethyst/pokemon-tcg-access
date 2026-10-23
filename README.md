# Pokémon TCG Live Accessibility

Screen-reader accessibility mod for **Pokémon TCG Live** (Unity 6.3.5 Mono).

Goal: parity with sighted players, including online matches.

This is an unofficial fan project, not affiliated with or endorsed by The Pokémon Company
International. The repository contains only the mod's own source code; no game files, assets or
decompiled game code are included, and building it requires your own installed copy of the game.

## Stack
- **Mod loader:** [MelonLoader 0.7+](https://github.com/LavaGang/MelonLoader)
- **Speech:** [Prism](https://github.com/ethindp/prism) (C, MPL-2.0) via direct P/Invoke
- **Target framework:** `net472` (the game's Mono runtime)
- **Patching:** Harmony (via MelonLoader)

## Build

Requires:
1. .NET SDK (any modern; the project targets `net472`)
2. The game installed at an **ASCII-only path** (MelonLoader's `plthook_open` fails on non-ASCII paths — see MelonLoader issue #978). Default expected:
   `C:\PokemonTCGLive` (see `Directory.Build.props`; a directory junction to the real install works)
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

- `Main.cs` — MelonLoader entry point; installs the Harmony patches and starts speech.
- `Hooks/` — game-wide Harmony patches, such as localization capture (`LocalizationManager.GetString`) and the battle log.
- `Screens/` — one handler per game screen or overlay, each patched onto the game class that drives it; `ScreenRouter` keeps the stack of active handlers.
- `Navigation/` — the shared keyboard navigator used by the screen handlers.
- `Speech/` — the Prism binding, speech engine and text cleanup.
- `Util/` — readers for cards, currency, rank and other game data.
- `Updater/` — a separate small mod that makes the game's standalone updater accessible.

## AI assistance

Most of this code was written by AI coding assistants (Anthropic's Claude, plus some OpenAI Codex)
under the maintainer's design direction and in-game testing; commits carry the corresponding
co-author trailers.

## License

MIT — see [LICENSE](LICENSE). Pokémon and Pokémon TCG Live are trademarks of their respective
owners.
