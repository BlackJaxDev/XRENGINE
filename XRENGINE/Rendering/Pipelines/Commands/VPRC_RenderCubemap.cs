using System.Numerics;
using XREngine.Data.Rendering;
using XREngine.Rendering.Commands;
using XREngine.Scene.Transforms;

namespace XREngine.Rendering.Pipelines.Commands
{
    public class VPRC_RenderCubemap : ViewportRenderCommand
    {
        private readonly RenderCommandCollection?[] _faceCommands = new RenderCommandCollection?[6];
        private readonly XRFrameBuffer?[] _targetFbos = new XRFrameBuffer?[6];
        private XRCamera[]? _faceCameras;
        private float _cachedNearPlane = -1.0f;
        private float _cachedFarPlane = -1.0f;

        public string CubemapTextureName { get; set; } = "EnvironmentCubemap";
        public string? DepthTextureName { get; set; }
        public int RenderPass { get; set; }
        public int MipLevel { get; set; }
        public Vector3 Position { get; set; }
        public float NearPlane { get; set; } = 0.1f;
        public float FarPlane { get; set; } = 1000.0f;
        public bool ClearColor { get; set; } = true;
        public bool ClearDepth { get; set; } = true;
        public bool ClearStencil { get; set; } = true;
        public bool CullWithFrustum { get; set; } = true;
        public bool CollectMirrors { get; set; }
        public bool GenerateMipmapsAfterRender { get; set; } = true;
        public bool GPUDispatch { get; set; }

        public override bool NeedsCollecVisible => true;

        protected override void Execute()
        {
            var instance = ActivePipelineInstance;
            var cubemap = instance.GetTexture<XRTextureCube>(CubemapTextureName)
                ?? throw new InvalidOperationException($"Cubemap texture '{CubemapTextureName}' was not found.");

            using var captureScope = VPRCRenderTargetHelpers.PushSceneCapturePass();
            int extent = Math.Max(1, (int)(cubemap.Extent >> Math.Max(0, MipLevel)));

            for (int faceIndex = 0; faceIndex < 6; ++faceIndex)
            {
                var face = (ECubemapFace)faceIndex;
                var fbo = _targetFbos[faceIndex] ??= new XRFrameBuffer();
                fbo.SetRenderTargets(BuildTargets(instance, cubemap, face, MipLevel));

                using var bindScope = fbo.BindForWritingState();
                if (ClearColor || ClearDepth || ClearStencil)
                    Engine.Rendering.State.ClearByBoundFBO(ClearColor, ClearDepth, ClearStencil);

                RenderCommandCollection commands = _faceCommands[faceIndex] ?? instance.MeshRenderCommands;
                VPRCRenderTargetHelpers.RenderPass(instance, commands, GetFaceCamera(face), RenderPass, extent, extent, GPUDispatch);
            }

            if (GenerateMipmapsAfterRender && MipLevel == 0)
                cubemap.GenerateMipmapsGPU();
        }

        public override void CollectVisible()
        {
            var instance = ActivePipelineInstance;
            for (int faceIndex = 0; faceIndex < 6; ++faceIndex)
            {
                var collection = VPRCRenderTargetHelpers.EnsureCollection(instance, ref _faceCommands[faceIndex]);
                VPRCRenderTargetHelpers.CollectVisible(instance, collection, GetFaceCamera((ECubemapFace)faceIndex), CullWithFrustum, CollectMirrors);
            }
        }

        public override void SwapBuffers()
        {
            for (int faceIndex = 0; faceIndex < _faceCommands.Length; ++faceIndex)
                _faceCommands[faceIndex]?.SwapBuffers();
        }

        public void SetOptions(string cubemapTextureName, int renderPass)
        {
            CubemapTextureName = cubemapTextureName;
            RenderPass = renderPass;
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