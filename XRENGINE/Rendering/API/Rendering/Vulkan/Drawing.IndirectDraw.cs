using System;
using System.Diagnostics;
using Silk.NET.Vulkan;
using XREngine.Data;
using XREngine.Data.Rendering;

namespace XREngine.Rendering.Vulkan
{
    public unsafe partial class VulkanRenderer
    {
        // =========== Indirect + Pipeline Abstraction stubs for Vulkan ===========

        public override void BindVAOForRenderer(XRMeshRenderer.BaseVersion? version)
        {
            if (version is null)
            {
                _boundMeshRendererForIndirect = null;
                _boundIndexType = IndexType.Uint32;
                _boundIndexCount = 0;
                return;
            }

            var vkMesh = GenericToAPI<VkMeshRenderer>(version);
            if (vkMesh is null)
            {
                _boundMeshRendererForIndirect = null;
                _boundIndexType = IndexType.Uint32;
                _boundIndexCount = 0;
                return;
            }

            vkMesh.Generate();
            _boundMeshRendererForIndirect = vkMesh;

            if (vkMesh.TryGetPrimaryIndexBinding(out _, out IndexType indexType, out uint indexCount))
            {
                _boundIndexType = indexType;
                _boundIndexCount = indexCount;
            }
            else
            {
                _boundIndexType = IndexType.Uint32;
                _boundIndexCount = 0;
            }
        }

        public override bool ValidateIndexedVAO(XRMeshRenderer.BaseVersion? version)
        {
            return TryGetIndexBufferInfo(version, out _, out _);
        }

        public override bool TryGetIndexBufferInfo(XRMeshRenderer.BaseVersion? version, out IndexSize indexElementSize, out uint indexCount)
        {
            indexElementSize = IndexSize.FourBytes;
            indexCount = 0;

            var vkMesh = version is not null ? GenericToAPI<VkMeshRenderer>(version) : _boundMeshRendererForIndirect;
            if (vkMesh is null)
                return false;

            bool updateBoundState = version is null || _boundMeshRendererForIndirect == vkMesh;
            vkMesh.Generate();
            if (!vkMesh.TryGetPrimaryIndexBufferInfo(out indexElementSize, out indexCount))
            {
                if (updateBoundState)
                {
                    _boundIndexType = IndexType.Uint32;
                    _boundIndexCount = 0;
                }

                return false;
            }

            if (updateBoundState)
            {
                _boundIndexType = ToVkIndexType(indexElementSize);
                _boundIndexCount = indexCount;
            }

            return true;
        }

        public override bool TrySyncMeshRendererIndexBuffer(XRMeshRenderer meshRenderer, XRDataBuffer indexBuffer, IndexSize elementSize)
        {
            if (meshRenderer is null || indexBuffer is null)
                return false;

            var version = meshRenderer.GetDefaultVersion();
            var vkMesh = GenericToAPI<VkMeshRenderer>(version);
            if (vkMesh is null)
                return false;

            var vkIndexBuffer = GenericToAPI<VkDataBuffer>(indexBuffer);
            if (vkIndexBuffer is null)
                return false;

            vkMesh.Generate();
            vkIndexBuffer.Generate();
            vkMesh.SetTriangleIndexBuffer(vkIndexBuffer, elementSize);

            if (_boundMeshRendererForIndirect == vkMesh &&
                vkMesh.TryGetPrimaryIndexBinding(out _, out IndexType boundType, out uint boundCount))
            {
                _boundIndexType = boundType;
                _boundIndexCount = boundCount;
            }

            MarkCommandBuffersDirty();
            return true;
        }

        // =========== Indirect Draw State ===========
        private VkDataBuffer? _boundIndirectBuffer;
        private VkDataBuffer? _boundParameterBuffer;
        private VkMeshRenderer? _boundMeshRendererForIndirect;
        private IndexType _boundIndexType = IndexType.Uint32;
        private uint _boundIndexCount;

        public override void BindDrawIndirectBuffer(XRDataBuffer buffer)
        {
            var vkBuffer = GenericToAPI<VkDataBuffer>(buffer);
            _boundIndirectBuffer = vkBuffer;
            MarkCommandBuffersDirty();
        }

        public override void UnbindDrawIndirectBuffer()
        {
            _boundIndirectBuffer = null;
            MarkCommandBuffersDirty();
        }

        public override void BindParameterBuffer(XRDataBuffer buffer)
        {
            var vkBuffer = GenericToAPI<VkDataBuffer>(buffer);
            _boundParameterBuffer = vkBuffer;
            MarkCommandBuffersDirty();
        }

        public override void UnbindParameterBuffer()
        {
            _boundParameterBuffer = null;
            MarkCommandBuffersDirty();
        }

        public override void MultiDrawElementsIndirect(uint drawCount, uint stride)
        {
            MultiDrawElementsIndirectWithOffset(drawCount, stride, 0);
        }

        public override void MultiDrawElementsIndirectWithOffset(uint drawCount, uint stride, nuint byteOffset)
        {
            if (_boundIndirectBuffer?.BufferHandle is null)
            {
                Debug.VulkanWarning("MultiDrawElementsIndirectWithOffset: No indirect buffer bound.");
                return;
            }

            if (_boundMeshRendererForIndirect is null || _boundIndexCount == 0)
            {
                Debug.VulkanWarning("MultiDrawElementsIndirectWithOffset: No indexed mesh renderer bound.");
                return;
            }

            int passIndex = Engine.Rendering.State.CurrentRenderGraphPassIndex;
            EnqueueFrameOp(new IndirectDrawOp(
                EnsureValidPassIndex(passIndex, "IndirectDraw"),
                _boundIndirectBuffer,
                _boundParameterBuffer,
                drawCount,
                stride,
                byteOffset,
                UseCount: false,
                CaptureFrameOpContext()));

            Engine.Rendering.Stats.IncrementMultiDrawCalls();
            Engine.Rendering.Stats.IncrementDrawCalls((int)drawCount);
        }

        public override void MultiDrawElementsIndirectCount(uint maxDrawCount, uint stride, nuint byteOffset)
        {
            if (!_supportsDrawIndirectCount)
            {
                Debug.VulkanWarning("MultiDrawElementsIndirectCount called but VK_KHR_draw_indirect_count is not supported. Falling back to regular indirect draw.");
                MultiDrawElementsIndirectWithOffset(maxDrawCount, stride, byteOffset);
                return;
            }

            if (_boundIndirectBuffer?.BufferHandle is null)
            {
                Debug.VulkanWarning("MultiDrawElementsIndirectCount: No indirect buffer bound.");
                return;
            }

            if (_boundMeshRendererForIndirect is null || _boundIndexCount == 0)
            {
                Debug.VulkanWarning("MultiDrawElementsIndirectCount: No indexed mesh renderer bound.");
                return;
            }

            if (_boundParameterBuffer?.BufferHandle is null)
            {
                Debug.VulkanWarning("MultiDrawElementsIndirectCount: No parameter (count) buffer bound. Falling back to regular indirect draw.");
                MultiDrawElementsIndirectWithOffset(maxDrawCount, stride, byteOffset);
                return;
            }

            int passIndex = Engine.Rendering.State.CurrentRenderGraphPassIndex;
            EnqueueFrameOp(new IndirectDrawOp(
                EnsureValidPassIndex(passIndex, "IndirectCountDraw"),
                _boundIndirectBuffer,
                _boundParameterBuffer,
                maxDrawCount,
                stride,
                byteOffset,
                UseCount: true,
                CaptureFrameOpContext()));

            Engine.Rendering.Stats.IncrementMultiDrawCalls();
            // Actual draw count is determined by GPU; we track max as approximation
            Engine.Rendering.Stats.IncrementDrawCalls((int)maxDrawCount);
        }

        private static IndexType ToVkIndexType(IndexSize size)
            => size switch
            {
                IndexSize.Byte => IndexType.Uint8Ext,
                IndexSize.TwoBytes => IndexType.Uint16,
                IndexSize.FourBytes => IndexType.Uint32,
                _ => IndexType.Uint32
            };

        public override bool SupportsIndirectCountDraw()
        {
            return _supportsDrawIndirectCount;
        }

        public override void ConfigureVAOAttributesForProgram(XRRenderProgram program, XRMeshRenderer.BaseVersion? version)
        {
            // Vulkan does not use VAOs; pipeline vertex input state handles this.
            // No-op for now.
        }
    }
}
