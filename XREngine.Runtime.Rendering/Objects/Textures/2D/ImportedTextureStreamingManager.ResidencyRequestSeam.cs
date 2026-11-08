using XREngine.Rendering.Residency;

namespace XREngine.Rendering;

/// <summary>
/// The explicit implementation of the public residency request seam. Request handles and budget profile leases own
/// their state, so callers that do not know each other cannot cancel or release the state of another caller.
/// </summary>
internal sealed partial class ImportedTextureStreamingManager : ITextureResidencyRequests
{
    private readonly Lock _budgetProfileLeaseSync = new();
    private int _cutsceneBudgetProfileLeaseCount;

    /// <summary>The number of live leases on the <see cref="ETextureStreamingBudgetProfile.Cutscene"/> profile.</summary>
    internal int BudgetProfileLeaseCount
    {
        get
        {
            lock (_budgetProfileLeaseSync)
                return _cutsceneBudgetProfileLeaseCount;
        }
    }

    event Action<TextureResidencyTicket>? ITextureResidencyRequests.ResidencyRequestCompleted
    {
        add => ResidencyRequestCompleted += value;
        remove => ResidencyRequestCompleted -= value;
    }

    long ITextureResidencyRequests.CurrentFrameId
        => CurrentFrameId;

    bool ITextureResidencyRequests.IsUnderBudgetPressure
        => IsUnderBudgetPressure;

    long ITextureResidencyRequests.BudgetPressureFrameId
        => BudgetPressureFrameId;

    bool ITextureResidencyRequests.AreStreamedTexturesImporting
        => HasActiveImportedModelImports;

    TextureResidencyHandle? ITextureResidencyRequests.RequestResidency(XRTexture2D texture, int targetMip, long deadlineFrame, JobPriority priority, int generation)
    {
        TextureResidencyTicket ticket = RequestResidency(texture, targetMip, deadlineFrame, priority, generation);
        return ticket.IsValid ? new TextureResidencyHandle(this, ticket) : null;
    }

    bool ITextureResidencyRequests.TryGetSourceMaxDimension(XRTexture2D texture, out uint sourceMaxDimension)
        => TryGetSourceMaxDimension(texture, out sourceMaxDimension);

    IDisposable ITextureResidencyRequests.AcquireBudgetProfile(ETextureStreamingBudgetProfile profile)
    {
        if (profile is not ETextureStreamingBudgetProfile.Cutscene)
            throw new ArgumentOutOfRangeException(nameof(profile), profile, "Only a profile other than Default can have a lease.");

        lock (_budgetProfileLeaseSync)
        {
            if (_cutsceneBudgetProfileLeaseCount++ == 0)
                SetBudgetProfile(ETextureStreamingBudgetProfile.Cutscene);
        }

        return new BudgetProfileLease(this);
    }

    private void ReleaseBudgetProfileLease()
    {
        lock (_budgetProfileLeaseSync)
        {
            if (_cutsceneBudgetProfileLeaseCount <= 0)
                return;

            if (--_cutsceneBudgetProfileLeaseCount == 0)
                SetBudgetProfile(ETextureStreamingBudgetProfile.Default);
        }
    }

    /// <summary>One lease on the cutscene budget profile. Dispose releases it one time.</summary>
    private sealed class BudgetProfileLease(ImportedTextureStreamingManager owner) : IDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
                owner.ReleaseBudgetProfileLease();
        }
    }
}
