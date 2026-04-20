using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;
using MemoryPack;
using XREngine.Animation;
using XREngine.Core.Files;
using XREngine.Serialization;
using YamlDotNet.Core;
using YamlDotNet.Core.Events;
using YamlDotNet.Serialization;

namespace XREngine;

internal static class AnimationClipCookedBinarySerializer
{
    public static bool CanHandle(Type type)
        => type == typeof(AnimationClip);

    public static void Write(CookedBinaryWriter writer, AnimationClip clip)
        => SerializedAssetSupport.WriteModel<AnimationClip, AnimationClipSerializedModel>(writer, clip, AnimationClipSerialization.CreateModel);

    public static AnimationClip Read(CookedBinaryReader reader)
        => SerializedAssetSupport.ReadModel<AnimationClip, AnimationClipSerializedModel>(reader, static () => new AnimationClip(), AnimationClipSerialization.ApplyModel);

    public static long CalculateSize(AnimationClip clip)
        => SerializedAssetSupport.CalculateModelSize<AnimationClip, AnimationClipSerializedModel>(clip, AnimationClipSerialization.CreateModel);
}

internal static class AnimationClipMemoryPackRegistration
{
    [SuppressMessage("Usage", "CA2255:Module initializers should not be used in libraries", Justification = "AnimationClip needs formatter registration before direct MemoryPack serialization.")]
    [ModuleInitializer]
    internal static void Initialize()
        => SerializedAssetSupport.RegisterFormatter(
            new SerializedAssetSupport.CookedBinaryMemoryPackFormatter<AnimationClip>(
                payload => SerializedAssetSupport.DeserializePayload(payload, static () => new AnimationClip())));
}

[YamlTypeConverter]
public sealed class AnimationClipYamlTypeConverter : IYamlTypeConverter
{
    public bool Accepts(Type type)
        => type == typeof(AnimationClip);

    public object? ReadYaml(IParser parser, Type type, ObjectDeserializer rootDeserializer)
    {
        if (parser.TryConsume<Scalar>(out var scalar))
        {
            if (scalar.Value is null || scalar.Value == "~" || string.Equals(scalar.Value, "null", StringComparison.OrdinalIgnoreCase))
                return null;

            throw new YamlException($"Unexpected scalar while deserializing {nameof(AnimationClip)}: '{scalar.Value}'.");
        }

        AnimationClipSerializedModel? model = rootDeserializer(typeof(AnimationClipSerializedModel)) as AnimationClipSerializedModel;
        AnimationClip clip = new();
        AnimationClipSerialization.ApplyModel(clip, model);
        return clip;
    }

    public void WriteYaml(IEmitter emitter, object? value, Type type, ObjectSerializer serializer)
    {
        if (value is null)
        {
            emitter.Emit(new Scalar("~"));
            return;
        }

        if (value is not AnimationClip clip)
            throw new YamlException($"Expected {nameof(AnimationClip)} but got '{value.GetType()}'.");

        if (TryWriteAsReference.TryEmitReference(emitter, clip))
            return;

        serializer(AnimationClipSerialization.CreateModel(clip), typeof(AnimationClipSerializedModel));
    }
}

internal static class AnimationClipSerialization
{
    private static readonly PropertyInfo? XRObjectIdProperty = typeof(XREngine.Data.Core.XRObjectBase)
        .GetProperty(nameof(XREngine.Data.Core.XRObjectBase.ID), BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
    private static readonly PropertyInfo? AnimationMemberParentClipProperty = typeof(AnimationMember)
        .GetProperty(nameof(AnimationMember.ParentClip), BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);

    public static AnimationClipSerializedModel CreateModel(AnimationClip clip)
    {
        ArgumentNullException.ThrowIfNull(clip);

        return new AnimationClipSerializedModel
        {
            AssetType = clip.GetType().FullName ?? clip.GetType().Name,
            Id = clip.ID,
            Name = clip.Name,
            OriginalPath = clip.OriginalPath,
            OriginalLastWriteTimeUtc = clip.OriginalLastWriteTimeUtc,
            TraversalMethod = clip.TraversalMethod,
            LengthInSeconds = clip.LengthInSeconds,
            Looped = clip.Looped,
            ClipKind = clip.ClipKind,
            HasMuscleChannels = clip.HasMuscleChannels,
            HasRootMotion = clip.HasRootMotion,
            HasIKGoals = clip.HasIKGoals,
            SampleRate = clip.SampleRate,
            RootMember = CreateModel(clip.RootMember)
        };
    }

    public static void ApplyModel(AnimationClip clip, AnimationClipSerializedModel? model)
    {
        ArgumentNullException.ThrowIfNull(clip);

        if (model is null)
        {
            clip.RootMember = null;
            clip.TotalAnimCount = 0;
            return;
        }

        SetAssetId(clip, model.Id);
        clip.Name = model.Name;
        clip.OriginalPath = model.OriginalPath;
        clip.OriginalLastWriteTimeUtc = model.OriginalLastWriteTimeUtc;
        clip.TraversalMethod = model.TraversalMethod;
        clip.Looped = model.Looped;
        clip.LengthInSeconds = model.LengthInSeconds;
        clip.ClipKind = model.ClipKind;
        clip.HasMuscleChannels = model.HasMuscleChannels;
        clip.HasRootMotion = model.HasRootMotion;
        clip.HasIKGoals = model.HasIKGoals;
        clip.SampleRate = model.SampleRate ?? 30;

        AnimationMember? rootMember = CreateRuntimeMember(model.RootMember);
        clip.RootMember = rootMember;
        clip.TotalAnimCount = AttachClip(rootMember, clip);
    }

    private static AnimationMemberSerializedModel? CreateModel(AnimationMember? member)
    {
        if (member is null)
            return null;

        List<AnimationMemberSerializedModel> children = new(member.Children.Count);
        foreach (AnimationMember child in member.Children)
        {
            AnimationMemberSerializedModel? childModel = CreateModel(child);
            if (childModel is not null)
                children.Add(childModel);
        }

        return new AnimationMemberSerializedModel
        {
            MemberName = member.MemberName,
            MemberType = member.MemberType,
            Animation = AnimationPropertySerialization.CreateModel(member.Animation),
            MethodArguments = CreateMethodArgumentModels(member.MethodArguments),
            AnimatedMethodArgumentIndex = member.AnimatedMethodArgumentIndex,
            CacheReturnValue = member.CacheReturnValue,
            Children = children
        };
    }

    private static AnimationMember? CreateRuntimeMember(AnimationMemberSerializedModel? model)
    {
        if (model is null)
            return null;

        AnimationMember member = new()
        {
            MemberName = model.MemberName ?? string.Empty,
            MemberType = model.MemberType,
            Animation = AnimationPropertySerialization.CreateRuntimeAnimation(model.Animation),
            MethodArguments = CreateMethodArguments(model.MethodArguments),
            AnimatedMethodArgumentIndex = model.AnimatedMethodArgumentIndex,
            CacheReturnValue = model.CacheReturnValue
        };

        if (model.Children is not null)
        {
            foreach (AnimationMemberSerializedModel childModel in model.Children)
            {
                AnimationMember? child = CreateRuntimeMember(childModel);
                if (child is not null)
                    member.Children.Add(child);
            }
        }

        return member;
    }

    private static void SetAssetId(AnimationClip clip, Guid id)
    {
        if (id == Guid.Empty || XRObjectIdProperty?.SetMethod is null)
            return;

        XRObjectIdProperty.SetValue(clip, id);
    }

    private static int AttachClip(AnimationMember? member, AnimationClip clip)
    {
        if (member is null)
            return 0;

        AnimationMemberParentClipProperty?.SetValue(member, clip);

        int count = member.Animation is null ? 0 : 1;
        foreach (AnimationMember child in member.Children)
            count += AttachClip(child, clip);

        return count;
    }

    private static List<SerializedMethodArgumentModel>? CreateMethodArgumentModels(object?[]? arguments)
    {
        if (arguments is null)
            return null;

        List<SerializedMethodArgumentModel> models = new(arguments.Length);
        foreach (object? argument in arguments)
        {
            if (argument is null)
            {
                models.Add(new SerializedMethodArgumentModel());
                continue;
            }

            Type runtimeType = argument.GetType();
            models.Add(new SerializedMethodArgumentModel
            {
                TypeName = runtimeType.AssemblyQualifiedName,
                Payload = MemoryPackSerializer.Serialize(runtimeType, argument)
            });
        }

        return models;
    }

    private static object?[] CreateMethodArguments(List<SerializedMethodArgumentModel>? models)
    {
        if (models is null || models.Count == 0)
            return [null];

        object?[] arguments = new object?[models.Count];
        for (int i = 0; i < models.Count; i++)
        {
            SerializedMethodArgumentModel model = models[i];
            if (model.Payload is null || model.Payload.Length == 0 || string.IsNullOrWhiteSpace(model.TypeName))
            {
                arguments[i] = null;
                continue;
            }

            Type runtimeType = AotRuntimeMetadataStore.ResolveType(model.TypeName)
                ?? throw new InvalidOperationException($"Failed to resolve method argument type '{model.TypeName}'.");
            arguments[i] = MemoryPackSerializer.Deserialize(runtimeType, model.Payload);
        }

        return arguments;
    }
}

[MemoryPackable]
internal sealed partial class AnimationClipSerializedModel
{
    [YamlMember(Alias = "__assetType", Order = -100)]
    [MemoryPackIgnore]
    public string? AssetType { get; set; }

    public Guid Id { get; set; }

    public string? Name { get; set; }

    public string? OriginalPath { get; set; }

    public DateTime? OriginalLastWriteTimeUtc { get; set; }

    public EAnimTreeTraversalMethod TraversalMethod { get; set; }

    public float LengthInSeconds { get; set; }

    public bool Looped { get; set; }

    public EAnimationClipKind ClipKind { get; set; }

    public bool HasMuscleChannels { get; set; }

    public bool HasRootMotion { get; set; }

    public bool HasIKGoals { get; set; }

    public int? SampleRate { get; set; }

    public AnimationMemberSerializedModel? RootMember { get; set; }
}

[MemoryPackable]
internal sealed partial class AnimationMemberSerializedModel
{
    public string? MemberName { get; set; }

    public EAnimationMemberType MemberType { get; set; }

    public SerializedPropertyAnimationModel? Animation { get; set; }

    public List<SerializedMethodArgumentModel>? MethodArguments { get; set; }

    public int AnimatedMethodArgumentIndex { get; set; }

    public bool CacheReturnValue { get; set; }

    public List<AnimationMemberSerializedModel> Children { get; set; } = [];
}

[MemoryPackable]
internal sealed partial class SerializedMethodArgumentModel
{
    public string? TypeName { get; set; }

    [YamlIgnore]
    public byte[]? Payload { get; set; }

    [MemoryPackIgnore]
    [YamlMember(Alias = "Payload")]
    public string? PayloadBase64
    {
        get => Payload is null ? null : Convert.ToBase64String(Payload);
        set => Payload = string.IsNullOrEmpty(value) ? null : Convert.FromBase64String(value);
    }
}