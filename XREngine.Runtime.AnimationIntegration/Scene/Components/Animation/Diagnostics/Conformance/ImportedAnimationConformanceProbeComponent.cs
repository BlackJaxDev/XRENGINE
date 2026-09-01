using XREngine.Animation.Importers;
using XREngine.Components;

namespace XREngine.Components.Animation;

/// <summary>
/// Explicit, scene-local target for Phase 10 imported-event and serialized-property
/// conformance observations. This is not a fallback for arbitrary Unity behaviours:
/// only the two documented probe properties are accepted.
/// </summary>
public sealed class ImportedAnimationConformanceProbeComponent : XRComponent,
    IImportedAnimationEventReceiver,
    IImportedAnimationBindingAdapter
{
    /// <summary>Reserved serialized-property name for a scalar probe binding.</summary>
    public const string ScalarAttribute = "m_XREConformanceScalar";

    /// <summary>Reserved serialized-property name for an object-reference probe binding.</summary>
    public const string ObjectReferenceAttribute = "m_XREConformanceObjectReference";

    private float _scalarValue;
    /// <summary>Last scalar value written by a supported imported binding.</summary>
    public float ScalarValue
    {
        get => _scalarValue;
        private set => SetField(ref _scalarValue, value);
    }

    private SourceAssetReference _objectReferenceValue;
    /// <summary>Last object reference written by a supported imported binding.</summary>
    public SourceAssetReference ObjectReferenceValue
    {
        get => _objectReferenceValue;
        private set => SetField(ref _objectReferenceValue, value);
    }

    /// <summary>Number of successful scalar writes performed by normal animation evaluation.</summary>
    public int ScalarWriteCount { get; private set; }

    /// <summary>Number of successful object-reference writes performed by normal animation evaluation.</summary>
    public int ObjectReferenceWriteCount { get; private set; }

    /// <summary>Whether an evaluated object-reference key was non-null.</summary>
    public bool ObservedNonNullObjectReference { get; private set; }

    /// <summary>Whether an evaluated object-reference key was null.</summary>
    public bool ObservedNullObjectReference { get; private set; }

    /// <summary>Scalar values written by normal animation evaluation, in write order.</summary>
    public IReadOnlyList<float> ScalarWrites => _scalarWrites;
    private readonly List<float> _scalarWrites = [];

    /// <summary>Object-reference values written by normal animation evaluation, in write order.</summary>
    public IReadOnlyList<SourceAssetReference> ObjectReferenceWrites => _objectReferenceWrites;
    private readonly List<SourceAssetReference> _objectReferenceWrites = [];

    /// <summary>Typed events delivered through the imported event dispatcher, in dispatch order.</summary>
    public IReadOnlyList<ImportedAnimationConformanceEventObservation> Events => _events;
    private readonly List<ImportedAnimationConformanceEventObservation> _events = [];

    /// <summary>Clears only recorded observations; it does not change animation runtime state.</summary>
    public void ClearObservations()
    {
        ScalarWriteCount = 0;
        ObjectReferenceWriteCount = 0;
        ObservedNonNullObjectReference = false;
        ObservedNullObjectReference = false;
        _scalarWrites.Clear();
        _objectReferenceWrites.Clear();
        _events.Clear();
    }

    /// <inheritdoc />
    public void ReceiveImportedAnimationEvent(in ImportedAnimationEventOccurrence occurrence)
        => _events.Add(new ImportedAnimationConformanceEventObservation
        {
            EventId = occurrence.Event.EventId,
            EventTime = occurrence.Event.Time,
            StringParameter = occurrence.Event.StringParameter,
            FloatParameter = occurrence.Event.FloatParameter,
            IntParameter = occurrence.Event.IntParameter,
            SourceOrder = occurrence.Event.SourceOrder,
            ObjectReferenceParameter = occurrence.Event.ObjectReferenceParameter,
            MessageOptions = occurrence.Event.MessageOptions,
            LoopCycle = occurrence.LoopCycle,
            Reverse = occurrence.Reverse,
            MotionOccurrenceId = occurrence.MotionOccurrenceId,
            StateName = occurrence.StateName,
            BlendWeight = occurrence.BlendWeight,
        });

    /// <inheritdoc />
    public bool CanBind(ImportedAnimationBindingDescriptor binding, out string diagnostic)
    {
        if (binding is null)
        {
            diagnostic = "Binding descriptor is null.";
            return false;
        }
        if (!binding.RequiresAdapter || binding.ClassId is not 114)
        {
            diagnostic = "Only explicit MonoBehaviour adapter bindings are supported by the conformance probe.";
            return false;
        }
        if (!string.IsNullOrEmpty(binding.NodePath) || binding.PathHash != 0)
        {
            diagnostic = "Conformance probe bindings must target the animated root node.";
            return false;
        }

        bool supported = binding.ValueKind == EImportedAnimationBindingValueKind.ObjectReference
            ? string.Equals(binding.Attribute, ObjectReferenceAttribute, StringComparison.Ordinal)
            : string.Equals(binding.Attribute, ScalarAttribute, StringComparison.Ordinal);
        diagnostic = supported
            ? string.Empty
            : $"Only '{ScalarAttribute}' and '{ObjectReferenceAttribute}' are supported by the conformance probe.";
        return supported;
    }

    /// <inheritdoc />
    public bool TryGetFloat(ImportedAnimationBindingDescriptor binding, out float value, out string diagnostic)
    {
        if (!CanBind(binding, out diagnostic) || binding.ValueKind == EImportedAnimationBindingValueKind.ObjectReference)
        {
            value = default;
            return false;
        }

        value = ScalarValue;
        return true;
    }

    /// <inheritdoc />
    public bool TrySetFloat(ImportedAnimationBindingDescriptor binding, float value, out string diagnostic)
    {
        if (!CanBind(binding, out diagnostic) || binding.ValueKind == EImportedAnimationBindingValueKind.ObjectReference)
            return false;

        ScalarValue = value;
        ScalarWriteCount++;
        _scalarWrites.Add(value);
        return true;
    }

    /// <inheritdoc />
    public bool TrySetObjectReference(
        ImportedAnimationBindingDescriptor binding,
        SourceAssetReference value,
        out string diagnostic)
    {
        if (!CanBind(binding, out diagnostic) || binding.ValueKind != EImportedAnimationBindingValueKind.ObjectReference)
            return false;

        ObjectReferenceValue = value;
        ObjectReferenceWriteCount++;
        ObservedNonNullObjectReference |= !value.IsNull;
        ObservedNullObjectReference |= value.IsNull;
        _objectReferenceWrites.Add(value);
        return true;
    }
}
