using XREngine.UnityScriptHost.Execution;

namespace HarmonyLib;

/// <summary>Declares an exception region used by method transformations.</summary>
public class ExceptionBlock
{
    /// <summary>Stores the exception region kind.</summary>
    public ExceptionBlockType blockType;
    /// <summary>Stores a catch type when this region handles a typed exception.</summary>
    public Type? catchType;

    /// <summary>Rejects exception region creation without creating patch data.</summary>
    public ExceptionBlock(ExceptionBlockType blockType, Type? catchType = null)
        => throw UnityCompatibilityRuntime.Unsupported("HarmonyLib.ExceptionBlock..ctor(HarmonyLib.ExceptionBlockType,System.Type)");
}
