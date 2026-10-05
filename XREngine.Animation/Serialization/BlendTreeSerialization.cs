using System;
using System.Collections.Generic;
using MemoryPack;
using XREngine.Animation;
using XREngine.Core.Files;
using XREngine.Serialization;
using YamlDotNet.Core;
using YamlDotNet.Core.Events;
using YamlDotNet.Serialization;

namespace XREngine;

internal static class BlendTreeCookedBinarySerializer
{
    public static bool CanHandle(Type type)
        => type == typeof(BlendTree1D) || type == typeof(BlendTree2D) || type == typeof(BlendTreeDirect);

    public static void Write(CookedBinaryWriter writer, BlendTree blendTree)
    {
        switch (blendTree)
        {
            case BlendTree1D tree:
                SerializedAssetSupport.WriteModel<BlendTree1D, BlendTree1DSerializedModel>(writer, tree, BlendTreeSerialization.CreateModel);
                break;
            case BlendTree2D tree:
                SerializedAssetSupport.WriteModel<BlendTree2D, BlendTree2DSerializedModel>(writer, tree, BlendTreeSerialization.CreateModel);
                break;
            case BlendTreeDirect tree:
                SerializedAssetSupport.WriteModel<BlendTreeDirect, BlendTreeDirectSerializedModel>(writer, tree, BlendTreeSerialization.CreateModel);
                break;
            default:
                throw new NotSupportedException($"Unsupported blend tree type '{blendTree.GetType().FullName}'.");
        }
    }

    public static BlendTree Read(Type type, CookedBinaryReader reader)
    {
        ArgumentNullException.ThrowIfNull(reader);

        object? model = type == typeof(BlendTree1D)
            ? CookedBinarySerializer.ReadTypedMemoryPackModel<BlendTree1DSerializedModel>(reader)
            : type == typeof(BlendTree2D)
                ? CookedBinarySerializer.ReadTypedMemoryPackModel<BlendTree2DSerializedModel>(reader)
                : type == typeof(BlendTreeDirect)
                    ? CookedBinarySerializer.ReadTypedMemoryPackModel<BlendTreeDirectSerializedModel>(reader)
                    : throw new NotSupportedException($"Unsupported blend tree type '{type.FullName}'.");

        return BlendTreeSerialization.CreateRuntimeBlendTree(type, model)
            ?? throw new InvalidOperationException($"Failed to deserialize blend tree '{type.FullName}'.");
    }

    public static long CalculateSize(BlendTree blendTree)
        => blendTree switch
        {
            BlendTree1D tree => SerializedAssetSupport.CalculateModelSize<BlendTree1D, BlendTree1DSerializedModel>(tree, BlendTreeSerialization.CreateModel),
            BlendTree2D tree => SerializedAssetSupport.CalculateModelSize<BlendTree2D, BlendTree2DSerializedModel>(tree, BlendTreeSerialization.CreateModel),
            BlendTreeDirect tree => SerializedAssetSupport.CalculateModelSize<BlendTreeDirect, BlendTreeDirectSerializedModel>(tree, BlendTreeSerialization.CreateModel),
            _ => throw new NotSupportedException($"Unsupported blend tree type '{blendTree.GetType().FullName}'.")
        };
}

internal static class BlendTreeMemoryPackRegistration
{
    internal static void EnsureRegistered()
    {
        SerializedAssetSupport.RegisterFormatter(
            new SerializedAssetSupport.CookedBinaryMemoryPackFormatter<BlendTree1D>(payload => SerializedAssetSupport.DeserializePayload<BlendTree1D>(payload)));
        SerializedAssetSupport.RegisterFormatter(
            new SerializedAssetSupport.CookedBinaryMemoryPackFormatter<BlendTree2D>(payload => SerializedAssetSupport.DeserializePayload<BlendTree2D>(payload)));
        SerializedAssetSupport.RegisterFormatter(
            new SerializedAssetSupport.CookedBinaryMemoryPackFormatter<BlendTreeDirect>(payload => SerializedAssetSupport.DeserializePayload<BlendTreeDirect>(payload)));
    }
}

[YamlTypeConverter]
public sealed class BlendTreeYamlTypeConverter : IYamlTypeConverter
{
    public bool Accepts(Type type)
        => BlendTreeCookedBinarySerializer.CanHandle(type);

    public object? ReadYaml(IParser parser, Type type, ObjectDeserializer rootDeserializer)
    {
        if (parser.TryConsume<Scalar>(out var scalar))
        {
            if (scalar.Value is null || scalar.Value == "~" || string.Equals(scalar.Value, "null", StringComparison.OrdinalIgnoreCase))
                return null;

            throw new YamlException($"Unexpected scalar while deserializing '{type.Name}': '{scalar.Value}'.");
        }

        object? model = rootDeserializer(type == typeof(BlendTree1D)
            ? typeof(BlendTree1DSerializedModel)
            : type == typeof(BlendTree2D)
                ? typeof(BlendTree2DSerializedModel)
                : typeof(BlendTreeDirectSerializedModel));
        return BlendTreeSerialization.CreateRuntimeBlendTree(type, model);
    }

    public void WriteYaml(IEmitter emitter, object? value, Type type, ObjectSerializer serializer)
    {
        if (value is null)
        {
            emitter.Emit(new Scalar("~"));
            return;
        }

        if (value is not BlendTree blendTree)
            throw new YamlException($"Expected BlendTree but got '{value.GetType()}'.");

        object model = BlendTreeSerialization.CreateModel(blendTree);
        serializer(model, model.GetType());
    }
}

internal static class BlendTreeSerialization
{
    public static object CreateModel(BlendTree blendTree)
        => blendTree switch
        {
            BlendTree1D blendTree1D => CreateModel(blendTree1D),
            BlendTree2D blendTree2D => CreateModel(blendTree2D),
            BlendTreeDirect blendTreeDirect => CreateModel(blendTreeDirect),
            _ => throw new NotSupportedException($"Unsupported blend tree type '{blendTree.GetType().FullName}'.")
        };

    public static BlendTree? CreateRuntimeBlendTree(Type type, object? model)
        => CreateRuntimeBlendTree(type, model, published: false);

    public static BlendTree CreatePublishedRuntimeBlendTree(Type type, object? model)
        => model is null
            ? throw new InvalidDataException($"Published blend tree '{type.FullName}' has an empty model.")
            : CreateRuntimeBlendTree(type, model, published: true)
                ?? throw new InvalidDataException($"Published blend tree '{type.FullName}' has an invalid model.");

    private static BlendTree? CreateRuntimeBlendTree(Type type, object? model, bool published)
    {
        if (type == typeof(BlendTree1D) && model is BlendTree1DSerializedModel blendTree1DModel)
            return CreateRuntimeBlendTree(blendTree1DModel, published);
        if (type == typeof(BlendTree2D) && model is BlendTree2DSerializedModel blendTree2DModel)
            return CreateRuntimeBlendTree(blendTree2DModel, published);
        if (type == typeof(BlendTreeDirect) && model is BlendTreeDirectSerializedModel blendTreeDirectModel)
            return CreateRuntimeBlendTree(blendTreeDirectModel, published);
        return null;
    }

    internal static BlendTree1DSerializedModel CreateModel(BlendTree1D blendTree)
        => CreateModel(blendTree, published: false);

    internal static BlendTree1DSerializedModel CreatePublishedModel(BlendTree1D blendTree)
        => CreateModel(blendTree, published: true);

    private static BlendTree1DSerializedModel CreateModel(BlendTree1D blendTree, bool published)
    {
        List<BlendTree1DChildSerializedModel> children = new(blendTree.Children.Count);
        foreach (BlendTree1D.Child child in blendTree.Children)
        {
            children.Add(new BlendTree1DChildSerializedModel
            {
                Motion = published ? MotionSerialization.CreatePublishedModel(child.Motion) : MotionSerialization.CreateModel(child.Motion),
                MotionOccurrenceId = child.MotionOccurrenceId,
                Speed = child.Speed,
                CycleOffset = child.CycleOffset,
                Threshold = child.Threshold,
                HumanoidMirror = child.HumanoidMirror
            });
        }

        return new BlendTree1DSerializedModel
        {
            Name = blendTree.Name,
            OriginalPath = blendTree.OriginalPath,
            OriginalLastWriteTimeUtc = blendTree.OriginalLastWriteTimeUtc,
            ParameterName = blendTree.ParameterName,
            Children = children
        };
    }

    internal static BlendTree2DSerializedModel CreateModel(BlendTree2D blendTree)
        => CreateModel(blendTree, published: false);

    internal static BlendTree2DSerializedModel CreatePublishedModel(BlendTree2D blendTree)
        => CreateModel(blendTree, published: true);

    private static BlendTree2DSerializedModel CreateModel(BlendTree2D blendTree, bool published)
    {
        List<BlendTree2DChildSerializedModel> children = new(blendTree.Children.Count);
        foreach (BlendTree2D.Child child in blendTree.Children)
        {
            children.Add(new BlendTree2DChildSerializedModel
            {
                Motion = published ? MotionSerialization.CreatePublishedModel(child.Motion) : MotionSerialization.CreateModel(child.Motion),
                MotionOccurrenceId = child.MotionOccurrenceId,
                PositionX = child.PositionX,
                PositionY = child.PositionY,
                Speed = child.Speed,
                CycleOffset = child.CycleOffset,
                HumanoidMirror = child.HumanoidMirror
            });
        }

        return new BlendTree2DSerializedModel
        {
            Name = blendTree.Name,
            OriginalPath = blendTree.OriginalPath,
            OriginalLastWriteTimeUtc = blendTree.OriginalLastWriteTimeUtc,
            XParameterName = blendTree.XParameterName,
            YParameterName = blendTree.YParameterName,
            BlendType = blendTree.BlendType,
            Children = children
        };
    }

    internal static BlendTreeDirectSerializedModel CreateModel(BlendTreeDirect blendTree)
        => CreateModel(blendTree, published: false);

    internal static BlendTreeDirectSerializedModel CreatePublishedModel(BlendTreeDirect blendTree)
        => CreateModel(blendTree, published: true);

    private static BlendTreeDirectSerializedModel CreateModel(BlendTreeDirect blendTree, bool published)
    {
        List<BlendTreeDirectChildSerializedModel> children = new(blendTree.Children.Count);
        foreach (BlendTreeDirect.Child child in blendTree.Children)
        {
            children.Add(new BlendTreeDirectChildSerializedModel
            {
                Motion = published ? MotionSerialization.CreatePublishedModel(child.Motion) : MotionSerialization.CreateModel(child.Motion),
                MotionOccurrenceId = child.MotionOccurrenceId,
                WeightParameterName = child.WeightParameterName,
                Speed = child.Speed,
                CycleOffset = child.CycleOffset,
                HumanoidMirror = child.HumanoidMirror
            });
        }

        return new BlendTreeDirectSerializedModel
        {
            Name = blendTree.Name,
            OriginalPath = blendTree.OriginalPath,
            OriginalLastWriteTimeUtc = blendTree.OriginalLastWriteTimeUtc,
            NormalizeBlendValues = blendTree.NormalizeBlendValues,
            Children = children
        };
    }

    private static BlendTree1D CreateRuntimeBlendTree(BlendTree1DSerializedModel model, bool published)
    {
        BlendTree1D blendTree = new()
        {
            Name = model.Name,
            OriginalPath = model.OriginalPath,
            OriginalLastWriteTimeUtc = model.OriginalLastWriteTimeUtc,
            ParameterName = model.ParameterName ?? string.Empty,
            Children = []
        };

        if (model.Children is not null)
        {
            foreach (BlendTree1DChildSerializedModel childModel in model.Children)
            {
                blendTree.Children.Add(new BlendTree1D.Child
                {
                    Motion = published
                        ? MotionSerialization.CreatePublishedRuntimeMotion(childModel.Motion)
                        : MotionSerialization.CreateRuntimeMotion(childModel.Motion),
                    MotionOccurrenceId = childModel.MotionOccurrenceId ?? Guid.NewGuid(),
                    Speed = childModel.Speed ?? 1.0f,
                    CycleOffset = childModel.CycleOffset ?? 0.0f,
                    Threshold = childModel.Threshold,
                    HumanoidMirror = childModel.HumanoidMirror
                });
            }
        }

        return blendTree;
    }

    private static BlendTree2D CreateRuntimeBlendTree(BlendTree2DSerializedModel model, bool published)
    {
        BlendTree2D blendTree = new()
        {
            Name = model.Name,
            OriginalPath = model.OriginalPath,
            OriginalLastWriteTimeUtc = model.OriginalLastWriteTimeUtc,
            XParameterName = model.XParameterName ?? string.Empty,
            YParameterName = model.YParameterName ?? string.Empty,
            BlendType = model.BlendType,
            Children = []
        };

        if (model.Children is not null)
        {
            foreach (BlendTree2DChildSerializedModel childModel in model.Children)
            {
                blendTree.Children.Add(new BlendTree2D.Child
                {
                    Motion = published
                        ? MotionSerialization.CreatePublishedRuntimeMotion(childModel.Motion)
                        : MotionSerialization.CreateRuntimeMotion(childModel.Motion),
                    MotionOccurrenceId = childModel.MotionOccurrenceId ?? Guid.NewGuid(),
                    PositionX = childModel.PositionX,
                    PositionY = childModel.PositionY,
                    Speed = childModel.Speed ?? 1.0f,
                    CycleOffset = childModel.CycleOffset ?? 0.0f,
                    HumanoidMirror = childModel.HumanoidMirror
                });
            }
        }

        return blendTree;
    }

    private static BlendTreeDirect CreateRuntimeBlendTree(BlendTreeDirectSerializedModel model, bool published)
    {
        BlendTreeDirect blendTree = new()
        {
            Name = model.Name,
            OriginalPath = model.OriginalPath,
            OriginalLastWriteTimeUtc = model.OriginalLastWriteTimeUtc,
            NormalizeBlendValues = model.NormalizeBlendValues ?? false,
            Children = []
        };

        if (model.Children is not null)
        {
            foreach (BlendTreeDirectChildSerializedModel childModel in model.Children)
            {
                blendTree.Children.Add(new BlendTreeDirect.Child
                {
                    Motion = published
                        ? MotionSerialization.CreatePublishedRuntimeMotion(childModel.Motion)
                        : MotionSerialization.CreateRuntimeMotion(childModel.Motion),
                    MotionOccurrenceId = childModel.MotionOccurrenceId ?? Guid.NewGuid(),
                    WeightParameterName = childModel.WeightParameterName,
                    Speed = childModel.Speed ?? 1.0f,
                    CycleOffset = childModel.CycleOffset ?? 0.0f,
                    HumanoidMirror = childModel.HumanoidMirror
                });
            }
        }

        return blendTree;
    }
}

[MemoryPackable]
internal sealed partial class BlendTree1DSerializedModel
{
    public string? Name { get; set; }
    public string? OriginalPath { get; set; }
    public DateTime? OriginalLastWriteTimeUtc { get; set; }
    public string? ParameterName { get; set; }
    public List<BlendTree1DChildSerializedModel> Children { get; set; } = [];
}

[MemoryPackable]
internal sealed partial class BlendTree1DChildSerializedModel
{
    public SerializedMotionModel? Motion { get; set; }
    public Guid? MotionOccurrenceId { get; set; }
    public float? Speed { get; set; }
    public float? CycleOffset { get; set; }
    public float Threshold { get; set; }
    public bool HumanoidMirror { get; set; }
}

[MemoryPackable]
internal sealed partial class BlendTree2DSerializedModel
{
    public string? Name { get; set; }
    public string? OriginalPath { get; set; }
    public DateTime? OriginalLastWriteTimeUtc { get; set; }
    public string? XParameterName { get; set; }
    public string? YParameterName { get; set; }
    public BlendTree2D.EBlendType BlendType { get; set; }
    public List<BlendTree2DChildSerializedModel> Children { get; set; } = [];
}

[MemoryPackable]
internal sealed partial class BlendTree2DChildSerializedModel
{
    public SerializedMotionModel? Motion { get; set; }
    public Guid? MotionOccurrenceId { get; set; }
    public float PositionX { get; set; }
    public float PositionY { get; set; }
    public float? Speed { get; set; }
    public float? CycleOffset { get; set; }
    public bool HumanoidMirror { get; set; }
}

[MemoryPackable]
internal sealed partial class BlendTreeDirectSerializedModel
{
    public string? Name { get; set; }
    public string? OriginalPath { get; set; }
    public DateTime? OriginalLastWriteTimeUtc { get; set; }
    public bool? NormalizeBlendValues { get; set; }
    public List<BlendTreeDirectChildSerializedModel> Children { get; set; } = [];
}

[MemoryPackable]
internal sealed partial class BlendTreeDirectChildSerializedModel
{
    public SerializedMotionModel? Motion { get; set; }
    public Guid? MotionOccurrenceId { get; set; }
    public string? WeightParameterName { get; set; }
    public float? Speed { get; set; }
    public float? CycleOffset { get; set; }
    public bool HumanoidMirror { get; set; }
}
