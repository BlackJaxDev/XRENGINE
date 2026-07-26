using Silk.NET.OpenGL;
using XREngine.Data.Rendering;
using static XREngine.Rendering.OpenGL.OpenGLRenderer;

namespace XREngine.Rendering.OpenGL
{
    public class GLTransformFeedback(OpenGLRenderer renderer, XRTransformFeedback data) :
        GLObject<XRTransformFeedback>(renderer, data),
        IXRTransformFeedbackApi
    {
        protected override void LinkData()
        {

        }
        protected override void UnlinkData()
        {

        }
        public override EGLObjectType Type => EGLObjectType.TransformFeedback;

        XRTransformFeedbackCapabilities IXRTransformFeedbackApi.GetCapabilities()
        {
            EXRTransformFeedbackFeatureFlags features =
                EXRTransformFeedbackFeatureFlags.Capture |
                EXRTransformFeedbackFeatureFlags.PauseResume |
                EXRTransformFeedbackFeatureFlags.PrimitiveCountQueries |
                EXRTransformFeedbackFeatureFlags.DrawCaptured |
                EXRTransformFeedbackFeatureFlags.RuntimeVaryingSelection;

            int maxBindings = 1;
            try
            {
                maxBindings = Math.Max(1, Api.GetInteger(GLEnum.MaxTransformFeedbackBuffers));
            }
            catch
            {
                // Keep a conservative default on older profiles where the query enum is unavailable.
            }

            if (maxBindings > 1)
                features |= EXRTransformFeedbackFeatureFlags.MultipleBuffers;

            return new XRTransformFeedbackCapabilities(
                "OpenGL",
                true,
                features,
                (uint)maxBindings,
                0ul,
                "OpenGL selects XRTransformFeedback.Names before program link. PerVertex captures may use multiple transform-feedback buffers; OutValues captures require one varying per dense binding.");
        }

        XRTransformFeedbackOperationResult IXRTransformFeedbackApi.BindTransformFeedbackBuffer(ulong offset, ulong? size)
        {
            if (!TryBindFeedbackBuffer(offset, size, out string? failure))
                return XRTransformFeedbackOperationResult.Failed(EXRTransformFeedbackOperation.BindBuffer, "OpenGL", failure ?? "Failed to bind transform feedback buffer.");

            return XRTransformFeedbackOperationResult.Success(EXRTransformFeedbackOperation.BindBuffer, "OpenGL");
        }

        XRTransformFeedbackOperationResult IXRTransformFeedbackApi.BeginTransformFeedback(XRDataBuffer? counterBuffer, ulong counterBufferOffset)
        {
            if (!TryBindFeedbackBuffer(0ul, null, out string? failure))
                return XRTransformFeedbackOperationResult.Failed(EXRTransformFeedbackOperation.Begin, "OpenGL", failure ?? "Failed to bind transform feedback buffer.");

            if (!TryGetPrimitiveMode(out GLEnum mode, out string? modeFailure))
                return XRTransformFeedbackOperationResult.Unsupported(EXRTransformFeedbackOperation.Begin, "OpenGL", modeFailure ?? "Unsupported transform feedback primitive type.");

            Api.BeginTransformFeedback(mode);
            return XRTransformFeedbackOperationResult.Success(EXRTransformFeedbackOperation.Begin, "OpenGL");
        }

        XRTransformFeedbackOperationResult IXRTransformFeedbackApi.EndTransformFeedback(XRDataBuffer? counterBuffer, ulong counterBufferOffset)
        {
            Api.EndTransformFeedback();
            return XRTransformFeedbackOperationResult.Success(EXRTransformFeedbackOperation.End, "OpenGL");
        }

        XRTransformFeedbackOperationResult IXRTransformFeedbackApi.PauseTransformFeedback(XRDataBuffer? counterBuffer, ulong counterBufferOffset)
        {
            Api.PauseTransformFeedback();
            return XRTransformFeedbackOperationResult.Success(EXRTransformFeedbackOperation.Pause, "OpenGL");
        }

        XRTransformFeedbackOperationResult IXRTransformFeedbackApi.ResumeTransformFeedback(XRDataBuffer? counterBuffer, ulong counterBufferOffset)
        {
            Api.ResumeTransformFeedback();
            return XRTransformFeedbackOperationResult.Success(EXRTransformFeedbackOperation.Resume, "OpenGL");
        }

        XRTransformFeedbackOperationResult IXRTransformFeedbackApi.DrawCapturedTransformFeedback(uint instanceCount, uint stream)
        {
            if (!TryGetPrimitiveMode(out GLEnum mode, out string? modeFailure))
                return XRTransformFeedbackOperationResult.Unsupported(EXRTransformFeedbackOperation.DrawCaptured, "OpenGL", modeFailure ?? "Unsupported transform feedback primitive type.");

            Generate();
            if (instanceCount <= 1u)
            {
                if (stream == 0u)
                    Api.DrawTransformFeedback(mode, BindingId);
                else
                    Api.DrawTransformFeedbackStream(mode, BindingId, stream);
            }
            else
            {
                if (stream == 0u)
                    Api.DrawTransformFeedbackInstanced(mode, BindingId, instanceCount);
                else
                    Api.DrawTransformFeedbackStreamInstanced(mode, BindingId, stream, instanceCount);
            }

            return XRTransformFeedbackOperationResult.Success(EXRTransformFeedbackOperation.DrawCaptured, "OpenGL");
        }

        XRTransformFeedbackOperationResult IXRTransformFeedbackApi.DrawTransformFeedbackIndirectByteCount(
            XRDataBuffer? counterBuffer,
            ulong counterBufferOffset,
            uint counterOffset,
            uint vertexStride,
            uint instanceCount,
            uint firstInstance)
            => XRTransformFeedbackOperationResult.Unsupported(
                EXRTransformFeedbackOperation.DrawIndirectByteCount,
                "OpenGL",
                "OpenGL exposes transform-feedback-object draw commands, not Vulkan's byte-count counter draw command.");

        private void Bind()
            => Api.BindTransformFeedback(GLEnum.TransformFeedback, BindingId);

        private bool TryBindFeedbackBuffer(ulong offset, ulong? size, out string? failure)
        {
            failure = null;
            Generate();
            Bind();

            if (Renderer.GetOrCreateAPIRenderObject(Data.FeedbackBuffer, generateNow: true) is not GLDataBuffer buffer)
            {
                failure = "Failed to resolve OpenGL transform feedback buffer.";
                return false;
            }

            if (!buffer.IsReadyForRendering)
                buffer.EnsureStorageAllocatedForGpuCopy();

            if (size.HasValue)
            {
                if (size.Value == 0ul)
                {
                    failure = "Transform feedback buffer range size must be greater than zero.";
                    return false;
                }

                if (offset > (ulong)nint.MaxValue || size.Value > (ulong)nuint.MaxValue)
                {
                    failure = "Transform feedback buffer range exceeds the current platform pointer size.";
                    return false;
                }

                Api.BindBufferRange(
                    GLEnum.TransformFeedbackBuffer,
                    Data.BindingLocation,
                    buffer.BindingId,
                    (nint)offset,
                    (nuint)size.Value);
            }
            else
            {
                Api.BindBufferBase(GLEnum.TransformFeedbackBuffer, Data.BindingLocation, buffer.BindingId);
            }

            return true;
        }

        private bool TryGetPrimitiveMode(out GLEnum mode, out string? failure)
        {
            failure = null;
            switch (Data.PrimitiveType)
            {
                case EPrimitiveType.Points:
                    mode = GLEnum.Points;
                    return true;
                case EPrimitiveType.Lines:
                case EPrimitiveType.LineLoop:
                case EPrimitiveType.LineStrip:
                case EPrimitiveType.LinesAdjacency:
                case EPrimitiveType.LineStripAdjacency:
                    mode = GLEnum.Lines;
                    return true;
                case EPrimitiveType.Triangles:
                case EPrimitiveType.TriangleStrip:
                case EPrimitiveType.TriangleFan:
                case EPrimitiveType.TrianglesAdjacency:
                case EPrimitiveType.TriangleStripAdjacency:
                    mode = GLEnum.Triangles;
                    return true;
                default:
                    mode = default;
                    failure = $"OpenGL transform feedback begin mode must resolve to points, lines, or triangles; got {Data.PrimitiveType}.";
                    return false;
            }
        }

        private void Unbind()
            => Api.BindTransformFeedback(GLEnum.TransformFeedback, 0);
    }
}
