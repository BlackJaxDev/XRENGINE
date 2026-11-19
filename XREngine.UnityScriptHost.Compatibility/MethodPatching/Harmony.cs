using System.Reflection;
using XREngine.UnityScriptHost.Execution;

namespace HarmonyLib;

/// <summary>Declares method patch requests. Patch operations are unsupported.</summary>
public class Harmony
{
    /// <summary>Rejects patch owner creation without creating patch state.</summary>
    public Harmony(string id)
        => throw UnityCompatibilityRuntime.Unsupported("HarmonyLib.Harmony..ctor(System.String)");

    /// <summary>Rejects a patch before it can alter compiled methods.</summary>
    public MethodInfo Patch(MethodBase original, HarmonyMethod? prefix = null, HarmonyMethod? postfix = null,
        HarmonyMethod? transpiler = null, HarmonyMethod? finalizer = null)
        => throw UnityCompatibilityRuntime.Unsupported("HarmonyLib.Harmony.Patch(System.Reflection.MethodBase,HarmonyLib.HarmonyMethod,HarmonyLib.HarmonyMethod,HarmonyLib.HarmonyMethod,HarmonyLib.HarmonyMethod)");

    /// <summary>Rejects patch removal because the worker has no patch owner state.</summary>
    public void UnpatchAll(string? harmonyID = null)
        => throw UnityCompatibilityRuntime.Unsupported("HarmonyLib.Harmony.UnpatchAll(System.String)");
}
