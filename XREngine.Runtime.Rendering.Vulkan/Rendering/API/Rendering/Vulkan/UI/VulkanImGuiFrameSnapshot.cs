using ImGuiNET;
using System;
using System.Collections.Generic;
using System.Numerics;

namespace XREngine.Rendering.Vulkan;

internal unsafe sealed class VulkanImGuiFrameSnapshot
{
    public Vector2 DisplayPos { get; private set; }
    public Vector2 DisplaySize { get; private set; }
    public Vector2 FramebufferScale { get; private set; }
    public uint FramebufferWidth { get; private set; }
    public uint FramebufferHeight { get; private set; }
    public int TotalVertexCount { get; private set; }
    public int TotalIndexCount { get; private set; }
    public int CommandListCount { get; private set; }
    public List<VulkanImGuiCommandListSnapshot> CommandLists { get; } = [];

    public void Capture(ImDrawDataPtr drawData)
    {
        ImDrawData* native = drawData.NativePtr;
        ImDrawList** lists = (ImDrawList**)native->CmdLists.Data;

        CommandLists.EnsureCapacity(drawData.CmdListsCount);
        int totalVertices = 0;
        int totalIndices = 0;

        for (int listIndex = 0; listIndex < drawData.CmdListsCount; listIndex++)
        {
            ImDrawListPtr cmdList = new(lists[listIndex]);
            if (listIndex == CommandLists.Count)
                CommandLists.Add(new VulkanImGuiCommandListSnapshot());

            VulkanImGuiCommandListSnapshot snapshot = CommandLists[listIndex];
            snapshot.Capture(cmdList);
            totalVertices += snapshot.VertexCount;
            totalIndices += snapshot.IndexCount;
        }

        DisplayPos = drawData.DisplayPos;
        DisplaySize = drawData.DisplaySize;
        FramebufferScale = drawData.FramebufferScale;
        FramebufferWidth = ComputeFramebufferExtent(drawData.DisplaySize.X, drawData.FramebufferScale.X);
        FramebufferHeight = ComputeFramebufferExtent(drawData.DisplaySize.Y, drawData.FramebufferScale.Y);
        TotalVertexCount = totalVertices;
        TotalIndexCount = totalIndices;
        CommandListCount = drawData.CmdListsCount;
    }

    private static uint ComputeFramebufferExtent(float displaySize, float framebufferScale)
    {
        if (!float.IsFinite(displaySize) || !float.IsFinite(framebufferScale))
            return 0;

        float value = displaySize * framebufferScale;
        if (value <= 0f)
            return 0;

        return value >= uint.MaxValue ? uint.MaxValue : (uint)MathF.Round(value);
    }
}
