namespace XREngine.RenderBench;

/// <summary>Actual imported hierarchy propagation and explicit production publication measurements.</summary>
public sealed record RuntimeTransformMeasurement(
    string AvatarIdentity, string ContentSha256, int Avatars, int Transforms, int AnimatedTransforms, int Repeat, int Frames,
    double PropagationP50Milliseconds, double PropagationP95Milliseconds, double PropagationMaxMilliseconds,
    long PropagationAllocatedBytes, double PublicationP50Milliseconds, double PublicationP95Milliseconds,
    double PublicationMaxMilliseconds, long PublicationAllocatedBytes,
    double CanonicalPublicationP50Milliseconds, double CanonicalPublicationP95Milliseconds,
    double CanonicalPublicationMaxMilliseconds, long CanonicalPublicationAllocatedBytes,
    double ProductionP50Milliseconds, double ProductionP95Milliseconds,
    double ProductionMaxMilliseconds, long ProductionAllocatedBytes, object Counters);
