using MemoryPack;

namespace XREngine.Animation
{
    public partial class AnimationClip
    {
        [MemoryPackIgnore]
        private AnimationMember[]? _samplingMembers;

        [MemoryPackIgnore]
        private AnimationMember? _samplingRootMember;

        /// <summary>
        /// Gets the members that <see cref="SampleToStore"/> does not write. Query this list when
        /// a clip opens. Each call walks the member tree and allocates, so do not call it each frame.
        /// </summary>
        /// <returns>The unsupported members. The list is empty when all members are supported.</returns>
        public IReadOnlyList<AnimationMember> GetUnsupportedSamplingMembers()
        {
            List<AnimationMember> unsupported = [];
            CollectUnsupportedSamplingMembers(_rootMember, unsupported);
            return unsupported;
        }

        /// <summary>
        /// Assigns a store slot to each supported member and sizes the store. Call this at setup,
        /// and again after a change to the member tree. Unsupported members get an invalid slot.
        /// This method writes the slots on the members, so a clip that a state machine uses
        /// cannot prepare for sampling. Use a separate clip instance for each sampling owner.
        /// </summary>
        /// <param name="store">The store to size. Its earlier values are discarded.</param>
        /// <exception cref="InvalidOperationException">A state machine owns the slots of this clip.</exception>
        public void PrepareSampling(AnimationValueStore store)
        {
            ArgumentNullException.ThrowIfNull(store);
            if (SlotLayout is not null)
                throw new InvalidOperationException(
                    $"Animation clip '{Name}' has slots from a state machine. Sample a separate clip instance.");

            Dictionary<string, AnimationMember> membersByPath = new(StringComparer.Ordinal);
            CollectSamplingMembers(_rootMember, string.Empty, membersByPath);
            store.Resize(AnimStateMachine.BuildSlotLayout(membersByPath, out _));
            _samplingMembers = [.. membersByPath.Values];
            _samplingRootMember = _rootMember;
        }

        /// <summary>
        /// Writes the value of each supported member at the given second to a store. The result
        /// depends only on the second and the authored data. This method does not read or write
        /// a retained key, the current time of an animation, or event state. It does not allocate
        /// for float, vector, quaternion, and bool animations.
        /// </summary>
        /// <param name="second">The clip time in seconds. Each animation resolves it as a seek does.</param>
        /// <param name="store">A store that <see cref="PrepareSampling"/> sized for this clip.</param>
        /// <exception cref="InvalidOperationException">This clip is not prepared for its current root member.</exception>
        public void SampleToStore(float second, AnimationValueStore store)
        {
            AnimationMember[]? members = _samplingMembers;
            if (members is null || !ReferenceEquals(_samplingRootMember, _rootMember))
                throw new InvalidOperationException(
                    $"Call {nameof(PrepareSampling)} on animation clip '{Name}' before {nameof(SampleToStore)}.");

            store.Clear();
            for (int i = 0; i < members.Length; i++)
                members[i].WriteSampleToStore(second, store);
        }

        private static void CollectSamplingMembers(
            AnimationMember? member,
            string path,
            Dictionary<string, AnimationMember> membersByPath)
        {
            if (member is null)
                return;

            member.Slot = AnimSlot.Invalid;
            path = AppendMemberPathSegment(path, member);

            if (member.Animation is not null && !member.IsUnsupportedForSampling)
                membersByPath.TryAdd(path, member);

            if (member.Children.Count == 0)
                return;

            if (member.MemberType != EAnimationMemberType.Group)
                path += "/";

            foreach (AnimationMember child in member.Children)
                CollectSamplingMembers(child, path, membersByPath);
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
