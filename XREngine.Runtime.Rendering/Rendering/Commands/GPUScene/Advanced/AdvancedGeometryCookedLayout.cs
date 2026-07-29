namespace XREngine.Rendering.Commands;

/// <summary>
/// Runtime layout version embedded in canonical cooked geometry records.
/// Increment when byte interpretation becomes incompatible.
/// </summary>
public static class AdvancedGeometryCookedLayout
{
    public const uint CurrentVersion = 1u;

    public static bool IsSupported(uint version)
        => version == CurrentVersion;
}
