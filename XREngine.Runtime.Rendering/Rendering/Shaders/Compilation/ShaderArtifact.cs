using System.Text;

namespace XREngine.Rendering.Shaders.Compilation;

/// <summary>
/// Shader payload paired with the target that determines its format.
/// </summary>
public sealed record ShaderArtifact
{
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private readonly byte[] _bytes;

    public ShaderArtifact(ShaderCompileTarget target, byte[] bytes)
        : this(target, (ReadOnlySpan<byte>)(bytes ?? throw new ArgumentNullException(nameof(bytes))))
    {
    }

    public ShaderArtifact(ShaderCompileTarget target, ReadOnlySpan<byte> bytes)
    {
        if (target is not (ShaderCompileTarget.Vulkan14Spirv16 or ShaderCompileTarget.WebGPUWgsl))
            throw new ArgumentOutOfRangeException(nameof(target), target, "Unsupported shader artifact target.");

        if (target == ShaderCompileTarget.WebGPUWgsl)
            _ = StrictUtf8.GetCharCount(bytes);

        Target = target;
        _bytes = bytes.ToArray();
    }

    /// <summary>The target and format of <see cref="Bytes"/>.</summary>
    public ShaderCompileTarget Target { get; }

    /// <summary>The privately owned immutable payload; WGSL uses UTF-8 encoding. Reading does not allocate.</summary>
    public ReadOnlySpan<byte> Bytes => _bytes;

    /// <summary>Copies a Vulkan artifact for legacy mutable-array callers, rejecting other targets.</summary>
    public byte[] SpirV => Target == ShaderCompileTarget.Vulkan14Spirv16
        ? _bytes.ToArray()
        : throw new InvalidOperationException($"Shader artifact target '{Target}' is not SPIR-V.");

    /// <summary>Reads a WGSL artifact as UTF-8 source, rejecting other targets.</summary>
    public string WgslSource => Target == ShaderCompileTarget.WebGPUWgsl
        ? StrictUtf8.GetString(Bytes)
        : throw new InvalidOperationException($"Shader artifact target '{Target}' is not WGSL.");

    public override string ToString() => $"ShaderArtifact {{ Target = {Target}, ByteLength = {Bytes.Length} }}";

    public static ShaderArtifact FromSpirV(byte[] bytes) => new(ShaderCompileTarget.Vulkan14Spirv16, bytes);

    public static ShaderArtifact FromWgsl(string source)
    {
        ArgumentNullException.ThrowIfNull(source);
        return new ShaderArtifact(ShaderCompileTarget.WebGPUWgsl, StrictUtf8.GetBytes(source));
    }
}
