using NUnit.Framework;
using Shouldly;
using XREngine.Rendering;
using XREngine.Rendering.Vulkan;

namespace XREngine.UnitTests.Rendering;

[TestFixture]
public sealed class VulkanTransformFeedbackRewriteTests
{
    [Test]
    public void Rewrite_AddsXfbDecorationsAndStride()
    {
        const string source = @"#version 460 core
layout(location = 0) out vec4 outPosition;
layout(location = 1) out vec2 outVelocity;
void main()
{
    outPosition = vec4(1.0);
    outVelocity = vec2(2.0);
}";

        var plan = new VulkanTransformFeedbackCompilePlan(
        [
            new VulkanTransformFeedbackBufferCapture(
                1,
                EFeedbackType.PerVertex,
                ["outPosition", "outVelocity"])
        ]);

        string rewritten = VulkanShaderTransformFeedback.Rewrite(source, EShaderType.Vertex, plan);

        rewritten.ShouldContain("#extension GL_EXT_transform_feedback : require");
        rewritten.ShouldContain("layout(xfb_buffer = 1, xfb_stride = 24) out;");
        rewritten.ShouldContain("layout(location = 0, xfb_buffer = 1, xfb_offset = 0) out vec4 outPosition;");
        rewritten.ShouldContain("layout(location = 1, xfb_buffer = 1, xfb_offset = 16) out vec2 outVelocity;");
    }

    [Test]
    public void Rewrite_ThrowsForMissingRequestedOutput()
    {
        const string source = @"#version 460 core
layout(location = 0) out vec4 outPosition;
void main() { outPosition = vec4(1.0); }";

        var plan = new VulkanTransformFeedbackCompilePlan(
        [
            new VulkanTransformFeedbackBufferCapture(
                0,
                EFeedbackType.PerVertex,
                ["missingOutput"])
        ]);

        Should.Throw<InvalidOperationException>(() =>
                VulkanShaderTransformFeedback.Rewrite(source, EShaderType.Vertex, plan))
            .Message.ShouldContain("missingOutput");
    }
}
