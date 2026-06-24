using Silk.NET.Vulkan;
using Buffer = Silk.NET.Vulkan.Buffer;

namespace XREngine.Rendering.Vulkan
{
    public unsafe partial class VulkanRenderer
    {
        public class VkTransformFeedback(VulkanRenderer api, XRTransformFeedback data) :
            VkObject<XRTransformFeedback>(api, data),
            IXRTransformFeedbackApi
        {
            public override VkObjectType Type => VkObjectType.TransformFeedback;
            public override bool IsGenerated => IsActive && Renderer.SupportsTransformFeedback;

            public bool IsSupported => Renderer.SupportsTransformFeedback;
            public uint MaxBindingCount => Renderer.TransformFeedbackProperties.MaxTransformFeedbackBuffers;
            public ulong MaxBufferSize => Renderer.TransformFeedbackProperties.MaxTransformFeedbackBufferSize;
            public bool SupportsQueries => Renderer.SupportsTransformFeedbackQueries;
            public bool SupportsByteCountDraw => Renderer.SupportsTransformFeedbackDraw;

            protected override uint CreateObjectInternal()
            {
                EnsureSupported();
                return CacheObject(this);
            }

            protected override void DeleteObjectInternal()
                => RemoveCachedObject(BindingId);

            protected override void LinkData() { }
            protected override void UnlinkData() { }

            XRTransformFeedbackCapabilities IXRTransformFeedbackApi.GetCapabilities()
            {
                if (!Renderer.SupportsTransformFeedback)
                    return XRTransformFeedbackCapabilities.Unsupported(
                        "Vulkan",
                        "Vulkan transform feedback requires VK_EXT_transform_feedback with the transformFeedback feature enabled.");

                EXRTransformFeedbackFeatureFlags features =
                    EXRTransformFeedbackFeatureFlags.Capture |
                    EXRTransformFeedbackFeatureFlags.PauseResume |
                    EXRTransformFeedbackFeatureFlags.ShaderDeclaredVaryings;

                if (Renderer.SupportsTransformFeedbackQueries)
                    features |= EXRTransformFeedbackFeatureFlags.PrimitiveCountQueries;
                if (Renderer.SupportsTransformFeedbackDraw)
                    features |= EXRTransformFeedbackFeatureFlags.DrawIndirectByteCount;
                if (Renderer.SupportsTransformFeedbackGeometryStreams)
                    features |= EXRTransformFeedbackFeatureFlags.GeometryStreams;
                if (MaxBindingCount > 1)
                    features |= EXRTransformFeedbackFeatureFlags.MultipleBuffers;

                return new XRTransformFeedbackCapabilities(
                    "Vulkan",
                    true,
                    features,
                    MaxBindingCount,
                    MaxBufferSize,
                    "Vulkan compiles XRRenderProgram.TransformFeedbacks into SPIR-V XFB decorations. DrawCaptured is unavailable; use DrawIndirectByteCount with a counter buffer when transformFeedbackDraw is supported.");
            }

            XRTransformFeedbackOperationResult IXRTransformFeedbackApi.BindTransformFeedbackBuffer(ulong offset, ulong? size)
                => EnqueueTransformFeedbackOperation(
                    EXRTransformFeedbackOperation.BindBuffer,
                    counterBuffer: null,
                    feedbackBufferOffset: offset,
                    feedbackBufferSize: size);

            XRTransformFeedbackOperationResult IXRTransformFeedbackApi.BeginTransformFeedback(XRDataBuffer? counterBuffer, ulong counterBufferOffset)
                => EnqueueTransformFeedbackOperation(
                    EXRTransformFeedbackOperation.Begin,
                    counterBuffer,
                    counterBufferOffset: counterBufferOffset);

            XRTransformFeedbackOperationResult IXRTransformFeedbackApi.EndTransformFeedback(XRDataBuffer? counterBuffer, ulong counterBufferOffset)
                => EnqueueTransformFeedbackOperation(
                    EXRTransformFeedbackOperation.End,
                    counterBuffer,
                    counterBufferOffset: counterBufferOffset);

            XRTransformFeedbackOperationResult IXRTransformFeedbackApi.PauseTransformFeedback(XRDataBuffer? counterBuffer, ulong counterBufferOffset)
            {
                if (counterBuffer is null)
                    return XRTransformFeedbackOperationResult.Unsupported(
                        EXRTransformFeedbackOperation.Pause,
                        "Vulkan",
                        "Vulkan transform feedback pause/resume requires a counter buffer.");

                return EnqueueTransformFeedbackOperation(
                    EXRTransformFeedbackOperation.Pause,
                    counterBuffer,
                    counterBufferOffset: counterBufferOffset);
            }

            XRTransformFeedbackOperationResult IXRTransformFeedbackApi.ResumeTransformFeedback(XRDataBuffer? counterBuffer, ulong counterBufferOffset)
            {
                if (counterBuffer is null)
                    return XRTransformFeedbackOperationResult.Unsupported(
                        EXRTransformFeedbackOperation.Resume,
                        "Vulkan",
                        "Vulkan transform feedback pause/resume requires a counter buffer.");

                return EnqueueTransformFeedbackOperation(
                    EXRTransformFeedbackOperation.Resume,
                    counterBuffer,
                    counterBufferOffset: counterBufferOffset);
            }

            XRTransformFeedbackOperationResult IXRTransformFeedbackApi.DrawCapturedTransformFeedback(uint instanceCount, uint stream)
                => XRTransformFeedbackOperationResult.Unsupported(
                    EXRTransformFeedbackOperation.DrawCaptured,
                    "Vulkan",
                    "Vulkan does not have OpenGL-style DrawTransformFeedback for transform feedback objects; use DrawIndirectByteCount with a counter buffer when transformFeedbackDraw is supported.");

            XRTransformFeedbackOperationResult IXRTransformFeedbackApi.DrawTransformFeedbackIndirectByteCount(
                XRDataBuffer? counterBuffer,
                ulong counterBufferOffset,
                uint counterOffset,
                uint vertexStride,
                uint instanceCount,
                uint firstInstance)
            {
                if (!Renderer.SupportsTransformFeedbackDraw)
                    return XRTransformFeedbackOperationResult.Unsupported(
                        EXRTransformFeedbackOperation.DrawIndirectByteCount,
                        "Vulkan",
                        "This Vulkan device does not support the transformFeedbackDraw property.");

                if (counterBuffer is null)
                    return XRTransformFeedbackOperationResult.Unsupported(
                        EXRTransformFeedbackOperation.DrawIndirectByteCount,
                        "Vulkan",
                        "Vulkan byte-count draws require a transform feedback counter buffer.");

                if (vertexStride == 0)
                    return XRTransformFeedbackOperationResult.Failed(
                        EXRTransformFeedbackOperation.DrawIndirectByteCount,
                        "Vulkan",
                        "Vertex stride must be non-zero.");

                return EnqueueTransformFeedbackOperation(
                    EXRTransformFeedbackOperation.DrawIndirectByteCount,
                    counterBuffer,
                    counterBufferOffset: counterBufferOffset,
                    counterOffset: counterOffset,
                    vertexStride: vertexStride,
                    instanceCount: instanceCount,
                    firstInstance: firstInstance);
            }

            private XRTransformFeedbackOperationResult EnqueueTransformFeedbackOperation(
                EXRTransformFeedbackOperation operation,
                XRDataBuffer? counterBuffer,
                ulong feedbackBufferOffset = 0,
                ulong? feedbackBufferSize = null,
                ulong counterBufferOffset = 0,
                uint counterOffset = 0,
                uint vertexStride = 0,
                uint instanceCount = 1,
                uint firstInstance = 0)
            {
                if (!Renderer.SupportsTransformFeedback || Renderer._extTransformFeedback is null)
                    return XRTransformFeedbackOperationResult.Unsupported(
                        operation,
                        "Vulkan",
                        "Vulkan transform feedback requires VK_EXT_transform_feedback with the transformFeedback feature enabled.");

                if (RuntimeEngine.Rendering.State.CurrentRenderingPipeline is null)
                    return XRTransformFeedbackOperationResult.Failed(
                        operation,
                        "Vulkan",
                        "Transform feedback commands must be enqueued while a render pipeline is active.");

                try
                {
                    ValidateBindingRange(Data.BindingLocation, 1);
                }
                catch (Exception ex)
                {
                    return XRTransformFeedbackOperationResult.Failed(operation, "Vulkan", ex.Message);
                }

                FrameOpContext context = Renderer.CaptureFrameOpContext();
                int passIndex = Renderer.EnsureValidPassIndex(
                    RuntimeEngine.Rendering.State.CurrentRenderGraphPassIndex,
                    "TransformFeedback",
                    context.PassMetadata);

                Renderer.EnqueueFrameOp(new TransformFeedbackOp(
                    passIndex,
                    Renderer.GetCurrentDrawFrameBuffer(),
                    this,
                    operation,
                    counterBuffer,
                    feedbackBufferOffset,
                    feedbackBufferSize,
                    counterBufferOffset,
                    counterOffset,
                    vertexStride,
                    instanceCount,
                    firstInstance,
                    context));

                return XRTransformFeedbackOperationResult.Success(operation, "Vulkan");
            }

            internal void BindFeedbackBuffer(CommandBuffer commandBuffer, ulong offset = 0, ulong size = Vk.WholeSize)
            {
                VkDataBuffer buffer = GetFeedbackBuffer();
                BindBuffer(commandBuffer, buffer, Data.BindingLocation, offset, size);
            }

            internal void BindBuffer(
                CommandBuffer commandBuffer,
                VkDataBuffer buffer,
                uint binding,
                ulong offset = 0,
                ulong size = Vk.WholeSize)
            {
                EnsureSupported();
                ValidateBindingRange(binding, 1);

                Buffer handle = ResolveBufferHandle(buffer, "transform feedback");
                ulong resolvedSize = ResolveBufferSize(buffer, offset, size);

                Extension.CmdBindTransformFeedbackBuffers(
                    commandBuffer,
                    binding,
                    1,
                    &handle,
                    &offset,
                    &resolvedSize);
            }

            internal void Begin(CommandBuffer commandBuffer)
            {
                EnsureSupported();
                Extension.CmdBeginTransformFeedback(commandBuffer, 0, 0, (Buffer*)null, (ulong*)null);
            }

            internal void Begin(
                CommandBuffer commandBuffer,
                VkDataBuffer counterBuffer,
                uint firstCounterBuffer,
                ulong counterBufferOffset = 0)
            {
                EnsureSupported();
                ValidateBindingRange(firstCounterBuffer, 1);

                Buffer counterHandle = ResolveBufferHandle(counterBuffer, "transform feedback counter");
                Extension.CmdBeginTransformFeedback(
                    commandBuffer,
                    firstCounterBuffer,
                    1,
                    &counterHandle,
                    &counterBufferOffset);
            }

            internal void End(CommandBuffer commandBuffer)
            {
                EnsureSupported();
                Extension.CmdEndTransformFeedback(commandBuffer, 0, 0, (Buffer*)null, (ulong*)null);
            }

            internal void End(
                CommandBuffer commandBuffer,
                VkDataBuffer counterBuffer,
                uint firstCounterBuffer,
                ulong counterBufferOffset = 0)
            {
                EnsureSupported();
                ValidateBindingRange(firstCounterBuffer, 1);

                Buffer counterHandle = ResolveBufferHandle(counterBuffer, "transform feedback counter");
                Extension.CmdEndTransformFeedback(
                    commandBuffer,
                    firstCounterBuffer,
                    1,
                    &counterHandle,
                    &counterBufferOffset);
            }

            internal void DrawIndirectByteCount(
                CommandBuffer commandBuffer,
                uint instanceCount,
                uint firstInstance,
                VkDataBuffer counterBuffer,
                ulong counterBufferOffset,
                uint counterOffset,
                uint vertexStride)
            {
                EnsureSupported();
                if (!Renderer.SupportsTransformFeedbackDraw)
                    throw new NotSupportedException("VK_EXT_transform_feedback byte-count draws are not supported by this device.");
                if (vertexStride == 0)
                    throw new ArgumentOutOfRangeException(nameof(vertexStride), "Vertex stride must be non-zero.");

                Buffer counterHandle = ResolveBufferHandle(counterBuffer, "transform feedback counter");
                Extension.CmdDrawIndirectByteCount(
                    commandBuffer,
                    instanceCount,
                    firstInstance,
                    counterHandle,
                    counterBufferOffset,
                    counterOffset,
                    vertexStride);
            }

            private VkDataBuffer GetFeedbackBuffer()
            {
                if (Renderer.GetOrCreateAPIRenderObject(Data.FeedbackBuffer, generateNow: true) is VkDataBuffer buffer)
                    return buffer;

                throw new InvalidOperationException("Failed to resolve transform feedback buffer.");
            }

            private Silk.NET.Vulkan.Extensions.EXT.ExtTransformFeedback Extension
            {
                get
                {
                    EnsureSupported();
                    return Renderer._extTransformFeedback!;
                }
            }

            private void EnsureSupported()
            {
                if (Renderer.SupportsTransformFeedback && Renderer._extTransformFeedback is not null)
                    return;

                throw new NotSupportedException("Vulkan transform feedback requires VK_EXT_transform_feedback with the transformFeedback feature enabled.");
            }

            private void ValidateBindingRange(uint firstBinding, uint bindingCount)
            {
                uint maxBindings = Renderer.TransformFeedbackProperties.MaxTransformFeedbackBuffers;
                if (maxBindings == 0)
                    return;

                if (bindingCount == 0 || firstBinding >= maxBindings || firstBinding + bindingCount > maxBindings)
                    throw new ArgumentOutOfRangeException(nameof(firstBinding), $"Transform feedback binding range {firstBinding}..{firstBinding + bindingCount - 1} exceeds device limit {maxBindings}.");
            }

            private static Buffer ResolveBufferHandle(VkDataBuffer buffer, string role)
            {
                buffer.Generate();
                Buffer? handle = buffer.BufferHandle;
                if (!handle.HasValue || handle.Value.Handle == 0)
                    throw new InvalidOperationException($"Cannot bind {role} buffer because it has no Vulkan handle.");

                return handle.Value;
            }

            private static ulong ResolveBufferSize(VkDataBuffer buffer, ulong offset, ulong size)
            {
                if (size != Vk.WholeSize)
                    return size;

                if (offset >= buffer.AllocatedByteSize)
                    throw new ArgumentOutOfRangeException(nameof(offset), "Transform feedback buffer offset is outside the allocated buffer.");

                return buffer.AllocatedByteSize - offset;
            }
        }
    }
}
