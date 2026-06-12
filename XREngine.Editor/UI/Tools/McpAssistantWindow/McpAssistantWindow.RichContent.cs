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
using XREngine.Rendering;
using XREngine.Rendering.OpenGL;
using XREngine.Rendering.Vulkan;
using XREngine.Scene;
using XREngine.Rendering.UI;


namespace XREngine.Editor.UI.Tools;

public sealed partial class McpAssistantWindow
{
    // ── Rich Content Renderer (Markdown-lite for ImGui) ──────────────────

    /// <summary>
    /// Renders text with basic markdown formatting:
    /// fenced code blocks, headers, bullet points, inline backtick code, and **bold**.
    /// </summary>
    private static void RenderRichContent(string content)
    {
        string[] lines = content.Split('\n');
        bool inCodeBlock = false;

        for (int li = 0; li < lines.Length; li++)
        {
            string rawLine = lines[li].TrimEnd('\r');

            // Fenced code block toggle.
            if (rawLine.TrimStart().StartsWith("```"))
            {
                inCodeBlock = !inCodeBlock;
                ImGui.Spacing();
                continue;
            }

            if (inCodeBlock)
            {
                ImGui.Indent(12f);
                ImGui.TextColored(ColorCodeFg, rawLine);
                ImGui.Unindent(12f);
                continue;
            }

            // Empty line -> spacing.
            if (string.IsNullOrWhiteSpace(rawLine))
            {
                ImGui.Spacing();
                continue;
            }

            string trimmed = rawLine.TrimStart();

            // Headers.
            if (trimmed.StartsWith("### "))
            {
                ImGui.Spacing();
                ImGui.TextColored(ColorHeader, trimmed[4..]);
                continue;
            }
            if (trimmed.StartsWith("## "))
            {
                ImGui.Spacing();
                ImGui.TextColored(ColorHeader, trimmed[3..]);
                continue;
            }
            if (trimmed.StartsWith("# "))
            {
                ImGui.Spacing();
                ImGui.TextColored(ColorHeader, trimmed[2..]);
                continue;
            }

            // Bullet points.
            if (trimmed.Length > 2 && (trimmed[0] == '-' || trimmed[0] == '*') && trimmed[1] == ' ')
            {
                int indentChars = rawLine.Length - trimmed.Length;
                float indent = indentChars * 4f + 12f;
                ImGui.Indent(indent);
                ImGui.TextColored(ColorBullet, "*");
                ImGui.SameLine();
                RenderInlineFormatted(trimmed[2..]);
                ImGui.Unindent(indent);
                continue;
            }

            // Regular line with inline formatting.
            RenderInlineFormatted(rawLine);
        }
    }

    /// <summary>
    /// Renders a single line with inline backtick code and **bold** formatting.
    /// Falls back to plain word-wrapped text when no formatting markers are found.
    /// </summary>
    private static void RenderInlineFormatted(string text)
    {
        // Fast path: no inline markers.
        if (!text.Contains('`') && !text.Contains("**", StringComparison.Ordinal))
        {
            // If GUIDs are present, render with clickable links instead of plain text.
            if (ContainsGuid(text))
            {
                bool f = true;
                RenderTextWithGuidLinks(text, ref f);
                return;
            }

            ImGui.PushTextWrapPos(0.0f);
            ImGui.TextUnformatted(text);
            ImGui.PopTextWrapPos();
            return;
        }

        int pos = 0;
        bool first = true;

        while (pos < text.Length)
        {
            int backtick = text.IndexOf('`', pos);
            int bold = text.IndexOf("**", pos, StringComparison.Ordinal);

            // Find the nearest special marker.
            int next = -1;
            bool isBacktick = false;

            if (backtick >= 0 && (bold < 0 || backtick <= bold))
            {
                next = backtick;
                isBacktick = true;
            }
            else if (bold >= 0)
            {
                next = bold;
            }

            if (next < 0)
            {
                // No more markers -- render the remainder.
                string rest = text[pos..];
                if (rest.Length > 0)
                    RenderTextWithGuidLinks(rest, ref first);
                break;
            }

            // Render plain text before the marker.
            if (next > pos)
            {
                string before = text[pos..next];
                RenderTextWithGuidLinks(before, ref first);
                pos = next;
            }

            if (isBacktick)
            {
                int close = text.IndexOf('`', pos + 1);
                if (close < 0)
                {
                    RenderTextWithGuidLinks(text[pos..], ref first);
                    break;
                }

                string code = text[(pos + 1)..close];
                if (!first) ImGui.SameLine(0, 0);
                RenderCodeSpan(code);
                first = false;
                pos = close + 1;
            }
            else
            {
                int close = text.IndexOf("**", pos + 2, StringComparison.Ordinal);
                if (close < 0)
                {
                    RenderTextWithGuidLinks(text[pos..], ref first);
                    break;
                }

                string boldText = text[(pos + 2)..close];
                if (!first) ImGui.SameLine(0, 0);
                ImGui.TextColored(ColorBold, boldText);
                first = false;
                pos = close + 2;
            }
        }
    }

    /// <summary>
    /// Renders an inline code span with a subtle background rectangle.
    /// If the code span is a single GUID, renders it as a clickable node-select button instead.
    /// </summary>
    private static void RenderCodeSpan(string code)
    {
        // If the entire code span is a GUID, render as a clickable link.
        string trimmed = code.Trim();
        if (trimmed.Length == 36 && Guid.TryParse(trimmed, out _))
        {
            bool unused = true;
            RenderGuidLink(trimmed, ref unused);
            return;
        }

        Vector2 textSize = ImGui.CalcTextSize(code);
        Vector2 cursorPos = ImGui.GetCursorScreenPos();
        var drawList = ImGui.GetWindowDrawList();

        const float padX = 3f;
        const float padY = 1f;
        drawList.AddRectFilled(
            cursorPos - new Vector2(padX, padY),
            cursorPos + textSize + new Vector2(padX, padY),
            ImGui.ColorConvertFloat4ToU32(ColorCodeBlockBg),
            3f);

        ImGui.TextColored(ColorCodeFg, code);
    }

    // ── GUID Link Rendering ──────────────────────────────────────────────

    /// <summary>
    /// Returns true if the text contains at least one GUID pattern.
    /// </summary>
    private static bool ContainsGuid(string text)
        => GuidPattern.IsMatch(text);

    /// <summary>
    /// Renders a GUID as a small clickable button that selects the corresponding
    /// scene node (or other XRObject) when clicked. Shows the object name as
    /// display text and the full GUID in a tooltip.
    /// </summary>
    private static void RenderGuidLink(string guidStr, ref bool first)
    {
        if (!Guid.TryParse(guidStr, out var guid))
        {
            if (!first) ImGui.SameLine(0, 0);
            ImGui.TextUnformatted(guidStr);
            first = false;
            return;
        }

        // Look up the object in the global cache.
        string displayName;
        XRObjectBase? obj = null;
        if (XRObjectBase.ObjectsCache.TryGetValue(guid, out var resolved) && !resolved.IsDestroyed)
        {
            obj = resolved;
            displayName = obj switch
            {
                SceneNode sn => sn.Name ?? "<unnamed>",
                _ => obj.GetType().Name
            };
        }
        else
        {
            // Object not in cache — show truncated GUID.
            displayName = guidStr[..8] + "...";
        }

        if (!first) ImGui.SameLine(0, 0);

        ImGui.PushStyleColor(ImGuiCol.Button, ColorGuidButton);
        ImGui.PushStyleColor(ImGuiCol.ButtonHovered, ColorGuidButtonHover);
        ImGui.PushStyleColor(ImGuiCol.ButtonActive, ColorGuidButtonActive);
        ImGui.PushStyleColor(ImGuiCol.Text, ColorGuidText);

        // Use the full GUID as a unique ImGui ID.
        bool clicked = ImGui.SmallButton($"Select node: {displayName}##{guidStr}");

        ImGui.PopStyleColor(4);

        if (ImGui.IsItemHovered())
        {
            ImGui.BeginTooltip();
            ImGui.TextUnformatted(guidStr);
            if (obj is SceneNode sn)
                ImGui.TextDisabled($"Click to select \"{sn.Name}\"");
            else if (obj is not null)
                ImGui.TextDisabled($"Type: {obj.GetType().Name}");
            else
                ImGui.TextDisabled("Object not found in cache");
            ImGui.EndTooltip();
        }

        if (clicked && obj is SceneNode clickedNode)
            Selection.SceneNodes = [clickedNode];

        first = false;
    }

    /// <summary>
    /// Renders a text segment inline, replacing any embedded GUID patterns with
    /// clickable <see cref="RenderGuidLink"/> buttons. Non-GUID text is rendered
    /// as plain <see cref="ImGui.TextUnformatted"/>.
    /// </summary>
    private static void RenderTextWithGuidLinks(string text, ref bool first)
    {
        if (string.IsNullOrEmpty(text))
            return;

        var matches = GuidPattern.Matches(text);
        if (matches.Count == 0)
        {
            if (!first) ImGui.SameLine(0, 0);
            ImGui.TextUnformatted(text);
            first = false;
            return;
        }

        int pos = 0;
        foreach (Match match in matches)
        {
            // Render text before the GUID.
            if (match.Index > pos)
            {
                string before = text[pos..match.Index];
                if (!first) ImGui.SameLine(0, 0);
                ImGui.TextUnformatted(before);
                first = false;
            }

            // Render the GUID as a clickable link.
            RenderGuidLink(match.Value, ref first);

            pos = match.Index + match.Length;
        }

        // Render remaining text after the last GUID.
        if (pos < text.Length)
        {
            string after = text[pos..];
            if (!first) ImGui.SameLine(0, 0);
            ImGui.TextUnformatted(after);
            first = false;
        }
    }

    /// <summary>
    /// Like <see cref="RenderTextWithGuidLinks"/> but renders non-GUID text
    /// segments with <see cref="ImGui.TextColored"/> instead of plain text.
    /// GUIDs still render as clickable buttons via <see cref="RenderGuidLink"/>.
    /// </summary>
    private static void RenderColoredTextWithGuidLinks(string text, Vector4 color, ref bool first)
    {
        if (string.IsNullOrEmpty(text))
            return;

        var matches = GuidPattern.Matches(text);
        if (matches.Count == 0)
        {
            if (!first) ImGui.SameLine(0, 0);
            ImGui.TextColored(color, text);
            first = false;
            return;
        }

        int pos = 0;
        foreach (Match match in matches)
        {
            if (match.Index > pos)
            {
                string before = text[pos..match.Index];
                if (!first) ImGui.SameLine(0, 0);
                ImGui.TextColored(color, before);
                first = false;
            }

            RenderGuidLink(match.Value, ref first);
            pos = match.Index + match.Length;
        }

        if (pos < text.Length)
        {
            string after = text[pos..];
            if (!first) ImGui.SameLine(0, 0);
            ImGui.TextColored(color, after);
            first = false;
        }
    }

    private static string StripGuidsForDisplay(string text)
    {
        if (string.IsNullOrEmpty(text))
            return string.Empty;

        string stripped = GuidPattern.Replace(text, "<node>");
        return Regex.Replace(stripped, @"\s+", " ").Trim();
    }

    private static void DrawInlineGuidButtonsFromText(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return;

        MatchCollection matches = GuidPattern.Matches(text);
        if (matches.Count == 0)
            return;

        var seen = new HashSet<Guid>();
        for (int i = 0; i < matches.Count; i++)
        {
            string value = matches[i].Value;
            if (!Guid.TryParse(value, out Guid guid) || !seen.Add(guid))
                continue;

            bool first = false;
            RenderGuidLink(value, ref first);
        }
    }
}
