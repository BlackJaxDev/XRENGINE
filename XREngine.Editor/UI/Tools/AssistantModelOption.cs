namespace XREngine.Editor.UI.Tools;

/// <summary>Stores a provider model ID, its menu label, and reported input capacity.</summary>
internal sealed record AssistantModelOption(string Id, string DisplayName, int? ContextWindowTokens = null, bool IsRealtime = false);
