namespace XREngine.Rendering;

/// <summary>Requests completion of all work submitted before the request, without blocking the frame loop.</summary>
public interface IBrowserCompletionCapability
{
    Task CompleteSubmittedWorkAsync(CancellationToken cancellationToken = default);
}
