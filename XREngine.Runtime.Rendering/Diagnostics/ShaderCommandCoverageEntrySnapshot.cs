namespace XREngine.Rendering;

/// <summary>One shader-stage identity and its command counters.</summary>
public sealed record ShaderCommandCoverageEntrySnapshot(
    string Backend,
    string CommandState,
    string Language,
    string Stage,
    string EntryPoint,
    string SourceIdentity,
    string AuthoredSourceSha256,
    long SourceRevision,
    long GraphicsDirect,
    long GraphicsIndirect,
    long ComputeDirect,
    long ComputeIndirect,
    long KnownInstanced,
    long KnownNonInstanced,
    long UnknownInstancing);
