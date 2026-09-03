using System;
using Silk.NET.Vulkan;
using XREngine.Rendering.Models.Materials;
using VkBufferHandle = Silk.NET.Vulkan.Buffer;

namespace XREngine.Rendering.Vulkan;

/// <summary>
/// Stateless serializer reading directly from VulkanResidentDrawTemplate and
/// VulkanResidentDrawTemplateNativeState to emit pure Vulkan commands into the command buffer
/// without entering VkMeshRenderer.RecordDraw, RecordDrawNoLock, or acquiring _recordDrawSync.
/// </summary>
internal static unsafe class VulkanResidentMeshEncoder
{
    internal static bool TryRecordDraw(
        Vk api,
        VulkanCommandRuntime commandRuntime,
        VulkanLaneRecordingContext? laneContext,
        CommandBuffer commandBuffer,
        in PendingMeshDraw draw,
        int passIndex,
        in FrameOpContext context,
        int drawUniformSlot,
        int frameDataImageIndex)
    {
        VkMeshRenderer renderer = draw.Renderer;
        if (renderer is null)
            return false;

        XRMaterial? material = draw.MaterialOverride ?? renderer.MeshRenderer.Material;
        if (material is null)
            return false;

        if (!renderer.TryGetResidentDrawTemplate(
                draw,
                passIndex,
                material,
                context,
                out _,
                out _,
                out _,
                out VulkanResidentDrawTemplate? residentTemplate) ||
            residentTemplate is null)
        {
            return false;
        }

        VulkanResidentDrawTemplateNativeState native = residentTemplate.NativeState;
        if (native.PrimitiveCount == 0)
            return false;

        // 1. Push Constants
        if (native.PipelineLayout.Handle != 0)
        {
            VkMeshRenderer.MeshDrawPushConstants constants = new(
                unchecked((uint)(material.GetHashCode() & int.MaxValue)),
                draw.Instances,
                (uint)draw.BillboardMode,
                (draw.IsStereoPass ? 1u : 0u) | (draw.UseUnjitteredProjection ? 2u : 0u));

            commandRuntime.PushConstantsTracked(
                commandBuffer,
                native.PipelineLayout,
                VulkanMeshRenderingConventions.GetCommonPushConstantStageFlags(renderer.BackendContext.DeviceContext),
                0,
                constants);
        }

        // 2. Descriptors
        if (!renderer.BindDescriptorsIfAvailable(
                commandBuffer,
                material,
                draw,
                drawUniformSlot,
                frameDataImageIndex,
                passIndex))
        {
            return false;
        }

        // 3. Vertex Buffers
        int vbCount = native.VertexBufferCount;
        if (vbCount > 0)
        {
            ulong signature = native.VertexBindingSignature;
            bool shouldBind = laneContext is null || laneContext.ShouldBindVertexBuffer(signature);
            if (shouldBind)
            {
                Span<VkBufferHandle> bufs = stackalloc VkBufferHandle[vbCount];
                Span<ulong> offsets = stackalloc ulong[vbCount];
                for (int b = 0; b < vbCount; b++)
                {
                    bufs[b] = native.GetVertexBuffer(b);
                    offsets[b] = 0UL;
                }
                fixed (VkBufferHandle* bufPtr = bufs)
                fixed (ulong* offPtr = offsets)
                {
                    api.CmdBindVertexBuffers(commandBuffer, 0, (uint)vbCount, bufPtr, offPtr);
                }
            }
        }

        // 4. Primitives & Draws
        for (int i = 0; i < native.PrimitiveCount; i++)
        {
            VulkanPreparedMeshPrimitive primitive = native.GetPrimitive(i);
            if (primitive.Pipeline.Handle == 0)
                continue;

            if (laneContext is null || laneContext.ShouldBindPipeline(PipelineBindPoint.Graphics, primitive.Pipeline))
            {
                api.CmdBindPipeline(commandBuffer, PipelineBindPoint.Graphics, primitive.Pipeline);
            }

            if (primitive.Indexed && primitive.IndexBuffer.Handle != 0 && primitive.ElementCount > 0)
            {
                if (laneContext is null || laneContext.ShouldBindIndexBuffer(primitive.IndexBuffer, 0, primitive.IndexType))
                {
                    api.CmdBindIndexBuffer(commandBuffer, primitive.IndexBuffer, 0, primitive.IndexType);
                }
                api.CmdDrawIndexed(commandBuffer, primitive.ElementCount, draw.Instances, 0, 0, 0);
            }
            else if (!primitive.Indexed && primitive.ElementCount > 0)
            {
                api.CmdDraw(commandBuffer, primitive.ElementCount, draw.Instances, 0, 0);
            }
        }

        return true;
    }
}
