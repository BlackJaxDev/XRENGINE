using System.Text.Json;
using System.Text.Json.Serialization;

namespace XREngine.Benchmarks.SelfIteration;

/// <summary>
/// Runs a bounded propose-edit-reload-measure-compare rendering improvement loop.
/// </summary>
public static class SelfIterationBenchmarkHarness
{
    public static async Task<int> RunAsync(string[] args)
    {
        try
        {
            string workspaceRoot = ResolveWorkspaceRoot();
            string configPath = GetOption(args, "--config")
                ?? throw new ArgumentException("--self-iterate requires --config <campaign.jsonc>.");
            configPath = Path.GetFullPath(
                Path.IsPathRooted(configPath)
                    ? configPath
                    : Path.Combine(workspaceRoot, configPath));
            bool validateOnly = args.Contains("--validate-only", StringComparer.OrdinalIgnoreCase);
            bool baselineOnly = args.Contains("--baseline-only", StringComparer.OrdinalIgnoreCase);

            SelfIterationConfiguration configuration = SelfIterationConfiguration.Load(configPath);
            configuration.NormalizeAndValidate(
                workspaceRoot,
                requireAgent: !validateOnly && !baselineOnly);

            Console.WriteLine(
                $"Validated self-iteration campaign '{configuration.CampaignId}' with " +
                $"{configuration.Scenarios.Count} scenario(s).");
            if (validateOnly)
                return 0;

            string runRoot = CreateRunRoot(workspaceRoot, configuration.CampaignId);
            Console.WriteLine($"Run evidence: {runRoot}");
            var processRunner = new SelfIterationProcessRunner();
            var workspace = new SelfIterationWorkspaceManager(
                workspaceRoot,
                configuration.AllowedPathPrefixes,
                [configuration.ProgressDocument, configuration.RejectedAttemptsDocument],
                processRunner);
            if (configuration.RequireCleanTrackedWorktree)
            {
                await workspace.EnsureCleanTrackedWorktreeAsync(
                    Path.Combine(runRoot, "workspace-preflight"),
                    CancellationToken.None);
            }

            var measurementRunner = new SelfIterationMeasurementRunner(
                workspaceRoot,
                configuration,
                processRunner);
            IReadOnlyList<SelfIterationScenarioMeasurement> baseline =
                await measurementRunner.MeasureMatrixAsync(
                    Path.Combine(runRoot, "baseline"),
                    "baseline",
                    CancellationToken.None);
            List<string> baselineErrors = baseline
                .SelectMany(measurement => measurement.Validate(configuration.Acceptance))
                .ToList();
            if (baselineErrors.Count > 0)
            {
                string errorPath = Path.Combine(runRoot, "baseline-validation-errors.txt");
                await File.WriteAllLinesAsync(errorPath, baselineErrors);
                Console.Error.WriteLine(
                    $"Baseline is not eligible for self-iteration. See {errorPath}");
                return 2;
            }
            if (baselineOnly)
            {
                Console.WriteLine("Baseline capture completed; no LLM was invoked.");
                return 0;
            }

            var documentation = new SelfIterationDocumentationWriter(
                workspaceRoot,
                configuration);
            var knownFingerprints = new HashSet<string>(
                documentation.ReadKnownFingerprints(),
                StringComparer.OrdinalIgnoreCase);
            var agent = new SelfIterationAgentRunner(
                workspaceRoot,
                runRoot,
                configuration.Agent,
                processRunner);
            var comparator = new SelfIterationComparator();
            var records = new List<SelfIterationAttemptRecord>();
            int acceptedCount = 0;

            await using var editorSession = new SelfIterationEditorSessionController(
                workspaceRoot,
                configuration,
                processRunner);
            for (int iteration = 1; iteration <= configuration.MaxIterations; iteration++)
            {
                Console.WriteLine($"=== Self-iteration {iteration}/{configuration.MaxIterations} ===");
                string iterationDirectory = Path.Combine(runRoot, $"iteration-{iteration:D3}");
                Directory.CreateDirectory(iterationDirectory);

                SelfIterationAgentProposal? proposal = await AcquireUniqueProposalAsync(
                    iteration,
                    iterationDirectory,
                    configuration,
                    baseline,
                    agent,
                    workspace,
                    knownFingerprints,
                    workspaceRoot,
                    CancellationToken.None);
                if (proposal is null)
                {
                    Console.WriteLine("No novel proposal was produced; stopping the campaign.");
                    break;
                }

                string fingerprint = SelfIterationFingerprint.Compute(
                    configuration.CampaignId,
                    proposal);
                SelfIterationScenario targetScenario = configuration.Scenarios.First(
                    scenario => scenario.Name.Equals(
                        proposal.TargetScenario,
                        StringComparison.OrdinalIgnoreCase));
                string reloadDirectory = Path.Combine(iterationDirectory, "reload-validation");
                await editorSession.PrepareAsync(
                    targetScenario,
                    reloadDirectory,
                    CancellationToken.None);

                string implementationDirectory = Path.Combine(iterationDirectory, "implementation");
                SelfIterationWorkspaceCheckpoint implementationCheckpoint =
                    await workspace.CaptureAsync(
                        Path.Combine(implementationDirectory, "checkpoint"),
                        CancellationToken.None);
                SelfIterationAgentImplementation? implementation = null;
                SelfIterationReloadResult? reload = null;
                SelfIterationComparisonResult? comparison = null;
                IReadOnlyList<string> changedPaths = [];
                bool accepted = false;
                string outcome;
                Exception? fatalAttemptException = null;

                try
                {
                    string implementationPrompt = SelfIterationPromptBuilder.BuildImplementation(
                        configuration,
                        baseline,
                        proposal,
                        Path.Combine(workspaceRoot, configuration.ProgressDocument),
                        Path.Combine(workspaceRoot, configuration.RejectedAttemptsDocument));
                    implementation = await agent.ImplementAsync(
                        implementationPrompt,
                        implementationDirectory,
                        CancellationToken.None);
                    changedPaths = await workspace.GetChangedPathsAsync(
                        implementationCheckpoint,
                        CancellationToken.None);

                    if (!implementation.Implemented)
                        throw new InvalidOperationException("Agent reported that it did not implement the proposal.");
                    if (changedPaths.Count == 0)
                        throw new InvalidOperationException("Agent made no source changes.");
                    string[] unauthorized = changedPaths
                        .Where(path => !workspace.IsAllowedPath(path))
                        .ToArray();
                    if (unauthorized.Length > 0)
                    {
                        throw new InvalidOperationException(
                            $"Agent changed paths outside the allow-list: {string.Join(", ", unauthorized)}");
                    }

                    SelfIterationReloadMode requestedReload =
                        implementation.ReloadMode == SelfIterationReloadMode.Auto
                            ? proposal.ReloadMode
                            : implementation.ReloadMode;
                    reload = await editorSession.ApplyAndValidateAsync(
                        requestedReload,
                        changedPaths,
                        reloadDirectory,
                        CancellationToken.None);
                    await editorSession.StopAsync(reloadDirectory, CancellationToken.None);

                    IReadOnlyList<SelfIterationScenarioMeasurement> candidate =
                        await measurementRunner.MeasureMatrixAsync(
                            Path.Combine(iterationDirectory, "candidate"),
                            $"iteration-{iteration:D3}",
                            CancellationToken.None);
                    comparison = comparator.Compare(
                        baseline,
                        candidate,
                        configuration.Acceptance);
                    accepted = comparison.Accepted;
                    if (accepted)
                    {
                        baseline = candidate;
                        acceptedCount++;
                        outcome = "Formal scenario matrix improved and passed every invariant.";
                    }
                    else
                    {
                        await workspace.RestoreAsync(
                            implementationCheckpoint,
                            CancellationToken.None);
                        outcome = "Formal comparison rejected the attempted fix and restored the prior source checkpoint.";
                    }
                }
                catch (Exception attemptException)
                {
                    Exception? stopException = null;
                    Exception? restoreException = null;
                    try
                    {
                        await editorSession.StopAsync(reloadDirectory, CancellationToken.None);
                    }
                    catch (Exception exception)
                    {
                        stopException = exception;
                    }
                    try
                    {
                        await workspace.RestoreAsync(
                            implementationCheckpoint,
                            CancellationToken.None);
                    }
                    catch (Exception exception)
                    {
                        restoreException = exception;
                    }

                    if (restoreException is null)
                    {
                        outcome =
                            $"Attempt failed before acceptance: {attemptException.Message}. " +
                            "Prior source checkpoint restored.";
                    }
                    else
                    {
                        outcome =
                            $"Attempt failed before acceptance: {attemptException.Message}. " +
                            $"Automatic checkpoint restoration also failed ({restoreException.Message}); " +
                            "manual recovery is required.";
                    }
                    if (stopException is not null)
                    {
                        outcome +=
                            $" Owned editor cleanup also failed ({stopException.Message}); " +
                            "the campaign will stop.";
                    }
                    if (restoreException is not null || stopException is not null)
                    {
                        fatalAttemptException = new AggregateException(
                            "Attempt cleanup was incomplete.",
                            new[] { attemptException, restoreException, stopException }
                                .OfType<Exception>());
                    }
                }

                knownFingerprints.Add(fingerprint);
                var record = new SelfIterationAttemptRecord
                {
                    Iteration = iteration,
                    Fingerprint = fingerprint,
                    Accepted = accepted,
                    Outcome = outcome,
                    Proposal = proposal,
                    Implementation = implementation,
                    Reload = reload,
                    Comparison = comparison,
                    ChangedPaths = changedPaths,
                    EvidenceDirectory = iterationDirectory,
                };
                records.Add(record);
                documentation.Append(record);
                WriteRunState(runRoot, configuration, records, acceptedCount);
                Console.WriteLine(
                    fatalAttemptException is not null
                        ? $"Rejected {proposal.AttemptKey}; cleanup was incomplete and the campaign is stopping."
                        : accepted
                        ? $"Accepted {proposal.AttemptKey}; new baseline established."
                        : $"Rejected {proposal.AttemptKey}; prior baseline retained.");
                if (fatalAttemptException is not null)
                    throw fatalAttemptException;
            }

            WriteRunState(runRoot, configuration, records, acceptedCount);
            Console.WriteLine(
                $"Self-iteration campaign complete: {acceptedCount} accepted, " +
                $"{records.Count - acceptedCount} rejected.");
            return 0;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"Self-iteration failed: {exception.Message}");
            return 2;
        }
    }

    private static async Task<SelfIterationAgentProposal?> AcquireUniqueProposalAsync(
        int iteration,
        string iterationDirectory,
        SelfIterationConfiguration configuration,
        IReadOnlyList<SelfIterationScenarioMeasurement> baseline,
        SelfIterationAgentRunner agent,
        SelfIterationWorkspaceManager workspace,
        ISet<string> knownFingerprints,
        string workspaceRoot,
        CancellationToken token)
    {
        for (int proposalAttempt = 1;
             proposalAttempt <= configuration.MaxProposalAttemptsPerIteration;
             proposalAttempt++)
        {
            string directory = Path.Combine(
                iterationDirectory,
                $"proposal-{proposalAttempt:D2}");
            SelfIterationWorkspaceCheckpoint checkpoint = await workspace.CaptureAsync(
                Path.Combine(directory, "checkpoint"),
                token);
            string prompt = SelfIterationPromptBuilder.BuildProposal(
                configuration,
                baseline,
                Path.Combine(workspaceRoot, configuration.ProgressDocument),
                Path.Combine(workspaceRoot, configuration.RejectedAttemptsDocument));
            SelfIterationAgentProposal proposal;
            try
            {
                proposal = await agent.ProposeAsync(prompt, directory, token);
                proposal.Validate(configuration.Scenarios);
            }
            catch
            {
                await workspace.RestoreAsync(checkpoint, token);
                throw;
            }

            IReadOnlyList<string> proposalChanges = await workspace.GetChangedPathsAsync(
                checkpoint,
                token);
            if (proposalChanges.Count > 0)
            {
                await workspace.RestoreAsync(checkpoint, token);
                throw new InvalidOperationException(
                    $"Read-only proposal phase changed files: {string.Join(", ", proposalChanges)}");
            }

            string fingerprint = SelfIterationFingerprint.Compute(
                configuration.CampaignId,
                proposal);
            if (!knownFingerprints.Contains(fingerprint))
                return proposal;

            Console.WriteLine(
                $"Blocked duplicate proposal '{proposal.AttemptKey}' before implementation " +
                $"({proposalAttempt}/{configuration.MaxProposalAttemptsPerIteration}).");
        }
        return null;
    }

    private static void WriteRunState(
        string runRoot,
        SelfIterationConfiguration configuration,
        IReadOnlyList<SelfIterationAttemptRecord> records,
        int acceptedCount)
    {
        var options = new JsonSerializerOptions
        {
            WriteIndented = true,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        };
        options.Converters.Add(new JsonStringEnumConverter());
        File.WriteAllText(
            Path.Combine(runRoot, "run-state.json"),
            JsonSerializer.Serialize(
                new
                {
                    schemaVersion = 1,
                    configuration.CampaignId,
                    configuration.Objective,
                    updatedUtc = DateTimeOffset.UtcNow,
                    acceptedCount,
                    rejectedCount = records.Count - acceptedCount,
                    records,
                },
                options));
    }

    private static string CreateRunRoot(string workspaceRoot, string campaignId)
    {
        string path = Path.Combine(
            workspaceRoot,
            "Build",
            "_AgentValidation",
            "self-iteration",
            campaignId,
            $"{DateTime.Now:yyyyMMdd-HHmmss}");
        Directory.CreateDirectory(path);
        return path;
    }

    private static string ResolveWorkspaceRoot()
        => TryFindWorkspaceRoot(Directory.GetCurrentDirectory())
            ?? TryFindWorkspaceRoot(AppContext.BaseDirectory)
            ?? throw new DirectoryNotFoundException("Could not locate XRENGINE.slnx.");

    private static string? TryFindWorkspaceRoot(string startPath)
    {
        DirectoryInfo? directory = new(startPath);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "XRENGINE.slnx")))
                return directory.FullName;
            directory = directory.Parent;
        }
        return null;
    }

    private static string? GetOption(string[] args, string optionName)
    {
        for (int index = 0; index < args.Length - 1; index++)
        {
            if (args[index].Equals(optionName, StringComparison.OrdinalIgnoreCase))
                return args[index + 1];
        }
        return null;
    }
}
