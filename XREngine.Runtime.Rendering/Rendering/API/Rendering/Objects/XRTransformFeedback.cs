using System.Diagnostics.CodeAnalysis;
using XREngine.Data.Rendering;

namespace XREngine.Rendering
{
    public enum EFeedbackType
    {
        OutValues,
        PerVertex,
    }

    public enum EXRTransformFeedbackOperation
    {
        BindBuffer,
        Begin,
        End,
        Pause,
        Resume,
        DrawCaptured,
        DrawIndirectByteCount,
    }

    public enum EXRTransformFeedbackStatus
    {
        Succeeded,
        Unsupported,
        Failed,
    }

    [Flags]
    public enum EXRTransformFeedbackFeatureFlags
    {
        None = 0,
        Capture = 1 << 0,
        PauseResume = 1 << 1,
        PrimitiveCountQueries = 1 << 2,
        DrawCaptured = 1 << 3,
        DrawIndirectByteCount = 1 << 4,
        RuntimeVaryingSelection = 1 << 5,
        ShaderDeclaredVaryings = 1 << 6,
        GeometryStreams = 1 << 7,
        MultipleBuffers = 1 << 8,
    }

    public readonly record struct XRTransformFeedbackOperationResult(
        EXRTransformFeedbackOperation Operation,
        EXRTransformFeedbackStatus Status,
        string RenderApi,
        string? Message = null)
    {
        public bool Succeeded => Status == EXRTransformFeedbackStatus.Succeeded;

        public static XRTransformFeedbackOperationResult Success(
            EXRTransformFeedbackOperation operation,
            string renderApi,
            string? message = null)
            => new(operation, EXRTransformFeedbackStatus.Succeeded, renderApi, message);

        public static XRTransformFeedbackOperationResult Unsupported(
            EXRTransformFeedbackOperation operation,
            string renderApi,
            string message)
            => new(operation, EXRTransformFeedbackStatus.Unsupported, renderApi, message);

        public static XRTransformFeedbackOperationResult Failed(
            EXRTransformFeedbackOperation operation,
            string renderApi,
            string message)
            => new(operation, EXRTransformFeedbackStatus.Failed, renderApi, message);
    }

    public readonly record struct XRTransformFeedbackCapabilities(
        string RenderApi,
        bool IsSupported,
        EXRTransformFeedbackFeatureFlags Features,
        uint MaxBufferBindings,
        ulong MaxBufferSize,
        string? Limitation = null)
    {
        public bool SupportsCapture => Features.HasFlag(EXRTransformFeedbackFeatureFlags.Capture);
        public bool SupportsPauseResume => Features.HasFlag(EXRTransformFeedbackFeatureFlags.PauseResume);
        public bool SupportsPrimitiveCountQueries => Features.HasFlag(EXRTransformFeedbackFeatureFlags.PrimitiveCountQueries);
        public bool SupportsDrawCaptured => Features.HasFlag(EXRTransformFeedbackFeatureFlags.DrawCaptured);
        public bool SupportsDrawIndirectByteCount => Features.HasFlag(EXRTransformFeedbackFeatureFlags.DrawIndirectByteCount);
        public bool SupportsRuntimeVaryingSelection => Features.HasFlag(EXRTransformFeedbackFeatureFlags.RuntimeVaryingSelection);
        public bool RequiresShaderDeclaredVaryings => Features.HasFlag(EXRTransformFeedbackFeatureFlags.ShaderDeclaredVaryings);
        public bool SupportsGeometryStreams => Features.HasFlag(EXRTransformFeedbackFeatureFlags.GeometryStreams);
        public bool SupportsMultipleBuffers => Features.HasFlag(EXRTransformFeedbackFeatureFlags.MultipleBuffers);

        public static XRTransformFeedbackCapabilities Unsupported(string renderApi, string limitation)
            => new(renderApi, false, EXRTransformFeedbackFeatureFlags.None, 0u, 0ul, limitation);
    }

    internal interface IXRTransformFeedbackApi
    {
        XRTransformFeedbackCapabilities GetCapabilities();
        XRTransformFeedbackOperationResult BindTransformFeedbackBuffer(ulong offset, ulong? size);
        XRTransformFeedbackOperationResult BeginTransformFeedback(XRDataBuffer? counterBuffer, ulong counterBufferOffset);
        XRTransformFeedbackOperationResult EndTransformFeedback(XRDataBuffer? counterBuffer, ulong counterBufferOffset);
        XRTransformFeedbackOperationResult PauseTransformFeedback(XRDataBuffer? counterBuffer, ulong counterBufferOffset);
        XRTransformFeedbackOperationResult ResumeTransformFeedback(XRDataBuffer? counterBuffer, ulong counterBufferOffset);
        XRTransformFeedbackOperationResult DrawCapturedTransformFeedback(uint instanceCount, uint stream);
        XRTransformFeedbackOperationResult DrawTransformFeedbackIndirectByteCount(
            XRDataBuffer? counterBuffer,
            ulong counterBufferOffset,
            uint counterOffset,
            uint vertexStride,
            uint instanceCount,
            uint firstInstance);
    }

    /// <summary>
    /// Render object used for retrieving shader output data from the GPU.
    /// </summary>
    public class XRTransformFeedback : GenericRenderObject
    {
        private uint _bindingLocation;
        private string[] _names;
        private EFeedbackType _type;
        private EPrimitiveType _primitiveType = EPrimitiveType.Points;
        private XRDataBuffer? _counterBuffer;
        private XRRenderProgram? _program;

        public XRTransformFeedback(EFeedbackType type, uint bindingLocation, params string[] names)
        {
            _type = type;
            _bindingLocation = bindingLocation;
            _names = names ?? [];
            FeedbackBuffer = new("", EBufferTarget.TransformFeedbackBuffer, false);
        }

        public uint BindingLocation
        {
            get => _bindingLocation;
            set => SetField(ref _bindingLocation, value);
        }

        /// <summary>
        /// Shader output names captured by APIs that support runtime varying selection.
        /// OpenGL consumes these before program link; Vulkan shaders must carry XFB declarations instead.
        /// </summary>
        public string[] Names
        {
            get => _names;
            set => SetField(ref _names, value ?? []);
        }

        public EFeedbackType Type
        {
            get => _type;
            set => SetField(ref _type, value);
        }

        /// <summary>
        /// Primitive mode used by OpenGL when beginning transform feedback.
        /// Vulkan derives primitive topology from the active graphics pipeline.
        /// </summary>
        public EPrimitiveType PrimitiveType
        {
            get => _primitiveType;
            set => SetField(ref _primitiveType, value);
        }

        /// <summary>
        /// Optional counter buffer used by Vulkan to resume capture and to draw from captured byte counts.
        /// OpenGL pause/resume and draw-captured operations do not require this buffer.
        /// </summary>
        public XRDataBuffer? CounterBuffer
        {
            get => _counterBuffer;
            set => SetField(ref _counterBuffer, value);
        }

        public XRDataBuffer FeedbackBuffer { get; }

        /// <summary>
        /// Program whose link/compile layout includes this capture. Assigning a program
        /// through <see cref="AttachToProgram"/> lets the active renderer configure the
        /// required OpenGL varyings or Vulkan XFB shader decorations.
        /// </summary>
        public XRRenderProgram? Program
        {
            get => _program;
            private set => SetField(ref _program, value);
        }

        public bool AttachToProgram(XRRenderProgram program)
        {
            ArgumentNullException.ThrowIfNull(program);
            return program.AttachTransformFeedback(this);
        }

        public bool DetachFromProgram()
        {
            XRRenderProgram? program = Program;
            return program is not null && program.DetachTransformFeedback(this);
        }

        internal void SetProgramOwner(XRRenderProgram? program)
            => Program = program;

        public XRTransformFeedbackCapabilities GetCapabilities()
        {
            if (!TryGetActiveApi(out IXRTransformFeedbackApi? api, out string renderApi, out string? reason))
                return XRTransformFeedbackCapabilities.Unsupported(renderApi, reason ?? "No active transform feedback backend.");

            try
            {
                return api.GetCapabilities();
            }
            catch (Exception ex)
            {
                return XRTransformFeedbackCapabilities.Unsupported(renderApi, ex.Message);
            }
        }

        public XRTransformFeedbackOperationResult Bind(ulong offset = 0, ulong? size = null)
        {
            if (!TryGetActiveApi(out IXRTransformFeedbackApi? api, out string renderApi, out string? reason))
                return XRTransformFeedbackOperationResult.Unsupported(EXRTransformFeedbackOperation.BindBuffer, renderApi, reason ?? "No active transform feedback backend.");

            try
            {
                return api.BindTransformFeedbackBuffer(offset, size);
            }
            catch (Exception ex)
            {
                return XRTransformFeedbackOperationResult.Failed(EXRTransformFeedbackOperation.BindBuffer, renderApi, ex.Message);
            }
        }

        public XRTransformFeedbackOperationResult Begin(XRDataBuffer? counterBuffer = null, ulong counterBufferOffset = 0)
        {
            if (!TryGetActiveApi(out IXRTransformFeedbackApi? api, out string renderApi, out string? reason))
                return XRTransformFeedbackOperationResult.Unsupported(EXRTransformFeedbackOperation.Begin, renderApi, reason ?? "No active transform feedback backend.");

            try
            {
                return api.BeginTransformFeedback(counterBuffer ?? CounterBuffer, counterBufferOffset);
            }
            catch (Exception ex)
            {
                return XRTransformFeedbackOperationResult.Failed(EXRTransformFeedbackOperation.Begin, renderApi, ex.Message);
            }
        }

        public XRTransformFeedbackOperationResult End(XRDataBuffer? counterBuffer = null, ulong counterBufferOffset = 0)
        {
            if (!TryGetActiveApi(out IXRTransformFeedbackApi? api, out string renderApi, out string? reason))
                return XRTransformFeedbackOperationResult.Unsupported(EXRTransformFeedbackOperation.End, renderApi, reason ?? "No active transform feedback backend.");

            try
            {
                return api.EndTransformFeedback(counterBuffer ?? CounterBuffer, counterBufferOffset);
            }
            catch (Exception ex)
            {
                return XRTransformFeedbackOperationResult.Failed(EXRTransformFeedbackOperation.End, renderApi, ex.Message);
            }
        }

        public XRTransformFeedbackOperationResult Pause(XRDataBuffer? counterBuffer = null, ulong counterBufferOffset = 0)
        {
            if (!TryGetActiveApi(out IXRTransformFeedbackApi? api, out string renderApi, out string? reason))
                return XRTransformFeedbackOperationResult.Unsupported(EXRTransformFeedbackOperation.Pause, renderApi, reason ?? "No active transform feedback backend.");

            try
            {
                return api.PauseTransformFeedback(counterBuffer ?? CounterBuffer, counterBufferOffset);
            }
            catch (Exception ex)
            {
                return XRTransformFeedbackOperationResult.Failed(EXRTransformFeedbackOperation.Pause, renderApi, ex.Message);
            }
        }

        public XRTransformFeedbackOperationResult Resume(XRDataBuffer? counterBuffer = null, ulong counterBufferOffset = 0)
        {
            if (!TryGetActiveApi(out IXRTransformFeedbackApi? api, out string renderApi, out string? reason))
                return XRTransformFeedbackOperationResult.Unsupported(EXRTransformFeedbackOperation.Resume, renderApi, reason ?? "No active transform feedback backend.");

            try
            {
                return api.ResumeTransformFeedback(counterBuffer ?? CounterBuffer, counterBufferOffset);
            }
            catch (Exception ex)
            {
                return XRTransformFeedbackOperationResult.Failed(EXRTransformFeedbackOperation.Resume, renderApi, ex.Message);
            }
        }

        public XRTransformFeedbackOperationResult DrawCaptured(uint instanceCount = 1, uint stream = 0)
        {
            if (!TryGetActiveApi(out IXRTransformFeedbackApi? api, out string renderApi, out string? reason))
                return XRTransformFeedbackOperationResult.Unsupported(EXRTransformFeedbackOperation.DrawCaptured, renderApi, reason ?? "No active transform feedback backend.");

            try
            {
                return api.DrawCapturedTransformFeedback(instanceCount, stream);
            }
            catch (Exception ex)
            {
                return XRTransformFeedbackOperationResult.Failed(EXRTransformFeedbackOperation.DrawCaptured, renderApi, ex.Message);
            }
        }

        public XRTransformFeedbackOperationResult DrawIndirectByteCount(
            uint vertexStride,
            XRDataBuffer? counterBuffer = null,
            ulong counterBufferOffset = 0,
            uint counterOffset = 0,
            uint instanceCount = 1,
            uint firstInstance = 0)
        {
            if (!TryGetActiveApi(out IXRTransformFeedbackApi? api, out string renderApi, out string? reason))
                return XRTransformFeedbackOperationResult.Unsupported(EXRTransformFeedbackOperation.DrawIndirectByteCount, renderApi, reason ?? "No active transform feedback backend.");

            try
            {
                return api.DrawTransformFeedbackIndirectByteCount(
                    counterBuffer ?? CounterBuffer,
                    counterBufferOffset,
                    counterOffset,
                    vertexStride,
                    instanceCount,
                    firstInstance);
            }
            catch (Exception ex)
            {
                return XRTransformFeedbackOperationResult.Failed(EXRTransformFeedbackOperation.DrawIndirectByteCount, renderApi, ex.Message);
            }
        }

        private bool TryGetActiveApi([NotNullWhen(true)] out IXRTransformFeedbackApi? api, out string renderApi, out string? reason)
        {
            api = null;
            reason = null;

            AbstractRenderer? renderer = AbstractRenderer.Current;
            if (renderer is null)
            {
                renderApi = "None";
                reason = "Transform feedback commands must run while a renderer is active.";
                return false;
            }

            renderApi = renderer.GetType().Name;
            AbstractRenderAPIObject? apiObject = renderer.GetOrCreateAPIRenderObject(this, generateNow: false);
            if (apiObject is IXRTransformFeedbackApi transformFeedbackApi)
            {
                api = transformFeedbackApi;
                return true;
            }

            reason = $"{renderApi} does not expose transform feedback commands.";
            return false;
        }
    }
}
