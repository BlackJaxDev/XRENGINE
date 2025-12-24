using System;
using System.Numerics;
using System.Runtime.InteropServices;
using ImGuiNET;

namespace XREngine.Editor;

/// <summary>
/// P/Invoke wrapper for ImGui DockBuilder functions that are not exposed in ImGuiNET.
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

    /// <summary>
    /// Docks a window into a specific dock node.
    /// </summary>
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

    /// <summary>
    /// Gets a dock node by ID.
    /// </summary>
    public static IntPtr GetNode(uint nodeId)
    {
        return igDockBuilderGetNode(nodeId);
    }

    /// <summary>
    /// Checks if a dock node exists.
    /// </summary>
    public static bool NodeExists(uint nodeId)
    {
        return igDockBuilderGetNode(nodeId) != IntPtr.Zero;
    }

    /// <summary>
    /// Removes a dock node and all its child nodes.
    /// </summary>
    public static void RemoveNode(uint nodeId)
    {
        igDockBuilderRemoveNode(nodeId);
    }

    /// <summary>
    /// Adds a new dock node.
    /// </summary>
    public static uint AddNode(uint nodeId, ImGuiDockNodeFlags flags)
    {
        return igDockBuilderAddNode(nodeId, flags);
    }

    /// <summary>
    /// Sets the size of a dock node.
    /// </summary>
    public static void SetNodeSize(uint nodeId, Vector2 size)
    {
        igDockBuilderSetNodeSize(nodeId, size);
    }

    /// <summary>
    /// Sets the position of a dock node.
    /// </summary>
    public static void SetNodePos(uint nodeId, Vector2 pos)
    {
        igDockBuilderSetNodePos(nodeId, pos);
    }

    /// <summary>
    /// Splits a dock node into two nodes.
    /// </summary>
    /// <param name="nodeId">The node to split.</param>
    /// <param name="splitDir">Direction of the split.</param>
    /// <param name="sizeRatio">Size ratio for the node in the split direction (0.0-1.0).</param>
    /// <param name="outIdAtDir">Output: ID of the node in the split direction.</param>
    /// <param name="outIdAtOpposite">Output: ID of the node opposite to the split direction.</param>
    /// <returns>The ID of the new node.</returns>
    public static uint SplitNode(uint nodeId, ImGuiDir splitDir, float sizeRatio, out uint outIdAtDir, out uint outIdAtOpposite)
    {
        uint atDir = 0;
        uint atOpposite = 0;
        uint result = igDockBuilderSplitNode(nodeId, splitDir, sizeRatio, &atDir, &atOpposite);
        outIdAtDir = atDir;
        outIdAtOpposite = atOpposite;
        return result;
    }

    /// <summary>
    /// Finalizes the dock builder layout.
    /// </summary>
    public static void Finish(uint nodeId)
    {
        igDockBuilderFinish(nodeId);
    }
}
