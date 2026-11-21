using System;
using System.Collections.Generic;
using System.Numerics;
using NUnit.Framework;
using Shouldly;
using XREngine.Animation;
using XREngine.Animation.IK;
using XREngine.Animation.Importers;
using XREngine.Components.Animation;

namespace XREngine.UnitTests.Animation;

[TestFixture]
public sealed class AnimationClipSamplingTests
{
    private const float Length = 3.0f;

    private static readonly float[] ForwardTimes = [0.0f, 0.1f, 0.25f, 0.5f, 0.75f, 1.0f, 1.4f, 2.0f, 2.6f, 2.95f, 3.0f, 3.5f];

    private static readonly Type[] TypedAnimations =
    [
        typeof(PropAnimFloat),
        typeof(PropAnimVector2),
        typeof(PropAnimVector3),
        typeof(PropAnimVector4),
        typeof(PropAnimQuaternion),
        typeof(PropAnimBool),
    ];

    [TestCaseSource(nameof(TypedAnimations))]
    public void SampleToStore_AfterBackwardJump_EqualsFreshInstance(Type animationType)
    {
        (AnimationClip jumped, AnimationMember jumpedMember, AnimationValueStore jumpedStore) = CreateClip(CreateAnimation(animationType, firstKeySecond: 0.5f));

        foreach (float time in new[] { 2.5f, 1.25f, 2.75f, 0.75f, 0.25f })
        {
            jumped.SampleToStore(time, jumpedStore);

            (AnimationClip fresh, AnimationMember freshMember, AnimationValueStore freshStore) = CreateClip(CreateAnimation(animationType, firstKeySecond: 0.5f));
            fresh.SampleToStore(time, freshStore);

            ReadStore(jumpedStore, jumpedMember.Slot).ShouldBe(ReadStore(freshStore, freshMember.Slot), $"time {time}");
        }
    }

    [TestCaseSource(nameof(TypedAnimations))]
    public void SampleToStore_LeavesPlaybackStateUnchanged(Type animationType)
    {
        BasePropAnim animation = CreateAnimation(animationType, firstKeySecond: 0.0f);
        animation.Seek(1.5f, wrapLooped: false);
        object? currentValue = animation.GetCurrentValueGeneric();
        float currentTime = animation.CurrentTime;
        int timeChanges = 0;
        animation.CurrentFrameChanged += _ => timeChanges++;
        (AnimationClip clip, _, AnimationValueStore store) = CreateClip(animation);

        clip.SampleToStore(0.25f, store);
        clip.SampleToStore(2.75f, store);

        animation.GetCurrentValueGeneric().ShouldBe(currentValue);
        animation.CurrentTime.ShouldBe(currentTime);
        timeChanges.ShouldBe(0);
    }

    [Test]
    public void SampleToStore_LeavesTypedCurrentValueUnchanged()
    {
        PropAnimQuaternion quaternion = (PropAnimQuaternion)CreateAnimation(typeof(PropAnimQuaternion), firstKeySecond: 0.0f);
        quaternion.Seek(1.5f, wrapLooped: false);
        Quaternion currentValue = quaternion.CurrentValue;
        (AnimationClip clip, _, AnimationValueStore store) = CreateClip(quaternion);

        clip.SampleToStore(0.25f, store);

        quaternion.CurrentValue.ShouldBe(currentValue);
    }

    [TestCaseSource(nameof(TypedAnimations))]
    public void SampleToStore_TypedAnimation_AllocatesNothing(Type animationType)
    {
        foreach (float firstKeySecond in new[] { 0.0f, 0.5f })
        {
            BasePropAnim animation = CreateAnimation(animationType, firstKeySecond);
            (AnimationClip clip, AnimationMember member, AnimationValueStore store) = CreateClip(animation);

            for (int i = 0; i < ForwardTimes.Length; i++)
                clip.SampleToStore(ForwardTimes[i], store);

            long before = GC.GetAllocatedBytesForCurrentThread();
            for (int iteration = 0; iteration < 200; iteration++)
            {
                for (int i = ForwardTimes.Length - 1; i >= 0; i--)
                {
                    clip.SampleToStore(ForwardTimes[i], store);
                    animation.WriteSampleToStore(ForwardTimes[i], store, in member.Slot);
                }
            }
            long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

            allocated.ShouldBe(0L);
        }
    }

    [Test]
    public void GetUnsupportedSamplingMembers_ReportsOnlyTracksThatCallCode()
    {
        var supported = new AnimationMember("Supported", EAnimationMemberType.Property, CreateAnimation(typeof(PropAnimFloat), 0.0f));
        var methodMember = new AnimationMember("SetKeyed", EAnimationMemberType.Method, CreateAnimation(typeof(PropAnimFloat), 0.0f));
        var delegateAnimated = new AnimationMember("Delegate", EAnimationMemberType.Property, new PropAnimMethod<float>(Length, false, second => second));
        var unanimatedMethod = new AnimationMember("SetDefault", EAnimationMemberType.Method);
        var navigation = new AnimationMember("GetComponent", EAnimationMemberType.Method);
        navigation.Children.Add(supported);
        var root = new AnimationMember("Root", EAnimationMemberType.Group);
        root.Children.Add(navigation);
        root.Children.Add(methodMember);
        root.Children.Add(delegateAnimated);
        root.Children.Add(unanimatedMethod);
        var clip = new AnimationClip(root);

        clip.GetUnsupportedSamplingMembers().ShouldBe([delegateAnimated]);

        var store = new AnimationValueStore();
        clip.PrepareSampling(store);
        clip.SampleToStore(0.5f, store);

        supported.Slot.IsValid.ShouldBeTrue();
        store.GetCoverage(supported.Slot).ShouldBe(1.0f);
        store.GetCoverage(methodMember.Slot).ShouldBe(1.0f);
        store.GetFloat(methodMember.Slot.TypeIndex).ShouldBe(((PropAnimFloat)methodMember.Animation!).GetValue(0.5f));
        unanimatedMethod.Slot.IsValid.ShouldBeTrue();
        navigation.Slot.IsValid.ShouldBeFalse();
        delegateAnimated.Slot.IsValid.ShouldBeFalse();
    }

    [Test]
    public void SampleToStore_WithoutPrepareSampling_Throws()
    {
        var clip = new AnimationClip(new AnimationMember("Value", EAnimationMemberType.Property, CreateAnimation(typeof(PropAnimFloat), 0.0f)));

        Should.Throw<InvalidOperationException>(() => clip.SampleToStore(0.0f, new AnimationValueStore()));
    }

    /// <summary>
    /// Compares a normal seek and store write with a history-free sample. Each time uses
    /// new instances, so no retained key or earlier time can affect the normal value.
    /// </summary>
    [Test]
    public void SampleToStore_ForwardTimes_MatchesNormalSeekAndRead(
        [ValueSource(nameof(TypedAnimations))] Type animationType,
        [Values(0.0f, 0.5f)] float firstKeySecond,
        [Values] bool looped,
        [Values] bool baked,
        [Values] bool constrained,
        [Values] bool lerpConstrained)
    {
        foreach (float time in ForwardTimes)
        {
            BasePropAnim Create() => CreateAnimation(animationType, firstKeySecond, looped, baked, constrained, lerpConstrained);

            BasePropAnim normalAnimation = Create();
            (_, AnimationMember normalMember, AnimationValueStore normalStore) = CreateClip(normalAnimation);
            normalAnimation.Seek(time, wrapLooped: normalAnimation.Looped);
            normalMember.WriteCurrentValueToStore(normalStore);

            (AnimationClip sampled, AnimationMember sampledMember, AnimationValueStore sampledStore) = CreateClip(Create());
            sampled.SampleToStore(time, sampledStore);

            ReadStore(sampledStore, sampledMember.Slot).ShouldBe(ReadStore(normalStore, normalMember.Slot), $"time {time}");
        }
    }

    [TestCase(PropAnimObject.EDiscreteValueRounding.Floor)]
    [TestCase(PropAnimObject.EDiscreteValueRounding.Nearest)]
    public void SampleToStore_ObjectAnimation_MatchesNormalRounding(PropAnimObject.EDiscreteValueRounding rounding)
    {
        foreach (float time in new[] { 0.25f, 0.75f, 1.75f })
        {
            PropAnimObject Create()
            {
                var animation = new PropAnimObject(Length, false, true) { DiscreteValueRounding = rounding };
                animation.Keyframes.Add(new ObjectKeyframe { Second = 0.0f, Value = "first" });
                animation.Keyframes.Add(new ObjectKeyframe { Second = 1.0f, Value = "second" });
                animation.Keyframes.Add(new ObjectKeyframe { Second = 2.0f, Value = "third" });
                return animation;
            }

            PropAnimObject normal = Create();
            normal.Seek(time, wrapLooped: false);
            (AnimationClip clip, AnimationMember member, AnimationValueStore store) = CreateClip(Create());

            clip.SampleToStore(time, store);

            store.GetDiscrete(member.Slot.TypeIndex).ShouldBe(normal.GetCurrentValueGeneric());
        }
    }

    /// <summary>
    /// Records ordinary seek behavior. A backward seek inside the authored key range
    /// gives the same value as a new instance, because the retained key walks back.
    /// </summary>
    [Test]
    public void QuaternionSeek_BackwardInsideKeyRange_MatchesFreshInstance()
    {
        foreach (float time in new[] { 0.5f, 0.75f, 1.25f, 1.9f })
        {
            var jumped = (PropAnimQuaternion)CreateAnimation(typeof(PropAnimQuaternion), firstKeySecond: 0.5f);
            jumped.Seek(2.75f, wrapLooped: false);
            jumped.Seek(time, wrapLooped: false);

            var fresh = (PropAnimQuaternion)CreateAnimation(typeof(PropAnimQuaternion), firstKeySecond: 0.5f);
            fresh.Seek(time, wrapLooped: false);

            jumped.CurrentValue.ShouldBe(fresh.CurrentValue, $"time {time}");
        }
    }

    /// <summary>
    /// Records a known ordinary seek defect and does not state wanted behavior. Before the
    /// first key, a new instance has no retained key and gives the default value. After a
    /// backward seek, the retained key gives a blend from the last key to the first key.
    /// </summary>
    [Test]
    public void QuaternionSeek_BackwardBeforeFirstKey_CurrentlyDiffersFromFreshInstance()
    {
        const float time = 0.25f;
        var jumped = (PropAnimQuaternion)CreateAnimation(typeof(PropAnimQuaternion), firstKeySecond: 0.5f);
        jumped.Seek(1.5f, wrapLooped: false);
        jumped.Seek(time, wrapLooped: false);

        var fresh = (PropAnimQuaternion)CreateAnimation(typeof(PropAnimQuaternion), firstKeySecond: 0.5f);
        fresh.Seek(time, wrapLooped: false);

        fresh.CurrentValue.ShouldBe(fresh.DefaultValue);
        jumped.CurrentValue.ShouldNotBe(fresh.CurrentValue);
        jumped.CurrentValue.ShouldBe(jumped.GetValueKeyframed(time));
    }

    [TestCase(false, false, 0.0f, true)]
    [TestCase(true, false, 0.0f, true)]
    [TestCase(false, true, 0.0f, true)]
    [TestCase(false, false, 0.3f, true)]
    [TestCase(true, true, 0.3f, true)]
    [TestCase(true, true, 0.3f, false)]
    public void SampleToStore_ImportedHumanoidClip_MatchesStateMachinePlayback(bool mirror, bool loopPose, float cycleOffset, bool loopTime)
    {
        // Times past the length cross a loop boundary, or clamp when the clip does not loop.
        foreach (float time in new[] { 0.0f, 0.2f, 0.5f, 0.75f, 1.1f, 1.9f, 2.0f, 2.3f, 3.6f, 4.25f })
        {
            AnimationClip normal = CreateImportedHumanoidClip(mirror, loopPose, cycleOffset, loopTime);
            var machine = new AnimStateMachine();
            var layer = new AnimLayer { States = [new AnimState(normal)], InitialStateIndex = 0 };
            machine.Layers.Add(layer);
            machine.Initialize(null);
            normal.EvaluateAnimationValuesAtNormalizedStateTime(new Dictionary<string, AnimVar>(), time / 2.0, additive: false);

            AnimationClip sampled = CreateImportedHumanoidClip(mirror, loopPose, cycleOffset, loopTime);
            var store = new AnimationValueStore();
            sampled.PrepareSampling(store);
            sampled.SampleToStore(time, store);

            store.TotalSlotCount.ShouldBe(normal.ValueStore.TotalSlotCount);
            ShouldMatch(store, normal.ValueStore, time);
        }
    }

    [TestCase(true, false, 0.0f)]
    [TestCase(false, true, 0.0f)]
    [TestCase(false, false, 0.3f)]
    public void SampleToStore_ImportedPolicy_ChangesValues(bool mirror, bool loopPose, float cycleOffset)
    {
        static float[] Sample(bool mirror, bool loopPose, float cycleOffset)
        {
            AnimationClip clip = CreateImportedHumanoidClip(mirror, loopPose, cycleOffset, loopTime: true);
            var store = new AnimationValueStore();
            clip.PrepareSampling(store);
            clip.SampleToStore(0.5f, store);
            float[] values = new float[store.FloatCount];
            for (int i = 0; i < values.Length; i++)
                values[i] = store.GetFloat(i);
            return values;
        }

        Sample(mirror, loopPose, cycleOffset).ShouldNotBe(Sample(false, false, 0.0f));
    }

    [Test]
    public void SampleToStore_ImportedHumanoidClip_AllocatesNothing()
    {
        AnimationClip clip = CreateImportedHumanoidClip(mirror: true, loopPose: true, cycleOffset: 0.3f, loopTime: true);
        var store = new AnimationValueStore();
        clip.PrepareSampling(store);
        clip.SampleToStore(0.5f, store);

        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 2000; i++)
            clip.SampleToStore(i * 0.013f, store);
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        allocated.ShouldBe(0L);
    }

    [TestCaseSource(nameof(TypedAnimations))]
    public void SampleToStore_RandomOrder_MatchesHintFreeResults(Type animationType)
    {
        var random = new Random(1234);
        foreach (bool constrained in new[] { false, true })
        {
            (AnimationClip hinted, AnimationMember hintedMember, AnimationValueStore hintedStore) =
                CreateClip(CreateAnimation(animationType, 0.5f, constrained: constrained, lerpConstrained: constrained));
            for (int i = 0; i < 300; i++)
            {
                float time = (float)(random.NextDouble() * (Length + 1.0));
                hinted.SampleToStore(time, hintedStore);

                (AnimationClip fresh, AnimationMember freshMember, AnimationValueStore freshStore) =
                    CreateClip(CreateAnimation(animationType, 0.5f, constrained: constrained, lerpConstrained: constrained));
                fresh.SampleToStore(time, freshStore);

                hintedStore.GetValue(hintedMember.Slot).ShouldBe(freshStore.GetValue(freshMember.Slot), $"time {time}");
            }
        }
    }

    [Test]
    public void SampleToStore_WrongKeyHint_DoesNotChangeResult()
    {
        var other = new PropAnimFloat(Length, false, true);
        other.Keyframes.Add(new FloatKeyframe(0.25f, 100.0f, 0.0f, EVectorInterpType.Linear));

        var animation = (PropAnimFloat)CreateAnimation(typeof(PropAnimFloat), 0.0f);
        FloatKeyframe removed = new(2.5f, 50.0f, 0.0f, EVectorInterpType.Linear);
        animation.Keyframes.Add(removed);
        removed.Remove();
        (AnimationClip clip, AnimationMember member, AnimationValueStore store) = CreateClip(animation);
        float expected = animation.GetValue(1.5f);

        foreach (FloatKeyframe? hint in new[] { animation.Keyframes.First, animation.Keyframes.Last, other.Keyframes.First, removed, null })
        {
            animation.SetSampleKeyHint(hint);
            clip.SampleToStore(1.5f, store);
            store.GetFloat(member.Slot.TypeIndex).ShouldBe(expected);
        }

        animation.SetSampleKeyHint(animation.Keyframes.Last);
        animation.Keyframes.Clear();
        animation.Keyframes.Add(new FloatKeyframe(0.0f, 7.0f, 0.0f, EVectorInterpType.Linear));
        clip.SampleToStore(1.5f, store);
        store.GetFloat(member.Slot.TypeIndex).ShouldBe(7.0f);
    }

    [Test]
    public void SampleToStore_LargeTrackForwardStep_DoesNotWalkFromFirstKey()
    {
        const int keyCount = 10000;
        var animation = new PropAnimFloat(keyCount * 0.01f, false, true);
        // Descending insertion links each key at the front, so setup does not walk the track.
        for (int i = keyCount - 1; i >= 0; i--)
            animation.Keyframes.Add(new FloatKeyframe(i * 0.01f, i, 0.0f, EVectorInterpType.Linear));
        (AnimationClip clip, AnimationMember member, AnimationValueStore store) = CreateClip(animation);

        FloatKeyframe? key = animation.Keyframes.FindKeyBefore(50.0f, null, out int steps);
        steps.ShouldBeGreaterThan(1000);

        for (int i = 1; i <= 2000; i++)
        {
            float time = 50.0f + i * 0.015f;
            key = animation.Keyframes.FindKeyBefore(time, key, out steps);
            steps.ShouldBeLessThanOrEqualTo(2);
            key.ShouldBe(animation.Keyframes.GetKeyBefore(time));

            clip.SampleToStore(time, store);
            // Key values equal key indices. A walk from the first key recurses too deep to compare here.
            store.GetFloat(member.Slot.TypeIndex).ShouldBe(time / 0.01f, 0.01f);
        }
    }

    [Test]
    public void SampleToStore_ImportedClip_LeavesMethodArgumentsUnchanged()
    {
        AnimationClip clip = CreateImportedHumanoidClip(mirror: true, loopPose: true, cycleOffset: 0.3f, loopTime: true);
        clip.PrepareSampling(new AnimationValueStore());
        var store = clip.SlotLayout!.CreateStore();
        AnimationMember member = clip.RootMember!.Children[0];

        member.MethodArguments[0] = EHumanoidValue.LeftArmFrontBack;
        clip.SampleToStore(0.5f, store);

        member.MethodArguments[0].ShouldBe(EHumanoidValue.LeftArmFrontBack);
    }

    [Test]
    public void KeyHint_AfterClear_DoesNotReachRemovedKeys()
    {
        var animation = (PropAnimFloat)CreateAnimation(typeof(PropAnimFloat), 0.0f);
        (AnimationClip clip, AnimationMember member, AnimationValueStore store) = CreateClip(animation);
        FloatKeyframe oldKey = animation.Keyframes.Last!;

        animation.Keyframes.Clear();
        oldKey.OwningTrack.ShouldBeNull();
        oldKey.Next.ShouldBeNull();
        oldKey.Prev.ShouldBeNull();
        animation.Keyframes.Count.ShouldBe(0);
        animation.Keyframes.Add(new FloatKeyframe(0.0f, 7.0f, 0.0f, EVectorInterpType.Linear));
        animation.SetSampleKeyHint(oldKey);

        clip.SampleToStore(2.5f, store);

        store.GetFloat(member.Slot.TypeIndex).ShouldBe(7.0f);
    }

    [TestCase(true)]
    [TestCase(false)]
    public void RemoveSingleKey_DetachesKey(bool removeFirst)
    {
        var animation = new PropAnimFloat(Length, false, true);
        var key = new FloatKeyframe(1.0f, 2.0f, 0.0f, EVectorInterpType.Linear);
        animation.Keyframes.Add(key);

        if (removeFirst)
            animation.Keyframes.RemoveFirst();
        else
            animation.Keyframes.RemoveLast();

        key.OwningTrack.ShouldBeNull();
        animation.Keyframes.Count.ShouldBe(0);
        animation.Keyframes.First.ShouldBeNull();
        animation.Keyframes.Last.ShouldBeNull();
    }

    [Test]
    public void KeyHint_AfterKeyMoveOrRemoval_MatchesFreshInstance()
    {
        static PropAnimFloat Create(float movedSecond, bool includeLast)
        {
            var animation = new PropAnimFloat(Length, false, true);
            animation.Keyframes.Add(new FloatKeyframe(0.0f, 1.0f, 0.0f, EVectorInterpType.Linear));
            animation.Keyframes.Add(new FloatKeyframe(movedSecond, 4.0f, 0.0f, EVectorInterpType.Linear));
            if (includeLast)
                animation.Keyframes.Add(new FloatKeyframe(2.0f, -2.0f, 0.0f, EVectorInterpType.Linear));
            return animation;
        }

        PropAnimFloat animation = Create(1.0f, includeLast: true);
        (AnimationClip clip, AnimationMember member, AnimationValueStore store) = CreateClip(animation);
        FloatKeyframe moved = animation.Keyframes.GetKeyBefore(1.0f)!;
        FloatKeyframe last = animation.Keyframes.Last!;

        // The live hint is the moved key, because the sample before the move found it.
        clip.SampleToStore(1.5f, store);
        int revision = animation.Keyframes.Revision;
        moved.Second = 2.5f;
        animation.Keyframes.Revision.ShouldNotBe(revision);
        foreach (float time in new[] { 1.5f, 2.25f, 2.75f })
        {
            clip.SampleToStore(time, store);
            (AnimationClip fresh, AnimationMember freshMember, AnimationValueStore freshStore) = CreateClip(Create(2.5f, includeLast: true));
            fresh.SampleToStore(time, freshStore);
            store.GetFloat(member.Slot.TypeIndex).ShouldBe(freshStore.GetFloat(freshMember.Slot.TypeIndex), $"moved, time {time}");
        }

        // The live hint is now the key that is removed.
        clip.SampleToStore(2.25f, store);
        revision = animation.Keyframes.Revision;
        last.Remove();
        animation.Keyframes.Revision.ShouldNotBe(revision);
        clip.SampleToStore(2.25f, store);
        (AnimationClip afterRemoval, AnimationMember afterRemovalMember, AnimationValueStore afterRemovalStore) = CreateClip(Create(2.5f, includeLast: false));
        afterRemoval.SampleToStore(2.25f, afterRemovalStore);
        store.GetFloat(member.Slot.TypeIndex).ShouldBe(afterRemovalStore.GetFloat(afterRemovalMember.Slot.TypeIndex));
    }

    [Test]
    public void PrepareSharedSampling_MirroredClipsInOneScope_MatchSoloSamples()
    {
        AnimationClip Create(bool mirror) => CreateImportedHumanoidClip(mirror, loopPose: false, cycleOffset: 0.0f, loopTime: true);

        AnimationClip mirrored = Create(true);
        AnimationClip plain = Create(false);
        AnimationSlotLayout layout = AnimationClip.PrepareSharedSampling([new(mirrored), new(plain)]);
        AnimationValueStore mirroredStore = layout.CreateStore();
        AnimationValueStore plainStore = layout.CreateStore();

        AnimationClip soloMirrored = Create(true);
        var soloMirroredStore = new AnimationValueStore();
        AnimationSlotLayout soloLayout = soloMirrored.PrepareSampling(soloMirroredStore);
        AnimationClip soloPlain = Create(false);
        var soloPlainStore = new AnimationValueStore();
        soloPlain.PrepareSampling(soloPlainStore);

        layout.FloatCount.ShouldBe(soloLayout.FloatCount);
        layout.SamplingApplyMembers.Count.ShouldBe(soloLayout.SamplingApplyMembers.Count);
        foreach (float time in new[] { 0.25f, 1.1f, 2.6f })
        {
            mirrored.SampleToStore(time, mirroredStore);
            plain.SampleToStore(time, plainStore);
            soloMirrored.SampleToStore(time, soloMirroredStore);
            soloPlain.SampleToStore(time, soloPlainStore);

            ShouldMatch(mirroredStore, soloMirroredStore, time);
            ShouldMatch(plainStore, soloPlainStore, time);
        }
    }

    [Test]
    public void PrepareSharedSampling_RejectedCall_ChangesNothing()
    {
        var firstMember = new AnimationMember("Value", EAnimationMemberType.Property, CreateAnimation(typeof(PropAnimFloat), 0.0f));
        var first = new AnimationClip(firstMember);
        var store = new AnimationValueStore();
        AnimationSlotLayout firstLayout = first.PrepareSampling(store);
        AnimSlot firstSlot = firstMember.Slot;
        var secondMember = new AnimationMember("Value", EAnimationMemberType.Property, CreateAnimation(typeof(PropAnimBool), 0.0f));
        var second = new AnimationClip(secondMember);

        Should.Throw<InvalidOperationException>(() => AnimationClip.PrepareSharedSampling([new(first), new(second)]));
        Should.Throw<ArgumentException>(() => AnimationClip.PrepareSharedSampling([new(first), new(first)]));

        firstMember.Slot.ShouldBe(firstSlot);
        first.SlotLayout.ShouldBe(firstLayout);
        secondMember.Slot.IsValid.ShouldBeFalse();
        second.SlotLayout.ShouldBeNull();
        second.AnimatedCurves.Count.ShouldBe(0);
        first.SampleToStore(0.5f, store);
    }

    [Test]
    public void PrepareSharedSampling_TwoClipsOnOneMember_ResolveToOneSlot()
    {
        var firstAnimation = new PropAnimFloat(Length, false, true);
        firstAnimation.Keyframes.Add(new FloatKeyframe(0.0f, 2.0f, 0.0f, EVectorInterpType.Step));
        var secondAnimation = new PropAnimFloat(Length, false, true);
        secondAnimation.Keyframes.Add(new FloatKeyframe(0.0f, 6.0f, 0.0f, EVectorInterpType.Step));
        var firstMember = new AnimationMember("Value", EAnimationMemberType.Property, firstAnimation);
        var secondMember = new AnimationMember("Value", EAnimationMemberType.Property, secondAnimation);
        var first = new AnimationClip(firstMember);
        var second = new AnimationClip(secondMember);

        AnimationSlotLayout layout = AnimationClip.PrepareSharedSampling([new(first), new(second)]);

        layout.FloatCount.ShouldBe(1);
        secondMember.Slot.ShouldBe(firstMember.Slot);
        layout.SamplingApplyMembers.ShouldBe([firstMember]);

        AnimationValueStore firstStore = layout.CreateStore();
        AnimationValueStore secondStore = layout.CreateStore();
        AnimationValueStore blended = layout.CreateStore();
        first.SampleToStore(1.0f, firstStore);
        second.SampleToStore(1.0f, secondStore);
        AnimationValueStore.Lerp(firstStore, secondStore, 0.5f, blended);

        blended.GetFloat(firstMember.Slot.TypeIndex).ShouldBe(4.0f);
    }

    [Test]
    public void PrepareSharedSampling_SeparateScopes_KeepSeparateSlots()
    {
        var firstMember = new AnimationMember("Value", EAnimationMemberType.Property, CreateAnimation(typeof(PropAnimFloat), 0.0f));
        var secondMember = new AnimationMember("Value", EAnimationMemberType.Property, CreateAnimation(typeof(PropAnimFloat), 0.0f));

        AnimationSlotLayout layout = AnimationClip.PrepareSharedSampling(
        [
            new(new AnimationClip(firstMember), "Actor A"),
            new(new AnimationClip(secondMember), "Actor B"),
        ]);

        layout.FloatCount.ShouldBe(2);
        secondMember.Slot.TypeIndex.ShouldNotBe(firstMember.Slot.TypeIndex);
        layout.SamplingApplyMembers.Count.ShouldBe(2);
    }

    [Test]
    public void PrepareSharedSampling_DifferentValueTypesOnOnePath_Throws()
    {
        var first = new AnimationClip(new AnimationMember("Value", EAnimationMemberType.Property, CreateAnimation(typeof(PropAnimFloat), 0.0f)));
        var second = new AnimationClip(new AnimationMember("Value", EAnimationMemberType.Property, CreateAnimation(typeof(PropAnimBool), 0.0f)));

        Should.Throw<InvalidOperationException>(() => AnimationClip.PrepareSharedSampling([new(first), new(second)]));
    }

    private static void ShouldMatch(AnimationValueStore actual, AnimationValueStore expected, float time)
    {
        const float tolerance = 2e-5f;
        actual.FloatCount.ShouldBe(expected.FloatCount);
        actual.Vector3Count.ShouldBe(expected.Vector3Count);
        actual.QuaternionCount.ShouldBe(expected.QuaternionCount);
        for (int i = 0; i < expected.FloatCount; i++)
            actual.GetFloat(i).ShouldBe(expected.GetFloat(i), tolerance, $"float {i} at time {time}");
        for (int i = 0; i < expected.Vector3Count; i++)
            Vector3.Distance(actual.GetVector3(i), expected.GetVector3(i)).ShouldBeLessThan(tolerance, $"vector {i} at time {time}");
        for (int i = 0; i < expected.QuaternionCount; i++)
        {
            Quaternion a = actual.GetQuaternion(i);
            Quaternion b = expected.GetQuaternion(i);
            (new Vector4(a.X, a.Y, a.Z, a.W) - new Vector4(b.X, b.Y, b.Z, b.W)).Length().ShouldBeLessThan(tolerance, $"quaternion {i} at time {time}");
        }
    }

    private static AnimationClip CreateImportedHumanoidClip(bool mirror, bool loopPose, float cycleOffset, bool loopTime)
    {
        const float clipLength = 2.0f;
        PropAnimFloat Scalar(float start, float middle, float end)
        {
            var animation = new PropAnimFloat(clipLength, false, true);
            animation.Keyframes.Add(new FloatKeyframe(0.0f, start, 0.0f, EVectorInterpType.Linear));
            animation.Keyframes.Add(new FloatKeyframe(1.0f, middle, 0.0f, EVectorInterpType.Linear));
            animation.Keyframes.Add(new FloatKeyframe(clipLength, end, 0.0f, EVectorInterpType.Linear));
            return animation;
        }

        var position = new PropAnimVector3(clipLength, false, true);
        position.Keyframes.Add(new Vector3Keyframe(0.0f, new Vector3(1.0f, 2.0f, 3.0f), Vector3.Zero, EVectorInterpType.Linear));
        position.Keyframes.Add(new Vector3Keyframe(clipLength, new Vector3(2.0f, 1.0f, -1.0f), Vector3.Zero, EVectorInterpType.Linear));
        var rotation = new PropAnimQuaternion(clipLength, false, true);
        rotation.Keyframes.Add(new QuaternionKeyframe(0.0f, Quaternion.CreateFromAxisAngle(Vector3.UnitY, 0.2f), Quaternion.Identity, ERadialInterpType.Linear));
        rotation.Keyframes.Add(new QuaternionKeyframe(clipLength, Quaternion.CreateFromAxisAngle(Vector3.UnitY, 1.2f), Quaternion.Identity, ERadialInterpType.Linear));

        AnimationMember Goal(string name, ELimbEndEffector goal, object? value, BasePropAnim animation)
            => new(name, EAnimationMemberType.Method, animation)
            {
                MethodArguments = [goal, value],
                AnimatedMethodArgumentIndex = 1,
            };

        var root = new AnimationMember("Root", EAnimationMemberType.Group);
        root.Children.Add(new AnimationMember("SetValue", EAnimationMemberType.Method, Scalar(0.1f, 0.6f, -0.4f))
        {
            MethodArguments = [EHumanoidValue.LeftArmDownUp, 0.0f],
            AnimatedMethodArgumentIndex = 1,
        });
        root.Children.Add(Goal("SetAnimatedIKPositionX", ELimbEndEffector.LeftFoot, 0.0f, Scalar(0.5f, 1.0f, 2.0f)));
        root.Children.Add(Goal("SetAnimatedIKPosition", ELimbEndEffector.RightHand, Vector3.Zero, position));
        root.Children.Add(Goal("SetAnimatedIKRotation", ELimbEndEffector.RightHand, Quaternion.Identity, rotation));
        root.Children.Add(Goal("SetAnimatedIKRotationX", ELimbEndEffector.LeftHand, 0.0f, Scalar(0.0f, 0.2f, 0.1f)));
        root.Children.Add(Goal("SetAnimatedIKRotationY", ELimbEndEffector.LeftHand, 0.0f, Scalar(0.1f, 0.3f, 0.0f)));
        root.Children.Add(Goal("SetAnimatedIKRotationZ", ELimbEndEffector.LeftHand, 0.0f, Scalar(0.0f, -0.1f, 0.2f)));
        root.Children.Add(Goal("SetAnimatedIKRotationW", ELimbEndEffector.LeftHand, 1.0f, Scalar(1.0f, 0.9f, 0.95f)));

        return new AnimationClip(root)
        {
            LengthInSeconds = clipLength,
            ImportedHumanoidRootMotionSettings = new ImportedHumanoidClipRootMotionSettings
            {
                StopTime = clipLength,
                LoopTime = loopTime,
                LoopPose = loopPose,
                CycleOffset = cycleOffset,
                Mirror = mirror,
            },
        };
    }

    private static (AnimationClip Clip, AnimationMember Member, AnimationValueStore Store) CreateClip(BasePropAnim animation)
    {
        var member = new AnimationMember("Value", EAnimationMemberType.Property, animation);
        var clip = new AnimationClip(member) { LengthInSeconds = Length };
        var store = new AnimationValueStore();
        clip.PrepareSampling(store);
        return (clip, member, store);
    }

    private static object? ReadStore(AnimationValueStore store, AnimSlot slot)
        => store.GetValue(slot);

    private static BasePropAnim CreateAnimation(
        Type animationType,
        float firstKeySecond,
        bool looped = false,
        bool baked = false,
        bool constrained = false,
        bool lerpConstrained = false)
    {
        BasePropAnimBakeable animation;
        if (animationType == typeof(PropAnimFloat))
        {
            var typed = new PropAnimFloat(Length, looped, true) { DefaultValue = -5.0f, ConstrainKeyframedFPS = constrained, LerpConstrainedFPS = lerpConstrained };
            typed.Keyframes.Add(new FloatKeyframe(firstKeySecond, 1.0f, 0.0f, EVectorInterpType.Linear));
            typed.Keyframes.Add(new FloatKeyframe(1.0f, 4.0f, 0.0f, EVectorInterpType.Linear));
            typed.Keyframes.Add(new FloatKeyframe(2.0f, -2.0f, 0.0f, EVectorInterpType.Linear));
            animation = typed;
        }
        else if (animationType == typeof(PropAnimVector2))
        {
            var typed = new PropAnimVector2(Length, looped, true) { DefaultValue = new Vector2(-5.0f), ConstrainKeyframedFPS = constrained, LerpConstrainedFPS = lerpConstrained };
            typed.Keyframes.Add(new Vector2Keyframe(firstKeySecond, new Vector2(1.0f, 2.0f), Vector2.Zero, EVectorInterpType.Linear));
            typed.Keyframes.Add(new Vector2Keyframe(1.0f, new Vector2(4.0f, 0.0f), Vector2.Zero, EVectorInterpType.Linear));
            typed.Keyframes.Add(new Vector2Keyframe(2.0f, new Vector2(-2.0f, 6.0f), Vector2.Zero, EVectorInterpType.Linear));
            animation = typed;
        }
        else if (animationType == typeof(PropAnimVector4))
        {
            var typed = new PropAnimVector4(Length, looped, true) { DefaultValue = new Vector4(-5.0f), ConstrainKeyframedFPS = constrained, LerpConstrainedFPS = lerpConstrained };
            typed.Keyframes.Add(new Vector4Keyframe(firstKeySecond, new Vector4(1.0f, 2.0f, 3.0f, 4.0f), Vector4.Zero, EVectorInterpType.Linear));
            typed.Keyframes.Add(new Vector4Keyframe(1.0f, new Vector4(4.0f, 0.0f, -1.0f, 2.0f), Vector4.Zero, EVectorInterpType.Linear));
            typed.Keyframes.Add(new Vector4Keyframe(2.0f, new Vector4(-2.0f, 6.0f, 2.0f, -3.0f), Vector4.Zero, EVectorInterpType.Linear));
            animation = typed;
        }
        else if (animationType == typeof(PropAnimVector3))
        {
            var typed = new PropAnimVector3(Length, looped, true) { DefaultValue = new Vector3(-5.0f), ConstrainKeyframedFPS = constrained, LerpConstrainedFPS = lerpConstrained };
            typed.Keyframes.Add(new Vector3Keyframe(firstKeySecond, new Vector3(1.0f, 2.0f, 3.0f), Vector3.Zero, EVectorInterpType.Linear));
            typed.Keyframes.Add(new Vector3Keyframe(1.0f, new Vector3(4.0f, 0.0f, -1.0f), Vector3.Zero, EVectorInterpType.Linear));
            typed.Keyframes.Add(new Vector3Keyframe(2.0f, new Vector3(-2.0f, 6.0f, 2.0f), Vector3.Zero, EVectorInterpType.Linear));
            animation = typed;
        }
        else if (animationType == typeof(PropAnimQuaternion))
        {
            var typed = new PropAnimQuaternion(Length, looped, true) { DefaultValue = Quaternion.CreateFromAxisAngle(Vector3.UnitX, 1.0f), ConstrainKeyframedFPS = constrained, LerpConstrainedFPS = lerpConstrained };
            typed.Keyframes.Add(new QuaternionKeyframe(firstKeySecond, Quaternion.CreateFromAxisAngle(Vector3.UnitY, 0.25f), Quaternion.Identity, ERadialInterpType.Linear));
            typed.Keyframes.Add(new QuaternionKeyframe(1.0f, Quaternion.CreateFromAxisAngle(Vector3.UnitY, 1.0f), Quaternion.Identity, ERadialInterpType.Linear));
            typed.Keyframes.Add(new QuaternionKeyframe(2.0f, Quaternion.CreateFromAxisAngle(Vector3.UnitZ, 2.0f), Quaternion.Identity, ERadialInterpType.Linear));
            animation = typed;
        }
        else if (animationType == typeof(PropAnimBool))
        {
            var typed = new PropAnimBool(Length, looped, true) { DefaultValue = true };
            typed.Keyframes.Add(new BoolKeyframe(firstKeySecond, false));
            typed.Keyframes.Add(new BoolKeyframe(1.0f, true));
            typed.Keyframes.Add(new BoolKeyframe(2.0f, false));
            animation = typed;
        }
        else
            throw new ArgumentOutOfRangeException(nameof(animationType));

        animation.BakedFramesPerSecond = 10;
        if (baked)
            animation.IsBaked = true;
        return animation;
    }
}
