namespace XREngine.Rendering;

/// <summary>
/// Allocation-free result from validating authored values against a layout.
/// </summary>
public readonly record struct AdvancedMaterialValidationResult(
    bool IsValid,
    EAdvancedMaterialValidationFailure Failure,
    uint ValueIndex,
    ulong SemanticHash)
{
    public static AdvancedMaterialValidationResult Valid => new(true, EAdvancedMaterialValidationFailure.None, 0u, 0ul);

    public static AdvancedMaterialValidationResult Invalid(
        EAdvancedMaterialValidationFailure failure,
        uint valueIndex,
        ulong semanticHash)
        => new(false, failure, valueIndex, semanticHash);
}
