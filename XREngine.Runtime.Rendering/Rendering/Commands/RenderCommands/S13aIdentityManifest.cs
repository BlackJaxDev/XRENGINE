namespace XREngine.Rendering.Commands;

/// <summary>
/// On-demand, owned copy of one published package and its resident submissions.
/// It holds no package, database, scene, command, or publication lease references.
/// </summary>
public sealed class S13aIdentityManifest
{
    public bool Complete { get; init; }
    public string? IncompleteReason { get; init; }
    public bool SourceLabelsComplete { get; init; }
    public bool FixtureKeysStable => false;
    public int TotalSubmissionCount { get; init; }
    public int CapturedSubmissionCount => Submissions.Length;
    public BackendReadyFramePackageIdentity PackageIdentity { get; init; }
    public long PackageGeneration { get; init; }
    public long SourceRevision { get; init; }
    public int PackageCommandCount { get; init; }
    public int PackageMeshCommandCount { get; init; }
    public BackendReadySubmissionResolution SubmissionResolution { get; init; }
    public BackendReadyCanonicalScenePublication Publication { get; init; }
    public S13aIdentityManifestSubmission[] Submissions { get; init; } = [];
    public S13aIdentityManifestView[] Views { get; init; } = [];
    public S13aIdentityManifestPass[] Passes { get; init; } = [];
}
