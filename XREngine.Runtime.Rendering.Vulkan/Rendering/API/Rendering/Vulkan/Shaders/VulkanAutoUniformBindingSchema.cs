using XREngine.Rendering.Models.Materials;
using XREngine.Data.Rendering;

namespace XREngine.Rendering.Vulkan;

/// <summary>
/// Immutable value-binding schema for one reflected auto-uniform block.
/// </summary>
internal sealed class VulkanAutoUniformBindingSchema
{
    private const string VertexUniformSuffix = "_VTX";

    private VulkanAutoUniformBindingSchema(
        AutoUniformBlockInfo block,
        ulong programLinkGeneration,
        VulkanAutoUniformBindingOperation[] operations,
        EVulkanBindingFrequencyMask frequencyMask,
        EVulkanAutoUniformFallbackReason fallbackKind,
        string? fallbackReason)
    {
        Block = block;
        ProgramLinkGeneration = programLinkGeneration;
        Operations = operations;
        PublicationLayoutSignature =
            ComputePublicationLayoutSignature(block, operations);
        FrequencyMask = frequencyMask;
        FallbackKind = fallbackKind;
        FallbackReason = fallbackReason;
    }

    internal AutoUniformBlockInfo Block { get; }
    internal ulong ProgramLinkGeneration { get; }
    internal VulkanAutoUniformBindingOperation[] Operations { get; }
    internal ulong PublicationLayoutSignature { get; }
    internal EVulkanBindingFrequencyMask FrequencyMask { get; }
    internal EVulkanAutoUniformFallbackReason FallbackKind { get; }
    internal string? FallbackReason { get; }
    internal bool IsFastPathEligible
        => FallbackKind == EVulkanAutoUniformFallbackReason.None;

    internal static VulkanAutoUniformBindingSchema Compile(
        AutoUniformBlockInfo block,
        ulong programLinkGeneration)
    {
        VulkanAutoUniformBindingOperation[] operations =
            new VulkanAutoUniformBindingOperation[block.Members.Count];
        EVulkanAutoUniformFallbackReason firstFallbackKind =
            EVulkanAutoUniformFallbackReason.None;
        EVulkanBindingFrequencyMask frequencyMask =
            EVulkanBindingFrequencyMask.None;
        string? firstFallbackReason = null;

        for (int memberIndex = 0; memberIndex < block.Members.Count; memberIndex++)
        {
            AutoUniformMember member = block.Members[memberIndex];
            VulkanAutoUniformBindingOperation operation = CompileOperation(block, member);
            operations[memberIndex] = operation;
            frequencyMask |= GetFrequencyMask(operation);
            if (firstFallbackKind == EVulkanAutoUniformFallbackReason.None)
                firstFallbackKind = operation.FallbackKind;
            firstFallbackReason ??= operation.FallbackReason;
        }

        return new VulkanAutoUniformBindingSchema(
            block,
            programLinkGeneration,
            operations,
            frequencyMask,
            firstFallbackKind,
            firstFallbackReason);
    }

    private static EVulkanBindingFrequencyMask GetFrequencyMask(
        in VulkanAutoUniformBindingOperation operation)
    {
        if (operation.SourceKind ==
            EVulkanAutoUniformSourceKind.MaterialOrRuntime)
        {
            return EVulkanBindingFrequencyMask.Material |
                EVulkanBindingFrequencyMask.RuntimeCallback;
        }

        int bitIndex = (int)operation.Frequency - 1;
        return (uint)bitIndex < 7u
            ? (EVulkanBindingFrequencyMask)(1 << bitIndex)
            : EVulkanBindingFrequencyMask.None;
    }

    private static VulkanAutoUniformBindingOperation CompileOperation(
        AutoUniformBlockInfo block,
        AutoUniformMember member)
    {
        EVulkanAutoUniformFallbackReason invalidKind =
            ValidateDestination(block, member, out string? invalidReason);
        if (invalidReason is not null)
            return Unsupported(member, invalidKind, invalidReason);

        if (member.StructMembers is { Count: > 0 })
        {
            return new VulkanAutoUniformBindingOperation(
                member,
                EVulkanAutoUniformSourceKind.StructSnapshot,
                // Struct fields are published individually, but the source
                // rewriter has already placed the complete struct in one
                // frequency-owned block. Inherit that declared ownership so
                // frame-slot publication follows the struct's actual owner.
                // Treating every struct as Material leaves Object-owned data
                // such as deferred LightData stale across reused slots.
                block.Frequency,
                default,
                default,
                default,
                default,
                EVulkanUniformWriteConversion.StructSnapshot,
                EVulkanAutoUniformFallbackReason.None,
                null);
        }

        EShaderVarType destinationType = member.EngineType!.Value;
        EVulkanUniformWriteConversion conversion = member.IsArray
            ? EVulkanUniformWriteConversion.TypedArray
            : EVulkanUniformWriteConversion.DirectTyped;

        string normalizedName = NormalizeName(member.Name);
        if (TryResolveTemporalSource(
                normalizedName,
                out EVulkanTemporalUniformSource temporalSource))
        {
            return new VulkanAutoUniformBindingOperation(
                member,
                EVulkanAutoUniformSourceKind.TemporalViewProjection,
                EVulkanBindingFrequency.View,
                default,
                default,
                temporalSource,
                destinationType,
                conversion,
                EVulkanAutoUniformFallbackReason.None,
                null);
        }

        if (Enum.TryParse(
                normalizedName,
                ignoreCase: false,
                out EEngineUniform engineUniform))
        {
            EShaderVarType sourceType = ResolveEngineSourceType(engineUniform);
            if (!AreCompatible(destinationType, sourceType))
            {
                return Unsupported(
                    member,
                    EVulkanAutoUniformFallbackReason.EngineSourceTypeMismatch,
                    $"Auto-uniform '{member.Name}' expects {destinationType}, but engine source " +
                    $"'{engineUniform}' publishes {sourceType}.");
            }

            return new VulkanAutoUniformBindingOperation(
                member,
                EVulkanAutoUniformSourceKind.Engine,
                ResolveEngineFrequency(engineUniform),
                engineUniform,
                default,
                default,
                destinationType,
                destinationType == sourceType
                    ? conversion
                    : EVulkanUniformWriteConversion.CompatibleTyped,
                EVulkanAutoUniformFallbackReason.None,
                null);
        }

        if (TryResolveSpecialSource(
                normalizedName,
                out EVulkanAutoUniformSpecialSource specialSource,
                out EShaderVarType specialType))
        {
            if (!AreCompatible(destinationType, specialType))
            {
                return Unsupported(
                    member,
                    EVulkanAutoUniformFallbackReason.MeshStateSourceTypeMismatch,
                    $"Auto-uniform '{member.Name}' expects {destinationType}, but mesh source " +
                    $"'{specialSource}' publishes {specialType}.");
            }

            return new VulkanAutoUniformBindingOperation(
                member,
                EVulkanAutoUniformSourceKind.MeshState,
                EVulkanBindingFrequency.Object,
                default,
                specialSource,
                default,
                destinationType,
                destinationType == specialType
                    ? conversion
                    : EVulkanUniformWriteConversion.CompatibleTyped,
                EVulkanAutoUniformFallbackReason.None,
                null);
        }

        return new VulkanAutoUniformBindingOperation(
            member,
            EVulkanAutoUniformSourceKind.MaterialOrRuntime,
            EVulkanBindingFrequency.Material,
            default,
            default,
            default,
            destinationType,
            conversion,
            EVulkanAutoUniformFallbackReason.None,
            null);
    }

    /// <summary>
    /// Classifies a source declaration before SPIR-V materialization so the
    /// Vulkan source rewriter can emit physically separate frequency blocks.
    /// Runtime publishers that later change an otherwise material-owned name's
    /// frequency remain on the explicit legacy fallback.
    /// </summary>
    internal static EVulkanBindingFrequency ResolveDeclaredFrequency(string name)
    {
        string normalizedName = NormalizeName(name);
        if (TryResolveTemporalSource(normalizedName, out _))
            return EVulkanBindingFrequency.View;
        if (Enum.TryParse(
                normalizedName,
                ignoreCase: false,
                out EEngineUniform engineUniform))
        {
            return ResolveEngineFrequency(engineUniform);
        }
        if (TryResolveSpecialSource(normalizedName, out _, out _))
            return EVulkanBindingFrequency.Object;

        return EVulkanBindingFrequency.Material;
    }

    internal static bool HasExplicitDefault(in AutoUniformMember member)
        => member.DefaultValue is not null ||
           member.DefaultArrayValues is { Count: > 0 };

    private static ulong ComputePublicationLayoutSignature(
        AutoUniformBlockInfo block,
        VulkanAutoUniformBindingOperation[] operations)
    {
        FrameOpSignatureHasher hash = new();
        hash.Add(block.Size);
        hash.Add((int)block.Frequency);
        hash.Add(operations.Length);
        for (int operationIndex = 0;
             operationIndex < operations.Length;
             operationIndex++)
        {
            VulkanAutoUniformBindingOperation operation =
                operations[operationIndex];
            AddMemberSignature(ref hash, operation.Member);
            hash.Add((int)operation.SourceKind);
            hash.Add((int)operation.Frequency);
            hash.Add((int)operation.EngineUniform);
            hash.Add((int)operation.SpecialSource);
            hash.Add((int)operation.TemporalSource);
            hash.Add((int)operation.DestinationType);
            hash.Add((int)operation.Conversion);
            hash.Add((int)operation.FallbackKind);
        }

        return hash.ToHash();
    }

    private static void AddMemberSignature(
        ref FrameOpSignatureHasher hash,
        in AutoUniformMember member)
    {
        hash.Add(member.Name);
        hash.Add(member.GlslType);
        hash.Add(member.EngineType.HasValue);
        hash.Add((int)(member.EngineType ?? default));
        hash.Add(member.IsArray);
        hash.Add(member.ArrayLength);
        hash.Add(member.ArrayStride);
        hash.Add(member.Offset);
        hash.Add(member.Size);
        AddDefaultValueSignature(ref hash, member.DefaultValue);

        IReadOnlyList<AutoUniformDefaultValue>? defaultArray =
            member.DefaultArrayValues;
        hash.Add(defaultArray?.Count ?? -1);
        if (defaultArray is not null)
        {
            for (int index = 0; index < defaultArray.Count; index++)
                AddDefaultValueSignature(ref hash, defaultArray[index]);
        }

        IReadOnlyList<AutoUniformMember>? structMembers =
            member.StructMembers;
        hash.Add(structMembers?.Count ?? -1);
        if (structMembers is null)
            return;

        for (int index = 0; index < structMembers.Count; index++)
            AddMemberSignature(ref hash, structMembers[index]);
    }

    private static void AddDefaultValueSignature(
        ref FrameOpSignatureHasher hash,
        AutoUniformDefaultValue? defaultValue)
    {
        hash.Add(defaultValue.HasValue);
        if (!defaultValue.HasValue)
            return;

        AutoUniformDefaultValue value = defaultValue.Value;
        hash.Add((int)value.Type);
        hash.Add(value.Value.GetType().FullName);
        hash.Add(value.Value.GetHashCode());
    }

    private static EVulkanAutoUniformFallbackReason ValidateDestination(
        AutoUniformBlockInfo block,
        AutoUniformMember member,
        out string? reason)
    {
        if (string.IsNullOrWhiteSpace(member.Name))
        {
            reason = $"Auto-uniform block '{block.InstanceName}' contains an unnamed member.";
            return EVulkanAutoUniformFallbackReason.InvalidMemberName;
        }

        if (member.EngineType is null &&
            member.StructMembers is not { Count: > 0 })
        {
            reason = $"Auto-uniform '{member.Name}' in block '{block.InstanceName}' uses " +
                $"unsupported GLSL type '{member.GlslType}'.";
            return EVulkanAutoUniformFallbackReason.UnsupportedShaderType;
        }

        if (member.Size == 0 ||
            member.Offset > block.Size ||
            member.Size > block.Size - member.Offset)
        {
            ulong end = (ulong)member.Offset + member.Size;
            reason = $"Auto-uniform '{member.Name}' range [{member.Offset}, " +
                $"{end}) exceeds block '{block.InstanceName}' size {block.Size}.";
            return EVulkanAutoUniformFallbackReason.InvalidDestinationRange;
        }

        if (member.IsArray && (member.ArrayLength == 0 || member.ArrayStride == 0))
        {
            reason = $"Auto-uniform array '{member.Name}' has invalid length/stride " +
                $"{member.ArrayLength}/{member.ArrayStride}.";
            return EVulkanAutoUniformFallbackReason.InvalidArrayLayout;
        }

        reason = null;
        return EVulkanAutoUniformFallbackReason.None;
    }

    private static VulkanAutoUniformBindingOperation Unsupported(
        AutoUniformMember member,
        EVulkanAutoUniformFallbackReason fallbackKind,
        string reason)
        => new(
            member,
            EVulkanAutoUniformSourceKind.Unsupported,
            EVulkanBindingFrequency.Unknown,
            default,
            default,
            default,
            member.EngineType ?? default,
            EVulkanUniformWriteConversion.Unsupported,
            fallbackKind,
            reason);

    private static string NormalizeName(string name)
        => name.EndsWith(VertexUniformSuffix, StringComparison.Ordinal)
            ? name[..^VertexUniformSuffix.Length]
            : name;

    private static bool TryResolveTemporalSource(
        string name,
        out EVulkanTemporalUniformSource source)
    {
        source = name switch
        {
            "CurrViewProjection" => EVulkanTemporalUniformSource.CurrentViewProjection,
            "PrevViewProjection" => EVulkanTemporalUniformSource.PreviousViewProjection,
            "CurrViewProjectionStereo" => EVulkanTemporalUniformSource.CurrentStereoViewProjection,
            "PrevViewProjectionStereo" => EVulkanTemporalUniformSource.PreviousStereoViewProjection,
            _ => EVulkanTemporalUniformSource.None,
        };
        return source != EVulkanTemporalUniformSource.None;
    }

    private static bool TryResolveSpecialSource(
        string name,
        out EVulkanAutoUniformSpecialSource source,
        out EShaderVarType type)
    {
        (source, type) = name switch
        {
            "TransformId" => (EVulkanAutoUniformSpecialSource.TransformId, EShaderVarType._uint),
            "skinPaletteBase" => (EVulkanAutoUniformSpecialSource.SkinPaletteBase, EShaderVarType._uint),
            "skinPaletteCount" => (EVulkanAutoUniformSpecialSource.SkinPaletteCount, EShaderVarType._uint),
            "skinningInfluenceCap" => (EVulkanAutoUniformSpecialSource.SkinningInfluenceCap, EShaderVarType._int),
            "blendshapeActiveCount" => (EVulkanAutoUniformSpecialSource.BlendshapeActiveCount, EShaderVarType._int),
            "blendshapeWeightThreshold" => (EVulkanAutoUniformSpecialSource.BlendshapeWeightThreshold, EShaderVarType._float),
            "usePrecombinedBlendshapeDeltas" => (EVulkanAutoUniformSpecialSource.UsePrecombinedBlendshapeDeltas, EShaderVarType._int),
            _ => (EVulkanAutoUniformSpecialSource.None, default),
        };
        return source != EVulkanAutoUniformSpecialSource.None;
    }

    private static EVulkanBindingFrequency ResolveEngineFrequency(
        EEngineUniform uniform)
        => uniform switch
        {
            EEngineUniform.UpdateDelta or
            EEngineUniform.RenderTime or
            EEngineUniform.EngineTime or
            EEngineUniform.DeltaTime or
            EEngineUniform.ClipSpaceYDirection or
            EEngineUniform.ClipDepthRange or
            EEngineUniform.FramebufferTextureYDirection
                => EVulkanBindingFrequency.Frame,

            EEngineUniform.ViewMatrix or
            EEngineUniform.LeftEyeViewMatrix or
            EEngineUniform.RightEyeViewMatrix or
            EEngineUniform.InverseViewMatrix or
            EEngineUniform.LeftEyeInverseViewMatrix or
            EEngineUniform.RightEyeInverseViewMatrix or
            EEngineUniform.InverseProjMatrix or
            EEngineUniform.LeftEyeInverseProjMatrix or
            EEngineUniform.RightEyeInverseProjMatrix or
            EEngineUniform.ProjMatrix or
            EEngineUniform.LeftEyeProjMatrix or
            EEngineUniform.RightEyeProjMatrix or
            EEngineUniform.ViewProjectionMatrix or
            EEngineUniform.LeftEyeViewProjectionMatrix or
            EEngineUniform.RightEyeViewProjectionMatrix or
            EEngineUniform.PrevViewMatrix or
            EEngineUniform.PrevLeftEyeViewMatrix or
            EEngineUniform.PrevRightEyeViewMatrix or
            EEngineUniform.PrevProjMatrix or
            EEngineUniform.PrevLeftEyeProjMatrix or
            EEngineUniform.PrevRightEyeProjMatrix or
            EEngineUniform.CameraFovX or
            EEngineUniform.CameraFovY or
            EEngineUniform.CameraAspect or
            EEngineUniform.CameraNearZ or
            EEngineUniform.CameraFarZ or
            EEngineUniform.DepthMode or
            EEngineUniform.CameraPosition or
            EEngineUniform.CameraForward or
            EEngineUniform.CameraUp or
            EEngineUniform.CameraRight or
            EEngineUniform.VRMode
                => EVulkanBindingFrequency.View,

            EEngineUniform.ScreenWidth or
            EEngineUniform.ScreenHeight or
            EEngineUniform.ScreenOrigin
                => EVulkanBindingFrequency.Pass,

            _ => EVulkanBindingFrequency.Object,
        };

    private static EShaderVarType ResolveEngineSourceType(EEngineUniform uniform)
        => uniform switch
        {
            EEngineUniform.ModelMatrix or
            EEngineUniform.ViewMatrix or
            EEngineUniform.LeftEyeViewMatrix or
            EEngineUniform.RightEyeViewMatrix or
            EEngineUniform.InverseViewMatrix or
            EEngineUniform.LeftEyeInverseViewMatrix or
            EEngineUniform.RightEyeInverseViewMatrix or
            EEngineUniform.InverseProjMatrix or
            EEngineUniform.LeftEyeInverseProjMatrix or
            EEngineUniform.RightEyeInverseProjMatrix or
            EEngineUniform.ProjMatrix or
            EEngineUniform.LeftEyeProjMatrix or
            EEngineUniform.RightEyeProjMatrix or
            EEngineUniform.ViewProjectionMatrix or
            EEngineUniform.LeftEyeViewProjectionMatrix or
            EEngineUniform.RightEyeViewProjectionMatrix or
            EEngineUniform.PrevModelMatrix or
            EEngineUniform.PrevViewMatrix or
            EEngineUniform.PrevLeftEyeViewMatrix or
            EEngineUniform.PrevRightEyeViewMatrix or
            EEngineUniform.PrevProjMatrix or
            EEngineUniform.PrevLeftEyeProjMatrix or
            EEngineUniform.PrevRightEyeProjMatrix or
            EEngineUniform.RootInvModelMatrix
                => EShaderVarType._mat4,

            EEngineUniform.CameraPosition or
            EEngineUniform.CameraForward or
            EEngineUniform.CameraUp or
            EEngineUniform.CameraRight or
            EEngineUniform.UIXYWH
                => EShaderVarType._vec4,

            EEngineUniform.ScreenOrigin
                => EShaderVarType._vec2,

            EEngineUniform.DepthMode or
            EEngineUniform.ClipSpaceYDirection or
            EEngineUniform.ClipDepthRange or
            EEngineUniform.FramebufferTextureYDirection or
            EEngineUniform.BillboardMode or
            EEngineUniform.VRMode
                => EShaderVarType._int,

            _ => EShaderVarType._float,
        };

    private static bool AreCompatible(EShaderVarType expected, EShaderVarType actual)
        => expected == actual || (expected, actual) switch
        {
            (EShaderVarType._vec4, EShaderVarType._vec3) => true,
            (EShaderVarType._vec3, EShaderVarType._vec4) => true,
            (EShaderVarType._int, EShaderVarType._bool) => true,
            (EShaderVarType._uint, EShaderVarType._bool) => true,
            (EShaderVarType._bool, EShaderVarType._int) => true,
            (EShaderVarType._bool, EShaderVarType._uint) => true,
            _ => false,
        };
}
