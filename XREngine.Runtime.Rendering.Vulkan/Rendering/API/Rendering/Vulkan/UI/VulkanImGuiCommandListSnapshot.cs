using ImGuiNET;
using System;

namespace XREngine.Rendering.Vulkan;

internal unsafe sealed class VulkanImGuiCommandListSnapshot
{
    private ImDrawVert[] _vertices = [];
    private ushort[] _indices = [];
    private VulkanImGuiCommandSnapshot[] _commands = [];

    public ImDrawVert[] Vertices => _vertices;
    public ushort[] Indices => _indices;
    public VulkanImGuiCommandSnapshot[] Commands => _commands;
    public int VertexCount { get; private set; }
    public int IndexCount { get; private set; }
    public int CommandCount { get; private set; }

    public void Capture(ImDrawListPtr cmdList)
    {
        VertexCount = cmdList.VtxBuffer.Size;
        IndexCount = cmdList.IdxBuffer.Size;
        CommandCount = cmdList.CmdBuffer.Size;

        EnsureCapacity(ref _vertices, VertexCount);
        EnsureCapacity(ref _indices, IndexCount);
        EnsureCapacity(ref _commands, CommandCount);

        if (VertexCount > 0)
        {
            fixed (ImDrawVert* vertexDst = _vertices)
            {
                nuint bytes = (nuint)(VertexCount * sizeof(ImDrawVert));
                System.Buffer.MemoryCopy(
                    cmdList.VtxBuffer.Data.ToPointer(),
                    vertexDst,
                    (long)bytes,
                    (long)bytes);
            }
        }

        if (IndexCount > 0)
        {
            fixed (ushort* indexDst = _indices)
            {
                nuint bytes = (nuint)(IndexCount * sizeof(ushort));
                System.Buffer.MemoryCopy(
                    cmdList.IdxBuffer.Data.ToPointer(),
                    indexDst,
                    (long)bytes,
                    (long)bytes);
            }
        }

        for (int cmdIndex = 0; cmdIndex < CommandCount; cmdIndex++)
        {
            ImDrawCmdPtr drawCmd = cmdList.CmdBuffer[cmdIndex];
            _commands[cmdIndex] = new VulkanImGuiCommandSnapshot
            {
                ClipRect = drawCmd.ClipRect,
                TextureId = drawCmd.TextureId,
                ElemCount = drawCmd.ElemCount,
                IdxOffset = drawCmd.IdxOffset,
                VtxOffset = drawCmd.VtxOffset,
                HasUserCallback = drawCmd.UserCallback != IntPtr.Zero
            };
        }
    }

    private static void EnsureCapacity<T>(ref T[] buffer, int requiredCount)
    {
        if (buffer.Length >= requiredCount)
            return;

        int doubledCapacity = buffer.Length <= Array.MaxLength / 2
            ? buffer.Length * 2
            : Array.MaxLength;
        int newCapacity = Math.Max(requiredCount, Math.Max(4, doubledCapacity));
        buffer = new T[newCapacity];
    }
}
