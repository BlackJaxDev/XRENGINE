using XREngine.Data.Rendering;
using XREngine.Rendering.Commands;

namespace XREngine.Rendering.Pipelines.Commands
{
    [RenderPipelineScriptCommand]
    public class VPRC_RenderToTextureArray : ViewportRenderCommand
    {
        private XRFrameBuffer? _targetFbo;
        private RenderCommandCollection? _sliceCommands;

        public string TextureArrayName { get; set; } = "TextureArray";
        public string? DepthTextureName { get; set; }
        public int RenderPass { get; set; }
        public int LayerIndex { get; set; }
        public int MipLevel { get; set; }
        public XRCamera? CameraOverride { get; set; }
        public bool ClearColor { get; set; } = true;
        public bool ClearDepth { get; set; } = true;
        public bool ClearStencil { get; set; } = true;
        public bool CullWithFrustum { get; set; } = true;
        public bool CollectMirrors { get; set; }
        public bool GPUDispatch { get; set; }

        public override bool NeedsCollecVisible => true;

        protected override void Execute()
        {
            var instance = ActivePipelineInstance;
            var textureArray = instance.GetTexture<XRTexture2DArray>(TextureArrayName)
                ?? throw new InvalidOperationException($"Texture array '{TextureArrayName}' was not found.");

            XRCamera camera = CameraOverride ?? instance.RenderState.SceneCamera
                ?? throw new InvalidOperationException("RenderToTextureArray requires a scene camera or CameraOverride.");

            _targetFbo ??= new XRFrameBuffer();
            _targetFbo.SetRenderTargets(BuildTargets(instance, textureArray));

            using var bindScope = _targetFbo.BindForWritingState();
            if (ClearColor || ClearDepth || ClearStencil)
                Engine.Rendering.State.ClearByBoundFBO(ClearColor, ClearDepth, ClearStencil);

            int width = Math.Max(1, (int)(textureArray.Width >> Math.Max(0, MipLevel)));
            int height = Math.Max(1, (int)(textureArray.Height >> Math.Max(0, MipLevel)));
            RenderCommandCollection commands = CameraOverride is null ? instance.MeshRenderCommands : (_sliceCommands ?? instance.MeshRenderCommands);
            VPRCRenderTargetHelpers.RenderPass(instance, commands, camera, RenderPass, width, height, GPUDispatch);
        }

        public override void CollectVisible()
        {
            if (CameraOverride is null)
                return;

            var instance = ActivePipelineInstance;
            var collection = VPRCRenderTargetHelpers.EnsureCollection(instance, ref _sliceCommands);
            VPRCRenderTargetHelpers.CollectVisible(instance, collection, CameraOverride, CullWithFrustum, CollectMirrors);
        }

        public override void SwapBuffers()
            => _sliceCommands?.SwapBuffers();

        public void SetOptions(string textureArrayName, int renderPass, int layerIndex)
        {
            TextureArrayName = textureArrayName;
            RenderPass = renderPass;
            LayerIndex = layerIndex;
        }

        private (IFrameBufferAttachement Target, EFrameBufferAttachment Attachment, int MipLevel, int LayerIndex)[] BuildTargets(
            XRRenderPipelineInstance instance,
            XRTexture2DArray textureArray)
        {
            var targets = new List<(IFrameBufferAttachement Target, EFrameBufferAttachment Attachment, int MipLevel, int LayerIndex)>
            {
                (textureArray, EFrameBufferAttachment.ColorAttachment0, MipLevel, LayerIndex),
            };

            if (!string.IsNullOrWhiteSpace(DepthTextureName) &&
                instance.TryGetTexture(DepthTextureName, out XRTexture? depthTexture) &&
                depthTexture is IFrameBufferAttachement depthAttachment)
            {
                targets.Add((depthAttachment, EFrameBufferAttachment.DepthStencilAttachment, MipLevel, LayerIndex));
            }

            return [.. targets];
        }
    }
}
