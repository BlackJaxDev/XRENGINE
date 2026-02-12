using NUnit.Framework;
using Shouldly;
using XREngine.Rendering;
using XREngine.Rendering.Commands;

namespace XREngine.UnitTests.Rendering;

[TestFixture]
public class GpuBackendParityTests
{
    [Test]
    public void IndirectParityChecklist_OpenGLCountPath_IsSelectedWhenSupported()
    {
        HybridRenderingManager.IndirectParityChecklist checklist = HybridRenderingManager.BuildIndirectParityChecklist(
            hasRenderer: true,
            hasIndirectDrawBuffer: true,
            hasParameterBuffer: true,
            parameterBufferReady: true,
            indexedVaoValid: true,
            supportsIndirectCountDraw: true,
            countDrawPathDisabled: false,
            backendName: "OpenGLRenderer");

        checklist.IsSubmissionReady.ShouldBeTrue();
        checklist.DrawIndirectBufferBindingReady.ShouldBeTrue();
        checklist.ParameterBufferBindingReady.ShouldBeTrue();
        checklist.UsesCountDrawPath.ShouldBeTrue();
        checklist.UsesFallbackPath.ShouldBeFalse();
    }

    [Test]
    public void IndirectParityChecklist_VulkanFallback_IsSelectedWhenCountUnsupported()
    {
        HybridRenderingManager.IndirectParityChecklist checklist = HybridRenderingManager.BuildIndirectParityChecklist(
            hasRenderer: true,
            hasIndirectDrawBuffer: true,
            hasParameterBuffer: true,
            parameterBufferReady: true,
            indexedVaoValid: true,
            supportsIndirectCountDraw: false,
            countDrawPathDisabled: false,
            backendName: "VulkanRenderer");

        checklist.IsSubmissionReady.ShouldBeTrue();
        checklist.DrawIndirectBufferBindingReady.ShouldBeTrue();
        checklist.UsesCountDrawPath.ShouldBeFalse();
        checklist.UsesFallbackPath.ShouldBeTrue();
    }

    [Test]
    public void CrossBackendParity_EquivalentSnapshots_Pass()
    {
        GPUIndirectRenderCommand[] commands =
        [
            new GPUIndirectRenderCommand { MeshID = 10, MaterialID = 100, RenderPass = 1 },
            new GPUIndirectRenderCommand { MeshID = 11, MaterialID = 101, RenderPass = 1 },
            new GPUIndirectRenderCommand { MeshID = 12, MaterialID = 102, RenderPass = 2 },
        ];

        GpuBackendParitySnapshot opengl = GpuBackendParity.BuildSnapshot("OpenGL", visibleCount: 3, drawCount: 3, commands, maxSamples: 3);
        GpuBackendParitySnapshot vulkan = GpuBackendParity.BuildSnapshot("Vulkan", visibleCount: 3, drawCount: 3, commands, maxSamples: 3);

        bool equivalent = GpuBackendParity.AreEquivalent(opengl, vulkan, out string reason);

        equivalent.ShouldBeTrue(reason);
        reason.ShouldBeEmpty();
    }

    [Test]
    public void CrossBackendParity_VisibleCountMismatch_Fails()
    {
        GPUIndirectRenderCommand[] commands =
        [
            new GPUIndirectRenderCommand { MeshID = 10, MaterialID = 100, RenderPass = 1 },
            new GPUIndirectRenderCommand { MeshID = 11, MaterialID = 101, RenderPass = 1 },
        ];

        GpuBackendParitySnapshot opengl = GpuBackendParity.BuildSnapshot("OpenGL", visibleCount: 2, drawCount: 2, commands, maxSamples: 2);
        GpuBackendParitySnapshot vulkan = GpuBackendParity.BuildSnapshot("Vulkan", visibleCount: 1, drawCount: 2, commands, maxSamples: 2);

        bool equivalent = GpuBackendParity.AreEquivalent(opengl, vulkan, out string reason);

        equivalent.ShouldBeFalse();
        reason.ShouldContain("Visible count mismatch");
    }

    [Test]
    public void CrossBackendParity_DrawCountMismatch_Fails()
    {
        GPUIndirectRenderCommand[] commands =
        [
            new GPUIndirectRenderCommand { MeshID = 10, MaterialID = 100, RenderPass = 1 },
            new GPUIndirectRenderCommand { MeshID = 11, MaterialID = 101, RenderPass = 1 },
        ];

        GpuBackendParitySnapshot opengl = GpuBackendParity.BuildSnapshot("OpenGL", visibleCount: 2, drawCount: 2, commands, maxSamples: 2);
        GpuBackendParitySnapshot vulkan = GpuBackendParity.BuildSnapshot("Vulkan", visibleCount: 2, drawCount: 1, commands, maxSamples: 2);

        bool equivalent = GpuBackendParity.AreEquivalent(opengl, vulkan, out string reason);

        equivalent.ShouldBeFalse();
        reason.ShouldContain("Draw count mismatch");
    }

    [Test]
    public void CrossBackendParity_SampledCommandSignatureMismatch_Fails()
    {
        GPUIndirectRenderCommand[] glCommands =
        [
            new GPUIndirectRenderCommand { MeshID = 10, MaterialID = 100, RenderPass = 1 },
            new GPUIndirectRenderCommand { MeshID = 11, MaterialID = 101, RenderPass = 1 },
        ];

        GPUIndirectRenderCommand[] vkCommands =
        [
            new GPUIndirectRenderCommand { MeshID = 10, MaterialID = 100, RenderPass = 1 },
            new GPUIndirectRenderCommand { MeshID = 99, MaterialID = 101, RenderPass = 1 },
        ];

        GpuBackendParitySnapshot opengl = GpuBackendParity.BuildSnapshot("OpenGL", visibleCount: 2, drawCount: 2, glCommands, maxSamples: 2);
        GpuBackendParitySnapshot vulkan = GpuBackendParity.BuildSnapshot("Vulkan", visibleCount: 2, drawCount: 2, vkCommands, maxSamples: 2);

        bool equivalent = GpuBackendParity.AreEquivalent(opengl, vulkan, out string reason);

        equivalent.ShouldBeFalse();
        reason.ShouldContain("Signature mismatch");
    }
}