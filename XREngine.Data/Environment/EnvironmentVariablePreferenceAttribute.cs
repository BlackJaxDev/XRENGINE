namespace XREngine;

/// <summary>
/// Marks an editor preference as mirroring an environment variable.
/// The editor inspector uses this to show machine-env state and Set/Clear actions.
/// </summary>
[AttributeUsage(AttributeTargets.Property | AttributeTargets.Field, AllowMultiple = true)]
public sealed class EnvironmentVariablePreferenceAttribute(string name) : Attribute
{
    public string Name { get; } = name;
}
