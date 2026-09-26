using System.Reflection;
using System.Runtime.Loader;

// sts2.dll's own dependencies (Steamworks.NET, Sentry, ...) live in the game folder, not next to this exe.
const string GameData = @"C:\Program Files (x86)\Steam\steamapps\common\Slay the Spire 2\data_sts2_windows_x86_64";
AssemblyLoadContext.Default.Resolving += (ctx, name) =>
{
    string path = Path.Combine(GameData, name.Name + ".dll");
    return File.Exists(path) ? ctx.LoadFromAssemblyPath(path) : null;
};

Stage.Run("1: TestMode + ModelDb.Init + RunState.CreateForTest(Ironclad)", () => Stages.CreateRun());
