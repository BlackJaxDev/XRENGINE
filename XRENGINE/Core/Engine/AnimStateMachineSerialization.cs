using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Numerics;
using System.Reflection;
using System.Runtime.CompilerServices;
using MemoryPack;
using XREngine.Animation;
using XREngine.Core.Files;
using XREngine.Data.Core;
using XREngine.Serialization;
using YamlDotNet.Core;
using YamlDotNet.Core.Events;
using YamlDotNet.Serialization;

namespace XREngine;

internal static class AnimStateMachineCookedBinarySerializer
{
    public static bool CanHandle(Type type)
        => type == typeof(AnimStateMachine);

    public static void Write(CookedBinaryWriter writer, AnimStateMachine stateMachine)
    {
        ArgumentNullException.ThrowIfNull(writer);
        ArgumentNullException.ThrowIfNull(stateMachine);
        writer.WriteValue(AnimStateMachineSerialization.CreateModel(stateMachine));
    }

    public static AnimStateMachine Read(CookedBinaryReader reader)
    {
        ArgumentNullException.ThrowIfNull(reader);

        AnimStateMachineSerializedModel? model = reader.ReadValue<AnimStateMachineSerializedModel>();
        AnimStateMachine stateMachine = new();
        AnimStateMachineSerialization.ApplyModel(stateMachine, model);
        return stateMachine;
    }

    public static long CalculateSize(AnimStateMachine stateMachine)
    {
        ArgumentNullException.ThrowIfNull(stateMachine);
        return CookedBinarySerializer.CalculateSize(AnimStateMachineSerialization.CreateModel(stateMachine));
    }
}

internal static class AnimStateMachineMemoryPackRegistration
{
    [SuppressMessage("Usage", "CA2255:Module initializers should not be used in libraries", Justification = "AnimStateMachine needs formatter registration before direct MemoryPack serialization.")]
    [ModuleInitializer]
    internal static void Initialize()
    {
        if (!MemoryPackFormatterProvider.IsRegistered<AnimStateMachine>())
            MemoryPackFormatterProvider.Register(new AnimStateMachineMemoryPackFormatter());
    }

    private sealed class AnimStateMachineMemoryPackFormatter : MemoryPackFormatter<AnimStateMachine>
    {
        public override void Serialize<TBufferWriter>(ref MemoryPackWriter<TBufferWriter> writer, scoped ref AnimStateMachine? value)
        {
            if (value is null)
            {
                writer.WriteNullObjectHeader();
                return;
            }

            writer.WriteObjectHeader(1);
            AnimStateMachine stateMachine = value;
            byte[] payload = CookedBinarySerializer.ExecuteWithMemoryPackSuppressed(() => CookedBinarySerializer.Serialize(stateMachine));
            writer.WriteUnmanagedArray(payload);
        }

        public override void Deserialize(ref MemoryPackReader reader, scoped ref AnimStateMachine? value)
        {
            if (!reader.TryReadObjectHeader(out byte count))
            {
                value = null;
                return;
            }

            if (count != 1)
                MemoryPackSerializationException.ThrowInvalidPropertyCount(1, count);

            byte[]? payload = reader.ReadUnmanagedArray<byte>();
            if (payload is null || payload.Length == 0)
            {
                value = new AnimStateMachine();
                return;
            }

            AnimStateMachine? stateMachine = CookedBinarySerializer.ExecuteWithMemoryPackSuppressed(
                () => CookedBinarySerializer.Deserialize(typeof(AnimStateMachine), payload) as AnimStateMachine);

            value = stateMachine ?? new AnimStateMachine();
        }
    }
}

[YamlTypeConverter]
public sealed class AnimStateMachineYamlTypeConverter : IYamlTypeConverter
{
    public bool Accepts(Type type)
        => type == typeof(AnimStateMachine);

    public object? ReadYaml(IParser parser, Type type, ObjectDeserializer rootDeserializer)
    {
        if (parser.TryConsume<Scalar>(out var scalar))
        {
            if (scalar.Value is null || scalar.Value == "~" || string.Equals(scalar.Value, "null", StringComparison.OrdinalIgnoreCase))
                return null;

            throw new YamlException($"Unexpected scalar while deserializing {nameof(AnimStateMachine)}: '{scalar.Value}'.");
        }

        AnimStateMachineSerializedModel? model = rootDeserializer(typeof(AnimStateMachineSerializedModel)) as AnimStateMachineSerializedModel;
        AnimStateMachine stateMachine = new();
        AnimStateMachineSerialization.ApplyModel(stateMachine, model);
        return stateMachine;
    }

    public void WriteYaml(IEmitter emitter, object? value, Type type, ObjectSerializer serializer)
    {
        if (value is null)
        {
            emitter.Emit(new Scalar("~"));
            return;
        }

        if (value is not AnimStateMachine stateMachine)
            throw new YamlException($"Expected {nameof(AnimStateMachine)} but got '{value.GetType()}'.");

        serializer(AnimStateMachineSerialization.CreateModel(stateMachine), typeof(AnimStateMachineSerializedModel));
    }
}

internal static class AnimStateMachineSerialization
{
    private static readonly PropertyInfo? XRObjectIdProperty = typeof(XRObjectBase)
        .GetProperty(nameof(XRObjectBase.ID), BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);

    public static AnimStateMachineSerializedModel CreateModel(AnimStateMachine stateMachine)
    {
        ArgumentNullException.ThrowIfNull(stateMachine);

        List<AnimLayerSerializedModel> layers = new(stateMachine.Layers.Count);
        foreach (AnimLayer layer in stateMachine.Layers)
            layers.Add(CreateModel(layer));

        List<AnimVarSerializedModel> variables = new(stateMachine.Variables.Count);
        foreach (KeyValuePair<string, AnimVar> kvp in stateMachine.Variables)
            variables.Add(CreateModel(kvp.Value));

        return new AnimStateMachineSerializedModel
        {
            AssetType = stateMachine.GetType().FullName ?? stateMachine.GetType().Name,
            Id = stateMachine.ID,
            Name = stateMachine.Name,
            OriginalPath = stateMachine.OriginalPath,
            OriginalLastWriteTimeUtc = stateMachine.OriginalLastWriteTimeUtc,
            AnimatePhysics = stateMachine.AnimatePhysics,
            Variables = variables,
            Layers = layers
        };
    }

    public static void ApplyModel(AnimStateMachine stateMachine, AnimStateMachineSerializedModel? model)
    {
        ArgumentNullException.ThrowIfNull(stateMachine);

        if (model is null)
        {
            stateMachine.Layers = [];
            stateMachine.Variables = [];
            return;
        }

        SetAssetId(stateMachine, model.Id);
        stateMachine.Name = model.Name;
        stateMachine.OriginalPath = model.OriginalPath;
        stateMachine.OriginalLastWriteTimeUtc = model.OriginalLastWriteTimeUtc;
        stateMachine.AnimatePhysics = model.AnimatePhysics;

        EventDictionary<string, AnimVar> variables = [];
        if (model.Variables is not null)
        {
            foreach (AnimVarSerializedModel variableModel in model.Variables)
            {
                AnimVar variable = CreateRuntimeVariable(variableModel);
                variables[variable.ParameterName] = variable;
            }
        }
        stateMachine.Variables = variables;

        List<AnimLayer> layers = new(model.Layers?.Count ?? 0);
        if (model.Layers is not null)
        {
            foreach (AnimLayerSerializedModel layerModel in model.Layers)
                layers.Add(CreateRuntimeLayer(layerModel));
        }
        stateMachine.Layers = [.. layers];
    }

    private static AnimLayerSerializedModel CreateModel(AnimLayer layer)
    {
        Dictionary<AnimState, int> stateIndices = new(ReferenceEqualityComparer.Instance);
        for (int i = 0; i < layer.States.Count; i++)
            stateIndices[layer.States[i]] = i;

        List<AnimStateSerializedModel> states = new(layer.States.Count);
        for (int i = 0; i < layer.States.Count; i++)
            states.Add(CreateModel(layer.States[i], stateIndices));

        return new AnimLayerSerializedModel
        {
            ApplyType = layer.ApplyType,
            Weight = layer.Weight,
            InitialStateIndex = layer.InitialStateIndex,
            AnyStatePosition = layer.AnyState.Position,
            AnyStateTransitions = CreateTransitionModels(layer.AnyState.Transitions, stateIndices),
            States = states
        };
    }

    private static AnimStateSerializedModel CreateModel(AnimState state, Dictionary<AnimState, int> stateIndices)
    {
        List<AnimStateComponentSerializedModel> components = new(state.Components.Count);
        foreach (AnimStateComponent component in state.Components)
            components.Add(CreateModel(component));

        return new AnimStateSerializedModel
        {
            Name = state.Name,
            Position = state.Position,
            Motion = MotionSerialization.CreateModel(state.Motion),
            StartSecond = state.StartSecond,
            EndSecond = state.EndSecond,
            Components = components,
            Transitions = CreateTransitionModels(state.Transitions, stateIndices)
        };
    }

    private static List<AnimStateTransitionSerializedModel> CreateTransitionModels(IEnumerable<AnimStateTransition> transitions, Dictionary<AnimState, int> stateIndices)
    {
        List<AnimStateTransitionSerializedModel> models = [];
        foreach (AnimStateTransition transition in transitions)
        {
            if (transition.DestinationState is null || !stateIndices.TryGetValue(transition.DestinationState, out int destinationStateIndex))
                throw new InvalidOperationException("Animation state transition destination must belong to its owning layer.");

            models.Add(new AnimStateTransitionSerializedModel
            {
                DestinationStateIndex = destinationStateIndex,
                Conditions = [.. transition.Conditions],
                BlendDuration = transition.BlendDuration,
                BlendType = transition.BlendType,
                CustomBlendFunction = AnimationPropertySerialization.CreateModel(transition.CustomBlendFunction),
                Priority = transition.Priority,
                Name = transition.Name,
                ExitTime = transition.ExitTime,
                FixedDuration = transition.FixedDuration,
                TransitionOffset = transition.TransitionOffset,
                InterruptionSource = transition.InterruptionSource,
                OrderedInterruption = transition.OrderedInterruption,
                CanTransitionToSelf = transition.CanTransitionToSelf
            });
        }

        return models;
    }

    private static AnimStateComponentSerializedModel CreateModel(AnimStateComponent component)
        => component switch
        {
            AnimParameterDriverComponent parameterDriver => new AnimStateComponentSerializedModel
            {
                Kind = AnimStateComponentKind.AnimParameterDriver,
                TypeName = parameterDriver.GetType().FullName,
                ExecuteLocally = parameterDriver.ExecuteLocally,
                ExecuteRemotely = parameterDriver.ExecuteRemotely,
                DstParameterName = parameterDriver.DstParameterName,
                SrcParameterName = parameterDriver.SrcParameterName,
                Operation = parameterDriver.Operation,
                ConstantValue = parameterDriver.ConstantValue,
                RandomMin = parameterDriver.RandomMin,
                RandomMax = parameterDriver.RandomMax
            },
            TrackingControllerComponent trackingController => new AnimStateComponentSerializedModel
            {
                Kind = AnimStateComponentKind.TrackingController,
                TypeName = trackingController.GetType().FullName,
                TrackingModeHead = trackingController.TrackingModeHead,
                TrackingModeLeftHand = trackingController.TrackingModeLeftHand,
                TrackingModeRightHand = trackingController.TrackingModeRightHand,
                TrackingModeLeftFoot = trackingController.TrackingModeLeftFoot,
                TrackingModeRightFoot = trackingController.TrackingModeRightFoot,
                TrackingModeLeftFingers = trackingController.TrackingModeLeftFingers,
                TrackingModeRightFingers = trackingController.TrackingModeRightFingers,
                TrackingModeEyes = trackingController.TrackingModeEyes,
                TrackingModeMouth = trackingController.TrackingModeMouth
            },
            _ => throw new NotSupportedException($"Unsupported animation state component type '{component.GetType().FullName}'.")
        };

    private static AnimVarSerializedModel CreateModel(AnimVar variable)
        => variable switch
        {
            AnimBool animBool => new AnimVarSerializedModel
            {
                Kind = AnimVarKind.Bool,
                Name = animBool.ParameterName,
                BoolValue = animBool.Value
            },
            AnimInt animInt => new AnimVarSerializedModel
            {
                Kind = AnimVarKind.Int,
                Name = animInt.ParameterName,
                IntValue = animInt.Value,
                NegativeAllowed = animInt.NegativeAllowed
            },
            AnimFloat animFloat => new AnimVarSerializedModel
            {
                Kind = AnimVarKind.Float,
                Name = animFloat.ParameterName,
                FloatValue = animFloat.Value,
                Smoothing = animFloat.Smoothing,
                CompressedBitCount = animFloat.CompressedBitCount
            },
            _ => throw new NotSupportedException($"Unsupported animation variable type '{variable.GetType().FullName}'.")
        };

    private static AnimLayer CreateRuntimeLayer(AnimLayerSerializedModel model)
    {
        AnimLayer layer = new()
        {
            ApplyType = model.ApplyType,
            Weight = model.Weight ?? 1.0f,
            InitialStateIndex = model.InitialStateIndex ?? -1
        };

        List<AnimState> states = new(model.States?.Count ?? 0);
        if (model.States is not null)
        {
            foreach (AnimStateSerializedModel stateModel in model.States)
            {
                AnimState state = new()
                {
                    Name = stateModel.Name ?? string.Empty,
                    Position = stateModel.Position,
                    Motion = MotionSerialization.CreateRuntimeMotion(stateModel.Motion),
                    StartSecond = stateModel.StartSecond,
                    EndSecond = stateModel.EndSecond,
                    Components = CreateRuntimeComponents(stateModel.Components)
                };
                states.Add(state);
            }
        }

        layer.States = [.. states];
        layer.AnyState.Position = model.AnyStatePosition;

        if (model.States is not null)
        {
            for (int i = 0; i < model.States.Count && i < states.Count; i++)
                states[i].Transitions = [.. CreateRuntimeTransitions(model.States[i].Transitions, states)];
        }

        layer.AnyState.Transitions = [.. CreateRuntimeTransitions(model.AnyStateTransitions, states)];
        return layer;
    }

    private static List<AnimStateTransition> CreateRuntimeTransitions(List<AnimStateTransitionSerializedModel>? models, List<AnimState> states)
    {
        List<AnimStateTransition> transitions = new(models?.Count ?? 0);
        if (models is null)
            return transitions;

        foreach (AnimStateTransitionSerializedModel model in models)
        {
            if (model.DestinationStateIndex < 0 || model.DestinationStateIndex >= states.Count)
                throw new InvalidOperationException($"Invalid animation state destination index '{model.DestinationStateIndex}'.");

            AnimStateTransition transition = new()
            {
                DestinationState = states[model.DestinationStateIndex],
                Conditions = model.Conditions is null ? [] : [.. model.Conditions],
                BlendDuration = model.BlendDuration,
                BlendType = model.BlendType,
                CustomBlendFunction = AnimationPropertySerialization.CreateRuntimeAnimation(model.CustomBlendFunction) as PropAnimFloat,
                Priority = model.Priority,
                Name = model.Name ?? string.Empty,
                ExitTime = model.ExitTime,
                FixedDuration = model.FixedDuration ?? true,
                TransitionOffset = model.TransitionOffset,
                InterruptionSource = model.InterruptionSource,
                OrderedInterruption = model.OrderedInterruption ?? true,
                CanTransitionToSelf = model.CanTransitionToSelf
            };
            transitions.Add(transition);
        }

        return transitions;
    }

    private static List<AnimStateComponent> CreateRuntimeComponents(List<AnimStateComponentSerializedModel>? models)
    {
        List<AnimStateComponent> components = new(models?.Count ?? 0);
        if (models is null)
            return components;

        foreach (AnimStateComponentSerializedModel model in models)
        {
            components.Add(model.Kind switch
            {
                AnimStateComponentKind.AnimParameterDriver => new AnimParameterDriverComponent
                {
                    ExecuteLocally = model.ExecuteLocally ?? true,
                    ExecuteRemotely = model.ExecuteRemotely ?? true,
                    DstParameterName = model.DstParameterName ?? string.Empty,
                    SrcParameterName = model.SrcParameterName ?? string.Empty,
                    Operation = model.Operation,
                    ConstantValue = model.ConstantValue,
                    RandomMin = model.RandomMin,
                    RandomMax = model.RandomMax
                },
                AnimStateComponentKind.TrackingController => new TrackingControllerComponent
                {
                    TrackingModeHead = model.TrackingModeHead,
                    TrackingModeLeftHand = model.TrackingModeLeftHand,
                    TrackingModeRightHand = model.TrackingModeRightHand,
                    TrackingModeLeftFoot = model.TrackingModeLeftFoot,
                    TrackingModeRightFoot = model.TrackingModeRightFoot,
                    TrackingModeLeftFingers = model.TrackingModeLeftFingers,
                    TrackingModeRightFingers = model.TrackingModeRightFingers,
                    TrackingModeEyes = model.TrackingModeEyes,
                    TrackingModeMouth = model.TrackingModeMouth
                },
                _ => throw new NotSupportedException($"Unsupported animation state component kind '{model.Kind}' ({model.TypeName ?? "unknown"}).")
            });
        }

        return components;
    }

    private static AnimVar CreateRuntimeVariable(AnimVarSerializedModel model)
        => model.Kind switch
        {
            AnimVarKind.Bool => new AnimBool(model.Name ?? string.Empty, model.BoolValue),
            AnimVarKind.Int => new AnimInt(model.Name ?? string.Empty, model.IntValue)
            {
                NegativeAllowed = model.NegativeAllowed
            },
            AnimVarKind.Float => new AnimFloat(model.Name ?? string.Empty, model.FloatValue)
            {
                Smoothing = model.Smoothing,
                CompressedBitCount = model.CompressedBitCount ?? 8
            },
            _ => throw new NotSupportedException($"Unsupported animation variable kind '{model.Kind}'.")
        };

    private static void SetAssetId(AnimStateMachine stateMachine, Guid id)
    {
        if (id == Guid.Empty || XRObjectIdProperty?.SetMethod is null)
            return;

        XRObjectIdProperty.SetValue(stateMachine, id);
    }
}

internal enum AnimStateComponentKind : byte
{
    AnimParameterDriver = 1,
    TrackingController = 2,
}

internal enum AnimVarKind : byte
{
    Bool = 1,
    Int = 2,
    Float = 3,
}

internal sealed class AnimStateMachineSerializedModel
{
    [YamlMember(Alias = "__assetType", Order = -100)]
    [MemoryPackIgnore]
    public string? AssetType { get; set; }

    public Guid Id { get; set; }

    public string? Name { get; set; }

    public string? OriginalPath { get; set; }

    public DateTime? OriginalLastWriteTimeUtc { get; set; }

    public bool AnimatePhysics { get; set; }

    public List<AnimVarSerializedModel> Variables { get; set; } = [];

    public List<AnimLayerSerializedModel> Layers { get; set; } = [];
}

internal sealed class AnimLayerSerializedModel
{
    public AnimLayer.EApplyType ApplyType { get; set; }

    public float? Weight { get; set; }

    public int? InitialStateIndex { get; set; }

    public Vector2 AnyStatePosition { get; set; }

    public List<AnimStateTransitionSerializedModel> AnyStateTransitions { get; set; } = [];

    public List<AnimStateSerializedModel> States { get; set; } = [];
}

internal sealed class AnimStateSerializedModel
{
    public string? Name { get; set; }

    public Vector2 Position { get; set; }

    public SerializedMotionModel? Motion { get; set; }

    public float StartSecond { get; set; }

    public float EndSecond { get; set; }

    public List<AnimStateComponentSerializedModel> Components { get; set; } = [];

    public List<AnimStateTransitionSerializedModel> Transitions { get; set; } = [];
}

internal sealed class AnimStateTransitionSerializedModel
{
    public int DestinationStateIndex { get; set; }

    public List<AnimTransitionCondition> Conditions { get; set; } = [];

    public float BlendDuration { get; set; }

    public EAnimBlendType BlendType { get; set; }

    public SerializedPropertyAnimationModel? CustomBlendFunction { get; set; }

    public int Priority { get; set; }

    public string? Name { get; set; }

    public float ExitTime { get; set; }

    public bool? FixedDuration { get; set; }

    public float TransitionOffset { get; set; }

    public ETransitionInterruptionSource InterruptionSource { get; set; }

    public bool? OrderedInterruption { get; set; }

    public bool CanTransitionToSelf { get; set; }
}

internal sealed class AnimStateComponentSerializedModel
{
    public AnimStateComponentKind Kind { get; set; }

    public string? TypeName { get; set; }

    public bool? ExecuteLocally { get; set; }

    public bool? ExecuteRemotely { get; set; }

    public string? DstParameterName { get; set; }

    public string? SrcParameterName { get; set; }

    public AnimParameterDriverComponent.EOperation Operation { get; set; }

    public float ConstantValue { get; set; }

    public float RandomMin { get; set; }

    public float RandomMax { get; set; }

    public TrackingControllerComponent.ETrackingMode TrackingModeHead { get; set; }

    public TrackingControllerComponent.ETrackingMode TrackingModeLeftHand { get; set; }

    public TrackingControllerComponent.ETrackingMode TrackingModeRightHand { get; set; }

    public TrackingControllerComponent.ETrackingMode TrackingModeLeftFoot { get; set; }

    public TrackingControllerComponent.ETrackingMode TrackingModeRightFoot { get; set; }

    public TrackingControllerComponent.ETrackingMode TrackingModeLeftFingers { get; set; }

    public TrackingControllerComponent.ETrackingMode TrackingModeRightFingers { get; set; }

    public TrackingControllerComponent.ETrackingMode TrackingModeEyes { get; set; }

    public TrackingControllerComponent.ETrackingMode TrackingModeMouth { get; set; }
}

internal sealed class AnimVarSerializedModel
{
    public AnimVarKind Kind { get; set; }

    public string? Name { get; set; }

    public bool BoolValue { get; set; }

    public int IntValue { get; set; }

    public float FloatValue { get; set; }

    public bool NegativeAllowed { get; set; }

    public float? Smoothing { get; set; }

    public int? CompressedBitCount { get; set; }
}