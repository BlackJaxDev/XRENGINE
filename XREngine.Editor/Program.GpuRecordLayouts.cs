using XREngine;
using XREngine.Editor.GpuLayouts;

internal partial class Program
{
    private static bool TryRunGpuRecordLayoutCommand(string[] args)
    {
        if (args.Length > 0 && string.Equals(args[0], "--validate-gpu-record-glsl", StringComparison.Ordinal))
        {
            if (args.Length != 3)
                throw new ArgumentException("Usage: --validate-gpu-record-glsl <Advanced|GPUScene> <declarations-file>");
            GpuRecordSpirvValidator.ValidateDeclarations(File.ReadAllText(args[2]), args[1]);
            return true;
        }

        if (args.Length == 0 || !string.Equals(args[0], "--generate-gpu-record-includes", StringComparison.Ordinal))
            return false;
        if (args.Length != 2 || string.IsNullOrWhiteSpace(args[1]))
            throw new ArgumentException("Usage: --generate-gpu-record-includes <repo-root>");

        GpuRecordIncludeGenerator.Write(args[1], "Advanced");
        GpuRecordIncludeGenerator.Write(args[1], "GPUScene");
        GpuRecordSpirvValidator.ValidateGeneratedRecords(args[1]);
        return true;
    }

    private static void ValidateDebugGpuRecordLayouts()
    {
#if DEBUG
        if (!string.Equals(Environment.GetEnvironmentVariable("XRE_VALIDATE_GPU_RECORD_LAYOUTS"), "1", StringComparison.Ordinal))
            return;

        string shaderDirectory = Path.Combine(Engine.Assets?.EngineAssetsPath
            ?? throw new InvalidOperationException("Engine assets are unavailable for GPU record validation."), "Shaders");
        GpuRecordSpirvValidator.ValidateShaderDirectory(shaderDirectory);
#endif
    }
}
