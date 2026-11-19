namespace XREngine.ShaderTranslation.Compilation;

/// <summary>
/// Converts SPIR-V to OpenGL GLSL 4.60 with the SPIRV-Cross command-line tool.
/// </summary>
/// <remarks>
/// The output is plain (non-Vulkan) GLSL. SPIRV-Cross combines each separate texture and sampler
/// pair into one <c>sampler*</c> uniform, because OpenGL has no separate sampler objects in GLSL.
/// Uniform blocks become plain uniform structs; <see cref="GlslPostProcessor"/> then makes them loose uniforms.
/// </remarks>
public sealed class SpirvCrossGlslEmitter(ExternalTool spirvCross)
{
    /// <summary>The environment variable that can name the <c>spirv-cross</c> executable.</summary>
    public const string OverrideVariable = "XRE_SPIRV_CROSS";

    /// <summary>Gets the cross-compiler tool.</summary>
    public ExternalTool Tool { get; } = spirvCross ?? throw new ArgumentNullException(nameof(spirvCross));

    /// <summary>Finds <c>spirv-cross</c>, or returns null when it is not installed.</summary>
    public static SpirvCrossGlslEmitter? FindDefault()
        => ExternalTool.Find("spirv-cross", OverrideVariable) is ExternalTool tool ? new SpirvCrossGlslEmitter(tool) : null;

    /// <summary>Emits GLSL for one SPIR-V module.</summary>
    /// <exception cref="ShaderCompileException">SPIRV-Cross rejected the module.</exception>
    public async Task<string> EmitAsync(byte[] spirv, string workDirectory, CancellationToken cancellationToken)
    {
        string spirvPath = Path.Combine(workDirectory, "cross-input.spv");
        string glslPath = Path.Combine(workDirectory, "cross-output.glsl");
        await File.WriteAllBytesAsync(spirvPath, spirv, cancellationToken).ConfigureAwait(false);
        string[] arguments =
        [
            spirvPath,
            "--version", "460",
            "--no-es",
            "--glsl-emit-ubo-as-plain-uniforms",
            "--output", glslPath,
        ];
        ExternalToolResult result = await Tool.RunAsync(arguments, workDirectory, cancellationToken).ConfigureAwait(false);
        if (result.ExitCode != 0 || !File.Exists(glslPath))
            throw new ShaderCompileException("spirv-cross", result.FailureText);

        return await File.ReadAllTextAsync(glslPath, cancellationToken).ConfigureAwait(false);
    }
}
