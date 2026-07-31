namespace XREngine;

/// <summary>
/// Marks an editor preference as mirroring an environment variable.
/// The editor inspector uses this to show launch/effective state and temporary
/// process-runtime override actions.
/// </summary>
[AttributeUsage(AttributeTargets.Property | AttributeTargets.Field, AllowMultiple = true)]
public sealed class EnvironmentVariablePreferenceAttribute(string name) : Attribute
{
    public string Name { get; } = name;
}
