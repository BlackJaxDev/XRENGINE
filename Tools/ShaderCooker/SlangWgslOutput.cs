namespace XREngine.Tools.ShaderCooker;

/// <summary>WGSL emitted by a pinned local Slang compiler, with its input provenance.</summary>
internal sealed record SlangWgslOutput(
    string Source,
    string CompilerIdentity,
    IReadOnlyDictionary<string, string> Dependencies,
    string ReflectionJson);
