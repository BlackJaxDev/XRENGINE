using XREngine.Rendering.Residency;

namespace XREngine.Rendering;

/// <summary>
/// The explicit implementation of the public residency request seam. Each member forwards to the internal member with
/// the same name, so the seam adds no behavior.
/// </summary>
internal sealed partial class ImportedTextureStreamingManager : ITextureResidencyRequests
{
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

    bool ITextureResidencyRequests.HasActiveImportedModelImports
        => HasActiveImportedModelImports;

    TextureResidencyTicket ITextureResidencyRequests.RequestResidency(XRTexture2D texture, int targetMip, long deadlineFrame, JobPriority priority, int generation)
        => RequestResidency(texture, targetMip, deadlineFrame, priority, generation);

    bool ITextureResidencyRequests.CancelResidency(TextureResidencyTicket ticket)
        => CancelResidency(ticket);

    ETextureResidencyRequestState ITextureResidencyRequests.GetResidencyRequestState(TextureResidencyTicket ticket)
        => GetResidencyRequestState(ticket);

    bool ITextureResidencyRequests.TryGetSourceMaxDimension(XRTexture2D texture, out uint sourceMaxDimension)
        => TryGetSourceMaxDimension(texture, out sourceMaxDimension);

    void ITextureResidencyRequests.SetBudgetProfile(ETextureStreamingBudgetProfile profile)
        => SetBudgetProfile(profile);
}
