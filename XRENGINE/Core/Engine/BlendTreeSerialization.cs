using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
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
        => SerializedAssetSupport.WriteModel<BlendTree, object>(writer, blendTree, BlendTreeSerialization.CreateModel);

    public static BlendTree Read(Type type, CookedBinaryReader reader)
    {
        ArgumentNullException.ThrowIfNull(reader);

        Type modelType = type == typeof(BlendTree1D)
            ? typeof(BlendTree1DSerializedModel)
            : type == typeof(BlendTree2D)
                ? typeof(BlendTree2DSerializedModel)
                : typeof(BlendTreeDirectSerializedModel);
        object? model = CookedBinarySerializer.ReadValue(reader, modelType, callbacks: null);

        return BlendTreeSerialization.CreateRuntimeBlendTree(type, model)
            ?? throw new InvalidOperationException($"Failed to deserialize blend tree '{type.FullName}'.");
    }

    public static long CalculateSize(BlendTree blendTree)
        => SerializedAssetSupport.CalculateModelSize<BlendTree, object>(blendTree, BlendTreeSerialization.CreateModel);
}

internal static class BlendTreeMemoryPackRegistration
{
    [SuppressMessage("Usage", "CA2255:Module initializers should not be used in libraries", Justification = "BlendTree needs formatter registration before direct MemoryPack serialization.")]
    [ModuleInitializer]
    internal static void Initialize()
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
    {
        if (type == typeof(BlendTree1D) && model is BlendTree1DSerializedModel blendTree1DModel)
            return CreateRuntimeBlendTree(blendTree1DModel);
        if (type == typeof(BlendTree2D) && model is BlendTree2DSerializedModel blendTree2DModel)
            return CreateRuntimeBlendTree(blendTree2DModel);
        if (type == typeof(BlendTreeDirect) && model is BlendTreeDirectSerializedModel blendTreeDirectModel)
            return CreateRuntimeBlendTree(blendTreeDirectModel);
        return null;
    }

    private static BlendTree1DSerializedModel CreateModel(BlendTree1D blendTree)
    {
        List<BlendTree1DChildSerializedModel> children = new(blendTree.Children.Count);
        foreach (BlendTree1D.Child child in blendTree.Children)
        {
            children.Add(new BlendTree1DChildSerializedModel
            {
                Motion = MotionSerialization.CreateModel(child.Motion),
                Speed = child.Speed,
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

    private static BlendTree2DSerializedModel CreateModel(BlendTree2D blendTree)
    {
        List<BlendTree2DChildSerializedModel> children = new(blendTree.Children.Count);
        foreach (BlendTree2D.Child child in blendTree.Children)
        {
            children.Add(new BlendTree2DChildSerializedModel
            {
                Motion = MotionSerialization.CreateModel(child.Motion),
                PositionX = child.PositionX,
                PositionY = child.PositionY,
                Speed = child.Speed,
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

    private static BlendTreeDirectSerializedModel CreateModel(BlendTreeDirect blendTree)
    {
        List<BlendTreeDirectChildSerializedModel> children = new(blendTree.Children.Count);
        foreach (BlendTreeDirect.Child child in blendTree.Children)
        {
            children.Add(new BlendTreeDirectChildSerializedModel
            {
                Motion = MotionSerialization.CreateModel(child.Motion),
                WeightParameterName = child.WeightParameterName,
                Speed = child.Speed,
                HumanoidMirror = child.HumanoidMirror
            });
        }

        return new BlendTreeDirectSerializedModel
        {
            Name = blendTree.Name,
            OriginalPath = blendTree.OriginalPath,
            OriginalLastWriteTimeUtc = blendTree.OriginalLastWriteTimeUtc,
            Children = children
        };
    }

    private static BlendTree1D CreateRuntimeBlendTree(BlendTree1DSerializedModel model)
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
                    Motion = MotionSerialization.CreateRuntimeMotion(childModel.Motion),
                    Speed = childModel.Speed ?? 1.0f,
                    Threshold = childModel.Threshold,
                    HumanoidMirror = childModel.HumanoidMirror
                });
            }
        }

        return blendTree;
    }

    private static BlendTree2D CreateRuntimeBlendTree(BlendTree2DSerializedModel model)
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
                    Motion = MotionSerialization.CreateRuntimeMotion(childModel.Motion),
                    PositionX = childModel.PositionX,
                    PositionY = childModel.PositionY,
                    Speed = childModel.Speed ?? 1.0f,
                    HumanoidMirror = childModel.HumanoidMirror
                });
            }
        }

        return blendTree;
    }

    private static BlendTreeDirect CreateRuntimeBlendTree(BlendTreeDirectSerializedModel model)
    {
        BlendTreeDirect blendTree = new()
        {
            Name = model.Name,
            OriginalPath = model.OriginalPath,
            OriginalLastWriteTimeUtc = model.OriginalLastWriteTimeUtc,
            Children = []
        };

        if (model.Children is not null)
        {
            foreach (BlendTreeDirectChildSerializedModel childModel in model.Children)
            {
                blendTree.Children.Add(new BlendTreeDirect.Child
                {
                    Motion = MotionSerialization.CreateRuntimeMotion(childModel.Motion),
                    WeightParameterName = childModel.WeightParameterName,
                    Speed = childModel.Speed ?? 1.0f,
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
    public float? Speed { get; set; }
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
    public float PositionX { get; set; }
    public float PositionY { get; set; }
    public float? Speed { get; set; }
    public bool HumanoidMirror { get; set; }
}

[MemoryPackable]
internal sealed partial class BlendTreeDirectSerializedModel
{
    public string? Name { get; set; }
    public string? OriginalPath { get; set; }
    public DateTime? OriginalLastWriteTimeUtc { get; set; }
    public List<BlendTreeDirectChildSerializedModel> Children { get; set; } = [];
}

[MemoryPackable]
internal sealed partial class BlendTreeDirectChildSerializedModel
{
    public SerializedMotionModel? Motion { get; set; }
    public string? WeightParameterName { get; set; }
    public float? Speed { get; set; }
    public bool HumanoidMirror { get; set; }
}