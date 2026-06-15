using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using XREngine.Data.Rendering;
using XREngine.Rendering.RenderGraph;

namespace XREngine.Rendering.Pipelines.Commands
{
    [RenderPipelineScriptCommand]
    public class VPRC_BindFBOByName : ViewportStateRenderCommand<VPRC_UnbindFBO>
    {
        public string? FrameBufferName { get; set; }
        public bool Write { get; set; } = true;
        public bool ClearColor { get; set; } = true;
        public bool ClearDepth { get; set; } = true;
        public bool ClearStencil { get; set; } = true;

        /// <summary>
        /// When set, evaluated at render time to determine the FBO name,
        /// overriding <see cref="FrameBufferName"/>.
        /// </summary>
        public Func<string?>? DynamicName { get; set; }

        /// <summary>
        /// When set, evaluated at render time to determine whether to clear color,
        /// overriding <see cref="ClearColor"/>.
        /// </summary>
        public Func<bool>? DynamicClearColor { get; set; }

        /// <summary>
        /// When set, evaluated at render time to determine whether to clear depth,
        /// overriding <see cref="ClearDepth"/>.
        /// </summary>
        public Func<bool>? DynamicClearDepth { get; set; }

        private sealed class TopologicalPassOrderCacheEntry
        {
            public TopologicalPassOrderCacheEntry(IReadOnlyCollection<RenderPassMetadata> metadata)
                => Passes = RenderGraphSynchronizationPlanner.TopologicallySort(metadata);

            public IReadOnlyList<RenderPassMetadata> Passes { get; }
        }

        private static readonly ConditionalWeakTable<IReadOnlyCollection<RenderPassMetadata>, TopologicalPassOrderCacheEntry> TopologicalPassOrderCache = new();

        public void SetOptions(string frameBufferName, bool write = true, bool clearColor = true, bool clearDepth = true, bool clearStencil = true)
        {
            FrameBufferName = frameBufferName;
            Write = write;
            ClearColor = clearColor;
            ClearDepth = clearDepth;
            ClearStencil = clearStencil;
        }

        protected override void Execute()
        {
            string? name = DynamicName?.Invoke() ?? FrameBufferName;
            if (name is null)
                return;

            var fbo = ActivePipelineInstance.GetFBO<XRFrameBuffer>(name);
            if (fbo is null)
                return;

            if (Write)
                fbo.BindForWriting();
            else
                fbo.BindForReading();

            PopCommand.FrameBuffer = fbo;
            PopCommand.Write = Write;
            PopCommand.RenderTargetScope = ActivePipelineInstance.RenderState.PushRenderTargetBinding(
                name,
                fbo,
                Write);

            bool clearColor = DynamicClearColor?.Invoke() ?? ClearColor;
            bool clearDepth = DynamicClearDepth?.Invoke() ?? ClearDepth;
            bool clearStencil = ClearStencil;

            if (DeferredLightingDiagnostics.Enabled && DeferredLightingDiagnostics.IsWatchedFrameBufferName(name))
            {
                XREngine.Debug.VulkanEvery(
                    $"DeferredLighting.BindFBO.{name}",
                    TimeSpan.FromSeconds(1),
                    "[DeferredLightingDiag][BindFBO] name='{0}' write={1} clearColor={2} clearDepth={3} clearStencil={4}",
                    name,
                    Write,
                    clearColor,
                    clearDepth,
                    clearStencil);
            }

            if (clearColor || clearDepth || clearStencil)
            {
                if (clearStencil)
                    RuntimeEngine.Rendering.State.StencilMask(0xFF);

                int clearPassIndex = ResolveClearPassIndex(name, clearColor, clearDepth, clearStencil);
                if (DeferredLightingDiagnostics.Enabled && DeferredLightingDiagnostics.IsWatchedFrameBufferName(name))
                {
                    XREngine.Debug.VulkanEvery(
                        $"DeferredLighting.BindFBO.Clear.{name}",
                        TimeSpan.FromSeconds(1),
                        "[DeferredLightingDiag][BindFBO.Clear] name='{0}' clearPassIndex={1} color={2} depth={3} stencil={4}",
                        name,
                        clearPassIndex,
                        clearColor,
                        clearDepth,
                        clearStencil);
                }

                using var passScope = clearPassIndex != int.MinValue
                    ? RuntimeEngine.Rendering.State.PushRenderGraphPassIndex(clearPassIndex)
                    : default;

                RuntimeEngine.Rendering.State.ClearByBoundFBO(clearColor, clearDepth, clearStencil);
            }
        }

        private int ResolveClearPassIndex(string frameBufferName, bool clearColor, bool clearDepth, bool clearStencil)
        {
            if (ParentPipeline?.PassMetadata is not { Count: > 0 } metadata)
                return int.MinValue;

            int passIndex = ResolveClearPassIndexForName(metadata, frameBufferName, clearColor, clearDepth, clearStencil);
            if (passIndex != int.MinValue)
                return passIndex;

            return string.IsNullOrWhiteSpace(FrameBufferName) || string.Equals(FrameBufferName, frameBufferName, StringComparison.Ordinal)
                ? int.MinValue
                : ResolveClearPassIndexForName(metadata, FrameBufferName!, clearColor, clearDepth, clearStencil);
        }

        private static int ResolveClearPassIndexForName(
            IReadOnlyCollection<RenderPassMetadata> metadata,
            string frameBufferName,
            bool clearColor,
            bool clearDepth,
            bool clearStencil)
        {
            string colorResource = MakeFboColorResource(frameBufferName);
            string depthResource = MakeFboDepthResource(frameBufferName);
            XRFrameBuffer? frameBuffer = RuntimeEngine.Rendering.State.CurrentRenderingPipeline?.GetFBO<XRFrameBuffer>(frameBufferName);

            foreach (RenderPassMetadata pass in GetTopologicalPassOrder(metadata))
            {
                for (int i = 0; i < pass.ResourceUsages.Count; i++)
                {
                    RenderPassResourceUsage usage = pass.ResourceUsages[i];
                    if (usage.LoadOp != ERenderPassLoadOp.Clear)
                        continue;

                    if (clearColor &&
                        usage.ResourceType == ERenderPassResourceType.ColorAttachment &&
                        (string.Equals(usage.ResourceName, colorResource, StringComparison.OrdinalIgnoreCase) ||
                         MatchesFrameBufferAttachmentResource(frameBuffer, usage.ResourceName, colorAttachment: true)))
                    {
                        return pass.PassIndex;
                    }

                    if ((clearDepth || clearStencil) &&
                        (usage.ResourceType == ERenderPassResourceType.DepthAttachment ||
                         usage.ResourceType == ERenderPassResourceType.StencilAttachment) &&
                        (string.Equals(usage.ResourceName, depthResource, StringComparison.OrdinalIgnoreCase) ||
                         MatchesFrameBufferAttachmentResource(frameBuffer, usage.ResourceName, colorAttachment: false)))
                    {
                        return pass.PassIndex;
                    }
                }
            }

            return int.MinValue;
        }

        private static bool MatchesFrameBufferAttachmentResource(
            XRFrameBuffer? frameBuffer,
            string resourceName,
            bool colorAttachment)
        {
            if (frameBuffer?.Targets is not { Length: > 0 } targets ||
                string.IsNullOrWhiteSpace(resourceName))
            {
                return false;
            }

            for (int i = 0; i < targets.Length; i++)
            {
                var (target, attachment, _, _) = targets[i];
                if (target is not XRTexture texture || string.IsNullOrWhiteSpace(texture.Name))
                    continue;

                bool isColorAttachment = attachment >= EFrameBufferAttachment.ColorAttachment0 &&
                    attachment <= EFrameBufferAttachment.ColorAttachment31;
                if (colorAttachment != isColorAttachment)
                    continue;

                if (string.Equals(resourceName, MakeTextureResource(texture.Name), StringComparison.OrdinalIgnoreCase))
                    return true;
            }

            return false;
        }

        private static IReadOnlyList<RenderPassMetadata> GetTopologicalPassOrder(IReadOnlyCollection<RenderPassMetadata> metadata)
            => TopologicalPassOrderCache.GetValue(
                metadata,
                static key => new TopologicalPassOrderCacheEntry(key)).Passes;

        internal override void DescribeRenderPass(RenderGraphDescribeContext context)
        {
            base.DescribeRenderPass(context);
            if (FrameBufferName is not null)
                context.PushRenderTarget(FrameBufferName, Write, ClearColor, ClearDepth, ClearStencil);
        }
    }
}
