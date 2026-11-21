namespace XREngine.Editor.UI.Tools;

/// <summary>
/// Identifies the editor item that the user selected for an explanation.
/// The Assistant uses these values as context and checks live state before it makes claims.
/// </summary>
public sealed record AssistantHelpRequest(
    string Kind,
    string Label,
    string UiLocation,
    string? SymbolName = null,
    string? TargetId = null,
    string? TargetName = null,
    string? CurrentValue = null,
    string? Description = null,
    string? Units = null,
    string? Diagnostic = null);
