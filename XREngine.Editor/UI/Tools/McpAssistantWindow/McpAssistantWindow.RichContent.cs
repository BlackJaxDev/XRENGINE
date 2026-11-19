using ImGuiNET;
using System.Net.Http.Headers;
using System.Net.WebSockets;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using XREngine.Components;
using XREngine.Data.Core;
using XREngine.Editor.Mcp;
using XREngine.Rendering;
using XREngine.Rendering.Vulkan;
using XREngine.Scene;
using XREngine.Rendering.UI;


namespace XREngine.Editor.UI.Tools;

public sealed partial class McpAssistantWindow
{
    private readonly ConditionalWeakTable<ChatMessage, Dictionary<int, CachedRichContent>> _richContentCache = new();

    /// <summary>
    /// Parses a message segment when its text changes and renders the saved lines
    /// while the message remains visible. A removed message releases its cache.
    /// </summary>
    private void RenderRichContentCached(ChatMessage message, int segmentKey, string source)
    {
        Dictionary<int, CachedRichContent> segments = _richContentCache.GetValue(message, static _ => []);
        if (!segments.TryGetValue(segmentKey, out CachedRichContent? cached)
            || !string.Equals(cached.Source, source, StringComparison.Ordinal))
        {
            cached = new CachedRichContent(source, ParseRichLines(NormalizeForUiDisplay(GetDisplayContent(source))));
            segments[segmentKey] = cached;
        }

        RenderRichLines(cached.Lines);
    }

    /// <summary>
    /// Splits one response into display lines and inline pieces. The UI can then
    /// draw unchanged text without splitting strings on every editor frame.
    /// </summary>
    private static RichLine[] ParseRichLines(string content)
    {
        string[] sourceLines = content.Split('\n');
        var result = new RichLine[sourceLines.Length];
        bool inCodeBlock = false;
        for (int index = 0; index < sourceLines.Length; index++)
        {
            string raw = sourceLines[index].TrimEnd('\r');
            string trimmed = raw.TrimStart();
            if (trimmed.StartsWith("```", StringComparison.Ordinal))
            {
                inCodeBlock = !inCodeBlock;
                result[index] = new RichLine(RichLineKind.Empty, string.Empty, 0f, []);
                continue;
            }

            if (inCodeBlock)
            {
                result[index] = new RichLine(RichLineKind.Code, raw, 12f, []);
                continue;
            }

            if (string.IsNullOrWhiteSpace(raw))
            {
                result[index] = new RichLine(RichLineKind.Empty, string.Empty, 0f, []);
                continue;
            }

            int headerLength = trimmed.StartsWith("### ", StringComparison.Ordinal) ? 4
                : trimmed.StartsWith("## ", StringComparison.Ordinal) ? 3
                : trimmed.StartsWith("# ", StringComparison.Ordinal) ? 2 : 0;
            if (headerLength > 0)
            {
                result[index] = new RichLine(RichLineKind.Heading, trimmed[headerLength..], 0f, []);
                continue;
            }

            if (trimmed.Length > 2 && (trimmed[0] == '-' || trimmed[0] == '*') && trimmed[1] == ' ')
            {
                float indent = (raw.Length - trimmed.Length) * 4f + 12f;
                result[index] = new RichLine(RichLineKind.Bullet, string.Empty, indent, ParseRichPieces(trimmed[2..]));
                continue;
            }

            result[index] = new RichLine(RichLineKind.Text, string.Empty, 0f, ParseRichPieces(raw));
        }
        return result;
    }

    private static RichPiece[] ParseRichPieces(string text)
    {
        var pieces = new List<RichPiece>();
        int position = 0;
        while (position < text.Length)
        {
            int backtick = text.IndexOf('`', position);
            int bold = text.IndexOf("**", position, StringComparison.Ordinal);
            int next = backtick >= 0 && (bold < 0 || backtick <= bold) ? backtick : bold;
            if (next < 0)
            {
                AddTextPieces(pieces, text[position..]);
                break;
            }

            if (next > position)
                AddTextPieces(pieces, text[position..next]);

            bool code = next == backtick;
            int markerLength = code ? 1 : 2;
            int close = code
                ? text.IndexOf('`', next + markerLength)
                : text.IndexOf("**", next + markerLength, StringComparison.Ordinal);
            if (close < 0)
            {
                AddTextPieces(pieces, text[next..]);
                break;
            }

            string markedText = text[(next + markerLength)..close];
            RichPieceKind kind = code
                ? Guid.TryParse(markedText.Trim(), out _) ? RichPieceKind.Guid : RichPieceKind.Code
                : RichPieceKind.Bold;
            pieces.Add(new RichPiece(kind, kind == RichPieceKind.Guid ? markedText.Trim() : markedText));
            position = close + markerLength;
        }
        return [.. pieces];
    }

    private static void AddTextPieces(List<RichPiece> pieces, string text)
    {
        if (text.Length == 0)
            return;

        MatchCollection matches = GuidPattern.Matches(text);
        int position = 0;
        foreach (Match match in matches)
        {
            if (match.Index > position)
                pieces.Add(new RichPiece(RichPieceKind.Text, text[position..match.Index]));
            pieces.Add(new RichPiece(RichPieceKind.Guid, match.Value));
            position = match.Index + match.Length;
        }

        if (position < text.Length)
            pieces.Add(new RichPiece(RichPieceKind.Text, text[position..]));
    }

    /// <summary>
    /// Draws parsed text while preserving scene-object links and simple formatting.
    /// </summary>
    private static void RenderRichLines(RichLine[] lines)
    {
        for (int index = 0; index < lines.Length; index++)
        {
            RichLine line = lines[index];
            if (line.Kind == RichLineKind.Empty)
            {
                ImGui.Spacing();
                continue;
            }

            if (line.Kind == RichLineKind.Code)
            {
                ImGui.Indent(line.Indent);
                ImGui.PushTextWrapPos(0f);
                ImGui.TextColored(ColorCodeFg, line.Text);
                ImGui.PopTextWrapPos();
                ImGui.Unindent(line.Indent);
                continue;
            }

            if (line.Kind == RichLineKind.Heading)
            {
                ImGui.Spacing();
                ImGui.PushTextWrapPos(0f);
                ImGui.TextColored(ColorHeader, line.Text);
                ImGui.PopTextWrapPos();
                continue;
            }

            if (line.Kind == RichLineKind.Bullet)
            {
                ImGui.Indent(line.Indent);
                ImGui.TextColored(ColorBullet, "*");
                ImGui.SameLine();
                RenderRichPieces(line.Pieces);
                ImGui.Unindent(line.Indent);
                continue;
            }

            RenderRichPieces(line.Pieces);
        }
    }

    /// <summary>
    /// Draws saved inline pieces without rescanning markdown or GUIDs each frame.
    /// </summary>
    private static void RenderRichPieces(RichPiece[] pieces)
    {
        if (pieces.Length == 1 && pieces[0].Kind == RichPieceKind.Text)
        {
            ImGui.PushTextWrapPos(0f);
            ImGui.TextUnformatted(pieces[0].Text);
            ImGui.PopTextWrapPos();
            return;
        }

        bool first = true;
        ImGui.PushTextWrapPos(0f);
        for (int index = 0; index < pieces.Length; index++)
        {
            RichPiece piece = pieces[index];
            if (!first && piece.Kind != RichPieceKind.Guid)
            {
                ImGui.SameLine(0f, 0f);
                // A long styled span needs the full line instead of a narrow trailing column.
                if (ImGui.CalcTextSize(piece.Text).X > ImGui.GetContentRegionAvail().X)
                    ImGui.NewLine();
            }
            switch (piece.Kind)
            {
                case RichPieceKind.Text:
                    ImGui.TextUnformatted(piece.Text);
                    first = RichPieceWrapped();
                    break;
                case RichPieceKind.Guid:
                    RenderGuidLink(piece.Text, ref first, piece);
                    break;
                case RichPieceKind.Bold:
                    ImGui.TextColored(ColorBold, piece.Text);
                    first = RichPieceWrapped();
                    break;
                case RichPieceKind.Code:
                    RenderCodeSpan(piece.Text);
                    first = RichPieceWrapped();
                    break;
            }
        }
        ImGui.PopTextWrapPos();
    }

    /// <summary>Keeps the next styled piece on a new row after text wraps.</summary>
    private static bool RichPieceWrapped()
        => ImGui.GetItemRectSize().Y > ImGui.GetTextLineHeightWithSpacing() * 1.25f;

    /// <summary>
    /// Draws short code over a shaded area and wraps code that exceeds the row.
    /// The parser sends GUID-only spans to the scene-object link renderer.
    /// </summary>
    private static void RenderCodeSpan(string code)
    {
        Vector2 textSize = ImGui.CalcTextSize(code);
        if (textSize.X > ImGui.GetContentRegionAvail().X)
        {
            ImGui.PushTextWrapPos(0f);
            ImGui.TextColored(ColorCodeFg, code);
            ImGui.PopTextWrapPos();
            return;
        }

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
    /// Renders a GUID as a small clickable button that selects the corresponding
    /// scene node (or other XRObject) when clicked. Shows the object name as
    /// display text and the full GUID in a tooltip.
    /// </summary>
    private static void RenderGuidLink(string guidStr, ref bool first, RichPiece? cachedPiece = null)
    {
        Guid guid;
        if (cachedPiece?.ParsedGuid is Guid parsed)
            guid = parsed;
        else if (!Guid.TryParse(guidStr, out guid))
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
            displayName = cachedPiece?.MissingObjectLabel ?? guidStr[..8] + "...";
        }

        if (!first) ImGui.SameLine(0, 0);

        ImGui.PushStyleColor(ImGuiCol.Button, ColorGuidButton);
        ImGui.PushStyleColor(ImGuiCol.ButtonHovered, ColorGuidButtonHover);
        ImGui.PushStyleColor(ImGuiCol.ButtonActive, ColorGuidButtonActive);
        ImGui.PushStyleColor(ImGuiCol.Text, ColorGuidText);

        // Use the full GUID as a unique ImGui ID.
        bool clicked = ImGui.SmallButton(cachedPiece?.GetButtonLabel(displayName)
            ?? $"Select node: {displayName}##{guidStr}");

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
