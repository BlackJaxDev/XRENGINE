using ImGuiNET;
using System.Net.Http.Headers;
using System.Net.WebSockets;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using XREngine.Components;
using XREngine.Data.Core;
using XREngine.Editor.Mcp;
using XREngine.Editor.Services;
using XREngine.Rendering;
using XREngine.Rendering.Vulkan;
using XREngine.Scene;
using XREngine.Rendering.UI;


namespace XREngine.Editor.UI.Tools;

public sealed partial class McpAssistantWindow
{
    private readonly Dictionary<string, string> _artifactFileNames = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Renders a clickable file path link that opens the file location in Explorer,
    /// plus an "Open" button that launches the file with the default application.
    /// </summary>
    private void DrawFilePathLink(string filePath, int messageIndex, int callIndex)
    {
        if (!_artifactFileNames.TryGetValue(filePath, out string? fileName))
        {
            fileName = Path.GetFileName(filePath);
            _artifactFileNames.Add(filePath, fileName);
        }
        ImGui.PushID(messageIndex);
        ImGui.PushID(callIndex);
        ImGui.PushID("Artifact");

        // Clickable file name link.
        ImGui.PushStyleColor(ImGuiCol.Text, ColorFilePath);
        bool clicked = ImGui.Selectable(fileName, false,
            ImGuiSelectableFlags.None, new Vector2(Math.Min(ImGui.GetContentRegionAvail().X, ImGui.CalcTextSize(fileName).X + 4f), 0));
        ImGui.PopStyleColor();

        if (ImGui.IsItemHovered())
        {
            ImGui.PushStyleColor(ImGuiCol.Text, ColorFilePathHover);
            ImGui.SetTooltip(filePath);
            ImGui.PopStyleColor();
            ImGui.SetMouseCursor(ImGuiMouseCursor.Hand);
        }

        if (clicked && File.Exists(filePath))
        {
            try
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("explorer.exe", $"/select,\"{filePath}\"")
                {
                    UseShellExecute = true
                });
            }
            catch { /* Non-critical */ }
        }

        // "Open" button to launch with default application.
        if (ImGui.SmallButton("Open file") && File.Exists(filePath))
        {
            try
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(filePath)
                {
                    UseShellExecute = true
                });
            }
            catch { /* Non-critical */ }
        }

        ImGui.PopID();
        ImGui.PopID();
        ImGui.PopID();
    }

    /// <summary>
    /// Renders a collapsible image thumbnail preview for a tool result image.
    /// Uses a cached XRTexture2D loaded from the file path.
    /// </summary>
    private void DrawImagePreview(string imagePath, int messageIndex, int callIndex)
    {
        ImGui.PushID(messageIndex);
        ImGui.PushID(callIndex);
        bool previewOpen = ImGui.TreeNodeEx("Image preview",
            ImGuiTreeNodeFlags.DefaultOpen);
        if (ImGui.IsItemToggledOpen())
            _followChat = false;

        if (previewOpen)
        {
            // Lazy-load and cache the texture.
            if (!_imagePreviewCache.TryGetValue(imagePath, out XRTexture2D? texture))
            {
                try
                {
                    texture = new XRTexture2D(imagePath);
                    _imagePreviewCache[imagePath] = texture;
                }
                catch
                {
                    _imagePreviewCache[imagePath] = null;
                    texture = null;
                }
            }

            if (texture is not null)
            {
                float maxPreviewEdge = Math.Max(1f, Math.Min(480f, ImGui.GetContentRegionAvail().X));
                nint handle = nint.Zero;
                var options = new RenderTexturePreviewOptions(ForceBaseMipSampling: true, UploadIfNeeded: true);

                if (Engine.IsRenderThread)
                    EditorTexturePreviewService.TryGetHandle(
                        texture,
                        in options,
                        out handle,
                        out _,
                        out _);

                if (handle != nint.Zero)
                {
                    float w = Math.Max(texture.Width, 1f);
                    float h = Math.Max(texture.Height, 1f);
                    float scale = Math.Min(maxPreviewEdge / w, maxPreviewEdge / h);
                    if (scale > 1f) scale = 1f;
                    var displaySize = new Vector2(w * scale, h * scale);

                    // File pixels already use the image's top-to-bottom row order.
                    ImGui.Image(handle, displaySize);
                }
                else
                {
                    ImGui.TextDisabled("(preview loading...)");
                }
            }
            else
            {
                ImGui.TextDisabled("(preview unavailable)");
            }

            ImGui.TreePop();
        }

        ImGui.PopID();
        ImGui.PopID();
    }

    private static ToolCallEntry[] SnapshotToolCalls(ChatMessage message)
    {
        lock (message.ToolCallsSyncRoot)
            return [.. message.ToolCalls];
    }

    private static void AddToolCall(ChatMessage message, ToolCallEntry entry)
    {
        lock (message.ToolCallsSyncRoot)
            message.ToolCalls.Add(entry);
    }

    /// <summary>
    /// Snapshots the segment list for UI rendering.
    /// </summary>
    private static ContentSegment[] SnapshotSegments(ChatMessage message)
    {
        lock (message.SegmentsSyncRoot)
            return [.. message.Segments];
    }

    /// <summary>
    /// Gets the current text segment or creates one. A new text segment is started
    /// after a tool-call-group segment so text and tool calls alternate.
    /// </summary>
    private static ContentSegment GetOrCreateTextSegment(ChatMessage message)
    {
        lock (message.SegmentsSyncRoot)
        {
            if (message.Segments.Count > 0 && message.Segments[^1].Kind == ContentSegment.SegmentKind.Text)
                return message.Segments[^1];

            var seg = new ContentSegment
            {
                UiId = Interlocked.Increment(ref s_nextContentSegmentUiId),
                Kind = ContentSegment.SegmentKind.Text
            };
            message.Segments.Add(seg);
            return seg;
        }
    }

    /// <summary>
    /// Starts a new tool-call-group segment (or returns the current one if the last
    /// segment is already a tool group).
    /// </summary>
    private static ContentSegment GetOrCreateToolCallGroupSegment(ChatMessage message)
    {
        lock (message.SegmentsSyncRoot)
        {
            if (message.Segments.Count > 0 && message.Segments[^1].Kind == ContentSegment.SegmentKind.ToolCallGroup)
                return message.Segments[^1];

            var seg = new ContentSegment
            {
                UiId = Interlocked.Increment(ref s_nextContentSegmentUiId),
                Kind = ContentSegment.SegmentKind.ToolCallGroup
            };
            message.Segments.Add(seg);
            return seg;
        }
    }

    /// <summary>
    /// Adds a tool call entry to both the flat list (legacy/copy) and the current
    /// tool-call-group segment.
    /// </summary>
    private static void AddToolCallSegmented(ChatMessage message, ToolCallEntry entry)
    {
        lock (message.ToolCallsSyncRoot)
            message.ToolCalls.Add(entry);

        var group = GetOrCreateToolCallGroupSegment(message);
        lock (message.SegmentsSyncRoot)
            group.ToolCalls.Add(entry);
    }

    /// <summary>
    /// Syncs the current StringBuilder content to the current text segment.
    /// Call this whenever sb changes during streaming so the segment stays current.
    /// </summary>
    /// <param name="message">The chat message whose segments to update.</param>
    /// <param name="sb">The full accumulated StringBuilder for the entire response.</param>
    /// <param name="startOffset">
    /// The offset into <paramref name="sb"/> where the current round's text begins.
    /// Only text from this offset onward is written to the current text segment,
    /// preventing duplication when multiple tool-call rounds each produce model text.
    /// Pass 0 for single-round or non-looped streaming (e.g. Realtime).
    /// </param>
    private static void SyncTextSegment(ChatMessage message, StringBuilder sb, int startOffset = 0)
    {
        int length = sb.Length - startOffset;
        if (length <= 0)
            return;

        var seg = GetOrCreateTextSegment(message);
        seg.Text = sb.ToString(startOffset, length);
    }

    /// <summary>
    /// Strips the inline "Tool calls:" text section from content since tool calls
    /// are rendered separately via the structured <see cref="ChatMessage.ToolCalls"/> list.
    /// Also strips trailing status markers like "[Executing tool calls...]".
    /// </summary>
    private static string GetDisplayContent(string content)
    {
        if (string.IsNullOrWhiteSpace(content))
            return string.Empty;

        string text = content;

        // Strip protocol markers so they never show in the UI.
        text = text.Replace(AssistantDoneMarker, string.Empty, StringComparison.Ordinal);
        text = text.Replace(AssistantContinueMarker, string.Empty, StringComparison.Ordinal);

        // Remove trailing status markers like "[Executing tool calls...]".
        int bracketIdx = text.LastIndexOf('[');
        if (bracketIdx >= 0 && text.TrimEnd().EndsWith(']'))
        {
            string candidate = text[bracketIdx..];
            if (candidate.Contains("tool call", StringComparison.OrdinalIgnoreCase)
                || candidate.Contains("model response", StringComparison.OrdinalIgnoreCase)
                || candidate.Contains("Calling tool", StringComparison.OrdinalIgnoreCase)
                || candidate.Contains("tool result", StringComparison.OrdinalIgnoreCase)
                || candidate.Contains("Continuing", StringComparison.OrdinalIgnoreCase)
                || candidate.Contains("Sending", StringComparison.OrdinalIgnoreCase))
            {
                text = text[..bracketIdx].TrimEnd();
            }
        }

        return text.Trim();
    }

    private static string NormalizeForUiDisplay(string text)
    {
        if (string.IsNullOrEmpty(text))
            return string.Empty;

        string result = text
            .Replace("\u00A0", " ", StringComparison.Ordinal)
            .Replace("\u2013", "-", StringComparison.Ordinal)
            .Replace("\u2014", "-", StringComparison.Ordinal)
            .Replace("\u2018", "'", StringComparison.Ordinal)
            .Replace("\u2019", "'", StringComparison.Ordinal)
            .Replace("\u201C", "\"", StringComparison.Ordinal)
            .Replace("\u201D", "\"", StringComparison.Ordinal)
            .Replace("\u2026", "...", StringComparison.Ordinal)
            .Replace("\u2192", "->", StringComparison.Ordinal);

        // Fix missing space between sentences that got concatenated during
        // multi-round streaming (e.g. "frame it.Creating" -> "frame it. Creating").
        var sb = new StringBuilder(result.Length + 32);
        for (int i = 0; i < result.Length; i++)
        {
            char c = result[i];
            sb.Append(c);

            // After a sentence-ending punctuation followed immediately by an uppercase letter,
            // insert a space so the sentences are visually separated.
            if ((c == '.' || c == '!' || c == '?') && i + 1 < result.Length)
            {
                char next = result[i + 1];
                if (char.IsUpper(next))
                    sb.Append(' ');
            }
        }

        return sb.ToString();
    }
}
