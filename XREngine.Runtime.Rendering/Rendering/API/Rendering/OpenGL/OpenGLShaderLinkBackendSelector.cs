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
    SourceUnavailable,
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
    bool PreferSharedContextForLargeSource,
    bool HashPreviouslyFailed,
    bool AllowSynchronousSourceLink,
    bool ForceSynchronousSourceRetry = false);

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

        if (context.ForceSynchronousSourceRetry)
        {
            return new OpenGLShaderLinkBackendSelection(
                EOpenGLProgramBuildLane.SynchronousSource,
                "async source link timed out; retrying synchronously",
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
            return SelectSynchronousSource(
                context,
                context.Strategy == EOpenGLShaderLinkStrategy.Synchronous
                    ? "strategy requests synchronous source compile/link"
                    : "async source compilation is disabled");
        }

        if (context.IsKnownAsyncLinkHazard)
        {
            // Hazardous shapes (single-stage separable, compute) are always denied
            // the driver-parallel lane — that is the documented NVIDIA parallel-link
            // worker hang. They may still use the shared-context source lane: the
            // link runs on a worker thread on a separate GL context, so even an
            // expensive cold link does not freeze the render thread. The queue
            // applies its own final guard for shapes (e.g. compute) that should
            // not be linked on the worker context.
            if (context.SharedContextCompileAvailable && context.CompileInputsReady)
            {
                return SelectSharedContextSource(
                    context,
                    "known async-link hazard; routed to shared-context lane to avoid render-thread stall");
            }

            return SelectSynchronousSource(
                context,
                "known async-link hazard; no shared-context lane available");
        }

        if (context.PreferSharedContextForLargeSource &&
            context.SharedContextCompileAvailable &&
            context.CompileInputsReady)
        {
            return SelectSharedContextSource(
                context,
                "large source program routed to shared-context lane to avoid driver-parallel timeout");
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

            _ => SelectSynchronousSource(context, "no async source lane is available"),
        };
    }

    private static OpenGLShaderLinkBackendSelection SelectSynchronousSource(
        OpenGLShaderLinkBackendContext context,
        string reason)
    {
        if (context.AllowSynchronousSourceLink)
        {
            return new OpenGLShaderLinkBackendSelection(
                EOpenGLProgramBuildLane.SynchronousSource,
                reason,
                IsAsync: false);
        }

        return new OpenGLShaderLinkBackendSelection(
            EOpenGLProgramBuildLane.SourceUnavailable,
            $"{reason}; synchronous source linking is disabled",
            IsAsync: true);
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
