using NUnit.Framework;
using Shouldly;
using Silk.NET.Vulkan;
using System.Runtime.CompilerServices;
using XREngine.Data.Core;
using XREngine.Data.Rendering;
using XREngine.Rendering;
using XREngine.Rendering.Models.Materials.Shaders.Parameters;
using XREngine.Rendering.Models.Materials.Textures;
using XREngine.Rendering.Vulkan;

namespace XREngine.UnitTests.Rendering;

[TestFixture]
public sealed class VulkanStablePacketAndDescriptorTests
{
    [Test]
    public void LayeredShadowUniformState_HashingIsAllocationFreeAfterWarmup()
    {
        LayeredShadowUniformState state = default;
        for (int iteration = 0; iteration < 128; iteration++)
            _ = MeasureLayeredShadowHashAllocations(state);

        MeasureLayeredShadowHashAllocations(state).ShouldBe(0);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static long MeasureLayeredShadowHashAllocations(
        LayeredShadowUniformState state)
    {
        long before = GC.GetAllocatedBytesForCurrentThread();
        int hash = 0;
        for (int iteration = 0; iteration < 1_000; iteration++)
            hash ^= state.GetHashCode();
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        GC.KeepAlive(hash);
        return allocated;
    }

    [Test]
    public void VulkanGenerations_SkipReservedZeroAtWrap()
    {
        VulkanGeneration.NextNonZero(ulong.MaxValue).ShouldBe(1UL);

        long resourceCounter = -1L;
        VulkanGeneration.IncrementNonZero(ref resourceCounter).ShouldBe(1UL);
        resourceCounter.ShouldBe(1L);

        string arenaSource = ReadWorkspaceFile(
            "XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/Commands/CommandBuffers/VulkanWorkerSecondaryCommandArena.cs");
        arenaSource.ShouldContain(
            "Generation = VulkanGeneration.NextNonZero(Generation)");
    }

    [Test]
    public void FrameDataArena_HasExplicitBoundsAndMonotonicRecreation()
    {
        string arena = ReadWorkspaceFile(
            "XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/Resources/Buffers/VulkanMappedFrameArena.cs");
        arena.ShouldContain("private const int MaxFrameSlots = 8;");
        arena.ShouldContain("private const int MaxReservations = 131_072;");
        arena.ShouldContain("if ((uint)requiredFrameSlots > MaxFrameSlots)");
        arena.ShouldContain("_reservations.Count + _frequencyReservations.Count >= MaxReservations");
        arena.ShouldContain(
            "IncrementGeneration();");
        arena.ShouldContain(
            "Volatile.Write(ref _active, 0);");
        arena.ShouldNotContain(
            "_generation = 0");
    }

    [Test]
    public void TypedBindingPublishers_UseCopyOnWriteFrequencyGenerationContract()
    {
        RenderBindingPublisherCollection publishers = new();
        TestBindingPublisher materialPublisher = new(
            ERenderBindingFrequency.Material,
            generation: 7);
        TestBindingPublisher objectPublisher = new(
            ERenderBindingFrequency.Object,
            generation: 11);

        publishers.Add(materialPublisher);
        IRenderBindingPublisher[] firstSnapshot =
            publishers.CaptureSnapshot();
        publishers.Add(objectPublisher);
        publishers.Add(materialPublisher);

        firstSnapshot.ShouldBe([materialPublisher]);
        publishers.CaptureSnapshot().ShouldBe(
            [materialPublisher, objectPublisher]);
        publishers.Remove(materialPublisher).ShouldBeTrue();
        publishers.CaptureSnapshot().ShouldBe([objectPublisher]);
    }

    [Test]
    public void TypedBindingPublication_MapsFrequenciesExplicitlyAndRejectsResources()
    {
        string capture = ReadWorkspaceFile(
            "XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/BackendObjects/Programs/VkRenderProgram.BindingCapture.cs");
        string bindings = ReadWorkspaceFile(
            "XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/BackendObjects/Programs/VkRenderProgram.Bindings.cs");

        capture.ShouldContain("ToVulkanBindingFrequency(frequency)");
        capture.ShouldNotContain("(EVulkanBindingFrequency)frequency");
        capture.ShouldContain("internal void RejectTypedResourceWrite(string resourceKind)");
        bindings.ShouldContain("capture.RejectTypedResourceWrite(\"sampler\")");
        bindings.ShouldContain("capture.RejectTypedResourceWrite(\"storage image\")");
        bindings.ShouldContain("capture.RejectTypedResourceWrite(\"buffer\")");
    }

    [Test]
    public void BindingSnapshot_PreservesTypedUniformOwnerGenerations()
    {
        Dictionary<string, ProgramUniformValue> uniforms =
            new(StringComparer.Ordinal)
            {
                ["MaterialValue"] = new ProgramUniformValue(
                    EShaderVarType._float,
                    2f),
                ["ObjectValue"] = new ProgramUniformValue(
                    EShaderVarType._uint,
                    9u),
            };
        Dictionary<string, VulkanRuntimeUniformPublication> publications =
            new(StringComparer.Ordinal)
            {
                ["MaterialValue"] = new(
                    EVulkanBindingFrequency.Material,
                    7),
                ["ObjectValue"] = new(
                    EVulkanBindingFrequency.Object,
                    11),
            };
        HashSet<string> mutableLegacyUniformNames =
            new(StringComparer.Ordinal);
        HashSet<string> requiredSamplerNames = new(StringComparer.Ordinal);
        Dictionary<uint, XRTexture> samplers = [];
        Dictionary<uint, string> samplerNames = [];
        Dictionary<string, XRTexture> namedSamplers =
            new(StringComparer.Ordinal);
        Dictionary<uint, ProgramImageBinding> images = [];
        ComputeDispatchSnapshot snapshot = new();

        snapshot.ExchangeCapturedBindings(
            ref uniforms,
            ref publications,
            ref mutableLegacyUniformNames,
            ref requiredSamplerNames,
            ref samplers,
            ref samplerNames,
            ref namedSamplers,
            ref images);
        PublishBindingLayoutSignaturesForTest(snapshot);

        snapshot.TryGetRuntimeUniformPublication(
                "MaterialValue",
                out VulkanRuntimeUniformPublication materialPublication)
            .ShouldBeTrue();
        materialPublication.Frequency
            .ShouldBe(EVulkanBindingFrequency.Material);
        materialPublication.Generation.ShouldBe(7UL);
        snapshot.TypedPublicationGenerations.Material.ShouldNotBe(0UL);
        snapshot.TypedPublicationGenerations.Object.ShouldNotBe(0UL);
        snapshot.TypedPublicationGenerations.View.ShouldBe(0UL);
        snapshot.RuntimeUniformPublicationLayoutSignature.ShouldNotBe(0UL);
    }

    [Test]
    public void ProgramBindingSchema_CompilesTypedValueAndResourceOwnership()
    {
        AutoUniformBlockInfo block = new(
            "DrawData",
            "drawData",
            Set: VulkanMeshRenderingConventions.DescriptorSetMaterial,
            Binding: 4,
            Size: 160,
            Members:
            [
                CreateAutoUniformMember(
                    nameof(EEngineUniform.ModelMatrix),
                    EShaderVarType._mat4,
                    offset: 0,
                    size: 64),
                CreateAutoUniformMember(
                    "CurrViewProjection",
                    EShaderVarType._mat4,
                    offset: 64,
                    size: 64),
                CreateAutoUniformMember(
                    "TransformId",
                    EShaderVarType._uint,
                    offset: 128,
                    size: 4),
                CreateAutoUniformMember(
                    "BaseColor",
                    EShaderVarType._vec4,
                    offset: 144,
                    size: 16),
            ],
            ShaderType: EShaderType.Vertex);
        DescriptorBindingInfo descriptor = new(
            Set: VulkanMeshRenderingConventions.DescriptorSetMaterial,
            Binding: 5,
            DescriptorType.CombinedImageSampler,
            ShaderStageFlags.FragmentBit,
            Count: 8,
            Name: "MaterialTextures");

        VulkanProgramBindingSchema schema = VulkanProgramBindingSchema.Compile(
            programLinkGeneration: 17,
            new Dictionary<string, AutoUniformBlockInfo>(StringComparer.Ordinal)
            {
                [block.InstanceName] = block,
            },
            [descriptor]);

        schema.ProgramLinkGeneration.ShouldBe(17UL);
        schema.TryGetAutoUniformBlock(block.InstanceName, out VulkanAutoUniformBindingSchema valueSchema)
            .ShouldBeTrue();
        valueSchema.IsFastPathEligible.ShouldBeTrue();
        valueSchema.Operations[0].SourceKind.ShouldBe(EVulkanAutoUniformSourceKind.Engine);
        valueSchema.Operations[0].Frequency.ShouldBe(EVulkanBindingFrequency.Object);
        valueSchema.Operations[0].EngineUniform.ShouldBe(EEngineUniform.ModelMatrix);
        valueSchema.Operations[1].TemporalSource
            .ShouldBe(EVulkanTemporalUniformSource.CurrentViewProjection);
        valueSchema.Operations[1].Frequency.ShouldBe(EVulkanBindingFrequency.View);
        valueSchema.Operations[2].SpecialSource
            .ShouldBe(EVulkanAutoUniformSpecialSource.TransformId);
        valueSchema.Operations[3].SourceKind
            .ShouldBe(EVulkanAutoUniformSourceKind.MaterialOrRuntime);
        valueSchema.Operations[3].Frequency.ShouldBe(EVulkanBindingFrequency.Material);
        valueSchema.FrequencyMask.ShouldBe(
            EVulkanBindingFrequencyMask.View |
            EVulkanBindingFrequencyMask.Material |
            EVulkanBindingFrequencyMask.Object |
            EVulkanBindingFrequencyMask.RuntimeCallback);

        VulkanDescriptorBindingSchemaEntry resource = schema.DescriptorBindings.ShouldHaveSingleItem();
        resource.Owner.ShouldBe(EVulkanDescriptorOwner.Material);
        resource.ArrayPolicy.ShouldBe(EVulkanDescriptorArrayPolicy.FixedCount);
        resource.DependsOnTopologyGeneration.ShouldBeTrue();
        resource.DependsOnContentGeneration.ShouldBeTrue();
    }

    [Test]
    public void ProgramBindingSchema_UsesPhysicalAutoUniformFrequencyAsDescriptorOwner()
    {
        EVulkanBindingFrequency[] frequencies =
        [
            EVulkanBindingFrequency.Frame,
            EVulkanBindingFrequency.View,
            EVulkanBindingFrequency.Pass,
            EVulkanBindingFrequency.Material,
            EVulkanBindingFrequency.Object,
            EVulkanBindingFrequency.Instance,
            EVulkanBindingFrequency.RuntimeCallback,
        ];
        EVulkanDescriptorOwner[] expectedOwners =
        [
            EVulkanDescriptorOwner.Frame,
            EVulkanDescriptorOwner.View,
            EVulkanDescriptorOwner.Pass,
            EVulkanDescriptorOwner.Material,
            EVulkanDescriptorOwner.Object,
            EVulkanDescriptorOwner.Instance,
            EVulkanDescriptorOwner.RuntimeCallback,
        ];
        Dictionary<string, AutoUniformBlockInfo> blocks =
            new(frequencies.Length, StringComparer.Ordinal);
        DescriptorBindingInfo[] descriptorBindings =
            new DescriptorBindingInfo[frequencies.Length];

        for (int index = 0; index < frequencies.Length; index++)
        {
            string instanceName = $"frequencyBlock{index}";
            uint binding = checked((uint)(64 + index));
            blocks.Add(
                instanceName,
                new AutoUniformBlockInfo(
                    $"FrequencyBlock{index}",
                    instanceName,
                    VulkanDescriptorManager.GlobalsSetIndex,
                    binding,
                    Size: 16,
                    Members:
                    [
                        CreateAutoUniformMember(
                            $"Value{index}",
                            EShaderVarType._vec4,
                            offset: 0,
                            size: 16),
                    ],
                    EShaderType.Fragment,
                    frequencies[index]));
            descriptorBindings[index] = new DescriptorBindingInfo(
                VulkanDescriptorManager.GlobalsSetIndex,
                binding,
                DescriptorType.UniformBuffer,
                ShaderStageFlags.FragmentBit,
                Count: 1,
                instanceName);
        }

        VulkanProgramBindingSchema schema =
            VulkanProgramBindingSchema.Compile(
                programLinkGeneration: 12,
                blocks,
                descriptorBindings);

        schema.DescriptorBindings
            .Select(binding => binding.Owner)
            .ShouldBe(expectedOwners);
    }

    [Test]
    public void AutoUniformOwnerSlotTable_ReusesOwnersAndBoundsFallbackSlots()
    {
        VulkanAutoUniformOwnerSlotTable table = new(
            frameCount: 2,
            drawSlotCapacity: 3);

        table.ResolvePublished(frameIndex: 0, drawSlot: 2)
            .ShouldBe(2);
        table.ResolveAndPublish(
                frameIndex: 0,
                drawSlot: 2,
                ownerIdentity: 101)
            .ShouldBe(0);
        table.ResolveAndPublish(
                frameIndex: 0,
                drawSlot: 1,
                ownerIdentity: 101)
            .ShouldBe(0);
        table.ResolveAndPublish(
                frameIndex: 0,
                drawSlot: 2,
                ownerIdentity: 202)
            .ShouldBe(1);
        table.ResolveAndPublish(
                frameIndex: 1,
                drawSlot: 0,
                ownerIdentity: 303)
            .ShouldBe(2);

        table.OwnerCount.ShouldBe(3);
        table.ResolvePublished(frameIndex: 0, drawSlot: 1)
            .ShouldBe(0);
        table.ResolvePublished(frameIndex: 1, drawSlot: 0)
            .ShouldBe(2);

        table.ResolveAndPublish(
                frameIndex: 1,
                drawSlot: 1,
                ownerIdentity: 404)
            .ShouldBe(1);
        table.OwnerCount.ShouldBe(3);
        table.ResolvePublished(frameIndex: 1, drawSlot: 1)
            .ShouldBe(1);
    }

    [Test]
    public void FrequencyAutoUniformReservation_SeparatesOwnersAndFrameLedgers()
    {
        VulkanFrequencyAutoUniformReservationKey materialOwner = new(
            PublicationLayoutSignature: 11,
            EVulkanBindingFrequency.Material,
            OwnerIdentity: 101);
        VulkanFrequencyAutoUniformReservationKey anotherMaterial =
            materialOwner with { OwnerIdentity = 202 };
        VulkanFrequencyAutoUniformReservationKey objectOwner =
            materialOwner with
            {
                Frequency = EVulkanBindingFrequency.Object,
            };
        VulkanFrequencyAutoUniformReservationKey incompatibleLayout =
            materialOwner with
            {
                PublicationLayoutSignature = 12,
            };

        materialOwner.ShouldNotBe(anotherMaterial);
        materialOwner.ShouldNotBe(objectOwner);
        materialOwner.ShouldNotBe(incompatibleLayout);

        VulkanFrequencyAutoUniformReservation reservation = new(
            materialOwner,
            offset: 512,
            size: 96,
            recordingVisibleGeneration: 13,
            frameCount: 3);
        reservation.Key.ShouldBe(materialOwner);
        reservation.Offset.ShouldBe(512UL);
        reservation.Size.ShouldBe(96u);
        reservation.RecordingVisibleGeneration.ShouldBe(13UL);
        reservation.PublicationStates.Length.ShouldBe(3);
        VulkanAutoUniformPublicationIdentity identity =
            reservation.CapturePublicationIdentity(contentGeneration: 17);
        identity.PublicationLayoutSignature.ShouldBe(11UL);
        identity.Frequency.ShouldBe(EVulkanBindingFrequency.Material);
        identity.OwnerIdentity.ShouldBe(101UL);
        identity.ContentGeneration.ShouldBe(17UL);
        identity.RecordingVisibleGeneration.ShouldBe(13UL);

        reservation.PublicationStates[0].PublishFrequency(
            EVulkanBindingFrequency.Material,
            generation: 17);
        reservation.PublicationStates[0].IsFrequencyPublished(
                EVulkanBindingFrequency.Material,
                generation: 17)
            .ShouldBeTrue();
        reservation.PublicationStates[1].IsFrequencyPublished(
                EVulkanBindingFrequency.Material,
                generation: 17)
            .ShouldBeFalse();
    }

    [Test]
    public void AutoUniformPublicationIdentity_SeparatesLayoutContentAndRecording()
    {
        VulkanAutoUniformPublicationIdentity original = new(
            PublicationLayoutSignature: 11,
            EVulkanBindingFrequency.Object,
            OwnerIdentity: 22,
            ContentGeneration: 33,
            RecordingVisibleGeneration: 44);
        VulkanAutoUniformPublicationIdentity contentChanged =
            original with { ContentGeneration = 34 };
        VulkanAutoUniformPublicationIdentity layoutChanged =
            original with { PublicationLayoutSignature = 12 };
        VulkanAutoUniformPublicationIdentity arenaChanged =
            original with { RecordingVisibleGeneration = 45 };

        original.IsComplete.ShouldBeTrue();
        original.HasStableRecordingLocation(contentChanged).ShouldBeTrue();
        contentChanged.RequiresContentPublication(original).ShouldBeTrue();
        original.HasStableRecordingLocation(layoutChanged).ShouldBeFalse();
        original.HasStableRecordingLocation(arenaChanged).ShouldBeFalse();
    }

    [Test]
    public void DescriptorOwnership_SharesOnlyDrawSlotInvariantBindingTables()
    {
        DescriptorBindingInfo dynamicUniform = new(
            VulkanDescriptorManager.GlobalsSetIndex,
            Binding: 64,
            DescriptorType.UniformBufferDynamic,
            ShaderStageFlags.FragmentBit,
            Count: 1,
            Name: "FrameData");
        DescriptorBindingInfo sampledImage = new(
            VulkanMeshRenderingConventions.DescriptorSetMaterial,
            Binding: 0,
            DescriptorType.CombinedImageSampler,
            ShaderStageFlags.FragmentBit,
            Count: 1,
            Name: "Albedo");

        VkMeshRenderer.AreDescriptorBindingsDrawSlotInvariant(
                [dynamicUniform, sampledImage],
                usesSharedMaterialTier: false,
                descriptorHeapDrawBindingActive: false)
            .ShouldBeTrue();
        VkMeshRenderer.AreDescriptorBindingsDrawSlotInvariant(
                [
                    dynamicUniform with
                    {
                        DescriptorType = DescriptorType.UniformBuffer,
                    },
                ],
                usesSharedMaterialTier: false,
                descriptorHeapDrawBindingActive: false)
            .ShouldBeFalse();
        VkMeshRenderer.AreDescriptorBindingsDrawSlotInvariant(
                [
                    dynamicUniform with
                    {
                        DescriptorType = DescriptorType.StorageBufferDynamic,
                    },
                ],
                usesSharedMaterialTier: false,
                descriptorHeapDrawBindingActive: false)
            .ShouldBeFalse();
        VkMeshRenderer.AreDescriptorBindingsDrawSlotInvariant(
                [dynamicUniform],
                usesSharedMaterialTier: false,
                descriptorHeapDrawBindingActive: true)
            .ShouldBeFalse();

        DescriptorBindingInfo fixedMaterialUniform = dynamicUniform with
        {
            Set = VulkanMeshRenderingConventions.DescriptorSetMaterial,
            DescriptorType = DescriptorType.UniformBuffer,
        };
        VkMeshRenderer.AreDescriptorBindingsDrawSlotInvariant(
                [dynamicUniform, fixedMaterialUniform],
                usesSharedMaterialTier: true,
                descriptorHeapDrawBindingActive: false)
            .ShouldBeTrue();
    }

    [Test]
    public void ProgramRelink_InvalidatesOnlyMeshLocalInterfaceDependentState()
    {
        string preparation = ReadWorkspaceFile(
            "XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/BackendObjects/MeshRendering/VkMeshRenderer.Preparation.cs");
        string pipeline = ReadWorkspaceFile(
            "XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/BackendObjects/MeshRendering/VkMeshRenderer.Pipeline.cs");

        preparation.ShouldContain(
            "bool replacingSameInterface =");
        preparation.ShouldContain(
            "ObserveActiveProgramLinkGeneration(_program, replacingSameInterface);");
        pipeline.ShouldContain(
            "ObserveActiveProgramLinkGeneration(_program, replacingSameInterface);");
        AssertOrdered(
            preparation,
            "_activeProgramLinkGeneration = linkGeneration;",
            "_pipelines.Clear();",
            "ReleaseDescriptorAllocation();",
            "DestroyEngineUniformBuffers();",
            "DestroyAutoUniformBuffers();",
            "_pipelineDirty = true;",
            "_descriptorDirty = true;",
            "_vertexInputStateDirty = true;");
    }

    [Test]
    public void QualifyingMeshBindingPath_UsesExactDescriptorCoordinates()
    {
        string layouts = ReadWorkspaceFile(
            "XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/BackendObjects/Programs/VkRenderProgram.Layouts.cs");
        string descriptors = ReadWorkspaceFile(
            "XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/BackendObjects/MeshRendering/VkMeshRenderer.Descriptors.cs");
        string descriptorUniforms = ReadWorkspaceFile(
            "XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/BackendObjects/MeshRendering/VkMeshRenderer.DescriptorUniforms.cs");
        string drawing = ReadWorkspaceFile(
            "XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/BackendObjects/MeshRendering/VkMeshRenderer.Drawing.cs");

        layouts.ShouldContain(
            "_autoUniformBlocksByBinding[(block.Set, block.Binding)] = block;");
        layouts.ShouldContain(
            "=> _autoUniformBlocksByBinding.TryGetValue((set, binding), out block!);");
        descriptors.ShouldNotContain("TryGetAutoUniformBlockFuzzy");
        descriptorUniforms.ShouldNotContain("TryGetAutoUniformBlockFuzzy");
        drawing.ShouldNotContain("TryGetAutoUniformBlockFuzzy");
    }

    [Test]
    public void FrequencyOwnedAutoUniforms_UseProgramPlansAndRendererArenaReservations()
    {
        string program = ReadWorkspaceFile(
            "XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/BackendObjects/Programs/VkRenderProgram.Bindings.cs");
        string arena = ReadWorkspaceFile(
            "XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/Resources/Buffers/VulkanMappedFrameArena.cs");
        string meshUniforms = ReadWorkspaceFile(
            "XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/BackendObjects/MeshRendering/VkMeshRenderer.Uniforms.cs");

        program.ShouldContain("_frequencyOwnedAutoUniformWritePlans");
        program.ShouldContain("_autoUniformMaterialWritePlans");
        arena.ShouldContain("_frequencyReservations");
        arena.ShouldContain("TryGetOrReserveFrequencyAutoUniformRange(");
        arena.ShouldContain("VulkanFrequencyAutoUniformReservationKey key = new(");
        meshUniforms.ShouldContain(
            "Renderer.MappedFrameArena");
        meshUniforms.ShouldContain(
            "!arena.TryGetOrReserveFrequencyAutoUniformRange(");
        meshUniforms.ShouldContain(
            "frequencyReservation.PublicationStates;");
        meshUniforms.ShouldContain(
            "ref publicationStates[publicationStateIndex];");
        meshUniforms.ShouldContain("draw.UseUnjitteredProjection");
        meshUniforms.ShouldContain(
            "bool bindingSnapshotEligible =");
        meshUniforms.ShouldContain(
            "!materialOwned ||");
        meshUniforms.ShouldContain(
            "operation.SourceKind ==");
    }

    [Test]
    public void DescriptorPublication_SeparatesTopologyContentAndPreparedRecordingState()
    {
        string schema = ReadWorkspaceFile(
            "XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/Shaders/VulkanDescriptorBindingSchemaEntry.cs");
        string allocation = ReadWorkspaceFile(
            "XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/BackendObjects/MeshRendering/Descriptors/VkMeshRenderer.DescriptorAllocation.cs");
        string writes = ReadWorkspaceFile(
            "XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/BackendObjects/MeshRendering/VkMeshRenderer.DescriptorWrites.cs");
        string drawing = ReadWorkspaceFile(
            "XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/BackendObjects/MeshRendering/VkMeshRenderer.Drawing.cs");

        schema.ShouldContain("bool DependsOnTopologyGeneration");
        schema.ShouldContain("bool DependsOnContentGeneration");
        allocation.ShouldContain("public ulong LayoutFingerprint;");
        allocation.ShouldContain("public ulong SchemaFingerprint;");
        allocation.ShouldContain("public ulong ResourceFingerprint;");
        allocation.ShouldContain("public ulong StableResourceFingerprint;");
        allocation.ShouldContain("public ulong TopologyGeneration = 1;");
        allocation.ShouldContain("public ulong ContentGeneration");
        allocation.ShouldContain("public bool IsOwnerGenerationPublished(");
        allocation.ShouldContain("ulong materialResourceVersion)");
        allocation.ShouldContain("Material?.BindingResourceVersion");
        allocation.ShouldContain("DescriptorWriteSignatures");
        writes.ShouldContain("if (DescriptorWriteMatches(allocation, bufferKey, bufferSignature))");
        writes.ShouldContain("if (DescriptorWriteMatches(allocation, imageKey, imageSignature))");
        writes.ShouldContain("if (DescriptorWriteMatches(allocation, texelKey, texelSignature))");
        writes.ShouldContain("Span<WriteDescriptorSet> writeSpan =");
        writes.ShouldNotContain("WriteDescriptorSet[] writeArray");
        writes.ShouldContain("scratch.TemplateWrites");
        writes.ShouldNotContain("List<WriteDescriptorSet> setWrites = []");
        drawing.ShouldContain("TryRentPreparedDescriptorBindings(");
        drawing.ShouldContain("new VulkanPreparedDescriptorSetBinding(");
        drawing.ShouldContain("BindPreparedMeshDescriptorSets(");
        drawing.ShouldContain("offsets = dynamicOffsets.AsSpan(");
    }

    [Test]
    public void DescriptorOwnerGeneration_RequiresExactSlotPublication()
    {
        VkMeshRenderer.DescriptorAllocation allocation = new()
        {
            SlotPublishedTopologyGenerations = new ulong[2],
            SlotPublishedContentGenerations = new ulong[2],
            SlotPublishedMaterialResourceVersions = new ulong[2],
        };

        allocation.IsOwnerGenerationPublished(0, 0).ShouldBeFalse();
        allocation.PublishOwnerGeneration(0);
        allocation.IsOwnerGenerationPublished(0, 0).ShouldBeTrue();
        allocation.IsOwnerGenerationPublished(1, 0).ShouldBeFalse();

        ulong changedGeneration = allocation.AdvanceContentGeneration();
        changedGeneration.ShouldNotBe(1UL);
        allocation.IsOwnerGenerationPublished(0, 0).ShouldBeFalse();

        allocation.PublishOwnerGeneration(1);
        allocation.IsOwnerGenerationPublished(0, 0).ShouldBeFalse();
        allocation.IsOwnerGenerationPublished(1, 0).ShouldBeTrue();
    }

    [Test]
    public void StableDescriptorReuse_UsesOwnerGenerationBeforeFingerprintBackstop()
    {
        string descriptors = ReadWorkspaceFile(
            "XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/BackendObjects/MeshRendering/VkMeshRenderer.Descriptors.cs");

        int ownerGenerationCheck = descriptors.IndexOf(
            "TryActivatePublishedDescriptorOwnerGeneration(",
            StringComparison.Ordinal);
        int fingerprintBackstop = descriptors.IndexOf(
            "RecordVulkanDescriptorRecordsValidated(bindings.Count);",
            ownerGenerationCheck,
            StringComparison.Ordinal);

        ownerGenerationCheck.ShouldBeGreaterThanOrEqualTo(0);
        fingerprintBackstop.ShouldBeGreaterThan(ownerGenerationCheck);
        descriptors[ownerGenerationCheck..fingerprintBackstop]
            .ShouldNotContain("RecordVulkanDescriptorRecordsValidated");
        descriptors.ShouldContain("includeFrameSourceDescriptors: false");
        descriptors.ShouldContain("allocation.IsOwnerGenerationPublished(");
        descriptors.ShouldContain("_descriptorAllocationsByOwner.TryGetValue(");
        descriptors.ShouldContain("CreateDescriptorOwnerLookupKey(");
    }

    [Test]
    public void ReusableFrameDataRefresh_ConsumesPreparedRequestsWithoutWalkingFrameOps()
    {
        string recording = ReadWorkspaceFile(
            "XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/Commands/CommandBuffers/FrameData/VulkanRenderer.CommandBufferReuse.FrameData.cs")
            .Replace("\r\n", "\n");
        int refreshStart = recording.IndexOf(
            "private bool TryRefreshReusableCommandBufferFrameData(",
            StringComparison.Ordinal);
        int refreshEnd = recording.IndexOf(
            "private static string FormatForcedCommandBufferDirtyReason(",
            refreshStart,
            StringComparison.Ordinal);
        refreshStart.ShouldBeGreaterThanOrEqualTo(0);
        refreshEnd.ShouldBeGreaterThan(refreshStart);
        string refresh = recording[refreshStart..refreshEnd];
        string scratch = ReadWorkspaceFile(
            "XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/Commands/CommandBuffers/VulkanRenderer.CommandBufferRecordingScratch.cs");

        refresh.ShouldContain(
            "ReadOnlySpan<VulkanReusableFrameDataRefreshRequest> requests");
        refresh.ShouldContain("ref activeRequests[i]");
        refresh.ShouldContain("request.DrawUniformSlot");
        refresh.ShouldContain("request.ComputeDescriptorKey");
        refresh.ShouldContain("ownerWorkRequests");
        refresh.ShouldContain("CanUseOwnerOnlyRefresh(batchInfo)");
        refresh.ShouldContain(
            "TryRefreshReusableFrequencyData(");
        SourceContractWorkspace.ReadVulkanSourcesContaining(
            "snapshot?.MutableLegacyUniformNameSignature ?? 0UL")
            .ShouldContain("snapshot?.MutableLegacyUniformNameSignature ?? 0UL");
        recording.ShouldContain(
            "RecordVulkanPreparedFrameDataDrawVisited(dynamicUi)");
        recording.ShouldContain("dynamicUi: false");
        recording.ShouldContain("dynamicUi: true");
        string meshDrawing = ReadWorkspaceFile(
            "XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/BackendObjects/MeshRendering/VkMeshRenderer.Drawing.cs");
        meshDrawing.ShouldContain(
            "mutable legacy callback writes were not captured");
        refresh.ShouldNotContain("FrameOp[] ops");
        refresh.ShouldNotContain("GetFrameWideMeshDrawUniformSlot(");
        refresh.ShouldNotContain("BuildFrameOpPlannerStateKey(");
        SourceContractWorkspace.ReadVulkanSourcesContaining("BuildReusableFrameDataRefreshRequests(")
            .ShouldContain("BuildReusableFrameDataRefreshRequests(");
        scratch.ShouldContain("_primaryReusableFrameDataRefreshRequests");
        scratch.ShouldContain("_dynamicUiReusableFrameDataRefreshRequests");
        scratch.ShouldContain(
            "EnsureReusableFrameDataRefreshRequestCapacity(");
        scratch.ShouldContain(
            "PrimaryReusableFrameDataOwnerWorkRequests");
        scratch.ShouldContain(
            "DynamicUiReusableFrameDataOwnerWorkRequests");
        scratch.ShouldContain("_primaryReusableFrameDataRefreshRequestCount);");
        scratch.ShouldContain("_dynamicUiReusableFrameDataRefreshRequestCount);");
    }

    [Test]
    public void ReusableFrameDataCohortSignature_ExcludesMutableOwnerContent()
    {
        string recording = ReadWorkspaceFile(
            "XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/Commands/CommandBuffers/FrameData/VulkanRenderer.CommandBufferRecording.FrameData.cs")
            .Replace("\r\n", "\n");
        int signatureStart = recording.IndexOf(
            "private static ulong ComputeReusableMeshStableDataSignature(",
            StringComparison.Ordinal);
        int signatureEnd = recording.IndexOf(
            "/// <summary>",
            signatureStart,
            StringComparison.Ordinal);

        signatureStart.ShouldBeGreaterThanOrEqualTo(0);
        signatureEnd.ShouldBeGreaterThan(signatureStart);
        string signature = recording[signatureStart..signatureEnd];

        signature.ShouldContain("material?.BindingResourceVersion");
        signature.ShouldContain("snapshot?.DescriptorSetLayoutSignature");
        signature.ShouldContain(
            "snapshot?.RuntimeUniformPublicationLayoutSignature");
        signature.ShouldNotContain("BindingValueVersion");
        signature.ShouldNotContain("BindingLayoutVersion");
        signature.ShouldNotContain("AutoUniformPublication");
        signature.ShouldNotContain("TypedPublicationGenerations");
        signature.ShouldNotContain("draw.TransformId");
        signature.ShouldNotContain("draw.BillboardMode");
    }

    [Test]
    public void ReusableFrameDataRefreshState_RequiresExactStableOwnerPublication()
    {
        VulkanReusableFrameDataRefreshState state = new();
        VulkanReusableFrameDataRefreshBatchInfo first = new(123UL, 4, false);

        state.CanUseOwnerOnlyRefresh(first).ShouldBeFalse();
        state.BeginFullRefresh(first);
        state.AddFallbackRequestIndex(2);
        state.CommitFullRefresh();
        state.CanUseOwnerOnlyRefresh(first).ShouldBeTrue();
        state.FallbackRequestIndices.ToArray().ShouldBe([2]);
        state.CanUseOwnerOnlyRefresh(new(124UL, 4, false)).ShouldBeFalse();
        state.CanUseOwnerOnlyRefresh(new(123UL, 5, false)).ShouldBeFalse();

        state.Invalidate();
        state.CanUseOwnerOnlyRefresh(first).ShouldBeFalse();
        state.BeginFullRefresh(first);
        state.CanUseOwnerOnlyRefresh(first).ShouldBeFalse();
    }

    [Test]
    public void OpenXrRefreshRequestStorage_PreventsProducerOverwriteDuringWorkerRead()
    {
        VulkanOpenXrFrameDataRefreshRequestStorage storage = new();
        VulkanReusableFrameDataRefreshRequest[] requests = [default];
        VulkanReusableFrameDataRefreshRequest[] ownerWork = [default];
        VulkanReusableFrameDataRefreshBatchInfo batchInfo =
            new(123UL, 1, false);
        VulkanOpenXrFrameDataRefreshRequestLease first =
            storage.Publish(requests, ownerWork, batchInfo);

        first.TryAcquire(out ReadOnlySpan<
                VulkanReusableFrameDataRefreshRequest> published,
            out ReadOnlySpan<VulkanReusableFrameDataRefreshRequest>
                publishedOwnerWork,
            out VulkanReusableFrameDataRefreshBatchInfo
                publishedBatchInfo).ShouldBeTrue();
        published.Length.ShouldBe(1);
        publishedOwnerWork.Length.ShouldBe(1);
        publishedBatchInfo.ShouldBe(batchInfo);
        Should.Throw<InvalidOperationException>(
            () => storage.Publish(requests, ownerWork, batchInfo));
        first.Release();

        VulkanOpenXrFrameDataRefreshRequestLease second =
            storage.Publish(ReadOnlySpan<
                    VulkanReusableFrameDataRefreshRequest>.Empty,
                ReadOnlySpan<
                    VulkanReusableFrameDataRefreshRequest>.Empty,
                default);
        first.TryAcquire(out _, out _, out _).ShouldBeFalse();
        second.TryAcquire(
            out published,
            out publishedOwnerWork,
            out publishedBatchInfo).ShouldBeTrue();
        published.IsEmpty.ShouldBeTrue();
        publishedOwnerWork.IsEmpty.ShouldBeTrue();
        publishedBatchInfo.ShouldBe(default);
        second.Release();

        string storageSource = ReadWorkspaceFile(
            "XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/OpenXR/VulkanOpenXrFrameDataRefreshRequestStorage.cs");
        storageSource.ShouldContain("if (_count > requests.Length)");
        storageSource.ShouldContain("_count - requests.Length");
        storageSource.ShouldContain(
            "if (_ownerWorkCount > ownerWorkRequests.Length)");
    }

    [Test]
    public void AutoUniformPublicationState_TracksFrequencyGenerationsIndependently()
    {
        AutoUniformBlockInfo block = new(
            "AutoData",
            "autoData",
            Set: VulkanDescriptorManager.GlobalsSetIndex,
            Binding: 0,
            Size: 64,
            Members:
            [
                CreateAutoUniformMember(
                    nameof(EEngineUniform.ModelMatrix),
                    EShaderVarType._mat4,
                    offset: 0,
                    size: 64),
            ],
            ShaderType: EShaderType.Vertex);
        VulkanProgramBindingSchema programSchema =
            VulkanProgramBindingSchema.Compile(
                programLinkGeneration: 9,
                new Dictionary<string, AutoUniformBlockInfo>(
                    StringComparer.Ordinal)
                {
                    [block.InstanceName] = block,
                },
                []);
        programSchema.TryGetAutoUniformBlock(
            block.InstanceName,
            out VulkanAutoUniformBindingSchema valueSchema).ShouldBeTrue();
        AutoUniformMaterialWritePlan plan = new(
            valueSchema,
            materialLayoutVersion: 1,
            materialValueVersion: 2,
            runtimeUniformNameSignature: 3,
            runtimeUniformPublicationLayoutSignature: 0,
            new byte[64],
            valueSchema.Operations);
        VulkanProgramBindingSchema compatibleProgramSchema =
            VulkanProgramBindingSchema.Compile(
                programLinkGeneration: 10,
                new Dictionary<string, AutoUniformBlockInfo>(
                    StringComparer.Ordinal)
                {
                    [block.InstanceName] = block,
                },
                []);
        compatibleProgramSchema.TryGetAutoUniformBlock(
            block.InstanceName,
            out VulkanAutoUniformBindingSchema compatibleValueSchema)
            .ShouldBeTrue();
        AutoUniformMaterialWritePlan compatiblePlan = new(
            compatibleValueSchema,
            materialLayoutVersion: 1,
            materialValueVersion: 2,
            runtimeUniformNameSignature: 3,
            runtimeUniformPublicationLayoutSignature: 0,
            new byte[64],
            compatibleValueSchema.Operations);

        VulkanAutoUniformPublicationState state = default;
        state.PublishPlan(plan);
        state.IsPlanPublished(compatiblePlan).ShouldBeTrue();
        state.IsFrequencyPublished(
            EVulkanBindingFrequency.Frame,
            generation: 0).ShouldBeFalse();

        state.PublishFrequency(
            EVulkanBindingFrequency.Frame,
            generation: 0);
        state.IsFrequencyPublished(
            EVulkanBindingFrequency.Frame,
            generation: 0).ShouldBeTrue();
        state.IsFrequencyPublished(
            EVulkanBindingFrequency.Object,
            generation: 0).ShouldBeFalse();

        state.PublishFrequency(
            EVulkanBindingFrequency.Object,
            generation: 17);
        state.IsFrequencyPublished(
            EVulkanBindingFrequency.Object,
            generation: 17).ShouldBeTrue();
        state.IsFrequencyPublished(
            EVulkanBindingFrequency.Frame,
            generation: 0).ShouldBeTrue();

        AutoUniformMaterialWritePlan replacementPlan = new(
            valueSchema,
            materialLayoutVersion: 4,
            materialValueVersion: 5,
            runtimeUniformNameSignature: 6,
            runtimeUniformPublicationLayoutSignature: 0,
            new byte[64],
            valueSchema.Operations);
        state.PublishPlan(replacementPlan);
        state.IsPlanPublished(plan).ShouldBeFalse();
        state.IsPlanPublished(replacementPlan).ShouldBeTrue();
        state.IsFrequencyPublished(
            EVulkanBindingFrequency.Frame,
            generation: 0).ShouldBeFalse();
        state.IsFrequencyPublished(
            EVulkanBindingFrequency.Object,
            generation: 17).ShouldBeFalse();

        state.Invalidate();
        state.IsPlanPublished(replacementPlan).ShouldBeFalse();
        state.IsFrequencyPublished(
            EVulkanBindingFrequency.Object,
            generation: 17).ShouldBeFalse();
    }

    [Test]
    public void AutoUniformPublicationSnapshot_ExposesIndependentOwnerGenerations()
    {
        VulkanAutoUniformPublicationSnapshot snapshot = new()
        {
            FrameGeneration = 1,
            ViewGeneration = 2,
            PassGeneration = 3,
            ObjectGeneration = 5,
            InstanceGeneration = 6,
            RuntimeCallbackGeneration = 7,
        };

        snapshot.GetGeneration(
            EVulkanBindingFrequency.Frame,
            materialGeneration: 4).ShouldBe(1UL);
        snapshot.GetGeneration(
            EVulkanBindingFrequency.View,
            materialGeneration: 4).ShouldBe(2UL);
        snapshot.GetGeneration(
            EVulkanBindingFrequency.Pass,
            materialGeneration: 4).ShouldBe(3UL);
        snapshot.GetGeneration(
            EVulkanBindingFrequency.Material,
            materialGeneration: 4).ShouldBe(4UL);
        snapshot.GetGeneration(
            EVulkanBindingFrequency.Object,
            materialGeneration: 4).ShouldBe(5UL);
        snapshot.GetGeneration(
            EVulkanBindingFrequency.Instance,
            materialGeneration: 4).ShouldBe(6UL);
        snapshot.GetGeneration(
            EVulkanBindingFrequency.RuntimeCallback,
            materialGeneration: 4).ShouldBe(7UL);
    }

    [Test]
    public void PreparedFrameDataPayloadHandle_ValidatesFrozenRangeAndOwnerGenerations()
    {
        VulkanAutoUniformPublicationSnapshot publication = new()
        {
            FrameGeneration = 11,
            ViewGeneration = 12,
            PassGeneration = 13,
            ObjectGeneration = 15,
            InstanceGeneration = 16,
            RuntimeCallbackGeneration = 17,
        };
        VulkanPreparedFrameDataPayloadHandle handle = new(
            new Silk.NET.Vulkan.Buffer(23),
            Offset: 256,
            Range: 160,
            DescriptorSet: VulkanMeshRenderingConventions.DescriptorSetMaterial,
            DescriptorBinding: 4,
            FrameIndex: 2,
            DrawUniformSlot: 7,
            ArenaGeneration: 19,
            EVulkanBindingFrequencyMask.View |
                EVulkanBindingFrequencyMask.Material |
                EVulkanBindingFrequencyMask.Object,
            publication,
            MaterialGeneration: 14);

        handle.IsValidFor(2, 7, 19).ShouldBeTrue();
        handle.IsValidFor(1, 7, 19).ShouldBeFalse();
        handle.ReferencesFrequency(EVulkanBindingFrequency.View).ShouldBeTrue();
        handle.ReferencesFrequency(EVulkanBindingFrequency.Frame).ShouldBeFalse();
        handle.GetContentGeneration(EVulkanBindingFrequency.View).ShouldBe(12UL);
        handle.GetContentGeneration(EVulkanBindingFrequency.Material).ShouldBe(14UL);
        handle.GetContentGeneration(EVulkanBindingFrequency.Object).ShouldBe(15UL);
    }

    [Test]
    public void PrimaryCommandPlan_CompilesStableOrderedTypedNodes()
    {
        FrameOpContext context = new(3, 4, null, null, null);
        ClearOp clear = new(
            PassIndex: 5,
            Target: null,
            ClearColor: true,
            ClearDepth: false,
            ClearStencil: false,
            Color: default,
            Depth: 1f,
            Stencil: 0,
            Rect: default,
            context);
        MemoryBarrierOp barrier = new(
            PassIndex: 6,
            EMemoryBarrierMask.All,
            context);
        FrameOp[] operations = [clear, barrier];
        VulkanPrimaryCommandPlan plan = new();
        VulkanPrimaryPlanTerminalContext terminalContext = new(
            PreserveSwapchainForOverlay: false,
            TransitionSwapchainToPresent: true,
            ReleaseExternalImageOwnership: true);

        plan.Build(LowerOperations(operations), terminalContext: terminalContext);
        ulong firstIdentity = plan.Identity;

        plan.OperationCount.ShouldBe(2);
        plan.Count.ShouldBe(5);
        plan.GetNode(0).Kind.ShouldBe(EVulkanPrimaryPlanNodeKind.Clear);
        plan.GetNode(0).SourceIndex.ShouldBe(0);
        plan.GetNode(0).Actions.ShouldBe(
            EVulkanPrimaryPlanAction.BarrierBatch |
            EVulkanPrimaryPlanAction.BeginRendering |
            EVulkanPrimaryPlanAction.RecordOperation);
        plan.GetNode(1).Kind.ShouldBe(
            EVulkanPrimaryPlanNodeKind.MemoryBarrier);
        plan.GetNode(1).SourceIndex.ShouldBe(1);
        plan.GetNode(1).Actions.ShouldBe(
            EVulkanPrimaryPlanAction.BarrierBatch |
            EVulkanPrimaryPlanAction.ExecuteSecondaryRange |
            EVulkanPrimaryPlanAction.RecordOperation |
            EVulkanPrimaryPlanAction.EndRendering);
        plan.GetNode(2).Kind.ShouldBe(
            EVulkanPrimaryPlanNodeKind.EndRendering);
        plan.GetNode(2).OperationIndex.ShouldBe(-1);
        plan.GetNode(2).Actions.ShouldBe(
            EVulkanPrimaryPlanAction.EndRendering);
        plan.GetNode(3).Kind.ShouldBe(
            EVulkanPrimaryPlanNodeKind.PreparePresent);
        plan.GetNode(3).Actions.ShouldBe(
            EVulkanPrimaryPlanAction.PreparePresent);
        plan.GetNode(4).Kind.ShouldBe(
            EVulkanPrimaryPlanNodeKind.ReleaseExternalImageOwnership);
        plan.GetNode(4).Actions.ShouldBe(
            EVulkanPrimaryPlanAction.ReleaseExternalImageOwnership);
        plan.HasTerminalAction(
            EVulkanPrimaryPlanAction.PreparePresent).ShouldBeTrue();
        plan.HasTerminalAction(
            EVulkanPrimaryPlanAction.ReleaseExternalImageOwnership).ShouldBeTrue();
        plan.IsFrozen.ShouldBeTrue();

        plan.Build(LowerOperations(operations), terminalContext: terminalContext);
        plan.Identity.ShouldBe(firstIdentity);
        plan.Build(LowerOperations([barrier, clear]), terminalContext: terminalContext);
        plan.Identity.ShouldNotBe(firstIdentity);
        plan.IsFrozen.ShouldBeTrue();
    }

    [Test]
    public void PrimaryRecorder_ConsumesTypedOrchestrationActions()
    {
        string source = SourceContractWorkspace.ReadVulkanSourcesContaining(
            "EVulkanPrimaryPlanAction.BarrierBatch");

        source.ShouldContain("EVulkanPrimaryPlanAction.BarrierBatch");
        source.ShouldContain("EVulkanPrimaryPlanAction.BeginRendering");
        source.ShouldContain("EVulkanPrimaryPlanAction.ExecuteSecondaryRange");
        source.ShouldContain("EVulkanPrimaryPlanAction.RecordOperation");
        source.ShouldContain("EVulkanPrimaryPlanAction.EndRendering");
        source.ShouldContain("EVulkanPrimaryPlanAction.PreparePresent");
        source.ShouldContain(
            "EVulkanPrimaryPlanAction.ReleaseExternalImageOwnership");
        source.ShouldContain(
            "EVulkanPrimaryPlanAction.QueueOwnershipTransfer");
        source.ShouldContain(
            "plannedQueueOwnershipTransfer !=");
        SourceContractWorkspace.ReadVulkanSourcesContaining("NormalizePrimaryPlanPassIndicesForPublication(")
            .ShouldContain("NormalizePrimaryPlanPassIndicesForPublication(");
        source.ShouldContain("EVulkanPrimaryPlanAction.RecordOperation");
        SourceContractWorkspace.ReadVulkanSourcesContaining(
            "inherited sentinel cannot make barrier or queue-ownership actions")
            .ShouldContain("inherited sentinel cannot make barrier or queue-ownership actions");
    }

    [Test]
    public void PrimaryRecording_DefersAndInvalidatesStaleSecondaryArtifacts()
    {
        string recording = ReadWorkspaceFile(
            "XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/Commands/CommandBuffers/Lifecycle/VulkanRenderer.CommandBufferLifecycle.Recording.cs");
        string lowering = ReadWorkspaceFile(
            "XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/Commands/Scheduling/CommandChains/Signatures/VulkanRenderer.CommandChains.Signatures.cs");

        recording.ShouldContain(
            "InvalidatePrimaryCommandBufferGroupSharedDependencyMismatches(");
        recording.ShouldContain(
            "stale secondary artifact(s)");
        recording.ShouldContain("variant.Dirty = true;");
        SourceContractWorkspace.ReadVulkanSourcesContaining("state.CommandChainPrimaryDirty = true;")
            .ShouldContain("state.CommandChainPrimaryDirty = true;");
        recording.ShouldNotContain(
            "throw new InvalidOperationException(\n                        $\"Recorded primary command buffer contains a secondary artifact");
        lowering.ShouldContain(
            "EVulkanRecordedCommandArtifactInvalidationReason.DependencyChanged");
        lowering.ShouldContain(
            "chain.DirtyReason |=");
    }

    [Test]
    public void PrimaryPlan_MapsTheSealedLoweredStreamAndChangesIdentityForChangedEmission()
    {
        FrameOpContext context = new(3, 4, null, null, null);
        FrameOp[] operations =
        [
            new ClearOp(
                PassIndex: 5,
                Target: null,
                ClearColor: true,
                ClearDepth: false,
                ClearStencil: false,
                Color: default,
                Depth: 1f,
                Stencil: 0,
                Rect: default,
                context),
            new MemoryBarrierOp(
                PassIndex: 6,
                EMemoryBarrierMask.All,
                context),
        ];
        VulkanPrimaryCommandPlan plan = new();
        plan.Build(LowerOperations(operations), operationSignature: 0xA55AUL);
        plan.Identity.ShouldNotBe(0UL);
        plan.IsFrozen.ShouldBeTrue();
        plan.OperationCount.ShouldBe(operations.Length);
        plan.Count.ShouldBe(operations.Length + 1);
        plan.GetNode(0).Kind.ShouldBe(EVulkanPrimaryPlanNodeKind.Clear);
        plan.GetNode(0).OperationIndex.ShouldBe(0);
        plan.GetNode(1).Kind.ShouldBe(EVulkanPrimaryPlanNodeKind.MemoryBarrier);
        plan.GetNode(1).OperationIndex.ShouldBe(1);
        plan.GetNode(2).Kind.ShouldBe(EVulkanPrimaryPlanNodeKind.EndRendering);
        plan.GetNode(2).OperationIndex.ShouldBe(-1);

        ulong firstIdentity = plan.Identity;
        FrameOp[] changedOperations =
        [
            operations[0],
            new MemoryBarrierOp(PassIndex: 7, EMemoryBarrierMask.All, context),
        ];
        plan.Build(LowerOperations(changedOperations), operationSignature: 0xA55AUL);
        plan.Identity.ShouldNotBe(firstIdentity);
    }

    [Test]
    public void RecordedCommandArtifact_OwnsStableLifecycleAndResourceIdentity()
    {
        VulkanRecordedCommandArtifact artifact = new(
            CommandBufferLevel.Secondary,
            frameSlot: 2);
        artifact.AssignNativeBuffer(
            new CommandBuffer(0x101),
            new CommandPool(0x202),
            ownsPool: false);

        artifact.Level.ShouldBe(CommandBufferLevel.Secondary);
        artifact.FrameSlot.ShouldBe(2);
        artifact.ArenaOwnerIdentity.ShouldBe(0x202UL);
        artifact.State.ShouldBe(EVulkanRecordedCommandArtifactState.Allocated);

        artifact.BeginRecording(recordingGeneration: 7);
        artifact.StoreInheritance(
            new VulkanRecordedCommandInheritance(
                DynamicRendering: true,
                default,
                default,
                default,
                DepthStencilReadOnly: false,
                SampleCountFlags.Count1Bit));
        List<KeyValuePair<VulkanResourceLifetimeKey, ulong>> dependencies =
        [
            new(
                new VulkanResourceLifetimeKey(
                    ObjectType.Buffer,
                    0x303),
                11),
            new(
                new VulkanResourceLifetimeKey(
                    ObjectType.ImageView,
                    0x404),
                13),
        ];
        artifact.PublishExecutable(
            default,
            dependencies,
            recordingGeneration: 7,
            queuedSubmissionCount: 0,
            recordedPrimaryReferenceCount: 0);

        artifact.IsExecutable.ShouldBeTrue();
        artifact.HasInheritance.ShouldBeTrue();
        artifact.ReferencedResourceCount.ShouldBe(2);
        artifact.ReferencedResourceIdentity.ShouldNotBe(0UL);
        ulong executableGeneration = artifact.Generation;

        artifact.Invalidate(
            EVulkanRecordedCommandArtifactInvalidationReason.DependencyChanged);
        artifact.State.ShouldBe(EVulkanRecordedCommandArtifactState.Invalid);
        artifact.IsExecutable.ShouldBeFalse();
        artifact.ReferencedResourceCount.ShouldBe(0);
        artifact.Generation.ShouldBeGreaterThan(executableGeneration);

        VulkanRecordedCommandArtifactRetirement retirement =
            artifact.CaptureRetirement();
        artifact.State.ShouldBe(
            EVulkanRecordedCommandArtifactState.PendingRetirement);
        artifact.IsPending.ShouldBeTrue();
        retirement.NativeBuffer.Handle.ShouldBe((nint)0x101);
        retirement.OwnerPool.Handle.ShouldBe(0x202UL);
        retirement.Level.ShouldBe(CommandBufferLevel.Secondary);
        retirement.FrameSlot.ShouldBe(2);
        retirement.RecordingGeneration.ShouldBe(7UL);
        retirement.ReferencedResourceIdentity.ShouldBe(0UL);
        artifact.MarkRetired();
        artifact.State.ShouldBe(EVulkanRecordedCommandArtifactState.Retired);
        artifact.NativeBuffer.Handle.ShouldBe(0);
    }

    [Test]
    public void RecordedCommandArtifact_ValidatesSharedStructuralDependencyAgreement()
    {
        VulkanRecordedCommandArtifact artifact = new(
            CommandBufferLevel.Secondary,
            frameSlot: 1);
        artifact.AssignNativeBuffer(
            new CommandBuffer(0x701),
            new CommandPool(0x702),
            ownsPool: false);
        artifact.BeginRecording(recordingGeneration: 3);
        artifact.StoreInheritance(default);
        CommandRecordingDependencySignature dependency = default;
        artifact.PublishExecutable(
            dependency,
            Array.Empty<KeyValuePair<VulkanResourceLifetimeKey, ulong>>(),
            recordingGeneration: 3,
            queuedSubmissionCount: 0,
            recordedPrimaryReferenceCount: 0);

        artifact.TryValidateCommandChainSecondaryDependency(
            dependency with { DataPublicationGeneration = 1 },
            out CommandRecordingDependencyMismatch dataMismatch).ShouldBeTrue();
        dataMismatch.InvalidationClass.ShouldBe(
            CommandRecordingInvalidationClass.DataOnly);
        artifact.TryValidateCommandChainSecondaryDependency(
            dependency with { PipelineGeneration = 1 },
            out CommandRecordingDependencyMismatch structuralMismatch).ShouldBeFalse();
        structuralMismatch.Field.ShouldBe(
            CommandRecordingDependencyField.PipelineGeneration);
    }

    [Test]
    public void RecordedCommandArtifact_RetirementSnapshotSurvivesSlotReuseWithoutAllocation()
    {
        VulkanRecordedCommandArtifact artifact = new(
            CommandBufferLevel.Secondary,
            frameSlot: 1);
        List<KeyValuePair<VulkanResourceLifetimeKey, ulong>> dependencies =
        [
            new(
                new VulkanResourceLifetimeKey(
                    ObjectType.Image,
                    0x303),
                5),
        ];

        static VulkanRecordedCommandArtifactRetirement CycleArtifact(
            VulkanRecordedCommandArtifact artifact,
            IReadOnlyList<KeyValuePair<VulkanResourceLifetimeKey, ulong>> dependencies,
            ulong generation)
        {
            artifact.AssignNativeBuffer(
                new CommandBuffer((nint)(0x100 + generation)),
                new CommandPool(0x202),
                ownsPool: false);
            artifact.BeginRecording(generation);
            artifact.PublishExecutable(
                default,
                dependencies,
                generation,
                queuedSubmissionCount: 0,
                recordedPrimaryReferenceCount: 0);
            return artifact.CaptureRetirement();
        }

        VulkanRecordedCommandArtifactRetirement first =
            CycleArtifact(artifact, dependencies, generation: 1);
        _ = CycleArtifact(artifact, dependencies, generation: 2);
        first.NativeBuffer.Handle.ShouldBe((nint)0x101);
        first.OwnerPool.Handle.ShouldBe(0x202UL);
        first.ReferencedResourceIdentity.ShouldNotBe(0UL);

        _ = CycleArtifact(artifact, dependencies, generation: 3);
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (ulong generation = 4; generation < 1_028; generation++)
            _ = CycleArtifact(artifact, dependencies, generation);
        long allocatedBytes = GC.GetAllocatedBytesForCurrentThread() - before;

        allocatedBytes.ShouldBe(0);
    }

    [Test]
    public void WorkerSecondaryArena_OwnsPoolsAndReusableArtifactSlots()
    {
        VulkanWorkerSecondaryCommandArena arena = new(workerIndex: 3);
        arena.Initialize(
        [
            new CommandPool(0x101),
            new CommandPool(0x202),
        ]);
        VulkanRecordedCommandArtifact artifact = new(
            CommandBufferLevel.Secondary,
            frameSlot: 1);

        artifact.AssignNativeBuffer(
            new CommandBuffer(0x303),
            arena.GetPool(frameSlot: 1),
            ownsPool: false,
            arena);

        arena.WorkerIndex.ShouldBe(3);
        arena.FrameSlotCount.ShouldBe(2);
        artifact.ArenaOwnerIdentity.ShouldBe(arena.Identity);
        artifact.WorkerArenaOwner.ShouldBeSameAs(arena);
        arena.GetArtifactCount(frameSlot: 1).ShouldBe(1);

        artifact.MarkRetired();
        arena.GetArtifactCount(frameSlot: 1).ShouldBe(0);
        Should.NotThrow(arena.ClearAfterPoolRetirement);
        arena.FrameSlotCount.ShouldBe(0);
    }

    [Test]
    public void WorkerSecondaryArena_AllowsPoolResetOnlyWithoutReusableOrPinnedArtifacts()
    {
        VulkanWorkerSecondaryCommandArena arena = new(workerIndex: 2);
        arena.Initialize([new CommandPool(0x101)]);
        VulkanRecordedCommandArtifact artifact = new(
            CommandBufferLevel.Secondary,
            frameSlot: 0);
        artifact.AssignNativeBuffer(
            new CommandBuffer(0x202),
            arena.GetPool(frameSlot: 0),
            ownsPool: false,
            arena);

        arena.CanResetPoolWithoutDiscardingReusableArtifacts(
            frameSlot: 0,
            out int reusableArtifactCount,
            out int pendingArtifactCount).ShouldBeTrue();
        reusableArtifactCount.ShouldBe(0);
        pendingArtifactCount.ShouldBe(0);

        artifact.PublishExecutable(
            default,
            Array.Empty<KeyValuePair<VulkanResourceLifetimeKey, ulong>>(),
            recordingGeneration: 1,
            queuedSubmissionCount: 0,
            recordedPrimaryReferenceCount: 1);
        arena.CanResetPoolWithoutDiscardingReusableArtifacts(
            frameSlot: 0,
            out reusableArtifactCount,
            out pendingArtifactCount).ShouldBeFalse();
        reusableArtifactCount.ShouldBe(1);
        pendingArtifactCount.ShouldBe(1);

        artifact.Invalidate(
            EVulkanRecordedCommandArtifactInvalidationReason.DependencyChanged);
        arena.CanResetPoolWithoutDiscardingReusableArtifacts(
            frameSlot: 0,
            out reusableArtifactCount,
            out pendingArtifactCount).ShouldBeFalse();
        reusableArtifactCount.ShouldBe(0);
        pendingArtifactCount.ShouldBe(1);

        artifact.MarkRetired();
        arena.CanResetPoolWithoutDiscardingReusableArtifacts(
            frameSlot: 0,
            out reusableArtifactCount,
            out pendingArtifactCount).ShouldBeTrue();
        reusableArtifactCount.ShouldBe(0);
        pendingArtifactCount.ShouldBe(0);
    }

    [Test]
    public void WorkerSecondaryArena_RejectsConcurrentOrDestructivePoolAccess()
    {
        VulkanWorkerSecondaryCommandArena arena = new(workerIndex: 1);
        arena.Initialize([new CommandPool(0x101)]);

        using (VulkanWorkerSecondaryCommandArena.RecordingLease lease =
               VulkanWorkerSecondaryCommandArena.EnterRecording(arena))
        {
            arena.IsRecording.ShouldBeTrue();
            Should.Throw<InvalidOperationException>(
                () => VulkanWorkerSecondaryCommandArena.EnterRecording(arena));
            Should.Throw<InvalidOperationException>(
                arena.ClearAfterPoolRetirement);
        }

        arena.IsRecording.ShouldBeFalse();
        Should.NotThrow(arena.ClearAfterPoolRetirement);
    }

    [Test]
    public void AutoUniformFrequencyPlan_CoalescesOnlyItsOwnedByteRanges()
    {
        AutoUniformBlockInfo block = new(
            "AutoData",
            "autoData",
            Set: VulkanDescriptorManager.GlobalsSetIndex,
            Binding: 0,
            Size: 160,
            Members:
            [
                CreateAutoUniformMember(
                    nameof(EEngineUniform.ModelMatrix),
                    EShaderVarType._mat4,
                    offset: 0,
                    size: 64),
                CreateAutoUniformMember(
                    "TransformId",
                    EShaderVarType._uint,
                    offset: 64,
                    size: 4),
                CreateAutoUniformMember(
                    "CurrViewProjection",
                    EShaderVarType._mat4,
                    offset: 80,
                    size: 64),
                CreateAutoUniformMember(
                    nameof(EEngineUniform.BillboardMode),
                    EShaderVarType._int,
                    offset: 68,
                    size: 4),
            ],
            ShaderType: EShaderType.Vertex);
        VulkanAutoUniformBindingSchema schema =
            VulkanAutoUniformBindingSchema.Compile(
                block,
                programLinkGeneration: 12);
        AutoUniformMaterialWritePlan plan = new(
            schema,
            materialLayoutVersion: 1,
            materialValueVersion: 2,
            runtimeUniformNameSignature: 3,
            runtimeUniformPublicationLayoutSignature: 0,
            new byte[160],
            schema.Operations);

        VulkanAutoUniformFrequencyPlan objectPlan =
            plan.GetFrequencyPlan(EVulkanBindingFrequency.Object);
        objectPlan.Operations.Length.ShouldBe(3);
        objectPlan.DirtyRanges.ShouldBe(
        [
            new VulkanAutoUniformDirtyRange(0, 72),
        ]);

        VulkanAutoUniformFrequencyPlan viewPlan =
            plan.GetFrequencyPlan(EVulkanBindingFrequency.View);
        viewPlan.Operations.ShouldHaveSingleItem();
        viewPlan.DirtyRanges.ShouldBe(
        [
            new VulkanAutoUniformDirtyRange(80, 64),
        ]);

        plan.GetFrequencyPlan(EVulkanBindingFrequency.Material)
            .DirtyRanges.ShouldBeEmpty();
    }

    [Test]
    public void AutoUniformDirtyRangeQueue_CoalescesAndFallsBackToFullPayload()
    {
        VulkanAutoUniformDirtyRangeQueue queue = default;
        queue.Publish(
        [
            new VulkanAutoUniformDirtyRange(16, 8),
            new VulkanAutoUniformDirtyRange(0, 8),
            new VulkanAutoUniformDirtyRange(8, 8),
            new VulkanAutoUniformDirtyRange(48, 8),
        ],
        payloadSize: 64);

        queue.Count.ShouldBe(2);
        queue.GetRange(0).ShouldBe(
            new VulkanAutoUniformDirtyRange(0, 24));
        queue.GetRange(1).ShouldBe(
            new VulkanAutoUniformDirtyRange(48, 8));

        VulkanAutoUniformDirtyRange[] fragmented =
            new VulkanAutoUniformDirtyRange[
                VulkanAutoUniformDirtyRangeQueue.Capacity + 1];
        for (int i = 0; i < fragmented.Length; i++)
        {
            fragmented[i] = new VulkanAutoUniformDirtyRange(
                checked((uint)(i * 2)),
                1);
        }

        queue.Publish(fragmented, payloadSize: 64);
        queue.Count.ShouldBe(1);
        queue.GetRange(0).ShouldBe(
            new VulkanAutoUniformDirtyRange(0, 64));
    }

    [Test]
    public void AutoUniformParityValidator_AttributesMismatchToSchemaDomain()
    {
        AutoUniformBlockInfo block = new(
            "AutoData",
            "autoData",
            Set: VulkanDescriptorManager.GlobalsSetIndex,
            Binding: 0,
            Size: 128,
            Members:
            [
                CreateAutoUniformMember(
                    nameof(EEngineUniform.ModelMatrix),
                    EShaderVarType._mat4,
                    offset: 0,
                    size: 64),
                CreateAutoUniformMember(
                    "CurrViewProjection",
                    EShaderVarType._mat4,
                    offset: 64,
                    size: 64),
            ],
            ShaderType: EShaderType.Vertex);
        VulkanAutoUniformBindingSchema schema =
            VulkanAutoUniformBindingSchema.Compile(
                block,
                programLinkGeneration: 12);
        byte[] legacy = new byte[128];
        byte[] packed = new byte[128];

        VulkanAutoUniformParityValidator.TryFindMismatch(
            legacy,
            packed,
            schema,
            out _).ShouldBeFalse();
        legacy[72] = 19;
        VulkanAutoUniformParityValidator.TryFindMismatch(
            legacy,
            packed,
            schema,
            out VulkanAutoUniformParityMismatch mismatch).ShouldBeTrue();
        mismatch.ByteOffset.ShouldBe(72);
        mismatch.LegacyValue.ShouldBe((byte)19);
        mismatch.PackedValue.ShouldBe((byte)0);
        mismatch.Frequency.ShouldBe(EVulkanBindingFrequency.View);
        mismatch.SchemaEntry.ShouldBe("CurrViewProjection");
    }

    [Test]
    public void AutoUniformPublicationState_QueuesOnlyChangedOwnerRanges()
    {
        VulkanAutoUniformPublicationState state = default;
        VulkanAutoUniformDirtyRange[] ranges =
        [
            new VulkanAutoUniformDirtyRange(0, 16),
            new VulkanAutoUniformDirtyRange(32, 16),
        ];

        state.TryBeginFrequencyPublication(
            EVulkanBindingFrequency.View,
            generation: 12,
            ranges,
            payloadSize: 64).ShouldBeTrue();
        state.PendingDirtyRangeCount.ShouldBe(2);
        state.CompleteFrequencyPublication(
            EVulkanBindingFrequency.View,
            generation: 12);
        state.PendingDirtyRangeCount.ShouldBe(0);

        state.TryBeginFrequencyPublication(
            EVulkanBindingFrequency.View,
            generation: 12,
            ranges,
            payloadSize: 64).ShouldBeFalse();
        state.PendingDirtyRangeCount.ShouldBe(0);
        state.TryBeginFrequencyPublication(
            EVulkanBindingFrequency.View,
            generation: 13,
            ranges,
            payloadSize: 64).ShouldBeTrue();
    }

    [Test]
    public void AutoUniformPublication_IsAllocationFreeAfterWarmup()
    {
        Span<VulkanAutoUniformDirtyRange> ranges =
            stackalloc VulkanAutoUniformDirtyRange[3];
        ranges[0] = new VulkanAutoUniformDirtyRange(0, 16);
        ranges[1] = new VulkanAutoUniformDirtyRange(32, 16);
        ranges[2] = new VulkanAutoUniformDirtyRange(64, 16);
        VulkanAutoUniformPublicationState state = default;

        state.TryBeginFrequencyPublication(
            EVulkanBindingFrequency.Object,
            generation: 1,
            ranges,
            payloadSize: 96).ShouldBeTrue();
        state.CompleteFrequencyPublication(
            EVulkanBindingFrequency.Object,
            generation: 1);

        int publishedRanges = 0;
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (ulong generation = 2; generation < 10_002; generation++)
        {
            if (!state.TryBeginFrequencyPublication(
                    EVulkanBindingFrequency.Object,
                    generation,
                    ranges,
                    payloadSize: 96))
            {
                continue;
            }

            publishedRanges += state.PendingDirtyRangeCount;
            state.CompleteFrequencyPublication(
                EVulkanBindingFrequency.Object,
                generation);
        }
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        publishedRanges.ShouldBe(30_000);
        allocated.ShouldBe(0);
    }

    [Test]
    public void ComputeDispatchSnapshot_PublishesStableRuntimeUniformContentGeneration()
    {
        ComputeDispatchSnapshot first = new(
            new Dictionary<string, ProgramUniformValue>(StringComparer.Ordinal)
            {
                ["RuntimeValue"] = new ProgramUniformValue(
                    EShaderVarType._float,
                    3.5f),
            },
            [],
            [],
            new Dictionary<string, XRTexture>(StringComparer.Ordinal),
            [],
            new Dictionary<uint, VulkanComputeBufferBinding>(),
            new Dictionary<string, VulkanComputeBufferBinding>(
                StringComparer.Ordinal));
        ComputeDispatchSnapshot equal = new(
            new Dictionary<string, ProgramUniformValue>(StringComparer.Ordinal)
            {
                ["RuntimeValue"] = new ProgramUniformValue(
                    EShaderVarType._float,
                    3.5f),
            },
            [],
            [],
            new Dictionary<string, XRTexture>(StringComparer.Ordinal),
            [],
            new Dictionary<uint, VulkanComputeBufferBinding>(),
            new Dictionary<string, VulkanComputeBufferBinding>(
                StringComparer.Ordinal));
        ComputeDispatchSnapshot changed = new(
            new Dictionary<string, ProgramUniformValue>(StringComparer.Ordinal)
            {
                ["RuntimeValue"] = new ProgramUniformValue(
                    EShaderVarType._float,
                    7.0f),
            },
            [],
            [],
            new Dictionary<string, XRTexture>(StringComparer.Ordinal),
            [],
            new Dictionary<uint, VulkanComputeBufferBinding>(),
            new Dictionary<string, VulkanComputeBufferBinding>(
                StringComparer.Ordinal));

        PublishBindingLayoutSignaturesForTest(first);
        PublishBindingLayoutSignaturesForTest(equal);
        PublishBindingLayoutSignaturesForTest(changed);

        first.RuntimeUniformNameSignature.ShouldBe(
            equal.RuntimeUniformNameSignature);
        first.RuntimeUniformValueSignature.ShouldBe(
            equal.RuntimeUniformValueSignature);
        changed.RuntimeUniformNameSignature.ShouldBe(
            first.RuntimeUniformNameSignature);
        changed.RuntimeUniformValueSignature.ShouldNotBe(
            first.RuntimeUniformValueSignature);
    }

    [Test]
    public void ComputeDispatchSnapshot_PublishesMutableLegacyUniformProvenance()
    {
        ComputeDispatchSnapshot snapshot = new();
        Dictionary<string, ProgramUniformValue> uniforms =
            new(StringComparer.Ordinal)
            {
                ["CallbackValue"] = new ProgramUniformValue(
                    EShaderVarType._float,
                    3.5f),
                ["VertexCallback_VTX"] = new ProgramUniformValue(
                    EShaderVarType._float,
                    2.0f),
            };
        Dictionary<string, VulkanRuntimeUniformPublication> publications =
            new(StringComparer.Ordinal);
        HashSet<string> mutableLegacyUniformNames =
            new(StringComparer.Ordinal)
            {
                "CallbackValue",
                "VertexCallback_VTX",
            };
        HashSet<string> requiredSamplerNames = new(StringComparer.Ordinal);
        Dictionary<uint, XRTexture> samplers = [];
        Dictionary<uint, string> samplerNamesByUnit = [];
        Dictionary<string, XRTexture> samplersByName =
            new(StringComparer.Ordinal);
        Dictionary<uint, ProgramImageBinding> images = [];

        snapshot.ExchangeCapturedBindings(
            ref uniforms,
            ref publications,
            ref mutableLegacyUniformNames,
            ref requiredSamplerNames,
            ref samplers,
            ref samplerNamesByUnit,
            ref samplersByName,
            ref images);
        PublishBindingLayoutSignaturesForTest(snapshot);

        snapshot.IsMutableLegacyUniform("CallbackValue").ShouldBeTrue();
        snapshot.IsMutableLegacyUniform("VertexCallback").ShouldBeTrue();
        snapshot.IsMutableLegacyUniform("MaterialValue").ShouldBeFalse();
        snapshot.MutableLegacyUniformNameSignature.ShouldNotBe(0UL);
        snapshot.MutableLegacyUniformValueSignature.ShouldNotBe(0UL);
        mutableLegacyUniformNames.ShouldBeEmpty();
        VkMeshRenderer.ResolveRuntimeOverrideFrequency(
                EVulkanBindingFrequency.Material,
                snapshot,
                "CallbackValue")
            .ShouldBe(EVulkanBindingFrequency.Material);
        VkMeshRenderer.ResolveRuntimeOverrideFrequency(
                EVulkanBindingFrequency.Unknown,
                snapshot,
                "CallbackValue")
            .ShouldBe(EVulkanBindingFrequency.RuntimeCallback);

        ulong nameSignature = snapshot.MutableLegacyUniformNameSignature;
        ulong valueSignature = snapshot.MutableLegacyUniformValueSignature;
        snapshot.Uniforms["MaterialValue"] = new ProgramUniformValue(
            EShaderVarType._float,
            7.0f);
        PublishBindingLayoutSignaturesForTest(snapshot);
        snapshot.MutableLegacyUniformNameSignature.ShouldBe(nameSignature);
        snapshot.MutableLegacyUniformValueSignature.ShouldBe(valueSignature);

        snapshot.Uniforms["CallbackValue"] = new ProgramUniformValue(
            EShaderVarType._float,
            8.0f);
        PublishBindingLayoutSignaturesForTest(snapshot);
        snapshot.MutableLegacyUniformNameSignature.ShouldBe(nameSignature);
        snapshot.MutableLegacyUniformValueSignature.ShouldNotBe(valueSignature);
    }

    [Test]
    public void MaterialBindingSnapshotEligibility_KeepsShadowSubstitutionConservative()
    {
        ComputeDispatchSnapshot snapshot = new();

        VkMeshRenderer.IsMaterialBindingSnapshotEligible(
                materialOwned: true,
                shadowPass: false,
                snapshot: null)
            .ShouldBeTrue();
        VkMeshRenderer.IsMaterialBindingSnapshotEligible(
                materialOwned: true,
                shadowPass: false,
                snapshot: snapshot)
            .ShouldBeFalse();

        snapshot.EnableMaterialBindingFastPath();

        VkMeshRenderer.IsMaterialBindingSnapshotEligible(
                materialOwned: true,
                shadowPass: false,
                snapshot: snapshot)
            .ShouldBeTrue();
        VkMeshRenderer.IsMaterialBindingSnapshotEligible(
                materialOwned: true,
                shadowPass: true,
                snapshot: snapshot)
            .ShouldBeFalse();
        VkMeshRenderer.IsMaterialBindingSnapshotEligible(
                materialOwned: false,
                shadowPass: true,
                snapshot: snapshot)
            .ShouldBeTrue();
    }

    [Test]
    public void ProgramBindingSchema_ClassifiesLiveTimeAsFrameOwned()
    {
        AutoUniformBlockInfo block = new(
            "FrameData",
            "frameData",
            Set: VulkanDescriptorManager.GlobalsSetIndex,
            Binding: 0,
            Size: 4,
            Members:
            [
                CreateAutoUniformMember(
                    nameof(EEngineUniform.RenderTime),
                    EShaderVarType._float,
                    offset: 0,
                    size: 4),
            ],
            ShaderType: EShaderType.Vertex);

        VulkanProgramBindingSchema schema =
            VulkanProgramBindingSchema.Compile(
                programLinkGeneration: 3,
                new Dictionary<string, AutoUniformBlockInfo>(
                    StringComparer.Ordinal)
                {
                    [block.InstanceName] = block,
                },
                []);

        schema.TryGetAutoUniformBlock(
            block.InstanceName,
            out VulkanAutoUniformBindingSchema valueSchema).ShouldBeTrue();
        valueSchema.Operations.ShouldHaveSingleItem().Frequency
            .ShouldBe(EVulkanBindingFrequency.Frame);
    }

    [Test]
    public void ProgramBindingSchema_CompilesStructSnapshotsAsMaterialOwned()
    {
        AutoUniformMember field = CreateAutoUniformMember(
            "Color",
            EShaderVarType._vec4,
            offset: 0,
            size: 16);
        AutoUniformMember value = new(
            Name: "Vignette",
            GlslType: "VignetteStruct",
            EngineType: null,
            IsArray: false,
            ArrayLength: 0,
            ArrayStride: 0,
            Offset: 0,
            Size: 16,
            DefaultValue: null,
            DefaultArrayValues: null,
            StructMembers: [field]);
        AutoUniformBlockInfo block = new(
            "MaterialData",
            "materialData",
            Set: VulkanMeshRenderingConventions.DescriptorSetMaterial,
            Binding: 0,
            Size: 16,
            Members: [value],
            ShaderType: EShaderType.Fragment,
            Frequency: EVulkanBindingFrequency.Material);

        VulkanAutoUniformBindingSchema schema =
            VulkanAutoUniformBindingSchema.Compile(
                block,
                programLinkGeneration: 4);

        schema.IsFastPathEligible.ShouldBeTrue();
        VulkanAutoUniformBindingOperation operation =
            schema.Operations.ShouldHaveSingleItem();
        operation.SourceKind.ShouldBe(
            EVulkanAutoUniformSourceKind.StructSnapshot);
        operation.Frequency.ShouldBe(EVulkanBindingFrequency.Material);
        operation.FallbackKind.ShouldBe(
            EVulkanAutoUniformFallbackReason.None);
        VulkanAutoUniformBindingSchema.HasExplicitDefault(value)
            .ShouldBeFalse();
    }

    [Test]
    public void MaterialPublicationGeneration_IncludesMutableCallbackValues()
    {
        ulong first = VkMeshRenderer.ComputeMaterialPublicationGeneration(
            materialLayoutVersion: 2,
            materialValueVersion: 3,
            runtimeUniformNameSignature: 5,
            mutableLegacyUniformValueSignature: 7);
        ulong equal = VkMeshRenderer.ComputeMaterialPublicationGeneration(
            materialLayoutVersion: 2,
            materialValueVersion: 3,
            runtimeUniformNameSignature: 5,
            mutableLegacyUniformValueSignature: 7);
        ulong changed = VkMeshRenderer.ComputeMaterialPublicationGeneration(
            materialLayoutVersion: 2,
            materialValueVersion: 3,
            runtimeUniformNameSignature: 5,
            mutableLegacyUniformValueSignature: 11);

        equal.ShouldBe(first);
        changed.ShouldNotBe(first);
    }

    [Test]
    public void MaterialPayloadCacheKey_TracksOnlyTheOwningMaterialRevision()
    {
        var roughness = new ShaderFloat(0.25f, "Roughness");
        var material = new XRMaterial([roughness]);

        MaterialUniformBindingCacheKey first = new(material);
        MaterialUniformBindingCacheKey equal = new(material);
        roughness.Value = 0.75f;
        MaterialUniformBindingCacheKey changed = new(material);

        equal.ShouldBe(first);
        changed.ShouldNotBe(first);
    }

    [Test]
    public void MaterialWritePlanCacheKey_RetainsRuntimeLayoutVariants()
    {
        var material = new XRMaterial();

        AutoUniformMaterialWritePlanCacheKey first = new(
            publicationLayoutSignature: 2,
            material,
            runtimeUniformNameSignature: 3,
            runtimeUniformPublicationLayoutSignature: 5);
        AutoUniformMaterialWritePlanCacheKey equal = new(
            publicationLayoutSignature: 2,
            material,
            runtimeUniformNameSignature: 3,
            runtimeUniformPublicationLayoutSignature: 5);
        AutoUniformMaterialWritePlanCacheKey differentNames = new(
            publicationLayoutSignature: 2,
            material,
            runtimeUniformNameSignature: 7,
            runtimeUniformPublicationLayoutSignature: 5);
        AutoUniformMaterialWritePlanCacheKey differentLayout = new(
            publicationLayoutSignature: 2,
            material,
            runtimeUniformNameSignature: 3,
            runtimeUniformPublicationLayoutSignature: 11);
        AutoUniformMaterialWritePlanCacheKey differentBlock = new(
            publicationLayoutSignature: 13,
            material,
            runtimeUniformNameSignature: 3,
            runtimeUniformPublicationLayoutSignature: 5);

        equal.ShouldBe(first);
        differentNames.ShouldNotBe(first);
        differentLayout.ShouldNotBe(first);
        differentBlock.ShouldNotBe(first);
    }

    [Test]
    public void CanonicalVulkanGate_RejectsAutoUniformLegacyFallback()
    {
        string measurement = ReadWorkspaceFile(
            "Tools/Measure-GameLoopRenderPipeline.ps1");
        string canonicalRunner = ReadWorkspaceFile(
            "Tools/Benchmarks/Invoke-VulkanPerf.ps1");

        measurement.ShouldContain(
            "[switch]$FailOnSteadyStateBindingFallback");
        measurement.ShouldContain(
            "'vulkan_auto_uniform_legacy_fallback_draws'");
        measurement.ShouldContain(
            "VulkanAutoUniformFallbackReasonTotals");
        measurement.ShouldContain(
            "throw \"Steady-state Vulkan binding fallback detected:");
        canonicalRunner.ShouldContain("if ($Preset -eq 'Gate')");
        canonicalRunner.ShouldContain(
            "$measureArguments.FailOnSteadyStateBindingFallback = $true");
    }

    [Test]
    public void ProgramBindingSchema_RejectsUnclassifiableValueWithActionableReason()
    {
        AutoUniformMember unsupported = new(
            Name: "UnsupportedValue",
            GlslType: "dmat3",
            EngineType: null,
            IsArray: false,
            ArrayLength: 0,
            ArrayStride: 0,
            Offset: 0,
            Size: 96,
            DefaultValue: null,
            DefaultArrayValues: null);
        AutoUniformBlockInfo block = new(
            "UnsupportedData",
            "unsupportedData",
            Set: VulkanDescriptorManager.GlobalsSetIndex,
            Binding: 0,
            Size: 96,
            Members: [unsupported],
            ShaderType: EShaderType.Vertex);

        VulkanAutoUniformBindingSchema schema =
            VulkanAutoUniformBindingSchema.Compile(block, programLinkGeneration: 9);

        schema.IsFastPathEligible.ShouldBeFalse();
        schema.FallbackKind.ShouldBe(
            EVulkanAutoUniformFallbackReason.UnsupportedShaderType);
        string fallbackReason = schema.FallbackReason.ShouldNotBeNull();
        fallbackReason.ShouldContain("UnsupportedValue");
        fallbackReason.ShouldContain("dmat3");
        schema.Operations[0].SourceKind.ShouldBe(EVulkanAutoUniformSourceKind.Unsupported);
        schema.Operations[0].Conversion.ShouldBe(EVulkanUniformWriteConversion.Unsupported);
        schema.Operations[0].FallbackKind.ShouldBe(
            EVulkanAutoUniformFallbackReason.UnsupportedShaderType);
    }

    [Test]
    public void ProgramBindingSchema_RejectsOverflowingDestinationRangeSafely()
    {
        AutoUniformMember overflowing = new(
            Name: "OverflowingValue",
            GlslType: "vec4",
            EngineType: EShaderVarType._vec4,
            IsArray: false,
            ArrayLength: 0,
            ArrayStride: 0,
            Offset: uint.MaxValue - 7,
            Size: 16,
            DefaultValue: null,
            DefaultArrayValues: null);
        AutoUniformBlockInfo block = new(
            "MaterialData",
            "materialData",
            Set: VulkanMeshRenderingConventions.DescriptorSetMaterial,
            Binding: 0,
            Size: 64,
            Members: [overflowing],
            ShaderType: EShaderType.Fragment);

        VulkanAutoUniformBindingSchema schema =
            VulkanAutoUniformBindingSchema.Compile(block, programLinkGeneration: 10);

        schema.IsFastPathEligible.ShouldBeFalse();
        schema.FallbackKind.ShouldBe(
            EVulkanAutoUniformFallbackReason.InvalidDestinationRange);
        schema.FallbackReason.ShouldNotBeNull().ShouldContain("4294967304");
    }

    [Test]
    public void ProgramBindingSchema_CompilationIsDeterministic()
    {
        AutoUniformBlockInfo block = new(
            "MaterialData",
            "materialData",
            Set: VulkanMeshRenderingConventions.DescriptorSetMaterial,
            Binding: 0,
            Size: 16,
            Members:
            [
                CreateAutoUniformMember(
                    "BaseColor",
                    EShaderVarType._vec4,
                    offset: 0,
                    size: 16),
            ],
            ShaderType: EShaderType.Fragment);

        VulkanAutoUniformBindingSchema first =
            VulkanAutoUniformBindingSchema.Compile(block, programLinkGeneration: 3);
        VulkanAutoUniformBindingSchema second =
            VulkanAutoUniformBindingSchema.Compile(block, programLinkGeneration: 3);

        second.ProgramLinkGeneration.ShouldBe(first.ProgramLinkGeneration);
        second.FallbackReason.ShouldBe(first.FallbackReason);
        second.Operations.ShouldBe(first.Operations);
    }

    [Test]
    public void StableMeshPackets_StartAtTenDrawsAndRemainBounded()
    {
        VulkanCommandRuntime.MinMeshDrawsPerRenderPacket.ShouldBe(10);
        VulkanCommandRuntime.MaxMeshDrawsPerRenderPacket.ShouldBeGreaterThanOrEqualTo(
            VulkanCommandRuntime.MinMeshDrawsPerRenderPacket);
    }

    private static AutoUniformMember CreateAutoUniformMember(
        string name,
        EShaderVarType type,
        uint offset,
        uint size)
        => new(
            name,
            type.ToString(),
            type,
            IsArray: false,
            ArrayLength: 0,
            ArrayStride: 0,
            offset,
            size,
            DefaultValue: null,
            DefaultArrayValues: null);

    [Test]
    public void CommandChainContainers_RebuildWithoutSteadyStateAllocations()
    {
        const int drawCount = VulkanCommandRuntime.MaxMeshDrawsPerRenderPacket;
        const string targetName = "SteadyTarget";
        RenderViewKey viewKey = new(1, 2, 0, RenderViewKind.Main, 0, -1);
        DrawPacket[] draws = new DrawPacket[drawCount];
        for (int i = 0; i < draws.Length; i++)
        {
            draws[i] = new DrawPacket(
                i,
                RendererIdentity: 3,
                MeshIdentity: i + 4,
                MaterialIdentity: 5,
                ProgramIdentity: 6,
                InstanceCount: 1,
                Transparent: false,
                StructuralSignature: (ulong)(i + 7),
                FrameDataSignature: (ulong)(i + 8));
        }

        CommandChainKey[] chainKeys = new CommandChainKey[drawCount];
        for (int i = 0; i < chainKeys.Length; i++)
            chainKeys[i] = new CommandChainKey(0, viewKey, 9, 10, 0, false, i);

        RenderPacket packet = new();
        RenderPacketPayloadArena payloadArena = new();
        RenderPassChainGroup group = new();
        CommandChainSchedule schedule = new();
        RenderPassChainGroup[] groups = [group];
        DescriptorBindingSnapshot descriptors = new(11, 3, 12);
        ResourcePlanSnapshot resources = new(13, 14, 15, 16);

        ResetContainers();
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int iteration = 0; iteration < 1_000; iteration++)
            ResetContainers();
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        allocated.ShouldBe(0);
        packet.DrawCount.ShouldBe(drawCount);
        packet.Seal();
        packet.GetDraw(drawCount - 1).OpIndex.ShouldBe(drawCount - 1);
        group.ChainKeys.Length.ShouldBe(drawCount);
        schedule.Groups.Length.ShouldBe(1);

        void ResetContainers()
        {
            payloadArena.ResetForPublication();
            packet.Reset(
                payloadArena,
                viewKey,
                9,
                10,
                targetName,
                RenderPacketVolatility.FrameDataOnly,
                draws,
                ReadOnlySpan<DispatchPacket>.Empty,
                descriptors,
                resources,
                17,
                18,
                0,
                drawCount,
                false);
            group.Reset(9, 10, targetName, chainKeys, 17, supportsSecondaryCommandBuffers: true, dynamicOverlay: false);
            schedule.Reset(17, 13, groups);
        }
    }

    [Test]
    public void BindingSnapshot_ResetReusesDictionaryStorageWithoutAllocating()
    {
        ComputeDispatchSnapshot snapshot = new();
        Dictionary<string, ProgramUniformValue> uniforms =
            new(StringComparer.Ordinal)
            {
                ["FrameValue"] = default,
            };
        Dictionary<uint, XRTexture> samplers = [];
        Dictionary<uint, string> samplerNames = [];
        Dictionary<string, XRTexture> samplersByName = new(StringComparer.Ordinal);
        Dictionary<uint, ProgramImageBinding> images = [];

        snapshot.Reset(uniforms, samplers, samplerNames, samplersByName, images);
        Dictionary<string, ProgramUniformValue> uniformStorage = snapshot.Uniforms;

        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int iteration = 0; iteration < 1_000; iteration++)
            snapshot.Reset(uniforms, samplers, samplerNames, samplersByName, images);
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        allocated.ShouldBe(0);
        snapshot.Uniforms.ShouldBeSameAs(uniformStorage);
        snapshot.Uniforms.ContainsKey("FrameValue").ShouldBeTrue();
    }

    [Test]
    public void BindingSnapshot_MaterialPayloadIsReleasedWhenFrameContentChanges()
    {
        ComputeDispatchSnapshot snapshot = new();
        MaterialUniformBindingPayload payload = new(
            new Dictionary<string, ProgramUniformValue>(StringComparer.Ordinal)
            {
                ["BaseColor"] = default,
            });
        snapshot.SetMaterialUniformBindings(payload);

        snapshot.MaterialUniformBindings.ShouldBeSameAs(payload);

        snapshot.Reset(
            new Dictionary<string, ProgramUniformValue>(StringComparer.Ordinal),
            [],
            [],
            new Dictionary<string, XRTexture>(StringComparer.Ordinal),
            []);

        snapshot.MaterialUniformBindings.ShouldBeNull();
    }

    [Test]
    public void BindingSnapshot_RuntimeUniformNameSignatureTracksTopologyNotValues()
    {
        ComputeDispatchSnapshot snapshot = new();
        Dictionary<string, ProgramUniformValue> first =
            new(StringComparer.Ordinal)
            {
                ["ScopedValue"] = default,
            };
        snapshot.Reset(first, [], [], new Dictionary<string, XRTexture>(StringComparer.Ordinal), []);
        PublishBindingLayoutSignaturesForTest(snapshot);
        ulong baseline = snapshot.RuntimeUniformNameSignature;

        first["ScopedValue"] = new ProgramUniformValue(EShaderVarType._float, 42.0f);
        snapshot.Reset(first, [], [], new Dictionary<string, XRTexture>(StringComparer.Ordinal), []);
        PublishBindingLayoutSignaturesForTest(snapshot);
        snapshot.RuntimeUniformNameSignature.ShouldBe(baseline);

        first["AnotherScopedValue"] = default;
        snapshot.Reset(first, [], [], new Dictionary<string, XRTexture>(StringComparer.Ordinal), []);
        PublishBindingLayoutSignaturesForTest(snapshot);
        snapshot.RuntimeUniformNameSignature.ShouldNotBe(baseline);
    }

    [Test]
    public void CapturedDescriptorAllocation_AlwaysKeepsItsResourceFingerprintVariant()
    {
        const ulong resourceFingerprint = 0x123456789ABCDEF0UL;

        VkMeshRenderer.ResolveDescriptorAllocationResourceVariantFingerprint(
            allActiveSetsUpdateAfterBind: true,
            hasCapturedBindingSnapshot: true,
            resourceFingerprint).ShouldBe(resourceFingerprint);
        VkMeshRenderer.ResolveDescriptorAllocationResourceVariantFingerprint(
            allActiveSetsUpdateAfterBind: false,
            hasCapturedBindingSnapshot: true,
            resourceFingerprint).ShouldBe(resourceFingerprint);
        VkMeshRenderer.ResolveDescriptorAllocationResourceVariantFingerprint(
            allActiveSetsUpdateAfterBind: false,
            hasCapturedBindingSnapshot: false,
            resourceFingerprint).ShouldBe(resourceFingerprint);
        VkMeshRenderer.ResolveDescriptorAllocationResourceVariantFingerprint(
            allActiveSetsUpdateAfterBind: true,
            hasCapturedBindingSnapshot: false,
            resourceFingerprint).ShouldBe(0UL);
    }

    [Test]
    public void PublishedDescriptorSnapshots_DriveExactSamplingTransitions()
    {
        string drawing = ReadWorkspaceFile(
            "XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/BackendObjects/MeshRendering/VkMeshRenderer.Drawing.cs");
        string barrierEmission = SourceContractWorkspace.ReadVulkanSourcesContaining(
            "internal bool TransitionPublishedDescriptorSetImagesForSampling(");

        drawing.ShouldContain("TryTransitionPreparedDescriptorImagesForSampling(");
        drawing.ShouldContain("TransitionPublishedDescriptorSetImagesForSampling(");
        barrierEmission.ShouldContain("_resourceLifetimeTracker.PublishedDescriptorSets.TryGetValue(");
        AssertOrdered(
            barrierEmission,
            "VulkanPublishedDescriptorImageReference published = snapshot.ImageReferences[i];",
            "TransitionDescriptorImageForSampling(commandBuffer, published.Reference.View, published.Reference.Layout, target);");
    }

    [Test]
    public void BindingSnapshot_NamedSamplerLookupUsesCapturedDictionary()
    {
        ComputeDispatchSnapshot snapshot = new();
        XRTexture2D texture = new();
        snapshot.SamplersByName["LightingTexture"] = texture;

        snapshot.TryGetSamplerTexture("LightingTexture", out XRTexture? resolved).ShouldBeTrue();
        resolved.ShouldBeSameAs(texture);

        snapshot.TryGetSamplerTexture("MissingTexture", out resolved).ShouldBeFalse();
        resolved.ShouldBeNull();
        snapshot.TryGetSamplerTexture(string.Empty, out resolved).ShouldBeFalse();
        resolved.ShouldBeNull();
    }

    [Test]
    public void FrameOpSignatureHasher_ReusesStableStringSignatureWithoutAllocating()
    {
        string value = string.Concat("Prepared", "Program", "Identity");
        FrameOpSignatureHasher warm = new();
        warm.Add(value);
        ulong expected = warm.ToHash();

        ulong actual = 0;
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int iteration = 0; iteration < 1_000; iteration++)
        {
            FrameOpSignatureHasher hash = new();
            hash.Add(value);
            actual = hash.ToHash();
        }
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        actual.ShouldBe(expected);
        allocated.ShouldBe(0);
        string equivalentValue = new(value.ToCharArray());
        FrameOpSignatureHasher equivalent = new();
        equivalent.Add(equivalentValue);
        equivalent.ToHash().ShouldBe(expected);
    }

    [Test]
    public void ProgramBindingCapture_IsAtomicAcrossUniformCallbacks()
    {
        string bindings = ReadWorkspaceFile(
            "XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/BackendObjects/Programs/VkRenderProgram.Bindings.cs");
        string capture = ReadWorkspaceFile(
            "XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/BackendObjects/Programs/VkRenderProgram.BindingCapture.cs");
        string meshRenderer = ReadWorkspaceFile(
            "XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/BackendObjects/MeshRendering/VkMeshRenderer.cs");
        bindings.ShouldContain("internal BindingUpdateScope BeginBindingUpdate()");
        bindings.ShouldContain("TryResolveBindingWriteState(out BindingCaptureState? capture)");
        bindings.ShouldContain("TryGetActiveBindingCaptureState(out BindingCaptureState capture)");
        bindings.ShouldContain("private void ClearBindingsNoLock()");
        bindings.ShouldContain("private ComputeDispatchSnapshot CaptureComputeSnapshotNoLock()");
        bindings.ShouldContain("private bool HasBoundDescriptorResourcesNoLock()");
        bindings.ShouldContain("private void SetSamplerNoLock(");
        capture.ShouldNotContain("[ThreadStatic]");
        capture.ShouldContain("ThreadLocal<BindingCaptureWorkspace>");
        capture.ShouldContain("ReferenceEquals(state.Owner, this)");
        capture.ShouldContain("private sealed class BindingCaptureState");
        capture.ShouldContain("internal ComputeDispatchSnapshot? RentFrameSnapshot()");
        capture.ShouldNotContain("Monitor.Enter");
        meshRenderer.ShouldContain("using VkRenderProgram.BindingUpdateScope bindingUpdate = program.BeginBindingUpdate();");
    }

    [Test]
    public void ReusableFrameDataRefresh_UsesPrivateBindingCaptureForSnapshotlessDraw()
    {
        string drawing = ReadWorkspaceFile(
            "XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/BackendObjects/MeshRendering/VkMeshRenderer.Drawing.cs");

        AssertOrdered(
            drawing,
            "draw.ProgramBindingSnapshot is null",
            "using VkRenderProgram.BindingUpdateScope bindingUpdate = activeProgram.BeginBindingUpdate();",
            "TryRefreshReusableCommandBufferFrameDataBindingsNoLock(",
            "NotifyDrawUniforms(material, programData, draw)",
            "UpdateAutoUniformBuffersForDraw(frameIndex, drawUniformSlot, material, draw)");
    }

    [Test]
    public void StableDeformationBuffers_BypassBufferStateLock()
    {
        string buffers = ReadWorkspaceFile(
            "XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/BackendObjects/MeshRendering/VkMeshRenderer.Buffers.cs");
        int methodStart = buffers.IndexOf("private void EnsureRuntimeDeformationBuffersCurrent()", StringComparison.Ordinal);
        int methodEnd = buffers.IndexOf("/// <summary>", methodStart, StringComparison.Ordinal);
        methodStart.ShouldBeGreaterThanOrEqualTo(0);
        methodEnd.ShouldBeGreaterThan(methodStart);
        string method = buffers[methodStart..methodEnd];
        method.ShouldContain("if (!RuntimeDeformationBufferReferencesChanged())");
        method.IndexOf("if (!RuntimeDeformationBufferReferencesChanged())", StringComparison.Ordinal)
            .ShouldBeLessThan(method.IndexOf("lock (_bufferStateSync)", StringComparison.Ordinal));
    }

    [Test]
    public void BindingSnapshots_AreFramePooledAndOutOfFrameCapturesKeepOwnership()
    {
        string bindings = ReadWorkspaceFile(
            "XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/BackendObjects/Programs/VkRenderProgram.Bindings.cs");
        string snapshot = ReadWorkspaceFile(
            "XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/BackendObjects/MeshRendering/Records/ComputeDispatchSnapshot.cs");
        string uniformArrayPool = ReadWorkspaceFile(
            "XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/BackendObjects/Programs/VkRenderProgram.FrameUniformArrayPool.cs");

        bindings.ShouldContain("RentFrameBindingSnapshot()");
        bindings.ShouldContain("RuntimeRenderingHostServices.FrameTiming.CurrentRenderPipelineContext");
        bindings.ShouldContain("RuntimeRenderingHostServices.FrameTiming.CurrentRenderFrameId");
        bindings.ShouldContain("if (frameId == 0)");
        bindings.ShouldContain("_frameBindingSnapshotPoolCursor = 0;");
        int captureStart = bindings.IndexOf(
            "private ComputeDispatchSnapshot CaptureComputeSnapshotNoLock()",
            StringComparison.Ordinal);
        int captureEnd = bindings.IndexOf(
            "internal bool ValidateComputeSnapshot(",
            captureStart,
            StringComparison.Ordinal);
        captureStart.ShouldBeGreaterThanOrEqualTo(0);
        captureEnd.ShouldBeGreaterThan(captureStart);
        string frameCapture = bindings[captureStart..captureEnd];
        frameCapture.ShouldNotContain("new Dictionary<string, ProgramUniformValue>(_uniformValues");
        frameCapture.ShouldNotContain("value.ToArray(), true");
        frameCapture.ShouldNotContain("value.Select(q =>");
        snapshot.ShouldContain("destination.EnsureCapacity(source.Count);");
        uniformArrayPool.ShouldContain("private sealed class FrameUniformArrayPool<T>");
        uniformArrayPool.ShouldContain("RuntimeRenderingHostServices.FrameTiming.CurrentRenderFrameId");
        uniformArrayPool.ShouldContain("values.CopyTo(snapshot);");
    }

    [Test]
    public void MeshDrawOp_ResetReusesTheLargeCapturedDrawStorageWithoutAllocating()
    {
        PendingMeshDraw firstDraw = default(PendingMeshDraw) with { Instances = 1u };
        PendingMeshDraw secondDraw = default(PendingMeshDraw) with { Instances = 2u };
        FrameOpContext context = default;
        MeshDrawOp op = new(1, null, firstDraw, context);
        ref readonly PendingMeshDraw drawRef = ref op.DrawRef;

        op.Reset(2, null, secondDraw, context, preserveSubmissionOrder: true);

        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int iteration = 0; iteration < 1_000; iteration++)
            op.Reset(3, null, secondDraw, context, preserveSubmissionOrder: false);
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        allocated.ShouldBe(0);
        op.PassIndex.ShouldBe(3);
        op.PreserveSubmissionOrder.ShouldBeFalse();
        drawRef.Instances.ShouldBe(2u);
    }

    [Test]
    public void DefaultPipelineFrameOps_ReuseSharedFrameBoundedStorage()
    {
        string frameOp = ReadWorkspaceFile(
            "XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/Commands/FrameOps/FrameOp.cs");
        string clearOp = ReadWorkspaceFile(
            "XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/BackendObjects/MeshRendering/Records/ClearOp.cs");
        string computeOp = ReadWorkspaceFile(
            "XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/BackendObjects/MeshRendering/ComputeDispatchOp.cs");
        string barrierOp = ReadWorkspaceFile(
            "XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/BackendObjects/MeshRendering/Records/MemoryBarrierOp.cs");
        string initialization = ReadWorkspaceFile(
            "XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/Bootstrap/VulkanRenderer.Initialization.cs");
        string frameOpApi = ReadWorkspaceFile(
            "XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/Commands/FrameOpApi.cs");
        string recording = ReadWorkspaceFile(
            "XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/Commands/CommandBuffers/Recording/Primary/VulkanRenderer.CommandBufferRecording.Primary.Secondaries.cs");
        string frameOperationQueue = SourceContractWorkspace.ReadVulkanSourcesContaining(
            "op.PassIndex = validatedPassIndex;");
        string swapchainContextCoalescer = ReadWorkspaceFile(
            "XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/RenderGraph/VulkanSwapchainContextCoalescer.cs");
        string openXrPrewarm = ReadWorkspaceFile(
            "XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/OpenXR/VulkanRenderer.OpenXR.PrewarmValidation.cs");
        string blitOp = ReadWorkspaceFile(
            "XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/BackendObjects/MeshRendering/Records/BlitOp.cs");
        string publishOp = ReadWorkspaceFile(
            "XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/BackendObjects/MeshRendering/Records/PublishFramebufferForSamplingOp.cs");

        frameOp.ShouldContain("protected static bool TryRentForCurrentFrame<T>");
        frameOp.ShouldContain("private static class FramePool<T>");
        frameOp.ShouldContain("internal abstract record FrameOp(int PassIndex");
        clearOp.ShouldContain("internal static ClearOp Rent(");
        computeOp.ShouldContain("internal static ComputeDispatchOp Rent(");
        barrierOp.ShouldContain("internal static MemoryBarrierOp Rent(");
        initialization.ShouldContain("EnqueueFrameOp(ComputeDispatchOp.Rent(");
        initialization.ShouldContain("EnqueueFrameOp(ClearOp.Rent(");
        frameOpApi.ShouldContain("EnqueueFrameOp(MemoryBarrierOp.Rent(");
        recording.ShouldNotContain("clear with { ClearColor = false }");
        frameOperationQueue.ShouldContain("op.PassIndex = validatedPassIndex;");
        frameOperationQueue.ShouldNotContain("with { PassIndex = validatedPassIndex }");
        swapchainContextCoalescer.ShouldContain("operation.Context = canonicalContext.Value;");
        openXrPrewarm.ShouldContain("capturedOp.Context = context;");
        openXrPrewarm.ShouldContain("op.Target = target;");
        openXrPrewarm.ShouldNotContain("capturedOp with { Context = context }");
        openXrPrewarm.ShouldNotContain("with { Target = target }");
        blitOp.ShouldContain("internal sealed record BlitOp(");
        publishOp.ShouldContain("PublishFramebufferForSamplingOp(");
    }

    [Test]
    public void DefaultPipelineDescriptorAndPlannerScopes_ReuseScratchStorage()
    {
        string material = ReadWorkspaceFile(
            "XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/BackendObjects/Materials/VkMaterial.cs");
        string planner = ReadWorkspaceFile(
            "XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/RenderGraph/VulkanRenderer.ResourcePlannerContext.cs");
        string linking = ReadWorkspaceFile(
            "XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/BackendObjects/Programs/VkRenderProgram.Linking.cs");
        string frameOutputs = ReadWorkspaceFile(
            "XREngine.Runtime.Rendering/Runtime/Statistics/RuntimeEngine.Rendering.Stats.FrameOutputs.cs");
        string resourceAllocator = ReadWorkspaceFile(
            "XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/Resources/VulkanResourceAllocator.cs");
        string frameOpSignatures = SourceContractWorkspace.ReadVulkanSourcesContaining(
            "HashUniformValue(ref item, pair.Value);");

        material.ShouldContain("private sealed class DescriptorUpdateScratch");
        material.ShouldContain("Span<WriteDescriptorSet> writeSpan = CollectionsMarshal.AsSpan(writes);");
        material.ShouldContain("ReturnDescriptorUpdateScratch(scratch);");
        material.ShouldNotContain("WriteDescriptorSet[] writeArray =");
        material.ShouldContain("static (renderProgram, samplerName) =>");
        planner.ShouldContain("PooledExternalResourcePlannerReadbackScope.Rent(this, context)");
        planner.ShouldContain("private sealed class PooledExternalResourcePlannerReadbackScope");
        frameOutputs.ShouldContain("private static readonly Stack<OutputAccumulator> OutputAccumulatorPool");
        frameOutputs.ShouldContain("OutputAccumulatorPool.Push(output);");
        frameOutputs.ShouldContain("? OutputAccumulatorPool.Pop()");
        frameOutputs.ShouldContain("output.Reset(");
        resourceAllocator.ShouldContain("Dictionary<VulkanAliasGroupKey, VulkanPhysicalImageGroup>.ValueCollection EnumeratePhysicalGroups()");
        resourceAllocator.ShouldNotContain("IEnumerable<VulkanPhysicalImageGroup> EnumeratePhysicalGroups()");
        frameOpSignatures.ShouldContain("HashUniformValue(ref item, pair.Value);");
        frameOpSignatures.ShouldNotContain("HashUniformValue(ref item, pair.Value.Value);");

        int linkedFastPath = linking.IndexOf("if (IsLinked &&", StringComparison.Ordinal);
        int stopwatchStart = linking.IndexOf(
            "global::System.Diagnostics.Stopwatch buildWatch = global::System.Diagnostics.Stopwatch.StartNew();",
            StringComparison.Ordinal);
        linkedFastPath.ShouldBeGreaterThanOrEqualTo(0);
        stopwatchStart.ShouldBeGreaterThan(linkedFastPath);
    }

    [Test]
    public void VulkanHotSettingsAndQueueOwnership_AvoidPerOperationWork()
    {
        OverrideableSetting<EVulkanGpuDrivenProfile> projectOverride =
            new(EVulkanGpuDrivenProfile.DevParity, hasOverride: true);

        OverrideableSettingExtensions.ResolveValueCascade(
            EVulkanGpuDrivenProfile.Auto,
            projectOverride,
            null).ShouldBe(EVulkanGpuDrivenProfile.DevParity);

        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int iteration = 0; iteration < 1_000; iteration++)
        {
            _ = OverrideableSettingExtensions.ResolveValueCascade(
                EVulkanGpuDrivenProfile.Auto,
                projectOverride,
                null);
        }
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        allocated.ShouldBe(0);

        string effectiveSettings = ReadWorkspaceFile(
            "XRENGINE/Engine/Subclasses/Engine.EffectiveSettings.cs");
        string queueOverlap = ReadWorkspaceFile(
            "XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/Commands/VulkanRenderer.QueueOverlap.cs");
        string diagnostics = ReadWorkspaceFile(
            "XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/Bootstrap/VulkanDiagnosticOptions.cs");
        string dataBuffer = ReadWorkspaceFile(
            "XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/BackendObjects/Buffers/VkDataBuffer.cs");
        string bufferOperations = SourceContractWorkspace.ReadVulkanSourcesContaining(
            "TryGetBufferMemoryAllocation(buffer, out allocation)");

        effectiveSettings.ShouldContain("OverrideableSettingExtensions.ResolveValueCascade(");
        queueOverlap.ShouldContain("_queueOwnershipConfigCacheFrameId != frameId");
        queueOverlap.ShouldContain("ReferenceEquals(entry.PassMetadata, passMetadata)");
        queueOverlap.ShouldContain("bool advanceAdaptivePolicy");
        diagnostics.ShouldContain("public bool EnableCrashBreadcrumbs => HasFlag(EVulkanDiagnosticFlags.CrashBreadcrumbs);");
        diagnostics.ShouldNotContain("public bool EnableCrashBreadcrumbs => Flags.HasFlag(");
        dataBuffer.ShouldContain("ResolveHostVisibleSubDataUploadRoute(_lastMemProps)");
        bufferOperations.ShouldContain("TryGetBufferMemoryAllocation(buffer, out allocation)");
    }

    [Test]
    public void FrameGraphQueueOwnership_StaysOnExecutableGraphicsSubmission()
    {
        string queueOverlap = ReadWorkspaceFile(
            "XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/Commands/VulkanRenderer.QueueOverlap.cs");
        string invalidation = ReadWorkspaceFile(
            "XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/Commands/CommandBuffers/VulkanRenderer.CommandBufferAllocation.cs");

        queueOverlap.ShouldContain("SupportsFrameGraphMultiQueueSubmission");
        queueOverlap.ShouldContain(
            "supportsFrameGraphMultiQueueSubmission &&");
        queueOverlap.ShouldContain(
            "_lastResolvedQueueOverlapMode = supportsFrameGraphMultiQueueSubmission");
        queueOverlap.ShouldContain(
            ": EVulkanQueueOverlapMode.GraphicsOnly;");
        queueOverlap.ShouldContain(
            "Queue-schedule metadata alone does not satisfy this");
        string backendSettings = ReadWorkspaceFile(
            "XREngine.Runtime.Rendering/Runtime/Settings/BackendRenderSettings.cs");
        string architecture = ReadWorkspaceFile(
            "docs/architecture/rendering/vulkan-renderer.md");
        backendSettings.ShouldContain(
            "scheduler emits metadata only and executable ownership remains on the graphics queue");
        architecture.ShouldContain(
            "Requested overlap modes never publish executable non-graphics queue-family ownership transitions");
        SourceContractWorkspace.ReadVulkanSourcesContaining(
            "new(_frameTelemetry, EVulkanCpuStage.CommandDirtyPropagation)")
            .ShouldContain("new(_frameTelemetry, EVulkanCpuStage.CommandDirtyPropagation)");
    }

    [Test]
    public void DefaultPipelineRemainingHotPaths_AvoidSuppressedLogArraysEnumBoxingAndDiagnosticStrings()
    {
        const string normalLogKey = nameof(DefaultPipelineRemainingHotPaths_AvoidSuppressedLogArraysEnumBoxingAndDiagnosticStrings) + ".Normal";
        const string warningLogKey = nameof(DefaultPipelineRemainingHotPaths_AvoidSuppressedLogArraysEnumBoxingAndDiagnosticStrings) + ".Warning";
        TimeSpan interval = TimeSpan.FromDays(1);
        _ = XREngine.Debug.ShouldLogEvery(normalLogKey, interval);
        _ = XREngine.Debug.ShouldLogEvery(warningLogKey, interval);
        XREngine.Debug.VulkanEvery(normalLogKey, interval, "Value={0}", 0);
        XREngine.Debug.VulkanWarningEvery(warningLogKey, interval, "Value={0}, active={1}", 0, true);

        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int iteration = 0; iteration < 1_000; iteration++)
        {
            XREngine.Debug.VulkanEvery(normalLogKey, interval, "Value={0}", iteration);
            XREngine.Debug.VulkanWarningEvery(
                warningLogKey,
                interval,
                "Value={0}, active={1}",
                iteration,
                true);
        }
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        allocated.ShouldBe(0);

        string debug = ReadWorkspaceFile("XREngine.Runtime.Core/Core/Diagnostics/Debug.cs");
        string dataBuffer = ReadWorkspaceFile(
            "XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/BackendObjects/Buffers/VkDataBuffer.cs");
        string forwardLighting = ReadWorkspaceFile(
            "XREngine.Runtime.Rendering/Rendering/Lights3DCollection.ForwardLighting.cs");
        string lights = ReadWorkspaceFile(
            "XREngine.Runtime.Rendering/Rendering/Lights3DCollection.cs");
        string scheduler = ReadWorkspaceFile(
            "XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/RenderGraph/VulkanFrameOperationScheduler.cs");
        string preparation = ReadWorkspaceFile(
            "XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/BackendObjects/MeshRendering/VkMeshRenderer.Preparation.cs");
        string bufferPolicy = ReadWorkspaceFile(
            "XREngine.Runtime.Rendering/Buffers/XRBufferMemoryPolicy.cs");
        string blit = ReadWorkspaceFile(
            "XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/Commands/VulkanRenderer.Blit.cs");
        string renderState = ReadWorkspaceFile(
            "XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/Commands/VulkanRenderer.RenderStateMutation.cs");
        string descriptorImageReference = ReadWorkspaceFile(
            "XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/Frame/VulkanRenderer.VulkanDescriptorImageReference.cs");
        string materialState = ReadWorkspaceFile(
            "XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/BackendObjects/Materials/VkMaterial.ProgramDescriptorState.cs");
        string material = ReadWorkspaceFile(
            "XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/BackendObjects/Materials/VkMaterial.cs");

        debug.ShouldContain("public static void VulkanEvery<T>(");
        debug.ShouldContain("public static void VulkanWarningEvery<T1, T2>(");
        dataBuffer.ShouldNotContain(".HasFlag(");
        forwardLighting.ShouldContain("LogForwardShadowNoTextureReason(");
        forwardLighting.ShouldNotContain("reason != _lastForwardShadowNoTexReason");
        lights.ShouldContain("_lastForwardShadowNoTextureReasonKey");
        scheduler.ShouldContain("private static readonly Comparison<FrameOpSortKey> FrameOpSortComparison");
        scheduler.ShouldContain("sortKeys.AsSpan(0, opCount).Sort(FrameOpSortComparison)");
        preparation.ShouldNotContain(".HasFlag(");
        bufferPolicy.ShouldNotContain(".HasFlag(");
        blit.ShouldNotContain(".HasFlag(");
        renderState.ShouldContain("private int _indexedViewportScissorCount;");
        renderState.ShouldContain("_indexedViewportScissorCount = 0;");
        descriptorImageReference.ShouldContain("IEquatable<VulkanDescriptorImageReference>");
        descriptorImageReference.ShouldContain("View.Handle == other.View.Handle");
        materialState.ShouldContain("public required DescriptorBindingInfo[] Bindings");
        material.ShouldContain("for (int bindingIndex = 0; bindingIndex < stateBindings.Length; bindingIndex++)");
    }

    [Test]
    public void DefaultPipelineCpuHotPaths_ReuseReflectionScopesFrustaAndCullingStorage()
    {
        string editorUi = ReadWorkspaceFile(
            "XREngine.Editor/IMGUI/EditorImGuiUI.ImGui.cs");
        string pipelineInstance = ReadWorkspaceFile(
            "XREngine.Runtime.Rendering/Rendering/Pipelines/XRRenderPipelineInstance.cs");
        string viewport = ReadWorkspaceFile(
            "XREngine.Runtime.Rendering/Rendering/XRViewport.cs");
        string renderingState = ReadWorkspaceFile(
            "XREngine.Runtime.Rendering/Rendering/Pipelines/RenderingState.cs");
        string runtimeEngine = ReadWorkspaceFile(
            "XREngine.Runtime.Rendering/Runtime/RuntimeEngine.cs");
        string engine = ReadWorkspaceFile(
            "XRENGINE/Engine/Engine.cs");
        string preparedFrustum = ReadWorkspaceFile(
            "XREngine.Data/Geometry/PreparedFrustum.cs");
        string camera = ReadWorkspaceFile(
            "XREngine.Runtime.Rendering/Rendering/Camera/XRCamera.cs");
        string shadowCollection = ReadWorkspaceFile(
            "XREngine.Runtime.Rendering/Rendering/Lights3DCollection.Shadows.cs");
        string aabb = ReadWorkspaceFile(
            "XREngine.Data/Geometry/AABB.cs");
        string box = ReadWorkspaceFile(
            "XREngine.Data/Geometry/Box.cs");
        string light = ReadWorkspaceFile(
            "XREngine.Runtime.Rendering/Scene/Components/Lights/Types/LightComponent.cs");
        string changedArgs = ReadWorkspaceFile(
            "XREngine.Data/Core/Objects/XRPropertyChangedEventArgs.cs");

        editorUi.ShouldContain("ImGui.SetWindowCollapsed(windowName, true, ImGuiCond.Always);");
        editorUi.ShouldNotContain("typeof(ImGui).GetMethods");
        pipelineInstance.ShouldContain("_screenSpaceUiCommandGeneration != commandGeneration");
        pipelineInstance.ShouldContain("for (int commandIndex = 0; commandIndex < container.Count; commandIndex++)");
        viewport.ShouldContain("!_renderPipeline.ContainsScreenSpaceUiRenderCommand()");
        renderingState.ShouldContain("StateObject.New(PopRenderAreaAction, this)");
        renderingState.ShouldNotContain("StateObject.New(PopRenderArea)");
        runtimeEngine.ShouldContain("StateObject.New(PopRenderGraphPassAction, stack)");
        runtimeEngine.ShouldNotContain("new DisposableAction(");
        engine.ShouldContain("private sealed class PooledExternalProfilerScope");
        preparedFrustum.ShouldContain("public void UpdateTransformed(in Frustum frustum, in Matrix4x4 worldMatrix)");
        camera.ShouldContain("public PreparedFrustum PreparedWorldFrustum()");
        shadowCollection.ShouldContain("frusta.Add(cameras[i].PreparedWorldFrustum());");
        aabb.ShouldContain("public readonly void GetCorners(Span<Vector3> corners)");
        box.ShouldContain("Span<Vector3> corners = stackalloc Vector3[8];");
        light.ShouldContain("publishNotifications: false");
        changedArgs.ShouldContain("object? IXRPropertyChangedEventArgs.PreviousValue => PreviousValue;");
    }

    [Test]
    public void IndirectDrawStateCapabilityScope_IsAValueTypeToAvoidPerBucketAllocation()
        => typeof(IndirectDrawStateCapabilityScope).IsValueType.ShouldBeTrue();

    [Test]
    public void IndirectDrawSecondaryRecordingScope_IsAValueTypeToAvoidPerDrawAllocation()
        => typeof(IndirectDrawSecondaryRecordingScope).IsValueType.ShouldBeTrue();

    [Test]
    public void GpuIndirectCommandChains_KeepMutableArgumentStreamsOnPrimary()
    {
        string recording = ReadWorkspaceFile(
            "XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/Commands/CommandBuffers/Recording/Primary/VulkanRenderer.CommandBufferRecording.Primary.Secondaries.cs");
        string capability = ReadWorkspaceFile(
            "XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/Commands/VulkanRenderer.IndirectSecondaryRecordingCapability.cs");
        string dispatch = ReadWorkspaceFile(
            "XREngine.Runtime.Rendering/Rendering/HybridRenderingManager.cs");

        recording.ShouldContain(
            "EvaluateIndirectSecondaryRecordingContract(firstDraw)");
        capability.ShouldContain(
            "EVulkanIndirectSecondaryEligibility.MutableCurrentFrame");
        SourceContractWorkspace.ReadVulkanSourcesContaining(
            "ComputeCommandBufferDataBufferSignature(")
            .ShouldContain("ComputeCommandBufferDataBufferSignature(");
        capability.ShouldContain(
            "IsIndirectSecondaryRangeValid(");
        dispatch.ShouldContain(
            "producerCompleteStableRange: true");
        dispatch.ShouldContain(
            "IIndirectDrawSecondaryRecordingBackendCapability capability");
        SourceContractWorkspace.ReadVulkanSourcesContaining("RecordIndirectDrawIntoCommandBuffer(")
            .ShouldContain("RecordIndirectDrawIntoCommandBuffer(");
        ReadWorkspaceFile("XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/Commands/FrameOps/IndirectDrawOp.cs")
            .ShouldContain("usedSecondary: false");
    }

    [Test]
    public void ProducerCompleteIndirectSecondaryEligibility_HasTypedTelemetryAndPrimaryFallback()
    {
        VulkanIndirectSecondaryRecordingContract eligible = new(
            EVulkanIndirectSecondaryEligibility.EligibleProducerComplete,
            1,
            2,
            1,
            16,
            0,
            0,
            false);
        VulkanIndirectSecondaryRecordingContract mutable = new(
            EVulkanIndirectSecondaryEligibility.MutableCurrentFrame,
            0,
            0,
            0,
            0,
            0,
            0,
            false);

        eligible.IsEligible.ShouldBeTrue();
        mutable.IsEligible.ShouldBeFalse();

        string recording = ReadWorkspaceFile(
            "XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/Commands/CommandBuffers/Recording/Primary/VulkanRenderer.CommandBufferRecording.Primary.Secondaries.cs");
        string profiler = ReadWorkspaceFile(
            "XREngine.Editor/Mcp/Actions/EditorMcpActions.Profiler.cs");

        recording.ShouldContain(
            "RecordVulkanIndirectSecondaryEligibility(");
        recording.ShouldContain(
            "CommandChainsDisabled);");
        recording.ShouldContain(
            "UnsupportedInheritance,");
        recording.ShouldContain(
            "ResourcePreparationFailed,");
        profiler.ShouldContain(
            "indirect_secondary_eligibility_counts");
        profiler.ShouldContain(
            "eligible_producer_complete");
        profiler.ShouldContain(
            "mutable_current_frame");
    }

    [Test]
    public void NonGraphicsSecondaryRecordingContract_IsAllocationFreeAndFamilySpecific()
    {
        VulkanSecondaryRecordingContract compute = new(
            EVulkanSecondaryCommandFamily.Compute,
            EVulkanSecondaryRecordingEligibility.Eligible);
        VulkanSecondaryRecordingContract transferFallback = new(
            EVulkanSecondaryCommandFamily.Transfer,
            EVulkanSecondaryRecordingEligibility.BarrierPlanUnavailable);
        VulkanQuerySecondaryInheritanceContract queryInheritance =
            VulkanQuerySecondaryInheritanceContract.Create(
                primaryQueryActive: false,
                inheritedQueriesEnabled: true);
        VulkanSecondaryRecordingContract query = new(
            EVulkanSecondaryCommandFamily.Query,
            EVulkanSecondaryRecordingEligibility.Eligible,
            queryInheritance);

        typeof(VulkanSecondaryRecordingContract).IsValueType.ShouldBeTrue();
        typeof(VulkanQuerySecondaryInheritanceContract).IsValueType
            .ShouldBeTrue();
        compute.IsEligible.ShouldBeTrue();
        transferFallback.IsEligible.ShouldBeFalse();
        transferFallback.Family.ShouldBe(EVulkanSecondaryCommandFamily.Transfer);
        query.IsEligible.ShouldBeTrue();
        query.QueryInheritance.InheritedQueriesEnabled.ShouldBeTrue();
        query.QueryInheritance.CanExecuteWithoutInheritedQueryState
            .ShouldBeTrue();
        query.QueryInheritance.OcclusionQueryEnable.ShouldBeFalse();
        query.QueryInheritance.QueryFlags.ShouldBe(QueryControlFlags.None);
        query.QueryInheritance.PipelineStatistics
            .ShouldBe(QueryPipelineStatisticFlags.None);
    }

    [Test]
    public void NonGraphicsSecondaryScheduler_AdmitsOnlyTypedSerialFamilies()
    {
        string scheduler = ReadWorkspaceFile(
            "XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/RenderGraph/VulkanFrameOperationScheduler.cs");
        string plan = ReadWorkspaceFile(
            "XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/Commands/Scheduling/Primary/VulkanPrimaryCommandPlan.cs");

        scheduler.ShouldContain("case ComputeDispatchOp:");
        scheduler.ShouldContain("case BufferCopyOp:");
        scheduler.ShouldContain("case QueryOp:");
        scheduler.ShouldContain("EVulkanSecondaryCommandFamily.Compute");
        scheduler.ShouldContain("EVulkanSecondaryCommandFamily.Transfer");
        scheduler.ShouldContain("EVulkanSecondaryCommandFamily.Query");
        scheduler.ShouldNotContain("=> op is BlitOp or IndirectDrawOp;");
        plan.ShouldContain(
            "EVulkanPrimaryPlanNodeKind.BufferCopy or\n            EVulkanPrimaryPlanNodeKind.MemoryBarrier or\n            EVulkanPrimaryPlanNodeKind.Query;");
        plan.ShouldContain("EVulkanPrimaryPlanNodeKind.Query");
    }

    [Test]
    public void NonGraphicsSecondaryEligibility_RequiresExactQueueBarrierScopeAndResources()
    {
        string eligibility = ReadWorkspaceFile(
            "XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/Commands/CommandBuffers/VulkanRenderer.NonGraphicsSecondaryRecording.cs");
        string queueSelection = ReadWorkspaceFile(
            "XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/Bootstrap/Device/VulkanQueueFamilySelector.cs");
        string state = ReadWorkspaceFile(
            "XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/Commands/CommandBuffers/VulkanRenderer.CommandBufferState.cs");

        state.ShouldContain("_enableComputeSecondaryCommandBuffers = true;");
        state.ShouldContain("_enableTransferSecondaryCommandBuffers = true;");
        state.ShouldContain("_enableQuerySecondaryCommandBuffers = true;");
        state.ShouldContain("VulkanComputeSecondaryCommandBuffers");
        state.ShouldContain("VulkanTransferSecondaryCommandBuffers");
        state.ShouldContain("VulkanQuerySecondaryCommandBuffers");
        eligibility.ShouldContain("EVulkanSecondaryRecordingEligibility.ActiveRenderScope");
        eligibility.ShouldContain("QueryInheritanceUnsupported");
        eligibility.ShouldContain("BarrierPlanner.HasKnownPass(resolvedPassIndex)");
        eligibility.ShouldContain("GraphicsFamilySupportsCompute");
        eligibility.ShouldContain("GraphicsFamilySupportsTransfer");
        eligibility.ShouldContain("sourceHandle.Handle != operation.SourceBuffer.Handle");
        eligibility.ShouldContain("destinationHandle.Handle != operation.DestinationBuffer.Handle");
        eligibility.ShouldContain("BufferUsageFlags.TransferSrcBit");
        eligibility.ShouldContain("BufferUsageFlags.TransferDstBit");
        eligibility.ShouldContain("IsBufferRangeValid(");
        queueSelection.ShouldContain("indices.GraphicsFamilySupportsTransfer =");
    }

    [Test]
    public void NonGraphicsSecondaryRecorder_HasTypedTelemetryAndPrimaryFallback()
    {
        string secondary = ReadWorkspaceFile(
            "XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/Commands/CommandBuffers/Recording/Secondary/VulkanRenderer.SecondaryCommandBuffers.cs");
        string primary = ReadWorkspaceFile(
            "XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/Commands/CommandBuffers/Recording/Primary/VulkanRenderer.CommandBufferRecording.Primary.Operations.cs");
        string profiler = ReadWorkspaceFile(
            "XREngine.Editor/Mcp/Actions/EditorMcpActions.Profiler.cs");

        secondary.ShouldContain("EvaluateSecondaryRecordingContract(");
        secondary.ShouldContain("RecordVulkanSecondaryRecordingEligibility(");
        secondary.ShouldContain("case ComputeDispatchOp computeDispatchOp:");
        secondary.ShouldContain("case BufferCopyOp bufferCopyOp:");
        secondary.ShouldContain("Operation: ERenderQueryOperation.CopyResults");
        SourceContractWorkspace.ReadVulkanSourcesContaining("TryRecordSecondaryBucket(")
            .ShouldContain("TryRecordSecondaryBucket(");
        secondary.ShouldContain("RecordComputeDispatchOp(secondaryCommandBuffer, imageIndex, computeDispatchOp, opIndex);");
        secondary.ShouldContain("RecordBufferCopyOp(secondaryCommandBuffer, bufferCopyOp);");
        profiler.ShouldContain("compute_secondary_eligibility_counts");
        profiler.ShouldContain("transfer_secondary_eligibility_counts");
        profiler.ShouldContain("query_secondary_eligibility_counts");
        profiler.ShouldContain("barrier_plan_unavailable");
        profiler.ShouldContain("query_pair_primary_owned");
        profiler.ShouldContain("query_result_ordering_unavailable");
        profiler.ShouldContain("invalid_operation_state");
    }

    [Test]
    public void QuerySecondaryEligibility_PreservesPairResetAndResultOrdering()
    {
        string eligibility = ReadWorkspaceFile(
            "XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/Commands/CommandBuffers/VulkanRenderer.NonGraphicsSecondaryRecording.cs");
        string query = ReadWorkspaceFile(
            "XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/BackendObjects/Queries/VkRenderQuery.cs");
        string inheritance = ReadWorkspaceFile(
            "XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/Commands/FrameOps/VulkanQuerySecondaryInheritanceContract.cs");

        eligibility.ShouldContain("QueryResetPrimaryOwned");
        eligibility.ShouldContain("QueryPairPrimaryOwned");
        eligibility.ShouldContain("QueryTimestampPrimaryOwned");
        eligibility.ShouldContain("QueryPropertiesPrimaryOwned");
        eligibility.ShouldContain("IsQueryResultCopyOrdered(");
        eligibility.ShouldContain("producerRecorded && !queryActive");
        query.ShouldContain("internal bool CanCopyResults(");
        inheritance.ShouldContain("bool PrimaryQueryActive");
        inheritance.ShouldContain("bool InheritedQueriesEnabled");
        inheritance.ShouldContain("bool OcclusionQueryEnable");
        inheritance.ShouldContain("QueryControlFlags QueryFlags");
        inheritance.ShouldContain(
            "QueryPipelineStatisticFlags PipelineStatistics");
    }

    [Test]
    public void MutableGpuDrivenPrimaries_ReuseStableInlineTopology()
    {
        string recording = ReadWorkspaceFile(
            "XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/Commands/Scheduling/CommandChains/Planning/VulkanRenderer.CommandChains.Planning.cs");
        string diagnostics = ReadWorkspaceFile(
            "XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/Commands/FrameOpDiagnostics.cs");
        string markers = ReadWorkspaceFile(
            "XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/Frame/VulkanRenderer.SubmissionMarkers.cs");
        string meshRenderer = ReadWorkspaceFile(
            "XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/BackendObjects/MeshRendering/VkMeshRenderer.cs");

        recording.ShouldContain("HasMutableGpuDrivenFrameOps(staticOps) ||");
        recording.ShouldContain("HasMutableGpuDrivenFrameOps(volatileOps);");
        AssertOrdered(
            recording,
            "bool requiresFreshPrimary =",
            "HasMutableGpuDrivenFrameOps(staticOps)",
            "HasMutableGpuDrivenFrameOps(volatileOps);");
        recording.ShouldNotContain("\"mutable-gpu-driven-frame-ops\"");
        SourceContractWorkspace.ReadVulkanSourcesContaining("HasMutableGpuDrivenFrameOps(")
            .ShouldContain("HasMutableGpuDrivenFrameOps(");
        markers.ShouldContain("RegisterSubmissionMarkersForCommandBuffer");
        meshRenderer.ShouldNotContain("hash.Add(marker.Fence.GetHashCode());");
    }

    [Test]
    public void VulkanPrimaryReuse_IsEnabledAfterPublicationGenerationsAreKeyed()
    {
        VulkanCommandRuntime.VulkanPrimaryCommandBufferReuseSafe.ShouldBeTrue();

        string state = ReadWorkspaceFile(
            "XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/Commands/CommandBuffers/VulkanRenderer.CommandBufferState.cs");
        state.ShouldContain("VulkanPrimaryCommandBufferReuseSafe &&");
        state.ShouldContain("immutable dependency");
        state.ShouldContain("RuntimeRenderingHostServices.Settings.EnableVulkanPrimaryCommandBufferReuse");
    }

    [Test]
    public void MutableGpuDrivenFrames_BypassCommandChainSecondaries()
    {
        string lowering = ReadWorkspaceFile(
            "XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/Commands/Scheduling/CommandChains/Planning/VulkanRenderer.CommandChains.Planning.cs");

        lowering.ShouldContain("HasMutableGpuDrivenFrameOps(staticOps) ||");
        lowering.ShouldContain("HasMutableGpuDrivenFrameOps(volatileOps);");
        lowering.ShouldContain("Mutable GPU publications remain inline in a freshly recorded primary.");
    }

    [Test]
    public void AsyncBackendCompile_IsExplicitAndOptIn()
    {
        XRRenderProgram program = new();
        program.AllowAsyncBackendCompile.ShouldBeFalse();

        program.AllowAsyncBackendCompile = true;

        program.AllowAsyncBackendCompile.ShouldBeTrue();
    }

    [TestCase(XRRenderProgram.EShaderProgramBackendStage.SourceQueued)]
    [TestCase(XRRenderProgram.EShaderProgramBackendStage.Compiling)]
    [TestCase(XRRenderProgram.EShaderProgramBackendStage.Linking)]
    [TestCase(XRRenderProgram.EShaderProgramBackendStage.QueueBackpressure)]
    public void IndirectProgramReadinessDeferral_IsNotAForbiddenFallback(
        XRRenderProgram.EShaderProgramBackendStage stage)
        => HybridRenderingManager.IsIndirectGraphicsProgramTerminalFailure(stage).ShouldBeFalse();

    [TestCase(XRRenderProgram.EShaderProgramBackendStage.BinaryUploadFailed)]
    [TestCase(XRRenderProgram.EShaderProgramBackendStage.Failed)]
    [TestCase(XRRenderProgram.EShaderProgramBackendStage.Abandoned)]
    public void IndirectProgramTerminalFailure_IsAForbiddenFallback(
        XRRenderProgram.EShaderProgramBackendStage stage)
        => HybridRenderingManager.IsIndirectGraphicsProgramTerminalFailure(stage).ShouldBeTrue();

    [Test]
    public void DescriptorChanges_HaveExplicitContentIdentityAndLayoutClasses()
    {
        RenderResourceChangeKind.FrameData.ShouldNotBe(RenderResourceChangeKind.CompatibleContentPublication);
        RenderResourceChangeKind.CompatibleContentPublication.ShouldNotBe(RenderResourceChangeKind.BindingIdentity);
        RenderResourceChangeKind.BindingIdentity.ShouldNotBe(RenderResourceChangeKind.StructuralLayout);
    }

    [Test]
    public void VulkanRecording_SharedPacketSecondariesRetainSimultaneousUseAndExactInheritance()
    {
        string source = ReadWorkspaceFile(
            "XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/Commands/CommandBuffers/Recording/Primary/VulkanRenderer.CommandBufferRecording.Primary.Secondaries.cs");
        string secondarySource = ReadWorkspaceFile(
            "XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/Commands/CommandBuffers/Recording/Secondary/VulkanRenderer.SecondaryCommandBuffers.cs");
        int workerStart = secondarySource.IndexOf("private void RecordScheduledMeshCommandChainWorker", StringComparison.Ordinal);
        int workerEnd = secondarySource.IndexOf("internal bool TryRecordSecondaryBucket", workerStart, StringComparison.Ordinal);
        string worker = secondarySource[workerStart..workerEnd];

        source.ShouldContain("scheduledOpCount += chain.SourceCount;");
        SourceContractWorkspace.ReadVulkanSourcesContaining("CmdExecuteCommandsTracked(recordingState.CommandBuffer, (uint)secondaryCount, secondaryPtr)")
            .ShouldContain("CmdExecuteCommandsTracked(recordingState.CommandBuffer, (uint)secondaryCount, secondaryPtr)");
        worker.ShouldContain("CommandBufferUsageFlags.RenderPassContinueBit | CommandBufferUsageFlags.SimultaneousUseBit");
        worker.ShouldContain("StoreCommandChainSecondaryInheritance(");
        source.ShouldContain("CommandChainSecondaryInheritanceMatches(");
        source.ShouldContain("ActiveMeshSecondaryInheritanceMatches(");
        worker.ShouldContain("EnterPreparedCommandChainEncodingScope()");
        worker.ShouldNotContain("EnterThreadResourcePlannerRuntimeStateScope");
        worker.ShouldNotContain("batch.HasPlannerState");
        worker.ShouldNotContain("_frameOpResourcePlannerReadbackLock");
            worker.IndexOf("EnterPreparedCommandChainEncodingScope()", StringComparison.Ordinal)
            .ShouldBeLessThan(worker.IndexOf("for (int drawIndex = 0; drawIndex < chain.SourceCount; drawIndex++)", StringComparison.Ordinal));
        worker.ShouldContain("chain.State = CommandChainState.Recorded;");
        worker.ShouldContain("A prewarmed Vulkan command-chain draw became unavailable during secondary recording.");
        worker.ShouldNotContain("bool pipelinesReady");
        source.ShouldContain("CommandBufferUsageFlags.RenderPassContinueBit | CommandBufferUsageFlags.OneTimeSubmitBit");
    }

    [Test]
    public void WorkerDispatch_RecordsOnlyCommandBuffersOwnedByThatWorkerPool()
    {
        string source = ReadWorkspaceFile(
            "XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/Commands/CommandBuffers/Recording/Secondary/Workers/VulkanRenderer.CommandChainWorkers.cs");
        string batch = ReadWorkspaceFile(
            "XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/Commands/CommandBuffers/Recording/Secondary/Workers/VulkanRenderer.CommandChainRecordingBatch.cs");

        batch.ShouldContain("public int[] RecordJobWorkerIndices = new int[16];");
        source.ShouldContain("if (batch.RecordJobWorkerIndices[jobIndex] != worker.WorkerIndex)");
        source.ShouldContain("RecordScheduledMeshCommandChainWorker(batch, chainIndex);");
    }

    [Test]
    public void WorkerPoolAssignment_UsesStablePreparedChainIdentity()
    {
        CommandChainKey firstKey = new(
            2,
            new RenderViewKey(3, 4, 5, RenderViewKind.Main, 6, 7),
            8,
            9,
            0,
            false,
            10);

        int first = VulkanCommandRuntime.ResolveCommandChainRecordingWorkerIndex(firstKey, workerCount: 6);
        int afterOtherJobsDisappear = VulkanCommandRuntime.ResolveCommandChainRecordingWorkerIndex(firstKey, workerCount: 6);

        first.ShouldBe(afterOtherJobsDisappear);
        first.ShouldBeInRange(0, 5);
        VulkanCommandRuntime.ResolveCommandChainRecordingWorkerIndex(firstKey, workerCount: 1).ShouldBe(0);

        HashSet<int> workers = [];
        for (int chainOrdinal = 0; chainOrdinal < 32; chainOrdinal++)
        {
            CommandChainKey independentKey = firstKey with
            {
                ChainOrdinal = chainOrdinal,
            };
            workers.Add(VulkanCommandRuntime.ResolveCommandChainRecordingWorkerIndex(
                independentKey,
                workerCount: 6));
        }

        workers.Count.ShouldBeGreaterThan(1);
    }

    [Test]
    public void CommandChainRendererFamily_MixedChainsRequireSerialRecording()
    {
        var firstRenderer = (VkMeshRenderer)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(
            typeof(VkMeshRenderer));
        var secondRenderer = (VkMeshRenderer)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(
            typeof(VkMeshRenderer));
        FrameOpContext firstContext = new(1, 2, null, null, null);
        FrameOpContext differentFamilyContext = firstContext with { ViewportIdentity = 3 };
        PendingMeshDraw firstDraw = default(PendingMeshDraw) with { Renderer = firstRenderer };
        PendingMeshDraw secondDraw = default(PendingMeshDraw) with { Renderer = secondRenderer };
        CommandChain chain = new(new CommandChainKey(
            0,
            new RenderViewKey(1, 2, 0, RenderViewKind.Main, 0, -1),
            0,
            0,
            0,
            false,
            1))
        {
            SourceStartIndex = 0,
            SourceCount = 2,
        };

        FrameOp[] homogeneousOps =
        [
            new MeshDrawOp(0, null, firstDraw, firstContext),
            new MeshDrawOp(0, null, firstDraw, firstContext),
        ];
        FrameOp[] mixedRendererOps =
        [
            homogeneousOps[0],
            new MeshDrawOp(0, null, secondDraw, firstContext),
        ];
        FrameOp[] mixedFamilyOps =
        [
            homogeneousOps[0],
            new MeshDrawOp(0, null, firstDraw, differentFamilyContext),
        ];

        VulkanCommandRuntime.TryResolveCommandChainRecordingRendererFamily(
                new FrameOperationSequence(LowerOperations(homogeneousOps)),
                chain,
                frameDataSlot: 0,
                EVulkanMeshFrameDataStreamKind.Primary,
                out VulkanMeshFrameDataRendererFamilyKey rendererFamily)
            .ShouldBeTrue();
        rendererFamily.Renderer.ShouldBeSameAs(firstRenderer);
        VulkanCommandRuntime.TryResolveCommandChainRecordingRendererFamily(
                new FrameOperationSequence(LowerOperations(mixedRendererOps)),
                chain,
                frameDataSlot: 0,
                EVulkanMeshFrameDataStreamKind.Primary,
                out _)
            .ShouldBeFalse();
        VulkanCommandRuntime.TryResolveCommandChainRecordingRendererFamily(
                new FrameOperationSequence(LowerOperations(mixedFamilyOps)),
                chain,
                frameDataSlot: 0,
                EVulkanMeshFrameDataStreamKind.Primary,
                out _)
            .ShouldBeFalse();
    }

    [Test]
    public void WorkerDispatch_UsesStablePoolCapacityWithoutRendererOwnershipPinning()
    {
        string workers = ReadWorkspaceFile(
            "XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/Commands/CommandBuffers/Recording/Secondary/Workers/VulkanRenderer.CommandChainWorkers.cs");
        string recording = ReadWorkspaceFile(
            "XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/Commands/CommandBuffers/Recording/Primary/VulkanRenderer.CommandBufferRecording.Primary.Secondaries.cs");

        workers.ShouldContain("workerCount = workers.Length;");
        workers.ShouldContain("AssignCommandChainRecordingWorker(");
        workers.ShouldContain("ResolveCommandChainRecordingWorkerIndex(chain.Key, workerCount)");
        workers.ShouldNotContain("TryGetRendererOwner");
        workers.ShouldNotContain("RendererOwnerWorkerIndices");
        workers.ShouldContain("CommandChainWorkerWaitTimeoutMilliseconds");
        workers.ShouldContain("batch.ActiveWorkerMask");
        workers.ShouldContain("_commandChainRecordingWorkerCountdown.Reset(activeWorkerCount);");
        recording.ShouldContain("AssignCommandChainRecordingWorker(");
        recording.ShouldContain("schedulingConflictCount++");
        recording.ShouldContain("recordJobWorkerIndices[jobIndex] < 0");
        recording.IndexOf("MarkCommandChainSecondaryCommandBufferInvalid(chain);", StringComparison.Ordinal)
            .ShouldBeLessThan(recording.IndexOf("DispatchCommandChainRecordingWorkers(batch, workers, workerCount)", StringComparison.Ordinal));
    }

    [Test]
    public void PreparedMeshEncoder_IsStaticAndDoesNotTraverseLiveDrawState()
    {
        string drawing = ReadWorkspaceFile(
            "XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/BackendObjects/MeshRendering/VkMeshRenderer.Drawing.cs");
        int encoderStart = drawing.IndexOf(
            "internal static bool RecordPreparedMeshDrawState(",
            StringComparison.Ordinal);
        int encoderEnd = drawing.IndexOf(
            "internal static void ReturnPreparedMeshDrawStateBuffers(",
            encoderStart,
            StringComparison.Ordinal);
        string encoder = drawing[encoderStart..encoderEnd];

        encoder.ShouldContain("VulkanRenderer renderer = recordingState.Renderer;");
        encoder.ShouldNotContain("recordingState.OwnerIdentity.Renderer");
        encoder.ShouldContain("in VulkanPreparedMeshDrawState recordingState");
        encoder.ShouldNotContain("XRMaterial");
        encoder.ShouldNotContain("PendingMeshDraw");
        encoder.ShouldNotContain("ComputeDispatchSnapshot");
        encoder.ShouldNotContain("TryPrepare");
        encoder.ShouldNotContain("UpdateAutoUniformBuffersForDraw");
    }

    [Test]
    public void PreparedCommandChain_FreezesWorkerInheritanceAndArtifactLease()
    {
        string workers = ReadWorkspaceFile(
            "XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/Commands/CommandBuffers/Recording/Secondary/Workers/VulkanRenderer.CommandChainWorkers.cs");
        string secondary = ReadWorkspaceFile(
            "XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/Commands/CommandBuffers/Recording/Secondary/VulkanRenderer.SecondaryCommandBuffers.cs");

        secondary.ShouldContain(
            "batch.PreparedFrame.GetCommandChain(chainIndex)");
        secondary.ShouldContain("preparedChain.Matches(chain, packet)");
        secondary.ShouldContain(
            "VulkanRecordedCommandInheritance inheritance =");
        secondary.ShouldContain("preparedChain.Inheritance");
        secondary.ShouldNotContain("batch.DynamicRendering");
        secondary.ShouldNotContain("batch.DynamicRenderingFormats");
        ReadWorkspaceFile(
            "XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/Commands/CommandBuffers/Recording/Secondary/Workers/VulkanRenderer.CommandChainRecordingBatch.cs").ShouldContain(
            "public readonly VulkanPreparedFrameRecording PreparedFrame = new();");
    }

    [Test]
    public void PreparedCommandChain_SerialAndParallelPathsShareOneOrderedEncoder()
    {
        string workers = ReadWorkspaceFile(
            "XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/Commands/CommandBuffers/Recording/Secondary/Workers/VulkanRenderer.CommandChainWorkers.cs");
        string recording = ReadWorkspaceFile(
            "XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/Commands/CommandBuffers/Recording/Primary/VulkanRenderer.CommandBufferRecording.Primary.Secondaries.cs");

        workers.ShouldContain(
            "RecordScheduledMeshCommandChainWorker(batch, chainIndex);");
        recording.ShouldContain(
            "RecordScheduledMeshCommandChainWorker(batch, recordJobChainIndices[jobIndex]);");
        recording.ShouldContain(
            "batch.PreparedFrame.AddCommandChain(");
        recording.ShouldContain(
            "if (preparedChainIndex != chainIndex)");
        SourceContractWorkspace.ReadVulkanSourcesContaining("CmdExecuteCommandsTracked(recordingState.CommandBuffer, (uint)secondaryCount, secondaryPtr)")
            .ShouldContain("CmdExecuteCommandsTracked(recordingState.CommandBuffer, (uint)secondaryCount, secondaryPtr)");
    }

    [Test]
    public void OpenXrEyeSubmissionContract_OrdersLeftThenRightBeforePublish()
    {
        string submission = ReadWorkspaceFile(
            "XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/OpenXR/VulkanRenderer.OpenXR.Submission.cs");
        string mirror = ReadWorkspaceFile(
            "XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/OpenXR/VulkanRenderer.OpenXR.MirrorPreview.cs");
        string target = ReadWorkspaceFile(
            "XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/OpenXR/VulkanRenderer.OpenXrEyeRenderTargetContext.cs");

        submission.IndexOf("commandBuffers[0] = firstCommandBuffer;", StringComparison.Ordinal)
            .ShouldBeLessThan(submission.IndexOf("commandBuffers[1] = secondCommandBuffer;", StringComparison.Ordinal));
        mirror.IndexOf("commandBuffers[0] = firstRecorded.CommandBuffer;", StringComparison.Ordinal)
            .ShouldBeLessThan(mirror.IndexOf("commandBuffers[1] = secondRecorded.CommandBuffer;", StringComparison.Ordinal));
        mirror.IndexOf("commandBuffers[1] = secondRecorded.CommandBuffer;", StringComparison.Ordinal)
            .ShouldBeLessThan(mirror.IndexOf("commandBuffers[2] = publishCommandBuffer;", StringComparison.Ordinal));
        target.ShouldContain("ImageLayout.Undefined");
        target.ShouldContain("ImageLayout.ColorAttachmentOptimal");
        target.ShouldContain("ImageAspectFlags DepthAspect");
    }

    [Test]
    public void CommandBufferReuse_GuardsNativeResetAndReplacesPendingSecondaries()
    {
        string lifetime = ReadWorkspaceFile(
            "XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/Frame/Resources/Lifetime/VulkanRenderer.ResourceLifetimeTracking.cs");
        string lowering = ReadWorkspaceFile(
            "XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/Commands/CommandBuffers/Recording/Secondary/VulkanRenderer.CommandChainSecondaryBuffers.cs");
        string recording = ReadWorkspaceFile(
            "XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/Commands/CommandBuffers/Recording/Primary/VulkanRenderer.CommandBufferRecording.Primary.Secondaries.cs");
        string secondaries = ReadWorkspaceFile(
            "XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/Commands/CommandBuffers/Recording/Secondary/VulkanRenderer.SecondaryCommandBuffers.cs");

        lifetime.ShouldContain("private bool CanResetVulkanCommandBuffer(");
        lifetime.IndexOf("CanResetVulkanCommandBuffer(commandBuffer, out string reason)", StringComparison.Ordinal)
            .ShouldBeLessThan(lifetime.IndexOf("Result result = Api!.ResetCommandBuffer(commandBuffer, 0);", StringComparison.Ordinal));
        lifetime.ShouldContain("commandRecord.Pins.HasRecordedReferences");
        lifetime.ShouldContain("commandRecord.Pins.RecordedReferenceCount");
        lowering.ShouldContain("CanResetVulkanCommandBuffer(secondary, out _)");
        recording.ShouldNotContain("Api!.ResetCommandBuffer(");
        secondaries.ShouldNotContain("Api!.ResetCommandBuffer(");
        secondaries.ShouldContain("TryEnsureMutableDynamicUiSecondaryCommandBuffer(");
        secondaries.ShouldContain("DeferSecondaryCommandBufferFree(imageIndex, pool, previous);");
    }

    [Test]
    public void CompatiblePublication_StillInvalidatesCommandBuffersThatRecordedAnUpdatedSet()
    {
        string lowering = ReadWorkspaceFile(
            "XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/Commands/Scheduling/CommandChains/Cache/VulkanRenderer.CommandChains.ArtifactCache.cs");
        string descriptors = ReadWorkspaceFile(
            "XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/Descriptors/VulkanRenderer.DescriptorSets.cs");
        string pipeline = ReadWorkspaceFile(
            "XREngine.Runtime.Rendering/Rendering/Pipelines/XRRenderPipelineInstance.cs");

        lowering.ShouldContain("RenderResourceChangeKind.CompatibleContentPublication");
        descriptors.ShouldContain("TryCaptureDescriptorUpdateInvalidations_NoLock(");
        descriptors.ShouldContain("InvalidateCachedCommandBuffersByHandle(");
        descriptors.ShouldContain("setState.UsesUpdateAfterBind");
        pipeline.ShouldContain("ClassifyTextureBindingChange");
        pipeline.ShouldContain("RenderResourceChangeKind.StructuralLayout");
    }

    [Test]
    public void MeshDescriptorRefresh_SkipsUnchangedBindingsBeforeNativeUpdate()
    {
        string descriptors = ReadWorkspaceFile(
            "XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/BackendObjects/MeshRendering/VkMeshRenderer.Descriptors.cs");
        string descriptorWrites = ReadWorkspaceFile(
            "XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/BackendObjects/MeshRendering/VkMeshRenderer.DescriptorWrites.cs");
        string allocation = ReadWorkspaceFile(
            "XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/BackendObjects/MeshRendering/Records/Classes/VkMeshRenderer.DescriptorAllocation.cs");
        string key = ReadWorkspaceFile(
            "XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/BackendObjects/MeshRendering/Descriptors/VkMeshRenderer.DescriptorWriteKey.cs");

        allocation.ShouldContain("Dictionary<DescriptorWriteKey, ulong> DescriptorWriteSignatures");
        key.ShouldContain("ulong DescriptorSetHandle");
        descriptorWrites.ShouldContain("ComputeDescriptorBufferInfoSignature(");
        descriptorWrites.ShouldContain("ComputeDescriptorImageInfoSignature(");
        descriptorWrites.ShouldContain("ComputeDescriptorTexelBufferSignature(");
        AssertOrdered(
            descriptorWrites,
            "if (DescriptorWriteMatches(allocation, bufferKey, bufferSignature))",
            "bufferMap.Add((writes.Count, bufferStart, binding, descriptorCount));");
        AssertOrdered(
            descriptorWrites,
            "if (DescriptorWriteMatches(allocation, imageKey, imageSignature))",
            "imageMap.Add((writes.Count, imageStart, binding, descriptorCount));");
        AssertOrdered(
            descriptorWrites,
            "if (DescriptorWriteMatches(allocation, texelKey, texelSignature))",
            "texelMap.Add((writes.Count, texelStart, binding, descriptorCount));");
        AssertOrdered(
            descriptorWrites,
            "Renderer.TryUpdateDescriptorSetsTracked",
            "allocation.DescriptorWriteSignatures[signatures[signatureIndex].key]");
    }

    [Test]
    public void DescriptorAllocationIdentity_UsesImmutableResourcesOnlyWithoutUpdateAfterBind()
    {
        VkMeshRenderer.DescriptorAllocationKey immutableIdentity = new(
            LayoutFingerprint: 11,
            SchemaFingerprint: 12,
            ProgramBindingId: 13,
            DescriptorFrameSlotCount: 3,
            SetCount: 4,
            MaterialIdentity: 5,
            MaterialBindingLayoutVersion: 6,
            ViewFamilyIdentity: 7,
            DescriptorOwnerSlot: 8,
            BindingIdentityFingerprint: 9,
            ImmutableResourceFingerprint: 20);
        VkMeshRenderer.DescriptorAllocationKey changedContent = immutableIdentity with
        {
            ImmutableResourceFingerprint = 21,
        };
        VkMeshRenderer.DescriptorAllocationKey changedBinding = immutableIdentity with
        {
            BindingIdentityFingerprint = 10,
        };
        VkMeshRenderer.DescriptorAllocationKey changedProgram = immutableIdentity with
        {
            ProgramBindingId = 14,
        };
        VkMeshRenderer.DescriptorAllocationKey updateAfterBindIdentity = immutableIdentity with
        {
            ImmutableResourceFingerprint = 0,
        };
        VkMeshRenderer.DescriptorAllocationKey sameUpdateAfterBindIdentity = updateAfterBindIdentity with { };

        changedContent.ShouldNotBe(immutableIdentity);
        changedBinding.ShouldNotBe(immutableIdentity);
        changedProgram.ShouldNotBe(immutableIdentity);
        sameUpdateAfterBindIdentity.ShouldBe(updateAfterBindIdentity);
    }

    [Test]
    public void CapturedDescriptorReuse_RefreshesNonUpdateAfterBindSetsOnlyAfterTheirSlotCompletes()
    {
        string descriptors = ReadWorkspaceFile(
            "XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/BackendObjects/MeshRendering/VkMeshRenderer.Descriptors.cs");
        string descriptorWrites = ReadWorkspaceFile(
            "XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/BackendObjects/MeshRendering/VkMeshRenderer.DescriptorWrites.cs");
        string state = ReadWorkspaceFile(
            "XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/Commands/CommandBuffers/VulkanRenderer.CommandBufferState.cs");

        descriptors.ShouldContain("bool allowCompletedDescriptorSlotRefresh =");
        descriptors.ShouldContain("!descriptorBindingsAreDrawSlotInvariant &&");
        descriptors.ShouldContain("bindingSnapshot is null &&");
        descriptors.ShouldContain(
            "refreshFrameIndex is { } completedFrameIndex &&");
        descriptors.ShouldContain("Renderer.CanUpdateCompletedDescriptorFrameSlot(completedFrameIndex)");
        descriptors.ShouldContain("!Renderer.CanUpdateCompletedDescriptorFrameSlot(frameIndex)");
        descriptors.ShouldContain("captured descriptor frame slot {frameIndex} is still in flight");
        descriptors.ShouldContain("recordDescriptorTableGeneration: false");
        descriptorWrites.ShouldContain("if (recordDescriptorTableGeneration)");
        descriptors.ShouldNotContain("captured descriptor allocation is immutable and requires a new resource snapshot");
        state.ShouldContain("internal bool CanUpdateCompletedDescriptorFrameSlot(int frameDataSlot)");
        state.ShouldContain("_swapchainImageTimelineValues");
        state.ShouldContain("_frameSlotTimelineValues");
        state.ShouldContain("HasTimelineValueCompleted(_graphicsTimelineSemaphore, completionValue)");
    }

    [Test]
    public void CompatiblePublication_UpdatesOnlyTheCompletedDescriptorSlot()
    {
        const ulong previousResource = 41;
        const ulong publishedResource = 42;
        ulong[] slotFingerprints = [previousResource, previousResource, previousResource];

        for (int completedSlot = 0; completedSlot < slotFingerprints.Length; completedSlot++)
        {
            VkMaterial.DescriptorSlotRequiresPublication(
                    slotFingerprints,
                    completedSlot,
                    publishedResource)
                .ShouldBeTrue();

            slotFingerprints[completedSlot] = publishedResource;
            for (int occupiedSlot = completedSlot + 1; occupiedSlot < slotFingerprints.Length; occupiedSlot++)
                slotFingerprints[occupiedSlot].ShouldBe(previousResource);
        }

        slotFingerprints.ShouldAllBe(static value => value == publishedResource);
    }

    [Test]
    public void MaterialDescriptorPublication_IsPerSlotAndWorkerSafe()
    {
        string material = ReadWorkspaceFile(
            "XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/BackendObjects/Materials/VkMaterial.cs");

        material.ShouldContain("lock (_stateSync)");
        material.ShouldContain("UpdateFrameDescriptorSet(state, resolvedFrame)");
        material.ShouldContain("state.SlotResourceFingerprints[resolvedFrame] = resourceFingerprint;");
        material.ShouldNotContain("UpdateDescriptorSets(state)");
    }

    [Test]
    public void DescriptorContents_AreSnapshottedPerSubmissionNotBakedIntoCommandDependencies()
    {
        string lifetime = ReadWorkspaceFile(
            "XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/Frame/Resources/Lifetime/VulkanRenderer.ResourceLifetimeTracking.cs");

        lifetime.ShouldContain("commandLifetime.RefreshTouchedDependencies();");
        lifetime.ShouldContain("TryAppendSubmittedDescriptorDependency_NoLock");
        lifetime.ShouldContain("ResourceKey(ObjectType.Image, backingImageHandle)");
        lifetime.ShouldNotContain("batch.RecordDependency(snapshot.References[i])");
        lifetime.ShouldNotContain("TrackVulkanCommandBufferResource_NoLock(commandBufferHandle, pair.First");
    }

    [Test]
    public void DescriptorSubmissionDependencyRefresh_UsesPersistentLookupInsteadOfQuadraticScan()
    {
        string lifetime = ReadWorkspaceFile(
            "XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/Frame/Resources/Lifetime/VulkanRenderer.ResourceLifetimeTracking.cs");
        string tracker = ReadWorkspaceFile(
            "XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/Resources/Lifetime/VulkanResourceLifetimeTracker.cs");

        tracker.ShouldContain(
            "Dictionary<VulkanRenderer.VulkanResourceLifetimeKey, ulong> SubmissionDependencyGenerationsScratch");
        lifetime.ShouldContain("_resourceLifetimeTracker.SubmissionDependencyGenerationsScratch");
        lifetime.ShouldContain("touchedGenerations.Clear();");
        lifetime.ShouldContain(
            "touchedGenerations.TryGetValue(key, out ulong trackedGeneration)");
        lifetime.ShouldNotContain("for (int i = 0; i < touched.Count; i++)");
        lifetime.ShouldNotContain("new Dictionary<VulkanResourceLifetimeKey, ulong>(touched.Count)");
    }

    [Test]
    public void DescriptorLayoutTracking_PreservesSecondaryExecutionAndFirstUseInvariants()
    {
        string lifetime = ReadWorkspaceFile(
            "XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/Frame/VulkanRenderer.ResourceLifetimeTracking.cs");
        string barrierEmission = SourceContractWorkspace.ReadVulkanSourcesContaining(
            "private void TransitionDescriptorImageForSampling(");

        lifetime.ShouldContain("lifetime.Level = allocateInfo.Level;");
        lifetime.ShouldContain("lifetime.Level == CommandBufferLevel.Secondary;");
        lifetime.ShouldContain("RecordSecondaryDescriptorImageLayoutRequirements(commandBuffer, descriptorSet, snapshotToValidate);");
        lifetime.ShouldContain("ValidateVulkanDescriptorImageLayouts(commandBuffer, descriptorSet, snapshotToValidate);");
        lifetime.ShouldContain("private bool RecordSecondaryDescriptorImageLayoutRequirement(");
        lifetime.ShouldContain("ImageLayout requiredLayout = reference.Type == DescriptorType.StorageImage");

        int transitionStart = barrierEmission.IndexOf(
            "private void TransitionDescriptorImageForSampling(",
            StringComparison.Ordinal);
        int transitionEnd = barrierEmission.IndexOf(
            "private bool IsImageRangeAttachedToFrameBuffer(",
            transitionStart,
            StringComparison.Ordinal);
        transitionStart.ShouldBeGreaterThanOrEqualTo(0);
        transitionEnd.ShouldBeGreaterThan(transitionStart);
        string transition = barrierEmission[transitionStart..transitionEnd];
        AssertOrdered(
            transition,
            "GetCurrentVulkanResourceGeneration(",
            "if (resourceGeneration == 0)",
            "priorState = VulkanImageAccessState.Undefined with",
            "CmdPipelineBarrierTracked(");
    }

    [Test]
    public void DescriptorSubmission_AllowsOnlyAlreadyRecordedPendingRetirementGenerations()
    {
        string lifetime = ReadWorkspaceFile(
            "XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/Frame/VulkanRenderer.ResourceLifetimeTracking.cs");
        int methodStart = lifetime.IndexOf(
            "private bool TryAppendSubmittedDescriptorDependency_NoLock(",
            StringComparison.Ordinal);
        int methodEnd = lifetime.IndexOf("\n    private ", methodStart + 1, StringComparison.Ordinal);
        methodStart.ShouldBeGreaterThanOrEqualTo(0);
        methodEnd.ShouldBeGreaterThan(methodStart);
        string method = lifetime[methodStart..methodEnd];

        AssertOrdered(
            method,
            "if ((resource.State & EVulkanResourceLifetimeState.Destroyed) != 0)",
            "if (touchedGenerations.TryGetValue(key, out ulong trackedGeneration))",
            "else",
            "if ((resource.State & EVulkanResourceLifetimeState.PendingRetirement) != 0 &&");
        method.ShouldContain("touched.Add(new KeyValuePair<VulkanResourceLifetimeKey, ulong>(key, resource.Generation));");
    }

    [Test]
    public void StreamingDescriptorRefresh_RejectsStaleResourceAndLayoutGenerations()
    {
        string material = ReadWorkspaceFile(
            "XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/BackendObjects/Materials/VkMaterial.cs");
        string synchronization = ReadWorkspaceFile(
            "XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/Frame/VulkanRenderer.Synchronization.cs");

        int reusableStart = material.IndexOf(
            "internal bool TryGetValidatedReusableMaterialDescriptorSet(",
            StringComparison.Ordinal);
        int reusableEnd = material.IndexOf(
            "\n\t\t\tinternal static bool DescriptorSlotRequiresPublication(",
            reusableStart,
            StringComparison.Ordinal);
        reusableStart.ShouldBeGreaterThanOrEqualTo(0);
        reusableEnd.ShouldBeGreaterThan(reusableStart);
        string reusable = material[reusableStart..reusableEnd];
        AssertOrdered(
            reusable,
            "ulong currentResourceFingerprint = ComputeResourceFingerprint(program);",
            "state.ResourceFingerprint != currentResourceFingerprint",
            "state.SlotResourceFingerprints[resolvedFrame] != state.ResourceFingerprint");

        int publicationStart = synchronization.IndexOf(
            "private void PublishRecordedImageLayouts(",
            StringComparison.Ordinal);
        int publicationEnd = synchronization.IndexOf(
            "\n    private void AdvanceCompletedImageLayouts(",
            publicationStart,
            StringComparison.Ordinal);
        publicationStart.ShouldBeGreaterThanOrEqualTo(0);
        publicationEnd.ShouldBeGreaterThan(publicationStart);
        string publication = synchronization[publicationStart..publicationEnd];
        AssertOrdered(
            publication,
            "ulong currentGeneration = GetCurrentVulkanResourceGeneration(",
            "if (pair.Value.ResourceGeneration != 0 &&",
            "currentGeneration != pair.Value.ResourceGeneration)",
            "continue;",
            "VulkanImageAccessState publishedState = pair.Value;",
            "state.Submitted = publishedState;");
    }

    [Test]
    public void DefaultPipelineCameraMotionHotPaths_ReuseSchedulesCollectionsAndDiagnosticStorage()
    {
        string lowering = ReadWorkspaceFile(
            "XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/Commands/Scheduling/CommandChains/Planning/VulkanRenderer.CommandChains.Planning.cs");
        string recording = ReadWorkspaceFile(
            "XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/Commands/CommandBuffers/Recording/Primary/VulkanRenderer.CommandBufferRecording.Primary.Secondaries.cs");
        string lifetime = ReadWorkspaceFile(
            "XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/Frame/Resources/Lifetime/VulkanRenderer.ResourceLifetimeTracking.cs");
        string barrierEmission = SourceContractWorkspace.ReadVulkanSourcesContaining(
            "private static void MergeBarrierScope(");
        string resourceLifetimeTracker = ReadWorkspaceFile(
            "XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/Resources/Lifetime/VulkanResourceLifetimeTracker.cs");
        string registry = ReadWorkspaceFile(
            "XREngine.Runtime.Rendering/Rendering/Resources/RenderResourceRegistry.cs");
        string renderToWindow = ReadWorkspaceFile(
            "XREngine.Runtime.Rendering/Rendering/Pipelines/Commands/VPRC_RenderToWindow.cs");
        string forwardPlus = ReadWorkspaceFile(
            "XREngine.Runtime.Rendering/Rendering/Pipelines/Commands/Features/VPRC_ForwardPlusLightCullingPass.cs");
        string renderCommands = ReadWorkspaceFile(
            "XREngine.Runtime.Rendering/Rendering/Commands/RenderCommands/RenderCommandCollection.cs");
        string collectionContext = ReadWorkspaceFile(
            "XREngine.Runtime.Rendering/Rendering/VisualScene3D.CollectionContext.cs");
        string debugDrawing = ReadWorkspaceFile(
            "XREngine.Runtime.Rendering/Runtime/RuntimeEngine.Rendering.Debug.cs");
        string components = ReadWorkspaceFile(
            "XREngine.Runtime.Core/Scene/SceneNode.Components.cs");
        string icons = ReadWorkspaceFile(
            "XREngine.Editor/IMGUI/EditorImGuiUI.Icons.cs");
        string hierarchy = ReadWorkspaceFile(
            "XREngine.Editor/IMGUI/EditorImGuiUI.HierarchyPanel.cs");
        string profiler = ReadWorkspaceFile(
            "XRENGINE/Engine/Subclasses/Engine.CodeProfiler.cs");
        string preferences = ReadWorkspaceFile(
            "XRENGINE/Settings/EditorPreferences.cs");
        string imageViewCache = ReadWorkspaceFile(
            "XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/BackendObjects/Textures/VkImageBackedTexture.ViewCache.cs");
        string viewport = ReadWorkspaceFile(
            "XREngine.Runtime.Rendering/Rendering/XRViewport.cs");
        string window = ReadWorkspaceFile(
            "XREngine.Runtime.Rendering/Rendering/API/XRWindow.cs");
        string events = ReadWorkspaceFile(
            "XREngine.Data/Core/Events/XREvent.cs");
        string uiInput = ReadWorkspaceFile(
            "XREngine.Runtime.InputIntegration/Scene/Components/Pawns/UICanvasInputComponent.cs");
        string collection2D = ReadWorkspaceFile(
            "XREngine.Runtime.Rendering/Rendering/VisualScene2D.CollectionContext.cs");
        string editorJobs = ReadWorkspaceFile(
            "XREngine.Editor/EditorJobTracker.cs");
        string directionalCascades = ReadWorkspaceFile(
            "XREngine.Runtime.Rendering/Scene/Components/Lights/Types/DirectionalLightComponent.CascadeShadows.cs");
        string vulkanMeshPipeline = ReadWorkspaceFile(
            "XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/BackendObjects/MeshRendering/VkMeshRenderer.Pipeline.cs");
        string generatedProgramState = ReadWorkspaceFile(
            "XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/BackendObjects/MeshRendering/Programs/VkMeshRenderer.GeneratedProgramState.cs");
        string meshRendererBase = ReadWorkspaceFile(
            "XREngine.Runtime.Rendering/Rendering/XRMeshRenderer.cs");
        string engine = ReadWorkspaceFile(
            "XRENGINE/Engine/Engine.cs");
        string renderingHost = ReadWorkspaceFile(
            "XREngine.Runtime.Bootstrap/RenderingHost/Engine.RuntimeRenderingHostServices.cs");
        string profilerDumps = ReadWorkspaceFile(
            "XREngine.Editor/ProfilerDiagnosticDumps.cs");
        string eventBase = ReadWorkspaceFile(
            "XREngine.Data/Core/Events/XREventBase.cs");

        lowering.ShouldNotContain("ResourcePlanRevisionChanged");
        ReadWorkspaceFile(
            "XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/Commands/Scheduling/CommandChains/Cache/VulkanRenderer.CommandChains.ScheduleCache.cs")
            .ShouldContain("Build the replacement command-chain");
        barrierEmission.ShouldContain("private static void MergeBarrierScope(");
        recording.ShouldNotContain("mask.HasFlag(");

        registry.ShouldContain("private XRFrameBuffer[] _cachedFrameBufferInstances = [];");
        registry.ShouldContain("internal XRFrameBuffer[] GetFrameBufferInstanceSnapshot()");
        renderToWindow.ShouldContain("instance.Resources.GetFrameBufferInstanceSnapshot()");
        renderToWindow.ShouldContain("_cachedRenderGraphPassName");

        int buildLights = forwardPlus.IndexOf("BuildLocalLights(world.Lights);", StringComparison.Ordinal);
        int resolveDepth = forwardPlus.IndexOf(
            "ActivePipelineInstance.GetTexture<XRTexture>(DepthViewTexture)",
            StringComparison.Ordinal);
        buildLights.ShouldBeGreaterThanOrEqualTo(0);
        resolveDepth.ShouldBeGreaterThan(buildLights);
        forwardPlus.ShouldContain("private readonly List<ForwardPlusLocalLight> _localLightsScratch = [];");
        forwardPlus.ShouldContain("if (lightCount == 0)");

        renderCommands.ShouldContain("private readonly Comparison<Entry> _entryComparison;");
        renderCommands.ShouldContain("_entries.Sort(_entryComparison);");
        collectionContext.ShouldContain("private static readonly Action<RenderInfo3D> CollectRenderCommandsCallback");
        collectionContext.ShouldContain("[ThreadStatic]");
        debugDrawing.ShouldContain("public readonly List<(Vector3 pos, ColorF4 color)> Points = [];");
        debugDrawing.ShouldNotContain("ConcurrentBag<");

        components.ShouldContain("for (int i = 0; i < ComponentsInternal.Count; ++i)");
        components.ShouldNotContain("ComponentsInternal.FirstOrDefault");
        components.ShouldNotContain("ComponentsInternal.LastOrDefault");
        icons.ShouldContain("private readonly record struct IconCacheKey");
        icons.ShouldNotContain("BuildIconCacheKey");

        int drawEntryStart = hierarchy.IndexOf("private static bool DrawSceneNodeEntry(", StringComparison.Ordinal);
        int drawEntryEnd = hierarchy.IndexOf("private static void QueueHierarchyReparent(", drawEntryStart, StringComparison.Ordinal);
        hierarchy[drawEntryStart..drawEntryEnd].ShouldNotContain("EnqueueSceneEdit(() =>");

        resourceLifetimeTracker.ShouldContain("internal ThreadLocal<HashSet<ulong>> ChangedDescriptorSetsScratch");
        lifetime.ShouldContain("_resourceLifetimeTracker.ChangedDescriptorSetsScratch.Value!");
        lifetime.ShouldNotContain("state.IndexedReferences.UnionWith(currentReferences)");
        profiler.ShouldContain("private bool _enableComponentTiming = false;");
        preferences.ShouldContain("[DefaultValue(false)]");

        imageViewCache.ShouldContain("entry.AttachmentViews.Clear();");
        imageViewCache.ShouldContain("private sealed class PhysicalImageViewCacheEntry(");
        viewport.ShouldContain("_swapBuffersProfileName ??=");
        window.ShouldContain("for (int i = 0; i < viewports.Count; i++)");
        window.ShouldNotContain("StartProfileScope($\"XRViewport.Render[");
        window.ShouldNotContain("StartProfileScope($\"XRViewport.RenderToFBO[");
        events.ShouldNotContain("WithProfiling(\"XREvent.Invoke\", InvokeInternal)");
        events.ShouldContain("IDisposable? sample = BeginProfiling(\"XREvent.Invoke\")");
        uiInput.ShouldContain("_intersectionCollectionScratch");
        uiInput.ShouldNotContain(".Union(UIElementIntersections)");
        uiInput.ShouldNotContain("LastUIElementIntersections.ToArray()");
        collection2D.ShouldContain("private static readonly Action<RenderInfo2D> CollectRenderCommandsCallback");
        editorJobs.ShouldContain("if (_cachedSnapshotRevision == _snapshotRevision)");
        editorJobs.ShouldNotContain(".OrderByDescending(");
        directionalCascades.ShouldContain("private static ReusableBoxVolume GetCascadeCullVolumeScratch");

        vulkanMeshPipeline.ShouldContain("_programStateCache.TryGetValue(programState");
        vulkanMeshPipeline.ShouldContain("_programStateCache[programState] = entry;");
        generatedProgramState.ShouldContain("ReferenceEquals(VersionKindLabel, other.VersionKindLabel)");
        meshRendererBase.ShouldContain("_versionKindLabel ??= ResolveVersionKindLabel()");

        renderingHost.ShouldContain("Engine.StartPooledProfilerScope(");
        engine.ShouldContain("PooledExternalProfilerScope.Rent(Profiler.Start(sampleName))");
        profilerDumps.ShouldContain("finally");
        profilerDumps.ShouldContain("Engine.Profiler.EnableFrameLogging = false;");
        eventBase.ShouldContain("private Dictionary<(string Prefix, TListener Listener, int Index), string>? _listenerProfilingNames;");
        eventBase.ShouldContain("_listenerProfilingNames ??= [];");
    }

    private static void AssertOrdered(string source, params string[] markers)
    {
        int previous = -1;
        foreach (string marker in markers)
        {
            int index = source.IndexOf(marker, previous + 1, StringComparison.Ordinal);
            index.ShouldBeGreaterThan(
                previous,
                $"Expected '{marker}' after the previous binding-refresh stage.");
            previous = index;
        }
    }

    private static string ReadWorkspaceFile(string relativePath)
        => SourceContractWorkspace.ReadFile(relativePath);

    private static FrameOperationStream LowerOperations(FrameOp[] operations)
    {
        FrameOperationIngress ingress = new();
        ingress.Populate(operations);
        FrameOperationStream stream = new();
        stream.Lower(ingress);
        return stream;
    }

    private static void PublishBindingLayoutSignaturesForTest(
        ComputeDispatchSnapshot snapshot)
        => snapshot.PublishBindingLayoutSignatures(
            backendContext: null!,
            wrapperLookup: new VulkanWrapperLookupPort(null!),
            frameSourcePipeline: null);

    private sealed class TestBindingPublisher(
        ERenderBindingFrequency frequency,
        ulong generation) : IRenderBindingPublisher
    {
        public ERenderBindingFrequency Frequency { get; } = frequency;
        public ulong Generation { get; } = generation;

        public void PublishUniforms(
            XRRenderProgram vertexProgram,
            XRRenderProgram materialProgram)
            => materialProgram.Uniform("TestValue", 1f);
    }
}
