using System.Numerics;
using System.Reflection;
using XREngine.Animation;
using XREngine.Components;
using XREngine.Scene;

namespace XREngine.Cutscenes;

/// <summary>
/// The state of one animated member: the property tracks that write it, the typed <see cref="AnimationMember"/>
/// that writes it, and its original value. Property tracks with the same slot, component, and member path share
/// one binding, so their clips blend by weight and one original value is restored.
/// The binding finds the member at the first frame with weight on a node, and again when the node of the slot
/// changes. Each time a clip becomes active, it walks the member path again. If an object on the path was replaced,
/// it binds to the new object and adds a diagnostic. In between, a replaced object is not seen.
/// The first write after an open or a stop reads the original value, which boxes it once.
/// The frame path samples typed curves and writes through the typed setter of the member, so it does not box.
/// </summary>
internal sealed class CutscenePropertyBinding
{
    private const BindingFlags MemberFlags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

    private readonly int _slot;
    private readonly int _trackIndex;
    private readonly PropertyTrack _track;
    private readonly List<CutsceneCompileDiagnostic> _diagnostics;

    private readonly PropertyTrackClip[] _clips;
    private readonly CutsceneTime[] _times;
    private readonly float[] _weights;
    private int _count;
    private float _totalWeight;

    private SceneNode? _resolvedNode;
    private AnimationMember? _member;
    private object? _owner;
    private PropertyInfo? _property;
    private FieldInfo? _field;
    private EAnimValueType _kind;

    private bool _hasOriginal;
    private float _originalFloat;
    private Vector3 _originalVector;
    private Quaternion _originalRotation;
    private bool _written;
    private bool _wasActive;
    private bool _reportedOwnerChange;
    private PropertyInfo?[] _pathProperties = [];
    private FieldInfo?[] _pathFields = [];
    private bool _reportedWrite;

    /// <summary>Creates a binding for the first property track of a member.</summary>
    /// <param name="trackIndex">The index of the first track.</param>
    /// <param name="track">The first track. Its slot, component, and member path identify the member.</param>
    /// <param name="clipCapacity">The number of clips of all tracks of the member.</param>
    /// <param name="diagnostics">The list that receives the problems that the binding finds.</param>
    public CutscenePropertyBinding(int trackIndex, PropertyTrack track, int clipCapacity, List<CutsceneCompileDiagnostic> diagnostics)
    {
        _trackIndex = trackIndex;
        _track = track;
        _slot = track.BindingSlot;
        _diagnostics = diagnostics;
        _clips = new PropertyTrackClip[clipCapacity];
        _times = new CutsceneTime[clipCapacity];
        _weights = new float[clipCapacity];
    }

    /// <summary>Checks if a track writes the member of this binding.</summary>
    /// <param name="track">The property track.</param>
    public bool Matches(PropertyTrack track)
        => IsSameMember(_track, track);

    /// <summary>Checks if two property tracks write the same member: the same slot, component, and member path.</summary>
    public static bool IsSameMember(PropertyTrack a, PropertyTrack b)
        => a.BindingSlot == b.BindingSlot
        && string.Equals(a.ComponentType, b.ComponentType, StringComparison.Ordinal)
        && string.Equals(a.MemberPath, b.MemberPath, StringComparison.Ordinal);

    /// <summary>Clears the clips of the last evaluation.</summary>
    public void BeginCollect()
    {
        _count = 0;
        _totalWeight = 0f;
    }

    /// <summary>Adds an active clip of the member to the evaluation.</summary>
    public void Collect(PropertyTrackClip clip, in CutsceneActiveClip active)
    {
        if (active.Weight <= 0f || _count >= _clips.Length)
            return;

        _clips[_count] = clip;
        _times[_count] = active.LocalTime;
        _weights[_count] = active.Weight;
        _count++;
        _totalWeight += active.Weight;
    }

    /// <summary>
    /// Writes the blend of the collected clips. A total weight of zero gives the original value back. A total above
    /// one normalizes the weights. A total below one blends from the original value by the total.
    /// </summary>
    /// <param name="bindings">The table that holds the bound nodes.</param>
    public void Evaluate(CutsceneBindingTable bindings)
    {
        bool becameActive = !_wasActive && _count > 0;
        _wasActive = _count > 0;
        if (_count == 0)
        {
            if (_written)
            {
                ApplyOriginal();
                _written = false;
            }
            return;
        }

        SceneNode? node = bindings.GetNode(_slot);
        if (node is null)
            return;

        if (!ReferenceEquals(node, _resolvedNode))
        {
            // The original value belongs to the earlier node, so it goes back there before the binding moves.
            if (_written)
                ApplyOriginal();
            _written = false;
            _hasOriginal = false;
            Resolve(node);
        }
        else if (becameActive && _member is not null && !ReferenceEquals(FindOwner(node), _owner))
        {
            // The member left its original value when the last clip ended, so only the record moves.
            if (_written)
                ApplyOriginal();
            _written = false;
            _hasOriginal = false;
            ReportOwnerChangeOnce();
            Resolve(node);
        }
        if (_member is null)
            return;

        CaptureOriginal();

        bool applied = _kind switch
        {
            EAnimValueType.Float => _member.TryApplyFloat(BlendFloat()),
            EAnimValueType.Vector3 => _member.TryApplyVector3(BlendVector()),
            _ => _member.TryApplyQuaternion(BlendRotation()),
        };
        _written = true;
        if (!applied)
            ReportWriteFailureOnce();
    }

    /// <summary>Forgets that the member was written. The snapshot restores the member itself.</summary>
    public void ForgetWrite()
    {
        _written = false;
        _wasActive = false;
    }

    /// <summary>Writes the original value to the member and clears the record, so the next write records a new original value.</summary>
    public void RestoreOriginal()
    {
        if (!_hasOriginal)
            return;
        ApplyOriginal();
        _hasOriginal = false;
    }

    /// <summary>Clears the record and keeps the live value. The next write records the live value as the original value.</summary>
    public void ForgetOriginal()
        => _hasOriginal = false;

    private float BlendFloat()
    {
        float divisor = MathF.Max(_totalWeight, 1f);
        float value = 0f;
        for (int i = 0; i < _count; i++)
            value += _clips[i].FloatCurve.Sample(_times[i]) * (_weights[i] / divisor);
        if (_totalWeight < 1f)
            value += _originalFloat * (1f - _totalWeight);
        return value;
    }

    private Vector3 BlendVector()
    {
        float divisor = MathF.Max(_totalWeight, 1f);
        Vector3 value = Vector3.Zero;
        for (int i = 0; i < _count; i++)
            value += _clips[i].Vector3Curve.Sample(_times[i]) * (_weights[i] / divisor);
        if (_totalWeight < 1f)
            value += _originalVector * (1f - _totalWeight);
        return value;
    }

    // A normalized weighted sum, with each rotation flipped into the hemisphere of the first one, as the transform track does.
    private Quaternion BlendRotation()
    {
        Quaternion sum = default;
        Quaternion first = Quaternion.Identity;
        for (int i = 0; i < _count; i++)
        {
            Quaternion sampled = _clips[i].QuaternionCurve.Sample(_times[i]);
            if (i == 0)
                first = sampled;
            else if (Quaternion.Dot(sampled, first) < 0f)
                sampled = Quaternion.Negate(sampled);
            sum += sampled * (_weights[i] / _totalWeight);
        }

        Quaternion blended = Quaternion.Normalize(sum);
        return _totalWeight < 1f ? Quaternion.Slerp(_originalRotation, blended, _totalWeight) : blended;
    }

    private void CaptureOriginal()
    {
        if (_hasOriginal)
            return;

        object? value = _property is not null ? _property.GetValue(_owner) : _field!.GetValue(_owner);
        switch (value)
        {
            case float number:
                _originalFloat = number;
                break;
            case Vector3 vector:
                _originalVector = vector;
                break;
            case Quaternion rotation:
                _originalRotation = rotation;
                break;
        }
        _hasOriginal = true;
    }

    private void ApplyOriginal()
    {
        if (!_hasOriginal || _member is null)
            return;

        switch (_kind)
        {
            case EAnimValueType.Float:
                _member.TryApplyFloat(_originalFloat);
                break;
            case EAnimValueType.Vector3:
                _member.TryApplyVector3(_originalVector);
                break;
            default:
                _member.TryApplyQuaternion(_originalRotation);
                break;
        }
    }

    /// <summary>Finds the member on a node. A failure adds one diagnostic and leaves the binding without a member.</summary>
    private void Resolve(SceneNode node)
    {
        _resolvedNode = node;
        _member = null;
        _owner = null;
        _property = null;
        _field = null;

        object? owner = FindTarget(node);
        if (owner is null)
        {
            Report($"The node '{node.Name}' of binding slot {_slot} has no component '{_track.ComponentType}'. The property track '{_track.Name}' writes nothing.");
            return;
        }

        string[] path = _track.MemberPath.Split('.', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (path.Length == 0)
        {
            Report($"The property track '{_track.Name}' has no member path. It writes nothing.");
            return;
        }

        PropertyInfo?[] pathProperties = new PropertyInfo?[path.Length - 1];
        FieldInfo?[] pathFields = new FieldInfo?[path.Length - 1];
        for (int i = 0; i < path.Length - 1; i++)
        {
            Type ownerType = owner.GetType();
            if (!TryFindMember(ownerType, path[i], out PropertyInfo? property, out FieldInfo? field))
            {
                Report($"The type '{ownerType.Name}' has no member '{path[i]}' in the path '{_track.MemberPath}' of the property track '{_track.Name}'. The track writes nothing.");
                return;
            }

            Type memberType = property?.PropertyType ?? field!.FieldType;
            if (memberType.IsValueType)
            {
                Report($"The member '{path[i]}' in the path '{_track.MemberPath}' of the property track '{_track.Name}' is a value type, so a write would change only a copy. The track writes nothing.");
                return;
            }

            object? next = property is not null ? property.GetValue(owner) : field!.GetValue(owner);
            if (next is null)
            {
                Report($"The member '{path[i]}' in the path '{_track.MemberPath}' of the property track '{_track.Name}' is null. The track writes nothing.");
                return;
            }
            pathProperties[i] = property;
            pathFields[i] = field;
            owner = next;
        }

        string leaf = path[^1];
        Type leafOwnerType = owner.GetType();
        if (!TryFindMember(leafOwnerType, leaf, out PropertyInfo? leafProperty, out FieldInfo? leafField)
            || (leafProperty is not null && (!leafProperty.CanRead || !leafProperty.CanWrite)))
        {
            Report($"The type '{leafOwnerType.Name}' has no readable and writable member '{leaf}' for the property track '{_track.Name}'. The track writes nothing.");
            return;
        }

        Type valueType = leafProperty?.PropertyType ?? leafField!.FieldType;
        EAnimValueType kind;
        if (valueType == typeof(float))
            kind = EAnimValueType.Float;
        else if (valueType == typeof(Vector3))
            kind = EAnimValueType.Vector3;
        else if (valueType == typeof(Quaternion))
            kind = EAnimValueType.Quaternion;
        else
        {
            Report($"The member '{leaf}' of the property track '{_track.Name}' has the type '{valueType.Name}'. A property track supports float, Vector3, and Quaternion members. The track writes nothing.");
            return;
        }

        AnimationMember member = new(leaf, leafProperty is not null ? EAnimationMemberType.Property : EAnimationMemberType.Field);
        if (leafProperty is not null)
            member.InitializeProperty(owner);
        else
            member.InitializeField(owner);
        if (member.MemberNotFound)
        {
            Report($"The animation member '{leaf}' of the property track '{_track.Name}' cannot bind to '{leafOwnerType.Name}'. The track writes nothing.");
            return;
        }

        _member = member;
        _owner = owner;
        _property = leafProperty;
        _field = leafField;
        _kind = kind;
        _reportedWrite = false;
        _pathProperties = pathProperties;
        _pathFields = pathFields;
    }

    /// <summary>Walks the bound path again from the node. It returns null if an object on the path is missing.</summary>
    private object? FindOwner(SceneNode node)
    {
        object? owner = FindTarget(node);
        for (int i = 0; i < _pathProperties.Length && owner is not null; i++)
        {
            PropertyInfo? property = _pathProperties[i];
            FieldInfo? field = _pathFields[i];
            Type? declaring = property?.DeclaringType ?? field?.DeclaringType;
            if (declaring is null || !declaring.IsInstanceOfType(owner))
                return null;
            owner = property is not null ? property.GetValue(owner) : field!.GetValue(owner);
        }
        return owner;
    }

    private void ReportOwnerChangeOnce()
    {
        if (_reportedOwnerChange)
            return;
        _reportedOwnerChange = true;
        Report($"An object on the member path '{_track.MemberPath}' of the property track '{_track.Name}' changed after the bind. The track now writes the new object. A change while a clip is active is seen only when the next clip becomes active.");
    }

    private object? FindTarget(SceneNode node)
    {
        string typeName = _track.ComponentType;
        if (string.IsNullOrWhiteSpace(typeName))
            return node;

        IReadOnlyList<XRComponent> components = node.Components;
        for (int i = 0; i < components.Count; i++)
        {
            Type type = components[i].GetType();
            if (string.Equals(type.Name, typeName, StringComparison.Ordinal) || string.Equals(type.FullName, typeName, StringComparison.Ordinal))
                return components[i];
        }
        return null;
    }

    private static bool TryFindMember(Type type, string name, out PropertyInfo? property, out FieldInfo? field)
    {
        property = null;
        field = null;
        for (Type? current = type; current is not null; current = current.BaseType)
        {
            property = current.GetProperty(name, MemberFlags | BindingFlags.DeclaredOnly);
            if (property is not null && property.GetIndexParameters().Length == 0)
                return true;
            property = null;

            field = current.GetField(name, MemberFlags | BindingFlags.DeclaredOnly);
            if (field is not null)
                return true;
        }
        return false;
    }

    private void ReportWriteFailureOnce()
    {
        if (_reportedWrite)
            return;
        _reportedWrite = true;
        Report($"The member '{_track.MemberPath}' of the property track '{_track.Name}' has no typed setter, or its setter failed. The value did not change.");
    }

    private void Report(string message)
    {
        _diagnostics.Add(new CutsceneCompileDiagnostic(_trackIndex, -1, message));
        Debug.LogWarning(ELogCategory.Animation, "Cutscene evaluation: {0}", message);
    }
}
