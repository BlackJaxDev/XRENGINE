using Extensions;
using ImageMagick;
using Silk.NET.OpenGL;
using Silk.NET.OpenGL.Extensions.ARB;
using Silk.NET.OpenGL.Extensions.ImGui;
using Silk.NET.OpenGL.Extensions.NV;
using Silk.NET.OpenGL.Extensions.OVR;
using Silk.NET.OpenGLES.Extensions.EXT;
using Silk.NET.OpenGLES.Extensions.NV;
using System;
using System.Numerics;
using XREngine.Data;
using XREngine.Data.Colors;
using XREngine.Data.Geometry;
using XREngine.Data.Rendering;
using XREngine.Rendering.Models.Materials;
using XREngine.Rendering.Models.Materials.Textures;
using XREngine.Rendering.UI;
using XREngine.Rendering.Shaders.Generator;
using PixelFormat = Silk.NET.OpenGL.PixelFormat;
using XREngine.Components;
using System.Runtime.InteropServices;

namespace XREngine.Rendering.OpenGL
{
    public partial class OpenGLRenderer : AbstractRenderer<GL>
    {
        public GL RawGL => Api; // public accessor for underlying GL instance
        public OvrMultiview? OVRMultiView { get; }
        public Silk.NET.OpenGL.Extensions.NV.NVMeshShader? NVMeshShader { get; }
        public Silk.NET.OpenGL.Extensions.NV.NVGpuShader5? NVGpuShader5 { get; }
        public Silk.NET.OpenGL.Extensions.NV.NVPathRendering? NVPathRendering { get; }
        public Silk.NET.OpenGLES.GL ESApi { get; }
        public NVViewportArray? NVViewportArray { get; }
        public ExtMemoryObject? EXTMemoryObject { get; }
        public ExtSemaphore? EXTSemaphore { get; }
        public ExtMemoryObjectWin32? EXTMemoryObjectWin32 { get; }
        public ExtSemaphoreWin32? EXTSemaphoreWin32 { get; }
        public ExtSemaphoreFd? EXTSemaphoreFd { get; }
        public ExtMemoryObjectFd? EXTMemoryObjectFd { get; }
        public NVBindlessMultiDrawIndirectCount? NVBindlessMultiDrawIndirectCount { get; }
        public ArbMultiDrawIndirect? ArbMultiDrawIndirect { get; }

        private static string? _version = null;
        public string? Version
        {
            get
            {
                unsafe
                {
                    _version ??= new((sbyte*)Api.GetString(StringName.Version));
                }
                return _version;
            }
        }
        public OpenGLRenderer(XRWindow window, bool shouldLinkWindow = true) : base(window, shouldLinkWindow)
        {
            var api = Api;
            ESApi = Silk.NET.OpenGLES.GL.GetApi(Window.GLContext);

            EXTMemoryObject = ESApi.TryGetExtension<ExtMemoryObject>(out var ext) ? ext : null;
            EXTSemaphore = ESApi.TryGetExtension<ExtSemaphore>(out var ext2) ? ext2 : null;
            EXTMemoryObjectWin32 = ESApi.TryGetExtension<ExtMemoryObjectWin32>(out var ext3) ? ext3 : null;
            EXTSemaphoreWin32 = ESApi.TryGetExtension<ExtSemaphoreWin32>(out var ext4) ? ext4 : null;
            EXTMemoryObjectFd = ESApi.TryGetExtension<ExtMemoryObjectFd>(out var ext5) ? ext5 : null;
            EXTSemaphoreFd = ESApi.TryGetExtension<ExtSemaphoreFd>(out var ext6) ? ext6 : null;

            OVRMultiView = api.TryGetExtension(out OvrMultiview ext7) ? ext7 : null;
            Engine.Rendering.State.HasOvrMultiViewExtension |= OVRMultiView is not null;
            NVMeshShader = api.TryGetExtension(out Silk.NET.OpenGL.Extensions.NV.NVMeshShader ext8) ? ext8 : null;
            NVGpuShader5 = api.TryGetExtension(out Silk.NET.OpenGL.Extensions.NV.NVGpuShader5 ext9) ? ext9 : null;
            NVViewportArray = ESApi.TryGetExtension(out NVViewportArray ext10) ? ext10 : null;

            NVBindlessMultiDrawIndirectCount = api.TryGetExtension<NVBindlessMultiDrawIndirectCount>(out var ext11) ? ext11 : null;
            ArbMultiDrawIndirect = api.TryGetExtension<ArbMultiDrawIndirect>(out var ext12) ? ext12 : null;
            NVPathRendering = api.TryGetExtension(out Silk.NET.OpenGL.Extensions.NV.NVPathRendering ext13) ? ext13 : null;
        }

        private ImGuiController? _imguiController;
        private OpenGLImGuiBackend? _imguiBackend;

        protected override bool SupportsImGui => true;

        private sealed class OpenGLImGuiBackend(ImGuiController controller) : IImGuiRendererBackend
        {
            private readonly ImGuiController _controller = controller;

            public void MakeCurrent()
                => _controller.MakeCurrent();

            public void Update(float deltaSeconds)
                => _controller.Update(deltaSeconds);

            public void Render()
                => _controller.Render();
        }

        private ImGuiController? GetImGuiController()
        {
            var controller = _imguiController;
            if (controller is not null)
                return controller;

            var input = XRWindow.Input;
            if (input is null)
                return null;

            controller = new ImGuiController(Api, XRWindow.Window, input);
            ImGuiContextTracker.Register(controller.Context);
            _imguiController = controller;
            _imguiBackend = null;
            return controller;
        }

        private OpenGLImGuiBackend? GetOrCreateImGuiBackend()
        {
            var controller = GetImGuiController();
            if (controller is null)
                return null;

            return _imguiBackend ??= new OpenGLImGuiBackend(controller);
        }

        protected override IImGuiRendererBackend? GetImGuiBackend(XRViewport? viewport)
            => GetOrCreateImGuiBackend();

        private static void InitGL(GL api)
        {
            string version;
            unsafe
            {
                version = new((sbyte*)api.GetString(StringName.Version));
                string vendor = new((sbyte*)api.GetString(StringName.Vendor));
                string renderer = new((sbyte*)api.GetString(StringName.Renderer));
                string shadingLanguageVersion = new((sbyte*)api.GetString(StringName.ShadingLanguageVersion));
                Debug.Out($"OpenGL Version: {version}");
                Debug.Out($"OpenGL Vendor: {vendor}");
                Debug.Out($"OpenGL Renderer: {renderer}");
                Debug.Out($"OpenGL Shading Language Version: {shadingLanguageVersion}");

                Engine.Rendering.State.IsNVIDIA = vendor.Contains("NVIDIA");

                // Probe for GL_NV_ray_tracing support early so features can decide whether to attempt the RT path.
                bool hasNvRayTracing = false;
                try
                {
                    int extCount = api.GetInteger(GLEnum.NumExtensions);
                    for (uint i = 0; i < extCount; i++)
                    {
                        string ext = new((sbyte*)api.GetString(StringName.Extensions, i));
                        if (ext == "GL_NV_ray_tracing")
                        {
                            hasNvRayTracing = true;
                            break;
                        }
                    }
                }
                catch (Exception ex)
                {
                    Debug.LogWarning($"Failed to query GL extensions for NV ray tracing: {ex.Message}");
                }

                Engine.Rendering.State.HasNvRayTracing = hasNvRayTracing;
                Debug.Out(EOutputVerbosity.Normal, false, hasNvRayTracing
                    ? "GL_NV_ray_tracing: available"
                    : "GL_NV_ray_tracing: NOT reported; RT path will fall back.");
            }

            GLRenderProgram.ReadBinaryShaderCache(version);

            api.Enable(EnableCap.Multisample);
            api.Enable(EnableCap.TextureCubeMapSeamless);
            api.FrontFace(FrontFaceDirection.Ccw);

            api.ClipControl(GLEnum.LowerLeft, GLEnum.NegativeOneToOne);

            //Fix gamma manually inside of the post process shader
            //api.Enable(EnableCap.FramebufferSrgb);

            api.PixelStore(PixelStoreParameter.PackAlignment, 1);
            api.PixelStore(PixelStoreParameter.UnpackAlignment, 1);
            api.PointSize(1.0f);
            api.LineWidth(1.0f);

            api.UseProgram(0);

            SetupDebug(api);
        }

        public override void MemoryBarrier(EMemoryBarrierMask mask)
        {
            Api.MemoryBarrier(ToGLMask(mask));
        }

        public override void WaitForGpu()
        {
            Api.Finish();
        }

        public override void ColorMask(bool red, bool green, bool blue, bool alpha)
        {
            Api.ColorMask(red, green, blue, alpha);
        }

        private uint ToGLMask(EMemoryBarrierMask mask)
        {
            if (mask.HasFlag(EMemoryBarrierMask.All))
                return uint.MaxValue;

            uint glMask = 0;
            if (mask.HasFlag(EMemoryBarrierMask.VertexAttribArray))
                glMask |= (uint)MemoryBarrierMask.VertexAttribArrayBarrierBit;
            if (mask.HasFlag(EMemoryBarrierMask.ElementArray))
                glMask |= (uint)MemoryBarrierMask.ElementArrayBarrierBit;
            if (mask.HasFlag(EMemoryBarrierMask.Uniform))
                glMask |= (uint)MemoryBarrierMask.UniformBarrierBit;
            if (mask.HasFlag(EMemoryBarrierMask.TextureFetch))
                glMask |= (uint)MemoryBarrierMask.TextureFetchBarrierBit;
            if (mask.HasFlag(EMemoryBarrierMask.ShaderGlobalAccess))
                glMask |= (uint)MemoryBarrierMask.ShaderGlobalAccessBarrierBitNV;
            if (mask.HasFlag(EMemoryBarrierMask.ShaderImageAccess))
                glMask |= (uint)MemoryBarrierMask.ShaderImageAccessBarrierBit;
            if (mask.HasFlag(EMemoryBarrierMask.Command))
                glMask |= (uint)MemoryBarrierMask.CommandBarrierBit;
            if (mask.HasFlag(EMemoryBarrierMask.PixelBuffer))
                glMask |= (uint)MemoryBarrierMask.PixelBufferBarrierBit;
            if (mask.HasFlag(EMemoryBarrierMask.TextureUpdate))
                glMask |= (uint)MemoryBarrierMask.TextureUpdateBarrierBit;
            if (mask.HasFlag(EMemoryBarrierMask.BufferUpdate))
                glMask |= (uint)MemoryBarrierMask.BufferUpdateBarrierBit;
            if (mask.HasFlag(EMemoryBarrierMask.Framebuffer))
                glMask |= (uint)MemoryBarrierMask.FramebufferBarrierBit;
            if (mask.HasFlag(EMemoryBarrierMask.TransformFeedback))
                glMask |= (uint)MemoryBarrierMask.TransformFeedbackBarrierBit;
            if (mask.HasFlag(EMemoryBarrierMask.AtomicCounter))
                glMask |= (uint)MemoryBarrierMask.AtomicCounterBarrierBit;
            if (mask.HasFlag(EMemoryBarrierMask.ShaderStorage))
                glMask |= (uint)MemoryBarrierMask.ShaderStorageBarrierBit;
            if (mask.HasFlag(EMemoryBarrierMask.ClientMappedBuffer))
                glMask |= (uint)MemoryBarrierMask.ClientMappedBufferBarrierBit;
            if (mask.HasFlag(EMemoryBarrierMask.QueryBuffer))
                glMask |= (uint)MemoryBarrierMask.QueryBufferBarrierBit;
            return glMask;
        }

        private unsafe static void SetupDebug(GL api)
        {
            api.Enable(EnableCap.DebugOutput);
            api.Enable(EnableCap.DebugOutputSynchronous);
            api.DebugMessageCallback(DebugCallback, null);
            uint[] ids = [];
            fixed (uint* ptr = ids)
                api.DebugMessageControl(GLEnum.DontCare, GLEnum.DontCare, GLEnum.DontCare, 0, ptr, true);
        }

        private static int[] _ignoredMessageIds =
        [
            131185, //buffer will use video memory
            131204, //no base level, no mipmaps, etc
            131169, //allocated memory for render buffer
            131154, //pixel transfer is synchronized with 3d rendering
            //131216,
            131218,
            131076,
            131139, //Rasterization quality warning: A non-fullscreen clear caused a fallback from CSAA to MSAA.
            131186, //Buffer performance warning: buffer is being copied/moved from video memory to host memory.
            131188, //Buffer usage warning: Analysis of buffer object usage indicates that CPU is consuming buffer object data.  The usage hint supplied with this buffer object, GL_DYNAMIC_COPY, is inconsistent with this usage pattern.  Try using GL_STREAM_READ_ARB, GL_STATIC_READ_ARB, or GL_DYNAMIC_READ_ARB instead.
            //1282,
            //0,
            //9,
        ];
        private static int[] _printMessageIds =
        [
            //1280, //Invalid texture format and type combination
            //1281, //Invalid texture format
            //1282,
        ];

        public unsafe static void DebugCallback(GLEnum source, GLEnum type, int id, GLEnum severity, int length, nint message, nint userParam)
        {
            if (_ignoredMessageIds.IndexOf(id) >= 0)
                return;

            string messageStr = new((sbyte*)message);
            Debug.LogWarning($"OPENGL {FormatSeverity(severity)} #{id} | {FormatSource(source)} {FormatType(type)} | {messageStr}", 1, 5);
            bool shouldTrack = type == GLEnum.DebugTypeError;
            RecordOpenGLError(id, FormatSource(source), FormatType(type), FormatSeverity(severity), messageStr, shouldTrack);
        }

        private static string FormatSeverity(GLEnum severity)
            => severity switch
            {
                GLEnum.DebugSeverityHigh => "High",
                GLEnum.DebugSeverityMedium => "Medium",
                GLEnum.DebugSeverityLow => "Low",
                GLEnum.DebugSeverityNotification => "Notification",
                _ => severity.ToString(),
            };

        private static string FormatType(GLEnum type)
            => type switch
            {
                GLEnum.DebugTypeError => "Error",
                GLEnum.DebugTypeDeprecatedBehavior => "Deprecated Behavior",
                GLEnum.DebugTypeUndefinedBehavior => "Undefined Behavior",
                GLEnum.DebugTypePortability => "Portability",
                GLEnum.DebugTypePerformance => "Performance",
                GLEnum.DebugTypeOther => "Other",
                GLEnum.DebugTypeMarker => "Marker",
                GLEnum.DebugTypePushGroup => "Push Group",
                GLEnum.DebugTypePopGroup => "Pop Group",
                _ => type.ToString(),
            };

        private static string FormatSource(GLEnum source)
            => source switch
            {
                GLEnum.DebugSourceApi => "API",
                GLEnum.DebugSourceWindowSystem => "Window System",
                GLEnum.DebugSourceShaderCompiler => "Shader Compiler",
                GLEnum.DebugSourceThirdParty => "Third Party",
                GLEnum.DebugSourceApplication => "Application",
                GLEnum.DebugSourceOther => "Other",
                _ => source.ToString(),
            };

        public static void CheckError(string? name)
        {
            //if (Current is not OpenGLRenderer renderer)
            //    return;

            //var error = renderer.Api.GetError();
            //if (error != GLEnum.NoError)
            //    Debug.LogWarning(name is null ? error.ToString() : $"{name}: {error}", 1);
        }

        public bool LogGLErrors(string context)
        {
            bool hadError = false;
            GLEnum error;
            while ((error = Api.GetError()) != GLEnum.NoError)
            {
                hadError = true;
                Debug.LogWarning($"OpenGL error after {context}: {error}");
            }

            return hadError;
        }

        protected override AbstractRenderAPIObject CreateAPIRenderObject(GenericRenderObject renderObject)
            => renderObject switch
            {
                //Materials
                XRMaterial data => new GLMaterial(this, data),
                XRShader s => new GLShader(this, s),

                //Meshes
                //"BaseVersion" here is the base class for different mesh renderers necessary for different render paths (like VR or not).
                XRMeshRenderer.BaseVersion data => new GLMeshRenderer(this, data),

                //Programs
                XRRenderProgramPipeline data => new GLRenderProgramPipeline(this, data),
                XRRenderProgram data => new GLRenderProgram(this, data),

                //Buffers
                XRDataBuffer data => new GLDataBuffer(this, data),
                XRDataBufferView data => new GLDataBufferView(this, data),

                //Render Targets
                XRRenderBuffer data => new GLRenderBuffer(this, data),
                XRFrameBuffer data => new GLFrameBuffer(this, data),

                //Texture 1D
                XRTexture1D data => new GLTexture1D(this, data),
                XRTexture1DArray data => new GLTexture1DArray(this, data),
                XRTextureViewBase data => new GLTextureView(this, data),

                //Texture 2D
                XRTexture2D data => new GLTexture2D(this, data),
                XRTexture2DArray data => new GLTexture2DArray(this, data),
                XRTextureRectangle data => new GLTextureRectangle(this, data),

                //Texture 3D
                XRTexture3D data => new GLTexture3D(this, data),

                //Texture Cube
                XRTextureCube data => new GLTextureCube(this, data),
                XRTextureCubeArray data => new GLTextureCubeArray(this, data),

                //Texture Buffer
                XRTextureBuffer data => new GLTextureBuffer(this, data),

                //Samplers
                XRSampler s => new GLSampler(this, s),

                //Feedback
                XRRenderQuery data => new GLRenderQuery(this, data),
                XRTransformFeedback data => new GLTransformFeedback(this, data),

                _ => throw new InvalidOperationException($"Render object type {renderObject.GetType()} is not supported.")
            };

        protected override GL GetAPI()
        {
            var api = GL.GetApi(Window.GLContext);
            InitGL(api);
            return api;
        }

        public override void Initialize()
        {

        }

        public override void CleanUp()
        {
            if (_imguiController is { } controller)
            {
                ImGuiControllerUtilities.DetachInputHandlers(controller);
                ImGuiContextTracker.Unregister(controller.Context);
                controller.Dispose();
            }
            _imguiController = null;
            _imguiBackend = null;
            ResetImGuiFrameMarker();

            // Clean up cached luminance front resources
            if (_luminanceFrontTex != 0)
            {
                Api.DeleteTexture(_luminanceFrontTex);
                _luminanceFrontTex = 0;
            }
            if (_luminanceFrontFbo != 0)
            {
                Api.DeleteFramebuffer(_luminanceFrontFbo);
                _luminanceFrontFbo = 0;
            }
            if (_luminanceFrontPbo != 0)
            {
                Api.DeleteBuffer(_luminanceFrontPbo);
                _luminanceFrontPbo = 0;
            }
            _luminanceFrontPboSize = 0;
            _luminanceFrontTexWidth = 0;
            _luminanceFrontTexHeight = 0;
            _luminanceFrontMipLevels = 0;

            // Clean up compute shader resources
            if (_luminanceResultBuffer != 0)
            {
                Api.DeleteBuffer(_luminanceResultBuffer);
                _luminanceResultBuffer = 0;
            }
            _luminanceResultBufferSize = 0;
            _luminanceComputeProgram?.Destroy();
            _luminanceComputeProgram = null;
            _luminanceComputeInitialized = false;
        }

        protected override void WindowRenderCallback(double delta)
        {

        }

        public override void DispatchCompute(XRRenderProgram program, int numGroupsX, int numGroupsY, int numGroupsZ)
        {
            GLRenderProgram? glProgram = GenericToAPI<GLRenderProgram>(program);
            if (glProgram is null)
                return;

            Api.UseProgram(glProgram.BindingId);
            Api.DispatchCompute((uint)numGroupsX, (uint)numGroupsY, (uint)numGroupsZ);
        }

        public override void AllowDepthWrite(bool allow)
        {
            Api.DepthMask(allow);
        }
        public override void BindFrameBuffer(EFramebufferTarget fboTarget, XRFrameBuffer? fbo)
        {
            Api.BindFramebuffer(GLObjectBase.ToGLEnum(fboTarget), GenericToAPI<GLFrameBuffer>(fbo)?.BindingId ?? 0u);
        }
        public override void Clear(bool color, bool depth, bool stencil)
        {
            uint mask = 0;
            if (color)
                mask |= (uint)GLEnum.ColorBufferBit;
            if (depth)
                mask |= (uint)GLEnum.DepthBufferBit;
            if (stencil)
                mask |= (uint)GLEnum.StencilBufferBit;
            if (mask == 0)
                return;
            Api.Clear(mask);
        }

        public override void ClearColor(ColorF4 color)
        {
            Api.ClearColor(color.R, color.G, color.B, color.A);
        }
        public override void ClearDepth(float depth)
        {
            Api.ClearDepth(depth);
        }
        public override void ClearStencil(int stencil)
        {
            Api.ClearStencil(stencil);
        }
        public override void StencilMask(uint v)
        {
            Api.StencilMask(v);
        }
        public override void DepthFunc(EComparison comparison)
        {
            var comp = comparison switch
            {
                EComparison.Never => GLEnum.Never,
                EComparison.Less => GLEnum.Less,
                EComparison.Equal => GLEnum.Equal,
                EComparison.Lequal => GLEnum.Lequal,
                EComparison.Greater => GLEnum.Greater,
                EComparison.Nequal => GLEnum.Notequal,
                EComparison.Gequal => GLEnum.Gequal,
                EComparison.Always => GLEnum.Always,
                _ => throw new ArgumentOutOfRangeException(nameof(comparison), comparison, null),
            };
            Api.DepthFunc(comp);
        }
        public override void EnableDepthTest(bool enable)
        {
            if (enable)
                Api.Enable(EnableCap.DepthTest);
            else
                Api.Disable(EnableCap.DepthTest);
        }
        public override unsafe byte GetStencilIndex(float x, float y)
        {
            byte stencil = 0;
            Api.ReadPixels((int)x, (int)y, 1, 1, PixelFormat.StencilIndex, PixelType.UnsignedByte, &stencil);
            return stencil;
        }
        public override void SetReadBuffer(EReadBufferMode mode)
        {
            Api.ReadBuffer(ToGLEnum(mode));
        }
        public override void SetReadBuffer(XRFrameBuffer? fbo, EReadBufferMode mode)
        {
            Api.NamedFramebufferReadBuffer(GenericToAPI<GLFrameBuffer>(fbo)?.BindingId ?? 0, ToGLEnum(mode));
        }

        private static GLEnum ToGLEnum(EReadBufferMode mode)
        {
            return mode switch
            {
                EReadBufferMode.None => GLEnum.None,
                EReadBufferMode.Front => GLEnum.Front,
                EReadBufferMode.Back => GLEnum.Back,
                EReadBufferMode.Left => GLEnum.Left,
                EReadBufferMode.Right => GLEnum.Right,
                EReadBufferMode.FrontLeft => GLEnum.FrontLeft,
                EReadBufferMode.FrontRight => GLEnum.FrontRight,
                EReadBufferMode.BackLeft => GLEnum.BackLeft,
                EReadBufferMode.BackRight => GLEnum.BackRight,
                EReadBufferMode.ColorAttachment0 => GLEnum.ColorAttachment0,
                EReadBufferMode.ColorAttachment1 => GLEnum.ColorAttachment1,
                EReadBufferMode.ColorAttachment2 => GLEnum.ColorAttachment2,
                EReadBufferMode.ColorAttachment3 => GLEnum.ColorAttachment3,
                EReadBufferMode.ColorAttachment4 => GLEnum.ColorAttachment4,
                EReadBufferMode.ColorAttachment5 => GLEnum.ColorAttachment5,
                EReadBufferMode.ColorAttachment6 => GLEnum.ColorAttachment6,
                EReadBufferMode.ColorAttachment7 => GLEnum.ColorAttachment7,
                EReadBufferMode.ColorAttachment8 => GLEnum.ColorAttachment8,
                EReadBufferMode.ColorAttachment9 => GLEnum.ColorAttachment9,
                EReadBufferMode.ColorAttachment10 => GLEnum.ColorAttachment10,
                EReadBufferMode.ColorAttachment11 => GLEnum.ColorAttachment11,
                EReadBufferMode.ColorAttachment12 => GLEnum.ColorAttachment12,
                EReadBufferMode.ColorAttachment13 => GLEnum.ColorAttachment13,
                EReadBufferMode.ColorAttachment14 => GLEnum.ColorAttachment14,
                EReadBufferMode.ColorAttachment15 => GLEnum.ColorAttachment15,
                EReadBufferMode.ColorAttachment16 => GLEnum.ColorAttachment16,
                EReadBufferMode.ColorAttachment17 => GLEnum.ColorAttachment17,
                EReadBufferMode.ColorAttachment18 => GLEnum.ColorAttachment18,
                EReadBufferMode.ColorAttachment19 => GLEnum.ColorAttachment19,
                EReadBufferMode.ColorAttachment20 => GLEnum.ColorAttachment20,
                EReadBufferMode.ColorAttachment21 => GLEnum.ColorAttachment21,
                EReadBufferMode.ColorAttachment22 => GLEnum.ColorAttachment22,
                EReadBufferMode.ColorAttachment23 => GLEnum.ColorAttachment23,
                EReadBufferMode.ColorAttachment24 => GLEnum.ColorAttachment24,
                EReadBufferMode.ColorAttachment25 => GLEnum.ColorAttachment25,
                EReadBufferMode.ColorAttachment26 => GLEnum.ColorAttachment26,
                EReadBufferMode.ColorAttachment27 => GLEnum.ColorAttachment27,
                EReadBufferMode.ColorAttachment28 => GLEnum.ColorAttachment28,
                EReadBufferMode.ColorAttachment29 => GLEnum.ColorAttachment29,
                EReadBufferMode.ColorAttachment30 => GLEnum.ColorAttachment30,
                EReadBufferMode.ColorAttachment31 => GLEnum.ColorAttachment31,
                _ => throw new ArgumentOutOfRangeException(nameof(mode), mode, null),
            };
        }

        public override void SetRenderArea(BoundingRectangle region)
            => Api.Viewport(region.X, region.Y, (uint)region.Width, (uint)region.Height);

        public override void CropRenderArea(BoundingRectangle region)
            => Api.Scissor(region.X, region.Y, (uint)region.Width, (uint)region.Height);

        public override void SetCroppingEnabled(bool enabled)
        {
            if (enabled)
                Api.Enable(EnableCap.ScissorTest);
            else
                Api.Disable(EnableCap.ScissorTest);
        }

        public void CheckFrameBufferErrors(GLFrameBuffer fbo)
        {
            var result = Api.CheckFramebufferStatus(FramebufferTarget.Framebuffer);
            string debug = GetFBODebugInfo(fbo, Environment.NewLine);
            string name = fbo.GetDescribingName();
            if (result != GLEnum.FramebufferComplete)
                Debug.LogWarning($"FBO {name} is not complete. Status: {result}{debug}", 0, 20);
            //else
            //    Debug.Out($"FBO {name} is complete.{debug}");
        }

        private static string GetFBODebugInfo(GLFrameBuffer fbo, string splitter)
        {
            string debug = string.Empty;
            if (fbo.Data.Targets is null || fbo.Data.Targets.Length == 0)
            {
                debug += $"{splitter}This FBO has no targets.";
                return debug;
            }

            foreach (var (Target, Attachment, MipLevel, LayerIndex) in fbo.Data.Targets)
            {
                GenericRenderObject? gro = Target as GenericRenderObject;
                bool targetExists = gro is not null;
                string texName = targetExists ? gro!.GetDescribingName() : "<null>";
                debug += $"{splitter}{Attachment}: {texName} Mip{MipLevel}";
                if (LayerIndex >= 0)
                    debug += $" Layer{LayerIndex}";
                if (targetExists)
                    debug += $" / {GetTargetDebugInfo(gro!)}";
            }
            return debug;
        }

        private static string GetTargetDebugInfo(GenericRenderObject gro)
        {
            string debug = string.Empty;
            switch (gro)
            {
                case XRTexture2DView t2dv:
                    debug += $"{t2dv.ViewedTexture.Width}x{t2dv.ViewedTexture.Height} | Viewing {t2dv.ViewedTexture.Name} | internal:{t2dv.InternalFormat}{FormatMipLevels(t2dv.ViewedTexture)}";
                    break;
                case XRTexture2D t2d:
                    debug += $"{t2d.Width}x{t2d.Height}{FormatMipLevels(t2d)}";
                    break;
                case XRRenderBuffer rb:
                    debug += $"{rb.Width}x{rb.Height} | {rb.Type}";
                    break;
                case XRTextureCube tc:
                    debug += $"{tc.MaxDimension}x{tc.MaxDimension}x{tc.MaxDimension}{FormatMipLevels(tc)}";
                    break;
            }
            return debug;
        }

        private static string FormatMipLevels(XRTextureCube tc)
        {
            switch (tc.Mipmaps.Length)
            {
                case 0:
                    return " | No mipmaps";
                case 1:
                    return $" | {FormatMipmap(0, tc.Mipmaps)}";
                default:
                    string mipmaps = $" | {tc.Mipmaps.Length} mipmaps";
                    for (int i = 0; i < tc.Mipmaps.Length; i++)
                        mipmaps += $"{Environment.NewLine}{FormatMipmap(i, tc.Mipmaps)}";
                    return mipmaps;
            }
        }

        private static string FormatMipLevels(XRTexture2D t2d)
        {
            switch (t2d.Mipmaps.Length)
            {
                case 0:
                    return " | No mipmaps";
                case 1:
                    return $" | {FormatMipmap(0, t2d.Mipmaps)}";
                default:
                    string mipmaps = $" | {t2d.Mipmaps.Length} mipmaps";
                    for (int i = 0; i < t2d.Mipmaps.Length; i++)
                        mipmaps += $"{Environment.NewLine}{FormatMipmap(i, t2d.Mipmaps)}";
                    return mipmaps;
            }
        }

        private static string FormatMipmap(int i, CubeMipmap[] mipmaps)
        {
            if (i >= mipmaps.Length)
                return string.Empty;

            CubeMipmap m = mipmaps[i];
            //Format all sides
            string sides = string.Empty;
            for (int j = 0; j < m.Sides.Length; j++)
            {
                Mipmap2D side = m.Sides[j];
                sides += $"{side.Width}x{side.Height} | internal:{side.InternalFormat} | {side.PixelFormat}/{side.PixelType}";
                if (j < m.Sides.Length - 1)
                    sides += Environment.NewLine;
            }
            return $"Mip{i} | {sides}";
        }

        private static string FormatMipmap(int i, XREngine.Rendering.Mipmap2D[] mipmaps)
        {
            if (i >= mipmaps.Length)
                return string.Empty;

            var m = mipmaps[i];
            return $"Mip{i} | {m.Width}x{m.Height} | internal:{m.InternalFormat} | {m.PixelFormat}/{m.PixelType}";
        }

        //public void SetMipmapParameters(uint bindingId, int minLOD, int maxLOD, int largestMipmapLevel, int smallestAllowedMipmapLevel)
        //{
        //    Api.TextureParameterI(bindingId, TextureParameterName.TextureBaseLevel, ref largestMipmapLevel);
        //    Api.TextureParameterI(bindingId, TextureParameterName.TextureMaxLevel, ref smallestAllowedMipmapLevel);
        //    Api.TextureParameterI(bindingId, TextureParameterName.TextureMinLod, ref minLOD);
        //    Api.TextureParameterI(bindingId, TextureParameterName.TextureMaxLod, ref maxLOD);
        //}

        //public void SetMipmapParameters(ETextureTarget target, int minLOD, int maxLOD, int largestMipmapLevel, int smallestAllowedMipmapLevel)
        //{
        //    TextureTarget t = ToTextureTarget(target);
        //    Api.TexParameterI(t, TextureParameterName.TextureBaseLevel, ref largestMipmapLevel);
        //    Api.TexParameterI(t, TextureParameterName.TextureMaxLevel, ref smallestAllowedMipmapLevel);
        //    Api.TexParameterI(t, TextureParameterName.TextureMinLod, ref minLOD);
        //    Api.TexParameterI(t, TextureParameterName.TextureMaxLod, ref maxLOD);
        //}

        public unsafe void ClearTexImage(uint bindingId, int level, ColorF4 color)
        {
            void* addr = color.Address;
            Api.ClearTexImage(bindingId, level, GLEnum.Rgba, GLEnum.Float, addr);
        }

        public unsafe void ClearTexImage(uint bindingId, int level, ColorF3 color)
        {
            void* addr = color.Address;
            Api.ClearTexImage(bindingId, level, GLEnum.Rgb, GLEnum.Float, addr);
        }

        public unsafe void ClearTexImage(uint bindingId, int level, RGBAPixel color)
        {
            void* addr = color.Address;
            Api.ClearTexImage(bindingId, level, GLEnum.Rgba, GLEnum.Byte, addr);
        }

        public static TextureTarget ToTextureTarget(ETextureTarget target)
            => target switch
            {
                ETextureTarget.Texture2D => TextureTarget.Texture2D,
                ETextureTarget.Texture3D => TextureTarget.Texture3D,
                ETextureTarget.TextureCubeMap => TextureTarget.TextureCubeMap,
                _ => TextureTarget.Texture2D
            };

        public override unsafe void CalcDotLuminanceAsync(XRTexture2DArray texture, Action<bool, float> callback, Vector3 luminance, bool genMipmapsNow = true)
        {
            using var prof = Engine.Profiler.Start("GLRenderer.CalcDotLuminanceAsync");

            var glTex = GenericToAPI<GLTexture2DArray>(texture);
            if (glTex is null)
            {
                callback(false, 0.0f);
                return;
            }

            if (genMipmapsNow)
                glTex.GenerateMipmaps();

            int mipLevel = XRTexture.GetSmallestMipmapLevel(texture.Width, texture.Height);
            int layerCount = (int)texture.Depth;
            if (layerCount <= 0)
            {
                callback(false, 0.0f);
                return;
            }

            uint byteSize = (uint)(sizeof(Vector4) * layerCount);
            uint pbo = Api.GenBuffer();
            Api.BindBuffer(GLEnum.PixelPackBuffer, pbo);
            Api.BufferData(GLEnum.PixelPackBuffer, byteSize, null, GLEnum.StreamRead);

            Api.GetTextureSubImage(
                glTex.BindingId,
                mipLevel,
                0, 0, 0,
                1, 1, (uint)layerCount,
                GLObjectBase.ToGLEnum(EPixelFormat.Rgba),
                GLObjectBase.ToGLEnum(EPixelType.Float),
                byteSize,
                (void*)IntPtr.Zero);

            IntPtr sync = Api.FenceSync(GLEnum.SyncGpuCommandsComplete, 0u);
            Api.BindBuffer(GLEnum.PixelPackBuffer, 0);

            bool FenceCheck()
            {
                if (!GetData(byteSize, _rgbDataForAsync(ref _asyncBuffer), sync, pbo))
                    return false;

                Api.DeleteSync(sync);
                Api.DeleteBuffer(pbo);

                Span<Vector4> samples = MemoryMarshal.Cast<byte, Vector4>(_asyncBuffer.AsSpan(0, (int)byteSize));
                Vector3 accum = Vector3.Zero;
                for (int i = 0; i < layerCount; i++)
                {
                    Vector4 s = samples[i];
                    if (float.IsNaN(s.X) || float.IsNaN(s.Y) || float.IsNaN(s.Z))
                    {
                        callback(false, 0.0f);
                        return true;
                    }
                    accum += s.XYZ();
                }

                Vector3 average = accum / layerCount;
                callback(true, average.Dot(luminance));
                return true;
            }

            Engine.AddMainThreadCoroutine(FenceCheck);
        }

        private byte[] _asyncBuffer = XRTexture.AllocateBytes(16, 1, EPixelFormat.Rgba, EPixelType.Float);
        private static byte[] _rgbDataForAsync(ref byte[] buffer)
        {
            if (buffer.Length < 16)
                buffer = XRTexture.AllocateBytes(16, 1, EPixelFormat.Rgba, EPixelType.Float);
            return buffer;
        }

        // Cached resources for CalcDotLuminanceFrontAsync to avoid per-frame allocations
        private uint _luminanceFrontTex;
        private uint _luminanceFrontFbo;
        private uint _luminanceFrontTexWidth;
        private uint _luminanceFrontTexHeight;
        private int _luminanceFrontMipLevels;
        private uint _luminanceFrontPbo;
        private uint _luminanceFrontPboSize;

        // Extension support flags (cached after first check)
        private bool? _hasTextureFilterMinmax;
        private bool HasTextureFilterMinmax => _hasTextureFilterMinmax ??= IsExtensionSupported("GL_ARB_texture_filter_minmax");

        // Compute shader for parallel luminance reduction
        private XRRenderProgram? _luminanceComputeProgram;
        private uint _luminanceResultBuffer;
        private uint _luminanceResultBufferSize;
        private bool _luminanceComputeInitialized;

        // Compute shaders for GPU auto exposure (writes exposure into a 1x1 R32F texture)
        private XRRenderProgram? _autoExposureComputeProgram2D;
        private XRRenderProgram? _autoExposureComputeProgram2DArray;
        private bool _autoExposureComputeInitialized;

        private const string LuminanceComputeShaderSource = @"
#version 460

layout(local_size_x = 16, local_size_y = 16, local_size_z = 1) in;

layout(binding = 0) uniform sampler2D inputTexture;
layout(std430, binding = 0) buffer ResultBuffer {
    vec4 result;
};

uniform ivec2 textureSize;
uniform vec3 luminanceWeights;

shared vec4 sharedAccum[256];

void main() {
    uint localIdx = gl_LocalInvocationID.x + gl_LocalInvocationID.y * 16u;
    ivec2 gid = ivec2(gl_GlobalInvocationID.xy);
    
    vec4 accum = vec4(0.0);
    if (gid.x < textureSize.x && gid.y < textureSize.y) {
        vec2 uv = (vec2(gid) + 0.5) / vec2(textureSize);
        accum = textureLod(inputTexture, uv, 0.0);
    }
    sharedAccum[localIdx] = accum;
    
    barrier();
    
    // Parallel reduction within workgroup
    for (uint stride = 128u; stride > 0u; stride >>= 1u) {
        if (localIdx < stride) {
            sharedAccum[localIdx] += sharedAccum[localIdx + stride];
        }
        barrier();
    }
    
    // First thread in workgroup atomically adds to result
    if (localIdx == 0u) {
        uint pixelCount = uint(textureSize.x * textureSize.y);
        vec4 avg = sharedAccum[0] / float(pixelCount);
        result = avg;
    }
}
";

        private const string AutoExposureComputeShaderSource2D = @"
#version 460

layout(local_size_x = 1, local_size_y = 1, local_size_z = 1) in;

layout(binding = 0) uniform sampler2D SourceTex;
layout(r32f, binding = 0) uniform image2D ExposureOut;

uniform int SmallestMip;
uniform vec3 LuminanceWeights;
uniform float AutoExposureBias;
uniform float AutoExposureScale;
uniform float ExposureDividend;
uniform float MinExposure;
uniform float MaxExposure;
uniform float ExposureTransitionSpeed;

void main()
{
    vec3 rgb = texelFetch(SourceTex, ivec2(0, 0), SmallestMip).rgb;
    float lumDot = dot(rgb, LuminanceWeights);

    float current = imageLoad(ExposureOut, ivec2(0, 0)).r;
    if (isnan(current) || isinf(current))
        current = 0.0;
    float clampedCurrent = clamp(current, MinExposure, MaxExposure);

    if (lumDot <= 0.0)
    {
        imageStore(ExposureOut, ivec2(0, 0), vec4(clampedCurrent, 0.0, 0.0, 0.0));
        return;
    }

    float target = ExposureDividend / lumDot;
    target = AutoExposureBias + AutoExposureScale * target;
    target = clamp(target, MinExposure, MaxExposure);

    float outExposure = (current < MinExposure || current > MaxExposure)
        ? target
        : mix(current, target, clamp(ExposureTransitionSpeed, 0.0, 1.0));

    imageStore(ExposureOut, ivec2(0, 0), vec4(outExposure, 0.0, 0.0, 0.0));
}
";

        private const string AutoExposureComputeShaderSource2DArray = @"
#version 460

layout(local_size_x = 1, local_size_y = 1, local_size_z = 1) in;

layout(binding = 0) uniform sampler2DArray SourceTex;
layout(r32f, binding = 0) uniform image2D ExposureOut;

uniform int SmallestMip;
uniform int LayerCount;
uniform vec3 LuminanceWeights;
uniform float AutoExposureBias;
uniform float AutoExposureScale;
uniform float ExposureDividend;
uniform float MinExposure;
uniform float MaxExposure;
uniform float ExposureTransitionSpeed;

void main()
{
    vec3 rgb = texelFetch(SourceTex, ivec3(0, 0, 0), SmallestMip).rgb;
    if (LayerCount > 1)
    {
        vec3 rgb1 = texelFetch(SourceTex, ivec3(0, 0, 1), SmallestMip).rgb;
        rgb = 0.5 * (rgb + rgb1);
    }

    float lumDot = dot(rgb, LuminanceWeights);

    float current = imageLoad(ExposureOut, ivec2(0, 0)).r;
    if (isnan(current) || isinf(current))
        current = 0.0;
    float clampedCurrent = clamp(current, MinExposure, MaxExposure);

    if (lumDot <= 0.0)
    {
        imageStore(ExposureOut, ivec2(0, 0), vec4(clampedCurrent, 0.0, 0.0, 0.0));
        return;
    }

    float target = ExposureDividend / lumDot;
    target = AutoExposureBias + AutoExposureScale * target;
    target = clamp(target, MinExposure, MaxExposure);

    float outExposure = (current < MinExposure || current > MaxExposure)
        ? target
        : mix(current, target, clamp(ExposureTransitionSpeed, 0.0, 1.0));

    imageStore(ExposureOut, ivec2(0, 0), vec4(outExposure, 0.0, 0.0, 0.0));
}
";

        private void EnsureLuminanceComputeResources()
        {
            if (_luminanceComputeInitialized)
                return;

            try
            {
                var shader = new XRShader(EShaderType.Compute, LuminanceComputeShaderSource);
                _luminanceComputeProgram = new XRRenderProgram(true, false, shader);
                
                // Create result buffer (single vec4)
                _luminanceResultBuffer = Api.GenBuffer();
                _luminanceResultBufferSize = 16; // sizeof(vec4)
                Api.BindBuffer(GLEnum.ShaderStorageBuffer, _luminanceResultBuffer);
                var nullPtr = IntPtr.Zero;
                Api.BufferData(GLEnum.ShaderStorageBuffer, _luminanceResultBufferSize, in nullPtr, GLEnum.DynamicRead);
                Api.BindBuffer(GLEnum.ShaderStorageBuffer, 0);
                
                _luminanceComputeInitialized = true;
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"Failed to initialize luminance compute shader: {ex.Message}");
                _luminanceComputeInitialized = false;
            }
        }

        private void EnsureAutoExposureComputeResources()
        {
            if (_autoExposureComputeInitialized)
                return;

            try
            {
                var shader2D = new XRShader(EShaderType.Compute, AutoExposureComputeShaderSource2D);
                _autoExposureComputeProgram2D = new XRRenderProgram(true, false, shader2D);

                var shader2DArray = new XRShader(EShaderType.Compute, AutoExposureComputeShaderSource2DArray);
                _autoExposureComputeProgram2DArray = new XRRenderProgram(true, false, shader2DArray);

                _autoExposureComputeInitialized = true;
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"Failed to initialize auto exposure compute shaders: {ex.Message}");
                _autoExposureComputeInitialized = false;
            }
        }

        public override bool SupportsGpuAutoExposure => true;

        public override void UpdateAutoExposureGpu(XRTexture sourceTex, XRTexture2D exposureTex, ColorGradingSettings settings, float deltaTime, bool generateMipmapsNow)
        {
            using var prof = Engine.Profiler.Start("GLRenderer.UpdateAutoExposureGpu");

            EnsureAutoExposureComputeResources();
            if (!_autoExposureComputeInitialized)
                return;

            var glExposure = GenericToAPI<GLTexture2D>(exposureTex);
            if (glExposure is null)
                return;

            int smallestMip;
            GLRenderProgram? glProgram;

            uint bindTarget;
            uint sourceBindingId;
            int layerCount = 1;

            if (sourceTex is XRTexture2D source2D)
            {
                var glSource = GenericToAPI<GLTexture2D>(source2D);
                if (glSource is null)
                    return;

                if (generateMipmapsNow)
                    glSource.GenerateMipmaps();

                smallestMip = XRTexture.GetSmallestMipmapLevel(source2D.Width, source2D.Height);
                glProgram = GenericToAPI<GLRenderProgram>(_autoExposureComputeProgram2D);
                bindTarget = (uint)TextureTarget.Texture2D;
                sourceBindingId = glSource.BindingId;
            }
            else if (sourceTex is XRTexture2DArray source2DArray)
            {
                var glSource = GenericToAPI<GLTexture2DArray>(source2DArray);
                if (glSource is null)
                    return;

                if (generateMipmapsNow)
                    glSource.GenerateMipmaps();

                smallestMip = XRTexture.GetSmallestMipmapLevel(source2DArray.Width, source2DArray.Height);
                layerCount = (int)source2DArray.Depth;
                glProgram = GenericToAPI<GLRenderProgram>(_autoExposureComputeProgram2DArray);
                bindTarget = (uint)TextureTarget.Texture2DArray;
                sourceBindingId = glSource.BindingId;
            }
            else
            {
                return;
            }

            if (glProgram is null || !glProgram.IsLinked)
                return;

            Api.UseProgram(glProgram.BindingId);

            glProgram.Uniform("SmallestMip", smallestMip);
            glProgram.Uniform("LuminanceWeights", Engine.Rendering.Settings.DefaultLuminance);
            glProgram.Uniform("AutoExposureBias", settings.AutoExposureBias);
            glProgram.Uniform("AutoExposureScale", settings.AutoExposureScale);
            glProgram.Uniform("ExposureDividend", settings.ExposureDividend);
            glProgram.Uniform("MinExposure", settings.MinExposure);
            glProgram.Uniform("MaxExposure", settings.MaxExposure);

            // Calculate time-based lerp factor
            // alpha = 1 - exp(-speed * dt)
            float alpha = 1.0f - MathF.Exp(-settings.ExposureTransitionSpeed * deltaTime);
            glProgram.Uniform("ExposureTransitionSpeed", alpha);

            if (sourceTex is XRTexture2DArray)
                glProgram.Uniform("LayerCount", layerCount);

            Api.ActiveTexture(GLEnum.Texture0);
            Api.BindTexture((TextureTarget)bindTarget, sourceBindingId);
            glProgram.Uniform("SourceTex", 0);

            // Bind exposure texture as an image for read/write
            Api.BindImageTexture(0, glExposure.BindingId, 0, false, 0, BufferAccessARB.ReadWrite, InternalFormat.R32f);

            Api.DispatchCompute(1, 1, 1);
            Api.MemoryBarrier((uint)(MemoryBarrierMask.ShaderImageAccessBarrierBit | MemoryBarrierMask.TextureFetchBarrierBit));

            // Debug: read back exposure value to verify compute shader is working (once per second approx)
            if ((int)(Engine.ElapsedTime * 10) % 10 == 0)
            {
                float[] exposureData = new float[1];
                Api.GetTextureImage(glExposure.BindingId, 0, PixelFormat.Red, PixelType.Float, (uint)(sizeof(float)), exposureData);
                //Debug.Out($"[AutoExposure] SmallestMip={smallestMip}, Computed exposure={exposureData[0]:F4}, Settings: Bias={settings.AutoExposureBias:F2}, Scale={settings.AutoExposureScale:F2}, Dividend={settings.ExposureDividend:F4}");
            }

            // Ensure that the compute shader write is visible to subsequent reads (by the fragment shader or the next compute dispatch)
            Api.MemoryBarrier(MemoryBarrierMask.ShaderImageAccessBarrierBit | MemoryBarrierMask.TextureFetchBarrierBit);
        }

        public override unsafe void CalcDotLuminanceAsync(XRTexture2D texture, Action<bool, float> callback, Vector3 luminance, bool genMipmapsNow = true)
        {
            using var prof = Engine.Profiler.Start("GLRenderer.CalcDotLuminanceAsync");

            var glTex = GenericToAPI<GLTexture2D>(texture);
            if (glTex is null)
            {
                callback(false, 0.0f);
                return;
            }

            if (genMipmapsNow)
                glTex.GenerateMipmaps();

            int mipLevel = XRTexture.GetSmallestMipmapLevel(texture.Width, texture.Height);

            uint byteSize = (uint)sizeof(Vector4);
            uint pbo = Api.GenBuffer();
            Api.BindBuffer(GLEnum.PixelPackBuffer, pbo);
            Api.BufferData(GLEnum.PixelPackBuffer, byteSize, null, GLEnum.StreamRead);

            Api.GetTextureImage(
                glTex.BindingId,
                mipLevel,
                GLObjectBase.ToGLEnum(EPixelFormat.Rgba),
                GLObjectBase.ToGLEnum(EPixelType.Float),
                byteSize,
                (void*)IntPtr.Zero);

            IntPtr sync = Api.FenceSync(GLEnum.SyncGpuCommandsComplete, 0u);
            Api.BindBuffer(GLEnum.PixelPackBuffer, 0);

            bool FenceCheck()
            {
                if (!GetData(byteSize, _rgbDataForAsync(ref _asyncBuffer), sync, pbo))
                    return false;

                Api.DeleteSync(sync);
                Api.DeleteBuffer(pbo);

                Vector3 rgb;
                unsafe
                {
                    fixed (byte* ptr = _asyncBuffer)
                    {
                        float* fptr = (float*)ptr;
                        rgb = new(fptr[0], fptr[1], fptr[2]);
                    }
                }

                if (float.IsNaN(rgb.X) || float.IsNaN(rgb.Y) || float.IsNaN(rgb.Z))
                {
                    callback(false, 0.0f);
                    return true;
                }

                callback(true, rgb.Dot(luminance));
                return true;
            }

            Engine.AddMainThreadCoroutine(FenceCheck);
        }

        public override unsafe bool CalcDotLuminance(XRTexture2DArray texture, Vector3 luminance, out float dotLuminance, bool genMipmapsNow = true)
        {
            using var prof = Engine.Profiler.Start("GLRenderer.CalcDotLuminance");

            dotLuminance = 1.0f;
            var glTex = GenericToAPI<GLTexture2DArray>(texture);
            if (glTex is null)
                return false;

            if (genMipmapsNow)
                glTex.GenerateMipmaps();

            int layerCount = (int)texture.Depth;
            if (layerCount <= 0)
                return false;

            Span<Vector4> samples = layerCount <= 8
                ? stackalloc Vector4[layerCount]
                : new Vector4[layerCount];

            int mipLevel = XRTexture.GetSmallestMipmapLevel(texture.Width, texture.Height);

            fixed (Vector4* ptr = samples)
            {
                uint byteSize = (uint)(sizeof(Vector4) * layerCount);
                Api.GetTextureImage(
                    glTex.BindingId,
                    mipLevel,
                    GLObjectBase.ToGLEnum(EPixelFormat.Rgba),
                    GLObjectBase.ToGLEnum(EPixelType.Float),
                    byteSize,
                    ptr);
            }

            Vector3 accum = Vector3.Zero;
            for (int i = 0; i < samples.Length; i++)
            {
                Vector4 sample = samples[i];
                if (float.IsNaN(sample.X) || float.IsNaN(sample.Y) || float.IsNaN(sample.Z))
                    return false;

                accum += sample.XYZ();
            }

            Vector3 average = accum / layerCount;
            dotLuminance = average.Dot(luminance);
            return true;
        }
        public override unsafe bool CalcDotLuminance(XRTexture2D texture, Vector3 luminance, out float dotLuminance, bool genMipmapsNow = true)
        {
            using var prof = Engine.Profiler.Start("GLRenderer.CalcDotLuminance");

            dotLuminance = 1.0f;
            var glTex = GenericToAPI<GLTexture2D>(texture);
            if (glTex is null)
                return false;

            //Calculate average color value using 1x1 mipmap of scene
            if (genMipmapsNow)
                glTex.GenerateMipmaps();
            
            int mipLevel = XRTexture.GetSmallestMipmapLevel(texture.Width, texture.Height);

            //Get the average color from the scene texture
            Vector4 rgb = Vector4.Zero;
            void* addr = &rgb;
            Api.GetTextureImage(glTex.BindingId, mipLevel, GLObjectBase.ToGLEnum(EPixelFormat.Rgba), GLObjectBase.ToGLEnum(EPixelType.Float), (uint)sizeof(Vector4), addr);

            if (float.IsNaN(rgb.X) ||
                float.IsNaN(rgb.Y) ||
                float.IsNaN(rgb.Z))
                return false;

            //Calculate luminance factor off of the average color
            dotLuminance = rgb.XYZ().Dot(luminance);
            return true;
        }

        public override unsafe void CalcDotLuminanceFrontAsync(BoundingRectangle region, bool withTransparency, Vector3 luminance, Action<bool, float> callback)
        {
            using var prof = Engine.Profiler.Start("GLRenderer.CalcDotLuminanceFrontAsync");

            uint w = (uint)region.Width;
            uint h = (uint)region.Height;
            if (w == 0 || h == 0)
            {
                callback(false, 0.0f);
                return;
            }

            // Copy the requested front buffer region into a cached FBO-backed texture, generate mipmaps, then read the 1x1 mip via ReadPixels.
            Api.BindFramebuffer(FramebufferTarget.ReadFramebuffer, 0);
            Api.ReadBuffer(ReadBufferMode.Front);

            // Check if we need to reallocate the cached texture/FBO (dimensions changed or not yet allocated)
            int mipLevels = 1 + (int)MathF.Floor(MathF.Log2(MathF.Max(w, h)));
            if (mipLevels < 1)
                mipLevels = 1;

            if (_luminanceFrontTex == 0 || _luminanceFrontTexWidth != w || _luminanceFrontTexHeight != h)
            {
                // Clean up old resources if they exist
                if (_luminanceFrontTex != 0)
                    Api.DeleteTexture(_luminanceFrontTex);
                if (_luminanceFrontFbo != 0)
                    Api.DeleteFramebuffer(_luminanceFrontFbo);
                if (_luminanceFrontPbo != 0)
                    Api.DeleteBuffer(_luminanceFrontPbo);

                // Create new texture with immutable storage
                _luminanceFrontTex = Api.GenTexture();
                Api.BindTexture(TextureTarget.Texture2D, _luminanceFrontTex);
                Api.TexStorage2D(TextureTarget.Texture2D, (uint)mipLevels, GLEnum.Rgba8, w, h);

                // Create FBO and attach texture
                _luminanceFrontFbo = Api.GenFramebuffer();
                Api.BindFramebuffer(FramebufferTarget.DrawFramebuffer, _luminanceFrontFbo);
                Api.FramebufferTexture2D(FramebufferTarget.DrawFramebuffer, FramebufferAttachment.ColorAttachment0, TextureTarget.Texture2D, _luminanceFrontTex, 0);

                // Create cached PBO for readback (4 bytes for RGBA8)
                _luminanceFrontPbo = Api.GenBuffer();
                _luminanceFrontPboSize = 4;
                Api.BindBuffer(GLEnum.PixelPackBuffer, _luminanceFrontPbo);
                var nullPtr = IntPtr.Zero;
                Api.BufferData(GLEnum.PixelPackBuffer, _luminanceFrontPboSize, in nullPtr, GLEnum.StreamRead);
                Api.BindBuffer(GLEnum.PixelPackBuffer, 0);

                _luminanceFrontTexWidth = w;
                _luminanceFrontTexHeight = h;
                _luminanceFrontMipLevels = mipLevels;
            }
            else
            {
                Api.BindTexture(TextureTarget.Texture2D, _luminanceFrontTex);
                Api.BindFramebuffer(FramebufferTarget.DrawFramebuffer, _luminanceFrontFbo);
                // Re-attach mip 0 for the blit target
                Api.FramebufferTexture2D(FramebufferTarget.DrawFramebuffer, FramebufferAttachment.ColorAttachment0, TextureTarget.Texture2D, _luminanceFrontTex, 0);
            }

            Api.BlitFramebuffer(
                region.X, region.Y, region.X + (int)w, region.Y + (int)h,
                0, 0, (int)w, (int)h,
                ClearBufferMask.ColorBufferBit,
                GLEnum.Linear);

            Api.BindTexture(TextureTarget.Texture2D, _luminanceFrontTex);
            Api.GenerateMipmap(TextureTarget.Texture2D);

            int mipLevel = XRTexture.GetSmallestMipmapLevel(w, h);

            // Re-attach the smallest mip for readback.
            Api.BindFramebuffer(FramebufferTarget.ReadFramebuffer, _luminanceFrontFbo);
            Api.FramebufferTexture2D(FramebufferTarget.ReadFramebuffer, FramebufferAttachment.ColorAttachment0, TextureTarget.Texture2D, _luminanceFrontTex, mipLevel);
            Api.ReadBuffer(ReadBufferMode.ColorAttachment0);

            // Use cached PBO for async readback
            uint pbo = _luminanceFrontPbo;
            uint byteSize = _luminanceFrontPboSize;
            Api.BindBuffer(GLEnum.PixelPackBuffer, pbo);

            Api.ReadPixels(0, 0, 1, 1, GLObjectBase.ToGLEnum(EPixelFormat.Rgba), GLObjectBase.ToGLEnum(EPixelType.UnsignedByte), (void*)IntPtr.Zero);

            IntPtr sync = Api.FenceSync(GLEnum.SyncGpuCommandsComplete, 0u);
            Api.BindBuffer(GLEnum.PixelPackBuffer, 0);

            bool FenceCheck()
            {
                if (!GetData(byteSize, _rgbDataForAsync(ref _asyncBuffer), sync, pbo))
                    return false;

                Api.DeleteSync(sync);
                // Note: PBO, texture and FBO are cached and NOT deleted here

                float r = _asyncBuffer[0] / 255.0f;
                float g = _asyncBuffer[1] / 255.0f;
                float b = _asyncBuffer[2] / 255.0f;

                if (float.IsNaN(r) || float.IsNaN(g) || float.IsNaN(b))
                {
                    callback(false, 0.0f);
                    return true;
                }

                callback(true, new Vector3(r, g, b).Dot(luminance));
                return true;
            }

            Engine.AddMainThreadCoroutine(FenceCheck);
        }

        /// <summary>
        /// Calculates average luminance using a compute shader for parallel reduction.
        /// This is an alternative to the mipmap-based approach that can be more efficient for large textures.
        /// </summary>
        public unsafe override void CalcDotLuminanceFrontAsyncCompute(BoundingRectangle region, bool withTransparency, Vector3 luminance, Action<bool, float> callback)
        {
            using var prof = Engine.Profiler.Start("GLRenderer.CalcDotLuminanceFrontAsyncCompute");

            uint w = (uint)region.Width;
            uint h = (uint)region.Height;
            if (w == 0 || h == 0)
            {
                callback(false, 0.0f);
                return;
            }

            EnsureLuminanceComputeResources();
            if (!_luminanceComputeInitialized || _luminanceComputeProgram is null)
            {
                // Fall back to mipmap method
                CalcDotLuminanceFrontAsync(region, withTransparency, luminance, callback);
                return;
            }

            // First, blit front buffer to texture (reuse existing cached texture)
            Api.BindFramebuffer(FramebufferTarget.ReadFramebuffer, 0);
            Api.ReadBuffer(ReadBufferMode.Front);

            int mipLevels = 1; // No mipmaps needed for compute path
            if (_luminanceFrontTex == 0 || _luminanceFrontTexWidth != w || _luminanceFrontTexHeight != h)
            {
                if (_luminanceFrontTex != 0)
                    Api.DeleteTexture(_luminanceFrontTex);
                if (_luminanceFrontFbo != 0)
                    Api.DeleteFramebuffer(_luminanceFrontFbo);

                _luminanceFrontTex = Api.GenTexture();
                Api.BindTexture(TextureTarget.Texture2D, _luminanceFrontTex);
                Api.TexStorage2D(TextureTarget.Texture2D, (uint)mipLevels, GLEnum.Rgba8, w, h);

                _luminanceFrontFbo = Api.GenFramebuffer();
                Api.BindFramebuffer(FramebufferTarget.DrawFramebuffer, _luminanceFrontFbo);
                Api.FramebufferTexture2D(FramebufferTarget.DrawFramebuffer, FramebufferAttachment.ColorAttachment0, TextureTarget.Texture2D, _luminanceFrontTex, 0);

                _luminanceFrontTexWidth = w;
                _luminanceFrontTexHeight = h;
                _luminanceFrontMipLevels = mipLevels;
            }
            else
            {
                Api.BindTexture(TextureTarget.Texture2D, _luminanceFrontTex);
                Api.BindFramebuffer(FramebufferTarget.DrawFramebuffer, _luminanceFrontFbo);
                Api.FramebufferTexture2D(FramebufferTarget.DrawFramebuffer, FramebufferAttachment.ColorAttachment0, TextureTarget.Texture2D, _luminanceFrontTex, 0);
            }

            Api.BlitFramebuffer(
                region.X, region.Y, region.X + (int)w, region.Y + (int)h,
                0, 0, (int)w, (int)h,
                ClearBufferMask.ColorBufferBit,
                GLEnum.Linear);

            // Clear result buffer
            Vector4 zero = Vector4.Zero;
            Api.BindBuffer(GLEnum.ShaderStorageBuffer, _luminanceResultBuffer);
            Api.BufferSubData(GLEnum.ShaderStorageBuffer, IntPtr.Zero, 16, &zero);

            // Bind resources and dispatch compute
            var glProgram = GenericToAPI<GLRenderProgram>(_luminanceComputeProgram);
            if (glProgram is null || !glProgram.IsLinked)
            {
                callback(false, 0.0f);
                return;
            }

            Api.UseProgram(glProgram.BindingId);
            glProgram.Uniform("textureSize", new Data.Vectors.IVector2((int)w, (int)h));
            glProgram.Uniform("luminanceWeights", luminance);

            Api.ActiveTexture(GLEnum.Texture0);
            Api.BindTexture(TextureTarget.Texture2D, _luminanceFrontTex);
            glProgram.Uniform("inputTexture", 0);

            Api.BindBufferBase(GLEnum.ShaderStorageBuffer, 0, _luminanceResultBuffer);

            uint groupsX = (w + 15) / 16;
            uint groupsY = (h + 15) / 16;
            Api.DispatchCompute(groupsX, groupsY, 1);

            Api.MemoryBarrier((uint)MemoryBarrierMask.ShaderStorageBarrierBit);

            // Async readback from result buffer
            IntPtr sync = Api.FenceSync(GLEnum.SyncGpuCommandsComplete, 0u);

            bool FenceCheck()
            {
                var result = Api.ClientWaitSync(sync, 0u, 0u);
                if (!(result == GLEnum.AlreadySignaled || result == GLEnum.ConditionSatisfied))
                    return false;

                Api.DeleteSync(sync);

                Vector4 avg;
                Api.BindBuffer(GLEnum.ShaderStorageBuffer, _luminanceResultBuffer);
                Api.GetBufferSubData(GLEnum.ShaderStorageBuffer, IntPtr.Zero, 16, &avg);
                Api.BindBuffer(GLEnum.ShaderStorageBuffer, 0);

                if (float.IsNaN(avg.X) || float.IsNaN(avg.Y) || float.IsNaN(avg.Z))
                {
                    callback(false, 0.0f);
                    return true;
                }

                callback(true, new Vector3(avg.X, avg.Y, avg.Z).Dot(luminance));
                return true;
            }

            Engine.AddMainThreadCoroutine(FenceCheck);
        }

        /// <summary>
        /// Checks if GL_ARB_texture_filter_minmax extension is available.
        /// This extension provides hardware-accelerated min/max/average filtering.
        /// </summary>
        public bool SupportsTextureFilterMinmax => HasTextureFilterMinmax;

        public override void GetScreenshotAsync(BoundingRectangle region, bool withTransparency, Action<MagickImage, int> imageCallback)
        {
            //TODO: render to an FBO with the desired render size and capture from that, instead of using the window size.

            //TODO: multi-glcontext readback.
            //This method is async on the CPU, but still executes synchronously on the GPU.
            //https://developer.download.nvidia.com/GTC/PDF/GTC2012/PresentationPDF/S0356-GTC2012-Texture-Transfers.pdf

            CaptureFBOColorAttachment(region, withTransparency, imageCallback, 0u, ReadBufferMode.Front, -1, true);
        }

        public void CaptureFBOAttachment(
            BoundingRectangle region,
            bool withTransparency,
            Action<MagickImage, int> imageCallback,
            uint readFBOBindingId,
            EFrameBufferAttachment attachment,
            int layer = -1,
            bool async = true)
        {
            switch (attachment)
            {
                case EFrameBufferAttachment.DepthAttachment:
                    CaptureFBOAttachment(
                        region,
                        imageCallback,
                        readFBOBindingId,
                        ReadBufferMode.None,
                        EPixelFormat.DepthComponent,
                        EPixelType.Float,
                        layer,
                        async);
                    break;
                case EFrameBufferAttachment.StencilAttachment:
                    CaptureFBOAttachment(
                        region,
                        imageCallback,
                        readFBOBindingId,
                        ReadBufferMode.None,
                        EPixelFormat.StencilIndex,
                        EPixelType.UnsignedByte,
                        layer,
                        async);

                    break;
                case EFrameBufferAttachment.DepthStencilAttachment:
                    CaptureFBOAttachment(
                        region,
                        imageCallback,
                        readFBOBindingId,
                        ReadBufferMode.None,
                        EPixelFormat.DepthStencil,
                        EPixelType.UnsignedInt248,
                        layer,
                        async);
                    break;
                default:
                    CaptureFBOColorAttachment(
                        region,
                        withTransparency,
                        imageCallback,
                        readFBOBindingId,
                        GLObjectBase.ToReadBufferMode(attachment),
                        layer,
                        async);
                    break;
            }
        }

        public void CaptureFBOColorAttachment(
            BoundingRectangle region,
            bool withTransparency,
            Action<MagickImage, int> imageCallback,
            uint readFBOBindingId,
            ReadBufferMode readBuffer,
            int layer = -1,
            bool async = true)
        {
            EPixelFormat format = withTransparency ? EPixelFormat.Bgra : EPixelFormat.Bgr;
            EPixelType pixelType = EPixelType.UnsignedByte;
            CaptureFBOAttachment(
                region,
                imageCallback,
                readFBOBindingId,
                readBuffer,
                format,
                pixelType,
                layer,
                async);
        }

        public void CaptureFBOAttachment(
            BoundingRectangle region,
            Action<MagickImage, int> imageCallback,
            uint readFBOBindingId,
            ReadBufferMode readBuffer,
            EPixelFormat format,
            EPixelType pixelType,
            int layer = -1,
            bool async = true)
        {
            //Specify which FBO to read from
            Api.BindFramebuffer(FramebufferTarget.ReadFramebuffer, readFBOBindingId);

            //Specify which attachment buffer to read from
            Api.ReadBuffer(readBuffer);

            CaptureCurrentlyBoundFBOAttachment(region, imageCallback, format, pixelType, async);
        }

        public delegate void DelImageCallback(MagickImage image, int layer, int channelIndex);

        public unsafe void CaptureTexture(
            BoundingRectangle region,
            DelImageCallback imageCallback,
            uint textureBindingId,
            int mipLevel,
            int layer,
            bool async = true)
        {
            uint w = (uint)region.Width;
            uint h = (uint)region.Height;

            Api.GetTextureLevelParameter(textureBindingId, mipLevel, GLEnum.TextureInternalFormat, out int format);
            InternalFormat internalFormat = (InternalFormat)format;
            //int bpp = GetBytesPerPixel(internalFormat);

            Api.GetTextureParameterI(textureBindingId, GLEnum.DepthStencilTextureMode, out int depthStencilMode);
            GLEnum mode = (GLEnum)depthStencilMode;
            
            EPixelFormat pixelFormat = EPixelFormat.Rgba;
            EPixelType pixelType = EPixelType.UnsignedByte;
            switch (internalFormat)
            {
                case InternalFormat.Depth24Stencil8:
                    pixelFormat = EPixelFormat.DepthStencil;
                    pixelType = EPixelType.UnsignedInt248;
                    break;
                case InternalFormat.Depth32fStencil8:
                    pixelFormat = EPixelFormat.DepthStencil;
                    pixelType = EPixelType.Float32UnsignedInt248Rev;
                    break;
            }

            var data = XRTexture.AllocateBytes(w, h, pixelFormat, pixelType);

            if (async)
            {
                uint size = (uint)data.Length;
                uint pbo = ReadTextureToPBO(textureBindingId, region, layer, 1, pixelFormat, pixelType, size, out IntPtr sync);
                bool FenceCheck()
                {
                    if (!GetData(size, data, sync, pbo))
                        return false;
                    else
                    {
                        Api.DeleteSync(sync);
                        Api.DeleteBuffer(pbo);

                        void MakeImage()
                        {
                            if (IsDepthStencilFormat(internalFormat))
                            {
                                switch (mode)
                                {
                                    case GLEnum.StencilIndex:
                                        imageCallback(MakeStencilImage(pixelType, w, h, data), layer, 0);
                                        break;
                                    case GLEnum.DepthComponent:
                                        imageCallback(MakeDepthImage(pixelType, w, h, data), layer, 0);
                                        break;
                                    default:
                                        imageCallback(OpenGLRenderer.MakeImage(pixelFormat, pixelType, w, h, data), layer, 0);
                                        break;
                                }
                            }
                            else
                                imageCallback(OpenGLRenderer.MakeImage(pixelFormat, pixelType, w, h, data), layer, 0);
                        }
                        Task.Run(MakeImage);

                        return true;
                    }
                }
                Engine.AddMainThreadCoroutine(FenceCheck);
            }
            else
            {
                fixed (byte* ptr = data)
                {
                    Api.GetTextureSubImage(textureBindingId, mipLevel, region.X, region.Y, layer, w, h, 1, GLObjectBase.ToGLEnum(pixelFormat), GLObjectBase.ToGLEnum(pixelType), (uint)data.Length, ptr);
                }
                Task.Run(() => imageCallback(XRTexture.NewImage(w, h, pixelFormat, pixelType, data), layer, 0));
            }
        }

        private bool IsDepthStencilFormat(InternalFormat internalFormat) => internalFormat switch
        {
            InternalFormat.Depth24Stencil8 or
            InternalFormat.Depth32fStencil8 or
            InternalFormat.Depth32fStencil8NV => true,
            _ => false,
        };

        public unsafe void CaptureCurrentlyBoundFBOAttachment(
            BoundingRectangle region,
            Action<MagickImage, int> imageCallback,
            EPixelFormat pixelFormat,
            EPixelType pixelType,
            bool async = true)
        {
            uint w = (uint)region.Width;
            uint h = (uint)region.Height;
            var data = XRTexture.AllocateBytes(w, h, pixelFormat, pixelType);

            if (async)
            {
                nuint size = (uint)data.Length;
                uint pbo = ReadFBOToPBO(region, pixelFormat, pixelType, size, out IntPtr sync);
                bool FenceCheck()
                {
                    if (!GetData(size, data, sync, pbo))
                        return false;
                    else
                    {
                        Api.DeleteSync(sync);
                        Api.DeleteBuffer(pbo);

                        void MakeImage()
                        {
                            if (pixelType == EPixelType.Float32UnsignedInt248Rev || pixelType == EPixelType.UnsignedInt248)
                            {
                                MakeDepthStencilImages(pixelType, w, h, data, out MagickImage depth, out MagickImage stencil);
                                imageCallback(depth, 0);
                                imageCallback(stencil, 1);
                            }
                            else
                                imageCallback(OpenGLRenderer.MakeImage(pixelFormat, pixelType, w, h, data), 0);
                        }
                        Task.Run(MakeImage);

                        return true;
                    }
                }
                Engine.AddMainThreadCoroutine(FenceCheck);
            }
            else
            {
                fixed (byte* ptr = data)
                {
                    Api.ReadPixels(region.X, region.Y, w, h, GLObjectBase.ToGLEnum(pixelFormat), GLObjectBase.ToGLEnum(pixelType), ptr);
                }
                Task.Run(() => imageCallback(XRTexture.NewImage(w, h, pixelFormat, pixelType, data), 0));
            }
        }

        private static unsafe MagickImage MakeImage(EPixelFormat format, EPixelType pixelType, uint w, uint h, byte[] data)
            => XRTexture.NewImage(w, h, format, pixelType, data);

        private unsafe void MakeDepthStencilImages(EPixelType pixelType, uint w, uint h, byte[] data, out MagickImage depth, out MagickImage stencil)
        {
            bool floatType = pixelType == EPixelType.Float32UnsignedInt248Rev;
            depth = XRTexture.NewImage(w, h, EPixelFormat.Rgb, EPixelType.UnsignedByte, ExtractDepthData(floatType, data));
            stencil = XRTexture.NewImage(w, h, EPixelFormat.Rgb, EPixelType.UnsignedByte, ExtractStencilData(floatType, data));
        }
        private unsafe MagickImage MakeDepthImage(EPixelType pixelType, uint w, uint h, byte[] data)
        {
            bool floatType = pixelType == EPixelType.Float32UnsignedInt248Rev;
            return XRTexture.NewImage(w, h, EPixelFormat.Rgb, EPixelType.UnsignedByte, ExtractDepthData(floatType, data));
        }
        private unsafe MagickImage MakeStencilImage(EPixelType pixelType, uint w, uint h, byte[] data)
        {
            bool floatType = pixelType == EPixelType.Float32UnsignedInt248Rev;
            return XRTexture.NewImage(w, h, EPixelFormat.Rgb, EPixelType.UnsignedByte, ExtractStencilData(floatType, data));
        }

        private byte[] ExtractStencilData(bool floatingPoint, byte[] data)
        {
            //every 3 bytes is the depth, and the last byte is the stencil
            //we're converting that last byte into grayscale rgb -> 3 bytes with same value
            int bytesPerPixel = floatingPoint ? 8 : 4;
            int stencilOffset = floatingPoint ? 4 : 3;
            int pixelCount = data.Length / bytesPerPixel;
            byte[] newData = new byte[pixelCount * 3];
            Parallel.For(0, pixelCount, i =>
            {
                int index = i * bytesPerPixel;
                int newIndex = i * 3;
                byte stencil = data[index + stencilOffset];
                newData[newIndex] = stencil;
                newData[newIndex + 1] = stencil;
                newData[newIndex + 2] = stencil;
            });
            return newData;
        }

        private byte[] ExtractDepthData(bool floatingPoint, byte[] data)
        {
            //every 3 bytes is the depth, and the last byte is the stencil
            //if float, 4 bytes are used for the depth, a byte for stencil, and 3 bytes to align
            //we're converting that depth value down into a byte and then into grayscale rgb -> 3 bytes with same value
            int bytesPerPixel = floatingPoint ? 8 : 4;
            int pixelCount = data.Length / bytesPerPixel;
            byte[] newData = new byte[pixelCount * 3];
            Parallel.For(0, pixelCount, i =>
            {
                int index = i * bytesPerPixel;
                int newIndex = i * 3;

                float depth = floatingPoint
                    ? BitConverter.Int32BitsToSingle((data[index] << 24) | (data[index + 1] << 16) | data[index + 2] << 8 | data[index + 3])
                    : ((data[index] << 16) | (data[index + 1] << 8) | data[index + 2]) / (float)0xFFFFFF;

                byte compressedDepth = (byte)(depth * 255.0f);

                newData[newIndex] = compressedDepth;
                newData[newIndex + 1] = compressedDepth;
                newData[newIndex + 2] = compressedDepth;
            });
            return newData;
        }

        public override void GetPixelAsync(int x, int y, bool withTransparency, Action<ColorF4> pixelCallback)
        {
            //TODO: render to an FBO with the desired render size and capture from that, instead of using the window size.

            //TODO: multi-glcontext readback.
            //This method is async on the CPU, but still executes synchronously on the GPU.
            //https://developer.download.nvidia.com/GTC/PDF/GTC2012/PresentationPDF/S0356-GTC2012-Texture-Transfers.pdf

            EPixelFormat format = withTransparency ? EPixelFormat.Bgra : EPixelFormat.Bgr;
            EPixelType pixelType = EPixelType.UnsignedByte;
            var data = XRTexture.AllocateBytes(1, 1, format, pixelType);

            Api.BindFramebuffer(FramebufferTarget.ReadFramebuffer, 0);
            Api.ReadBuffer(ReadBufferMode.Front);

            nuint size = (uint)data.Length;
            uint pbo = ReadFBOToPBO(new BoundingRectangle(x, y, 1, 1), format, pixelType, size, out IntPtr sync);
            void FenceCheck()
            {
                if (GetData(size, data, sync, pbo))
                {
                    Api.DeleteSync(sync);
                    Api.DeleteBuffer(pbo);
                    ColorF4 color = new(data[0] / 255.0f, data[1] / 255.0f, data[2] / 255.0f, data[3] / 255.0f);
                    Task.Run(() => pixelCallback(color));
                }
                else
                {
                    Engine.EnqueueMainThreadTask(FenceCheck);
                }
            }
            Engine.EnqueueMainThreadTask(FenceCheck);
        }
        public override unsafe void GetDepthAsync(XRFrameBuffer fbo, int x, int y, Action<float> depthCallback)
        {
            //TODO: render to an FBO with the desired render size and capture from that, instead of using the window size.

            //TODO: multi-glcontext readback.
            //This method is async on the CPU, but still executes synchronously on the GPU.
            //https://developer.download.nvidia.com/GTC/PDF/GTC2012/PresentationPDF/S0356-GTC2012-Texture-Transfers.pdf

            EPixelFormat format = EPixelFormat.DepthComponent;
            EPixelType pixelType = EPixelType.Float;
            var data = XRTexture.AllocateBytes(1, 1, format, pixelType);

            using var t = fbo.BindForReadingState();
            Api.ReadBuffer(ReadBufferMode.None);

            nuint size = (uint)data.Length;
            uint pbo = ReadFBOToPBO(new BoundingRectangle(x, y, 1, 1), format, pixelType, size, out IntPtr sync);
            void FenceCheck()
            {
                if (GetData(size, data, sync, pbo))
                {
                    Api.DeleteSync(sync);
                    Api.DeleteBuffer(pbo);
                    fixed (byte* ptr = data)
                    {
                        float depth = *(float*)ptr;
                        Task.Run(() => depthCallback(depth));
                    }
                }
                else
                {
                    Engine.EnqueueMainThreadTask(FenceCheck);
                }
            }
            Engine.EnqueueMainThreadTask(FenceCheck);
        }

        private unsafe uint ReadFBOToPBO(BoundingRectangle region, EPixelFormat format, EPixelType type, nuint size, out IntPtr sync)
        {
            uint pbo = Api.GenBuffer();
            Api.BindBuffer(GLEnum.PixelPackBuffer, pbo);
            Api.BufferData(GLEnum.PixelPackBuffer, size, null, GLEnum.StreamRead);
            Api.ReadPixels(region.X, region.Y, (uint)region.Width, (uint)region.Height, GLObjectBase.ToGLEnum(format), GLObjectBase.ToGLEnum(type), null);
            sync = Api.FenceSync(GLEnum.SyncGpuCommandsComplete, 0u);
            Api.BindBuffer(GLEnum.PixelPackBuffer, 0);
            return pbo;
        }

        private unsafe uint ReadTextureToPBO(uint textureId, BoundingRectangle region, int layerOffset, uint layerCount, EPixelFormat format, EPixelType type, uint size, out IntPtr sync)
        {
            uint pbo = Api.GenBuffer();
            Api.BindBuffer(GLEnum.PixelPackBuffer, pbo);
            Api.BufferData(GLEnum.PixelPackBuffer, size, null, GLEnum.StreamRead);
            Api.GetTextureSubImage(textureId, 0, region.X, region.Y, layerOffset, (uint)region.Width, (uint)region.Height, layerCount, GLObjectBase.ToGLEnum(format), GLObjectBase.ToGLEnum(type), size, null);
            sync = Api.FenceSync(GLEnum.SyncGpuCommandsComplete, 0u);
            Api.BindBuffer(GLEnum.PixelPackBuffer, 0);
            return pbo;
        }

        private unsafe bool GetData(nuint size, byte[] data, IntPtr sync, uint pbo)
        {
            var result = Api.ClientWaitSync(sync, 0u, 0u);
            if (!(result == GLEnum.AlreadySignaled || result == GLEnum.ConditionSatisfied))
                return false;

            Api.BindBuffer(GLEnum.PixelPackBuffer, pbo);
            fixed (byte* ptr = data)
            {
                Api.GetBufferSubData(GLEnum.PixelPackBuffer, IntPtr.Zero, size, ptr);
            }
            Api.BindBuffer(GLEnum.PixelPackBuffer, 0);

            return true;
        }

        public override unsafe float GetDepth(int x, int y)
        {
            float depth = 0.0f;
            Api.ReadPixels(x, y, 1, 1, PixelFormat.DepthComponent, PixelType.Float, &depth);
            return depth;
        }

        public void DeleteObjects<T>(params T[] objs) where T : GLObjectBase
        {
            if (objs.Length == 0)
                return;

            uint[] bindingIds = new uint[objs.Length];
            bindingIds.Fill(GLObjectBase.InvalidBindingId);

            for (int i = 0; i < objs.Length; ++i)
            {
                var o = objs[i];
                if (!o.IsGenerated)
                    continue;

                o.PreDeleted();
                bindingIds[i] = o.BindingId;
            }

            bindingIds = bindingIds.Where(i => i != GLObjectBase.InvalidBindingId).ToArray();
            EGLObjectType type = objs[0].Type;
            uint len = (uint)bindingIds.Length;
            switch (type)
            {
                case EGLObjectType.Buffer:
                    Api.DeleteBuffers(len, bindingIds);
                    break;
                case EGLObjectType.Framebuffer:
                    Api.DeleteFramebuffers(len, bindingIds);
                    break;
                case EGLObjectType.Program:
                    foreach (var i in objs)
                        Api.DeleteProgram(i.BindingId);
                    break;
                case EGLObjectType.ProgramPipeline:
                    Api.DeleteProgramPipelines(len, bindingIds);
                    break;
                case EGLObjectType.Query:
                    Api.DeleteQueries(len, bindingIds);
                    break;
                case EGLObjectType.Renderbuffer:
                    Api.DeleteRenderbuffers(len, bindingIds);
                    break;
                case EGLObjectType.Sampler:
                    Api.DeleteSamplers(len, bindingIds);
                    break;
                case EGLObjectType.Texture:
                    Api.DeleteTextures(len, bindingIds);
                    break;
                case EGLObjectType.TransformFeedback:
                    Api.DeleteTransformFeedbacks(len, bindingIds);
                    break;
                case EGLObjectType.VertexArray:
                    Api.DeleteVertexArrays(len, bindingIds);
                    break;
                case EGLObjectType.Shader:
                    foreach (uint i in bindingIds)
                        Api.DeleteShader(i);
                    break;
            }

            foreach (var o in objs)
            {
                if (Array.IndexOf(bindingIds, o._bindingId) < 0)
                    continue;

                o._bindingId = null;
                o.PostDeleted();
            }
        }

        public uint[] CreateObjects(EGLObjectType type, uint count)
        {
            uint[] ids = new uint[count];
            switch (type)
            {
                case EGLObjectType.Buffer:
                    Api.CreateBuffers(count, ids);
                    break;
                case EGLObjectType.Framebuffer:
                    Api.CreateFramebuffers(count, ids);
                    break;
                case EGLObjectType.Program:
                    for (int i = 0; i < count; ++i)
                        ids[i] = Api.CreateProgram();
                    break;
                case EGLObjectType.ProgramPipeline:
                    Api.CreateProgramPipelines(count, ids);
                    break;
                case EGLObjectType.Query:
                    //throw new InvalidOperationException("Call CreateQueries instead.");
                    Api.GenQueries(count, ids);
                    break;
                case EGLObjectType.Renderbuffer:
                    Api.CreateRenderbuffers(count, ids);
                    break;
                case EGLObjectType.Sampler:
                    Api.CreateSamplers(count, ids);
                    break;
                case EGLObjectType.Texture:
                    //throw new InvalidOperationException("Call CreateTextures instead.");
                    Api.GenTextures(count, ids);
                    break;
                case EGLObjectType.TransformFeedback:
                    Api.CreateTransformFeedbacks(count, ids);
                    break;
                case EGLObjectType.VertexArray:
                    Api.CreateVertexArrays(count, ids);
                    break;
                case EGLObjectType.Shader:
                    //for (int i = 0; i < count; ++i)
                    //    ids[i] = Api.CreateShader(CurrentShaderMode);
                    break;
            }
            return ids;
        }

        //public T[] CreateObjects<T>(uint count) where T : GLObjectBase, new()
        //    => CreateObjects(TypeFor<T>(), count).Select(i => (T)Activator.CreateInstance(typeof(T), this, i)!).ToArray();

        private static EGLObjectType TypeFor<T>() where T : GLObjectBase, new()
            => typeof(T) switch
            {
                Type t when typeof(GLDataBuffer).IsAssignableFrom(t)
                    => EGLObjectType.Buffer,

                Type t when typeof(GLShader).IsAssignableFrom(t)
                    => EGLObjectType.Shader,

                Type t when typeof(GLRenderProgram).IsAssignableFrom(t)
                    => EGLObjectType.Program,

                Type t when typeof(GLMeshRenderer).IsAssignableFrom(t)
                    => EGLObjectType.VertexArray,

                Type t when typeof(GLRenderQuery).IsAssignableFrom(t)
                    => EGLObjectType.Query,

                Type t when typeof(GLRenderProgramPipeline).IsAssignableFrom(t)
                    => EGLObjectType.ProgramPipeline,

                Type t when typeof(GLTransformFeedback).IsAssignableFrom(t)
                    => EGLObjectType.TransformFeedback,

                Type t when typeof(GLSampler).IsAssignableFrom(t)
                    => EGLObjectType.Sampler,

                Type t when typeof(IGLTexture).IsAssignableFrom(t)
                    => EGLObjectType.Texture,

                Type t when typeof(GLRenderBuffer).IsAssignableFrom(t)
                    => EGLObjectType.Renderbuffer,

                Type t when typeof(GLFrameBuffer).IsAssignableFrom(t)
                    => EGLObjectType.Framebuffer,

                Type t when typeof(GLMaterial).IsAssignableFrom(t)
                    => EGLObjectType.Material,
                _ => throw new InvalidOperationException($"Type {typeof(T)} is not a valid GLObjectBase type."),
            };

        public uint CreateMemoryObject()
            => EXTMemoryObject?.CreateMemoryObject() ?? 0;

        public uint CreateSemaphore()
            => EXTSemaphore?.GenSemaphore() ?? 0;

        public IntPtr GetMemoryObjectHandle(uint memoryObject)
        {
            if (EXTMemoryObject is null)
                return IntPtr.Zero;
            EXTMemoryObject.GetMemoryObjectParameter(memoryObject, EXT.HandleTypeOpaqueWin32Ext, out int handle);
            return (IntPtr)handle;
        }

        public IntPtr GetSemaphoreHandle(uint semaphore)
        {
            if (EXTSemaphore is null)
                return IntPtr.Zero;
            EXTSemaphore.GetSemaphoreParameter(semaphore, EXT.HandleTypeOpaqueWin32Ext, out ulong handle);
            return (IntPtr)handle;
        }

        public unsafe void SetMemoryObjectHandle(uint memoryObject, void* memoryObjectHandle)
            => EXTMemoryObjectWin32?.ImportMemoryWin32Handle(memoryObject, 0, EXT.HandleTypeOpaqueWin32Ext, memoryObjectHandle);

        public unsafe void SetSemaphoreHandle(uint semaphore, void* semaphoreHandle)
            => EXTSemaphoreWin32?.ImportSemaphoreWin32Handle(semaphore, EXT.HandleTypeOpaqueWin32Ext, semaphoreHandle);

        public override void Blit(
            XRFrameBuffer? inFBO,
            XRFrameBuffer? outFBO,
            int inX, int inY, uint inW, uint inH,
            int outX, int outY, uint outW, uint outH,
            EReadBufferMode readBufferMode,
            bool colorBit, bool depthBit, bool stencilBit,
            bool linearFilter)
        {
            ClearBufferMask mask = 0;
            if (colorBit)
                mask |= ClearBufferMask.ColorBufferBit;
            if (depthBit)
                mask |= ClearBufferMask.DepthBufferBit;
            if (stencilBit)
                mask |= ClearBufferMask.StencilBufferBit;

            var glIn = GenericToAPI<GLFrameBuffer>(inFBO);
            var glOut = GenericToAPI<GLFrameBuffer>(outFBO);
            var inID = glIn?.BindingId ?? 0u;
            var outID = glOut?.BindingId ?? 0u;

            Api.NamedFramebufferReadBuffer(inID, ToGLEnum(readBufferMode));
            Api.BlitNamedFramebuffer(
                inID,
                outID,
                inX,
                inY,
                inX + (int)inW,
                inY + (int)inH,
                outX,
                outY,
                outX + (int)outW,
                outY + (int)outH,
                mask,
                linearFilter ? BlitFramebufferFilter.Linear : BlitFramebufferFilter.Nearest);
        }

        public static int GetBytesPerPixel(InternalFormat internalFormat) => internalFormat switch
        {
            // Standard formats
            InternalFormat.Rgba8 => 4,
            InternalFormat.Rgb8 => 3,
            InternalFormat.R8 => 1,
            InternalFormat.RG8 => 2,

            // Depth/Stencil formats
            InternalFormat.DepthComponent32f => 4,
            InternalFormat.DepthComponent24 => 3,
            InternalFormat.DepthComponent16 => 2,
            InternalFormat.DepthComponent32 => 4,
            InternalFormat.DepthComponent => 4, // Default to 4 bytes (32-bit float)
            InternalFormat.StencilIndex8 => 1,
            InternalFormat.StencilIndex1 => 1,
            InternalFormat.StencilIndex4 => 1,
            InternalFormat.StencilIndex16 => 2,
            InternalFormat.StencilIndex => 1, // Default to 1 byte (8-bit)
            InternalFormat.Depth24Stencil8 => 4,
            InternalFormat.Depth32fStencil8 => 5, // 4 bytes depth + 1 byte stencil
            InternalFormat.DepthStencil => 4,
            InternalFormat.DepthStencilMesa => 4,

            // Base formats
            InternalFormat.Red => 1,
            InternalFormat.RG => 2,
            InternalFormat.Rgb => 3,
            InternalFormat.Rgba => 4,

            // Higher bit depth formats
            InternalFormat.Rgba16 => 8,
            InternalFormat.Rgb16 => 6,
            InternalFormat.RG16 => 4,
            InternalFormat.R16 => 2,
            InternalFormat.Rgba16f => 8,
            InternalFormat.Rgb16f => 6,
            InternalFormat.RG16f => 4,
            InternalFormat.R16f => 2,
            InternalFormat.Rgba32f => 16,
            InternalFormat.Rgb32f => 12,
            InternalFormat.RG32f => 8,
            InternalFormat.R32f => 4,

            // Integer formats
            InternalFormat.Rgba8i => 4,
            InternalFormat.Rgb8i => 3,
            InternalFormat.RG8i => 2,
            InternalFormat.R8i => 1,
            InternalFormat.Rgba16i => 8,
            InternalFormat.Rgb16i => 6,
            InternalFormat.RG16i => 4,
            InternalFormat.R16i => 2,
            InternalFormat.Rgba32i => 16,
            InternalFormat.Rgb32i => 12,
            InternalFormat.RG32i => 8,
            InternalFormat.R32i => 4,
            InternalFormat.Rgba8ui => 4,
            InternalFormat.Rgb8ui => 3,
            InternalFormat.RG8ui => 2,
            InternalFormat.R8ui => 1,
            InternalFormat.Rgba16ui => 8,
            InternalFormat.Rgb16ui => 6,
            InternalFormat.RG16ui => 4,
            InternalFormat.R16ui => 2,
            InternalFormat.Rgba32ui => 16,
            InternalFormat.Rgb32ui => 12,
            InternalFormat.RG32ui => 8,
            InternalFormat.R32ui => 4,

            // Special formats
            InternalFormat.R3G3B2 => 1,
            InternalFormat.Rgb565Oes => 2,
            InternalFormat.Rgba4 => 2,
            InternalFormat.Rgb5A1 => 2,
            InternalFormat.Rgb10A2 => 4,
            InternalFormat.Rgb10A2ui => 4,
            InternalFormat.R11fG11fB10f => 4,
            InternalFormat.Rgb9E5 => 4,

            // sRGB formats
            InternalFormat.Srgb8 => 3,
            InternalFormat.Srgb8Alpha8 => 4,
            InternalFormat.Srgb => 3,
            InternalFormat.SrgbAlpha => 4,

            // Signed normalized formats
            InternalFormat.R8SNorm => 1,
            InternalFormat.RG8SNorm => 2,
            InternalFormat.Rgb8SNorm => 3,
            InternalFormat.Rgba8SNorm => 4,
            InternalFormat.R16SNorm => 2,
            InternalFormat.RG16SNorm => 4,
            InternalFormat.Rgb16SNorm => 6,
            InternalFormat.Rgba16SNorm => 8,

            // Compressed formats - return estimated bytes per block
            InternalFormat.CompressedRgbS3TCDxt1Ext => 1, // ~0.5 bytes per pixel
            InternalFormat.CompressedRgbaS3TCDxt1Ext => 1, // ~0.5 bytes per pixel
            InternalFormat.CompressedRgbaS3TCDxt3Angle => 1, // ~1 byte per pixel
            InternalFormat.CompressedRgbaS3TCDxt5Angle => 1, // ~1 byte per pixel
            InternalFormat.CompressedRed => 1, // Depends on actual compression
            InternalFormat.CompressedRG => 1, // Depends on actual compression
            InternalFormat.CompressedRgb => 1, // Depends on actual compression
            InternalFormat.CompressedRgba => 1, // Depends on actual compression
            InternalFormat.CompressedSrgb => 1, // Depends on actual compression
            InternalFormat.CompressedSrgbAlpha => 1, // Depends on actual compression
            InternalFormat.CompressedSrgbS3TCDxt1Ext => 1, // ~0.5 bytes per pixel
            InternalFormat.CompressedSrgbAlphaS3TCDxt1Ext => 1, // ~0.5 bytes per pixel
            InternalFormat.CompressedSrgbAlphaS3TCDxt3Ext => 1, // ~1 byte per pixel
            InternalFormat.CompressedSrgbAlphaS3TCDxt5Ext => 1, // ~1 byte per pixel
            InternalFormat.CompressedRedRgtc1 => 1, // ~0.5 bytes per pixel
            InternalFormat.CompressedSignedRedRgtc1 => 1, // ~0.5 bytes per pixel
            InternalFormat.CompressedRedGreenRgtc2Ext => 1, // ~1 byte per pixel
            InternalFormat.CompressedSignedRedGreenRgtc2Ext => 1, // ~1 byte per pixel
            InternalFormat.Etc1Rgb8Oes => 1, // ~0.5 bytes per pixel

            // ASTC and other modern compressed formats
            InternalFormat.CompressedRgbaBptcUnorm => 1, // ~1 byte per pixel
            InternalFormat.CompressedSrgbAlphaBptcUnorm => 1, // ~1 byte per pixel
            InternalFormat.CompressedRgbBptcSignedFloat => 1, // ~1 byte per pixel
            InternalFormat.CompressedRgbBptcUnsignedFloat => 1, // ~1 byte per pixel
            InternalFormat.CompressedR11Eac => 1, // ~0.5 bytes per pixel
            InternalFormat.CompressedSignedR11Eac => 1, // ~0.5 bytes per pixel
            InternalFormat.CompressedRG11Eac => 1, // ~1 byte per pixel
            InternalFormat.CompressedSignedRG11Eac => 1, // ~1 byte per pixel
            InternalFormat.CompressedRgb8Etc2 => 1, // ~0.5 bytes per pixel
            InternalFormat.CompressedSrgb8Etc2 => 1, // ~0.5 bytes per pixel
            InternalFormat.CompressedRgb8PunchthroughAlpha1Etc2 => 1, // ~0.5 bytes per pixel
            InternalFormat.CompressedSrgb8PunchthroughAlpha1Etc2 => 1, // ~0.5 bytes per pixel
            InternalFormat.CompressedRgba8Etc2Eac => 1, // ~1 byte per pixel
            InternalFormat.CompressedSrgb8Alpha8Etc2Eac => 1, // ~1 byte per pixel

            // ASTC formats (all approximately 1 byte per pixel or less)
            InternalFormat.CompressedRgbaAstc4x4 => 1,
            InternalFormat.CompressedRgbaAstc5x4 => 1,
            InternalFormat.CompressedRgbaAstc5x5 => 1,
            InternalFormat.CompressedRgbaAstc6x5 => 1,
            InternalFormat.CompressedRgbaAstc6x6 => 1,
            InternalFormat.CompressedRgbaAstc8x5 => 1,
            InternalFormat.CompressedRgbaAstc8x6 => 1,
            InternalFormat.CompressedRgbaAstc8x8 => 1,
            InternalFormat.CompressedRgbaAstc10x5 => 1,
            InternalFormat.CompressedRgbaAstc10x6 => 1,
            InternalFormat.CompressedRgbaAstc10x8 => 1,
            InternalFormat.CompressedRgbaAstc10x10 => 1,
            InternalFormat.CompressedRgbaAstc12x10 => 1,
            InternalFormat.CompressedRgbaAstc12x12 => 1,

            // 3D ASTC formats
            InternalFormat.CompressedRgbaAstc3x3x3Oes => 1,
            InternalFormat.CompressedRgbaAstc4x3x3Oes => 1,
            InternalFormat.CompressedRgbaAstc4x4x3Oes => 1,
            InternalFormat.CompressedRgbaAstc4x4x4Oes => 1,
            InternalFormat.CompressedRgbaAstc5x4x4Oes => 1,
            InternalFormat.CompressedRgbaAstc5x5x4Oes => 1,
            InternalFormat.CompressedRgbaAstc5x5x5Oes => 1,
            InternalFormat.CompressedRgbaAstc6x5x5Oes => 1,
            InternalFormat.CompressedRgbaAstc6x6x5Oes => 1,
            InternalFormat.CompressedRgbaAstc6x6x6Oes => 1,

            // sRGB ASTC formats
            InternalFormat.CompressedSrgb8Alpha8Astc4x4 => 1,
            InternalFormat.CompressedSrgb8Alpha8Astc5x4 => 1,
            InternalFormat.CompressedSrgb8Alpha8Astc5x5 => 1,
            InternalFormat.CompressedSrgb8Alpha8Astc6x5 => 1,
            InternalFormat.CompressedSrgb8Alpha8Astc6x6 => 1,
            InternalFormat.CompressedSrgb8Alpha8Astc8x5 => 1,
            InternalFormat.CompressedSrgb8Alpha8Astc8x6 => 1,
            InternalFormat.CompressedSrgb8Alpha8Astc8x8 => 1,
            InternalFormat.CompressedSrgb8Alpha8Astc10x5 => 1,
            InternalFormat.CompressedSrgb8Alpha8Astc10x6 => 1,
            InternalFormat.CompressedSrgb8Alpha8Astc10x8 => 1,
            InternalFormat.CompressedSrgb8Alpha8Astc10x10 => 1,
            InternalFormat.CompressedSrgb8Alpha8Astc12x10 => 1,
            InternalFormat.CompressedSrgb8Alpha8Astc12x12 => 1,

            // 3D sRGB ASTC formats
            InternalFormat.CompressedSrgb8Alpha8Astc3x3x3Oes => 1,
            InternalFormat.CompressedSrgb8Alpha8Astc4x3x3Oes => 1,
            InternalFormat.CompressedSrgb8Alpha8Astc4x4x3Oes => 1,
            InternalFormat.CompressedSrgb8Alpha8Astc4x4x4Oes => 1,
            InternalFormat.CompressedSrgb8Alpha8Astc5x4x4Oes => 1,
            InternalFormat.CompressedSrgb8Alpha8Astc5x5x4Oes => 1,
            InternalFormat.CompressedSrgb8Alpha8Astc5x5x5Oes => 1,
            InternalFormat.CompressedSrgb8Alpha8Astc6x5x5Oes => 1,
            InternalFormat.CompressedSrgb8Alpha8Astc6x6x5Oes => 1,
            InternalFormat.CompressedSrgb8Alpha8Astc6x6x6Oes => 1,

            // Extension formats
            InternalFormat.Alpha4Ext => 1,
            InternalFormat.Alpha8Ext => 1,
            InternalFormat.Alpha12Ext => 2,
            InternalFormat.Alpha16Ext => 2,
            InternalFormat.Luminance4Ext => 1,
            InternalFormat.Luminance8Ext => 1,
            InternalFormat.Luminance12Ext => 2,
            InternalFormat.Luminance16Ext => 2,
            InternalFormat.Luminance4Alpha4Ext => 1,
            InternalFormat.Luminance6Alpha2Ext => 1,
            InternalFormat.Luminance8Alpha8Ext => 2,
            InternalFormat.Luminance12Alpha4Ext => 2,
            InternalFormat.Luminance12Alpha12Ext => 3,
            InternalFormat.Luminance16Alpha16Ext => 4,
            InternalFormat.Intensity4Ext => 1,
            InternalFormat.Intensity8Ext => 1,
            InternalFormat.Intensity12Ext => 2,
            InternalFormat.Intensity16Ext => 2,
            InternalFormat.Rgb2Ext => 1,
            InternalFormat.Rgb4 => 2,
            InternalFormat.Rgb5 => 2,
            InternalFormat.Rgb10 => 4,
            InternalFormat.Rgb12 => 5, // 36-bit, packed as 5 bytes
            InternalFormat.Rgba2 => 1,
            InternalFormat.Rgba12 => 6, // 48-bit, packed as 6 bytes

            // Dual formats
            InternalFormat.DualAlpha4Sgis =>  1,
            InternalFormat.DualAlpha8Sgis => 1,
            InternalFormat.DualAlpha12Sgis => 2,
            InternalFormat.DualAlpha16Sgis => 2,
            InternalFormat.DualLuminance4Sgis => 1,
            InternalFormat.DualLuminance8Sgis => 1,
            InternalFormat.DualLuminance12Sgis => 2,
            InternalFormat.DualLuminance16Sgis => 2,
            InternalFormat.DualIntensity4Sgis => 1,
            InternalFormat.DualIntensity8Sgis => 1,
            InternalFormat.DualIntensity12Sgis => 2,
            InternalFormat.DualIntensity16Sgis => 2,
            InternalFormat.DualLuminanceAlpha4Sgis => 1,
            InternalFormat.DualLuminanceAlpha8Sgis => 2,
            InternalFormat.QuadAlpha4Sgis => 2,
            InternalFormat.QuadAlpha8Sgis => 4,
            InternalFormat.QuadLuminance4Sgis => 2,
            InternalFormat.QuadLuminance8Sgis => 4,
            InternalFormat.QuadIntensity4Sgis => 2,
            InternalFormat.QuadIntensity8Sgis => 4,

            // Ext formats
            InternalFormat.Alpha32uiExt => 4,
            InternalFormat.Intensity32uiExt => 4,
            InternalFormat.Luminance32uiExt => 4,
            InternalFormat.LuminanceAlpha32uiExt => 8,
            InternalFormat.Alpha16uiExt => 2,
            InternalFormat.Intensity16uiExt => 2,
            InternalFormat.Luminance16uiExt => 2,
            InternalFormat.LuminanceAlpha16uiExt => 4,
            InternalFormat.Alpha8uiExt => 1,
            InternalFormat.Intensity8uiExt => 1,
            InternalFormat.Luminance8uiExt => 1,
            InternalFormat.LuminanceAlpha8uiExt => 2,
            InternalFormat.Alpha32iExt => 4,
            InternalFormat.Intensity32iExt => 4,
            InternalFormat.Luminance32iExt => 4,
            InternalFormat.LuminanceAlpha32iExt => 8,
            InternalFormat.Alpha16iExt => 2,
            InternalFormat.Intensity16iExt => 2,
            InternalFormat.Luminance16iExt => 2,
            InternalFormat.LuminanceAlpha16iExt => 4,
            InternalFormat.Alpha8iExt => 1,
            InternalFormat.Intensity8iExt => 1,
            InternalFormat.Luminance8iExt => 1,
            InternalFormat.LuminanceAlpha8iExt => 2,
            InternalFormat.DepthComponent32fNV => 4,
            InternalFormat.Depth32fStencil8NV => 5,

            // SR formats
            InternalFormat.SR8Ext => 1,
            InternalFormat.Srg8Ext => 2,

            // Default for unknown formats - conservative 4 bytes
            _ => 4
        };

        // ===================== Indirect + Pipeline Abstraction (OpenGL) =====================
        public override void BindVAOForRenderer(XRMeshRenderer.BaseVersion? version)
        {
            if (version is null)
            {
                UnbindMeshRenderer();
                return;
            }
            var glMesh = GenericToAPI<GLMeshRenderer>(version);
            BindMeshRenderer(glMesh);
        }

        public override bool ValidateIndexedVAO(XRMeshRenderer.BaseVersion? version)
        {
            var glMesh = version != null ? GenericToAPI<GLMeshRenderer>(version) : ActiveMeshRenderer;
            if (glMesh is null)
                return false;
            return (glMesh.TriangleIndicesBuffer?.Data?.ElementCount > 0) ||
                   (glMesh.LineIndicesBuffer?.Data?.ElementCount > 0) ||
                   (glMesh.PointIndicesBuffer?.Data?.ElementCount > 0);
        }

        public override void BindDrawIndirectBuffer(XRDataBuffer buffer)
        {
            var glBuf = GenericToAPI<GLDataBuffer>(buffer);
            if (glBuf is null)
                return;
            Api.BindBuffer(GLEnum.DrawIndirectBuffer, glBuf.BindingId);
        }

        public override void UnbindDrawIndirectBuffer()
        {
            Api.BindBuffer(GLEnum.DrawIndirectBuffer, 0);
        }

        public override void BindParameterBuffer(XRDataBuffer buffer)
        {
            var glBuf = GenericToAPI<GLDataBuffer>(buffer);
            if (glBuf is null)
                return;
            const GLEnum GL_PARAMETER_BUFFER = (GLEnum)0x80EE;
            Api.BindBuffer(GL_PARAMETER_BUFFER, glBuf.BindingId);
        }

        public override void UnbindParameterBuffer()
        {
            const GLEnum GL_PARAMETER_BUFFER = (GLEnum)0x80EE;
            Api.BindBuffer(GL_PARAMETER_BUFFER, 0);
        }

        private (PrimitiveType prim, DrawElementsType elem) GetActivePrimitiveAndElementType()
        {
            PrimitiveType primitiveType = PrimitiveType.Triangles;
            DrawElementsType elementType = DrawElementsType.UnsignedInt;
            var renderer = ActiveMeshRenderer;
            if (renderer is not null)
            {
                if (renderer.TriangleIndicesBuffer is not null)
                {
                    primitiveType = PrimitiveType.Triangles;
                    elementType = renderer.TrianglesElementType switch
                    {
                        IndexSize.Byte => DrawElementsType.UnsignedByte,
                        IndexSize.TwoBytes => DrawElementsType.UnsignedShort,
                        _ => DrawElementsType.UnsignedInt,
                    };
                }
                else if (renderer.LineIndicesBuffer is not null)
                {
                    primitiveType = PrimitiveType.Lines;
                    elementType = renderer.LineIndicesElementType switch
                    {
                        IndexSize.Byte => DrawElementsType.UnsignedByte,
                        IndexSize.TwoBytes => DrawElementsType.UnsignedShort,
                        _ => DrawElementsType.UnsignedInt,
                    };
                }
                else if (renderer.PointIndicesBuffer is not null)
                {
                    primitiveType = PrimitiveType.Points;
                    elementType = renderer.PointIndicesElementType switch
                    {
                        IndexSize.Byte => DrawElementsType.UnsignedByte,
                        IndexSize.TwoBytes => DrawElementsType.UnsignedShort,
                        _ => DrawElementsType.UnsignedInt,
                    };
                }
            }
            return (primitiveType, elementType);
        }

        public override unsafe void MultiDrawElementsIndirect(uint drawCount, uint stride)
        {
            var (prim, elem) = GetActivePrimitiveAndElementType();
            Api.MultiDrawElementsIndirect(prim, elem, null, drawCount, stride);
            Engine.Rendering.Stats.IncrementMultiDrawCalls();
            Engine.Rendering.Stats.IncrementDrawCalls((int)drawCount);
        }

        public override unsafe void MultiDrawElementsIndirectWithOffset(uint drawCount, uint stride, nuint byteOffset)
        {
            var (prim, elem) = GetActivePrimitiveAndElementType();
            Api.MultiDrawElementsIndirect(prim, elem, (void*)byteOffset, drawCount, stride);
            Engine.Rendering.Stats.IncrementMultiDrawCalls();
            Engine.Rendering.Stats.IncrementDrawCalls((int)drawCount);
        }

        public override unsafe void MultiDrawElementsIndirectCount(uint maxDrawCount, uint stride, nuint byteOffset)
        {
            var (prim, elem) = GetActivePrimitiveAndElementType();
            Api.MultiDrawElementsIndirectCount(prim, elem, (void*)byteOffset, IntPtr.Zero, maxDrawCount, stride);
            Engine.Rendering.Stats.IncrementMultiDrawCalls();
            // Note: actual draw count is determined by GPU, we track max as approximation
            Engine.Rendering.Stats.IncrementDrawCalls((int)maxDrawCount);
        }

        public unsafe void MultiDrawElementsIndirectCountNVBindless(uint drawCountOffset, uint maxDrawCount, uint stride)
        {
            var (prim, elem) = GetActivePrimitiveAndElementType();
            NVBindlessMultiDrawIndirectCount?.MultiDrawElementsIndirectBindlessCount(
                prim,
                elem,
                null,
                drawCountOffset,
                maxDrawCount,
                stride,
                1);
            Engine.Rendering.Stats.IncrementMultiDrawCalls();
            Engine.Rendering.Stats.IncrementDrawCalls((int)maxDrawCount);
        }

        public unsafe void MultiDrawElementsIndirectCount(uint drawCountOffset, uint maxDrawCount, uint stride)
        {
            var (primitiveType, elementType) = GetActivePrimitiveAndElementType();
            // Requires GL 4.6 or ARB_indirect_parameters
            Api.MultiDrawElementsIndirectCount(
                primitiveType,
                elementType,
                null,
                (nint)drawCountOffset,
                maxDrawCount,
                stride);
            Engine.Rendering.Stats.IncrementMultiDrawCalls();
            Engine.Rendering.Stats.IncrementDrawCalls((int)maxDrawCount);
        }

        //public unsafe void MultiDrawElementsIndirectCount(uint maxCommands, uint stride)
        //{
        //    //Get primitive type and element type from currently bound renderer
        //    PrimitiveType primitiveType = PrimitiveType.Triangles;
        //    DrawElementsType elementType = DrawElementsType.UnsignedInt;
        //    var renderer = ActiveMeshRenderer;
        //    if (renderer is not null)
        //    {
        //        if (renderer.TriangleIndicesBuffer is not null)
        //        {
        //            primitiveType = PrimitiveType.Triangles;
        //            elementType = ToDrawElementsType(renderer.TrianglesElementType);
        //        }
        //        else if (renderer.LineIndicesBuffer is not null)
        //        {
        //            primitiveType = PrimitiveType.Lines;
        //            elementType = ToDrawElementsType(renderer.LineIndicesElementType);
        //        }
        //        else if (renderer.PointIndicesBuffer is not null)
        //        {
        //            primitiveType = PrimitiveType.Points;
        //            elementType = ToDrawElementsType(renderer.PointIndicesElementType);
        //        }
        //    }

        //    // Requires GL 4.6 or ARB_indirect_parameters
        //    Api.MultiDrawElementsIndirectCount(
        //        primitiveType,
        //        elementType,
        //        null,
        //        IntPtr.Zero,
        //        maxCommands,
        //        stride);
        //}

        //public unsafe void MultiDrawElementsIndirectWithOffset(uint drawCount, uint stride, nuint byteOffset)
        //{
        //    // Determine primitive and element types from currently bound renderer (ActiveMeshRenderer)
        //    PrimitiveType primitiveType = PrimitiveType.Triangles;
        //    DrawElementsType elementType = DrawElementsType.UnsignedInt;
        //    var renderer = ActiveMeshRenderer;
        //    if (renderer is not null)
        //    {
        //        if (renderer.TriangleIndicesBuffer is not null)
        //        {
        //            primitiveType = PrimitiveType.Triangles;
        //            elementType = ToDrawElementsType(renderer.TrianglesElementType);
        //        }
        //        else if (renderer.LineIndicesBuffer is not null)
        //        {
        //            primitiveType = PrimitiveType.Lines;
        //            elementType = ToDrawElementsType(renderer.LineIndicesElementType);
        //        }
        //        else if (renderer.PointIndicesBuffer is not null)
        //        {
        //            primitiveType = PrimitiveType.Points;
        //            elementType = ToDrawElementsType(renderer.PointIndicesElementType);
        //        }
        //    }

        //    Api.MultiDrawElementsIndirect(
        //        primitiveType,
        //        elementType,
        //        (void*)byteOffset,
        //        drawCount,
        //        stride);
        //}

        public override bool SupportsIndirectCountDraw()
        {
            try
            {
                string? verStr = Version;
                if (!string.IsNullOrWhiteSpace(verStr))
                {
                    var parts = verStr.Split(' ');
                    var num = parts[0].Split('.');
                    if (num.Length >= 2 && int.TryParse(num[0], out int maj) && int.TryParse(num[1], out int min))
                    {
                        if (maj > 4 || (maj == 4 && min >= 6))
                            return true;
                    }
                }
            }
            catch { }
            return Api.IsExtensionPresent("GL_ARB_indirect_parameters");
        }

        public IGLTexture? BoundTexture { get; set; }

        /// <summary>
        /// Modifies the rendering API's state to adhere to the given material's settings.
        /// </summary>
        /// <param name="parameters"></param>
        public override void ApplyRenderParameters(RenderingParameters parameters)
        {
            if (parameters is null)
                return;

            //Api.PointSize(r.PointSize);
            //Api.LineWidth(r.LineWidth.Clamp(0.0f, 1.0f));
            Api.ColorMask(parameters.WriteRed, parameters.WriteGreen, parameters.WriteBlue, parameters.WriteAlpha);

            var winding = parameters.Winding;
            if (Engine.Rendering.State.ReverseWinding)
                winding = winding == EWinding.Clockwise ? EWinding.CounterClockwise : EWinding.Clockwise;
            Api.FrontFace(ToGLEnum(winding));

            ApplyCulling(parameters);
            ApplyDepth(parameters);
            ApplyBlending(parameters);
            ApplyStencil(parameters);
            //Alpha testing is done in-shader
        }

        private GLEnum ToGLEnum(EWinding winding)
            => winding switch
            {
                EWinding.Clockwise => GLEnum.CW,
                EWinding.CounterClockwise => GLEnum.Ccw,
                _ => GLEnum.Ccw
            };

        private void ApplyStencil(RenderingParameters r)
        {
            switch (r.StencilTest.Enabled)
            {
                case ERenderParamUsage.Enabled:
                    {
                        StencilTest st = r.StencilTest;
                        StencilTestFace b = st.BackFace;
                        StencilTestFace f = st.FrontFace;

                        Api.StencilOpSeparate(GLEnum.Back,
                            (StencilOp)(int)b.BothFailOp,
                            (StencilOp)(int)b.StencilPassDepthFailOp,
                            (StencilOp)(int)b.BothPassOp);

                        Api.StencilOpSeparate(GLEnum.Front,
                            (StencilOp)(int)f.BothFailOp,
                            (StencilOp)(int)f.StencilPassDepthFailOp,
                            (StencilOp)(int)f.BothPassOp);

                        Api.StencilMaskSeparate(GLEnum.Back, b.WriteMask);
                        Api.StencilMaskSeparate(GLEnum.Front, f.WriteMask);

                        Api.StencilFuncSeparate(GLEnum.Back,
                            StencilFunction.Never + (int)b.Function, b.Reference, b.ReadMask);
                        Api.StencilFuncSeparate(GLEnum.Front,
                            StencilFunction.Never + (int)f.Function, f.Reference, f.ReadMask);

                        break;
                    }

                case ERenderParamUsage.Disabled:
                    //GL.Disable(EnableCap.StencilTest);
                    Api.StencilMask(0);
                    Api.StencilOp(GLEnum.Keep, GLEnum.Keep, GLEnum.Keep);
                    Api.StencilFunc(StencilFunction.Always, 0, 0);
                    break;
            }
        }

        private void ApplyBlending(RenderingParameters r)
        {
            if (r.BlendModeAllDrawBuffers is not null)
            {
                var x = r.BlendModeAllDrawBuffers;
                if (x.Enabled == ERenderParamUsage.Enabled)
                {
                    Api.Enable(EnableCap.Blend);

                    Api.BlendEquationSeparate(
                        ToGLEnum(x.RgbEquation),
                        ToGLEnum(x.AlphaEquation));

                    Api.BlendFuncSeparate(
                        ToGLEnum(x.RgbSrcFactor),
                        ToGLEnum(x.RgbDstFactor),
                        ToGLEnum(x.AlphaSrcFactor),
                        ToGLEnum(x.AlphaDstFactor));
                }
                else if (x.Enabled == ERenderParamUsage.Disabled)
                    Api.Disable(EnableCap.Blend);
            }
            else if (r.BlendModesPerDrawBuffer is not null)
            {
                if (r.BlendModesPerDrawBuffer.Any(r => r.Value.Enabled == ERenderParamUsage.Enabled))
                {
                    Api.Enable(EnableCap.Blend);
                    foreach (KeyValuePair<uint, BlendMode> pair in r.BlendModesPerDrawBuffer)
                    {
                        uint drawBuffer = pair.Key;
                        BlendMode x = pair.Value;
                        if (x.Enabled == ERenderParamUsage.Enabled)
                        {
                            Api.BlendEquationSeparate(
                                drawBuffer,
                                ToGLEnum(x.RgbEquation),
                                ToGLEnum(x.AlphaEquation));

                            Api.BlendFuncSeparate(
                                drawBuffer,
                                ToGLEnum(x.RgbSrcFactor),
                                ToGLEnum(x.RgbDstFactor),
                                ToGLEnum(x.AlphaSrcFactor),
                                ToGLEnum(x.AlphaDstFactor));
                        }
                        else
                        {
                            //Apply a blend mode that mimics non-blending for this draw buffer

                            Api.BlendEquationSeparate(
                                drawBuffer,
                                GLEnum.FuncAdd,
                                GLEnum.FuncAdd);

                            Api.BlendFuncSeparate(
                                drawBuffer,
                                GLEnum.One,
                                GLEnum.Zero,
                                GLEnum.One,
                                GLEnum.Zero);
                        }
                    }
                }
                else if (r.BlendModesPerDrawBuffer.Count == 0 || r.BlendModesPerDrawBuffer.Any(r => r.Value.Enabled == ERenderParamUsage.Disabled))
                    Api.Disable(EnableCap.Blend);
            }
            else
                Api.Disable(EnableCap.Blend);
        }

        private void ApplyCulling(RenderingParameters r)
        {
            if (r.CullMode == ECullMode.None)
                Api.Disable(EnableCap.CullFace);
            else
            {
                Api.Enable(EnableCap.CullFace);
                var cullMode = r.CullMode;
                if (Engine.Rendering.State.ReverseCulling)
                    cullMode = cullMode switch
                    {
                        ECullMode.Front => ECullMode.Back,
                        ECullMode.Back => ECullMode.Front,
                        _ => cullMode
                    };
                Api.CullFace(ToGLEnum(cullMode));
            }
        }

        private void ApplyDepth(RenderingParameters r)
        {
            switch (r.DepthTest.Enabled)
            {
                case ERenderParamUsage.Enabled:
                    Api.Enable(EnableCap.DepthTest);
                    Api.DepthFunc(ToGLEnum(r.DepthTest.Function));
                    Api.DepthMask(r.DepthTest.UpdateDepth);
                    break;

                case ERenderParamUsage.Disabled:
                    Api.Disable(EnableCap.DepthTest);
                    break;
            }
        }

        private GLEnum ToGLEnum(EBlendingFactor factor)
            => factor switch
            {
                EBlendingFactor.Zero => GLEnum.Zero,
                EBlendingFactor.One => GLEnum.One,
                EBlendingFactor.SrcColor => GLEnum.SrcColor,
                EBlendingFactor.OneMinusSrcColor => GLEnum.OneMinusSrcColor,
                EBlendingFactor.DstColor => GLEnum.DstColor,
                EBlendingFactor.OneMinusDstColor => GLEnum.OneMinusDstColor,
                EBlendingFactor.SrcAlpha => GLEnum.SrcAlpha,
                EBlendingFactor.OneMinusSrcAlpha => GLEnum.OneMinusSrcAlpha,
                EBlendingFactor.DstAlpha => GLEnum.DstAlpha,
                EBlendingFactor.OneMinusDstAlpha => GLEnum.OneMinusDstAlpha,
                EBlendingFactor.ConstantColor => GLEnum.ConstantColor,
                EBlendingFactor.OneMinusConstantColor => GLEnum.OneMinusConstantColor,
                EBlendingFactor.ConstantAlpha => GLEnum.ConstantAlpha,
                EBlendingFactor.OneMinusConstantAlpha => GLEnum.OneMinusConstantAlpha,
                EBlendingFactor.SrcAlphaSaturate => GLEnum.SrcAlphaSaturate,
                _ => GLEnum.Zero,
            };

        private GLEnum ToGLEnum(EBlendEquationMode equation)
            => equation switch
            {
                EBlendEquationMode.FuncAdd => GLEnum.FuncAdd,
                EBlendEquationMode.FuncSubtract => GLEnum.FuncSubtract,
                EBlendEquationMode.FuncReverseSubtract => GLEnum.FuncReverseSubtract,
                EBlendEquationMode.Min => GLEnum.Min,
                EBlendEquationMode.Max => GLEnum.Max,
                _ => GLEnum.FuncAdd,
            };

        private GLEnum ToGLEnum(EComparison function)
            => function switch
            {
                EComparison.Never => GLEnum.Never,
                EComparison.Less => GLEnum.Less,
                EComparison.Equal => GLEnum.Equal,
                EComparison.Lequal => GLEnum.Lequal,
                EComparison.Greater => GLEnum.Greater,
                EComparison.Nequal => GLEnum.Notequal,
                EComparison.Gequal => GLEnum.Gequal,
                EComparison.Always => GLEnum.Always,
                _ => GLEnum.Never,
            };

        private GLEnum ToGLEnum(ECullMode cullMode)
            => cullMode switch
            {
                ECullMode.Front => GLEnum.Front,
                ECullMode.Back => GLEnum.Back,
                _ => GLEnum.FrontAndBack,
            };

        private GLEnum ToGLEnum(IndexSize elementType)
            => elementType switch
            {
                IndexSize.Byte => GLEnum.UnsignedByte,
                IndexSize.TwoBytes => GLEnum.UnsignedShort,
                IndexSize.FourBytes => GLEnum.UnsignedInt,
                _ => GLEnum.UnsignedInt,
            };

        private GLEnum ToGLEnum(EPrimitiveType type)
            => type switch
            {
                EPrimitiveType.Points => GLEnum.Points,
                EPrimitiveType.Lines => GLEnum.Lines,
                EPrimitiveType.LineLoop => GLEnum.LineLoop,
                EPrimitiveType.LineStrip => GLEnum.LineStrip,
                EPrimitiveType.Triangles => GLEnum.Triangles,
                EPrimitiveType.TriangleStrip => GLEnum.TriangleStrip,
                EPrimitiveType.TriangleFan => GLEnum.TriangleFan,
                EPrimitiveType.LinesAdjacency => GLEnum.LinesAdjacency,
                EPrimitiveType.LineStripAdjacency => GLEnum.LineStripAdjacency,
                EPrimitiveType.TrianglesAdjacency => GLEnum.TrianglesAdjacency,
                EPrimitiveType.TriangleStripAdjacency => GLEnum.TriangleStripAdjacency,
                EPrimitiveType.Patches => GLEnum.Patches,
                _ => GLEnum.Triangles,
            };

        public int GetInteger(GLEnum value)
            => Api.GetInteger(value);

        public unsafe bool IsExtensionSupported(string name)
        {
            if (string.IsNullOrEmpty(name))
                return false;

            //Check if the extension is already loaded
            if (Api.IsExtensionPresent(name))
                return true;

            //Check if the extension is supported by the OpenGL context
            byte* extensions = Api.GetString(GLEnum.Extensions);
            if (extensions is null)
                return false;

            //Split the extensions string into individual extensions
            string str = new((sbyte*)extensions);
            string[] extList = str.Split(' ', StringSplitOptions.RemoveEmptyEntries);

            //Check if the requested extension is in the list
            foreach (string ext in extList)
                if (ext.Equals(name, StringComparison.OrdinalIgnoreCase))
                    return true;

            //If we reach here, the extension is not supported
            return false;
        }

        private DrawElementsType ToDrawElementsType(IndexSize type) => type switch
        {
            IndexSize.Byte => DrawElementsType.UnsignedByte,
            IndexSize.TwoBytes => DrawElementsType.UnsignedShort,
            IndexSize.FourBytes => DrawElementsType.UnsignedInt,
            _ => DrawElementsType.UnsignedInt,
        };

        public override void ConfigureVAOAttributesForProgram(XRRenderProgram program, XRMeshRenderer.BaseVersion? version)
        {
            var glProgram = GenericToAPI<GLRenderProgram>(program);
            var glMesh = version is null ? ActiveMeshRenderer : GenericToAPI<GLMeshRenderer>(version);
            if (glProgram is null || glMesh is null)
                return;

            // Bind VAO to ensure we write into the correct object
            BindMeshRenderer(glMesh);

            // Rebind mesh + renderer buffers against this program's attribute locations
            // 1) Mesh vertex buffers
            var mesh = glMesh.Mesh;
            if (mesh?.Buffers is IEventDictionary<string, XRDataBuffer> meshBuffers)
            {
                foreach (var kv in meshBuffers)
                {
                    var glBuf = GenericToAPI<GLDataBuffer>(kv.Value);
                    glBuf?.BindToRenderer(glProgram, glMesh);
                }
            }

            // 2) Renderer extra buffers (SSBO/UBO/instance attributes)
            var rendBuffers = glMesh.MeshRenderer.Buffers as IEventDictionary<string, XRDataBuffer>;
            if (rendBuffers is not null)
            {
                foreach (var kv in rendBuffers)
                {
                    var glBuf = GenericToAPI<GLDataBuffer>(kv.Value);
                    glBuf?.BindToRenderer(glProgram, glMesh);
                }
            }
        }

        public override void SetEngineUniforms(XRRenderProgram program, XRCamera camera)
        {
            var glProgram = GenericToAPI<GLRenderProgram>(program);
            if (glProgram is null)
                return;

            // Mirror GLMaterial.SetEngineUniforms minimal camera bits
            bool stereoPass = Engine.Rendering.State.IsStereoPass;
            if (stereoPass)
            {
                var rightCam = Engine.Rendering.State.RenderingStereoRightEyeCamera;
                PassCameraUniforms(glProgram, camera, EEngineUniform.LeftEyeInverseViewMatrix, EEngineUniform.LeftEyeProjMatrix);
                PassCameraUniforms(glProgram, rightCam, EEngineUniform.RightEyeInverseViewMatrix, EEngineUniform.RightEyeProjMatrix);
            }
            else
            {
                PassCameraUniforms(glProgram, camera, EEngineUniform.InverseViewMatrix, EEngineUniform.ProjMatrix);
            }
        }

        private static void PassCameraUniforms(GLRenderProgram program, XRCamera? camera, EEngineUniform invView, EEngineUniform proj)
        {
            Matrix4x4 viewMatrix;        // The actual view matrix (inverse of camera world transform)
            Matrix4x4 inverseViewMatrix; // The camera's world transform (inverse of view matrix)
            Matrix4x4 projMatrix;
            if (camera != null)
            {
                // ViewMatrix is InverseRenderMatrix - the actual view transformation
                // InverseViewMatrix is RenderMatrix - the camera's world position (kept for compatibility)
                viewMatrix = camera.Transform.InverseRenderMatrix;
                inverseViewMatrix = camera.Transform.RenderMatrix;
                // Use unjittered projection when rendering motion vectors to match fragment shader expectations
                bool useUnjittered = Engine.Rendering.State.RenderingPipelineState?.UseUnjitteredProjection ?? false;
                projMatrix = useUnjittered ? camera.ProjectionMatrixUnjittered : camera.ProjectionMatrix;
            }
            else
            {
                viewMatrix = Matrix4x4.Identity;
                inverseViewMatrix = Matrix4x4.Identity;
                projMatrix = Matrix4x4.Identity;
            }
            // Pass ViewMatrix (actual view transform) for accurate motion vector computation
            // This avoids single-precision inverse() in shader which causes precision issues for far objects
            program.Uniform($"{EEngineUniform.ViewMatrix}{DefaultVertexShaderGenerator.VertexUniformSuffix}", viewMatrix);
            program.Uniform($"{invView}{DefaultVertexShaderGenerator.VertexUniformSuffix}", inverseViewMatrix);
            program.Uniform($"{proj}{DefaultVertexShaderGenerator.VertexUniformSuffix}", projMatrix);
        }

        public override void SetMaterialUniforms(XRMaterial material, XRRenderProgram program)
        {
            var glProgram = GenericToAPI<GLRenderProgram>(program);
            var glMaterial = GenericToAPI<GLMaterial>(material);
            if (glProgram is null || glMaterial is null)
                return;

            glMaterial.SetUniforms(glProgram);
        }
    }
}