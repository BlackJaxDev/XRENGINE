namespace XREngine.Rendering;

/// <summary>
/// Packs the velocity validity reason into a draw/deformation flags word.
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

        return PackValidatedVelocityReason(flags, encoded);
    }

    /// <summary>Packs a validated velocity reason while preserving the other flag bits.</summary>
    /// <remarks>The encoded reason must fit in four bits.</remarks>
    [global::Engine.Diagnostics.HotPath]
    private static uint PackValidatedVelocityReason(uint flags, uint encoded)
        => (flags & ~VelocityReasonMask) | (encoded << VelocityReasonShift);

    /// <summary>Reads the velocity validity reason from its four-bit field.</summary>
    [global::Engine.Diagnostics.HotPath]
    public static EAdvancedVelocityValidityReason DecodeVelocityReason(
        uint flags)
        => (EAdvancedVelocityValidityReason)(
            (flags & VelocityReasonMask) >>
            VelocityReasonShift);

    /// <summary>Tests whether the velocity reason requires reactive reconstruction.</summary>
    [global::Engine.Diagnostics.HotPath]
    public static bool IsReactive(uint flags)
        => DecodeVelocityReason(flags) !=
           EAdvancedVelocityValidityReason.Valid;
}
