using NUnit.Framework;
using Shouldly;
using XREngine.Rendering;

namespace XREngine.UnitTests.Rendering;

[TestFixture]
[NonParallelizable]
public sealed class TextureResidencyRequestTests
{
    private TextureResidencyTestHost _host = null!;

    [SetUp]
    public void SetUp()
        => _host = new TextureResidencyTestHost();

    [TearDown]
    public void TearDown()
        => _host.Dispose();

    [Test]
    public void Request_PromotesTextureWithNoRenderUsage()
    {
        ImportedTextureStreamingManager manager = _host.Manager;
        (XRTexture2D texture, ImportedTextureStreamingRecord record) = _host.AddTexture("unused");

        manager.Evaluate(101);
        _host.Backend.Loads.ShouldBeEmpty();

        List<TextureResidencyTicket> completed = [];
        manager.ResidencyRequestCompleted += completed.Add;
        TextureResidencyTicket ticket = manager.RequestResidency(texture, targetMip: 0, deadlineFrame: long.MaxValue, JobPriority.Normal, generation: 1);
        ticket.IsValid.ShouldBeTrue();

        manager.Evaluate(102);

        _host.Backend.Loads.Count.ShouldBe(1);
        _host.Backend.Loads[0].MaxResidentDimension.ShouldBe(1024u);
        manager.GetResidencyRequestState(ticket).ShouldBe(ETextureResidencyRequestState.Pending);

        _host.Backend.CompleteLast().ShouldBeTrue();
        manager.Evaluate(103);

        lock (record.Sync)
            record.ResidentMaxDimension.ShouldBe(1024u);
        manager.GetResidencyRequestState(ticket).ShouldBe(ETextureResidencyRequestState.Resident);
        completed.ShouldBe([ticket]);
    }

    [Test]
    public void Request_DoesNotCountAsVisibilityOrSetVisiblePin()
    {
        ImportedTextureStreamingManager manager = _host.Manager;
        (XRTexture2D texture, ImportedTextureStreamingRecord record) = _host.AddTexture("not-visible");
        manager.RequestResidency(texture, targetMip: 1, deadlineFrame: long.MaxValue, JobPriority.Normal, generation: 1);

        manager.Evaluate(102);
        lock (record.Sync)
        {
            record.PendingMaxDimension.ShouldBe(512u);
            record.PendingTransitionRequestDriven.ShouldBeTrue();
        }

        _host.Backend.CompleteLast().ShouldBeTrue();

        lock (record.Sync)
        {
            record.ResidentMaxDimension.ShouldBe(512u);
            record.LastVisibleFrameId.ShouldBe(long.MinValue);
            record.LastBoundFrameId.ShouldBe(long.MinValue);
            record.PinUntilFrameId.ShouldBe(long.MinValue);
            record.PromotionCooldownUntilFrameId.ShouldBe(102L + TextureStreamingBudgetLimits.Default.PromotionCooldownFrames);
        }
    }

    [Test]
    public void CanceledRequest_DoesNotPublish()
    {
        ImportedTextureStreamingManager manager = _host.Manager;
        (XRTexture2D texture, ImportedTextureStreamingRecord record) = _host.AddTexture("canceled");
        TextureResidencyTicket ticket = manager.RequestResidency(texture, targetMip: 0, deadlineFrame: long.MaxValue, JobPriority.High, generation: 3);
        manager.Evaluate(102);
        FakeTextureStreamingResidencyBackend.ScheduledLoad load = _host.Backend.Loads.ShouldHaveSingleItem();

        manager.CancelResidency(ticket).ShouldBeTrue();

        load.CancellationToken.IsCancellationRequested.ShouldBeTrue();
        load.ShouldAcceptResult!().ShouldBeFalse();
        _host.Backend.CompleteLast().ShouldBeFalse();

        // A backend that reports a late completion of the cancelled generation still does not publish it.
        load.OnFinished!(texture);
        lock (record.Sync)
        {
            record.ResidentMaxDimension.ShouldBe(64u);
            record.PendingMaxDimension.ShouldBe(0u);
            record.PublicationEligibleGeneration.ShouldBe(0L);
        }

        manager.GetResidencyRequestState(ticket).ShouldBe(ETextureResidencyRequestState.None);
        manager.CancelResidency(ticket).ShouldBeFalse();

        // With no request and no render usage, the next evaluation queues nothing.
        manager.Evaluate(105);
        _host.Backend.Loads.Count.ShouldBe(1);
    }

    [Test]
    public void CancelOfResidentRequest_DoesNotForceDemotion()
    {
        ImportedTextureStreamingManager manager = _host.Manager;
        (XRTexture2D texture, ImportedTextureStreamingRecord record) = _host.AddTexture("released");
        TextureResidencyTicket ticket = manager.RequestResidency(texture, targetMip: 0, deadlineFrame: long.MaxValue, JobPriority.Normal, generation: 1);
        manager.Evaluate(102);
        _host.Backend.CompleteLast().ShouldBeTrue();
        manager.Evaluate(103);
        manager.GetResidencyRequestState(ticket).ShouldBe(ETextureResidencyRequestState.Resident);

        manager.CancelResidency(ticket).ShouldBeTrue();

        lock (record.Sync)
        {
            record.ResidentMaxDimension.ShouldBe(1024u);
            record.RequestedMaxDimension.ShouldBe(0u);
        }
    }

    [Test]
    public void DefaultProfile_KeepsTodaysLimits()
    {
        TextureStreamingBudgetLimits.Default.ShouldBe(new TextureStreamingBudgetLimits(
            MaxResidentTransitionsPerFrame: 2,
            MaxPromotionTransitionsPerFrame: 1,
            MaxPromotionBytesPerFrame: 32L * 1024L * 1024L,
            PromotionCooldownFrames: 6,
            PromotionFadeFrames: 90));

        ImportedTextureStreamingManager manager = _host.Manager;
        manager.BudgetProfile.ShouldBe(ETextureStreamingBudgetProfile.Default);
        manager.Evaluate(101);
        manager.ActiveBudgetLimits.ShouldBe(TextureStreamingBudgetLimits.Default);

        // Two requested textures: the default profile queues one promotion in a frame.
        (XRTexture2D first, _) = _host.AddTexture("first");
        (XRTexture2D second, _) = _host.AddTexture("second");
        manager.RequestResidency(first, 0, long.MaxValue, JobPriority.Normal, 1);
        manager.RequestResidency(second, 0, long.MaxValue, JobPriority.Normal, 1);
        manager.Evaluate(102);
        _host.Backend.Loads.Count.ShouldBe(1);
    }

    [Test]
    public void CutsceneProfile_FadesInAndOut()
    {
        RuntimeEngine.Rendering.EngineSettings settings = _host.Settings;
        settings.TextureStreamingBudgetProfileFadeFrames = 2;
        settings.TextureStreamingCutsceneMaxTransitionsPerFrame = 6;
        settings.TextureStreamingCutsceneMaxPromotionsPerFrame = 4;
        settings.TextureStreamingCutsceneMaxPromotionMegabytesPerFrame = 96;
        settings.TextureStreamingCutscenePromotionCooldownFrames = 2;
        settings.TextureStreamingCutscenePromotionFadeFrames = 12;
        TextureStreamingBudgetLimits cutscene = new(6, 4, 96L * 1024L * 1024L, 2, 12);
        ImportedTextureStreamingManager.ResolveCutsceneBudgetLimits(settings).ShouldBe(cutscene);

        ImportedTextureStreamingManager manager = _host.Manager;
        manager.SetBudgetProfile(ETextureStreamingBudgetProfile.Cutscene);
        manager.Evaluate(101);
        manager.BudgetProfileBlend.ShouldBe(0.5f);
        TextureStreamingBudgetLimits halfway = manager.ActiveBudgetLimits;
        halfway.MaxPromotionTransitionsPerFrame.ShouldBeInRange(2, 3);
        halfway.PromotionFadeFrames.ShouldBeInRange(12, 90);

        manager.Evaluate(102);
        manager.ActiveBudgetLimits.ShouldBe(cutscene);

        // The cutscene profile admits more promotions in one frame.
        (XRTexture2D first, _) = _host.AddTexture("first");
        (XRTexture2D second, _) = _host.AddTexture("second");
        (XRTexture2D third, _) = _host.AddTexture("third");
        manager.RequestResidency(first, 2, long.MaxValue, JobPriority.Normal, 1);
        manager.RequestResidency(second, 2, long.MaxValue, JobPriority.Normal, 1);
        manager.RequestResidency(third, 2, long.MaxValue, JobPriority.Normal, 1);
        manager.Evaluate(103);
        _host.Backend.Loads.Count.ShouldBe(3);

        manager.SetBudgetProfile(ETextureStreamingBudgetProfile.Default);
        manager.Evaluate(104);
        manager.ActiveBudgetLimits.ShouldNotBe(TextureStreamingBudgetLimits.Default);
        manager.Evaluate(105);
        manager.ActiveBudgetLimits.ShouldBe(TextureStreamingBudgetLimits.Default);
    }

    [Test]
    public void CutsceneProfile_StillFitsRequestsToVramBudget()
    {
        _host.Settings.TextureStreamingBudgetProfileFadeFrames = 0;
        ImportedTextureStreamingManager manager = _host.Manager;
        manager.SetBudgetProfile(ETextureStreamingBudgetProfile.Cutscene);
        (XRTexture2D texture, ImportedTextureStreamingRecord record) = _host.AddTexture("budget");

        // Only the resident preview uses VRAM. The budget holds a 256 texture, not a 512 or 1024 texture.
        _host.Host.TrackedVramBytes = FakeTextureStreamingResidencyBackend.BytesFor(64u);
        _host.Host.TrackedVramBudgetBytes = FakeTextureStreamingResidencyBackend.BytesFor(256u) + 1024L;

        TextureResidencyTicket ticket = manager.RequestResidency(texture, targetMip: 0, deadlineFrame: 0L, JobPriority.Highest, generation: 1);
        manager.Evaluate(102);

        manager.ActiveBudgetLimits.ShouldBe(ImportedTextureStreamingManager.ResolveCutsceneBudgetLimits(_host.Settings));
        _host.Backend.Loads.ShouldHaveSingleItem().MaxResidentDimension.ShouldBe(256u);

        _host.Backend.CompleteLast().ShouldBeTrue();
        manager.Evaluate(103);
        lock (record.Sync)
            record.ResidentMaxDimension.ShouldBe(256u);
        manager.GetResidencyRequestState(ticket).ShouldBe(ETextureResidencyRequestState.Pending);
    }

    [Test]
    public void Request_RejectsUntrackedTexture()
    {
        XRTexture2D texture = new() { Name = "untracked" };

        _host.Manager.RequestResidency(texture, 0, long.MaxValue, JobPriority.Normal, 1).ShouldBe(TextureResidencyTicket.None);
    }

    [Test]
    public void OverdueRequest_UsesHighestPriority()
    {
        ImportedTextureStreamingManager manager = _host.Manager;
        (XRTexture2D texture, _) = _host.AddTexture("overdue");
        manager.RequestResidency(texture, targetMip: 0, deadlineFrame: 50L, JobPriority.Normal, generation: 1);

        manager.Evaluate(102);

        _host.Backend.Loads.ShouldHaveSingleItem().Priority.ShouldBe(JobPriority.Highest);
    }
}
