using System;
using System.IO;
using XREngine.Core.Reflection.Attributes;

namespace XREngine.Editor.IMGUI;

internal static class ImGuiExternalPathDrop
{
    private sealed record HoveredTarget(InspectorPathKind PathKind, Action<string> ApplyPath);

    private static HoveredTarget? _hoveredTarget;

    public static void BeginFrame()
        => _hoveredTarget = null;

    public static void RegisterHoveredTarget(InspectorPathKind pathKind, Action<string> applyPath)
    {
        ArgumentNullException.ThrowIfNull(applyPath);
        _hoveredTarget = new HoveredTarget(pathKind, applyPath);
    }

    public static bool TryHandleExternalDrop(string[] paths)
    {
        if (_hoveredTarget is null || paths is null || paths.Length == 0)
            return false;

        string? selectedPath = _hoveredTarget.PathKind switch
        {
            InspectorPathKind.File => Array.Find(paths, File.Exists),
            InspectorPathKind.Folder => Array.Find(paths, Directory.Exists),
            _ => null,
        };

        if (string.IsNullOrWhiteSpace(selectedPath))
            return false;

        _hoveredTarget.ApplyPath(Path.GetFullPath(selectedPath));
        _hoveredTarget = null;
        return true;
    }
}