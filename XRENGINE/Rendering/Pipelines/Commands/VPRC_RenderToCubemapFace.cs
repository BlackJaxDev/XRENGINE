using System.Numerics;
using XREngine.Data.Rendering;
using XREngine.Rendering.Commands;
using XREngine.Scene.Transforms;

namespace XREngine.Rendering.Pipelines.Commands
{
    public class VPRC_RenderToCubemapFace : ViewportRenderCommand
    {
        private XRFrameBuffer? _targetFbo;
        private RenderCommandCollection? _faceCommands;
        private XRCamera[]? _faceCameras;
        private float _cachedNearPlane = -1.0f;
        private float _cachedFarPlane = -1.0f;

        public string CubemapTextureName { get; set; } = "EnvironmentCubemap";
        public string? DepthTextureName { get; set; }
        public int RenderPass { get; set; }
        public ECubemapFace Face { get; set; } = ECubemapFace.PosX;
        public int MipLevel { get; set; }
        public Vector3 Position { get; set; }
        public float NearPlane { get; set; } = 0.1f;
        public float FarPlane { get; set; } = 1000.0f;
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
            var cubemap = instance.GetTexture<XRTextureCube>(CubemapTextureName)
                ?? throw new InvalidOperationException($"Cubemap texture '{CubemapTextureName}' was not found.");

            XRCamera faceCamera = GetFaceCamera(Face);
            int extent = Math.Max(1, (int)(cubemap.Extent >> Math.Max(0, MipLevel)));

            _targetFbo ??= new XRFrameBuffer();
            _targetFbo.SetRenderTargets(BuildTargets(instance, cubemap, Face, MipLevel));

            using var captureScope = VPRCRenderTargetHelpers.PushSceneCapturePass();
            using var bindScope = _targetFbo.BindForWritingState();
            if (ClearColor || ClearDepth || ClearStencil)
                Engine.Rendering.State.ClearByBoundFBO(ClearColor, ClearDepth, ClearStencil);

            RenderCommandCollection commands = _faceCommands ?? instance.MeshRenderCommands;
            VPRCRenderTargetHelpers.RenderPass(instance, commands, faceCamera, RenderPass, extent, extent, GPUDispatch);
        }

        public override void CollectVisible()
        {
            var instance = ActivePipelineInstance;
            RenderCommandCollection collection = VPRCRenderTargetHelpers.EnsureCollection(instance, ref _faceCommands);
            VPRCRenderTargetHelpers.CollectVisible(instance, collection, GetFaceCamera(Face), CullWithFrustum, CollectMirrors);
        }

        public override void SwapBuffers()
            => _faceCommands?.SwapBuffers();

        public void SetOptions(string cubemapTextureName, int renderPass, ECubemapFace face)
        {
            CubemapTextureName = cubemapTextureName;
            RenderPass = renderPass;
            Face = face;
        }

        private XRCamera GetFaceCamera(ECubemapFace face)
        {
            if (_faceCameras is null ||
                !NearPlane.Equals(_cachedNearPlane) ||
                !FarPlane.Equals(_cachedFarPlane))
            {
                _faceCameras = XRCubeFrameBuffer.GetCamerasPerFace(NearPlane, FarPlane, true, null);
                _cachedNearPlane = NearPlane;
                _cachedFarPlane = FarPlane;
            }

            var camera = _faceCameras[(int)face];
            if (camera.Transform is Transform transform)
            {
                transform.SetWorldTranslation(Position);
                transform.RecalculateMatrices(true, true);
            }
            return camera;
        }

        private (IFrameBufferAttachement Target, EFrameBufferAttachment Attachment, int MipLevel, int LayerIndex)[] BuildTargets(
            XRRenderPipelineInstance instance,
            XRTextureCube cubemap,
            ECubemapFace face,
            int mipLevel)
        {
            var targets = new List<(IFrameBufferAttachement Target, EFrameBufferAttachment Attachment, int MipLevel, int LayerIndex)>
            {
                (cubemap, EFrameBufferAttachment.ColorAttachment0, mipLevel, (int)face),
            };

            if (!string.IsNullOrWhiteSpace(DepthTextureName) &&
                instance.TryGetTexture(DepthTextureName, out XRTexture? depthTexture) &&
                depthTexture is IFrameBufferAttachement depthAttachment)
            {
                targets.Add((depthAttachment, EFrameBufferAttachment.DepthStencilAttachment, mipLevel, (int)face));
            }

            return [.. targets];
        }
    }
}