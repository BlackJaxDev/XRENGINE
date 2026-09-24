namespace XREngine.Rendering.Commands;

/// <summary>Published pass signatures and copied command membership.</summary>
public readonly record struct S13aIdentityManifestPass(
    int PassIndex, ulong PassGeneration, ulong CanonicalDependencySignature,
    ulong MembershipSignature, int PackageCommandCount,
    int PackageMeshCommandCount, ulong CommandSetSignature,
    ulong PackageDependencySignature, uint[] MemberStableQueryKeys);
