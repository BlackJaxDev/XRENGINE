namespace XREngine.Rendering.GI.Contracts;

/// <summary>
/// Immutable admission result with an operator-visible reason when a provider cannot run.
/// </summary>
public sealed record GlobalIlluminationSupportResult(
    EGlobalIlluminationSupportState State,
    string Diagnostic)
{
    public static GlobalIlluminationSupportResult Supported(string diagnostic = "Supported.")
        => new(EGlobalIlluminationSupportState.Supported, diagnostic);

    public static GlobalIlluminationSupportResult Unsupported(string diagnostic)
        => new(EGlobalIlluminationSupportState.Unsupported, diagnostic);

    public static GlobalIlluminationSupportResult Initializing(string diagnostic)
        => new(EGlobalIlluminationSupportState.Initializing, diagnostic);

    public static GlobalIlluminationSupportResult Failed(string diagnostic)
        => new(EGlobalIlluminationSupportState.Failed, diagnostic);
}
