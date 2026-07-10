namespace XREngine;

public readonly record struct RvcAttributeReconstructionContract(
    bool Position,
    bool Normal,
    bool Tangent,
    bool Uv,
    bool MaterialRow,
    bool PreviousPosition,
    bool Velocity)
{
    public static RvcAttributeReconstructionContract Full => new(
        Position: true,
        Normal: true,
        Tangent: true,
        Uv: true,
        MaterialRow: true,
        PreviousPosition: true,
        Velocity: true);
}
