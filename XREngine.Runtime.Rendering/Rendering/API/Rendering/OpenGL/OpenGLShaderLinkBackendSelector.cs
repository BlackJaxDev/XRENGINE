namespace XREngine.Rendering.OpenGL;

internal enum EOpenGLProgramBuildLane
{
    None,
    BinaryUploadAsync,
    BinaryUploadSynchronous,
    BinaryQueueBackpressure,
    DriverParallelSource,
    SharedContextSource,
    SharedContextQueueBackpressure,
    SynchronousSource,
    FailedHash,
}

internal readonly record struct OpenGLShaderLinkBackendSelection(
    EOpenGLProgramBuildLane Lane,
    string Reason,
    bool IsAsync,
    bool IsBackpressure = false);

internal readonly record struct OpenGLShaderLinkBackendContext(
    EOpenGLShaderLinkStrategy Strategy,
    bool AsyncProgramCompilation,
    bool AllowBinaryProgramCaching,
    bool AsyncProgramBinaryUpload,
    bool HasBinaryCacheHit,
    bool BinaryUploadAvailable,
    bool BinaryUploadCanEnqueue,
    bool DriverParallelAvailable,
    bool SharedContextCompileAvailable,
    bool SharedContextCompileCanEnqueue,
    bool CompileInputsReady,
    bool IsKnownAsyncLinkHazard,
    bool HashPreviouslyFailed);

internal static class OpenGLShaderLinkBackendSelector
{
    public static OpenGLShaderLinkBackendSelection Select(OpenGLShaderLinkBackendContext context)
    {
        if (context.HashPreviouslyFailed)
        {
            return new OpenGLShaderLinkBackendSelection(
                EOpenGLProgramBuildLane.FailedHash,
                "hash is marked failed",
                IsAsync: false);
        }

        if (context.AllowBinaryProgramCaching && context.HasBinaryCacheHit)
        {
            if (context.AsyncProgramBinaryUpload && context.BinaryUploadAvailable)
            {
                return context.BinaryUploadCanEnqueue
                    ? new OpenGLShaderLinkBackendSelection(
                        EOpenGLProgramBuildLane.BinaryUploadAsync,
                        "binary cache hit with async upload lane available",
                        IsAsync: true)
                    : new OpenGLShaderLinkBackendSelection(
                        EOpenGLProgramBuildLane.BinaryQueueBackpressure,
                        "binary upload queue is at capacity",
                        IsAsync: true,
                        IsBackpressure: true);
            }

            return new OpenGLShaderLinkBackendSelection(
                EOpenGLProgramBuildLane.BinaryUploadSynchronous,
                "binary cache hit without async upload lane",
                IsAsync: false);
        }

        if (!context.AsyncProgramCompilation || context.Strategy == EOpenGLShaderLinkStrategy.Synchronous)
        {
            return new OpenGLShaderLinkBackendSelection(
                EOpenGLProgramBuildLane.SynchronousSource,
                context.Strategy == EOpenGLShaderLinkStrategy.Synchronous
                    ? "strategy requests synchronous source compile/link"
                    : "async source compilation is disabled",
                IsAsync: false);
        }

        if (context.IsKnownAsyncLinkHazard)
        {
            return new OpenGLShaderLinkBackendSelection(
                EOpenGLProgramBuildLane.SynchronousSource,
                "known async-link hazard; driver-parallel and shared-source lanes are bypassed",
                IsAsync: false);
        }

        return context.Strategy switch
        {
            EOpenGLShaderLinkStrategy.DriverParallel when context.DriverParallelAvailable =>
                new OpenGLShaderLinkBackendSelection(
                    EOpenGLProgramBuildLane.DriverParallelSource,
                    "driver-parallel strategy is available",
                    IsAsync: true),

            EOpenGLShaderLinkStrategy.DriverParallel when context.SharedContextCompileAvailable && context.CompileInputsReady =>
                SelectSharedContextSource(context, "driver-parallel unavailable; shared-context fallback is available"),

            EOpenGLShaderLinkStrategy.SharedContext when context.SharedContextCompileAvailable && context.CompileInputsReady =>
                SelectSharedContextSource(context, "shared-context strategy is available"),

            EOpenGLShaderLinkStrategy.Auto when context.DriverParallelAvailable =>
                new OpenGLShaderLinkBackendSelection(
                    EOpenGLProgramBuildLane.DriverParallelSource,
                    "auto selected driver-parallel after startup probe",
                    IsAsync: true),

            EOpenGLShaderLinkStrategy.Auto when context.SharedContextCompileAvailable && context.CompileInputsReady =>
                SelectSharedContextSource(context, "auto selected shared-context fallback"),

            _ => new OpenGLShaderLinkBackendSelection(
                EOpenGLProgramBuildLane.SynchronousSource,
                "no async source lane is available",
                IsAsync: false),
        };
    }

    private static OpenGLShaderLinkBackendSelection SelectSharedContextSource(
        OpenGLShaderLinkBackendContext context,
        string availableReason)
    {
        return context.SharedContextCompileCanEnqueue
            ? new OpenGLShaderLinkBackendSelection(
                EOpenGLProgramBuildLane.SharedContextSource,
                availableReason,
                IsAsync: true)
            : new OpenGLShaderLinkBackendSelection(
                EOpenGLProgramBuildLane.SharedContextQueueBackpressure,
                "shared-context compile/link queue is at capacity",
                IsAsync: true,
                IsBackpressure: true);
    }
}
