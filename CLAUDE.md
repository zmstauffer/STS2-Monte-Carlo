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
- `scripts/deploy.ps1` builds and installs (game must be closed). The original author's `backend/` and `questcespire-api/` server code has been deleted.

Verified in game (v0.111.0): the mod loads, all 17 Harmony patches apply, SQLite works, and the card reward, shop (with purchases), map, event, rest site, card upgrade, and combat hooks fire. Notes from that:

- `NChooseARelicSelection` is dead code in v0.111 (nothing calls `RelicSelectCmd`), so the relic-choice hooks never fire. Relics arrive through Ancient events (act start; the options are `EventModel.CurrentOptions`), shops, and rewards/treasure.
- The original event-id lookup never worked (`NEventRoom` has no `Event` property); the id now comes from the `EventModel` parameter of `NEventRoom.Create`.
- `ActMap.GetAllMapPoints` omits the start and boss nodes; snapshots add them explicitly.

Snapshots and the app:

- `SpireMonteCarlo.Contracts` defines `RunSnapshot` (run info, deck, relics, potions, the offer, event options, the act map) and `SnapshotSerializer` (snake_case JSON). The mod writes one snapshot per decision screen to `%APPDATA%\SpireMonteCarlo\snapshots\` (newest 500 kept). Real captured snapshots live in `SpireMonteCarlo.Tests/Fixtures/`.
- `SpireMonteCarlo.Advisor` is the standalone app (console for now). It loads snapshots from files, so it runs without the game. No IPC yet; the IPC contract comes once the app produces recommendations worth sending back.

The Codex cache (`SpireMonteCarlo.Codex`, `advisor codex update|status|check`) lives in `%APPDATA%\SpireMonteCarlo\codex\`: the entity export (cards, relics, potions, monsters, encounters, events, ...), card/relic/potion metrics for brackets `all`, `wr50`, and `a10`, and encounter stats. `update` refreshes only when the game version changed (or with `--force`) and swaps the new cache in only if every request succeeded. It uses ~11 requests; the export endpoint is limited to 10/hour. An optional `SPIRE_CODEX_API_KEY` env var raises the other limits. Findings that matter for the simulator:

- Codex lags the game: v0.111.0 has 596 card classes, Codex has 577, and its export data was last changed in May–July 2026. The simulator must tolerate ids Codex doesn't know. `advisor codex check <snapshot>` reports them; every id in the captured fixtures is covered.
- Elo only exists in the global metrics rows (per-character metric rows have `elo: null`, and the `character` filter on `/runs/scores` doesn't filter). That's fine: a card is only ever offered to its own character (or is colorless), so Elo is comparable within a reward screen. Filter by the card's `color`.
- Bracket sizes differ a lot: `a10` has ~496k runs, `wr50` ~20k, `all` ~1.8M. Prefer `a10` for stability and keep `wr50` for a stronger-player comparison.
- Skip has no Elo row. From `offered`/`picked` (assuming 3 cards per screen) players skip roughly 35–38% of card reward screens for every character in both brackets. Starting rule: treat Skip as a fourth option whose Bradley-Terry weight is set so its probability matches that rate; revisit once there's a way to tell when skipping is right for a given deck.
- `damage`/`block` in the export are sometimes strings like `"+3"` (scaling), so `CodexCard` keeps the raw JSON and exposes numeric accessors.

Simulator decisions (owner, from the design discussion):

- First milestone character is Ironclad; default Elo bracket is `a10`.
- The combat bot should be a middle ground: smarter than "highest damage first" (it weighs incoming damage, block, energy efficiency, and card synergies at a basic level) but not a search-based player. Only comparisons between options matter, so calibrate it against Codex encounter stats rather than chase absolute accuracy.
- Card-reward skips are not a flat rate. The chance of skipping should rise as the deck fills and as a card fits the deck worse: few skips early, more later. The Codex per-act pick rates (`pick_rate_by_act`) and the ~35–38% overall skip rate are calibration targets, not the rule itself.
- Rest sites: rest below ~50% HP, otherwise upgrade.
- Speed matters: the simulation must run many fights per decision, so it needs to use all CPU cores.
- Engine: chosen and built as a lean own engine (`SpireMonteCarlo.Sim`), data-driven from Codex plus the decompiled game. The game's own combat code can't run outside Godot (see `spikes/headless-combat/README.md`).

Simulator status (an end-to-end pipeline exists, but it is not yet trustworthy enough to act on):

- What exists: `advisor advise <snapshot>` runs thousands of simulated futures of the rest of the act for each offered card and for skipping, on shared random seeds, and reports survival, HP left, the paired difference from skipping and its uncertainty. About 40,000 whole-act rollouts per second on all cores. Supporting commands: `advisor sim extract` (reads the decompiled game), `sim fight`, `sim encounters`, `sim calibrate`, `sim calibrate-run`.
- Engine rules follow the decompiled game (damage/block math, debuff timing, Ritual skipping its first trigger). Card effects are derived automatically from Codex numbers (`CardLibrary`), with `Approximate` set on cards whose text says more; `Overrides.cs` holds hand fixes (only Body Slam so far).
- Monster AI is extracted from the decompiled classes (`MonsterAiExtractor`): weighted random branches, repeat rules, staggered starts, alternate starts. Codex's own state machines are unreliable (missing links and weights). Encounter lineups come from the decompiled encounter classes, with the 6 random Act 1 ones written by hand in `EncounterLibrary`. Extracted data is only ever written to the local cache.
- The game decides the act's upcoming encounters when the act starts (`ActModel._rooms`), so snapshots now carry an `ActPlan` (remaining normal/elite encounters in draw order, boss, second boss) and the live `Odds` counters. The rollout uses them when present and samples otherwise.
- Rollout policies: route chosen by a value DP over the map plus randomness; rest below 50% HP else upgrade the best card; card rewards picked by Bradley-Terry over a10 Elo with a skip option whose Elo grows with deck size (fit to the Codex per-act skip rates: about 25% Act 1, 42% Act 2, 52% Act 3); `?` rooms roll with the game's real odds. Shops, events, treasure, potions, and relics are not modelled yet.
- Calibration: against real Ironclad encounter stats, normal fights track well (rank correlation ~0.75) but elites and bosses do not (~0.1 and ~0.3). The raw simulated player is far weaker than a real one (Act 1 survival ~8% vs ~65%), so the rollout multiplies the player's HP pool by `ActRollout.PlayerHpScale` (2.0) as a stand-in for everything unmodelled. Scaling the HP pool (not enemy damage) keeps self-damage cards like Offering priced consistently. A value closer to 1.0 would mean a better model.
- Elite and boss mechanics are modelled (rules read from the decompiled power and monster classes, each pinned by a test in `MonsterMechanicsTests` and spot-checked with `sim fight --trace`): Slow, Territorial, Asleep (Lagavulin, incl. waking by damage), Minion (fight ends when the non-minions die), Skittish, Infested (spawns 4 Wrigglers), Hardened Shell, Slippery, Intangible, Shriek and Plow (HP-threshold stun and phase change), Vigor, Ringing (one card per turn), Steam Eruption (the Waterfall Giant becomes untouchable and explodes), plus stuns, monsters starting on an alternative move, and the status cards monsters hand out (Wound, Dazed, Slimed, Infection, Burn, Toxic, Beckon, ...; extracted from the move methods, including ascension-dependent counts, and Burn/Infection-style end-of-turn damage on cards). Monsters now pick their next move after the round's end-of-turn effects, like the game. Every Act 1 elite and boss is free of ignored powers; 7 ordinary monsters still ignore one small power each (Ravenous, Suck, Surprise, Smoggy, Constrict, Tangled).
- With the mechanics in, a decent hand-built 15-card deck at the calibrated HP beats every Act 1 elite and boss essentially every time and takes damage within ~30% of real players. But rollouts from the act start now die to bosses far more than real players (survival ~21%, boss deaths ~76%) because the decks the reward policy builds are weak: **~53% of the cards it picks are only partly modelled**, and some of the most-picked are wrong (Rage and Feel No Pain do nothing; Dominate, Molten Fist, Armaments, True Grit, Headbutt, Battle Trance, Flame Barrier lose their main effects). `advisor sim rewards` shows this. The card recipes are now the main thing limiting trust.
- Other known gaps: the bot never uses potions, is weak against "many small hits" mechanics, and does not plan a whole turn; shops, events, and treasure are not modelled.
- Next fidelity work, in rough order of payoff: hand-checked recipes for the Ironclad cards, starting with the most-picked (`sim rewards` lists them) and re-checking with `sim calibrate-run`; then potions and a one-turn-planning bot; then shops, events, and Act 2.

Next: improve fidelity as listed, checking each step with `sim calibrate` and `sim calibrate-run`. Still to define later: the IPC between the mod and the app, and turning reports into plain-language explanations in the overlay.

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