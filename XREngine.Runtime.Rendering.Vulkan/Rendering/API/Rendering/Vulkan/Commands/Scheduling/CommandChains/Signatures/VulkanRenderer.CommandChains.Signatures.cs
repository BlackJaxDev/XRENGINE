using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using Silk.NET.Vulkan;
using XREngine.Data.Rendering;
using XREngine.Rendering.Models.Materials;
using XREngine.Rendering.RenderGraph;
using XREngine.Rendering.Shadows;

namespace XREngine.Rendering.Vulkan;

public unsafe partial class VulkanRenderer
{
    private static ulong ComputeScheduleStructuralSignature(
        ReadOnlySpan<RenderPassChainGroup> groups,
        bool requiresFreshPrimary,
        int inlineFrameOpCount)
    {
        FrameOpSignatureHasher hash = new();
        hash.Add(groups.Length);
        hash.Add(requiresFreshPrimary);
        hash.Add(inlineFrameOpCount);
        for (int i = 0; i < groups.Length; i++)
        {
            RenderPassChainGroup group = groups[i];
            hash.Add(group.PassIndex);
            hash.Add(group.TargetIdentity);
            hash.Add(group.StructuralSignature);
            hash.Add(group.SupportsSecondaryCommandBuffers);
            hash.Add(group.DynamicOverlay);

            ReadOnlySpan<CommandChainKey> keys = group.ChainKeys.Span;
            hash.Add(keys.Length);
            for (int keyIndex = 0; keyIndex < keys.Length; keyIndex++)
            {
                CommandChainKey key = keys[keyIndex];
                hash.Add(key.FrameSlot);
                hash.Add(key.PassIndex);
                hash.Add(key.TargetIdentity);
                hash.Add(key.DescriptorBindingVariant);
                hash.Add(key.ChainOrdinal);
                hash.Add(key.ViewKey.PipelineIdentity);
                hash.Add(key.ViewKey.ViewportIdentity);
                hash.Add(key.ViewKey.ViewIndex);
                hash.Add((int)key.ViewKey.Kind);
                hash.Add(key.ViewKey.LightIdentity);
                hash.Add(key.ViewKey.CascadeIndex);
            }
        }

        return hash.ToHash();
    }

    /// <summary>
    /// Returns whether every prepared mesh binding represented by the aggregate
    /// primary dependency signature is recorded in a command-chain secondary.
    /// Inline clears, blits, barriers, and publications do not own those bindings
    /// and therefore must not force the thin primary to track mesh identity.
    /// </summary>
    internal static bool AreAllPreparedDrawBindingsSecondaryOwned(
        CommandChainSchedule schedule,
        FrameOp[] ops)
    {
        ReadOnlySpan<RenderPassChainGroup> groups = schedule.Groups.Span;
        if (groups.Length == 0)
            return false;

        for (int i = 0; i < groups.Length; i++)
            if (!groups[i].SupportsSecondaryCommandBuffers)
                return false;

        bool foundPreparedDraw = false;
        int queryBracketDepth = 0;
        for (int i = 0; i < ops.Length; i++)
        {
            FrameOp op = ops[i];
            if (op is QueryOp queryOp)
            {
                if (queryOp.Operation == ERenderQueryOperation.Begin)
                    queryBracketDepth++;
                else if (queryOp.Operation == ERenderQueryOperation.End && queryBracketDepth > 0)
                    queryBracketDepth--;
                continue;
            }

            PendingMeshDraw draw = op switch
            {
                MeshDrawOp direct => direct.Draw,
                IndirectDrawOp indirect => indirect.Draw,
                _ => default,
            };
            if (draw.Renderer is null)
                continue;

            foundPreparedDraw = true;
            if (queryBracketDepth != 0 ||
                !IsSchedulableCommandChainFrameOp(op, dynamicOverlay: false))
            {
                return false;
            }
        }

        return foundPreparedDraw;
    }

    internal static ulong ComputeCommandChainPrimarySkeletonSignature(FrameOp[] ops)
    {
        FrameOpSignatureHasher hash = new();
        hash.Add(0x5052494D534B454CUL);

        int queryBracketDepth = 0;
        int inlineOpIndex = 0;
        int secondaryRunCount = 0;
        bool inSecondaryRun = false;
        int currentPassIndex = 0;
        int currentTargetIdentity = 0;
        RenderViewKey currentViewKey = default;

        for (int opIndex = 0; opIndex < ops.Length; opIndex++)
        {
            FrameOp op = ops[opIndex];
            bool isQuery = op is QueryOp;
            bool schedulable =
                !isQuery &&
                queryBracketDepth == 0 &&
                IsSchedulableCommandChainFrameOp(op, dynamicOverlay: false);

            if (schedulable)
            {
                int passIndex = op.PassIndex;
                int targetIdentity = ResolveCommandChainTargetIdentity(op);
                RenderViewKey viewKey = BuildRenderViewKey(op, dynamicOverlay: false);
                if (!inSecondaryRun ||
                    passIndex != currentPassIndex ||
                    targetIdentity != currentTargetIdentity ||
                    viewKey != currentViewKey)
                {
                    hash.Add(0x5345434F4E444152UL);
                    hash.Add(passIndex);
                    hash.Add(targetIdentity);
                    hash.Add(viewKey.PipelineIdentity);
                    hash.Add(viewKey.ViewportIdentity);
                    hash.Add(viewKey.ViewIndex);
                    hash.Add((int)viewKey.Kind);
                    hash.Add(viewKey.LightIdentity);
                    hash.Add(viewKey.CascadeIndex);
                    secondaryRunCount++;
                    currentPassIndex = passIndex;
                    currentTargetIdentity = targetIdentity;
                    currentViewKey = viewKey;
                }

                inSecondaryRun = true;
            }
            else
            {
                inSecondaryRun = false;
                RenderPacketVolatility volatility = ClassifyRenderPacketVolatility(op, dynamicOverlay: false);
                hash.Add(0x494E4C494E454F50UL);
                hash.Add(ComputeFrameOpStructuralSignature(op, inlineOpIndex, volatility));
                hash.Add(ResolvePipelineGeneration(op));
                inlineOpIndex++;
            }

            if (op is QueryOp queryOp)
            {
                if (queryOp.Operation == ERenderQueryOperation.Begin)
                    queryBracketDepth++;
                else if (queryOp.Operation == ERenderQueryOperation.End && queryBracketDepth > 0)
                    queryBracketDepth--;
            }
        }

        hash.Add(secondaryRunCount);
        hash.Add(inlineOpIndex);
        return hash.ToHash();
    }

    internal static ulong ComputePrimaryCommandBufferGroupSignature(CommandChainSchedule schedule)
        => ComputePrimaryCommandBufferGroupSignature(schedule, null);

    internal static ulong ComputePrimaryCommandBufferGroupSignature(
        CommandChainSchedule schedule,
        IReadOnlyDictionary<CommandChainKey, CommandChain>? chains)
        => ComputePrimaryCommandBufferGroupIdentity(schedule, chains).Combined;

    internal static VulkanCommandIdentityComponents
        ComputePrimaryCommandBufferGroupIdentity(
            CommandChainSchedule schedule,
            IReadOnlyDictionary<CommandChainKey, CommandChain>? chains)
    {
        FrameOpSignatureHasher orderedNodes = new();
        FrameOpSignatureHasher renderScopeInheritance = new();
        FrameOpSignatureHasher nestedArtifacts = new();
        FrameOpSignatureHasher primaryOnly = new();
        ReadOnlySpan<RenderPassChainGroup> groups = schedule.Groups.Span;
        orderedNodes.Add(schedule.InlineFrameOpCount);
        primaryOnly.Add(schedule.RequiresFreshPrimary);
        primaryOnly.Add(schedule.InlineFrameOpCount);
        orderedNodes.Add(groups.Length);
        renderScopeInheritance.Add(groups.Length);
        nestedArtifacts.Add(groups.Length);
        primaryOnly.Add(groups.Length);
        for (int i = 0; i < groups.Length; i++)
        {
            RenderPassChainGroup group = groups[i];
            orderedNodes.Add(group.PassIndex);
            orderedNodes.Add(group.TargetIdentity);
            orderedNodes.Add(group.SupportsSecondaryCommandBuffers);
            orderedNodes.Add(group.DynamicOverlay);
            renderScopeInheritance.Add(group.PassIndex);
            renderScopeInheritance.Add(group.TargetIdentity);
            renderScopeInheritance.Add(group.DynamicOverlay);
            // Draw membership is recorded into secondary command buffers. A group that
            // cannot use a secondary remains inline and therefore retains its packet
            // structure as part of the primary identity.
            if (!group.SupportsSecondaryCommandBuffers)
                primaryOnly.Add(group.StructuralSignature);
            primaryOnly.Add(group.SupportsSecondaryCommandBuffers);
            primaryOnly.Add(group.DynamicOverlay);
            ReadOnlySpan<CommandChainKey> keys = group.ChainKeys.Span;
            orderedNodes.Add(keys.Length);
            nestedArtifacts.Add(keys.Length);
            for (int keyIndex = 0; keyIndex < keys.Length; keyIndex++)
            {
                CommandChainKey key = keys[keyIndex];
                orderedNodes.Add(key.FrameSlot);
                orderedNodes.Add(key.PassIndex);
                orderedNodes.Add(key.TargetIdentity);
                // DescriptorBindingVariant selects the exact secondary command
                // buffer whose descriptor sets were recorded. Omitting it lets a
                // thin primary recorded for an earlier frame-source publication
                // appear compatible with a newly selected secondary chain.
                orderedNodes.Add(key.DescriptorBindingVariant);
                orderedNodes.Add(key.ChainOrdinal);
                orderedNodes.Add(key.ViewKey.PipelineIdentity);
                orderedNodes.Add(key.ViewKey.ViewportIdentity);
                orderedNodes.Add(key.ViewKey.ViewIndex);
                orderedNodes.Add((int)key.ViewKey.Kind);
                orderedNodes.Add(key.ViewKey.LightIdentity);
                orderedNodes.Add(key.ViewKey.CascadeIndex);
                renderScopeInheritance.Add(key.PassIndex);
                renderScopeInheritance.Add(key.TargetIdentity);
                renderScopeInheritance.Add(key.ViewKey.ViewIndex);
                renderScopeInheritance.Add((int)key.ViewKey.Kind);
                if (chains is not null && chains.TryGetValue(key, out CommandChain? chain))
                {
                    VulkanRecordedCommandArtifactReference artifact =
                        chain.RecordedArtifact.CreateReference();
                    artifact.AddTo(ref nestedArtifacts);
                }
                else
                {
                    default(VulkanRecordedCommandArtifactReference)
                        .AddTo(ref nestedArtifacts);
                }
            }
        }

        VulkanCommandIdentityComponents scheduleComponents =
            schedule.DependencySignature.CaptureIdentityComponents();
        return new VulkanCommandIdentityComponents(
            orderedNodes.ToHash(),
            ResourceGenerations: 0,
            renderScopeInheritance.ToHash(),
            scheduleComponents.QueueAssumptions,
            nestedArtifacts.ToHash(),
            primaryOnly.ToHash(),
            SecondaryOnly: 0,
            DataContent: 0);
    }

    internal static bool TryValidatePrimaryCommandBufferGroupSharedDependencies(
        CommandChainSchedule schedule,
        IReadOnlyDictionary<CommandChainKey, CommandChain> chains,
        out CommandRecordingDependencyMismatch mismatch)
    {
        ReadOnlySpan<RenderPassChainGroup> groups = schedule.Groups.Span;
        for (int groupIndex = 0; groupIndex < groups.Length; groupIndex++)
        {
            ReadOnlySpan<CommandChainKey> keys =
                groups[groupIndex].ChainKeys.Span;
            for (int keyIndex = 0; keyIndex < keys.Length; keyIndex++)
            {
                if (!chains.TryGetValue(keys[keyIndex], out CommandChain? chain))
                {
                    mismatch = CommandRecordingDependencyMismatch.None;
                    return false;
                }

                if (!chain.RecordedArtifact.TryValidateSharedDependency(
                        chain.DependencySignature,
                        out mismatch))
                    return false;
            }
        }

        mismatch = CommandRecordingDependencyMismatch.None;
        return true;
    }

    private static int InvalidatePrimaryCommandBufferGroupSharedDependencyMismatches(
        CommandChainSchedule schedule,
        IReadOnlyDictionary<CommandChainKey, CommandChain> chains)
    {
        int invalidated = 0;
        ReadOnlySpan<RenderPassChainGroup> groups = schedule.Groups.Span;
        for (int groupIndex = 0; groupIndex < groups.Length; groupIndex++)
        {
            ReadOnlySpan<CommandChainKey> keys =
                groups[groupIndex].ChainKeys.Span;
            for (int keyIndex = 0; keyIndex < keys.Length; keyIndex++)
            {
                if (!chains.TryGetValue(keys[keyIndex], out CommandChain? chain) ||
                    chain.RecordedArtifact.TryValidateSharedDependency(
                        chain.DependencySignature,
                        out CommandRecordingDependencyMismatch mismatch))
                {
                    continue;
                }

                MarkCommandChainSecondaryCommandBufferInvalid(
                    chain,
                    EVulkanRecordedCommandArtifactInvalidationReason.DependencyChanged);
                chain.DirtyReason |=
                    mismatch.InvalidationClass ==
                        CommandRecordingInvalidationClass.BindingIdentity
                        ? CommandChainDirtyReason.ResourcePlan
                        : CommandChainDirtyReason.Structure;
                invalidated++;
            }
        }

        return invalidated;
    }

    internal static CommandChainKey[] BuildCommandChainKeysByFrameOpIndex(
        CommandChainSchedule schedule,
        IReadOnlyDictionary<CommandChainKey, CommandChain> commandChains,
        int staticOpCount)
    {
        if (staticOpCount <= 0)
            return [];

        CommandChainKey[] keysByOpIndex = new CommandChainKey[staticOpCount];
        PopulateCommandChainKeysByFrameOpIndex(
            schedule,
            commandChains,
            keysByOpIndex.AsSpan(),
            staticOpCount);
        return keysByOpIndex;
    }

    private static void PopulateCommandChainKeysByFrameOpIndex(
        CommandChainSchedule schedule,
        IReadOnlyDictionary<CommandChainKey, CommandChain> commandChains,
        Span<CommandChainKey> keysByOpIndex,
        int staticOpCount)
    {
        if (staticOpCount <= 0)
            return;
        if (keysByOpIndex.Length < staticOpCount)
            throw new ArgumentException("The command-chain key scratch span is smaller than the frame-op count.", nameof(keysByOpIndex));

        keysByOpIndex = keysByOpIndex[..staticOpCount];
        CommandChainKey unmappedKey = new(0, default, 0, 0, 0UL, false, -1);
        keysByOpIndex.Fill(unmappedKey);
        ReadOnlySpan<RenderPassChainGroup> groups = schedule.Groups.Span;
        for (int groupIndex = 0; groupIndex < groups.Length; groupIndex++)
        {
            RenderPassChainGroup group = groups[groupIndex];
            if (group.DynamicOverlay)
                continue;

            ReadOnlySpan<CommandChainKey> keys = group.ChainKeys.Span;
            for (int keyIndex = 0; keyIndex < keys.Length; keyIndex++)
            {
                CommandChainKey key = keys[keyIndex];
                if (!commandChains.TryGetValue(key, out CommandChain? chain) ||
                    chain.SourceStartIndex < 0 ||
                    chain.SourceCount <= 0)
                {
                    continue;
                }

                int endIndex = Math.Min(staticOpCount, chain.SourceStartIndex + chain.SourceCount);
                for (int opIndex = chain.SourceStartIndex; opIndex < endIndex; opIndex++)
                    keysByOpIndex[opIndex] = key;
            }
        }

    }

    internal static ulong ComputeShadowCommandChainStructuralSignature(in LayeredShadowUniformState shadowState)
    {
        FrameOpSignatureHasher hash = new();
        hash.Add(shadowState.IsShadowPass);
        hash.Add(shadowState.DirectionalCascadeInstancedLayeredShadowPass);
        hash.Add(shadowState.DirectionalCascadeShadowLayerCount);
        hash.Add(shadowState.PointLightInstancedLayeredShadowPass);
        hash.Add(shadowState.PointLightShadowFaceCount);
        for (int i = 0; i < shadowState.PointLightShadowFaceCount; i++)
        {
            shadowState.TryGetPointLightShadowFaceIndex(i, out int faceIndex);
            hash.Add(faceIndex);
        }

        return hash.ToHash();
    }

    internal static void ValidateCommandChainShadowFallbackMode(ShadowFallbackMode fallbackMode, bool shadowTileResident)
    {
        if (shadowTileResident)
        {
            if (fallbackMode is not ShadowFallbackMode.None and not ShadowFallbackMode.StaleTile)
            {
                throw new InvalidOperationException(
                    $"Command-chain shadow validation rejected resident shadow tile with fallback mode {fallbackMode}.");
            }

            return;
        }

        if (fallbackMode == ShadowFallbackMode.None)
            throw new InvalidOperationException("Command-chain shadow validation rejected non-resident shadow tile without an explicit fallback mode.");
    }

    private static ulong ComputeFrameOpFrameDataSignature(FrameOp op, int opIndex)
    {
        FrameOpSignatureHasher hash = new();
        hash.Add(opIndex);
        switch (op)
        {
            case MeshDrawOp draw:
                AddMatrixSignature(ref hash, draw.Draw.ModelMatrix);
                AddMatrixSignature(ref hash, draw.Draw.PreviousModelMatrix);
                AddMatrixSignature(ref hash, draw.Draw.ViewProjectionMatrix);
                AddMatrixSignature(ref hash, draw.Draw.PreviousViewProjectionMatrix);
                if (draw.Draw.IsStereoPass)
                {
                    AddMatrixSignature(ref hash, draw.Draw.RightEyeViewProjectionMatrix);
                    AddMatrixSignature(ref hash, draw.Draw.PreviousRightEyeViewProjectionMatrix);
                }
                AddVector3Signature(ref hash, draw.Draw.CameraPosition);
                AddVector3Signature(ref hash, draw.Draw.CameraForward);
                AddVector3Signature(ref hash, draw.Draw.CameraUp);
                AddVector3Signature(ref hash, draw.Draw.CameraRight);
                hash.Add(draw.Draw.TransformId);
                hash.Add(draw.Draw.RenderAreaWidth);
                hash.Add(draw.Draw.RenderAreaHeight);
                break;
            case ComputeDispatchOp compute:
                hash.Add(compute.Snapshot.HasPublishedBindingLayoutSignatures
                    ? compute.Snapshot.RuntimeUniformValueSignature
                    : HashUniformBindings(compute.Snapshot.Uniforms));
                break;
            case ClearOp clear:
                hash.Add(clear.Color.R);
                hash.Add(clear.Color.G);
                hash.Add(clear.Color.B);
                hash.Add(clear.Color.A);
                hash.Add(clear.Depth);
                hash.Add(clear.Stencil);
                break;
        }

        return hash.ToHash();
    }

    private static void AddViewportScissorSignature(ref FrameOpSignatureHasher hash, in PendingMeshDraw draw)
    {
        AddViewportSignature(ref hash, draw.Viewport);
        AddRectSignature(ref hash, draw.Scissor);
        hash.Add(draw.ViewportScissorCount);
        if (draw.ViewportScissorCount <= 1 ||
            draw.IndexedViewports is not { } indexedViewports ||
            draw.IndexedScissors is not { } indexedScissors)
        {
            return;
        }

        int indexedCount = (int)Math.Min(
            draw.ViewportScissorCount,
            (uint)Math.Min(indexedViewports.Length, indexedScissors.Length));
        hash.Add(indexedCount);
        for (int i = 0; i < indexedCount; i++)
        {
            AddViewportSignature(ref hash, indexedViewports[i]);
            AddRectSignature(ref hash, indexedScissors[i]);
        }
    }

    private static void AddViewportSignature(ref FrameOpSignatureHasher hash, in Viewport viewport)
    {
        hash.Add(viewport.X);
        hash.Add(viewport.Y);
        hash.Add(viewport.Width);
        hash.Add(viewport.Height);
        hash.Add(viewport.MinDepth);
        hash.Add(viewport.MaxDepth);
    }

    private static void AddRectSignature(ref FrameOpSignatureHasher hash, in Rect2D rect)
    {
        hash.Add(rect.Offset.X);
        hash.Add(rect.Offset.Y);
        hash.Add(rect.Extent.Width);
        hash.Add(rect.Extent.Height);
    }

    private static void AddMatrixSignature(ref FrameOpSignatureHasher hash, in Matrix4x4 matrix)
    {
        hash.Add(matrix.M11);
        hash.Add(matrix.M12);
        hash.Add(matrix.M13);
        hash.Add(matrix.M14);
        hash.Add(matrix.M21);
        hash.Add(matrix.M22);
        hash.Add(matrix.M23);
        hash.Add(matrix.M24);
        hash.Add(matrix.M31);
        hash.Add(matrix.M32);
        hash.Add(matrix.M33);
        hash.Add(matrix.M34);
        hash.Add(matrix.M41);
        hash.Add(matrix.M42);
        hash.Add(matrix.M43);
        hash.Add(matrix.M44);
    }

    private static void AddVector3Signature(ref FrameOpSignatureHasher hash, in Vector3 vector)
    {
        hash.Add(vector.X);
        hash.Add(vector.Y);
        hash.Add(vector.Z);
    }

    private static ulong ComputeDispatchSnapshotSignature(ComputeDispatchSnapshot snapshot)
    {
        FrameOpSignatureHasher hash = new();
        if (snapshot.HasPublishedBindingLayoutSignatures)
        {
            // CaptureProgramBindingSnapshot has already reduced the exact sampler,
            // image, and buffer resources into this immutable signature. Rewalking
            // its dictionaries for every compatibility and dependency comparison
            // made clean command-buffer reuse O(draws * reflected bindings).
            hash.Add(1);
            hash.Add(snapshot.PersistentEngineResourceSignature);
            return hash.ToHash();
        }

        HashProgramBindingSnapshot(ref hash, snapshot, includeMutableFrameSourceDescriptors: true);
        return hash.ToHash();
    }

    private static ulong ComputeDispatchSnapshotDescriptorSetSignature(ComputeDispatchSnapshot snapshot)
    {
        if (snapshot.HasPublishedBindingLayoutSignatures)
            return snapshot.DescriptorSetLayoutSignature;

        FrameOpSignatureHasher hash = new();
        hash.Add(1);
        hash.Add(HashSamplerUnitBindingLayout(snapshot.Samplers, snapshot.SamplerNamesByUnit));
        hash.Add(HashSamplerNameBindingLayout(snapshot.SamplersByName));
        hash.Add(HashImageBindingLayout(snapshot.Images));
        hash.Add(HashBufferBindingLayout(snapshot.Buffers));
        return hash.ToHash();
    }

    internal static ulong HashSamplerUnitBindingLayout(Dictionary<uint, XRTexture> samplers, Dictionary<uint, string> samplerNamesByUnit)
    {
        ulong xor = 0;
        ulong sum = 0;
        foreach (KeyValuePair<uint, XRTexture> pair in samplers)
        {
            FrameOpSignatureHasher item = new();
            item.Add(pair.Key);
            item.Add(samplerNamesByUnit.TryGetValue(pair.Key, out string? name) ? name : string.Empty);
            AddUnorderedItemHash(ref xor, ref sum, item.ToHash());
        }

        return FinishUnorderedHash(samplers.Count, xor, sum);
    }

    internal static ulong HashSamplerNameBindingLayout(Dictionary<string, XRTexture> samplers)
    {
        ulong xor = 0;
        ulong sum = 0;
        foreach (KeyValuePair<string, XRTexture> pair in samplers)
        {
            FrameOpSignatureHasher item = new();
            item.Add(pair.Key);
            AddUnorderedItemHash(ref xor, ref sum, item.ToHash());
        }

        return FinishUnorderedHash(samplers.Count, xor, sum);
    }

    internal static ulong HashImageBindingLayout(Dictionary<uint, ProgramImageBinding> images)
    {
        ulong xor = 0;
        ulong sum = 0;
        foreach (KeyValuePair<uint, ProgramImageBinding> pair in images)
        {
            ProgramImageBinding binding = pair.Value;
            FrameOpSignatureHasher item = new();
            item.Add(pair.Key);
            item.Add(binding.Level);
            item.Add(binding.Layered);
            item.Add(binding.Layer);
            item.Add((int)binding.Access);
            item.Add((int)binding.Format);
            AddUnorderedItemHash(ref xor, ref sum, item.ToHash());
        }

        return FinishUnorderedHash(images.Count, xor, sum);
    }

    internal static ulong HashBufferBindingLayout(Dictionary<uint, VulkanComputeBufferBinding> buffers)
    {
        ulong xor = 0;
        ulong sum = 0;
        foreach (KeyValuePair<uint, VulkanComputeBufferBinding> pair in buffers)
        {
            FrameOpSignatureHasher item = new();
            item.Add(pair.Key);
            AddUnorderedItemHash(ref xor, ref sum, item.ToHash());
        }

        return FinishUnorderedHash(buffers.Count, xor, sum);
    }

    private static ulong MixSignature(ulong left, ulong right)
    {
        unchecked
        {
            ulong value = left == 0 ? 14695981039346656037UL : left;
            value ^= right;
            value *= 1099511628211UL;
            value ^= right >> 32;
            return value;
        }
    }
}

