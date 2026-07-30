namespace XREngine.Rendering;

/// <summary>
/// Stable resource identities for reconstruction diagnostics and counters.
/// </summary>
public static class AdvancedReconstructionResourceNames
{
    public const string Prefix = "Advanced.Reconstruction.";
    public const string DebugOutput = Prefix + "DebugOutput";
    public const string DerivativeError = Prefix + "DerivativeError";
    public const string SelectedMip = Prefix + "SelectedMip";
    public const string ReferenceOutput = Prefix + "ReferenceOutput.NonProduction";

    public static string Counters(uint frameSlot)
        => Prefix + $"Counters.Frame{frameSlot}";
}
