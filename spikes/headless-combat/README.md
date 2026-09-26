# Spike: run the game's own combat code outside Godot

Question: can `sts2.dll` run a combat in a plain .NET process, so the simulator reuses exact game rules?
Status: **stopped, not viable as-is.** Kept for the findings; not part of the solution.

## How far it got (game v0.111.0)

`dotnet run` in this folder loads `sts2.dll` from the game folder and gets as far as creating a `Player`:

- `TestMode.TurnOnInternal()`, then set `ModManager.State = Skipped` by reflection (what `ModManager.Initialize` does in test mode).
- Follow `OneTimeInitialization.ExecuteEssential` order: `AssemblyInfo.Init()`, `ModelDb.Init()`, `ModelIdSerializationCache.Init()`, `ModelDb.InitIds()`. These work.
- The static `Log` class calls Godot (`OS.GetCmdlineArgs`, `GD.Print`); Harmony no-op patches on `Logger.GetIsRunningFromGodotEditor` and `ConsoleLogPrinter.Print` get past it.
- `Player.CreateForNewRun` then needs `SaveManager.Instance`. Constructing one runs `MigrationManager`, whose migrations touch `Controller`'s static `StringName` fields, which **crashes the process (0xC0000005)**: Godot's native function pointers are null without the engine, so any `StringName`/`Variant`/`GD.*` use is fatal and uncatchable.
- Patching `SaveManager.Instance` to null gets one step further, then `Player.PopulateStartingRelics` dereferences it.

## Why it doesn't converge

- Every layer (logging, saves, controller input, localization, and presumably powers/VFX/audio commands on the combat path) has its own Godot calls, and each is a fatal crash rather than an exception, so each costs a restart.
- The game's own tests run *inside* a Godot process (`RiderTestRunner` scenes, `MockGodotFileIo`); nothing suggests a supported standalone mode.
- Even if it worked, `CombatManager.Instance` / `RunManager.Instance` / `ModelDb` are process-wide singletons: one simulated combat at a time per process, and never inside the live game process (it would corrupt the running game's state). Parallelism would mean many processes.

## Remaining option, not tried

Launch a second, headless copy of the game (`SlayTheSpire2.exe --headless`) with the mod in a "sim worker" mode, so the real engine supplies the natives. Unknowns: whether a second instance can start next to the running game (Steam), startup time and memory per worker, and per-fight speed (async command pipeline built for animation). Still single-threaded per process.
