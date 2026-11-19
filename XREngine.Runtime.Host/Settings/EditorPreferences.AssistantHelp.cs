using System.ComponentModel;

namespace XREngine;

public partial class EditorPreferences
{
    private string _mcpAssistantUserInstructions = string.Empty;
    private int _mcpAssistantInteractionMode;

    /// <summary>Stores the user's additional instructions for wording and level of detail.</summary>
    [Category("Assistant")]
    [DisplayName("Personal instructions")]
    [Description("Additional Assistant instructions. These do not replace engine instructions or grant tool permissions.")]
    public string McpAssistantUserInstructions
    {
        get => _mcpAssistantUserInstructions;
        set => SetField(ref _mcpAssistantUserInstructions, (value ?? string.Empty)[..Math.Min(value?.Length ?? 0, 8192)]);
    }

    /// <summary>Selects automatic intent, explanation, diagnosis, or editing for new requests.</summary>
    [Category("Assistant")]
    [DisplayName("Conversation mode")]
    public int McpAssistantInteractionMode
    {
        get => _mcpAssistantInteractionMode;
        set => SetField(ref _mcpAssistantInteractionMode, Math.Clamp(value, 0, 3));
    }
}
