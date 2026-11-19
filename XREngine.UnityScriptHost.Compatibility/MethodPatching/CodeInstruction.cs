using System.Reflection.Emit;
using XREngine.UnityScriptHost.Execution;

namespace HarmonyLib;

/// <summary>Declares an instruction used by method transformations without executing them.</summary>
public class CodeInstruction
{
    /// <summary>Stores the operation field used by instruction transforms.</summary>
    public OpCode opcode;
    /// <summary>Stores the operand field used by instruction transforms.</summary>
    public object? operand;
    /// <summary>Stores branch labels used by instruction transforms.</summary>
    public List<Label> labels = [];
    /// <summary>Stores exception blocks used by instruction transforms.</summary>
    public List<ExceptionBlock> blocks = [];

    /// <summary>Rejects instruction creation without creating patch data.</summary>
    public CodeInstruction(OpCode opcode, object? operand = null)
        => throw UnityCompatibilityRuntime.Unsupported("HarmonyLib.CodeInstruction..ctor(System.Reflection.Emit.OpCode,System.Object)");
}
