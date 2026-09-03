namespace XREngine.Rendering;

/// <summary>
/// Enforces late-pass eligibility rules and rejects legacy forward opaque bypasses.
/// </summary>
public static class AdvancedLatePassEligibilityValidator
{
    /// <summary>
    /// Validates whether a given draw can execute in the requested late pass.
    /// Opaque and masked materials are strictly forbidden from entering late passes.
    /// </summary>
    public static bool TryValidateLatePass(
        AdvancedLatePassMetadata metadata,
        bool isOpaqueOrMasked,
        out string? rejectionReason)
    {
        if (isOpaqueOrMasked)
        {
            rejectionReason = "Opaque and masked surfaces must be classified and shaded natively through ARP 06/07 and cannot enter late transparency passes.";
            return false;
        }

        if (metadata.UnsupportedReason != null)
        {
            rejectionReason = metadata.UnsupportedReason;
            return false;
        }

        rejectionReason = null;
        return true;
    }
}
