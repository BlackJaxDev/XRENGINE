namespace XREngine.Data;

/// <summary>Runs host-owned external tools, terminating the owned process when the invocation is cancelled.</summary>
public interface IRuntimeProcessRunner
{
    Task<RuntimeProcessResult> RunAsync(RuntimeProcessRequest request, CancellationToken cancellationToken = default);
}
