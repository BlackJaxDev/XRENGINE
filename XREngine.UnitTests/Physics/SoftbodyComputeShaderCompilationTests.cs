using NUnit.Framework;
using Shouldly;

namespace XREngine.UnitTests.Physics;

[TestFixture]
public sealed class SoftbodyComputeShaderCompilationTests : GpuTestBase
{
    [TestCase("Compute/Softbody/Integrate.comp")]
    [TestCase("Compute/Softbody/CollideCapsules.comp")]
    [TestCase("Compute/Softbody/SolveDistance.comp")]
    [TestCase("Compute/Softbody/Finalize.comp")]
    public void Softbody_ComputeShader_CompilesSuccessfully(string relativeShaderPath)
    {
        RunWithGLContext(gl =>
        {
            string source = LoadShaderSource(relativeShaderPath);
            uint shader = CompileComputeShader(gl, source);
            shader.ShouldBeGreaterThan(0u);

            uint program = CreateComputeProgram(gl, shader);
            program.ShouldBeGreaterThan(0u);

            gl.DeleteProgram(program);
            gl.DeleteShader(shader);
        });
    }
}