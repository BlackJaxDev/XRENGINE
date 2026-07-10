using NUnit.Framework;
using Shouldly;

namespace XREngine.UnitTests.Rendering;

[TestFixture]
public sealed class VulkanCoreHardeningPhase523Tests
{
    [Test]
    public void ImageViews_AreInternedByCompleteStructuralIdentityAndReferenceCounted()
    {
        string source = ReadWorkspaceFile(
            "XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/Resources/Textures/VulkanRenderer.ImageViewLifetime.cs");

        foreach (string identityField in new[]
                 {
                     "ImageHandle", "ImageGeneration", "Flags", "ViewType", "Format",
                     "ComponentSwizzle R", "ComponentSwizzle G", "ComponentSwizzle B", "ComponentSwizzle A",
                     "AspectMask", "BaseMipLevel", "LevelCount", "BaseArrayLayer", "LayerCount"
                 })
        {
            source.ShouldContain(identityField);
        }

        source.ShouldContain("TryAcquireInternedImageView");
        source.ShouldContain("existing.ReferenceCount++");
        source.ShouldContain("ReleaseInternedImageView");
        source.ShouldContain("entry.ReferenceCount--");
        source.ShouldContain("RetireInternedImageViewsForBackingImage");
        source.ShouldContain("entry.ReferenceCount = 0");

        string imageBacked = ReadWorkspaceFile(
            "XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/BackendObjects/Textures/VkImageBackedTexture.cs");
        imageBacked.ShouldContain("CreateView(descriptor, _view)");
        imageBacked.ShouldContain("IsLiveImageViewStructurallyEquivalent(reusableView, in viewInfo)");
    }

    [Test]
    public void MeshAndDeformationBuffers_CompareStableStructuralIdentity()
    {
        string source = ReadWorkspaceFile(
            "XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/BackendObjects/MeshRendering/VkMeshRenderer.Buffers.cs");

        foreach (string identityField in new[]
                 {
                     "ulong Handle", "ulong AllocationGeneration", "ulong Range", "uint Binding",
                     "EBufferTarget Target", "EComponentType ComponentType", "uint ComponentCount", "uint ElementCount"
                 })
        {
            source.ShouldContain(identityField);
        }

        source.ShouldContain("UpdateBufferStructuralIdentitySnapshot()");
        source.ShouldContain("_cachedSkinnedPositionsIdentity != CaptureBufferStructuralIdentity");
        source.ShouldNotContain("ReferenceEquals(_cachedSkinnedPositions");
    }

    [Test]
    public void ExternalIndirectIndexBuffer_IsNotOverwrittenByTheMeshWrapper()
    {
        string source = ReadWorkspaceFile(
            "XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/BackendObjects/MeshRendering/VkMeshRenderer.Buffers.cs");

        int externalBranch = source.IndexOf("else if (_triangleIndexBufferExternallyProvided)", StringComparison.Ordinal);
        int meshBranch = source.IndexOf("else if (Mesh is not null)", externalBranch, StringComparison.Ordinal);
        externalBranch.ShouldBeGreaterThanOrEqualTo(0);
        meshBranch.ShouldBeGreaterThan(externalBranch);
        source.ShouldContain("CaptureBufferStructuralIdentity(_triangleIndexBuffer) != CaptureBufferStructuralIdentity(buffer)");
    }

    [Test]
    public void RetirementReverseIndex_DirtiesOnlyExactRecordedDependents()
    {
        string lifetime = ReadWorkspaceFile(
            "XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/Frame/VulkanRenderer.ResourceLifetimeTracking.cs");
        string allocation = ReadWorkspaceFile(
            "XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/Commands/VulkanRenderer.CommandBufferAllocation.cs");

        lifetime.ShouldContain("InvalidateCachedCommandBuffersForRetiringResource(key, generation, resourceOwner, dependentCommandBuffers)");
        lifetime.ShouldContain("InvalidateCachedCommandBuffersByHandle(");
        allocation.ShouldContain("VulkanExactInvalidationResult");
        allocation.ShouldContain("dependentHandles.Contains(unchecked((ulong)variant.PrimaryCommandBuffer.Handle))");
        allocation.ShouldContain("UnrelatedVariantsPreserved");
        allocation.ShouldContain("GlobalFallbackInvalidations");
    }

    [Test]
    public void DescriptorGeneration_IsPublishedOncePerDirtyEpoch()
    {
        string source = ReadWorkspaceFile(
            "XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/BackendObjects/Textures/VkTexture.cs");
        int start = source.IndexOf("protected void MarkDescriptorPublished()", StringComparison.Ordinal);
        int end = source.IndexOf("protected void MarkUploaded()", start, StringComparison.Ordinal);
        start.ShouldBeGreaterThanOrEqualTo(0);
        end.ShouldBeGreaterThan(start);
        string method = source[start..end];

        method.ShouldContain("if (!IsDescriptorDirty)");
        method.ShouldContain("return;");
        method.ShouldContain("IncrementDescriptorGeneration()");
    }

    [Test]
    public void ExactInvalidationTelemetry_IsCapturedAndGlobalFallbackFailsTheStrictGate()
    {
        string capture = ReadWorkspaceFile("XRENGINE/Engine/Engine.ProfileCapture.cs");
        string harness = ReadWorkspaceFile("Tools/Measure-GameLoopRenderPipeline.ps1");

        capture.ShouldContain("vulkan_exact_variants_dirtied");
        capture.ShouldContain("vulkan_exact_command_chains_dirtied");
        capture.ShouldContain("vulkan_unrelated_variants_preserved");
        capture.ShouldContain("vulkan_global_fallback_invalidations");
        harness.ShouldContain("VulkanGlobalFallbackInvalidationsTotal");
        harness.ShouldContain("[double]$_.VulkanGlobalFallbackInvalidationsTotal -gt 0.0");
    }

    private static string ReadWorkspaceFile(string relativePath)
    {
        string path = Path.Combine(
            ResolveRepoRoot(),
            relativePath.Replace('/', Path.DirectorySeparatorChar));
        File.Exists(path).ShouldBeTrue($"Expected workspace file '{relativePath}'.");
        return File.ReadAllText(path).Replace("\r\n", "\n");
    }

    private static string ResolveRepoRoot()
    {
        string? directory = TestContext.CurrentContext.TestDirectory;
        while (!string.IsNullOrEmpty(directory))
        {
            if (File.Exists(Path.Combine(directory, "XRENGINE.slnx")))
                return directory;
            directory = Directory.GetParent(directory)?.FullName;
        }

        throw new DirectoryNotFoundException("Could not locate repository root from test directory.");
    }
}
