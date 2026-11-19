using System.Reflection;
using XREngine.UnityScriptHost.Execution;

namespace HarmonyLib;

/// <summary>Declares instruction queries used by method transformations.</summary>
public static class CodeInstructionExtensions
{
    /// <summary>Rejects an instruction query without inspecting patch data.</summary>
    public static bool Calls(this CodeInstruction code, MethodInfo method)
        => throw UnityCompatibilityRuntime.Unsupported("HarmonyLib.CodeInstructionExtensions.Calls(HarmonyLib.CodeInstruction,System.Reflection.MethodInfo)");
}
