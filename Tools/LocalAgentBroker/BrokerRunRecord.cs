using System.Text;
using XREngine.AgentOrchestration;

namespace XREngine.LocalAgentBroker;

/// <summary>
/// Synchronized mutable state for one background broker run.
/// </summary>
internal sealed class BrokerRunRecord
{
    private readonly object _sync = new();
    private readonly StringBuilder _incrementalText = new();
    private readonly List<AgentToolEvidence> _toolEvidence = [];
    private readonly List<AgentProviderAttemptDiagnostic> _providerAttempts = [];
    private AgentRunStatus _status = AgentRunStatus.Queued;
    private AgentTokenUsage _usage = new();
    private AgentRunResult? _result;
    private string _actualModel = string.Empty;
    private int _retryCount;
    private DateTimeOffset _updatedUtc;
    private string _progressMessage = "queued";

    public BrokerRunRecord(string runId, AgentRunRequest request)
    {
        RunId = runId;
        Request = request;
        CreatedUtc = DateTimeOffset.UtcNow;
        _updatedUtc = CreatedUtc;
    }

    public string RunId { get; }

    public AgentRunRequest Request { get; }

    public DateTimeOffset CreatedUtc { get; }

    public CancellationTokenSource Cancellation { get; } = new();

    public void MarkRunning()
    {
        lock (_sync)
        {
            _status = AgentRunStatus.Running;
            _progressMessage = "orchestration_started";
            _updatedUtc = DateTimeOffset.UtcNow;
        }
    }

    public void UpdateStatus(string message)
    {
        lock (_sync)
        {
            _progressMessage = string.IsNullOrWhiteSpace(message) ? "running" : message;
            _updatedUtc = DateTimeOffset.UtcNow;
        }
    }

    public void AppendText(string text)
    {
        lock (_sync)
        {
            _incrementalText.Append(text);
            _updatedUtc = DateTimeOffset.UtcNow;
        }
    }

    public void AddUsage(AgentTokenUsage usage)
    {
        lock (_sync)
        {
            _usage += usage;
            _updatedUtc = DateTimeOffset.UtcNow;
        }
    }

    public void AddToolEvidence(AgentToolEvidence evidence)
    {
        lock (_sync)
        {
            _toolEvidence.Add(evidence);
            _updatedUtc = DateTimeOffset.UtcNow;
        }
    }

    public void AddProviderAttempt(AgentProviderAttemptDiagnostic diagnostic)
    {
        lock (_sync)
        {
            int existingIndex = _providerAttempts.FindIndex(candidate =>
                candidate.TurnNumber == diagnostic.TurnNumber
                && candidate.AttemptNumber == diagnostic.AttemptNumber);
            if (existingIndex >= 0)
                _providerAttempts[existingIndex] = diagnostic;
            else
                _providerAttempts.Add(diagnostic);
            if (!string.IsNullOrWhiteSpace(diagnostic.ActualModel))
                _actualModel = diagnostic.ActualModel;
            _progressMessage = string.IsNullOrWhiteSpace(diagnostic.LastProviderEventType)
                ? diagnostic.Outcome
                : diagnostic.LastProviderEventType;
            _updatedUtc = DateTimeOffset.UtcNow;
        }
    }

    public void RecordRetry()
    {
        lock (_sync)
        {
            _retryCount++;
            _updatedUtc = DateTimeOffset.UtcNow;
        }
    }

    public void SetResult(AgentRunResult result)
    {
        lock (_sync)
        {
            _result = result;
            _status = result.Status;
            _progressMessage = result.Status.ToString();
            if (!string.IsNullOrWhiteSpace(result.ActualModel))
                _actualModel = result.ActualModel;
            _usage = result.Usage;
            _toolEvidence.Clear();
            _toolEvidence.AddRange(result.ToolEvidence);
            if (result.ProviderAttempts.Count > 0)
            {
                _providerAttempts.Clear();
                _providerAttempts.AddRange(result.ProviderAttempts);
            }
            _retryCount = Math.Max(_retryCount, result.RetryCount);
            if (_incrementalText.Length == 0 && !string.IsNullOrEmpty(result.FinalText))
                _incrementalText.Append(result.FinalText);
            _updatedUtc = DateTimeOffset.UtcNow;
        }
    }

    public AgentRunSnapshot Snapshot()
    {
        lock (_sync)
        {
            DateTimeOffset observedUtc = DateTimeOffset.UtcNow;
            return new AgentRunSnapshot
            {
                RunId = RunId,
                Status = _status,
                CreatedUtc = CreatedUtc,
                UpdatedUtc = _updatedUtc,
                ObservedUtc = observedUtc,
                ElapsedMilliseconds = Math.Max(
                    0L,
                    (long)(observedUtc - CreatedUtc).TotalMilliseconds),
                ProgressMessage = _progressMessage,
                RequestedModel = Request.RequestedModel,
                ActualModel = _actualModel,
                RequestedReasoningEffort = Request.ReasoningEffort,
                RequestedTextVerbosity = Request.TextVerbosity,
                MaxOutputTokens = Request.Budget.MaxOutputTokens,
                EditorSession = Request.EditorSession,
                UseBackgroundMode = Request.UseBackgroundMode,
                IncrementalText = _incrementalText.ToString(),
                Usage = _usage,
                ToolEvidence = _toolEvidence.ToArray(),
                RetryCount = _retryCount,
                ProviderAttempts = _providerAttempts.ToArray(),
                Result = _result,
            };
        }
    }
}
