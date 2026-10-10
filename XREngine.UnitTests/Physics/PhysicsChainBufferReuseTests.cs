using NUnit.Framework;
using Shouldly;
using XREngine.Data.Rendering;
using XREngine.Rendering;
using XREngine.Rendering.Compute;

namespace XREngine.UnitTests.Physics;

/// <summary>
/// Tests for <see cref="PhysicsChainBufferReuse"/>: native reuse of physics chain buffers and the
/// release of unprepared bounds buffers when a free output page is reclaimed.
/// </summary>
[TestFixture]
public sealed class PhysicsChainBufferReuseTests
{
    private const string OutputPagesPath =
        "XREngine.Runtime.Rendering/Rendering/PhysicsCompute/GPUPhysicsChainDispatcher.OutputPages.cs";

    private static readonly EGpuBufferContentReuseStatus[] BlockingStatuses =
    [
        EGpuBufferContentReuseStatus.AwaitingSubmission,
        EGpuBufferContentReuseStatus.PendingCompletion,
        EGpuBufferContentReuseStatus.Superseded,
        EGpuBufferContentReuseStatus.DeviceLost,
    ];

    private static readonly EGpuBufferContentReuseStatus[] NotReadyStatuses =
        [.. BlockingStatuses, EGpuBufferContentReuseStatus.Unsupported];

    private readonly List<XRDataBuffer> _buffers = [];
    private readonly TestWrapperOwner _owner = new();

    [TearDown]
    public void TearDown()
    {
        foreach (XRDataBuffer buffer in _buffers)
            buffer.Dispose();
        _buffers.Clear();
    }

    [Test]
    public void IsReady_MissingOrUnusedBuffer_DoesNotQueryBackend()
    {
        var capability = new StubReuseCapability();
        XRDataBuffer unused = CreateBuffer("Unused", generated: false);

        PhysicsChainBufferReuse.IsReady(capability, null).ShouldBeTrue();
        PhysicsChainBufferReuse.IsReady(capability, unused).ShouldBeTrue();
        capability.QueryCount.ShouldBe(0);
    }

    [TestCase(EGpuBufferContentReuseStatus.Ready, true)]
    [TestCase(EGpuBufferContentReuseStatus.AwaitingSubmission, false)]
    [TestCase(EGpuBufferContentReuseStatus.PendingCompletion, false)]
    [TestCase(EGpuBufferContentReuseStatus.Superseded, false)]
    [TestCase(EGpuBufferContentReuseStatus.DeviceLost, false)]
    [TestCase(EGpuBufferContentReuseStatus.Unsupported, false)]
    public void IsReady_GeneratedBuffer_RequiresReadyStatus(EGpuBufferContentReuseStatus status, bool expected)
    {
        var capability = new StubReuseCapability();
        XRDataBuffer buffer = CreateBuffer("Generated", generated: true);
        capability.Set(buffer, status);

        PhysicsChainBufferReuse.IsReady(capability, buffer).ShouldBe(expected);
    }

    [Test]
    public void FreePage_AllBuffersReady_IsReusableWithoutRelease()
    {
        var capability = new StubReuseCapability();
        PageBuffers page = CreatePage();

        Evaluate(capability, page, out bool releaseBoundsAtlas, out bool releaseSlotMetadata).ShouldBeTrue();

        releaseBoundsAtlas.ShouldBeFalse();
        releaseSlotMetadata.ShouldBeFalse();
    }

    [TestCase(true, false)]
    [TestCase(false, true)]
    [TestCase(true, true)]
    public void FreePage_UnsupportedBoundsBuffer_IsReleasedAndPageIsReusable(
        bool boundsAtlasUnsupported, bool slotMetadataUnsupported)
    {
        var capability = new StubReuseCapability();
        PageBuffers page = CreatePage();
        if (boundsAtlasUnsupported)
            capability.Set(page.BoundsAtlas, EGpuBufferContentReuseStatus.Unsupported);
        if (slotMetadataUnsupported)
            capability.Set(page.SlotMetadata, EGpuBufferContentReuseStatus.Unsupported);

        Evaluate(capability, page, out bool releaseBoundsAtlas, out bool releaseSlotMetadata).ShouldBeTrue();

        releaseBoundsAtlas.ShouldBe(boundsAtlasUnsupported);
        releaseSlotMetadata.ShouldBe(slotMetadataUnsupported);
    }

    [Test]
    public void FreePage_BoundsBufferInUse_BlocksReuseWithoutRelease(
        [ValueSource(nameof(BlockingStatuses))] EGpuBufferContentReuseStatus status,
        [Values] bool onSlotMetadata)
    {
        var capability = new StubReuseCapability();
        PageBuffers page = CreatePage();
        capability.Set(onSlotMetadata ? page.SlotMetadata : page.BoundsAtlas, status);
        // The other bounds buffer is unprepared, so a release must wait for the whole page.
        capability.Set(onSlotMetadata ? page.BoundsAtlas : page.SlotMetadata,
            EGpuBufferContentReuseStatus.Unsupported);

        Evaluate(capability, page, out bool releaseBoundsAtlas, out bool releaseSlotMetadata).ShouldBeFalse();

        releaseBoundsAtlas.ShouldBeFalse();
        releaseSlotMetadata.ShouldBeFalse();
    }

    [Test]
    public void FreePage_PaletteNotReady_BlocksReuseWithoutRelease(
        [ValueSource(nameof(NotReadyStatuses))] EGpuBufferContentReuseStatus status,
        [Values] bool onPreviousPalette)
    {
        var capability = new StubReuseCapability();
        PageBuffers page = CreatePage();
        capability.Set(page.BoundsAtlas, EGpuBufferContentReuseStatus.Unsupported);
        capability.Set(page.SlotMetadata, EGpuBufferContentReuseStatus.Unsupported);
        capability.Set(onPreviousPalette ? page.PreviousPalette : page.CurrentPalette, status);

        Evaluate(capability, page, out bool releaseBoundsAtlas, out bool releaseSlotMetadata).ShouldBeFalse();

        releaseBoundsAtlas.ShouldBeFalse();
        releaseSlotMetadata.ShouldBeFalse();
    }

    [Test]
    public void FreePage_UnsupportedBoundsAtlas_IsReleasedOnceOtherBuffersPermitReuse()
    {
        var capability = new StubReuseCapability();
        PageBuffers page = CreatePage();
        capability.Set(page.BoundsAtlas, EGpuBufferContentReuseStatus.Unsupported);
        capability.Set(page.CurrentPalette, EGpuBufferContentReuseStatus.PendingCompletion);

        Evaluate(capability, page, out bool releaseBoundsAtlas, out _).ShouldBeFalse();
        releaseBoundsAtlas.ShouldBeFalse();

        capability.Set(page.CurrentPalette, EGpuBufferContentReuseStatus.Ready);

        Evaluate(capability, page, out releaseBoundsAtlas, out bool releaseSlotMetadata).ShouldBeTrue();
        releaseBoundsAtlas.ShouldBeTrue();
        releaseSlotMetadata.ShouldBeFalse();
    }

    [Test]
    public void FreePage_MissingBoundsBuffers_IsReusableWithoutRelease()
    {
        var capability = new StubReuseCapability();
        PageBuffers page = CreatePage() with { BoundsAtlas = null, SlotMetadata = null };

        Evaluate(capability, page, out bool releaseBoundsAtlas, out bool releaseSlotMetadata).ShouldBeTrue();

        releaseBoundsAtlas.ShouldBeFalse();
        releaseSlotMetadata.ShouldBeFalse();
    }

    /// <summary>
    /// A live producer renderer is required to drive the dispatcher page ring, so this contract
    /// checks the source order that keeps published, history, retained, and fenced pages out of
    /// the release path.
    /// </summary>
    [Test]
    public void Dispatcher_ReleasesBoundsBuffersOnlyOnFreePagesAfterFenceAndBufferChecks()
    {
        string source = SourceContractWorkspace.ReadExactFile(OutputPagesPath);
        string begin = SliceMethod(source, "private bool TryBeginOutputPage(", "private void RecordOutputPageBusyAttempt(");
        string canReuse = SliceMethod(source, "private bool CanReuseOutputPage(", "private void ReleaseUnpreparedBoundsBuffer(");

        int skipRing = begin.IndexOf(
            "if (index == _publishedOutputPageIndex || index == _historyOutputPageIndex)\n                    continue;",
            StringComparison.Ordinal);
        int reuseCheck = begin.IndexOf("page.RetainCount != 0 || !CanReuseOutputPage(backend, page)", StringComparison.Ordinal);
        skipRing.ShouldBeGreaterThanOrEqualTo(0);
        reuseCheck.ShouldBeGreaterThan(skipRing);

        int awaitingSubmission = canReuse.IndexOf(
            "if (status == EGpuFenceSubmissionStatus.AwaitingSubmission)\n                return false;",
            StringComparison.Ordinal);
        int fencePoll = canReuse.IndexOf(
            "else if (page.ProducerFence.Poll() != EGpuFenceStatus.Signaled)\n                return false;",
            StringComparison.Ordinal);
        int evaluate = canReuse.IndexOf("PhysicsChainBufferReuse.TryEvaluateFreeOutputPage(", StringComparison.Ordinal);
        int firstRelease = canReuse.IndexOf("ReleaseUnpreparedBoundsBuffer(ref page.", StringComparison.Ordinal);
        awaitingSubmission.ShouldBeGreaterThanOrEqualTo(0);
        fencePoll.ShouldBeGreaterThan(awaitingSubmission);
        evaluate.ShouldBeGreaterThan(fencePoll);
        firstRelease.ShouldBeGreaterThan(evaluate);

        // The two calls in CanReuseOutputPage and the declaration are the only uses.
        CountOccurrences(canReuse, "ReleaseUnpreparedBoundsBuffer(ref page.").ShouldBe(2);
        CountOccurrences(source, "ReleaseUnpreparedBoundsBuffer(").ShouldBe(3);
    }

    private static bool Evaluate(StubReuseCapability capability, PageBuffers page,
        out bool releaseBoundsAtlas, out bool releaseSlotMetadata)
        => PhysicsChainBufferReuse.TryEvaluateFreeOutputPage(capability,
            page.BoundsAtlas, page.SlotMetadata, page.CurrentPalette, page.PreviousPalette,
            out releaseBoundsAtlas, out releaseSlotMetadata);

    private PageBuffers CreatePage()
        => new(
            CreateBuffer("BoundsAtlas", generated: true),
            CreateBuffer("SlotMetadata", generated: true),
            CreateBuffer("CurrentPalette", generated: true),
            CreateBuffer("PreviousPalette", generated: true));

    private XRDataBuffer CreateBuffer(string name, bool generated)
    {
        var buffer = new XRDataBuffer<uint>(name, EBufferTarget.ShaderStorageBuffer, 4u);
        _buffers.Add(buffer);
        if (!generated)
            return buffer;
        // A backend publishes an owner-first buffer at its first use, before it attaches a wrapper.
        buffer.EnsureOwnerFirstConstructionCompleted();
        buffer.AddWrapper(new GeneratedBufferWrapper(_owner));
        return buffer;
    }

    private static string SliceMethod(string source, string startToken, string endToken)
    {
        int start = source.IndexOf(startToken, StringComparison.Ordinal);
        start.ShouldBeGreaterThanOrEqualTo(0);
        int end = source.IndexOf(endToken, start, StringComparison.Ordinal);
        end.ShouldBeGreaterThan(start);
        return source[start..end];
    }

    private static int CountOccurrences(string source, string token)
    {
        int count = 0;
        for (int index = source.IndexOf(token, StringComparison.Ordinal); index >= 0;
            index = source.IndexOf(token, index + token.Length, StringComparison.Ordinal))
            ++count;
        return count;
    }

    private sealed record PageBuffers(
        XRDataBuffer? BoundsAtlas,
        XRDataBuffer? SlotMetadata,
        XRDataBuffer? CurrentPalette,
        XRDataBuffer? PreviousPalette);

    /// <summary>Reports a configured status per buffer and <see cref="EGpuBufferContentReuseStatus.Ready"/> otherwise.</summary>
    private sealed class StubReuseCapability : IGpuBufferContentReuseCapability
    {
        private readonly Dictionary<XRDataBuffer, EGpuBufferContentReuseStatus> _statuses =
            new(System.Collections.Generic.ReferenceEqualityComparer.Instance);

        public int QueryCount { get; private set; }

        public void Set(XRDataBuffer? buffer, EGpuBufferContentReuseStatus status)
            => _statuses[buffer!] = status;

        public EGpuBufferContentReuseStatus QueryBufferContentReuse(XRDataBuffer buffer)
        {
            ++QueryCount;
            return _statuses.TryGetValue(buffer, out EGpuBufferContentReuseStatus status)
                ? status : EGpuBufferContentReuseStatus.Ready;
        }
    }

    /// <summary>Makes a buffer report a generated native object, as a buffer that a backend created does.</summary>
    private sealed class GeneratedBufferWrapper(IRenderApiWrapperOwner owner) : AbstractRenderAPIObject(owner)
    {
        public override bool IsGenerated => true;
        public override void Generate() { }
        public override void Destroy() { }
        public override string GetDescribingName() => nameof(GeneratedBufferWrapper);
    }

    private sealed class TestWrapperOwner : IRenderApiWrapperOwner
    {
        public string RenderApiWrapperOwnerName => nameof(TestWrapperOwner);

        public AbstractRenderAPIObject? GetOrCreateAPIRenderObject(GenericRenderObject renderObject, bool generateNow = false)
            => null;
    }
}
