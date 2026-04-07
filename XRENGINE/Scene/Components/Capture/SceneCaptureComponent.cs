using System;
using System.Numerics;
using XREngine.Components.Scene.Transforms;
using XREngine.Data.Geometry;
using XREngine.Data.Rendering;
using XREngine.Data.Vectors;
using XREngine.Data.Core;
using XREngine.Data.Transforms.Rotations;
using XREngine.Rendering;
using XREngine.Rendering.OpenGL;
using XREngine.Rendering.Commands;
using XREngine.Rendering.Models.Materials;
using XREngine;
using XREngine.Scene.Transforms;
using YamlDotNet.Serialization;

namespace XREngine.Components.Lights
{
    public class SceneCaptureComponent : SceneCaptureComponentBase
    {
        private uint _colorResolution = Engine.Rendering.Settings.LightProbeResolution;
        public uint Resolution
        {
            get => _colorResolution;
            set => SetField(ref _colorResolution, value);
        }

        private bool _captureDepthCubeMap = Engine.Rendering.Settings.LightProbesCaptureDepth;
        public bool CaptureDepthCubeMap
        {
            get => _captureDepthCubeMap;
            set => SetField(ref _captureDepthCubeMap, value);
        }

        protected XRViewport? XPosVP => Viewports[0];
        protected XRViewport? XNegVP => Viewports[1];
        protected XRViewport? YPosVP => Viewports[2];
        protected XRViewport? YNegVP => Viewports[3];
        protected XRViewport? ZPosVP => Viewports[4];
        protected XRViewport? ZNegVP => Viewports[5];

        [RuntimeOnly]
        [YamlIgnore]
        public XRViewport?[] Viewports { get; } = new XRViewport?[6];

        private static readonly Quaternion[] FaceRotationOffsets =
        [
            XRMath.LookRotation(-Vector3.UnitX, -Vector3.UnitY), // +X
            XRMath.LookRotation( Vector3.UnitX, -Vector3.UnitY), // -X
            XRMath.LookRotation(-Vector3.UnitY,  Vector3.UnitZ), // +Y
            XRMath.LookRotation( Vector3.UnitY, -Vector3.UnitZ), // -Y
            XRMath.LookRotation(-Vector3.UnitZ, -Vector3.UnitY), // +Z
            XRMath.LookRotation( Vector3.UnitZ, -Vector3.UnitY), // -Z
        ];

        [RuntimeOnly]
        protected XRTextureCube? _environmentTextureCubemap;

        [RuntimeOnly]
        protected XRTexture2D? _environmentTextureOctahedral;

        [RuntimeOnly]
        protected XRTextureCube? _environmentDepthTextureCubemap;
        protected XRRenderBuffer? _tempDepth;
        private XRCubeFrameBuffer? _renderFBO;
        private XRQuadFrameBuffer? _octahedralFBO;
        private XRMaterial? _octahedralMaterial;
        private bool _captureResourcesDirty = true;
        private bool _captureResourceInitializationQueued;
        private bool _deferCaptureResourceRefresh;

        private const uint OctahedralResolutionMultiplier = 2u;
        private static XRShader? s_cubemapToOctaShader;
        private static XRShader? s_fullscreenTriVertexShader;

        [RuntimeOnly]
        public XRTextureCube? EnvironmentTextureCubemap
        {
            get => _environmentTextureCubemap;
            set => SetField(ref _environmentTextureCubemap, value);
        }

        [RuntimeOnly]
        public XRTexture2D? EnvironmentTextureOctahedral
        {
            get => _environmentTextureOctahedral;
            private set => SetField(ref _environmentTextureOctahedral, value);
        }
        public XRTextureCube? EnvironmentDepthTextureCubemap => _environmentDepthTextureCubemap;
        protected XRCubeFrameBuffer? RenderFBO => _renderFBO;

        public void SetCaptureResolution(uint colorResolution, bool captureDepth = false)
        {
            _deferCaptureResourceRefresh = true;
            try
            {
                Resolution = colorResolution;
                CaptureDepthCubeMap = captureDepth;
            }
            finally
            {
                _deferCaptureResourceRefresh = false;
            }

            InvalidateCaptureResources();
            EnsureCaptureResourcesInitialized();
        }

        protected virtual bool ShouldInitializeCaptureResourcesOnActivate
            => true;

        protected override void OnComponentActivated()
        {
            base.OnComponentActivated();

            if (ShouldInitializeCaptureResourcesOnActivate)
                EnsureCaptureResourcesInitialized();
        }

        protected override void OnPropertyChanged<T>(string? propName, T prev, T field)
        {
            base.OnPropertyChanged(propName, prev, field);

            switch (propName)
            {
                case nameof(Resolution):
                case nameof(CaptureDepthCubeMap):
                    InvalidateCaptureResources();
                    if (!_deferCaptureResourceRefresh && IsActiveInHierarchy &&
                        (ShouldInitializeCaptureResourcesOnActivate || HasAllocatedCaptureResources()))
                    {
                        EnsureCaptureResourcesInitialized();
                    }

                    break;
            }
        }

        protected void InvalidateCaptureResources()
            => _captureResourcesDirty = true;

        protected void EnsureCaptureResourcesInitialized()
        {
            if (!_captureResourcesDirty && AreCaptureResourcesInitialized())
                return;

            if (!Engine.IsRenderThread)
            {
                if (_captureResourceInitializationQueued)
                    return;

                _captureResourceInitializationQueued = Engine.InvokeOnMainThread(() =>
                {
                    _captureResourceInitializationQueued = false;
                    EnsureCaptureResourcesInitialized();
                }, $"{GetType().Name}.EnsureCaptureResourcesInitialized", executeNowIfAlreadyMainThread: true);
                return;
            }

            _captureResourceInitializationQueued = false;
            InitializeForCapture();
            _captureResourcesDirty = false;
        }

        private bool HasAllocatedCaptureResources()
            => _renderFBO is not null ||
               _environmentTextureCubemap is not null ||
               _environmentTextureOctahedral is not null ||
               Viewports[0] is not null;

        private bool AreCaptureResourcesInitialized()
        {
            if (_renderFBO is null || _environmentTextureCubemap is null)
                return false;

            if (CaptureDepthCubeMap)
            {
                if (_environmentDepthTextureCubemap is null)
                    return false;
            }
            else if (_tempDepth is null)
            {
                return false;
            }

            for (int i = 0; i < Viewports.Length; ++i)
            {
                if (Viewports[i] is null)
                    return false;
            }

            if (ShouldEncodeEnvironmentToOctahedralMap() && (_octahedralFBO is null || _environmentTextureOctahedral is null))
                return false;

            return true;
        }

        protected static void SynchronizeCaptureTextureWrites()
        {
            if (AbstractRenderer.Current is OpenGLRenderer renderer)
            {
                renderer.MemoryBarrier(
                    EMemoryBarrierMask.Framebuffer |
                    EMemoryBarrierMask.TextureFetch |
                    EMemoryBarrierMask.TextureUpdate);
                return;
            }

            AbstractRenderer.Current?.WaitForGpu();
        }

        protected virtual void InitializeForCapture()
        {
            _environmentTextureCubemap?.Destroy();
            _environmentTextureCubemap = CreateEnvironmentColorCubemap(Resolution);
            //_envTex.Generate();

            if (CaptureDepthCubeMap)
            {
                _environmentDepthTextureCubemap?.Destroy();
                _environmentDepthTextureCubemap = new XRTextureCube(Resolution, EPixelInternalFormat.DepthComponent24, EPixelFormat.DepthStencil, EPixelType.UnsignedInt248, false)
                {
                    MinFilter = ETexMinFilter.NearestMipmapLinear,
                    MagFilter = ETexMagFilter.Nearest,
                    UWrap = ETexWrapMode.ClampToEdge,
                    VWrap = ETexWrapMode.ClampToEdge,
                    WWrap = ETexWrapMode.ClampToEdge,
                    Resizable = false,
                    SizedInternalFormat = ESizedInternalFormat.Depth24Stencil8,
                    Name = "SceneCaptureEnvDepth",
                    AutoGenerateMipmaps = false,
                    //FrameBufferAttachment = EFrameBufferAttachment.DepthAttachment,
                };
                //_envDepthTex.Generate();
            }
            else
            {
                _tempDepth = new XRRenderBuffer(Resolution, Resolution, ERenderBufferStorage.Depth24Stencil8);
                //_tempDepth.Generate();
                //_tempDepth.Allocate();
            }

            _renderFBO = new XRCubeFrameBuffer(null);
            //_renderFBO.Generate();

            if (ShouldEncodeEnvironmentToOctahedralMap())
                InitializeOctahedralEncodingResources();
            else
                DisableOctahedralEncodingResources();

            var cameras = XRCubeFrameBuffer.GetCamerasPerFace(0.1f, 10000.0f, true, Transform);
            for (int i = 0; i < cameras.Length; i++)
            {
                XRCamera cam = cameras[i];
                // Exclude gizmos layer from capture so debug visuals don't appear in reflections/probes
                cam.CullingMask = DefaultLayers.EverythingExceptGizmos;
                Viewports[i] = new XRViewport(null, Resolution, Resolution)
                {
                    WorldInstanceOverride = WorldAs<XREngine.Rendering.XRWorldInstance>(),
                    Camera = cam,
                    RenderPipeline = Engine.Rendering.NewRenderPipeline(),
                    SetRenderPipelineFromCamera = false,
                    AutomaticallyCollectVisible = false,
                    AutomaticallySwapBuffers = false,
                    AllowUIRender = false,
                    CullWithFrustum = true,
                };
                var colorStage = cam.GetPostProcessStageState<ColorGradingSettings>();
                if (colorStage?.TryGetBacking(out ColorGradingSettings? grading) == true && grading is not null)
                {
                    grading.AutoExposure = false;
                    grading.Exposure = 1.0f;
                }
                else
                {
                    colorStage?.SetValue(nameof(ColorGradingSettings.AutoExposure), false);
                    colorStage?.SetValue(nameof(ColorGradingSettings.Exposure), 1.0f);
                }
            }

            SyncCaptureCameraTransforms();
        }

        protected virtual XRTextureCube CreateEnvironmentColorCubemap(uint resolution)
            => new(resolution, EPixelInternalFormat.Rgba8, EPixelFormat.Rgba, EPixelType.UnsignedByte, false)
            {
                MinFilter = ETexMinFilter.Linear,
                MagFilter = ETexMagFilter.Linear,
                UWrap = ETexWrapMode.ClampToEdge,
                VWrap = ETexWrapMode.ClampToEdge,
                WWrap = ETexWrapMode.ClampToEdge,
                Resizable = false,
                SizedInternalFormat = ESizedInternalFormat.Rgba8,
                Name = "SceneCaptureEnvColor",
                AutoGenerateMipmaps = false,
            };

        protected virtual bool ShouldEncodeEnvironmentToOctahedralMap()
            => true;

        private bool _progressiveRenderEnabled = true;
        /// <summary>
        /// If true, the SceneCaptureComponent will render one face of the cubemap each time a Render call is made.
        /// </summary>
        public bool ProgressiveRenderEnabled
        {
            get => _progressiveRenderEnabled;
            set => SetField(ref _progressiveRenderEnabled, value);
        }

        private int _currentFace = 0;

        /// <summary>
        /// True after the most recent <see cref="Render"/> call completed a full 6-face cubemap cycle.
        /// Subclasses use this to decide when to run finalization work (e.g. IBL generation).
        /// </summary>
        protected bool LastRenderCompletedCycle { get; private set; }

        private XRViewport? SharedCaptureViewport => XPosVP;

        public override void CollectVisible()
        {
            EnsureCaptureResourcesInitialized();
            if (Viewports[0] is null)
                return;

            if (_progressiveRenderEnabled)
                CollectVisibleFace(_currentFace);
            else
                CollectVisibleShared();
        }

        private void CollectVisibleFace(int i)
            => Viewports[i]?.CollectVisible(false);

        private void CollectVisibleShared()
        {
            XRViewport? viewport = SharedCaptureViewport;
            if (viewport?.ActiveCamera is null || Transform is null)
                return;

            float radius = Math.Max(0.1f, viewport.ActiveCamera.FarZ);
            Sphere collectionVolume = new(Transform.WorldTranslation, radius);
            viewport.CollectVisible(
                collectMirrors: false,
                renderCommandsOverride: null,
                allowScreenSpaceUICollectVisible: false,
                collectionVolumeOverride: collectionVolume);
        }

        public override void SwapBuffers()
        {
            if (_progressiveRenderEnabled)
                SwapBuffersFace(_currentFace);
            else
                SwapBuffersShared();
        }

        private void SwapBuffersFace(int i)
            => Viewports[i]?.SwapBuffers();

        private void SwapBuffersShared()
            => SharedCaptureViewport?.SwapBuffers(allowScreenSpaceUISwap: false);

        /// <summary>
        /// Renders the scene to the ResultTexture cubemap.
        /// </summary>
        public override void Render()
        {
            EnsureCaptureResourcesInitialized();
            if (World is null || RenderFBO is null)
                return;

            Engine.Rendering.State.IsSceneCapturePass = true;

            try
            {
                SyncCaptureCameraTransforms();

                GetDepthParams(out IFrameBufferAttachement depthAttachment, out int[] depthLayers);

                if (_progressiveRenderEnabled)
                {
                    RenderFace(depthAttachment, depthLayers, _currentFace);
                    _currentFace = (_currentFace + 1) % 6;
                }
                else
                {
                    RenderCommandCollection? sharedCommands = SharedCaptureViewport?.RenderPipelineInstance.MeshRenderCommands;
                    for (int i = 0; i < 6; ++i)
                        RenderFace(depthAttachment, depthLayers, i, sharedCommands);
                }

                bool completedCycle = !_progressiveRenderEnabled || _currentFace == 0;
                LastRenderCompletedCycle = completedCycle;

                if (!completedCycle)
                    WorldAs<XREngine.Rendering.XRWorldInstance>()?.Lights?.QueueForCapture(this);

                if (completedCycle && _environmentTextureCubemap is not null)
                {
                    _environmentTextureCubemap.Bind();
                    _environmentTextureCubemap.GenerateMipmapsGPU();
                }

                if (completedCycle && ShouldEncodeEnvironmentToOctahedralMap())
                    EncodeEnvironmentToOctahedralMap();
            }
            finally
            {
                Engine.Rendering.State.IsSceneCapturePass = false;
            }
        }

        protected override void OnTransformRenderWorldMatrixChanged(TransformBase transform, Matrix4x4 renderMatrix)
        {
            base.OnTransformRenderWorldMatrixChanged(transform, renderMatrix);
            SyncCaptureCameraTransforms();
        }

        private void SyncCaptureCameraTransforms()
        {
            TransformBase? probeTransform = Transform;
            if (probeTransform is null)
                return;

            if (Viewports is null || Viewports.Length == 0)
                return;

            for (int i = 0; i < Viewports.Length; ++i)
            {
                var viewport = Viewports[i];
                var camera = viewport?.Camera;
                if (camera?.Transform is not Transform faceTransform)
                    continue;

                if (!ReferenceEquals(faceTransform.Parent, probeTransform))
                    faceTransform.SetParent(probeTransform, false, EParentAssignmentMode.Immediate);

                faceTransform.Translation = Vector3.Zero;
                faceTransform.Rotation = FaceRotationOffsets[i];
                faceTransform.Scale = Vector3.One;
                faceTransform.RecalculateMatrices(true, true);
            }
        }

        private void DisableOctahedralEncodingResources()
        {
            if (_octahedralFBO is not null)
                _octahedralFBO.SettingUniforms -= BindOctahedralSampler;

            _environmentTextureOctahedral?.Destroy();
            EnvironmentTextureOctahedral = null;
        }

        private void InitializeOctahedralEncodingResources()
        {
            if (_environmentTextureCubemap is null)
                return;

            uint extent = GetOctahedralExtent();
            if (extent == 0u)
                return;

            _environmentTextureOctahedral?.Destroy();
            EnvironmentTextureOctahedral = CreateOctahedralTexture(extent);

            if (_octahedralMaterial is null)
            {
                RenderingParameters renderParams = new();
                renderParams.DepthTest.Enabled = ERenderParamUsage.Disabled;
                renderParams.WriteRed = true;
                renderParams.WriteGreen = true;
                renderParams.WriteBlue = true;
                renderParams.WriteAlpha = true;

                _octahedralMaterial = new XRMaterial([_environmentTextureCubemap], GetFullscreenTriVertexShader(), GetCubemapToOctaShader())
                {
                    RenderOptions = renderParams,
                };
                _octahedralFBO = new XRQuadFrameBuffer(_octahedralMaterial);
            }
            else
            {
                _octahedralMaterial.Textures[0] = _environmentTextureCubemap;
            }

            // Always (re)bind the sampler once per init to avoid handler accumulation across recaptures.
            _octahedralFBO!.SettingUniforms -= BindOctahedralSampler;
            _octahedralFBO!.SettingUniforms += BindOctahedralSampler;

            _octahedralFBO.FullScreenMesh.GetDefaultVersion().AllowShaderPipelines = false;
            _octahedralFBO.FullScreenMesh.GetOVRMultiViewVersion().AllowShaderPipelines = false;
            _octahedralFBO.FullScreenMesh.GetNVStereoVersion().AllowShaderPipelines = false;

            _octahedralFBO!.SetRenderTargets((_environmentTextureOctahedral!, EFrameBufferAttachment.ColorAttachment0, 0, -1));
        }

        private void BindOctahedralSampler(XRRenderProgram program)
        {
            if (_environmentTextureCubemap is null)
            {
                Debug.RenderingWarning("SceneCapture: cubemap is null during octa blit!");
                return;
            }

            var cubeTex = _environmentTextureCubemap as XRTextureCube;
            Debug.Rendering($"SceneCapture octa blit: cubemap '{_environmentTextureCubemap.Name}' Extent={cubeTex?.Extent ?? 0}");

            // Get the GL texture object to check if it exists
            var renderer = AbstractRenderer.Current;
            if (renderer != null)
            {
                var glTex = renderer.GetOrCreateAPIRenderObject(_environmentTextureCubemap, generateNow: true);
                if (glTex != null)
                {
                    var handle = glTex.GetHandle();
                    Debug.Rendering($"SceneCapture: GL cubemap handle={handle}, IsGenerated={glTex.IsGenerated}");
                }
                else
                {
                    Debug.RenderingWarning("SceneCapture: Failed to get GL texture object for cubemap!");
                }
            }

            // Force bind the cubemap to texture unit 0 and set the sampler uniform
            _environmentTextureCubemap.Bind();
            program.Sampler("Texture0", _environmentTextureCubemap, 0);
        }

        private uint GetOctahedralExtent()
            => Math.Max(1u, Resolution * OctahedralResolutionMultiplier);

        private static XRTexture2D CreateOctahedralTexture(uint extent)
            => new(extent, extent, EPixelInternalFormat.Rgba16f, EPixelFormat.Rgba, EPixelType.HalfFloat, false)
            {
                MinFilter = ETexMinFilter.Linear,
                MagFilter = ETexMagFilter.Linear,
                UWrap = ETexWrapMode.ClampToEdge,
                VWrap = ETexWrapMode.ClampToEdge,
                Resizable = false,
                SizedInternalFormat = ESizedInternalFormat.Rgba16f,
                Name = "SceneCaptureEnvOcta",
                AutoGenerateMipmaps = false,
            };

        private static XRShader GetCubemapToOctaShader()
            => s_cubemapToOctaShader ??= ShaderHelper.LoadEngineShader("Scene3D\\CubemapToOctahedron.fs", EShaderType.Fragment);

        private static XRShader GetFullscreenTriVertexShader()
            => s_fullscreenTriVertexShader ??= ShaderHelper.LoadEngineShader("Scene3D\\FullscreenTri.vs", EShaderType.Vertex);

        private void EncodeEnvironmentToOctahedralMap()
        {
            if (_octahedralFBO is null || _environmentTextureOctahedral is null || _environmentTextureCubemap is null)
                return;

            // OpenGL only needs an explicit visibility barrier here; a full GPU drain causes large probe stalls.
            SynchronizeCaptureTextureWrites();

            int width = (int)Math.Max(1u, _environmentTextureOctahedral.Width);
            int height = (int)Math.Max(1u, _environmentTextureOctahedral.Height);
            // Guarantee a clean viewport/scissor for the fullscreen blit
            var pipelineState = Engine.Rendering.State.RenderingPipelineState;
            BoundingRectangle previousCrop = pipelineState?.CurrentCropRegion ?? BoundingRectangle.Empty;
            bool hadCrop = previousCrop.Width > 0 && previousCrop.Height > 0;

            using (_octahedralFBO.BindForWritingState())
            {
                AbstractRenderer.Current?.SetCroppingEnabled(false);

                // Ensure the viewport matches the octa target even if no pipeline state is active.
                using StateObject? renderArea = pipelineState?.PushRenderArea(width, height);
                if (renderArea is null)
                    AbstractRenderer.Current?.SetRenderArea(new BoundingRectangle(IVector2.Zero, new IVector2(width, height)));

                // Make sure the cubemap is bound on GL before the blit; avoids relying solely on shader program state when other passes clear bindings.
                _environmentTextureCubemap?.Bind();

                Engine.Rendering.State.ClearByBoundFBO();
                _octahedralFBO.Render(null, true);
            }

            _environmentTextureOctahedral.GenerateMipmapsGPU();

            if (hadCrop)
            {
                AbstractRenderer.Current?.SetCroppingEnabled(true);
                AbstractRenderer.Current?.CropRenderArea(previousCrop);
            }
        }

        private void GetDepthParams(out IFrameBufferAttachement depthAttachment, out int[] depthLayers)
        {
            if (CaptureDepthCubeMap)
            {
                depthAttachment = _environmentDepthTextureCubemap!;
                depthLayers = [0, 1, 2, 3, 4, 5];
            }
            else
            {
                depthAttachment = _tempDepth!;
                depthLayers = [0, 0, 0, 0, 0, 0];
            }
        }

        private void RenderFace(IFrameBufferAttachement depthAttachment, int[] depthLayers, int i, RenderCommandCollection? renderCommandsOverride = null)
        {
            RenderFBO!.SetRenderTargets(
                (_environmentTextureCubemap!, EFrameBufferAttachment.ColorAttachment0, 0, i),
                (depthAttachment, EFrameBufferAttachment.DepthStencilAttachment, 0, depthLayers[i]));

            // After SetRenderTargets triggers attachment + completeness check, skip this face
            // if the FBO is incomplete to avoid driver crashes (e.g. access violation in DrawElementsInstanced).
            if (!RenderFBO.IsLastCheckComplete)
                return;

            XRViewport viewport = Viewports[i]!;
            RenderCommandCollection? previousOverride = viewport.MeshRenderCommandsOverride;
            viewport.MeshRenderCommandsOverride = renderCommandsOverride;
            try
            {
                viewport.Render(RenderFBO, null, null, false, null);
            }
            finally
            {
                viewport.MeshRenderCommandsOverride = previousOverride;
            }
        }

        public void FullCapture(uint colorResolution, bool captureDepth)
        {
            SetCaptureResolution(colorResolution, captureDepth);
            QueueCapture();
        }

        public void QueueCapture()
            => WorldAs<XREngine.Rendering.XRWorldInstance>()?.Lights?.QueueForCapture(this);

        /// <summary>
        /// Executes a single cubemap face capture: collect visible, swap buffers, and render.
        /// Called by the per-frame face-level work queue in <see cref="XREngine.Scene.Lights3DCollection"/>.
        /// </summary>
        public virtual void ExecuteCaptureFace(int faceIndex)
        {
            EnsureCaptureResourcesInitialized();
            if (World is null || RenderFBO is null || Viewports[faceIndex] is null)
                return;

            Engine.Rendering.State.IsSceneCapturePass = true;
            try
            {
                SyncCaptureCameraTransforms();
                Viewports[faceIndex]!.CollectVisible(false);
                Viewports[faceIndex]!.SwapBuffers();
                GetDepthParams(out var depthAttachment, out int[] depthLayers);
                RenderFace(depthAttachment, depthLayers, faceIndex);
            }
            finally
            {
                Engine.Rendering.State.IsSceneCapturePass = false;
            }
        }

        /// <summary>
        /// Finalizes a cubemap capture cycle: generates mipmaps and encodes to octahedral.
        /// Called by the work queue after all 6 faces have been rendered.
        /// Subclasses override to add IBL generation.
        /// </summary>
        public virtual void FinalizeCubemapCapture()
        {
            EnsureCaptureResourcesInitialized();
            if (_environmentTextureCubemap is null)
                return;

            Engine.Rendering.State.IsSceneCapturePass = true;
            try
            {
                _environmentTextureCubemap.Bind();
                _environmentTextureCubemap.GenerateMipmapsGPU();

                if (ShouldEncodeEnvironmentToOctahedralMap())
                    EncodeEnvironmentToOctahedralMap();
            }
            finally
            {
                Engine.Rendering.State.IsSceneCapturePass = false;
            }
        }
    }
}
