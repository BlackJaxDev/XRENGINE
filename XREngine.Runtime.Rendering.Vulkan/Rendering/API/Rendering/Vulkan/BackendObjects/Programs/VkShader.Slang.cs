using XREngine.Rendering.Shaders.Compilation;

namespace XREngine.Rendering.Vulkan;

internal sealed unsafe partial class VkShader
{
    private VulkanShaderArtifact BuildSlangArtifact(
        int shaderConfigVersion,
        bool usesVulkanClipDepthRemap,
        VulkanTransformFeedbackCompilePlan? transformFeedbackPlan)
    {
        if (transformFeedbackPlan is not null)
            throw new NotSupportedException("Native Slang does not support the GLSL transform-feedback rewrite policy.");
        if (usesVulkanClipDepthRemap && Data.Type is EShaderType.Vertex or EShaderType.Geometry or EShaderType.TessEvaluation)
            throw new NotSupportedException("Native Slang position outputs must use Vulkan clip depth; disable the GLSL clip-depth rewrite policy for this program.");

        // Snapshot the immutable frontend inputs before entering the isolated compiler.
        // Existing asynchronous publication also rejects a changed SourceRevision.
        long revision = Data.SourceRevision;
        SlangShaderOptions options = Data.SlangOptions;
        ShaderSourceFileBackendServices.TryCapture(out IShaderSourceFileBackend? fileBackend, out int fileBackendGeneration);
        ShaderCompileRequest request = new(
            ShaderSourceLanguage.Slang, Data.Source?.Text ?? string.Empty,
            Data.Source?.FilePath ?? Data.FilePath, Data.Type, Data.EntryPoint,
            Includes: options.Includes, Defines: options.Defines,
            RequiredCapabilities: options.RequiredCapabilities,
            MatrixLayout: options.MatrixLayout,
            SemanticSchemaIdentity: options.SemanticSchemaIdentity);
        ShaderCompileResult compiled = SlangVulkanShaderCompiler.CompileAsync(request, CancellationToken.None).GetAwaiter().GetResult();
        SlangShaderReflectionResult reflected = SlangShaderReflection.Validate(
            compiled.ReflectionJson ?? throw new InvalidOperationException("Slang reflection is required."),
            compiled.SpirV, options.Resources, ToVulkan(request.Stage), request.Stage);

        if (Data.SourceRevision != revision)
            throw new InvalidOperationException("Slang source or binding policy changed during compilation; the obsolete artifact was rejected.");
        ShaderSourceFileDependency[] files = new ShaderSourceFileDependency[compiled.Dependencies.Count];
        for (int index = 0; index < files.Length; index++)
        {
            string path = Path.GetFullPath(compiled.Dependencies[index].Path);
            if (fileBackend is null)
                throw new NotSupportedException("Native Slang dependency metadata requires a shader source file backend.");
            if (!fileBackend.TryGetFileMetadata(path, out long lastWriteTimeUtcTicks, out long length))
                throw new FileNotFoundException($"Could not find file '{path}'.", path);
            files[index] = new ShaderSourceFileDependency(path, lastWriteTimeUtcTicks, length);
        }

        if (Data.SourceRevision != revision)
            throw new InvalidOperationException("Slang source or binding policy changed during compilation; the obsolete artifact was rejected.");
        if (!ShaderSourceFileBackendServices.IsCurrent(fileBackend, fileBackendGeneration))
            throw new InvalidOperationException("The shader source file backend changed during native Slang compilation; the obsolete artifact was rejected.");

        Data.RegisterNativeSourceDependencies(request.SourcePath, options.Includes, files);
        RuntimeEngine.Rendering.Stats.RecordShaderVariant(warming: true, loadedFromDiskCache: compiled.LoadedFromCache);
        return new VulkanShaderArtifact(
            compiled.ArtifactIdentity, request.Stage, compiled.EntryPoint, request.SourcePath,
            request.Source, compiled.SpirV, reflected.DescriptorBindings,
            reflected.AutoUniformBlocks.Count == 0 ? null : reflected.AutoUniformBlocks[0],
            reflected.VertexInputLocations, ToVulkan(request.Stage), shaderConfigVersion,
            usesVulkanClipDepthRemap, LoadedFromDiskCache: compiled.LoadedFromCache,
            FrequencyOwnedAutoUniformBlocks: reflected.AutoUniformBlocks);
    }
}
