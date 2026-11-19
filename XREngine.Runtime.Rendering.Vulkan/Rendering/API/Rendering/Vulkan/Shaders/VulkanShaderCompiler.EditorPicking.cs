using System.Text;
using Silk.NET.Core.Native;
using Silk.NET.Shaderc;

namespace XREngine.Rendering.Vulkan;

internal static partial class VulkanShaderCompiler
{
    /// <summary>
    /// Resolves conditional outputs with shaderc before binding the editor color
    /// reader. This runs after uniform rewriting so frequency ownership is kept.
    /// </summary>
    private static unsafe string FinalizeEditorPickingSource(XRShader shader, string source)
    {
        Compiler* compiler = ShadercApi.CompilerInitialize();
        if (compiler is null)
            throw new InvalidOperationException("Could not initialize shaderc for editor picking.");
        CompileOptions* options = null;
        CompilationResult* result = null;
        try
        {
            options = ShadercApi.CompileOptionsInitialize();
            if (options is null)
                throw new InvalidOperationException("Could not allocate editor picking compiler options.");
            ShadercApi.CompileOptionsSetSourceLanguage(options, SourceLanguage.Glsl);
            ConfigureTargetEnvironment(options, shader.Type, source);
            byte[] bytes = Encoding.UTF8.GetBytes(source);
            byte[] name = GetNullTerminatedUtf8(shader.Name ?? "EditorPickingFragment");
            byte[] entry = GetNullTerminatedUtf8("main");
            fixed (byte* sourcePointer = bytes)
            fixed (byte* namePointer = name)
            fixed (byte* entryPointer = entry)
                result = ShadercApi.CompileIntoPreprocessedText(compiler, sourcePointer,
                    (nuint)bytes.Length, ToShaderKind(shader.Type), namePointer, entryPointer, options);
            if (result is null)
                throw new InvalidOperationException("Shaderc returned no editor picking preprocessor result.");
            if (ShadercApi.ResultGetCompilationStatus(result) != CompilationStatus.Success)
                throw new NotSupportedException("Editor picking preprocessing failed: " +
                    SilkMarshal.PtrToString((nint)ShadercApi.ResultGetErrorMessage(result)));
            string preprocessed = Encoding.UTF8.GetString(new ReadOnlySpan<byte>(
                ShadercApi.ResultGetBytes(result), checked((int)ShadercApi.ResultGetLength(result))));
            if (!EditorHighlightFragmentSourceAdapter.TryFinalizePickingSource(
                    preprocessed, out string finalized, out string failure))
                throw new NotSupportedException(failure);
            return finalized;
        }
        finally
        {
            if (result is not null)
                ShadercApi.ResultRelease(result);
            if (options is not null)
                ShadercApi.CompileOptionsRelease(options);
            ShadercApi.CompilerRelease(compiler);
        }
    }
}
