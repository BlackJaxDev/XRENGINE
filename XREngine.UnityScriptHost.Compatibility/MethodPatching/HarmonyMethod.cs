using System.Reflection;
using XREngine.UnityScriptHost.Execution;

namespace HarmonyLib;

/// <summary>Declares a patch method reference. Patch preparation is unsupported.</summary>
public class HarmonyMethod
{
    /// <summary>Rejects patch method creation without creating patch state.</summary>
    public HarmonyMethod(MethodInfo method)
        => throw UnityCompatibilityRuntime.Unsupported("HarmonyLib.HarmonyMethod..ctor(System.Reflection.MethodInfo)");
}
