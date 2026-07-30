namespace XREngine.Rendering;

/// <summary>
/// Packs the phase-04 velocity validity reason into a draw/deformation flags word.
/// Publishers retain the low feature bits and write this field once per frame.
/// </summary>
public static class AdvancedReconstructionTemporalFlags
{
    public const int VelocityReasonShift = 16;
    public const uint VelocityReasonMask = 0xFu << VelocityReasonShift;

    public static uint PackVelocityReason(
        uint flags,
        EAdvancedVelocityValidityReason reason)
    {
        uint encoded = (uint)reason;
        if ((encoded & ~0xFu) != 0u)
            throw new ArgumentOutOfRangeException(nameof(reason));

        return (flags & ~VelocityReasonMask) |
               (encoded << VelocityReasonShift);
    }

    public static EAdvancedVelocityValidityReason DecodeVelocityReason(
        uint flags)
        => (EAdvancedVelocityValidityReason)(
            (flags & VelocityReasonMask) >>
            VelocityReasonShift);

    public static bool IsReactive(uint flags)
        => DecodeVelocityReason(flags) !=
           EAdvancedVelocityValidityReason.Valid;
}
