using System.Reflection;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Modding;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Characters;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.TestSupport;
using MegaCrit.Sts2.Core.Unlocks;

static class Stages
{
    public static RunState CreateRun()
    {
        TestMode.TurnOnInternal();
        Neuter.Returns(typeof(MegaCrit.Sts2.Core.Logging.Logger), "GetIsRunningFromGodotEditor", nameof(Neuter.ReturnFalse));
        Neuter.NoOp(typeof(MegaCrit.Sts2.Core.Logging.ConsoleLogPrinter), "Print");
        Neuter.Returns(typeof(MegaCrit.Sts2.Core.Saves.SaveManager), "get_Instance", nameof(Neuter.ReturnNull));
        // ModManager.Initialize does this itself in test mode; calling it would touch Godot's command line.
        typeof(ModManager).GetProperty("State", BindingFlags.Static | BindingFlags.Public)!
            .SetValue(null, ModManagerState.Skipped);
        MegaCrit.Sts2.Core.Modding.AssemblyInfo.Init();
        ModelDb.Init();
        MegaCrit.Sts2.Core.Multiplayer.Serialization.ModelIdSerializationCache.Init();
        ModelDb.InitIds();
        Player player = Player.CreateForNewRun<Ironclad>(UnlockState.all, 1uL);
        RunState run = RunState.CreateForTest(new[] { player }, seed: "SPIKE1");
        Console.WriteLine($"player deck: {player.Deck.Cards.Count} cards, HP {player.Creature.CurrentHp}/{player.Creature.MaxHp}");
        return run;
    }
}
