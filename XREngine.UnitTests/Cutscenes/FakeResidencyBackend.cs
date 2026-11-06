using XREngine.Cutscenes;

namespace XREngine.UnitTests.Cutscenes;

/// <summary>
/// A residency backend for tests. It records each request and each cancel, and the test completes the work.
/// </summary>
internal sealed class FakeResidencyBackend : IResidencyBackend
{
    private readonly Dictionary<Guid, EResidencyLevel> _levels = [];
    private readonly Dictionary<long, ResidencyRequest> _requestsByTicket = [];
    private readonly HashSet<long> _complete = [];
    private long _nextId = 1;

    /// <summary>The planner that receives the completions.</summary>
    public CutsceneResidencyPlanner? Planner { get; set; }

    /// <summary>Each accepted request, in call order.</summary>
    public List<(ResidencyTicket Ticket, ResidencyRequest Request)> Requests { get; } = [];

    /// <summary>Each cancelled ticket, in call order.</summary>
    public List<ResidencyTicket> Cancelled { get; } = [];

    /// <summary>The requests above T0, in call order.</summary>
    public IEnumerable<(ResidencyTicket Ticket, ResidencyRequest Request)> UpperRequests
        => Requests.Where(r => r.Request.TargetLevel > 0);

    public ResidencyTicket Request(in ResidencyRequest request)
    {
        ResidencyTicket ticket = new(_nextId++);
        _requestsByTicket[ticket.Id] = request;
        Requests.Add((ticket, request));
        return ticket;
    }

    public void Cancel(ResidencyTicket ticket)
    {
        Cancelled.Add(ticket);
        if (!_complete.Remove(ticket.Id))
            return;

        // A cancel of a complete upper level releases that data. The proxy stays if its ticket is still complete.
        ResidencyRequest request = _requestsByTicket[ticket.Id];
        if (request.TargetLevel > 0)
            _levels[request.AssetId] = HasCompleteProxy(request.AssetId) ? EResidencyLevel.Proxy : EResidencyLevel.None;
        else
            _levels.Remove(request.AssetId);
    }

    /// <summary>
    /// The number of calls to <see cref="GetLevel"/>. A plan reads the level of each asset in the window that has
    /// no running work, so a fixture with such an asset counts each plan. A frame with no plan and no playback wait does not call it.
    /// </summary>
    public int GetLevelCalls { get; private set; }

    public EResidencyLevel GetLevel(Guid assetId)
    {
        GetLevelCalls++;
        return _levels.TryGetValue(assetId, out EResidencyLevel level) ? level : EResidencyLevel.None;
    }

    /// <summary>Sets the resident level of an asset, as a cache would.</summary>
    public void SetLevel(Guid assetId, EResidencyLevel level)
        => _levels[assetId] = level;

    /// <summary>Completes the work of a ticket, sets its level, and reports it to the planner.</summary>
    public void Complete(ResidencyTicket ticket)
    {
        ResidencyRequest request = _requestsByTicket[ticket.Id];
        _complete.Add(ticket.Id);
        EResidencyLevel level = (EResidencyLevel)request.TargetLevel;
        if (!_levels.TryGetValue(request.AssetId, out EResidencyLevel current) || current < level)
            _levels[request.AssetId] = level;
        Planner?.NotifyCompleted(ticket);
    }

    private bool HasCompleteProxy(Guid assetId)
    {
        foreach ((ResidencyTicket ticket, ResidencyRequest request) in Requests)
            if (request.AssetId == assetId && request.TargetLevel == 0 && _complete.Contains(ticket.Id))
                return true;
        return false;
    }
}
