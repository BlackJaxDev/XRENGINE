namespace XREngine.Rendering;

[Flags]
public enum EAdvancedPreparedDrawDeformationFlags : uint
{
    None = 0,
    Active = 1u << 0,
    PreviousValid = 1u << 1,
}
