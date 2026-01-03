using System;
using System.Numerics;
using System.Runtime.InteropServices;

namespace XREngine.Editor;

/// <summary>
/// P/Invoke helpers for Dear ImGui drag/drop APIs not exposed by ImGui.NET.
/// </summary>
internal static class ImGuiDragDropNative
{
    private const string CImGuiLib = "cimgui";

    [StructLayout(LayoutKind.Sequential)]
    private struct ImRect
    {
        public Vector2 Min;
        public Vector2 Max;

        public ImRect(Vector2 min, Vector2 max)
        {
            Min = min;
            Max = max;
        }
    }

    [DllImport(CImGuiLib, CallingConvention = CallingConvention.Cdecl)]
    private static extern byte igBeginDragDropTargetCustom(ref ImRect bb, uint id);

    public static bool BeginDragDropTargetCustom(Vector2 min, Vector2 max, uint id)
    {
        var rect = new ImRect(min, max);
        return igBeginDragDropTargetCustom(ref rect, id) != 0;
    }
}
