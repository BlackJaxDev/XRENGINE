using System.Runtime.CompilerServices;
using NUnit.Framework;
using Shouldly;
using Silk.NET.Vulkan;
using XREngine.Rendering;
using XREngine.Rendering.Commands;
using XREngine.Rendering.Vulkan;
using Buffer = Silk.NET.Vulkan.Buffer;

namespace XREngine.UnitTests.Rendering;

/// <summary>
/// Contracts that keep an imported loading placeholder out of Advanced texture publication,
/// and that make a backend readiness change reach the resident texture table.
/// </summary>
[TestFixture]
public sealed class AdvancedTextureFirstResidencyTests
{
    [Test]
    public void AwaitingFirstResidency_MarkedPlaceholder_IsNotPublishable()
    {
        XRTexture2D texture = new();
        VulkanAdvancedSceneResourceRuntime.IsAwaitingFirstResidency(texture).ShouldBeFalse();

        texture.MarkAwaitingFirstResidency();

        texture.IsAwaitingFirstResidency.ShouldBeTrue();
        VulkanAdvancedSceneResourceRuntime.IsAwaitingFirstResidency(texture).ShouldBeTrue();
    }

    [Test]
    public void AwaitingFirstResidency_FirstResidentPublication_ClearsState()
    {
        XRTexture2D texture = new()
        {
            Mipmaps = [new Mipmap2D(8u, 8u, new byte[8 * 8 * 4])],
        };
        texture.MarkAwaitingFirstResidency();
        Mipmap2D[] resident = [new Mipmap2D(16u, 16u, new byte[16 * 16 * 4])];
        ImportedTextureResidentPublicationPlan plan = texture.CreateImportedResidentPublicationPlan(
            new TextureStreamingResidentData(resident, 16u, 16u, 16u),
            includeMipChain: false);

        texture.BeginImportedResidentPublication();
        try
        {
            texture.CommitImportedResidentPublication(plan);
        }
        finally
        {
            texture.CompleteImportedResidentPublication();
        }

        texture.IsAwaitingFirstResidency.ShouldBeFalse();
    }

    [Test]
    public void ReadinessTracker_ChangedEncodedReferenceIndex_AdvancesGeneration()
    {
        VulkanAdvancedTextureReadinessTracker tracker = new();
        tracker.Allocate(4);
        AdvancedTextureRecord[] rows = new AdvancedTextureRecord[2];

        tracker.Update(rows).ShouldBeTrue();
        ulong loading = tracker.Generation;
        tracker.Update(rows).ShouldBeFalse();
        tracker.Generation.ShouldBe(loading);

        rows[1].EncodedReferenceIndex = 2u;

        tracker.Update(rows).ShouldBeTrue();
        tracker.Generation.ShouldNotBe(loading);
    }

    [Test]
    public void ResidentTable_ChangedBackendGeneration_RejectsReuseAndPatch()
    {
        VulkanAdvancedSceneResidentTable<AdvancedTextureRecord> resident = new();
        AdvancedTextureRecord[] rows = new AdvancedTextureRecord[2];
        resident.TryInitialize(Slice(rows.Length), rows).ShouldBeTrue();
        AdvancedGpuOwnerGenerations generations = default;
        resident.StampPublication(1u, 1u, in generations, backendContentGeneration: 1u);

        resident.CanReuseUnchanged(1u, in generations, rows, backendContentGeneration: 1u).ShouldBeTrue();
        resident.CanReuseUnchanged(1u, in generations, rows, backendContentGeneration: 2u).ShouldBeFalse();
        resident.CanPatch(1u, 1u, 1u, hasRetainedJournal: false, rows, backendContentGeneration: 1u).ShouldBeTrue();
        resident.CanPatch(1u, 1u, 1u, hasRetainedJournal: false, rows, backendContentGeneration: 2u).ShouldBeFalse();
    }

    private static VulkanFrameDataSlice Slice(int rowCount)
        => new(
            ArenaIdentity: 1u,
            BufferIdentity: 2u,
            MemoryIdentity: 3u,
            Lane: default,
            ChunkIndex: 0,
            FrameSlot: 0,
            Offset: 0u,
            Length: checked((uint)(rowCount * Unsafe.SizeOf<AdvancedTextureRecord>())),
            Alignment: 16u,
            Generation: 1u,
            Buffer: new Buffer(2u),
            Memory: new DeviceMemory(3u));
}
