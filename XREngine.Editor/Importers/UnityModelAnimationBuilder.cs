using System.Numerics;
using XREngine.Animation;
using XREngine.Data;
using XREngine.Scene.Transforms;

namespace XREngine.Scene.Importers;

/// <summary>
/// Builds owned clip tracks against serialized transform identities.
/// </summary>
internal sealed class UnityModelAnimationBuilder(SceneNode root)
{
    private readonly AnimationMember _members = new("Root", EAnimationMemberType.Group);
    private readonly Dictionary<SceneNode, AnimationMember> _nodes = [];
    private AnimationMember? _rootTransform;
    private int _trackCount;

    public AnimationClip Build(string name, float duration)
        => new(_members)
        {
            Name = name,
            ClipKind = EAnimationClipKind.GenericTransform,
            LengthInSeconds = duration,
            SampleRate = 120,
            TotalAnimCount = _trackCount,
            Looped = false,
        };

    public void AddTransform(SceneNode node, float[] times, Matrix4x4[] matrices, float duration)
    {
        var translation = new PropAnimVector3(duration, false, true);
        var rotation = new PropAnimQuaternion(duration, false, true);
        var scale = new PropAnimVector3(duration, false, true);
        translation.Keyframes.PreInfinityMode = translation.Keyframes.PostInfinityMode = EKeyframeInfinityMode.Clamp;
        rotation.Keyframes.PreInfinityMode = rotation.Keyframes.PostInfinityMode = EKeyframeInfinityMode.Clamp;
        scale.Keyframes.PreInfinityMode = scale.Keyframes.PostInfinityMode = EKeyframeInfinityMode.Clamp;
        Quaternion previous = Quaternion.Identity;
        for (int index = 0; index < times.Length; index++)
        {
            if (!Matrix4x4.Decompose(matrices[index], out Vector3 s, out Quaternion r, out Vector3 t))
                throw new InvalidDataException($"Animation target '{node.Name}' has a non-TRS sample.");
            r = Quaternion.Normalize(r);
            if (index > 0 && Quaternion.Dot(previous, r) < 0.0f)
                r = -r;
            previous = r;
            translation.Keyframes.Add(new Vector3Keyframe(times[index], t, Vector3.Zero, EVectorInterpType.Linear)
            { InterpolationTypeIn = EVectorInterpType.Linear });
            rotation.Keyframes.Add(new QuaternionKeyframe(times[index], r, Quaternion.Identity, ERadialInterpType.Linear)
            {
                InterpolationTypeIn = ERadialInterpType.Linear,
            });
            scale.Keyframes.Add(new Vector3Keyframe(times[index], s, Vector3.Zero, EVectorInterpType.Linear)
            { InterpolationTypeIn = EVectorInterpType.Linear });
        }
        AnimationMember transform = GetNode(node);
        transform.Children.Add(new AnimationMember(nameof(Transform.Translation)) { Animation = translation });
        transform.Children.Add(new AnimationMember(nameof(Transform.Rotation)) { Animation = rotation });
        transform.Children.Add(new AnimationMember(nameof(Transform.Scale)) { Animation = scale });
        _trackCount += 3;
    }

    public void AddBlendShape(SceneNode node, string name, PropAnimFloat animation)
    {
        AnimationMember component = new("GetComponent", EAnimationMemberType.Method)
        {
            MethodArguments = ["ModelComponent"],
            AnimatedMethodArgumentIndex = -1,
            CacheReturnValue = true,
        };
        component.Children.Add(new AnimationMember("SetBlendShapeWeightNormalized", EAnimationMemberType.Method)
        {
            MethodArguments = [name, 0.0f, StringComparison.Ordinal],
            AnimatedMethodArgumentIndex = 1,
            Animation = animation,
        });
        AnimationMember sceneNode = new(nameof(TransformBase.SceneNode), EAnimationMemberType.Property);
        sceneNode.Children.Add(component);
        GetNode(node).Children.Add(sceneNode);
        _trackCount++;
    }

    private AnimationMember GetNode(SceneNode node)
    {
        if (_nodes.TryGetValue(node, out AnimationMember? member))
            return member;
        if (_rootTransform is null)
        {
            AnimationMember owner = new("SceneNode", EAnimationMemberType.Property);
            _rootTransform = new AnimationMember(nameof(SceneNode.Transform), EAnimationMemberType.Property);
            owner.Children.Add(_rootTransform);
            _members.Children.Add(owner);
        }
        // Pin the model identity before outer prefab overrides adopt their IDs.
        // The serialized reference stays stable when runtime IDs are regenerated.
        node.Transform.SerializedReferenceId = node.Transform.EffectiveSerializedReferenceId;
        if (!ReferenceEquals(root.Transform.FindSelfOrDescendantBySerializedReferenceId(node.Transform.SerializedReferenceId), node.Transform))
            throw new InvalidDataException($"Animation target '{node.Name}' has no unique model identity.");
        member = new AnimationMember(nameof(TransformBase.FindSelfOrDescendantBySerializedReferenceId), EAnimationMemberType.Method)
        {
            MethodArguments = [node.Transform.SerializedReferenceId],
            AnimatedMethodArgumentIndex = -1,
            CacheReturnValue = true,
        };
        _rootTransform.Children.Add(member);
        _nodes.Add(node, member);
        return member;
    }
}
