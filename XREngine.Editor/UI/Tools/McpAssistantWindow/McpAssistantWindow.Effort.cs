using ImGuiNET;

namespace XREngine.Editor.UI.Tools;

public sealed partial class McpAssistantWindow
{
    private string _effortSelectionKey = string.Empty;
    private string? _effortKeyModel;
    private int _effortKeyProvider = -1;
    private bool _effortKeySubscription;
    private string _activeReasoningEffort = string.Empty;

    /// <summary>Reuses the preference key until the selected connection or model changes.</summary>
    private string GetEffortSelectionKey()
    {
        string model = GetSelectedModel();
        bool subscription = UsesSubscription;
        if (_effortKeyModel != model || _effortKeyProvider != ProviderIndex || _effortKeySubscription != subscription)
        {
            _effortKeyModel = model;
            _effortKeyProvider = ProviderIndex;
            _effortKeySubscription = subscription;
            _effortSelectionKey = $"{ProviderIndex}/{(subscription ? "subscription" : "api")}/{model}";
        }
        return _effortSelectionKey;
    }

    private string GetSelectedEffort()
        => Prefs?.McpAssistantModelEfforts.TryGetValue(GetEffortSelectionKey(), out string? effort) == true
            ? effort ?? string.Empty : string.Empty;

    private IReadOnlyList<string> GetSupportedEfforts()
        => UsesSubscription
            ? SubscriptionEditorConnection.SupportedReasoningEfforts(SubscriptionBrand)
            : Array.Empty<string>();

    /// <summary>Prevents a saved effort from becoming an unreported fallback after a catalog change.</summary>
    private bool IsEffortSelectionValid()
    {
        string selected = GetSelectedEffort();
        if (string.IsNullOrEmpty(selected))
            return true;
        IReadOnlyList<string> efforts = GetSupportedEfforts();
        for (int i = 0; i < efforts.Count; i++)
            if (string.Equals(efforts[i], selected, StringComparison.Ordinal))
                return true;
        return false;
    }

    private void SelectEffort(string effort)
    {
        if (Prefs is not { } preferences)
            return;
        var choices = new Dictionary<string, string>(preferences.McpAssistantModelEfforts, StringComparer.Ordinal);
        if (string.IsNullOrEmpty(effort))
            choices.Remove(GetEffortSelectionKey());
        else
            choices[GetEffortSelectionKey()] = effort;
        preferences.McpAssistantModelEfforts = choices;
    }

    /// <summary>Offers only effort values reported for the selected model and supported by its client.</summary>
    private void DrawEffortPicker()
    {
        IReadOnlyList<string> efforts = GetSupportedEfforts();
        string selected = GetSelectedEffort();
        bool valid = IsEffortSelectionValid();
        // Leave a stale choice editable so the user can return to the provider default.
        bool unavailable = efforts.Count == 0 && valid;
        ImGui.BeginDisabled(unavailable);
        if (ImGui.BeginCombo("##AssistantEffort", valid ? EffortLabel(selected) : "Choose effort"))
        {
            if (ImGui.Selectable("Default", string.IsNullOrEmpty(selected)))
                SelectEffort(string.Empty);
            for (int i = 0; i < efforts.Count; i++)
            {
                string effort = efforts[i];
                ImGui.PushID(effort);
                if (ImGui.Selectable(EffortLabel(effort), selected == effort))
                    SelectEffort(effort);
                ImGui.PopID();
            }
            ImGui.EndCombo();
        }
        ImGui.EndDisabled();
        if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
            ImGui.SetTooltip(!valid
                ? "This model no longer offers the saved effort. Choose another level or Default."
                : unavailable
                    ? "This connection does not report adjustable effort for this model. The provider controls reasoning."
                    : "Controls how much reasoning the model can use. Higher effort can take longer and use more of your plan. Default uses the provider's setting.");
    }

    private static string EffortLabel(string effort) => effort switch
    {
        "" => "Default",
        "none" => "None",
        "minimal" => "Minimal",
        "low" => "Low",
        "medium" => "Medium",
        "high" => "High",
        "xhigh" => "Extra high",
        "max" => "Max",
        "ultra" => "Ultra",
        _ => effort,
    };
}
