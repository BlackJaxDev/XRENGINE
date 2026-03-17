using System.Linq;
using System.Numerics;
using XREngine.Components;
using XREngine.Data.Geometry;
using XREngine.Rendering.Commands;
using XREngine.Rendering.UI;
using XREngine.Scene;

namespace XREngine.Rendering;

public sealed partial class XRRenderPipelineInstance
{
    public class RenderingState
    {
        /// <summary>
        /// The viewport being rendered to.
        /// May be null if rendering directly to a framebuffer.
        /// </summary>
        public XRViewport? WindowViewport { get; private set; }
        /// <summary>
        /// The scene being rendered.
        /// </summary>
        public VisualScene? Scene { get; private set; }
        /// <summary>
        /// The camera this render pipeline is rendering the scene through.
        /// </summary>
        public XRCamera? SceneCamera { get; private set; }
        /// <summary>
        /// The right eye camera for stereo rendering.
        /// </summary>
        public XRCamera? StereoRightEyeCamera { get; private set; }
        /// <summary>
        /// The output FBO target for the render pass.
        /// May be null if rendering to the screen.
        /// </summary>
        public XRFrameBuffer? OutputFBO { get; private set; }
        /// <summary>
        /// If this pipeline is rendering a shadow pass.
        /// Shadow passes do not need to execute all rendering commands.
        /// </summary>
        public bool ShadowPass { get; private set; } = false;
        /// <summary>
        /// If this pipeline is rendering a stereo pass.
        /// Stereo passes will inject a geometry shader into each mesh pipeline, or expect the mesh to already have a vertex or geometry shader that supports it.
        /// </summary>
        public bool StereoPass { get; private set; } = false;
        /// <summary>
        /// If set, this material will be used to render all objects in the scene.
        /// Typically used for shadow passes.
        /// </summary>
        public XRMaterial? GlobalMaterialOverride { get; set; }
        /// <summary>
        /// The screen-space UI to render over the scene.
        /// </summary>
        public UICanvasComponent? ScreenSpaceUserInterface { get; private set; }
        /// <summary>
        /// All collected render commands for the current frame.
        /// </summary>
        public RenderCommandCollection? MeshRenderCommands { get; set; }

        //TODO: instead of bools for shadow and stereo passes, use an int for the pass type.

        public StateObject PushMainAttributes(
            XRViewport? viewport,
            VisualScene? scene,
            XRCamera? camera,
            XRCamera? stereoRightEyeCamera,
            XRFrameBuffer? target,
            bool shadowPass,
            bool stereoPass,
            XRMaterial? globalMaterialOverride,
            UICanvasComponent? screenSpaceUI,
            RenderCommandCollection? meshRenderCommands)
        {
            WindowViewport = viewport;
            Scene = scene;
            SceneCamera = camera;
            StereoRightEyeCamera = stereoRightEyeCamera;
            OutputFBO = target;
            ShadowPass = shadowPass;
            StereoPass = stereoPass;
            GlobalMaterialOverride = globalMaterialOverride;
            ScreenSpaceUserInterface = screenSpaceUI?.CanvasTransform?.DrawSpace == ECanvasDrawSpace.Screen ? screenSpaceUI : null;
            MeshRenderCommands = meshRenderCommands;

            if (WindowViewport is not null)
                _renderingViewports.Push(WindowViewport);

            if (Scene is not null)
                _renderingScenes.Push(Scene);

            if (SceneCamera is not null)
                _renderingCameras.Push(SceneCamera);

            return StateObject.New(PopMainAttributes);
        }

        public void PopMainAttributes()
        {
            if (WindowViewport is not null)
                _renderingViewports.Pop();

            if (Scene is not null)
                _renderingScenes.Pop();

            if (SceneCamera is not null)
                _renderingCameras.Pop();

            WindowViewport = null;
            Scene = null;
            SceneCamera = null;
            StereoRightEyeCamera = null;
            OutputFBO = null;
            ShadowPass = false;
            StereoPass = false;
            GlobalMaterialOverride = null;
            ScreenSpaceUserInterface = null;
            MeshRenderCommands = null;
        }

        public XRCamera? RenderingCamera
            => _renderingCameras.TryPeek(out var c) ? c : null;
        private readonly Stack<XRCamera?> _renderingCameras = new();
        public StateObject PushRenderingCamera(XRCamera? camera)
        {
            _renderingCameras.Push(camera);
            return StateObject.New(PopRenderingCamera);
        }
        public void PopRenderingCamera()
            => _renderingCameras.Pop();

        public BoundingRectangle CurrentRenderRegion
            => _renderRegionStack.TryPeek(out var area) ? area : BoundingRectangle.Empty;
        private readonly Stack<BoundingRectangle> _renderRegionStack = new();
        public StateObject PushRenderArea(int width, int height)
            => PushRenderArea(0, 0, width, height);
        public StateObject PushRenderArea(int x, int y, int width, int height)
            => PushRenderArea(new BoundingRectangle(x, y, width, height));
        public StateObject PushRenderArea(BoundingRectangle region)
        {
            _renderRegionStack.Push(region);
            AbstractRenderer.Current?.SetRenderArea(region);
            return StateObject.New(PopRenderArea);
        }
        public void PopRenderArea()
        {
            if (_renderRegionStack.Count <= 0)
                return;

            _renderRegionStack.Pop();
            if (_renderRegionStack.Count > 0)
                AbstractRenderer.Current?.SetRenderArea(_renderRegionStack.Peek());
        }

        public BoundingRectangle CurrentCropRegion
            => _cropRegionStack.TryPeek(out var area) ? area : BoundingRectangle.Empty;
        private readonly Stack<BoundingRectangle> _cropRegionStack = new();
        public StateObject PushCropArea(int width, int height)
            => PushCropArea(0, 0, width, height);
        public StateObject PushCropArea(int x, int y, int width, int height)
            => PushCropArea(new BoundingRectangle(x, y, width, height));
        public StateObject PushCropArea(BoundingRectangle region)
        {
            _cropRegionStack.Push(region);
            AbstractRenderer.Current?.SetCroppingEnabled(true);
            AbstractRenderer.Current?.CropRenderArea(region);
            return StateObject.New(PopCropArea);
        }
        public void PopCropArea()
        {
            if (_cropRegionStack.Count <= 0)
                return;

            _cropRegionStack.Pop();
            if (_cropRegionStack.Count > 0)
                AbstractRenderer.Current?.CropRenderArea(_cropRegionStack.Peek());
            else
                AbstractRenderer.Current?.SetCroppingEnabled(false);
        }

        /// <summary>
        /// This material will be used to render all objects in the scene if set.
        /// </summary>
        public XRMaterial? OverrideMaterial
            => _overrideMaterials.TryPeek(out var m) ? m : null;
        private readonly Stack<XRMaterial> _overrideMaterials = new();
        public StateObject PushOverrideMaterial(XRMaterial material)
        {
            _overrideMaterials.Push(material);
            return StateObject.New(PopOverrideMaterial);
        }
        public void PopOverrideMaterial()
        {
            if (_overrideMaterials.Count > 0)
                _overrideMaterials.Pop();
        }

        public sealed class ScopedTextureBinding
        {
            public required string TextureName { get; init; }
            public required string SamplerName { get; init; }
            public required int TextureUnit { get; init; }

            public void Apply(XRRenderPipelineInstance pipeline, XRRenderProgram program)
            {
                if (string.IsNullOrWhiteSpace(TextureName) || string.IsNullOrWhiteSpace(SamplerName))
                    return;

                if (pipeline.TryGetTexture(TextureName, out XRTexture? texture) && texture is not null)
                    program.Sampler(SamplerName, texture, TextureUnit);
            }
        }

        public sealed class ScopedBufferBinding
        {
            public required string BufferName { get; init; }
            public required uint BindingLocation { get; init; }

            public void Apply(XRRenderPipelineInstance pipeline, XRRenderProgram program)
            {
                if (string.IsNullOrWhiteSpace(BufferName))
                    return;

                if (pipeline.TryGetBuffer(BufferName, out XRDataBuffer? buffer) && buffer is not null)
                    program.BindBuffer(buffer, BindingLocation);
            }
        }

        public sealed class ScopedShaderGlobals
        {
            public Dictionary<string, bool> BoolUniforms { get; } = [];
            public Dictionary<string, int> IntUniforms { get; } = [];
            public Dictionary<string, uint> UIntUniforms { get; } = [];
            public Dictionary<string, float> FloatUniforms { get; } = [];
            public Dictionary<string, Vector2> Vector2Uniforms { get; } = [];
            public Dictionary<string, Vector3> Vector3Uniforms { get; } = [];
            public Dictionary<string, Vector4> Vector4Uniforms { get; } = [];
            public Dictionary<string, Matrix4x4> Matrix4Uniforms { get; } = [];

            public void Apply(XRRenderProgram program)
            {
                foreach (var pair in BoolUniforms)
                    program.Uniform(pair.Key, pair.Value);
                foreach (var pair in IntUniforms)
                    program.Uniform(pair.Key, pair.Value);
                foreach (var pair in UIntUniforms)
                    program.Uniform(pair.Key, pair.Value);
                foreach (var pair in FloatUniforms)
                    program.Uniform(pair.Key, pair.Value);
                foreach (var pair in Vector2Uniforms)
                    program.Uniform(pair.Key, pair.Value);
                foreach (var pair in Vector3Uniforms)
                    program.Uniform(pair.Key, pair.Value);
                foreach (var pair in Vector4Uniforms)
                    program.Uniform(pair.Key, pair.Value);
                foreach (var pair in Matrix4Uniforms)
                    program.Uniform(pair.Key, pair.Value);
            }
        }

        private readonly Stack<ScopedTextureBinding> _textureBindings = new();
        private readonly Stack<ScopedBufferBinding> _bufferBindings = new();
        private readonly Stack<ScopedShaderGlobals> _shaderGlobals = new();

        public StateObject PushTextureBinding(ScopedTextureBinding binding)
        {
            _textureBindings.Push(binding);
            return StateObject.New(PopTextureBinding);
        }

        public void PopTextureBinding()
        {
            if (_textureBindings.Count > 0)
                _textureBindings.Pop();
        }

        public StateObject PushBufferBinding(ScopedBufferBinding binding)
        {
            _bufferBindings.Push(binding);
            return StateObject.New(PopBufferBinding);
        }

        public void PopBufferBinding()
        {
            if (_bufferBindings.Count > 0)
                _bufferBindings.Pop();
        }

        public StateObject PushShaderGlobals(ScopedShaderGlobals globals)
        {
            _shaderGlobals.Push(globals);
            return StateObject.New(PopShaderGlobals);
        }

        public void PopShaderGlobals()
        {
            if (_shaderGlobals.Count > 0)
                _shaderGlobals.Pop();
        }

        public void ApplyScopedProgramBindings(XRRenderProgram program)
        {
            XRRenderPipelineInstance? pipeline = Engine.Rendering.State.CurrentRenderingPipeline;
            if (pipeline is null)
                return;

            pipeline.Variables.Apply(program);

            foreach (var binding in _textureBindings.Reverse())
                binding.Apply(pipeline, program);

            foreach (var binding in _bufferBindings.Reverse())
                binding.Apply(pipeline, program);

            foreach (var globals in _shaderGlobals.Reverse())
                globals.Apply(program);
        }

        /// <summary>
        /// When true, mesh renderers should prefer a cached per-material depth-normal fragment variant
        /// instead of the original forward fragment shader during the depth+normal pre-pass.
        /// </summary>
        public bool UseDepthNormalMaterialVariants { get; private set; }
        private int _useDepthNormalMaterialVariantsDepth;
        public StateObject PushUseDepthNormalMaterialVariants()
        {
            _useDepthNormalMaterialVariantsDepth++;
            UseDepthNormalMaterialVariants = true;
            return StateObject.New(PopUseDepthNormalMaterialVariants);
        }
        private void PopUseDepthNormalMaterialVariants()
        {
            _useDepthNormalMaterialVariantsDepth--;
            if (_useDepthNormalMaterialVariantsDepth <= 0)
            {
                _useDepthNormalMaterialVariantsDepth = 0;
                UseDepthNormalMaterialVariants = false;
            }
        }

        /// <summary>
        /// When true, camera projection matrices should be returned without jitter applied.
        /// Used by motion vectors pass to ensure consistent projections between vertex and fragment stages.
        /// </summary>
        public bool UseUnjitteredProjection { get; private set; }
        private int _unjitteredProjectionDepth;
        public StateObject PushUnjitteredProjection()
        {
            _unjitteredProjectionDepth++;
            UseUnjitteredProjection = true;
            return StateObject.New(PopUnjitteredProjection);
        }
        private void PopUnjitteredProjection()
        {
            _unjitteredProjectionDepth--;
            if (_unjitteredProjectionDepth <= 0)
            {
                _unjitteredProjectionDepth = 0;
                UseUnjitteredProjection = false;
            }
        }

        /// <summary>
        /// When true, shader pipeline mode is forced regardless of global AllowShaderPipelines setting.
        /// This ensures that material overrides (like motion vectors material) work correctly even when
        /// the global setting disables shader pipelines, since combined shader mode ignores overrides.
        /// </summary>
        public bool ForceShaderPipelines { get; private set; }
        private int _forceShaderPipelinesDepth;
        public StateObject PushForceShaderPipelines()
        {
            _forceShaderPipelinesDepth++;
            ForceShaderPipelines = true;
            return StateObject.New(PopForceShaderPipelines);
        }
        private void PopForceShaderPipelines()
        {
            _forceShaderPipelinesDepth--;
            if (_forceShaderPipelinesDepth <= 0)
            {
                _forceShaderPipelinesDepth = 0;
                ForceShaderPipelines = false;
            }
        }

        /// <summary>
        /// When true, mesh renderers should bypass material-specific vertex shaders and use their generated default vertex stage.
        /// This is used by passes like motion vectors that depend on engine-defined varyings such as FragPosLocal.
        /// </summary>
        public bool ForceGeneratedVertexProgram { get; private set; }
        private int _forceGeneratedVertexProgramDepth;
        public StateObject PushForceGeneratedVertexProgram()
        {
            _forceGeneratedVertexProgramDepth++;
            ForceGeneratedVertexProgram = true;
            return StateObject.New(PopForceGeneratedVertexProgram);
        }
        private void PopForceGeneratedVertexProgram()
        {
            _forceGeneratedVertexProgramDepth--;
            if (_forceGeneratedVertexProgramDepth <= 0)
            {
                _forceGeneratedVertexProgramDepth = 0;
                ForceGeneratedVertexProgram = false;
            }
        }

        public IReadOnlyCollection<XRViewport?> ViewportStack => _renderingViewports;

        public XRViewport? RenderingViewport
            => _renderingViewports.TryPeek(out var v) ? v : null;
        private readonly Stack<XRViewport> _renderingViewports = new();
        public StateObject PushViewport(XRViewport viewport)
        {
            _renderingViewports.Push(viewport);
            PushRenderArea(viewport.Region);
            return StateObject.New(PopViewport);
        }
        public void PopViewport()
        {
            _renderingViewports.Pop();
            PopRenderArea();
        }

        public VisualScene? RenderingScene
            => _renderingScenes.TryPeek(out var s) ? s : null;

        private readonly Stack<VisualScene> _renderingScenes = new();
        public StateObject PushRenderingScene(VisualScene scene)
        {
            _renderingScenes.Push(scene);
            return StateObject.New(PopRenderingScene);
        }
        public void PopRenderingScene()
            => _renderingScenes.Pop();

        public StateObject RequestCameraProjectionJitter(Vector2 jitterInTexels)
            => RequestCameraProjectionJitter(jitterInTexels, null);

        public StateObject RequestCameraProjectionJitter(Vector2 jitterInTexels, Vector2? renderResolutionOverride)
        {
            var camera = RenderingCamera;
            if (camera is null)
                return StateObject.New();

            Vector2 resolution = renderResolutionOverride ?? GetActiveRenderResolution();
            return camera.PushProjectionJitter(ProjectionJitterRequest.TexelSpace(jitterInTexels, resolution));
        }

        public StateObject RequestCameraProjectionJitterClipSpace(Vector2 clipSpaceOffset)
        {
            var camera = RenderingCamera;
            if (camera is null)
                return StateObject.New();

            return camera.PushProjectionJitter(ProjectionJitterRequest.ClipSpace(clipSpaceOffset));
        }

        private Vector2 GetActiveRenderResolution()
        {
            BoundingRectangle region = CurrentRenderRegion;
            if (region.Width > 0 && region.Height > 0)
                return new Vector2(region.Width, region.Height);

            var viewport = WindowViewport;
            if (viewport is not null)
            {
                int width = viewport.InternalWidth > 0 ? viewport.InternalWidth : viewport.Width;
                int height = viewport.InternalHeight > 0 ? viewport.InternalHeight : viewport.Height;
                if (width > 0 && height > 0)
                    return new Vector2(width, height);
            }

            return Vector2.One;
        }
    }
}