using XREngine.UnityScriptHost.Execution;

namespace HarmonyLib;

/// <summary>Declares method patch metadata. Patch state is unsupported.</summary>
[Serializable]
public class PatchInfo
{
    /// <summary>Rejects patch metadata creation without creating patch state.</summary>
    public PatchInfo()
        => throw UnityCompatibilityRuntime.Unsupported("HarmonyLib.PatchInfo..ctor()");
}
