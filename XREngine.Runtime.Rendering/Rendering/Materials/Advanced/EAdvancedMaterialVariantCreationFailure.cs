namespace XREngine.Rendering;

/// <summary>Reason a compound material/schema creation request was rejected before mutation.</summary>
public enum EAdvancedMaterialVariantCreationFailure : uint
{
    None = 0,
    InvalidLayout = 1,
    InvalidKernel = 2,
    InvalidMaterial = 3,
    LayoutMemberCapacity = 4,
    LayoutPublicationCapacity = 5,
    KernelPublicationCapacity = 6,
    MaterialPublicationCapacity = 7,
}
