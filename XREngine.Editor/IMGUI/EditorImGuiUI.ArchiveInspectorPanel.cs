using System;
using System.Collections.Generic;
using System.IO;
using System.Numerics;
using System.Text;
using ImGuiNET;
using XREngine;
using XREngine.Core.Files;
using XREngine.Diagnostics;

namespace XREngine.Editor;

public static partial class EditorImGuiUI
{
    private static bool _showArchiveInspector;

    // ── Loaded archive state ──────────────────────────────────────────
    private static string? _archiveInspectorPath;
    private static AssetPacker.ArchiveInfo? _archiveInspectorInfo;
    private static string? _archiveInspectorError;
    private static ArchiveInspectorTreeNode? _archiveInspectorRoot;
    private static AssetPacker.ArchiveEntryInfo? _archiveInspectorSelectedEntry;
    private static string? _archiveInspectorSelectedPath;

    // ── On-demand decompressed content ────────────────────────────────
    private static byte[]? _archiveInspectorDecompressedData;
    private static string? _archiveInspectorDecompressedText;
    private static bool _archiveInspectorDecompressError;
    private static string? _archiveInspectorDecompressErrorMsg;
    private static bool _archiveInspectorShowHex;
    private static string _archiveInspectorFilterText = string.Empty;

    private const string ArchiveInspectorFileDialogId = "ArchiveInspectorOpenDialog";
    private const int HexBytesPerRow = 16;
    private const int MaxTextPreviewBytes = 2 * 1024 * 1024; // 2 MB limit for text preview

    // ── Tree node for folder hierarchy ────────────────────────────────
    private sealed class ArchiveInspectorTreeNode
    {
        public string Name { get; init; } = string.Empty;
        public string FullPath { get; init; } = string.Empty;
        public bool IsDirectory { get; init; }
        public AssetPacker.ArchiveEntryInfo? Entry { get; init; }
        public List<ArchiveInspectorTreeNode> Children { get; } = [];

        /// <summary>Total compressed bytes for this node (file) or all descendants (folder).</summary>
        public long TotalCompressedBytes { get; set; }
        /// <summary>Number of files at or below this node.</summary>
        public int TotalFileCount { get; set; }
    }

    // ═══════════════════════════════════════════════════════════════════
    // Panel entry point
    // ═══════════════════════════════════════════════════════════════════

    private static void DrawArchiveInspectorPanel()
    {
        if (!_showArchiveInspector)
            return;

        ImGui.SetNextWindowSize(new Vector2(900, 650), ImGuiCond.FirstUseEver);
        if (!ImGui.Begin("Archive Inspector", ref _showArchiveInspector, ImGuiWindowFlags.MenuBar))
        {
            ImGui.End();
            return;
        }

        using var profilerScope = Engine.Profiler.Start("UI.DrawArchiveInspectorPanel");

        DrawArchiveInspectorMenuBar();

        if (_archiveInspectorError is not null)
        {
            ImGui.PushStyleColor(ImGuiCol.Text, new Vector4(1.0f, 0.3f, 0.3f, 1.0f));
            ImGui.TextWrapped(_archiveInspectorError);
            ImGui.PopStyleColor();
        }
        else if (_archiveInspectorInfo is null)
        {
            ImGui.TextDisabled("No archive loaded. Use File > Open to select a .pak file.");
        }
        else
        {
            DrawArchiveInspectorContent();
        }

        ImGui.End();
    }

    // ═══════════════════════════════════════════════════════════════════
    // Menu bar
    // ═══════════════════════════════════════════════════════════════════

    private static void DrawArchiveInspectorMenuBar()
    {
        if (!ImGui.BeginMenuBar())
            return;

        if (ImGui.BeginMenu("File"))
        {
            if (ImGui.MenuItem("Open...", "Ctrl+O"))
                OpenArchiveInspectorFileDialog();

            bool hasArchive = _archiveInspectorInfo is not null;
            if (ImGui.MenuItem("Close", null, false, hasArchive))
                ClearArchiveInspectorState();

            ImGui.EndMenu();
        }

        ImGui.EndMenuBar();
    }

    private static void OpenArchiveInspectorFileDialog()
    {
        UI.ImGuiFileBrowser.OpenFile(
            ArchiveInspectorFileDialogId,
            "Open Packed Archive",
            result =>
            {
                if (result.Success && !string.IsNullOrEmpty(result.SelectedPath))
                    LoadArchiveForInspector(result.SelectedPath);
            },
            "PAK Archives (*.pak)|*.pak|All Files (*.*)|*.*");
    }

    // ═══════════════════════════════════════════════════════════════════
    // Loading
    // ═══════════════════════════════════════════════════════════════════

    private static void LoadArchiveForInspector(string path)
    {
        ClearArchiveInspectorState();
        _archiveInspectorPath = path;

        try
        {
            _archiveInspectorInfo = AssetPacker.ReadArchiveInfo(path);
            _archiveInspectorRoot = BuildArchiveInspectorTree(_archiveInspectorInfo);
        }
        catch (Exception ex)
        {
            _archiveInspectorError = $"Failed to read archive: {ex.Message}";
            Debug.LogException(ex, $"Archive Inspector: failed to read '{path}'.");
        }
    }

    private static void ClearArchiveInspectorState()
    {
        _archiveInspectorPath = null;
        _archiveInspectorInfo = null;
        _archiveInspectorError = null;
        _archiveInspectorRoot = null;
        _archiveInspectorSelectedEntry = null;
        _archiveInspectorSelectedPath = null;
        _archiveInspectorDecompressedData = null;
        _archiveInspectorDecompressedText = null;
        _archiveInspectorDecompressError = false;
        _archiveInspectorDecompressErrorMsg = null;
        _archiveInspectorShowHex = false;
        _archiveInspectorFilterText = string.Empty;
    }

    // ═══════════════════════════════════════════════════════════════════
    // Tree construction
    // ═══════════════════════════════════════════════════════════════════

    private static ArchiveInspectorTreeNode BuildArchiveInspectorTree(AssetPacker.ArchiveInfo info)
    {
        var root = new ArchiveInspectorTreeNode
        {
            Name = Path.GetFileName(info.FilePath),
            FullPath = string.Empty,
            IsDirectory = true,
        };

        // Maps directory path → node.
        var directoryMap = new Dictionary<string, ArchiveInspectorTreeNode>(StringComparer.OrdinalIgnoreCase)
        {
            [string.Empty] = root,
        };

        foreach (var entry in info.Entries)
        {
            string normalized = entry.Path.Replace('\\', '/');
            string[] parts = normalized.Split('/', StringSplitOptions.RemoveEmptyEntries);

            var parent = root;
            for (int i = 0; i < parts.Length - 1; i++)
            {
                string dirPath = string.Join('/', parts, 0, i + 1);
                if (!directoryMap.TryGetValue(dirPath, out var dirNode))
                {
                    dirNode = new ArchiveInspectorTreeNode
                    {
                        Name = parts[i],
                        FullPath = dirPath,
                        IsDirectory = true,
                    };
                    parent.Children.Add(dirNode);
                    directoryMap[dirPath] = dirNode;
                }
                parent = dirNode;
            }

            string fileName = parts.Length > 0 ? parts[^1] : normalized;
            var fileNode = new ArchiveInspectorTreeNode
            {
                Name = fileName,
                FullPath = normalized,
                IsDirectory = false,
                Entry = entry,
                TotalCompressedBytes = entry.CompressedSize,
                TotalFileCount = 1,
            };
            parent.Children.Add(fileNode);
        }

        // Sort children: directories first, then alphabetical.
        SortArchiveInspectorTree(root);

        // Accumulate folder sizes.
        AccumulateArchiveInspectorSizes(root);

        return root;
    }

    private static void SortArchiveInspectorTree(ArchiveInspectorTreeNode node)
    {
        node.Children.Sort((a, b) =>
        {
            if (a.IsDirectory != b.IsDirectory)
                return a.IsDirectory ? -1 : 1;
            return string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase);
        });

        foreach (var child in node.Children)
        {
            if (child.IsDirectory)
                SortArchiveInspectorTree(child);
        }
    }

    private static void AccumulateArchiveInspectorSizes(ArchiveInspectorTreeNode node)
    {
        if (!node.IsDirectory)
            return;

        long totalBytes = 0;
        int totalFiles = 0;

        foreach (var child in node.Children)
        {
            AccumulateArchiveInspectorSizes(child);
            totalBytes += child.TotalCompressedBytes;
            totalFiles += child.TotalFileCount;
        }

        node.TotalCompressedBytes = totalBytes;
        node.TotalFileCount = totalFiles;
    }

    // ═══════════════════════════════════════════════════════════════════
    // Main content layout (header + splitter)
    // ═══════════════════════════════════════════════════════════════════

    private static void DrawArchiveInspectorContent()
    {
        var info = _archiveInspectorInfo!;

        // ── Archive header details ────────────────────────────────────
        if (ImGui.CollapsingHeader("Archive Details", ImGuiTreeNodeFlags.DefaultOpen))
        {
            if (ImGui.BeginTable("ArchiveDetailsTable", 2, ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg | ImGuiTableFlags.SizingStretchProp))
            {
                ImGui.TableSetupColumn("Property", ImGuiTableColumnFlags.WidthFixed, 180.0f);
                ImGui.TableSetupColumn("Value", ImGuiTableColumnFlags.WidthStretch);

                DrawArchiveDetailRow("File", info.FilePath);
                DrawArchiveDetailRow("File Size", FormatBytes(info.FileSize));
                DrawArchiveDetailRow("Magic", $"0x{info.MagicNumber:X8} (\"FREK\")");
                DrawArchiveDetailRow("Version", info.Version.ToString());
                DrawArchiveDetailRow("Lookup Mode", info.LookupMode.ToString());
                DrawArchiveDetailRow("File Count", info.FileCount.ToString());
                DrawArchiveDetailRow("Total Compressed", FormatBytes(info.TotalCompressedBytes));
                DrawArchiveDetailRow("TOC Offset", $"0x{info.TocOffset:X}");
                DrawArchiveDetailRow("String Table Offset", $"0x{info.StringTableOffset:X}");
                if (info.IndexTableOffset != 0)
                    DrawArchiveDetailRow("Index Table Offset", $"0x{info.IndexTableOffset:X}");

                ImGui.EndTable();
            }
        }

        ImGui.Separator();

        // ── Filter ────────────────────────────────────────────────────
        ImGui.SetNextItemWidth(300.0f);
        ImGui.InputTextWithHint("##ArchiveFilter", "Filter files...", ref _archiveInspectorFilterText, 256);
        ImGui.SameLine();
        ImGui.TextDisabled($"({info.FileCount} files)");

        ImGui.Separator();

        // ── Two-pane layout: tree on the left, content on the right ──
        float availWidth = ImGui.GetContentRegionAvail().X;
        float treeWidth = MathF.Max(availWidth * 0.4f, 250.0f);
        float contentWidth = availWidth - treeWidth - ImGui.GetStyle().ItemSpacing.X;

        // Left pane — file tree.
        if (ImGui.BeginChild("ArchiveTreePane", new Vector2(treeWidth, -1.0f), ImGuiChildFlags.Border | ImGuiChildFlags.ResizeX))
        {
            if (_archiveInspectorRoot is not null)
                DrawArchiveInspectorTreeNode(_archiveInspectorRoot, isRoot: true);
        }
        ImGui.EndChild();

        ImGui.SameLine();

        // Right pane — selected entry details / content preview.
        if (ImGui.BeginChild("ArchiveContentPane", new Vector2(contentWidth, -1.0f), ImGuiChildFlags.Border))
        {
            DrawArchiveInspectorSelectedContent();
        }
        ImGui.EndChild();
    }

    private static void DrawArchiveDetailRow(string label, string value)
    {
        ImGui.TableNextRow();
        ImGui.TableSetColumnIndex(0);
        ImGui.TextUnformatted(label);
        ImGui.TableSetColumnIndex(1);
        ImGui.TextUnformatted(value);
    }

    // ═══════════════════════════════════════════════════════════════════
    // Tree rendering
    // ═══════════════════════════════════════════════════════════════════

    private static void DrawArchiveInspectorTreeNode(ArchiveInspectorTreeNode node, bool isRoot = false)
    {
        bool hasFilter = !string.IsNullOrWhiteSpace(_archiveInspectorFilterText);

        foreach (var child in node.Children)
        {
            if (hasFilter && !NodeMatchesFilter(child, _archiveInspectorFilterText))
                continue;

            if (child.IsDirectory)
            {
                var flags = ImGuiTreeNodeFlags.OpenOnArrow | ImGuiTreeNodeFlags.OpenOnDoubleClick | ImGuiTreeNodeFlags.SpanAvailWidth;
                if (hasFilter)
                    flags |= ImGuiTreeNodeFlags.DefaultOpen;

                bool open = ImGui.TreeNodeEx($"\uF07B {child.Name}##dir_{child.FullPath}", flags);

                // Tooltip with folder stats.
                if (ImGui.IsItemHovered())
                {
                    ImGui.BeginTooltip();
                    ImGui.Text($"{child.TotalFileCount} file(s), {FormatBytes(child.TotalCompressedBytes)} compressed");
                    ImGui.EndTooltip();
                }

                if (open)
                {
                    DrawArchiveInspectorTreeNode(child);
                    ImGui.TreePop();
                }
            }
            else
            {
                var flags = ImGuiTreeNodeFlags.Leaf | ImGuiTreeNodeFlags.NoTreePushOnOpen | ImGuiTreeNodeFlags.SpanAvailWidth;
                bool isSelected = string.Equals(_archiveInspectorSelectedPath, child.FullPath, StringComparison.Ordinal);
                if (isSelected)
                    flags |= ImGuiTreeNodeFlags.Selected;

                ImGui.TreeNodeEx($"\uF15B {child.Name}##file_{child.FullPath}", flags);

                if (ImGui.IsItemClicked())
                    SelectArchiveInspectorEntry(child);

                // Tooltip with file info.
                if (ImGui.IsItemHovered())
                {
                    ImGui.BeginTooltip();
                    ImGui.Text($"Compressed: {FormatBytes(child.Entry!.Value.CompressedSize)}");
                    ImGui.Text($"Hash: 0x{child.Entry!.Value.Hash:X8}");
                    ImGui.Text($"Offset: 0x{child.Entry!.Value.DataOffset:X}");
                    ImGui.EndTooltip();
                }
            }
        }
    }

    private static bool NodeMatchesFilter(ArchiveInspectorTreeNode node, string filter)
    {
        if (node.FullPath.Contains(filter, StringComparison.OrdinalIgnoreCase))
            return true;

        if (node.IsDirectory)
        {
            foreach (var child in node.Children)
            {
                if (NodeMatchesFilter(child, filter))
                    return true;
            }
        }

        return false;
    }

    // ═══════════════════════════════════════════════════════════════════
    // Selection & decompression
    // ═══════════════════════════════════════════════════════════════════

    private static void SelectArchiveInspectorEntry(ArchiveInspectorTreeNode fileNode)
    {
        if (string.Equals(_archiveInspectorSelectedPath, fileNode.FullPath, StringComparison.Ordinal))
            return; // Already selected.

        _archiveInspectorSelectedEntry = fileNode.Entry;
        _archiveInspectorSelectedPath = fileNode.FullPath;

        // Clear previous decompressed content — it will be loaded on demand.
        _archiveInspectorDecompressedData = null;
        _archiveInspectorDecompressedText = null;
        _archiveInspectorDecompressError = false;
        _archiveInspectorDecompressErrorMsg = null;
        _archiveInspectorShowHex = false;
    }

    private static void DecompressSelectedEntry()
    {
        if (_archiveInspectorSelectedEntry is null || _archiveInspectorPath is null)
            return;

        try
        {
            byte[] data = AssetPacker.DecompressEntry(_archiveInspectorPath, _archiveInspectorSelectedEntry.Value);
            _archiveInspectorDecompressedData = data;
            _archiveInspectorDecompressError = false;
            _archiveInspectorDecompressErrorMsg = null;

            // Attempt to interpret as text (UTF-8).
            if (data.Length <= MaxTextPreviewBytes && LooksLikeText(data))
            {
                _archiveInspectorDecompressedText = Encoding.UTF8.GetString(data);
            }
            else
            {
                _archiveInspectorDecompressedText = null;
            }
        }
        catch (Exception ex)
        {
            _archiveInspectorDecompressError = true;
            _archiveInspectorDecompressErrorMsg = ex.Message;
            _archiveInspectorDecompressedData = null;
            _archiveInspectorDecompressedText = null;
            Debug.LogException(ex, $"Archive Inspector: failed to decompress '{_archiveInspectorSelectedPath}'.");
        }
    }

    // ═══════════════════════════════════════════════════════════════════
    // Right pane — selected file content
    // ═══════════════════════════════════════════════════════════════════

    private static void DrawArchiveInspectorSelectedContent()
    {
        if (_archiveInspectorSelectedEntry is null)
        {
            ImGui.TextDisabled("Select a file to view its details.");
            return;
        }

        var entry = _archiveInspectorSelectedEntry.Value;

        // ── Entry details table ───────────────────────────────────────
        ImGui.TextUnformatted(_archiveInspectorSelectedPath ?? entry.Path);
        ImGui.Separator();

        if (ImGui.BeginTable("EntryDetailsTable", 2, ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg | ImGuiTableFlags.SizingStretchProp))
        {
            ImGui.TableSetupColumn("Property", ImGuiTableColumnFlags.WidthFixed, 150.0f);
            ImGui.TableSetupColumn("Value", ImGuiTableColumnFlags.WidthStretch);

            DrawArchiveDetailRow("Path", entry.Path);
            DrawArchiveDetailRow("Hash", $"0x{entry.Hash:X8}");
            DrawArchiveDetailRow("Data Offset", $"0x{entry.DataOffset:X}");
            DrawArchiveDetailRow("Compressed Size", FormatBytes(entry.CompressedSize));

            if (_archiveInspectorDecompressedData is not null)
                DrawArchiveDetailRow("Decompressed Size", FormatBytes(_archiveInspectorDecompressedData.Length));

            ImGui.EndTable();
        }

        ImGui.Spacing();

        // ── Decompress button ─────────────────────────────────────────
        if (_archiveInspectorDecompressedData is null && !_archiveInspectorDecompressError)
        {
            if (ImGui.Button("Decompress & Preview", new Vector2(200, 0)))
                DecompressSelectedEntry();

            ImGui.SameLine();
            ImGui.TextDisabled("Data is decompressed on demand.");
            return;
        }

        if (_archiveInspectorDecompressError)
        {
            ImGui.PushStyleColor(ImGuiCol.Text, new Vector4(1.0f, 0.3f, 0.3f, 1.0f));
            ImGui.TextWrapped($"Decompression failed: {_archiveInspectorDecompressErrorMsg}");
            ImGui.PopStyleColor();

            if (ImGui.Button("Retry"))
                DecompressSelectedEntry();
            return;
        }

        // ── Content preview ───────────────────────────────────────────
        byte[] data = _archiveInspectorDecompressedData!;

        // View mode toggle.
        bool hasText = _archiveInspectorDecompressedText is not null;
        if (hasText)
        {
            if (ImGui.RadioButton("Text", !_archiveInspectorShowHex))
                _archiveInspectorShowHex = false;
            ImGui.SameLine();
        }
        if (ImGui.RadioButton("Hex", _archiveInspectorShowHex))
            _archiveInspectorShowHex = true;

        ImGui.SameLine();
        ImGui.TextDisabled($"({FormatBytes(data.Length)})");

        ImGui.Separator();

        if (!_archiveInspectorShowHex && hasText)
        {
            DrawArchiveInspectorTextPreview(_archiveInspectorDecompressedText!);
        }
        else
        {
            DrawArchiveInspectorHexPreview(data);
        }
    }

    // ═══════════════════════════════════════════════════════════════════
    // Text preview
    // ═══════════════════════════════════════════════════════════════════

    private static void DrawArchiveInspectorTextPreview(string text)
    {
        var avail = ImGui.GetContentRegionAvail();
        ImGui.InputTextMultiline("##ArchiveTextPreview", ref text, (uint)text.Length + 1, avail,
            ImGuiInputTextFlags.ReadOnly);
    }

    // ═══════════════════════════════════════════════════════════════════
    // Hex preview
    // ═══════════════════════════════════════════════════════════════════

    private static void DrawArchiveInspectorHexPreview(byte[] data)
    {
        // For very large files, use a clipper to render only visible rows.
        int totalRows = (data.Length + HexBytesPerRow - 1) / HexBytesPerRow;

        float lineHeight = ImGui.GetTextLineHeightWithSpacing();
        var avail = ImGui.GetContentRegionAvail();

        if (ImGui.BeginChild("HexView", avail, ImGuiChildFlags.None))
        {
            ImGui.PushStyleVar(ImGuiStyleVar.ItemSpacing, new Vector2(0, 0));

            var sb = new StringBuilder(128);

            unsafe
            {
                var clipper = new ImGuiListClipper();
                ImGuiNative.ImGuiListClipper_Begin(&clipper, totalRows, lineHeight);

                while (ImGuiNative.ImGuiListClipper_Step(&clipper) != 0)
                {
                    for (int row = clipper.DisplayStart; row < clipper.DisplayEnd; row++)
                    {
                        int offset = row * HexBytesPerRow;
                        sb.Clear();

                        // Address column.
                        sb.Append($"{offset:X8}  ");

                        // Hex bytes.
                        int bytesInRow = Math.Min(HexBytesPerRow, data.Length - offset);
                        for (int col = 0; col < HexBytesPerRow; col++)
                        {
                            if (col < bytesInRow)
                                sb.Append($"{data[offset + col]:X2} ");
                            else
                                sb.Append("   ");

                            if (col == 7)
                                sb.Append(' ');
                        }

                        sb.Append(" |");

                        // ASCII column.
                        for (int col = 0; col < bytesInRow; col++)
                        {
                            byte b = data[offset + col];
                            sb.Append(b is >= 0x20 and < 0x7F ? (char)b : '.');
                        }

                        sb.Append('|');

                        ImGui.TextUnformatted(sb.ToString());
                    }
                }

                ImGuiNative.ImGuiListClipper_End(&clipper);
            }

            ImGui.PopStyleVar();
        }
        ImGui.EndChild();
    }

    // ═══════════════════════════════════════════════════════════════════
    // Utilities
    // ═══════════════════════════════════════════════════════════════════

    private static string FormatBytes(long bytes)
    {
        const double KB = 1024.0;
        const double MB = KB * 1024.0;
        const double GB = MB * 1024.0;

        return bytes switch
        {
            >= (long)GB => $"{bytes / GB:F2} GB",
            >= (long)MB => $"{bytes / MB:F2} MB",
            >= (long)KB => $"{bytes / KB:F2} KB",
            _ => $"{bytes} B",
        };
    }

    /// <summary>
    /// Quick heuristic: if the first N bytes contain no non-whitespace control characters, treat it as text.
    /// </summary>
    private static bool LooksLikeText(byte[] data)
    {
        int checkLength = Math.Min(data.Length, 8192);
        for (int i = 0; i < checkLength; i++)
        {
            byte b = data[i];
            if (b < 0x08)
                return false;
            if (b > 0x0D && b < 0x20 && b != 0x1B) // Allow TAB, CR, LF, and ESC.
                return false;
        }
        return true;
    }
}
