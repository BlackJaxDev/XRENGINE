using System.Runtime.InteropServices;
using XREngine.Rendering;

namespace XREngine.Editor.HotReload;

public sealed record RendererBackendGenerationManifest
{
    public required string BackendId { get; init; }
    public required long Generation { get; init; }
    public required int AbiVersion { get; init; }
    public required string TargetFramework { get; init; }
    public required Architecture ProcessArchitecture { get; init; }
    public required string EntryAssembly { get; init; }
    public required string EntryPointType { get; init; }
    public required string BuildHash { get; init; }
    public required DateTimeOffset CreatedAt { get; init; }
    public required Dictionary<string, string> FileHashes { get; init; }

    public RendererBackendId GetBackendId()
        => new(BackendId);
}
