using NUnit.Framework;
using Shouldly;
using XREngine.Rendering;
using XREngine.Rendering.Residency;

namespace XREngine.UnitTests.Rendering;

[TestFixture]
[NonParallelizable]
public sealed class TextureResidencySeamTests
{
    private TextureResidencyTestHost _host = null!;

    [SetUp]
    public void SetUp()
        => _host = new TextureResidencyTestHost();

    [TearDown]
    public void TearDown()
        => _host.Dispose();

    private ITextureResidencyRequests Seam
        => _host.Manager;

    [Test]
    public void BudgetLeases_KeepTheProfile_UntilTheLastDispose()
    {
        IDisposable first = Seam.AcquireBudgetProfile(ETextureStreamingBudgetProfile.Cutscene);
        IDisposable second = Seam.AcquireBudgetProfile(ETextureStreamingBudgetProfile.Cutscene);
        _host.Manager.BudgetProfileLeaseCount.ShouldBe(2);
        _host.Manager.BudgetProfile.ShouldBe(ETextureStreamingBudgetProfile.Cutscene);

        first.Dispose();
        first.Dispose();
        _host.Manager.BudgetProfileLeaseCount.ShouldBe(1);
        _host.Manager.BudgetProfile.ShouldBe(ETextureStreamingBudgetProfile.Cutscene);

        second.Dispose();
        _host.Manager.BudgetProfileLeaseCount.ShouldBe(0);
        _host.Manager.BudgetProfile.ShouldBe(ETextureStreamingBudgetProfile.Default);
    }

    [Test]
    public void BudgetLease_RejectsTheDefaultProfile()
    {
        Should.Throw<ArgumentOutOfRangeException>(() => Seam.AcquireBudgetProfile(ETextureStreamingBudgetProfile.Default));
        _host.Manager.BudgetProfileLeaseCount.ShouldBe(0);
    }

    [Test]
    public void RequestHandle_ReportsResidentState()
    {
        (XRTexture2D texture, _) = _host.AddTexture("resident");
        using TextureResidencyHandle handle = Seam.RequestResidency(texture, 0, long.MaxValue, JobPriority.Normal, 1).ShouldNotBeNull();
        handle.Ticket.IsValid.ShouldBeTrue();

        _host.Manager.Evaluate(102);
        handle.State.ShouldBe(ETextureResidencyRequestState.Pending);

        _host.Backend.CompleteLast().ShouldBeTrue();
        _host.Manager.Evaluate(103);
        handle.State.ShouldBe(ETextureResidencyRequestState.Resident);
    }

    [Test]
    public void DisposingTheHandle_CancelsTheRequest()
    {
        (XRTexture2D texture, ImportedTextureStreamingRecord record) = _host.AddTexture("disposed");
        TextureResidencyHandle handle = Seam.RequestResidency(texture, 0, long.MaxValue, JobPriority.Normal, 1).ShouldNotBeNull();
        _host.Manager.Evaluate(102);
        FakeTextureStreamingResidencyBackend.ScheduledLoad load = _host.Backend.Loads.ShouldHaveSingleItem();

        handle.Dispose();
        handle.Dispose();

        handle.IsDisposed.ShouldBeTrue();
        handle.State.ShouldBe(ETextureResidencyRequestState.None);
        _host.Manager.GetResidencyRequestState(handle.Ticket).ShouldBe(ETextureResidencyRequestState.None);
        _host.Manager.ActiveResidencyRequestCount.ShouldBe(0);
        load.CancellationToken.IsCancellationRequested.ShouldBeTrue();
        lock (record.Sync)
            record.RequestedMaxDimension.ShouldBe(0u);
    }

    [Test]
    public void RequestOfUntrackedTexture_ReturnsNoHandle()
    {
        XRTexture2D texture = new() { Name = "untracked" };

        Seam.RequestResidency(texture, 0, long.MaxValue, JobPriority.Normal, 1).ShouldBeNull();
    }

    [Test]
    public void RequestOfDestroyedTexture_EndsAtTheNextEvaluation()
    {
        (XRTexture2D texture, _) = _host.AddTexture("destroyed");
        TextureResidencyHandle handle = Seam.RequestResidency(texture, 0, long.MaxValue, JobPriority.Normal, 1).ShouldNotBeNull();
        _host.Manager.ActiveResidencyRequestCount.ShouldBe(1);

        texture.Destroy(true);
        _host.Manager.Evaluate(102);

        _host.Manager.ActiveResidencyRequestCount.ShouldBe(0);
        handle.State.ShouldBe(ETextureResidencyRequestState.None);
        handle.Dispose();
        handle.IsDisposed.ShouldBeTrue();
    }
}
