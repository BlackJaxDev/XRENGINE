using System.Collections.Concurrent;
using XREngine.AgentOrchestration;
using XREngine.LocalAgentBroker.Shared;

namespace XREngine.LocalAgentBroker;

/// <summary>
/// Coalesces high-frequency observer updates into durable snapshots for the tray companion.
/// </summary>
internal sealed class BrokerHistoryPublisher : IAsyncDisposable
{
    private static readonly TimeSpan s_coalesceDelay = TimeSpan.FromMilliseconds(75);

    private readonly BrokerHistoryStore _store;
    private readonly ConcurrentDictionary<string, BrokerRunRecord> _records = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, string> _promptTexts = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, byte> _pending = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _signal = new(0);
    private readonly CancellationTokenSource _shutdown = new();
    private readonly Task _writerTask;

    public BrokerHistoryPublisher(BrokerConfiguration configuration)
    {
        _store = new BrokerHistoryStore(new BrokerUiPaths(configuration.RepositoryRoot));
        BrokerUiSettings settings = _store.LoadSettings();
        if (settings.RecordRetentionHours is not null)
        {
            _store.DeleteTerminalRecordsOlderThan(
                TimeSpan.FromHours(settings.RecordRetentionHours.Value));
        }
        _writerTask = Task.Run(WriteLoopAsync);
    }

    public void Track(BrokerRunRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);
        _records[record.RunId] = record;
        _promptTexts[record.RunId] = record.Request.UseCompactHandoffPrompt
            ? AgentPromptBuilder.Build(record.Request)
            : record.Request.Objective;
        PublishNow(record);
    }

    public void QueueUpdate(BrokerRunRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);
        if (_pending.TryAdd(record.RunId, 0))
            _signal.Release();
    }

    public void PublishNow(BrokerRunRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);
        _pending.TryRemove(record.RunId, out _);
        _store.SaveRecord(CreateHistoryRecord(record));
    }

    public async ValueTask DisposeAsync()
    {
        _shutdown.Cancel();
        _signal.Release();
        try
        {
            await _writerTask;
        }
        catch (OperationCanceledException)
        {
        }

        foreach (BrokerRunRecord record in _records.Values)
            PublishNow(record);
        _signal.Dispose();
        _shutdown.Dispose();
    }

    private async Task WriteLoopAsync()
    {
        while (!_shutdown.IsCancellationRequested)
        {
            await _signal.WaitAsync(_shutdown.Token);
            await Task.Delay(s_coalesceDelay, _shutdown.Token);

            foreach (string runId in _pending.Keys)
            {
                if (!_pending.TryRemove(runId, out _)
                    || !_records.TryGetValue(runId, out BrokerRunRecord? record))
                {
                    continue;
                }

                try
                {
                    _store.SaveRecord(CreateHistoryRecord(record));
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                {
                    // A later observer event or terminal flush retries the durable snapshot.
                }
            }
        }
    }

    private BrokerHistoryRecord CreateHistoryRecord(BrokerRunRecord record)
    {
        AgentRunSnapshot snapshot = record.Snapshot();
        AgentRunRequest request = record.Request;
        AgentRunResult? result = snapshot.Result;
        return new BrokerHistoryRecord
        {
            RunId = record.RunId,
            Status = snapshot.Status,
            CreatedUtc = snapshot.CreatedUtc,
            UpdatedUtc = snapshot.UpdatedUtc,
            Objective = request.Objective,
            PromptText = _promptTexts.TryGetValue(record.RunId, out string? promptText)
                ? promptText
                : request.Objective,
            SystemInstructions = request.SystemInstructions,
            RequestedModel = snapshot.RequestedModel,
            ActualModel = snapshot.ActualModel,
            EditorSession = snapshot.EditorSession,
            ProgressMessage = snapshot.ProgressMessage,
            ResponseText = string.IsNullOrEmpty(result?.FinalText)
                ? snapshot.IncrementalText
                : result.FinalText,
            FailureSummary = result?.Failure?.Summary ?? string.Empty,
            FailureDetail = result?.Failure?.DiagnosticDetail ?? string.Empty,
            Usage = snapshot.Usage,
            TurnCount = result?.TurnCount ?? 0,
            ToolCallCount = result?.ToolCallCount ?? snapshot.ToolEvidence.Count,
            RetryCount = snapshot.RetryCount,
        };
    }
}
