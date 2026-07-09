using System;
using System.Buffers;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Silk.NET.Vulkan;
using XREngine.Data.Colors;
using XREngine.Data.Rendering;

namespace XREngine.Rendering.Vulkan
{
    public unsafe partial class VulkanRenderer
    {
        private void StoreFrameOpSignatureDebugParts(CommandBufferCacheVariant variant, FrameOp[] ops)
        {
            if (!FrameOpSignatureDiffDiagnosticsEnabled)
                return;

            variant.SignatureDebugParts = CaptureFrameOpSignatureDebugParts(ops);
        }

        private void LogFrameOpSignatureDiff(
            uint imageIndex,
            CommandBufferCacheVariant variant,
            ulong currentSignature,
            FrameOp[] ops)
        {
            if (!FrameOpSignatureDiffDiagnosticsEnabled || variant.FrameOpsSignature == ulong.MaxValue)
                return;

            int logIndex = Interlocked.Increment(ref _frameOpSignatureDiffLogCount);
            if (logIndex > FrameOpSignatureDiffLogLimit)
                return;

            FrameOpSignatureDebugPart[] currentParts = CaptureFrameOpSignatureDebugParts(ops);
            string summary = variant.SignatureDebugParts is null
                ? "no previous component snapshot for this swapchain image"
                : BuildFrameOpSignatureDiffSummary(variant.SignatureDebugParts, currentParts);

            Debug.Vulkan(
                $"[Vulkan] Frame-op signature mismatch image={imageIndex} previous=0x{variant.FrameOpsSignature:X16} current=0x{currentSignature:X16} ops={ops.Length}: {summary}");
        }

        private void LogFrameOpSignatureVariantEvictionDiff(
            uint imageIndex,
            CommandBufferCacheVariant evicted,
            ulong currentSignature,
            FrameOp[] ops)
        {
            if (!FrameOpSignatureDiffDiagnosticsEnabled || evicted.FrameOpsSignature == ulong.MaxValue)
                return;

            int logIndex = Interlocked.Increment(ref _frameOpSignatureDiffLogCount);
            if (logIndex > FrameOpSignatureDiffLogLimit)
                return;

            FrameOpSignatureDebugPart[] currentParts = CaptureFrameOpSignatureDebugParts(ops);
            string summary = evicted.SignatureDebugParts is null
                ? "no previous component snapshot for evicted variant"
                : BuildFrameOpSignatureDiffSummary(evicted.SignatureDebugParts, currentParts);

            Debug.Vulkan(
                $"[Vulkan] Frame-op variant cache eviction image={imageIndex} previous=0x{evicted.FrameOpsSignature:X16} current=0x{currentSignature:X16} ops={ops.Length} variants={PrimaryCommandBufferVariantCapacity}: {summary}");
        }

        private static string BuildFrameOpSignatureDiffSummary(FrameOpSignatureDebugPart[] previous, FrameOpSignatureDebugPart[] current)
        {
            int commonCount = Math.Min(previous.Length, current.Length);
            for (int i = 0; i < commonCount; i++)
            {
                FrameOpSignatureDebugPart previousPart = previous[i];
                FrameOpSignatureDebugPart currentPart = current[i];
                if (previousPart.OpIndex == currentPart.OpIndex &&
                    string.Equals(previousPart.OpType, currentPart.OpType, StringComparison.Ordinal) &&
                    string.Equals(previousPart.Component, currentPart.Component, StringComparison.Ordinal) &&
                    previousPart.Signature == currentPart.Signature)
                {
                    continue;
                }

                return $"firstDiffPart={i} previous={DescribeFrameOpSignaturePart(previousPart)} current={DescribeFrameOpSignaturePart(currentPart)}";
            }

            if (previous.Length != current.Length)
                return $"partCount {previous.Length}->{current.Length}";

            return "component signatures match; aggregate mismatch likely comes from a hashing-order bug";
        }

        private static string DescribeFrameOpSignaturePart(in FrameOpSignatureDebugPart part)
            => $"op={part.OpIndex}:{part.OpType}.{part.Component} sig=0x{part.Signature:X16} {part.Detail}";

        private static FrameOpSignatureDebugPart[] CaptureFrameOpSignatureDebugParts(FrameOp[] ops)
        {
            List<FrameOpSignatureDebugPart> parts = new(Math.Max(ops.Length * 5, 8));
            for (int i = 0; i < ops.Length; i++)
            {
                FrameOp op = ops[i];
                string opType = op.GetType().Name;
                AddFrameOpBaseSignaturePart(parts, i, opType, op);

                switch (op)
                {
                    case ClearOp clear:
                        AddClearSignaturePart(parts, i, opType, clear);
                        break;
                    case MeshDrawOp draw:
                        AddMeshDrawSignatureParts(parts, i, opType, draw);
                        break;
                    case QueryOp query:
                        AddQuerySignaturePart(parts, i, opType, query);
                        break;
                    case BlitOp blit:
                        AddBlitSignaturePart(parts, i, opType, blit);
                        break;
                    case IndirectDrawOp indirect:
                        AddIndirectDrawSignaturePart(parts, i, opType, indirect);
                        break;
                    case MeshTaskDispatchIndirectCountOp meshTask:
                        AddMeshTaskSignaturePart(parts, i, opType, meshTask);
                        break;
                    case MemoryBarrierOp barrier:
                        AddMemoryBarrierSignaturePart(parts, i, opType, barrier);
                        break;
                    case PublishFramebufferForSamplingOp publish:
                        AddPublishFramebufferForSamplingSignaturePart(parts, i, opType, publish);
                        break;
                    case DlssUpscaleOp dlss:
                        AddDlssUpscaleSignaturePart(parts, i, opType, dlss);
                        break;
                    case DlssFrameGenerationOp dlssFrameGeneration:
                        AddDlssFrameGenerationSignaturePart(parts, i, opType, dlssFrameGeneration);
                        break;
                    case TransformFeedbackOp transformFeedback:
                        AddTransformFeedbackSignaturePart(parts, i, opType, transformFeedback);
                        break;
                    case ComputeDispatchOp compute:
                        AddComputeDispatchSignatureParts(parts, i, opType, compute);
                        break;
                    case TextureUploadFrameOp upload:
                        AddTextureUploadSignaturePart(parts, i, opType, upload);
                        break;
                }
            }

            return [.. parts];
        }

        private static void AddFrameOpBaseSignaturePart(List<FrameOpSignatureDebugPart> parts, int opIndex, string opType, FrameOp op)
        {
            HashCode hash = new();
            hash.Add(op.GetType().Name, StringComparer.Ordinal);
            hash.Add(op.PassIndex);
            hash.Add(ResolveCommandChainTargetIdentity(op));
            hash.Add(op.Context.PipelineIdentity);
            hash.Add(op.Context.ViewportIdentity);
            hash.Add(op.Context.OutputTargetIdentity);
            AddSignaturePart(
                parts,
                opIndex,
                opType,
                "base",
                hash,
                $"pass={op.PassIndex} target='{ResolveCommandChainTargetName(op)}' targetId={ResolveCommandChainTargetIdentity(op)} pipe={op.Context.PipelineIdentity} vp={op.Context.ViewportIdentity} sched={op.Context.SchedulingIdentity}");
        }

        private static void AddClearSignaturePart(List<FrameOpSignatureDebugPart> parts, int opIndex, string opType, ClearOp clear)
        {
            HashCode hash = new();
            hash.Add(clear.ClearColor);
            hash.Add(clear.ClearDepth);
            hash.Add(clear.ClearStencil);
            hash.Add(clear.Color.R);
            hash.Add(clear.Color.G);
            hash.Add(clear.Color.B);
            hash.Add(clear.Color.A);
            hash.Add(clear.Depth);
            hash.Add(clear.Stencil);
            hash.Add(clear.Rect.Offset.X);
            hash.Add(clear.Rect.Offset.Y);
            hash.Add(clear.Rect.Extent.Width);
            hash.Add(clear.Rect.Extent.Height);
            AddSignaturePart(
                parts,
                opIndex,
                opType,
                "clear",
                hash,
                $"flags={clear.ClearColor}/{clear.ClearDepth}/{clear.ClearStencil} color=({clear.Color.R},{clear.Color.G},{clear.Color.B},{clear.Color.A}) rect={clear.Rect.Offset.X},{clear.Rect.Offset.Y},{clear.Rect.Extent.Width}x{clear.Rect.Extent.Height}");
        }

        private static void AddMeshDrawSignatureParts(List<FrameOpSignatureDebugPart> parts, int opIndex, string opType, MeshDrawOp meshDraw)
        {
            PendingMeshDraw draw = meshDraw.Draw;
            AddMeshDrawViewportSignaturePart(parts, opIndex, opType, draw);
            AddMeshDrawPipelineStateSignaturePart(parts, opIndex, opType, draw);
            AddMeshDrawMaterialSignaturePart(parts, opIndex, opType, draw);
            AddProgramBindingSignatureParts(parts, opIndex, opType, "program", draw.ProgramBindingSnapshot, meshDraw.Context.PipelineInstance);
        }

        private static void AddMeshDrawViewportSignaturePart(List<FrameOpSignatureDebugPart> parts, int opIndex, string opType, in PendingMeshDraw draw)
        {
            HashCode hash = new();
            hash.Add(draw.Viewport.X);
            hash.Add(draw.Viewport.Y);
            hash.Add(draw.Viewport.Width);
            hash.Add(draw.Viewport.Height);
            hash.Add(draw.Scissor.Offset.X);
            hash.Add(draw.Scissor.Offset.Y);
            hash.Add(draw.Scissor.Extent.Width);
            hash.Add(draw.Scissor.Extent.Height);
            hash.Add(draw.ViewportScissorCount);

            if (draw.ViewportScissorCount > 1 &&
                draw.IndexedViewports is { } indexedViewports &&
                draw.IndexedScissors is { } indexedScissors)
            {
                int indexedCount = (int)Math.Min(draw.ViewportScissorCount, (uint)Math.Min(indexedViewports.Length, indexedScissors.Length));
                for (int indexedIndex = 0; indexedIndex < indexedCount; indexedIndex++)
                {
                    Viewport indexedViewport = indexedViewports[indexedIndex];
                    Rect2D indexedScissor = indexedScissors[indexedIndex];
                    hash.Add(indexedViewport.X);
                    hash.Add(indexedViewport.Y);
                    hash.Add(indexedViewport.Width);
                    hash.Add(indexedViewport.Height);
                    hash.Add(indexedViewport.MinDepth);
                    hash.Add(indexedViewport.MaxDepth);
                    hash.Add(indexedScissor.Offset.X);
                    hash.Add(indexedScissor.Offset.Y);
                    hash.Add(indexedScissor.Extent.Width);
                    hash.Add(indexedScissor.Extent.Height);
                }
            }

            AddSignaturePart(
                parts,
                opIndex,
                opType,
                "viewport",
                hash,
                $"vp=({draw.Viewport.X},{draw.Viewport.Y},{draw.Viewport.Width}x{draw.Viewport.Height}) scissor=({draw.Scissor.Offset.X},{draw.Scissor.Offset.Y},{draw.Scissor.Extent.Width}x{draw.Scissor.Extent.Height}) count={draw.ViewportScissorCount}");
        }

        private static void AddMeshDrawPipelineStateSignaturePart(List<FrameOpSignatureDebugPart> parts, int opIndex, string opType, in PendingMeshDraw draw)
        {
            HashCode hash = new();
            hash.Add(draw.DepthTestEnabled);
            hash.Add(draw.DepthWriteEnabled);
            hash.Add((int)draw.DepthCompareOp);
            hash.Add(draw.StencilTestEnabled);
            hash.Add(draw.StencilWriteMask);
            hash.Add((int)draw.ColorWriteMask);
            hash.Add((int)draw.CullMode);
            hash.Add((int)draw.FrontFace);
            hash.Add(draw.BlendEnabled);
            hash.Add((int)draw.ColorBlendOp);
            hash.Add((int)draw.AlphaBlendOp);
            hash.Add((int)draw.SrcColorBlendFactor);
            hash.Add((int)draw.DstColorBlendFactor);
            hash.Add((int)draw.SrcAlphaBlendFactor);
            hash.Add((int)draw.DstAlphaBlendFactor);
            AddSignaturePart(
                parts,
                opIndex,
                opType,
                "pipeline",
                hash,
                $"depth={draw.DepthTestEnabled}/{draw.DepthWriteEnabled}/{draw.DepthCompareOp} stencil={draw.StencilTestEnabled} colorMask={draw.ColorWriteMask} cull={draw.CullMode} front={draw.FrontFace} blend={draw.BlendEnabled}");
        }

        private static void AddMeshDrawMaterialSignaturePart(List<FrameOpSignatureDebugPart> parts, int opIndex, string opType, in PendingMeshDraw draw)
        {
            VkMeshRenderer? renderer = draw.Renderer;
            HashCode hash = new();
            hash.Add(renderer?.GetHashCode() ?? 0);
            hash.Add(draw.MaterialOverride?.GetHashCode() ?? 0);
            hash.Add(draw.Instances);
            hash.Add((int)draw.BillboardMode);
            hash.Add(draw.IsStereoPass);
            hash.Add(draw.UseUnjitteredProjection);

            XRMaterial? material = draw.MaterialOverride ?? renderer?.MeshRenderer.Material;
            AddSignaturePart(
                parts,
                opIndex,
                opType,
                "material",
                hash,
                $"mesh='{renderer?.MeshRenderer.Mesh?.Name ?? "<unnamed mesh>"}' material='{material?.Name ?? "<unnamed material>"}' renderer=0x{(renderer?.GetHashCode() ?? 0):X8} instances={draw.Instances} stereo={draw.IsStereoPass} unjittered={draw.UseUnjitteredProjection}");
        }

        private static void AddQuerySignaturePart(List<FrameOpSignatureDebugPart> parts, int opIndex, string opType, QueryOp query)
        {
            HashCode hash = new();
            hash.Add(query.Query.GetHashCode());
            hash.Add((int)query.QueryTarget);
            hash.Add((int)query.Operation);
            AddSignaturePart(
                parts,
                opIndex,
                opType,
                "query",
                hash,
                $"op={query.Operation} target={query.QueryTarget} query=0x{query.Query.GetHashCode():X8}");
        }

        private static void AddBlitSignaturePart(List<FrameOpSignatureDebugPart> parts, int opIndex, string opType, BlitOp blit)
        {
            HashCode hash = new();
            hash.Add(blit.InFbo?.GetHashCode() ?? 0);
            hash.Add(blit.OutFbo?.GetHashCode() ?? 0);
            hash.Add(blit.InX);
            hash.Add(blit.InY);
            hash.Add(blit.InW);
            hash.Add(blit.InH);
            hash.Add(blit.OutX);
            hash.Add(blit.OutY);
            hash.Add(blit.OutW);
            hash.Add(blit.OutH);
            hash.Add((int)blit.ReadBufferMode);
            hash.Add(blit.ColorBit);
            hash.Add(blit.DepthBit);
            hash.Add(blit.StencilBit);
            hash.Add(blit.LinearFilter);
            AddSignaturePart(
                parts,
                opIndex,
                opType,
                "blit",
                hash,
                $"in='{blit.InFbo?.Name ?? "<swapchain/null>"}' out='{blit.OutFbo?.Name ?? "<swapchain/null>"}' src={blit.InX},{blit.InY},{blit.InW}x{blit.InH} dst={blit.OutX},{blit.OutY},{blit.OutW}x{blit.OutH} bits={blit.ColorBit}/{blit.DepthBit}/{blit.StencilBit}");
        }

        private static void AddIndirectDrawSignaturePart(List<FrameOpSignatureDebugPart> parts, int opIndex, string opType, IndirectDrawOp indirect)
        {
            HashCode hash = new();
            hash.Add(ComputeCommandBufferDataBufferSignature(indirect.IndirectBuffer));
            hash.Add(ComputeCommandBufferDataBufferSignature(indirect.ParameterBuffer));
            hash.Add(indirect.DrawCount);
            hash.Add(indirect.Stride);
            hash.Add(indirect.ByteOffset);
            hash.Add(indirect.CountByteOffset);
            hash.Add(indirect.UseCount);
            hash.Add(indirect.BindlessMaterialTextures?.Program.GetHashCode() ?? 0);
            hash.Add(indirect.BindlessMaterialTextures?.Consumer, StringComparer.Ordinal);
            AddSignaturePart(parts, opIndex, opType, "indirect", hash, $"draws={indirect.DrawCount} stride={indirect.Stride} byteOffset={indirect.ByteOffset} countOffset={indirect.CountByteOffset} useCount={indirect.UseCount} indirectBuffer=0x{indirect.IndirectBuffer.BufferHandle?.Handle ?? 0UL:X} parameterBuffer=0x{indirect.ParameterBuffer?.BufferHandle?.Handle ?? 0UL:X} bindlessMaterialTextures={indirect.BindlessMaterialTextures.HasValue}");
        }

        private static void AddMeshTaskSignaturePart(List<FrameOpSignatureDebugPart> parts, int opIndex, string opType, MeshTaskDispatchIndirectCountOp meshTask)
        {
            HashCode hash = new();
            hash.Add(ComputeCommandBufferDataBufferSignature(meshTask.IndirectBuffer));
            hash.Add(ComputeCommandBufferDataBufferSignature(meshTask.CountBuffer));
            hash.Add(meshTask.MaxDrawCount);
            hash.Add(meshTask.Stride);
            hash.Add(meshTask.ByteOffset);
            hash.Add(meshTask.CountByteOffset);
            hash.Add(meshTask.BindlessMaterialTextures?.Program.GetHashCode() ?? 0);
            hash.Add(meshTask.BindlessMaterialTextures?.Consumer, StringComparer.Ordinal);
            AddSignaturePart(parts, opIndex, opType, "meshTask", hash, $"maxDraws={meshTask.MaxDrawCount} stride={meshTask.Stride} indirectBuffer=0x{meshTask.IndirectBuffer.BufferHandle?.Handle ?? 0UL:X} countBuffer=0x{meshTask.CountBuffer.BufferHandle?.Handle ?? 0UL:X} bindlessMaterialTextures={meshTask.BindlessMaterialTextures.HasValue}");
        }

        private static void AddMemoryBarrierSignaturePart(List<FrameOpSignatureDebugPart> parts, int opIndex, string opType, MemoryBarrierOp barrier)
        {
            HashCode hash = new();
            hash.Add((int)barrier.Mask);
            AddSignaturePart(parts, opIndex, opType, "barrier", hash, $"mask={barrier.Mask}");
        }

        private static void AddPublishFramebufferForSamplingSignaturePart(List<FrameOpSignatureDebugPart> parts, int opIndex, string opType, PublishFramebufferForSamplingOp publish)
        {
            HashCode hash = new();
            hash.Add(publish.FrameBuffer.GetHashCode());
            AddSignaturePart(parts, opIndex, opType, "publish-fbo-sampling", hash, $"fbo='{publish.FrameBuffer.Name ?? "<unnamed>"}'");
        }

        private static void AddDlssUpscaleSignaturePart(List<FrameOpSignatureDebugPart> parts, int opIndex, string opType, DlssUpscaleOp dlss)
        {
            HashCode hash = new();
            hash.Add(dlss.Session.GetHashCode());
            hash.Add(dlss.SourceColor.Image.Handle);
            hash.Add(dlss.Depth.Image.Handle);
            hash.Add(dlss.Motion.Image.Handle);
            hash.Add(dlss.OutputColor.Image.Handle);
            hash.Add(dlss.Exposure?.Image.Handle ?? 0UL);
            hash.Add(dlss.Parameters.InputWidth);
            hash.Add(dlss.Parameters.InputHeight);
            hash.Add(dlss.Parameters.OutputWidth);
            hash.Add(dlss.Parameters.OutputHeight);
            hash.Add(dlss.Parameters.FrameIndex);
            hash.Add(dlss.Parameters.ResetHistory);
            hash.Add(dlss.Parameters.OutputHdr);
            hash.Add(dlss.Parameters.DlssQuality);
            AddSignaturePart(parts, opIndex, opType, "dlss", hash, $"frame={dlss.Parameters.FrameIndex} reset={dlss.Parameters.ResetHistory} input={dlss.Parameters.InputWidth}x{dlss.Parameters.InputHeight} output={dlss.Parameters.OutputWidth}x{dlss.Parameters.OutputHeight}");
        }

        private static void AddDlssFrameGenerationSignaturePart(List<FrameOpSignatureDebugPart> parts, int opIndex, string opType, DlssFrameGenerationOp dlssFrameGeneration)
        {
            HashCode hash = new();
            hash.Add(dlssFrameGeneration.Session.GetHashCode());
            hash.Add(dlssFrameGeneration.Depth.Image.Handle);
            hash.Add(dlssFrameGeneration.Motion.Image.Handle);
            hash.Add(dlssFrameGeneration.HudlessColor.Image.Handle);
            hash.Add(dlssFrameGeneration.Parameters.InputWidth);
            hash.Add(dlssFrameGeneration.Parameters.InputHeight);
            hash.Add(dlssFrameGeneration.Parameters.OutputWidth);
            hash.Add(dlssFrameGeneration.Parameters.OutputHeight);
            hash.Add(dlssFrameGeneration.Parameters.FrameIndex);
            hash.Add(dlssFrameGeneration.Parameters.ResetHistory);
            hash.Add(dlssFrameGeneration.Parameters.OutputHdr);
            AddSignaturePart(parts, opIndex, opType, "dlssFrameGen", hash, $"frame={dlssFrameGeneration.Parameters.FrameIndex} reset={dlssFrameGeneration.Parameters.ResetHistory}");
        }

        private static void AddTransformFeedbackSignaturePart(List<FrameOpSignatureDebugPart> parts, int opIndex, string opType, TransformFeedbackOp transformFeedback)
        {
            HashCode hash = new();
            hash.Add(transformFeedback.TransformFeedback.GetHashCode());
            hash.Add((int)transformFeedback.Operation);
            hash.Add(transformFeedback.CounterBuffer?.GetHashCode() ?? 0);
            hash.Add(transformFeedback.FeedbackBufferOffset);
            hash.Add(transformFeedback.FeedbackBufferSize ?? 0ul);
            hash.Add(transformFeedback.CounterBufferOffset);
            hash.Add(transformFeedback.CounterOffset);
            hash.Add(transformFeedback.VertexStride);
            hash.Add(transformFeedback.InstanceCount);
            hash.Add(transformFeedback.FirstInstance);
            AddSignaturePart(parts, opIndex, opType, "transformFeedback", hash, $"op={transformFeedback.Operation} instances={transformFeedback.InstanceCount} stride={transformFeedback.VertexStride}");
        }

        private static void AddComputeDispatchSignatureParts(List<FrameOpSignatureDebugPart> parts, int opIndex, string opType, ComputeDispatchOp compute)
        {
            HashCode hash = new();
            hash.Add(compute.Program.GetHashCode());
            hash.Add(compute.GroupsX);
            hash.Add(compute.GroupsY);
            hash.Add(compute.GroupsZ);
            AddSignaturePart(parts, opIndex, opType, "compute", hash, $"program='{compute.Program.Data.Name ?? "<unnamed program>"}' groups={compute.GroupsX},{compute.GroupsY},{compute.GroupsZ}");
            AddProgramBindingSignatureParts(parts, opIndex, opType, "computeProgram", compute.Snapshot, compute.Context.PipelineInstance);
        }

        private static void AddTextureUploadSignaturePart(List<FrameOpSignatureDebugPart> parts, int opIndex, string opType, TextureUploadFrameOp upload)
        {
            HashCode hash = new();
            hash.Add(upload.Upload.PublicationToken);
            hash.Add(upload.Upload.Request.StreamingGeneration);
            hash.Add(upload.Upload.Image.Handle);
            hash.Add(upload.Upload.ImageView.Handle);
            hash.Add(upload.Upload.StagingResources.Length);
            AddSignaturePart(
                parts,
                opIndex,
                opType,
                "textureUpload",
                hash,
                $"token={upload.Upload.PublicationToken} gen={upload.Upload.Request.StreamingGeneration} extent={upload.Upload.Extent.Width}x{upload.Upload.Extent.Height} mips={upload.Upload.MipLevels}");
        }

        private static void AddProgramBindingSignatureParts(
            List<FrameOpSignatureDebugPart> parts,
            int opIndex,
            string opType,
            string prefix,
            ComputeDispatchSnapshot? snapshot,
            XRRenderPipelineInstance? pipeline)
        {
            if (snapshot is null)
            {
                AddSignaturePart(parts, opIndex, opType, $"{prefix}.null", 0, "snapshot=<null>");
                return;
            }

            AddSignaturePart(
                parts,
                opIndex,
                opType,
                $"{prefix}.uniforms",
                HashUniformBindingLayout(snapshot.Uniforms),
                $"count={snapshot.Uniforms.Count} valueHash=0x{unchecked((ulong)HashUniformBindings(snapshot.Uniforms)):X16} stableValueHash=0x{unchecked((ulong)HashUniformBindingsStable(snapshot.Uniforms)):X16} keys=[{SampleKeys(snapshot.Uniforms.Keys)}]");
            AddSignaturePart(
                parts,
                opIndex,
                opType,
                $"{prefix}.samplerUnits",
                HashSamplerUnitBindingLayout(snapshot.Samplers, snapshot.SamplerNamesByUnit),
                $"count={snapshot.Samplers.Count} descriptor=0x{HashSamplerUnitBindings(snapshot.Samplers, snapshot.SamplerNamesByUnit, pipeline):X16} stableDescriptor=0x{unchecked((ulong)HashSamplerUnitBindingsStable(snapshot.Samplers, snapshot.SamplerNamesByUnit, pipeline)):X16} keys=[{SampleKeys(snapshot.Samplers.Keys)}]");
            AddSignaturePart(
                parts,
                opIndex,
                opType,
                $"{prefix}.samplerNames",
                HashSamplerNameBindingLayout(snapshot.SamplersByName),
                $"count={snapshot.SamplersByName.Count} descriptor=0x{HashSamplerNameBindings(snapshot.SamplersByName, pipeline):X16} stableDescriptor=0x{unchecked((ulong)HashSamplerNameBindingsStable(snapshot.SamplersByName, pipeline)):X16} keys=[{SampleKeys(snapshot.SamplersByName.Keys)}]");
            AddSignaturePart(
                parts,
                opIndex,
                opType,
                $"{prefix}.images",
                HashImageBindingLayout(snapshot.Images),
                $"count={snapshot.Images.Count} descriptor=0x{unchecked((ulong)HashImageBindings(snapshot.Images)):X16} stableDescriptor=0x{unchecked((ulong)HashImageBindingsStable(snapshot.Images)):X16} keys=[{SampleKeys(snapshot.Images.Keys)}]");
            AddSignaturePart(
                parts,
                opIndex,
                opType,
                $"{prefix}.buffers",
                HashBufferBindingLayout(snapshot.Buffers),
                $"count={snapshot.Buffers.Count} descriptor=0x{unchecked((ulong)HashBufferBindings(snapshot.Buffers)):X16} stableDescriptor=0x{unchecked((ulong)HashBufferBindingsStable(snapshot.Buffers)):X16} keys=[{SampleKeys(snapshot.Buffers.Keys)}]");
        }

        private static void AddSignaturePart(
            List<FrameOpSignatureDebugPart> parts,
            int opIndex,
            string opType,
            string component,
            HashCode hash,
            string detail)
            => AddSignaturePart(parts, opIndex, opType, component, unchecked((ulong)hash.ToHashCode()), detail);

        private static void AddSignaturePart(
            List<FrameOpSignatureDebugPart> parts,
            int opIndex,
            string opType,
            string component,
            int signature,
            string detail)
            => AddSignaturePart(parts, opIndex, opType, component, unchecked((ulong)signature), detail);

        private static void AddSignaturePart(
            List<FrameOpSignatureDebugPart> parts,
            int opIndex,
            string opType,
            string component,
            ulong signature,
            string detail)
            => parts.Add(new FrameOpSignatureDebugPart(opIndex, opType, component, signature, detail));

        private static int HashUniformBindingsStable(Dictionary<string, ProgramUniformValue> uniforms)
        {
            HashCode hash = new();
            hash.Add(uniforms.Count);
            foreach (var pair in uniforms.OrderBy(p => p.Key, StringComparer.Ordinal))
            {
                HashCode item = new();
                item.Add(pair.Key, StringComparer.Ordinal);
                item.Add((int)pair.Value.Type);
                item.Add(pair.Value.IsArray);
                HashUniformValue(ref item, pair.Value.Value);
                hash.Add(item.ToHashCode());
            }

            return hash.ToHashCode();
        }

        private static int HashSamplerUnitBindingsStable(
            Dictionary<uint, XRTexture> samplers,
            Dictionary<uint, string> samplerNamesByUnit,
            XRRenderPipelineInstance? pipeline)
        {
            HashCode hash = new();
            hash.Add(samplers.Count);
            foreach (var pair in samplers.OrderBy(p => p.Key))
            {
                hash.Add(pair.Key);
                bool mutableFrameSource = samplerNamesByUnit.TryGetValue(pair.Key, out string? samplerName) &&
                    IsMutableFrameSourceSamplerNameForSignatureDebug(samplerName, pipeline);
                hash.Add(mutableFrameSource
                    ? FrameSourceMutableDescriptorSignature
                    : ComputeTextureDescriptorSignature(pair.Value));
            }

            return hash.ToHashCode();
        }

        private static int HashSamplerNameBindingsStable(Dictionary<string, XRTexture> samplers, XRRenderPipelineInstance? pipeline)
        {
            HashCode hash = new();
            hash.Add(samplers.Count);
            foreach (var pair in samplers.OrderBy(p => p.Key, StringComparer.Ordinal))
            {
                hash.Add(pair.Key, StringComparer.Ordinal);
                hash.Add(IsMutableFrameSourceSamplerNameForSignatureDebug(pair.Key, pipeline)
                    ? FrameSourceMutableDescriptorSignature
                    : ComputeTextureDescriptorSignature(pair.Value));
            }

            return hash.ToHashCode();
        }

        private static bool IsMutableFrameSourceSamplerNameForSignatureDebug(string? name, XRRenderPipelineInstance? pipeline)
        {
            if (IsFrameSourceSamplerName(name))
                return true;

            return !string.IsNullOrWhiteSpace(name) &&
                pipeline is not null &&
                pipeline.TryGetTexture(name, out XRTexture? texture) &&
                texture is not null;
        }

        private static int HashImageBindingsStable(Dictionary<uint, ProgramImageBinding> images)
        {
            HashCode hash = new();
            hash.Add(images.Count);
            foreach (var pair in images.OrderBy(p => p.Key))
            {
                ProgramImageBinding binding = pair.Value;
                hash.Add(pair.Key);
                hash.Add(binding.Texture.GetHashCode());
                hash.Add(ComputeTextureDescriptorSignature(binding.Texture));
                hash.Add(binding.Level);
                hash.Add(binding.Layered);
                hash.Add(binding.Layer);
                hash.Add((int)binding.Access);
                hash.Add((int)binding.Format);
            }

            return hash.ToHashCode();
        }

        private static int HashBufferBindingsStable(Dictionary<uint, XRDataBuffer> buffers)
        {
            HashCode hash = new();
            hash.Add(buffers.Count);
            foreach (var pair in buffers.OrderBy(p => p.Key))
            {
                hash.Add(pair.Key);
                hash.Add(pair.Value.GetHashCode());
            }

            return hash.ToHashCode();
        }

        private static string SampleKeys(IEnumerable<string> keys)
            => string.Join(",", keys.OrderBy(k => k, StringComparer.Ordinal).Take(6));

        private static string SampleKeys(IEnumerable<uint> keys)
            => string.Join(",", keys.OrderBy(k => k).Take(6));

    }
}
