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
    private AgentRunStatus _status = AgentRunStatus.Queued;
    private AgentTokenUsage _usage = new();
    private AgentRunResult? _result;
    private string _actualModel = string.Empty;
    private DateTimeOffset _updatedUtc;

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

    public void SetResult(AgentRunResult result)
    {
        lock (_sync)
        {
            _result = result;
            _status = result.Status;
            _actualModel = result.ActualModel;
            _usage = result.Usage;
            _toolEvidence.Clear();
            _toolEvidence.AddRange(result.ToolEvidence);
            if (_incrementalText.Length == 0 && !string.IsNullOrEmpty(result.FinalText))
                _incrementalText.Append(result.FinalText);
            _updatedUtc = DateTimeOffset.UtcNow;
        }
    }

    public AgentRunSnapshot Snapshot()
    {
        lock (_sync)
        {
            return new AgentRunSnapshot
            {
                RunId = RunId,
                Status = _status,
                CreatedUtc = CreatedUtc,
                UpdatedUtc = _updatedUtc,
                RequestedModel = Request.RequestedModel,
                ActualModel = _actualModel,
                EditorSession = Request.EditorSession,
                IncrementalText = _incrementalText.ToString(),
                Usage = _usage,
                ToolEvidence = _toolEvidence.ToArray(),
                Result = _result,
            };
        }
    }
}
