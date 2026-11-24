namespace XREngine.Editor.Tools;

/// <summary>
/// Describes one parsed parameter from a PowerShell dependency script <c>param(...)</c> block.
/// </summary>
public sealed record DependencyScriptParameterDescriptor(
    string Name,
    DependencyScriptParameterKind Kind,
    string DefaultValue,
    string[] AllowedValues,
    string Description);
