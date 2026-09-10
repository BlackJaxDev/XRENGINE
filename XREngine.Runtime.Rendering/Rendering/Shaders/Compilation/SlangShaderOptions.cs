using System.Collections.Immutable;
using System.Text.Json;

namespace XREngine.Rendering.Shaders.Compilation;

/// <summary>Immutable opt-in native frontend policy and explicit resource ownership contract.</summary>
public sealed record SlangShaderOptions
{
    public ImmutableArray<string> Includes { get; init; } = [];
    public ImmutableArray<string> Defines { get; init; } = [];
    public ImmutableArray<string> RequiredCapabilities { get; init; } = [];
    public ShaderMatrixLayout MatrixLayout { get; init; } = ShaderMatrixLayout.ColumnMajor;
    public ImmutableArray<ShaderAbiResourceContract> Resources { get; init; } = [];

    /// <summary>Versioned semantic input to artifact identity, including every ownership and ABI field.</summary>
    public string SemanticSchemaIdentity => "xr-slang-abi-v1:" + JsonSerializer.Serialize(Resources);
}
