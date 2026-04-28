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

        protected static readonly Quaternion[] FaceRotationOffsets =
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

        // Light probes are captured through a single global queue, so one off-screen viewport
        // and pipeline instance can be reused across every probe face.
        private static XRViewport? s_sharedCaptureViewport;

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

            if (SharedCaptureViewport?.Camera is null)
                return false;

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

            XRViewport sharedViewport = GetOrCreateSharedCaptureViewport();
            Viewports[0] = sharedViewport;
            for (int i = 1; i < Viewports.Length; ++i)
                Viewports[i] = null;

            PrepareCaptureViewportForFace(_currentFace);
        }

        private XRViewport GetOrCreateSharedCaptureViewport()
        {
            XRViewport viewport = s_sharedCaptureViewport ??= CreateSharedCaptureViewport();
            ConfigureSharedCaptureViewport(viewport);
            return viewport;
        }

        private static XRViewport CreateSharedCaptureViewport()
        {
            Transform captureTransform = new();
            XRCamera captureCamera = new(captureTransform, new XRPerspectiveCameraParameters(90.0f, 1.0f, 0.1f, 10000.0f));
            XRViewport viewport = new(null, 1u, 1u)
            {
                Camera = captureCamera,
                SetRenderPipelineFromCamera = false,
                AutomaticallyCollectVisible = false,
                AutomaticallySwapBuffers = false,
                AllowUIRender = false,
                CullWithFrustum = true,
            };

            ApplyCaptureCameraSettings(captureCamera);
            return viewport;
        }

        private void ConfigureSharedCaptureViewport(XRViewport viewport)
        {
            viewport.WorldInstanceOverride = WorldAs<XREngine.Rendering.XRWorldInstance>();
            viewport.SetRenderPipelineFromCamera = false;
            viewport.AutomaticallyCollectVisible = false;
            viewport.AutomaticallySwapBuffers = false;
            viewport.AllowUIRender = false;
            viewport.CullWithFrustum = true;

            if ((uint)viewport.Width != Resolution ||
                (uint)viewport.Height != Resolution ||
                viewport.InternalWidth != (int)Resolution ||
                viewport.InternalHeight != (int)Resolution)
            {
                viewport.Resize(Resolution, Resolution);
            }

            if (viewport.Camera is not null)
                ApplyCaptureCameraSettings(viewport.Camera);
        }

        private static void ApplyCaptureCameraSettings(XRCamera camera)
        {
            // Exclude gizmos so debug visuals don't end up inside the captured probe data.
            camera.CullingMask = DefaultLayers.EverythingExceptGizmos;

            var colorStage = camera.GetPostProcessStageState<ColorGradingSettings>();
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

        private XRViewport? SharedCaptureViewport => Viewports[0];

        protected bool TryGetCaptureFaceWorldTransform(int faceIndex, out Vector3 translation, out Quaternion rotation)
        {
            translation = Vector3.Zero;
            rotation = Quaternion.Identity;

            TransformBase? probeTransform = Transform;
            if (probeTransform is null || (uint)faceIndex >= (uint)FaceRotationOffsets.Length)
                return false;

            translation = probeTransform.GetWorldTranslation();
            rotation = Quaternion.Normalize(probeTransform.GetWorldRotation() * FaceRotationOffsets[faceIndex]);
            return true;
        }

        public override void CollectVisible()
        {
            EnsureCaptureResourcesInitialized();
            if (SharedCaptureViewport is null)
                return;

            if (_progressiveRenderEnabled)
                CollectVisibleFace(_currentFace);
            else
                CollectVisibleShared();
        }

        private void CollectVisibleFace(int i)
        {
            XRViewport? viewport = SharedCaptureViewport;
            if (viewport is null)
                return;

            PrepareCaptureViewportForFace(i);
            viewport.CollectVisible(false);
        }

        private void CollectVisibleShared()
        {
            XRViewport? viewport = SharedCaptureViewport;
            if (viewport?.ActiveCamera is null || Transform is null)
                return;

            PrepareCaptureViewportForFace(0);

            float radius = Math.Max(0.1f, viewport.ActiveCamera.FarZ);
            Sphere collectionVolume = new(Transform.GetWorldTranslation(), radius);
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
            => SharedCaptureViewport?.SwapBuffers();

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
                GetDepthParams(out IFrameBufferAttachement depthAttachment, out int[] depthLayers);

                if (_progressiveRenderEnabled)
                {
                    RenderFace(depthAttachment, depthLayers, _currentFace);
                    _currentFace = (_currentFace + 1) % 6;
                }
                else
                {
                    for (int i = 0; i < 6; ++i)
                        RenderFace(depthAttachment, depthLayers, i);
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
            if (SharedCaptureViewport is not null)
                PrepareCaptureViewportForFace(_currentFace);
        }

        private void PrepareCaptureViewportForFace(int faceIndex)
        {
            XRViewport? viewport = SharedCaptureViewport;
            if (viewport?.Camera?.Transform is not Transform captureTransform)
                return;

            ConfigureSharedCaptureViewport(viewport);

            if (!TryGetCaptureFaceWorldTransform(faceIndex, out Vector3 translation, out Quaternion rotation))
                return;

            captureTransform.SetWorldTranslationRotation(translation, rotation);
            captureTransform.Scale = Vector3.One;
            captureTransform.RecalculateMatrices(true, true);
        }

        private void DisableOctahedralEncodingResources()
        {
            if (_octahedralFBO is not null)
                _octahedralFBO.SettingUniforms -= BindOctahedralSampler;

            _environmentTextureOctahedral?.Destroy();
            EnvironmentTextureOctahedral = null;
        }

        protected void ReleaseCapturedEnvironmentTextures(bool releaseCubemap, bool releaseOctahedral)
        {
            if (releaseOctahedral)
            {
                if (_octahedralFBO is not null)
                    _octahedralFBO.SettingUniforms -= BindOctahedralSampler;

                _environmentTextureOctahedral?.Destroy();
                EnvironmentTextureOctahedral = null;
                _octahedralFBO?.Destroy();
                _octahedralFBO = null;
                _octahedralMaterial?.Destroy();
                _octahedralMaterial = null;
            }

            if (releaseCubemap)
            {
                _environmentTextureCubemap?.Destroy();
                EnvironmentTextureCubemap = null;
            }
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
                renderParams.StencilTest.Enabled = ERenderParamUsage.Disabled;
                renderParams.CullMode = ECullMode.None;
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

        private void RenderFace(IFrameBufferAttachement depthAttachment, int[] depthLayers, int i)
        {
            XRViewport? viewport = SharedCaptureViewport;
            if (viewport is null)
                return;

            PrepareCaptureViewportForFace(i);

            RenderFBO!.SetRenderTargets(
                (_environmentTextureCubemap!, EFrameBufferAttachment.ColorAttachment0, 0, i),
                (depthAttachment, EFrameBufferAttachment.DepthStencilAttachment, 0, depthLayers[i]));

            // After SetRenderTargets triggers attachment + completeness check, skip this face
            // if the FBO is incomplete to avoid driver crashes (e.g. access violation in DrawElementsInstanced).
            if (!RenderFBO.IsLastCheckComplete)
                return;

            viewport.Render(RenderFBO, null, null, false, null);
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
            XRViewport? viewport = SharedCaptureViewport;
            if (World is null || RenderFBO is null || viewport is null)
                return;

            Engine.Rendering.State.IsSceneCapturePass = true;
            try
            {
                PrepareCaptureViewportForFace(faceIndex);
                viewport.CollectVisible(false);
                viewport.SwapBuffers();
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
