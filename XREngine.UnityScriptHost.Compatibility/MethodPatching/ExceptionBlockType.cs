namespace HarmonyLib;

/// <summary>Names exception region kinds in method instruction data.</summary>
public enum ExceptionBlockType
{
    BeginExceptionBlock = 0,
    BeginCatchBlock = 1,
    BeginExceptFilterBlock = 2,
    BeginFaultBlock = 3,
    BeginFinallyBlock = 4,
    EndExceptionBlock = 5
}
