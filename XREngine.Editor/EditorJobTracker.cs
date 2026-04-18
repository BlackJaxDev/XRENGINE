using System;
using System.Collections.Generic;
using System.Linq;

namespace XREngine.Editor;

public static class EditorJobTracker
{
    public enum TrackedJobState
    {
        Running,
        Completed,
        Faulted,
        Canceled
    }

    public readonly record struct TrackedJobSnapshot(
        Guid JobId,
        string Label,
        float Progress,
        string? Status,
        TrackedJobState State,
        DateTime UpdatedAt,
        bool IsDeterminate);

    private sealed class TrackedJob(Guid id, Job? job, string label, Func<object?, string?>? payloadFormatter)
    {
        public Guid Id { get; } = id;
        public Job? Job { get; } = job;
        public string Label { get; } = label;
        public float Progress { get; set; }
        public bool HasDeterminateProgress { get; set; }
        public string? Status { get; set; }
        public TrackedJobState State { get; set; } = TrackedJobState.Running;
        public DateTime LastUpdated { get; set; } = DateTime.UtcNow;
        public Func<object?, string?>? PayloadFormatter { get; } = payloadFormatter;

        public Action<Job, float>? ProgressHandler { get; set; }
        public Action<Job, float, object?>? ProgressWithPayloadHandler { get; set; }
        public Action<Job>? CompletedHandler { get; set; }
        public Action<Job, Exception>? FaultedHandler { get; set; }
        public Action<Job>? CanceledHandler { get; set; }
    }

    private static readonly object _lock = new();
    private static readonly Dictionary<Guid, TrackedJob> _trackedJobs = new();
    private static readonly TimeSpan CompletedRetention = TimeSpan.FromSeconds(10);

    public static void Track(Job job, string label, Func<object?, string?>? payloadFormatter = null)
    {
        ArgumentNullException.ThrowIfNull(job);

        if (string.IsNullOrWhiteSpace(label))
            label = "Background Job";

        lock (_lock)
        {
            if (_trackedJobs.ContainsKey(job.Id))
                return;

            var tracked = new TrackedJob(job.Id, job, label, payloadFormatter);
            _trackedJobs[job.Id] = tracked;

            tracked.ProgressHandler = (j, value) => UpdateTrackedInternal(j.Id, value, null);
            tracked.ProgressWithPayloadHandler = (j, value, payload) => UpdateTrackedInternal(j.Id, value, payload);
            tracked.CompletedHandler = j => MarkState(j.Id, TrackedJobState.Completed, "Completed");
            tracked.CanceledHandler = j => MarkState(j.Id, TrackedJobState.Canceled, "Canceled");
            tracked.FaultedHandler = (j, ex) => MarkState(j.Id, TrackedJobState.Faulted, ex.Message);

            job.ProgressChanged += tracked.ProgressHandler;
            job.ProgressWithPayload += tracked.ProgressWithPayloadHandler;
            job.Completed += tracked.CompletedHandler;
            job.Canceled += tracked.CanceledHandler;
            job.Faulted += tracked.FaultedHandler;
        }
    }

    public static Guid TrackOperation(string label, string? initialStatus = null)
    {
        if (string.IsNullOrWhiteSpace(label))
            label = "Background Operation";

        Guid operationId = Guid.NewGuid();
        lock (_lock)
        {
            var tracked = new TrackedJob(operationId, null, label, null)
            {
                Status = string.IsNullOrWhiteSpace(initialStatus) ? label : initialStatus
            };
            _trackedJobs[operationId] = tracked;
        }

        return operationId;
    }

    public static void Report(Job job, float progress, string? status = null)
    {
        ArgumentNullException.ThrowIfNull(job);
        UpdateTrackedInternal(job.Id, progress, status);
    }

    public static void Report(Guid operationId, float progress, string? status = null)
        => UpdateTrackedInternal(operationId, progress, status);

    public static void SetStatus(Guid operationId, string? status)
        => UpdateTrackedInternal(operationId, float.NaN, status);

    public static void Complete(Guid operationId, string? status = null)
        => MarkState(operationId, TrackedJobState.Completed, status ?? "Completed");

    public static void Fault(Guid operationId, string? status)
        => MarkState(operationId, TrackedJobState.Faulted, string.IsNullOrWhiteSpace(status) ? "Failed" : status);

    public static void Cancel(Guid operationId, string? status = null)
        => MarkState(operationId, TrackedJobState.Canceled, status ?? "Canceled");

    public static void SetStatus(Job job, string? status)
    {
        ArgumentNullException.ThrowIfNull(job);
        UpdateTrackedInternal(job.Id, float.NaN, status);
    }

    public static IReadOnlyList<TrackedJobSnapshot> GetSnapshots()
    {
        lock (_lock)
        {
            CleanupExpiredEntries();

            return [.. _trackedJobs.Values
                .OrderByDescending(t => t.State == TrackedJobState.Running)
                .ThenByDescending(t => t.LastUpdated)
                .Select(t => new TrackedJobSnapshot(
                    t.Id,
                    t.Label,
                    t.State == TrackedJobState.Running ? t.Progress : 1f,
                    t.Status,
                    t.State,
                    t.LastUpdated,
                    t.HasDeterminateProgress))];
        }
    }

    private static void UpdateTrackedInternal(Guid trackedId, float progress, object? payload)
    {
        lock (_lock)
        {
            if (!_trackedJobs.TryGetValue(trackedId, out var tracked))
                return;

            if (!float.IsNaN(progress))
            {
                tracked.Progress = Math.Clamp(progress, 0f, 1f);
                tracked.HasDeterminateProgress = true;
            }

            if (payload is not null)
            {
                tracked.Status = tracked.PayloadFormatter?.Invoke(payload) ?? FormatPayload(payload);
            }
            else if (!float.IsNaN(progress))
            {
                tracked.Status ??= tracked.Label;
            }

            tracked.LastUpdated = DateTime.UtcNow;
        }
    }

    private static string? FormatPayload(object payload)
        => payload switch
        {
            string text => text,
            XREngine.ArchiveExtractionProgress extraction when !string.IsNullOrWhiteSpace(extraction.Message) => extraction.Message,
            _ => payload.ToString()
        };

    private static void MarkState(Guid trackedId, TrackedJobState state, string? status)
    {
        lock (_lock)
        {
            if (!_trackedJobs.TryGetValue(trackedId, out var tracked))
                return;

            tracked.State = state;
            tracked.Progress = state == TrackedJobState.Running ? tracked.Progress : 1f;
            if (!string.IsNullOrWhiteSpace(status))
                tracked.Status = status;
            tracked.LastUpdated = DateTime.UtcNow;

            DetachHandlers(tracked);
        }
    }

    private static void DetachHandlers(TrackedJob tracked)
    {
        var job = tracked.Job;
        if (job is null)
            return;

        if (tracked.ProgressHandler is not null)
            job.ProgressChanged -= tracked.ProgressHandler;
        if (tracked.ProgressWithPayloadHandler is not null)
            job.ProgressWithPayload -= tracked.ProgressWithPayloadHandler;
        if (tracked.CompletedHandler is not null)
            job.Completed -= tracked.CompletedHandler;
        if (tracked.CanceledHandler is not null)
            job.Canceled -= tracked.CanceledHandler;
        if (tracked.FaultedHandler is not null)
            job.Faulted -= tracked.FaultedHandler;
    }

    private static void CleanupExpiredEntries()
    {
        var now = DateTime.UtcNow;
        var toRemove = _trackedJobs
            .Where(pair => pair.Value.State != TrackedJobState.Running && (now - pair.Value.LastUpdated) > CompletedRetention)
            .Select(pair => pair.Key)
            .ToList();

        foreach (var jobId in toRemove)
            _trackedJobs.Remove(jobId);
    }
}
