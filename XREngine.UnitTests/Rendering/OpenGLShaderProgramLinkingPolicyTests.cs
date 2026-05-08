using NUnit.Framework;
using Shouldly;
using XREngine.Rendering.OpenGL;

namespace XREngine.UnitTests.Rendering;

[TestFixture]
public sealed class OpenGLShaderProgramLinkingPolicyTests
{
    [Test]
    public void Auto_PrefersDriverParallel_WhenProbeSucceeded()
    {
        OpenGLShaderLinkBackendSelection selection = OpenGLShaderLinkBackendSelector.Select(CreateContext(
            strategy: EOpenGLShaderLinkStrategy.Auto,
            driverParallelAvailable: true,
            sharedContextCompileAvailable: true));

        selection.Lane.ShouldBe(EOpenGLProgramBuildLane.DriverParallelSource);
        selection.IsAsync.ShouldBeTrue();
    }

    [Test]
    public void Auto_FallsBackToSharedContext_WhenDriverParallelUnavailable()
    {
        OpenGLShaderLinkBackendSelection selection = OpenGLShaderLinkBackendSelector.Select(CreateContext(
            strategy: EOpenGLShaderLinkStrategy.Auto,
            driverParallelAvailable: false,
            sharedContextCompileAvailable: true));

        selection.Lane.ShouldBe(EOpenGLProgramBuildLane.SharedContextSource);
        selection.IsAsync.ShouldBeTrue();
    }

    [Test]
    public void KnownHazards_BypassDriverParallel_ButPreferSharedContext()
    {
        OpenGLShaderLinkBackendSelection selection = OpenGLShaderLinkBackendSelector.Select(CreateContext(
            strategy: EOpenGLShaderLinkStrategy.Auto,
            driverParallelAvailable: true,
            sharedContextCompileAvailable: true,
            isKnownAsyncLinkHazard: true));

        // Hazards must never use driver-parallel, but the shared-context lane
        // links on a worker thread on a separate GL context so a slow cold
        // link does not freeze the render thread.
        selection.Lane.ShouldBe(EOpenGLProgramBuildLane.SharedContextSource);
        selection.IsAsync.ShouldBeTrue();
        selection.Reason.ShouldContain("hazard");
    }

    [Test]
    public void KnownHazards_Defer_WhenSharedContextUnavailable()
    {
        OpenGLShaderLinkBackendSelection selection = OpenGLShaderLinkBackendSelector.Select(CreateContext(
            strategy: EOpenGLShaderLinkStrategy.Auto,
            driverParallelAvailable: true,
            sharedContextCompileAvailable: false,
            isKnownAsyncLinkHazard: true));

        selection.Lane.ShouldBe(EOpenGLProgramBuildLane.SourceUnavailable);
        selection.IsAsync.ShouldBeTrue();
        selection.Reason.ShouldContain("hazard");
        selection.Reason.ShouldContain("synchronous source linking is disabled");
    }

    [Test]
    public void KnownHazards_Defer_WhenCompileInputsNotReady()
    {
        OpenGLShaderLinkBackendSelection selection = OpenGLShaderLinkBackendSelector.Select(CreateContext(
            strategy: EOpenGLShaderLinkStrategy.Auto,
            driverParallelAvailable: true,
            sharedContextCompileAvailable: true,
            compileInputsReady: false,
            isKnownAsyncLinkHazard: true));

        selection.Lane.ShouldBe(EOpenGLProgramBuildLane.SourceUnavailable);
        selection.IsAsync.ShouldBeTrue();
    }

    [Test]
    public void SynchronousStrategy_StillAllowsConfiguredAsyncBinaryUploads()
    {
        OpenGLShaderLinkBackendSelection selection = OpenGLShaderLinkBackendSelector.Select(CreateContext(
            strategy: EOpenGLShaderLinkStrategy.Synchronous,
            hasBinaryCacheHit: true,
            binaryUploadAvailable: true,
            binaryUploadCanEnqueue: true));

        selection.Lane.ShouldBe(EOpenGLProgramBuildLane.BinaryUploadAsync);
        selection.IsAsync.ShouldBeTrue();
    }

    [Test]
    public void BinaryUploadQueueBackpressure_IsReportedAsBackpressure()
    {
        OpenGLShaderLinkBackendSelection selection = OpenGLShaderLinkBackendSelector.Select(CreateContext(
            hasBinaryCacheHit: true,
            binaryUploadAvailable: true,
            binaryUploadCanEnqueue: false));

        selection.Lane.ShouldBe(EOpenGLProgramBuildLane.BinaryQueueBackpressure);
        selection.IsBackpressure.ShouldBeTrue();
    }

    [Test]
    public void CacheKey_ChangesWithStageTopology()
    {
        ShaderBinaryRuntimeFingerprint fingerprint = CreateFingerprint();

        string vertexFragment = ComputeKey(stageTopology: "Vertex+Fragment", fingerprint: fingerprint);
        string vertexGeometryFragment = ComputeKey(stageTopology: "Vertex+Geometry+Fragment", fingerprint: fingerprint);

        vertexGeometryFragment.ShouldNotBe(vertexFragment);
    }

    [Test]
    public void CacheKey_ChangesWithRuntimeFingerprint()
    {
        string nvidia = ComputeKey(fingerprint: new ShaderBinaryRuntimeFingerprint("4.6", "NVIDIA", "RTX", "4.60"));
        string amd = ComputeKey(fingerprint: new ShaderBinaryRuntimeFingerprint("4.6", "AMD", "RX", "4.60"));

        amd.ShouldNotBe(nvidia);
    }

    [Test]
    public void CacheKey_ChangesWithVariantMetadata()
    {
        ShaderBinaryRuntimeFingerprint fingerprint = CreateFingerprint();

        string baseVariant = ComputeKey(variantKind: "UberForward", variantHash: 1, fingerprint: fingerprint);
        string toggledVariant = ComputeKey(variantKind: "UberForward", variantHash: 2, fingerprint: fingerprint);

        toggledVariant.ShouldNotBe(baseVariant);
    }

    [Test]
    public void CacheKey_ChangesWithSchemaVersion()
    {
        ShaderBinaryRuntimeFingerprint fingerprint = CreateFingerprint();

        string current = ComputeKey(schemaVersion: OpenGLRenderer.GLRenderProgram.BinaryCacheSchemaVersion, fingerprint: fingerprint);
        string future = ComputeKey(schemaVersion: OpenGLRenderer.GLRenderProgram.BinaryCacheSchemaVersion + 1, fingerprint: fingerprint);

        future.ShouldNotBe(current);
    }

    // Phase 2 regression guard: the binary-cache-hit fast path must not depend on
    // CompileInputsReady. PrepareLinkData skips PrepareCompileInputs() on cache hits,
    // so CompileInputsReady will be false on that path. The selector must still pick
    // the binary upload lane regardless.
    [Test]
    public void BinaryCacheHit_SelectsBinaryUploadLane_EvenWithoutCompileInputs()
    {
        OpenGLShaderLinkBackendSelection asyncSelection = OpenGLShaderLinkBackendSelector.Select(CreateContext(
            hasBinaryCacheHit: true,
            binaryUploadAvailable: true,
            binaryUploadCanEnqueue: true,
            compileInputsReady: false));

        asyncSelection.Lane.ShouldBe(EOpenGLProgramBuildLane.BinaryUploadAsync);
        asyncSelection.IsAsync.ShouldBeTrue();

        OpenGLShaderLinkBackendSelection syncSelection = OpenGLShaderLinkBackendSelector.Select(CreateContext(
            hasBinaryCacheHit: true,
            asyncProgramBinaryUpload: false,
            binaryUploadAvailable: false,
            compileInputsReady: false));

        syncSelection.Lane.ShouldBe(EOpenGLProgramBuildLane.BinaryUploadSynchronous);
        syncSelection.IsAsync.ShouldBeFalse();
    }

    private static OpenGLShaderLinkBackendContext CreateContext(
        EOpenGLShaderLinkStrategy strategy = EOpenGLShaderLinkStrategy.Auto,
        bool asyncProgramCompilation = true,
        bool allowBinaryProgramCaching = true,
        bool asyncProgramBinaryUpload = true,
        bool hasBinaryCacheHit = false,
        bool binaryUploadAvailable = false,
        bool binaryUploadCanEnqueue = false,
        bool driverParallelAvailable = false,
        bool sharedContextCompileAvailable = false,
        bool sharedContextCompileCanEnqueue = true,
        bool compileInputsReady = true,
        bool isKnownAsyncLinkHazard = false,
        bool hashPreviouslyFailed = false,
        bool allowSynchronousSourceLink = false)
        => new(
            strategy,
            asyncProgramCompilation,
            allowBinaryProgramCaching,
            asyncProgramBinaryUpload,
            hasBinaryCacheHit,
            binaryUploadAvailable,
            binaryUploadCanEnqueue,
            driverParallelAvailable,
            sharedContextCompileAvailable,
            sharedContextCompileCanEnqueue,
            compileInputsReady,
            isKnownAsyncLinkHazard,
            hashPreviouslyFailed,
            allowSynchronousSourceLink);

    private static ShaderBinaryRuntimeFingerprint CreateFingerprint()
        => new("4.6", "TestVendor", "TestRenderer", "4.60");

    private static string ComputeKey(
        int schemaVersion = OpenGLRenderer.GLRenderProgram.BinaryCacheSchemaVersion,
        ulong sourceHash = 12345,
        string stageTopology = "Vertex+Fragment",
        bool separable = false,
        string? variantKind = null,
        ulong variantHash = 0,
        string? binaryCachePolicy = "Default",
        ShaderBinaryRuntimeFingerprint? fingerprint = null)
        => OpenGLRenderer.GLRenderProgram.ComputeBinaryCacheKeyHash(new ShaderBinaryCacheKey(
            schemaVersion,
            sourceHash,
            stageTopology,
            separable,
            variantKind,
            variantHash,
            binaryCachePolicy,
            fingerprint ?? CreateFingerprint()));
}
