using System.Reflection;
using System.Reflection.Emit;
using XREngine.UnityScriptHost.Execution;

namespace HarmonyLib;

/// <summary>Declares method patch access. Patch operations are unsupported.</summary>
public class PatchProcessor
{
    /// <summary>Rejects a patch processor without creating patch state.</summary>
    public PatchProcessor(Harmony instance, MethodBase original)
        => throw UnityCompatibilityRuntime.Unsupported("HarmonyLib.PatchProcessor..ctor(HarmonyLib.Harmony,System.Reflection.MethodBase)");

    /// <summary>Rejects instruction inspection without reading a method body.</summary>
    public static List<CodeInstruction> GetOriginalInstructions(MethodBase original, ILGenerator? generator = null)
        => throw UnityCompatibilityRuntime.Unsupported("HarmonyLib.PatchProcessor.GetOriginalInstructions(System.Reflection.MethodBase,System.Reflection.Emit.ILGenerator)");
}
