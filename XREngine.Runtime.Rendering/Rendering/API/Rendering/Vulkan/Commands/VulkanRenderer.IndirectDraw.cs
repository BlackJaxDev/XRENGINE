using System;
using System.Diagnostics;
using System.Numerics;
using Silk.NET.Vulkan;
using XREngine.Data;
using XREngine.Data.Rendering;
using XREngine.Rendering.Models.Materials;

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
            bool indexBindingChanged = vkMesh.SetTriangleIndexBuffer(vkIndexBuffer, elementSize);

            if (_boundMeshRendererForIndirect == vkMesh &&
                vkMesh.TryGetPrimaryIndexBinding(out _, out IndexType boundType, out uint boundCount))
            {
                _boundIndexType = boundType;
                _boundIndexCount = boundCount;
            }

            if (indexBindingChanged)
                MarkCommandBuffersDirtyForLegacyMeshState();
            return true;
        }

        // =========== Indirect Draw State ===========
        private VkDataBuffer? _boundIndirectBuffer;
        private VkDataBuffer? _boundParameterBuffer;
        private VkMeshRenderer? _boundMeshRendererForIndirect;
        private IndexType _boundIndexType = IndexType.Uint32;
        private uint _boundIndexCount;
        private VulkanIndirectDrawState? _pendingIndirectDrawState;

        private readonly record struct VulkanIndirectDrawState(
            XRRenderProgram Program,
            XRMaterial Material,
            Matrix4x4 ModelMatrix);

        internal IDisposable PushIndirectDrawState(XRRenderProgram program, XRMaterial material, Matrix4x4 modelMatrix)
        {
            VulkanIndirectDrawState? previous = _pendingIndirectDrawState;
            _pendingIndirectDrawState = new VulkanIndirectDrawState(program, material, modelMatrix);
            return new IndirectDrawStateScope(this, previous);
        }

        private sealed class IndirectDrawStateScope(VulkanRenderer renderer, VulkanIndirectDrawState? previous) : IDisposable
        {
            private bool _disposed;

            public void Dispose()
            {
                if (_disposed)
                    return;

                renderer._pendingIndirectDrawState = previous;
                _disposed = true;
            }
        }

        private bool TryCaptureIndirectDrawPayload(
            string contextName,
            out VkMeshRenderer meshRenderer,
            out PendingMeshDraw draw)
        {
            meshRenderer = null!;
            draw = default;

            if (_boundMeshRendererForIndirect is null || _boundIndexCount == 0)
            {
                Debug.VulkanWarning("{0}: No indexed mesh renderer bound.", contextName);
                return false;
            }

            if (_pendingIndirectDrawState is not { } state)
            {
                Debug.VulkanWarningEvery(
                    $"Vulkan.IndirectDrawStateMissing.{contextName}",
                    TimeSpan.FromSeconds(1),
                    "{0}: No Vulkan indirect draw state was pushed before indirect submission.",
                    contextName);
                return false;
            }

            var preparedProgram = GetOrCreateAPIRenderObject(state.Program) as VkRenderProgram;
            if (preparedProgram is null)
            {
                Debug.VulkanWarning("{0}: Vulkan program wrapper is unavailable for indirect draw program '{1}'.", contextName, state.Program.Name ?? "<unnamed>");
                return false;
            }

            if (!preparedProgram.IsLinked && !preparedProgram.Link())
            {
                Debug.VulkanWarning("{0}: Vulkan indirect draw program '{1}' is not linked.", contextName, state.Program.Name ?? "<unnamed>");
                return false;
            }

            ComputeDispatchSnapshot bindingSnapshot = preparedProgram.CaptureComputeSnapshot();
            string programIdentity = state.Program.Name ?? preparedProgram.GetHashCode().ToString();
            if (!_boundMeshRendererForIndirect.TryCreatePreparedIndirectDrawSnapshot(
                    state.Material,
                    preparedProgram,
                    programIdentity,
                    bindingSnapshot,
                    state.ModelMatrix,
                    GetCurrentDrawFrameBuffer(),
                    out draw,
                    out string reason))
            {
                Debug.VulkanWarningEvery(
                    $"Vulkan.IndirectDrawSnapshotFailed.{contextName}.{reason}",
                    TimeSpan.FromSeconds(2),
                    "{0}: Failed to capture indirect draw state for program '{1}' material '{2}': {3}. {4}",
                    contextName,
                    state.Program.Name ?? "<unnamed program>",
                    state.Material.Name ?? "<unnamed material>",
                    reason,
                    _boundMeshRendererForIndirect.LastPrepareDetail);
                return false;
            }

            meshRenderer = _boundMeshRendererForIndirect;
            return true;
        }

        public override void BindDrawIndirectBuffer(XRDataBuffer buffer)
        {
            var vkBuffer = GenericToAPI<VkDataBuffer>(buffer);
            _boundIndirectBuffer = vkBuffer;
        }

        public override void UnbindDrawIndirectBuffer()
        {
            _boundIndirectBuffer = null;
        }

        public override void BindParameterBuffer(XRDataBuffer buffer)
        {
            var vkBuffer = GenericToAPI<VkDataBuffer>(buffer);
            _boundParameterBuffer = vkBuffer;
        }

        public override void UnbindParameterBuffer()
        {
            _boundParameterBuffer = null;
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

            if (!TryCaptureIndirectDrawPayload("MultiDrawElementsIndirectWithOffset", out VkMeshRenderer meshRenderer, out PendingMeshDraw draw))
                return;

            FrameOpContext context = CaptureFrameOpContext();
            int passIndex = RuntimeEngine.Rendering.State.CurrentRenderGraphPassIndex;
            XRFrameBuffer? target = GetCurrentDrawFrameBuffer();
            EnqueueFrameOp(new IndirectDrawOp(
                EnsureValidPassIndex(passIndex, "IndirectDraw", context.PassMetadata),
                target,
                _boundIndirectBuffer,
                _boundParameterBuffer,
                meshRenderer,
                draw,
                drawCount,
                stride,
                byteOffset,
                0,
                UseCount: false,
                CaptureGlobalMaterialTextureDescriptorBindingForNextFrameOp(),
                context));

        }

        public override void MultiDrawElementsIndirectCount(uint maxDrawCount, uint stride, nuint byteOffset, nuint countByteOffset)
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

            if (!TryCaptureIndirectDrawPayload("MultiDrawElementsIndirectCount", out VkMeshRenderer meshRenderer, out PendingMeshDraw draw))
                return;

            FrameOpContext context = CaptureFrameOpContext();
            int passIndex = RuntimeEngine.Rendering.State.CurrentRenderGraphPassIndex;
            XRFrameBuffer? target = GetCurrentDrawFrameBuffer();
            EnqueueFrameOp(new IndirectDrawOp(
                EnsureValidPassIndex(passIndex, "IndirectCountDraw", context.PassMetadata),
                target,
                _boundIndirectBuffer,
                _boundParameterBuffer,
                meshRenderer,
                draw,
                maxDrawCount,
                stride,
                byteOffset,
                countByteOffset,
                UseCount: true,
                CaptureGlobalMaterialTextureDescriptorBindingForNextFrameOp(),
                context));

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
