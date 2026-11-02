using MemoryPack;
using XREngine.Animation.Importers;

namespace XREngine.Animation
{
    public partial class AnimationClip
    {
        [MemoryPackIgnore]
        private AnimationSlotLayout? _samplingLayout;

        [MemoryPackIgnore]
        private AnimationMember? _samplingRootMember;

        /// <summary>
        /// Gets the members that <see cref="SampleToStore"/> does not write, because their tracks
        /// call code at the sample time. Query this list when a clip opens. Each call walks the
        /// member tree and allocates, so do not call it each frame.
        /// </summary>
        /// <returns>The unsupported members. The list is empty when all members are supported.</returns>
        public IReadOnlyList<AnimationMember> GetUnsupportedSamplingMembers()
        {
            List<AnimationMember> unsupported = [];
            CollectUnsupportedSamplingMembers(_rootMember, unsupported);
            return unsupported;
        }

        /// <summary>
        /// Gives this clip its own slot layout and sizes the store for it.
        /// See <see cref="PrepareSharedSampling"/> for the rules.
        /// </summary>
        /// <param name="store">The store to size. Its earlier values are discarded.</param>
        /// <returns>The layout, which lists the members that apply the sampled values.</returns>
        /// <exception cref="InvalidOperationException">A state machine owns the slots of this clip.</exception>
        public AnimationSlotLayout PrepareSampling(AnimationValueStore store)
        {
            ArgumentNullException.ThrowIfNull(store);
            AnimationSlotLayout layout = PrepareSharedSampling([new AnimationSamplingBinding(this)]);
            store.Resize(layout);
            return layout;
        }

        /// <summary>
        /// Builds one slot layout for several clips, so that their stores can blend. Create each
        /// store with <see cref="AnimationSlotLayout.CreateStore"/>. Call this at setup, and again
        /// after a change to a member tree. Tracks that call code get an invalid slot.
        /// This method writes the slots on the members, so a clip that a state machine uses cannot
        /// prepare for sampling. Use a separate clip instance for each binding. Bind the members to
        /// their targets first: a mirrored target binding copies its parent object at this time.
        /// </summary>
        /// <param name="bindings">The clips and their target scopes.</param>
        /// <returns>The layout, which lists the members that apply the sampled values.</returns>
        /// <exception cref="ArgumentException">One clip instance occurs more than once.</exception>
        /// <exception cref="InvalidOperationException">
        /// A state machine owns the slots of a clip, or two clips give different value types to one path.
        /// </exception>
        public static AnimationSlotLayout PrepareSharedSampling(IReadOnlyList<AnimationSamplingBinding> bindings)
        {
            ArgumentNullException.ThrowIfNull(bindings);

            var clips = new HashSet<AnimationClip>();
            var membersByScope = new Dictionary<string, Dictionary<string, AnimationMember>>(StringComparer.Ordinal);
            for (int i = 0; i < bindings.Count; i++)
            {
                AnimationClip clip = bindings[i].Clip;
                if (!clips.Add(clip))
                    throw new ArgumentException($"Animation clip '{clip.Name}' occurs more than once. Use a separate instance for each binding.", nameof(bindings));
                if (clip.SlotLayout is not null && !ReferenceEquals(clip.SlotLayout, clip._samplingLayout))
                    throw new InvalidOperationException($"Animation clip '{clip.Name}' has slots from a state machine. Sample a separate clip instance.");

                if (!membersByScope.TryGetValue(bindings[i].Scope, out Dictionary<string, AnimationMember>? members))
                    membersByScope.Add(bindings[i].Scope, members = new(StringComparer.Ordinal));
                clip.CollectSampledMembers(members);
            }

            var layout = new AnimationSlotLayout();
            var slotsByScope = new Dictionary<string, Dictionary<string, AnimSlot>>(StringComparer.Ordinal);
            List<AnimationQuaternionFloatSlotGroup> quaternionGroups = [];
            List<AnimationMember> applyMembers = [];
            foreach ((string scope, Dictionary<string, AnimationMember> members) in membersByScope)
            {
                AnimStateMachine.RegisterImportedHumanoidMirroredBindings(members);
                Dictionary<string, AnimSlot> slots = AnimStateMachine.AppendSlots(layout, members);
                slotsByScope.Add(scope, slots);
                quaternionGroups.AddRange(AnimStateMachine.BuildQuaternionFloatSlotGroups(slots, members));
                applyMembers.AddRange(members.Values);
            }
            layout.QuaternionFloatGroups = [.. quaternionGroups];
            layout.SamplingApplyMembers = [.. applyMembers];

            for (int i = 0; i < bindings.Count; i++)
                bindings[i].Clip.CompleteSamplingPreparation(layout, slotsByScope[bindings[i].Scope]);

            return layout;
        }

        /// <summary>
        /// Writes the value of each supported member at the given second to a store. The result
        /// depends only on the second and the authored data. Imported humanoid policy (mirror,
        /// loop pose, cycle offset, and loop time) applies as it does in normal playback. This
        /// method does not read or write a current time, a current value, a playback clock, or
        /// event state. It does not allocate for float, vector, quaternion, and bool animations.
        /// </summary>
        /// <param name="second">The clip time in seconds. It can be past the clip length.</param>
        /// <param name="store">A store that has the layout of this clip.</param>
        /// <exception cref="InvalidOperationException">This clip is not prepared for its current root member.</exception>
        public void SampleToStore(float second, AnimationValueStore store)
        {
            if (_samplingLayout is null
                || !ReferenceEquals(SlotLayout, _samplingLayout)
                || !ReferenceEquals(_samplingRootMember, _rootMember))
                throw new InvalidOperationException(
                    $"Call {nameof(PrepareSampling)} on animation clip '{Name}' before {nameof(SampleToStore)}.");

            store.Clear();
            bool hasPolicy = TryGetImportedHumanoidSampleContext(second, out ImportedHumanoidRootMotionPolicy policy, out float sampleTime, out float samplePhase);
            AnimationMember[] members = AnimatedMembersArray;
            for (int i = 0; i < members.Length; i++)
            {
                AnimationMember member = members[i];
                if (hasPolicy && TryWriteImportedHumanoidValueToStore(store, member, policy, sampleTime, samplePhase, useSampleKeyHint: true))
                    continue;

                member.WriteSampleToStore(second, store);
            }
            ApplyImportedHumanoidScalarQuaternionCorrections(store, hasPolicy, policy, samplePhase);
        }

        /// <summary>
        /// Gets the imported humanoid sample time for a playback time. The time wraps or clamps as
        /// the state clock does, but no clock field changes.
        /// </summary>
        private bool TryGetImportedHumanoidSampleContext(
            float second,
            out ImportedHumanoidRootMotionPolicy policy,
            out float sampleTime,
            out float samplePhase)
        {
            policy = default;
            sampleTime = 0.0f;
            samplePhase = 0.0f;
            if (ImportedHumanoidRootMotionSettings is not { } settings
                || !ImportedHumanoidRootMotionPolicy.TryCreate(settings, out policy, out _))
                return false;

            long lengthTicks = SecondsToImportedHumanoidTicks(LengthInSeconds);
            long playbackTicks = SecondsToImportedHumanoidTicks(second);
            playbackTicks = policy.LoopTime && lengthTicks > 0L
                ? FloorModulus(playbackTicks, lengthTicks)
                : Math.Clamp(playbackTicks, 0L, Math.Max(0L, lengthTicks));
            ResolveImportedHumanoidSample(policy, ImportedHumanoidTicksToSeconds(playbackTicks), out sampleTime, out samplePhase, out _);
            return true;
        }

        /// <summary>
        /// Registers this clip's members by logical path and adds those that have a sampled value.
        /// In one scope, the first clip on a path owns the apply member, as in a state machine.
        /// </summary>
        private void CollectSampledMembers(Dictionary<string, AnimationMember> membersByPath)
        {
            _animatedCurves.Clear();
            EnsureStandaloneSamplingBindings();
            foreach ((string path, AnimationMember member) in _animatedCurves)
            {
                member.Slot = AnimSlot.Invalid;
                if (member.HasSampledValue)
                    membersByPath.TryAdd(path, member);
            }
        }

        private void CompleteSamplingPreparation(AnimationSlotLayout layout, Dictionary<string, AnimSlot> slotsByPath)
        {
            List<AnimationMember> sampled = [];
            foreach ((string path, AnimationMember member) in _animatedCurves)
            {
                if (!member.HasSampledValue)
                    continue;

                AnimSlot slot = slotsByPath[path];
                EAnimValueType valueType = member.DetermineValueType();
                if (slot.Type != valueType)
                    throw new InvalidOperationException(
                        $"Animation binding '{path}' in clip '{Name}' has value type {valueType}, but its shared slot uses {slot.Type}.");

                member.Slot = slot;
                sampled.Add(member);
            }

            AnimatedMembersArray = [.. sampled];
            SlotLayout = layout;
            ValueStore.Resize(layout);
            _samplingLayout = layout;
            _samplingRootMember = _rootMember;
            PrepareImportedHumanoidMirrorSlotBindings(layout, slotsByPath);
            PrepareImportedHumanoidScalarQuaternionBindings(layout);

            // The first restore of each member stores its baseline. Do it here, not in a sample.
            if (ImportedHumanoidRootMotionSettings is not null)
                for (int i = 0; i < sampled.Count; i++)
                    RestoreImportedHumanoidMethodArguments(sampled[i]);
        }

        private static void CollectUnsupportedSamplingMembers(
            AnimationMember? member,
            List<AnimationMember> unsupported)
        {
            if (member is null)
                return;

            if (member.IsUnsupportedForSampling)
                unsupported.Add(member);

            foreach (AnimationMember child in member.Children)
                CollectUnsupportedSamplingMembers(child, unsupported);
        }
    }
}
