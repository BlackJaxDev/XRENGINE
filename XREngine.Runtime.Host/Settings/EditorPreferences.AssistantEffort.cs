using System.ComponentModel;

namespace XREngine;

public partial class EditorPreferences
{
    private Dictionary<string, string> _mcpAssistantModelEfforts = new(StringComparer.Ordinal);

    /// <summary>Stores effort choices by connection type and model. Missing choices use the provider default.</summary>
    [Category("Assistant")]
    [DisplayName("Model effort choices")]
    public Dictionary<string, string> McpAssistantModelEfforts
    {
        get => _mcpAssistantModelEfforts;
        set => SetField(ref _mcpAssistantModelEfforts, value ?? new(StringComparer.Ordinal));
    }
}
