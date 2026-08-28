namespace XREngine.Rendering;

/// <summary>Stable renderer-neutral comparison operation used by logical samplers.</summary>
public enum EAdvancedCompareOperation : uint
{
    Never = 0,
    Less = 1,
    Equal = 2,
    LessOrEqual = 3,
    Greater = 4,
    NotEqual = 5,
    GreaterOrEqual = 6,
    Always = 7,
}
