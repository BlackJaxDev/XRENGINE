using ImGuiNET;
using System.Numerics;
using System.Text;
using System.Text.Json;
using XREngine.Editor.Mcp;
using XREngine.Editor.Services.AssistantHelp;

namespace XREngine.Editor.UI.Tools;

public sealed partial class McpAssistantWindow
{
    private Task<McpToolResponse>? _sourceReadTask;
    private string _sourceDisplayText = string.Empty;
    private string? _sourceExpectedHash;
    private bool _openSourceView;

    /// <summary>Links only source references returned by the bounded engine help reader.</summary>
    private void DrawSourceReferences(ChatMessage message)
    {
        ToolCallEntry[] calls = GetChatView(message).Tools;
        bool opened = false;
        for (int i = 0; i < calls.Length; i++)
        {
            ToolCallEntry call = calls[i];
            if (string.IsNullOrWhiteSpace(call.SourcePath))
                continue;
            if (!opened)
            {
                if (!ImGui.TreeNode("Sources"))
                    return;
                opened = true;
            }
            ImGui.PushID(i);
            ImGui.PushTextWrapPos(0f);
            ImGui.TextUnformatted(call.SourcePath);
            ImGui.PopTextWrapPos();
            ImGui.BeginDisabled(_sourceReadTask is not null);
            if (ImGui.SmallButton("Inspect source"))
            {
                _sourceExpectedHash = call.SourceHash;
                _sourceDisplayText = "Loading source...";
                _sourceReadTask = EngineSourceHelpService.ReadAsync(call.SourcePath, call.SourceStartLine ?? 1, 80, CancellationToken.None);
                _openSourceView = true;
            }
            ImGui.EndDisabled();
            ImGui.PopID();
        }
        if (opened)
            ImGui.TreePop();
    }

    /// <summary>Shows a bounded source excerpt and reports changes since the cited snapshot.</summary>
    private void DrawSourceView()
    {
        if (_sourceReadTask is { IsCompleted: true } pending)
        {
            try
            {
                McpToolResponse response = pending.GetAwaiter().GetResult();
                _sourceDisplayText = response.Message;
                if (!response.IsError && response.Data is not null)
                {
                    JsonElement data = JsonSerializer.SerializeToElement(response.Data);
                    var text = new StringBuilder();
                    text.AppendLine(data.GetProperty("path").GetString());
                    text.Append("Build match: ").AppendLine(data.GetProperty("binaryMatch").GetString());
                    string? hash = data.GetProperty("sha256").GetString();
                    text.Append("SHA-256: ").AppendLine(hash);
                    if (!string.Equals(hash, _sourceExpectedHash, StringComparison.Ordinal))
                        text.AppendLine("Source changed since this answer. Ask again before relying on its old explanation.");
                    text.AppendLine();
                    foreach (JsonElement line in data.GetProperty("lines").EnumerateArray())
                        text.Append(line.GetProperty("line").GetInt32()).Append(": ").AppendLine(line.GetProperty("text").GetString());
                    _sourceDisplayText = text.ToString();
                }
            }
            catch (Exception ex)
            {
                _sourceDisplayText = "Source is unavailable: " + ex.Message;
            }
            _sourceReadTask = null;
        }
        if (_openSourceView)
        {
            ImGui.OpenPopup("Assistant source");
            _openSourceView = false;
        }
        ImGui.SetNextWindowSize(new Vector2(680f, 450f), ImGuiCond.Appearing);
        if (!ImGui.BeginPopup("Assistant source"))
            return;
        ImGui.TextWrapped("This excerpt is read again from the source. A local checkout can differ from the running build.");
        ImGui.InputTextMultiline("##SourceExcerpt", ref _sourceDisplayText, (uint)Math.Max(1024, _sourceDisplayText.Length + 1), new Vector2(-1f, -40f), ImGuiInputTextFlags.ReadOnly);
        if (ImGui.Button("Close"))
            ImGui.CloseCurrentPopup();
        ImGui.EndPopup();
    }
}
