using ImGuiNET;
using System.Numerics;

namespace XREngine.Editor.UI.Tools;

public sealed partial class McpAssistantWindow
{
    /// <summary>
    /// Shows the inputs that the next Assistant request can use. The controls
    /// change the existing attachment settings while no request is running.
    /// </summary>
    private void DrawConversationContextPopup()
    {
        Vector2 displaySize = ImGui.GetIO().DisplaySize;
        float width = Math.Min(440f, Math.Max(180f, displaySize.X - 32f));
        float height = Math.Min(340f, Math.Max(120f, displaySize.Y * 0.42f));
        // A child has no intrinsic width for the popup's automatic size calculation.
        ImGui.SetNextWindowSize(new Vector2(width, height + ImGui.GetStyle().WindowPadding.Y * 2f));
        Vector2 panelPosition = ImGui.GetWindowPos();
        Vector2 panelSize = ImGui.GetWindowSize();
        ImGui.SetNextWindowPos(new Vector2(
            Math.Max(panelPosition.X, panelPosition.X + panelSize.X - width - 8f),
            panelPosition.Y + ImGui.GetFrameHeightWithSpacing() * 2f), ImGuiCond.Appearing);
        if (!ImGui.BeginPopup("Assistant context"))
            return;

        ImGui.BeginChild("##AssistantContextContents", new Vector2(-1f, height));
        ImGui.TextUnformatted("Next request");
        ImGui.Separator();
        ImGui.TextUnformatted("Assistant");
        ImGui.SameLine();
        ImGui.TextUnformatted(GetBrandLabel());
        ImGui.TextUnformatted("Model");
        ImGui.SameLine();
        ImGui.TextWrapped(GetSelectedModel());
        ImGui.TextUnformatted("Effort");
        ImGui.SameLine();
        ImGui.TextUnformatted(EffortLabel(_isBusy ? _activeReasoningEffort : GetSelectedEffort()));

        ImGui.Spacing();
        ImGui.TextUnformatted("Included context");
        ImGui.BeginDisabled(_isBusy);
        bool screenshot = AttachViewportScreenshot;
        if (ImGui.Checkbox("Viewport screenshot", ref screenshot))
            AttachViewportScreenshot = screenshot;
        bool tools = AttachMcpServer;
        if (ImGui.Checkbox("Editor tools", ref tools))
            AttachMcpServer = tools;
        ImGui.EndDisabled();
        ImGui.PushTextWrapPos(0f);
        if (AttachViewportScreenshot)
            ImGui.TextDisabled("A new viewport capture is attached when you send.");
        if (AttachMcpServer)
            ImGui.TextDisabled("Explain and Diagnose use inspection tools only. Edit can request changes under editor permissions.");
        ImGui.PopTextWrapPos();

        string personalInstructions = _isBusy ? _activeUserInstructions : Prefs?.McpAssistantUserInstructions ?? string.Empty;
        if (!string.IsNullOrWhiteSpace(personalInstructions) && ImGui.TreeNode("Personal instructions included"))
        {
            ImGui.PushTextWrapPos(0f);
            ImGui.TextUnformatted(personalInstructions);
            ImGui.PopTextWrapPos();
            ImGui.TreePop();
        }

        ImGui.Spacing();
        if (ImGui.TreeNode("Conversation context"))
        {
            if (UsesSubscription)
            {
                if (SubscriptionEditorConnection.SupportsConversationReplay(SubscriptionBrand))
                    ImGui.TextWrapped("The next request can include up to 12 recent messages and 48,000 characters from this connection.");
                else
                    ImGui.TextWrapped("Each request starts a new provider chat. Only the current request and selected context are sent.");
            }
            else if (string.IsNullOrWhiteSpace(_conversationSummary))
            {
                ImGui.TextWrapped("Recent chat messages can be included with the next request. No earlier summary is saved for this chat.");
            }
            else
            {
                ImGui.TextWrapped("Earlier chat was summarized for API context. The full visible chat remains above.");
                if (ImGui.TreeNode("Inspect earlier summary"))
                {
                    ImGui.PushTextWrapPos(0f);
                    ImGui.TextUnformatted(_conversationSummary);
                    ImGui.PopTextWrapPos();
                    ImGui.TreePop();
                }
            }
            ImGui.TreePop();
        }

        ImGui.EndChild();
        ImGui.EndPopup();
    }
}
