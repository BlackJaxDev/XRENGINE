using NUnit.Framework;
using Shouldly;
using XREngine.Rendering.Vulkan;

namespace XREngine.UnitTests.Rendering;

[TestFixture]
public sealed class VulkanUniformBufferGenerationCacheTests
{
    [Test]
    public void AlternatingDesktopAndStrictStereoRendererUse_ReusesGenerationsWithoutSteadyStateRetirement()
    {
        VulkanUniformBufferGenerationCache<Generation> cache = new();
        Generation? active = null;
        int allocationCount = 0;
        int retirementCount = 0;

        Activate(slotCount: 3, payloadCapacity: 256);
        Generation desktopGeneration = active!;
        Activate(slotCount: 12, payloadCapacity: 256);
        Generation strictStereoGeneration = active!;

        for (int frame = 0; frame < 300; frame++)
        {
            Activate(slotCount: 3, payloadCapacity: 256);
            active.ShouldBeSameAs(desktopGeneration);
            Activate(slotCount: 12, payloadCapacity: 256);
            active.ShouldBeSameAs(strictStereoGeneration);
        }

        allocationCount.ShouldBe(2);
        retirementCount.ShouldBe(0);
        cache.GenerationCount.ShouldBe(1);

        if (active is not null)
        {
            retirementCount++;
            active = null;
        }

        while (cache.TryTakeAny(out _))
            retirementCount++;

        retirementCount.ShouldBe(2);
        cache.GenerationCount.ShouldBe(0);

        void Activate(int slotCount, uint payloadCapacity)
        {
            if (active is { } current &&
                current.SlotCount == slotCount &&
                current.PayloadCapacity >= payloadCapacity)
            {
                return;
            }

            if (active is not null)
                cache.Retain("SceneUniforms", active.SlotCount, active.PayloadCapacity, active);

            if (!cache.TryTake("SceneUniforms", slotCount, payloadCapacity, out active))
                active = new Generation(++allocationCount, slotCount, payloadCapacity);
        }
    }

    [Test]
    public void GenerationSelection_UsesSmallestAdequatePayloadCapacity()
    {
        VulkanUniformBufferGenerationCache<Generation> cache = new();
        Generation large = new(1, 6, 1024);
        Generation exact = new(2, 6, 256);
        cache.Retain("AutoUniforms", large.SlotCount, large.PayloadCapacity, large);
        cache.Retain("AutoUniforms", exact.SlotCount, exact.PayloadCapacity, exact);

        cache.TryTake("AutoUniforms", slotCount: 6, requiredPayloadCapacity: 256, out Generation selected)
            .ShouldBeTrue();
        selected.ShouldBeSameAs(exact);
        cache.GenerationCount.ShouldBe(1);
    }

    [Test]
    public void MeshUniformCapacityGrowth_PreservesRecordedGenerationsAndDescriptorVariants()
    {
        string root = FindWorkspaceRoot();
        string uniforms = File.ReadAllText(Path.Combine(
            root,
            "XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/BackendObjects/MeshRendering/VkMeshRenderer.Uniforms.cs"));
        string cleanup = File.ReadAllText(Path.Combine(
            root,
            "XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/BackendObjects/MeshRendering/VkMeshRenderer.Cleanup.cs"));
        string pipeline = File.ReadAllText(Path.Combine(
            root,
            "XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/BackendObjects/MeshRendering/VkMeshRenderer.Pipeline.cs"));

        string reserve = SliceBetween(
            uniforms,
            "internal void EnsureUniformDrawSlotCapacity",
            "private int ResolveUniformBufferIndex");
        reserve.ShouldNotContain("DestroyDescriptors");
        reserve.ShouldContain("_descriptorDirty = true;");
        uniforms.ShouldContain("RetainEngineUniformBufferGeneration(name, existing);");
        uniforms.ShouldContain("_retainedEngineUniformBufferGenerations.TryTake");
        uniforms.ShouldContain("RetainAutoUniformBufferGeneration(name, existing);");
        uniforms.ShouldContain("_retainedAutoUniformBufferGenerations.TryTake");
        cleanup.ShouldContain("_retainedEngineUniformBufferGenerations.TryTakeAny");
        cleanup.ShouldContain("_retainedAutoUniformBufferGenerations.TryTakeAny");

        string pipelineTrim = SliceBetween(
            pipeline,
            "if (pipelineInvalidated && _pipelines.Count > 256)",
            "// Check pipeline cache before creating a new pipeline object");
        pipelineTrim.ShouldContain("_pipelines.Clear();");
        pipelineTrim.ShouldNotContain("DestroyPipelines");
    }

    private sealed record Generation(int Id, int SlotCount, uint PayloadCapacity);

    private static string SliceBetween(string source, string startMarker, string endMarker)
    {
        int start = source.IndexOf(startMarker, StringComparison.Ordinal);
        start.ShouldBeGreaterThanOrEqualTo(0);
        int end = source.IndexOf(endMarker, start + startMarker.Length, StringComparison.Ordinal);
        end.ShouldBeGreaterThan(start);
        return source[start..end];
    }

    private static string FindWorkspaceRoot()
    {
        DirectoryInfo? current = new(TestContext.CurrentContext.TestDirectory);
        while (current is not null)
        {
            if (File.Exists(Path.Combine(current.FullName, "XRENGINE.slnx")))
                return current.FullName;
            current = current.Parent;
        }

        throw new DirectoryNotFoundException("Unable to locate the XRENGINE workspace root.");
    }
}
