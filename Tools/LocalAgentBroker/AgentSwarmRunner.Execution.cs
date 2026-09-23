using XREngine.AgentOrchestration;

namespace XREngine.LocalAgentBroker;

public sealed partial class AgentSwarmRunner
{
    private const int MaxReplacementCharacters = 256_000;

    private async Task<IReadOnlyList<AgentSwarmCodeChange>> RunNodeAsync(string runId, AgentRunRequest root, AgentSwarmOptions options, IReadOnlyDictionary<string, AgentContextFileSnapshot> files, AgentSwarmMutableNode node, Func<AgentSwarmSnapshot, CancellationToken, ValueTask>? progress, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (node.Role == AgentSwarmRole.Leaf)
            return await RunLeafAsync(runId, root, options, files, node, progress, cancellationToken);
        if (node.Depth >= options.MaxDepth)
            throw Fail(node, $"Depth limit {options.MaxDepth} reached by orchestrator; it may not produce code.", AgentFailureCategory.BudgetExceeded);
        node.Status = AgentSwarmNodeStatus.Planning;
        await PublishAsync(runId, AgentRunStatus.Running, progress, cancellationToken);
        AgentSwarmPlan plan = ParsePlan(await ExecutePhaseAsync(runId, root, options, files, node, BuildPlanPrompt(node, root.SuccessCriteria, options), cancellationToken), node);
        ValidatePlan(node, plan, options);
        List<AgentSwarmMutableNode> children = AdmitChildren(node, plan, options);
        node.Status = AgentSwarmNodeStatus.Running;
        await PublishAsync(runId, AgentRunStatus.Running, progress, cancellationToken);
        using var siblingCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        Task<IReadOnlyList<AgentSwarmCodeChange>>[] tasks = children.Select(child => RunNodeAsync(runId, root, options, files, child, progress, siblingCancellation.Token)).ToArray();
        IReadOnlyList<AgentSwarmCodeChange>[] childChanges;
        try
        {
            var remaining = new HashSet<Task<IReadOnlyList<AgentSwarmCodeChange>>>(tasks);
            while (remaining.Count > 0)
            {
                Task<IReadOnlyList<AgentSwarmCodeChange>> completed = await Task.WhenAny(remaining);
                remaining.Remove(completed);
                await completed;
            }
            childChanges = tasks.Select(static task => task.Result).ToArray();
        }
        catch
        {
            siblingCancellation.Cancel();
            try { await Task.WhenAll(tasks); }
            catch { }
            throw;
        }
        IReadOnlyList<AgentSwarmCodeChange> artifacts = childChanges.SelectMany(static changes => changes).ToArray();
        _ = AgentSwarmChangeMerger.Merge(artifacts, files.Values.ToArray());
        node.Status = AgentSwarmNodeStatus.Reviewing;
        await PublishAsync(runId, AgentRunStatus.Running, progress, cancellationToken);
        AgentSwarmReview review = ParseReview(await ExecutePhaseAsync(runId, root, options, files, node, BuildReviewPrompt(node, artifacts), cancellationToken), node);
        node.Approved = review.Approved;
        node.ReviewSummary = review.Summary;
        if (!review.Approved)
            throw Fail(node, $"Review rejected its unchanged child artifacts: {node.ReviewSummary}");
        node.Artifacts = artifacts;
        node.Status = AgentSwarmNodeStatus.Completed;
        await PublishAsync(runId, AgentRunStatus.Running, progress, cancellationToken);
        return artifacts;
    }

    private async Task<IReadOnlyList<AgentSwarmCodeChange>> RunLeafAsync(string runId, AgentRunRequest root, AgentSwarmOptions options, IReadOnlyDictionary<string, AgentContextFileSnapshot> files, AgentSwarmMutableNode node, Func<AgentSwarmSnapshot, CancellationToken, ValueTask>? progress, CancellationToken cancellationToken)
    {
        if (node.Paths.Count != 1)
            throw Fail(node, "A leaf must own exactly one path.");
        node.Status = AgentSwarmNodeStatus.Running;
        await PublishAsync(runId, AgentRunStatus.Running, progress, cancellationToken);
        AgentSwarmProposedChange proposed = ParseChange(await ExecutePhaseAsync(runId, root, options, files, node, BuildLeafPrompt(node, files[node.Paths[0]], options.MaxChangedLinesPerLeaf, (int)Math.Max(0, MaxArtifactCharacters - Volatile.Read(ref _reservedArtifactCharacters))), cancellationToken), node);
        AgentContextFileSnapshot file = files[node.Paths[0]];
        ValidateLeafChange(node, proposed, file, options);
        var change = new AgentSwarmCodeChange { Path = proposed.Path, BaseSha256 = proposed.BaseSha256, OldText = proposed.OldText, NewText = proposed.NewText, NodeId = node.Id };
        if (!TryReserveArtifactCharacters(System.Text.Json.JsonSerializer.Serialize(change).Length))
            throw Fail(node, "The swarm's artifact rendering budget is exhausted.", AgentFailureCategory.BudgetExceeded);
        node.Artifacts = [change];
        node.Status = AgentSwarmNodeStatus.Completed;
        await PublishAsync(runId, AgentRunStatus.Running, progress, cancellationToken);
        return node.Artifacts;
    }

    private async Task<string> ExecutePhaseAsync(string runId, AgentRunRequest root, AgentSwarmOptions options, IReadOnlyDictionary<string, AgentContextFileSnapshot> files, AgentSwarmMutableNode node, string instructions, CancellationToken cancellationToken)
    {
        if (Interlocked.Add(ref _reservedOutputTokens, options.MaxPhaseOutputTokens) > options.MaxOutputTokens)
        {
            Interlocked.Add(ref _reservedOutputTokens, -options.MaxPhaseOutputTokens);
            throw Fail(node, "The swarm's output-token reservation budget is exhausted.", AgentFailureCategory.BudgetExceeded);
        }
        AgentRunRequest request = CreatePhaseRequest(root, options, files, node, instructions);
        SemaphoreSlim swarmSlots = _swarmSlots ?? throw new InvalidOperationException("The swarm execution slots were not initialized.");
        await swarmSlots.WaitAsync(cancellationToken);
        try
        {
            await _providerSlots.WaitAsync(cancellationToken);
            AgentRunResult result;
            try
            {
                result = await _orchestrator.RunAsync($"{runId}:{node.Id}", request, EmptyAgentToolProvider.Instance, null, cancellationToken);
            }
            finally
            {
                _providerSlots.Release();
            }
            node.AddResult(result); cancellationToken.ThrowIfCancellationRequested();
            if (result.Status != AgentRunStatus.Completed || !string.Equals(result.ActualModel, LunaModel, StringComparison.Ordinal)) throw Fail(node, result.Failure?.Summary ?? "The provider phase did not complete with GPT-6 Luna.", result.Failure?.Category ?? AgentFailureCategory.ModelSubstitution, result.Failure?.DiagnosticDetail ?? string.Empty);
            if (result.FinalText.Length > AgentSwarmWire.MaxResponseCharacters) throw Fail(node, "Provider response exceeds the swarm JSON size limit.");
            return result.FinalText;
        }
        finally { swarmSlots.Release(); }
    }

    private List<AgentSwarmMutableNode> AdmitChildren(AgentSwarmMutableNode parent, AgentSwarmPlan plan, AgentSwarmOptions options)
    {
        int count = plan.Children.Count;
        while (true)
        {
            int admitted = Volatile.Read(ref _admittedNodes);
            if (admitted > options.MaxAgents - count) throw Fail(parent, "The swarm agent admission limit is exhausted.", AgentFailureCategory.BudgetExceeded);
            if (Interlocked.CompareExchange(ref _admittedNodes, admitted + count, admitted) == admitted) break;
        }
        var children = new List<AgentSwarmMutableNode>(count);
        foreach (AgentSwarmPlanChild child in plan.Children)
        {
            string id = $"{parent.Id}.{children.Count + 1}";
            var node = new AgentSwarmMutableNode(id, parent.Id, parent.Depth + 1, child.Role, child.Objective, child.Paths);
            if (!_nodes.TryAdd(id, node)) throw new InvalidOperationException($"Duplicate swarm node '{id}'.");
            children.Add(node);
        }
        return children;
    }

    private static void ValidateLeafChange(AgentSwarmMutableNode node, AgentSwarmProposedChange proposed, AgentContextFileSnapshot file, AgentSwarmOptions options)
    {
        if (!string.Equals(proposed.Path, file.Path, StringComparison.Ordinal) || !string.Equals(proposed.BaseSha256, file.Sha256, StringComparison.Ordinal)) throw Fail(node, "Leaf change did not target its immutable assigned file and base hash.");
        if (proposed.OldText.Length > MaxReplacementCharacters || proposed.NewText.Length > MaxReplacementCharacters || CountLines(proposed.OldText) + CountLines(proposed.NewText) > options.MaxChangedLinesPerLeaf) throw Fail(node, "Leaf replacement exceeds the configured changed-line or text limit.");
        if (string.Equals(proposed.OldText, proposed.NewText, StringComparison.Ordinal) || HasForbiddenControl(proposed.NewText)) throw Fail(node, "Leaf change must change text and cannot contain unsupported control characters.");
        if (file.Sha256 != "missing" && file.Content.Length > 0 && (proposed.OldText.Length == 0 || CountOccurrences(file.Content, proposed.OldText) != 1)) throw Fail(node, "Leaf old_text must be non-empty and occur exactly once in its immutable source snapshot.");
        if (file.Sha256 != "missing" && file.Content.Length == 0 && proposed.OldText.Length != 0) throw Fail(node, "An empty existing file requires empty old_text.");
        if (file.Sha256 == "missing" && proposed.OldText.Length != 0) throw Fail(node, "A new file proposal must have empty old_text.");
    }
}
