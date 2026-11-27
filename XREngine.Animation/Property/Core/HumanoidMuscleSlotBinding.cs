using System;
using XREngine.Components.Animation;

namespace XREngine.Animation;

/// <summary>
/// Builds and caches canonical <c>HumanoidComponent.SetImportedRawValue</c> slot bindings
/// for a fixed list of <see cref="EHumanoidValue"/> channels so procedural motions write
/// directly into <see cref="AnimationValueStore"/> with zero per-frame allocations.
/// </summary>
internal sealed class HumanoidMuscleSlotBinding
{
    private readonly EHumanoidValue[] _channels;
    private AnimationMember? _rootMember;
    private AnimationMember[] _setterMembers = [];
    private int[] _slotIndices = [];

    public HumanoidMuscleSlotBinding(EHumanoidValue[] channels)
        => _channels = channels;

    public int ChannelCount => _channels.Length;

    /// <summary>
    /// Builds the binding member tree on first access so <see cref="MotionBase.InitializeMotion"/>
    /// allocates shared <see cref="AnimSlot"/> indices matching imported humanoid clips.
    /// </summary>
    public AnimationMember EnsureBindingRootMember()
    {
        if (_rootMember is not null)
            return _rootMember;

        var root = new AnimationMember(string.Empty, EAnimationMemberType.Group);
        var sceneNode = new AnimationMember("SceneNode", EAnimationMemberType.Property);
        root.Children.Add(sceneNode);

        var getHumanoid = new AnimationMember("GetComponentInHierarchy", EAnimationMemberType.Method)
        {
            MethodArguments = ["HumanoidComponent"],
            AnimatedMethodArgumentIndex = -1,
            CacheReturnValue = true,
        };
        sceneNode.Children.Add(getHumanoid);

        var setters = new AnimationMember[_channels.Length];
        for (int i = 0; i < _channels.Length; i++)
        {
            var setter = new AnimationMember("SetImportedRawValue", EAnimationMemberType.Method)
            {
                MethodArguments = [_channels[i], 0.0f, false],
                AnimatedMethodArgumentIndex = 1,
                CacheReturnValue = false,
                DefaultValue = 0.0f,
            };
            getHumanoid.Children.Add(setter);
            setters[i] = setter;
        }

        _rootMember = root;
        _setterMembers = setters;
        _slotIndices = new int[_channels.Length];
        Array.Fill(_slotIndices, -1);
        return root;
    }

    /// <summary>
    /// Resolves and caches integer float slot indices after <see cref="AnimStateMachine"/> layout propagation.
    /// </summary>
    public void CacheResolvedSlots()
    {
        if (_slotIndices.Length != _setterMembers.Length)
            _slotIndices = new int[_setterMembers.Length];

        for (int i = 0; i < _setterMembers.Length; i++)
        {
            AnimSlot slot = _setterMembers[i].Slot;
            _slotIndices[i] = slot.IsValid && slot.Type == EAnimValueType.Float
                ? slot.TypeIndex
                : -1;
        }
    }

    /// <summary>
    /// Writes <paramref name="value"/> into the bound <see cref="AnimationValueStore"/> slot for channel <paramref name="channelIndex"/>.
    /// </summary>
    public void WriteChannel(AnimationValueStore store, int channelIndex, float value)
    {
        if ((uint)channelIndex >= (uint)_slotIndices.Length)
            return;

        int slotIndex = _slotIndices[channelIndex];
        if (slotIndex < 0)
            return;

        store.SetFloat(slotIndex, float.IsFinite(value) ? value : 0.0f);
    }

    /// <summary>
    /// Returns true when slot indices have not yet been resolved for non-empty channel bindings.
    /// </summary>
    public bool NeedsSlotRefresh => _setterMembers.Length > 0
        && (_slotIndices.Length != _setterMembers.Length || _slotIndices[0] < 0);
}
