using NUnit.Framework;
using Shouldly;

namespace XREngine.UnitTests.Rendering;

[TestFixture]
public sealed class DetailPreservingMipmapComputeShaderCompilationTests : GpuTestBase
{
    [Test]
    public void DetailPreservingMipmap_ComputeShader_CompilesSuccessfully()
    {
        RunWithGLContext(gl =>
        {
            string source = LoadShaderSource("Compute/Textures/DetailPreservingMipmaps.comp");
            uint shader = CompileComputeShader(gl, source);
            shader.ShouldBeGreaterThan(0u);

            uint program = CreateComputeProgram(gl, shader);
            program.ShouldBeGreaterThan(0u);

            gl.DeleteProgram(program);
            gl.DeleteShader(shader);
        });
    }
}