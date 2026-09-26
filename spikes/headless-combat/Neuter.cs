using System.Reflection;
using HarmonyLib;

/// <summary>Replaces game methods that call into Godot's native engine (unavailable in a plain .NET process) with no-ops or fixed results.</summary>
static class Neuter
{
    private static readonly Harmony H = new("spike.neuter");
    public static readonly List<string> Applied = new();

    public static void NoOp(Type type, string method)
    {
        MethodBase target = AccessTools.Method(type, method) ?? throw new MissingMethodException(type.FullName, method);
        H.Patch(target, prefix: new HarmonyMethod(typeof(Neuter), nameof(SkipVoid)));
        Applied.Add($"{type.Name}.{method} -> no-op");
    }

    public static void Returns(Type type, string method, string patchMethod)
    {
        MethodBase target = AccessTools.Method(type, method) ?? throw new MissingMethodException(type.FullName, method);
        H.Patch(target, prefix: new HarmonyMethod(typeof(Neuter), patchMethod));
        Applied.Add($"{type.Name}.{method} -> {patchMethod}");
    }

    public static bool SkipVoid() => false;
    public static bool ReturnNull(ref object __result) { __result = null!; return false; }
    public static bool ReturnFalse(ref bool __result) { __result = false; return false; }
}
