using System.Numerics;
using MemoryPack;
using XREngine;
using XREngine.Components;
using XREngine.Components.Scene.Mesh;
using XREngine.Rendering;
using XREngine.Scene;
using XREngine.Scene.Transforms;
using YamlDotNet.Serialization;

namespace RenderingParity;

/// <summary>Animates the saved skeleton and morph target through ordinary engine ticks.</summary>
public sealed class RenderingParityAnimationComponent : XRComponent
{
    private string _rootBoneNodeName = "Ribbon Root Bone";
    private string _tipBoneNodeName = "Ribbon Tip Bone";
    private string _modelNodeName = "Animated Ribbon";
    private float _periodSeconds = 4.0f;
    private float _tipAngleDegrees = 24.0f;
    private bool _animationPaused;
    private Transform? _rootBone;
    private Transform? _tipBone;
    private XRMeshRenderer? _renderer;
    private uint _blendshapeIndex;
    private float _elapsedSeconds;

    public string RootBoneNodeName { get => _rootBoneNodeName; set => SetField(ref _rootBoneNodeName, value); }
    public string TipBoneNodeName { get => _tipBoneNodeName; set => SetField(ref _tipBoneNodeName, value); }
    public string ModelNodeName { get => _modelNodeName; set => SetField(ref _modelNodeName, value); }
    public float PeriodSeconds { get => _periodSeconds; set => SetField(ref _periodSeconds, Math.Max(0.1f, value)); }
    public float TipAngleDegrees { get => _tipAngleDegrees; set => SetField(ref _tipAngleDegrees, value); }
    public bool AnimationPaused { get => _animationPaused; set => SetField(ref _animationPaused, value); }

    [YamlIgnore, MemoryPackIgnore]
    public float ElapsedSeconds => _elapsedSeconds;

    [YamlIgnore, MemoryPackIgnore]
    public float BlendshapePercentage => 50.0f * (1.0f - MathF.Cos(_elapsedSeconds * MathF.Tau / PeriodSeconds));

    protected override void OnBeginPlay()
    {
        base.OnBeginPlay();
        BindAuthoredRenderers();
        ResetAnimation();
    }

    protected override void OnComponentActivated()
    {
        base.OnComponentActivated();
        if (SceneNode.HasBegunPlay && _renderer is null)
            BindAuthoredRenderers();
        RegisterTick(ETickGroup.Normal, ETickOrder.Animation, TickAnimation);
    }

    protected override void OnComponentDeactivated()
    {
        UnregisterTick(ETickGroup.Normal, ETickOrder.Animation, TickAnimation);
        _renderer = null;
        _rootBone = null;
        _tipBone = null;
        base.OnComponentDeactivated();
    }

    private void BindAuthoredRenderers()
    {
        SceneNode root = SceneNode;
        _rootBone = RequireNode(root, RootBoneNodeName).GetTransformAs<Transform>(false)
            ?? throw new InvalidOperationException("The ribbon root requires an authored standard transform.");
        _tipBone = RequireNode(root, TipBoneNodeName).GetTransformAs<Transform>(false)
            ?? throw new InvalidOperationException("The ribbon tip requires an authored standard transform.");
        ModelComponent model = RequireNode(root, ModelNodeName).GetComponent<ModelComponent>()
            ?? throw new InvalidOperationException("The ribbon requires its authored model component.");
        if (model.Meshes.Count != 1 || model.Meshes[0].LODs.Count != 1)
            throw new InvalidOperationException("The ribbon requires exactly one saved submesh and LOD.");
        _renderer = model.Meshes[0].LODs.First!.Value.Renderer;
        XRMesh mesh = _renderer.Mesh
            ?? throw new InvalidOperationException("The ribbon renderer has no saved mesh.");
        if (!mesh.HasSkinning || !mesh.HasBlendshapes || !mesh.GetBlendshapeIndex("Ripple", out _blendshapeIndex))
            throw new InvalidOperationException("The ribbon requires its authored skeleton and Ripple morph target.");
    }

    private static SceneNode RequireNode(SceneNode root, string name)
        => root.FindDescendantByName(name, StringComparison.Ordinal)
            ?? throw new InvalidOperationException($"The rendering world is missing '{name}'.");

    /// <summary>Returns to the saved bind pose without replacing any authored objects.</summary>
    public void ResetAnimation()
    {
        SetField(ref _elapsedSeconds, 0.0f, nameof(ElapsedSeconds));
        ApplyPose();
    }

    private void TickAnimation()
    {
        if (AnimationPaused || _renderer is null || !Engine.PlayMode.IsPlaying)
            return;
        float seconds = _elapsedSeconds + Math.Clamp(Engine.Delta, 0.0f, 0.1f);
        SetField(ref _elapsedSeconds, seconds % PeriodSeconds, publishNotifications: false, propertyName: nameof(ElapsedSeconds));
        ApplyPose();
    }

    private void ApplyPose()
    {
        if (_renderer is null || _rootBone is null || _tipBone is null)
            return;
        float phase = _elapsedSeconds * MathF.Tau / PeriodSeconds;
        _rootBone.Rotation = Quaternion.CreateFromAxisAngle(Vector3.UnitY, 0.12f * MathF.Sin(phase));
        _tipBone.Rotation = Quaternion.CreateFromAxisAngle(Vector3.UnitZ,
            TipAngleDegrees * MathF.PI / 180.0f * MathF.Sin(phase));
        _renderer.SetBlendshapeWeight(_blendshapeIndex, BlendshapePercentage);
    }
}
