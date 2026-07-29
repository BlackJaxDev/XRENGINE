namespace XREngine.Rendering;

/// <summary>
/// Machine-readable material packing validation failures.
/// </summary>
public enum EAdvancedMaterialValidationFailure : uint
{
    None = 0,
    InvalidLayoutHandle,
    UndeclaredValue,
    ValueKindMismatch,
    DuplicateValue,
    ConstantRangeOverflow,
    TextureRangeOverflow,
}
