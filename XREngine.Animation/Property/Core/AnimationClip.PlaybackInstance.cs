using System.Numerics;
using MemoryPack;
using XREngine.Components.Animation;

namespace XREngine.Animation;

public partial class AnimationClip
{
    /// <summary>
    /// Create an independent clip for one playback owner. Keep the source asset unchanged.
    /// </summary>
    public AnimationClip CreatePlaybackInstance()
        => CreatePlaybackInstance(new Dictionary<AnimationClip, AnimationClip>(ReferenceEqualityComparer.Instance));

    private AnimationClip CreatePlaybackInstance(Dictionary<AnimationClip, AnimationClip> instances)
    {
        if (instances.TryGetValue(this, out AnimationClip? existing))
            return existing;

        // Copy metadata without asking the legacy serializer to replace source keyframe tracks.
        AnimationClipSerializedModel metadata = new()
        {
            Name = Name,
            OriginalPath = OriginalPath,
            OriginalLastWriteTimeUtc = OriginalLastWriteTimeUtc,
            TraversalMethod = TraversalMethod,
            LengthInSeconds = LengthInSeconds,
            Looped = Looped,
            ClipKind = ClipKind,
            HasMuscleChannels = HasMuscleChannels,
            HasRootMotion = HasRootMotion,
            HasIKGoals = HasIKGoals,
            SampleRate = SampleRate,
            ImportedHumanoidRootMotionSettings = ImportedHumanoidRootMotionSettings,
            ImportedMetadata = ImportedMetadata,
            ImportedEvents = ImportedEvents,
            ImportedGenericBindings = ImportedGenericBindings,
            SourceImportManifest = SourceImportManifest,
        };
        metadata = MemoryPackSerializer.Deserialize<AnimationClipSerializedModel>(MemoryPackSerializer.Serialize(metadata))
            ?? throw new InvalidOperationException("Could not copy animation clip metadata for playback.");

        AnimationClip instance = new();
        instances.Add(this, instance);
        AnimationClipSerialization.ApplyModel(instance, metadata);
        Dictionary<BasePropAnim, BasePropAnim> copiedTracks = new(ReferenceEqualityComparer.Instance);
        instance.RootMember = CopyMember(RootMember, copiedTracks);
        instance.TotalAnimCount = AttachClip(instance.RootMember, instance);
        instance.SourceMaterialBindings = [.. SourceMaterialBindings];
        instance.MaterialBindingDiagnostics = [.. MaterialBindingDiagnostics];
        if (ImportedAdditiveReferencePoseClip is { } referenceClip)
            instance.ImportedAdditiveReferencePoseClip = referenceClip.CreatePlaybackInstance(instances);
        return instance;
    }

    private static AnimationMember? CopyMember(
        AnimationMember? source,
        Dictionary<BasePropAnim, BasePropAnim> copiedTracks)
    {
        if (source is null)
            return null;

        AnimationMember copy = new()
        {
            MemberName = source.MemberName,
            MemberType = source.MemberType,
            CacheReturnValue = source.CacheReturnValue,
            DefaultValue = source.DefaultValue,
            AnimatedMethodArgumentIndex = source.AnimatedMethodArgumentIndex,
            MethodArguments = (object?[])source.MethodArguments.Clone(),
        };
        if (source.Animation is { } animation)
        {
            if (!copiedTracks.TryGetValue(animation, out BasePropAnim? copiedTrack))
            {
                copiedTrack = CopyTrack(animation);
                copiedTracks.Add(animation, copiedTrack);
            }
            copy.Animation = copiedTrack;
        }

        foreach (AnimationMember child in source.Children)
            copy.Children.Add(CopyMember(child, copiedTracks)!);
        return copy;
    }

    private static int AttachClip(AnimationMember? member, AnimationClip clip)
    {
        if (member is null)
            return 0;
        member.ParentClip = clip;
        int count = member.Animation is null ? 0 : 1;
        foreach (AnimationMember child in member.Children)
            count += AttachClip(child, clip);
        return count;
    }

    private static BasePropAnim CopyTrack(BasePropAnim source)
    {
        Type type = source.GetType();
        if (type == typeof(PropAnimFloat))
            return CopyVector<float, FloatKeyframe, PropAnimFloat>((PropAnimFloat)source);
        if (type == typeof(PropAnimVector3))
            return CopyVector<Vector3, Vector3Keyframe, PropAnimVector3>((PropAnimVector3)source);
        if (type == typeof(PropAnimQuaternion))
            return CopyQuaternion((PropAnimQuaternion)source);
        if (type == typeof(PropAnimObject))
            return CopyObject((PropAnimObject)source);
        if (type == typeof(PropAnimBool) || type == typeof(PropAnimInt)
            || type == typeof(PropAnimVector2) || type == typeof(PropAnimVector4)
            || type == typeof(PropAnimMatrix) || type == typeof(PropAnimString)
            || type == typeof(AnimationCurve))
        {
            const string context = "playback clip";
            return PublishedAnimationPropertyCodec.CreateRuntimeAnimation(
                PublishedAnimationPropertyCodec.CreateModel(source, context), context)
                ?? throw new InvalidOperationException("Could not copy a published animation track for playback.");
        }

        return source.CreatePlaybackCopy();
    }

    private static TAnimation CopyVector<TValue, TKeyframe, TAnimation>(PropAnimVector<TValue, TKeyframe> source)
        where TValue : unmanaged
        where TKeyframe : VectorKeyframe<TValue>, new()
        where TAnimation : PropAnimVector<TValue, TKeyframe>, new()
    {
        TAnimation copy = new();
        CopyCommon(source, copy, source.Keyframes, copy.Keyframes);
        copy.DefaultValue = source.DefaultValue;
        copy.ConstrainKeyframedFPS = source.ConstrainKeyframedFPS;
        copy.LerpConstrainedFPS = source.LerpConstrainedFPS;
        copy.UseTangentRelativeSpeed = source.UseTangentRelativeSpeed;
        foreach (TKeyframe sourceKey in source.Keyframes)
        {
            TKeyframe key = new()
            {
                Second = sourceKey.Second,
                AuthoredFrameIndex = sourceKey.AuthoredFrameIndex,
            };
            key.RestorePublishedHandles(
                sourceKey.InValue, sourceKey.OutValue, sourceKey.InTangent, sourceKey.OutTangent,
                sourceKey.InterpolationTypeIn, sourceKey.InterpolationTypeOut,
                sourceKey.SyncInOutValues, sourceKey.SyncInOutTangentDirections,
                sourceKey.SyncInOutTangentMagnitudes);
            if (key is FloatKeyframe floatKey && sourceKey is FloatKeyframe sourceFloatKey)
            {
                floatKey.WeightedMode = sourceFloatKey.WeightedMode;
                floatKey.InWeight = sourceFloatKey.InWeight;
                floatKey.OutWeight = sourceFloatKey.OutWeight;
            }
            copy.Keyframes.Add(key);
        }
        if (source.IsBaked)
            copy.RestorePublishedBakedValues(source.CapturePublishedBakedValues(), source.EncodedBakedValueCompressionAlgorithm);
        CopyBakedState(source, copy);
        return copy;
    }

    private static PropAnimQuaternion CopyQuaternion(PropAnimQuaternion source)
    {
        PropAnimQuaternion copy = new();
        CopyCommon(source, copy, source.Keyframes, copy.Keyframes);
        copy.DefaultValue = source.DefaultValue;
        copy.ConstrainKeyframedFPS = source.ConstrainKeyframedFPS;
        copy.LerpConstrainedFPS = source.LerpConstrainedFPS;
        foreach (QuaternionKeyframe sourceKey in source.Keyframes)
        {
            copy.Keyframes.Add(new QuaternionKeyframe
            {
                Second = sourceKey.Second,
                AuthoredFrameIndex = sourceKey.AuthoredFrameIndex,
                InValue = sourceKey.InValue,
                OutValue = sourceKey.OutValue,
                InTangent = sourceKey.InTangent,
                OutTangent = sourceKey.OutTangent,
                InterpolationTypeIn = sourceKey.InterpolationTypeIn,
                InterpolationTypeOut = sourceKey.InterpolationTypeOut,
            });
        }
        if (source.IsBaked)
            copy.RestorePublishedBakedValues(source.CapturePublishedBakedValues(), source.EncodedBakedValueCompressionAlgorithm);
        CopyBakedState(source, copy);
        return copy;
    }

    private static PropAnimObject CopyObject(PropAnimObject source)
    {
        PropAnimObject copy = new();
        CopyCommon(source, copy, source.Keyframes, copy.Keyframes);
        copy.DefaultValue = source.DefaultValue;
        copy.DiscreteValueRounding = source.DiscreteValueRounding;
        foreach (ObjectKeyframe sourceKey in source.Keyframes)
        {
            copy.Keyframes.Add(new ObjectKeyframe
            {
                Second = sourceKey.Second,
                AuthoredFrameIndex = sourceKey.AuthoredFrameIndex,
                Value = sourceKey.Value,
            });
        }
        if (source.IsBaked)
            copy.RestorePublishedBakedValues(source.CapturePublishedBakedValues(), source.EncodedBakedValueCompressionAlgorithm);
        CopyBakedState(source, copy);
        return copy;
    }

    private static void CopyCommon(
        BasePropAnimBakeable source,
        BasePropAnimBakeable copy,
        BaseKeyframeTrack sourceTrack,
        BaseKeyframeTrack copyTrack)
    {
        if (source.AuthoredFramesPerSecond > 0 && source.AuthoredFrameCount > 0)
            copy.SetAuthoredCadence(new AuthoredCadence(source.AuthoredFrameCount, source.AuthoredFramesPerSecond), notifyChanged: false);
        copy.LengthInSeconds = source.LengthInSeconds;
        copy.Looped = source.Looped;
        copy.Speed = source.Speed;
        copyTrack.PreInfinityMode = sourceTrack.PreInfinityMode;
        copyTrack.PostInfinityMode = sourceTrack.PostInfinityMode;
    }

    private static void CopyBakedState(BasePropAnimBakeable source, BasePropAnimBakeable copy)
        => copy.RestorePublishedBakedState(
            source.BakedFramesPerSecond,
            source.BakedFrameCount,
            source.BakedValueCompressionAlgorithm,
            source.EncodedBakedValueCompressionAlgorithm,
            source.IsBaked);
}
