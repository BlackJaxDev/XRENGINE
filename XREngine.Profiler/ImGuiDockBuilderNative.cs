using System;
using System.Numerics;
using System.Runtime.InteropServices;
using ImGuiNET;

namespace XREngine.Profiler;

/// <summary>
/// P/Invoke wrapper for ImGui DockBuilder functions that are not exposed in ImGuiNET.
/// Copied from XREngine.Editor/IMGUI/ImGuiDockBuilderNative.cs with namespace change.
/// </summary>
internal static unsafe class ImGuiDockBuilderNative
{
    private const string CImGuiLib = "cimgui";

    [DllImport(CImGuiLib, CallingConvention = CallingConvention.Cdecl)]
    private static extern void igDockBuilderDockWindow(byte* window_name, uint node_id);

    [DllImport(CImGuiLib, CallingConvention = CallingConvention.Cdecl)]
    private static extern IntPtr igDockBuilderGetNode(uint node_id);

    [DllImport(CImGuiLib, CallingConvention = CallingConvention.Cdecl)]
    private static extern void igDockBuilderRemoveNode(uint node_id);

    [DllImport(CImGuiLib, CallingConvention = CallingConvention.Cdecl)]
    private static extern uint igDockBuilderAddNode(uint node_id, ImGuiDockNodeFlags flags);

    [DllImport(CImGuiLib, CallingConvention = CallingConvention.Cdecl)]
    private static extern void igDockBuilderSetNodeSize(uint node_id, Vector2 size);

    [DllImport(CImGuiLib, CallingConvention = CallingConvention.Cdecl)]
    private static extern void igDockBuilderSetNodePos(uint node_id, Vector2 pos);

    [DllImport(CImGuiLib, CallingConvention = CallingConvention.Cdecl)]
    private static extern uint igDockBuilderSplitNode(uint node_id, ImGuiDir split_dir, float size_ratio_for_node_at_dir, uint* out_id_at_dir, uint* out_id_at_opposite_dir);

    [DllImport(CImGuiLib, CallingConvention = CallingConvention.Cdecl)]
    private static extern void igDockBuilderFinish(uint node_id);

    public static void DockWindow(string windowName, uint nodeId)
    {
        if (string.IsNullOrEmpty(windowName))
            return;

        int byteCount = System.Text.Encoding.UTF8.GetByteCount(windowName) + 1;
        byte* nameBytes = stackalloc byte[byteCount];
        fixed (char* namePtr = windowName)
        {
            System.Text.Encoding.UTF8.GetBytes(namePtr, windowName.Length, nameBytes, byteCount - 1);
        }
        nameBytes[byteCount - 1] = 0;

        igDockBuilderDockWindow(nameBytes, nodeId);
    }

    public static IntPtr GetNode(uint nodeId)
    {
        return igDockBuilderGetNode(nodeId);
    }

    public static bool NodeExists(uint nodeId)
    {
        return igDockBuilderGetNode(nodeId) != IntPtr.Zero;
    }

    public static void RemoveNode(uint nodeId)
    {
        igDockBuilderRemoveNode(nodeId);
    }

    public static uint AddNode(uint nodeId, ImGuiDockNodeFlags flags)
    {
        return igDockBuilderAddNode(nodeId, flags);
    }

    public static void SetNodeSize(uint nodeId, Vector2 size)
    {
        igDockBuilderSetNodeSize(nodeId, size);
    }

    public static void SetNodePos(uint nodeId, Vector2 pos)
    {
        igDockBuilderSetNodePos(nodeId, pos);
    }

    public static uint SplitNode(uint nodeId, ImGuiDir splitDir, float sizeRatio, out uint outIdAtDir, out uint outIdAtOpposite)
    {
        uint atDir = 0;
        uint atOpposite = 0;
        uint result = igDockBuilderSplitNode(nodeId, splitDir, sizeRatio, &atDir, &atOpposite);
        outIdAtDir = atDir;
        outIdAtOpposite = atOpposite;
        return result;
    }

    public static void Finish(uint nodeId)
    {
        igDockBuilderFinish(nodeId);
    }
}
