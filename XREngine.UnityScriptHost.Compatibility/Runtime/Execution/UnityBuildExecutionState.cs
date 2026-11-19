using XREngine.UnityScriptHost.Contracts;

namespace XREngine.UnityScriptHost.Execution;

/// <summary>
/// Retains an unsupported-operation failure after package code catches its exception.
/// </summary>
public sealed class UnityBuildExecutionState
{
    private const int MaxDiagnosticCount = 4096;
    private const int MaxMemberLength = 2048;
    private readonly object _sync = new();
    private readonly List<UnityBuildDiagnostic> _diagnostics = [];
    private readonly int _maxDiagnostics;
    private readonly UnityConversionCoverageBuilder _coverage;
    private bool _unsupportedOperation;

    /// <summary>Creates a state with a bounded diagnostic capacity.</summary>
    public UnityBuildExecutionState(int maxDiagnostics)
    {
        if (maxDiagnostics < 1 || maxDiagnostics > MaxDiagnosticCount)
            throw new ArgumentOutOfRangeException(nameof(maxDiagnostics));

        _maxDiagnostics = maxDiagnostics;
        _coverage = new(UnityConversionAssessmentStage.Authoring, maxDiagnostics);
    }

    /// <summary>Gets whether an unsupported operation was executed.</summary>
    public bool HasUnsupportedOperation
    {
        get
        {
            lock (_sync)
                return _unsupportedOperation;
        }
    }

    /// <summary>Records a terminal unsupported-operation failure.</summary>
    public void FailUnsupportedOperation(string memberIdentifier)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(memberIdentifier);

        var boundedMember = memberIdentifier.Length <= MaxMemberLength
            ? memberIdentifier
            : memberIdentifier[..MaxMemberLength];

        lock (_sync)
        {
            _unsupportedOperation = true;
            _coverage.Add(new UnityConversionFeature
            {
                FeatureId = "UnsupportedOperation",
                Status = UnityConversionFeatureStatus.AuthoringBlocked,
                Member = boundedMember,
                Functionality = "Execute the required authoring operation before assessing native output.",
                Reason = "The authoring member has no executable implementation. Native conversion loss is unknown."
            });
            if (_diagnostics.Count >= _maxDiagnostics)
                return;

            _diagnostics.Add(new UnityBuildDiagnostic
            {
                Code = UnityDiagnosticCode.UnsupportedOperation,
                Severity = UnityDiagnosticSeverity.Error,
                Message = "Unity member has no executable implementation.",
                Member = boundedMember
            });
        }
    }

    /// <summary>Creates a worker result that keeps the terminal failure latch.</summary>
    public UnityBuildResult CreateResult(
        UnityBuildRequest request,
        UnityBuildStageStatus compilation,
        UnityBuildStageStatus execution,
        UnityBuildStageStatus export,
        UnityProcessedGraphDescriptor? graph = null,
        IReadOnlyList<UnityBlobDescriptor>? blobs = null)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.Profile);
        if (!Enum.IsDefined(compilation))
            throw new ArgumentOutOfRangeException(nameof(compilation));
        if (!Enum.IsDefined(execution))
            throw new ArgumentOutOfRangeException(nameof(execution));
        if (!Enum.IsDefined(export))
            throw new ArgumentOutOfRangeException(nameof(export));

        lock (_sync)
        {
            var succeeded = !_unsupportedOperation &&
                compilation == UnityBuildStageStatus.Succeeded &&
                execution == UnityBuildStageStatus.Succeeded &&
                export == UnityBuildStageStatus.Succeeded &&
                graph is not null;

            return new UnityBuildResult
            {
                RequestId = request.RequestId,
                SourceDigest = request.SourceDigest,
                Profile = request.Profile,
                Status = _unsupportedOperation
                    ? UnityBuildTerminalStatus.UnsupportedOperation
                    : succeeded ? UnityBuildTerminalStatus.Succeeded : UnityBuildTerminalStatus.Failed,
                Compilation = compilation,
                Execution = _unsupportedOperation ? UnityBuildStageStatus.Failed : execution,
                Export = export,
                Graph = succeeded ? graph : null,
                Blobs = succeeded ? blobs ?? Array.Empty<UnityBlobDescriptor>() : Array.Empty<UnityBlobDescriptor>(),
                Diagnostics = _diagnostics.ToArray(),
                ConversionCoverage = execution == UnityBuildStageStatus.NotRun && !_unsupportedOperation
                    ? new UnityConversionCoverageReport() : _coverage.Build()
            };
        }
    }
}
