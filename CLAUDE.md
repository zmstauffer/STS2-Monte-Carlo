# STS2 Monte Carlo — Project Context

This file replaces the original author's CLAUDE.md, which is outdated. Read this first in every session.

## Working with the owner

- The owner is fluent in C# but new to git. He uses the VS Code Source Control panel (commit / Sync Changes buttons) rather than git commands. When git actions are needed, describe them in terms of that UI, or give the exact command and say what it does. Never run destructive git commands (reset --hard, force push, branch deletion) without asking.
- Work in small steps. After each step that builds and works, suggest a commit with a clear message.
- Environment: Windows, VS Code, PowerShell. Several paths contain spaces, so always quote them.
- Work happens directly on `main` (solo project, no branches needed).

## Goal

An advisor for Slay the Spire 2 that helps with **run-level decisions**: card rewards (including skip), map pathing, shops (buy / remove), events, and rest sites. It does **not** give turn-by-turn combat advice.

Every recommendation is justified with Monte Carlo simulation, with a horizon of **the end of the current act**, and explained in plain language (e.g. "Option A survives Act 1 slightly less often but wins 20% more Act 2 elite fights").

## Architecture (decided)

1. **Mod ("eyes")** — this repo's `SpireMonteCarlo` project, forked from `ebadon16/sts2-advisor`. Harmony hooks detect decision screens and read run state (deck, relics, potions, HP, gold, floor, visible map). Later it sends that state to the app over local IPC (socket or named pipe) and draws recommendations in its existing Godot overlay. Keep the mod thin: capture and display only.
2. **Standalone app ("brain")** — a separate C# process (to be created in this repo). Holds the simulator, rollout loop, scoring, and explanation text. Must be testable without the game by loading saved state snapshots. Kept out of the game process so heavy CPU work can't stutter or crash the game, and so game patches break less.
3. **Data** — a local cache of Spire Codex data (see below), stored under `%APPDATA%\SpireMonteCarlo\`, refreshed when the game version changes. Replaces the original repo's hand-made tier JSON files.

## Simulator design notes

- Rollouts still need an **automated combat player**. A simple heuristic bot is fine: absolute win rates will be off, but comparisons between options are what matter. Compare options using **common random numbers** (same seeds for each option) to reduce noise.
- One rollout: from the current state, walk a path on the visible map to the act boss. Sample combats from the act's encounter pools, sample `?` rooms from event/room odds, and resolve future decisions with default policies:
  - Card rewards: sample picks using **Codex Elo** as a Bradley-Terry model (per-character slice, stronger-player bracket such as `wr50` or `a10`). Need a rule for Skip if Skip isn't in the Elo data.
  - Rest sites: rest below ~50% HP, otherwise upgrade.
  - Shops: enumerate affordable bundles (including card removal), prune implausible ones using Elo.
- **Scoring** each rollout: survived the act, HP at act end, plus a short "probe" where the end-of-act deck fights a few sampled next-act elites. Survival alone undervalues scaling cards, since nearly everything survives Act 1.
- **Performance**: expect tens of thousands of simulated fights per decision. Use a lean engine with plain structs and no Godot dependency.
- **Open question to spike early**: can the game's own combat logic in `sts2.dll` run headless outside Godot? If it's tangled with nodes and animation, write our own lean combat engine and use the game/Codex data for numbers. Expected outcome: our own engine.
- **Calibration**: compare the bot's simulated damage taken and fatal rate per encounter against Spire Codex `/api/runs/encounter-stats`.
- **First milestone**: one character, Act 1 only, card reward decisions only. That exercises the whole pipeline with the least content to encode. Pathing and shops come after.

## Spire Codex (data source)

- Site: https://spire-codex.com — API base `https://spire-codex.com/api` — source: https://github.com/ptrlrd/spire-codex
- Their data is extracted from the decompiled game (regex parsers over the C# source), so spot-check unusual monsters/events against our own decompiled copy before trusting them in simulation.
- Useful endpoints:
  - `GET /api/exports/eng` — ZIP of all entity JSON (limited to 10/hour). Use this for the local cache.
  - `GET /api/cards`, `/api/relics`, `/api/potions`, `/api/monsters` (HP, move state machines, intents, attack patterns), `/api/encounters?act=&room_type=`, `/api/events` (decision trees, outcomes, preconditions), `/api/acts`, `/api/powers`
  - `GET /api/merchant/config` — shop pricing
  - `GET /api/runs/metrics/{cards|relics|potions}?bracket=all|solo|a10|wr30|wr50|wr75|...` — Codex Score, Codex Elo, win rate, pick rate, per-act splits
  - `GET /api/runs/scores/{type}?character=&bracket=` — Score + Elo per entity; relics accept `?act=`
  - `GET /api/runs/encounter-stats` — per-encounter fatal rate, average damage, turns
  - `GET /api/history/{entity_type}/{entity_id}` — per-entity patch history
  - `?channel=beta` serves Steam public-beta data
- Two metrics:
  - **Codex Score**: Bayesian-shrunk win rate. Known biases (staples sink, late rares float). Use only as a sanity check.
  - **Codex Elo**: Bradley-Terry fit on reward screens (taken card beats skipped cards). Largely skill-agnostic revealed preference. Use as the rollout pick policy.
  - Neither is ground truth. Where our simulation disagrees with Elo for a specific deck, that's either a bug or the most valuable advice the app gives.
- Rate limits are per endpoint: 15/min with no key, 60/min with a free key sent as `X-API-Key`. Never call the API during a decision; read from the local cache.
- License: Spire Codex source is PolyForm Noncommercial; the hosted API is free within rate limits; game data is © Mega Crit. This project is personal and noncommercial.

## Environment

- Repo: `C:\Users\zmsta\source\repos\STS2 Monte Carlo\STS2-Monte-Carlo` (GitHub: `zmstauffer/STS2-Monte-Carlo`)
- Decompiled game source: `C:\Users\zmsta\source\repos\STS2 Monte Carlo\sts2-decompiled` (outside the repo, added to the VS Code workspace). Regenerate after every game patch:
  ```
  ilspycmd -p -o "C:\Users\zmsta\source\repos\STS2 Monte Carlo\sts2-decompiled" "C:\Program Files (x86)\Steam\steamapps\common\Slay the Spire 2\data_sts2_windows_x86_64\sts2.dll"
  ```
- Game install (verify): `C:\Program Files (x86)\Steam\steamapps\common\Slay the Spire 2`. Game assemblies are in `data_sts2_windows_x86_64\`. Installed mods go in `mods\SpireMonteCarlo\`.
- The mod targets `net9.0` (the game's runtime). The .NET 10 SDK may also be installed; it's only needed for ilspycmd.
- The game is Godot 4.5.1 (Mega Crit fork) with all game logic in C# in `sts2.dll`. Harmony ships with the game.
- Modding references: https://fresh-milkshake.github.io/Modding-Tutorial/ (documented against v0.103.3) and the StS2 modding hub on slaythespire.wiki.gg.

## Current status: updating the forked mod

The upstream repo's last commit was 2026-03-19, targeting game v0.99.1. The game is now v0.111.0. Steps 1–7 of the update are done and committed:

- The project builds against the installed game DLLs (`GameDir` in the csproj). All Harmony patch targets exist in v0.111 with compatible signatures (checked against the decompiled source). Three broken reflection reads were fixed (shop prices via `MerchantEntry.Cost`, combat piles via `Player.PlayerCombatState`, purchase-log card id via `CreationResult.Card`).
- Known, deliberately unfixed: `GameStateReader.CardModelToInfo` reads the private base-class field `CardModel._keywords` through the leaf type, so card `Tags` are always empty. It only feeds `SynergyScorer`, which gets replaced by Codex + simulation.
- The manifest is `SpireMonteCarlo/SpireMonteCarlo.json` (snake_case). The loader loads `<manifest dir>\<id>.dll`, so the manifest `id` must match the DLL name (case-insensitive on Windows).
- JSON data lives in `SpireMonteCarlo/AppData/` and is deployed to `%APPDATA%\SpireMonteCarlo\`; `SpireMonteCarlo/Data/` (CardProperties `.tsv`) ships in `mods\SpireMonteCarlo\Data\`. The only `.json` under `mods\` is the manifest. The log and SQLite db stay next to the DLL.
- Removed: CloudSync, the version check / update banner, and the `IsRunningModded` bypass. Modded play now uses the game's separate modded profile (the game copies unmodded saves on the first modded launch). Imported community stats are no longer re-merged after a run ends (that relied on the CloudSync cache); `RecomputeAll` resets them.
- `scripts/deploy.ps1` builds and installs (game must be closed). `backend/` and `questcespire-api/` are the original author's server code, still present and no longer referenced by the mod; candidates for deletion.

Remaining step:

8. **Verify in game**: launch the game, accept the mods warning, check `mods\SpireMonteCarlo\spiremontecarlo.log` and `%APPDATA%\SlayTheSpire2\logs\godot.log` (`PatchMethod` logs every hook it couldn't find), then play a quick run and confirm each hook fires: card reward, relic choice, shop, map, event, rest site. Also confirm SQLite works (the log shows database errors if the native `runtimes\` lookup fails).

After the mod works: define the run-state snapshot format and IPC contract, add snapshot export to the mod, create the standalone app project, build the Spire Codex cache, then start the first simulator milestone.

## Notes on the original codebase

- Game IDs are `UPPER_SNAKE_CASE` (`BODY_SLAM`); the old tier JSON uses Title Case. `TierEngine.NormalizeId()` bridges them. Character IDs are lowercase (`ironclad`, `silent`, `defect`, `regent`, `necrobinder`).
- `GameBridge/GameStateReader.cs` reads run state via reflection (RunManager, Player, deck, etc.).
- Existing features: tier badges, archetype detection, synergy scoring, local SQLite run tracking, overlay with F7–F11 hotkeys. The tier/synergy scoring will be replaced by Codex data plus simulation; the hooks, state reading, and overlay are what we're keeping.
- Structure: `Plugin.cs` (entry, `[ModInitializer("Init")]`), `GamePatches.cs`, `Core/`, `GameBridge/`, `Tracking/`, `UI/`. About 10.5k lines of C#.

## Guardrails

- Never commit decompiled game code or game DLLs. Make sure `.gitignore` covers `lib/`, build output, and anything under `sts2-decompiled`.
- Don't redistribute game data.
- The mod must stay read-only with respect to gameplay.
- Keep heavy computation in the standalone app, not the mod.