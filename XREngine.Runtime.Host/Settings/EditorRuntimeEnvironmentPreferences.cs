namespace XREngine;

/// <summary>
/// Session-scoped editor view over every environment variable declared by XREngine.
/// Values are not serialized: launch values remain authoritative until the user
/// explicitly creates a temporary runtime override.
/// </summary>
public sealed class EditorRuntimeEnvironmentPreferences
{
    public IReadOnlyList<RuntimeEnvironmentVariableDescriptor> Variables
        => XREngineEnvironmentVariableCatalog.All;
}
